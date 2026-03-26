namespace MissionControl.Tasks;

public class TaskItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public string AssignedTo { get; set; } = "";
    public List<string> Collaborators { get; set; } = [];
    public TaskItemStatus Status { get; set; } = TaskItemStatus.NotStarted;
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public List<SubTask> SubTasks { get; set; } = [];
    public List<string> BlockedBy { get; set; } = [];
    public int EstimatedMinutes { get; set; }
    public int ActualMinutes { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? MissionId { get; set; }
    public List<TaskComment> Comments { get; set; } = [];
    public string? ParentTaskId { get; set; }
    public string? ValidationResult { get; set; }
    public string? ValidationFeedback { get; set; }
    public string? ValidatedBy { get; set; }
    public string? LastOutput { get; set; }
}
