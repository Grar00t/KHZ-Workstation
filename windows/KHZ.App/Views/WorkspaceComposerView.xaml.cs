using System;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class WorkspaceComposerView : UserControl
{
    internal event EventHandler<WorkspaceComposerNavigationEventArgs>? NavigateRequested;

    public WorkspaceComposerView()
    {
        InitializeComponent();
    }

    private void Navigate_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.Tag is not string destination
            || string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        NavigateRequested?.Invoke(
            this,
            new WorkspaceComposerNavigationEventArgs(destination));
    }
}

internal sealed class WorkspaceComposerNavigationEventArgs : EventArgs
{
    internal WorkspaceComposerNavigationEventArgs(
        string destination)
    {
        Destination = destination;
    }

    internal string Destination { get; }
}
