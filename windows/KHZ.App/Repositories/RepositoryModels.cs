using System.Collections.Generic;

namespace KHZ.App.Repositories;

internal sealed record RepositoryChange(
    string Status,
    string Path);

internal sealed record RepositoryCommit(
    string Sha,
    string ShortSha,
    string Occurred,
    string Subject);

internal sealed record RepositorySnapshot(
    bool IsRepository,
    string RequestedPath,
    string? RootPath,
    string? Branch,
    string? HeadSha,
    bool IsClean,
    IReadOnlyList<RepositoryChange> Changes,
    IReadOnlyList<RepositoryCommit> RecentCommits,
    string? Message);
