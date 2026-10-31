using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using EqLog.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace EqLog.Overlay;

internal enum UsageFeature
{
    AbilityBreakdown,
    LootQuestLookup,
    SkyQuestWatchChanged,
    NpcLookup,
    NpcWikiRefresh,
    ThemeChanged,
    WhatsNewOpened,
    DpsPanel,
    DamageTakenPanel,
    HealingPanel,
    SpellTimersPanel,
    SessionPanel,
    LootPanel,
    ChatPanel,
    SpellCastingPanel,
    AlertsPanel,
    SpawnTimersPanel,
    MotesPanel,
    QuestOpportunitiesPanel,
    SkyQuestPanel,
    MobInfoPanel,
    NpcCompendiumPanel,
    WikiActivityPanel,
    InventoryPanel,
    MergePanel,
    ItemUpgradesPanel,
    MoteUpgradePlannerPanel,
    ExaltationsPanel,
    OtherPanel,
}

internal enum UsageErrorSource
{
    Startup,
    StartupData,
    Dispatcher,
    AppDomain,
    TaskScheduler,
    Updates,
    Wiki,
    InventoryImport,
    ResistanceReset,
}

internal static class UsageTelemetry
{
    private const string DefaultConnectionString =
        "InstrumentationKey=7db2a0ee-3a70-40f0-a425-bd4ff75dfd3d;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=f380dd49-dc42-4e5d-a811-8ed222d6d6a7";

    private static readonly object Gate = new();
    private static ILoggerFactory? _loggerFactory;
    private static ILogger? _logger;
    private static bool _enabled;
    private static string _sessionId = "";
    private static readonly string AppVersion = typeof(UsageTelemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public static void SetEnabled(bool enabled)
    {
        SetEnabled(enabled, CreateLoggerFactory);
    }

    internal static void SetEnabled(bool enabled, Func<ILoggerFactory> createLoggerFactory)
    {
        ILoggerFactory? factoryToDispose = null;
        lock (Gate)
        {
            if (_enabled == enabled)
            {
                return;
            }

            if (enabled)
            {
                _loggerFactory = createLoggerFactory();
                _logger = _loggerFactory.CreateLogger("EqLogOverlay.Usage");
                _sessionId = Guid.NewGuid().ToString("N");
                _enabled = true;
            }
            else
            {
                _enabled = false;
                _logger = null;
                factoryToDispose = _loggerFactory;
                _loggerFactory = null;
            }
        }

        factoryToDispose?.Dispose();
    }

    public static void TrackApplicationStarted()
    {
        TrackEvent("app_started");
    }

    public static void TrackFeature(UsageFeature feature)
    {
        TrackEvent("feature_used", [new("feature", feature.ToString())]);
    }

    public static void TrackAction(UsageAction action)
    {
        if (action == UsageAction.None)
        {
            return;
        }

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        TrackEvent("feature_action", [new("action", action.ToString())]);
    }

    public static void TrackTheme(ThemePalette theme, bool followsWindows,
        IReadOnlyCollection<ThemePalette> bundledThemes, bool changed)
    {
        // Never send imported/edited palette names. Only exact shipped palettes get a name.
        bool bundled = bundledThemes.Any(candidate => candidate.Name == theme.Name &&
            ThemePalette.AllColorKeys.All(key => string.Equals(candidate.GetColor(key), theme.GetColor(key),
                StringComparison.OrdinalIgnoreCase)));
        string name = (bundled ? theme.Name : "") switch
        {
            "Neutral Light" or "Neutral Dark" or "High Contrast Dark" or "High Contrast Light" or
            "Enchanted Forest" or "Arctic Frost" or "Midnight Ocean" or "Halloween" or
            "Desert Sand" or "Crimson Ember" or "Royal Amethyst" or
            "Colorblind Safe (Red-Green)" or "Colorblind Safe (Blue-Yellow)" => theme.Name,
            _ => "Custom",
        };
        TrackEvent(changed ? "theme_changed" : "theme_state",
            new("theme", name),
            new("theme.mode", followsWindows ? "Windows" : "Fixed"));
    }

    public static void TrackPanelState(string panelKey, bool visible, bool docked, bool changed)
    {
        TrackEvent(changed ? "panel_changed" : "panel_state",
            new("feature", PanelFeature(panelKey).ToString()),
            new("panel.visible", visible),
            new("panel.placement", docked ? "Docked" : "Floating"));
    }

    public static void TrackPanelOpened(string panelKey)
    {
        TrackFeature(PanelFeature(panelKey));
    }

    private static UsageFeature PanelFeature(string panelKey)
    {
        return panelKey switch
        {
            "dps" => UsageFeature.DpsPanel,
            "taken" => UsageFeature.DamageTakenPanel,
            "heals" => UsageFeature.HealingPanel,
            "timers" => UsageFeature.SpellTimersPanel,
            "session" => UsageFeature.SessionPanel,
            "loot" => UsageFeature.LootPanel,
            "chat" => UsageFeature.ChatPanel,
            "spells" => UsageFeature.SpellCastingPanel,
            "alerts" => UsageFeature.AlertsPanel,
            "spawns" => UsageFeature.SpawnTimersPanel,
            "motes" => UsageFeature.MotesPanel,
            "quests" => UsageFeature.QuestOpportunitiesPanel,
            "sky" => UsageFeature.SkyQuestPanel,
            "mobinfo" => UsageFeature.MobInfoPanel,
            "npcs" => UsageFeature.NpcCompendiumPanel,
            "wiki" => UsageFeature.WikiActivityPanel,
            "inventory" => UsageFeature.InventoryPanel,
            "merge" => UsageFeature.MergePanel,
            "itemupgrades" => UsageFeature.ItemUpgradesPanel,
            "moteplan" => UsageFeature.MoteUpgradePlannerPanel,
            "exaltations" => UsageFeature.ExaltationsPanel,
            _ => UsageFeature.OtherPanel,
        };
    }

    public static void TrackError(Exception exception, UsageErrorSource source)
    {
        TrackEvent("app_error",
            new("error.type", exception.GetType().Name),
            new("error.source", source.ToString()));
    }

    public static void Shutdown()
    {
        SetEnabled(false);
    }

    private static ILoggerFactory CreateLoggerFactory()
    {
        string? configured = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
        string connectionString = string.IsNullOrWhiteSpace(configured)
            ? DefaultConnectionString
            : configured;
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddOpenTelemetry(logging =>
            {
                logging.ParseStateValues = true;
                logging.IncludeFormattedMessage = false;
                logging.IncludeScopes = false;
                logging.AddAzureMonitorLogExporter(options =>
                {
                    options.ConnectionString = connectionString;
                });
            });
        });
    }

    private static void TrackEvent(string eventName, params KeyValuePair<string, object?>[] properties)
    {
        lock (Gate)
        {
            if (!_enabled || _logger is null)
            {
                return;
            }

            KeyValuePair<string, object?>[] state =
            [
                new("microsoft.custom_event.name", eventName),
                new("app.version", AppVersion),
                new("session.id", _sessionId),
                .. properties,
            ];
            _logger.Log(LogLevel.Information, new EventId(0), state, null, static (_, _) => "");
        }
    }
}
