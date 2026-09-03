using KHZ.App.Terminal;
using KHZ.App.Trust;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class TerminalView : UserControl
{
    private static readonly TimeSpan CommandTimeout =
        TimeSpan.FromSeconds(60);

    private ITerminalRunner? _runner;
    private IActivityStore? _activity;
    private CapabilityPolicy? _policy;
    private UserTerminalSessionGate? _sessionGate;

    private CancellationTokenSource? _runCancellation;

    private string _workingDirectory =
        Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments);

    public TerminalView()
    {
        InitializeComponent();

        TerminalWorkingDirectoryText.Text =
            _workingDirectory;

        RefreshState();
    }

    internal void Configure(
        ITerminalRunner runner,
        IActivityStore activity,
        CapabilityPolicy policy,
        UserTerminalSessionGate sessionGate)
    {
        _runner =
            runner
            ?? throw new ArgumentNullException(
                nameof(runner));

        _activity =
            activity
            ?? throw new ArgumentNullException(
                nameof(activity));

        _policy =
            policy
            ?? throw new ArgumentNullException(
                nameof(policy));

        _sessionGate =
            sessionGate
            ?? throw new ArgumentNullException(
                nameof(sessionGate));

        RefreshState();
    }

    internal void SetInitialDirectory(
        string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)
            || !Directory.Exists(directory))
        {
            return;
        }

        _workingDirectory =
            Path.GetFullPath(directory);

        TerminalWorkingDirectoryText.Text =
            _workingDirectory;
    }

    internal void RefreshState()
    {
        if (_policy is null
            || _sessionGate is null)
        {
            TerminalSessionStatusText.Text =
                "Not configured";

            TerminalSessionButton.IsEnabled =
                false;

            TerminalRunButton.IsEnabled =
                false;

            TerminalCancelButton.IsEnabled =
                false;

            return;
        }

        if (WindowsExecutionContext.IsElevated())
        {
            TerminalSessionStatusText.Text =
                "Blocked · KHZ Workstation is running as administrator";

            TerminalSessionButton.Content =
                "Unavailable while elevated";

            TerminalSessionButton.IsEnabled =
                false;

            TerminalRunButton.IsEnabled =
                false;

            TerminalCancelButton.IsEnabled =
                _runCancellation is not null;

            return;
        }

        var policyAllowed =
            _policy.IsAllowed(
                Capability.UserTerminalExecution);

        var sessionEnabled =
            _sessionGate.IsEnabled;

        var authorized =
            policyAllowed
            || sessionEnabled;

        TerminalSessionStatusText.Text =
            policyAllowed
                ? "Allowed by policy"
                : sessionEnabled
                    ? "Enabled for this session · not persisted"
                    : "Disabled · explicit session enable required";

        TerminalSessionButton.Content =
            sessionEnabled
                ? "Disable for this session"
                : "Enable for this session";

        TerminalSessionButton.IsEnabled =
            !policyAllowed
            && _runCancellation is null;

        TerminalRunButton.IsEnabled =
            authorized
            && _runCancellation is null;

        TerminalCancelButton.IsEnabled =
            _runCancellation is not null;
    }

    private void TerminalSession_Click(
        object sender,
        RoutedEventArgs e)
    {
        TerminalErrorText.Text =
            string.Empty;

        if (_policy is null
            || _sessionGate is null
            || _activity is null)
        {
            TerminalErrorText.Text =
                "Terminal dependencies are not configured.";

            return;
        }

        if (WindowsExecutionContext.IsElevated())
        {
            TerminalErrorText.Text =
                "Terminal execution is unavailable while KHZ Workstation is running as administrator.";

            RefreshState();
            return;
        }

        if (_policy.IsAllowed(
                Capability.UserTerminalExecution))
        {
            RefreshState();
            return;
        }

        if (_sessionGate.IsEnabled)
        {
            _sessionGate.Disable();

            _activity.Record(
                category: "security",
                action: "terminal.session",
                target: "user-terminal",
                result: "DISABLED",
                details: new
                {
                    persisted = false,
                    userInitiated = true,
                    aiUsed = false
                });
        }
        else
        {
            _sessionGate.Enable();

            _activity.Record(
                category: "security",
                action: "terminal.session",
                target: "user-terminal",
                result: "ENABLED",
                details: new
                {
                    persisted = false,
                    userInitiated = true,
                    aiUsed = false,
                    sandboxed = false
                });
        }

        RefreshState();
    }

    private async void Run_Click(
        object sender,
        RoutedEventArgs e)
    {
        TerminalErrorText.Text =
            string.Empty;

        if (_runner is null
            || _activity is null
            || _policy is null
            || _sessionGate is null)
        {
            TerminalErrorText.Text =
                "Terminal dependencies are not configured.";

            return;
        }

        if (!IsExecutionAuthorized())
        {
            TerminalErrorText.Text =
                WindowsExecutionContext.IsElevated()
                    ? "Terminal execution is unavailable while KHZ Workstation is running as administrator."
                    : "Enable terminal execution for this session first.";

            return;
        }

        var command =
            TerminalCommandText.Text;

        if (string.IsNullOrWhiteSpace(command))
        {
            TerminalErrorText.Text =
                "Enter a command.";

            return;
        }

        TerminalOutputText.Text =
            string.Empty;

        _runCancellation =
            new CancellationTokenSource();

        RefreshState();

        _activity.Record(
            category: "terminal",
            action: "terminal.execute",
            target: _workingDirectory,
            result: "STARTED",
            details: new
            {
                commandLength = command.Length,
                commandCaptured = false,
                timeoutSeconds =
                    (int)CommandTimeout.TotalSeconds,
                userInitiated = true,
                aiUsed = false,
                sandboxed = false
            });

        try
        {
            var result =
                await _runner.ExecuteAsync(
                    new TerminalExecutionRequest(
                        Command: command,
                        WorkingDirectory:
                            _workingDirectory,
                        Timeout:
                            CommandTimeout),
                    _runCancellation.Token);

            TerminalOutputText.Text =
                FormatResult(result);

            var auditResult =
                result.Status switch
                {
                    TerminalExecutionStatus.Exited
                        when result.ExitCode == 0
                            => "PASSED",

                    TerminalExecutionStatus.Exited
                            => "FAILED",

                    TerminalExecutionStatus.TimedOut
                            => "TIMED_OUT",

                    TerminalExecutionStatus.Cancelled
                            => "CANCELLED",

                    _ => "FAILED"
                };

            _activity.Record(
                category: "terminal",
                action: "terminal.execute",
                target: _workingDirectory,
                result: auditResult,
                details: new
                {
                    status =
                        result.Status.ToString(),
                    exitCode =
                        result.ExitCode,
                    durationMs =
                        result.Duration.TotalMilliseconds,
                    stdoutLength =
                        result.StandardOutput.Length,
                    stderrLength =
                        result.StandardError.Length,
                    commandCaptured = false,
                    networkInspection =
                        "not_performed",
                    processContainment =
                        result.Containment.ToString(),
                    userInitiated = true,
                    aiUsed = false,
                    sandboxed = false
                });
        }
        catch (Exception ex)
        {
            TerminalErrorText.Text =
                ex.Message;

            _activity.Record(
                category: "terminal",
                action: "terminal.execute",
                target: _workingDirectory,
                result: "FAILED",
                details: new
                {
                    error =
                        ex.GetType().Name,
                    commandCaptured = false,
                    userInitiated = true,
                    aiUsed = false,
                    sandboxed = false
                });
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;

            RefreshState();
        }
    }

    private void Cancel_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_runCancellation is null)
            return;

        TerminalCancelButton.IsEnabled =
            false;

        _runCancellation.Cancel();

        _activity?.Record(
            category: "terminal",
            action: "terminal.cancel",
            target: _workingDirectory,
            result: "REQUESTED",
            details: new
            {
                userInitiated = true,
                aiUsed = false
            });
    }

    private bool IsExecutionAuthorized()
        => !WindowsExecutionContext.IsElevated()
           && (
               _policy?.IsAllowed(
                   Capability.UserTerminalExecution)
               == true
               || _sessionGate?.IsEnabled
               == true
           );

    private static string FormatResult(
        TerminalExecutionResult result)
    {
        var output =
            new StringBuilder();

        output.AppendLine(
            $"Status: {result.Status}");

        output.AppendLine(
            $"Exit code: {result.ExitCode?.ToString() ?? "-"}");

        output.AppendLine(
            $"Duration: {result.Duration.TotalMilliseconds:0} ms");

        output.AppendLine(
            $"Process containment: {result.Containment}");

        output.AppendLine();
        output.AppendLine("STDOUT");
        output.AppendLine("------");
        output.AppendLine(
            result.StandardOutput);

        output.AppendLine();
        output.AppendLine("STDERR");
        output.AppendLine("------");
        output.AppendLine(
            result.StandardError);

        return output.ToString();
    }
}
