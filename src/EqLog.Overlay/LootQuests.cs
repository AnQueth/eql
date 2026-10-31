using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core;
using EqLog.Core.Parsing;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private readonly HttpClient _wikiHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, QuestLookupResult> _questCache = new(StringComparer.OrdinalIgnoreCase);
    private long _questSelectionVersion;
    private readonly CancellationTokenSource _wikiCts = new();
    private static string QuestCachePath
    {
        get
        {
            return Path.Combine(AppSettings.Folder, "quest-lookups.jsonl");
        }
    }

    private static string LegacyQuestCachePath
    {
        get
        {
            return Path.Combine(AppSettings.Folder, "quest-lookups.json");
        }
    }

    private QuestInventory _questInventory = new();
    private static string QuestInventoryPath
    {
        get
        {
            return Path.Combine(AppSettings.Folder, "quest-inventory.json");
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _questLoot = new();
    private readonly SemaphoreSlim _questSaveGate = new(1, 1);
    private readonly HashSet<string> _questAttempted = new(StringComparer.OrdinalIgnoreCase);
    private int _questCacheCursor;
    private bool _questWorking;
    private bool _questDirty;
    private string _questProgress = "Waiting for loot.";
    private string? _questActiveItem;
    private readonly HashSet<string> _questFailed = new(StringComparer.OrdinalIgnoreCase);
    private ProgressBar? _questProgressBar;
    private ContentControl? _questProgressText;
    private OverlayTable? _questProgressTable;
    private TextBlock? _questCurrentText;
    private readonly System.Collections.ObjectModel.ObservableCollection<QuestInventoryItem> _ownedQuestItems = [];

    private void InitializeQuestInventory(StartupState state)
    {
        if (state.QuestInventory is { } inventory)
        {
            _questInventory = inventory;
        }
        if (state.QuestInventoryError is { } error)
        {
            QuestLookupStatus.Text = $"Could not load quest inventory: {error.Message}";
        }

        QuestOwnedGrid.ItemsSource = _ownedQuestItems;
    }

    private void PumpQuestLoot()
    {
        bool changed = !_questLoot.IsEmpty;
        var budget = Stopwatch.StartNew();
        while (budget.ElapsedMilliseconds < 5 && _questLoot.TryDequeue(out var note))
        {
            note();
            _questDirty = true;
        }

        var items = _questInventory.Items.Where(i => i.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!_ownedQuestItems.SequenceEqual(items))
        {
            _ownedQuestItems.Clear();
            foreach (var item in items)
            {
                _ownedQuestItems.Add(item);
            }
        }
        else if (changed && !QuestOwnedGrid.IsKeyboardFocusWithin)
        {
            QuestOwnedGrid.Items.Refresh();
        }

        var cacheBudget = Stopwatch.StartNew();
        int cacheHits = 0;
        // Continue across frames so large inventories cannot leave later rows' markers stale.
        for (int checkedItems = 0; checkedItems < items.Length; checkedItems++)
        {
            if (_questCacheCursor >= items.Length)
            {
                _questCacheCursor = 0;
            }
            var item = items[_questCacheCursor++];
            string title = GetQuestItemTitle(item.Item);
            _questCache.TryGetValue(title, out var cached);
            item.HasQuestReferences = cached?.HasQuestReferences == true;
            if (!_questAttempted.Contains(title) && cached is not null && !cached.NeedsRefresh)
            {
                _questAttempted.Add(title);
                _questFailed.Remove(title);
                cacheHits++;
            }
            if (cacheBudget.ElapsedMilliseconds >= 5)
            {
                break;
            }
        }
        if (cacheHits > 0 && !_questWorking)
        {
            _questProgress = $"Used cached wiki references for {cacheHits:N0} items (no HTTP needed).";
        }

        if (!_questWorking)
        {
            var next = items.FirstOrDefault(i =>
            {
                string title = GetQuestItemTitle(i.Item);
                return !_questAttempted.Contains(title) &&
                    (!_questCache.TryGetValue(title, out var cached) || cached.NeedsRefresh);
            });
            if (next is not null)
            {
                _questAttempted.Add(GetQuestItemTitle(next.Item));
                _ = AutoLookupQuestAsync(next.Item);
            }
            UpdateQuestProgress();
        }
    }

    private async Task<QuestLookupResult> GetQuestResultAsync(string item, WikiPriority priority = WikiPriority.Interactive)
    {
        item = QuestLookup.ItemTitle(item);
        var result = await _wikiRepository.GetQuestAsync(item, priority, false, _wikiCts.Token);
        await _questSaveGate.WaitAsync(_wikiCts.Token);
        try
        {
            var previous = _questCache.GetValueOrDefault(item);
            if (previous == result)
            {
                return result;
            }
            await QuestLookupStore.AppendAsync(QuestCachePath, result, _wikiCts.Token);
            _questCache[item] = result;
            if ((previous?.HasQuestReferences == true) != result.HasQuestReferences)
            {
                RefreshMergeView();
            }
            return result;
        }
        finally
        {
            _questSaveGate.Release();
        }
    }

    private bool HasQuestReferences(string item)
    {
        return _questCache.TryGetValue(GetQuestItemTitle(item), out var result) && result.HasQuestReferences;
    }

    private string GetQuestItemTitle(string item)
    {
        if (!_questTitles.TryGetValue(item, out string? title))
        {
            title = QuestLookup.ItemTitle(item);
            _questTitles[item] = title;
        }
        return title;
    }

    private async Task AutoLookupQuestAsync(string item)
    {
        _questWorking = true;
        _questActiveItem = QuestLookup.ItemTitle(item);
        _questProgress = $"Looking up {item}...";
        try
        {
            var result = await GetQuestResultAsync(item, WikiPriority.Background);
            _questFailed.Remove(QuestLookup.ItemTitle(item));
            _questActiveItem = null;
            _questProgress = result.Found ? $"Wiki references checked: {item}."
                : $"Wiki item page not found: {item}. Cached for 24 hours.";
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
            UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Wiki);
            _questFailed.Add(QuestLookup.ItemTitle(item));
            _questProgress = $"Lookup failed for {item}: {ex.Message}. Use Retry lookups.";
            QuestLookupStatus.Text = _questProgress;
        }
        finally
        {
            _questWorking = false;
            _questActiveItem = null;
            UpdateQuestProgress();
        }
    }

    private OverlayContent QuestOpportunities()
    {
        var rows = new List<OverlayRow>();
        var quests = _questInventory.Opportunities(_characterName ?? "", _questCache, _settings.QuestFilter, _settings.QuestRecentFirst);
        foreach (var quest in quests)
        {
            rows.Add(new(quest.Title, Bold: true, QuestHeading: true, Wrap: true,
                Link: QuestLookup.PageUrl(quest.Title), ToolTip: $"Open {quest.Title} on EQLWiki"));
            var items = _settings.QuestRecentFirst
                ? quest.Items.OrderByDescending(i => i.LastLootUtc).ThenBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                : quest.Items.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                rows.Add(new(item.Item, Wrap: true, LootItem: item.Item,
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
            rows.Add(new(string.IsNullOrWhiteSpace(_settings.QuestFilter)
            ? "No quest opportunities found yet." : "No quests or items match your filter.", Wrap: true));
        }

        return new("Quest opportunities", _characterName, rows);
    }

    private void ConfigureQuestToolbar(OverlayPanel panel)
    {
        var toolbar = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 5, 0, 5) };
        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sort = new System.Windows.Controls.ComboBox
        {
            ItemsSource = new[] { "Recent items", "Quest name (A-Z)" },
            SelectedIndex = _settings.QuestRecentFirst ? 0 : 1,
            Width = 155,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Sort quests by latest looted item (inventory-only items last), or alphabetically.",
        };
        sort.SelectionChanged += (_, _) => _settings.QuestRecentFirst = sort.SelectedIndex == 0;
        var search = new System.Windows.Controls.TextBox
        {
            Text = _settings.QuestFilter,
            ToolTip = "Filter by item or quest name (case-insensitive). Clear to show all.",
        };
        search.TextChanged += (_, _) => _settings.QuestFilter = search.Text;
        controls.Children.Add(sort);
        Grid.SetColumn(search, 1);
        controls.Children.Add(search);
        toolbar.Children.Add(controls);
        _questProgressBar = new ProgressBar
        {
            Height = 8,
            Minimum = 0,
            Maximum = 1,
            Margin = new Thickness(0, 10, 0, 5),
            ToolTip = "Completed checks, including failed attempts. Failed items can be retried from the Inventory & loot quests overlay.",
        };
        toolbar.Children.Add(_questProgressBar);
        _questProgressText = new ContentControl();
        toolbar.Children.Add(_questProgressText);
        _questCurrentText = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 6),
        };
        _questCurrentText.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        toolbar.Children.Add(_questCurrentText);
        var caveat = new TextBlock
        {
            Text = "Matches are wiki mentions, not a ready-to-turn-in checklist.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            ToolTip = "An item may be an ingredient, a reward, or a walkthrough mention. " +
                "This list does not analyze required quantities, other ingredients, faction, or prerequisites. " +
                "Use the dedicated Sky tracker for parsed Plane of Sky ingredient counts.",
        };
        caveat.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        toolbar.Children.Add(caveat);
        panel.SetToolbar(toolbar);
    }

    private void UpdateQuestProgress()
    {
        _questWikiActivity = null;
        if (_questProgressBar is null || _questProgressText is null || _questCurrentText is null)
        {
            return;
        }

        var activity = GetQuestWorkProgress();
        _questProgressBar.Maximum = Math.Max(1, activity.Total);
        _questProgressBar.Value = activity.Completed;
        // Always show the same table shape so the quest list below never shifts as work starts.
        var table = OverlayTable.Columns(
        [
            ("Checked", $"{activity.Completed:N0} / {activity.Total:N0}"),
            ("Done", activity.Total == 0 ? "-" : $"{100.0 * activity.Completed / activity.Total:0}%"),
            ("Cached", $"{activity.Cached:N0}"),
            ("Queued", $"{activity.QueuedCount:N0}"),
            ("Failed", $"{activity.Failed:N0}"),
        ]);
        if (table != _questProgressTable)
        {
            var element = OverlayPanel.BuildTable(table, 12);
            element.Margin = new Thickness(0);
            _questProgressText.Content = element;
            _questProgressTable = table;
        }
        string current = activity.Total == 0 && string.IsNullOrEmpty(_questProgress)
            ? activity.Summary
            : _questProgress;
        _questCurrentText.Text = current;
        _questCurrentText.ToolTip = string.IsNullOrEmpty(current) ? null : current;
    }

    private QuestWorkProgress GetQuestWorkProgress()
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _questInventory.Items)
        {
            if (!entry.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            titles.Add(GetQuestItemTitle(entry.Item));
        }
        var items = titles.ToArray();
        bool Cached(string item)
        {
            return _questCache.TryGetValue(item, out var result) &&
            !result.NeedsRefresh;
        }

        int cached = items.Count(Cached);
        int failed = items.Count(i => _questFailed.Contains(i) && !Cached(i));
        int completed = items.Count(i => Cached(i) || (_questAttempted.Contains(i) && i != _questActiveItem));
        int active = items.Count(i => i == _questActiveItem && !Cached(i));
        int queued = Math.Max(0, items.Length - completed - active);
        string summary = items.Length == 0 ? "Waiting for inventory or loot" :
            $"{completed:N0} / {items.Length:N0} checked ({100.0 * completed / items.Length:0}%) · {cached:N0} cached · {queued:N0} queued · {failed:N0} failed";
        var waiting = items.Where(i => !Cached(i) && !_questAttempted.Contains(i)).ToArray();
        return new(items.Length, completed, summary, waiting, cached, queued, failed);
    }

    private QuestWikiActivity GetQuestWikiActivity()
    {
        // The wiki panel refreshes every second; rescanning thousands of inventory rows each time is wasted work,
        // so reuse the last result briefly. UpdateQuestProgress still computes fresh numbers on every change.
        if (_questWikiActivity is null || DateTime.UtcNow - _questWikiActivityAt > QuestWikiActivityLifetime)
        {
            var progress = GetQuestWorkProgress();
            _questWikiActivity = new(progress.Summary, _questProgress, _questActiveItem, progress.Queued,
                _questProgress.StartsWith("Lookup failed", StringComparison.Ordinal));
            _questWikiActivityAt = DateTime.UtcNow;
        }
        return _questWikiActivity;
    }

    private static readonly TimeSpan QuestWikiActivityLifetime = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, string> _questTitles = new(StringComparer.Ordinal);
    private QuestWikiActivity? _questWikiActivity;
    private DateTime _questWikiActivityAt;

    private sealed record QuestWorkProgress(int Total, int Completed, string Summary, IReadOnlyList<string> Queued,
        int Cached = 0, int QueuedCount = 0, int Failed = 0);

    private readonly SemaphoreSlim _questInventorySaveGate = new(1, 1);
    private long _questInventorySaveSequence;

    private async Task SaveQuestInventoryAsync()
    {
        try
        {
            await StartQuestInventorySave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _questDirty = true;
            QuestLookupStatus.Text = $"Could not save quest inventory: {ex.Message}";
        }
    }

    /// <summary>
    /// Applies queued loot in short slices, then snapshots the inventory for an asynchronous write.
    /// The caller marks the inventory dirty again if the write fails.
    /// </summary>
    private async Task StartQuestInventorySave()
    {
        await DrainQuestLootAsync();
        if (!_questDirty)
        {
            return;
        }

        var snapshot = _questInventory.Clone();
        _questDirty = false;
        await WriteQuestInventoryAsync(snapshot, Interlocked.Increment(ref _questInventorySaveSequence)).ConfigureAwait(false);
    }

    private async Task DrainQuestLootAsync()
    {
        var budget = Stopwatch.StartNew();
        while (_questLoot.TryDequeue(out var note))
        {
            note();
            _questDirty = true;
            // Session-ending saves run on a worker while the dispatcher is parked.
            if (budget.ElapsedMilliseconds >= 5 && Dispatcher.CheckAccess())
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                budget.Restart();
            }
        }
    }

    private async Task WriteQuestInventoryAsync(QuestInventory snapshot, long sequence)
    {
        await _questInventorySaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A newer snapshot is already queued behind this one; writing this would only be overwritten.
            if (sequence == Interlocked.Read(ref _questInventorySaveSequence))
            {
                await AtomicFile.WriteJsonAsync(QuestInventoryPath, snapshot).ConfigureAwait(false);
            }
        }
        finally
        {
            _questInventorySaveGate.Release();
        }
    }

    private void QuestOwned_Edit(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == System.Windows.Controls.DataGridEditAction.Commit &&
            e.EditingElement is System.Windows.Controls.TextBox box &&
            (!int.TryParse(box.Text, out int count) || count < 0))
        {
            e.Cancel = true;
            QuestLookupStatus.Text = "Owned count must be a whole number, zero or greater.";
            return;
        }
        _questDirty = true;
        Dispatcher.BeginInvoke(async () =>
        {
            QuestOwnedGrid.CommitEdit();
            await SaveQuestInventoryAsync();
        });
    }

    private async void CompleteQuest_Click(object sender, RoutedEventArgs e)
    {
        if (QuestResults.SelectedItem is not QuestReference quest)
        {
            QuestLookupStatus.Text = "Select a quest reference first.";
            return;
        }
        string key = QuestInventory.CompletionKey(_characterName ?? "", quest.Title);
        bool completed = _questInventory.Completed.Add(key);
        if (!completed)
        {
            _questInventory.Completed.Remove(key);
        }

        _questDirty = true;
        QuestLookupStatus.Text = completed ? "Quest marked completed. Adjust owned counts after turning items in." : "Quest marked incomplete.";
        await SaveQuestInventoryAsync();
    }

    private void RetryQuests_Click(object sender, RoutedEventArgs e)
    {
        _questAttempted.Clear();
        _questFailed.Clear();
        UpdateQuestProgress();
    }

    private void InitializeLootQuests(StartupState state)
    {
        _wikiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("EqLogOverlay/1.0");
        if (state.QuestCacheError is { } error)
        {
            QuestLookupStatus.Text = $"Could not load wiki cache: {error.Message}";
        }
        foreach (var result in state.QuestCache)
        {
            string title = QuestLookup.ItemTitle(result.Item);
            if (!_questCache.TryGetValue(title, out var existing) || result.CheckedUtc > existing.CheckedUtc)
            {
                _questCache[title] = result with
                {
                    Item = title
                };
            }
        }
    }

    private async void QuestItem_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (QuestOwnedGrid.SelectedItem is QuestInventoryItem selected)
        {
            await LookupSelectedQuestItemAsync(selected);
        }
        else
        {
            _questSelectionVersion++;
            QuestResults.ItemsSource = null;
        }
    }

    private async void LookupQuest_Click(object sender, RoutedEventArgs e)
    {
        if (QuestOwnedGrid.SelectedItem is QuestInventoryItem selected)
        {
            await LookupSelectedQuestItemAsync(selected);
        }
        else
        {
            QuestLookupStatus.Text = "Select an inventory row first.";
        }
    }

    private async Task LookupLootItemAsync(string item)
    {
        UsageTelemetry.TrackFeature(UsageFeature.LootQuestLookup);
        ShowInventoryOverlay();
        var selected = _questInventory.Items.FirstOrDefault(i =>
            i.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase) && ItemNames.Comparer.Equals(i.Item, item));
        if (selected is null)
        {
            // Other players' visible loot can be inspected without claiming ownership.
            selected = new QuestInventoryItem { Character = _characterName ?? "", Item = item };
            _questInventory.Items.Add(selected);
            _questDirty = true;
        }
        if (!_ownedQuestItems.Contains(selected))
        {
            _ownedQuestItems.Add(selected);
        }

        if (ReferenceEquals(QuestOwnedGrid.SelectedItem, selected))
        {
            await LookupSelectedQuestItemAsync(selected);
        }
        else
        {
            QuestOwnedGrid.SelectedItem = selected;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            QuestOwnedGrid.UpdateLayout();
            QuestOwnedGrid.ScrollIntoView(selected, QuestOwnedGrid.Columns[0]);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async Task LookupSelectedQuestItemAsync(QuestInventoryItem selected)
    {
        long version = ++_questSelectionVersion;
        string item = selected.Item;
        QuestResults.ItemsSource = null;
        QuestLookupStatus.Text = $"Looking up {item} on EQLWiki...";
        try
        {
            var result = await GetQuestResultAsync(item);
            if (version != _questSelectionVersion)
            {
                return;
            }

            QuestResults.ItemsSource = result.Quests;
            QuestLookupStatus.Text = !result.Found
                ? $"EQLWiki has no item page named '{result.Item}'. Cached for 24 hours; try searching the wiki directly."
                : result.Quests.Count == 0
                ? "No linked quest pages found. This does not prove the item has no quest use; wiki coverage may be incomplete."
                : $"{result.Quests.Count} quest reference(s). Open a page to check requirements versus rewards.";
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested)
        {
            if (version == _questSelectionVersion)
            {
                QuestLookupStatus.Text = "Wiki lookup cancelled.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
            UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Wiki);
            if (version == _questSelectionVersion)
            {
                QuestLookupStatus.Text = $"Wiki lookup failed: {ex.Message}";
            }
        }
    }

    private void OpenQuest_Click(object sender, RoutedEventArgs e)
    {
        if (QuestResults.SelectedItem is QuestReference quest)
        {
            OpenWiki(quest.Url);
        }
        else
        {
            QuestLookupStatus.Text = "Select a quest reference first.";
        }
    }

    private void OpenItemWiki_Click(object sender, RoutedEventArgs e)
    {
        if (QuestOwnedGrid.SelectedItem is QuestInventoryItem selected)
        {
            OpenWiki(QuestLookup.PageUrl(QuestLookup.ItemTitle(selected.Item)));
        }
        else
        {
            QuestLookupStatus.Text = "Select an inventory row first.";
        }
    }

    private void ItemWikiLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Documents.Hyperlink { Tag: string item })
        {
            OpenWiki(QuestLookup.ItemUrl(item));
            e.Handled = true;
        }
    }

    private async void QuestBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Documents.Hyperlink { Tag: string item })
        {
            e.Handled = true;
            await LookupLootItemAsync(item);
        }
    }

    private void NpcWikiLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Documents.Hyperlink { Tag: string npc })
        {
            OpenWiki(QuestLookup.SearchUrl(npc));
            e.Handled = true;
        }
    }

    private void UrlWikiLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Documents.Hyperlink { Tag: string url })
        {
            OpenWiki(url);
            e.Handled = true;
        }
    }

    private void OpenWiki(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            QuestLookupStatus.Text = $"Could not open browser: {ex.Message}";
        }
    }
}
