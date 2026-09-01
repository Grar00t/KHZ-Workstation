using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KHZ.App.StructuredData;

internal sealed class XlsxStructuredDataService
{
    private const int MaximumColumns = 256;
    private const int MaximumRows = 5000;
    private const long MaximumSourceBytes =
        32L * 1024L * 1024L;

    private readonly IWorkspaceDataStore _store;

    public XlsxStructuredDataService(
        IWorkspaceDataStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public string ImportXlsx(
        string sourcePath,
        string? tableName = null)
    {
        var source =
            NormalizeExistingXlsx(
                sourcePath);

        var info =
            new FileInfo(source);

        if (info.Length > MaximumSourceBytes)
        {
            throw new InvalidDataException(
                $"XLSX source exceeds the {MaximumSourceBytes} byte import limit.");
        }

        using var document =
            SpreadsheetDocument.Open(
                source,
                false);

        var workbookPart =
            document.WorkbookPart
            ?? throw new InvalidDataException(
                "XLSX workbook part is missing.");

        var workbook =
            workbookPart.Workbook
            ?? throw new InvalidDataException(
                "XLSX workbook is missing.");

        var sheet =
            workbook
                .Sheets?
                .Elements<Sheet>()
                .FirstOrDefault()
            ?? throw new InvalidDataException(
                "XLSX contains no worksheet.");

        var relationshipId =
            sheet.Id?.Value
            ?? throw new InvalidDataException(
                "XLSX worksheet relationship is missing.");

        if (workbookPart.GetPartById(
                relationshipId)
            is not WorksheetPart worksheetPart)
        {
            throw new InvalidDataException(
                "XLSX worksheet part is invalid.");
        }

        var worksheet =
            worksheetPart.Worksheet
            ?? throw new InvalidDataException(
                "XLSX worksheet is missing.");

        var sheetData =
            worksheet
                .GetFirstChild<SheetData>()
            ?? throw new InvalidDataException(
                "XLSX worksheet has no sheet data.");

        var sharedStrings =
            workbookPart
                .SharedStringTablePart?
                .SharedStringTable?
                .Elements<SharedStringItem>()
                .Select(x => x.InnerText)
                .ToList()
            ?? new List<string>();

        var rows =
            sheetData
                .Elements<Row>()
                .ToList();

        var headerIndex =
            rows.FindIndex(
                row =>
                    row.Elements<Cell>().Any());

        if (headerIndex < 0)
        {
            throw new InvalidDataException(
                "XLSX contains no header row.");
        }

        var headerMap =
            ReadRow(
                rows[headerIndex],
                sharedStrings);

        if (headerMap.Count == 0)
        {
            throw new InvalidDataException(
                "XLSX header row is empty.");
        }

        var headerWidth =
            headerMap.Keys.Max();

        if (headerWidth > MaximumColumns)
        {
            throw new InvalidDataException(
                $"XLSX contains more than {MaximumColumns} columns.");
        }

        var rawHeaders =
            new string?[headerWidth];

        foreach (var pair in headerMap)
        {
            rawHeaders[pair.Key - 1] =
                pair.Value;
        }

        var headers =
            NormalizeHeaders(
                rawHeaders);

        var rawRows =
            new List<string?[]>();

        foreach (var row
                 in rows.Skip(headerIndex + 1))
        {
            var map =
                ReadRow(
                    row,
                    sharedStrings);

            if (map.Any(
                    pair =>
                        pair.Key > headerWidth
                        && !string.IsNullOrEmpty(
                            pair.Value)))
            {
                throw new InvalidDataException(
                    "XLSX data row contains values beyond the header width.");
            }

            var values =
                new string?[headerWidth];

            foreach (var pair
                     in map)
            {
                if (pair.Key <= headerWidth)
                {
                    values[pair.Key - 1] =
                        pair.Value;
                }
            }

            if (values.All(
                    string.IsNullOrEmpty))
            {
                continue;
            }

            rawRows.Add(values);

            if (rawRows.Count > MaximumRows)
            {
                throw new InvalidDataException(
                    $"XLSX contains more than {MaximumRows} data rows.");
            }
        }

        var columns =
            new List<DataColumnDefinition>(
                headers.Count);

        for (var index = 0;
             index < headers.Count;
             index++)
        {
            columns.Add(
                new DataColumnDefinition(
                    headers[index],
                    InferType(
                        rawRows.Select(
                            row => row[index]))));
        }

        var convertedRows =
            new List<
                IReadOnlyDictionary<string, object?>>(
                    rawRows.Count);

        foreach (var rawRow
                 in rawRows)
        {
            var converted =
                new Dictionary<string, object?>(
                    StringComparer.OrdinalIgnoreCase);

            for (var index = 0;
                 index < headers.Count;
                 index++)
            {
                var raw =
                    rawRow[index];

                if (string.IsNullOrEmpty(raw))
                    continue;

                converted[headers[index]] =
                    ConvertCell(
                        raw,
                        columns[index].Type);
            }

            convertedRows.Add(
                converted);
        }

        var effectiveName =
            NormalizeIdentifier(
                string.IsNullOrWhiteSpace(tableName)
                    ? Path.GetFileNameWithoutExtension(source)
                    : tableName,
                "Imported");

        return _store.CreateTableWithRows(
            effectiveName,
            columns,
            convertedRows);
    }

    public string ExportXlsx(
        string tableId,
        string destinationPath)
    {
        var destination =
            NormalizeDestination(
                destinationPath);

        var rowCount =
            _store.CountRows(
                tableId);

        if (rowCount > MaximumRows)
        {
            throw new InvalidOperationException(
                $"XLSX export refuses to truncate {rowCount} rows at the {MaximumRows} row query boundary.");
        }

        var result =
            _store.Query(
                tableId,
                limit:
                    rowCount == 0
                        ? 1
                        : checked((int)rowCount));

        if (result.Rows.Count != rowCount)
        {
            throw new InvalidDataException(
                "XLSX export row-count verification failed.");
        }

        var parent =
            Path.GetDirectoryName(
                destination)
            ?? throw new InvalidOperationException(
                "XLSX destination has no parent directory.");

        Directory.CreateDirectory(
            parent);

        var temporaryPath =
            destination
            + ".tmp-"
            + Guid.NewGuid()
                .ToString("N");

        try
        {
            using (
                var stream =
                    new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        options:
                            FileOptions.WriteThrough))
            {
                using (
                    var document =
                        SpreadsheetDocument.Create(
                            stream,
                            SpreadsheetDocumentType.Workbook,
                            true))
                {
                    var workbookPart =
                        document.AddWorkbookPart();

                    workbookPart.Workbook =
                        new Workbook();

                    var worksheetPart =
                        workbookPart
                            .AddNewPart<WorksheetPart>();

                    var sheetData =
                        new SheetData();

                    worksheetPart.Worksheet =
                        new Worksheet(
                            sheetData);

                    var sheets =
                        workbookPart
                            .Workbook
                            .AppendChild(
                                new Sheets());

                    sheets.Append(
                        new Sheet
                        {
                            Id =
                                workbookPart
                                    .GetIdOfPart(
                                        worksheetPart),

                            SheetId = 1U,
                            Name = "Data"
                        });

                    uint rowNumber = 1;

                    var header =
                        new Row
                        {
                            RowIndex =
                                rowNumber
                        };

                    for (var columnIndex = 0;
                         columnIndex < result.Columns.Count;
                         columnIndex++)
                    {
                        header.Append(
                            CreateTextCell(
                                result.Columns[columnIndex],
                                rowNumber,
                                columnIndex + 1));
                    }

                    sheetData.Append(
                        header);

                    foreach (var dataRow
                             in result.Rows)
                    {
                        rowNumber++;

                        var row =
                            new Row
                            {
                                RowIndex =
                                    rowNumber
                            };

                        for (var columnIndex = 0;
                             columnIndex < result.Columns.Count;
                             columnIndex++)
                        {
                            var column =
                                result.Columns[columnIndex];

                            row.Append(
                                CreateValueCell(
                                    dataRow[column],
                                    rowNumber,
                                    columnIndex + 1));
                        }

                        sheetData.Append(
                            row);
                    }

                    worksheetPart
                        .Worksheet
                        .Save();

                    workbookPart
                        .Workbook
                        .Save();
                }

                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                temporaryPath,
                destination,
                overwrite: true);

            return destination;
        }
        finally
        {
            if (File.Exists(
                    temporaryPath))
            {
                File.Delete(
                    temporaryPath);
            }
        }
    }

