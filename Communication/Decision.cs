namespace MissionControl.Communication;

public class Decision
{
    public string Id { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Question { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public string Context { get; set; } = "";
    public string? Response { get; set; }
    public string Status { get; set; } = "pending"; // pending, resolved
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}
