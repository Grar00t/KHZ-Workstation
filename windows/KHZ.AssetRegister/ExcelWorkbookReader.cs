using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace KHZ.AssetRegister;

public sealed class ExcelPreviewRow
{
    public bool IsSelected { get; set; } = true;
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
    private static readonly Dictionary<string, string> HeaderAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["assettag"] = "AssetTag",
            ["tag"] = "AssetTag",
            ["assetno"] = "AssetTag",
            ["assetnumber"] = "AssetTag",
            ["serialnumber"] = "SerialNumber",
            ["serial"] = "SerialNumber",
            ["serialno"] = "SerialNumber",
            ["sn"] = "SerialNumber",
            ["barcode"] = "Barcode",
            ["category"] = "Category",
            ["description"] = "Description",
            ["manufacturer"] = "Manufacturer",
            ["make"] = "Manufacturer",
            ["model"] = "Model",
            ["location"] = "Location",
            ["department"] = "Department",
            ["dept"] = "Department",
            ["custodian"] = "Custodian",
            ["assignedto"] = "Custodian",
            ["owner"] = "Custodian",
            ["status"] = "Status",
            ["purchasedate"] = "PurchaseDate",
            ["purchasecost"] = "PurchaseCost",
            ["cost"] = "PurchaseCost",
            ["warrantyexpiry"] = "WarrantyExpiry",
            ["warrantyexpiration"] = "WarrantyExpiry",
            ["condition"] = "Condition",
            ["notes"] = "Notes",
            ["note"] = "Notes",
            ["remarks"] = "Notes"
        };

    public static ExcelImportData Read(string filePath)
    {
        using var document = SpreadsheetDocument.Open(filePath, false);

        var workbookPart = document.WorkbookPart
            ?? throw new InvalidOperationException("Excel workbook part was not found.");

        var sheet = workbookPart.Workbook.Sheets?
            .Elements<Sheet>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The workbook does not contain a worksheet.");

        if (sheet.Id?.Value is null)
            throw new InvalidOperationException("The first worksheet has no relationship id.");

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
        var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidOperationException("The worksheet does not contain sheet data.");

        var rows = sheetData.Elements<Row>().ToList();
        if (rows.Count == 0)
            throw new InvalidOperationException("The worksheet is empty.");

        var headerRow = rows.FirstOrDefault(RowHasAnyValue)
            ?? throw new InvalidOperationException("No header row was found.");

        var headers = ReadRow(workbookPart, headerRow);
        var mappedColumns = BuildColumnMap(headers);

        if (mappedColumns.Count == 0)
        {
            throw new InvalidOperationException(
                "No recognized asset columns were found. Expected headers such as AssetTag, SerialNumber, Category, Location, Custodian, Status, PurchaseDate, PurchaseCost, Condition or Notes.");
        }

        var dataRows = new List<ExcelPreviewRow>();
        var headerIndex = rows.IndexOf(headerRow);

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var values = ReadRow(workbookPart, rows[i]);
            if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace))
                continue;

            var preview = new ExcelPreviewRow();

            foreach (var mapping in mappedColumns)
            {
                values.TryGetValue(mapping.Key, out var raw);
                SetValue(preview, mapping.Value, raw ?? string.Empty, i + 1);
            }

            if (string.IsNullOrWhiteSpace(preview.AssetTag))
                preview.AssetTag = $"KHZ-IMP-{DateTime.Now:yyyyMMddHHmmss}-{i + 1:D5}";

            if (string.IsNullOrWhiteSpace(preview.Status))
                preview.Status = "In Service";

            dataRows.Add(preview);
        }

        if (dataRows.Count == 0)
            throw new InvalidOperationException("No data rows were found below the header row.");

        return new ExcelImportData(
            sheet.Name?.Value ?? "Sheet1",
            dataRows);
    }

    private static Dictionary<int, string> BuildColumnMap(Dictionary<int, string> headers)
    {
        var result = new Dictionary<int, string>();

        foreach (var item in headers)
        {
            var normalized = NormalizeHeader(item.Value);
            if (HeaderAliases.TryGetValue(normalized, out var property))
                result[item.Key] = property;
        }

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
            return workbookPart.SharedStringTablePart?
                .SharedStringTable
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

        foreach (var character in cellReference)
        {
            if (!char.IsLetter(character))
                break;

            index = checked(index * 26 + (char.ToUpperInvariant(character) - 'A' + 1));
        }

        return index - 1;
    }

    private static string NormalizeHeader(string value)
        => new(value
            .Where(char.IsLetterOrDigit)
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

                if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariant)
                    || decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out invariant))
                {
                    row.PurchaseCost = invariant;
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

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oaDate)
            && oaDate >= 1
            && oaDate <= 2958465)
        {
            try
            {
                return DateTime.FromOADate(oaDate)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch
            {
            }
        }

        return value;
    }
}
