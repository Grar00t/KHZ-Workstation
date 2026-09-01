using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KHZ.Tools.Safety;

/// <summary>
/// The single authority for turning an untrusted, model-supplied relative path
/// into an absolute path that is provably inside the active workspace root.
/// </summary>
/// <remarks>
/// Every filesystem-touching tool MUST route through <see cref="Resolve"/>.
/// The guard enforces four independent rules:
/// <list type="number">
/// <item>absolute and rooted paths are rejected outright;</item>
/// <item>the canonical path must equal the root or start with root + separator;</item>
/// <item>no path segment may be a filesystem reparse point (junction/symlink),
/// checked segment-by-segment so a mid-path junction cannot smuggle the
/// caller outside the root after canonicalisation;</item>
/// <item>the internal <c>.khz</c> metadata directory is never exposed.</item>
/// </list>
/// </remarks>
public static class WorkspaceGuard
{
    /// <summary>Internal metadata directory that is never exposed to tools.</summary>
    public const string InternalMetadataFolder = ".khz";

    private static readonly char[] Separators =
    [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
    ];

    /// <summary>Canonicalises and validates a workspace root.</summary>
    public static string ResolveRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Workspace root is required.", nameof(root));

        var full = Path.GetFullPath(root.Trim())
            .TrimEnd(Separators);

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(
                "Workspace root does not exist: " + full);
        }

        return full;
    }

    /// <summary>
    /// Resolves a model-supplied relative path against <paramref name="root"/>.
    /// </summary>
    /// <exception cref="ToolSecurityException">The path violates a boundary.</exception>
    public static string Resolve(string root, string? relativePath)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Separators);
        var requested = (relativePath ?? string.Empty).Trim();

        if (requested.Length == 0 || requested == ".")
            return canonicalRoot;

        if (Path.IsPathRooted(requested))
        {
            throw new ToolSecurityException(
                "absolute_path_rejected",
                "Tool paths must be relative to the active workspace/folder.");
        }

        // A bare drive-relative form such as "C:foo" is not caught by
        // IsPathRooted on every runtime; reject any volume separator outright.
        if (requested.Contains(':', StringComparison.Ordinal))
        {
            throw new ToolSecurityException(
                "volume_qualified_path_rejected",
                "Tool paths must not contain a volume separator.");
        }

        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, requested));
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;

        if (!string.Equals(candidate, canonicalRoot, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolSecurityException(
                "path_escape_rejected",
                "Tool path escapes the active workspace/folder.");
        }

        var segments = Segments(canonicalRoot, candidate);

        if (segments.Any(segment => string.Equals(
                segment,
                InternalMetadataFolder,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolSecurityException(
                "internal_metadata_rejected",
                "Internal .khz metadata is not exposed to agent tools.");
        }

        RejectReparseTraversal(canonicalRoot, segments);
        return candidate;
    }

    /// <summary>Path relative to the root, for safe echoing back to the model.</summary>
    public static string Relative(string root, string absolutePath)
        => Path.GetRelativePath(
            Path.GetFullPath(root).TrimEnd(Separators),
            absolutePath);

    /// <summary>True when the entry exists and carries the reparse-point attribute.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return false;

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            // An unreadable entry is treated as untrusted.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Depth-first file enumeration that skips reparse points and <c>.khz</c>,
    /// and never throws on an unreadable directory.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] children;

            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsReparsePoint(file))
                    yield return file;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);

                if (string.Equals(name, InternalMetadataFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsReparsePoint(child))
                    pending.Push(child);
            }
        }
    }

    private static string[] Segments(string root, string candidate)
        => Path.GetRelativePath(root, candidate)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static void RejectReparseTraversal(string root, string[] segments)
    {
        var current = root;

        if (IsReparsePoint(current))
        {
            throw new ToolSecurityException(
                "reparse_root_rejected",
                "The workspace root itself is a reparse point.");
        }

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            if (IsReparsePoint(current))
            {
                throw new ToolSecurityException(
                    "reparse_traversal_rejected",
                    "Agent tools do not traverse filesystem reparse points.");
            }
        }
    }
}
