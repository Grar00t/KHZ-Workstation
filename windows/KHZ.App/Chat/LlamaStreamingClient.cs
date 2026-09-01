using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Chat;

/// <summary>A tool call requested by the model.</summary>
internal sealed record AgentToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>One entry in the conversation sent to the model.</summary>
/// <param name="Role">"system", "user", "assistant", or "tool".</param>
/// <param name="Content">Message text.</param>
/// <param name="ToolCallId">For tool results: the id being answered.</param>
/// <param name="ToolCalls">For assistant turns: the calls that were requested.</param>
internal sealed record AgentMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null);

/// <summary>Outcome of one model turn.</summary>
internal sealed record AgentTurn(
    string Content,
    IReadOnlyList<AgentToolCall> ToolCalls,
    string FinishReason);

/// <summary>
/// Streaming OpenAI-compatible chat client for the local llama.cpp server.
/// </summary>
/// <remarks>
/// Three deliberate differences from the previous non-streaming client:
/// <list type="number">
/// <item><b>Streaming.</b> Tokens are surfaced as they arrive, which is what
/// makes a local model feel responsive on consumer hardware.</item>
/// <item><b>All tool calls.</b> Deltas are accumulated per tool-call index and
/// every completed call is returned. Reading only the first call silently
/// dropped work whenever the model batched several actions.</item>
/// <item><b>No hardcoded reasoning format.</b> The reasoning format is opt-in
/// via constructor argument, so a runtime that does not support it is not sent
/// a field it will reject.</item>
/// </list>
/// </remarks>
internal sealed class LlamaStreamingClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _reasoningFormat;

    internal LlamaStreamingClient(
        HttpClient http,
        string endpoint,
        string? reasoningFormat = null)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/') + "/v1/chat/completions";
        _reasoningFormat = reasoningFormat;
    }

    /// <summary>Temperature used for tool-capable turns.</summary>
    internal double Temperature { get; init; } = 0.4;

    /// <summary>Upper bound on generated tokens per turn.</summary>
    internal int MaxTokens { get; init; } = 2048;

    /// <summary>
    /// Runs one streaming turn. <paramref name="onToken"/> is invoked for each
    /// visible content fragment.
    /// </summary>
    internal async Task<AgentTurn> CompleteAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        Action<string> onToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(
                BuildPayload(messages, tools),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            throw new InvalidOperationException(
                "Local model returned " + (int)response.StatusCode + ": "
                + (body.Length > 500 ? body[..500] : body));
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var calls = new SortedDictionary<int, ToolCallAccumulator>();
        var finishReason = "stop";

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
                break;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line[5..].Trim();

            if (payload.Length == 0 || payload == "[DONE]")
                continue;

            JsonNode? chunk;

            try
            {
                chunk = JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                continue;
            }

            var choice = chunk?["choices"]?[0];

            if (choice is null)
                continue;

            finishReason = choice["finish_reason"]?.GetValue<string>() ?? finishReason;

            var delta = choice["delta"];

            if (delta?["content"]?.GetValue<string>() is { Length: > 0 } fragment)
            {
                content.Append(fragment);
                onToken(fragment);
            }

            if (delta?["tool_calls"] is JsonArray toolCalls)
                Accumulate(calls, toolCalls);
        }

        var completed = calls.Values
            .Where(call => call.Name.Length > 0)
            .Select((call, index) => new AgentToolCall(
                Id: call.Id.Length > 0 ? call.Id : "call_" + index,
                Name: call.Name,
                ArgumentsJson: call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()))
            .ToList();

        return new AgentTurn(content.ToString(), completed, finishReason);
    }

    /// <summary>Merges streamed tool-call fragments keyed by their index.</summary>
    private static void Accumulate(
        SortedDictionary<int, ToolCallAccumulator> calls,
        JsonArray toolCalls)
    {
        foreach (var entry in toolCalls.OfType<JsonObject>())
        {
            var index = entry["index"]?.GetValue<int>() ?? calls.Count;

            if (!calls.TryGetValue(index, out var accumulator))
            {
                accumulator = new ToolCallAccumulator();
                calls[index] = accumulator;
            }

            if (entry["id"]?.GetValue<string>() is { Length: > 0 } id)
                accumulator.Id = id;

            var function = entry["function"];

            if (function?["name"]?.GetValue<string>() is { Length: > 0 } name)
                accumulator.Name = name;

            if (function?["arguments"]?.GetValue<string>() is { Length: > 0 } arguments)
                accumulator.Arguments.Append(arguments);
        }
    }

    private string BuildPayload(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools)
    {
        var payload = new JsonObject
        {
            ["stream"] = true,
            ["temperature"] = Temperature,
            ["max_tokens"] = MaxTokens,
            ["messages"] = new JsonArray(messages.Select(Serialize).ToArray())
        };

        if (_reasoningFormat is { Length: > 0 })
            payload["reasoning_format"] = _reasoningFormat;

        if (tools.Count > 0)
        {
            payload["tools"] = new JsonArray(tools
                .Select(tool => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJson)
                    }
                })
                .ToArray());

            payload["tool_choice"] = "auto";
        }

        return payload.ToJsonString();
    }

    private static JsonNode Serialize(AgentMessage message)
    {
        var node = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (message.ToolCallId is { Length: > 0 })
            node["tool_call_id"] = message.ToolCallId;

        if (message.ToolCalls is { Count: > 0 })
        {
            node["tool_calls"] = new JsonArray(message.ToolCalls
                .Select(call => (JsonNode)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson
                    }
                })
                .ToArray());
        }

        return node;
    }

    private sealed class ToolCallAccumulator
    {
        internal string Id { get; set; } = string.Empty;

        internal string Name { get; set; } = string.Empty;

        internal StringBuilder Arguments { get; } = new();
    }
}
