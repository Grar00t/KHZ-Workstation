using System.IO;
using System.Security;
using System.Windows;
using LibRdlWpfViewer;
using Majorsilence.Reporting.Rdl;
using Microsoft.Data.Sqlite;

namespace KHZ.Reporting.Spike;

public partial class MainWindow : Window
{
    private readonly RdlWpfViewer _viewer = new();
    private readonly string _templatePath;
    private readonly string _databasePath;

    public MainWindow()
    {
        InitializeComponent();

        ViewerHost.Content = _viewer;
        _templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "AssetRegister.rdl");

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "ReportingSpike");

        Directory.CreateDirectory(dataDir);
        _databasePath = Path.Combine(dataDir, "asset-register.db");

        Loaded += async (_, _) =>
        {
            try
            {
                EnsureSampleDatabase();
                await LoadReportAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        };
    }

    private void EnsureSampleDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS assets (
                row_no      INTEGER PRIMARY KEY,
                tag         TEXT NOT NULL,
                description TEXT NOT NULL,
                location    TEXT NOT NULL
            );

            INSERT OR IGNORE INTO assets(row_no, tag, description, location) VALUES
                (1, 'KU093973', 'DRAWER', 'COSHP-F...'),
                (2, 'KU093974', 'CABINET', 'COSHP-F...'),
                (3, 'KU093975', 'WORK BENCH', 'COSHP-G...'),
                (4, 'KU093976', 'STORAGE RACK', 'COSHP-G...');
            """;
        command.ExecuteNonQuery();
    }

    private async Task LoadReportAsync()
    {
        if (!File.Exists(_templatePath))
            throw new FileNotFoundException("Asset register template was not copied to the output directory.", _templatePath);

        var title = SecurityElement.Escape(TitleBox.Text.Trim()) ?? "ASSET REGISTER";
        var company = SecurityElement.Escape(CompanyBox.Text.Trim()) ?? "KHZ";
        var database = SecurityElement.Escape(_databasePath) ?? _databasePath;

        var rdl = await File.ReadAllTextAsync(_templatePath);
        rdl = rdl
            .Replace("__REPORT_TITLE__", title, StringComparison.Ordinal)
            .Replace("__COMPANY__", company, StringComparison.Ordinal)
            .Replace("__DB_PATH__", database, StringComparison.Ordinal);

        await _viewer.SetSourceRdl(rdl);
        await _viewer.Rebuild();
        StatusText.Text = $"Preview ready · local DB: {_databasePath}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadReportAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Asset Register",
                Filter = "PDF document (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = "asset-register.pdf"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            await _viewer.SaveAs(dialog.FileName, OutputPresentationType.PDF);
            StatusText.Text = $"PDF written: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(
            this,
            ex.ToString(),
            "KHZ Reporting Spike",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
