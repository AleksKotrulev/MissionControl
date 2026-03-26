using MissionControl.Communication;
using MissionControl.Data;
using MissionControl.Tasks;

namespace MissionControl.Missions;

public class MissionOrchestrator
{
    private readonly TaskManager _taskManager;
    private readonly CommunicationManager _comms;

    public MissionOrchestrator(TaskManager taskManager, CommunicationManager comms)
    {
        _taskManager = taskManager;
        _comms = comms;
    }

    public async Task<List<Mission>> GetAllAsync() =>
        await JsonDataStore.ReadAsync<List<Mission>>(DataPaths.Missions);

    public async Task<Mission?> GetByIdAsync(string id)
    {
        var missions = await GetAllAsync();
        return missions.FirstOrDefault(m => m.Id == id);
    }

    public async Task<Mission> CreateAsync(Mission mission)
    {
        if (string.IsNullOrWhiteSpace(mission.Id))
            mission.Id = Guid.NewGuid().ToString("N")[..8];

        mission.CreatedAt = DateTime.UtcNow;
        mission.Status = MissionStatus.Running;

        // Tag all tasks with this mission
        var tasks = await _taskManager.GetAllAsync();
        foreach (var taskId in mission.TaskIds)
        {
            var task = tasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null)
            {
                task.MissionId = mission.Id;
                await _taskManager.UpdateAsync(task);
            }
        }

        await JsonDataStore.MutateAsync<List<Mission>>(DataPaths.Missions, missions =>
        {
            missions.Add(mission);
            return missions;
        });

        await _comms.LogActivityAsync("mission_created", $"Mission '{mission.Title}' created with {mission.TaskIds.Count} tasks");
        return mission;
    }

    public async Task ReconcileAsync()
    {
        var missions = await GetAllAsync();
        var tasks = await _taskManager.GetAllAsync();

        foreach (var mission in missions.Where(m => m.Status == MissionStatus.Running))
        {
            var missionTasks = tasks.Where(t => mission.TaskIds.Contains(t.Id)).ToList();

            var allDone = missionTasks.All(t =>
                t.Status is TaskItemStatus.Done or TaskItemStatus.Skipped);
            var anyStalled = missionTasks.Any(t =>
                t.Status == TaskItemStatus.Failed && t.AttemptCount >= 3);
            var hasUnresolvableDeps = missionTasks.Any(t =>
                t.Status == TaskItemStatus.NotStarted &&
                t.BlockedBy.Any(depId => missionTasks.Any(d => d.Id == depId && d.Status == TaskItemStatus.Failed)));

            if (allDone)
            {
                mission.Status = MissionStatus.Completed;
                mission.CompletedAt = DateTime.UtcNow;
                await _comms.LogActivityAsync("mission_completed", $"Mission '{mission.Title}' completed");
            }
            else if (anyStalled || hasUnresolvableDeps)
            {
                mission.Status = MissionStatus.Stalled;
                await _comms.LogActivityAsync("mission_stalled", $"Mission '{mission.Title}' stalled");
            }
        }

        await JsonDataStore.WriteAsync(DataPaths.Missions, missions);
    }

    public async Task AddHistoryEntryAsync(string missionId, string taskId, string agentId, string outcome)
    {
        await JsonDataStore.MutateAsync<List<Mission>>(DataPaths.Missions, missions =>
        {
            var mission = missions.FirstOrDefault(m => m.Id == missionId);
            mission?.History.Add(new MissionHistoryEntry
            {
                TaskId = taskId,
                AgentId = agentId,
                Outcome = outcome,
                Timestamp = DateTime.UtcNow
            });
            return missions;
        });
    }
}
