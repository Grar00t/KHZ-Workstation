using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using KHZ.App.Chat;
using KHZ.App.Mcp;
using KHZ.Tools;
using KHZ.Tools.Safety;
using KHZ.Tools.Tools;

namespace KHZ.App.Views;

/// <summary>A rendered transcript entry.</summary>
internal sealed class TranscriptEntry : INotifyPropertyChanged
{
    private string _text = string.Empty;

    internal TranscriptEntry(string author, string text, Brush background, bool monospace)
    {
        Author = author;
        _text = text;
        Background = background;
        FontFamily = monospace
            ? new FontFamily("Consolas")
            : new FontFamily("Segoe UI");
    }

    public string Author { get; }

    public Brush Background { get; }

    public FontFamily FontFamily { get; }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    /// <summary>Appends a streamed fragment.</summary>
    internal void Append(string fragment) => Text += fragment;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One row in the MCP server list.</summary>
internal sealed record ServerRow(string Name, string Detail);

/// <summary>
/// View model behind the executive chat window.
/// </summary>
/// <remarks>
/// The view model owns the workspace root because the root is the security
/// boundary for every tool: changing it rebuilds the tool context so that no
/// in-flight tool can be pointed at a directory the user did not choose.
/// </remarks>
internal sealed class NiyahChatViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly Brush UserBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xF6, 0xF3));
    private static readonly Brush AssistantBrush = new SolidColorBrush(Colors.White);
    private static readonly Brush ToolBrush = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF9));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly McpToolBridge _mcp = new();
    private readonly Func<Window?> _owner;

    private ToolRouter _router;
    private ToolContext _context;
    private NiyahAgentSession _session;
    private CancellationTokenSource? _turn;

    private string _rootPath;
    private string _draft = string.Empty;
    private string _statusText = "جاهز";
    private string _toolSummary = string.Empty;
    private bool _isBusy;

    internal NiyahChatViewModel(string rootPath, string endpoint, Func<Window?> owner)
    {
        _owner = owner;
        _rootPath = rootPath;

        var client = new LlamaStreamingClient(_http, endpoint);

        (_router, _context) = BuildTools(rootPath);
        _session = new NiyahAgentSession(client, _router, _context, _mcp);

        Endpoint = endpoint;
        UpdateToolSummary();
    }

    /// <summary>Local model endpoint, e.g. http://127.0.0.1:8080.</summary>
    internal string Endpoint { get; }

    public ObservableCollection<TranscriptEntry> Messages { get; } = [];

    public ObservableCollection<ServerRow> Servers { get; } = [];

    public string RootPath
    {
        get => _rootPath;
        private set => Set(ref _rootPath, value);
    }

    public string Draft
    {
        get => _draft;
        set
        {
            if (Set(ref _draft, value))
                Raise(nameof(CanSend));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ToolSummary
    {
        get => _toolSummary;
        private set => Set(ref _toolSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                Raise(nameof(CanSend));
        }
    }

    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    /// <summary>Connects configured MCP servers and refreshes the sidebar.</summary>
    internal async Task ConnectMcpAsync()
    {
        StatusText = "جارٍ ربط خوادم MCP...";

        var statuses = await _mcp.ConnectAsync().ConfigureAwait(true);

        Servers.Clear();

        foreach (var status in statuses)
            Servers.Add(new ServerRow(status.Name, status.Detail));

        if (statuses.Count == 0)
            Servers.Add(new ServerRow("(لا يوجد)", "أضف خادمًا في mcp-servers.json"));

        UpdateToolSummary();
        StatusText = "جاهز";
    }

    /// <summary>Points every tool at a new workspace root.</summary>
    internal void ChangeRoot(string path)
    {
        if (!Directory.Exists(path))
            return;

        (_router, _context) = BuildTools(path);

        RootPath = _context.RootPath;

        _session = new NiyahAgentSession(
            new LlamaStreamingClient(_http, Endpoint),
            _router,
            _context,
            _mcp);

        Messages.Add(Notice("تم تغيير مجلد العمل إلى: " + RootPath + " (بدأت محادثة جديدة)"));
        UpdateToolSummary();
    }

    /// <summary>Clears the transcript and the model's context.</summary>
    internal void NewChat()
    {
        _session.Reset();
        Messages.Clear();
        StatusText = "جاهز";
    }

    /// <summary>Cancels the in-flight turn.</summary>
    internal void Stop() => _turn?.Cancel();

    /// <summary>Sends the composer contents and streams the response.</summary>
    internal async Task SendAsync()
    {
        if (!CanSend)
            return;

        var prompt = Draft.Trim();

        Draft = string.Empty;
        IsBusy = true;
        StatusText = "جارٍ التنفيذ...";

        Messages.Add(new TranscriptEntry("أنت", prompt, UserBrush, monospace: false));

        var bubble = new TranscriptEntry("نيّة", string.Empty, AssistantBrush, monospace: false);
        Messages.Add(bubble);

        _turn = new CancellationTokenSource();

        try
        {
            await _session.SendAsync(prompt, evt => Dispatch(evt, ref bubble), _turn.Token)
                .ConfigureAwait(true);

            StatusText = "جاهز";
        }
        catch (OperationCanceledException)
        {
            Messages.Add(Notice("تم إيقاف الدورة بطلب منك."));
            StatusText = "موقوف";
        }
        catch (Exception exception)
        {
            Messages.Add(new TranscriptEntry(
                "خطأ",
                exception.Message,
                ErrorBrush,
                monospace: false));

            StatusText = "فشل";
        }
        finally
        {
            if (bubble.Text.Length == 0)
                Messages.Remove(bubble);

            _turn?.Dispose();
            _turn = null;
            IsBusy = false;
        }
    }

    /// <summary>Marshals an agent event onto the UI thread.</summary>
    private void Dispatch(AgentEvent evt, ref TranscriptEntry bubble)
    {
        var target = bubble;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            switch (evt.Kind)
            {
                case AgentEventKind.Token:
                    target.Append(evt.Text);
                    break;

                case AgentEventKind.ToolStarted:
                    StatusText = "تنفيذ: " + evt.Text;

                    Messages.Add(new TranscriptEntry(
                        "أداة ▸ " + evt.Text,
                        evt.Detail ?? string.Empty,
                        ToolBrush,
                        monospace: true));

                    break;

                case AgentEventKind.ToolFinished:
                case AgentEventKind.ToolFailed:
                    Messages.Add(new TranscriptEntry(
                        (evt.Kind == AgentEventKind.ToolFailed ? "فشل ▸ " : "نتيجة ▸ ")
                        + evt.Text,
                        evt.Detail ?? string.Empty,
                        evt.Kind == AgentEventKind.ToolFailed ? ErrorBrush : ToolBrush,
                        monospace: true));

                    // The model continues after a tool result, so start a fresh
                    // bubble instead of appending to text written before the call.
                    var next = new TranscriptEntry("نيّة", string.Empty, AssistantBrush, false);
                    Messages.Add(next);
                    target = next;

                    break;

                case AgentEventKind.Notice:
                    Messages.Add(Notice(evt.Text));
                    break;
            }
        });

        bubble = target;
    }

    private static (ToolRouter Router, ToolContext Context) BuildToolsCore(
        string rootPath,
        IConfirmationBroker broker)
    {
        var router = KhzToolCatalog.CreateRouter();

        var context = ToolContext.ForRoot(
            root: rootPath,
            confirmations: broker,
            audit: NullToolAuditSink.Instance,
            shell: new PowerShellRunner());

        return (router, context);
    }

    private (ToolRouter Router, ToolContext Context) BuildTools(string rootPath)
        => BuildToolsCore(rootPath, new WpfConfirmationBroker(_owner));

    private void UpdateToolSummary()
    {
        var remote = _mcp.Definitions().Count;

        ToolSummary = _router.Descriptors.Count + " أداة محلية"
                      + (remote > 0 ? " + " + remote + " أداة MCP" : string.Empty)
                      + " · " + Endpoint;
    }

    private static TranscriptEntry Notice(string text)
        => new("نظام", text, ToolBrush, monospace: false);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (Equals(field, value))
            return false;

        field = value;
        Raise(property);

        return true;
    }

    private void Raise(string? property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public event PropertyChangedEventHandler? PropertyChanged;

    public async ValueTask DisposeAsync()
    {
        _turn?.Cancel();
        _turn?.Dispose();

        await _mcp.DisposeAsync().ConfigureAwait(false);

        _http.Dispose();
    }
}
