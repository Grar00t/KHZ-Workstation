using System;

namespace KHZ.App.Terminal;

internal enum TerminalExecutionStatus
{
    Exited,
    TimedOut,
    Cancelled,
    Failed
}

internal sealed record TerminalExecutionResult(
    TerminalExecutionStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt)
{
    public TimeSpan Duration =>
        FinishedAt - StartedAt;
}
