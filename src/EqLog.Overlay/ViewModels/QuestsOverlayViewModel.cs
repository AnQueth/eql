using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay.Messaging;

namespace EqLog.Overlay.ViewModels;

/// <summary>Detached inputs used by the quest opportunities renderer.</summary>
/// <param name="Inventory">The current inventory projection.</param>
/// <param name="CharacterName">The character whose opportunities are shown.</param>
/// <param name="Cache">Cached wiki lookups for inventory items.</param>
/// <param name="CacheRevision">The revision of the wiki cache.</param>
/// <param name="SessionRevision">The current session revision.</param>
public sealed record QuestsOverlayState(
    InventoryReadModel Inventory,
    string CharacterName,
    IReadOnlyDictionary<string, QuestLookupResult> Cache,
    long CacheRevision,
    long SessionRevision);

/// <summary>The quest opportunities overlay, including its filter, sort preference and wiki lookup progress.</summary>
public sealed class QuestsOverlayViewModel : OverlayViewModel, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<QuestsOverlayState> _getState;
    private readonly Action<string> _filterChanged;
    private readonly Action<bool> _sortChanged;
    private readonly AsyncTextFilter<OverlayContent> _filterWork;
    private string _filterText;
    private bool _recentFirst;
    private string _filterError = "";
    private double _progressMaximum = 1;
    private double _progressValue;
    private string _currentStatus = "";
    private OverlayTable? _progressTable;

    /// <summary>Creates the quest opportunities overlay.</summary>
    public QuestsOverlayViewModel(
        OverlayDefinition definition,
        IOverlayActions actions,
        Dispatcher dispatcher,
        Func<QuestsOverlayState> getState,
        string filterText,
        bool recentFirst,
        Action<string> filterChanged,
        Action<bool> sortChanged,
        Action<Exception>? reportFilterError = null)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(getState);
        ArgumentNullException.ThrowIfNull(filterChanged);
        ArgumentNullException.ThrowIfNull(sortChanged);
        _dispatcher = dispatcher;
        _getState = getState;
        _filterChanged = filterChanged;
        _sortChanged = sortChanged;
        _filterText = filterText ?? "";
        _recentFirst = recentFirst;
        _filterWork = new AsyncTextFilter<OverlayContent>(dispatcher, error =>
        {
            FilterError = $"Quest filtering failed: {error.Message}";
            reportFilterError?.Invoke(error);
        });
        UpdateProgress(0, 0, 0, 0, 0, "Waiting for inventory or loot", "");
    }

    /// <summary>The case-insensitive quest or item filter.</summary>
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

    /// <summary>Whether to sort quests by the most recently looted item rather than alphabetically.</summary>
    public bool RecentFirst
    {
        get
        {
            return _recentFirst;
        }
        set
        {
            if (SetProperty(ref _recentFirst, value))
            {
                OnPropertyChanged(nameof(SortIndex));
                _sortChanged(value);
            }
        }
    }

    /// <summary>The available quest sort choices.</summary>
    public IReadOnlyList<string> SortOptions { get; } = ["Recent items", "Quest name (A-Z)"];

    /// <summary>The selected sort option, with recent items first at index zero.</summary>
    public int SortIndex
    {
        get
        {
            return _recentFirst ? 0 : 1;
        }
        set
        {
            RecentFirst = value == 0;
        }
    }

    /// <summary>An error produced while filtering quest opportunities.</summary>
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

    /// <summary>The largest value shown by the lookup progress bar.</summary>
    public double ProgressMaximum
    {
        get
        {
            return _progressMaximum;
        }
        private set
        {
            SetProperty(ref _progressMaximum, value);
        }
    }

    /// <summary>The completed lookup count shown by the progress bar.</summary>
    public double ProgressValue
    {
        get
        {
            return _progressValue;
        }
        private set
        {
            SetProperty(ref _progressValue, value);
        }
    }

    /// <summary>The current lookup status shown beneath the progress summary.</summary>
    public string CurrentStatus
    {
        get
        {
            return _currentStatus;
        }
        private set
        {
            SetProperty(ref _currentStatus, value);
        }
    }

    /// <summary>The stable progress table shown above the quest rows.</summary>
    public OverlayTable? ProgressTable
    {
        get
        {
            return _progressTable;
        }
        private set
        {
            SetProperty(ref _progressTable, value);
        }
    }

    /// <summary>Updates the progress summary from the inventory/wiki service.</summary>
    public void UpdateProgress(
        int total,
        int completed,
        int cached,
        int queued,
        int failed,
        string summary,
        string currentStatus)
    {
        ProgressMaximum = Math.Max(1, total);
        ProgressValue = completed;
        OverlayTable table = OverlayTable.Columns(
        [
            ("Checked", $"{completed:N0} / {total:N0}"),
            ("Done", total == 0 ? "-" : $"{100.0 * completed / total:0}%"),
            ("Cached", $"{cached:N0}"),
            ("Queued", $"{queued:N0}"),
            ("Failed", $"{failed:N0}"),
        ]);
        ProgressTable = table;
        CurrentStatus = total == 0 && string.IsNullOrEmpty(currentStatus) ? summary : currentStatus;
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        QuestsOverlayState state = _getState();
        var key = (state.Inventory, state.CharacterName, _filterText, _recentFirst, state.CacheRevision, state.SessionRevision);
        if (!_filterWork.Matches(key))
        {
            var cache = new Dictionary<string, QuestLookupResult>(state.Cache, StringComparer.OrdinalIgnoreCase);
            _filterWork.Request(
                key,
                token => BuildQuestOpportunities(state.Inventory, state.CharacterName, cache, _filterText, _recentFirst, token),
                context: (state.CharacterName, state.SessionRevision));
        }

        OverlayContent content = _filterWork.Result
            ?? new OverlayContent("Quest opportunities", state.CharacterName,
                [new OverlayRow("Filtering quest opportunities...", Wrap: true)]);
        if (FilterError.Length > 0)
        {
            content = new OverlayContent(content.Title, content.Subtitle,
                [new OverlayRow(FilterError, Wrap: true), .. content.Rows]);
        }
        Show(content);
    }

    /// <summary>Builds rows for quest opportunities from detached inventory and wiki data.</summary>
    public static OverlayContent BuildQuestOpportunities(
        InventoryReadModel inventory,
        string character,
        IReadOnlyDictionary<string, QuestLookupResult> cache,
        string filter,
        bool recentFirst,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(cache);
        token.ThrowIfCancellationRequested();
        var rows = new List<OverlayRow>();
        var quests = inventory.Opportunities(character, cache, filter, recentFirst);
        foreach (var quest in quests)
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new OverlayRow(quest.Title, Bold: true, QuestHeading: true, Wrap: true,
                Link: WikiLinks.PageUrl(quest.Title), ToolTip: $"Open {quest.Title} on EQLWiki"));
            var items = recentFirst
                ? quest.Items.OrderByDescending(item => item.LastLootUtc).ThenBy(item => item.Item, StringComparer.OrdinalIgnoreCase)
                : quest.Items.OrderBy(item => item.Item, StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                rows.Add(new OverlayRow(item.Item, Wrap: true, LootItem: item.Item,
                    Table: OverlayTable.Columns(
                    [
                        ("Owned", $"{item.Owned}"),
                        ("Carried", $"{item.Carried}"),
                        ("Bank", $"{item.Bank}"),
                        ("Hoard", $"{item.Hoard}"),
                        ("Depot", $"{item.Depot}"),
                        ("Other", $"{item.OtherStorage}"),
                        ("Collection", $"{item.Collection}"),
                        ("Installed", $"{item.Installed}"),
                        ("Looted*", $"{item.Historical}"),
                    ]),
                    Details: item.Historical > 0 ? "* Looted is from log history and unconfirmed." : null));
            }
        }
        if (rows.Count == 0)
        {
            rows.Add(new OverlayRow(string.IsNullOrWhiteSpace(filter)
                ? "No quest opportunities found yet."
                : "No quests or items match your filter.", Wrap: true));
        }

        return new OverlayContent("Quest opportunities", character, rows);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        _filterWork.Dispose();
    }
}
