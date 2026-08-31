using System.Windows.Threading;
using System.Globalization;
using KHZ.App.Trust;
using KHZ.App.Integrations;
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
    private readonly DispatcherTimer _clockTimer =
        new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };

    private static readonly CultureInfo GregorianClockCulture =
        CultureInfo.GetCultureInfo("en-US");

    private static readonly CultureInfo HijriClockCulture =
        CreateHijriClockCulture();

    private readonly TrustStore _trust = new();

    private readonly IActivityStore _activity;

    private readonly IActivityReader _activityReader;

    private readonly IIntegrationConfigStore _integrationConfigStore;

    private readonly CapabilityPolicy _policy =
        CapabilityPolicy.CreateInstitutionalDefault();

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

        _activity = _trust;
        _activityReader = _trust;

        _integrationConfigStore =
            new SqliteIntegrationConfigStore(
                _trust.DatabasePath);

        ActivitySurface.Configure(
            _activityReader);

        SecuritySurface.Configure(
            _trust,
            _policy);

        IntegrationsSurface.Configure(
            _integrationConfigStore,
            _activity);

        _clockTimer.Tick += (_, _) => UpdateClock();

        UpdateClock();
        _clockTimer.Start();
        Loaded += MainWindow_Loaded;
    }

    private static CultureInfo CreateHijriClockCulture()
    {
        var culture =
            (CultureInfo)CultureInfo
                .GetCultureInfo("ar-SA")
                .Clone();

        culture.DateTimeFormat.Calendar =
            new UmAlQuraCalendar();

        return culture;
    }

    private void UpdateClock()
    {
        var now = DateTimeOffset.Now;

        var period =
            now.Hour < 12
                ? "ص"
                : "م";

        ClockTimeText.Text =
            $"{now.ToString(
                "hh:mm:ss",
                CultureInfo.InvariantCulture)} {period}";

        GregorianDateText.Text =
            now.ToString(
                "dddd, dd MMMM yyyy",
                GregorianClockCulture);

        HijriDateText.Text =
            now.DateTime.ToString(
                "dddd، d MMMM yyyy هـ",
                HijriClockCulture);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _trust.Initialize();

        _activity.Record(
            category: "system",
            action: "application.start",
            target: Environment.ProcessPath,
            result: "STARTED",
            details: new
            {
                database = _trust.DatabasePath,
                integrity = _trust.IntegrityStatus
            });

        var dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "WebView2");

        Directory.CreateDirectory(dataPath);

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: dataPath);

        await OfficeWeb.EnsureCoreWebView2Async(env);

        OfficeWeb.CoreWebView2.NavigationStarting += OfficeNavigationStarting;
        OfficeWeb.CoreWebView2.NewWindowRequested += OfficeNewWindowRequested;

        await RefreshRuntimeStatusAsync();
        ShowHome();
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        try
        {
            using var response = await _http.GetAsync(GatewayHealth);

            if (response.IsSuccessStatusCode)
            {
                RuntimeStatus.Text = "Local Office runtime online";
                HomeRuntimeStatus.Text = "Online";
                RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(72, 170, 90));
                OfficeStatusText.Text = "OFFICE ONLINE";
                OfficeStatusPill.Background =
                    new SolidColorBrush(Color.FromRgb(231, 246, 234));

                _activity.Record(
                    category: "runtime",
                    action: "office.health",
                    target: GatewayHealth.ToString(),
                    result: "ONLINE");

                return;
            }
        }
        catch
        {
        }

        RuntimeStatus.Text = "Local Office runtime offline";
        HomeRuntimeStatus.Text = "Offline";
        RuntimeDot.Fill = new SolidColorBrush(Color.FromRgb(190, 80, 80));
        OfficeStatusText.Text = "OFFICE OFFLINE";
        OfficeStatusPill.Background =
            new SolidColorBrush(Color.FromRgb(249, 232, 232));

        _activity.Record(
            category: "runtime",
            action: "office.health",
            target: GatewayHealth.ToString(),
            result: "OFFLINE");
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
        => await RefreshRuntimeStatusAsync();

    private void NavigateOffice(string kind)
    {
        ActivitySurface.Visibility = Visibility.Collapsed;
        SecuritySurface.Visibility = Visibility.Collapsed;
        IntegrationsSurface.Visibility = Visibility.Collapsed;

        if (OfficeWeb.CoreWebView2 is null)
            return;

        HomeSurface.Visibility = Visibility.Collapsed;
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

        _activity.Record(
            category: "navigation",
            action: "office.open",
            target: kind,
            result: "REQUESTED");

        OfficeWeb.CoreWebView2.Navigate(
            $"http://localhost:8090/editor/{kind}");
    }

    private void ShowHome()
    {
        ActivitySurface.Visibility = Visibility.Collapsed;
        SecuritySurface.Visibility = Visibility.Collapsed;
        IntegrationsSurface.Visibility = Visibility.Collapsed;

        OfficeWeb.Visibility = Visibility.Collapsed;
        FilesSurface.Visibility = Visibility.Collapsed;
        HomeSurface.Visibility = Visibility.Visible;
        SectionTitle.Text = "Home";

        _activity.Record(
            category: "navigation",
            action: "home.open",
            target: "home",
            result: "OPENED");
    }

    private void Home_Click(object sender, RoutedEventArgs e)
        => ShowHome();

    private bool IsAllowedOfficeNavigation(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target))
            return false;

        if (target.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!target.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return false;

        var localHost =
            target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            target.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        return
            localHost &&
            target.Port == 8090 &&
            _policy.IsAllowed(
                Capability.LocalOfficeNavigation);
    }

    private void OfficeNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAllowedOfficeNavigation(e.Uri))
        {
            e.Cancel = true;

            _activity.Record(
                category: "security",
                action: "webview.navigation",
                target: e.Uri,
                result: "DENIED",
                details: new
                {
                    capability =
                        Capability.ExternalWebNavigation.ToString()
                });
        }
    }

    private void OfficeNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (IsAllowedOfficeNavigation(e.Uri))
            OfficeWeb.CoreWebView2?.Navigate(e.Uri);
    }

    private void Files_Click(object sender, RoutedEventArgs e)
    {
        ActivitySurface.Visibility = Visibility.Collapsed;
        SecuritySurface.Visibility = Visibility.Collapsed;
        IntegrationsSurface.Visibility = Visibility.Collapsed;

        HomeSurface.Visibility = Visibility.Collapsed;
        OfficeWeb.Visibility = Visibility.Collapsed;
        FilesSurface.Visibility = Visibility.Visible;
        SectionTitle.Text = "Files";

        _activity.Record(
            category: "navigation",
            action: "files.open",
            target: _currentDirectory,
            result: "OPENED");

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
                    x.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    "",
                    true));

            var files = dir.EnumerateFiles()
                .Select(x => new FileEntry(
                    x.Name,
                    x.FullName,
                    x.Extension.TrimStart('.').ToUpperInvariant(),
                    x.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
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

        if (!_policy.IsAllowed(
                Capability.LocalFileLaunch))
        {
            _activity.Record(
                category: "security",
                action: "file.launch",
                target: entry.FullPath,
                result: "DENIED");

            FilesError.Text =
                "Local file launch is not permitted by policy.";

            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = entry.FullPath,
                UseShellExecute = true
            });

            _activity.Record(
                category: "filesystem",
                action: "file.launch",
                target: entry.FullPath,
                result: "OPENED");

            FilesError.Text = "";
        }
        catch (Exception ex)
        {
            FilesError.Text = ex.Message;
        }
    }

    private void Activity_Click(
        object sender,
        RoutedEventArgs e)
    {
        _activity.Record(
            category: "navigation",
            action: "activity.open",
            target: "activity",
            result: "OPENED");

        OfficeWeb.Visibility = Visibility.Collapsed;
        HomeSurface.Visibility = Visibility.Collapsed;
        FilesSurface.Visibility = Visibility.Collapsed;
        SecuritySurface.Visibility = Visibility.Collapsed;
        IntegrationsSurface.Visibility = Visibility.Collapsed;

        ActivitySurface.Visibility = Visibility.Visible;
        SectionTitle.Text = "Activity";

        ActivitySurface.RefreshActivity();
    }

    private void Security_Click(
        object sender,
        RoutedEventArgs e)
    {
        _activity.Record(
            category: "navigation",
            action: "security.open",
            target: "security",
            result: "OPENED");

        OfficeWeb.Visibility = Visibility.Collapsed;
        HomeSurface.Visibility = Visibility.Collapsed;
        FilesSurface.Visibility = Visibility.Collapsed;
        ActivitySurface.Visibility = Visibility.Collapsed;
        IntegrationsSurface.Visibility = Visibility.Collapsed;

        SecuritySurface.Visibility = Visibility.Visible;
        SectionTitle.Text = "Security";

        SecuritySurface.RefreshSecurity();
    }

    private void Integrations_Click(
        object sender,
        RoutedEventArgs e)
    {
        _activity.Record(
            category: "navigation",
            action: "integrations.open",
            target: "integrations",
            result: "OPENED");

        OfficeWeb.Visibility = Visibility.Collapsed;
        HomeSurface.Visibility = Visibility.Collapsed;
        FilesSurface.Visibility = Visibility.Collapsed;
        ActivitySurface.Visibility = Visibility.Collapsed;
        SecuritySurface.Visibility = Visibility.Collapsed;

        IntegrationsSurface.Visibility = Visibility.Visible;
        SectionTitle.Text = "Integrations";

        IntegrationsSurface.LoadConfiguration();
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
        _clockTimer.Stop();
        try
        {
            _activity.Record(
                category: "system",
                action: "application.stop",
                target: Environment.ProcessPath,
                result: "STOPPED");
        }
        catch
        {
            // Shutdown must not be blocked by audit persistence failure.
        }

        _http.Dispose();
        base.OnClosed(e);
    }

    private sealed record FileEntry(
        string Name,
        string FullPath,
        string Type,
        string Modified,
        string Size,
        bool IsDirectory);
}
