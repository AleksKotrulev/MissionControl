namespace MissionControl.Communication;

public class InboxMessage
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Type { get; set; } = "delegation"; // delegation, report, notification
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "unread"; // unread, read
    public string? TaskId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
