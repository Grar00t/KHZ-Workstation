using KHZ.App.StructuredData;
using KHZ.App.Trust;
using KHZ.App.Workspaces;
using Microsoft.Win32;
using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class StructuredDataView : UserControl
{
    private const int PreviewLimit = 500;

    private IActivityStore? _activity;

    private WorkspaceContext? _workspace;

    private IWorkspaceDataStore? _store;

    private CsvStructuredDataService? _csv;

    private XlsxStructuredDataService? _xlsx;

    public StructuredDataView()
    {
        InitializeComponent();
    }

    internal void Configure(
        IActivityStore activity)
    {
        _activity =
            activity
            ?? throw new ArgumentNullException(
                nameof(activity));
    }

    internal void SetWorkspace(
        WorkspaceContext? context)
    {
        _workspace =
            context;

        TablesGrid.ItemsSource = null;
        TablesGrid.SelectedItem = null;

        ClearPreview();

        if (context is null)
        {
            _store = null;
            _csv = null;
            _xlsx = null;

            WorkspaceStatusText.Text =
                "Folder mode · activate a workspace to use Structured Data";

            ErrorText.Text =
                string.Empty;

            UpdateCommandState();

            return;
        }

        try
        {
            var store =
                new SqliteWorkspaceDataStore(
                    context);

            _store =
                store;

            _csv =
                new CsvStructuredDataService(
                    store);

            _xlsx =
                new XlsxStructuredDataService(
                    store);

            WorkspaceStatusText.Text =
                $"Workspace · {context.Info.Name}";

            ErrorText.Text =
                string.Empty;

            RefreshTables();
        }
        catch (Exception ex)
        {
            _store = null;
            _csv = null;
            _xlsx = null;

            WorkspaceStatusText.Text =
                $"Workspace unavailable · {context.Info.Name}";

            ErrorText.Text =
                "Structured Data initialization failed: "
                + ex.Message;

            UpdateCommandState();
        }
    }

    internal void RefreshData()
    {
        if (_store is null)
        {
            UpdateCommandState();
            return;
        }

        RefreshTables();
    }

    private void Refresh_Click(
        object sender,
        RoutedEventArgs e)
        => RefreshTables();

    private void TablesGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateCommandState();

        if (TablesGrid.SelectedItem
            is not TableRow selected)
        {
            ClearPreview();
            return;
        }

        LoadPreview(
            selected);
    }

    private void ImportCsv_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_csv is null)
            return;

        var dialog =
            new OpenFileDialog
            {
                Title =
                    "Import CSV into Structured Data",

                Filter =
                    "CSV files (*.csv)|*.csv",

                CheckFileExists =
                    true,

                Multiselect =
                    false
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var tableId =
                _csv.ImportCsv(
                    dialog.FileName);

            _activity?.Record(
                category: "structured-data",
                action: "structured-data.import",
                target: tableId,
                result: "IMPORTED",
                details: new
                {
                    format = "csv",
                    workspaceId =
                        _workspace?.Info.WorkspaceId,
                    pathCaptured = false,
                    networkAttempted = false,
                    aiUsed = false
                });

            RefreshTables(
                tableId);

            ErrorText.Text =
                string.Empty;
        }
        catch (Exception ex)
        {
            RecordFailure(
                action:
                    "structured-data.import",
                format:
                    "csv",
                ex);

            ErrorText.Text =
                "CSV import failed: "
                + ex.Message;
        }
    }

    private void ImportXlsx_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_xlsx is null)
            return;

        var dialog =
            new OpenFileDialog
            {
                Title =
                    "Import XLSX into Structured Data",

                Filter =
                    "Excel workbook (*.xlsx)|*.xlsx",

                CheckFileExists =
                    true,

                Multiselect =
                    false
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var tableId =
                _xlsx.ImportXlsx(
                    dialog.FileName);

            _activity?.Record(
                category: "structured-data",
                action: "structured-data.import",
                target: tableId,
                result: "IMPORTED",
                details: new
                {
                    format = "xlsx",
                    workspaceId =
                        _workspace?.Info.WorkspaceId,
                    pathCaptured = false,
                    networkAttempted = false,
                    aiUsed = false
                });

            RefreshTables(
                tableId);

            ErrorText.Text =
                string.Empty;
        }
        catch (Exception ex)
        {
            RecordFailure(
                action:
                    "structured-data.import",
                format:
                    "xlsx",
                ex);

            ErrorText.Text =
                "XLSX import failed: "
                + ex.Message;
        }
    }

    private void ExportCsv_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_csv is null)
            return;

        if (TablesGrid.SelectedItem
            is not TableRow selected)
        {
            ErrorText.Text =
                "Select a table first.";

            return;
        }

        var dialog =
            new SaveFileDialog
            {
                Title =
                    "Export Structured Data as CSV",

                Filter =
                    "CSV files (*.csv)|*.csv",

                DefaultExt =
                    ".csv",

                AddExtension =
                    true,

                OverwritePrompt =
                    true,

                FileName =
                    selected.Name
                    + ".csv"
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _csv.ExportCsv(
                selected.TableId,
                dialog.FileName);

            RecordExport(
                selected,
                format:
                    "csv");

            ErrorText.Text =
                string.Empty;
        }
        catch (Exception ex)
        {
            RecordFailure(
                action:
                    "structured-data.export",
                format:
                    "csv",
                ex,
                selected.TableId);

            ErrorText.Text =
                "CSV export failed: "
                + ex.Message;
        }
    }

    private void ExportXlsx_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_xlsx is null)
            return;

        if (TablesGrid.SelectedItem
            is not TableRow selected)
        {
            ErrorText.Text =
                "Select a table first.";

            return;
        }

        var dialog =
            new SaveFileDialog
            {
                Title =
                    "Export Structured Data as XLSX",

                Filter =
                    "Excel workbook (*.xlsx)|*.xlsx",

                DefaultExt =
                    ".xlsx",

                AddExtension =
                    true,

                OverwritePrompt =
                    true,

                FileName =
                    selected.Name
                    + ".xlsx"
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _xlsx.ExportXlsx(
                selected.TableId,
                dialog.FileName);

            RecordExport(
                selected,
                format:
                    "xlsx");

            ErrorText.Text =
                string.Empty;
        }
        catch (Exception ex)
        {
            RecordFailure(
                action:
                    "structured-data.export",
                format:
                    "xlsx",
                ex,
                selected.TableId);

            ErrorText.Text =
                "XLSX export failed: "
                + ex.Message;
        }
    }

    private void RefreshTables(
        string? selectTableId = null)
    {
        if (_store is null)
        {
            TablesGrid.ItemsSource = null;
            TableCountText.Text = "0";
            ClearPreview();
            UpdateCommandState();
            return;
        }

        try
        {
            var tables =
                _store.ListTables();

            var rows =
                tables
                    .Select(
                        table =>
                            new TableRow(
                                TableId:
                                    table.TableId,

                                Name:
                                    table.Name,

                                RowCount:
                                    _store.CountRows(
                                        table.TableId),

                                ColumnCount:
                                    table.Columns.Count,

                                Created:
                                    FormatTimestamp(
                                        table.CreatedUtc)))
                    .ToList();

            TablesGrid.ItemsSource =
                rows;

            TableCountText.Text =
                rows.Count.ToString(
                    CultureInfo.InvariantCulture);

            TableRow? selection = null;

            if (!string.IsNullOrWhiteSpace(
                    selectTableId))
            {
                selection =
                    rows.FirstOrDefault(
                        row =>
                            string.Equals(
                                row.TableId,
                                selectTableId,
                                StringComparison.Ordinal));
            }

            selection ??=
                rows.FirstOrDefault();

            TablesGrid.SelectedItem =
                selection;

            if (selection is null)
                ClearPreview();

            ErrorText.Text =
                string.Empty;

            UpdateCommandState();
        }
        catch (Exception ex)
        {
            TablesGrid.ItemsSource = null;
            TableCountText.Text = "0";

            ClearPreview();

            ErrorText.Text =
                "Structured Data refresh failed: "
                + ex.Message;

            UpdateCommandState();
        }
    }

    private void LoadPreview(
        TableRow selected)
    {
        if (_store is null)
        {
            ClearPreview();
            return;
        }

        try
        {
            var result =
                _store.Query(
                    selected.TableId,
                    limit:
                        PreviewLimit);

            var table =
                new DataTable();

            foreach (var column
                     in result.Columns)
            {
                table.Columns.Add(
                    column,
                    typeof(object));
            }

            foreach (var sourceRow
                     in result.Rows)
            {
                var row =
                    table.NewRow();

                foreach (var column
                         in result.Columns)
                {
                    var value =
                        sourceRow[column];

                    row[column] =
                        value switch
                        {
                            null =>
                                DBNull.Value,

                            byte[] bytes =>
                                $"BLOB · {bytes.Length} bytes",

                            _ =>
                                value
                        };
                }

                table.Rows.Add(
                    row);
            }

            PreviewGrid.ItemsSource =
                table.DefaultView;

            PreviewTitleText.Text =
                selected.Name;

            PreviewCountText.Text =
                $"Showing {result.Rows.Count} of {selected.RowCount}";

            ErrorText.Text =
                string.Empty;
        }
        catch (Exception ex)
        {
            ClearPreview();

            ErrorText.Text =
                "Preview failed: "
                + ex.Message;
        }
    }

    private void ClearPreview()
    {
        PreviewGrid.ItemsSource = null;

        PreviewTitleText.Text =
            "PREVIEW";

        PreviewCountText.Text =
            string.Empty;
    }

    private void UpdateCommandState()
    {
        var workspaceReady =
            _store is not null;

        ImportCsvButton.IsEnabled =
            workspaceReady;

        ImportXlsxButton.IsEnabled =
            workspaceReady;

        RefreshButton.IsEnabled =
            workspaceReady;

        var selectionReady =
            workspaceReady
            && TablesGrid.SelectedItem
                is TableRow;

        ExportCsvButton.IsEnabled =
            selectionReady;

        ExportXlsxButton.IsEnabled =
            selectionReady;
    }

    private void RecordExport(
        TableRow selected,
        string format)
    {
        _activity?.Record(
            category: "structured-data",
            action: "structured-data.export",
            target: selected.TableId,
            result: "EXPORTED",
            details: new
            {
                format,
                rowCount =
                    selected.RowCount,
                workspaceId =
                    _workspace?.Info.WorkspaceId,
                pathCaptured = false,
                networkAttempted = false,
                aiUsed = false
            });
    }

    private void RecordFailure(
        string action,
        string format,
        Exception exception,
        string target = "structured-data")
    {
        _activity?.Record(
            category: "structured-data",
            action: action,
            target: target,
            result: "FAILED",
            details: new
            {
                format,
                workspaceId =
                    _workspace?.Info.WorkspaceId,
                errorType =
                    exception.GetType().Name,
                pathCaptured = false,
                networkAttempted = false,
                aiUsed = false
            });
    }

    private static string FormatTimestamp(
        string value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed
                .ToLocalTime()
                .ToString(
                    "yyyy-MM-dd hh:mm tt",
                    CultureInfo.InvariantCulture);
        }

        return value;
    }

    private sealed record TableRow(
        string TableId,
        string Name,
        long RowCount,
        int ColumnCount,
        string Created);
}
