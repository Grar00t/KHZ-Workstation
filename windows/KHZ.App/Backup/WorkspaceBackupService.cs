using KHZ.App.Workspaces;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KHZ.App.Backup;

internal sealed class WorkspaceBackupException : IOException
{
    public WorkspaceBackupException(
        string message)
        : base(message)
    {
    }

    public WorkspaceBackupException(
        string message,
        Exception innerException)
        : base(
            message,
            innerException)
    {
    }
}

internal sealed class WorkspaceBackupManifest
{
    [JsonPropertyName("format")]
    public string Format { get; init; } =
        string.Empty;

    [JsonPropertyName("workspace_id")]
    public string WorkspaceId { get; init; } =
        string.Empty;

    [JsonPropertyName("created_utc")]
    public string CreatedUtc { get; init; } =
        string.Empty;

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files
    {
        get;
        init;
    } = new(
        StringComparer.Ordinal);
}

internal sealed record WorkspaceRestoreResult(
    string RestoredPath,
    string? PreservedPath,
    string WorkspaceId);

internal sealed class WorkspaceBackupService
{
    internal const string FormatId =
        "KHZ-WORKSPACE-BACKUP-V1";

    internal const string ManifestName =
        "KHZ-BACKUP-MANIFEST.json";

    private const int MaximumManifestBytes =
        8 * 1024 * 1024;

    private const int MaximumEntryCount =
        100_000;

    private const int CopyBufferSize =
        128 * 1024;

    private readonly WorkspaceContext _workspace;

    private readonly string _root;

    public WorkspaceBackupService(
        WorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(
            workspace);

        _workspace =
            workspace;

        _root =
            Path.GetFullPath(
                workspace.Info.Root);

        if (!Directory.Exists(
                _root))
        {
            throw new DirectoryNotFoundException(
                $"Workspace root does not exist: {_root}");
        }
    }

