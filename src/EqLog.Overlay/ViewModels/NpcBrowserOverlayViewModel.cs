using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>Application operations used by the NPC compendium overlay.</summary>
public interface INpcBrowserOverlayServices
{
    /// <summary>The revision of the active log session.</summary>
    long Revision { get; }

    /// <summary>Whether NPC compendium data can be saved.</summary>
    bool CanSaveNpcData { get; }

    /// <summary>Whether mob resistance data can be saved.</summary>
    bool CanSaveMobData { get; }

    /// <summary>Whether wiki lookups are currently paused.</summary>
    bool WikiLookupPaused { get; }

    /// <summary>Searches the recorded NPC catalog.</summary>
    IReadOnlyList<NpcCatalogEntry> SearchNpcs(string filter);

    /// <summary>Loads the selected NPC's recorded encounters.</summary>
    Task<NpcReadModel?> GetNpcAsync(string mob, string zone);

    /// <summary>Returns the current mob resistance and wiki data.</summary>
    MobDetails GetMob(string zone, string mob);

    /// <summary>Returns prepared spell resistance details for the selected NPC.</summary>
    IReadOnlyList<NpcSpellDetails> NpcSpellDetails(string zone, string mob);

    /// <summary>Looks up the mob and its observed spells, reporting progress.</summary>
    Task LookupMobAsync(string mob, string zone, bool force, CancellationToken token,
        Action<string> report, WikiPriority priority);

    /// <summary>Opens the best known wiki page for the mob.</summary>
    void OpenMobWiki(string mob, string zone, Action<string> report);

    /// <summary>Persists the NPC search text.</summary>
    void PersistSearch(string search);

    /// <summary>Persists the selected NPC and zone.</summary>
    void PersistSelection(string? mob, string? zone);

    /// <summary>Persists the selected NPC view.</summary>
    void PersistView(int view);

    /// <summary>Records the name-lookup usage event.</summary>
    void TrackNpcLookup();

    /// <summary>Records the wiki-refresh usage event.</summary>
    void TrackNpcWikiRefresh();

    /// <summary>Shows the saved DPS history window for the selected NPC.</summary>
    void ShowDpsHistory(NpcReadModel npc);
}

/// <summary>A recorded NPC choice displayed in the compendium selector.</summary>
/// <param name="Mob">The NPC name.</param>
/// <param name="Zone">The full logged zone name.</param>
public sealed record NpcChoice(string Mob, string Zone)
{
    /// <summary>The selector label.</summary>
    public string Label
    {
        get
        {
            return $"{Mob} - {Zone}";
        }
    }
}

/// <summary>Searches recorded NPCs and presents wiki, resistance and encounter history.</summary>
public sealed class NpcBrowserOverlayViewModel : OverlayViewModel, IDisposable
{
    private const int HistoryPageSize = 10;
    private static readonly TimeSpan RenderInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ChoiceRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly INpcBrowserOverlayServices _services;
    private readonly AsyncTextFilter<NpcChoice[]> _choiceFilter;
    private CancellationTokenSource? _wikiLookup;
    private string _searchText;
    private NpcChoice? _selectedNpc;
    private string? _selectedMob;
    private string? _selectedZone;
    private string? _storageError;
    private string? _dpsStatus;
    private string _wikiStatus = "";
    private string? _wikiSelection;
    private string _spellSignature = "";
    private string _choiceSignature = "";
    private string? _entryKey;
    private NpcReadModel? _selectedEntry;
    private int _viewIndex;
    private int _historyPage;
    private int _lastHistoryCount;
    private long _entryRevision = -1;
    private long _lastSessionRevision = -1;
    private DateTime _nextChoiceRefresh;
    private DateTime _nextRender;
    private bool _isLoadingDpsHistory;

