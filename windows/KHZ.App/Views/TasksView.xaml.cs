using KHZ.App.Tasks;
using KHZ.App.Trust;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KHZ.App.Views;

public partial class TasksView : UserControl
{
    private ITaskStore? _taskStore;
    private IActivityStore? _activity;

    public TasksView()
    {
        InitializeComponent();
    }

    internal void Configure(
        ITaskStore taskStore,
        IActivityStore activity)
    {
        _taskStore =
            taskStore
            ?? throw new ArgumentNullException(nameof(taskStore));

        _activity =
            activity
            ?? throw new ArgumentNullException(nameof(activity));
    }

    internal void LoadTasks()
    {
        if (_taskStore is null)
            return;

        try
        {
            var tasks =
                _taskStore.List(
                    includeCompleted: true);

            var rows =
                tasks
                    .Select(ToRow)
                    .ToList();

            TasksGrid.ItemsSource = rows;

            var open =
                tasks.Count(
                    task => !task.IsCompleted);

            var completed =
                tasks.Count(
                    task => task.IsCompleted);

            TaskCountText.Text =
                $"TASKS · {tasks.Count}  |  OPEN · {open}  |  COMPLETED · {completed}";

            TaskErrorText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            TasksGrid.ItemsSource = null;
            TaskErrorText.Text =
                "Load failed: " + ex.Message;
        }
    }

    private void AddTask_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_taskStore is null)
            return;

        try
        {
            var title =
                TaskTitleBox.Text.Trim();

            DateOnly? dueDate = null;

            if (TaskDueDatePicker.SelectedDate is DateTime selected)
            {
                dueDate =
                    DateOnly.FromDateTime(
                        selected);
            }

            var created =
                _taskStore.Create(
                    title,
                    dueDate);

            _activity?.Record(
                category: "task",
                action: "task.create",
                target: created.TaskId,
                result: "CREATED",
                details: new
                {
                    title = created.Title,
                    dueDate =
                        created.DueDate?.ToString(
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture),
                    networkAttempted = false,
                    aiUsed = false
                });

            TaskTitleBox.Text = string.Empty;
            TaskDueDatePicker.SelectedDate = null;

            LoadTasks();
            TaskTitleBox.Focus();
        }
        catch (Exception ex)
        {
            TaskErrorText.Text =
                "Create failed: " + ex.Message;
        }
    }

    private void CompleteSelected_Click(
        object sender,
        RoutedEventArgs e)
        => SetSelectedCompleted(
            completed: true);

    private void ReopenSelected_Click(
        object sender,
        RoutedEventArgs e)
        => SetSelectedCompleted(
            completed: false);

    private void Refresh_Click(
        object sender,
        RoutedEventArgs e)
        => LoadTasks();

    private void SetSelectedCompleted(
        bool completed)
    {
        if (_taskStore is null)
            return;

        if (TasksGrid.SelectedItem is not TaskRow selected)
        {
            TaskErrorText.Text =
                "Select a task first.";

            return;
        }

        try
        {
            var updated =
                _taskStore.SetCompleted(
                    selected.TaskId,
                    completed);

            _activity?.Record(
                category: "task",
                action:
                    completed
                        ? "task.complete"
                        : "task.reopen",
                target: updated.TaskId,
                result:
                    completed
                        ? "COMPLETED"
                        : "REOPENED",
                details: new
                {
                    title = updated.Title,
                    networkAttempted = false,
                    aiUsed = false
                });

            LoadTasks();
        }
        catch (Exception ex)
        {
            TaskErrorText.Text =
                "Update failed: " + ex.Message;
        }
    }

    private static TaskRow ToRow(
        TaskItem task)
    {
        var due =
            task.DueDate?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)
            ?? "";

        var updated =
            FormatLocalTimestamp(
                task.UpdatedLocal);

        return new TaskRow(
            TaskId: task.TaskId,
            Status:
                task.IsCompleted
                    ? "Completed"
                    : "Open",
            Title: task.Title,
            Due: due,
            Updated: updated);
    }

    private static string FormatLocalTimestamp(
        string value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed.ToString(
                "yyyy-MM-dd hh:mm tt",
                CultureInfo.InvariantCulture);
        }

        return value;
    }

    private sealed record TaskRow(
        string TaskId,
        string Status,
        string Title,
        string Due,
        string Updated);
}
