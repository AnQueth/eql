using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Application.Sessions;
using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

/// <summary>Overlay view model construction and the central message pump that feeds them.</summary>
public partial class MainWindow
{
    private static readonly string[] LootGroupingOptions =
        ["Recent drops", "Grouped: most recent", "Grouped: item name (A-Z)"];

    private static readonly string[] ForageGroupingOptions =
        ["Recent finds", "Grouped: most recent", "Grouped: item name (A-Z)"];

    private readonly Dictionary<string, OverlayViewModel> _overlayViewModels = [];
    private OverlayShellServices _overlayServices = null!;
    private OverlayMessagePump _pump = null!;

    /// <summary>Creates the view model for one overlay; frames reach it through the pump.</summary>
    private OverlayViewModel CreateOverlayViewModel(OverlayDefinition def)
    {
        var actions = _overlayServices;
        var source = _overlayServices;
        OverlayViewModel vm = def.Key switch
        {
            "dps" => new DpsOverlayViewModel(def, actions, source, _settings.DpsExpandedView, expanded =>
            {
                _settings.DpsExpandedView = expanded;
                SaveSettingsInBackground();
            }),
            "spawns" => new SpawnsOverlayViewModel(def, actions, source, change =>
            {
                _session.SetSpawnTracked(change.Zone, change.Mob, change.Tracked);
                CheckSpawnWiki();
                _ = SaveQuietlyAsync(_session.SaveSpawnsAsync(_tracking));
            }),
            "timers" => new SpellTimersOverlayViewModel(def, actions, source, _settings.SpellTimersShowKnownSpells,
                show =>
                {
                    _settings.SpellTimersShowKnownSpells = show;
                    SaveSettingsInBackground();
                },
                change =>
                {
                    _session.SetSpellTracked(change.Spell, change.Tracked);
                    _ = SaveQuietlyAsync(_session.SaveSpellDurationsAsync(_tracking));
                }),
            "loot" => new ItemListOverlayViewModel(def, actions, source, LootGroupingOptions, _settings.LootGrouping,
                "Grouped views count all loot observed this session, separately per looter. Upgrade variants are combined.",
                grouping => _settings.LootGrouping = grouping),
            "forage" => new ItemListOverlayViewModel(def, actions, source, ForageGroupingOptions, _settings.ForageGrouping,
                "Grouped views total all forage finds this session per item, with the zones they came from.",
                grouping => _settings.ForageGrouping = grouping),
            "itemupgrades" => new ItemListOverlayViewModel(def, actions, source),
            "chat" => new ChatOverlayViewModel(def, actions, source),
            "wiki" => new WikiOverlayViewModel(def, actions, source),
            "motes" => new MotesOverlayViewModel(def, actions, source),
            "alerts" => new AlertsOverlayViewModel(def, actions, source, SystemSounds.Exclamation.Play,
                message => TriggerStatusText.Text = message),
            "quests" => new QuestsOverlayViewModel(def, actions, Dispatcher, GetQuestsOverlayState,
                _settings.QuestFilter, _settings.QuestRecentFirst,
                filter => _settings.QuestFilter = filter,
                recentFirst => _settings.QuestRecentFirst = recentFirst,
                error => QuestLookupStatus.Text = $"Quest filtering failed: {error.Message}"),
            "sky" => new SkyOverlayViewModel(def, actions, Dispatcher, GetSkyOverlayState,
                async (refresh, background) => await LoadSkyQuestsAsync(refresh,
                    background ? WikiPriority.Background : WikiPriority.Interactive),
                TryStartInitialSkyLoad, _settings.SkyFilter, _settings.SkyClass, _settings.SkyView,
                _settings.SkyWatched, filter => _settings.SkyFilter = filter,
                selectedClass => _settings.SkyClass = selectedClass,
                view => _settings.SkyView = view, SaveSkyWatched,
                status => _skyStatus = status,
                () => UsageTelemetry.TrackFeature(UsageFeature.SkyQuestWatchChanged)),
            "tradeskills" => new TradeskillsOverlayViewModel(def, actions, _overlayServices, _settings.Tradeskill,
                _settings.TradeskillSearch, _settings.TradeskillRecipe, _settings.TradeskillSortByTrivial,
                _settings.TradeskillCurrentZoneOnly, skill => _settings.Tradeskill = skill,
                search => _settings.TradeskillSearch = search, recipe => _settings.TradeskillRecipe = recipe,
                sort => _settings.TradeskillSortByTrivial = sort, zoneOnly => _settings.TradeskillCurrentZoneOnly = zoneOnly),
            "mobinfo" => new MobInfoOverlayViewModel(def, actions, _overlayServices,
                _settings.MobInfoTimeoutSeconds, _mobStartupStatus,
                error => UsageTelemetry.TrackError(error, UsageErrorSource.ResistanceReset)),
            "npcs" => new NpcBrowserOverlayViewModel(def, actions, _overlayServices,
                _settings.NpcSearch, _settings.NpcSelectedMob, _settings.NpcSelectedZone,
                _settings.NpcView, _npcStartupError),
            "inventory" => new InventoryOverlayViewModel(def, actions, _inventoryView,
                LookupInventoryQuestReferencesAsync, ToggleInventoryQuestCompletionAsync, RetryInventoryQuestLookups),
            "merge" => new MergeOverlayViewModel(def, actions, () => _inventoryService.Snapshot(), () => _characterName),
            "moteplan" => new MotePlanOverlayViewModel(def, actions, () => _inventoryService.Snapshot()),
            "leveling" => new LevelingOverlayViewModel(def, actions, source, new LevelingOverlayServices(this), Dispatcher),
            "exaltations" => new ExaltationsOverlayViewModel(def, actions, _overlayServices),
            _ => new PresenterOverlayViewModel(def, actions, source),
        };
        _overlayViewModels[def.Key] = vm;
        return vm;
    }

