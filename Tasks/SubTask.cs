namespace MissionControl.Tasks;

public class SubTask
{
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
}

public class TaskComment
{
    public string Author { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
