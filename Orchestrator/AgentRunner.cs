using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MissionControl.Agents;
using MissionControl.Communication;
using MissionControl.Config;
using MissionControl.Data;
using MissionControl.Missions;
using MissionControl.Tasks;

namespace MissionControl.Orchestrator;

public class AgentRunner
{
    private readonly MissionControlConfig _config;
    private readonly PromptBuilder _promptBuilder;
    private readonly TaskManager _taskManager;
    private readonly CommunicationManager _comms;
    private readonly MissionOrchestrator _missions;
    private readonly AgentRegistry _agents;

    public AgentRunner(
        MissionControlConfig config,
        PromptBuilder promptBuilder,
        TaskManager taskManager,
        CommunicationManager comms,
        MissionOrchestrator missions,
        AgentRegistry agents)
    {
        _config = config;
        _promptBuilder = promptBuilder;
        _taskManager = taskManager;
        _comms = comms;
        _missions = missions;
        _agents = agents;
    }

    public async Task<bool> RunTaskAsync(AgentDefinition agent, TaskItem task, CancellationToken ct)
    {
        Mission? mission = null;
        if (task.MissionId != null)
            mission = await _missions.GetByIdAsync(task.MissionId);

        var prompt = await _promptBuilder.BuildTaskPromptAsync(agent, task, mission);

        // Register active run
        var run = new ActiveRun
        {
            TaskId = task.Id,
            AgentId = agent.Id,
            ContinuationIndex = 0
        };

        await RegisterRunAsync(run);
        await _taskManager.SetStatusAsync(task.Id, TaskItemStatus.InProgress);
        await _taskManager.IncrementAttemptAsync(task.Id);
        await _comms.LogActivityAsync("agent_spawned",
            $"Agent '{agent.Name}' started task '{task.Title}'",
            agent.Id, task.Id, task.MissionId);

        try
        {
            var workDir = !string.IsNullOrWhiteSpace(agent.WorkingDirectory)
                ? Path.Combine(_config.WorkingDirectory, agent.WorkingDirectory)
                : _config.WorkingDirectory;

            var (exitCode, output) = await SpawnClaudeCodeAsync(prompt, workDir, ct);

            if (exitCode == 0)
            {
                return await HandleSuccessAsync(agent, task, output);
            }
            else
            {
                await _taskManager.SetStatusAsync(task.Id, TaskItemStatus.Failed);
                await _comms.PostFailureReportAsync(agent.Id, task.Id, task.Title, output);
                await _comms.LogActivityAsync("task_failed",
                    $"Task '{task.Title}' failed (attempt {task.AttemptCount}): {TruncateOutput(output)}",
                    agent.Id, task.Id, task.MissionId);

                if (task.MissionId != null)
                    await _missions.AddHistoryEntryAsync(task.MissionId, task.Id, agent.Id, "failed");

                return false;
            }
        }
        finally
        {
            await UnregisterRunAsync(run.Id);
        }
    }

    private async Task<bool> HandleSuccessAsync(AgentDefinition agent, TaskItem task, string output)
    {
        switch (agent.AgentType)
        {
            case AgentType.Coordinator:
                var created = await HandleCoordinatorOutputAsync(agent, task, output);
                if (created)
                {
                    // Parent stays InProgress — children will execute,
                    // then Dispatcher reconciliation completes the parent
                    await _comms.LogActivityAsync("coordinator_decomposed",
                        $"Coordinator '{agent.Name}' decomposed '{task.Title}' into child tasks",
                        agent.Id, task.Id, task.MissionId);
                    return true;
                }
                // Failed to parse output — treat as failure
                await _taskManager.SetStatusAsync(task.Id, TaskItemStatus.Failed);
                await _comms.PostFailureReportAsync(agent.Id, task.Id, task.Title,
                    "Coordinator output could not be parsed as subtask definitions");
                await _comms.LogActivityAsync("coordinator_parse_failed",
                    $"Failed to parse Coordinator output for '{task.Title}'",
                    agent.Id, task.Id, task.MissionId);
                return false;

            case AgentType.Gate:
                await HandleGateOutputAsync(agent, task, output);
                return true;

            case AgentType.Builder when await ShouldValidateAsync(task):
                await _taskManager.UpdateFieldsAsync(task.Id, t =>
                {
                    t.LastOutput = TruncateOutput(output, 5000);
                });
                await _taskManager.SetStatusAsync(task.Id, TaskItemStatus.AwaitingValidation);
                await _comms.PostCompletionReportAsync(agent.Id, task.Id, task.Title, output);
                await _comms.LogActivityAsync("task_awaiting_validation",
                    $"Task '{task.Title}' completed by {agent.Name}, awaiting Oracle validation",
                    agent.Id, task.Id, task.MissionId);

                if (task.MissionId != null)
                    await _missions.AddHistoryEntryAsync(task.MissionId, task.Id, agent.Id, "awaiting_validation");

                return true;

            default:
                // Builder without validation, Analyst — standard Done
                await _taskManager.SetStatusAsync(task.Id, TaskItemStatus.Done);
                await _comms.PostCompletionReportAsync(agent.Id, task.Id, task.Title, output);
                await _comms.LogActivityAsync("task_completed",
                    $"Task '{task.Title}' completed by {agent.Name}",
                    agent.Id, task.Id, task.MissionId);

                if (task.MissionId != null)
                    await _missions.AddHistoryEntryAsync(task.MissionId, task.Id, agent.Id, "completed");

                return true;
        }
    }

