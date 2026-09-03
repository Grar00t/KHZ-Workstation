using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.Mcp.Server.JsonRpc;

/// <summary>Standard JSON-RPC 2.0 error codes plus MCP-specific additions.</summary>
public static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>
/// Newline-delimited JSON-RPC 2.0 over stdin/stdout, as used by the MCP stdio
/// transport.
/// </summary>
/// <remarks>
/// Two invariants matter and are enforced here:
/// <list type="bullet">
/// <item><b>stdout carries protocol only.</b> Any diagnostic text written to
/// stdout would corrupt the stream, so logging goes to stderr exclusively.</item>
/// <item><b>One message per line.</b> Responses are serialised without
/// indentation and flushed immediately, so a host that reads line-by-line never
/// blocks waiting for a buffer.</item>
/// </list>
/// </remarks>
public sealed class JsonRpcTransport : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonRpcTransport()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        _input = new StreamReader(Console.OpenStandardInput(), encoding);
        _output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = false };
        _log = Console.Error;
    }

    /// <summary>Writes a diagnostic line to stderr. Never to stdout.</summary>
    public void Log(string message)
    {
        _log.WriteLine("[khz-mcp] " + message);
        _log.Flush();
    }

    /// <summary>Reads the next message, or null at end of stream.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
                return null;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                if (JsonNode.Parse(line) is JsonObject message)
                    return message;

                await WriteErrorAsync(null, ErrorCodes.InvalidRequest, "Message must be a JSON object.")
                    .ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                await WriteErrorAsync(null, ErrorCodes.ParseError, exception.Message)
                    .ConfigureAwait(false);
            }
        }
    }

    public Task WriteResultAsync(JsonNode? id, JsonNode? result)
        => WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result ?? new JsonObject()
        });

    public Task WriteErrorAsync(JsonNode? id, int code, string message)
        => WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        });

    private async Task WriteAsync(JsonObject message)
    {
        var payload = message.ToJsonString(SerializerOptions);

        await _writeLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await _output.WriteLineAsync(payload).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _output.Flush();
        _output.Dispose();
        _input.Dispose();
        _writeLock.Dispose();
    }
}
