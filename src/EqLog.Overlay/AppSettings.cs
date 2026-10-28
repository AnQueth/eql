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

    public OverlayPlacement Clone()
    {
        return (OverlayPlacement)MemberwiseClone();
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

    /// <summary>
    /// Automatically rearrange docked panels (reflow to fill empty space). When false, panels move only when
    /// the user drags or resizes them.
    /// </summary>
    public bool AutoArrange { get; set; }

    /// <summary>Tile order by overlay key.</summary>
    public List<string> Order { get; set; } = [];

    public DockWindowSettings Clone()
    {
        var copy = (DockWindowSettings)MemberwiseClone();
        copy.Order = Order is null ? null! : [.. Order];
        return copy;
    }
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
    public int SkyView
    {
        get; set;
    }
    public string SkyFilter { get; set; } = "";

    /// <summary>Watched Plane of Sky quests as "Class|Quest name" keys; may span classes.</summary>
    public List<string> SkyWatched { get; set; } = [];
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

    public static async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                await using var stream = EqLog.Core.AtomicFile.OpenRead(SettingsPath);
                return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Json, ct).ConfigureAwait(false)
                    ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Fall back to defaults if the settings file is unreadable.
        }

        return new AppSettings();
    }

    private byte[]? _lastSavedHash;
    private long _saveSequence;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>
    /// Takes a detached snapshot now (call on the UI thread, which owns the settings), then streams it to disk
    /// off the UI thread. Unchanged content is detected by hash, so neither the JSON nor the last save is kept in memory.
    /// Writes are ordered; a save superseded by a newer one while waiting is skipped.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        AppSettings snapshot = Snapshot();
        long sequence = Interlocked.Increment(ref _saveSequence);
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (sequence != Interlocked.Read(ref _saveSequence))
            {
                return;
            }

            byte[] hash = EqLog.Core.AtomicFile.HashJson(snapshot, Json);
            if (_lastSavedHash is not null && hash.AsSpan().SequenceEqual(_lastSavedHash) && File.Exists(SettingsPath))
            {
                return;
            }

            await EqLog.Core.AtomicFile.WriteJsonAsync(SettingsPath, snapshot, Json, ct).ConfigureAwait(false);
            _lastSavedHash = hash;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Copy of the persisted object graph (collections and mutable children; strings are shared),
    /// so serialization can stream on a worker thread while the UI keeps editing the live settings.
    /// </summary>
    internal AppSettings Snapshot()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SkyWatched = SkyWatched is null ? null! : [.. SkyWatched];
        copy.Theme = Theme?.Clone();
        copy.Overlays = Overlays is null
            ? null!
            : Overlays.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone()!, Overlays.Comparer);
        copy.DockWindow = DockWindow?.Clone()!;
        copy.ChatChannels = ChatChannels is null ? null! : [.. ChatChannels];
        copy.Triggers = Triggers is null ? null! : Triggers.Select(trigger => trigger?.Clone()!).ToList();
        return copy;
    }
}
