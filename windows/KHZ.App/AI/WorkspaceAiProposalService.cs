using KHZ.App.Trust;
using KHZ.App.Workspaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KHZ.App.AI;

internal sealed record WorkspaceAiProposal
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("proposal_id")]
    public string ProposalId { get; init; } = string.Empty;

    [JsonPropertyName("workspace_id")]
    public string WorkspaceId { get; init; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    [JsonPropertyName("expected_sha256")]
    public string? ExpectedSha256 { get; init; }

    [JsonPropertyName("observed_sha256")]
    public string? ObservedSha256 { get; init; }

    [JsonPropertyName("proposed_content")]
    public string ProposedContent { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("created_utc")]
    public string CreatedUtc { get; init; } = string.Empty;

    [JsonPropertyName("decision_utc")]
    public string? DecisionUtc { get; init; }

    [JsonIgnore]
    public string Display => $"{Target}  ·  {CreatedUtc}";
}

internal sealed class WorkspaceAiProposalService
{
    private const int MaximumProposalBytes = 1_000_000;
    private const int MaximumContentCharacters = 200_000;

    private static readonly HashSet<string> ProtectedDirectories =
        new(
            new[] { ".git", ".khz", ".svn", ".hg" },
            StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    private readonly IActivityStore _activity;

    internal WorkspaceAiProposalService(IActivityStore activity)
    {
        _activity = activity;
    }

    internal IReadOnlyList<WorkspaceAiProposal> ListPending(
        WorkspaceContext workspace)
    {
        var directory = ProposalDirectory(workspace);
        if (!Directory.Exists(directory))
            return Array.Empty<WorkspaceAiProposal>();

        var proposals = new List<WorkspaceAiProposal>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var proposal = ReadProposal(path, workspace);
                if (string.Equals(
                        proposal.Status,
                        "PENDING",
                        StringComparison.Ordinal))
                    proposals.Add(proposal);
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException)
            {
                _activity.Record(
                    category: "ai",
                    action: "ai.proposal.read",
                    target: "proposal",
                    result: "REJECTED",
                    details: new
                    {
                        errorType = ex.GetType().Name,
                        contentCaptured = false
                    });
            }
        }

