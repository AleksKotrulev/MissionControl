namespace MissionControl.Config;

public class MissionControlConfig
{
    public int MaxParallelAgents { get; set; } = 2;
    public int DispatchIntervalSeconds { get; set; } = 10;
    public int MaxTaskRetries { get; set; } = 3;
    public int TaskTimeoutMinutes { get; set; } = 30;
    public int MaxTurnsPerSession { get; set; } = 50;
    public int MaxContinuations { get; set; } = 3;
    public string ClaudeCodePath { get; set; } = "claude";
    public string WorkingDirectory { get; set; } = "";
    public int ApiPort { get; set; } = 5100;
}
