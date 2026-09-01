using KHZ.App.Repositories;
using KHZ.App.Terminal;
using KHZ.App.Trust;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
            "replace_text",
            "Replace one exact text occurrence in a workspace file. Requires the current SHA-256 and explicit user confirmation before the atomic write.",
            """
            {"type":"object","properties":{"path":{"type":"string"},"expected_sha256":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","expected_sha256","old_text","new_text"],"additionalProperties":false}
            """,
            true),
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

            "replace_text" => ReplaceText(
                context,
                RequireString(args, "path"),
                RequireString(args, "expected_sha256"),
                RequireString(args, "old_text"),
                RequireString(args, "new_text")),

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

        var bytes = File.ReadAllBytes(path);
        var hash = Sha256(bytes);
        var text = DecodeText(bytes);
        var truncated = text.Length > maxChars;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            path = ToRelative(context, path),
            sha256 = hash,
            content = truncated ? text[..maxChars] : text,
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

    private string ReplaceText(
        ChatContext context,
        string relativePath,
        string expectedSha256,
        string oldText,
        string newText)
    {
        if (oldText.Length == 0)
            return Error("old_text_empty", relativePath);

        if (oldText.Length > 200_000 || newText.Length > 200_000)
            return Error("replacement_too_large", relativePath);

        var path = ResolveBoundedPath(context, relativePath);
        if (!File.Exists(path))
            return Error("file_not_found", relativePath);

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 4 * 1024 * 1024)
            return Error("file_too_large_for_edit_tool", relativePath);

        var actualHash = Sha256(bytes);
        expectedSha256 = expectedSha256.Trim().ToLowerInvariant();

        if (!string.Equals(
                actualHash,
                expectedSha256,
                StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "stale_file",
                path = ToRelative(context, path),
                expected_sha256 = expectedSha256,
                actual_sha256 = actualHash
            });
        }

        var text = DecodeText(bytes);
        var first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            return Error("old_text_not_found", relativePath);

        if (text.IndexOf(
                oldText,
                first + oldText.Length,
                StringComparison.Ordinal) >= 0)
        {
            return Error("old_text_not_unique", relativePath);
        }

        var previewOld = Bound(oldText, 1200);
        var previewNew = Bound(newText, 1200);
        var decision = MessageBox.Show(
            "The local model proposed a bounded text replacement.\n\n" +
            "File:\n" + ToRelative(context, path) +
            "\n\nReplace:\n" + previewOld +
            "\n\nWith:\n" + previewNew +
            "\n\nApply this edit?",
            "KHZ · Confirm file edit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (decision != MessageBoxResult.Yes)
        {
            _activity.Record(
                category: "ai",
                action: "tool.replace_text",
                target: context.ContextId,
                result: "DENIED",
                details: new
                {
                    pathCaptured = false,
                    userConfirmed = false,
                    aiUsed = true
                });

            return JsonSerializer.Serialize(new
            {
                ok = false,
                denied_by_user = true
            });
        }

        var updated = text[..first] + newText + text[(first + oldText.Length)..];
        var temp = path + ".khz-ai-" + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            WriteUtf8Atomically(path, temp, updated);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
            }
            throw;
        }

        var afterBytes = File.ReadAllBytes(path);
        var afterHash = Sha256(afterBytes);

        _activity.Record(
            category: "ai",
            action: "tool.replace_text",
            target: context.ContextId,
            result: "PASSED",
            details: new
            {
                pathCaptured = false,
                userConfirmed = true,
                aiUsed = true,
                beforeSha256 = actualHash,
                afterSha256 = afterHash,
                oldLength = oldText.Length,
                newLength = newText.Length
            });

        return JsonSerializer.Serialize(new
        {
            ok = true,
            path = ToRelative(context, path),
            before_sha256 = actualHash,
            after_sha256 = afterHash
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
        {
            var rootOnly = Path.GetFullPath(context.RootPath);
            RejectReparseTraversal(rootOnly, rootOnly);
            return rootOnly;
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                "Tool paths must be relative to the active workspace/folder.");
        }

        var root = Path.GetFullPath(context.RootPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(
            Path.Combine(root, relativePath));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
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

        RejectReparseTraversal(root, candidate);
        return candidate;
    }

    private static void RejectReparseTraversal(
        string root,
        string candidate)
    {
        var current = Path.GetFullPath(root);
        RejectIfExistingReparse(current);

        var relative = Path.GetRelativePath(current, candidate);
        if (relative == ".")
            return;

        foreach (var part in relative.Split(
                     new[]
                     {
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            RejectIfExistingReparse(current);
        }
    }

    private static void RejectIfExistingReparse(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Model tools do not traverse filesystem reparse points.");
        }
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
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        yield return file;
                }
                catch
                {
                }
            }

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

    private static void WriteUtf8Atomically(
        string path,
        string temp,
        string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Target directory could not be resolved.");

        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        using (var stream = new FileStream(
                   temp,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   64 * 1024,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(
                   stream,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
