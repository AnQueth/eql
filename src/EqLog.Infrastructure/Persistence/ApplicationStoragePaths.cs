namespace EqLog.Infrastructure.Persistence;

/// <summary>The existing app-owned storage layout; explicit folders support isolated integration tests.</summary>
public sealed class ApplicationStoragePaths
{
    public ApplicationStoragePaths(string? folder = null)
    {
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EqLogOverlay");
    }

    public string Folder
    {
        get;
    }
    public string Settings
    {
        get
        {
            return Path.Combine(Folder, "settings.json");
        }
    }
    public string Durations
    {
        get
        {
            return Path.Combine(Folder, "spell-durations.json");
        }
    }
    public string Spawns
    {
        get
        {
            return Path.Combine(Folder, "spawn-timers.json");
        }
    }
    public string Motes
    {
        get
        {
            return Path.Combine(Folder, "motes.json");
        }
    }
}
