using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Chat;

internal sealed class LlamaChatClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    internal async Task<ChatCompletionResult> CompleteAsync(
        Uri endpoint,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatToolDefinition> tools,
        LocalAiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var boundedHistory = BoundHistory(history, settings.ContextSize);
        var payload = BuildPayload(
            boundedHistory,
            tools,
            settings,
            includeReasoningFormat: settings.HideReasoning);

        var result = await SendAsync(endpoint, payload, cancellationToken);

        if (!result.Success
            && settings.HideReasoning
            && LooksLikeUnsupportedReasoningFormat(result.Body))
        {
            payload = BuildPayload(
                boundedHistory,
                tools,
                settings,
                includeReasoningFormat: false);

            result = await SendAsync(endpoint, payload, cancellationToken);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Local model request failed ({result.StatusCode}): {Bound(result.Body, 2000)}");
        }

        using var document = JsonDocument.Parse(result.Body);
        var root = document.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Local model response did not contain choices.");
        }

        var choice = choices[0];
        var finishReason =
            choice.TryGetProperty("finish_reason", out var finish)
                ? finish.GetString() ?? string.Empty
                : string.Empty;

        if (!choice.TryGetProperty("message", out var message))
            throw new InvalidDataException("Local model response did not contain a message.");

        return new ChatCompletionResult(
            Content: SanitizeVisibleContent(ReadContent(message)),
            ToolCall: ReadFirstToolCall(message),
            FinishReason: finishReason);
    }

    private static JsonObject BuildPayload(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatToolDefinition> tools,
        LocalAiSettings settings,
        bool includeReasoningFormat)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] =
                    "Answer directly. The application owns model identity. Never claim a vendor/model name; if asked, say the configured model label is shown by KHZ. Use tools when workspace evidence or execution is needed. Do not expose hidden reasoning. Distinguish observed tool results from inference."
            }
        };

        foreach (var item in history)
        {
            if (item.Role == "tool")
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = item.ToolCallId ?? string.Empty,
                    ["content"] = item.Content
                });
                continue;
            }

            if (item.Role == "assistant"
                && !string.IsNullOrWhiteSpace(item.ToolName)
                && !string.IsNullOrWhiteSpace(item.ToolCallId))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.IsNullOrWhiteSpace(item.Content) ? null : item.Content,
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = item.ToolCallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = item.ToolName,
                                ["arguments"] = item.ToolArgumentsJson ?? "{}"
                            }
                        }
                    }
                });
                continue;
            }

            messages.Add(new JsonObject
            {
                ["role"] = item.Role,
                ["content"] = item.Content
            });
        }

        var maxTokens = Math.Clamp(settings.ContextSize / 4, 512, 4096);

        var payload = new JsonObject
        {
            ["model"] = settings.ModelLabel,
            ["messages"] = messages,
            ["stream"] = false,
            ["temperature"] = 0.6,
            ["top_p"] = 0.95,
            ["max_tokens"] = maxTokens
        };

        if (includeReasoningFormat)
            payload["reasoning_format"] = "deepseek";

        if (settings.ToolsEnabled && tools.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJson)
                    }
                });
            }

            payload["tools"] = toolArray;
            payload["tool_choice"] = "auto";
        }

        return payload;
    }

    private static IReadOnlyList<ChatMessage> BoundHistory(
        IReadOnlyList<ChatMessage> history,
        int contextSize)
    {
        if (history.Count == 0)
            return history;

        var maxCharacters = Math.Clamp(contextSize * 2, 8_000, 180_000);
        var selected = new List<ChatMessage>();
        var used = 0;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var item = history[i];
            var cost =
                item.Content.Length
                + (item.ToolArgumentsJson?.Length ?? 0)
                + (item.ToolName?.Length ?? 0)
                + 96;

            if (selected.Count > 0 && used + cost > maxCharacters)
                break;

            selected.Add(item);
            used += cost;
        }

        selected.Reverse();

        while (selected.Count > 0 && selected[0].Role == "tool")
            selected.RemoveAt(0);

        return selected;
    }

    private async Task<SendResult> SendAsync(
        Uri endpoint,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint, "v1/chat/completions"))
        {
            Content = JsonContent.Create(payload)
        };

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new SendResult(response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    private static bool LooksLikeUnsupportedReasoningFormat(string body)
        => body.Contains("reasoning_format", StringComparison.OrdinalIgnoreCase)
           || body.Contains("unknown field", StringComparison.OrdinalIgnoreCase)
           || body.Contains("unsupported", StringComparison.OrdinalIgnoreCase);

    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : content.ToString();
    }

    private static ChatToolCall? ReadFirstToolCall(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls)
            || calls.ValueKind != JsonValueKind.Array
            || calls.GetArrayLength() == 0)
        {
            return null;
        }

        var call = calls[0];
        if (!call.TryGetProperty("function", out var function))
            return null;

        var id = call.TryGetProperty("id", out var idElement)
            ? idElement.GetString() ?? Guid.NewGuid().ToString("D")
            : Guid.NewGuid().ToString("D");

        var name = function.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;

        var arguments = function.TryGetProperty("arguments", out var argsElement)
            ? argsElement.ValueKind == JsonValueKind.String
                ? argsElement.GetString() ?? "{}"
                : argsElement.GetRawText()
            : "{}";

        return string.IsNullOrWhiteSpace(name)
            ? null
            : new ChatToolCall(id, name, arguments);
    }

    internal static string SanitizeVisibleContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        content = StripDelimited(content, "<think>", "</think>");
        content = StripDelimited(content, "<reasoning>", "</reasoning>");
        content = StripDelimited(content, "<analysis>", "</analysis>");
        return content.Trim();
    }

    private static string StripDelimited(string value, string start, string end)
    {
        var cursor = 0;
        while (true)
        {
            var first = value.IndexOf(start, cursor, StringComparison.OrdinalIgnoreCase);
            if (first < 0)
                break;

            var last = value.IndexOf(
                end,
                first + start.Length,
                StringComparison.OrdinalIgnoreCase);

            if (last < 0)
            {
                value = value[..first];
                break;
            }

            value = value.Remove(first, last + end.Length - first);
            cursor = first;
        }

        return value;
    }

    private static string Bound(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    public void Dispose()
        => _http.Dispose();

    private sealed record SendResult(bool Success, int StatusCode, string Body);
}
