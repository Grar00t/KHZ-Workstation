using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Tools.Safety;

namespace KHZ.Tools.Tools;

/// <summary>
/// Executes a PowerShell command in the workspace after static risk analysis
/// and explicit human authorisation.
/// </summary>
/// <remarks>
/// Honest scope statement: a shell tool is not sandboxed by the workspace
/// guard, because a shell can address the whole filesystem. What this tool does
/// provide is (a) refusal of a catastrophic-command class before any prompt,
/// (b) an optional allowlist, (c) explicit risk flags in the prompt so approval
/// is informed rather than habitual, (d) a bounded timeout and output budget,
/// and (e) an audit record that never stores the raw command text.
/// </remarks>
public sealed class RunPowerShellTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "run_powershell",
        Title: "Run PowerShell command",
        Description: "Runs a non-interactive PowerShell command with the workspace root as the "
                     + "working directory. Requires user confirmation. Irreversible or "
                     + "machine-scope commands (disk formatting, shadow-copy deletion, boot or "
                     + "Defender configuration, account or firewall changes) are refused. "
                     + "Output is capped and the command times out.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "PowerShell command to execute (1 to 2000 characters)." },
            "timeout_seconds": { "type": "integer", "description": "5 to 300. Defaults to 60." },
            "working_directory": { "type": "string", "description": "Optional workspace-relative working directory." }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """,
        Risk: ToolRisk.Execute,
        RequiresConfirmation: true);

    public async Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var command = ToolArgs.RequireString(arguments, "command").Trim();

        if (command.Length > 2000)
        {
            throw new ToolFailureException(
                "invalid_command",
                "Command must be 2000 characters or fewer.");
        }

        var timeoutSeconds = ToolArgs.OptionalInt(arguments, "timeout_seconds", 60, 5, 300);
        var workingDirectory = context.Resolve(
            ToolArgs.OptionalString(arguments, "working_directory") ?? ".");

        if (!Directory.Exists(workingDirectory))
        {
            throw new ToolFailureException(
                "directory_not_found",
                "Working directory not found: " + context.Relative(workingDirectory));
        }

        var assessment = CommandRisk.Assess(command);

        if (assessment.Blocked)
        {
            context.Audit.Record("agent", "tool.run_powershell", "redacted", "blocked", new
            {
                commandCaptured = false,
                commandLength = command.Length,
                blockReason = assessment.BlockReason
            });

            throw new ToolSecurityException(
                assessment.BlockReason ?? "command_blocked",
                "Command refused by KHZ command policy (" + assessment.BlockReason + "). "
                + string.Join(" ", assessment.Flags));
        }

        await ToolRouter.RequireConfirmationAsync(
            context,
            new ConfirmationRequest(
                ToolName: Descriptor.Name,
                Risk: ToolRisk.Execute,
                Title: "Run a PowerShell command (risk: " + assessment.Level + ")",
                Target: context.Relative(workingDirectory),
                Summary: "Execute in the workspace with a " + timeoutSeconds + "s timeout.",
                After: command,
                Warnings: assessment.Flags),
            cancellationToken).ConfigureAwait(false);

        var result = await context.Shell
            .RunAsync(
                command,
                workingDirectory,
                TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken)
            .ConfigureAwait(false);

        var json = ToolArgs.Serialize(new
        {
            status = result.TimedOut ? "timed_out" : "completed",
            exitCode = result.ExitCode,
            riskLevel = assessment.Level,
            riskFlags = assessment.Flags,
            truncated = result.Truncated,
            stdout = Cap(result.StandardOutput),
            stderr = Cap(result.StandardError)
        });

        return new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new
            {
                commandCaptured = false,
                rawPayloadCaptured = false,
                commandLength = command.Length,
                riskLevel = assessment.Level,
                riskFlagCount = assessment.Flags.Length,
                exitCode = result.ExitCode,
                timedOut = result.TimedOut,
                userConfirmed = true,
                aiUsed = true
            });
    }

    private static string Cap(string value)
        => value.Length <= 20_000 ? value : value[..20_000] + "\n... (output truncated)";
}
