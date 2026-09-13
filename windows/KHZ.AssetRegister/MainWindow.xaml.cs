using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Win32;

namespace KHZ.AssetRegister;

public partial class MainWindow : Window
{
    private readonly AssetStore _store = new();
    private readonly ObservableCollection<AssetRecord> _assets = new();
    private readonly ICollectionView _assetsView;
    private bool _settingTheme;

    public MainWindow()
    {
        InitializeComponent();

        DatabasePathText.Text = _store.DatabasePath;

        _assetsView = CollectionViewSource.GetDefaultView(_assets);
        _assetsView.Filter = FilterAsset;
        AssetsGrid.ItemsSource = _assetsView;

        SetThemeSelection(ThemeManager.CurrentTheme);
        LoadAssets();
    }

    private void LoadAssets()
    {
        foreach (var asset in _assets)
            asset.PropertyChanged -= Asset_PropertyChanged;

        _assets.Clear();

        foreach (var asset in _store.LoadAssets())
        {
            asset.PropertyChanged += Asset_PropertyChanged;
            _assets.Add(asset);
        }

        _assetsView.Refresh();
        SetStatus("Loaded from local SQLite");
        UpdateCounts();
    }

    private void NewRow_Click(object sender, RoutedEventArgs e)
    {
        var asset = new AssetRecord
        {
            AssetTag = GenerateAssetTag(),
            Status = "In Service",
            Condition = "Good"
        };

        asset.PropertyChanged += Asset_PropertyChanged;
        _assets.Insert(0, asset);
        _assetsView.Refresh();

        AssetsGrid.SelectedItem = asset;
        AssetsGrid.ScrollIntoView(asset);
        AssetsGrid.Focus();

        SetStatus("New unsaved asset row added");
        UpdateCounts();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();

        try
        {
            _store.SaveChanges(_assets);
            LoadAssets();
            SetStatus("Changes saved");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();

        var selected = AssetsGrid.SelectedItems
            .OfType<AssetRecord>()
            .Distinct()
            .ToArray();

        if (selected.Length == 0)
        {
            SetStatus("Select one or more asset rows first");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Delete {selected.Length} selected asset row(s)?",
            "Delete assets",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            foreach (var asset in selected)
            {
                if (asset.AssetId > 0)
                    _store.Delete(asset.AssetId);

                asset.PropertyChanged -= Asset_PropertyChanged;
                _assets.Remove(asset);
            }

            _assetsView.Refresh();
            SetStatus($"Deleted {selected.Length} row(s)");
            UpdateCounts();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Delete failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            LoadAssets();
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges())
        {
            var answer = MessageBox.Show(
                this,
                "Discard unsaved grid changes and reload from SQLite?",
                "Reload",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        LoadAssets();
    }

    private void OpenExcel_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();

        if (HasUnsavedChanges())
        {
            var answer = MessageBox.Show(
                this,
                "Save current unsaved changes before opening an Excel file?",
                "Excel import",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                SetStatus("Excel import cancelled because unsaved changes exist");
                return;
            }

            try
            {
                _store.SaveChanges(_assets);
                LoadAssets();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Save before import failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }

        var picker = new OpenFileDialog
        {
            Title = "Open Excel workbook",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            CheckFileExists = true,
            Multiselect = false
        };

        if (picker.ShowDialog(this) != true)
            return;

        try
        {
            var workbook = ExcelWorkbookReader.Read(picker.FileName);

            var preview = new ExcelImportWindow(
                picker.FileName,
                workbook.SheetName,
                workbook.Rows)
            {
                Owner = this
            };

            if (preview.ShowDialog() != true)
            {
                SetStatus("Excel preview closed without importing");
                return;
            }

            var imported = preview.ImportedAssets.ToArray();
            if (imported.Length == 0)
            {
                SetStatus("No Excel rows selected for import");
                return;
            }

            ValidateImportAssetTags(imported);
            _store.SaveChanges(imported);
            LoadAssets();

            SetStatus(
                $"Imported {imported.Length:N0} row(s) from {Path.GetFileName(picker.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Excel import failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            SetStatus("Excel import failed");
        }
    }

    private void ThemeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_settingTheme || ThemeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var theme = Convert.ToString(item.Content, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(theme))
            return;

        ThemeManager.Apply(theme);
        SetStatus($"Theme changed to {theme}");
    }

    private void SetThemeSelection(string theme)
    {
        _settingTheme = true;

        try
        {
            var item = ThemeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(
                    Convert.ToString(x.Content, CultureInfo.InvariantCulture),
                    theme,
                    StringComparison.OrdinalIgnoreCase));

            ThemeComboBox.SelectedItem = item ?? ThemeComboBox.Items[0];
        }
        finally
        {
            _settingTheme = false;
        }
    }

    private void ValidateImportAssetTags(IReadOnlyList<AssetRecord> imported)
    {
        var duplicateInWorkbook = imported
            .GroupBy(x => x.AssetTag.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(10)
            .ToArray();

        if (duplicateInWorkbook.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate AssetTag values exist in the selected Excel rows:\n" +
                string.Join("\n", duplicateInWorkbook));
        }

        var existing = _store.LoadAssets()
            .Select(x => x.AssetTag.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var collisions = imported
            .Select(x => x.AssetTag.Trim())
            .Where(existing.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        if (collisions.Length > 0)
        {
            throw new InvalidOperationException(
                "These AssetTag values already exist in the local database:\n" +
                string.Join("\n", collisions));
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _assetsView.Refresh();
        UpdateCounts();
    }

    private bool FilterAsset(object item)
    {
        if (item is not AssetRecord asset)
            return false;

        var query = SearchBox.Text.Trim();

        if (query.Length == 0)
            return true;

        return Contains(asset.AssetTag, query)
            || Contains(asset.SerialNumber, query)
            || Contains(asset.Barcode, query)
            || Contains(asset.Category, query)
            || Contains(asset.Description, query)
            || Contains(asset.Manufacturer, query)
            || Contains(asset.Model, query)
            || Contains(asset.Location, query)
            || Contains(asset.Department, query)
            || Contains(asset.Custodian, query)
            || Contains(asset.Status, query)
            || Contains(asset.Condition, query)
            || Contains(asset.Notes, query);
    }

    private static bool Contains(string value, string query)
        => value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export KHZ Asset Register as CSV",
            Filter = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"KHZ-Assets-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var rows = GetVisibleAssets();

        using var writer = new StreamWriter(
            dialog.FileName,
            false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine(string.Join(',', ExportHeaders.Select(CsvEscape)));

        foreach (var asset in rows)
        {
            writer.WriteLine(string.Join(',', ExportValues(asset).Select(CsvEscape)));
        }

        SetStatus($"CSV exported: {dialog.FileName}");
    }

    private void ExportXlsx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export KHZ Asset Register as XLSX",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"KHZ-Assets-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var rows = GetVisibleAssets();

        using var document = SpreadsheetDocument.Create(
            dialog.FileName,
            SpreadsheetDocumentType.Workbook);

        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(sheetData);

        var headerRow = new Row();
        foreach (var header in ExportHeaders)
            AppendTextCell(headerRow, header);
        sheetData.Append(headerRow);

        foreach (var asset in rows)
        {
            var row = new Row();
            foreach (var value in ExportValues(asset))
                AppendTextCell(row, value);
            sheetData.Append(row);
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Assets"
        });

        workbookPart.Workbook.Save();
        SetStatus($"XLSX exported: {dialog.FileName}");
    }

    private void AuditLog_Click(object sender, RoutedEventArgs e)
    {
        var window = new AuditWindow(_store)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backup = _store.CreateBackup();
            SetStatus($"Database backup created: {backup}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Backup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Integrity_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var integrity = _store.IntegrityCheck();
            var assets = _store.CountAssets();
            var audits = _store.CountAuditRows();
            var size = File.Exists(_store.DatabasePath)
                ? new FileInfo(_store.DatabasePath).Length
                : 0;

            MessageBox.Show(
                this,
                $"Integrity: {integrity}\nAssets: {assets:N0}\nAudit rows: {audits:N0}\nDatabase size: {size:N0} bytes\n\n{_store.DatabasePath}",
                "SQLite database check",
                MessageBoxButton.OK,
                integrity.Equals("ok", StringComparison.OrdinalIgnoreCase)
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);

            SetStatus($"Database integrity: {integrity}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Database check failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_store.DatabaseDirectory}\"",
            UseShellExecute = true
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitGridEdit();

        if (!HasUnsavedChanges())
        {
            base.OnClosing(e);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Save unsaved asset changes before closing?",
            "KHZ Asset Register",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                _store.SaveChanges(_assets);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Save failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }

    private void Asset_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetRecord.IsDirty))
            UpdateCounts();
    }

    private void UpdateCounts()
    {
        var visible = _assetsView.Cast<AssetRecord>().Count();
        var dirty = _assets.Count(x => x.IsDirty || x.AssetId <= 0);

        CountText.Text = $"{visible:N0} visible / {_assets.Count:N0} total";
        DirtyText.Text = dirty == 0 ? "SAVED" : $"UNSAVED: {dirty:N0}";
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        UpdateCounts();
    }

    private bool HasUnsavedChanges()
        => _assets.Any(x => x.IsDirty || x.AssetId <= 0);

    private void CommitGridEdit()
    {
        AssetsGrid.CommitEdit(
            DataGridEditingUnit.Cell,
            exitEditingMode: true);

        AssetsGrid.CommitEdit(
            DataGridEditingUnit.Row,
            exitEditingMode: true);
    }

    private AssetRecord[] GetVisibleAssets()
        => _assetsView.Cast<AssetRecord>().ToArray();

    private static string GenerateAssetTag()
        => $"KHZ-{DateTime.Now:yyyyMMdd-HHmmssfff}";

    private static string CsvEscape(string value)
    {
        if (!value.Contains(',')
            && !value.Contains('"')
            && !value.Contains('\r')
            && !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static void AppendTextCell(Row row, string value)
    {
        row.Append(new Cell
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty))
        });
    }

    private static readonly string[] ExportHeaders =
    [
        "AssetId",
        "AssetTag",
        "SerialNumber",
        "Barcode",
        "Category",
        "Description",
        "Manufacturer",
        "Model",
        "Location",
        "Department",
        "Custodian",
        "Status",
        "PurchaseDate",
        "PurchaseCost",
        "WarrantyExpiry",
        "Condition",
        "Notes",
        "CreatedAt"
    ];

    private static string[] ExportValues(AssetRecord asset)
    =>
    [
        asset.AssetId.ToString(CultureInfo.InvariantCulture),
        asset.AssetTag,
        asset.SerialNumber,
        asset.Barcode,
        asset.Category,
        asset.Description,
        asset.Manufacturer,
        asset.Model,
        asset.Location,
        asset.Department,
        asset.Custodian,
        asset.Status,
        asset.PurchaseDate,
        asset.PurchaseCost.ToString(CultureInfo.InvariantCulture),
        asset.WarrantyExpiry,
        asset.Condition,
        asset.Notes,
        asset.CreatedAt
    ];
}
