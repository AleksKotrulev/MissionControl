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
                await ReconcileParentTasksAsync();
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

        var dispatched = 0;

        // Pass 1: Normal task dispatch
        var dispatchable = await _taskManager.GetDispatchableAsync(_config.MaxTaskRetries);

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

            dispatched++;
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

        // Pass 2: Validation dispatch for AwaitingValidation tasks
        var remainingSlots = availableSlots - dispatched;
        if (remainingSlots <= 0) return;

        var awaitingValidation = await _taskManager.GetAwaitingValidationAsync();
        var activeRunTaskIds = activeRuns.Select(r => r.TaskId).ToHashSet();

        foreach (var task in awaitingValidation)
        {
            if (remainingSlots <= 0) break;
            if (activeRunTaskIds.Contains(task.Id)) continue;

            var gateAgent = await FindGateAgentAsync(task);
            if (gateAgent == null)
            {
                _logger.LogWarning("No active Gate agent found for validation of task '{TaskId}'", task.Id);
                continue;
            }

            _logger.LogInformation("Dispatching validation of '{Title}' to Oracle '{Agent}'",
                task.Title, gateAgent.Name);

            remainingSlots--;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _runner.RunTaskAsync(gateAgent, task, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Oracle validation failed for task '{TaskId}'", task.Id);
                }
            }, ct);
        }
    }

    private async Task ReconcileParentTasksAsync()
    {
        var allTasks = await _taskManager.GetAllAsync();

        // Find tasks that are parents (have children pointing to them) and are still InProgress
        var childTasksByParent = allTasks
            .Where(t => t.ParentTaskId != null)
            .GroupBy(t => t.ParentTaskId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (parentId, children) in childTasksByParent)
        {
            var parent = allTasks.FirstOrDefault(t => t.Id == parentId);
            if (parent == null || parent.Status != TaskItemStatus.InProgress) continue;

            var allChildrenDone = children.All(c =>
                c.Status is TaskItemStatus.Done or TaskItemStatus.Skipped);
            var anyChildPermanentlyFailed = children.Any(c =>
                c.Status == TaskItemStatus.Failed && c.AttemptCount >= _config.MaxTaskRetries);

            if (allChildrenDone)
            {
                await _taskManager.SetStatusAsync(parent.Id, TaskItemStatus.Done);
                await _comms.LogActivityAsync("parent_task_completed",
                    $"Parent task '{parent.Title}' completed (all {children.Count} children done)",
                    taskId: parent.Id, missionId: parent.MissionId);
            }
            else if (anyChildPermanentlyFailed)
            {
                await _taskManager.SetStatusAsync(parent.Id, TaskItemStatus.Failed);
                await _comms.LogActivityAsync("parent_task_failed",
                    $"Parent task '{parent.Title}' failed (child task permanently failed)",
                    taskId: parent.Id, missionId: parent.MissionId);
            }
        }
    }

    private async Task<AgentDefinition?> FindGateAgentAsync(TaskItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.ValidatedBy))
        {
            var specific = await _agents.GetByIdAsync(task.ValidatedBy);
            if (specific != null && specific.Status == "active") return specific;
        }

        var agents = await _agents.GetAllAsync();
        return agents.FirstOrDefault(a => a.AgentType == AgentType.Gate && a.Status == "active");
    }
}
