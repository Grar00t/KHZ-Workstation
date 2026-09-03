using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KHZ.App.Mcp;
using KHZ.Tools.Tools;

namespace KHZ.App.Chat;

/// <summary>Progress events emitted while a turn runs.</summary>
internal enum AgentEventKind
{
    Token,
    ToolStarted,
    ToolFinished,
    ToolFailed,
    Notice
}

/// <param name="Kind">Event type.</param>
/// <param name="Text">Token text, tool name, or notice.</param>
/// <param name="Detail">Optional payload summary.</param>
internal sealed record AgentEvent(AgentEventKind Kind, string Text, string? Detail = null);

/// <summary>
/// The executive agent loop: model turn, tool execution, repeat until the model
/// stops asking for tools.
/// </summary>
/// <remarks>
/// Bounded by design. <see cref="MaxToolRounds"/> caps how many tool rounds a
/// single user message may trigger, so a model stuck in a retry loop cannot
/// execute an unbounded series of actions. Every mutating tool still requires
/// human approval inside the router, so the cap is a resource guard rather than
/// the safety control.
/// </remarks>
internal sealed class NiyahAgentSession
{
    /// <summary>Maximum tool rounds per user message.</summary>
    internal const int MaxToolRounds = 8;

    private const string SystemPrompt =
        "You are Niyah, the local executive assistant inside KHZ Workstation. You act on the "
        + "user's workspace with tools; you do not describe what you would do, you do it. "
        + "Rules you must follow:\n"
        + "1. All paths are relative to the workspace root. Never use absolute paths.\n"
        + "2. Before editing anything, read it first and pass the returned sha256 back as "
        + "expected_sha256. A stale hash means the file changed; re-read and retry.\n"
        + "3. For Office files use the office_* tools. read_file and replace_text are for plain "
        + "text only and will refuse .docx, .xlsx, and .pptx.\n"
        + "4. Mutating tools ask the user for approval. If a call returns denied_by_user, stop "
        + "and report it; do not retry the same action or look for a way around it.\n"
        + "5. Prefer the smallest precise edit over rewriting a document.\n"
        + "6. State what you actually did, including file names and hashes when relevant. Do not "
        + "claim an action succeeded unless a tool result confirms it.\n"
        + "Answer in the user's language.";

    private readonly List<AgentMessage> _messages = [];
    private readonly LlamaStreamingClient _client;
    private readonly ToolRouter _router;
    private readonly ToolContext _context;
    private readonly McpToolBridge? _mcp;

    internal NiyahAgentSession(
        LlamaStreamingClient client,
        ToolRouter router,
        ToolContext context,
        McpToolBridge? mcp = null)
    {
        _client = client;
        _router = router;
        _context = context;
        _mcp = mcp;

        _messages.Add(new AgentMessage("system", SystemPrompt));
    }

    /// <summary>Local plus bridged MCP tools, as advertised to the model.</summary>
    internal IReadOnlyList<ChatToolDefinition> Tools()
    {
        var local = _router.Descriptors
            .Select(descriptor => new ChatToolDefinition(
                descriptor.Name,
                descriptor.Description,
                descriptor.ParametersJson,
                descriptor.RequiresConfirmation))
            .ToList();

        if (_mcp is not null)
            local.AddRange(_mcp.Definitions());

        return local;
    }

    /// <summary>Sends a user message and drives the loop to completion.</summary>
    internal async Task<string> SendAsync(
        string userMessage,
        Action<AgentEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        _messages.Add(new AgentMessage("user", userMessage));

        var tools = Tools();
        var final = string.Empty;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var turn = await _client
                .CompleteAsync(
                    _messages,
                    tools,
                    fragment => onEvent(new AgentEvent(AgentEventKind.Token, fragment)),
                    cancellationToken)
                .ConfigureAwait(false);

            _messages.Add(new AgentMessage(
                "assistant",
                turn.Content,
                ToolCalls: turn.ToolCalls.Count > 0 ? turn.ToolCalls : null));

            if (turn.Content.Length > 0)
                final = turn.Content;

            if (turn.ToolCalls.Count == 0)
                return final;

            foreach (var call in turn.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                onEvent(new AgentEvent(AgentEventKind.ToolStarted, call.Name, call.ArgumentsJson));

                var result = await ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
                var failed = result.Contains("\"code\":", StringComparison.Ordinal)
                             && result.Contains("\"error\":", StringComparison.Ordinal);

                onEvent(new AgentEvent(
                    failed ? AgentEventKind.ToolFailed : AgentEventKind.ToolFinished,
                    call.Name,
                    result.Length > 600 ? result[..600] + "..." : result));

                _messages.Add(new AgentMessage(
                    "tool",
                    result,
                    ToolCallId: call.Id));
            }
        }

        onEvent(new AgentEvent(
            AgentEventKind.Notice,
            "Stopped after " + MaxToolRounds + " tool rounds without a final answer."));

        return final;
    }

    private Task<string> ExecuteAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        if (_mcp is not null && _mcp.Handles(call.Name))
            return _mcp.InvokeAsync(call.Name, call.ArgumentsJson, cancellationToken);

        return _router.InvokeAsync(call.Name, call.ArgumentsJson, _context, cancellationToken);
    }

    /// <summary>Clears the conversation, keeping the system prompt.</summary>
    internal void Reset()
    {
        _messages.RemoveRange(1, _messages.Count - 1);
    }

    /// <summary>Number of stored turns, excluding the system prompt.</summary>
    internal int TurnCount => Math.Max(0, _messages.Count - 1);
}
