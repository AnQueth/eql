using System.IO;
using System.Text.Json;
using System.Windows;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

public partial class MainWindow
{
    /// <summary>Application state; lightweight shell settings are available before saved history is loaded.</summary>
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
        public IReadOnlyList<TradeskillWikiPage> Tradeskills { get; init; } = [];
        public Exception? TradeskillError { get; init; }
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

        internal static async Task<StartupState> LoadShellAsync()
        {
            var settings = Task.Run(SettingsComposition.LoadAsync);
            var themes = Attempt(LoadThemesAsync);
            await Task.WhenAll(settings, themes);
            return CreateShell(settings.Result, themes.Result.Value ?? [], themes.Result.Error);
        }

        internal static StartupState CreateShell(SettingsLoadResult settings,
            IReadOnlyList<ThemePalette> themes, Exception? themesError = null)
        {
            return new StartupState
            {
                Settings = settings.State,
                SettingsPersistence = settings.Service,
                SettingsError = settings.Error,
                Themes = themes,
                ThemesError = themesError,
                Session = new GameSession(new(new(), new(), new(), new(), new(), [])),
                Inventory = InventoryComposition.CreateService(new(), null),
                QuestCache = [],
            };
        }

        internal static async Task<StartupState> LoadDataAsync(StartupState shell,
            TaskCompletionSource<StartupState>? availableData = null)
        {
            try
            {
                var persistence = shell.SettingsPersistence
                    ?? throw new InvalidOperationException("Shell settings persistence has not been initialized.");
                string? resetBackup = await Task.Run(() => TrackingComposition.CreateService().ApplyPendingResetAsync());
                var state = await LoadAsync(new SettingsLoadResult(shell.Settings, persistence, shell.SettingsError), availableData);
                state.ResetBackup = resetBackup;
                return state;
            }
            catch (Exception error)
            {
                availableData?.TrySetException(error);
                throw;
            }
        }

        internal string? ResetBackup { get; private set; }

