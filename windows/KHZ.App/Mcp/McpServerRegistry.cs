using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KHZ.App.Mcp;

/// <summary>One configured MCP server.</summary>
/// <param name="Name">Short identifier used to namespace the server's tools.</param>
/// <param name="Command">Absolute path to the executable.</param>
/// <param name="Arguments">Arguments passed verbatim.</param>
/// <param name="WorkingDirectory">Optional working directory.</param>
/// <param name="Enabled">Whether the app should connect on startup.</param>
/// <param name="Description">Human note shown in the UI.</param>
internal sealed record McpServerConfig(
    string Name,
    string Command,
    string[] Arguments,
    string? WorkingDirectory,
    bool Enabled,
    string? Description);

/// <summary>
/// Reads the local MCP server configuration.
/// </summary>
/// <remarks>
/// Configuration is a file, not UI state, for a specific reason: launching an
/// MCP server means starting a process with arguments, which is the same class
/// of authority as the terminal. Keeping it in an explicit, inspectable file
/// under the user profile makes that grant reviewable and revocable, and keeps
/// it out of the model's reach entirely.
/// </remarks>
internal static class McpServerRegistry
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Canonical configuration path.</summary>
    internal static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KHZ",
        "mcp-servers.json");

    /// <summary>
    /// Loads enabled server configurations. Returns an empty list when the file
    /// is absent or malformed; a broken config must not prevent the app from
    /// starting.
    /// </summary>
    internal static IReadOnlyList<McpServerConfig> Load(out string? error)
    {
        error = null;
        var path = ConfigPath;

        try
        {
            if (!File.Exists(path))
            {
                WriteDefault(path);
                return [];
            }

            var document = JsonSerializer.Deserialize<RegistryFile>(
                File.ReadAllText(path),
                Options);

            if (document?.Servers is null)
                return [];

            var results = new List<McpServerConfig>();

            foreach (var entry in document.Servers)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)
                    || string.IsNullOrWhiteSpace(entry.Command))
                {
                    continue;
                }

                var command = entry.Command.Trim();

                // A relative command would resolve against whatever the app's
                // current directory happens to be, which is not a stable or
                // reviewable grant. Require an absolute path.
                if (!Path.IsPathFullyQualified(command))
                {
                    error = "Server '" + entry.Name
                            + "' was skipped: 'command' must be an absolute path.";

                    continue;
                }

                results.Add(new McpServerConfig(
                    Name: Sanitize(entry.Name),
                    Command: command,
                    Arguments: entry.Arguments ?? [],
                    WorkingDirectory: string.IsNullOrWhiteSpace(entry.WorkingDirectory)
                        ? null
                        : entry.WorkingDirectory.Trim(),
                    Enabled: entry.Enabled ?? true,
                    Description: entry.Description));
            }

            return results
                .GroupBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
        catch (JsonException exception)
        {
            error = "mcp-servers.json could not be parsed: " + exception.Message;
            return [];
        }
        catch (IOException exception)
        {
            error = "mcp-servers.json could not be read: " + exception.Message;
            return [];
        }
        catch (UnauthorizedAccessException exception)
        {
            error = "mcp-servers.json could not be read: " + exception.Message;
            return [];
        }
    }

    /// <summary>Restricts a server name to characters safe in a tool identifier.</summary>
    internal static string Sanitize(string name)
    {
        var cleaned = new string(name
            .Trim()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
            .ToArray())
            .Trim('_')
            .ToLowerInvariant();

        return cleaned.Length == 0
            ? "server"
            : cleaned[..Math.Min(cleaned.Length, 24)];
    }

    /// <summary>
    /// Writes a disabled, self-documenting example on first run so the feature
    /// is discoverable without granting anything by default.
    /// </summary>
    private static void WriteDefault(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var sample = new RegistryFile
            {
                Comment =
                    "KHZ MCP servers. 'command' must be an absolute path. Add --allow-writes "
                    + "only when you intend that server to modify files. Set enabled to true "
                    + "to connect on startup.",
                Servers =
                [
                    new RegistryEntry
                    {
                        Name = "khz-local",
                        Command = Path.Combine(
                            AppContext.BaseDirectory,
                            "khz-mcp-server.exe"),
                        Arguments = ["--root", "C:\\\\Workspaces\\\\example"],
                        Enabled = false,
                        Description =
                            "Bundled KHZ tool server (files, DOCX/XLSX/PPTX, PDF export, "
                            + "PowerShell). Read-only until --allow-writes is added."
                    }
                ]
            };

            File.WriteAllText(path, JsonSerializer.Serialize(sample, Options));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RegistryFile
    {
        [JsonPropertyName("_comment")]
        public string? Comment { get; set; }

        [JsonPropertyName("servers")]
        public List<RegistryEntry>? Servers { get; set; }
    }

    private sealed class RegistryEntry
    {
        public string? Name { get; set; }

        public string? Command { get; set; }

        public string[]? Arguments { get; set; }

        public string? WorkingDirectory { get; set; }

        public bool? Enabled { get; set; }

        public string? Description { get; set; }
    }
}
