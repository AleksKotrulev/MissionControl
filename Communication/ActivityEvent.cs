namespace MissionControl.Communication;

public class ActivityEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string EventType { get; set; } = "";
    public string Description { get; set; } = "";
    public string? AgentId { get; set; }
    public string? TaskId { get; set; }
    public string? MissionId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
