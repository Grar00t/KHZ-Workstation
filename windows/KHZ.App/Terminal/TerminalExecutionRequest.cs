using System;

namespace KHZ.App.Terminal;

internal sealed record TerminalExecutionRequest(
    string Command,
    string WorkingDirectory,
    TimeSpan Timeout);
