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
        AddChatCard();
    }

    private void AddChatCard()
    {
        if (Content is not StackPanel root)
            return;

        WrapPanel? workspacePanel = null;
        foreach (var child in root.Children)
        {
            if (child is WrapPanel panel)
            {
                workspacePanel = panel;
                break;
            }
        }

        if (workspacePanel is null)
            return;

        var title = new TextBlock
        {
            Text = "Chat",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.Black
        };

        var subtitle = new TextBlock
        {
            Text = "Local model with workspace tools",
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(112, 112, 112))
        };
        Grid.SetRow(subtitle, 1);

        var grid = new Grid
        {
            Margin = new Thickness(16, 12, 16, 12)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(title);
        grid.Children.Add(subtitle);

        var button = new Button
        {
            Tag = "chat",
            Content = grid
        };
        button.SetResourceReference(
            FrameworkElement.StyleProperty,
            "ComposerCard");
        button.Click += Navigate_Click;

        workspacePanel.Children.Insert(0, button);
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
