using System.Text;
using MissionControl.Agents;
using MissionControl.Communication;
using MissionControl.Missions;
using MissionControl.Tasks;

namespace MissionControl.Orchestrator;

public class PromptBuilder
{
    private readonly CommunicationManager _comms;

    public PromptBuilder(CommunicationManager comms) => _comms = comms;

    public async Task<string> BuildTaskPromptAsync(
        AgentDefinition agent, TaskItem task, Mission? mission)
    {
        var sb = new StringBuilder();

        // 1. Agent persona
        sb.AppendLine("# Agent Identity");
        sb.AppendLine($"You are **{agent.Name}** ({agent.Role}).");
        sb.AppendLine(agent.Description);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(agent.Instructions))
        {
            sb.AppendLine("## Instructions");
            sb.AppendLine(agent.Instructions);
            sb.AppendLine();
        }
        if (agent.Capabilities.Count > 0)
        {
            sb.AppendLine("## Capabilities");
            foreach (var cap in agent.Capabilities)
                sb.AppendLine($"- {cap}");
            sb.AppendLine();
        }

        // 2. Task details
        sb.AppendLine("# Task");
        sb.AppendLine($"**Title:** {task.Title}");
        sb.AppendLine($"**Priority:** {task.Priority}");
        if (!string.IsNullOrWhiteSpace(task.Description))
            sb.AppendLine($"**Description:** {task.Description}");
        if (!string.IsNullOrWhiteSpace(task.AcceptanceCriteria))
        {
            sb.AppendLine("**Acceptance Criteria:**");
            sb.AppendLine(task.AcceptanceCriteria);
        }
        if (task.SubTasks.Count > 0)
        {
            sb.AppendLine("**Subtasks:**");
            foreach (var st in task.SubTasks)
                sb.AppendLine($"- [{(st.Completed ? "x" : " ")}] {st.Title}");
        }
        if (task.EstimatedMinutes > 0)
            sb.AppendLine($"**Estimated time:** {task.EstimatedMinutes} minutes");
        sb.AppendLine();

        // 3. Guardrails
        sb.AppendLine("# Standard Operating Procedures");
        sb.AppendLine("- Work within the project directory specified for your role.");
        sb.AppendLine("- Follow existing code patterns and conventions (see CLAUDE.md).");
        sb.AppendLine("- Run `dotnet build` to verify your changes compile.");
        sb.AppendLine("- Do NOT modify mission control data files (tasks.json, inbox.json, etc.).");
        sb.AppendLine("- If you need a human decision, clearly state the question and options in your output.");
        sb.AppendLine("- On completion, summarize what you did and any follow-up actions needed.");
        sb.AppendLine();

        // 4. Context overlays
        if (task.AttemptCount > 0)
        {
            sb.AppendLine("# Retry Context");
            sb.AppendLine($"This is attempt #{task.AttemptCount + 1}. Previous attempts failed.");
            sb.AppendLine("Try a different approach than before.");
            sb.AppendLine();
        }

        if (mission != null && mission.History.Count > 0)
        {
            sb.AppendLine("# Mission Context");
            sb.AppendLine($"This task is part of mission: **{mission.Title}**");
            var recentHistory = mission.History.TakeLast(10);
            foreach (var entry in recentHistory)
                sb.AppendLine($"- [{entry.Timestamp:HH:mm}] Task {entry.TaskId} by {entry.AgentId}: {entry.Outcome}");
            sb.AppendLine();
        }

        // Check for resolved decisions
        var decisions = await _comms.GetDecisionsAsync();
        var resolved = decisions.Where(d => d.TaskId == task.Id && d.Status == "resolved").ToList();
        if (resolved.Count > 0)
        {
            sb.AppendLine("# Human Decisions");
            foreach (var d in resolved)
                sb.AppendLine($"- Q: {d.Question} → A: {d.Response}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
