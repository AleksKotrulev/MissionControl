using System.Text;
using MissionControl.Agents;
using MissionControl.Communication;
using MissionControl.Missions;
using MissionControl.Tasks;

namespace MissionControl.Orchestrator;

public class PromptBuilder
{
    private readonly CommunicationManager _comms;
    private readonly AgentRegistry _agents;

    public PromptBuilder(CommunicationManager comms, AgentRegistry agents)
    {
        _comms = comms;
        _agents = agents;
    }

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

        // 4. Agent-type-specific sections
        if (agent.AgentType == AgentType.Coordinator)
            await AppendCoordinatorInstructions(sb);
        else if (agent.AgentType == AgentType.Gate)
            AppendGateInstructions(sb, task);
        else if (agent.AgentType == AgentType.Analyst)
            AppendAnalystInstructions(sb);

        // 5. Context overlays
        if (task.AttemptCount > 0)
        {
            sb.AppendLine("# Retry Context");
            sb.AppendLine($"This is attempt #{task.AttemptCount + 1}. Previous attempts failed.");
            if (!string.IsNullOrWhiteSpace(task.ValidationFeedback))
            {
                sb.AppendLine("## Validation Feedback from Previous Attempt");
                sb.AppendLine(task.ValidationFeedback);
            }
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

    private async Task AppendCoordinatorInstructions(StringBuilder sb)
    {
        sb.AppendLine("# Coordinator Instructions");
        sb.AppendLine("You are a task decomposition agent. Your job is to break down this task into concrete subtasks that can be assigned to builder agents.");
        sb.AppendLine();
        sb.AppendLine("## Available Builder Agents");
        var agents = await _agents.GetActiveAsync();
        foreach (var a in agents.Where(a => a.AgentType == AgentType.Builder))
            sb.AppendLine($"- **{a.Id}** ({a.Name}): {a.Role} — {string.Join(", ", a.Capabilities)}");
        sb.AppendLine();
        sb.AppendLine("## Output Format");
        sb.AppendLine("You MUST output ONLY a JSON array of subtask objects. No other text, no markdown, no explanation.");
        sb.AppendLine("Each object must have these fields:");
        sb.AppendLine("```");
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title\",");
        sb.AppendLine("    \"description\": \"What needs to be done\",");
        sb.AppendLine("    \"acceptanceCriteria\": \"How to verify it's done correctly\",");
        sb.AppendLine("    \"assignedTo\": \"<agent-id from the list above>\",");
        sb.AppendLine("    \"estimatedMinutes\": 15,");
        sb.AppendLine("    \"blockedBy\": []");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine("If a subtask depends on another subtask from this batch, add its zero-based index to \"blockedBy\".");
        sb.AppendLine("Do NOT write code. Do NOT execute tasks yourself. Only decompose and delegate.");
        sb.AppendLine();
    }

    private static void AppendGateInstructions(StringBuilder sb, TaskItem task)
    {
        sb.AppendLine("# Validation Instructions");
        sb.AppendLine("You are a quality gate. Review the builder's work output and validate it against the acceptance criteria.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(task.AcceptanceCriteria))
        {
            sb.AppendLine("## Acceptance Criteria Checklist");
            sb.AppendLine(task.AcceptanceCriteria);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(task.LastOutput))
        {
            sb.AppendLine("## Builder Output to Validate");
            sb.AppendLine(task.LastOutput);
            sb.AppendLine();
        }
        sb.AppendLine("## Output Format");
        sb.AppendLine("You MUST output ONLY a JSON object with your verdict. No other text.");
        sb.AppendLine("```");
        sb.AppendLine("{\"verdict\": \"PASS\" or \"FAIL\", \"feedback\": \"Detailed explanation\"}");
        sb.AppendLine("```");
        sb.AppendLine("If FAIL, explain specifically what is wrong and what needs to change.");
        sb.AppendLine("If PASS, confirm which criteria were satisfied.");
        sb.AppendLine();
    }

    private static void AppendAnalystInstructions(StringBuilder sb)
    {
        sb.AppendLine("# Analyst Guardrails");
        sb.AppendLine("You are a READ-ONLY research agent. You MUST NOT:");
        sb.AppendLine("- Create, modify, or delete any files");
        sb.AppendLine("- Run any commands that change system state (git commit, npm install, etc.)");
        sb.AppendLine("- Execute any build or test commands that produce side effects");
        sb.AppendLine();
        sb.AppendLine("You MAY:");
        sb.AppendLine("- Read files, search codebases, explore directories");
        sb.AppendLine("- Run read-only commands (git log, git diff, find, grep, etc.)");
        sb.AppendLine("- Analyze patterns, architectures, and dependencies");
        sb.AppendLine();
        sb.AppendLine("Output a structured summary of your findings.");
        sb.AppendLine();
    }
}
