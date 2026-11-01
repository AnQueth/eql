using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private readonly Dictionary<string, ItemEffectInfo> _itemEffects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _itemEffectErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _itemEffectRetries = new(StringComparer.OrdinalIgnoreCase);
    private bool _itemEffectsWorking;
    private string? _itemEffectsActive;
    private string? _itemEffectsStorageError;
    private TextBox? _exaltationSearch;
    private CheckBox? _exaltationsInstalledOnly;
    private Button? _refreshExaltations;
    private string _exaltationFilter = "";
    private IReadOnlyList<ExaltationLocation> _exaltationLocations = [];
    private AsyncTextFilter<OverlayContent>? _exaltationFilterWork;
    private long _itemEffectsRevision;

    private void InitializeExaltations(StartupState state)
    {
        foreach (var result in state.ItemEffects)
        {
            _itemEffects[result.Item] = result;
            _itemEffectsRevision++;
            _wikiRepository.SeedItemEffects(result);
        }
        if (state.ItemEffectsError is { } error)
        {
            _itemEffectsStorageError = $"Could not load effect cache: {error.Message}. Session-only lookups; existing cache kept.";
        }
    }

    private void ConfigureExaltations(OverlayPanel panel)
    {
        _exaltationSearch = new TextBox
        {
            ToolTip = "Search what it does (string, bard instruments, haste), an exaltation, host item, or location. All words must match.",
            Margin = new Thickness(0, 0, 0, 6),
        };
        _exaltationSearch.TextChanged += (_, _) =>
        {
            _exaltationFilter = _exaltationSearch.Text;
        };
        _exaltationsInstalledOnly = new CheckBox
        {
            Content = "Installed only",
            ToolTip = "Only installations confirmed by the export. Hides exalt storage, loose copies, and unverified wiki defaults.",
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _refreshExaltations = new Button
        {
            Content = "Refresh effects",
            ToolTip = "Re-fetch base-item effects and descriptions. Does not refresh inventory; use /outputfile inventory in game.",
        };
        UsageTracking.SetAction(_refreshExaltations, UsageAction.RefreshExaltationEffects);
        _refreshExaltations.Click += async (_, _) =>
        {
            await LookupExaltationEffectsAsync(force: true);
        };
        var hint = new TextBlock
        {
            Text = "Find an exaltation by what it does. Each card names the exaltation, its effects, " +
                "and the item carrying it. Locations come from your last /outputfile inventory.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        hint.ToolTip = "Only base-item/effect names are sent to EQLWiki; host names and locations stay local.";
        hint.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        var exportLimitation = new TextBlock
        {
            Text = "Export limitation: exaltations installed in items in Equipment storage may be omitted by " +
                "/outputfile inventory. This list and search can be incomplete even after re-exporting. " +
                "Wiki-default matches show expected effects, not confirmed ownership or installation. " +
                "No match does not mean you do not own it. Check the in-game Find Item window.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        exportLimitation.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        panel.SetToolbar(new StackPanel
        {
            Margin = new Thickness(0, 5, 0, 5),
            Children =
            {
                hint,
                exportLimitation,
                new TextBlock { Text = "Find effects, items, or locations:", Margin = new Thickness(0, 0, 0, 4) },
                _exaltationSearch,
                new WrapPanel { Children = { _exaltationsInstalledOnly, _refreshExaltations } },
            },
        });
    }

    private IReadOnlyList<ExaltationLocation> CurrentExaltations()
    {
        if (_inventoryRowsCharacter is null ||
            !_inventoryRowsCharacter.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        return _exaltationLocations;
    }

    private OverlayContent ExaltationsContent()
    {
        var all = CurrentExaltations();
        if (!_itemEffectsWorking && all.Any(location =>
            (!_itemEffects.TryGetValue(location.BaseItem, out var cached) || WikiQueries.NeedsRefresh(cached)) &&
            _itemEffectRetries.GetValueOrDefault(location.BaseItem) <= DateTime.UtcNow))
        {
            _ = LookupExaltationEffectsAsync(force: false);
        }
        _exaltationFilterWork ??= new(Dispatcher, error => _itemEffectsStorageError = $"Effect filtering failed: {error.Message}");
        string? character = _characterName;
        bool current = _inventoryRowsCharacter == character && _inventoryRows.Count > 0;
        bool installedOnly = _exaltationsInstalledOnly?.IsChecked == true;
        string filter = _exaltationFilter;
        string? activeItem = _itemEffectsActive;
        string? storageError = _itemEffectsStorageError;
        var exported = _inventoryLoaded?.Written;
        var key = (all, character, current, installedOnly, filter, activeItem, storageError, exported,
            _itemEffectsRevision, _session.Revision);
        if (!_exaltationFilterWork.Matches(key))
        {
            var effects = new Dictionary<string, ItemEffectInfo>(_itemEffects, StringComparer.OrdinalIgnoreCase);
            var errors = new Dictionary<string, string>(_itemEffectErrors, StringComparer.OrdinalIgnoreCase);
            _exaltationFilterWork.Request(key, token => BuildExaltationsContent(all, effects, errors, character,
                current, installedOnly, filter, activeItem, storageError, exported, token),
                context: (character, _session.Revision));
        }
        return _exaltationFilterWork.Result ?? new("Exaltations", character, [new("Filtering exaltations...", Wrap: true)]);
    }

    private static OverlayContent BuildExaltationsContent(IReadOnlyList<ExaltationLocation> all,
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
            Table: OverlayTable.Pairs(fields).WithLinks(links), Details: warning);
    }

    private async Task LookupExaltationEffectsAsync(bool force)
    {
        if (_itemEffectsWorking || WikiLookupPaused)
        {
            return;
        }
        using var lookup = BeginWikiLookup();
        var token = lookup.Token;
        _itemEffectsWorking = true;
        if (_refreshExaltations is not null)
        {
            _refreshExaltations.IsEnabled = false;
        }
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
                _itemEffectsActive = item;
                try
                {
                    var result = await _wikiRepository.GetItemEffectsAsync(item,
                        force ? WikiPriority.Interactive : WikiPriority.Background, force, token);
                    token.ThrowIfCancellationRequested();
                    _itemEffects[item] = result;
                    _itemEffectsRevision++;
                    _itemEffectErrors.Remove(item);
                    _itemEffectRetries.Remove(item);
                    _itemEffectsStorageError = _wikiRepository.GetCacheError("item-effects:" + item) is { } cacheError
                        ? $"Could not save effects: {cacheError}. Results kept for this session." : null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is WikiTransportException or JsonException or IOException or
                    InvalidOperationException or OperationCanceledException)
                {
                    UsageTelemetry.TrackError(ex, UsageErrorSource.Wiki);
                    _itemEffectErrors[item] = ex.Message;
                    _itemEffectsRevision++;
                    _itemEffectRetries[item] = DateTime.UtcNow.AddMinutes(5);
                }
            }
        }
        finally
        {
            _itemEffectsWorking = false;
            _itemEffectsActive = null;
            if (_refreshExaltations is not null)
            {
                _refreshExaltations.IsEnabled = true;
            }
        }
    }
}
