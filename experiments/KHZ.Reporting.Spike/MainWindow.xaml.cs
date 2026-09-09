using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using LibRdlWpfViewer;
using Majorsilence.Reporting.Rdl;
using Microsoft.Data.Sqlite;

namespace KHZ.Reporting.Spike;

public partial class MainWindow : Window
{
    private const int MaximumImportRows = 5000;
    private const int MaximumImportColumns = 256;
    private const long MaximumImportBytes = 32L * 1024L * 1024L;

    private readonly RdlWpfViewer _viewer = new();
    private readonly string _templatePath;
    private readonly string _databasePath;

    public MainWindow()
    {
        InitializeComponent();

        ViewerHost.Content = _viewer;
        _templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "AssetRegister.rdl");

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "ReportingSpike");

        Directory.CreateDirectory(dataDir);
        _databasePath = Path.Combine(dataDir, "asset-register.db");

        Loaded += async (_, _) =>
        {
            try
            {
                EnsureSampleDatabase();
                await LoadReportAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        };
    }

    private void EnsureSampleDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS assets (
                    row_no      INTEGER PRIMARY KEY,
                    tag         TEXT NOT NULL,
                    description TEXT NOT NULL,
                    location    TEXT NOT NULL
                );
                """;
            schema.ExecuteNonQuery();
        }

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM assets;";
        var count = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count != 0)
            return;

        using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO assets(row_no, tag, description, location) VALUES
                (1, 'KU093973', 'DRAWER', 'COSHP-F...'),
                (2, 'KU093974', 'CABINET', 'COSHP-F...'),
                (3, 'KU093975', 'WORK BENCH', 'COSHP-G...'),
                (4, 'KU093976', 'STORAGE RACK', 'COSHP-G...');
            """;
        seed.ExecuteNonQuery();
    }

    private async Task LoadReportAsync()
    {
        if (!File.Exists(_templatePath))
            throw new FileNotFoundException("Asset register template was not copied to the output directory.", _templatePath);

        var rawTitle = string.IsNullOrWhiteSpace(TitleBox.Text) ? "ASSET REGISTER" : TitleBox.Text.Trim();
        var rawCompany = string.IsNullOrWhiteSpace(CompanyBox.Text) ? "KHZ" : CompanyBox.Text.Trim();

        var title = SecurityElement.Escape(rawTitle) ?? "ASSET REGISTER";
        var company = SecurityElement.Escape(rawCompany) ?? "KHZ";
        var database = SecurityElement.Escape(_databasePath) ?? _databasePath;
        var generatedAt = SecurityElement.Escape(
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)) ?? "";

        var rdl = await File.ReadAllTextAsync(_templatePath);
        rdl = rdl
            .Replace("__REPORT_TITLE__", title, StringComparison.Ordinal)
            .Replace("__COMPANY__", company, StringComparison.Ordinal)
            .Replace("__DB_PATH__", database, StringComparison.Ordinal)
            .Replace("__GENERATED_AT__", generatedAt, StringComparison.Ordinal);

        await _viewer.SetSourceRdl(rdl);
        await _viewer.Rebuild();
        StatusText.Text = $"Preview ready · local DB: {_databasePath}";
    }

    private async void ImportData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import tabular data",
                Filter = "Tabular data (*.xlsx;*.csv;*.tsv)|*.xlsx;*.csv;*.tsv|Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|TSV (*.tsv)|*.tsv",
                Multiselect = false,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
                return;

            var rows = ReadImport(dialog.FileName);
            ReplaceAssets(rows);
            await LoadReportAsync();

            StatusText.Text = $"Imported {rows.Count:N0} rows · {Path.GetFileName(dialog.FileName)} · {_databasePath}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private static IReadOnlyList<AssetRow> ReadImport(string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        var info = new FileInfo(source);

        if (!info.Exists)
            throw new FileNotFoundException("Import source was not found.", source);

        if (info.Length > MaximumImportBytes)
            throw new InvalidDataException($"Import source exceeds the {MaximumImportBytes} byte limit.");

        var extension = Path.GetExtension(source).ToLowerInvariant();
        var rawRows = extension switch
        {
            ".xlsx" => ReadXlsxRows(source),
            ".csv" => ReadDelimitedRows(source, ','),
            ".tsv" => ReadDelimitedRows(source, '\t'),
            _ => throw new InvalidDataException("Supported import formats are XLSX, CSV, and TSV.")
        };

        return MapAssetRows(rawRows);
    }

    private static List<string?[]> ReadXlsxRows(string sourcePath)
    {
        using var document = SpreadsheetDocument.Open(sourcePath, false);

        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("XLSX workbook part is missing.");

        var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidDataException("XLSX contains no worksheet.");

        var relationshipId = sheet.Id?.Value
            ?? throw new InvalidDataException("XLSX worksheet relationship is missing.");

        if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            throw new InvalidDataException("XLSX worksheet part is invalid.");

        var sheetData = worksheetPart.Worksheet?.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException("XLSX worksheet contains no sheet data.");

        var sharedStrings = workbookPart.SharedStringTablePart?
            .SharedStringTable?
            .Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToList()
            ?? new List<string>();

        var rows = new List<string?[]>();

        foreach (var row in sheetData.Elements<Row>())
        {
            var cells = new Dictionary<int, string?>();
            var nextColumn = 1;

            foreach (var cell in row.Elements<Cell>())
            {
                var column = TryGetColumnNumber(cell.CellReference?.Value, out var explicitColumn)
                    ? explicitColumn
                    : nextColumn;

                if (column < 1 || column > MaximumImportColumns)
                    throw new InvalidDataException($"XLSX exceeds the {MaximumImportColumns} column limit.");

                cells[column] = ReadCellText(cell, sharedStrings);
                nextColumn = column + 1;
            }

            if (cells.Count == 0)
                continue;

            var width = cells.Keys.Max();
            var values = new string?[width];
            foreach (var pair in cells)
                values[pair.Key - 1] = pair.Value;

            if (values.All(string.IsNullOrWhiteSpace))
                continue;

            rows.Add(values);
            if (rows.Count > MaximumImportRows + 1)
                throw new InvalidDataException($"XLSX exceeds the {MaximumImportRows} data-row limit.");
        }

        return rows;
    }

    private static string? ReadCellText(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell.CellFormula is not null)
            throw new InvalidDataException("XLSX formulas are not accepted in this spike; import stored values instead.");

        var type = cell.DataType?.Value;

        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";

        var raw = cell.CellValue?.Text ?? "";

        if (type == CellValues.SharedString)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= sharedStrings.Count)
            {
                throw new InvalidDataException("XLSX shared-string index is invalid.");
            }

            return sharedStrings[index];
        }

        if (type == CellValues.Boolean)
            return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw;

        if (type == CellValues.Error)
            throw new InvalidDataException("XLSX error cells are not accepted.");

        return raw;
    }

    private static bool TryGetColumnNumber(string? cellReference, out int columnNumber)
    {
        columnNumber = 0;
        if (string.IsNullOrWhiteSpace(cellReference))
            return false;

        foreach (var character in cellReference)
        {
            if (character >= 'A' && character <= 'Z')
            {
                checked
                {
                    columnNumber = columnNumber * 26 + character - 'A' + 1;
                }
                continue;
            }

            if (character >= 'a' && character <= 'z')
            {
                checked
                {
                    columnNumber = columnNumber * 26 + character - 'a' + 1;
                }
                continue;
            }

            break;
        }

        return columnNumber > 0;
    }

    private static List<string?[]> ReadDelimitedRows(string sourcePath, char delimiter)
    {
        var text = File.ReadAllText(sourcePath, Encoding.UTF8);
        var rows = new List<string?[]>();
        var row = new List<string?>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (character == delimiter && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
                continue;
            }

            if ((character == '\r' || character == '\n') && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    index++;

                row.Add(field.ToString());
                field.Clear();
                AddDelimitedRow(rows, row);
                row.Clear();
                continue;
            }

            field.Append(character);
        }

        if (quoted)
            throw new InvalidDataException("Delimited file contains an unterminated quoted field.");

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            AddDelimitedRow(rows, row);
        }

        if (rows.Count > MaximumImportRows + 1)
            throw new InvalidDataException($"Delimited file exceeds the {MaximumImportRows} data-row limit.");

        return rows;
    }

    private static void AddDelimitedRow(List<string?[]> rows, IReadOnlyList<string?> row)
    {
        if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
            rows.Add(row.ToArray());
    }

    private static IReadOnlyList<AssetRow> MapAssetRows(IReadOnlyList<string?[]> rawRows)
    {
        if (rawRows.Count < 2)
            throw new InvalidDataException("Import must contain a header row and at least one data row.");

        var headers = rawRows[0].Select(NormalizeHeader).ToArray();
        var rowNoIndex = FindHeader(headers, "no", "number", "rowno", "rownumber", "رقم", "الرقم");
        var tagIndex = FindHeader(headers, "tag", "tagnumber", "assettag", "رقمالاصل", "رقمالأصل", "رقمالتاق");
        var descriptionIndex = FindHeader(headers, "description", "assetdescription", "الوصف", "وصفالاصل", "وصفالأصل");
        var locationIndex = FindHeader(headers, "location", "mloc", "assetlocation", "الموقع", "موقع");

        if (tagIndex < 0 || descriptionIndex < 0 || locationIndex < 0)
        {
            throw new InvalidDataException(
                "Could not map required columns. Expected headers equivalent to TAG NUMBER, ASSET DESCRIPTION, and M/LOC. NO. is optional.");
        }

        var result = new List<AssetRow>();
        var usedRowNumbers = new HashSet<long>();
        long nextAutomaticRow = 1;

        foreach (var rawRow in rawRows.Skip(1))
        {
            var tag = GetCell(rawRow, tagIndex).Trim();
            var description = GetCell(rawRow, descriptionIndex).Trim();
            var location = GetCell(rawRow, locationIndex).Trim();

            if (tag.Length == 0 && description.Length == 0 && location.Length == 0)
                continue;

            long rowNo;
            var rawRowNo = rowNoIndex >= 0 ? GetCell(rawRow, rowNoIndex).Trim() : "";

            if (rawRowNo.Length > 0)
            {
                if (!long.TryParse(rawRowNo, NumberStyles.Integer, CultureInfo.InvariantCulture, out rowNo) || rowNo <= 0)
                    throw new InvalidDataException($"Invalid NO. value: {rawRowNo}");
            }
            else
            {
                while (usedRowNumbers.Contains(nextAutomaticRow))
                    nextAutomaticRow++;

                rowNo = nextAutomaticRow++;
            }

            if (!usedRowNumbers.Add(rowNo))
                throw new InvalidDataException($"Duplicate NO. value: {rowNo}");

            result.Add(new AssetRow(rowNo, tag, description, location));
            if (result.Count > MaximumImportRows)
                throw new InvalidDataException($"Import exceeds the {MaximumImportRows} data-row limit.");
        }

        if (result.Count == 0)
            throw new InvalidDataException("Import contains no usable asset rows.");

        return result;
    }

    private static int FindHeader(IReadOnlyList<string> headers, params string[] aliases)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (aliases.Contains(headers[index], StringComparer.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private static string NormalizeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder();
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
        }

        return builder.ToString();
    }

    private static string GetCell(IReadOnlyList<string?> row, int index)
        => index >= 0 && index < row.Count ? row[index] ?? "" : "";

    private void ReplaceAssets(IReadOnlyList<AssetRow> rows)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM assets;";
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO assets(row_no, tag, description, location)
            VALUES ($row_no, $tag, $description, $location);
            """;

        var rowNoParameter = insert.Parameters.Add("$row_no", SqliteType.Integer);
        var tagParameter = insert.Parameters.Add("$tag", SqliteType.Text);
        var descriptionParameter = insert.Parameters.Add("$description", SqliteType.Text);
        var locationParameter = insert.Parameters.Add("$location", SqliteType.Text);

        foreach (var row in rows)
        {
            rowNoParameter.Value = row.RowNo;
            tagParameter.Value = row.Tag;
            descriptionParameter.Value = row.Description;
            locationParameter.Value = row.Location;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadReportAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Asset Register",
                Filter = "PDF document (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = "asset-register.pdf"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            await _viewer.SaveAs(dialog.FileName, OutputPresentationType.PDF);
            StatusText.Text = $"PDF written: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(
            this,
            ex.ToString(),
            "KHZ Reporting Spike",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private sealed record AssetRow(long RowNo, string Tag, string Description, string Location);
}
