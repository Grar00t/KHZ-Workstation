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
    private const int MaxReadBytes = 4 * 1024 * 1024;
    private const int MaxEditChars = 200_000;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> TextExtensions =
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
        Tool(
            "list_directory",
            "List files and directories below the active workspace/folder. Paths are relative.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}"""),
        Tool(
            "read_file",
            "Read a UTF-8 text file below the active workspace/folder and return its SHA-256.",
            """{"type":"object","properties":{"path":{"type":"string"},"max_chars":{"type":"integer","minimum":1,"maximum":200000}},"required":["path"],"additionalProperties":false}"""),
        Tool(
            "search_text",
            "Search local text files for a literal string.",
            """{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string"}},"required":["query"],"additionalProperties":false}"""),
        Tool(
            "inspect_repository",
            "Inspect local Git root, branch, HEAD, changes, and recent commits.",
            """{"type":"object","properties":{"path":{"type":"string"}},"additionalProperties":false}"""),
        Tool(
            "replace_text",
            "Replace one exact text occurrence in a UTF-8 workspace file. Requires the current SHA-256 and user confirmation.",
            """{"type":"object","properties":{"path":{"type":"string"},"expected_sha256":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","expected_sha256","old_text","new_text"],"additionalProperties":false}""",
            requiresConfirmation: true),
        Tool(
            "run_powershell",
            "Run one PowerShell command in the active workspace/folder. The exact command requires user confirmation.",
            """{"type":"object","properties":{"command":{"type":"string"},"timeout_seconds":{"type":"integer","minimum":1,"maximum":300}},"required":["command"],"additionalProperties":false}""",
            requiresConfirmation: true)
    ];

    internal async Task<string> ExecuteAsync(
        ChatToolCall call,
        ChatContext context,
        CancellationToken cancellationToken)
    {
        using var parsed = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(call.ArgumentsJson)
                ? "{}"
                : call.ArgumentsJson);

        var args = parsed.RootElement;

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

            _ => Error("unknown_tool", call.Name)
        };
    }

    private static ChatToolDefinition Tool(
        string name,
        string description,
        string parameters,
        bool requiresConfirmation = false)
        => new(name, description, parameters, requiresConfirmation);

    private static string ListDirectory(
        ChatContext context,
        string relativePath)
    {
        var path = ResolvePath(context, relativePath);
        if (!Directory.Exists(path))
            return Error("directory_not_found", relativePath);

        var entries = new List<object>();

        foreach (var item in new DirectoryInfo(path)
                     .EnumerateFileSystemInfos()
                     .OrderByDescending(item => item is DirectoryInfo)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(250))
        {
            if (string.Equals(item.Name, ".khz", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsReparsePoint(item.FullName))
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
            path = Relative(context, path),
            entries
        });
    }

    private static string ReadFile(
        ChatContext context,
        string relativePath,
        int maxChars)
    {
        var path = ResolvePath(context, relativePath);
        if (!File.Exists(path))
            return Error("file_not_found", relativePath);

        var bytes = ReadBoundedBytes(path);
        var text = DecodeUtf8(bytes, out _);
        maxChars = Math.Clamp(maxChars, 1, MaxEditChars);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            path = Relative(context, path),
            sha256 = Sha256(bytes),
            content = text.Length <= maxChars ? text : text[..maxChars],
            truncated = text.Length > maxChars
        });
    }

    private static string SearchText(
        ChatContext context,
        string query,
        string relativePath)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 500)
            return Error("invalid_query", "Query length must be 1-500 characters.");

        var root = ResolvePath(context, relativePath);
        if (!Directory.Exists(root))
            return Error("directory_not_found", relativePath);

        var hits = new List<object>();
        var inspected = 0;

        foreach (var file in EnumerateFilesSafe(root))
        {
            if (inspected >= 400 || hits.Count >= 80)
                break;

            if (!TextExtensions.Contains(Path.GetExtension(file)))
                continue;

            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1024 * 1024)
                    continue;

                var bytes = File.ReadAllBytes(file);
                var text = DecodeUtf8(bytes, out _);
                inspected++;

                using var reader = new StringReader(text);
                var lineNumber = 0;
                string? line;

                while ((line = reader.ReadLine()) is not null)
                {
                    lineNumber++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        continue;

                    hits.Add(new
                    {
                        path = Relative(context, file),
                        line = lineNumber,
                        text = Bound(line, 500)
                    });

                    if (hits.Count >= 80)
                        break;
                }
            }
            catch
            {
                // Unreadable/non-UTF8 files are excluded from this text tool.
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
        var path = ResolvePath(context, relativePath);
        var snapshot = await _repositories.InspectAsync(path, cancellationToken);

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

        if (oldText.Length > MaxEditChars || newText.Length > MaxEditChars)
            return Error("replacement_too_large", relativePath);

        var path = ResolvePath(context, relativePath);
        if (!File.Exists(path))
            return Error("file_not_found", relativePath);

        var bytes = ReadBoundedBytes(path);
        var actualHash = Sha256(bytes);
        var expected = expectedSha256.Trim().ToLowerInvariant();

        if (!string.Equals(actualHash, expected, StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "stale_file",
                path = Relative(context, path),
                expected_sha256 = expected,
                actual_sha256 = actualHash
            });
        }

        var text = DecodeUtf8(bytes, out var hadBom);
        var first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            return Error("old_text_not_found", relativePath);

        if (text.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
            return Error("old_text_not_unique", relativePath);

        var decision = MessageBox.Show(
            "The local model proposed this file edit:\n\n" +
            "File:\n" + Relative(context, path) +
            "\n\nReplace:\n" + Bound(oldText, 1200) +
            "\n\nWith:\n" + Bound(newText, 1200) +
            "\n\nApply this edit?",
            "KHZ · Confirm file edit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (decision != MessageBoxResult.Yes)
        {
            AuditMutation("tool.replace_text", context, "DENIED", false);
            return JsonSerializer.Serialize(new { ok = false, denied_by_user = true });
        }

        var updated = text[..first] + newText + text[(first + oldText.Length)..];
        WriteTextAtomically(path, updated, hadBom);

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
            path = Relative(context, path),
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
            return Error("invalid_command", "Command length must be 1-16384 characters.");

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
            AuditMutation("tool.run_powershell", context, "DENIED", false);
            return JsonSerializer.Serialize(new { ok = false, denied_by_user = true });
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

        var passed = result.Status == TerminalExecutionStatus.Exited
                     && result.ExitCode == 0;

        _activity.Record(
            category: "ai",
            action: "tool.run_powershell",
            target: context.ContextId,
            result: passed ? "PASSED" : "FAILED",
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
            ok = passed,
            status = result.Status.ToString(),
            exit_code = result.ExitCode,
            stdout = Bound(result.StandardOutput, 100_000),
            stderr = Bound(result.StandardError, 40_000)
        });
    }

    private void AuditMutation(
        string action,
        ChatContext context,
        string result,
        bool confirmed)
        => _activity.Record(
            category: "ai",
            action: action,
            target: context.ContextId,
            result: result,
            details: new
            {
                userConfirmed = confirmed,
                aiUsed = true,
                rawPayloadCaptured = false
            });

    private static string ResolvePath(
        ChatContext context,
        string relativePath)
    {
        var root = Path.GetFullPath(context.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        relativePath = (relativePath ?? string.Empty).Trim();
        var candidate = relativePath.Length == 0 || relativePath == "."
            ? root
            : Path.IsPathRooted(relativePath)
                ? throw new InvalidOperationException("Tool paths must be relative.")
                : Path.GetFullPath(Path.Combine(root, relativePath));

        var prefix = root + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tool path escapes the active workspace/folder.");
        }

        var parts = Path.GetRelativePath(root, candidate).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Any(part => string.Equals(part, ".khz", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Internal .khz metadata is not exposed to model tools.");

        RejectReparseTraversal(root, candidate);
        return candidate;
    }

    private static void RejectReparseTraversal(string root, string candidate)
    {
        var current = root;
        RejectIfReparse(current);

        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
            return;

        foreach (var part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            RejectIfReparse(current);
        }
    }

    private static void RejectIfReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && IsReparsePoint(path))
            throw new InvalidOperationException("Model tools do not traverse filesystem reparse points.");
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var include = false;
                try
                {
                    include = !IsReparsePoint(file);
                }
                catch
                {
                }

                if (include)
                    yield return file;
            }

            foreach (var child in directories)
            {
                if (string.Equals(Path.GetFileName(child), ".khz", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (!IsReparsePoint(child))
                        pending.Push(child);
                }
                catch
                {
                }
            }
        }
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxReadBytes)
            throw new InvalidDataException("File is too large for the local text tool.");
        return File.ReadAllBytes(path);
    }

    private static string DecodeUtf8(byte[] bytes, out bool hadBom)
    {
        hadBom = bytes.Length >= 3
                 && bytes[0] == 0xEF
                 && bytes[1] == 0xBB
                 && bytes[2] == 0xBF;

        var offset = hadBom ? 3 : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static void WriteTextAtomically(
        string path,
        string content,
        bool emitBom)
    {
        var temp = path + ".khz-ai-" + Guid.NewGuid().ToString("N") + ".tmp";
        var encoding = new UTF8Encoding(emitBom, throwOnInvalidBytes: true);

        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Relative(ChatContext context, string path)
        => Path.GetRelativePath(context.RootPath, path);

    private static string RequireString(JsonElement args, string name)
        => GetString(args, name)
           ?? throw new InvalidDataException($"Tool argument '{name}' is required.");

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt32(JsonElement args, string name, int fallback)
        => args.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static string Error(string code, string detail)
        => JsonSerializer.Serialize(new { ok = false, error = code, detail });

    private static string Bound(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
