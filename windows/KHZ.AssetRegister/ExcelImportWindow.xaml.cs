using System.Collections.ObjectModel;
using System.ComponentModel;
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

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        PreviewGrid.ItemsSource = _rows;

        SourceText.Text = $"{filePath}  |  Sheet: {sheetName}";
        UpdateCount();
    }

    public IReadOnlyList<AssetRecord> ImportedAssets { get; private set; }
        = Array.Empty<AssetRecord>();

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExcelPreviewRow.IsSelected))
            UpdateCount();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsSelected = true;

        UpdateCount();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsSelected = false;

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
    }

    private void ImportAll_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();

        if (_rows.Count == 0)
        {
            MessageBox.Show(
                this,
                "No rows are available to import.",
                "Excel import",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ImportedAssets = _rows
            .Select(x => x.ToAssetRecord())
            .ToArray();

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
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

    protected override void OnClosed(EventArgs e)
    {
        foreach (var row in _rows)
            row.PropertyChanged -= Row_PropertyChanged;

        base.OnClosed(e);
    }
}
