using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KHZ.Tools.Safety;

namespace KHZ.App.Chat;

/// <summary>
/// Modal approval surface for a single agent action.
/// </summary>
/// <remarks>
/// Built in code rather than XAML deliberately: it keeps the approval path free
/// of markup compilation and resource-dictionary coupling, so the security
/// prompt cannot be broken by an unrelated theme change.
/// <para>
/// The prompt shows the exact payload the tool will act on. Approval that does
/// not show the concrete before/after content is not informed consent, which is
/// why the raw strings are displayed verbatim in a monospace, selectable box
/// rather than summarised.
/// </para>
/// </remarks>
internal sealed class AgentApprovalWindow : Window
{
    private AgentApprovalWindow(ConfirmationRequest request)
    {
        Title = "KHZ - approve agent action";
        Width = 720;
        MaxHeight = 720;
        SizeToContent = SizeToContentMode.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        FlowDirection = FlowDirection.LeftToRight;

        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.Children.Add(Header(request));
        layout.Children.Add(Facts(request));
        layout.Children.Add(Payload(request));
        layout.Children.Add(Buttons());

        Content = layout;
    }

    private UIElement Header(ConfirmationRequest request)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        stack.Children.Add(new TextBlock
        {
            Text = request.Title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new TextBlock
        {
            Text = request.Summary,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))
        });

        Grid.SetRow(stack, 0);
        return stack;
    }

    private UIElement Facts(ConfirmationRequest request)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        stack.Children.Add(Line("Tool", request.ToolName));
        stack.Children.Add(Line("Risk", request.Risk.ToString().ToUpperInvariant()));
        stack.Children.Add(Line("Target", request.Target));

        if (request.Warnings is { Length: > 0 })
        {
            var warnings = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 8, 0, 0),
                CornerRadius = new CornerRadius(4)
            };

            var list = new StackPanel();

            list.Children.Add(new TextBlock
            {
                Text = "Risk flags",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            foreach (var warning in request.Warnings)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "- " + warning,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            warnings.Child = list;
            stack.Children.Add(warnings);
        }

        Grid.SetRow(stack, 1);
        return stack;
    }

    private UIElement Payload(ConfirmationRequest request)
    {
        var columns = new Grid();

        if (request.Before is not null && request.After is not null)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            columns.ColumnDefinitions.Add(new ColumnDefinition());

            columns.Children.Add(Box("Current", request.Before, 0));
            columns.Children.Add(Box("Proposed", request.After, 2));
        }
        else if (request.After is not null)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.Children.Add(Box("Payload", request.After, 0));
        }

        Grid.SetRow(columns, 2);
        return columns;
    }

    private UIElement Buttons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var deny = new Button
        {
            Content = "Deny",
            MinWidth = 110,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };

        var approve = new Button
        {
            Content = "Approve once",
            MinWidth = 140,
            Padding = new Thickness(12, 6, 12, 6),
            IsDefault = false
        };

        // Deny is the default focus: the safe outcome must be the one a stray
        // Enter key produces.
        deny.Click += (_, _) => Close(false);
        approve.Click += (_, _) => Close(true);

        panel.Children.Add(deny);
        panel.Children.Add(approve);

        Loaded += (_, _) => deny.Focus();

        Grid.SetRow(panel, 3);
        return panel;
    }

    private static UIElement Line(string label, string value)
    {
        var text = new TextBlock { Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new System.Windows.Documents.Run(label + ": ") { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run(value));
        return text;
    }

    private static UIElement Box(string caption, string content, int column)
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = caption,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        stack.Children.Add(new TextBox
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7))
        });

        Grid.SetColumn(stack, column);
        return stack;
    }

    private void Close(bool approved)
    {
        DialogResult = approved;
        base.Close();
    }

    /// <summary>Shows the prompt on the UI thread and returns the decision.</summary>
    internal static bool Prompt(ConfirmationRequest request, Window? owner)
    {
        var window = new AgentApprovalWindow(request);

        if (owner is not null && owner.IsLoaded)
            window.Owner = owner;

        return window.ShowDialog() == true;
    }
}

/// <summary>
/// Bridges the transport-neutral confirmation contract to the WPF UI thread.
/// </summary>
/// <remarks>
/// This replaces the previous <c>MessageBox.Show</c> call made from inside an
/// async tool path. That pattern blocked the calling thread and could deadlock
/// or freeze the chat turn; here the prompt is marshalled with
/// <see cref="System.Windows.Threading.Dispatcher.InvokeAsync(System.Func{bool})"/>
/// and awaited, so the tool thread yields instead of blocking.
/// </remarks>
internal sealed class WpfConfirmationBroker : IConfirmationBroker
{
    private readonly Func<Window?> _ownerAccessor;

    internal WpfConfirmationBroker(Func<Window?> ownerAccessor)
        => _ownerAccessor = ownerAccessor;

    public async Task<bool> ConfirmAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        var application = Application.Current;

        if (application is null)
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        return await application.Dispatcher
            .InvokeAsync(() => AgentApprovalWindow.Prompt(request, _ownerAccessor()))
            .Task
            .ConfigureAwait(false);
    }
}
