namespace MissionControl.Missions;

public enum MissionStatus
{
    Running,
    Completed,
    Stalled
}

public class MissionHistoryEntry
{
    public string TaskId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
