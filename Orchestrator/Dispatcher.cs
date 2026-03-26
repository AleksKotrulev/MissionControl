using MissionControl.Agents;
using MissionControl.Communication;
using MissionControl.Config;
using MissionControl.Missions;
using MissionControl.Tasks;

namespace MissionControl.Orchestrator;

public class Dispatcher : BackgroundService
{
    private readonly MissionControlConfig _config;
    private readonly AgentRegistry _agents;
    private readonly TaskManager _taskManager;
    private readonly AgentRunner _runner;
    private readonly MissionOrchestrator _missions;
    private readonly CommunicationManager _comms;
    private readonly ILogger<Dispatcher> _logger;

    public Dispatcher(
        MissionControlConfig config,
        AgentRegistry agents,
        TaskManager taskManager,
        AgentRunner runner,
        MissionOrchestrator missions,
        CommunicationManager comms,
        ILogger<Dispatcher> logger)
    {
        _config = config;
        _agents = agents;
        _taskManager = taskManager;
        _runner = runner;
        _missions = missions;
        _comms = comms;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Dispatcher started. Polling every {Interval}s, max {Max} parallel agents",
            _config.DispatchIntervalSeconds, _config.MaxParallelAgents);

        await _comms.LogActivityAsync("daemon_started", "Dispatcher daemon started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchTickAsync(stoppingToken);
                await _missions.ReconcileAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatcher tick failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.DispatchIntervalSeconds), stoppingToken);
        }

        await _comms.LogActivityAsync("daemon_stopped", "Dispatcher daemon stopped");
    }

    private async Task DispatchTickAsync(CancellationToken ct)
    {
        var activeRuns = await _runner.GetActiveRunsAsync();
        var availableSlots = _config.MaxParallelAgents - activeRuns.Count;

        if (availableSlots <= 0) return;

        var dispatchable = await _taskManager.GetDispatchableAsync(_config.MaxTaskRetries);

        // Filter out tasks with pending decisions
        var tasksToDispatch = new List<TaskItem>();
        foreach (var task in dispatchable)
        {
            if (await _comms.HasPendingDecisionForTaskAsync(task.Id))
                continue;
            tasksToDispatch.Add(task);
            if (tasksToDispatch.Count >= availableSlots) break;
        }

        foreach (var task in tasksToDispatch)
        {
            var agent = await _agents.GetByIdAsync(task.AssignedTo);
            if (agent == null || agent.Status != "active")
            {
                _logger.LogWarning("Agent '{AgentId}' not found or inactive for task '{TaskId}'",
                    task.AssignedTo, task.Id);
                continue;
            }

            _logger.LogInformation("Dispatching task '{Title}' to agent '{Agent}'",
                task.Title, agent.Name);

            // Fire and forget — runs concurrently
            _ = Task.Run(async () =>
            {
                try
                {
                    await _runner.RunTaskAsync(agent, task, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Agent run failed for task '{TaskId}'", task.Id);
                }
            }, ct);
        }
    }
}
