using KHZ.App.Views;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KHZ.App;

public partial class MainWindow
{
    private ChatView? _chatSurface;
    private Button? _chatButton;
    private bool _chatInitialized;

    private void InitializeChatSurface()
    {
        if (_chatInitialized)
            return;

        _chatInitialized = true;

        var host = TerminalSurface.Parent as Grid
            ?? throw new InvalidOperationException(
                "Main content host was not found.");

        _chatSurface = new ChatView
        {
            Visibility = Visibility.Collapsed
        };

        _chatSurface.Configure(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "KHZ",
                "state",
                "local-ai.db"),
            _repositoryInspector,
            _terminalRunner,
            _activity);

        Panel.SetZIndex(_chatSurface, 2);
        host.Children.Add(_chatSurface);

        var navigation = HomeButton.Parent as Panel
            ?? throw new InvalidOperationException(
                "Navigation host was not found.");

        _chatButton = CreateChatNavigationButton();

        var searchIndex = navigation.Children.IndexOf(SearchButton);
        navigation.Children.Insert(
            searchIndex >= 0 ? searchIndex + 1 : 0,
            _chatButton);

        AttachChatCollapseHandlers();
        Closed += MainWindow_ChatClosed;
    }

    private Button CreateChatNavigationButton()
    {
        var icon = new TextBlock
        {
            Text = "✦",
            FontSize = 15,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = "Chat",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        content.Children.Add(icon);
        content.Children.Add(label);

        var button = new Button
        {
            Name = "ChatButton",
            Content = content,
            Background = new SolidColorBrush(
                Color.FromRgb(48, 48, 48))
        };

        button.SetResourceReference(
            FrameworkElement.StyleProperty,
            "NavButton");

        button.Click += Chat_Click;
        return button;
    }

    private void AttachChatCollapseHandlers()
    {
        Button[] buttons =
        [
            HomeButton,
            FilesButton,
            SearchButton,
            TasksButton,
            StructuredDataButton,
            BackupRestoreButton,
            RepositoriesButton,
            TerminalButton,
            DocumentsButton,
            SheetsButton,
            SlidesButton,
            PdfButton,
            ActivityButton,
            SecurityButton,
            IntegrationsButton,
            SettingsButton
        ];

        foreach (var button in buttons)
            button.Click += (_, _) => HideChatSurface();
    }

    private void HideChatSurface()
    {
        if (_chatSurface is not null)
            _chatSurface.Visibility = Visibility.Collapsed;
    }

    private void Chat_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_chatSurface is null)
            return;

        _activity.Record(
            category: "navigation",
            action: "chat.open",
            target: _activeWorkspace?.Info.WorkspaceId ?? _currentDirectory,
            result: "OPENED",
            details: new
            {
                localOnly = true,
                modelDownloadedByKhz = false
            });

        OfficeWeb.Visibility = Visibility.Collapsed;
        HomeSurface.Visibility = Visibility.Collapsed;
        FilesSurface.Visibility = Visibility.Collapsed;
        ActivitySurface.Visibility = Visibility.Collapsed;
        SecuritySurface.Visibility = Visibility.Collapsed;
        IntegrationsSurface.Visibility = Visibility.Collapsed;
        SettingsSurface.Visibility = Visibility.Collapsed;
        SearchSurface.Visibility = Visibility.Collapsed;
        TasksSurface.Visibility = Visibility.Collapsed;
        StructuredDataSurface.Visibility = Visibility.Collapsed;
        BackupRestoreSurface.Visibility = Visibility.Collapsed;
        RepositoriesSurface.Visibility = Visibility.Collapsed;
        TerminalSurface.Visibility = Visibility.Collapsed;

        _chatSurface.SetContext(
            _currentDirectory,
            _activeWorkspace);

        _chatSurface.Visibility = Visibility.Visible;
        SectionTitle.Text = "Chat";
    }

    private void MainWindow_ChatClosed(
        object? sender,
        EventArgs e)
    {
        Closed -= MainWindow_ChatClosed;
        _chatSurface?.Shutdown();
    }
}
