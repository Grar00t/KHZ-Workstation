using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace KHZ.AssetRegister;

public sealed class ExcelPreviewRow : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string AssetTag { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Custodian { get; set; } = string.Empty;
    public string Status { get; set; } = "In Service";
    public string PurchaseDate { get; set; } = string.Empty;
    public decimal PurchaseCost { get; set; }
    public string WarrantyExpiry { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AssetRecord ToAssetRecord()
        => new()
        {
            AssetTag = AssetTag.Trim(),
            SerialNumber = SerialNumber.Trim(),
            Barcode = Barcode.Trim(),
            Category = Category.Trim(),
            Description = Description.Trim(),
            Manufacturer = Manufacturer.Trim(),
            Model = Model.Trim(),
            Location = Location.Trim(),
            Department = Department.Trim(),
            Custodian = Custodian.Trim(),
            Status = string.IsNullOrWhiteSpace(Status) ? "In Service" : Status.Trim(),
            PurchaseDate = PurchaseDate.Trim(),
            PurchaseCost = PurchaseCost,
            WarrantyExpiry = WarrantyExpiry.Trim(),
            Condition = Condition.Trim(),
            Notes = Notes.Trim()
        };
}

public sealed record ExcelImportData(
    string SheetName,
    IReadOnlyList<ExcelPreviewRow> Rows);

public static class ExcelWorkbookReader
{
    private const int HeaderScanLimit = 50;
    private const int MaxImportedRows = 100_000;
    private const int MaxColumnIndex = 16_384;

    private static readonly Dictionary<string, string> HeaderAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["assettag"] = "AssetTag",
            ["tag"] = "AssetTag",
            ["assetno"] = "AssetTag",
            ["assetnumber"] = "AssetTag",
            ["رقمالأصل"] = "AssetTag",
            ["رقمالاصل"] = "AssetTag",
            ["رقمالعهدة"] = "AssetTag",
            ["رقمالعهد"] = "AssetTag",
            ["serialnumber"] = "SerialNumber",
            ["serial"] = "SerialNumber",
            ["serialno"] = "SerialNumber",
            ["sn"] = "SerialNumber",
            ["الرقمالتسلسلي"] = "SerialNumber",
            ["رقمتسلسلي"] = "SerialNumber",
            ["barcode"] = "Barcode",
            ["باركود"] = "Barcode",
            ["الباركود"] = "Barcode",
            ["category"] = "Category",
            ["الفئة"] = "Category",
            ["التصنيف"] = "Category",
            ["description"] = "Description",
            ["الوصف"] = "Description",
            ["manufacturer"] = "Manufacturer",
            ["make"] = "Manufacturer",
            ["المصنع"] = "Manufacturer",
            ["الشركةالمصنعة"] = "Manufacturer",
            ["model"] = "Model",
            ["الموديل"] = "Model",
            ["الطراز"] = "Model",
            ["location"] = "Location",
            ["الموقع"] = "Location",
            ["department"] = "Department",
            ["dept"] = "Department",
            ["القسم"] = "Department",
            ["الإدارة"] = "Department",
            ["الادارة"] = "Department",
            ["custodian"] = "Custodian",
            ["assignedto"] = "Custodian",
            ["owner"] = "Custodian",
            ["المستلم"] = "Custodian",
            ["المستخدم"] = "Custodian",
            ["المسؤول"] = "Custodian",
            ["status"] = "Status",
            ["الحالة"] = "Status",
            ["purchasedate"] = "PurchaseDate",
            ["تاريخالشراء"] = "PurchaseDate",
            ["purchasecost"] = "PurchaseCost",
            ["cost"] = "PurchaseCost",
            ["التكلفة"] = "PurchaseCost",
            ["سعرالشراء"] = "PurchaseCost",
            ["warrantyexpiry"] = "WarrantyExpiry",
            ["warrantyexpiration"] = "WarrantyExpiry",
            ["انتهاءالضمان"] = "WarrantyExpiry",
            ["تاريخانتهاءالضمان"] = "WarrantyExpiry",
            ["condition"] = "Condition",
            ["حالةالأصل"] = "Condition",
            ["حالةالاصل"] = "Condition",
            ["notes"] = "Notes",
            ["note"] = "Notes",
            ["remarks"] = "Notes",
            ["ملاحظات"] = "Notes",
            ["الملاحظات"] = "Notes"
        };