    private static Dictionary<int, string?> ReadRow(
        Row row,
        IReadOnlyList<string> sharedStrings)
    {
        var result =
            new Dictionary<int, string?>();

        var nextColumn =
            1;

        foreach (var cell
                 in row.Elements<Cell>())
        {
            var column =
                TryGetColumnNumber(
                    cell.CellReference?.Value,
                    out var explicitColumn)
                    ? explicitColumn
                    : nextColumn;

            if (column < 1
                || column > MaximumColumns + 1)
            {
                throw new InvalidDataException(
                    "XLSX cell reference exceeds the supported column boundary.");
            }

            if (!result.TryAdd(
                    column,
                    ReadCellText(
                        cell,
                        sharedStrings)))
            {
                throw new InvalidDataException(
                    "XLSX row contains duplicate cell coordinates.");
            }

            nextColumn =
                column + 1;
        }

        return result;
    }

    private static string? ReadCellText(
        Cell cell,
        IReadOnlyList<string> sharedStrings)
    {
        if (cell.CellFormula is not null)
        {
            throw new InvalidDataException(
                "XLSX formulas are not accepted; store values instead of formulas.");
        }

        var type =
            cell.DataType?.Value;

        if (type == CellValues.InlineString)
        {
            return cell
                .InlineString?
                .InnerText
                ?? "";
        }

        var raw =
            cell.CellValue?.Text
            ?? "";

        if (type == CellValues.SharedString)
        {
            if (!int.TryParse(
                    raw,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index)
                || index < 0
                || index >= sharedStrings.Count)
            {
                throw new InvalidDataException(
                    "XLSX shared-string index is invalid.");
            }

            return sharedStrings[index];
        }

        if (type == CellValues.Boolean)
        {
            return raw == "1"
                ? "TRUE"
                : raw == "0"
                    ? "FALSE"
                    : throw new InvalidDataException(
                        "XLSX boolean value is invalid.");
        }

        if (type == CellValues.Error)
        {
            throw new InvalidDataException(
                "XLSX error cells are not accepted.");
        }

        return raw;
    }

