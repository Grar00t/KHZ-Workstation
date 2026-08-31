using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Repositories;

internal sealed class GitRepositoryInspector : IRepositoryInspector
{
    private static readonly TimeSpan CommandTimeout =
        TimeSpan.FromSeconds(5);

    private string? _gitExecutable;

    public async Task<RepositorySnapshot> InspectAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException(
                "Repository directory is required.",
                nameof(directory));
        }

        var requestedPath =
            Path.GetFullPath(
                directory.Trim());

        if (!Directory.Exists(requestedPath))
        {
            throw new DirectoryNotFoundException(
                requestedPath);
        }

        var rootResult =
            await RunGitAsync(
                requestedPath,
                cancellationToken,
                "rev-parse",
                "--show-toplevel");

        if (rootResult.ExitCode != 0)
        {
            return new RepositorySnapshot(
                IsRepository: false,
                RequestedPath: requestedPath,
                RootPath: null,
                Branch: null,
                HeadSha: null,
                IsClean: true,
                Changes: [],
                RecentCommits: [],
                Message: "The selected folder is not inside a Git repository.");
        }

        var root =
            NormalizeRootPath(
                rootResult.StandardOutput);

        var headResult =
            await RunGitAsync(
                root,
                cancellationToken,
                "rev-parse",
                "HEAD");

        EnsureSuccess(
            headResult,
            "Unable to read repository HEAD.");

        var headSha =
            FirstLine(
                headResult.StandardOutput);

        var branchResult =
            await RunGitAsync(
                root,
                cancellationToken,
                "symbolic-ref",
                "--quiet",
                "--short",
                "HEAD");

        string branch;

        if (branchResult.ExitCode == 0)
        {
            branch =
                FirstLine(
                    branchResult.StandardOutput);
        }
        else
        {
            var shortHeadResult =
                await RunGitAsync(
                    root,
                    cancellationToken,
                    "rev-parse",
                    "--short",
                    "HEAD");

            EnsureSuccess(
                shortHeadResult,
                "Unable to identify detached HEAD.");

            branch =
                "detached@" +
                FirstLine(
                    shortHeadResult.StandardOutput);
        }

        var statusResult =
            await RunGitAsync(
                root,
                cancellationToken,
                "status",
                "--porcelain=v1",
                "--untracked-files=normal");

        EnsureSuccess(
            statusResult,
            "Unable to read repository status.");

        var changes =
            ParseChanges(
                statusResult.StandardOutput);

        var logResult =
            await RunGitAsync(
                root,
                cancellationToken,
                "log",
                "-10",
                "--date=iso-strict",
                "--pretty=format:%H%x09%h%x09%ad%x09%s");

        EnsureSuccess(
            logResult,
            "Unable to read recent commits.");

        var commits =
            ParseCommits(
                logResult.StandardOutput);

        return new RepositorySnapshot(
            IsRepository: true,
            RequestedPath: requestedPath,
            RootPath: root,
            Branch: branch,
            HeadSha: headSha,
            IsClean: changes.Count == 0,
            Changes: changes,
            RecentCommits: commits,
            Message: null);
    }

    private async Task<GitResult> RunGitAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var gitExecutable =
            _gitExecutable
            ??= ResolveGitExecutable();

        var startInfo =
            new ProcessStartInfo
            {
                FileName = gitExecutable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

        // Disable interactive/network credential prompting.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";

        // Prevent read-only inspection commands such as
        // `git status` from taking optional locks or refreshing
        // index metadata on disk.
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        // Never invoke a configured pager process.
        startInfo.Environment["GIT_PAGER"] = "cat";

        // Avoid repository-configured fsmonitor execution and
        // recursive submodule traversal during local inspection.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("submodule.recurse=false");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(
                argument);
        }

        using var process =
            new Process
            {
                StartInfo = startInfo
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Git process could not be started.");
        }

        var stdoutTask =
            process.StandardOutput.ReadToEndAsync();

        var stderrTask =
            process.StandardError.ReadToEndAsync();

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            CommandTimeout);

        try
        {
            await process.WaitForExitAsync(
                timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(
                        entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort termination after timeout.
            }

            throw new TimeoutException(
                "Git repository inspection timed out.");
        }

        var standardOutput =
            await stdoutTask;

        var standardError =
            await stderrTask;

        return new GitResult(
            process.ExitCode,
            standardOutput,
            standardError);
    }

    private static IReadOnlyList<RepositoryChange> ParseChanges(
        string output)
    {
        var rows =
            new List<RepositoryChange>();

        foreach (var rawLine in SplitLines(output))
        {
            if (rawLine.Length < 3)
                continue;

            var status =
                rawLine[..2];

            var path =
                rawLine[3..].Trim();

            if (path.Length == 0)
                continue;

            rows.Add(
                new RepositoryChange(
                    Status: status,
                    Path: path));
        }

        return rows;
    }

    private static IReadOnlyList<RepositoryCommit> ParseCommits(
        string output)
    {
        var rows =
            new List<RepositoryCommit>();

        foreach (var line in SplitLines(output))
        {
            var parts =
                line.Split(
                    '\t',
                    4);

            if (parts.Length != 4)
                continue;

            rows.Add(
                new RepositoryCommit(
                    Sha: parts[0],
                    ShortSha: parts[1],
                    Occurred: parts[2],
                    Subject: parts[3]));
        }

        return rows;
    }

    private static string ResolveGitExecutable()
    {
        var candidates =
            new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFiles),
                    "Git",
                    "cmd",
                    "git.exe"),

                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFilesX86),
                    "Git",
                    "cmd",
                    "git.exe"),

                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Git",
                    "cmd",
                    "git.exe")
            };

        var found =
            candidates.FirstOrDefault(
                File.Exists);

        if (found is null)
        {
            throw new FileNotFoundException(
                "Git for Windows was not found in supported installation locations.");
        }

        return found;
    }

    private static string NormalizeRootPath(
        string output)
    {
        var root =
            FirstLine(output);

        return Path.GetFullPath(
            root);
    }

    private static string FirstLine(
        string value)
    {
        var first =
            SplitLines(value)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(first))
        {
            throw new InvalidOperationException(
                "Git returned an empty result.");
        }

        return first.Trim();
    }

    private static IEnumerable<string> SplitLines(
        string value)
        => value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

    private static void EnsureSuccess(
        GitResult result,
        string message)
    {
        if (result.ExitCode == 0)
            return;

        var detail =
            result.StandardError.Trim();

        throw new InvalidOperationException(
            detail.Length == 0
                ? message
                : $"{message} {detail}");
    }

    private sealed record GitResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
