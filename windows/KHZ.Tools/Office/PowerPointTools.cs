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
        var list = presentationPart.Presentation?.SlideIdList;

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
