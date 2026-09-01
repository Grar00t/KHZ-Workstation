using System;
using System.IO;
using System.Text;

namespace KHZ.Tools.Safety;

/// <summary>
/// Publish-by-replace writer. Content is written to a temporary sibling, flushed
/// to disk, then swapped in with <see cref="File.Replace(string,string,string?)"/>
/// so a crash mid-write cannot leave a truncated document behind.
/// </summary>
public static class AtomicFile
{
    public static string WriteAllText(string path, string content, bool emitBom)
    {
        var encoding = new UTF8Encoding(emitBom, throwOnInvalidBytes: true);
        return Publish(path, temp => File.WriteAllText(temp, content, encoding));
    }

    public static string WriteAllBytes(string path, byte[] content)
        => Publish(path, temp => File.WriteAllBytes(temp, content));

    /// <summary>
    /// Runs <paramref name="write"/> against a temporary sibling path, replaces
    /// the target on success, and returns the resulting SHA-256.
    /// </summary>
    public static string Publish(string path, Action<string> write)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        ?? throw new IOException("Target directory could not be resolved.");

        var temp = Path.Combine(
            directory,
            Path.GetFileName(path) + ".khz-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            write(temp);

            if (File.Exists(path))
            {
                File.Replace(
                    temp,
                    path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }

            return Hashes.Sha256OfFile(path);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Copies the target to a temporary sibling so an in-place editor (such as
    /// the OpenXML SDK) can mutate a copy that is then published atomically.
    /// </summary>
    public static string PublishFromCopy(string path, Action<string> mutate)
        => Publish(path, temp =>
        {
            File.Copy(path, temp, overwrite: true);
            mutate(temp);
        });

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