    private async Task<bool> HandleCoordinatorOutputAsync(AgentDefinition agent, TaskItem parentTask, string output)
    {
        var resultText = ExtractResultText(output);
        if (resultText == null) return false;

        List<CoordinatorSubtaskDefinition>? subtaskDefs;
        try
        {
            subtaskDefs = JsonSerializer.Deserialize<List<CoordinatorSubtaskDefinition>>(
                resultText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return false;
        }

        if (subtaskDefs == null || subtaskDefs.Count == 0) return false;

        // Convert definitions to TaskItems
        var childTasks = subtaskDefs.Select(def => new TaskItem
        {
            Title = def.Title,
            Description = def.Description,
            AcceptanceCriteria = def.AcceptanceCriteria,
            AssignedTo = def.AssignedTo,
            EstimatedMinutes = def.EstimatedMinutes,
            ParentTaskId = parentTask.Id,
            MissionId = parentTask.MissionId,
            Priority = parentTask.Priority
        }).ToList();

        // Create all child tasks
        var created = await _taskManager.CreateBatchAsync(childTasks);

        // Resolve blockedBy (index-based references to actual task IDs)
        for (int i = 0; i < subtaskDefs.Count; i++)
        {
            if (subtaskDefs[i].BlockedBy.Count > 0)
            {
                created[i].BlockedBy = subtaskDefs[i].BlockedBy
                    .Select(idx => int.TryParse(idx, out var n) && n >= 0 && n < created.Count
                        ? created[n].Id : idx)
                    .ToList();
                await _taskManager.UpdateAsync(created[i]);
            }
        }

        // Add child task IDs to mission if applicable
        if (parentTask.MissionId != null)
            await _missions.AddTasksToMissionAsync(parentTask.MissionId, created.Select(t => t.Id).ToList());

        return true;
    }

    private async Task HandleGateOutputAsync(AgentDefinition agent, TaskItem task, string output)
    {
        var resultText = ExtractResultText(output);
        string verdict = "FAIL";
        string feedback = "Oracle output could not be parsed";

        if (resultText != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(resultText);
                verdict = doc.RootElement.TryGetProperty("verdict", out var v) ? v.GetString() ?? "FAIL" : "FAIL";
                feedback = doc.RootElement.TryGetProperty("feedback", out var f) ? f.GetString() ?? "" : "";
            }
            catch { /* parse failure — defaults to FAIL */ }
        }

        if (verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            await _taskManager.SetStatusAsync(task.Id, TaskItemStatus.Done);
            await _comms.PostCompletionReportAsync(agent.Id, task.Id, task.Title,
                $"Oracle validated: PASS — {feedback}");
            await _comms.LogActivityAsync("validation_passed",
                $"Oracle '{agent.Name}' validated '{task.Title}': PASS",
                agent.Id, task.Id, task.MissionId);

            if (task.MissionId != null)
                await _missions.AddHistoryEntryAsync(task.MissionId, task.Id, agent.Id, "validated_pass");
        }
        else
        {
            await _taskManager.UpdateFieldsAsync(task.Id, t =>
            {
                t.ValidationResult = "FAIL";
                t.ValidationFeedback = feedback;
                t.Status = TaskItemStatus.Failed;
            });
            await _comms.PostFailureReportAsync(agent.Id, task.Id, task.Title,
                $"Oracle validation FAILED: {feedback}");
            await _comms.LogActivityAsync("validation_failed",
                $"Oracle '{agent.Name}' rejected '{task.Title}': {TruncateOutput(feedback)}",
                agent.Id, task.Id, task.MissionId);

            if (task.MissionId != null)
                await _missions.AddHistoryEntryAsync(task.MissionId, task.Id, agent.Id, "validated_fail");
        }
    }

    private async Task<bool> ShouldValidateAsync(TaskItem task)
    {
        if (string.IsNullOrWhiteSpace(task.AcceptanceCriteria)) return false;
        var agents = await _agents.GetActiveAsync();
        return agents.Any(a => a.AgentType == AgentType.Gate);
    }

    private static string? ExtractResultText(string jsonOutput)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            if (doc.RootElement.TryGetProperty("result", out var result))
                return result.GetString();
        }
        catch { /* not valid JSON */ }
        return null;
    }

    private async Task<(int ExitCode, string Output)> SpawnClaudeCodeAsync(
        string prompt, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.ClaudeCodePath,
            Arguments = $"--dangerously-skip-permissions -p \"{EscapeForShell(prompt)}\" --output-format json --max-turns {_config.MaxTurnsPerSession}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_config.TaskTimeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "Task timed out or was cancelled");
        }

        var output = stdout.Length > 0 ? stdout.ToString() : stderr.ToString();
        return (process.ExitCode, output);
    }

    private static string EscapeForShell(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    private static string TruncateOutput(string output, int maxLength = 200) =>
        output.Length > maxLength ? output[..maxLength] + "..." : output;

    private async Task RegisterRunAsync(ActiveRun run)
    {
        await JsonDataStore.MutateAsync<List<ActiveRun>>(DataPaths.ActiveRuns, runs =>
        {
            runs.Add(run);
            return runs;
        });
    }

    private async Task UnregisterRunAsync(string runId)
    {
        await JsonDataStore.MutateAsync<List<ActiveRun>>(DataPaths.ActiveRuns, runs =>
        {
            runs.RemoveAll(r => r.Id == runId);
            return runs;
        });
    }

    public async Task<List<ActiveRun>> GetActiveRunsAsync() =>
        await JsonDataStore.ReadAsync<List<ActiveRun>>(DataPaths.ActiveRuns);

    public async Task KillAllRunsAsync()
    {
        var runs = await GetActiveRunsAsync();
        foreach (var run in runs)
        {
            try { Process.GetProcessById(run.ProcessId).Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        }
        await JsonDataStore.WriteAsync(DataPaths.ActiveRuns, new List<ActiveRun>());
        await _comms.LogActivityAsync("emergency_stop", $"Killed {runs.Count} active agent sessions");
    }
}
