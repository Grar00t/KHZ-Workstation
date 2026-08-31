using System;

namespace KHZ.App.Tasks;

internal sealed record TaskItem(
    string TaskId,
    string Title,
    DateOnly? DueDate,
    bool IsCompleted,
    string CreatedUtc,
    string CreatedLocal,
    string UpdatedUtc,
    string UpdatedLocal);
