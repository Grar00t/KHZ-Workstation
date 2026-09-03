using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KHZ.Tools.Safety;
using KHZ.Tools.Tools;

namespace KHZ.Tools.Office;

/// <summary>Reads the textual structure of a DOCX package.</summary>
public sealed class ReadWordDocumentTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "office_read_document",
        Title: "Read Word document",
        Description: "Reads a .docx file and returns its paragraphs (with index and style) and "
                     + "tables. Works directly on the OOXML package: Word does not need to be "
                     + "installed or running. Use the returned sha256 as expected_sha256 when "
                     + "editing, and the paragraph index to target an edit precisely.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative .docx path." },
            "max_paragraphs": { "type": "integer", "description": "1 to 2000. Defaults to 400." },
            "include_tables": { "type": "boolean", "description": "Include table contents. Defaults to true." }
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
            OfficeKind.Word);

        var maxParagraphs = ToolArgs.OptionalInt(arguments, "max_paragraphs", 400, 1, 2000);
        var includeTables = ToolArgs.OptionalBool(arguments, "include_tables", true);

        using var document = WordprocessingDocument.Open(path, isEditable: false);

        var body = document.MainDocumentPart?.Document?.Body
                   ?? throw new ToolFailureException(
                       "invalid_package",
                       "The .docx package has no document body.");

        var paragraphs = new List<object>();
        var index = 0;
        var truncated = false;

        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = ParagraphText(paragraph);
            var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            var currentIndex = index++;

            if (text.Length == 0)
                continue;

            if (paragraphs.Count >= maxParagraphs)
            {
                truncated = true;
                break;
            }

            paragraphs.Add(new
            {
                index = currentIndex,
                style,
                text = OfficeGuard.Cap(text, 4000)
            });
        }

        var tables = new List<object>();

        if (includeTables)
        {
            var tableIndex = 0;

            foreach (var table in body.Descendants<Table>().Take(50))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rows = table
                    .Descendants<TableRow>()
                    .Take(200)
                    .Select(row => row
                        .Descendants<TableCell>()
                        .Take(50)
                        .Select(cell => OfficeGuard.Cap(
                            string.Join(
                                " ",
                                cell.Descendants<Paragraph>().Select(ParagraphText))
                                .Trim(),
                            500))
                        .ToArray())
                    .ToArray();

                tables.Add(new { index = tableIndex++, rowCount = rows.Length, rows });
            }
        }

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(path),
            sha256 = sha,
            paragraphCount = paragraphs.Count,
            truncated,
            paragraphs,
            tableCount = tables.Count,
            tables
        });

        return Task.FromResult(new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new
            {
                pathCaptured = false,
                sha256 = sha,
                paragraphCount = paragraphs.Count,
                tableCount = tables.Count
            }));
    }

    /// <summary>Concatenates the visible text of a paragraph, including tabs and breaks.</summary>
    internal static string ParagraphText(Paragraph paragraph)
    {
        var builder = new StringBuilder();

        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text text:
                    builder.Append(text.Text);
                    break;
                case TabChar:
                    builder.Append('\t');
                    break;
                case Break:
                    builder.Append(' ');
                    break;
            }
        }

        return builder.ToString().Trim();
    }
}