    /// <summary>Creates an NPC compendium overlay.</summary>
    public NpcBrowserOverlayViewModel(OverlayDefinition definition, IOverlayActions actions,
        INpcBrowserOverlayServices services, string searchText, string? selectedMob, string? selectedZone,
        int viewIndex, string? storageError)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(searchText);
        _services = services;
        _lastSessionRevision = services.Revision;
        _searchText = searchText;
        _selectedMob = selectedMob;
        _selectedZone = selectedZone;
        _viewIndex = Math.Clamp(viewIndex, 0, 2);
        _storageError = storageError;
        _choiceFilter = new AsyncTextFilter<NpcChoice[]>(Dispatcher.CurrentDispatcher, error =>
        {
            StorageError = $"NPC filtering failed: {error.Message}";
            _nextRender = default;
        });
        LookupNameCommand = new RelayCommand(LookupName);
        RefreshWikiCommand = new RelayCommand(RefreshWiki, () => !_services.WikiLookupPaused);
        OpenWikiCommand = new RelayCommand(OpenWiki);
        OpenDpsHistoryCommand = new AsyncRelayCommand(OpenDpsHistoryAsync, CanOpenDpsHistory);
        PreviousPageCommand = new RelayCommand(PreviousPage, CanGoPreviousPage);
        NextPageCommand = new RelayCommand(NextPage, CanGoNextPage);
        if (_selectedMob is not null && _selectedZone is not null)
        {
            _selectedNpc = new NpcChoice(_selectedMob, _selectedZone);
            LoadSelectedEntry();
        }
    }

    /// <summary>The NPC and zone choices shown in the selector.</summary>
    public BulkObservableCollection<NpcChoice> Choices { get; } = [];

    internal void NotifyWikiLookupStateChanged()
    {
        RefreshWikiCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The recorded NPC selector's search text.</summary>
    public string SearchText
    {
        get
        {
            return _searchText;
        }
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                _services.PersistSearch(_searchText);
                _nextChoiceRefresh = default;
            }
        }
    }

    /// <summary>The currently selected recorded NPC, if it appears in the filtered choices.</summary>
    public NpcChoice? SelectedNpc
    {
        get
        {
            return _selectedNpc;
        }
        set
        {
            if (value is not null)
            {
                SelectNpc(value.Mob, value.Zone);
            }
            else if (_selectedNpc is not null)
            {
                SetProperty(ref _selectedNpc, null);
            }
        }
    }

    /// <summary>The selected NPC name, including an exact-name lookup without recorded encounters.</summary>
    public string? SelectedMob
    {
        get
        {
            return _selectedMob;
        }
        private set
        {
            SetProperty(ref _selectedMob, value);
        }
    }

    /// <summary>The selected NPC's logged zone.</summary>
    public string? SelectedZone
    {
        get
        {
            return _selectedZone;
        }
        private set
        {
            SetProperty(ref _selectedZone, value);
        }
    }

    /// <summary>The selected NPC view: overview, spell resists, or encounters.</summary>
    public int ViewIndex
    {
        get
        {
            return _viewIndex;
        }
        set
        {
            int view = Math.Clamp(value, 0, 2);
            if (SetProperty(ref _viewIndex, view))
            {
                _services.PersistView(view);
                OnPropertyChanged(nameof(IsEncounterView));
                _nextRender = default;
            }
        }
    }

    /// <summary>Whether the encounter pager is visible.</summary>
    public bool IsEncounterView
    {
        get
        {
            return _viewIndex == 2;
        }
    }

    /// <summary>Whether there is a previous encounter page.</summary>
    public bool CanGoToPreviousPage
    {
        get
        {
            return _historyPage > 0;
        }
    }

    /// <summary>Whether there is a later encounter page.</summary>
    public bool CanGoToNextPage
    {
        get
        {
            return _historyPage + 1 < PageCount;
        }
    }

    /// <summary>The current encounter page label.</summary>
    public string PageLabel
    {
        get
        {
            return $"Page {_historyPage + 1} of {PageCount} · {_lastHistoryCount:N0} encounters";
        }
    }

    /// <summary>The storage, lookup or DPS history status shown in the NPC overlay.</summary>
    public string? StorageError
    {
        get
        {
            return _storageError;
        }
        private set
        {
            if (SetProperty(ref _storageError, value))
            {
                _nextRender = default;
            }
        }
    }

    /// <summary>The selected NPC's DPS-history status.</summary>
    public string? DpsStatus
    {
        get
        {
            return _dpsStatus;
        }
        private set
        {
            if (SetProperty(ref _dpsStatus, value))
            {
                _nextRender = default;
            }
        }
    }

    /// <summary>The current wiki lookup status.</summary>
    public string WikiStatus
    {
        get
        {
            return _wikiStatus;
        }
        private set
        {
            if (SetProperty(ref _wikiStatus, value))
            {
                _nextRender = default;
            }
        }
    }

    /// <summary>Whether the DPS history button is loading data.</summary>
    public bool IsLoadingDpsHistory
    {
        get
        {
            return _isLoadingDpsHistory;
        }
        private set
        {
            if (SetProperty(ref _isLoadingDpsHistory, value))
            {
                OnPropertyChanged(nameof(DpsHistoryButtonText));
                OpenDpsHistoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The DPS history command's button label.</summary>
    public string DpsHistoryButtonText
    {
        get
        {
            return _isLoadingDpsHistory ? "Loading DPS history..." : "DPS by loadout";
        }
    }

    /// <summary>Starts an exact-name lookup for the entered search text.</summary>
    public RelayCommand LookupNameCommand
    {
        get;
    }

    /// <summary>Refreshes wiki data for the selected NPC.</summary>
    public RelayCommand RefreshWikiCommand
    {
        get;
    }

    /// <summary>Opens the selected NPC's wiki page.</summary>
    public RelayCommand OpenWikiCommand
    {
        get;
    }

    /// <summary>Opens saved DPS history for the selected NPC and zone.</summary>
    public AsyncRelayCommand OpenDpsHistoryCommand
    {
        get;
    }

    /// <summary>Moves to the previous encounter history page.</summary>
    public RelayCommand PreviousPageCommand
    {
        get;
    }

    /// <summary>Moves to the next encounter history page.</summary>
    public RelayCommand NextPageCommand
    {
        get;
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Now >= _nextChoiceRefresh)
        {
            RefreshChoices(frame.Now);
        }
        if (_services.Revision != _lastSessionRevision)
        {
            _lastSessionRevision = _services.Revision;
            _entryKey = null;
            _selectedEntry = null;
            if (_selectedMob is not null && _selectedZone is not null)
            {
                LoadSelectedEntry();
            }
            _nextRender = default;
        }
        if (frame.Now >= _nextRender)
        {
            RenderNpcContent();
            _nextRender = frame.Now.Add(RenderInterval);
        }
    }

    /// <summary>Invalidates wiki status and details after the application clears its cache.</summary>
    public void ResetWikiState()
    {
        _wikiLookup?.Cancel();
        _wikiSelection = null;
        _spellSignature = "";
        WikiStatus = "Wiki cache reset.";
    }

    /// <summary>Cancels pending filters and lookups when the overlay shell shuts down.</summary>
    public void Dispose()
    {
        _choiceFilter.Dispose();
        _wikiLookup?.Cancel();
        _wikiLookup?.Dispose();
        _wikiLookup = null;
    }

    /// <summary>Updates the storage error after an application-level save attempt.</summary>
    public void SetStorageError(string? error)
    {
        StorageError = error;
    }

    /// <summary>Refreshes selected NPC history after the active log changes.</summary>
    public void ResetForNewLog()
    {
        _entryKey = null;
        _selectedEntry = null;
        _entryRevision = -1;
        _lastHistoryCount = 0;
        _historyPage = 0;
        NotifyHistoryPageStateChanged();
        _nextChoiceRefresh = default;
        _nextRender = default;
        if (_selectedMob is not null && _selectedZone is not null)
        {
            LoadSelectedEntry();
        }
    }

    private int PageCount
    {
        get
        {
            return Math.Max(1, (_lastHistoryCount + HistoryPageSize - 1) / HistoryPageSize);
        }
    }

    private void RefreshChoices(DateTime now)
    {
        _nextChoiceRefresh = now.Add(ChoiceRefreshInterval);
        string filter = _searchText;
        long revision = _services.Revision;
        DateTime refreshTime = _nextChoiceRefresh;
        var key = (filter, revision, refreshTime);
        _choiceFilter.Request(key, token =>
        {
            token.ThrowIfCancellationRequested();
            return _services.SearchNpcs(filter)
                .Select(entry => new NpcChoice(entry.Mob, entry.Zone)).ToArray();
        }, matches =>
        {
            if (revision == _services.Revision && filter == _searchText)
            {
                ApplyChoices(matches);
            }
        }, context: revision);
    }

    private void ApplyChoices(NpcChoice[] matches)
    {
        string signature = string.Join("\n", matches.Select(choice => choice.Label));
        if (_choiceSignature == signature)
        {
            return;
        }
        _choiceSignature = signature;
        Choices.Reconcile(matches, static (existing, choice) => existing == choice, choice => choice);
        var selected = Choices.FirstOrDefault(choice => choice.Mob == _selectedMob && choice.Zone == _selectedZone);
        if (selected is not null)
        {
            if (SetProperty(ref _selectedNpc, selected, nameof(SelectedNpc)))
            {
                OnPropertyChanged(nameof(SelectedMob));
                OnPropertyChanged(nameof(SelectedZone));
            }
        }
        else if (_selectedMob is null && Choices.Count > 0)
        {
            SelectNpc(Choices[0].Mob, Choices[0].Zone);
        }
        else if (_selectedNpc is not null)
        {
            SetProperty(ref _selectedNpc, null, nameof(SelectedNpc));
        }
        _nextRender = default;
    }

    private void LookupName()
    {
        string name = _searchText.Trim();
        if (name.Length == 0)
        {
            WikiStatus = "Enter an NPC name before looking it up.";
            return;
        }
        _services.TrackNpcLookup();
        var matches = _services.SearchNpcs(name);
        var exact = matches.FirstOrDefault(entry =>
            string.Equals(entry.Mob, name, StringComparison.OrdinalIgnoreCase));
        SelectNpc(exact?.Mob ?? name, exact?.Zone ?? "(unknown zone)");
        StartWikiLookup(false);
    }

    private void RefreshWiki()
    {
        _services.TrackNpcWikiRefresh();
        StartWikiLookup(true);
    }

    private void OpenWiki()
    {
        if (_selectedMob is not { } mob || _selectedZone is not { } zone)
        {
            WikiStatus = "Choose an NPC or enter an exact name first.";
            return;
        }
        _services.OpenMobWiki(mob, zone, message =>
        {
            WikiStatus = message;
        });
    }

    private void SelectNpc(string mob, string zone)
    {
        if (_selectedMob == mob && _selectedZone == zone)
        {
            return;
        }
        _wikiLookup?.Cancel();
        _wikiLookup?.Dispose();
        _wikiLookup = null;
        SelectedMob = mob;
        SelectedZone = zone;
        _selectedNpc = Choices.FirstOrDefault(choice => choice.Mob == mob && choice.Zone == zone) ?? new NpcChoice(mob, zone);
        OnPropertyChanged(nameof(SelectedNpc));
        _services.PersistSelection(mob, zone);
        _historyPage = 0;
        _lastHistoryCount = 0;
        _entryKey = null;
        _selectedEntry = null;
        _wikiSelection = null;
        _wikiStatus = "";
        _dpsStatus = null;
        _nextRender = default;
        OnPropertiesChanged(nameof(WikiStatus), nameof(DpsStatus));
        NotifyHistoryPageStateChanged();
        OpenDpsHistoryCommand.RaiseCanExecuteChanged();
        LoadSelectedEntry();
    }

    private void StartWikiLookup(bool force)
    {
        if (_services.WikiLookupPaused)
        {
            return;
        }
        if (_selectedMob is not { } mob || _selectedZone is not { } zone)
        {
            WikiStatus = "Choose an NPC or enter an exact name first.";
            return;
        }
        _wikiLookup?.Cancel();
        _wikiLookup?.Dispose();
        _wikiLookup = new CancellationTokenSource();
        _wikiSelection = mob + "\n" + zone;
        _spellSignature = string.Join("\n", _services.GetMob(zone, mob).Spells);
        _ = LookupWikiAsync(mob, zone, force, _wikiLookup.Token,
            WikiPriority.Interactive);
    }

    private async Task LookupWikiAsync(string mob, string zone, bool force, CancellationToken token, WikiPriority priority)
    {
        try
        {
            await _services.LookupMobAsync(mob, zone, force, token, message =>
            {
                WikiStatus = message;
            }, priority);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void LoadSelectedEntry()
    {
        if (_selectedMob is not { } mob || _selectedZone is not { } zone)
        {
            return;
        }
        long revision = _services.Revision;
        string key = mob + "\n" + zone;
        if (_entryKey == key && _entryRevision == revision)
        {
            return;
        }
        _entryKey = null;
        _selectedEntry = null;
        _ = LoadEntryAsync(mob, zone, key, revision);
    }

    private async Task LoadEntryAsync(string mob, string zone, string key, long revision)
    {
        try
        {
            var entry = await _services.GetNpcAsync(mob, zone);
            if (revision != _services.Revision || _selectedMob != mob || _selectedZone != zone)
            {
                return;
            }
            _selectedEntry = entry;
            _entryKey = key;
            _entryRevision = revision;
            _lastHistoryCount = entry?.Encounters.Count ?? 0;
            _historyPage = Math.Clamp(_historyPage, 0, PageCount - 1);
            NotifyHistoryPageStateChanged();
            _nextRender = default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (revision == _services.Revision && _selectedMob == mob && _selectedZone == zone)
            {
                StorageError = $"Could not read NPC history: {error.Message}";
            }
        }
    }

    private bool CanOpenDpsHistory()
    {
        return !_isLoadingDpsHistory;
    }

    private async Task OpenDpsHistoryAsync()
    {
        if (_selectedMob is not { } mob || _selectedZone is not { } zone)
        {
            DpsStatus = "Choose an NPC and zone before comparing DPS by loadout.";
            return;
        }
        IsLoadingDpsHistory = true;
        DpsStatus = null;
        long revision = _services.Revision;
        try
        {
            NpcReadModel? entry;
            if (_entryKey == mob + "\n" + zone && _entryRevision == revision && _selectedEntry is not null)
            {
                entry = _selectedEntry;
            }
            else
            {
                entry = await _services.GetNpcAsync(mob, zone);
            }
            if (revision != _services.Revision || _selectedMob != mob || _selectedZone != zone)
            {
                return;
            }
            if (entry is null)
            {
                DpsStatus = "No saved encounter history for this NPC and zone yet.";
                return;
            }
            _services.ShowDpsHistory(entry);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            DpsStatus = $"Could not read DPS history: {error.Message}";
        }
        finally
        {
            IsLoadingDpsHistory = false;
        }
    }

    private bool CanGoPreviousPage()
    {
        return CanGoToPreviousPage;
    }

    private bool CanGoNextPage()
    {
        return CanGoToNextPage;
    }

    private void PreviousPage()
    {
        HistoryPage = Math.Max(0, _historyPage - 1);
    }

    private void NextPage()
    {
        HistoryPage = Math.Min(Math.Max(0, PageCount - 1), _historyPage + 1);
    }

    /// <summary>The zero-based page currently shown in encounter history.</summary>
    public int HistoryPage
    {
        get
        {
            return _historyPage;
        }
        set
        {
            if (_historyPage == value)
            {
                return;
            }
            _historyPage = value;
            NotifyHistoryPageStateChanged();
            _nextRender = default;
        }
    }

    private void NotifyHistoryPageStateChanged()
    {
        OnPropertiesChanged(nameof(PageLabel), nameof(CanGoToPreviousPage), nameof(CanGoToNextPage));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
    }

    private void RenderNpcContent()
    {
        if (_selectedMob is not { } mob || _selectedZone is not { } zone)
        {
            string intro = _storageError ?? _dpsStatus ??
                "Importing the selected log's history and recording new interactions. Choose a name/zone above. Unlock floating overlays to search.";
            Show("NPC compendium", "Search recorded NPCs or look up an exact name", [new(intro, Wrap: true)]);
            return;
        }
        var resistanceInfo = _services.GetMob(zone, mob);
        string spellSignature = string.Join("\n", resistanceInfo.Spells);
        if (_wikiSelection != mob + "\n" + zone || _spellSignature != spellSignature)
        {
            StartWikiLookup(false);
        }
        NpcReadModel? entry = _entryKey == mob + "\n" + zone && _entryRevision == _services.Revision
            ? _selectedEntry
            : null;
        var rows = new List<OverlayRow>();
        if (_storageError is not null)
        {
            rows.Add(new(_storageError, Wrap: true, Bold: true));
        }
        if (_dpsStatus is not null)
        {
            rows.Add(new(_dpsStatus, Wrap: true, Bold: true));
        }
        if (_viewIndex == 1)
        {
            AddNpcSpellRows(rows, resistanceInfo, zone, mob);
        }
        else if (_viewIndex == 2)
        {
            AddNpcEncounterRows(rows, entry);
        }
        else
        {
            AddNpcOverviewRows(rows, entry, mob, zone);
        }
        if (_viewIndex != 0)
        {
            rows.AddRange(MobInfoOverlayViewModel.MobQuestRows(resistanceInfo.Wiki));
        }
        Show(new(mob, zone, rows, WikiLinks.SearchUrl(mob), mob));
    }

    private void AddNpcOverviewRows(List<OverlayRow> rows, NpcReadModel? entry, string mob, string zone)
    {
        if (entry is not null)
        {
            AddNpcCombatSummary(rows, entry);
        }
        rows.Add(new("NPC reference", Bold: true, QuestHeading: true));
        var shared = MobInfoOverlayViewModel.BuildMobInfoContent(_services.GetMob(zone, mob), mob, zone,
            entry?.LastConsider, entry?.LastAssessment, _wikiStatus, _services.CanSaveMobData, compact: true);
        rows.AddRange(shared.Rows);
        if (entry is null)
        {
            rows.Add(new("No recorded encounters",
                Details: "Wiki reference only. The selected log's history and new live interactions are recorded automatically.",
                Stacked: true));
        }
        else
        {
            var seen = new List<(string Label, string Value)>
            {
                ("First seen", $"{entry.FirstSeen:g}"),
                ("Considers", $"{entry.Considers:N0}"),
            };
            if (entry.LastConsider is { } consider)
            {
                seen.Add(("Last consider", $"{consider.Time:g}"));
            }
            rows.Add(new("Last seen", $"{entry.LastSeen:g}", Table: OverlayTable.Pairs(seen),
                Details: entry.LastConsider is not null ? "Historical, not your current game target." : null,
                Stacked: true));
        }
    }

    private static void AddNpcCombatSummary(List<OverlayRow> rows, NpcReadModel entry)
    {
        var encounters = entry.Encounters;
        rows.Add(new("Your combat record",
            entry.HasPersonalDamage ? $"{entry.YourAverageDps:N1} average DPS" : "No personal damage recorded",
            Table: OverlayTable.Pairs(
            [
                ("Damage", $"{entry.YourDamage:N0}"),
                ("Encounters", $"{encounters.Count:N0}"),
                ("NPC deaths observed", $"{entry.ObservedDeaths:N0}"),
                ("Damage taken", $"{entry.IncomingDamage:N0}"),
            ]),
            Stacked: true));
        if (entry.HasOtherDamage)
        {
            rows.Add(new("Other attackers", $"{entry.OtherAverageDps:N1} average DPS",
                Details: $"{entry.OtherDamage:N0} observed damage; group membership is not verified.",
                Stacked: true));
        }
    }

    private void AddNpcEncounterRows(List<OverlayRow> rows, NpcReadModel? entry)
    {
        var encounters = entry?.Encounters ?? [];
        _lastHistoryCount = encounters.Count;
        int pages = PageCount;
        _historyPage = Math.Clamp(_historyPage, 0, pages - 1);
        NotifyHistoryPageStateChanged();
        if (entry is not null)
        {
            AddNpcCombatSummary(rows, entry);
        }
        if (encounters.Count == 0)
        {
            rows.Add(new("No recorded encounters",
                Details: "Interactions from the selected log will appear here. Missing deaths are never treated as confirmed kills.",
                Stacked: true));
        }
        foreach (var encounter in encounters.Skip(_historyPage * HistoryPageSize).Take(HistoryPageSize))
        {
            var spellRows = encounter.Spells
                .OrderByDescending(spell => spell.Damage)
                .Select(spell => (IReadOnlyList<string>)[spell.Spell, $"{spell.Damage:N0}",
                    $"{spell.Landings:N0}", $"{spell.Resists:N0}"])
                .ToArray();
            OverlayTable? spells = spellRows.Length > 0
                ? new OverlayTable(["Spell", "Damage", "Landings", "Resists"], spellRows)
                : null;
            rows.Add(new($"{encounter.Start:g}  /  {NpcOutcomeText(encounter)}",
                $"{encounter.YourDps:N1} DPS",
                Table: OverlayTable.Pairs(
                [
                    ("Character", encounter.Character),
                    ("Classes", encounter.Classes ?? "unknown"),
                    ("Level", encounter.Level?.ToString() ?? "?"),
                    ("Duration", $"{encounter.Seconds:0.#}s"),
                    ("Your damage", $"{encounter.YourDamage:N0}"),
                    ("Others' damage", $"{encounter.OtherDamage:N0}"),
                    ("Damage taken", $"{encounter.IncomingDamage:N0}"),
                ], pairsPerRow: 2, then: spells),
                Stacked: true));
        }
        rows.Add(new("How this history is measured", Details:
            "Average DPS is total damage / summed encounter seconds (last activity - start + 1, minimum 1s). " +
            "Your average uses fights where you dealt damage. Direct landings include partial/finishing hits; DoT ticks only add damage. " +
            "Encounters stay open until a logged death, zone/login, or loadout/level change; inactivity never closes them. " +
            "Same-name NPCs can merge if an ending event is missing. No raw lines are stored.", Stacked: true));
    }

    /// <summary>Formats the typed outcome label for an encounter.</summary>
    public static string NpcOutcomeText(NpcEncounterDetails encounter)
    {
        return encounter.Outcome switch
        {
            NpcEncounterOutcome.Active => "Active",
            NpcEncounterOutcome.LoadoutChanged => "Loadout changed",
            NpcEncounterOutcome.PlayerDied => "You died",
            NpcEncounterOutcome.NpcDied => "NPC died",
            NpcEncounterOutcome.Zoned => "Zoned",
            NpcEncounterOutcome.Login => "Login",
            NpcEncounterOutcome.Unknown => $"Unknown outcome ({encounter.UnrecognizedOutcome})",
            _ => throw new ArgumentOutOfRangeException(nameof(encounter)),
        };
    }

    private void AddNpcSpellRows(List<OverlayRow> rows, MobDetails resistanceInfo, string zone, string mob)
    {
        rows.Add(new("Spell resistance history", Bold: true, QuestHeading: true));
        rows.Add(new("Saved observations for this NPC and zone. School and damage rating are separate.",
            Wrap: true, Scale: 0.85));
        if (resistanceInfo.Spells.Count == 0)
        {
            rows.Add(new("No spell observations yet",
                Details: "Targeted resists and confirmed personal casts will appear here as the log is read.",
                Stacked: true));
        }
        foreach (var card in _services.NpcSpellDetails(zone, mob))
        {
            var presentation = ResistancePresentation.Spell(card);
            rows.Add(new(card.Spell, presentation.Result,
                Table: OverlayTable.Pairs(presentation.Evidence, pairsPerRow: 3),
                Details: presentation.Explanation, Stacked: true));
        }
        rows.Add(new("About damage ratings", Details:
            "Only explicit resists enter the resist percentage for verified single-target instant damage (including lifetaps). " +
            "Single-target DoTs count once per cast: landed when its first tick hits, or resisted; ticks from a DoT already running are not counted. " +
            "Area waves are not reliable cast-success denominators. " +
            "Reduced damage is shown separately using 90% of the sample-weighted median; critical/modified and recent finishing hits are excluded. " +
            "Missing school, reduced damage, and unsupported spell shape are not NPC immunity.", Stacked: true));
        if (!string.IsNullOrEmpty(_wikiStatus))
        {
            rows.Add(new(_wikiStatus, Wrap: true, Scale: 0.85));
        }
    }
}
