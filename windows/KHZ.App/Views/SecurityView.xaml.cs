using KHZ.App.Trust;
using KHZ.App.Terminal;
using System;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class SecurityView : UserControl
{
    private TrustStore? _trust;
    private CapabilityPolicy? _policy;
    private UserTerminalSessionGate? _terminalSessionGate;

    public SecurityView()
    {
        InitializeComponent();
    }

    internal void Configure(
        TrustStore trust,
        CapabilityPolicy policy,
        UserTerminalSessionGate terminalSessionGate)
    {
        _trust =
            trust
            ?? throw new ArgumentNullException(
                nameof(trust));

        _policy =
            policy
            ?? throw new ArgumentNullException(
                nameof(policy));

        _terminalSessionGate =
            terminalSessionGate
            ?? throw new ArgumentNullException(
                nameof(terminalSessionGate));
    }

    internal void RefreshSecurity()
    {
        if (_trust is null
            || _policy is null
            || _terminalSessionGate is null)
        {
            ShowError(
                "Security dependencies are not configured.");

            return;
        }

        try
        {
            var integrity =
                _trust.CheckIntegrity();

            SecurityIntegrityText.Text =
                string.Equals(
                    integrity,
                    "ok",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Healthy · integrity_check=ok"
                    : $"Attention · {integrity}";

            SecurityDatabasePathText.Text =
                _trust.DatabasePath;

            SecurityOfficeNavigationText.Text =
                CapabilityStatus(
                    Capability.LocalOfficeNavigation,
                    "Allowed · localhost:8090 only");

            SecurityFileLaunchText.Text =
                CapabilityStatus(
                    Capability.LocalFileLaunch);

            SecurityRepositoryInspectionText.Text =
                CapabilityStatus(
                    Capability.LocalRepositoryInspection,
                    "Allowed · read-only Git metadata");

            SecurityUserTerminalText.Text =
                _policy.IsAllowed(
                    Capability.UserTerminalExecution)
                    ? "Allowed by policy"
                    : _terminalSessionGate.IsEnabled
                        ? "Session-enabled by user · not persisted"
                        : "Denied by default · session disabled";

            SecurityExternalWebText.Text =
                CapabilityStatus(
                    Capability.ExternalWebNavigation);

            SecurityInstitutionalNetworkText.Text =
                CapabilityStatus(
                    Capability.InstitutionalNetwork);

            SecurityInternetEgressText.Text =
                _policy.IsAllowed(
                    Capability.InternetEgress)
                    ? "Allowed"
                    : _terminalSessionGate.IsEnabled
                        ? "Denied to KHZ-managed clients · user terminal may use OS network"
                        : "Denied";

            SecurityProcessText.Text =
                _policy.IsAllowed(
                    Capability.ArbitraryProcessExecution)
                    ? "Allowed"
                    : _terminalSessionGate.IsEnabled
                        ? "Denied to automation · user terminal is separate execution"
                        : "Denied";

            SecurityIntegrationWriteText.Text =
                CapabilityStatus(
                    Capability.IntegrationWrite);

            SecurityAiText.Text =
                CapabilityStatus(
                    Capability.AiInference);

            SecurityErrorText.Text = "";
            SecurityErrorText.Visibility =
                Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private string CapabilityStatus(
        Capability capability,
        string allowedDetail = "Allowed")
        => _policy?.IsAllowed(capability) == true
            ? allowedDetail
            : "Denied";

    private void ShowError(
        string message)
    {
        SecurityIntegrityText.Text =
            "Check failed";

        SecurityErrorText.Text =
            message;

        SecurityErrorText.Visibility =
            Visibility.Visible;
    }

    private void RefreshSecurity_Click(
        object sender,
        RoutedEventArgs e)
        => RefreshSecurity();
}