/// <summary>
/// Replaces text inside a DOCX paragraph, guarded by package hash and human
/// confirmation.
/// </summary>
/// <remarks>
/// Scope, stated precisely: a match must lie within a single paragraph. Word
/// splits a sentence across arbitrarily many runs (spell-check state, revision
/// marks, formatting changes), so this tool reads the paragraph's concatenated
/// text and, on a match, rewrites the paragraph as one run that inherits the
/// first run's formatting. Consequence: mixed inline formatting inside the
/// rewritten paragraph is normalised to the first run's formatting. Matches
/// spanning a paragraph boundary are refused rather than silently mishandled.
/// </remarks>
public sealed class EditWordDocumentTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "office_edit_document",
        Title: "Edit Word document text",
        Description: "Replaces text inside a .docx paragraph. Requires expected_sha256 from "
                     + "office_read_document. The match must be inside one paragraph; the "
                     + "paragraph is rewritten as a single run inheriting the first run's "
                     + "formatting, so mixed inline formatting in that paragraph is normalised. "
                     + "Writes atomically and requires user confirmation.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative .docx path." },
            "expected_sha256": { "type": "string", "description": "sha256 from office_read_document." },
            "old_text": { "type": "string", "description": "Text to replace, within a single paragraph." },
            "new_text": { "type": "string", "description": "Replacement text. May be empty." },
            "paragraph_index": { "type": "integer", "description": "Restrict the edit to this paragraph index." },
            "replace_all": { "type": "boolean", "description": "Replace every matching paragraph. Defaults to false, which requires a unique match." }
          },
          "required": ["path", "expected_sha256", "old_text", "new_text"],
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
            OfficeKind.Word);

        OfficeGuard.RequireCurrentHash(
            ToolArgs.RequireString(arguments, "expected_sha256"),
            beforeSha);

        var oldText = ToolArgs.RequireString(arguments, "old_text");
        var newText = ToolArgs.OptionalString(arguments, "new_text") ?? string.Empty;
        var replaceAll = ToolArgs.OptionalBool(arguments, "replace_all", false);
        var targetIndex = ToolArgs.OptionalInt(arguments, "paragraph_index", -1, -1, int.MaxValue);

        if (newText.Length > 20_000)
        {
            throw new ToolFailureException(
                "replacement_too_large",
                "new_text must be 20000 characters or fewer.");
        }

        // Pass 1: locate matches without mutating anything, so the confirmation
        // prompt can state the exact scope of the change.
        var matches = new List<(int Index, string Text)>();

        using (var probe = WordprocessingDocument.Open(path, isEditable: false))
        {
            var body = probe.MainDocumentPart?.Document?.Body
                       ?? throw new ToolFailureException(
                           "invalid_package",
                           "The .docx package has no document body.");

            var index = 0;

            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                var currentIndex = index++;

                if (targetIndex >= 0 && currentIndex != targetIndex)
                    continue;

                var text = ReadWordDocumentTool.ParagraphText(paragraph);

                if (text.Contains(oldText, StringComparison.Ordinal))
                    matches.Add((currentIndex, text));
            }
        }

        if (matches.Count == 0)
        {
            throw new ToolFailureException(
                "old_text_not_found",
                "old_text was not found inside any single paragraph. A match spanning a "
                + "paragraph boundary is not supported; edit one paragraph at a time.");
        }

        if (matches.Count > 1 && !replaceAll)
        {
            throw new ToolFailureException(
                "old_text_not_unique",
                "old_text matches " + matches.Count + " paragraphs (indexes "
                + string.Join(", ", matches.Select(match => match.Index))
                + "). Pass paragraph_index to target one, or replace_all to change all.");
        }

        await ToolRouter.RequireConfirmationAsync(
            context,
            new ConfirmationRequest(
                ToolName: Descriptor.Name,
                Risk: ToolRisk.Write,
                Title: "Edit a Word document",
                Target: context.Relative(path),
                Summary: "Replace text in " + matches.Count + " paragraph(s): index "
                         + string.Join(", ", matches.Select(match => match.Index)) + ".",
                Before: OfficeGuard.Cap(matches[0].Text, 1500),
                After: OfficeGuard.Cap(
                    matches[0].Text.Replace(oldText, newText, StringComparison.Ordinal),
                    1500),
                Warnings: ["Inline formatting inside each edited paragraph is normalised to the paragraph's first run."]),
            cancellationToken).ConfigureAwait(false);

        var replacements = 0;

        var afterSha = AtomicFile.PublishFromCopy(path, working =>
        {
            using var document = WordprocessingDocument.Open(working, isEditable: true);

            var body = document.MainDocumentPart?.Document?.Body
                       ?? throw new ToolFailureException(
                           "invalid_package",
                           "The .docx package has no document body.");

            var index = 0;

            foreach (var paragraph in body.Descendants<Paragraph>().ToList())
            {
                var currentIndex = index++;

                if (!matches.Any(match => match.Index == currentIndex))
                    continue;

                var text = ReadWordDocumentTool.ParagraphText(paragraph);
                var updated = text.Replace(oldText, newText, StringComparison.Ordinal);

                Rewrite(paragraph, updated);
                replacements++;

                if (!replaceAll)
                    break;
            }

            document.MainDocumentPart.Document.Save();
        });

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(path),
            status = "written",
            paragraphsChanged = replacements,
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
                paragraphsChanged = replacements,
                beforeSha256 = beforeSha,
                afterSha256 = afterSha
            });
    }

    /// <summary>
    /// Replaces a paragraph's content with a single run carrying the original
    /// first run's properties, preserving paragraph-level properties.
    /// </summary>
    private static void Rewrite(Paragraph paragraph, string text)
    {
        var template = paragraph
            .Descendants<Run>()
            .FirstOrDefault()?
            .RunProperties?
            .CloneNode(deep: true) as RunProperties;

        var properties = paragraph.ParagraphProperties?.CloneNode(deep: true) as ParagraphProperties;

        paragraph.RemoveAllChildren();

        if (properties is not null)
            paragraph.AppendChild(properties);

        var run = new Run();

        if (template is not null)
            run.AppendChild(template);

        // Space must be preserved explicitly or Word collapses leading and
        // trailing whitespace in the run.
        run.AppendChild(new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
        paragraph.AppendChild(run);
    }
}
