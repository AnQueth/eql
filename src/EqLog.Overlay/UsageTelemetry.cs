using Azure.Monitor.OpenTelemetry.Exporter;
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
    MoteUpgradePlannerPanel,
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
}

internal static class UsageTelemetry
{
    private const string DefaultConnectionString =
        "InstrumentationKey=7db2a0ee-3a70-40f0-a425-bd4ff75dfd3d;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=f380dd49-dc42-4e5d-a811-8ed222d6d6a7";

    private static readonly object Gate = new();
    private static ILoggerFactory? _loggerFactory;
    private static ILogger? _logger;
    private static bool _enabled;

    public static void SetEnabled(bool enabled)
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
                string? configured = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
                string connectionString = string.IsNullOrWhiteSpace(configured)
                    ? DefaultConnectionString
                    : configured;

                _loggerFactory = LoggerFactory.Create(builder =>
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
                _logger = _loggerFactory.CreateLogger("EqLogOverlay.Usage");
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
        TrackEvent("feature_used", feature.ToString());
    }

    public static void TrackPanelOpened(string panelKey)
    {
        UsageFeature feature = panelKey switch
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
            "moteplan" => UsageFeature.MoteUpgradePlannerPanel,
            _ => UsageFeature.OtherPanel,
        };

        TrackFeature(feature);
    }

    public static void TrackError(Exception exception, UsageErrorSource source)
    {
        lock (Gate)
        {
            if (!_enabled || _logger is null)
            {
                return;
            }

            _logger.LogInformation(
                "{microsoft.custom_event.name} {error.type} {error.source}",
                "app_error",
                exception.GetType().Name,
                source.ToString());
        }
    }

    public static void Shutdown()
    {
        SetEnabled(false);
    }

    private static void TrackEvent(string eventName)
    {
        lock (Gate)
        {
            if (!_enabled || _logger is null)
            {
                return;
            }

            _logger.LogInformation("{microsoft.custom_event.name}", eventName);
        }
    }

    private static void TrackEvent(string eventName, string feature)
    {
        lock (Gate)
        {
            if (!_enabled || _logger is null)
            {
                return;
            }

            _logger.LogInformation(
                "{microsoft.custom_event.name} {feature}",
                eventName,
                feature);
        }
    }
}
