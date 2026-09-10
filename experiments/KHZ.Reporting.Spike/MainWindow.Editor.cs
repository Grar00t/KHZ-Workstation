using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using TextBox = System.Windows.Controls.TextBox;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;

namespace KHZ.Reporting.Spike;

public partial class MainWindow
{
    private readonly ObservableCollection<EditableAssetRow> _editorRows = new();
    private bool _editorInitialized;

    private void AssetGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_editorInitialized)
        {
            AssetGrid.ItemsSource = _editorRows;
            _editorInitialized = true;
        }

        LoadEditorRows();
        ApplyEditorVisualStyle();
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabs))
            return;

        if (EditTab.IsSelected && _editorInitialized)
            LoadEditorRows();
    }

    private void LoadEditorRows()
    {
        if (!_editorInitialized)
            return;

        _editorRows.Clear();

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_no, tag, description, location FROM assets ORDER BY row_no;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _editorRows.Add(new EditableAssetRow
            {
                RowNo = reader.GetInt64(0),
                Tag = reader.GetString(1),
                Description = reader.GetString(2),
                Location = reader.GetString(3)
            });
        }
    }

    private void AddEditorRow_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();

        var next = _editorRows.Count == 0 ? 1 : _editorRows.Max(row => row.RowNo) + 1;
        var row = new EditableAssetRow { RowNo = next };
        _editorRows.Add(row);
        AssetGrid.SelectedItem = row;
        AssetGrid.ScrollIntoView(row);
        AssetGrid.CurrentCell = new DataGridCellInfo(row, AssetGrid.Columns[1]);
        AssetGrid.BeginEdit();
        Keyboard.Focus(AssetGrid);
    }

    private void DeleteEditorRows_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();

        var selected = AssetGrid.SelectedItems
            .OfType<EditableAssetRow>()
            .ToArray();

        foreach (var row in selected)
            _editorRows.Remove(row);

        StatusText.Text = selected.Length == 0
            ? "No rows selected."
            : $"Removed {selected.Length:N0} row(s) from the editor. Click Save data to commit.";
    }

    private async void SaveEditorData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveEditorRows();
            await LoadReportAsync();
            StatusText.Text = $"Saved {_editorRows.Count:N0} row(s) to local SQLite.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void SaveAndPreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveEditorRows();
            await LoadReportAsync();
            PreviewTab.IsSelected = true;
            StatusText.Text = $"Saved {_editorRows.Count:N0} row(s) and refreshed preview.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void SaveEditorRows()
    {
        CommitGridEdits();

        var rows = _editorRows
            .Where(row => row.RowNo != 0
                || !string.IsNullOrWhiteSpace(row.Tag)
                || !string.IsNullOrWhiteSpace(row.Description)
                || !string.IsNullOrWhiteSpace(row.Location))
            .ToArray();

        var seen = new HashSet<long>();
        foreach (var row in rows)
        {
            if (row.RowNo <= 0)
                throw new InvalidDataException("NO. must be a positive integer.");
            if (!seen.Add(row.RowNo))
                throw new InvalidDataException($"Duplicate NO.: {row.RowNo}");
            if (string.IsNullOrWhiteSpace(row.Tag))
                throw new InvalidDataException($"TAG NUMBER is required on row {row.RowNo}.");
            if (string.IsNullOrWhiteSpace(row.Description))
                throw new InvalidDataException($"ASSET DESCRIPTION is required on row {row.RowNo}.");
            if (string.IsNullOrWhiteSpace(row.Location))
                throw new InvalidDataException($"M/LOC is required on row {row.RowNo}.");
        }

        ReplaceAssets(rows.Select(row => new AssetRow(
            row.RowNo,
            row.Tag.Trim(),
            row.Description.Trim(),
            row.Location.Trim())).ToArray());
    }

    private void CommitGridEdits()
    {
        AssetGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        AssetGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void ApplyPresetEditor_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreset_Click(sender, e);
        ApplyEditorVisualStyle();
    }

    private void ApplyStyleEditor_Click(object sender, RoutedEventArgs e)
    {
        ApplyEditorVisualStyle();
        ApplyStyle_Click(sender, e);
    }

    private void EditorAlignLeft_Click(object sender, RoutedEventArgs e)
    {
        _alignment = "Left";
        ApplyEditorVisualStyle();
    }

    private void EditorAlignCenter_Click(object sender, RoutedEventArgs e)
    {
        _alignment = "Center";
        ApplyEditorVisualStyle();
    }

    private void EditorAlignRight_Click(object sender, RoutedEventArgs e)
    {
        _alignment = "Right";
        ApplyEditorVisualStyle();
    }

    private void PickHeaderColorEditor_Click(object sender, RoutedEventArgs e)
    {
        PickHeaderColor_Click(sender, e);
        ApplyEditorVisualStyle();
    }

    private void EditorStyleControl_Changed(object sender, SelectionChangedEventArgs e)
        => ApplyEditorVisualStyle();

    private void EditorStyleToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyEditorVisualStyle();

    private void EditorStyleField_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ApplyEditorVisualStyle();

    private void ApplyEditorVisualStyle()
    {
        if (!_editorInitialized || FontFamilyBox is null || FontSizeBox is null || FormatTargetBox is null)
            return;

        var fontName = FontFamilyBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(fontName))
            fontName = FontFamilyBox.Text;
        if (string.IsNullOrWhiteSpace(fontName))
            fontName = "Segoe UI";

        if (!double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)
            || fontSize < 6
            || fontSize > 96)
        {
            return;
        }

        var family = new FontFamily(fontName);
        var weight = BoldToggle.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        var style = ItalicToggle.IsChecked == true ? FontStyles.Italic : FontStyles.Normal;
        var alignment = _alignment switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };

        var target = SelectedComboText(FormatTargetBox);
        switch (target)
        {
            case "Company":
                ApplyTextBoxStyle(CompanyBox, family, fontSize, weight, style, alignment);
                break;

            case "Table":
                AssetGrid.FontFamily = family;
                AssetGrid.FontSize = fontSize;
                AssetGrid.FontWeight = weight;
                AssetGrid.FontStyle = style;
                AssetGrid.HorizontalContentAlignment = alignment switch
                {
                    TextAlignment.Left => HorizontalAlignment.Left,
                    TextAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center
                };

                var cellStyle = new Style(typeof(DataGridCell));
                cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
                    alignment switch
                    {
                        TextAlignment.Left => HorizontalAlignment.Left,
                        TextAlignment.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Center
                    }));
                AssetGrid.CellStyle = cellStyle;

                var headerStyle = new Style(typeof(DataGridColumnHeader));
                headerStyle.Setters.Add(new Setter(Control.FontFamilyProperty, family));
                headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, fontSize));
                headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, weight));
                headerStyle.Setters.Add(new Setter(Control.FontStyleProperty, style));
                headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
                headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
                headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_headerColor))));
                AssetGrid.ColumnHeaderStyle = headerStyle;
                break;

            default:
                ApplyTextBoxStyle(TitleBox, family, fontSize, weight, style, alignment);
                break;
        }
    }

    private static void ApplyTextBoxStyle(
        TextBox textBox,
        FontFamily family,
        double size,
        FontWeight weight,
        FontStyle style,
        TextAlignment alignment)
    {
        textBox.FontFamily = family;
        textBox.FontSize = size;
        textBox.FontWeight = weight;
        textBox.FontStyle = style;
        textBox.TextAlignment = alignment;
    }

    private sealed class EditableAssetRow
    {
        public long RowNo { get; set; }
        public string Tag { get; set; } = "";
        public string Description { get; set; } = "";
        public string Location { get; set; } = "";
    }
}
