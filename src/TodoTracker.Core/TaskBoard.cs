namespace TodoTracker.Core;

public sealed class TaskBoard
{
    private readonly List<WorkItem> _tasks = [];

    public IReadOnlyList<WorkItem> Tasks => _tasks;

    public IReadOnlyList<WorkItem> AllTasks()
    {
        return Flatten(_tasks).ToList();
    }

    public WorkItem AddTask(string title, TaskPriority priority = TaskPriority.Normal)
    {
        var task = new WorkItem(title, priority);
        _tasks.Add(task);
        return task;
    }

    public IReadOnlyList<WorkItem> Agenda(DateTimeOffset now)
    {
        return AllTasks()
            .Where(item => item.IsActionable(now))
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.EffectiveDueAt() ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.CreatedAt)
            .ToList();
    }

    public IReadOnlyList<WorkItem> Waiting(DateTimeOffset now)
    {
        return AllTasks()
            .Where(item => item.Status == WorkItemStatus.Waiting && !item.IsActionable(now))
            .OrderBy(item => item.EffectiveDueAt() ?? DateTimeOffset.MaxValue)
            .ThenByDescending(item => item.Priority)
            .ToList();
    }

    public IReadOnlyList<WorkItem> RecentNotes(int maximumCount)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        return AllTasks()
            .Where(item => item.Notes.Count > 0)
            .OrderByDescending(item => item.Notes[^1].CreatedAt)
            .Take(maximumCount)
            .ToList();
    }

    private static IEnumerable<WorkItem> Flatten(IEnumerable<WorkItem> items)
    {
        foreach (var item in items)
        {
            yield return item;

            foreach (var subtask in Flatten(item.Subtasks))
            {
                yield return subtask;
            }
        }
    }
}
