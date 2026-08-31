namespace KHZ.App.Integrations;

internal sealed record IntegrationConfig(
    string ProviderId,
    string DisplayName,
    bool Enabled,
    string? Endpoint,
    int? Port,
    string? DatabaseName,
    string AuthMode,
    string UpdatedUtc,
    string UpdatedLocal);
