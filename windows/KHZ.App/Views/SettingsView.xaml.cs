using KHZ.App.Settings;
using KHZ.App.Trust;
using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class SettingsView : UserControl
{
    private IAppSettingsStore? _store;
    private IActivityStore? _activity;

    public SettingsView()
    {
        InitializeComponent();
    }

    internal void Configure(
        IAppSettingsStore store,
        IActivityStore activity)
    {
        _store =
            store ?? throw new ArgumentNullException(
                nameof(store));

        _activity =
            activity ?? throw new ArgumentNullException(
                nameof(activity));
    }

    internal void LoadSettings()
    {
        if (_store is null)
            return;

        try
        {
            var fallback =
                GetDocumentsFolder();

            var saved =
                _store.GetDefaultWorkspaceFolder();

            WorkspaceFolderText.Text =
                saved ?? fallback;

            if (string.IsNullOrWhiteSpace(saved))
            {
                SavedPreferenceText.Text =
                    "Not configured";

                EffectiveWorkspaceText.Text =
                    fallback;

                SettingsFeedbackText.Text =
                    "Windows Documents is used when no default is saved.";

                return;
            }

            SavedPreferenceText.Text =
                saved;

            if (Directory.Exists(saved))
            {
                EffectiveWorkspaceText.Text =
                    saved;

                SettingsFeedbackText.Text =
                    "Saved locally. This folder will be used on the next launch.";
            }
            else
            {
                EffectiveWorkspaceText.Text =
                    fallback;

                SettingsFeedbackText.Text =
                    "Saved folder is unavailable. Startup will fall back to Windows Documents.";
            }
        }
        catch (Exception ex)
        {
            SettingsFeedbackText.Text =
                "Load failed: " + ex.Message;
        }
    }

    private void ChooseWorkspace_Click(
        object sender,
        RoutedEventArgs e)
    {
        var initial =
            Directory.Exists(WorkspaceFolderText.Text)
                ? WorkspaceFolderText.Text
                : GetDocumentsFolder();

        var picker =
            new OpenFolderDialog
            {
                Title = "Choose default KHZ workspace folder",
                InitialDirectory = initial
            };

        if (picker.ShowDialog() != true)
            return;

        WorkspaceFolderText.Text =
            picker.FolderName;

        SettingsFeedbackText.Text =
            "Folder selected. Save to make it the startup default.";
    }

    private void SaveWorkspace_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_store is null)
            return;

        try
        {
            var saved =
                _store.SaveDefaultWorkspaceFolder(
                    WorkspaceFolderText.Text);

            var readBack =
                _store.GetDefaultWorkspaceFolder()
                ?? throw new InvalidOperationException(
                    "Settings read-back failed.");

            if (!string.Equals(
                    saved,
                    readBack,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Settings read-back mismatch.");
            }

            WorkspaceFolderText.Text =
                readBack;

            SavedPreferenceText.Text =
                readBack;

            EffectiveWorkspaceText.Text =
                readBack;

            SettingsFeedbackText.Text =
                "Saved locally. The new default applies on the next launch.";

            _activity?.Record(
                category: "settings",
                action: "workspace.default_folder.save",
                target: "workspace.default_folder",
                result: "SAVED",
                details: new
                {
                    pathCaptured = false,
                    appliesOnNextLaunch = true
                });
        }
        catch (Exception ex)
        {
            SettingsFeedbackText.Text =
                "Save failed: " + ex.Message;
        }
    }

    private void ResetWorkspace_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_store is null)
            return;

        try
        {
            _store.ClearDefaultWorkspaceFolder();

            if (_store.GetDefaultWorkspaceFolder() is not null)
            {
                throw new InvalidOperationException(
                    "Settings reset read-back failed.");
            }

            var fallback =
                GetDocumentsFolder();

            WorkspaceFolderText.Text =
                fallback;

            SavedPreferenceText.Text =
                "Not configured";

            EffectiveWorkspaceText.Text =
                fallback;

            SettingsFeedbackText.Text =
                "Reset locally. Windows Documents will be used on the next launch.";

            _activity?.Record(
                category: "settings",
                action: "workspace.default_folder.reset",
                target: "workspace.default_folder",
                result: "RESET",
                details: new
                {
                    pathCaptured = false,
                    appliesOnNextLaunch = true
                });
        }
        catch (Exception ex)
        {
            SettingsFeedbackText.Text =
                "Reset failed: " + ex.Message;
        }
    }

    private static string GetDocumentsFolder()
        => Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments);
}
