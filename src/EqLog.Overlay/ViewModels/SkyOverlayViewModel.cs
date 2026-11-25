using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Media;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>Detached source data required to render the Sky quest tracker.</summary>
/// <param name="Inventory">The current inventory projection.</param>
/// <param name="CharacterName">The current character, if known.</param>
/// <param name="Catalog">The loaded Sky quest data, when available.</param>
/// <param name="IsLoading">Whether a catalog request is in progress.</param>
/// <param name="Status">The latest wiki service status.</param>
/// <param name="SessionRevision">The current session revision.</param>
public sealed record SkyOverlayState(
    InventoryReadModel Inventory,
    string CharacterName,
    SkyQuestCatalog? Catalog,
    bool IsLoading,
    string Status,
    long SessionRevision);

/// <summary>The Plane of Sky quest tracker with persisted class, view, filter and watch state.</summary>
public sealed class SkyOverlayViewModel : OverlayViewModel, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<SkyOverlayState> _getState;
    private readonly Func<bool, bool, Task> _loadQuests;
    private readonly Func<bool> _tryInitialLoad;
    private readonly Action<string> _filterChanged;
    private readonly Action<string> _classChanged;
    private readonly Action<int> _viewChanged;
    private readonly Action<IReadOnlyList<string>> _watchedChanged;
    private readonly Action<string> _statusChanged;
    private readonly Action _watchTracked;
    private readonly AsyncTextFilter<OverlayContent> _filterWork;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly RelayCommand _openWikiCommand;
    private IReadOnlyList<string> _cachedClassOptions = [];
    private SkyQuestCatalog? _classOptionsCatalog;
    private string _classOptionsSelection = "";
    private bool _hasClassOptionsCache;
    private HashSet<string> _watchedQuestSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private string _watchedQuestKey = "";
    private SkyOverlayFilterKey _lastFilterKey;
    private bool _hasFilterKey;
    private string _filterText;
    private string _selectedClass;
    private int _selectedView;
    private string _status = "";
    private bool _isLoading;
    private string _filterError = "";

    /// <summary>Creates the Sky quest tracker.</summary>
    public SkyOverlayViewModel(
        OverlayDefinition definition,
        IOverlayActions actions,
        Dispatcher dispatcher,
        Func<SkyOverlayState> getState,
        Func<bool, bool, Task> loadQuests,
        Func<bool> tryInitialLoad,
        string filterText,
        string selectedClass,
        int selectedView,
        IEnumerable<string> watchedQuestKeys,
        Action<string> filterChanged,
        Action<string> classChanged,
        Action<int> viewChanged,
        Action<IReadOnlyList<string>> watchedChanged,
        Action<string> statusChanged,
        Action watchTracked)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(getState);
        ArgumentNullException.ThrowIfNull(loadQuests);
        ArgumentNullException.ThrowIfNull(tryInitialLoad);
        ArgumentNullException.ThrowIfNull(watchedQuestKeys);
        ArgumentNullException.ThrowIfNull(filterChanged);
        ArgumentNullException.ThrowIfNull(classChanged);
        ArgumentNullException.ThrowIfNull(viewChanged);
        ArgumentNullException.ThrowIfNull(watchedChanged);
        ArgumentNullException.ThrowIfNull(statusChanged);
        ArgumentNullException.ThrowIfNull(watchTracked);
        _dispatcher = dispatcher;
        _getState = getState;
        _loadQuests = loadQuests;
        _tryInitialLoad = tryInitialLoad;
        _filterChanged = filterChanged;
        _classChanged = classChanged;
        _viewChanged = viewChanged;
        _watchedChanged = watchedChanged;
        _statusChanged = statusChanged;
        _watchTracked = watchTracked;
        _filterText = filterText ?? "";
        _selectedClass = string.IsNullOrWhiteSpace(selectedClass) ? SkyOverlayRenderer.AllSkyClasses : selectedClass;
        _selectedView = Math.Clamp(selectedView, SkyOverlayRenderer.SkyViewByClass, SkyOverlayRenderer.SkyViewNeed);
        UpdateClassOptions(null);
        WatchedQuestKeys = new ObservableCollection<string>(watchedQuestKeys);
        UpdateWatchedQuestSnapshot();
        WatchedQuestKeys.CollectionChanged += OnWatchedQuestKeysChanged;
        _filterWork = new AsyncTextFilter<OverlayContent>(dispatcher, error =>
        {
            FilterError = $"Sky filtering failed: {error.Message}";
            _statusChanged(FilterError);
        });
        _refreshCommand = new AsyncRelayCommand(() => _loadQuests(true, false));
        _openWikiCommand = new RelayCommand(() => Actions.OpenLink(SkyQueries.SourceUrl));
    }

    /// <summary>The case-insensitive search across quests, rewards, items and droppers.</summary>
    public string FilterText
    {
        get
        {
            return _filterText;
        }
        set
        {
            string valueToSet = value ?? "";
            if (SetProperty(ref _filterText, valueToSet))
            {
                FilterError = "";
                _filterChanged(valueToSet);
            }
        }
    }

    /// <summary>The class selected for the By class, By dropper and Need views.</summary>
    public string SelectedClass
    {
        get
        {
            return _selectedClass;
        }
        set
        {
            string valueToSet = string.IsNullOrWhiteSpace(value) ? SkyOverlayRenderer.AllSkyClasses : value;
            if (SetProperty(ref _selectedClass, valueToSet))
            {
                _classChanged(valueToSet);
            }
        }
    }

    /// <summary>The latest wiki status shown by the Sky tracker.</summary>
    public string Status
    {
        get
        {
            return _status;
        }
        private set
        {
            SetProperty(ref _status, value);
        }
    }

    /// <summary>Whether the Sky catalog is currently loading.</summary>
    public bool IsLoading
    {
        get
        {
            return _isLoading;
        }
        private set
        {
            SetProperty(ref _isLoading, value);
        }
    }

    /// <summary>An error produced while filtering Sky quests.</summary>
    public string FilterError
    {
        get
        {
            return _filterError;
        }
        private set
        {
            SetProperty(ref _filterError, value);
        }
    }

    /// <summary>The selected tab: By class, By dropper, Watching or Need.</summary>
    public int SelectedView
    {
        get
        {
            return _selectedView;
        }
        set
        {
            int valueToSet = Math.Clamp(value, SkyOverlayRenderer.SkyViewByClass, SkyOverlayRenderer.SkyViewNeed);
            if (SetProperty(ref _selectedView, valueToSet))
            {
                OnPropertyChanged(nameof(SelectedTabIndex));
                _viewChanged(valueToSet);
            }
        }
    }

    /// <summary>The tab control index, preserving the original order of Need and Watching.</summary>
    public int SelectedTabIndex
    {
        get
        {
            return _selectedView switch
            {
                SkyOverlayRenderer.SkyViewNeed => 2,
                SkyOverlayRenderer.SkyViewWatching => 3,
                _ => _selectedView,
            };
        }
        set
        {
            SelectedView = value switch
            {
                2 => SkyOverlayRenderer.SkyViewNeed,
                3 => SkyOverlayRenderer.SkyViewWatching,
                _ => Math.Clamp(value, SkyOverlayRenderer.SkyViewByClass, SkyOverlayRenderer.SkyViewByDropper),
            };
        }
    }

    /// <summary>Available Sky quest classes, including the all-classes option.</summary>
    public ObservableCollection<string> ClassOptions { get; } = [];

    /// <summary>Keys of quests the user is watching.</summary>
    public ObservableCollection<string> WatchedQuestKeys
    {
        get;
    }

    internal Task FilterCompletion
    {
        get
        {
            return _filterWork.Completion;
        }
    }

    /// <summary>Reloads the Plane of Sky catalog from EQLWiki.</summary>
    public AsyncRelayCommand RefreshCommand
    {
        get
        {
            return _refreshCommand;
        }
    }

    /// <summary>Opens the Plane of Sky page on EQLWiki.</summary>
    public RelayCommand OpenWikiCommand
    {
        get
        {
            return _openWikiCommand;
        }
    }

    /// <summary>Builds stable class choices and preserves a matching current selection.</summary>
    public static IReadOnlyList<string> BuildClassOptions(SkyQuestCatalog? catalog, string selected)
    {
        IEnumerable<string> classes = catalog is null
            ? [selected]
            : catalog.Quests.Select(quest => quest.Class);
        return new[] { SkyOverlayRenderer.AllSkyClasses }
            .Concat(classes.Where(name => !string.IsNullOrWhiteSpace(name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name == SkyOverlayRenderer.AllSkyClasses ? 0 : 1)
            .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    protected override void OnWatchToggled(string watchKey)
    {
        _watchTracked();
        int index = -1;
        for (int i = 0; i < WatchedQuestKeys.Count; i++)
        {
            if (string.Equals(WatchedQuestKeys[i], watchKey, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index >= 0)
        {
            WatchedQuestKeys.RemoveAt(index);
        }
        else
        {
            WatchedQuestKeys.Add(watchKey);
        }
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        SkyOverlayState state = _getState();
        IsLoading = state.IsLoading;
        Status = state.Status;
        UpdateClassOptions(state.Catalog);
        if (state.Catalog is null && !state.IsLoading)
        {
            _tryInitialLoad();
        }

        var key = new SkyOverlayFilterKey(
            state.Inventory,
            state.CharacterName,
            _filterText,
            _selectedClass,
            _selectedView,
            state.Catalog,
            state.IsLoading,
            state.Status,
            _watchedQuestKey,
            state.SessionRevision);
        if (!_hasFilterKey || _lastFilterKey != key)
        {
            if (!_filterWork.Matches(key))
            {
                InventoryReadModel inventory = state.Inventory;
                string characterName = state.CharacterName;
                string filterText = _filterText;
                string selectedClass = _selectedClass;
                int selectedView = _selectedView;
                SkyQuestCatalog? catalog = state.Catalog;
                bool isLoading = state.IsLoading;
                string status = state.Status;
                ISet<string> watched = _watchedQuestSnapshot;
                long sessionRevision = state.SessionRevision;

                _filterWork.Request(
                    key,
                    token => SkyOverlayRenderer.BuildSkyOverlayContent(
                        inventory,
                        catalog,
                        characterName,
                        filterText,
                        selectedClass,
                        selectedView,
                        watched,
                        isLoading,
                        status,
                        token),
                    context: (characterName, sessionRevision));
            }
            _lastFilterKey = key;
            _hasFilterKey = true;
        }

        OverlayContent content = _filterWork.Result
            ?? new OverlayContent("Sky quest tracker", state.CharacterName,
                [new OverlayRow("Filtering Sky quests...", Wrap: true)]);
        if (FilterError.Length > 0 && state.Status != FilterError)
        {
            content = new OverlayContent(content.Title, content.Subtitle,
                [new OverlayRow(FilterError, Wrap: true), .. content.Rows]);
        }
        Show(content);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        WatchedQuestKeys.CollectionChanged -= OnWatchedQuestKeysChanged;
        _filterWork.Dispose();
    }

    private void UpdateClassOptions(SkyQuestCatalog? catalog)
    {
        bool cacheMatches = _hasClassOptionsCache &&
            ReferenceEquals(_classOptionsCatalog, catalog) &&
            string.Equals(_classOptionsSelection, _selectedClass, StringComparison.Ordinal);
        if (cacheMatches && ClassOptions.SequenceEqual(_cachedClassOptions, StringComparer.Ordinal))
        {
            return;
        }

        if (!cacheMatches)
        {
            _cachedClassOptions = BuildClassOptions(catalog, _selectedClass);
            _classOptionsCatalog = catalog;
            _classOptionsSelection = _selectedClass;
            _hasClassOptionsCache = true;
        }
        if (!ClassOptions.SequenceEqual(_cachedClassOptions, StringComparer.Ordinal))
        {
            ClassOptions.Clear();
            foreach (string option in _cachedClassOptions)
            {
                ClassOptions.Add(option);
            }
        }
        string? matchingClass = ClassOptions.FirstOrDefault(option =>
            option.Equals(_selectedClass, StringComparison.OrdinalIgnoreCase));
        if (matchingClass is null)
        {
            SelectedClass = SkyOverlayRenderer.AllSkyClasses;
        }
        else if (!string.Equals(matchingClass, _selectedClass, StringComparison.Ordinal))
        {
            SelectedClass = matchingClass;
        }
        _classOptionsSelection = _selectedClass;
    }

    private void OnWatchedQuestKeysChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        UpdateWatchedQuestSnapshot();
        _watchedChanged(WatchedQuestKeys.ToArray());
    }

    private void UpdateWatchedQuestSnapshot()
    {
        _watchedQuestSnapshot = WatchedQuestKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _watchedQuestKey = string.Join("\n", WatchedQuestKeys);
    }

    private readonly record struct SkyOverlayFilterKey(
        InventoryReadModel Inventory,
        string CharacterName,
        string FilterText,
        string SelectedClass,
        int SelectedView,
        SkyQuestCatalog? Catalog,
        bool IsLoading,
        string Status,
        string WatchedKey,
        long SessionRevision);
}

/// <summary>Pure row rendering for the Sky tracker, separated from the WPF shell.</summary>
internal static class SkyOverlayRenderer
{
    internal const string AllSkyClasses = "All classes";
    internal const int SkyViewByClass = 0;
    internal const int SkyViewByDropper = 1;
    internal const int SkyViewWatching = 2;
    internal const int SkyViewNeed = 3;

    private static readonly Brush SkyHave = FrozenBrush(Color.FromRgb(120, 230, 120));
    private static readonly Brush SkyNeed = FrozenBrush(Color.FromRgb(255, 170, 60));
    private static readonly Brush SkyDim = FrozenBrush(Color.FromRgb(170, 170, 180));

    internal static OverlayContent BuildSkyOverlayContent(InventoryReadModel inventory, SkyQuestCatalog? source,
        string character, string filter, string selectedClass, int view, ISet<string> watched,
        bool loading, string status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = new List<OverlayRow>();
        if (loading)
        {
            rows.Add(new("Refreshing wiki data...", Wrap: true, Foreground: SkyDim));
        }
        else if (status.Length > 0)
        {
            rows.Add(new(status, Wrap: true, Foreground: SkyNeed));
        }

        if (source is not { } catalog)
        {
            if (rows.Count == 0)
            {
                rows.Add(new("Loading Plane of Sky quests...", Wrap: true));
            }
            return new("Sky quest tracker", null, rows);
        }

        var classQuests = catalog.Quests
            .Where(q => selectedClass == AllSkyClasses || q.Class.Equals(selectedClass, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string subtitle;
        switch (view)
        {
            case SkyViewByDropper:
                subtitle = AddSkyDropperRows(rows, catalog, classQuests, filter, character, watched, inventory, selectedClass);
                break;
            case SkyViewWatching:
                subtitle = AddSkyWatchRows(rows, catalog, filter, character, watched, inventory);
                break;
            case SkyViewNeed:
                subtitle = AddSkyNeedRows(rows, catalog, classQuests, filter, character, watched, inventory, selectedClass);
                break;
            default:
                subtitle = AddSkyClassRows(rows, catalog, classQuests, filter, character, watched, inventory, selectedClass);
                break;
        }

        if (character.Length > 0)
        {
            subtitle += $" · {character}";
        }
        token.ThrowIfCancellationRequested();
        return new("Sky quest tracker", subtitle, rows);
    }

    private static string AddSkyClassRows(List<OverlayRow> rows, SkyQuestCatalog catalog, IReadOnlyList<SkyQuest> classQuests,
        string filter, string character, ISet<string> watched, InventoryReadModel inventory, string selectedClass)
    {
        var quests = classQuests.Where(q => SkyQueries.Matches(q, filter, catalog)).ToArray();
        var progress = SkyQueries.ProgressAll(quests, inventory, character);
        foreach (var quest in quests)
        {
            AddSkyQuestRows(rows, catalog, quest, progress[quest], watched);
        }

        if (quests.Length == 0)
        {
            rows.Add(new("No quests match the search.", Wrap: true, Foreground: SkyDim));
        }
        return $"{selectedClass} · {quests.Length} quests";
    }

    private static void AddSkyQuestRows(List<OverlayRow> rows, SkyQuestCatalog catalog, SkyQuest quest,
        IReadOnlyList<SkyItemProgress> progress, ISet<string> watched)
    {
        string key = SkyQueries.Key(quest);
        int have = progress.Count(p => p.Satisfied);
        rows.Add(new(quest.Name, $"{have} / {progress.Count}", Bold: true, QuestHeading: true, Wrap: true,
            Foreground: progress.Count > 0 && have == progress.Count ? SkyHave : null,
            WatchKey: key, Watched: watched.Contains(key),
            Link: quest.Url, ToolTip: $"Open the {quest.Class} tests on EQLWiki"));
        var info = new List<(string Label, string Value)>
        {
            ("Reward", quest.Reward),
            ("Quest giver", quest.Giver),
        };
        if (!string.IsNullOrWhiteSpace(quest.Phrase))
        {
            info.Add(("Say", $"\"{quest.Phrase}\""));
        }
        rows.Add(new("", Table: OverlayTable.Pairs(info).WithLinks(
        [
            (quest.Reward, WikiLinks.ItemUrl(quest.Reward)),
            (quest.Giver, WikiLinks.SearchUrl(quest.Giver)),
        ])));
        foreach (var item in progress)
        {
            rows.Add(new(SkyQueries.Label(quest, item.Item), $"{(item.Satisfied ? "HAVE" : "NEED")} {item.Owned} / {item.Required}",
                Wrap: true, LootItem: item.Item, Foreground: item.Satisfied ? SkyHave : SkyNeed,
                Table: SkyItemTable(catalog, item)));
        }
    }

    private static OverlayTable SkyItemTable(SkyQuestCatalog catalog, SkyItemProgress item)
    {
        var droppedBy = SkyQueries.DroppedBy(catalog, item.Item);
        var pairs = new List<(string Label, string Value)>(
            droppedBy.Count > 0 ? OverlayTable.Labeled("Dropped by", droppedBy) : [("Dropped by", "No specific mob listed")]);
        if (item.Carried > 0)
        {
            pairs.Add(("Carried / equipped", $"{item.Carried}"));
        }

        if (item.Bank > 0)
        {
            pairs.Add(("Bank", $"{item.Bank}"));
        }

        if (item.Hoard > 0)
        {
            pairs.Add(("Dragon's Hoard", $"{item.Hoard}"));
        }

        if (item.Depot > 0)
        {
            pairs.Add(("Personal depot", $"{item.Depot}"));
        }

        if (item.OtherStorage > 0)
        {
            pairs.Add(("Other location", $"{item.OtherStorage}"));
        }

        if (item.Collection > 0)
        {
            pairs.Add(("Collection", $"{item.Collection} (verify turn-in)"));
        }

        if (item.Installed > 0)
        {
            pairs.Add(("Installed", $"{item.Installed} (not loose)"));
        }

        if (item.Historical > 0)
        {
            pairs.Add(("Looted", $"{item.Historical} (unconfirmed)"));
        }
        return OverlayTable.Pairs(pairs).WithLinks(droppedBy.Select(mob => (mob, WikiLinks.SearchUrl(mob))));
    }

    /// <summary>"Needed for" table listing quest names that link to their wiki section; watched quests are starred.</summary>
    private static OverlayTable SkyNeededFor(IEnumerable<SkyQuest> quests, ISet<string> watched)
    {
        var list = quests.ToArray();
        var duplicated = list.GroupBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var quest in list)
        {
            // The same quest name can exist for several classes; show the class so each link is distinct.
            string name = (watched.Contains(SkyQueries.Key(quest)) ? "\u2605 " : "") + quest.Name +
                (duplicated.Contains(quest.Name) ? $" ({quest.Class})" : "");
            names.Add(name);
            links.TryAdd(name, quest.Url);
        }
        return OverlayTable.Pairs(OverlayTable.Labeled("Needed for", names)) with
        {
            Links = links
        };
    }

    private static string AddSkyDropperRows(List<OverlayRow> rows, SkyQuestCatalog catalog, IReadOnlyList<SkyQuest> classQuests,
        string filter, string character, ISet<string> watched, InventoryReadModel inventory, string selectedClass)
    {
        var groups = SkyQueries.ByDropper(classQuests, catalog, filter);
        var progress = SkyQueries.ProgressAll(classQuests, inventory, character);
        foreach (var group in groups)
        {
            bool known = group.Mob.Length > 0;
            rows.Add(new(known ? group.Mob : "No specific mob listed", $"{group.Items.Count} item{(group.Items.Count == 1 ? "" : "s")}",
                Bold: true, QuestHeading: true, Wrap: true, Link: known ? WikiLinks.SearchUrl(group.Mob) : null,
                ToolTip: known ? $"Open {group.Mob} on EQLWiki" : "EQLWiki lists only the zone for these items (typically runes)."));
            foreach (var item in group.Items)
            {
                int owned = progress[item.Quests[0]].First(p => ItemPresentation.Comparer.Equals(p.Item, item.Item)).Owned;
                rows.Add(new(item.Label, $"Have {owned}", Wrap: true, LootItem: item.Item,
                    Foreground: owned > 0 ? SkyHave : null,
                    Table: SkyNeededFor(item.Quests, watched)));
            }
        }

        if (groups.Count == 0)
        {
            rows.Add(new("No items match the search.", Wrap: true, Foreground: SkyDim));
        }
        return $"{selectedClass} · {groups.Count(g => g.Mob.Length > 0)} droppers";
    }

    private static string AddSkyWatchRows(List<OverlayRow> rows, SkyQuestCatalog catalog, string filter, string character,
        ISet<string> watched, InventoryReadModel inventory)
    {
        var quests = catalog.Quests.Where(q => watched.Contains(SkyQueries.Key(q))).ToArray();
        if (quests.Length == 0)
        {
            rows.Add(new("No watched quests yet. Click the \u2606 next to a quest in By class to watch it; quests from any class can be watched together.",
                Wrap: true, Foreground: SkyDim));
            return "Watching 0 quests";
        }

        var progress = SkyQueries.ProgressAll(quests, inventory, character);
        var shown = quests.Where(q => SkyQueries.Matches(q, filter, catalog)).ToArray();

        var missing = new List<OverlayRow>();
        AddSkyNeededRows(missing, SkyQueries.ByDropper(shown, catalog), progress, watched, bossHeadings: false);
        if (missing.Count > 0)
        {
            rows.Add(new("Still needed - by dropper", Bold: true, QuestHeading: true));
            rows.AddRange(missing);
        }
        else if (shown.Length > 0)
        {
            rows.Add(new("Every listed item for the watched quests is owned.", Wrap: true, Foreground: SkyHave));
        }

        foreach (var quest in shown)
        {
            AddSkyQuestRows(rows, catalog, quest, progress[quest], watched);
        }

        if (shown.Length == 0)
        {
            rows.Add(new("No watched quests match the search.", Wrap: true, Foreground: SkyDim));
        }
        return $"Watching {quests.Length} quest{(quests.Length == 1 ? "" : "s")}";
    }

    private static string AddSkyNeedRows(List<OverlayRow> rows, SkyQuestCatalog catalog, IReadOnlyList<SkyQuest> classQuests,
        string filter, string character, ISet<string> watched, InventoryReadModel inventory, string selectedClass)
    {
        var progress = SkyQueries.ProgressAll(classQuests, inventory, character);
        var (bosses, items) = AddSkyNeededRows(rows, SkyQueries.ByDropper(classQuests, catalog, filter), progress, watched, bossHeadings: true);
        if (items == 0)
        {
            rows.Add(new(string.IsNullOrWhiteSpace(filter) ? "Nothing needed - every listed item is owned." : "No needed items match the search.",
                Wrap: true, Foreground: string.IsNullOrWhiteSpace(filter) ? SkyHave : SkyDim));
        }
        return $"{selectedClass} · need {items} item{(items == 1 ? "" : "s")} from {bosses} boss{(bosses == 1 ? "" : "es")}";
    }

    /// <summary>
    /// Adds boss rows followed by the items still missing from that boss. Items are not reserved per quest,
    /// so the shortfall compares the combined requirement against what is owned.
    /// </summary>
    private static (int Bosses, int Items) AddSkyNeededRows(List<OverlayRow> rows, IReadOnlyList<SkyDropGroup> groups,
        IReadOnlyDictionary<SkyQuest, IReadOnlyList<SkyItemProgress>> progress, ISet<string> watched, bool bossHeadings)
    {
        int bosses = 0;
        int items = 0;
        foreach (var group in SkyQueries.Needed(groups, progress))
        {
            var needed = new List<OverlayRow>();
            foreach (var item in group.Items)
            {
                needed.Add(new(item.Label, item.Owned > 0 ? $"need {item.Missing} (have {item.Owned})" : $"need {item.Required}",
                    Wrap: true, LootItem: item.Item, Foreground: SkyNeed,
                    Table: SkyNeededFor(item.Quests, watched)));
            }

            if (needed.Count == 0)
            {
                continue;
            }

            bool known = group.Mob.Length > 0;
            if (known)
            {
                bosses++;
            }

            items += needed.Count;
            rows.Add(new(known ? group.Mob : "No specific mob listed", bossHeadings ? $"{needed.Count} item{(needed.Count == 1 ? "" : "s")}" : "",
                Bold: true, QuestHeading: bossHeadings, Wrap: true, Link: known ? WikiLinks.SearchUrl(group.Mob) : null,
                ToolTip: known ? $"Open {group.Mob} on EQLWiki" : "EQLWiki lists only the zone for these items (typically runes)."));
            rows.AddRange(needed);
        }
        return (bosses, items);
    }


    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
