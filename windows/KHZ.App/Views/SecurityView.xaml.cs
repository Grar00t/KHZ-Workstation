using KHZ.App.Trust;
using System;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class SecurityView : UserControl
{
    private TrustStore? _trust;
    private CapabilityPolicy? _policy;

    public SecurityView()
    {
        InitializeComponent();
    }

    internal void Configure(
        TrustStore trust,
        CapabilityPolicy policy)
    {
        _trust =
            trust
            ?? throw new ArgumentNullException(
                nameof(trust));

        _policy =
            policy
            ?? throw new ArgumentNullException(
                nameof(policy));
    }

    internal void RefreshSecurity()
    {
        if (_trust is null || _policy is null)
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

            SecurityExternalWebText.Text =
                CapabilityStatus(
                    Capability.ExternalWebNavigation);

            SecurityExternalNetworkText.Text =
                CapabilityStatus(
                    Capability.ExternalNetwork);

            SecurityProcessText.Text =
                CapabilityStatus(
                    Capability.ArbitraryProcessExecution);

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
