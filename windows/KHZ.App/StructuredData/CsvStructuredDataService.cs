using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KHZ.App.StructuredData;

internal sealed class CsvStructuredDataService
{
    private const int MaximumColumns = 256;
    private const int MaximumImportRows = 5000;
    private const long MaximumSourceBytes =
        32L * 1024L * 1024L;

    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    private static readonly UTF8Encoding Utf8WithBom =
        new(
            encoderShouldEmitUTF8Identifier: true,
            throwOnInvalidBytes: true);

    private readonly IWorkspaceDataStore _store;

    public CsvStructuredDataService(
        IWorkspaceDataStore store)
    {
        ArgumentNullException.ThrowIfNull(
            store);

        _store = store;
    }

    public string ImportCsv(
        string sourcePath,
        string? tableName = null)
    {
        var normalizedSource =
            NormalizeExistingFile(
                sourcePath);

        var file =
            new FileInfo(
                normalizedSource);

        if (file.Length > MaximumSourceBytes)
        {
            throw new InvalidDataException(
                $"CSV source exceeds the {MaximumSourceBytes} byte import limit.");
        }

        var bytes =
            File.ReadAllBytes(
                normalizedSource);

        var offset =
            HasUtf8Bom(bytes)
                ? 3
                : 0;

        string text;

        try
        {
            text =
                StrictUtf8.GetString(
                    bytes,
                    offset,
                    bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "CSV source must be valid UTF-8.",
                ex);
        }

        var records =
            ParseCsv(
                text);

        if (records.Count == 0)
        {
            throw new InvalidDataException(
                "CSV contains no header row.");
        }

        var rawHeaders =
            records[0];

        if (rawHeaders.Length == 0)
        {
            throw new InvalidDataException(
                "CSV header row is empty.");
        }

        if (rawHeaders.Length > MaximumColumns)
        {
            throw new InvalidDataException(
                $"CSV contains more than {MaximumColumns} columns.");
        }

        var headers =
            NormalizeHeaders(
                rawHeaders);

        var sourceRows =
            records
                .Skip(1)
                .ToList();

        if (sourceRows.Count > MaximumImportRows)
        {
            throw new InvalidDataException(
                $"CSV contains more than {MaximumImportRows} data rows.");
        }

        var normalizedRows =
            new List<string?[]>(
                sourceRows.Count);

        foreach (var sourceRow in sourceRows)
        {
            if (sourceRow.Length
                > headers.Count)
            {
                throw new InvalidDataException(
                    "CSV data row contains more fields than the header row.");
            }

            var row =
                new string?[headers.Count];

            for (var i = 0;
                 i < sourceRow.Length;
                 i++)
            {
                row[i] =
                    sourceRow[i];
            }

            normalizedRows.Add(
                row);
        }

        var columns =
            new List<DataColumnDefinition>(
                headers.Count);

        for (var columnIndex = 0;
             columnIndex < headers.Count;
             columnIndex++)
        {
            columns.Add(
                new DataColumnDefinition(
                    headers[columnIndex],
                    InferType(
                        normalizedRows.Select(
                            row =>
                                row[columnIndex]))));
        }

        var convertedRows =
            new List<
                IReadOnlyDictionary<string, object?>>(
                    normalizedRows.Count);

        foreach (var row in normalizedRows)
        {
            var converted =
                new Dictionary<string, object?>(
                    StringComparer.OrdinalIgnoreCase);

            for (var i = 0;
                 i < headers.Count;
                 i++)
            {
                var raw =
                    row[i];

                if (string.IsNullOrEmpty(
                        raw))
                {
                    continue;
                }

                converted[headers[i]] =
                    ConvertCell(
                        raw,
                        columns[i].Type);
            }

            convertedRows.Add(
                converted);
        }

        var effectiveTableName =
            NormalizeIdentifier(
                string.IsNullOrWhiteSpace(
                    tableName)
                    ? Path.GetFileNameWithoutExtension(
                        normalizedSource)
                    : tableName,
                fallback: "Imported");

        return _store.CreateTableWithRows(
            effectiveTableName,
            columns,
            convertedRows);
    }

