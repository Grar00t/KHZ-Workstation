using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Tools.Safety;

namespace KHZ.Tools.Tools;

/// <summary>Shared limits for read-side tools.</summary>
public static class ReadLimits
{
    /// <summary>Maximum bytes read from a single file.</summary>
    public const int MaxReadBytes = 4 * 1024 * 1024;

    /// <summary>Extensions treated as UTF-8 text by read_file/search_text.</summary>
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl", ".ndjson", ".csv", ".tsv", ".cs", ".xaml",
        ".xml", ".yaml", ".yml", ".toml", ".py", ".ps1", ".psm1", ".cmd", ".bat",
        ".c", ".h", ".cpp", ".hpp", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".html",
        ".css", ".sql", ".ini", ".config", ".sln", ".csproj", ".props", ".targets",
        ".editorconfig", ".gitignore", ".env", ".log", ".rst", ".sh"
    };

    public static bool IsTextFile(string path)
        => TextExtensions.Contains(Path.GetExtension(path));
}

/// <summary>Lists the immediate children of a workspace-relative directory.</summary>
public sealed class ListDirectoryTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "list_directory",
        Title: "List directory",
        Description: "Lists immediate files and folders under a workspace-relative path. "
                     + "Reparse points and the internal .khz folder are omitted. "
                     + "Returns at most 500 entries, sorted folders first.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Workspace-relative directory path. Omit or use '.' for the root."
            }
          },
          "additionalProperties": false
        }
        """,
        Risk: ToolRisk.Read,
        RequiresConfirmation: false);

    public Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var relative = ToolArgs.OptionalString(arguments, "path") ?? ".";
        var absolute = context.Resolve(relative);

        if (!Directory.Exists(absolute))
        {
            throw new ToolFailureException(
                "directory_not_found",
                "Directory not found: " + context.Relative(absolute));
        }

        var entries = new List<object>();
        var truncated = false;

        foreach (var child in Directory
                     .EnumerateFileSystemEntries(absolute)
                     .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(child);

            if (string.Equals(name, WorkspaceGuard.InternalMetadataFolder, StringComparison.OrdinalIgnoreCase))
                continue;

            if (WorkspaceGuard.IsReparsePoint(child))
                continue;

            if (entries.Count >= 500)
            {
                truncated = true;
                break;
            }

            var isDirectory = Directory.Exists(child);

            entries.Add(new
            {
                name,
                type = isDirectory ? "directory" : "file",
                sizeBytes = isDirectory ? (long?)null : new FileInfo(child).Length,
                modifiedUtc = File.GetLastWriteTimeUtc(child).ToString("O")
            });
        }

        var ordered = entries
            .OrderByDescending(entry => (string)entry.GetType().GetProperty("type")!.GetValue(entry)! == "directory")
            .ToList();

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(absolute),
            count = ordered.Count,
            truncated,
            entries = ordered
        });

        return Task.FromResult(new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new { pathCaptured = false, entryCount = ordered.Count }));
    }
}

/// <summary>Reads a UTF-8 text file and returns its content plus its SHA-256.</summary>
public sealed class ReadFileTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "read_file",
        Title: "Read text file",
        Description: "Reads a UTF-8 text file under the workspace root and returns its content "
                     + "with its SHA-256. Pass that hash back as expected_sha256 when editing. "
                     + "Limited to 4 MiB and to known text extensions; use office_read_* for "
                     + "DOCX/XLSX/PPTX.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Workspace-relative file path."
            }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """,
        Risk: ToolRisk.Read,
        RequiresConfirmation: false);

    public Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var absolute = context.Resolve(ToolArgs.RequireString(arguments, "path"));

        if (!File.Exists(absolute))
        {
            throw new ToolFailureException(
                "file_not_found",
                "File not found: " + context.Relative(absolute));
        }

        if (!ReadLimits.IsTextFile(absolute))
        {
            throw new ToolFailureException(
                "unsupported_file_type",
                "read_file handles UTF-8 text only. For Office documents use "
                + "office_read_document, office_read_sheet, or office_read_slides.");
        }

        var info = new FileInfo(absolute);

        if (info.Length > ReadLimits.MaxReadBytes)
        {
            throw new ToolFailureException(
                "file_too_large",
                "File exceeds the 4 MiB read limit (" + info.Length + " bytes).");
        }

        var bytes = File.ReadAllBytes(absolute);
        string content;

        try
        {
            content = Hashes.DecodeUtf8(bytes, out _);
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new ToolFailureException(
                "not_utf8",
                "File is not valid UTF-8 text.");
        }

        var sha = Hashes.Sha256(bytes);

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(absolute),
            sha256 = sha,
            sizeBytes = bytes.LongLength,
            content
        });

        return Task.FromResult(new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new { pathCaptured = false, sizeBytes = bytes.LongLength, sha256 = sha }));
    }
}

/// <summary>Literal, case-insensitive text search across workspace text files.</summary>
public sealed class SearchTextTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "search_text",
        Title: "Search text",
        Description: "Case-insensitive literal substring search across UTF-8 text files under a "
                     + "workspace-relative folder. Returns file, line number, and trimmed line. "
                     + "Not a regular expression search.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Literal text to find (2 to 200 characters)." },
            "path": { "type": "string", "description": "Workspace-relative folder to search. Defaults to the root." },
            "max_results": { "type": "integer", "description": "1 to 200. Defaults to 50." }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """,
        Risk: ToolRisk.Read,
        RequiresConfirmation: false);

    public Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var query = ToolArgs.RequireString(arguments, "query");

        if (query.Length is < 2 or > 200)
        {
            throw new ToolFailureException(
                "invalid_query",
                "Search query must be between 2 and 200 characters.");
        }

        var scope = context.Resolve(ToolArgs.OptionalString(arguments, "path") ?? ".");
        var maxResults = ToolArgs.OptionalInt(arguments, "max_results", 50, 1, 200);

        if (!Directory.Exists(scope))
        {
            throw new ToolFailureException(
                "directory_not_found",
                "Directory not found: " + context.Relative(scope));
        }

        var matches = new List<object>();
        var filesScanned = 0;
        var truncated = false;

        foreach (var file in WorkspaceGuard.EnumerateFiles(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ReadLimits.IsTextFile(file))
                continue;

            var info = new FileInfo(file);

            if (info.Length > ReadLimits.MaxReadBytes)
                continue;

            filesScanned++;
            string[] lines;

            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (System.Text.DecoderFallbackException)
            {
                continue;
            }

            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                var text = lines[index].Trim();

                matches.Add(new
                {
                    path = context.Relative(file),
                    line = index + 1,
                    text = text.Length > 400 ? text[..400] : text
                });
            }

            if (truncated)
                break;
        }

        var json = ToolArgs.Serialize(new
        {
            query,
            scope = context.Relative(scope),
            filesScanned,
            matchCount = matches.Count,
            truncated,
            matches
        });

        return Task.FromResult(new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new
            {
                pathCaptured = false,
                queryCaptured = false,
                queryLength = query.Length,
                filesScanned,
                matchCount = matches.Count
            }));
    }
}
