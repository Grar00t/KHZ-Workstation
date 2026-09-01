using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using KHZ.Tools.Safety;
using KHZ.Tools.Tools;
using A = DocumentFormat.OpenXml.Drawing;

namespace KHZ.Tools.Office;

/// <summary>Shared PPTX navigation.</summary>
internal static class SlidePackage
{
    /// <summary>Slide parts in presentation order.</summary>
    internal static List<SlidePart> Slides(PresentationPart presentationPart)
    {
        var list = presentationPart.Presentation?.SldIdList;

        if (list is null)
            return [];

        var parts = new List<SlidePart>();

        foreach (var slideId in list.ChildElements.OfType<SlideId>())
        {
            var relationship = slideId.RelationshipId?.Value;

            if (relationship is null)
                continue;

            if (presentationPart.GetPartById(relationship) is SlidePart part)
                parts.Add(part);
        }

        return parts;
    }

    /// <summary>Concatenated visible text of a drawing paragraph.</summary>
    internal static string ParagraphText(A.Paragraph paragraph)
        => string.Concat(paragraph.Descendants<A.Text>().Select(text => text.Text));

    /// <summary>
    /// Rewrites a drawing paragraph as a single run inheriting the first run's
    /// properties, preserving paragraph properties.
    /// </summary>
    internal static void Rewrite(A.Paragraph paragraph, string text)
    {
        var template = paragraph
            .Descendants<A.Run>()
            .FirstOrDefault()?
            .RunProperties?
            .CloneNode(deep: true) as A.RunProperties;

        var properties = paragraph.ParagraphProperties?.CloneNode(deep: true) as A.ParagraphProperties;

        paragraph.RemoveAllChildren();

        if (properties is not null)
            paragraph.AppendChild(properties);

        var run = new A.Run();

        if (template is not null)
            run.AppendChild(template);

        run.AppendChild(new A.Text(text));
        paragraph.AppendChild(run);
    }
}

/// <summary>Reads slide text from a PPTX package.</summary>
public sealed class ReadSlidesTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "office_read_slides",
        Title: "Read PowerPoint slides",
        Description: "Reads a .pptx file and returns per-slide shape text with slide and shape "
                     + "indexes. Works directly on the OOXML package: PowerPoint does not need "
                     + "to be installed. Use the returned sha256 as expected_sha256 when writing.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative .pptx path." },
            "max_slides": { "type": "integer", "description": "1 to 300. Defaults to 100." }
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
            OfficeKind.PowerPoint);

        var maxSlides = ToolArgs.OptionalInt(arguments, "max_slides", 100, 1, 300);

        using var document = PresentationDocument.Open(path, isEditable: false);

        var presentationPart = document.PresentationPart
                               ?? throw new ToolFailureException(
                                   "invalid_package",
                                   "The .pptx package has no presentation part.");

        var slideParts = SlidePackage.Slides(presentationPart);
        var slides = new List<object>();

        for (var slideIndex = 0; slideIndex < slideParts.Count && slideIndex < maxSlides; slideIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var shapes = new List<object>();
            var shapeIndex = 0;

            foreach (var shape in slideParts[slideIndex].Slide.Descendants<Shape>())
            {
                var currentShape = shapeIndex++;

                var text = string.Join(
                    "\n",
                    shape.Descendants<A.Paragraph>()
                        .Select(SlidePackage.ParagraphText)
                        .Where(value => value.Length > 0));

                if (text.Length == 0)
                    continue;

                shapes.Add(new
                {
                    index = currentShape,
                    name = shape.NonVisualShapeProperties?
                        .NonVisualDrawingProperties?
                        .Name?.Value,
                    text = OfficeGuard.Cap(text, 3000)
                });
            }

            slides.Add(new { index = slideIndex, shapeCount = shapes.Count, shapes });
        }

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(path),
            sha256 = sha,
            slideCount = slideParts.Count,
            returnedSlides = slides.Count,
            truncated = slideParts.Count > slides.Count,
            slides
        });

        return Task.FromResult(new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new { pathCaptured = false, sha256 = sha, slideCount = slideParts.Count }));
    }
}

