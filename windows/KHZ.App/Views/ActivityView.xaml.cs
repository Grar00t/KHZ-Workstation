using KHZ.App.Trust;
using System;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class ActivityView : UserControl
{
    private IActivityReader? _reader;

    public ActivityView()
    {
        InitializeComponent();
    }

    internal void Configure(
        IActivityReader reader)
    {
        _reader =
            reader
            ?? throw new ArgumentNullException(
                nameof(reader));
    }

    internal void RefreshActivity()
    {
        if (_reader is null)
        {
            ActivityGrid.ItemsSource = null;

            ActivityCountText.Text =
                "Activity store not configured";

            ActivityErrorText.Text =
                "The activity reader has not been configured.";

            ActivityErrorText.Visibility =
                Visibility.Visible;

            return;
        }

        try
        {
            var rows =
                _reader.ReadRecent(250);

            ActivityGrid.ItemsSource =
                rows;

            ActivityCountText.Text =
                $"{rows.Count} recent local events";

            ActivityErrorText.Text = "";
            ActivityErrorText.Visibility =
                Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ActivityGrid.ItemsSource = null;

            ActivityCountText.Text =
                "Activity unavailable";

            ActivityErrorText.Text =
                ex.Message;

            ActivityErrorText.Visibility =
                Visibility.Visible;
        }
    }

    private void RefreshActivity_Click(
        object sender,
        RoutedEventArgs e)
        => RefreshActivity();
}
