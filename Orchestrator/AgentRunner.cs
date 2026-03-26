using System.Diagnostics;
using System.Text;
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

    public AgentRunner(
        MissionControlConfig config,
        PromptBuilder promptBuilder,
        TaskManager taskManager,
        CommunicationManager comms,
        MissionOrchestrator missions)
    {
        _config = config;
        _promptBuilder = promptBuilder;
        _taskManager = taskManager;
        _comms = comms;
        _missions = missions;
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
                await _taskManager.SetStatusAsync(task.Id, TaskItemStatus.Done);
                await _comms.PostCompletionReportAsync(agent.Id, task.Id, task.Title, output);
                await _comms.LogActivityAsync("task_completed",
                    $"Task '{task.Title}' completed by {agent.Name}",
                    agent.Id, task.Id, task.MissionId);

                if (task.MissionId != null)
                    await _missions.AddHistoryEntryAsync(task.MissionId, task.Id, agent.Id, "completed");

                return true;
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
