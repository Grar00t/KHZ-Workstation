using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using KHZ.Tools.Safety;
using KHZ.Tools.Tools;

namespace KHZ.Tools.Office;

/// <summary>Shared XLSX package navigation.</summary>
internal static class ExcelPackage
{
    /// <summary>Finds a worksheet part by sheet name, or the first sheet when omitted.</summary>
    internal static (WorksheetPart Part, string Name) OpenSheet(
        WorkbookPart workbookPart,
        string? sheetName)
    {
        var sheets = workbookPart.Workbook
            .Descendants<Sheet>()
            .Where(sheet => sheet.Id?.Value is not null)
            .ToList();

        if (sheets.Count == 0)
        {
            throw new ToolFailureException(
                "invalid_package",
                "The workbook contains no worksheets.");
        }

        var sheet = string.IsNullOrWhiteSpace(sheetName)
            ? sheets[0]
            : sheets.FirstOrDefault(candidate => string.Equals(
                candidate.Name?.Value,
                sheetName.Trim(),
                StringComparison.OrdinalIgnoreCase));

        if (sheet is null)
        {
            throw new ToolFailureException(
                "sheet_not_found",
                "Sheet not found: " + sheetName + ". Available: "
                + string.Join(", ", sheets.Select(candidate => candidate.Name?.Value)));
        }

        var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        return (part, sheet.Name?.Value ?? "Sheet1");
    }

    /// <summary>Resolves a cell to display text, resolving shared strings.</summary>
    internal static string? ReadValue(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var raw = cell.CellValue?.InnerText;

            if (raw is null || sharedStrings is null)
                return null;

            return int.TryParse(raw, out var index)
                   && index >= 0
                   && index < sharedStrings.ChildElements.Count
                ? sharedStrings.ChildElements[index].InnerText
                : null;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.Text?.Text ?? cell.InnerText;

        if (cell.DataType?.Value == CellValues.Boolean)
            return cell.CellValue?.InnerText == "1" ? "TRUE" : "FALSE";

        return cell.CellValue?.InnerText;
    }

    /// <summary>Returns the row, creating it in ascending index order if needed.</summary>
    internal static Row GetOrCreateRow(SheetData sheetData, uint rowIndex)
    {
        var existing = sheetData
            .Elements<Row>()
            .FirstOrDefault(row => row.RowIndex?.Value == rowIndex);

        if (existing is not null)
            return existing;

        var created = new Row { RowIndex = rowIndex };

        var successor = sheetData
            .Elements<Row>()
            .FirstOrDefault(row => row.RowIndex?.Value > rowIndex);

        if (successor is null)
            sheetData.AppendChild(created);
        else
            sheetData.InsertBefore(created, successor);

        return created;
    }

