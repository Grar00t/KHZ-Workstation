using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Tools.Safety;

namespace KHZ.Tools.Tools;

/// <summary>
/// Hash-guarded, single-occurrence text replacement in a UTF-8 file.
/// </summary>
/// <remarks>
/// Three guarantees make this safe to expose to a model:
/// <list type="bullet">
/// <item><b>Optimistic concurrency.</b> The caller must supply the SHA-256 it
/// read; if the file changed since, the write is refused as <c>stale_file</c>
/// rather than overwriting the user's newer content.</item>
/// <item><b>Unique anchor.</b> <c>old_text</c> must appear exactly once, so an
/// ambiguous edit cannot land in the wrong place.</item>
/// <item><b>Atomic publish.</b> The new content is written to a temporary
/// sibling and swapped in, so an interrupted write cannot truncate the file.</item>
/// </list>
/// </remarks>
public sealed class ReplaceTextTool : IKhzTool
{
    /// <summary>Maximum characters permitted in the resulting document.</summary>
    public const int MaxEditChars = 200_000;

    public ToolDescriptor Descriptor { get; } = new(
        Name: "replace_text",
        Title: "Replace text in file",
        Description: "Replaces one unique occurrence of old_text with new_text in a UTF-8 text "
                     + "file. Requires expected_sha256 from a prior read_file; a mismatch aborts "
                     + "the write. Requires user confirmation.",
        ParametersJson: """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative file path." },
            "expected_sha256": { "type": "string", "description": "SHA-256 returned by read_file." },
            "old_text": { "type": "string", "description": "Exact text to replace. Must occur exactly once." },
            "new_text": { "type": "string", "description": "Replacement text. May be empty to delete." }
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
        var absolute = context.Resolve(ToolArgs.RequireString(arguments, "path"));
        var expected = ToolArgs.RequireString(arguments, "expected_sha256");
        var oldText = ToolArgs.RequireString(arguments, "old_text");
        var newText = ToolArgs.OptionalString(arguments, "new_text") ?? string.Empty;

        if (!File.Exists(absolute))
        {
            throw new ToolFailureException(
                "file_not_found",
                "File not found: " + context.Relative(absolute));
        }

        if (!ReadLimits.IsTextFile(absolute))
        {
            throw new ToolFailureException(
                "unsupported_file_type",
                "replace_text handles UTF-8 text only. Use office_edit_document for DOCX.");
        }

        var bytes = File.ReadAllBytes(absolute);

        if (bytes.LongLength > ReadLimits.MaxReadBytes)
        {
            throw new ToolFailureException(
                "file_too_large",
                "File exceeds the 4 MiB edit limit.");
        }

        var beforeSha = Hashes.Sha256(bytes);

        if (!Hashes.Matches(expected, beforeSha))
        {
            throw new ToolFailureException(
                "stale_file",
                "The file changed since it was read. Re-read it and retry with the current "
                + "SHA-256. Current hash: " + beforeSha);
        }

        string content;

        try
        {
            content = Hashes.DecodeUtf8(bytes, out var hadBom);
            _ = hadBom;
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new ToolFailureException("not_utf8", "File is not valid UTF-8 text.");
        }

        Hashes.DecodeUtf8(bytes, out var emitBom);

        var first = content.IndexOf(oldText, StringComparison.Ordinal);

        if (first < 0)
        {
            throw new ToolFailureException(
                "old_text_not_found",
                "old_text was not found. It must match the file byte-for-byte, including "
                + "whitespace and line endings.");
        }

        if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
        {
            throw new ToolFailureException(
                "old_text_not_unique",
                "old_text occurs more than once. Extend it with surrounding context until it "
                + "is unique.");
        }

        var updated = string.Concat(
            content.AsSpan(0, first),
            newText,
            content.AsSpan(first + oldText.Length));

        if (updated.Length > MaxEditChars)
        {
            throw new ToolFailureException(
                "replacement_too_large",
                "Resulting file would exceed " + MaxEditChars + " characters.");
        }

        await ToolRouter.RequireConfirmationAsync(
            context,
            new ConfirmationRequest(
                ToolName: Descriptor.Name,
                Risk: ToolRisk.Write,
                Title: "Replace text in a file",
                Target: context.Relative(absolute),
                Summary: "Replace " + oldText.Length + " characters with " + newText.Length
                         + " characters at offset " + first + ".",
                Before: Preview(oldText),
                After: Preview(newText)),
            cancellationToken).ConfigureAwait(false);

        var afterSha = AtomicFile.WriteAllText(absolute, updated, emitBom);

        var json = ToolArgs.Serialize(new
        {
            path = context.Relative(absolute),
            status = "written",
            beforeSha256 = beforeSha,
            sha256 = afterSha,
            charactersRemoved = oldText.Length,
            charactersAdded = newText.Length
        });

        return new JsonNodeResult(
            json,
            Target: "redacted",
            AuditDetails: new
            {
                pathCaptured = false,
                userConfirmed = true,
                aiUsed = true,
                beforeSha256 = beforeSha,
                afterSha256 = afterSha,
                charactersRemoved = oldText.Length,
                charactersAdded = newText.Length
            });
    }

    private static string Preview(string text)
        => text.Length <= 2000 ? text : text[..2000] + "\n... (truncated for display)";
}
