using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace KHZ.App.Office;

internal sealed class OnlyOfficeGatewayAdapter
    : IOfficeEngineAdapter
{
    private const string GatewayTokenEnvironmentVariable =
        "KHZ_OFFICE_GATEWAY_TOKEN";

    private static readonly Uri GatewayBase =
        new("http://127.0.0.1:8090/");

    private static readonly HashSet<string> SupportedKinds =
        new(
            new[]
            {
                "document",
                "sheet",
                "slide",
                "pdf"
            },
            StringComparer.Ordinal);

    private readonly string? _gatewayToken;

    private OnlyOfficeGatewayAdapter(
        string? gatewayToken)
    {
        _gatewayToken =
            IsValidToken(gatewayToken)
                ? gatewayToken
                : null;
    }

    public string EngineId =>
        "onlyoffice-document-server";

    public string DisplayName =>
        "ONLYOFFICE Document Server";

    public bool IsConfigured =>
        _gatewayToken is not null;

    public Uri HealthEndpoint =>
        new(
            GatewayBase,
            "health");

    internal static OnlyOfficeGatewayAdapter FromEnvironment()
        => new(
            Environment.GetEnvironmentVariable(
                GatewayTokenEnvironmentVariable));

    public HttpRequestMessage CreateHealthRequest()
    {
        var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                HealthEndpoint);

        request.Headers.Authorization =
            CreateAuthorizationHeader();

        request.Headers.CacheControl =
            new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };

        return request;
    }

    public OfficeEditorRequest CreateEditorRequest(
        string kind)
    {
        if (!SupportedKinds.Contains(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Unsupported Office editor kind.");
        }

        return CreateNavigationRequest(
            new Uri(
                GatewayBase,
                $"editor/{kind}"));
    }

    public OfficeEditorRequest CreateNavigationRequest(
        Uri uri)
    {
        if (!IsAllowedNavigation(uri))
        {
            throw new InvalidOperationException(
                "Office navigation is outside the configured loopback gateway.");
        }

        var authorization =
            CreateAuthorizationHeader();

        return new OfficeEditorRequest(
            Uri: uri,
            AdditionalHeaders:
                $"Authorization: {authorization.Scheme} {authorization.Parameter}\r\n"
                + "Cache-Control: no-store\r\n");
    }

    public bool IsAllowedNavigation(
        Uri uri)
    {
        if (uri is null)
            return false;

        if (uri.Scheme.Equals(
                "about",
                StringComparison.OrdinalIgnoreCase))
        {
            return uri.OriginalString.Equals(
                "about:blank",
                StringComparison.OrdinalIgnoreCase);
        }

        return
            uri.Scheme.Equals(
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(
                GatewayBase.Host,
                StringComparison.OrdinalIgnoreCase)
            && uri.Port == GatewayBase.Port
            && string.IsNullOrEmpty(
                uri.UserInfo);
    }

    private AuthenticationHeaderValue CreateAuthorizationHeader()
    {
        if (_gatewayToken is null)
        {
            throw new InvalidOperationException(
                "The local Office session is not configured. Start the authenticated Office runtime before opening an embedded editor.");
        }

        return new AuthenticationHeaderValue(
            "Bearer",
            _gatewayToken);
    }

    private static bool IsValidToken(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) < 32)
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed =
                character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_';

            if (!allowed)
                return false;
        }

        return true;
    }
}