    /// <summary>
    /// Returns the cell, creating it in ascending column order if needed.
    /// </summary>
    /// <remarks>
    /// Column order matters: Excel treats a row whose cells are out of
    /// reference order as a corrupt package, so insertion position is computed
    /// from parsed column indexes rather than appended blindly.
    /// </remarks>
    internal static Cell GetOrCreateCell(Row row, CellAddress address)
    {
        var reference = address.ToString();

        var existing = row
            .Elements<Cell>()
            .FirstOrDefault(cell => string.Equals(
                cell.CellReference?.Value,
                reference,
                StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return existing;

        var created = new Cell { CellReference = reference };

        var successor = row
            .Elements<Cell>()
            .FirstOrDefault(cell =>
                CellAddress.TryParse(cell.CellReference?.Value, out var candidate)
                && candidate.Column > address.Column);

        if (successor is null)
            row.AppendChild(created);
        else
            row.InsertBefore(created, successor);

        return created;
    }

    /// <summary>
    /// Marks the workbook for full recalculation on open.
    /// </summary>
    /// <remarks>
    /// The SDK writes formula text but computes no values, so without this flag
    /// a newly written formula would display a stale or empty cached result.
    /// </remarks>
    internal static void ForceRecalculation(WorkbookPart workbookPart)
    {
        var properties = workbookPart.Workbook.GetFirstChild<CalculationProperties>();

        if (properties is null)
        {
            properties = new CalculationProperties();
            workbookPart.Workbook.AppendChild(properties);
        }

        properties.FullCalculationOnLoad = BooleanValue.FromBoolean(true);

        // A stale calculation chain can override the recalculation request.
        if (workbookPart.CalculationChainPart is not null)
            workbookPart.DeletePart(workbookPart.CalculationChainPart);
    }
}

/// <summary>Reads cells from an XLSX worksheet.</summary>
public sealed class ReadSheetTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "office_read_sheet",
        Title: "Read Excel worksheet",
        Description: "Reads cells from a .xlsx worksheet and returns A1-addressed values with "
                     + "formula text where present. Works directly on the OOXML package: Excel "
                     + "does not need to be installed. Use the returned sha256 as "
                     + "expected_sha256 when writing.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative .xlsx path." },
            "sheet": { "type": "string", "description": "Sheet name. Defaults to the first sheet." },
            "max_cells": { "type": "integer", "description": "1 to 5000. Defaults to 1500." }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """,
        Risk: ToolRisk.Read,
        RequiresConfirmation: false);

    public Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var (path, sha) = OfficeGuard.Require(
            context,
            ToolArgs.RequireString(arguments, "path"),
            OfficeKind.Excel);

        var maxCells = ToolArgs.OptionalInt(arguments, "max_cells", 1500, 1, 5000);

        using var document = SpreadsheetDocument.Open(path, isEditable: false);

        var workbookPart = document.WorkbookPart
                           ?? throw new ToolFailureException(
                               "invalid_package",
                               "The .xlsx package has no workbook part.");

        var (part, name) = ExcelPackage.OpenSheet(
            workbookPart,
            ToolArgs.OptionalString(arguments, "sheet"));

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var sheetData = part.Worksheet.GetFirstChild<SheetData>();

        var cells = new List<object>();
        var truncated = false;
        var maxRow = 0;
        var maxColumn = 0;

        foreach (var row in sheetData?.Elements<Row>() ?? Enumerable.Empty<Row>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var cell in row.Elements<Cell>())
            {
                var reference = cell.CellReference?.Value;

                if (reference is null || !CellAddress.TryParse(reference, out var address))
                    continue;

                var value = ExcelPackage.ReadValue(cell, sharedStrings);
                var formula = cell.CellFormula?.Text;

                if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(formula))
                    continue;

                maxRow = Math.Max(maxRow, address.Row);
                maxColumn = Math.Max(maxColumn, address.Column);

                if (cells.Count >= maxCells)
                {
                    truncated = true;
                    break;
                }

                cells.Add(new
                {
                    reference = address.ToString(),
                    row = address.Row,
                    column = address.Column,
                    value = value is null ? null : OfficeGuard.Cap(value, 1000),
                    formula = formula is null ? null : "=" + OfficeGuard.Cap(formula, 500)
                });
            }

            if (truncated)
                break;
        }

        var sheetNames = workbookPart.Workbook
            .Descendants<Sheet>()
            .Select(sheet => sheet.Name?.Value)
            .Where(value => value is not null)
            .ToArray();

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(path),
            sha256 = sha,
            sheet = name,
            sheets = sheetNames,
            usedRange = cells.Count == 0
                ? null
                : "A1:" + new CellAddress(maxColumn, maxRow),
            cellCount = cells.Count,
            truncated,
            cells
        });

        return Task.FromResult(new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new
            {
                pathCaptured = false,
                sha256 = sha,
                sheetCount = sheetNames.Length,
                cellCount = cells.Count
            }));
    }
}

/// <summary>Writes values or formulas into an XLSX worksheet.</summary>
public sealed class WriteCellsTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "office_write_cells",
        Title: "Write Excel cells",
        Description: "Writes values or formulas into a .xlsx worksheet. A value starting with "
                     + "'=' is stored as a formula and the workbook is marked for full "
                     + "recalculation on open. Numeric-looking values are stored as numbers. "
                     + "Requires expected_sha256 from office_read_sheet, writes atomically, and "
                     + "requires user confirmation. Does not change cell styles or number formats.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative .xlsx path." },
            "expected_sha256": { "type": "string", "description": "sha256 from office_read_sheet." },
            "sheet": { "type": "string", "description": "Sheet name. Defaults to the first sheet." },
            "cells": {
              "type": "array",
              "description": "Up to 500 cell writes.",
              "items": {
                "type": "object",
                "properties": {
                  "reference": { "type": "string", "description": "A1 reference, for example B7." },
                  "value": { "type": "string", "description": "Text, number, or formula starting with '='. Empty string clears the cell." }
                },
                "required": ["reference", "value"],
                "additionalProperties": false
              }
            }
          },
          "required": ["path", "expected_sha256", "cells"],
          "additionalProperties": false
        }
        """,
        Risk: ToolRisk.Write,
        RequiresConfirmation: true);

    public async Task<JsonNodeResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var (path, beforeSha) = OfficeGuard.Require(
            context,
            ToolArgs.RequireString(arguments, "path"),
            OfficeKind.Excel);

        OfficeGuard.RequireCurrentHash(
            ToolArgs.RequireString(arguments, "expected_sha256"),
            beforeSha);

        var sheetName = ToolArgs.OptionalString(arguments, "sheet");

        if (!arguments.TryGetProperty("cells", out var cellsElement)
            || cellsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ToolFailureException(
                "missing_argument",
                "Argument 'cells' must be an array of { reference, value } objects.");
        }

        var writes = new List<(CellAddress Address, string Value)>();

        foreach (var entry in cellsElement.EnumerateArray())
        {
            var reference = ToolArgs.RequireString(entry, "reference");
            var value = ToolArgs.OptionalString(entry, "value") ?? string.Empty;

            if (value.Length > 4000)
            {
                throw new ToolFailureException(
                    "value_too_large",
                    "Cell " + reference + " exceeds the 4000 character limit.");
            }

            writes.Add((CellAddress.Parse(reference), value));
        }

        if (writes.Count is 0 or > 500)
        {
            throw new ToolFailureException(
                "invalid_argument",
                "Provide between 1 and 500 cell writes.");
        }

        var preview = string.Join(
            "\n",
            writes.Take(20).Select(write => write.Address + " = " + OfficeGuard.Cap(write.Value, 120)));

        await ToolRouter.RequireConfirmationAsync(
            context,
            new ConfirmationRequest(
                ToolName: Descriptor.Name,
                Risk: ToolRisk.Write,
                Title: "Write " + writes.Count + " cell(s) in a workbook",
                Target: context.Relative(path) + (sheetName is null ? string.Empty : " [" + sheetName + "]"),
                Summary: "Overwrite " + writes.Count + " cell(s). Existing values are replaced.",
                After: preview,
                Warnings: writes.Any(write => write.Value.StartsWith("=", StringComparison.Ordinal))
                    ? ["Includes formulas; the workbook will recalculate when opened."]
                    : null),
            cancellationToken).ConfigureAwait(false);

        var formulaCount = 0;

        var afterSha = AtomicFile.PublishFromCopy(path, working =>
        {
            using var document = SpreadsheetDocument.Open(working, isEditable: true);

            var workbookPart = document.WorkbookPart
                               ?? throw new ToolFailureException(
                                   "invalid_package",
                                   "The .xlsx package has no workbook part.");

            var (part, _) = ExcelPackage.OpenSheet(workbookPart, sheetName);
            var worksheet = part.Worksheet;
            var sheetData = worksheet.GetFirstChild<SheetData>();

            if (sheetData is null)
            {
                sheetData = new SheetData();
                worksheet.AppendChild(sheetData);
            }

            foreach (var (address, value) in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = ExcelPackage.GetOrCreateRow(sheetData, (uint)address.Row);
                var cell = ExcelPackage.GetOrCreateCell(row, address);

                cell.CellFormula = null;
                cell.InlineString = null;

                if (value.Length == 0)
                {
                    cell.CellValue = null;
                    cell.DataType = null;
                    continue;
                }

                if (value.StartsWith("=", StringComparison.Ordinal))
                {
                    cell.CellFormula = new CellFormula(value[1..]);
                    cell.CellValue = null;
                    cell.DataType = null;
                    formulaCount++;
                    continue;
                }

                if (double.TryParse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number))
                {
                    cell.CellValue = new CellValue(
                        number.ToString(CultureInfo.InvariantCulture));
                    cell.DataType = null;
                    continue;
                }

                // Inline strings keep the write self-contained: no shared-string
                // table surgery, so concurrent structure stays valid.
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(value));
            }

            if (formulaCount > 0)
                ExcelPackage.ForceRecalculation(workbookPart);

            worksheet.Save();
            workbookPart.Workbook.Save();
        });

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(path),
            status = "written",
            cellsWritten = writes.Count,
            formulasWritten = formulaCount,
            beforeSha256 = beforeSha,
            sha256 = afterSha
        });

        return new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new
            {
                pathCaptured = false,
                userConfirmed = true,
                aiUsed = true,
                cellsWritten = writes.Count,
                formulasWritten = formulaCount,
                beforeSha256 = beforeSha,
                afterSha256 = afterSha
            });
    }
}
