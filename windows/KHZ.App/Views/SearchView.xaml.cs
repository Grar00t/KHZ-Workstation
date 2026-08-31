using KHZ.App.Trust;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KHZ.App.Views;

public partial class SearchView : UserControl
{
    private IActivityReader? _activityReader;
    private IActivityStore? _activity;
    private CapabilityPolicy? _policy;

    private string _rootDirectory = string.Empty;

    public SearchView()
    {
        InitializeComponent();
    }

    internal void Configure(
        IActivityReader activityReader,
        IActivityStore activity,
        CapabilityPolicy policy)
    {
        _activityReader =
            activityReader
            ?? throw new ArgumentNullException(nameof(activityReader));

        _activity =
            activity
            ?? throw new ArgumentNullException(nameof(activity));

        _policy =
            policy
            ?? throw new ArgumentNullException(nameof(policy));
    }

    internal void SetRootDirectory(
        string path)
    {
        _rootDirectory =
            path ?? string.Empty;

        RootPathText.Text =
            string.IsNullOrWhiteSpace(_rootDirectory)
                ? "Current folder: unavailable"
                : $"Current folder: {_rootDirectory}";
    }

    internal void FocusSearch()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private void Search_Click(
        object sender,
        RoutedEventArgs e)
        => RunSearch();

    private void QueryBox_KeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        RunSearch();
        e.Handled = true;
    }

    private void RunSearch()
    {
        SearchErrorText.Text = string.Empty;

        var query =
            QueryBox.Text.Trim();

        if (query.Length == 0)
        {
            FilesResults.ItemsSource = null;
            ActivityResults.ItemsSource = null;

            FilesCountText.Text = "FILES";
            ActivityCountText.Text = "ACTIVITY";

            SearchErrorText.Text =
                "Enter a search term.";

            return;
        }

        try
        {
            SearchFiles(query);
            SearchActivity(query);

            _activity?.Record(
                category: "search",
                action: "local.search",
                target: query,
                result: "COMPLETED",
                details: new
                {
                    fileScope = _rootDirectory,
                    recursive = false,
                    activityLimit = 500,
                    networkAttempted = false,
                    aiUsed = false
                });
        }
        catch (Exception ex)
        {
            SearchErrorText.Text =
                $"Search failed: {ex.Message}";
        }
    }

    private void SearchFiles(
        string query)
    {
        if (string.IsNullOrWhiteSpace(_rootDirectory))
        {
            FilesResults.ItemsSource = null;
            FilesCountText.Text = "FILES · 0";
            return;
        }

        var directory =
            new DirectoryInfo(_rootDirectory);

        if (!directory.Exists)
        {
            FilesResults.ItemsSource = null;
            FilesCountText.Text = "FILES · 0";
            return;
        }

        var rows =
            directory
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(
                    file => file.Name.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Name)
                .Take(200)
                .Select(
                    file => new FileSearchResult(
                        Name: file.Name,
                        FullPath: file.FullName,
                        Type:
                            file.Extension
                                .TrimStart('.')
                                .ToUpperInvariant(),
                        Modified:
                            file.LastWriteTime.ToString(
                                "yyyy-MM-dd HH:mm:ss",
                                CultureInfo.InvariantCulture)))
                .ToList();

        FilesResults.ItemsSource = rows;
        FilesCountText.Text = $"FILES · {rows.Count}";
    }

    private void SearchActivity(
        string query)
    {
        if (_activityReader is null)
        {
            ActivityResults.ItemsSource = null;
            ActivityCountText.Text = "ACTIVITY · 0";
            return;
        }

        var rows =
            _activityReader
                .ReadRecent(500)
                .Where(
                    item =>
                        Contains(item.Category, query)
                        || Contains(item.Action, query)
                        || Contains(item.Result, query)
                        || Contains(item.Target, query))
                .Take(200)
                .ToList();

        ActivityResults.ItemsSource = rows;
        ActivityCountText.Text = $"ACTIVITY · {rows.Count}";
    }

    private void FilesResults_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (FilesResults.SelectedItem
            is not FileSearchResult file)
        {
            return;
        }

        if (_policy is null
            || !_policy.IsAllowed(
                Capability.LocalFileLaunch))
        {
            SearchErrorText.Text =
                "Local file launch is not permitted by policy.";

            _activity?.Record(
                category: "security",
                action: "file.launch",
                target: file.FullPath,
                result: "DENIED");

            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = file.FullPath,
                    UseShellExecute = true
                });

            _activity?.Record(
                category: "filesystem",
                action: "file.launch",
                target: file.FullPath,
                result: "OPENED");

            SearchErrorText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            SearchErrorText.Text =
                $"Open failed: {ex.Message}";
        }
    }

    private static bool Contains(
        string? value,
        string query)
        => !string.IsNullOrEmpty(value)
           && value.Contains(
               query,
               StringComparison.OrdinalIgnoreCase);

    private sealed record FileSearchResult(
        string Name,
        string FullPath,
        string Type,
        string Modified);
}
