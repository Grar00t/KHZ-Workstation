namespace KHZ.App.Workspaces;

internal sealed record WorkspaceInfo(
    string WorkspaceId,
    string Name,
    string Root,
    string CreatedUtc,
    string Classification,
    int SchemaVersion);

internal sealed record WorkspaceContext(
    WorkspaceInfo Info,
    string MetadataDirectory,
    string ManifestPath,
    string MetadataDatabasePath);
