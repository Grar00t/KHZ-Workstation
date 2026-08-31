using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.App.Terminal;

internal sealed class PowerShellTerminalRunner : ITerminalRunner
{
    private const int MaxCapturedCharacters = 1_048_576;
    private const string TruncationMarker =
        "[KHZ output truncated after 1048576 characters]";

    private readonly string _powerShellExecutable;

    internal PowerShellTerminalRunner()
    {
        _powerShellExecutable =
            ResolvePowerShellExecutable();
    }

    public async Task<TerminalExecutionResult> ExecuteAsync(
        TerminalExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        request =
            TerminalExecutionValidator.Validate(
                request);

        var startedAt =
            DateTimeOffset.Now;

        var startInfo =
            new ProcessStartInfo
            {
                FileName = _powerShellExecutable,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true
            };

        startInfo.ArgumentList.Add(
            "-NoLogo");

        startInfo.ArgumentList.Add(
            "-NoProfile");

        startInfo.ArgumentList.Add(
            "-NonInteractive");

        startInfo.ArgumentList.Add(
            "-Command");

        startInfo.ArgumentList.Add(
            request.Command);

        // This runner has no stdin. Prevent Git or credential
        // managers from opening hidden interactive prompts.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";

        using var process =
            new Process
            {
                StartInfo = startInfo
            };

        try
        {
            if (!process.Start())
            {
                return Failed(
                    startedAt,
                    "PowerShell process could not be started.");
            }
        }
        catch (Exception ex)
        {
            return Failed(
                startedAt,
                ex.Message);
        }

        var stdoutTask =
            ReadBoundedAsync(process.StandardOutput);

        var stderrTask =
            ReadBoundedAsync(process.StandardError);

        using var timeoutSource =
            new CancellationTokenSource(
                request.Timeout);

        using var waitSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

        TerminalExecutionStatus status;
        int? exitCode = null;

        try
        {
            await process.WaitForExitAsync(
                waitSource.Token);

            status =
                TerminalExecutionStatus.Exited;

            exitCode =
                process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            status =
                cancellationToken.IsCancellationRequested
                    ? TerminalExecutionStatus.Cancelled
                    : TerminalExecutionStatus.TimedOut;

            await TerminateAsync(
                process);
        }
        catch (Exception ex)
        {
            await TerminateAsync(
                process);

            var stdout =
                await SafeReadAsync(
                    stdoutTask);

            var stderr =
                await SafeReadAsync(
                    stderrTask);

            if (stderr.Length > 0)
            {
                stderr += Environment.NewLine;
            }

            stderr += ex.Message;

            return new TerminalExecutionResult(
                Status: TerminalExecutionStatus.Failed,
                ExitCode: null,
                StandardOutput: stdout,
                StandardError: stderr,
                StartedAt: startedAt,
                FinishedAt: DateTimeOffset.Now);
        }

        var standardOutput =
            await SafeReadAsync(
                stdoutTask);

        var standardError =
            await SafeReadAsync(
                stderrTask);

        return new TerminalExecutionResult(
            Status: status,
            ExitCode: exitCode,
            StandardOutput: standardOutput,
            StandardError: standardError,
            StartedAt: startedAt,
            FinishedAt: DateTimeOffset.Now);
    }

    private static async Task TerminateAsync(
        Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort. The caller still receives
            // Cancelled or TimedOut.
        }

        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync();
            }
        }
        catch
        {
            // Process may already have exited or become unavailable.
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());

            if (read == 0)
                break;

            var remaining =
                MaxCapturedCharacters - builder.Length;

            if (remaining > 0)
            {
                builder.Append(
                    buffer,
                    0,
                    Math.Min(remaining, read));
            }

            if (read > remaining)
                truncated = true;
        }

        if (truncated)
        {
            builder.AppendLine();
            builder.Append(TruncationMarker);
        }

        return builder.ToString();
    }

    private static async Task<string> SafeReadAsync(
        Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TerminalExecutionResult Failed(
        DateTimeOffset startedAt,
        string error)
        => new(
            Status: TerminalExecutionStatus.Failed,
            ExitCode: null,
            StandardOutput: string.Empty,
            StandardError: error,
            StartedAt: startedAt,
            FinishedAt: DateTimeOffset.Now);

    private static string ResolvePowerShellExecutable()
    {
        var systemDirectory =
            Environment.GetFolderPath(
                Environment.SpecialFolder.System);

        var windowsPowerShell =
            Path.Combine(
                systemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

        if (File.Exists(
                windowsPowerShell))
        {
            return windowsPowerShell;
        }

        throw new FileNotFoundException(
            "Windows PowerShell was not found.");
    }
}