/// <summary>Replaces text inside a PPTX shape paragraph.</summary>
public sealed class WriteSlideTextTool : IKhzTool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "office_write_slide_text",
        Title: "Edit PowerPoint slide text",
        Description: "Replaces text inside .pptx slide shapes. Requires expected_sha256 from "
                     + "office_read_slides. The match must lie inside one shape paragraph, which "
                     + "is rewritten as a single run inheriting the first run's formatting, so "
                     + "mixed inline formatting in that paragraph is normalised. Writes "
                     + "atomically and requires user confirmation.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative .pptx path." },
            "expected_sha256": { "type": "string", "description": "sha256 from office_read_slides." },
            "old_text": { "type": "string", "description": "Text to replace, within a single shape paragraph." },
            "new_text": { "type": "string", "description": "Replacement text. May be empty." },
            "slide_index": { "type": "integer", "description": "Restrict the edit to this slide index." },
            "replace_all": { "type": "boolean", "description": "Replace every match. Defaults to false, which requires a unique match." }
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
            OfficeKind.PowerPoint);

        OfficeGuard.RequireCurrentHash(
            ToolArgs.RequireString(arguments, "expected_sha256"),
            beforeSha);

        var oldText = ToolArgs.RequireString(arguments, "old_text");
        var newText = ToolArgs.OptionalString(arguments, "new_text") ?? string.Empty;
        var replaceAll = ToolArgs.OptionalBool(arguments, "replace_all", false);
        var targetSlide = ToolArgs.OptionalInt(arguments, "slide_index", -1, -1, int.MaxValue);

        if (newText.Length > 10_000)
        {
            throw new ToolFailureException(
                "replacement_too_large",
                "new_text must be 10000 characters or fewer.");
        }

        var matches = new List<(int Slide, string Text)>();

        using (var probe = PresentationDocument.Open(path, isEditable: false))
        {
            var presentationPart = probe.PresentationPart
                                   ?? throw new ToolFailureException(
                                       "invalid_package",
                                       "The .pptx package has no presentation part.");

            var slideParts = SlidePackage.Slides(presentationPart);

            for (var index = 0; index < slideParts.Count; index++)
            {
                if (targetSlide >= 0 && index != targetSlide)
                    continue;

                foreach (var paragraph in slideParts[index].Slide.Descendants<A.Paragraph>())
                {
                    var text = SlidePackage.ParagraphText(paragraph);

                    if (text.Contains(oldText, StringComparison.Ordinal))
                        matches.Add((index, text));
                }
            }
        }

        if (matches.Count == 0)
        {
            throw new ToolFailureException(
                "old_text_not_found",
                "old_text was not found inside any single shape paragraph.");
        }

        if (matches.Count > 1 && !replaceAll)
        {
            throw new ToolFailureException(
                "old_text_not_unique",
                "old_text matches " + matches.Count + " paragraphs on slides "
                + string.Join(", ", matches.Select(match => match.Slide).Distinct())
                + ". Pass slide_index to narrow it, or replace_all to change all.");
        }

        await ToolRouter.RequireConfirmationAsync(
            context,
            new ConfirmationRequest(
                ToolName: Descriptor.Name,
                Risk: ToolRisk.Write,
                Title: "Edit slide text",
                Target: context.Relative(path),
                Summary: "Replace text in " + matches.Count + " paragraph(s) on slide(s) "
                         + string.Join(", ", matches.Select(match => match.Slide).Distinct()) + ".",
                Before: OfficeGuard.Cap(matches[0].Text, 1000),
                After: OfficeGuard.Cap(
                    matches[0].Text.Replace(oldText, newText, StringComparison.Ordinal),
                    1000),
                Warnings: ["Inline formatting inside each edited paragraph is normalised to its first run."]),
            cancellationToken).ConfigureAwait(false);

        var replacements = 0;

        var afterSha = AtomicFile.PublishFromCopy(path, working =>
        {
            using var document = PresentationDocument.Open(working, isEditable: true);

            var presentationPart = document.PresentationPart
                                   ?? throw new ToolFailureException(
                                       "invalid_package",
                                       "The .pptx package has no presentation part.");

            var slideParts = SlidePackage.Slides(presentationPart);

            for (var index = 0; index < slideParts.Count; index++)
            {
                if (targetSlide >= 0 && index != targetSlide)
                    continue;

                var changedOnSlide = false;

                foreach (var paragraph in slideParts[index].Slide.Descendants<A.Paragraph>().ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var text = SlidePackage.ParagraphText(paragraph);

                    if (!text.Contains(oldText, StringComparison.Ordinal))
                        continue;

                    SlidePackage.Rewrite(
                        paragraph,
                        text.Replace(oldText, newText, StringComparison.Ordinal));

                    replacements++;
                    changedOnSlide = true;

                    if (!replaceAll)
                        break;
                }

                if (changedOnSlide)
                {
                    slideParts[index].Slide.Save();

                    if (!replaceAll)
                        break;
                }
            }
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
}
