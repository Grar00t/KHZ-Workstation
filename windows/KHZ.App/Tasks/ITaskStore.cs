using System;
using System.Collections.Generic;

namespace KHZ.App.Tasks;

internal interface ITaskStore
{
    TaskItem Create(
        string title,
        DateOnly? dueDate);

    IReadOnlyList<TaskItem> List(
        bool includeCompleted = true);

    TaskItem SetCompleted(
        string taskId,
        bool completed);
}