    /// <summary>Creates the pump and routes its messages to the overlays and shell, in overlay order.</summary>
    private void CreateMessagePump()
    {
        _pump = new OverlayMessagePump(new SessionLogFeed(_logSessions, _session));
        _pump.CycleFailed += error =>
        {
            UsageTelemetry.TrackError(error, UsageErrorSource.StartupData);
            StatusText.Text = $"Overlay refresh failed: {error.Message}";
        };
        _pump.SubscribeBackground<LogBatchMessage>(message => RememberRawLines(message.Batch.Lines));
        _pump.SubscribeBackground<SpellObservedMessage>(message => _wikiRepository.ObserveSpell(message.Spell));
        if (_overlayViewModels["mobinfo"] is MobInfoOverlayViewModel mobInfo)
        {
            _pump.Subscribe<MobConsideredMessage>(mobInfo);
            _pump.Subscribe<ConsiderAssessmentMessage>(mobInfo);
        }
        _pump.Subscribe<OverlayTickMessage>(OnOverlayTick);
        foreach (var def in OverlayPresenters.All)
        {
            var vm = _overlayViewModels[def.Key];
            _pump.Subscribe<OverlayFrameMessage>(vm);
            if (vm is IMessageReceiver<TriggerAlertsMessage> alerts)
            {
                _pump.Subscribe(alerts);
            }
        }
        _pump.Subscribe<OverlayFrameCompletedMessage>(OnOverlayFrameCompleted);
    }

    private void OnOverlayTick(OverlayTickMessage tick)
    {
        if (_housekeepingTask.IsFaulted && !_housekeepingFailureReported)
        {
            _housekeepingFailureReported = true;
            var error = _housekeepingTask.Exception!.GetBaseException();
            UsageTelemetry.TrackError(error, UsageErrorSource.StartupData);
            ResetTrackingDataStatus.Text = $"Application maintenance stopped: {error.Message}. Restart to resume automatic checks.";
        }
        PumpMobInfo(tick.Now);
        var p = tick.Progress;
        double progress = p.TotalBytes > 0 ? (double)p.BytesRead / p.TotalBytes : 0;
        if (HistoryProgress.Value != progress)
        {
            HistoryProgress.Value = progress;
        }
        bool loading = !_dataReady || (_currentFile is not null && !p.CaughtUp);
        HistoryProgress.IsIndeterminate = !_dataReady;
        if (_dataReady)
        {
            HistoryProgress.Tag = $"Loading log history {progress:P0}";
        }
        _dock.SetLoadingNotice(loading ? HistoryProgress : null);
        var progressVisibility = loading ? Visibility.Visible : Visibility.Hidden;
        if (HistoryProgress.Visibility != progressVisibility)
        {
            HistoryProgress.Visibility = progressVisibility;
        }

        PumpQuestLoot();
        if (LogLinesTab.IsSelected)
        {
            RefreshRawLines();
        }
        foreach (var (key, vm) in _overlayViewModels)
        {
            vm.IsShowing = IsShowing(key);
        }
    }

