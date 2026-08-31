using KHZ.App.Repositories;
using KHZ.App.Trust;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class RepositoriesView : UserControl
{
    private IRepositoryInspector? _inspector;
    private IActivityStore? _activity;
    private CapabilityPolicy? _policy;

    private string _selectedDirectory =
        Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments);

    private CancellationTokenSource? _inspectionCancellation;

    public RepositoriesView()
    {
        InitializeComponent();

        RepositoryPathBox.Text =
            _selectedDirectory;
    }

    internal void Configure(
        IRepositoryInspector inspector,
        IActivityStore activity,
        CapabilityPolicy policy)
    {
        _inspector =
            inspector
            ?? throw new ArgumentNullException(nameof(inspector));

        _activity =
            activity
            ?? throw new ArgumentNullException(nameof(activity));

        _policy =
            policy
            ?? throw new ArgumentNullException(nameof(policy));
    }

    internal void SetInitialDirectory(
        string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        if (!Directory.Exists(directory))
            return;

        _selectedDirectory =
            Path.GetFullPath(directory);

        RepositoryPathBox.Text =
            _selectedDirectory;
    }

    private void ChooseFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        var picker =
            new OpenFolderDialog
            {
                Title = "Choose local Git repository",
                InitialDirectory =
                    Directory.Exists(_selectedDirectory)
                        ? _selectedDirectory
                        : Environment.GetFolderPath(
                            Environment.SpecialFolder.MyDocuments)
            };

        if (picker.ShowDialog() != true)
            return;

        _selectedDirectory =
            Path.GetFullPath(
                picker.FolderName);

        RepositoryPathBox.Text =
            _selectedDirectory;

        ClearSnapshot();
    }

    private async void Inspect_Click(
        object sender,
        RoutedEventArgs e)
    {
        await InspectSelectedAsync();
    }

    private async Task InspectSelectedAsync()
    {
        if (_inspector is null
            || _activity is null
            || _policy is null)
        {
            RepositoryErrorText.Text =
                "Repository inspection dependencies are not configured.";

            return;
        }

        if (!_policy.IsAllowed(
                Capability.LocalRepositoryInspection))
        {
            RepositoryErrorText.Text =
                "Local repository inspection is not permitted by policy.";

            _activity.Record(
                category: "security",
                action: "repository.inspect",
                target: _selectedDirectory,
                result: "DENIED");

            return;
        }

        if (!Directory.Exists(
                _selectedDirectory))
        {
            RepositoryErrorText.Text =
                "Selected folder does not exist.";

            return;
        }

        _inspectionCancellation?.Cancel();
        _inspectionCancellation?.Dispose();

        _inspectionCancellation =
            new CancellationTokenSource();

        InspectButton.IsEnabled = false;
        RepositoryErrorText.Text = "Inspecting...";

        try
        {
            var snapshot =
                await _inspector.InspectAsync(
                    _selectedDirectory,
                    _inspectionCancellation.Token);

            ApplySnapshot(snapshot);

            _activity.Record(
                category: "repository",
                action: "repository.inspect",
                target:
                    snapshot.RootPath
                    ?? snapshot.RequestedPath,
                result:
                    snapshot.IsRepository
                        ? "INSPECTED"
                        : "NOT_REPOSITORY",
                details: new
                {
                    snapshot.IsRepository,
                    snapshot.Branch,
                    snapshot.HeadSha,
                    snapshot.IsClean,
                    changeCount = snapshot.Changes.Count,
                    commitCount = snapshot.RecentCommits.Count,
                    networkAttempted = false,
                    writeAttempted = false,
                    aiUsed = false
                });
        }
        catch (OperationCanceledException)
        {
            RepositoryErrorText.Text =
                "Inspection cancelled.";
        }
        catch (Exception ex)
        {
            RepositoryErrorText.Text =
                "Inspection failed: " + ex.Message;

            _activity.Record(
                category: "repository",
                action: "repository.inspect",
                target: _selectedDirectory,
                result: "FAILED",
                details: new
                {
                    error = ex.Message,
                    networkAttempted = false,
                    writeAttempted = false,
                    aiUsed = false
                });
        }
        finally
        {
            InspectButton.IsEnabled = true;
        }
    }

    private void ApplySnapshot(
        RepositorySnapshot snapshot)
    {
        if (!snapshot.IsRepository)
        {
            ClearSnapshot();

            RepositoryErrorText.Text =
                snapshot.Message
                ?? "The selected folder is not a Git repository.";

            return;
        }

        RepositoryRootText.Text =
            snapshot.RootPath ?? "";

        RepositoryBranchText.Text =
            snapshot.Branch ?? "";

        RepositoryHeadText.Text =
            snapshot.HeadSha ?? "";

        RepositoryStatusText.Text =
            snapshot.IsClean
                ? "Clean"
                : $"Dirty · {snapshot.Changes.Count} change(s)";

        ChangesGrid.ItemsSource =
            snapshot.Changes;

        CommitsGrid.ItemsSource =
            snapshot.RecentCommits;

        ChangesHeaderText.Text =
            $"CHANGED FILES · {snapshot.Changes.Count}";

        CommitsHeaderText.Text =
            $"RECENT COMMITS · {snapshot.RecentCommits.Count}";

        RepositoryErrorText.Text =
            string.Empty;
    }

    private void ClearSnapshot()
    {
        RepositoryRootText.Text = "";
        RepositoryBranchText.Text = "";
        RepositoryHeadText.Text = "";
        RepositoryStatusText.Text = "";

        ChangesGrid.ItemsSource = null;
        CommitsGrid.ItemsSource = null;

        ChangesHeaderText.Text =
            "CHANGED FILES";

        CommitsHeaderText.Text =
            "RECENT COMMITS";

        RepositoryErrorText.Text =
            string.Empty;
    }
}
