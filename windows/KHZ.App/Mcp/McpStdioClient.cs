using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Mcp;

/// <summary>A tool advertised by a connected MCP server.</summary>
/// <param name="ServerName">Owning server.</param>
/// <param name="Name">Remote tool name.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Description">Behaviour description from the server.</param>
/// <param name="InputSchemaJson">JSON Schema for the tool's arguments.</param>
/// <param name="ReadOnly">The server claims the tool performs no mutation.</param>
internal sealed record McpRemoteTool(
    string ServerName,
    string Name,
    string Title,
    string Description,
    string InputSchemaJson,
    bool ReadOnly);

/// <summary>Result of a remote tool call.</summary>
/// <param name="Text">Concatenated text content blocks.</param>
/// <param name="IsError">The server flagged the call as failed.</param>
internal sealed record McpCallResult(string Text, bool IsError);

/// <summary>
/// Minimal MCP client over a child process's stdio.
/// </summary>
/// <remarks>
/// Design notes that matter for correctness:
/// <list type="bullet">
/// <item>Responses are correlated by JSON-RPC id through a pending-request map,
/// because a server may answer out of order or interleave notifications.</item>
/// <item>stderr is drained continuously into a bounded buffer. Not draining it
/// would eventually block the child process once the pipe filled.</item>
/// <item>Every call has a timeout, so a hung server degrades to a failed tool
/// call instead of a frozen chat turn.</item>
/// </list>
/// </remarks>
internal sealed class McpStdioClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private const int MaxStderrChars = 20_000;

    private readonly McpServerConfig _config;
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Task? _reader;
    private int _nextId;

    private McpStdioClient(McpServerConfig config, Process process)
    {
        _config = config;
        _process = process;
    }

    internal string ServerName => _config.Name;

    internal IReadOnlyList<McpRemoteTool> Tools { get; private set; } = [];

    /// <summary>Recent stderr output, for diagnostics in the UI.</summary>
    internal string Diagnostics
    {
        get
        {
            lock (_stderr)
                return _stderr.ToString();
        }
    }

    /// <summary>Starts the server, performs the handshake, and lists its tools.</summary>
    internal static async Task<McpStdioClient> ConnectAsync(
        McpServerConfig config,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(config.Command))
        {
            throw new FileNotFoundException(
                "MCP server executable was not found: " + config.Command,
                config.Command);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = config.Command,
            WorkingDirectory = config.WorkingDirectory
                               ?? Path.GetDirectoryName(config.Command)
                               ?? AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (var argument in config.Arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(
                "MCP server process failed to start: " + config.Name);
        }

        var client = new McpStdioClient(config, process);

        try
        {
            client._reader = Task.Run(client.PumpStdoutAsync);
            _ = Task.Run(client.PumpStderrAsync);

            await client.HandshakeAsync(timeout, cancellationToken).ConfigureAwait(false);
            await client.RefreshToolsAsync(timeout, cancellationToken).ConfigureAwait(false);

            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Calls a remote tool by its server-local name.</summary>
    internal async Task<McpCallResult> CallToolAsync(
        string toolName,
        string argumentsJson,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = JsonNode.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson)
        };

        var result = await RequestAsync("tools/call", parameters, timeout, cancellationToken)
            .ConfigureAwait(false);

        var builder = new StringBuilder();

        if (result["content"] is JsonArray content)
        {
            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                    builder.AppendLine(block["text"]?.GetValue<string>() ?? string.Empty);
            }
        }

        var isError = result["isError"]?.GetValue<bool>() ?? false;
        return new McpCallResult(builder.ToString().Trim(), isError);
    }

    private async Task HandshakeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await RequestAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "KHZ Workstation",
                    ["version"] = "0.1.0"
                }
            },
            timeout,
            cancellationToken).ConfigureAwait(false);

        await NotifyAsync("notifications/initialized").ConfigureAwait(false);
    }

    private async Task RefreshToolsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), timeout, cancellationToken)
            .ConfigureAwait(false);

        var tools = new List<McpRemoteTool>();

        if (result["tools"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var name = entry["name"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var readOnly = entry["annotations"]?["readOnlyHint"]?.GetValue<bool>() ?? false;

                tools.Add(new McpRemoteTool(
                    ServerName: _config.Name,
                    Name: name,
                    Title: entry["title"]?.GetValue<string>() ?? name,
                    Description: entry["description"]?.GetValue<string>() ?? string.Empty,
                    InputSchemaJson: entry["inputSchema"]?.ToJsonString()
                                     ?? """{"type":"object","properties":{}}""",
                    ReadOnly: readOnly));
            }
        }

        Tools = tools;
    }

    private async Task<JsonObject> RequestAsync(
        string method,
        JsonNode parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                "MCP server '" + _config.Name + "' exited with code " + _process.ExitCode
                + ". " + Diagnostics);
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id] = completion;

        try
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            }).ConfigureAwait(false);

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutSource.Token,
                cancellationToken,
                _lifetime.Token);

            await using var registration = linked.Token.Register(() => completion.TrySetCanceled())
                .ConfigureAwait(false);

            return await completion.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "MCP server '" + _config.Name + "' did not answer " + method + " within "
                + timeout.TotalSeconds + "s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method)
        => WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = new JsonObject()
        });

    private async Task WriteAsync(JsonObject message)
    {
        await _writeLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);

        try
        {
            await _process.StandardInput
                .WriteLineAsync(message.ToJsonString())
                .ConfigureAwait(false);

            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardOutput
                    .ReadLineAsync(_lifetime.Token)
                    .ConfigureAwait(false);

                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonObject? message;

                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    // A server that prints non-protocol text to stdout is
                    // misbehaving; skip the line rather than tearing down.
                    continue;
                }

                if (message?["id"] is null)
                    continue;

                if (!message["id"]!.AsValue().TryGetValue<int>(out var id))
                    continue;

                if (!_pending.TryRemove(id, out var completion))
                    continue;

                if (message["error"] is JsonObject error)
                {
                    completion.TrySetException(new InvalidOperationException(
                        "MCP error " + error["code"] + ": " + error["message"]));

                    continue;
                }

                completion.TrySetResult(message["result"] as JsonObject ?? new JsonObject());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new InvalidOperationException(
                    "MCP server '" + _config.Name + "' closed the connection. " + Diagnostics));
            }

            _pending.Clear();
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardError
                    .ReadLineAsync(_lifetime.Token)
                    .ConfigureAwait(false);

                if (line is null)
                    break;

                lock (_stderr)
                {
                    if (_stderr.Length < MaxStderrChars)
                        _stderr.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();

                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _process.Dispose();
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