    public string Create(
        string destinationPath)
    {
        var destination =
            NormalizeDestination(
                destinationPath);

        ValidateDestinationBoundary(
            destination);

        var parent =
            Path.GetDirectoryName(
                destination)
            ?? throw new WorkspaceBackupException(
                "Backup destination has no parent directory.");

        Directory.CreateDirectory(
            parent);

        var temporaryArchive =
            destination
            + ".tmp-"
            + Guid.NewGuid()
                .ToString("N");

        var snapshotDirectory =
            Path.Combine(
                Path.GetTempPath(),
                "khz-backup-snapshot-"
                + Guid.NewGuid()
                    .ToString("N"));

        Directory.CreateDirectory(
            snapshotDirectory);

        try
        {
            string? metadataSnapshot =
                null;

            if (File.Exists(
                    _workspace.MetadataDatabasePath))
            {
                metadataSnapshot =
                    Path.Combine(
                        snapshotDirectory,
                        "metadata.db");

                CreateMetadataSnapshot(
                    _workspace.MetadataDatabasePath,
                    metadataSnapshot);
            }

            var sources =
                EnumerateWorkspaceFiles()
                    .OrderBy(
                        item =>
                            item.RelativePath,
                        StringComparer.Ordinal)
                    .ToList();

            var hashes =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);

            using (
                var stream =
                    new FileStream(
                        temporaryArchive,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize:
                            CopyBufferSize,
                        options:
                            FileOptions.WriteThrough))
            {
                using (
                    var archive =
                        new ZipArchive(
                            stream,
                            ZipArchiveMode.Create,
                            leaveOpen: true))
                {
                    foreach (var source
                             in sources)
                    {
                        var physicalSource =
                            source.IsMetadataDatabase
                            && metadataSnapshot
                                is not null
                                ? metadataSnapshot
                                : source.FullPath;

                        hashes.Add(
                            source.RelativePath,
                            AddFile(
                                archive,
                                physicalSource,
                                source.RelativePath));
                    }

                    var manifest =
                        new WorkspaceBackupManifest
                        {
                            Format =
                                FormatId,

                            WorkspaceId =
                                _workspace
                                    .Info
                                    .WorkspaceId,

                            CreatedUtc =
                                DateTimeOffset
                                    .UtcNow
                                    .ToString(
                                        "O",
                                        CultureInfo.InvariantCulture),

                            Files =
                                hashes
                        };

                    AddManifest(
                        archive,
                        manifest);
                }

                stream.Flush(
                    flushToDisk: true);
            }

            Validate(
                temporaryArchive,
                _workspace.Info.WorkspaceId);

            File.Move(
                temporaryArchive,
                destination,
                overwrite: true);

            return destination;
        }
        catch (WorkspaceBackupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkspaceBackupException(
                "Workspace backup creation failed.",
                ex);
        }
        finally
        {
            TryDeleteFile(
                temporaryArchive);

            TryDeleteDirectory(
                snapshotDirectory);
        }
    }

    public static WorkspaceRestoreResult Restore(
        string backupPath,
        string destinationPath,
        bool preserveExisting = true,
        string? expectedWorkspaceId = null)
        => RestoreCore(
            backupPath,
            destinationPath,
            preserveExisting,
            expectedWorkspaceId,
            static (stage, destination) =>
                Directory.Move(
                    stage,
                    destination));

    internal static WorkspaceRestoreResult RestoreCore(
        string backupPath,
        string destinationPath,
        bool preserveExisting,
        string? expectedWorkspaceId,
        Action<string, string> publishDirectory)
    {
        ArgumentNullException.ThrowIfNull(
            publishDirectory);

        var backup =
            NormalizeExistingBackup(
                backupPath);

        var destination =
            NormalizeRestoreDestination(
                destinationPath);

        var manifest =
            Validate(
                backup,
                expectedWorkspaceId);

        var parent =
            Path.GetDirectoryName(
                destination)
            ?? throw new WorkspaceBackupException(
                "Restore destination has no parent directory.");

        Directory.CreateDirectory(
            parent);

        var stage =
            Path.Combine(
                parent,
                Path.GetFileName(destination)
                + ".restore-stage-"
                + Guid.NewGuid()
                    .ToString("N"));

        string? preserved =
            null;

        var published =
            false;

        try
        {
            Directory.CreateDirectory(
                stage);

            ExtractToStage(
                backup,
                manifest,
                stage);

            ValidateRestoredStage(
                stage,
                manifest);

            if (Directory.Exists(
                    destination))
            {
                if (!preserveExisting)
                {
                    throw new WorkspaceBackupException(
                        "Restore destination exists and preservation is required by policy.");
                }

                preserved =
                    destination
                    + ".pre-restore-"
                    + DateTimeOffset.UtcNow
                        .ToString(
                            "yyyyMMddTHHmmssfffZ",
                            CultureInfo.InvariantCulture)
                    + "-"
                    + Guid.NewGuid()
                        .ToString("N");

                Directory.Move(
                    destination,
                    preserved);
            }
            else if (File.Exists(
                         destination))
            {
                throw new WorkspaceBackupException(
                    "Restore destination points to an existing file.");
            }

            try
            {
                publishDirectory(
                    stage,
                    destination);

                published =
                    true;
            }
            catch (Exception publicationException)
            {
                if (preserved is not null
                    && Directory.Exists(
                        preserved)
                    && !Directory.Exists(
                        destination)
                    && !File.Exists(
                        destination))
                {
                    Directory.Move(
                        preserved,
                        destination);

                    preserved =
                        null;
                }

                throw new WorkspaceBackupException(
                    "Restore publication failed; the preserved destination was restored when possible.",
                    publicationException);
            }

            var opened =
                new WorkspaceService()
                    .Open(
                        destination);

            if (!string.Equals(
                    opened.Info.WorkspaceId,
                    manifest.WorkspaceId,
                    StringComparison.Ordinal))
            {
                throw new WorkspaceBackupException(
                    "Restored workspace identity does not match the backup manifest.");
            }

            return new WorkspaceRestoreResult(
                RestoredPath:
                    destination,

                PreservedPath:
                    preserved,

                WorkspaceId:
                    manifest.WorkspaceId);
        }
        catch (WorkspaceBackupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkspaceBackupException(
                "Workspace restore failed.",
                ex);
        }
        finally
        {
            if (!published)
            {
                TryDeleteDirectory(
                    stage);
            }
        }
    }

    private static void ExtractToStage(
        string backupPath,
        WorkspaceBackupManifest manifest,
        string stage)
    {
        using var stream =
            new FileStream(
                backupPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

        using var archive =
            new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                leaveOpen: false);

        var members =
            archive
                .Entries
                .Where(
                    entry =>
                        !IsDirectoryEntry(
                            entry))
                .ToDictionary(
                    entry =>
                        entry.FullName,
                    entry =>
                        entry,
                    StringComparer.OrdinalIgnoreCase);

        foreach (var pair
                 in manifest.Files)
        {
            ValidateArchivePath(
                pair.Key);

            if (!members.TryGetValue(
                    pair.Key,
                    out var entry))
            {
                throw new WorkspaceBackupException(
                    $"Missing restore member: {pair.Key}");
            }

            var target =
                GetSafeRestoreTarget(
                    stage,
                    pair.Key);

            var targetParent =
                Path.GetDirectoryName(
                    target)
                ?? throw new WorkspaceBackupException(
                    $"Restore member has no parent: {pair.Key}");

            Directory.CreateDirectory(
                targetParent);

            using var source =
                entry.Open();

            using var destination =
                new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize:
                        CopyBufferSize,
                    options:
                        FileOptions.WriteThrough);

            source.CopyTo(
                destination,
                CopyBufferSize);

            destination.Flush(
                flushToDisk: true);
        }
    }

    private static void ValidateRestoredStage(
        string stage,
        WorkspaceBackupManifest manifest)
    {
        var actualFiles =
            Directory
                .EnumerateFiles(
                    stage,
                    "*",
                    SearchOption.AllDirectories)
                .Select(
                    path =>
                        Path.GetRelativePath(
                            stage,
                            path)
                        .Replace(
                            '\\',
                            '/'))
                .ToList();

        if (actualFiles.Count !=
            manifest.Files.Count)
        {
            throw new WorkspaceBackupException(
                "Staged restore file count does not match the manifest.");
        }

        var expected =
            new HashSet<string>(
                manifest.Files.Keys,
                StringComparer.OrdinalIgnoreCase);

        if (actualFiles.Any(
                path =>
                    !expected.Contains(
                        path)))
        {
            throw new WorkspaceBackupException(
                "Staged restore contains an unexpected file.");
        }

        foreach (var pair
                 in manifest.Files)
        {
            var path =
                GetSafeRestoreTarget(
                    stage,
                    pair.Key);

            if (!File.Exists(
                    path))
            {
                throw new WorkspaceBackupException(
                    $"Staged restore member is missing: {pair.Key}");
            }

            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            var actualHash =
                ComputeSha256(
                    stream);

            if (!string.Equals(
                    actualHash,
                    pair.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspaceBackupException(
                    $"Staged restore hash mismatch: {pair.Key}");
            }
        }

        var opened =
            new WorkspaceService()
                .Open(
                    stage);

        if (!string.Equals(
                opened.Info.WorkspaceId,
                manifest.WorkspaceId,
                StringComparison.Ordinal))
        {
            throw new WorkspaceBackupException(
                "Staged workspace identity does not match the backup manifest.");
        }
    }

    private static string GetSafeRestoreTarget(
        string stage,
        string relativePath)
    {
        ValidateArchivePath(
            relativePath);

        var root =
            Path.GetFullPath(
                stage);

        var target =
            Path.GetFullPath(
                Path.Combine(
                    root,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));

        if (!IsInside(
                root,
                target)
            || string.Equals(
                root,
                target,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceBackupException(
                $"Restore path escaped the staging directory: {relativePath}");
        }

        return target;
    }

    private static string NormalizeRestoreDestination(
        string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            throw new ArgumentException(
                "Restore destination is required.",
                nameof(path));
        }

        var normalized =
            Path.GetFullPath(
                path.Trim());

        var root =
            Path.GetPathRoot(
                normalized);

        if (string.Equals(
                root,
                normalized,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceBackupException(
                "A filesystem root cannot be used as a restore destination.");
        }

        return normalized;
    }

    public static WorkspaceBackupManifest Validate(
        string backupPath,
        string? expectedWorkspaceId = null)
    {
        var path =
            NormalizeExistingBackup(
                backupPath);

        try
        {
            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            using var archive =
                new ZipArchive(
                    stream,
                    ZipArchiveMode.Read,
                    leaveOpen: false);

            if (archive.Entries.Count >
                MaximumEntryCount)
            {
                throw new WorkspaceBackupException(
                    "Backup contains too many entries.");
            }

            var duplicateCheck =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var entry
                     in archive.Entries)
            {
                if (!duplicateCheck.Add(
                        entry.FullName))
                {
                    throw new WorkspaceBackupException(
                        $"Duplicate backup member: {entry.FullName}");
                }

                if (IsDirectoryEntry(
                        entry))
                {
                    continue;
                }

                ValidateArchivePath(
                    entry.FullName);
            }

            var manifestEntry =
                archive
                    .Entries
                    .SingleOrDefault(
                        entry =>
                            string.Equals(
                                entry.FullName,
                                ManifestName,
                                StringComparison.Ordinal))
                ?? throw new WorkspaceBackupException(
                    "Backup manifest missing.");

            if (manifestEntry.Length >
                MaximumManifestBytes)
            {
                throw new WorkspaceBackupException(
                    "Backup manifest exceeds the supported size.");
            }

            WorkspaceBackupManifest manifest;

            using (
                var manifestStream =
                    manifestEntry.Open())
            {
                manifest =
                    JsonSerializer.Deserialize<
                        WorkspaceBackupManifest>(
                            manifestStream)
                    ?? throw new WorkspaceBackupException(
                        "Backup manifest is empty or invalid.");
            }

            ValidateManifest(
                manifest,
                expectedWorkspaceId);

            var actualMembers =
                archive
                    .Entries
                    .Where(
                        entry =>
                            !IsDirectoryEntry(entry)
                            && !string.Equals(
                                entry.FullName,
                                ManifestName,
                                StringComparison.Ordinal))
                    .ToDictionary(
                        entry =>
                            entry.FullName,
                        entry =>
                            entry,
                        StringComparer.OrdinalIgnoreCase);

            var manifestMembers =
                new HashSet<string>(
                    manifest.Files.Keys,
                    StringComparer.OrdinalIgnoreCase);

            if (actualMembers.Count !=
                manifestMembers.Count
                || actualMembers.Keys.Any(
                    name =>
                        !manifestMembers.Contains(
                            name)))
            {
                throw new WorkspaceBackupException(
                    "Backup members do not exactly match the manifest.");
            }

            foreach (var pair
                     in manifest.Files)
            {
                ValidateArchivePath(
                    pair.Key);

                if (string.Equals(
                        pair.Key,
                        ManifestName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceBackupException(
                        "Manifest cannot describe itself as a workspace file.");
                }

                if (!actualMembers.TryGetValue(
                        pair.Key,
                        out var entry))
                {
                    throw new WorkspaceBackupException(
                        $"Missing backup member: {pair.Key}");
                }

                using var member =
                    entry.Open();

                var actualHash =
                    ComputeSha256(
                        member);

                if (!string.Equals(
                        actualHash,
                        pair.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceBackupException(
                        $"Hash mismatch: {pair.Key}");
                }
            }

            return manifest;
        }
        catch (WorkspaceBackupException)
        {
            throw;
        }
        catch (
            Exception ex)
            when (
                ex is InvalidDataException
                || ex is IOException
                || ex is JsonException
                || ex is UnauthorizedAccessException
                || ex is InvalidOperationException)
        {
            throw new WorkspaceBackupException(
                "Backup validation failed.",
                ex);
        }
    }

    private IEnumerable<BackupSource>
        EnumerateWorkspaceFiles()
    {
        var pending =
            new Stack<string>();

        pending.Push(
            _root);

        while (pending.Count > 0)
        {
            var directory =
                pending.Pop();

            foreach (var childDirectory
                     in Directory.EnumerateDirectories(
                         directory))
            {
                var relative =
                    GetCanonicalRelativePath(
                        childDirectory);

                if (IsBackupDirectory(
                        relative))
                {
                    continue;
                }

                var attributes =
                    File.GetAttributes(
                        childDirectory);

                if ((attributes
                     & FileAttributes.ReparsePoint)
                    != 0)
                {
                    throw new WorkspaceBackupException(
                        $"Reparse directory is not allowed in backup: {relative}");
                }

                pending.Push(
                    childDirectory);
            }

            foreach (var file
                     in Directory.EnumerateFiles(
                         directory))
            {
                var relative =
                    GetCanonicalRelativePath(
                        file);

                if (ShouldSkipFile(
                        relative))
                {
                    continue;
                }

                if (string.Equals(
                        relative,
                        ManifestName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkspaceBackupException(
                        $"Workspace file conflicts with reserved backup manifest name: {ManifestName}");
                }

                var attributes =
                    File.GetAttributes(
                        file);

                if ((attributes
                     & FileAttributes.ReparsePoint)
                    != 0)
                {
                    throw new WorkspaceBackupException(
                        $"Reparse file is not allowed in backup: {relative}");
                }

                yield return new BackupSource(
                    FullPath:
                        file,

                    RelativePath:
                        relative,

                    IsMetadataDatabase:
                        string.Equals(
                            relative,
                            ".khz/metadata.db",
                            StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static string AddFile(
        ZipArchive archive,
        string sourcePath,
        string relativePath)
    {
        ValidateArchivePath(
            relativePath);

        var entry =
            archive.CreateEntry(
                relativePath,
                CompressionLevel.Optimal);

        using var source =
            new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize:
                    CopyBufferSize,
                options:
                    FileOptions.SequentialScan);

        using var destination =
            entry.Open();

        using var hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);

        var buffer =
            new byte[CopyBufferSize];

        while (true)
        {
            var read =
                source.Read(
                    buffer,
                    0,
                    buffer.Length);

            if (read == 0)
                break;

            destination.Write(
                buffer,
                0,
                read);

            hash.AppendData(
                buffer,
                0,
                read);
        }

        return Convert
            .ToHexString(
                hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void AddManifest(
        ZipArchive archive,
        WorkspaceBackupManifest manifest)
    {
        var entry =
            archive.CreateEntry(
                ManifestName,
                CompressionLevel.Optimal);

        using var stream =
            entry.Open();

        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false),
                bufferSize:
                    4096,
                leaveOpen:
                    false);

        var json =
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions
                {
                    WriteIndented =
                        true
                });

        writer.Write(
            json);

        writer.Write(
            '\n');
    }

    private static void CreateMetadataSnapshot(
        string sourceDatabase,
        string destinationDatabase)
    {
        var sourceBuilder =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    sourceDatabase,

                Mode =
                    SqliteOpenMode.ReadOnly,

                Pooling =
                    false
            };

        var destinationBuilder =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    destinationDatabase,

                Mode =
                    SqliteOpenMode.ReadWriteCreate,

                Pooling =
                    false
            };

        using var source =
            new SqliteConnection(
                sourceBuilder.ToString());

        using var destination =
            new SqliteConnection(
                destinationBuilder.ToString());

        source.Open();
        destination.Open();

        source.BackupDatabase(
            destination);

        using var integrity =
            destination.CreateCommand();

        integrity.CommandText =
            "PRAGMA integrity_check;";

        var result =
            Convert.ToString(
                integrity.ExecuteScalar(),
                CultureInfo.InvariantCulture);

        if (!string.Equals(
                result,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkspaceBackupException(
                "Workspace metadata snapshot failed SQLite integrity validation.");
        }
    }

    private static void ValidateManifest(
        WorkspaceBackupManifest manifest,
        string? expectedWorkspaceId)
    {
        if (!string.Equals(
                manifest.Format,
                FormatId,
                StringComparison.Ordinal))
        {
            throw new WorkspaceBackupException(
                "Unknown backup format.");
        }

        if (!Guid.TryParseExact(
                manifest.WorkspaceId,
                "D",
                out _))
        {
            throw new WorkspaceBackupException(
                "Backup workspace ID is not a canonical GUID.");
        }

        if (!DateTimeOffset.TryParse(
                manifest.CreatedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new WorkspaceBackupException(
                "Backup creation timestamp is invalid.");
        }

        if (manifest.Files is null)
        {
            throw new WorkspaceBackupException(
                "Backup manifest file map is missing.");
        }

        if (manifest.Files.Count >
            MaximumEntryCount)
        {
            throw new WorkspaceBackupException(
                "Backup manifest contains too many files.");
        }

        var names =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var pair
                 in manifest.Files)
        {
            ValidateArchivePath(
                pair.Key);

            if (!names.Add(
                    pair.Key))
            {
                throw new WorkspaceBackupException(
                    $"Duplicate manifest path: {pair.Key}");
            }

            if (pair.Value.Length != 64
                || pair.Value.Any(
                    character =>
                        !Uri.IsHexDigit(
                            character)))
            {
                throw new WorkspaceBackupException(
                    $"Invalid SHA-256 digest for: {pair.Key}");
            }
        }

        if (expectedWorkspaceId is not null
            && !string.Equals(
                manifest.WorkspaceId,
                expectedWorkspaceId,
                StringComparison.Ordinal))
        {
            throw new WorkspaceBackupException(
                "Workspace identity mismatch.");
        }
    }

    private void ValidateDestinationBoundary(
        string destination)
    {
        if (!IsInside(
                _root,
                destination))
        {
            return;
        }

        var relative =
            GetCanonicalRelativePath(
                destination);

        if (IsBackupPath(
                relative))
        {
            return;
        }

        throw new WorkspaceBackupException(
            "Backup destination inside a workspace is allowed only under .khz/backups.");
    }

    private string GetCanonicalRelativePath(
        string path)
    {
        var full =
            Path.GetFullPath(
                path);

        if (!IsInside(
                _root,
                full))
        {
            throw new WorkspaceBackupException(
                "Backup path escaped the workspace root.");
        }

        var relative =
            Path.GetRelativePath(
                _root,
                full)
            .Replace(
                '\\',
                '/');

        ValidateArchivePath(
            relative);

        return relative;
    }

    private static bool IsInside(
        string root,
        string candidate)
    {
        var relative =
            Path.GetRelativePath(
                root,
                candidate);

        return relative == "."
            || (
                !Path.IsPathRooted(
                    relative)
                && relative != ".."
                && !relative.StartsWith(
                    ".."
                    + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                && !relative.StartsWith(
                    ".."
                    + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal)
            );
    }

    private static bool IsBackupDirectory(
        string relative)
        => string.Equals(
               relative,
               ".khz/backups",
               StringComparison.OrdinalIgnoreCase)
           || relative.StartsWith(
               ".khz/backups/",
               StringComparison.OrdinalIgnoreCase);

    private static bool IsBackupPath(
        string relative)
        => relative.StartsWith(
               ".khz/backups/",
               StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipFile(
        string relative)
    {
        if (IsBackupPath(
                relative))
        {
            return true;
        }

        return string.Equals(
                   relative,
                   ".khz/metadata.db-wal",
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   relative,
                   ".khz/metadata.db-shm",
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   relative,
                   ".khz/metadata.db-journal",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateArchivePath(
        string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            throw new WorkspaceBackupException(
                "Backup contains an empty path.");
        }

        if (path.Contains(
                "\\\\",
                StringComparison.Ordinal)
            || path.StartsWith(
                "/",
                StringComparison.Ordinal)
            || path.EndsWith(
                "/",
                StringComparison.Ordinal)
            || path.Contains(
                '\0'))
        {
            throw new WorkspaceBackupException(
                $"Unsafe backup path: {path}");
        }

        var parts =
            path.Split(
                '/');

        foreach (var part
                 in parts)
        {
            if (part.Length == 0
                || part == "."
                || part == ".."
                || part.Contains(
                    ":",
                    StringComparison.Ordinal))
            {
                throw new WorkspaceBackupException(
                    $"Unsafe backup path: {path}");
            }
        }
    }

    private static string ComputeSha256(
        Stream stream)
    {
        var hash =
            SHA256.HashData(
                stream);

        return Convert
            .ToHexString(
                hash)
            .ToLowerInvariant();
    }

    private static string NormalizeDestination(
        string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            throw new ArgumentException(
                "Backup destination is required.",
                nameof(path));
        }

        var normalized =
            Path.GetFullPath(
                path.Trim());

        if (Directory.Exists(
                normalized))
        {
            throw new WorkspaceBackupException(
                "Backup destination points to a directory.");
        }

        return normalized;
    }

    private static string NormalizeExistingBackup(
        string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            throw new ArgumentException(
                "Backup path is required.",
                nameof(path));
        }

        var normalized =
            Path.GetFullPath(
                path.Trim());

        if (!File.Exists(
                normalized))
        {
            throw new FileNotFoundException(
                "Backup archive was not found.",
                normalized);
        }

        return normalized;
    }

    private static bool IsDirectoryEntry(
        ZipArchiveEntry entry)
        => entry.FullName.EndsWith(
            "/",
            StringComparison.Ordinal);

    private static void TryDeleteFile(
        string path)
    {
        try
        {
            if (File.Exists(
                    path))
            {
                File.Delete(
                    path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(
        string path)
    {
        try
        {
            if (Directory.Exists(
                    path))
            {
                Directory.Delete(
                    path,
                    recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record BackupSource(
        string FullPath,
        string RelativePath,
        bool IsMetadataDatabase);
}
