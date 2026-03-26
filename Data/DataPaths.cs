namespace MissionControl.Data;

public static class DataPaths
{
    private static string _baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

    public static void SetBaseDirectory(string path) => _baseDir = path;
    public static string BaseDir => _baseDir;

    public static string Agents => Path.Combine(_baseDir, "agents.json");
    public static string Tasks => Path.Combine(_baseDir, "tasks.json");
    public static string Missions => Path.Combine(_baseDir, "missions.json");
    public static string Inbox => Path.Combine(_baseDir, "inbox.json");
    public static string Decisions => Path.Combine(_baseDir, "decisions.json");
    public static string ActivityLog => Path.Combine(_baseDir, "activity-log.json");
    public static string ActiveRuns => Path.Combine(_baseDir, "active-runs.json");
    public static string Config => Path.Combine(_baseDir, "config.json");
}
