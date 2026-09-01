using KHZ.App.Backup;
using KHZ.App.Trust;
using KHZ.App.Workspaces;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class BackupRestoreView : UserControl
{
    private WorkspaceContext? _workspace;

    private IActivityStore? _activity;

    public BackupRestoreView()
    {
        InitializeComponent();
    }

    internal void Configure(
        IActivityStore activity)
    {
        _activity =
            activity
            ?? throw new ArgumentNullException(
                nameof(activity));
    }

    internal void SetWorkspace(
        WorkspaceContext? context)
    {
        _workspace =
            context;

        CreateBackupButton.IsEnabled =
            context is not null;

        if (context is null)
        {
            WorkspaceStatusText.Text =
                "Folder mode · open a workspace to create backups";

            return;
        }

        WorkspaceStatusText.Text =
            $"Workspace · {context.Info.Name}";
    }

    internal string CreateBackupFile(
        string destinationPath)
    {
        if (_workspace is null)
        {
            throw new InvalidOperationException(
                "An active workspace is required to create a backup.");
        }

        var service =
            new WorkspaceBackupService(
                _workspace);

        var created =
            service.Create(
                destinationPath);

        _activity?.Record(
            category: "backup",
            action: "workspace.backup",
            target:
                _workspace.Info.WorkspaceId,
            result: "CREATED",
            details: new
            {
                pathCaptured = false,
                networkAttempted = false,
                aiUsed = false
            });

        return created;
    }

    internal WorkspaceBackupManifest ValidateBackupFile(
        string backupPath)
    {
        try
        {
            var manifest =
                WorkspaceBackupService.Validate(
                    backupPath);

            _activity?.Record(
                category: "backup",
                action: "workspace.backup.validate",
                target:
                    manifest.WorkspaceId,
                result: "VALID",
                details: new
                {
                    fileCount =
                        manifest.Files.Count,
                    pathCaptured = false,
                    networkAttempted = false,
                    aiUsed = false
                });

            return manifest;
        }
        catch (Exception ex)
        {
            _activity?.Record(
                category: "backup",
                action: "workspace.backup.validate",
                target: "backup",
                result: "FAILED",
                details: new
                {
                    errorType =
                        ex.GetType().Name,
                    pathCaptured = false,
                    networkAttempted = false,
                    aiUsed = false
                });

            throw;
        }
    }

    internal WorkspaceRestoreResult RestoreBackupFile(
        string backupPath,
        string destinationPath)
    {
        RejectActiveWorkspaceOverlap(
            destinationPath);

        try
        {
            var result =
                WorkspaceBackupService.Restore(
                    backupPath,
                    destinationPath,
                    preserveExisting: true,
                    expectedWorkspaceId: null);

            _activity?.Record(
                category: "backup",
                action: "workspace.restore",
                target:
                    result.WorkspaceId,
                result: "RESTORED",
                details: new
                {
                    preservedExisting =
                        result.PreservedPath
                        is not null,
                    pathCaptured = false,
                    networkAttempted = false,
                    aiUsed = false
                });

            return result;
        }
        catch (Exception ex)
        {
            _activity?.Record(
                category: "backup",
                action: "workspace.restore",
                target: "backup",
                result: "FAILED",
                details: new
                {
                    errorType =
                        ex.GetType().Name,
                    pathCaptured = false,
                    networkAttempted = false,
                    aiUsed = false
                });

            throw;
        }
    }

    private void CreateBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_workspace is null)
            return;

        var dialog =
            new SaveFileDialog
            {
                Title =
                    "Create KHZ workspace backup",

                Filter =
                    "KHZ workspace backup (*.khzbackup.zip)|*.khzbackup.zip|ZIP archive (*.zip)|*.zip",

                AddExtension =
                    true,

                OverwritePrompt =
                    true,

                FileName =
                    MakeBackupFileName(
                        _workspace.Info.Name)
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var created =
                CreateBackupFile(
                    dialog.FileName);

            ResultText.Text =
                "Backup created and validated · "
                + Path.GetFileName(
                    created);
        }
        catch (Exception ex)
        {
            ResultText.Text =
                "Backup failed · "
                + ex.Message;
        }
    }

    private void ValidateBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog =
            CreateBackupOpenDialog(
                "Validate KHZ workspace backup");

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var manifest =
                ValidateBackupFile(
                    dialog.FileName);

            ResultText.Text =
                $"Backup valid · {manifest.Files.Count} files · workspace {ShortId(manifest.WorkspaceId)}";
        }
        catch (Exception ex)
        {
            ResultText.Text =
                "Backup invalid · "
                + ex.Message;
        }
    }

    private void RestoreBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        var archiveDialog =
            CreateBackupOpenDialog(
                "Restore KHZ workspace backup");

        if (archiveDialog.ShowDialog() != true)
            return;

        try
        {
            ValidateBackupFile(
                archiveDialog.FileName);
        }
        catch (Exception ex)
        {
            ResultText.Text =
                "Backup invalid · "
                + ex.Message;

            return;
        }

        var parentDialog =
            new OpenFolderDialog
            {
                Title =
                    "Choose parent folder for restored workspace",

                Multiselect =
                    false
            };

        if (parentDialog.ShowDialog() != true)
            return;

        var destination =
            Path.Combine(
                parentDialog.FolderName,
                MakeRestoreFolderName(
                    archiveDialog.FileName));

        try
        {
            var result =
                RestoreBackupFile(
                    archiveDialog.FileName,
                    destination);

            ResultText.Text =
                result.PreservedPath is null
                    ? "Workspace restored · "
                      + Path.GetFileName(
                          result.RestoredPath)
                    : "Workspace restored · previous destination preserved · "
                      + Path.GetFileName(
                          result.RestoredPath);
        }
        catch (Exception ex)
        {
            ResultText.Text =
                "Restore failed · "
                + ex.Message;
        }
    }

    private void RejectActiveWorkspaceOverlap(
        string destinationPath)
    {
        if (_workspace is null)
            return;

        var active =
            Path.GetFullPath(
                _workspace.Info.Root);

        var destination =
            Path.GetFullPath(
                destinationPath);

        if (IsInside(
                active,
                destination)
            || IsInside(
                destination,
                active))
        {
            throw new WorkspaceBackupException(
                "Restore destination cannot overlap the active workspace.");
        }
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

    private static OpenFileDialog CreateBackupOpenDialog(
        string title)
        => new()
        {
            Title =
                title,

            Filter =
                "KHZ workspace backup (*.khzbackup.zip;*.zip)|*.khzbackup.zip;*.zip",

            CheckFileExists =
                true,

            Multiselect =
                false
        };

    private static string MakeBackupFileName(
        string workspaceName)
    {
        var safeName =
            SanitizeFileName(
                workspaceName);

        return safeName
            + "-"
            + DateTime.Now.ToString(
                "yyyyMMdd-HHmmss")
            + ".khzbackup.zip";
    }

    private static string MakeRestoreFolderName(
        string backupPath)
    {
        var name =
            Path.GetFileName(
                backupPath);

        if (name.EndsWith(
                ".khzbackup.zip",
                StringComparison.OrdinalIgnoreCase))
        {
            name =
                name[
                    ..^".khzbackup.zip".Length];
        }
        else
        {
            name =
                Path.GetFileNameWithoutExtension(
                    name);
        }

        name =
            SanitizeFileName(
                name);

        if (string.IsNullOrWhiteSpace(
                name))
        {
            name =
                "RestoredWorkspace";
        }

        return name
            + "-restored";
    }

    private static string SanitizeFileName(
        string value)
    {
        var invalid =
            Path.GetInvalidFileNameChars();

        var cleaned =
            new string(
                value
                    .Select(
                        character =>
                            invalid.Contains(
                                character)
                                ? '_'
                                : character)
                    .ToArray())
                .Trim();

        return string.IsNullOrWhiteSpace(
                cleaned)
            ? "Workspace"
            : cleaned;
    }

    private static string ShortId(
        string workspaceId)
        => workspaceId.Length <= 8
            ? workspaceId
            : workspaceId[..8];
}
