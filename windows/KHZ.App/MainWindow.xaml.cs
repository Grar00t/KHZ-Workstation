using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KHZ.App;

public partial class MainWindow : Window
{
    private static readonly Uri GatewayHealth =
        new("http://127.0.0.1:8090/health");

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private string _currentDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "WebView2");

        Directory.CreateDirectory(dataPath);

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: dataPath);

        await OfficeWeb.EnsureCoreWebView2Async(env);

        await RefreshRuntimeStatusAsync();
        NavigateOffice("sheet");
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        try
        {
            using var response = await _http.GetAsync(GatewayHealth);

            if (response.IsSuccessStatusCode)
            {
                RuntimeStatus.Text = "Local Office runtime online";
                RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(72, 170, 90));
                OfficeStatusText.Text = "OFFICE ONLINE";
                OfficeStatusPill.Background =
                    new SolidColorBrush(Color.FromRgb(231, 246, 234));
                return;
            }
        }
        catch
        {
        }

        RuntimeStatus.Text = "Local Office runtime offline";
        RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(190, 80, 80));
        OfficeStatusText.Text = "OFFICE OFFLINE";
        OfficeStatusPill.Background =
            new SolidColorBrush(Color.FromRgb(249, 232, 232));
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
        => await RefreshRuntimeStatusAsync();

    private void NavigateOffice(string kind)
    {
        if (OfficeWeb.CoreWebView2 is null)
            return;

        FilesSurface.Visibility = Visibility.Collapsed;
        OfficeWeb.Visibility = Visibility.Visible;

        SectionTitle.Text = kind switch
        {
            "document" => "Documents",
            "sheet" => "Sheets",
            "slide" => "Slides",
            "pdf" => "PDF",
            _ => "Office"
        };

        OfficeWeb.CoreWebView2.Navigate(
            $"http://localhost:8090/editor/{kind}");
    }

    private void Files_Click(object sender, RoutedEventArgs e)
    {
        OfficeWeb.Visibility = Visibility.Collapsed;
        FilesSurface.Visibility = Visibility.Visible;
        SectionTitle.Text = "Files";

        LoadDirectory(_currentDirectory);
    }

    private void LoadDirectory(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);

            if (!dir.Exists)
                return;

            _currentDirectory = dir.FullName;
            CurrentFolderText.Text = _currentDirectory;

            var directories = dir.EnumerateDirectories()
                .Select(x => new FileEntry(
                    x.Name,
                    x.FullName,
                    "Folder",
                    x.LastWriteTime,
                    "",
                    true));

            var files = dir.EnumerateFiles()
                .Select(x => new FileEntry(
                    x.Name,
                    x.FullName,
                    x.Extension.TrimStart('.').ToUpperInvariant(),
                    x.LastWriteTime,
                    FormatSize(x.Length),
                    false));

            FilesList.ItemsSource =
                directories.Concat(files)
                    .OrderByDescending(x => x.IsDirectory)
                    .ThenBy(x => x.Name)
                    .ToList();

            FilesError.Text = "";
        }
        catch (Exception ex)
        {
            FilesList.ItemsSource = null;
            FilesError.Text = ex.Message;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Open folder in KHZ Workstation",
            InitialDirectory = _currentDirectory
        };

        if (picker.ShowDialog() == true)
            LoadDirectory(picker.FolderName);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentDirectory);

        if (parent is not null)
            LoadDirectory(parent.FullName);
    }

    private void RefreshFiles_Click(object sender, RoutedEventArgs e)
        => LoadDirectory(_currentDirectory);

    private void FilesList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is not FileEntry entry)
            return;

        if (entry.IsDirectory)
        {
            LoadDirectory(entry.FullPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = entry.FullPath,
                UseShellExecute = true
            });

            FilesError.Text = "";
        }
        catch (Exception ex)
        {
            FilesError.Text = ex.Message;
        }
    }

    private void Documents_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("document");

    private void Sheets_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("sheet");

    private void Slides_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("slide");

    private void Pdf_Click(object sender, RoutedEventArgs e)
        => NavigateOffice("pdf");

    protected override void OnClosed(EventArgs e)
    {
        _http.Dispose();
        base.OnClosed(e);
    }

    private sealed record FileEntry(
        string Name,
        string FullPath,
        string Type,
        DateTime Modified,
        string Size,
        bool IsDirectory);
}