    public string ExportCsv(
        string tableId,
        string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(
                destinationPath))
        {
            throw new ArgumentException(
                "CSV destination path is required.",
                nameof(destinationPath));
        }

        var normalizedDestination =
            Path.GetFullPath(
                destinationPath.Trim());

        if (Directory.Exists(
                normalizedDestination))
        {
            throw new IOException(
                "CSV destination points to a directory.");
        }

        var parent =
            Path.GetDirectoryName(
                normalizedDestination)
            ?? throw new InvalidOperationException(
                "CSV destination has no parent directory.");

        Directory.CreateDirectory(
            parent);

        var rowCount =
            _store.CountRows(
                tableId);

        if (rowCount > MaximumImportRows)
        {
            throw new InvalidOperationException(
                $"CSV export refuses to truncate {rowCount} rows at the {MaximumImportRows} row query boundary.");
        }

        var result =
            _store.Query(
                tableId,
                limit:
                    rowCount == 0
                        ? 1
                        : checked((int)rowCount));

        if (result.Rows.Count
            != rowCount)
        {
            throw new InvalidDataException(
                "CSV export row-count verification failed.");
        }

        var temporaryPath =
            normalizedDestination
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
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        options:
                            FileOptions.WriteThrough))
            {
                using (
                    var writer =
                        new StreamWriter(
                            stream,
                            Utf8WithBom,
                            bufferSize: 4096,
                            leaveOpen: true))
                {
                    writer.NewLine =
                        "\r\n";

                    WriteCsvRecord(
                        writer,
                        result.Columns);

                    foreach (var row
                             in result.Rows)
                    {
                        WriteCsvRecord(
                            writer,
                            result.Columns.Select(
                                column =>
                                    FormatCsvValue(
                                        row[column])));
                    }

                    writer.Flush();
                }

                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                temporaryPath,
                normalizedDestination,
                overwrite: true);

            return normalizedDestination;
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

    private static List<string[]> ParseCsv(
        string text)
    {
        var utf8 =
            StrictUtf8.GetBytes(
                text);

        using var stream =
            new MemoryStream(
                utf8,
                writable: false);

        using var parser =
            new TextFieldParser(
                stream,
                StrictUtf8,
                detectEncoding: false)
            {
                TextFieldType =
                    FieldType.Delimited,

                HasFieldsEnclosedInQuotes =
                    true,

                TrimWhiteSpace =
                    false
            };

        parser.SetDelimiters(
            ",");

        var records =
            new List<string[]>();

        try
        {
            while (!parser.EndOfData)
            {
                var fields =
                    parser.ReadFields();

                if (fields is not null)
                {
                    records.Add(
                        fields);
                }
            }
        }
        catch (MalformedLineException ex)
        {
            throw new InvalidDataException(
                $"Malformed CSV record near line {ex.LineNumber}.",
                ex);
        }

        return records;
    }

    private static IReadOnlyList<string> NormalizeHeaders(
        IReadOnlyList<string> rawHeaders)
    {
        var result =
            new List<string>(
                rawHeaders.Count);

        var used =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        for (var i = 0;
             i < rawHeaders.Count;
             i++)
        {
            var candidate =
                NormalizeIdentifier(
                    rawHeaders[i],
                    $"Column_{i + 1}");

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
        if (!used.Contains(
                candidate))
        {
            return candidate;
        }

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

            var unique =
                candidate[..prefixLength]
                + suffix;

            if (!used.Contains(
                    unique))
            {
                return unique;
            }
        }

        throw new InvalidOperationException(
            "Unable to generate a unique CSV column name.");
    }

    private static string NormalizeIdentifier(
        string? raw,
        string fallback)
    {
        var source =
            string.IsNullOrWhiteSpace(
                raw)
                ? fallback
                : raw.Trim();

        var builder =
            new StringBuilder();

        foreach (var character
                 in source)
        {
            var valid =
                IsAsciiLetter(
                    character)
                || (character >= '0'
                    && character <= '9')
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
        {
            candidate =
                fallback;
        }

        if (!IsAsciiLetter(
                candidate[0]))
        {
            candidate =
                "Column_"
                + candidate;
        }

        if (candidate.Length > 63)
        {
            candidate =
                candidate[..63];
        }

        return candidate;
    }

    private static StructuredDataType InferType(
        IEnumerable<string?> values)
    {
        var present =
            values
                .Where(
                    value =>
                        !string.IsNullOrEmpty(
                            value))
                .Cast<string>()
                .ToList();

        if (present.Count == 0)
        {
            return StructuredDataType.Text;
        }

        if (present.All(
                TryParseBlob))
        {
            return StructuredDataType.Blob;
        }

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
                ParseFiniteDouble(
                    value),

            StructuredDataType.Blob =>
                ParseBlob(
                    value),

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

        return double.IsFinite(
            parsed);
    }

    private static double ParseFiniteDouble(
        string value)
    {
        if (!TryParseFiniteDouble(
                value))
        {
            throw new InvalidDataException(
                "CSV REAL value is not finite or valid.");
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
        if (!TryParseBlob(
                value))
        {
            throw new InvalidDataException(
                "CSV BLOB value must use the base64: prefix.");
        }

        return Convert.FromBase64String(
            value["base64:".Length..]);
    }

    private static void WriteCsvRecord(
        TextWriter writer,
        IEnumerable<string> values)
    {
        var first =
            true;

        foreach (var value
                 in values)
        {
            if (!first)
            {
                writer.Write(
                    ',');
            }

            writer.Write(
                EscapeCsv(
                    value));

            first =
                false;
        }

        writer.WriteLine();
    }

    private static string EscapeCsv(
        string value)
    {
        var requiresQuotes =
            value.Contains(
                ',')
            || value.Contains(
                '"')
            || value.Contains(
                '\r')
            || value.Contains(
                '\n')
            || (
                value.Length > 0
                && (
                    char.IsWhiteSpace(
                        value[0])
                    || char.IsWhiteSpace(
                        value[^1])
                )
            );

        if (!requiresQuotes)
        {
            return value;
        }

        return "\""
               + value.Replace(
                   "\"",
                   "\"\"",
                   StringComparison.Ordinal)
               + "\"";
    }

    private static string FormatCsvValue(
        object? value)
        => value switch
        {
            null =>
                "",

            byte[] bytes =>
                "base64:"
                + Convert.ToBase64String(
                    bytes),

            double number =>
                number.ToString(
                    "R",
                    CultureInfo.InvariantCulture),

            float number =>
                number.ToString(
                    "R",
                    CultureInfo.InvariantCulture),

            decimal number =>
                number.ToString(
                    CultureInfo.InvariantCulture),

            IFormattable formattable =>
                formattable.ToString(
                    null,
                    CultureInfo.InvariantCulture)
                ?? "",

            _ =>
                Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture)
                ?? ""
        };

    private static string NormalizeExistingFile(
        string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            throw new ArgumentException(
                "CSV source path is required.",
                nameof(path));
        }

        var normalized =
            Path.GetFullPath(
                path.Trim());

        if (!File.Exists(
                normalized))
        {
            throw new FileNotFoundException(
                "CSV source file was not found.",
                normalized);
        }

        return normalized;
    }

    private static bool HasUtf8Bom(
        IReadOnlyList<byte> bytes)
        => bytes.Count >= 3
           && bytes[0] == 0xEF
           && bytes[1] == 0xBB
           && bytes[2] == 0xBF;

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
