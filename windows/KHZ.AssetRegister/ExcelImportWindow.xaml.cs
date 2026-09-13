using System.Collections.ObjectModel;
using System.Windows;

namespace KHZ.AssetRegister;

public partial class ExcelImportWindow : Window
{
    private readonly ObservableCollection<ExcelPreviewRow> _rows;

    public ExcelImportWindow(
        string filePath,
        string sheetName,
        IEnumerable<ExcelPreviewRow> rows)
    {
        InitializeComponent();

        _rows = new ObservableCollection<ExcelPreviewRow>(rows);
        PreviewGrid.ItemsSource = _rows;

        SourceText.Text = $"{filePath}  |  Sheet: {sheetName}";
        UpdateCount();
    }

    public IReadOnlyList<AssetRecord> ImportedAssets { get; private set; }
        = Array.Empty<AssetRecord>();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsSelected = true;

        PreviewGrid.Items.Refresh();
        UpdateCount();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsSelected = false;

        PreviewGrid.Items.Refresh();
        UpdateCount();
    }

    private void ImportSelected_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();

        var selected = _rows
            .Where(x => x.IsSelected)
            .Select(x => x.ToAssetRecord())
            .ToArray();

        if (selected.Length == 0)
        {
            MessageBox.Show(
                this,
                "Select at least one row to import.",
                "Excel import",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ImportedAssets = selected;
        DialogResult = true;
        Close();
    }

    private void ImportAll_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();

        ImportedAssets = _rows
            .Select(x => x.ToAssetRecord())
            .ToArray();

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CommitGridEdit()
    {
        PreviewGrid.CommitEdit(
            System.Windows.Controls.DataGridEditingUnit.Cell,
            exitEditingMode: true);

        PreviewGrid.CommitEdit(
            System.Windows.Controls.DataGridEditingUnit.Row,
            exitEditingMode: true);

        UpdateCount();
    }

    private void UpdateCount()
    {
        var selected = _rows.Count(x => x.IsSelected);
        CountText.Text = $"{_rows.Count:N0} row(s) detected · {selected:N0} selected for import";
    }
}
