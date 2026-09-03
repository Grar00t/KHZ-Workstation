using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Tools.Safety;

namespace KHZ.Tools.Tools;

/// <summary>
/// The one place a tool call becomes an effect.
/// </summary>
/// <remarks>
/// Centralising dispatch means confirmation, auditing, and error shaping cannot
/// be forgotten by an individual tool: a new capability inherits all three by
/// registering here.
/// </remarks>
public sealed class ToolRouter
{
    private readonly ReadOnlyDictionary<string, IKhzTool> _tools;

    public ToolRouter(IEnumerable<IKhzTool> tools)
    {
        var map = new Dictionary<string, IKhzTool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (!map.TryAdd(tool.Descriptor.Name, tool))
            {
                throw new ArgumentException(
                    "Duplicate tool name: " + tool.Descriptor.Name,
                    nameof(tools));
            }
        }

        _tools = new ReadOnlyDictionary<string, IKhzTool>(map);
    }

    public IReadOnlyList<ToolDescriptor> Descriptors
        => _tools.Values
            .Select(tool => tool.Descriptor)
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToList();

    public bool TryGetDescriptor(string name, out ToolDescriptor? descriptor)
    {
        if (_tools.TryGetValue(name, out var tool))
        {
            descriptor = tool.Descriptor;
            return true;
        }

        descriptor = null;
        return false;
    }

    /// <summary>
    /// Executes a named tool. Always returns JSON: either the tool payload or a
    /// <c>{ "error": ..., "code": ... }</c> envelope. Never throws for expected
    /// failure paths, so a model can read the failure and correct itself.
    /// </summary>
    public async Task<string> InvokeAsync(
        string name,
        string argumentsJson,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return Error("unknown_tool", "Unknown tool: " + name);
        }

        JsonElement arguments;

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

            arguments = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return Error("invalid_arguments_json", exception.Message);
        }

        try
        {
            var result = await tool
                .ExecuteAsync(arguments, context, cancellationToken)
                .ConfigureAwait(false);

            context.Audit.Record(
                category: "agent",
                action: "tool." + name,
                target: result.Target,
                result: "ok",
                details: result.AuditDetails);

            return result.Json;
        }
        catch (ToolDeniedException exception)
        {
            context.Audit.Record("agent", "tool." + name, "redacted", "denied");
            return Error("denied_by_user", exception.Message);
        }
        catch (ToolSecurityException exception)
        {
            context.Audit.Record("agent", "tool." + name, "redacted", "blocked", new
            {
                code = exception.Code
            });

            return Error(exception.Code, exception.Message);
        }
        catch (ToolFailureException exception)
        {
            context.Audit.Record("agent", "tool." + name, "redacted", "failed", new
            {
                code = exception.Code
            });

            return Error(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Unexpected: record the type but not the message target, and give
            // the model a stable code rather than a stack trace.
            context.Audit.Record("agent", "tool." + name, "redacted", "error", new
            {
                exceptionType = exception.GetType().Name
            });

            return Error("tool_error", exception.Message);
        }
    }

    /// <summary>Requests authorisation, throwing <see cref="ToolDeniedException"/> on refusal.</summary>
    public static async Task RequireConfirmationAsync(
        ToolContext context,
        ConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        var approved = await context.Confirmations
            .ConfirmAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!approved)
            throw new ToolDeniedException();
    }

    private static string Error(string code, string message)
        => ToolArgs.Serialize(new { error = message, code });
}
