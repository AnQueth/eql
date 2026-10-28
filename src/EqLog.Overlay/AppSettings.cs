using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EqLog.Core.Parsing;
using EqLog.Core.Triggers;

namespace EqLog.Overlay;

public sealed class OverlayPlacement
{
    public double Left
    {
        get; set;
    }

    public double Top
    {
        get; set;
    }

    public double Width
    {
        get; set;
    }

    public double Height
    {
        get; set;
    }

    public bool Visible { get; set; } = true;

    /// <summary>Shown as a tile in the docked-panels window instead of as a floating overlay.</summary>
    public bool Docked
    {
        get; set;
    }

    /// <summary>Individual text size; null inherits the previous global setting when first loaded.</summary>
    public double? FontSize
    {
        get; set;
    }

    /// <summary>Individual background opacity; null inherits the previous global setting when first loaded.</summary>
    public double? BackgroundOpacity
    {
        get; set;
    }

    /// <summary>Individual floating-window click-through lock; null inherits the previous global lock.</summary>
    public bool? Locked
    {
        get; set;
    }

    /// <summary>Tile size in the docked-panels window (0 = use the overlay size).</summary>
    public double DockWidth
    {
        get; set;
    }

    public double DockHeight
    {
        get; set;
    }

    /// <summary>Freeform canvas position, separate from the floating-window position.</summary>
    public double? DockLeft
    {
        get; set;
    }

    public double? DockTop
    {
        get; set;
    }
}

public sealed class DockWindowSettings
{
    public double? Left
    {
        get; set;
    }

    public double? Top
    {
        get; set;
    }

    public double Width
    {
        get; set;
    }

    public double Height
    {
        get; set;
    }

    public bool Maximized
    {
        get; set;
    }

    public bool Topmost
    {
        get; set;
    }

    /// <summary>Show the settings side panel.</summary>
    public bool SidePanelOpen { get; set; } = true;

    /// <summary>Width of the settings side panel; 0 until first saved.</summary>
    public double SidePanelWidth
    {
        get; set;
    }

    /// <summary>Tile order by overlay key.</summary>
    public List<string> Order { get; set; } = [];
}

public sealed class AppSettings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EqLogOverlay");
    public static readonly string SettingsPath = Path.Combine(Folder, "settings.json");
    public static readonly string DurationsPath = Path.Combine(Folder, "spell-durations.json");
    public static readonly string SpawnsPath = Path.Combine(Folder, "spawn-timers.json");

    public static readonly string MotesPath = Path.Combine(Folder, "motes.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Explicit log file. When null the newest eqlog file in <see cref="LogDirectory"/> is used.</summary>
    public string? LogPath
    {
        get; set;
    }

    public string? LogDirectory
    {
        get; set;
    }

    /// <summary>Switch automatically when a newer eqlog file appears (e.g. you log in another character).</summary>
    public bool FollowNewest { get; set; } = true;

    public bool Locked
    {
        get; set;
    }

    /// <summary>Hide floating overlays while the EverQuest window isn't visible (not running, minimized or covered).</summary>
    public bool OnlyWhenGameVisible { get; set; } = true;

    public double BackgroundOpacity { get; set; } = 0.55;

    public double FontSize { get; set; } = 13;

    public bool ResetOnLogin { get; set; } = true;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool AutomaticallyDownloadUpdates { get; set; } = true;
    public double MobInfoTimeoutSeconds { get; set; } = 5;
    public string NpcSearch { get; set; } = "";
    public int NpcView
    {
        get; set;
    }
    public string? NpcSelectedMob
    {
        get; set;
    }
    public string? NpcSelectedZone
    {
        get; set;
    }
    public string SkyClass { get; set; } = "All classes";
    public bool QuestRecentFirst { get; set; } = true;
    public string QuestFilter { get; set; } = "";
    public int LootGrouping
    {
        get; set;
    }
    public EqLog.Core.ThemePalette? Theme
    {
        get; set;
    }

    public Dictionary<string, OverlayPlacement> Overlays { get; set; } = [];

    public DockWindowSettings DockWindow { get; set; } = new();

    public List<ChatChannel> ChatChannels
    {
        get; set;
    } =
        [ChatChannel.Tell, ChatChannel.Group, ChatChannel.Guild, ChatChannel.Raid, ChatChannel.Fellowship, ChatChannel.Say, ChatChannel.Shout];

    /// <summary>Custom alerts. They fire only for lines written while the app is following the log, never for history.</summary>
    public List<AlertTrigger> Triggers { get; set; } = AlertTrigger.Defaults();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Fall back to defaults if the settings file is unreadable.
        }

        return new AppSettings();
    }

    private string? _lastSaved;

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, Json);
        if (json == _lastSaved && File.Exists(SettingsPath))
        {
            return;
        }

        EqLog.Core.AtomicFile.WriteAllText(SettingsPath, json);
        _lastSaved = json;
    }
}