    public static ExcelImportData Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Excel file path is required.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Excel workbook was not found.", filePath);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var document = SpreadsheetDocument.Open(stream, false);

        var workbookPart = document.WorkbookPart
            ?? throw new InvalidOperationException("Excel workbook part was not found.");

        var workbook = workbookPart.Workbook
            ?? throw new InvalidOperationException("Excel workbook metadata was not found.");

        var sheets = workbook.Sheets?.Elements<Sheet>().ToArray()
            ?? Array.Empty<Sheet>();

        if (sheets.Length == 0)
            throw new InvalidOperationException("The workbook does not contain a worksheet.");

        SheetCandidate? best = null;

        foreach (var sheet in sheets)
        {
            var relationshipId = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId))
                continue;

            WorksheetPart? worksheetPart;

            try
            {
                worksheetPart = workbookPart.GetPartById(relationshipId) as WorksheetPart;
            }
            catch
            {
                continue;
            }

            var worksheet = worksheetPart?.Worksheet;
            var sheetData = worksheet?.GetFirstChild<SheetData>();
            if (sheetData is null)
                continue;

            var rows = sheetData.Elements<Row>()
                .Take(MaxImportedRows + HeaderScanLimit + 2)
                .ToList();

            if (rows.Count == 0)
                continue;

            var scanCount = Math.Min(rows.Count, HeaderScanLimit);

            for (var index = 0; index < scanCount; index++)
            {
                var row = rows[index];
                if (!RowHasAnyValue(row))
                    continue;

                var headers = ReadRow(workbookPart, row);
                var score = CountRecognizedHeaders(headers);

                if (score == 0 || (best is not null && score <= best.Score))
                    continue;

                best = new SheetCandidate(
                    sheet.Name?.Value ?? "Sheet1",
                    rows,
                    index,
                    headers,
                    score);
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException(
                "No recognized asset header row was found in the first 50 rows of any worksheet. Expected headers such as AssetTag, SerialNumber, Category, Location, Custodian, Status, PurchaseDate, PurchaseCost, Condition or Notes.");
        }

        var mappedColumns = BuildColumnMap(best.Headers);
        var dataRows = new List<ExcelPreviewRow>();
        var importBatch = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        for (var index = best.HeaderIndex + 1; index < best.Rows.Count; index++)
        {
            if (dataRows.Count >= MaxImportedRows)
            {
                throw new InvalidOperationException(
                    $"The workbook exceeds the import safety limit of {MaxImportedRows:N0} data rows.");
            }

            var sourceRow = best.Rows[index];
            var values = ReadRow(workbookPart, sourceRow);

            if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace))
                continue;

            var rowNumber = sourceRow.RowIndex?.Value is uint actualRow && actualRow <= int.MaxValue
                ? (int)actualRow
                : index + 1;

            var preview = new ExcelPreviewRow();

            foreach (var mapping in mappedColumns)
            {
                values.TryGetValue(mapping.Key, out var raw);
                SetValue(preview, mapping.Value, raw ?? string.Empty, rowNumber);
            }

            if (string.IsNullOrWhiteSpace(preview.AssetTag))
                preview.AssetTag = $"KHZ-IMP-{importBatch}-{rowNumber:D6}";

            if (string.IsNullOrWhiteSpace(preview.Status))
                preview.Status = "In Service";

            dataRows.Add(preview);
        }

        if (dataRows.Count == 0)
            throw new InvalidOperationException("No data rows were found below the detected asset header row.");

        return new ExcelImportData(best.SheetName, dataRows);
    }

    private static int CountRecognizedHeaders(Dictionary<int, string> headers)
        => headers.Values
            .Select(NormalizeHeader)
            .Where(HeaderAliases.ContainsKey)
            .Select(x => HeaderAliases[x])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static Dictionary<int, string> BuildColumnMap(Dictionary<int, string> headers)
    {
        var result = new Dictionary<int, string>();
        var mappedProperties = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in headers.OrderBy(x => x.Key))
        {
            var normalized = NormalizeHeader(item.Value);
            if (!HeaderAliases.TryGetValue(normalized, out var property))
                continue;

            if (mappedProperties.TryGetValue(property, out var existingColumn))
            {
                throw new InvalidOperationException(
                    $"The Excel header maps more than one column to '{property}' (columns {existingColumn + 1} and {item.Key + 1}). Rename or remove the duplicate column before importing.");
            }

            result[item.Key] = property;
            mappedProperties[property] = item.Key;
        }

        if (result.Count == 0)
            throw new InvalidOperationException("No recognized asset columns were found.");

        return result;
    }

    private static Dictionary<int, string> ReadRow(WorkbookPart workbookPart, Row row)
    {
        var result = new Dictionary<int, string>();

        foreach (var cell in row.Elements<Cell>())
        {
            var reference = cell.CellReference?.Value;
            if (string.IsNullOrWhiteSpace(reference))
                continue;

            var column = GetColumnIndex(reference);
            if (column < 0 || column >= MaxColumnIndex)
                continue;

            result[column] = GetCellText(workbookPart, cell);
        }

        return result;
    }

    private static bool RowHasAnyValue(Row row)
        => row.Elements<Cell>().Any(cell =>
            !string.IsNullOrWhiteSpace(cell.CellValue?.InnerText)
            || !string.IsNullOrWhiteSpace(cell.InlineString?.InnerText));

    private static string GetCellText(WorkbookPart workbookPart, Cell cell)
    {
        var raw = cell.CellValue?.InnerText ?? cell.InlineString?.InnerText ?? string.Empty;

        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex))
        {
            var sharedStringTable = workbookPart.SharedStringTablePart?.SharedStringTable;
            if (sharedStringTable is null || sharedIndex < 0)
                return string.Empty;

            return sharedStringTable
                .Elements<SharedStringItem>()
                .ElementAtOrDefault(sharedIndex)?
                .InnerText ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";

        return raw;
    }

    private static int GetColumnIndex(string cellReference)
    {
        var index = 0;
        var foundLetter = false;

        foreach (var character in cellReference)
        {
            if (!char.IsLetter(character))
                break;

            foundLetter = true;
            index = checked(index * 26 + (char.ToUpperInvariant(character) - 'A' + 1));
        }

        return foundLetter ? index - 1 : -1;
    }

    private static string NormalizeHeader(string value)
        => new(value
            .Where(character => char.IsLetterOrDigit(character) && character != '\u0640')
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static void SetValue(
        ExcelPreviewRow row,
        string property,
        string raw,
        int rowNumber)
    {
        var value = raw.Trim();

        switch (property)
        {
            case "AssetTag": row.AssetTag = value; break;
            case "SerialNumber": row.SerialNumber = value; break;
            case "Barcode": row.Barcode = value; break;
            case "Category": row.Category = value; break;
            case "Description": row.Description = value; break;
            case "Manufacturer": row.Manufacturer = value; break;
            case "Model": row.Model = value; break;
            case "Location": row.Location = value; break;
            case "Department": row.Department = value; break;
            case "Custodian": row.Custodian = value; break;
            case "Status": row.Status = value; break;
            case "PurchaseDate": row.PurchaseDate = NormalizeDate(value); break;
            case "WarrantyExpiry": row.WarrantyExpiry = NormalizeDate(value); break;
            case "Condition": row.Condition = value; break;
            case "Notes": row.Notes = value; break;
            case "PurchaseCost":
                if (string.IsNullOrWhiteSpace(value))
                {
                    row.PurchaseCost = 0m;
                    break;
                }

                if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedCost)
                    || decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out parsedCost))
                {
                    row.PurchaseCost = parsedCost;
                    break;
                }

                throw new InvalidOperationException(
                    $"PurchaseCost '{value}' on Excel row {rowNumber} is not a valid number.");
        }
    }

    private static string NormalizeDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oaDate)
            && oaDate >= 1
            && oaDate <= 2_958_465)
        {
            try
            {
                return DateTime.FromOADate(oaDate)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
            }
        }

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return value;
    }

    private sealed record SheetCandidate(
        string SheetName,
        List<Row> Rows,
        int HeaderIndex,
        Dictionary<int, string> Headers,
        int Score);
}
