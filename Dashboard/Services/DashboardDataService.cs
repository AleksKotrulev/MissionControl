using MissionControl.Agents;
using MissionControl.Communication;
using MissionControl.Config;
using MissionControl.Data;
using MissionControl.Missions;
using MissionControl.Orchestrator;
using MissionControl.Tasks;

namespace MissionControl.Dashboard.Services;

public class DashboardDataService
{
    private readonly AgentRegistry _agents;
    private readonly TaskManager _tasks;
    private readonly MissionOrchestrator _missions;
    private readonly CommunicationManager _comms;
    private readonly AgentRunner _runner;
    private readonly MissionControlConfig _config;

    public DashboardDataService(
        AgentRegistry agents,
        TaskManager tasks,
        MissionOrchestrator missions,
        CommunicationManager comms,
        AgentRunner runner,
        MissionControlConfig config)
    {
        _agents = agents;
        _tasks = tasks;
        _missions = missions;
        _comms = comms;
        _runner = runner;
        _config = config;
    }

    public Task<List<AgentDefinition>> GetAgentsAsync() => _agents.GetAllAsync();
    public Task<List<TaskItem>> GetTasksAsync() => _tasks.GetAllAsync();
    public Task<List<Mission>> GetMissionsAsync() => _missions.GetAllAsync();
    public Task<List<InboxMessage>> GetInboxAsync() => _comms.GetInboxAsync();
    public Task<List<Decision>> GetDecisionsAsync() => _comms.GetDecisionsAsync();
    public Task<List<Decision>> GetPendingDecisionsAsync() => _comms.GetPendingDecisionsAsync();
    public Task<List<ActivityEvent>> GetActivityLogAsync() => _comms.GetActivityLogAsync();
    public Task<List<ActiveRun>> GetActiveRunsAsync() => _runner.GetActiveRunsAsync();
    public MissionControlConfig GetConfig() => _config;

    public Task<TaskItem> CreateTaskAsync(TaskItem task) => _tasks.CreateAsync(task);

    public Task ResolveDecisionAsync(string id, string response) =>
        _comms.ResolveDecisionAsync(id, response);

    public Task EmergencyStopAsync() => _runner.KillAllRunsAsync();

    public async Task SaveConfigAsync(MissionControlConfig config) =>
        await JsonDataStore.WriteAsync(DataPaths.Config, config);
}
