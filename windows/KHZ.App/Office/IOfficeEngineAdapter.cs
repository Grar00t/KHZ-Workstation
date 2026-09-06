using System;
using System.Net.Http;

namespace KHZ.App.Office;

internal sealed record OfficeEditorRequest(
    Uri Uri,
    string AdditionalHeaders);

internal interface IOfficeEngineAdapter
{
    string EngineId { get; }

    string DisplayName { get; }

    bool IsConfigured { get; }

    Uri HealthEndpoint { get; }

    HttpRequestMessage CreateHealthRequest();

    OfficeEditorRequest CreateEditorRequest(
        string kind);

    OfficeEditorRequest CreateNavigationRequest(
        Uri uri);

    bool IsAllowedNavigation(
        Uri uri);
}
