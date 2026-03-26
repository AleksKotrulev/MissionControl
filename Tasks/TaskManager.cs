using MissionControl.Data;

namespace MissionControl.Tasks;

public class TaskManager
{
    public async Task<List<TaskItem>> GetAllAsync() =>
        await JsonDataStore.ReadAsync<List<TaskItem>>(DataPaths.Tasks);

    public async Task<TaskItem?> GetByIdAsync(string id)
    {
        var tasks = await GetAllAsync();
        return tasks.FirstOrDefault(t => t.Id == id);
    }

    public async Task<TaskItem> CreateAsync(TaskItem task)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
            task.Id = Guid.NewGuid().ToString("N")[..8];

        task.CreatedAt = DateTime.UtcNow;
        task.Status = TaskItemStatus.NotStarted;

        await JsonDataStore.MutateAsync<List<TaskItem>>(DataPaths.Tasks, tasks =>
        {
            tasks.Add(task);
            return tasks;
        });

        return task;
    }

    public async Task<TaskItem> UpdateAsync(TaskItem task)
    {
        await JsonDataStore.MutateAsync<List<TaskItem>>(DataPaths.Tasks, tasks =>
        {
            var idx = tasks.FindIndex(t => t.Id == task.Id);
            if (idx < 0) throw new KeyNotFoundException($"Task '{task.Id}' not found");
            tasks[idx] = task;
            return tasks;
        });

        return task;
    }

    public async Task DeleteAsync(string id)
    {
        await JsonDataStore.MutateAsync<List<TaskItem>>(DataPaths.Tasks, tasks =>
        {
            tasks.RemoveAll(t => t.Id == id);
            return tasks;
        });
    }

    public async Task<List<TaskItem>> GetDispatchableAsync(int maxRetries)
    {
        var tasks = await GetAllAsync();
        var doneTaskIds = tasks.Where(t => t.Status == TaskItemStatus.Done).Select(t => t.Id).ToHashSet();

        return tasks.Where(t =>
            t.Status == TaskItemStatus.NotStarted &&
            t.AttemptCount < maxRetries &&
            !string.IsNullOrWhiteSpace(t.AssignedTo) &&
            t.BlockedBy.All(depId => doneTaskIds.Contains(depId))
        ).OrderByDescending(t => t.Priority).ToList();
    }

    public async Task SetStatusAsync(string id, TaskItemStatus status)
    {
        await JsonDataStore.MutateAsync<List<TaskItem>>(DataPaths.Tasks, tasks =>
        {
            var task = tasks.FirstOrDefault(t => t.Id == id);
            if (task == null) throw new KeyNotFoundException($"Task '{id}' not found");
            task.Status = status;
            if (status == TaskItemStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            return tasks;
        });
    }

    public async Task IncrementAttemptAsync(string id)
    {
        await JsonDataStore.MutateAsync<List<TaskItem>>(DataPaths.Tasks, tasks =>
        {
            var task = tasks.FirstOrDefault(t => t.Id == id);
            if (task != null) task.AttemptCount++;
            return tasks;
        });
    }
}
