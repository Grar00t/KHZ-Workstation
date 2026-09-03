using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KHZ.App.Chat;

namespace KHZ.App.Mcp;

/// <summary>Connection state of one configured MCP server.</summary>
/// <param name="Name">Server name.</param>
/// <param name="Connected">Handshake completed and tools listed.</param>
/// <param name="ToolCount">Number of tools advertised.</param>
/// <param name="Detail">Human-readable status or failure reason.</param>
internal sealed record McpServerStatus(
    string Name,
    bool Connected,
    int ToolCount,
    string Detail);

/// <summary>
/// Owns the connected MCP servers and exposes their tools to the chat runtime.
/// </summary>
/// <remarks>
/// Two invariants protect the built-in tool surface:
/// <list type="number">
/// <item><b>Namespacing.</b> A remote tool is advertised as
/// <c>mcp__server__tool</c>, so a third-party server cannot register a name
/// that shadows <c>replace_text</c> or <c>run_powershell</c> and silently
/// intercept those calls.</item>
/// <item><b>Confirmation floor.</b> A remote tool is treated as requiring
/// confirmation unless it declares <c>readOnlyHint</c>. The server's own claim
/// can only ever raise the requirement, never lower it, because the server is
/// outside the trust boundary.</item>
/// </list>
/// </remarks>
internal sealed class McpToolBridge : IAsyncDisposable
{
    /// <summary>Separator used in namespaced tool names.</summary>
    internal const string Separator = "__";

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(3);

    private readonly List<McpStdioClient> _clients = [];
    private readonly List<McpServerStatus> _statuses = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal IReadOnlyList<McpServerStatus> Statuses
        => new ReadOnlyCollection<McpServerStatus>(_statuses);

    /// <summary>Connects every enabled configured server. Failures are reported, not thrown.</summary>
    internal async Task<IReadOnlyList<McpServerStatus>> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);

            var configs = McpServerRegistry.Load(out var configError);

            if (configError is not null)
                _statuses.Add(new McpServerStatus("(config)", false, 0, configError));

            foreach (var config in configs.Where(candidate => candidate.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var client = await McpStdioClient
                        .ConnectAsync(config, HandshakeTimeout, cancellationToken)
                        .ConfigureAwait(false);

                    _clients.Add(client);

                    _statuses.Add(new McpServerStatus(
                        config.Name,
                        true,
                        client.Tools.Count,
                        "Connected. " + client.Tools.Count + " tool(s) available."));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _statuses.Add(new McpServerStatus(
                        config.Name,
                        false,
                        0,
                        exception.Message));
                }
            }

            return Statuses;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Namespaced tool definitions to merge into the model's tool list.</summary>
    internal IReadOnlyList<ChatToolDefinition> Definitions()
        => _clients
            .SelectMany(client => client.Tools)
            .Select(tool => new ChatToolDefinition(
                Name: QualifiedName(tool.ServerName, tool.Name),
                Description: "[MCP: " + tool.ServerName + "] "
                             + (string.IsNullOrWhiteSpace(tool.Description)
                                 ? tool.Title
                                 : tool.Description),
                ParametersJson: tool.InputSchemaJson,
                RequiresConfirmation: !tool.ReadOnly))
            .ToList();

    /// <summary>True when the name belongs to a bridged MCP tool.</summary>
    internal bool Handles(string toolName)
        => toolName.StartsWith("mcp" + Separator, StringComparison.Ordinal)
           && Resolve(toolName) is not null;

    /// <summary>Invokes a bridged tool. Returns a JSON error envelope on failure.</summary>
    internal async Task<string> InvokeAsync(
        string qualifiedName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(qualifiedName);

        if (resolved is null)
        {
            return Error(
                "unknown_tool",
                "No connected MCP server provides '" + qualifiedName + "'.");
        }

        var (client, toolName) = resolved.Value;

        try
        {
            var result = await client
                .CallToolAsync(toolName, argumentsJson, CallTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsError && result.Text.Length > 0)
                return result.Text;

            return result.Text.Length == 0
                ? Error("empty_result", "The MCP server returned no content.")
                : result.Text;
        }
        catch (TimeoutException exception)
        {
            return Error("mcp_timeout", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error("mcp_error", exception.Message);
        }
    }

    private (McpStdioClient Client, string ToolName)? Resolve(string qualifiedName)
    {
        foreach (var client in _clients)
        {
            foreach (var tool in client.Tools)
            {
                if (string.Equals(
                        QualifiedName(tool.ServerName, tool.Name),
                        qualifiedName,
                        StringComparison.Ordinal))
                {
                    return (client, tool.Name);
                }
            }
        }

        return null;
    }

    private static string QualifiedName(string server, string tool)
        => "mcp" + Separator + McpServerRegistry.Sanitize(server) + Separator + tool;

    private static string Error(string code, string message)
        => System.Text.Json.JsonSerializer.Serialize(new { error = message, code });

    private async Task DisconnectCoreAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync().ConfigureAwait(false);

        _clients.Clear();
        _statuses.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
