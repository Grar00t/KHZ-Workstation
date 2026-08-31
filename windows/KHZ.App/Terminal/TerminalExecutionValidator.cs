using System;
using System.IO;

namespace KHZ.App.Terminal;

internal static class TerminalExecutionValidator
{
    internal static TerminalExecutionRequest Validate(
        TerminalExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(
            request);

        var command =
            request.Command?.Trim()
            ?? string.Empty;

        if (command.Length == 0)
        {
            throw new ArgumentException(
                "Command is required.",
                nameof(request));
        }

        if (command.Length > 16_384)
        {
            throw new ArgumentException(
                "Command exceeds the supported length.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(
                request.WorkingDirectory))
        {
            throw new ArgumentException(
                "Working directory is required.",
                nameof(request));
        }

        var workingDirectory =
            Path.GetFullPath(
                request.WorkingDirectory);

        if (!Directory.Exists(
                workingDirectory))
        {
            throw new DirectoryNotFoundException(
                workingDirectory);
        }

        var timeout =
            request.Timeout;

        if (timeout < TimeSpan.FromSeconds(1)
            || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Timeout must be between 1 second and 5 minutes.");
        }

        return request with
        {
            Command = command,
            WorkingDirectory = workingDirectory,
            Timeout = timeout
        };
    }
}
