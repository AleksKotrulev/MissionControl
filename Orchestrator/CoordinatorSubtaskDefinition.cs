namespace MissionControl.Orchestrator;

public class CoordinatorSubtaskDefinition
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public string AssignedTo { get; set; } = "";
    public int EstimatedMinutes { get; set; }
    public List<string> BlockedBy { get; set; } = [];
}
