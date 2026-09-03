using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.Tools.Tools;

/// <param name="ExitCode">Process exit code, or null when it timed out.</param>
/// <param name="StandardOutput">Captured stdout, possibly truncated.</param>
/// <param name="StandardError">Captured stderr, possibly truncated.</param>
/// <param name="TimedOut">The process was terminated after the timeout.</param>
/// <param name="Truncated">Output exceeded the capture budget.</param>
public sealed record ShellResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Truncated);

/// <summary>Shell backend. The WPF host substitutes its audited terminal runner.</summary>
public interface IShellRunner
{
    Task<ShellResult> RunAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal non-interactive PowerShell backend used when no host runner is
/// supplied (for example inside the standalone MCP server process).
/// </summary>
/// <remarks>
/// Runs with <c>-NoProfile</c> so user profile scripts cannot alter behaviour,
/// <c>-NonInteractive</c> so a prompt cannot hang the agent, and inherits the
/// current (non-elevated) token. Output is capped to keep a runaway command
/// from exhausting host memory or flooding the model context.
/// </remarks>
public sealed class PowerShellRunner : IShellRunner
{
    /// <summary>Maximum characters captured per stream.</summary>
    public const int MaxStreamChars = 100_000;

    public async Task<ShellResult> RunAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var truncated = false;

        process.OutputDataReceived += (_, e) => Append(stdout, e.Data, ref truncated);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data, ref truncated);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token,
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            return new ShellResult(
                ExitCode: null,
                StandardOutput: stdout.ToString(),
                StandardError: stderr.ToString(),
                TimedOut: true,
                Truncated: truncated);
        }

        return new ShellResult(
            ExitCode: process.ExitCode,
            StandardOutput: stdout.ToString(),
            StandardError: stderr.ToString(),
            TimedOut: false,
            Truncated: truncated);
    }

    private static void Append(StringBuilder buffer, string? line, ref bool truncated)
    {
        if (line is null)
            return;

        lock (buffer)
        {
            if (buffer.Length >= MaxStreamChars)
            {
                truncated = true;
                return;
            }

            buffer.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
