namespace MissionControl.Agents;

public class AgentDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];
    public List<string> SkillIds { get; set; } = [];
    public string Status { get; set; } = "active";
    public string WorkingDirectory { get; set; } = "";
}
