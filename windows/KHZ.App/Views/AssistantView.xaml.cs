using KHZ.App.AI;
using KHZ.App.Trust;
using KHZ.App.Workspaces;
using Microsoft.Web.WebView2.Core;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KHZ.App.Views;

public partial class AssistantView : UserControl
{
    private readonly HttpClient _http =
        new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

    private IActivityStore? _activity;
    private WorkspaceAiProposalService? _proposalService;
    private WorkspaceContext? _workspace;
    private LocalAiSession? _session;
    private bool _initialized;

    public AssistantView()
    {
        InitializeComponent();
    }

    internal void Configure(IActivityStore activity)
    {
        _activity = activity
            ?? throw new ArgumentNullException(nameof(activity));
        _proposalService = new WorkspaceAiProposalService(activity);
    }

    internal void SetWorkspace(WorkspaceContext? workspace)
    {
        _workspace = workspace;
        _session = null;
        DisconnectedPanel.Visibility = Visibility.Visible;
        WorkspaceStatusText.Text = workspace is null
            ? "No KHZ workspace is active"
            : $"Workspace · {workspace.Info.Name} · MCP reads and proposes only";
        RefreshProposals();
    }

    internal async Task InitializeAsync()
    {
        if (_initialized)
            return;

        var dataPath = System.IO.Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "WebView2-AI");
        System.IO.Directory.CreateDirectory(dataPath);
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: dataPath);
        await AssistantWeb.EnsureCoreWebView2Async(environment);

        var settings = AssistantWeb.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsWebMessageEnabled = false;

        AssistantWeb.CoreWebView2.NavigationStarting += NavigationStarting;
        AssistantWeb.CoreWebView2.NewWindowRequested += NewWindowRequested;
        AssistantWeb.CoreWebView2.NavigationCompleted += NavigationCompleted;
        AssistantWeb.CoreWebView2.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All);
        AssistantWeb.CoreWebView2.WebResourceRequested += WebResourceRequested;
        _initialized = true;
    }

    internal async Task RefreshSessionAsync()
    {
        ErrorText.Text = string.Empty;
        RefreshProposals();
        if (!_initialized)
            await InitializeAsync();

        if (!LocalAiSession.TryLoad(
                _workspace,
                out var candidate,
                out var status)
            || candidate is null)
        {
            Disconnect(status);
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(candidate.EndpointUri, "health"));
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", candidate.ApiToken);
            request.Headers.CacheControl =
                new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true
                };
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Disconnect("Local model session did not pass authenticated health check.");
                return;
            }

            _session = candidate;
            ModelStatusText.Text = status;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(52, 168, 83));
            DisconnectedPanel.Visibility = Visibility.Collapsed;
            Navigate(candidate.EndpointUri);
            _activity?.Record(
                category: "ai",
                action: "ai.session.connect",
                target: candidate.ModelFamily,
                result: "CONNECTED",
                details: new
                {
                    workspaceId = candidate.WorkspaceId,
                    loopbackOnly = true,
                    authenticated = true,
                    tokenCaptured = false,
                    mcpBoundary = "read_search_propose"
                });
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or InvalidOperationException)
        {
            Disconnect(
                "Local model runtime is unavailable: " + ex.GetType().Name);
        }
    }

    internal void Shutdown()
    {
        _http.Dispose();
    }

    private async void RefreshSession_Click(object sender, RoutedEventArgs e)
        => await RefreshSessionAsync();

    private void Navigate(Uri endpoint)
    {
        var environment = AssistantWeb.CoreWebView2?.Environment;
        if (environment is null || _session is null)
            return;
        var request = environment.CreateWebResourceRequest(
            endpoint.ToString(),
            "GET",
            postData: null,
            $"Authorization: Bearer {_session.ApiToken}\r\nCache-Control: no-store\r\n");
        AssistantWeb.CoreWebView2.NavigateWithWebResourceRequest(request);
    }

    private void Disconnect(string status)
    {
        _session = null;
        ModelStatusText.Text = "Local model session not connected";
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(183, 130, 55));
        DisconnectedText.Text = status;
        DisconnectedPanel.Visibility = Visibility.Visible;
        if (_initialized)
            AssistantWeb.CoreWebView2.Navigate("about:blank");
    }

    private bool IsAllowedNavigation(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target))
            return false;
        if (target.OriginalString.Equals(
                "about:blank",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IsSessionOrigin(target);
    }

    private bool IsAllowedResource(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target))
            return false;
        if (target.OriginalString.Equals(
                "about:blank",
                StringComparison.OrdinalIgnoreCase)
            || target.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            || target.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IsSessionOrigin(target);
    }

    private bool IsSessionOrigin(Uri target)
    {
        var endpoint = _session?.EndpointUri;
        return endpoint is not null
            && target.Scheme.Equals(endpoint.Scheme, StringComparison.Ordinal)
            && target.Host.Equals(endpoint.Host, StringComparison.Ordinal)
            && target.Port == endpoint.Port
            && string.IsNullOrEmpty(target.UserInfo);
    }

    private void NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedNavigation(e.Uri))
            return;
        e.Cancel = true;
        RecordBlocked("ai.webview.navigation");
    }

    private void NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
    }

    private void NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess || _session is null)
            return;
        ErrorText.Text = "The local assistant UI failed to load.";
    }

    private void WebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (IsAllowedResource(e.Request.Uri))
        {
            if (_session is not null
                && Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var target)
                && target.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal))
            {
                e.Request.Headers.SetHeader(
                    "Authorization",
                    "Bearer " + _session.ApiToken);
            }
            return;
        }

        e.Response = AssistantWeb.CoreWebView2.Environment
            .CreateWebResourceResponse(
                null,
                403,
                "Blocked by KHZ local AI policy",
                "Content-Type: text/plain\r\nCache-Control: no-store\r\n");
        RecordBlocked("ai.webview.resource");
    }

    private void RecordBlocked(string action)
    {
        _activity?.Record(
            category: "security",
            action: action,
            target: "external-origin",
            result: "DENIED",
            details: new
            {
                policy = "ai_exact_loopback_origin",
                uriCaptured = false,
                queryCaptured = false,
                tokenCaptured = false
            });
    }

    private void RefreshProposals()
    {
        ProposalList.ItemsSource = null;
        ProposalPreviewText.Text = string.Empty;
        ProposalTargetText.Text = "Select a proposal to preview";
        ApplyProposalButton.IsEnabled = false;
        RejectProposalButton.IsEnabled = false;
        if (_workspace is null || _proposalService is null)
            return;
        try
        {
            ProposalList.ItemsSource = _proposalService.ListPending(_workspace);
        }
        catch (Exception ex) when (
            ex is System.IO.IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            ErrorText.Text = "Could not load AI proposals: " + ex.Message;
        }
    }

    private void ProposalList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ProposalList.SelectedItem is not WorkspaceAiProposal proposal)
        {
            ProposalPreviewText.Text = string.Empty;
            ProposalTargetText.Text = "Select a proposal to preview";
            ApplyProposalButton.IsEnabled = false;
            RejectProposalButton.IsEnabled = false;
            return;
        }
        ProposalTargetText.Text = proposal.Target;
        ProposalPreviewText.Text = proposal.ProposedContent;
        ApplyProposalButton.IsEnabled = true;
        RejectProposalButton.IsEnabled = true;
    }

    private void ApplyProposal_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null
            || _proposalService is null
            || ProposalList.SelectedItem is not WorkspaceAiProposal proposal)
        {
            return;
        }

        var decision = MessageBox.Show(
            $"Apply the reviewed content to '{proposal.Target}'?\n\n"
            + "KHZ will re-check the file hash and preserve the current version.",
            "Apply local AI proposal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (decision != MessageBoxResult.Yes)
            return;

        try
        {
            _proposalService.Apply(_workspace, proposal.ProposalId);
            ErrorText.Text = string.Empty;
            RefreshProposals();
        }
        catch (Exception ex) when (
            ex is System.IO.IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Security.Cryptography.CryptographicException
            or System.Text.Json.JsonException)
        {
            ErrorText.Text = "Proposal was not applied: " + ex.Message;
        }
    }

    private void RejectProposal_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null
            || _proposalService is null
            || ProposalList.SelectedItem is not WorkspaceAiProposal proposal)
        {
            return;
        }
        try
        {
            _proposalService.Reject(_workspace, proposal.ProposalId);
            ErrorText.Text = string.Empty;
            RefreshProposals();
        }
        catch (Exception ex) when (
            ex is System.IO.IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            ErrorText.Text = "Proposal was not rejected: " + ex.Message;
        }
    }
}
