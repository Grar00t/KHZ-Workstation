using KHZ.App.Workspaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KHZ.App.AI;

internal sealed record LocalAiSession
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("api_token")]
    public string ApiToken { get; init; } = string.Empty;

    [JsonPropertyName("process_id")]
    public int ProcessId { get; init; }

    [JsonPropertyName("process_containment")]
    public string ProcessContainment { get; init; } = string.Empty;

    [JsonPropertyName("model_family")]
    public string ModelFamily { get; init; } = string.Empty;

    [JsonPropertyName("model_display_name")]
    public string ModelDisplayName { get; init; } = string.Empty;

    [JsonPropertyName("model_sha256")]
    public string ModelSha256 { get; init; } = string.Empty;

    [JsonPropertyName("workspace_id")]
    public string WorkspaceId { get; init; } = string.Empty;

    [JsonPropertyName("workspace_root")]
    public string WorkspaceRoot { get; init; } = string.Empty;

    [JsonIgnore]
    public Uri EndpointUri => new(Endpoint, UriKind.Absolute);

    internal static string SessionPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "runtime",
            "ai-session.json");

    internal static bool TryLoad(
        WorkspaceContext? workspace,
        out LocalAiSession? session,
        out string status)
    {
        session = null;
        if (workspace is null)
        {
            status = "Activate a KHZ workspace before starting the assistant.";
            return false;
        }

        try
        {
            if (!File.Exists(SessionPath))
            {
                status =
                    "No local model session. Run khz serve qwen --workspace <path>.";
                return false;
            }

            var file = new FileInfo(SessionPath);
            if (file.Length <= 0 || file.Length > 64 * 1024)
                throw new InvalidDataException("Local AI session file is invalid.");

            var loaded =
                JsonSerializer.Deserialize<LocalAiSession>(
                    File.ReadAllText(SessionPath))
                ?? throw new InvalidDataException(
                    "Local AI session file is empty.");

            Validate(loaded, workspace);
            session = loaded;
            status = $"{loaded.ModelDisplayName} · workspace MCP";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException)
        {
            status = "Local AI session rejected: " + ex.Message;
            return false;
        }
    }

    private static void Validate(
        LocalAiSession session,
        WorkspaceContext workspace)
    {
        if (session.SchemaVersion != 1
            || !string.Equals(session.State, "READY", StringComparison.Ordinal)
            || !Guid.TryParseExact(session.SessionId, "D", out _))
        {
            throw new InvalidDataException(
                "Session is not in the supported READY state.");
        }

        if (!string.Equals(
                session.WorkspaceId,
                workspace.Info.WorkspaceId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Session belongs to another workspace.");
        }

        var sessionRoot = Path.GetFullPath(session.WorkspaceRoot);
        var activeRoot = Path.GetFullPath(workspace.Info.Root);
        if (!sessionRoot.Equals(activeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Session workspace path does not match the active workspace.");
        }

        if (!Uri.TryCreate(session.Endpoint, UriKind.Absolute, out var endpoint)
            || !endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !endpoint.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            || endpoint.Port is < 1024 or > 65535
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.AbsolutePath != "/"
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException(
                "Session endpoint is not a strict IPv4 loopback origin.");
        }

        if (!IsValidToken(session.ApiToken))
            throw new InvalidDataException("Session token is invalid.");

        if (session.ModelSha256 is null
            || session.ModelSha256.Length != 64
            || session.ModelSha256.Any(
                character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Model digest is invalid.");
        }

        if (session.ProcessId <= 0)
            throw new InvalidDataException("Session process ID is invalid.");

        if (!string.Equals(
                session.ProcessContainment,
                "windows_job_object",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Session process does not have Windows Job Object containment.");
        }

        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (process.HasExited)
                throw new InvalidDataException("Local model process has exited.");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                "Local model process is not running.",
                ex);
        }
    }

    private static bool IsValidToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) < 32)
        {
            return false;
        }

        return value.All(
            character =>
                character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_');
    }
}
