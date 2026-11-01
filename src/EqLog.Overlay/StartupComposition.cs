using System.IO;
using System.Text.Json;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

public partial class MainWindow
{
    /// <summary>Everything read from disk at startup, loaded concurrently off the UI thread before any window is built.</summary>
    public sealed class StartupState
    {
        public required AppSettings Settings
        {
            get; init;
        }
        public EqLog.Application.Settings.SettingsService? SettingsPersistence
        {
            get; init;
        }
        public Exception? SettingsError
        {
            get; init;
        }
        public TrackingService? Tracking
        {
            get; init;
        }
        public IReadOnlyList<TrackingLoadFailure> TrackingFailures { get; init; } = [];
        public SkyQuestCatalog? SkyCatalog
        {
            get; init;
        }
        public Exception? SkyError
        {
            get; init;
        }
        public IReadOnlyList<string> WikiWarnings { get; init; } = [];
        public required IReadOnlyList<ThemePalette> Themes
        {
            get; init;
        }
        public Exception? ThemesError
        {
            get; init;
        }
        public required GameSession Session
        {
            get; init;
        }
        public Exception? MobResistancesError
        {
            get; init;
        }
        public Exception? NpcCompendiumError
        {
            get; init;
        }
        public required InventoryService Inventory
        {
            get; init;
        }
        public Exception? QuestInventoryError
        {
            get; init;
        }
        public required IReadOnlyList<QuestLookupResult> QuestCache
        {
            get; init;
        }
        public Exception? QuestCacheError
        {
            get; init;
        }
        public IReadOnlyList<ItemEffectInfo> ItemEffects { get; init; } = [];
        public Exception? ItemEffectsError
        {
            get; init;
        }

        internal static async Task<StartupState> LoadAsync(SettingsLoadResult? initialSettings = null)
        {
            // Everything starts on the thread pool, so even the File.Exists probes stay off the UI thread.
            var settings = initialSettings is null
                ? Task.Run(SettingsComposition.LoadAsync)
                : Task.FromResult(initialSettings);
            var themes = Attempt(LoadThemesAsync);
            var trackingService = TrackingComposition.CreateService();
            var tracking = Task.Run(() => trackingService.LoadAsync());
            var inventory = Attempt(InventoryComposition.LoadAsync);
            var wiki = Task.Run(WikiComposition.LoadAsync);
            await Task.WhenAll(settings, themes, tracking, inventory, wiki);
            return new StartupState
            {
                Settings = settings.Result.State,
                SettingsPersistence = settings.Result.Service,
                SettingsError = settings.Result.Error,
                Tracking = trackingService,
                TrackingFailures = tracking.Result.Failures,
                Themes = themes.Result.Value ?? [],
                ThemesError = themes.Result.Error,
                Session = new GameSession(tracking.Result),
                MobResistancesError = tracking.Result.Failures.FirstOrDefault(failure => failure.Store == "mobs")?.Error,
                NpcCompendiumError = tracking.Result.Failures.FirstOrDefault(failure => failure.Store == "npcs")?.Error,
                Inventory = InventoryComposition.CreateService(inventory.Result.Value ?? new(), inventory.Result.Error),
                QuestInventoryError = inventory.Result.Error,
                QuestCache = wiki.Result.Quests.Value ?? [],
                QuestCacheError = wiki.Result.Quests.Error,
                ItemEffects = wiki.Result.Effects.Value ?? [],
                ItemEffectsError = wiki.Result.Effects.Error,
                SkyCatalog = wiki.Result.Sky.Value,
                SkyError = wiki.Result.Sky.Error,
                WikiWarnings = wiki.Result.Warnings,
            };
        }

        /// <summary>Runs a load on the thread pool; expected storage failures are returned rather than thrown.</summary>
        private static Task<(T? Value, Exception? Error)> Attempt<T>(Func<Task<T>> load)
        {
            return Task.Run(async () =>
            {
                try
                {
                    return ((T?)await load().ConfigureAwait(false), (Exception?)null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
                {
                    UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
                    return (default(T), ex);
                }
            });
        }

        private static async Task<IReadOnlyList<ThemePalette>> LoadThemesAsync()
        {
            return await ThemeComposition.Service.LoadBundledAsync().ConfigureAwait(false);
        }

    }
}
