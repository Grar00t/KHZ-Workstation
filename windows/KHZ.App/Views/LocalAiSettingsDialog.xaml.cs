using KHZ.App.Chat;
using Microsoft.Win32;
using System;
using System.Globalization;
using System.Windows;

namespace KHZ.App.Views;

public partial class LocalAiSettingsDialog : Window
{
    private readonly SqliteLocalAiStore _store;

    internal LocalAiSettings? SavedSettings { get; private set; }

    internal LocalAiSettingsDialog(
        SqliteLocalAiStore store)
    {
        _store = store
            ?? throw new ArgumentNullException(nameof(store));

        InitializeComponent();
        LoadValues(_store.GetSettings().ResolveEffective());
    }

    private void LoadValues(LocalAiSettings settings)
    {
        ModelLabelText.Text = settings.ModelLabel;
        RuntimePathText.Text = settings.RuntimeExecutable;
        ModelPathText.Text = settings.ModelPath;
        AdapterPathText.Text = settings.AdapterPath ?? string.Empty;
        TemplatePathText.Text = settings.ChatTemplatePath ?? string.Empty;
        ContextSizeText.Text = settings.ContextSize.ToString(CultureInfo.InvariantCulture);
        GpuLayersText.Text = settings.GpuLayers;
        ToolsEnabledCheck.IsChecked = settings.ToolsEnabled;
        HideReasoningCheck.IsChecked = settings.HideReasoning;
    }

    private void BrowseRuntime_Click(object sender, RoutedEventArgs e)
        => BrowseInto(
            RuntimePathText,
            "Executables (*.exe)|*.exe|All files (*.*)|*.*");

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
        => BrowseInto(
            ModelPathText,
            "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*");

    private void BrowseAdapter_Click(object sender, RoutedEventArgs e)
        => BrowseInto(
            AdapterPathText,
            "GGUF adapters (*.gguf)|*.gguf|All files (*.*)|*.*");

    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
        => BrowseInto(
            TemplatePathText,
            "Jinja templates (*.jinja)|*.jinja|Text files (*.txt)|*.txt|All files (*.*)|*.*");

    private static void BrowseInto(
        System.Windows.Controls.TextBox target,
        string filter)
    {
        var picker = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog() == true)
            target.Text = picker.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        if (!int.TryParse(
                ContextSizeText.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var contextSize))
        {
            ErrorText.Text = "Context tokens must be an integer.";
            return;
        }

        try
        {
            var settings = new LocalAiSettings(
                ModelLabel: ModelLabelText.Text,
                RuntimeExecutable: RuntimePathText.Text,
                ModelPath: ModelPathText.Text,
                AdapterPath: NullIfBlank(AdapterPathText.Text),
                ChatTemplatePath: NullIfBlank(TemplatePathText.Text),
                ContextSize: contextSize,
                GpuLayers: GpuLayersText.Text,
                ToolsEnabled: ToolsEnabledCheck.IsChecked == true,
                HideReasoning: HideReasoningCheck.IsChecked == true);

            SavedSettings = _store.SaveSettings(settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _store.ClearSettings();
        SavedSettings = LocalAiSettings.Default();
        LoadValues(SavedSettings);
        ErrorText.Text = "Saved local AI configuration cleared.";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}
