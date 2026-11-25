using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>Filters the current inventory's exaltations and displays the supported effects of their base items.</summary>
public sealed class ExaltationsOverlayViewModel : OverlayViewModel, IDisposable
{
    private readonly IExaltationsOverlayServices _services;
    private readonly Dictionary<string, ItemEffectInfo> _itemEffects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _itemEffectErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _itemEffectRetries = new(StringComparer.OrdinalIgnoreCase);
    private AsyncTextFilter<OverlayContent>? _filterWork;
    private bool _isWorking;
    private bool _installedOnly;
    private string _filterText = "";
    private string? _activeItem;
    private string? _storageError;
    private long _effectsRevision;
    private bool _disposed;

    /// <summary>Creates an exaltations overlay view model.</summary>
    public ExaltationsOverlayViewModel(OverlayDefinition definition, IOverlayActions actions,
        IExaltationsOverlayServices services)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        foreach (var effect in services.InitialEffects)
        {
            _itemEffects[effect.Item] = effect;
        }
        if (services.InitialError is { } error)
        {
            _storageError = $"Could not load effect cache: {error.Message}. Session-only lookups; existing cache kept.";
        }
        RefreshEffectsCommand = new AsyncRelayCommand(() => LookupExaltationEffectsAsync(true),
            () => !_isWorking && !_services.WikiLookupPaused);
        SetInstalledOnlyCommand = new RelayCommand<object>(SetInstalledOnly);
        if (definition.Key != "exaltations")
        {
            throw new ArgumentException("Exaltations view models require the exaltations overlay definition.", nameof(definition));
        }
    }

    /// <summary>The effect and item-name search text.</summary>
    public string FilterText
    {
        get
        {
            return _filterText;
        }
        set
        {
            SetProperty(ref _filterText, value ?? "");
        }
    }

    /// <summary>Whether to show only exaltations that the export confirms are installed.</summary>
    public bool InstalledOnly
    {
        get
        {
            return _installedOnly;
        }
        set
        {
            SetProperty(ref _installedOnly, value);
        }
    }

    /// <summary>Whether a base-item effect lookup is currently running.</summary>
    public bool IsWorking
    {
        get
        {
            return _isWorking;
        }
        private set
        {
            if (SetProperty(ref _isWorking, value))
            {
                RefreshEffectsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The currently looked-up base item, if any.</summary>
    public string? ActiveItem
    {
        get
        {
            return _activeItem;
        }
        private set
        {
            SetProperty(ref _activeItem, value);
        }
    }

    /// <summary>The most recent effect-cache or filtering error.</summary>
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
                _effectsRevision++;
            }
        }
    }

    /// <summary>Changes whether only export-confirmed installed exaltations are shown.</summary>
    public RelayCommand<object> SetInstalledOnlyCommand { get; }

    /// <summary>Refreshes base-item effects and descriptions without refreshing inventory.</summary>
    public AsyncRelayCommand RefreshEffectsCommand { get; }

    internal void NotifyWikiLookupStateChanged()
    {
        RefreshEffectsCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var all = CurrentExaltations();
        StartMissingEffectLookups(all);

        string? character = _services.CharacterName;
        bool current = _services.HasCurrentInventory;
        var key = (all, character, current, _installedOnly, _filterText, _activeItem, _storageError,
            _services.InventoryWritten, _effectsRevision, _services.InventoryRevision);
        _filterWork ??= new(Dispatcher.CurrentDispatcher, error =>
        {
            StorageError = $"Effect filtering failed: {error.Message}";
        });
        if (!_filterWork.Matches(key))
        {
            var effects = new Dictionary<string, ItemEffectInfo>(_itemEffects, StringComparer.OrdinalIgnoreCase);
            var errors = new Dictionary<string, string>(_itemEffectErrors, StringComparer.OrdinalIgnoreCase);
            string? activeItem = _activeItem;
            string? storageError = _storageError;
            bool installedOnly = _installedOnly;
            string filter = _filterText;
            DateTime? exported = _services.InventoryWritten;
            long inventoryRevision = _services.InventoryRevision;
            _filterWork.Request(key, token => BuildExaltationsContent(all, effects, errors, character,
                current, installedOnly, filter, activeItem, storageError, exported, token),
                context: (character, inventoryRevision));
        }

        Show(_filterWork.Result ?? new OverlayContent("Exaltations", character,
            [new OverlayRow("Filtering exaltations...", Wrap: true)]));
    }

    /// <summary>Clears local effect data after the shell resets the shared wiki cache.</summary>
    internal void ResetWikiCache()
    {
        _itemEffects.Clear();
        _itemEffectErrors.Clear();
        _itemEffectRetries.Clear();
        StorageError = null;
        ActiveItem = null;
        _effectsRevision++;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _filterWork?.Dispose();
    }

    internal static OverlayContent BuildExaltationsContent(IReadOnlyList<ExaltationLocation> all,
        IReadOnlyDictionary<string, ItemEffectInfo> effects, IReadOnlyDictionary<string, string> errors,
        string? character, bool current, bool installedOnly, string filter, string? activeItem,
        string? storageError, DateTime? exported, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = new List<OverlayRow>();
        if (storageError is not null)
        {
            rows.Add(new(storageError, Wrap: true));
        }
        if (!current)
        {
            rows.Add(new("Waiting for this character's inventory export. Run /outputfile inventory in game.", Wrap: true));
            return new("Exaltations", character, rows);
        }
        var shown = all.Where(location => (!installedOnly || location.Installed) &&
            (!location.WikiDefault || effects.GetValueOrDefault(location.BaseItem) is { Found: true, Effects.Count: > 0 }) &&
            InventoryQueries.MatchesExaltation(location, effects.GetValueOrDefault(location.BaseItem), filter)).ToArray();
        int ready = all.Select(location => location.BaseItem).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(item => effects.ContainsKey(item));
        int total = all.Select(location => location.BaseItem).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        rows.Add(new($"{shown.Where(location => !location.WikiDefault).Sum(location => location.Count)} matching exported copies / " +
            $"{all.Where(location => !location.WikiDefault).Sum(location => location.Count)} total; " +
            $"{shown.Count(location => location.WikiDefault)} unverified wiki-default matches; " +
            $"effects loaded {ready}/{total}." +
            (activeItem is { } active ? $" Looking up {active}..." : ""),
            Wrap: true, Scale: 0.9));
        if (exported is DateTime exportTime)
        {
            rows.Add(new($"Inventory snapshot: {exportTime.ToLocalTime():g}. Re-export after moving or swapping exaltations.",
                Wrap: true, Scale: 0.9));
        }
        foreach (string item in all.Select(location => location.BaseItem).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(item => errors.ContainsKey(item)))
        {
            rows.Add(new($"Effect lookup failed for {item}: {errors[item]}. " +
                "Effect searches may be incomplete. Retrying after 5 minutes, or use Refresh effects.", Wrap: true));
        }
        if (shown.Length == 0)
        {
            rows.Add(new(all.Count == 0 ? "No exaltations installed, loose, or in exalt storage in this export." :
                "No matches yet. Effect searches fill in as wiki lookups complete; try fewer words or turn off Installed only to include exalt storage.", Wrap: true));
        }
        foreach (var group in shown.GroupBy(location => (location.BaseItem, location.WikiDefault)))
        {
            token.ThrowIfCancellationRequested();
            effects.TryGetValue(group.Key.BaseItem, out var info);
            string? warning = null;
            if (errors.TryGetValue(group.Key.BaseItem, out string? error))
            {
                warning = $"Wiki lookup failed: {error}. Retrying after 5 minutes, or use Refresh effects.";
            }
            if (info is not null && WikiQueries.NeedsRefresh(info))
            {
                warning = string.Join("\n", new[] { warning, "Cached effects are stale; refresh pending." }
                    .Where(value => value is not null));
            }
            rows.Add(BuildExaltationCard(group.ToArray(), info, warning));
        }
        return new("Exaltations", character, rows);
    }

    internal static OverlayRow BuildExaltationCard(IReadOnlyList<ExaltationLocation> copies,
        ItemEffectInfo? info, string? warning = null)
    {
        var first = copies[0];
        var fields = new List<(string Label, string Value)>();
        var links = new List<(string Text, string Url)>();
        if (first.WikiDefault)
        {
            fields.Add(("Evidence", "Wiki default only - exaltation ownership and current installation unverified."));
        }
        if (info is null || !info.Found || info.Effects.Count == 0)
        {
            fields.Add(("Effect", info is null ? "Looking up wiki effects..." :
                !info.Found ? "Unknown - base item page not found or unsupported." :
                "Unknown - no supported effect listed on the base item."));
        }
        else
        {
            foreach (var effect in info.Effects)
            {
                string value = $"{effect.Kind}: {effect.Name}";
                fields.Add(("Effect", value));
                if (effect.Url is { } effectUrl)
                {
                    links.Add((value, effectUrl));
                }
                if (effect.Description is { } description)
                {
                    fields.Add(("What it does", description));
                }
            }
        }
        string source = $"{first.BaseItem} (original item wiki)";
        fields.Add(("Effect source", source));
        links.Add((source, info?.Url ?? WikiLinks.PageUrl(first.BaseItem)));
        for (int i = 0; i < copies.Count; i++)
        {
            var copy = copies[i];
            string prefix = copies.Count > 1 ? $"Copy {i + 1} - " : "";
            if (copy.WikiDefault)
            {
                fields.Add((prefix + "Original item", copy.Host ?? copy.BaseItem));
                fields.Add(("Item location", copy.Where));
                fields.Add(("Installation", "Unverified - check in-game Find Item; the exaltation may have been moved or removed."));
                continue;
            }
            fields.Add((prefix + "Installed in", copy.Installed
                ? copy.Host ?? "Unknown host - missing from export"
                : copy.InExaltStorage ? "Not installed (in exalt storage)" : "Not installed (loose exaltation)"));
            fields.Add((copy.Installed ? "Host location" : "Stored at", copy.Where));
            if (copy.Socket is { } socket)
            {
                fields.Add(("Socket", socket));
            }
            fields.Add(("Quantity", copy.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new(first.WikiDefault ? $"Expected exaltation (unverified): {first.Name}" : $"Exaltation: {first.Name}",
            Wrap: true, Bold: true, Stacked: true,
            Table: OverlayTable.Pairs(fields).WithLinks(links), Details: warning, IconItem: first.Name);
    }

    private void SetInstalledOnly(object parameter)
    {
        InstalledOnly = parameter is true;
    }

    private IReadOnlyList<ExaltationLocation> CurrentExaltations()
    {
        if (!_services.HasCurrentInventory)
        {
            return [];
        }
        return _services.ExaltationLocations;
    }

    private void StartMissingEffectLookups(IReadOnlyList<ExaltationLocation> all)
    {
        if (_isWorking || _services.WikiLookupPaused || !all.Any(location =>
            (!_itemEffects.TryGetValue(location.BaseItem, out var cached) || WikiQueries.NeedsRefresh(cached)) &&
            _itemEffectRetries.GetValueOrDefault(location.BaseItem) <= DateTime.UtcNow))
        {
            return;
        }
        _ = LookupExaltationEffectsAsync(false);
    }

    private async Task LookupExaltationEffectsAsync(bool force)
    {
        if (_isWorking || _services.WikiLookupPaused)
        {
            return;
        }
        using var lookup = _services.BeginLookup();
        var token = lookup.Token;
        IsWorking = true;
        try
        {
            string[] items = CurrentExaltations().OrderBy(location => location.WikiDefault)
                .Select(location => location.BaseItem)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string item in items)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                if (!CurrentExaltations().Any(location => location.BaseItem.Equals(item, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (!force && ((_itemEffects.TryGetValue(item, out var cached) && !WikiQueries.NeedsRefresh(cached)) ||
                    _itemEffectRetries.GetValueOrDefault(item) > DateTime.UtcNow))
                {
                    continue;
                }
                ActiveItem = item;
                try
                {
                    var result = await _services.GetItemEffectsAsync(item,
                        force ? WikiPriority.Interactive : WikiPriority.Background, force, token);
                    token.ThrowIfCancellationRequested();
                    _itemEffects[item] = result;
                    _effectsRevision++;
                    _itemEffectErrors.Remove(item);
                    _itemEffectRetries.Remove(item);
                    StorageError = _services.GetCacheError("item-effects:" + item) is { } cacheError
                        ? $"Could not save effects: {cacheError}. Results kept for this session." : null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (IsLookupFailure(error))
                {
                    _services.TrackWikiError(error);
                    _itemEffectErrors[item] = error.Message;
                    _effectsRevision++;
                    _itemEffectRetries[item] = DateTime.UtcNow.AddMinutes(5);
                }
            }
        }
        finally
        {
            IsWorking = false;
            ActiveItem = null;
        }
    }

    private static bool IsLookupFailure(Exception error)
    {
        return error is WikiTransportException or JsonException or IOException or InvalidOperationException or
            OperationCanceledException;
    }
}
