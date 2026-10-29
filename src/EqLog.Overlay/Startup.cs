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
        public required IReadOnlyList<ThemePalette> Themes
        {
            get; init;
        }
        public Exception? ThemesError
        {
            get; init;
        }
        public required SpellDurationStore Durations
        {
            get; init;
        }
        public required SpawnStore Spawns
        {
            get; init;
        }
        public required MoteStore Motes
        {
            get; init;
        }
        public MobResistanceStore? MobResistances
        {
            get; init;
        }
        public Exception? MobResistancesError
        {
            get; init;
        }
        public NpcCompendium? NpcCompendium
        {
            get; init;
        }
        public Exception? NpcCompendiumError
        {
            get; init;
        }
        public QuestInventory? QuestInventory
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

        public static async Task<StartupState> LoadAsync(AppSettings? initialSettings = null)
        {
            // Everything starts on the thread pool, so even the File.Exists probes stay off the UI thread.
            var settings = initialSettings is null
                ? Task.Run(() => AppSettings.LoadAsync())
                : Task.FromResult(initialSettings);
            var themes = Attempt(LoadThemesAsync);
            var durations = Task.Run(() => SpellDurationStore.LoadAsync(AppSettings.DurationsPath));
            var spawns = Task.Run(() => SpawnStore.LoadAsync(AppSettings.SpawnsPath));
            var motes = Task.Run(() => MoteStore.LoadAsync(AppSettings.MotesPath));
            var mobs = Attempt(() => MobResistanceStore.LoadAsync(MobResistancePath));
            var npcs = Attempt(() => NpcCompendium.LoadAsync(NpcCompendiumFolder, LegacyNpcCompendiumPath));
            var inventory = Attempt(LoadQuestInventoryAsync);
            var questCache = Attempt(LoadQuestCacheAsync);
            await Task.WhenAll(settings, themes, durations, spawns, motes, mobs, npcs, inventory, questCache);
            return new StartupState
            {
                Settings = settings.Result,
                Themes = themes.Result.Value ?? [],
                ThemesError = themes.Result.Error,
                Durations = durations.Result,
                Spawns = spawns.Result,
                Motes = motes.Result,
                MobResistances = mobs.Result.Value,
                MobResistancesError = mobs.Result.Error,
                NpcCompendium = npcs.Result.Value,
                NpcCompendiumError = npcs.Result.Error,
                QuestInventory = inventory.Result.Value,
                QuestInventoryError = inventory.Result.Error,
                QuestCache = questCache.Result.Value ?? [],
                QuestCacheError = questCache.Result.Error,
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

        private static async Task<List<ThemePalette>> LoadThemesAsync()
        {
            var themes = new List<ThemePalette>();
            foreach (string path in Directory.GetFiles(ThemeManager.BuiltInFolder, "*.json").Order())
            {
                themes.Add(await ThemeManager.LoadAsync(path).ConfigureAwait(false));
            }
            return themes;
        }

        private static async Task<QuestInventory?> LoadQuestInventoryAsync()
        {
            if (!File.Exists(QuestInventoryPath))
            {
                return null;
            }
            return await AtomicFile.ReadJsonAsync<QuestInventory>(QuestInventoryPath).ConfigureAwait(false)
                ?? throw new JsonException("Empty quest inventory.");
        }

        private static Task<IReadOnlyList<QuestLookupResult>> LoadQuestCacheAsync()
        {
            return QuestLookupStore.LoadAsync(QuestCachePath, LegacyQuestCachePath);
        }
    }
}
