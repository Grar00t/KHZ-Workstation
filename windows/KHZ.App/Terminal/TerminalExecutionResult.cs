using System;

namespace KHZ.App.Terminal;

internal enum TerminalExecutionStatus
{
    Exited,
    TimedOut,
    Cancelled,
    Failed
}

internal enum TerminalProcessContainment
{
    NotStarted,
    WindowsJobObject
}

internal sealed record TerminalExecutionResult(
    TerminalExecutionStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    TerminalProcessContainment Containment)
{
    public TimeSpan Duration =>
        FinishedAt - StartedAt;
}
