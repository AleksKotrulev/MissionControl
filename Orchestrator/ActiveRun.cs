namespace MissionControl.Orchestrator;

public class ActiveRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string TaskId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public int ProcessId { get; set; }
    public int ContinuationIndex { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}
