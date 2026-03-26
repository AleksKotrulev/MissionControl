namespace MissionControl.Missions;

public class Mission
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public MissionStatus Status { get; set; } = MissionStatus.Running;
    public List<string> TaskIds { get; set; } = [];
    public int MaxParallelAgents { get; set; } = 2;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<MissionHistoryEntry> History { get; set; } = [];
}
