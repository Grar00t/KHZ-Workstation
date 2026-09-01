using KHZ.App.Chat;
using KHZ.App.Repositories;
using KHZ.App.Terminal;
using KHZ.App.Trust;
using KHZ.App.Workspaces;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KHZ.App.Views;

public partial class ChatView : UserControl
{
    private const int MaxToolSteps = 8;

    private readonly ObservableCollection<ChatConversation> _conversations = new();
    private readonly ObservableCollection<TranscriptRow> _transcript = new();
    private readonly LlamaRuntimeHost _runtime = new();
    private readonly LlamaChatClient _client = new();

    private SqliteLocalAiStore? _store;
    private ChatToolExecutor? _tools;
    private IActivityStore? _activity;
    private ChatContext? _context;
    private string? _conversationId;
    private CancellationTokenSource? _requestCancellation;
    private bool _configured;
    private bool _suppressSelection;
    private bool _disposed;

    public ChatView()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _conversations;
        TranscriptItems.ItemsSource = _transcript;

        _runtime.StateChanged += Runtime_StateChanged;
        LocalAiSessionGate.Shared.Changed += SessionGate_Changed;
        RefreshSessionUi();
    }

    internal void Configure(
        string databasePath,
        IRepositoryInspector repositoryInspector,
        ITerminalRunner terminalRunner,
        IActivityStore activity)
    {
        if (_configured)
            return;

        _store = new SqliteLocalAiStore(databasePath);
        _store.Initialize();
        _tools = new ChatToolExecutor(
            repositoryInspector,
            terminalRunner,
            activity);
        _activity = activity;
        _configured = true;
        RefreshSettingsUi();
    }

    internal void SetContext(
        string currentDirectory,
        WorkspaceContext? workspace)
    {
        EnsureConfigured();
        var next = ChatContext.Create(currentDirectory, workspace);

        if (_context?.ContextId == next.ContextId)
        {
            ContextText.Text = next.DisplayName;
            return;
        }

        _context = next;
        ContextText.Text = next.DisplayName;
        _conversationId = null;
        _transcript.Clear();
        LoadConversations(selectFirst: true);
    }

    private void LoadConversations(bool selectFirst)
    {
        if (_store is null || _context is null)
            return;

        _suppressSelection = true;
        try
        {
            _conversations.Clear();
            foreach (var item in _store.ListConversations(_context.ContextId))
                _conversations.Add(item);

            if (selectFirst && _conversations.Count > 0)
                ConversationList.SelectedIndex = 0;
            else if (_conversations.Count == 0)
                ShowEmptyState();
        }
        finally
        {
            _suppressSelection = false;
        }

        if (selectFirst && _conversations.Count > 0)
            OpenConversation(_conversations[0]);
    }

    private void OpenConversation(ChatConversation conversation)
    {
        if (_store is null || _context is null)
            return;

        _conversationId = conversation.ConversationId;
        _transcript.Clear();

        foreach (var message in _store.GetMessages(
                     _context.ContextId,
                     conversation.ConversationId))
        {
            if (message.Role == "tool")
                continue;

            if (message.Role == "assistant"
                && !string.IsNullOrWhiteSpace(message.ToolName)
                && string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            _transcript.Add(new TranscriptRow(
                RoleLabel: message.Role == "user" ? "You" : "Assistant",
                Content: message.Content,
                Background: message.Role == "user"
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(247, 247, 248))));
        }

        ScrollToEnd();
    }

    private void ShowEmptyState()
    {
        _transcript.Clear();
        _transcript.Add(new TranscriptRow(
            "KHZ",
            "Start a local conversation. The model is not trusted to identify itself; KHZ displays the configured model label. Hidden reasoning is not stored in chat history.",
            Brushes.White));
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        EnsureConfigured();
        if (_store is null || _context is null)
            return;

        var conversation = _store.CreateConversation(
            _context.ContextId,
            "New chat");
        _conversationId = conversation.ConversationId;
        LoadConversations(selectFirst: false);

        _suppressSelection = true;
        ConversationList.SelectedItem = _conversations.FirstOrDefault(
            x => x.ConversationId == conversation.ConversationId);
        _suppressSelection = false;

        _transcript.Clear();
        ComposerText.Focus();
    }

    private void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _context is null || _conversationId is null)
            return;

        var decision = MessageBox.Show(
            "Delete this local conversation?",
            "KHZ · Delete chat",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (decision != MessageBoxResult.Yes)
            return;

        _store.DeleteConversation(_context.ContextId, _conversationId);
        _conversationId = null;
        LoadConversations(selectFirst: true);
    }

    private void ConversationList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;

        if (ConversationList.SelectedItem is ChatConversation conversation)
            OpenConversation(conversation);
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (LocalAiSessionGate.Shared.IsEnabled)
        {
            LocalAiSessionGate.Shared.Disable();
            _ = _runtime.StopAsync();

            _activity?.Record(
                category: "ai",
                action: "session.disable",
                target: "local-ai",
                result: "DISABLED",
                details: new { persisted = false });
        }
        else
        {
            LocalAiSessionGate.Shared.Enable();
            _activity?.Record(
                category: "ai",
                action: "session.enable",
                target: "local-ai",
                result: "ENABLED",
                details: new { persisted = false });
        }
    }

    private async void Runtime_Click(object sender, RoutedEventArgs e)
    {
        EnsureConfigured();

        if (!LocalAiSessionGate.Shared.IsEnabled)
        {
            FeedbackText.Text = "Enable local AI for this session first.";
            return;
        }

        if (_runtime.Snapshot.Status is LocalAiRuntimeStatus.Ready
            or LocalAiRuntimeStatus.Starting)
        {
            await _runtime.StopAsync();
            return;
        }

        try
        {
            var settings = GetSettingsForUse();
            await _runtime.EnsureStartedAsync(settings);
        }
        catch (Exception ex)
        {
            FeedbackText.Text = ex.Message;
        }
    }

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        EnsureConfigured();
        if (_store is null)
            return;

        var dialog = new LocalAiSettingsDialog(_store)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true)
        {
            RefreshSettingsUi();
            _ = _runtime.StopAsync();
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
        => await SendCurrentAsync();

    private async Task SendCurrentAsync()
    {
        EnsureConfigured();

        if (_store is null
            || _tools is null
            || _activity is null
            || _context is null)
        {
            FeedbackText.Text = "Chat dependencies are not ready.";
            return;
        }

        if (!LocalAiSessionGate.Shared.IsEnabled)
        {
            FeedbackText.Text = "Enable local AI for this session first.";
            return;
        }

        var text = ComposerText.Text.Trim();
        if (text.Length == 0)
            return;

        if (text.Length > 100_000)
        {
            FeedbackText.Text = "Message is too large.";
            return;
        }

        var settings = GetSettingsForUse();

        if (_conversationId is null)
        {
            var created = _store.CreateConversation(
                _context.ContextId,
                MakeTitle(text));
            _conversationId = created.ConversationId;
            LoadConversations(selectFirst: false);
        }
        else
        {
            var existing = _store.GetConversation(
                _context.ContextId,
                _conversationId);
            if (existing is not null
                && string.Equals(existing.Title, "New chat", StringComparison.Ordinal))
            {
                _store.RenameConversation(
                    _context.ContextId,
                    _conversationId,
                    MakeTitle(text));
            }
        }

        var conversationId = _conversationId
            ?? throw new InvalidOperationException("Conversation was not created.");

        _store.AppendMessage(
            _context.ContextId,
            conversationId,
            "user",
            text);

        _transcript.Add(new TranscriptRow(
            "You",
            text,
            Brushes.White));

        ComposerText.Clear();
        SetBusy(true);
        _requestCancellation = new CancellationTokenSource();
        var token = _requestCancellation.Token;

        _activity.Record(
            category: "ai",
            action: "chat.request",
            target: _context.ContextId,
            result: "STARTED",
            details: new
            {
                conversationId,
                promptLength = text.Length,
                rawPromptCaptured = false,
                model = settings.ModelLabel,
                toolsEnabled = settings.ToolsEnabled
            });

        try
        {
            var endpoint = await _runtime.EnsureStartedAsync(settings, token);

            for (var step = 0; step < MaxToolSteps; step++)
            {
                var history = _store.GetMessages(
                    _context.ContextId,
                    conversationId);

                var completion = await _client.CompleteAsync(
                    endpoint,
                    history,
                    settings.ToolsEnabled
                        ? _tools.Definitions
                        : Array.Empty<ChatToolDefinition>(),
                    settings,
                    token);

                if (completion.ToolCall is null)
                {
                    var visible = string.IsNullOrWhiteSpace(completion.Content)
                        ? "No visible response was returned."
                        : completion.Content;

                    _store.AppendMessage(
                        _context.ContextId,
                        conversationId,
                        "assistant",
                        visible);

                    _transcript.Add(new TranscriptRow(
                        "Assistant",
                        visible,
                        new SolidColorBrush(Color.FromRgb(247, 247, 248))));

                    _activity.Record(
                        category: "ai",
                        action: "chat.response",
                        target: _context.ContextId,
                        result: "PASSED",
                        details: new
                        {
                            conversationId,
                            responseLength = visible.Length,
                            hiddenReasoningPersisted = false,
                            toolSteps = step
                        });

                    LoadConversations(selectFirst: false);
                    ScrollToEnd();
                    return;
                }

                _store.AppendMessage(
                    _context.ContextId,
                    conversationId,
                    "assistant",
                    completion.Content,
                    toolName: completion.ToolCall.Name,
                    toolCallId: completion.ToolCall.Id,
                    toolArgumentsJson: completion.ToolCall.ArgumentsJson);

                var toolResult = await _tools.ExecuteAsync(
                    completion.ToolCall,
                    _context,
                    token);

                _store.AppendMessage(
                    _context.ContextId,
                    conversationId,
                    "tool",
                    toolResult,
                    toolName: completion.ToolCall.Name,
                    toolCallId: completion.ToolCall.Id);

                FeedbackText.Text =
                    $"Tool: {completion.ToolCall.Name} · step {step + 1}";
            }

            throw new InvalidOperationException(
                $"Tool loop exceeded {MaxToolSteps} steps without a final answer.");
        }
        catch (OperationCanceledException)
        {
            FeedbackText.Text = "Cancelled.";
            _activity.Record(
                category: "ai",
                action: "chat.request",
                target: _context.ContextId,
                result: "CANCELLED",
                details: new { conversationId });
        }
        catch (Exception ex)
        {
            FeedbackText.Text = ex.Message;
            _activity.Record(
                category: "ai",
                action: "chat.request",
                target: _context.ContextId,
                result: "FAILED",
                details: new
                {
                    conversationId,
                    error = ex.GetType().Name,
                    rawPromptCaptured = false
                });
        }
        finally
        {
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            SetBusy(false);
            RefreshRuntimeUi(_runtime.Snapshot);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => _requestCancellation?.Cancel();

    private async void ComposerText_KeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendCurrentAsync();
        }
    }

    private LocalAiSettings GetSettingsForUse()
    {
        if (_store is null)
            throw new InvalidOperationException("Local AI store is not configured.");

        var effective = _store.GetSettings().ResolveEffective();
        return effective.ValidateForUse();
    }

    private void RefreshSettingsUi()
    {
        if (_store is null)
            return;

        var settings = _store.GetSettings().ResolveEffective();
        ModelText.Text = settings.ModelLabel;
    }

    private void RefreshSessionUi()
    {
        var enabled = LocalAiSessionGate.Shared.IsEnabled;
        SessionButton.Content = enabled
            ? "Disable local AI"
            : "Enable local AI";
        RuntimeButton.IsEnabled = enabled && _requestCancellation is null;
        SendButton.IsEnabled = enabled && _requestCancellation is null;

        if (!enabled)
            RuntimeStatusText.Text = "Local AI disabled for this session";
        else
            RefreshRuntimeUi(_runtime.Snapshot);
    }

    private void Runtime_StateChanged(
        object? sender,
        LocalAiRuntimeSnapshot snapshot)
    {
        Dispatcher.Invoke(() => RefreshRuntimeUi(snapshot));
    }

    private void SessionGate_Changed(object? sender, EventArgs e)
        => Dispatcher.Invoke(RefreshSessionUi);

    private void RefreshRuntimeUi(LocalAiRuntimeSnapshot snapshot)
    {
        RuntimeStatusText.Text = snapshot.Detail;
        RuntimeButton.Content = snapshot.Status is LocalAiRuntimeStatus.Ready
            or LocalAiRuntimeStatus.Starting
                ? "Stop model"
                : "Start model";
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy && LocalAiSessionGate.Shared.IsEnabled;
        CancelButton.IsEnabled = busy;
        SessionButton.IsEnabled = !busy;
        RuntimeButton.IsEnabled = !busy && LocalAiSessionGate.Shared.IsEnabled;
        ComposerText.IsEnabled = !busy;
        if (busy)
            FeedbackText.Text = "Working locally…";
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(
            new Action(() => TranscriptScroll.ScrollToEnd()));
    }

    private static string MakeTitle(string text)
    {
        var oneLine = string.Join(
            " ",
            text.Split(
                new[] { '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries));
        oneLine = oneLine.Trim();
        if (oneLine.Length == 0)
            return "New chat";
        return oneLine.Length <= 72
            ? oneLine
            : oneLine[..72] + "…";
    }

    private void EnsureConfigured()
    {
        if (!_configured)
            throw new InvalidOperationException("ChatView is not configured.");
    }

    private async void ChatView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
            return;

        var window = Window.GetWindow(this);
        if (window is not null && window.IsVisible)
            return;

        _disposed = true;
        LocalAiSessionGate.Shared.Changed -= SessionGate_Changed;
        _requestCancellation?.Cancel();
        await _runtime.DisposeAsync();
        _client.Dispose();
    }

    private sealed record TranscriptRow(
        string RoleLabel,
        string Content,
        Brush Background);
}