        return proposals
            .OrderByDescending(item => item.CreatedUtc, StringComparer.Ordinal)
            .ToArray();
    }

    internal void Apply(
        WorkspaceContext workspace,
        string proposalId)
    {
        var proposal = ReadOwnedProposal(workspace, proposalId);
        var target = ResolveTarget(workspace, proposal.Target);
        var currentHash = File.Exists(target) ? Sha256(target) : null;
        if (!FixedEquals(currentHash, proposal.ObservedSha256)
            || (proposal.ExpectedSha256 is not null
                && !FixedEquals(currentHash, proposal.ExpectedSha256)))
        {
            throw new InvalidOperationException(
                "The target changed after the proposal was created. Refresh and ask for a new proposal.");
        }

        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("Proposal target has no parent folder.");
        Directory.CreateDirectory(parent);
        RejectReparseTraversal(workspace.Info.Root, target);

        if (File.Exists(target))
            PreserveVersion(workspace, proposal, target);

        var temporary = Path.Combine(
            parent,
            ".khz-ai-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(proposal.ProposedContent);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            var finalHash = File.Exists(target) ? Sha256(target) : null;
            if (!FixedEquals(finalHash, proposal.ObservedSha256))
            {
                throw new InvalidOperationException(
                    "The target changed while the proposal was being applied.");
            }

            if (File.Exists(target))
                File.Replace(temporary, target, null, ignoreMetadataErrors: false);
            else
                File.Move(temporary, target);

            WriteDecision(workspace, proposal with
            {
                Status = "APPLIED",
                DecisionUtc = DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture)
            });

            _activity.Record(
                category: "ai",
                action: "ai.proposal.apply",
                target: proposal.Target,
                result: "APPLIED",
                details: new
                {
                    proposalId = proposal.ProposalId,
                    workspaceId = workspace.Info.WorkspaceId,
                    explicitUserApproval = true,
                    modelApplied = false,
                    contentCaptured = false
                });
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal void Reject(
        WorkspaceContext workspace,
        string proposalId)
    {
        var proposal = ReadOwnedProposal(workspace, proposalId);
        WriteDecision(workspace, proposal with
        {
            Status = "REJECTED",
            DecisionUtc = DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture)
        });

        _activity.Record(
            category: "ai",
            action: "ai.proposal.reject",
            target: proposal.Target,
            result: "REJECTED",
            details: new
            {
                proposalId = proposal.ProposalId,
                workspaceId = workspace.Info.WorkspaceId,
                explicitUserDecision = true,
                contentCaptured = false
            });
    }

    private static string ProposalDirectory(WorkspaceContext workspace) =>
        Path.Combine(workspace.MetadataDirectory, "ai-proposals");

    private static WorkspaceAiProposal ReadOwnedProposal(
        WorkspaceContext workspace,
        string proposalId)
    {
        if (!Guid.TryParseExact(proposalId, "D", out _))
            throw new InvalidDataException("Proposal ID is invalid.");

        var path = Path.Combine(
            ProposalDirectory(workspace),
            proposalId + ".json");
        var proposal = ReadProposal(path, workspace);
        if (!string.Equals(
                proposal.Status,
                "PENDING",
                StringComparison.Ordinal))
            throw new InvalidOperationException("Proposal is no longer pending.");
        return proposal;
    }

    private static WorkspaceAiProposal ReadProposal(
        string path,
        WorkspaceContext workspace)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumProposalBytes)
            throw new InvalidDataException("Proposal file size is invalid.");

        var proposal =
            JsonSerializer.Deserialize<WorkspaceAiProposal>(
                File.ReadAllText(path))
            ?? throw new InvalidDataException("Proposal file is empty.");

        if (proposal.SchemaVersion != 1
            || !Guid.TryParseExact(proposal.ProposalId, "D", out _)
            || !Path.GetFileNameWithoutExtension(path).Equals(
                proposal.ProposalId,
                StringComparison.Ordinal)
            || !string.Equals(
                proposal.WorkspaceId,
                workspace.Info.WorkspaceId,
                StringComparison.Ordinal)
            || !string.Equals(
                proposal.Operation,
                "write_text",
                StringComparison.Ordinal)
            || proposal.ProposedContent is null
            || proposal.ProposedContent.Length > MaximumContentCharacters)
        {
            throw new InvalidDataException("Proposal schema or identity is invalid.");
        }

        _ = ResolveTarget(workspace, proposal.Target);
        return proposal;
    }

    private static string ResolveTarget(
        WorkspaceContext workspace,
        string relativeTarget)
    {
        if (string.IsNullOrWhiteSpace(relativeTarget)
            || Path.IsPathRooted(relativeTarget)
            || relativeTarget.Length > 1000)
        {
            throw new InvalidDataException(
                "Proposal target must be a bounded relative path.");
        }

        var parts = relativeTarget.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || parts.Any(part => part is "." or "..")
            || parts.Any(ProtectedDirectories.Contains))
        {
            throw new InvalidDataException("Proposal target is protected.");
        }

        var root = Path.GetFullPath(workspace.Info.Root);
        var target = Path.GetFullPath(Path.Combine(root, relativeTarget));
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Proposal target escapes the workspace.");
        }

        RejectReparseTraversal(root, target);
        if (Directory.Exists(target))
            throw new InvalidDataException("Proposal target must be a file.");
        return target;
    }

    private static void RejectReparseTraversal(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var part in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Proposal target crosses a reparse point.");
            }
        }
    }

    private static void PreserveVersion(
        WorkspaceContext workspace,
        WorkspaceAiProposal proposal,
        string target)
    {
        var stamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMddTHHmmssfffffffZ",
            CultureInfo.InvariantCulture);
        var version = Path.Combine(
            workspace.MetadataDirectory,
            "versions",
            "ai",
            stamp + "-" + proposal.ProposalId,
            proposal.Target.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            Path.GetDirectoryName(version)
            ?? throw new InvalidDataException("Version path has no parent."));
        File.Copy(target, version, overwrite: false);
    }

    private static string Sha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left.Length != 64 || right.Length != 64)
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WriteDecision(
        WorkspaceContext workspace,
        WorkspaceAiProposal proposal)
    {
        var directory = ProposalDirectory(workspace);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, proposal.ProposalId + ".json");
        var temporary = Path.Combine(
            directory,
            "." + proposal.ProposalId + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, proposal, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Replace(temporary, destination, null, ignoreMetadataErrors: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
