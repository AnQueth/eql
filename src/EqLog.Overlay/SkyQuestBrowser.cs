using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private const string AllSkyClasses = "All classes";
    private const int SkyViewByClass = 0;
    private const int SkyViewByDropper = 1;
    private const int SkyViewWatching = 2;
    private const int SkyViewNeed = 3;

    private static readonly Brush SkyHave = FrozenBrush(Color.FromRgb(120, 230, 120));
    private static readonly Brush SkyNeed = FrozenBrush(Color.FromRgb(255, 170, 60));
    private static readonly Brush SkyDim = FrozenBrush(Color.FromRgb(170, 170, 180));

    private SkyQuestCatalog? _skyCatalog;
    private bool _skyLoading;
    private WikiPriority _skyLoadPriority;
    private bool _skyForceRefresh;
    private bool _skyOverlayLoadAttempted;
    private string _skyStatus = "";
    private ComboBox? _skyOverlayClass;
    private OverlayContent? _skyContent;
    private DateTime _skyNextRender;

    private static string SkyCachePath
    {
        get
        {
            return Path.Combine(AppSettings.Folder, "sky-quests.json");
        }
    }

    private async Task LoadSkyQuestsAsync(bool refresh, WikiPriority priority = WikiPriority.Interactive)
    {
        if (_skyLoading)
        {
            if (priority < _skyLoadPriority)
            {
                _skyLoadPriority = priority;
            }
            _skyForceRefresh |= refresh;
            _wikiRepository.PromoteSky(priority);
            return;
        }

        _skyLoading = true;
        _skyLoadPriority = priority;
        _skyForceRefresh = refresh;
        _skyStatus = "Loading Plane of Sky class quests...";
        _skyNextRender = default;
        try
        {
            if (_skyCatalog is null && await Task.Run(() => File.Exists(SkyCachePath)))
            {
                try
                {
                    _skyCatalog = JsonSerializer.Deserialize<SkyQuestCatalog>(await File.ReadAllTextAsync(SkyCachePath, _wikiCts.Token))
                        ?? throw new JsonException("Empty Sky quest cache.");
                    if (_skyCatalog.Quests.Count == 0)
                    {
                        throw new JsonException("Sky cache contains no quests.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _skyStatus = $"Sky cache could not be read: {ex.Message}. Fetching from EQLWiki...";
                }
            }

            if (_skyForceRefresh || _skyCatalog is null || _skyCatalog.DropSources is null ||
                _skyCatalog.Quests.Any(q => q.Requirements is null || q.RuneLinks is null || q.ItemLinks is null) ||
                DateTime.UtcNow - _skyCatalog.CheckedUtc >= TimeSpan.FromDays(1))
            {
                if (_skyCatalog is not null)
                {
                    _wikiRepository.SeedSky(_skyCatalog);
                }
                _skyCatalog = await _wikiRepository.GetSkyAsync(_skyLoadPriority, _skyForceRefresh, _wikiCts.Token);
                await AtomicFile.WriteJsonAsync(SkyCachePath, _skyCatalog, ct: _wikiCts.Token);
            }
            _skyStatus = "";
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            HttpRequestException or OperationCanceledException or InvalidOperationException or FormatException)
        {
            _skyStatus = $"Sky quest load failed: {ex.Message}" +
                (_skyCatalog is null ? " Click Refresh to retry." : " Showing previously loaded data.");
        }
        finally
        {
            if (_skyCatalog is not null && _skyOverlayClass is not null)
            {
                string selected = _settings.SkyClass;
                _skyOverlayClass.ItemsSource = new[] { AllSkyClasses }
                    .Concat(_skyCatalog.Quests.Select(q => q.Class).Distinct().Order()).ToArray();
                _skyOverlayClass.SelectedItem = _skyOverlayClass.Items.Contains(selected) ? selected : AllSkyClasses;
            }
            _skyLoading = false;
            _skyNextRender = default;
        }
    }

    private void ConfigureSkyToolbar(OverlayPanel panel)
    {
        var bar = new StackPanel();
        var searchRow = new DockPanel { Margin = new Thickness(0, 5, 0, 4) };
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Reload quests and drop sources from EQLWiki" };
        refresh.Click += async (_, _) => await LoadSkyQuestsAsync(true);
        var wiki = new Button { Content = "Wiki", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Open the Plane of Sky page on EQLWiki" };
        wiki.Click += (_, _) => OpenWiki(SkyQuests.SourceUrl);
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(wiki, Dock.Right);
        searchRow.Children.Add(refresh);
        searchRow.Children.Add(wiki);
        var search = new TextBox
        {
            Text = _settings.SkyFilter,
            MinWidth = 100,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Search quests, rewards, items, and droppers. Unlock floating panels to type.",
        };
        search.TextChanged += (_, _) =>
        {
            _settings.SkyFilter = search.Text;
            _skyNextRender = default;
        };
        searchRow.Children.Add(search);
        bar.Children.Add(searchRow);

        _skyOverlayClass = new ComboBox
        {
            ItemsSource = new[] { AllSkyClasses, _settings.SkyClass }.Distinct().ToArray(),
            SelectedItem = _settings.SkyClass,
            ToolTip = "Class filter for By class and By dropper. Watching shows every watched quest.",
        };
        _skyOverlayClass.SelectionChanged += (_, _) =>
        {
            if (_skyOverlayClass.SelectedItem is string selected)
            {
                _settings.SkyClass = selected;
                _skyNextRender = default;
            }
        };
        bar.Children.Add(_skyOverlayClass);

        var views = new TabControl
        {
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 4),
        };
        views.Items.Add(new TabItem { Header = "By class", Tag = SkyViewByClass });
        views.Items.Add(new TabItem { Header = "By dropper", Tag = SkyViewByDropper });
        views.Items.Add(new TabItem
        {
            Header = "Need",
            Tag = SkyViewNeed,
            ToolTip = "Only items you still need for the filtered quests, grouped by boss",
        });
        views.Items.Add(new TabItem { Header = "Watching", Tag = SkyViewWatching });
        _settings.SkyView = Math.Clamp(_settings.SkyView, SkyViewByClass, SkyViewNeed);
        views.SelectedItem = views.Items.Cast<TabItem>().First(t => (int)t.Tag == _settings.SkyView);
        views.SelectionChanged += (_, e) =>
        {
            if (e.Source == views && views.SelectedItem is TabItem { Tag: int view })
            {
                _settings.SkyView = view;
                _skyNextRender = default;
            }
        };
        bar.Children.Add(views);
        panel.SetToolbar(bar);
        panel.WatchToggleRequested += ToggleSkyWatch;
    }

    private void ToggleSkyWatch(string key)
    {
        int index = _settings.SkyWatched.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _settings.SkyWatched.RemoveAt(index);
        }
        else
        {
            _settings.SkyWatched.Add(key);
        }

        SaveSettingsInBackground(error => _skyStatus = $"Could not save watched quests: {error}");
        _skyNextRender = default;
    }

    private OverlayContent SkyOverlayContent()
    {
        if (_skyCatalog is null && !_skyLoading && !_skyOverlayLoadAttempted)
        {
            _skyOverlayLoadAttempted = true;
            _ = LoadSkyQuestsAsync(false, WikiPriority.Background);
        }

        if (_skyContent is null || DateTime.Now >= _skyNextRender)
        {
            _skyContent = BuildSkyOverlayContent();
            _skyNextRender = DateTime.Now.AddSeconds(1);
        }
        return _skyContent;
    }

    private OverlayContent BuildSkyOverlayContent()
    {
        var rows = new List<OverlayRow>();
        if (_skyLoading)
        {
            rows.Add(new("Refreshing wiki data...", Wrap: true, Foreground: SkyDim));
        }
        else if (_skyStatus.Length > 0)
        {
            rows.Add(new(_skyStatus, Wrap: true, Foreground: SkyNeed));
        }

        if (_skyCatalog is not { } catalog)
        {
            if (rows.Count == 0)
            {
                rows.Add(new("Loading Plane of Sky quests...", Wrap: true));
            }
            return new("Sky quest tracker", null, rows);
        }

        string character = _characterName ?? "";
        string filter = _settings.SkyFilter;
        var watched = _settings.SkyWatched.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classQuests = catalog.Quests
            .Where(q => _settings.SkyClass == AllSkyClasses || q.Class.Equals(_settings.SkyClass, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string subtitle;
        switch (_settings.SkyView)
        {
            case SkyViewByDropper:
                subtitle = AddSkyDropperRows(rows, catalog, classQuests, filter, character, watched);
                break;
            case SkyViewWatching:
                subtitle = AddSkyWatchRows(rows, catalog, filter, character, watched);
                break;
            case SkyViewNeed:
                subtitle = AddSkyNeedRows(rows, catalog, classQuests, filter, character, watched);
                break;
            default:
                subtitle = AddSkyClassRows(rows, catalog, classQuests, filter, character, watched);
                break;
        }

        if (character.Length > 0)
        {
            subtitle += $" · {character}";
        }
        return new("Sky quest tracker", subtitle, rows);
    }

    private string AddSkyClassRows(List<OverlayRow> rows, SkyQuestCatalog catalog, IReadOnlyList<SkyQuest> classQuests,
        string filter, string character, ISet<string> watched)
    {
        var quests = classQuests.Where(q => SkyQuests.Matches(q, filter, catalog)).ToArray();
        var progress = SkyQuests.ProgressAll(quests, _questInventory, character);
        foreach (var quest in quests)
        {
            AddSkyQuestRows(rows, catalog, quest, progress[quest], watched);
        }

        if (quests.Length == 0)
        {
            rows.Add(new("No quests match the search.", Wrap: true, Foreground: SkyDim));
        }
        return $"{_settings.SkyClass} · {quests.Length} quests";
    }

    private void AddSkyQuestRows(List<OverlayRow> rows, SkyQuestCatalog catalog, SkyQuest quest,
        IReadOnlyList<SkyRequirementProgress> progress, ISet<string> watched)
    {
        string key = SkyQuests.Key(quest);
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
            (quest.Reward, QuestLookup.ItemUrl(quest.Reward)),
            (quest.Giver, QuestLookup.SearchUrl(quest.Giver)),
        ])));
        foreach (var item in progress)
        {
            rows.Add(new(SkyQuests.Label(quest, item.Item), $"{(item.Satisfied ? "HAVE" : "NEED")} {item.Owned} / {item.Required}",
                Wrap: true, LootItem: item.Item, Foreground: item.Satisfied ? SkyHave : SkyNeed,
                Table: SkyItemTable(catalog, item)));
        }
    }

    private static OverlayTable SkyItemTable(SkyQuestCatalog catalog, SkyRequirementProgress item)
    {
        var droppedBy = catalog.DroppedBy(item.Item);
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
        return OverlayTable.Pairs(pairs).WithLinks(droppedBy.Select(mob => (mob, QuestLookup.SearchUrl(mob))));
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
            string name = (watched.Contains(SkyQuests.Key(quest)) ? "\u2605 " : "") + quest.Name +
                (duplicated.Contains(quest.Name) ? $" ({quest.Class})" : "");
            names.Add(name);
            links.TryAdd(name, quest.Url);
        }
        return OverlayTable.Pairs(OverlayTable.Labeled("Needed for", names)) with { Links = links };
    }

    private string AddSkyDropperRows(List<OverlayRow> rows, SkyQuestCatalog catalog, IReadOnlyList<SkyQuest> classQuests,
        string filter, string character, ISet<string> watched)
    {
        var groups = SkyQuests.ByDropper(classQuests, catalog, filter);
        var progress = SkyQuests.ProgressAll(classQuests, _questInventory, character);
        foreach (var group in groups)
        {
            bool known = group.Mob.Length > 0;
            rows.Add(new(known ? group.Mob : "No specific mob listed", $"{group.Items.Count} item{(group.Items.Count == 1 ? "" : "s")}",
                Bold: true, QuestHeading: true, Wrap: true, Link: known ? QuestLookup.SearchUrl(group.Mob) : null,
                ToolTip: known ? $"Open {group.Mob} on EQLWiki" : "EQLWiki lists only the zone for these items (typically runes)."));
            foreach (var item in group.Items)
            {
                int owned = progress[item.Quests[0]].First(p => ItemNames.Comparer.Equals(p.Item, item.Item)).Owned;
                rows.Add(new(item.Label, $"Have {owned}", Wrap: true, LootItem: item.Item,
                    Foreground: owned > 0 ? SkyHave : null,
                    Table: SkyNeededFor(item.Quests, watched)));
            }
        }

        if (groups.Count == 0)
        {
            rows.Add(new("No items match the search.", Wrap: true, Foreground: SkyDim));
        }
        return $"{_settings.SkyClass} · {groups.Count(g => g.Mob.Length > 0)} droppers";
    }

    private string AddSkyWatchRows(List<OverlayRow> rows, SkyQuestCatalog catalog, string filter, string character, ISet<string> watched)
    {
        var quests = catalog.Quests.Where(q => watched.Contains(SkyQuests.Key(q))).ToArray();
        if (quests.Length == 0)
        {
            rows.Add(new("No watched quests yet. Click the \u2606 next to a quest in By class to watch it; quests from any class can be watched together.",
                Wrap: true, Foreground: SkyDim));
            return "Watching 0 quests";
        }

        var progress = SkyQuests.ProgressAll(quests, _questInventory, character);
        var shown = quests.Where(q => SkyQuests.Matches(q, filter, catalog)).ToArray();

        var missing = new List<OverlayRow>();
        AddSkyNeededRows(missing, SkyQuests.ByDropper(shown, catalog), progress, watched, bossHeadings: false);
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

    private string AddSkyNeedRows(List<OverlayRow> rows, SkyQuestCatalog catalog, IReadOnlyList<SkyQuest> classQuests,
        string filter, string character, ISet<string> watched)
    {
        var progress = SkyQuests.ProgressAll(classQuests, _questInventory, character);
        var (bosses, items) = AddSkyNeededRows(rows, SkyQuests.ByDropper(classQuests, catalog, filter), progress, watched, bossHeadings: true);
        if (items == 0)
        {
            rows.Add(new(string.IsNullOrWhiteSpace(filter) ? "Nothing needed - every listed item is owned." : "No needed items match the search.",
                Wrap: true, Foreground: string.IsNullOrWhiteSpace(filter) ? SkyHave : SkyDim));
        }
        return $"{_settings.SkyClass} · need {items} item{(items == 1 ? "" : "s")} from {bosses} boss{(bosses == 1 ? "" : "es")}";
    }

    /// <summary>
    /// Adds boss rows followed by the items still missing from that boss. Items are not reserved per quest,
    /// so the shortfall compares the combined requirement against what is owned.
    /// </summary>
    private static (int Bosses, int Items) AddSkyNeededRows(List<OverlayRow> rows, IReadOnlyList<SkyDropGroup> groups,
        IReadOnlyDictionary<SkyQuest, IReadOnlyList<SkyRequirementProgress>> progress, ISet<string> watched, bool bossHeadings)
    {
        int bosses = 0;
        int items = 0;
        foreach (var group in groups)
        {
            var needed = new List<OverlayRow>();
            foreach (var item in group.Items)
            {
                var entries = item.Quests.Select(q => progress[q].First(p => ItemNames.Comparer.Equals(p.Item, item.Item))).ToArray();
                int required = entries.Sum(p => p.Required);
                int owned = entries[0].Owned;
                if (owned < required)
                {
                    needed.Add(new(item.Label, owned > 0 ? $"need {required - owned} (have {owned})" : $"need {required}",
                        Wrap: true, LootItem: item.Item, Foreground: SkyNeed,
                        Table: SkyNeededFor(item.Quests, watched)));
                }
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
                Bold: true, QuestHeading: bossHeadings, Wrap: true, Link: known ? QuestLookup.SearchUrl(group.Mob) : null,
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