    private static Cell CreateValueCell(
        object? value,
        uint rowNumber,
        int columnNumber)
    {
        if (value is null)
        {
            return new Cell
            {
                CellReference =
                    CellReference(
                        rowNumber,
                        columnNumber)
            };
        }

        if (value is byte[] bytes)
        {
            return CreateTextCell(
                "base64:"
                + Convert.ToBase64String(bytes),
                rowNumber,
                columnNumber);
        }

        if (value is string text)
        {
            return CreateTextCell(
                text,
                rowNumber,
                columnNumber);
        }

        if (value is long integer)
        {
            return CreateNumberCell(
                integer.ToString(
                    CultureInfo.InvariantCulture),
                rowNumber,
                columnNumber);
        }

        if (value is double real)
        {
            if (!double.IsFinite(real))
            {
                throw new InvalidDataException(
                    "Non-finite REAL values cannot be exported to XLSX.");
            }

            return CreateNumberCell(
                real.ToString(
                    "R",
                    CultureInfo.InvariantCulture),
                rowNumber,
                columnNumber);
        }

        throw new InvalidDataException(
            $"Unsupported Structured Data value type for XLSX export: {value.GetType().Name}");
    }

    private static Cell CreateTextCell(
        string value,
        uint rowNumber,
        int columnNumber)
        => new()
        {
            CellReference =
                CellReference(
                    rowNumber,
                    columnNumber),

            DataType =
                CellValues.InlineString,

            InlineString =
                new InlineString(
                    new Text(value)
                    {
                        Space =
                            SpaceProcessingModeValues.Preserve
                    })
        };

    private static Cell CreateNumberCell(
        string value,
        uint rowNumber,
        int columnNumber)
        => new()
        {
            CellReference =
                CellReference(
                    rowNumber,
                    columnNumber),

            DataType =
                CellValues.Number,

            CellValue =
                new CellValue(value)
        };

    private static string CellReference(
        uint rowNumber,
        int columnNumber)
        => ColumnName(columnNumber)
           + rowNumber.ToString(
               CultureInfo.InvariantCulture);

