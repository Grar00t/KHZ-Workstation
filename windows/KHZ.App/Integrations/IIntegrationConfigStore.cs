using System.Collections.Generic;

namespace KHZ.App.Integrations;

internal interface IIntegrationConfigStore
{
    IntegrationConfig? Get(
        string providerId);

    IReadOnlyList<IntegrationConfig> List();

    IntegrationConfig Save(
        string providerId,
        string displayName,
        bool enabled,
        string? endpoint,
        int? port,
        string? databaseName,
        string authMode);
}
