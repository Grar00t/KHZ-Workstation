using KHZ.App.Views;
using System;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App;

public partial class MainWindow
{
    private bool _workspaceComposerInitialized;

    protected override void OnContentRendered(
        EventArgs e)
    {
        base.OnContentRendered(e);

        if (_workspaceComposerInitialized)
            return;

        _workspaceComposerInitialized = true;

        var composer =
            new WorkspaceComposerView();

        composer.NavigateRequested +=
            WorkspaceComposer_NavigateRequested;

        HomeSurface.Content =
            composer;

        RenameHomeNavigation();

        HomeButton.Click +=
            HomeButton_WorkspaceComposerClick;

        if (HomeSurface.Visibility
            == Visibility.Visible)
        {
            SectionTitle.Text =
                "Workspace";
        }

        _activity.Record(
            category: "navigation",
            action: "workspace-composer.open",
            target: "workspace-composer",
            result: "OPENED",
            details: new
            {
                version = 1,
                duplicatedState = false,
                networkAttempted = false,
                aiUsed = false
            });
    }

    private void RenameHomeNavigation()
    {
        if (HomeButton.Content
            is not StackPanel panel)
        {
            return;
        }

        foreach (var child in panel.Children)
        {
            if (child is not TextBlock text
                || !string.Equals(
                    text.Text,
                    "Home",
                    StringComparison.Ordinal))
            {
                continue;
            }

            text.Text =
                "Workspace";

            break;
        }
    }

    private void HomeButton_WorkspaceComposerClick(
        object sender,
        RoutedEventArgs e)
    {
        SectionTitle.Text =
            "Workspace";
    }

    private void WorkspaceComposer_NavigateRequested(
        object? sender,
        WorkspaceComposerNavigationEventArgs e)
    {
        var routed =
            new RoutedEventArgs();

        switch (e.Destination)
        {
            case "files":
                Files_Click(this, routed);
                break;

            case "tasks":
                Tasks_Click(this, routed);
                break;

            case "structured-data":
                StructuredData_Click(this, routed);
                break;

            case "search":
                Search_Click(this, routed);
                break;

            case "repositories":
                Repositories_Click(this, routed);
                break;

            case "terminal":
                Terminal_Click(this, routed);
                break;

            case "backup":
                BackupRestore_Click(this, routed);
                break;

            case "documents":
                Documents_Click(this, routed);
                break;

            case "sheets":
                Sheets_Click(this, routed);
                break;

            case "slides":
                Slides_Click(this, routed);
                break;

            case "pdf":
                Pdf_Click(this, routed);
                break;

            case "activity":
                Activity_Click(this, routed);
                break;

            case "security":
                Security_Click(this, routed);
                break;

            case "integrations":
                Integrations_Click(this, routed);
                break;

            case "settings":
                Settings_Click(this, routed);
                break;

            default:
                _activity.Record(
                    category: "navigation",
                    action: "workspace-composer.navigate",
                    target: e.Destination,
                    result: "DENIED",
                    details: new
                    {
                        reason = "unknown_destination"
                    });
                break;
        }
    }
}
