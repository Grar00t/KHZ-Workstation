using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KHZ.Tools.Safety;

/// <summary>
/// Content identity used by every guarded mutation. A tool that changes a file
/// must state the SHA-256 it believes the file currently has; a mismatch aborts
/// the write instead of silently clobbering concurrent edits.
/// </summary>
public static class Hashes
{
    /// <summary>Strict UTF-8: invalid byte sequences throw instead of degrading.</summary>
    public static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);

        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Case- and whitespace-insensitive comparison of two hex digests.</summary>
    public static bool Matches(string? expected, string actual)
        => string.Equals(
            (expected ?? string.Empty).Trim(),
            actual,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes strict UTF-8 and reports whether a BOM was present.</summary>
    public static string DecodeUtf8(byte[] bytes, out bool hadBom)
    {
        hadBom = bytes.Length >= 3
                 && bytes[0] == 0xEF
                 && bytes[1] == 0xBB
                 && bytes[2] == 0xBF;

        var offset = hadBom ? 3 : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }
}
