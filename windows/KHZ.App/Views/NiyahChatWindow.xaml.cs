using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using KHZ.App.Mcp;
using Microsoft.Win32;

namespace KHZ.App.Views;

/// <summary>
/// Executive chat window: local model, local tools, MCP servers.
/// </summary>
public partial class NiyahChatWindow : Window
{
    private readonly NiyahChatViewModel _model;

    /// <summary>Creates the window for a workspace root and local model endpoint.</summary>
    /// <param name="rootPath">Workspace root that bounds every tool.</param>
    /// <param name="endpoint">Local model base URL.</param>
    public NiyahChatWindow(string rootPath, string endpoint)
    {
        InitializeComponent();

        _model = new NiyahChatViewModel(rootPath, endpoint, () => this);
        DataContext = _model;

        Loaded += async (_, _) => await _model.ConnectMcpAsync();
        Closed += async (_, _) => await _model.DisposeAsync();
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        await _model.SendAsync();
        Transcript.ScrollToEnd();
    }

    private void OnStop(object sender, RoutedEventArgs e) => _model.Stop();

    private void OnNewChat(object sender, RoutedEventArgs e) => _model.NewChat();

    private async void OnReconnectMcp(object sender, RoutedEventArgs e)
        => await _model.ConnectMcpAsync();

    private void OnPickRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "اختر مجلد العمل",
            InitialDirectory = Directory.Exists(_model.RootPath)
                ? _model.RootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog(this) == true)
            _model.ChangeRoot(dialog.FolderName);
    }

    private void OnOpenMcpConfig(object sender, RoutedEventArgs e)
    {
        // Load() writes a documented default when the file is missing, so the
        // config always exists before we try to open it.
        McpServerRegistry.Load(out _);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = McpServerRegistry.ConfigPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "تعذر فتح ملف الإعداد",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>Enter sends; Shift+Enter inserts a newline.</summary>
    private async void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        e.Handled = true;

        await _model.SendAsync();
        Transcript.ScrollToEnd();
    }
}
