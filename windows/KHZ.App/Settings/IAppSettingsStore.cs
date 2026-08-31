namespace KHZ.App.Settings;

internal interface IAppSettingsStore
{
    string? GetDefaultWorkspaceFolder();

    string SaveDefaultWorkspaceFolder(
        string path);

    void ClearDefaultWorkspaceFolder();
}