    private static string ColumnName(
        int columnNumber)
    {
        if (columnNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columnNumber));
        }

        var builder =
            new StringBuilder();

        var value =
            columnNumber;

        while (value > 0)
        {
            value--;

            builder.Insert(
                0,
                (char)(
                    'A'
                    + value % 26));

            value /= 26;
        }

        return builder.ToString();
    }

    private static bool TryGetColumnNumber(
        string? cellReference,
        out int columnNumber)
    {
        columnNumber = 0;

        if (string.IsNullOrWhiteSpace(
                cellReference))
        {
            return false;
        }

        foreach (var character
                 in cellReference)
        {
            if (character >= 'A'
                && character <= 'Z')
            {
                checked
                {
                    columnNumber =
                        columnNumber * 26
                        + character
                        - 'A'
                        + 1;
                }

                continue;
            }

            if (character >= 'a'
                && character <= 'z')
            {
                checked
                {
                    columnNumber =
                        columnNumber * 26
                        + character
                        - 'a'
                        + 1;
                }

                continue;
            }

            break;
        }

        return columnNumber > 0;
    }

    private static IReadOnlyList<string> NormalizeHeaders(
        IReadOnlyList<string?> rawHeaders)
    {
        var result =
            new List<string>(
                rawHeaders.Count);

        var used =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        for (var index = 0;
             index < rawHeaders.Count;
             index++)
        {
            var candidate =
                NormalizeIdentifier(
                    rawHeaders[index],
                    $"Column_{index + 1}");

            if (string.Equals(
                    candidate,
                    "row_id",
                    StringComparison.OrdinalIgnoreCase))
            {
                candidate =
                    "Source_row_id";
            }

            candidate =
                MakeUniqueIdentifier(
                    candidate,
                    used);

            result.Add(
                candidate);

            used.Add(
                candidate);
        }

        return result;
    }

    private static string MakeUniqueIdentifier(
        string candidate,
        IReadOnlySet<string> used)
    {
        if (!used.Contains(candidate))
            return candidate;

        for (var suffixNumber = 2;
             suffixNumber < int.MaxValue;
             suffixNumber++)
        {
            var suffix =
                "_"
                + suffixNumber.ToString(
                    CultureInfo.InvariantCulture);

            var prefixLength =
                Math.Min(
                    candidate.Length,
                    63 - suffix.Length);

            var value =
                candidate[..prefixLength]
                + suffix;

            if (!used.Contains(value))
                return value;
        }

        throw new InvalidOperationException(
            "Unable to generate a unique XLSX column name.");
    }

    private static string NormalizeIdentifier(
        string? raw,
        string fallback)
    {
        var source =
            string.IsNullOrWhiteSpace(raw)
                ? fallback
                : raw.Trim();

        var builder =
            new StringBuilder();

        foreach (var character
                 in source)
        {
            var valid =
                IsAsciiLetter(character)
                || (
                    character >= '0'
                    && character <= '9'
                )
                || character == '_';

            builder.Append(
                valid
                    ? character
                    : '_');
        }

        var candidate =
            builder
                .ToString()
                .Trim('_');

        if (candidate.Length == 0)
            candidate = fallback;

        if (!IsAsciiLetter(
                candidate[0]))
        {
            candidate =
                "Column_"
                + candidate;
        }

        if (candidate.Length > 63)
            candidate = candidate[..63];

        return candidate;
    }

    private static StructuredDataType InferType(
        IEnumerable<string?> values)
    {
        var present =
            values
                .Where(
                    value =>
                        !string.IsNullOrEmpty(value))
                .Cast<string>()
                .ToList();

        if (present.Count == 0)
            return StructuredDataType.Text;

        if (present.All(TryParseBlob))
            return StructuredDataType.Blob;

        if (present.All(
                value =>
                    long.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out _)))
        {
            return StructuredDataType.Integer;
        }

        if (present.All(
                TryParseFiniteDouble))
        {
            return StructuredDataType.Real;
        }

        return StructuredDataType.Text;
    }

    private static object ConvertCell(
        string value,
        StructuredDataType type)
        => type switch
        {
            StructuredDataType.Text =>
                value,

            StructuredDataType.Integer =>
                long.Parse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture),

            StructuredDataType.Real =>
                ParseFiniteDouble(value),

            StructuredDataType.Blob =>
                ParseBlob(value),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(type))
        };

    private static bool TryParseFiniteDouble(
        string value)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        return double.IsFinite(parsed);
    }

    private static double ParseFiniteDouble(
        string value)
    {
        if (!TryParseFiniteDouble(value))
        {
            throw new InvalidDataException(
                "XLSX REAL value is not finite or valid.");
        }

        return double.Parse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }

    private static bool TryParseBlob(
        string value)
    {
        if (!value.StartsWith(
                "base64:",
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            Convert.FromBase64String(
                value["base64:".Length..]);

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] ParseBlob(
        string value)
    {
        if (!TryParseBlob(value))
        {
            throw new InvalidDataException(
                "XLSX BLOB value must use the base64: prefix.");
        }

        return Convert.FromBase64String(
            value["base64:".Length..]);
    }

    private static string NormalizeExistingXlsx(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "XLSX source path is required.",
                nameof(path));
        }

        var normalized =
            Path.GetFullPath(
                path.Trim());

        if (!File.Exists(normalized))
        {
            throw new FileNotFoundException(
                "XLSX source file was not found.",
                normalized);
        }

        if (!string.Equals(
                Path.GetExtension(normalized),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Structured Data XLSX import accepts .xlsx files only.");
        }

        return normalized;
    }

    private static string NormalizeDestination(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "XLSX destination path is required.",
                nameof(path));
        }

        var normalized =
            Path.GetFullPath(
                path.Trim());

        if (!string.Equals(
                Path.GetExtension(normalized),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Structured Data XLSX export requires a .xlsx destination.");
        }

        if (Directory.Exists(normalized))
        {
            throw new IOException(
                "XLSX destination points to a directory.");
        }

        return normalized;
    }

    private static bool IsAsciiLetter(
        char character)
        => (
               character >= 'A'
               && character <= 'Z'
           )
           || (
               character >= 'a'
               && character <= 'z'
           );
}
