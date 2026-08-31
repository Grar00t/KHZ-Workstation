using KHZ.App.Integrations;
using KHZ.App.Trust;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class IntegrationsView : UserControl
{
    private const string OracleProviderId = "oracle-ebs";
    private const string PostgresProviderId = "postgresql";

    private IIntegrationConfigStore? _store;
    private IActivityStore? _activity;

    public IntegrationsView()
    {
        InitializeComponent();
    }

    internal void Configure(
        IIntegrationConfigStore store,
        IActivityStore activity)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    }

    internal void LoadConfiguration()
    {
        if (_store is null)
            return;

        try
        {
            var configs = _store.List();

            var oracle = configs.FirstOrDefault(
                x => x.ProviderId == OracleProviderId);

            var postgres = configs.FirstOrDefault(
                x => x.ProviderId == PostgresProviderId);

            OracleBaseUrlBox.Text =
                oracle?.Endpoint ?? string.Empty;

            PostgresHostBox.Text =
                postgres?.Endpoint ?? string.Empty;

            PostgresPortBox.Text =
                postgres?.Port?.ToString(CultureInfo.InvariantCulture)
                ?? "5432";

            PostgresDatabaseBox.Text =
                postgres?.DatabaseName ?? string.Empty;

            OracleStatusText.Text =
                string.IsNullOrWhiteSpace(oracle?.Endpoint)
                    ? "Not configured"
                    : "Endpoint saved - connection disabled";

            PostgresStatusText.Text =
                string.IsNullOrWhiteSpace(postgres?.Endpoint)
                || string.IsNullOrWhiteSpace(postgres?.DatabaseName)
                    ? "Not configured"
                    : "Target saved - connection disabled";

            OracleFeedbackText.Text = "";
            PostgresFeedbackText.Text = "";
        }
        catch (Exception ex)
        {
            OracleFeedbackText.Text = "Load failed: " + ex.Message;
        }
    }

    private void SaveOracle_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_store is null)
            return;

        try
        {
            var endpoint = Normalize(OracleBaseUrlBox.Text);

            if (endpoint is not null)
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp
                        && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException(
                        "Base URL must be an absolute HTTP or HTTPS URL.");
                }
            }

            _store.Save(
                OracleProviderId,
                "Oracle E-Business Suite",
                false,
                endpoint,
                null,
                null,
                "not_configured");

            var saved = _store.Get(OracleProviderId)
                ?? throw new InvalidOperationException(
                    "Oracle read-back failed.");

            if (!string.Equals(
                    saved.Endpoint,
                    endpoint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Oracle read-back mismatch.");
            }

            OracleStatusText.Text =
                endpoint is null
                    ? "Not configured"
                    : "Endpoint saved - connection disabled";

            OracleFeedbackText.Text =
                "Saved locally. No network connection attempted.";

            _activity?.Record(
                "integration",
                "configuration.save",
                OracleProviderId,
                "SAVED",
                new
                {
                    networkAttempted = false,
                    secretsStored = false
                });
        }
        catch (Exception ex)
        {
            OracleFeedbackText.Text =
                "Save failed: " + ex.Message;
        }
    }

    private void SavePostgres_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_store is null)
            return;

        try
        {
            var host = Normalize(PostgresHostBox.Text);
            var database = Normalize(PostgresDatabaseBox.Text);

            if (!int.TryParse(
                    PostgresPortBox.Text.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port)
                || port < 1
                || port > 65535)
            {
                throw new ArgumentException(
                    "Port must be between 1 and 65535.");
            }

            _store.Save(
                PostgresProviderId,
                "PostgreSQL",
                false,
                host,
                port,
                database,
                "not_configured");

            var saved = _store.Get(PostgresProviderId)
                ?? throw new InvalidOperationException(
                    "PostgreSQL read-back failed.");

            if (!string.Equals(saved.Endpoint, host, StringComparison.Ordinal)
                || saved.Port != port
                || !string.Equals(
                    saved.DatabaseName,
                    database,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "PostgreSQL read-back mismatch.");
            }

            PostgresStatusText.Text =
                host is null || database is null
                    ? "Not configured"
                    : "Target saved - connection disabled";

            PostgresFeedbackText.Text =
                "Saved locally. No network connection attempted.";

            _activity?.Record(
                "integration",
                "configuration.save",
                PostgresProviderId,
                "SAVED",
                new
                {
                    networkAttempted = false,
                    secretsStored = false
                });
        }
        catch (Exception ex)
        {
            PostgresFeedbackText.Text =
                "Save failed: " + ex.Message;
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}