        internal static async Task<StartupState> LoadAsync(SettingsLoadResult? initialSettings = null,
            TaskCompletionSource<StartupState>? availableData = null)
        {
            // Everything starts on the thread pool, so even the File.Exists probes stay off the UI thread.
            var settings = initialSettings is null
                ? Task.Run(SettingsComposition.LoadAsync)
                : Task.FromResult(initialSettings);
            var themes = Attempt(LoadThemesAsync);
            var trackingService = TrackingComposition.CreateService();
            var inventory = Attempt(InventoryComposition.LoadAsync);
            var wiki = Task.Run(WikiComposition.LoadAsync);
            var caches = Task.WhenAll(settings, themes, inventory, wiki);
            InventoryService? inventoryService = null;
            var tracking = Task.Run(() => trackingService.LoadAsync(coreLoaded: availableData is null ? null : async data =>
            {
                await caches.ConfigureAwait(false);
                availableData.TrySetResult(CreateState(data));
            }));
            await Task.WhenAll(settings, themes, tracking, inventory, wiki);
            return CreateState(tracking.Result);

            StartupState CreateState(TrackingStartup data)
            {
                inventoryService ??= InventoryComposition.CreateService(inventory.Result.Value ?? new(), inventory.Result.Error);
                return new StartupState
                {
                    Settings = settings.Result.State,
                    SettingsPersistence = settings.Result.Service,
                    SettingsError = settings.Result.Error,
                    Tracking = trackingService,
                    TrackingFailures = data.Failures,
                    Themes = themes.Result.Value ?? [],
                    ThemesError = themes.Result.Error,
                    Session = new GameSession(data),
                    MobResistancesError = data.Failures.FirstOrDefault(failure => failure.Store == "mobs")?.Error,
                    NpcCompendiumError = data.Failures.FirstOrDefault(failure => failure.Store == "npcs")?.Error,
                    Inventory = inventoryService,
                    QuestInventoryError = inventory.Result.Error,
                    QuestCache = wiki.Result.Quests.Value ?? [],
                    QuestCacheError = wiki.Result.Quests.Error,
                    ItemEffects = wiki.Result.Effects.Value ?? [],
                    ItemEffectsError = wiki.Result.Effects.Error,
                    SkyCatalog = wiki.Result.Sky.Value,
                    SkyError = wiki.Result.Sky.Error,
                    Tradeskills = wiki.Result.Tradeskills.Value ?? [],
                    TradeskillError = wiki.Result.Tradeskills.Error,
                    WikiWarnings = wiki.Result.Warnings,
                };
            }
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

    private void InitializeStartupData(StartupState state)
    {
        InitializeLootQuests(state);
        InitializeQuestInventory(state);
        _logSessions = LogSessionComposition.Create(_inventoryService);
        _mobSaveEnabled = true;
        _npcSaveEnabled = true;
        _npcStartupError = null;
        InitializeMobInfo(state);
        if (state.WikiWarnings.Count > 0)
        {
            ResetWikiCacheStatus.Text = string.Join(" ", state.WikiWarnings);
        }
        _skyCatalog = state.SkyCatalog;
        if (_skyCatalog is not null)
        {
            _wikiRepository.SeedSky(_skyCatalog);
        }
        if (state.SkyError is { } skyError)
        {
            _skyStatus = $"Sky cache could not load: {skyError.Message}. The original cache is protected until reset or repair.";
        }
        if (state.TrackingFailures.Count > 0)
        {
            ResetTrackingDataStatus.Text = string.Join(" ", state.TrackingFailures.Select(failure =>
                $"{failure.Store} could not load: {failure.Error.Message}. Existing data is protected."));
        }
        if (state.TrackingFailures.FirstOrDefault(failure => failure.Store == "leveling") is { } levelingFailure)
        {
            _levelingStorageError = $"Leveling history could not load: {levelingFailure.Error.Message}. " +
                "The saved file is protected; new observations are temporary until repair and restart.";
        }
        InitializeExaltations(state);
        InitializeTradeskills(state);
        InitializeNpcCompendium(state);
        _persistence = new(_settingsPersistence, _tracking, _inventoryService, _wikiRepository);
    }

    private void SetDataControlsEnabled(bool enabled)
    {
        BrowseLogButton.IsEnabled = enabled;
        ChooseFolderButton.IsEnabled = enabled;
        ResetTrackingDataButton.IsEnabled = enabled;
        ResetWikiCacheButton.IsEnabled = enabled;
        CheckUpdateButton.IsEnabled = enabled;
        SpawnsTab.IsEnabled = enabled;
        DurationsTab.IsEnabled = enabled;
    }

    private async Task CompleteStartupAsync(Task<StartupState> pendingState, Task<StartupState>? availableData)
    {
        try
        {
            bool appliedAvailableData = false;
            if (availableData is not null && await Task.WhenAny(availableData, pendingState) == availableData &&
                !pendingState.IsCompletedSuccessfully)
            {
                var available = await availableData;
                appliedAvailableData = await ApplyStartupDataAsync(available, false, true);
            }
            var state = await pendingState;
            if (!await ApplyStartupDataAsync(state, true, !appliedAvailableData))
            {
                return;
            }
            SetDataControlsEnabled(true);
            _dock.RefreshWorkspaceMenus();
            UpdateLockState();
            StatusText.Text = "Saved history restored.";
            if (state.ResetBackup is { } resetBackup)
            {
                MessageBox.Show(_dock, $"Tracking data was reset. A backup was saved to:\n{resetBackup}\n\nThe selected log will be reprocessed. Only history present in available logs can be rebuilt.",
                    "Tracking data reset", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            StartRuntime();
        }
        catch (Exception error)
        {
            UsageTelemetry.TrackError(error, UsageErrorSource.StartupData);
            if (!_shuttingDown)
            {
                _dataReady = false;
                SetDataControlsEnabled(false);
                StatusText.Text = $"Saved data could not be restored: {error.Message}. Existing data is protected; restart after fixing the error.";
                MessageBox.Show(_dock, StatusText.Text, "Startup data error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task<bool> ApplyStartupDataAsync(StartupState state, bool complete, bool replaceViewModels)
    {
        if (_shuttingDown)
        {
            state.Inventory.Dispose();
            return false;
        }

        _pump.Dispose();
        await _wikiRepository.DisposeAsync();
        await _logSessions.DisposeAsync();
        if (_shuttingDown)
        {
            state.Inventory.Dispose();
            return false;
        }
        if (!ReferenceEquals(_inventoryService, state.Inventory))
        {
            _inventoryService.Dispose();
        }
        _session = state.Session;
        if (_startupLogPreview is { } preview)
        {
            _session.PreviewLog(preview);
        }
        _tracking = state.Tracking ?? throw new InvalidOperationException("Tracking startup has not been initialized.");
        _dataReady = complete;
        InitializeStartupData(state);
        if (!complete && _settings.LogPath is { } savedLog)
        {
            try
            {
                var info = await _logSessions.DescribeAsync(savedLog);
                if (_shuttingDown)
                {
                    return false;
                }
                _characterName = info.CharacterName;
                _startupLogPreview = info;
                _session.PreviewLog(info);
                _inventoryService.SelectSession(savedLog, _characterName);
                var imported = await _inventoryService.PollAsync(_wikiCts.Token);
                if (imported.Status == EqLog.Application.Inventory.InventoryImportStatus.Stabilizing)
                {
                    await _inventoryService.PollAsync(_wikiCts.Token);
                }
                SynchronizeInventoryView();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
                _inventoryView.ImportStatus = $"Saved inventory preview could not load: {error.Message}. Existing data is kept.";
            }
        }
        if (_shuttingDown)
        {
            return false;
        }
        RebuildTriggers();
        _overlayServices = new OverlayShellServices(this, state);
        if (replaceViewModels)
        {
            foreach (var definition in OverlayPresenters.All)
            {
                var panel = _overlays[definition.Key].Panel;
                var previous = panel.ViewModel;
                var viewModel = CreateOverlayViewModel(definition);
                viewModel.FontSize = previous.FontSize;
                viewModel.BackgroundOpacity = previous.BackgroundOpacity;
                viewModel.HoverBackgroundOpacity = previous.HoverBackgroundOpacity;
                viewModel.IsLocked = previous.IsLocked;
                viewModel.IsChromeHidden = previous.IsChromeHidden;
                viewModel.IsShowing = previous.IsShowing;
                viewModel.SetDocked(previous.IsDocked, previous.IsTemporarilyDocked);
                viewModel.SetWorkspaceOptions(
                    _settings.DockWindow.Workspaces.Where(workspace => !workspace.Deleted)
                        .Select(workspace => (workspace.Id, workspace.Name)),
                    _settings.DockWindow.ActiveWorkspaceId ?? "");
                panel.ReplaceViewModel(viewModel);
                if (previous is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                var slider = _backgroundSliders[definition.Key];
                TrackBackgroundOpacity(viewModel, _settings.Overlays[definition.Key], value => slider.Value = value);
                if (viewModel is ViewModels.MobInfoOverlayViewModel mobInfo)
                {
                    MobTimeoutBox.DataContext = mobInfo;
                    MobInfoStatusText.DataContext = mobInfo;
                }
            }
        }
        CreateMessagePump();
        if (!complete)
        {
            _pump.Start();
            HistoryProgress.Tag = "Loading resistance history...";
            StatusText.Text = "Saved inventory, NPCs and wiki data are available. Restoring resistance history...";
        }
        else
        {
            if (_overlayViewModels.GetValueOrDefault("mobinfo") is ViewModels.MobInfoOverlayViewModel mobInfo)
            {
                mobInfo.ReportStatus(_mobStartupStatus);
            }
            if (_overlayViewModels.GetValueOrDefault("tradeskills") is ViewModels.TradeskillsOverlayViewModel tradeskills)
            {
                tradeskills.NotifyWikiLookupStateChanged();
            }
            if (_overlayViewModels.GetValueOrDefault("exaltations") is ViewModels.ExaltationsOverlayViewModel exaltations)
            {
                exaltations.NotifyWikiLookupStateChanged();
            }
            if (_overlayViewModels.GetValueOrDefault("npcs") is ViewModels.NpcBrowserOverlayViewModel npcs)
            {
                npcs.NotifyWikiLookupStateChanged();
            }
        }
        return true;
    }
}
