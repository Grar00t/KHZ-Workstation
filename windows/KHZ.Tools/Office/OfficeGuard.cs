using System;
using System.IO;
using KHZ.Tools.Safety;
using KHZ.Tools.Tools;

namespace KHZ.Tools.Office;

/// <summary>Office package kinds the agent can address.</summary>
public enum OfficeKind
{
    Word,
    Excel,
    PowerPoint
}

/// <summary>
/// Shared preconditions for Office tools: extension checks, size budget, and
/// SHA-256 optimistic concurrency over the whole package.
/// </summary>
/// <remarks>
/// Office packages are binary ZIP containers, so identity is the hash of the
/// file bytes, not of decoded text. That is why <see cref="Hashes.Sha256OfFile"/>
/// is used here rather than the UTF-8 path used by the text tools.
/// </remarks>
public static class OfficeGuard
{
    /// <summary>Maximum package size the agent will open.</summary>
    public const long MaxPackageBytes = 64L * 1024 * 1024;

    public static string Extension(OfficeKind kind) => kind switch
    {
        OfficeKind.Word => ".docx",
        OfficeKind.Excel => ".xlsx",
        OfficeKind.PowerPoint => ".pptx",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Resolves, validates, and returns the package path with its hash.</summary>
    public static (string Path, string Sha256) Require(
        ToolContext context,
        string relativePath,
        OfficeKind kind)
    {
        var absolute = context.Resolve(relativePath);
        var expectedExtension = Extension(kind);
        var actualExtension = Path.GetExtension(absolute);

        if (!string.Equals(actualExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolFailureException(
                "unsupported_file_type",
                "This tool requires a " + expectedExtension + " file. Legacy .doc, .xls, and "
                + ".ppt formats are not OOXML packages and are not supported; convert them "
                + "first. Received: " + actualExtension);
        }

        if (!File.Exists(absolute))
        {
            throw new ToolFailureException(
                "file_not_found",
                "File not found: " + context.Relative(absolute));
        }

        var info = new FileInfo(absolute);

        if (info.Length > MaxPackageBytes)
        {
            throw new ToolFailureException(
                "file_too_large",
                "Package exceeds the 64 MiB limit (" + info.Length + " bytes).");
        }

        return (absolute, Hashes.Sha256OfFile(absolute));
    }

    /// <summary>Enforces optimistic concurrency before any mutation.</summary>
    public static void RequireCurrentHash(string expected, string actual)
    {
        if (Hashes.Matches(expected, actual))
            return;

        throw new ToolFailureException(
            "stale_file",
            "The document changed since it was read. Re-read it and retry with the current "
            + "SHA-256. Current hash: " + actual);
    }

    /// <summary>Truncates a value for inclusion in a model-facing payload.</summary>
    public static string Cap(string value, int limit)
        => value.Length <= limit ? value : value[..limit] + "...";
}