    private void OnOverlayFrameCompleted(OverlayFrameCompletedMessage message)
    {
        var frame = message.Frame;
        var snap = frame.Snapshot;
        string title = $"EverQuest Log Overlay - v{AppVersion}";
        if (!string.IsNullOrWhiteSpace(snap.CharacterName))
        {
            title += $" - {snap.CharacterName}";
            title += snap.CurrentLoadout is { } current
                ? $" [{current.Level} {current.Classes}]" : " [classes unknown - use /who]";
        }
        if (_dock.Title != title)
        {
            _dock.Title = title;
        }

        if (message.ReaderCompletion is { IsFaulted: true } t)
        {
            string error = $"Log reader error: {t.Exception?.GetBaseException().Message}";
            if (StatusText.Text != error)
            {
                StatusText.Text = error;
            }
            return;
        }

        if (_currentFile is null)
        {
            return;
        }

        string phase = frame.Progress.CaughtUp ? "Live" : $"Loading history {HistoryProgress.Value:P0}";
        string zone = snap.Session.Zone is { } z ? $" in {z}" : "";
        string when = snap.LastLogTime is DateTime lt ? $"last line {lt:g}" : "no lines yet";
        string replayNote = frame.Replay ? "  (log is not being written; showing it as of the last line)" : "";
        string loadout = snap.CurrentLoadout is { } profile
            ? $" [{profile.Level} {profile.Classes}]" : " [classes unknown - use /who]";
        string status = $"{phase}: {snap.CharacterName}{loadout}{zone} - {snap.LinesProcessed:N0} lines, {when}{replayNote}";
        if (StatusText.Text != status)
        {
            StatusText.Text = status;
        }
    }

