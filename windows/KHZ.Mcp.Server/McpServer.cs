using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Mcp.Server.JsonRpc;
using KHZ.Tools.Tools;

namespace KHZ.Mcp.Server;

/// <summary>MCP request dispatcher over the KHZ tool router.</summary>
public sealed class McpServer
{
    /// <summary>MCP revision this server implements.</summary>
    public const string ProtocolVersion = "2025-06-18";

    private readonly JsonRpcTransport _transport;
    private readonly ToolRouter _router;
    private readonly ToolContext _context;
    private readonly bool _writesAllowed;

    public McpServer(
        JsonRpcTransport transport,
        ToolRouter router,
        ToolContext context,
        bool writesAllowed)
    {
        _transport = transport;
        _router = router;
        _context = context;
        _writesAllowed = writesAllowed;
    }

    /// <summary>Reads and services messages until stdin closes.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (message is null)
            {
                _transport.Log("stdin closed; shutting down.");
                return;
            }

            var id = message["id"];
            var method = message["method"]?.GetValue<string>();

            if (method is null)
            {
                // A message with no method is a response to something we never
                // sent; ignore it rather than answering and desynchronising.
                continue;
            }

            try
            {
                await DispatchAsync(id, method, message["params"], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _transport.Log("unhandled error in " + method + ": " + exception.Message);

                if (id is not null)
                {
                    await _transport
                        .WriteErrorAsync(id, ErrorCodes.InternalError, exception.Message)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private async Task DispatchAsync(
        JsonNode? id,
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await _transport.WriteResultAsync(id, Initialize()).ConfigureAwait(false);
                return;

            case "notifications/initialized":
            case "notifications/cancelled":
                // Notifications carry no id and must not be answered.
                return;

            case "ping":
                await _transport.WriteResultAsync(id, new JsonObject()).ConfigureAwait(false);
                return;

            case "tools/list":
                await _transport.WriteResultAsync(id, ListTools()).ConfigureAwait(false);
                return;

            case "tools/call":
                await CallToolAsync(id, parameters, cancellationToken).ConfigureAwait(false);
                return;

            case "resources/list":
                await _transport
                    .WriteResultAsync(id, new JsonObject { ["resources"] = new JsonArray() })
                    .ConfigureAwait(false);
                return;

            case "prompts/list":
                await _transport
                    .WriteResultAsync(id, new JsonObject { ["prompts"] = new JsonArray() })
                    .ConfigureAwait(false);
                return;

            default:
                if (id is not null)
                {
                    await _transport
                        .WriteErrorAsync(id, ErrorCodes.MethodNotFound, "Unsupported method: " + method)
                        .ConfigureAwait(false);
                }

                return;
        }
    }

    private JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = false }
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "khz-workstation",
            ["version"] = "0.1.0"
        },
        ["instructions"] =
            "KHZ Workstation local tools. All paths are relative to the workspace root the "
            + "server was started with; absolute paths, parent traversal, reparse points, and "
            + "the internal .khz folder are refused. Read a file or document first and pass the "
            + "returned sha256 back as expected_sha256 when writing, otherwise the write is "
            + "refused as stale. Office tools operate on the OOXML package directly, so no "
            + "Office installation is required."
            + (_writesAllowed
                ? " This instance was started with --allow-writes: mutating tools will execute."
                : " This instance is read-only: mutating tools return denied_by_user. Restart the "
                  + "server with --allow-writes to permit changes.")
    };

    private JsonObject ListTools()
    {
        var tools = new JsonArray();

        foreach (var descriptor in _router.Descriptors)
        {
            tools.Add(new JsonObject
            {
                ["name"] = descriptor.Name,
                ["title"] = descriptor.Title,
                ["description"] = descriptor.Description
                                  + (descriptor.RequiresConfirmation && !_writesAllowed
                                      ? " NOTE: this server instance is read-only, so this tool "
                                        + "will be refused."
                                      : string.Empty),
                ["inputSchema"] = JsonNode.Parse(descriptor.ParametersJson),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = descriptor.ReadOnly,
                    ["destructiveHint"] = !descriptor.ReadOnly,
                    ["idempotentHint"] = descriptor.ReadOnly,
                    ["openWorldHint"] = false
                }
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private async Task CallToolAsync(
        JsonNode? id,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            await _transport
                .WriteErrorAsync(id, ErrorCodes.InvalidParams, "'name' is required.")
                .ConfigureAwait(false);

            return;
        }

        if (!_router.TryGetDescriptor(name, out var descriptor))
        {
            await _transport
                .WriteErrorAsync(id, ErrorCodes.InvalidParams, "Unknown tool: " + name)
                .ConfigureAwait(false);

            return;
        }

        var argumentsJson = parameters?["arguments"]?.ToJsonString() ?? "{}";

        var payload = await _router
            .InvokeAsync(name, argumentsJson, _context, cancellationToken)
            .ConfigureAwait(false);

        var isError = IsErrorPayload(payload);

        _transport.Log(
            "tools/call " + name + " risk=" + descriptor!.Risk
            + " result=" + (isError ? "error" : "ok"));

        await _transport.WriteResultAsync(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload
                }
            },
            ["isError"] = isError
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects the router's error envelope so the host can surface a failed tool
    /// call as a tool error rather than as a successful result.
    /// </summary>
    private static bool IsErrorPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("code", out _)
                   && document.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
