using MissionControl.Data;

namespace MissionControl.Communication;

public class CommunicationManager
{
    // Inbox
    public async Task<List<InboxMessage>> GetInboxAsync() =>
        await JsonDataStore.ReadAsync<List<InboxMessage>>(DataPaths.Inbox);

    public async Task<List<InboxMessage>> GetInboxForAgentAsync(string agentId)
    {
        var messages = await GetInboxAsync();
        return messages.Where(m => m.To == agentId).ToList();
    }

    public async Task SendMessageAsync(InboxMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
            message.Id = Guid.NewGuid().ToString("N")[..8];
        message.CreatedAt = DateTime.UtcNow;

        await JsonDataStore.MutateAsync<List<InboxMessage>>(DataPaths.Inbox, messages =>
        {
            messages.Add(message);
            return messages;
        });
    }

    public async Task PostCompletionReportAsync(string agentId, string taskId, string taskTitle, string result)
    {
        await SendMessageAsync(new InboxMessage
        {
            From = agentId,
            To = "human",
            Type = "report",
            Subject = $"Completed: {taskTitle}",
            Body = result,
            TaskId = taskId
        });
    }

    public async Task PostFailureReportAsync(string agentId, string taskId, string taskTitle, string error)
    {
        await SendMessageAsync(new InboxMessage
        {
            From = agentId,
            To = "human",
            Type = "report",
            Subject = $"Failed: {taskTitle}",
            Body = error,
            TaskId = taskId
        });
    }

    // Decisions
    public async Task<List<Decision>> GetDecisionsAsync() =>
        await JsonDataStore.ReadAsync<List<Decision>>(DataPaths.Decisions);

    public async Task<List<Decision>> GetPendingDecisionsAsync()
    {
        var decisions = await GetDecisionsAsync();
        return decisions.Where(d => d.Status == "pending").ToList();
    }

    public async Task RequestDecisionAsync(Decision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Id))
            decision.Id = Guid.NewGuid().ToString("N")[..8];
        decision.CreatedAt = DateTime.UtcNow;

        await JsonDataStore.MutateAsync<List<Decision>>(DataPaths.Decisions, decisions =>
        {
            decisions.Add(decision);
            return decisions;
        });

        await LogActivityAsync("decision_requested",
            $"Agent '{decision.AgentId}' requests decision for task '{decision.TaskId}': {decision.Question}",
            decision.AgentId, decision.TaskId);
    }

    public async Task ResolveDecisionAsync(string decisionId, string response)
    {
        await JsonDataStore.MutateAsync<List<Decision>>(DataPaths.Decisions, decisions =>
        {
            var decision = decisions.FirstOrDefault(d => d.Id == decisionId);
            if (decision != null)
            {
                decision.Response = response;
                decision.Status = "resolved";
                decision.ResolvedAt = DateTime.UtcNow;
            }
            return decisions;
        });
    }

    public async Task<bool> HasPendingDecisionForTaskAsync(string taskId)
    {
        var decisions = await GetPendingDecisionsAsync();
        return decisions.Any(d => d.TaskId == taskId);
    }

    // Activity Log
    public async Task<List<ActivityEvent>> GetActivityLogAsync() =>
        await JsonDataStore.ReadAsync<List<ActivityEvent>>(DataPaths.ActivityLog);

    public async Task LogActivityAsync(string eventType, string description,
        string? agentId = null, string? taskId = null, string? missionId = null)
    {
        await JsonDataStore.MutateAsync<List<ActivityEvent>>(DataPaths.ActivityLog, log =>
        {
            log.Add(new ActivityEvent
            {
                EventType = eventType,
                Description = description,
                AgentId = agentId,
                TaskId = taskId,
                MissionId = missionId
            });

            // Rotate: keep last 500 entries
            if (log.Count > 500)
                log = log.Skip(log.Count - 500).ToList();

            return log;
        });
    }
}