    /// <summary>Keeps a placement's saved background in step with the overlay (its header slider changes it too).</summary>
    private static void TrackBackgroundOpacity(OverlayViewModel vm, OverlayPlacement placement, Action<double> changed)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.BackgroundOpacity) &&
                placement.BackgroundOpacity != vm.BackgroundOpacity)
            {
                placement.BackgroundOpacity = vm.BackgroundOpacity;
                changed(vm.BackgroundOpacity);
            }
        };
    }

    /// <summary>The shell's side of the leveling overlay view model.</summary>
    private sealed class LevelingOverlayServices(MainWindow shell) : ILevelingOverlayServices
    {
        public string? CharacterName
        {
            get
            {
                return shell._characterName;
            }
        }

        public long Revision
        {
            get
            {
                return shell._session.Revision;
            }
        }

        public string? StorageError
        {
            get
            {
                return shell._levelingStorageError;
            }
        }

        public EqLog.Application.Sessions.LevelingReport LevelingReport(EqLog.Application.Sessions.LevelingRequest request)
        {
            return shell._session.LevelingReport(request);
        }
    }

    /// <summary>The shell's side of the overlay view models: application actions and shared state.</summary>
    private sealed class OverlayShellServices(MainWindow shell, StartupState state) : IOverlayActions, IOverlayViewSource,
        ITradeskillsOverlayServices, IExaltationsOverlayServices, IMobInfoOverlayServices, INpcBrowserOverlayServices
    {
        IReadOnlyList<TradeskillWikiPage> ITradeskillsOverlayServices.InitialPages
        {
            get
            {
                return state.Tradeskills;
            }
        }

        Exception? ITradeskillsOverlayServices.InitialError
        {
            get
            {
                return state.TradeskillError;
            }
        }

        IReadOnlyList<ItemEffectInfo> IExaltationsOverlayServices.InitialEffects
        {
            get
            {
                return state.ItemEffects;
            }
        }

        Exception? IExaltationsOverlayServices.InitialError
        {
            get
            {
                return state.ItemEffectsError;
            }
        }

        public bool WikiLookupPaused
        {
            get
            {
                return shell.WikiLookupPaused;
            }
        }

        public WikiIconViewModel? GetWikiIcon(WikiIconKey key)
        {
            return shell._wikiIcons?.Get(key);
        }

        public IOverlayWikiLookupLease BeginLookup()
        {
            return shell.BeginWikiLookup();
        }

        public Task<TradeskillWikiPage> GetTradeskillAsync(TradeskillPageKind kind, string title, bool force,
            CancellationToken token)
        {
            return shell._wikiRepository.GetTradeskillAsync(kind, title, WikiPriority.Interactive, force, token);
        }

        public Task<ItemEffectInfo> GetItemEffectsAsync(string item, WikiPriority priority, bool force,
            CancellationToken token)
        {
            return shell._wikiRepository.GetItemEffectsAsync(item, priority, force, token);
        }

        public string? GetCacheError(string operation)
        {
            return shell._wikiRepository.GetCacheError(operation);
        }

        public IReadOnlyList<ExaltationLocation> ExaltationLocations
        {
            get
            {
                return shell._exaltationLocations;
            }
        }

        public bool HasCurrentInventory
        {
            get
            {
                return shell._inventoryRowsCharacter is not null &&
                    shell._inventoryRowsCharacter.Equals(shell._characterName, StringComparison.OrdinalIgnoreCase) &&
                    shell._inventoryRows.Count > 0;
            }
        }

        public DateTime? InventoryWritten
        {
            get
            {
                return shell._inventoryLoaded?.Written;
            }
        }

        public long InventoryRevision
        {
            get
            {
                return shell._session.Revision;
            }
        }

        public void TrackWikiError(Exception error)
        {
            UsageTelemetry.TrackError(error, UsageErrorSource.Wiki);
        }

        public ISet<ChatChannel> ChatChannels
        {
            get
            {
                return shell._chatChannels;
            }
        }

        public string? CharacterName
        {
            get
            {
                return shell._characterName;
            }
        }

        public InventoryReadModel? Inventory
        {
            get
            {
                return shell._questInventory;
            }
        }

        public MoteInventoryReadModel? Motes()
        {
            return shell._questInventory.Motes(shell._characterName);
        }

        public WikiActivitySnapshot? WikiActivity()
        {
            return shell._wikiRepository.GetActivity();
        }

        public QuestWikiActivity? QuestWikiActivity()
        {
            return shell.GetQuestWikiActivity();
        }

        public bool CanSaveMobData
        {
            get
            {
                return shell._mobSaveEnabled;
            }
        }

        public bool IsMobInfoEnabled
        {
            get
            {
                return shell._settings.Overlays["mobinfo"].Visible;
            }
        }

        public MobDetails GetMob(string zone, string mob)
        {
            return shell._session.GetMob(zone, mob);
        }

        public Task LookupMobAsync(string mob, string zone, bool force, CancellationToken token,
            Action<string> report, WikiPriority priority)
        {
            return shell.LookupMobInfoAsync(mob, zone, force, token, priority, report);
        }

        public void OpenMobWiki(string mob, string zone, Action<string> report)
        {
            shell.OpenMobWiki(mob, zone, report);
        }

        public Task ResetResistancesAsync(string zone, string mob)
        {
            return shell.ResetMobResistancesAsync(zone, mob);
        }

        public void PersistTimeout(double seconds)
        {
            shell._settings.MobInfoTimeoutSeconds = seconds;
            shell.SaveSettingsInBackground();
        }

        public long Revision
        {
            get
            {
                return shell._session.Revision;
            }
        }

        public bool CanSaveNpcData
        {
            get
            {
                return shell._npcSaveEnabled;
            }
        }

        public IReadOnlyList<NpcCatalogEntry> SearchNpcs(string filter)
        {
            return shell._session.SearchNpcs(filter);
        }

        public Task<NpcReadModel?> GetNpcAsync(string mob, string zone)
        {
            return shell._session.GetNpcAsync(shell._tracking, mob, zone);
        }

        public IReadOnlyList<NpcSpellDetails> NpcSpellDetails(string zone, string mob)
        {
            return shell._session.NpcSpellDetails(zone, mob);
        }

        public void PersistSearch(string search)
        {
            shell._settings.NpcSearch = search;
        }

        public void PersistSelection(string? mob, string? zone)
        {
            shell._settings.NpcSelectedMob = mob;
            shell._settings.NpcSelectedZone = zone;
            shell.SaveSettingsInBackground();
        }

        public void PersistView(int view)
        {
            shell._settings.NpcView = view;
            shell.SaveSettingsInBackground();
        }

        public void TrackNpcLookup()
        {
            UsageTelemetry.TrackFeature(UsageFeature.NpcLookup);
        }

        public void TrackNpcWikiRefresh()
        {
            UsageTelemetry.TrackFeature(UsageFeature.NpcWikiRefresh);
        }

        public void ShowDpsHistory(NpcReadModel npc)
        {
            new NpcDpsHistoryWindow(npc) { Owner = shell._dock }.Show();
        }

        public void OpenLink(string url)
        {
            shell.OpenWiki(url);
        }

        public void OpenItemWiki(string item)
        {
            shell.OpenWiki(WikiLinks.PageUrl(WikiLinks.ItemTitle(item)));
        }

        public async void LookupQuestReferences(string item)
        {
            await shell.LookupLootItemAsync(item);
        }

        public bool HasQuestReferences(string item)
        {
            return shell.HasQuestReferences(item);
        }

        public void ShowBreakdown(AbilityBreakdown breakdown)
        {
            UsageTelemetry.TrackFeature(UsageFeature.AbilityBreakdown);
            new AbilityBreakdownWindow(breakdown) { Owner = shell._dock }.Show();
        }

        public void ToggleDock(OverlayViewModel overlay)
        {
            string key = overlay.Key;
            shell.Dispatcher.BeginInvoke(() =>
            {
                if (shell._dock.IsPanelInActiveWorkspace(key))
                {
                    shell.FloatPanel(key);
                }
                else
                {
                    shell.SetDocked(key, true);
                }
            });
        }

        public void MoveToWorkspace(OverlayViewModel overlay, string workspaceId)
        {
            shell._dock.MovePanelToWorkspace(overlay.Key, workspaceId);
        }
    }
}
