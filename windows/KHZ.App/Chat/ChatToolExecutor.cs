using KHZ.App.Repositories;
using KHZ.App.Terminal;
using KHZ.App.Trust;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace KHZ.App.Chat;

internal sealed class ChatToolExecutor
{
    private static readonly HashSet<string> SearchableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".json", ".jsonl", ".ndjson", ".csv",
            ".cs", ".xaml", ".xml", ".yaml", ".yml", ".toml",
            ".py", ".ps1", ".cmd", ".bat", ".c", ".h", ".cpp",
            ".hpp", ".js", ".ts", ".tsx", ".jsx", ".html", ".css",
            ".sql", ".ini", ".config", ".sln", ".csproj"
        };

    private readonly IRepositoryInspector _repositories;
    private readonly ITerminalRunner _terminal;
    private readonly IActivityStore _activity;

    internal ChatToolExecutor(
        IRepositoryInspector repositories,
        ITerminalRunner terminal,
        IActivityStore activity)
    {
        _repositories = repositories;
        _terminal = terminal;
        _activity = activity;
    }

    internal IReadOnlyList<ChatToolDefinition> Definitions { get; } =
    [
        new(
            "list_directory",
            "List files and directories under the active workspace/folder. Use relative paths only.",
            """
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """,
            false),
        new(
            "read_file",
            "Read a UTF-8/text file under the active workspace/folder. Use relative paths only.",
            """
            {"type":"object","properties":{"path":{"type":"string"},"max_chars":{"type":"integer","minimum":1,"maximum":200000}},"required":["path"],"additionalProperties":false}
            """,
            false),
        new(
            "search_text",
            "Search text files under the active workspace/folder for a literal query.",
            """
            {"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string"}},"required":["query"],"additionalProperties":false}
            """,
            false),
        new(
            "inspect_repository",
            "Inspect the active local Git repository: root, branch, HEAD, changes and recent commits.",
            """
            {"type":"object","properties":{"path":{"type":"string"}},"additionalProperties":false}
            """,
            false),
        new(
            "run_powershell",
            "Run one PowerShell command in the active workspace/folder. This always requires explicit user confirmation before execution.",
            """
            {"type":"object","properties":{"command":{"type":"string"},"timeout_seconds":{"type":"integer","minimum":1,"maximum":300}},"required":["command"],"additionalProperties":false}
            """,
            true)
    ];

    internal async Task<string> ExecuteAsync(
        ChatToolCall call,
        ChatContext context,
        CancellationToken cancellationToken)
    {
        using var argsDocument = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson)
                ? "{}"
                : call.ArgumentsJson);
        var args = argsDocument.RootElement;

        return call.Name switch
        {
            "list_directory" => ListDirectory(
                context,
                GetString(args, "path") ?? "."),

            "read_file" => ReadFile(
                context,
                RequireString(args, "path"),
                GetInt32(args, "max_chars", 80_000)),

            "search_text" => SearchText(
                context,
                RequireString(args, "query"),
                GetString(args, "path") ?? "."),

            "inspect_repository" => await InspectRepositoryAsync(
                context,
                GetString(args, "path") ?? ".",
                cancellationToken),

            "run_powershell" => await RunPowerShellAsync(
                context,
                RequireString(args, "command"),
                GetInt32(args, "timeout_seconds", 60),
                cancellationToken),

            _ => JsonSerializer.Serialize(new
            {
                ok = false,
                error = "unknown_tool",
                tool = call.Name
            })
        };
    }

    private static string ListDirectory(
        ChatContext context,
        string relativePath)
    {
        var path = ResolveBoundedPath(context, relativePath);
        if (!Directory.Exists(path))
            return Error("directory_not_found", relativePath);

        var entries = new List<object>();

        foreach (var item in new DirectoryInfo(path)
                     .EnumerateFileSystemInfos()
                     .OrderByDescending(x => x is DirectoryInfo)
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(250))
        {
            if (string.Equals(item.Name, ".khz", StringComparison.OrdinalIgnoreCase))
                continue;

            entries.Add(new
            {
                name = item.Name,
                kind = item is DirectoryInfo ? "directory" : "file",
                size = item is FileInfo file ? file.Length : (long?)null,
                modified_utc = item.LastWriteTimeUtc.ToString("O")
            });
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            path = ToRelative(context, path),
            entries
        });
    }

    private static string ReadFile(
        ChatContext context,
        string relativePath,
        int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 1, 200_000);
        var path = ResolveBoundedPath(context, relativePath);
        if (!File.Exists(path))
            return Error("file_not_found", relativePath);

        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024)
            return Error("file_too_large_for_text_tool", relativePath);

        using var reader = new StreamReader(
            path,
            detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        var truncated = read > maxChars;
        var count = Math.Min(read, maxChars);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            path = ToRelative(context, path),
            content = new string(buffer, 0, count),
            truncated
        });
    }

    private static string SearchText(
        ChatContext context,
        string query,
        string relativePath)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 500)
            return Error("invalid_query", query);

        var root = ResolveBoundedPath(context, relativePath);
        if (!Directory.Exists(root))
            return Error("directory_not_found", relativePath);

        var hits = new List<object>();
        var inspected = 0;

        foreach (var file in EnumerateFilesSafe(root))
        {
            if (inspected >= 400 || hits.Count >= 80)
                break;

            if (!SearchableExtensions.Contains(Path.GetExtension(file)))
                continue;

            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1024 * 1024)
                    continue;

                inspected++;
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    var index = line.IndexOf(
                        query,
                        StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                        continue;

                    hits.Add(new
                    {
                        path = ToRelative(context, file),
                        line = lineNumber,
                        text = line.Length <= 500
                            ? line
                            : line[..500] + "…"
                    });

                    if (hits.Count >= 80)
                        break;
                }
            }
            catch
            {
            }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            query,
            inspected_files = inspected,
            hits,
            truncated = inspected >= 400 || hits.Count >= 80
        });
    }

    private async Task<string> InspectRepositoryAsync(
        ChatContext context,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var path = ResolveBoundedPath(context, relativePath);
        var snapshot = await _repositories.InspectAsync(
            path,
            cancellationToken);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            snapshot
        });
    }

    private async Task<string> RunPowerShellAsync(
        ChatContext context,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        command = command.Trim();
        if (command.Length is < 1 or > 16_384)
        {
            return Error(
                "invalid_command",
                "PowerShell command length is invalid.");
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 300);

        var decision = MessageBox.Show(
            "The local model proposed this PowerShell command:\n\n" + command +
            "\n\nWorking directory:\n" + context.RootPath +
            "\n\nRun it now?",
            "KHZ · Confirm command",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (decision != MessageBoxResult.Yes)
        {
            _activity.Record(
                category: "ai",
                action: "tool.run_powershell",
                target: context.ContextId,
                result: "DENIED",
                details: new
                {
                    commandCaptured = false,
                    userConfirmed = false,
                    aiUsed = true
                });

            return JsonSerializer.Serialize(new
            {
                ok = false,
                denied_by_user = true
            });
        }

        _activity.Record(
            category: "ai",
            action: "tool.run_powershell",
            target: context.ContextId,
            result: "STARTED",
            details: new
            {
                commandCaptured = false,
                userConfirmed = true,
                aiUsed = true,
                timeoutSeconds
            });

        var result = await _terminal.ExecuteAsync(
            new TerminalExecutionRequest(
                Command: command,
                WorkingDirectory: context.RootPath,
                Timeout: TimeSpan.FromSeconds(timeoutSeconds)),
            cancellationToken);

        var succeeded =
            result.Status == TerminalExecutionStatus.Exited
            && result.ExitCode == 0;

        _activity.Record(
            category: "ai",
            action: "tool.run_powershell",
            target: context.ContextId,
            result: succeeded ? "PASSED" : "FAILED",
            details: new
            {
                commandCaptured = false,
                userConfirmed = true,
                aiUsed = true,
                status = result.Status.ToString(),
                exitCode = result.ExitCode,
                stdoutLength = result.StandardOutput.Length,
                stderrLength = result.StandardError.Length
            });

        return JsonSerializer.Serialize(new
        {
            ok = succeeded,
            status = result.Status.ToString(),
            exit_code = result.ExitCode,
            stdout = Bound(result.StandardOutput, 100_000),
            stderr = Bound(result.StandardError, 40_000)
        });
    }

    private static string ResolveBoundedPath(
        ChatContext context,
        string relativePath)
    {
        relativePath = (relativePath ?? string.Empty).Trim();
        if (relativePath.Length == 0 || relativePath == ".")
            return Path.GetFullPath(context.RootPath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                "Tool paths must be relative to the active workspace/folder.");
        }

        var root = Path.GetFullPath(context.RootPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(
            Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                candidate.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Tool path escapes the active workspace/folder.");
        }

        var relative = Path.GetRelativePath(root, candidate);
        var parts = relative.Split(
            new[]
            {
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Any(
                part => string.Equals(
                    part,
                    ".khz",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Internal .khz metadata is not exposed to model tools.");
        }

        return candidate;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;

            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
                directories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            foreach (var child in directories)
            {
                if (string.Equals(
                        Path.GetFileName(child),
                        ".khz",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static string ToRelative(
        ChatContext context,
        string path)
        => Path.GetRelativePath(context.RootPath, path);

    private static string RequireString(
        JsonElement args,
        string name)
        => GetString(args, name)
           ?? throw new InvalidDataException(
               $"Tool argument '{name}' is required.");

    private static string? GetString(
        JsonElement args,
        string name)
        => args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt32(
        JsonElement args,
        string name,
        int fallback)
        => args.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static string Error(string code, string detail)
        => JsonSerializer.Serialize(new
        {
            ok = false,
            error = code,
            detail
        });

    private static string Bound(string value, int max)
        => value.Length <= max
            ? value
            : value[..max] + "\n[truncated]";
}
