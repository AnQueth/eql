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
    private static string QuestCachePath => Path.Combine(AppSettings.Folder, "quest-lookups.json");
    private QuestInventory _questInventory = new();
    private static string QuestInventoryPath => Path.Combine(AppSettings.Folder, "quest-inventory.json");
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _questLoot = new();
    private readonly SemaphoreSlim _questLookupGate = new(1, 1);
    private readonly HashSet<string> _questAttempted = new(StringComparer.OrdinalIgnoreCase);
    private bool _questWorking;
    private bool _questDirty;
    private string _questProgress = "Waiting for loot.";
    private string? _questActiveItem;
    private readonly HashSet<string> _questFailed = new(StringComparer.OrdinalIgnoreCase);
    private ProgressBar? _questProgressBar;
    private TextBlock? _questProgressText;
    private TextBlock? _questCurrentText;
    private readonly System.Collections.ObjectModel.ObservableCollection<QuestInventoryItem> _ownedQuestItems = [];

    private void InitializeQuestInventory()
    {
        try
        {
            if (File.Exists(QuestInventoryPath))
            {
                _questInventory = JsonSerializer.Deserialize<QuestInventory>(File.ReadAllText(QuestInventoryPath))
                    ?? throw new JsonException("Empty quest inventory.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            QuestLookupStatus.Text = $"Could not load quest inventory: {ex.Message}";
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
            foreach (var item in items) _ownedQuestItems.Add(item);
        }
        else if (changed && !QuestOwnedGrid.IsKeyboardFocusWithin)
        {
            QuestOwnedGrid.Items.Refresh();
        }

        if (!_questWorking)
        {
            var next = items.FirstOrDefault(i => !_questAttempted.Contains(QuestLookup.ItemTitle(i.Item)));
            if (next is not null)
            {
                _questAttempted.Add(QuestLookup.ItemTitle(next.Item));
                _ = AutoLookupQuestAsync(next.Item);
            }
            UpdateQuestProgress();
        }
    }

    private async Task<QuestLookupResult> GetQuestResultAsync(string item)
    {
        item = QuestLookup.ItemTitle(item);
        await _questLookupGate.WaitAsync(_wikiCts.Token);
        try
        {
            if (_questCache.TryGetValue(item, out var cached) && DateTime.UtcNow - cached.CheckedUtc < TimeSpan.FromDays(7))
                return cached;
            var result = await Task.Run(() => new QuestLookup(_wikiHttp).FindAsync(item, _wikiCts.Token), _wikiCts.Token);
            var snapshot = _questCache.Values.Where(r => !QuestLookup.ItemTitle(r.Item).Equals(item,
                StringComparison.OrdinalIgnoreCase)).Append(result).ToArray();
            await Task.Run(() => AtomicFile.WriteAllText(QuestCachePath, JsonSerializer.Serialize(snapshot)), _wikiCts.Token);
            _questCache[item] = result;
            return result;
        }
        finally
        {
            _questLookupGate.Release();
        }
    }

    private async Task AutoLookupQuestAsync(string item)
    {
        _questWorking = true;
        _questActiveItem = QuestLookup.ItemTitle(item);
        _questProgress = $"Looking up {item}...";
        try
        {
            await GetQuestResultAsync(item);
            _questFailed.Remove(QuestLookup.ItemTitle(item));
            _questActiveItem = null;
            _questProgress = $"Checked {item}. Requirements not verified.";
            await Task.Delay(1000, _wikiCts.Token);
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
            UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _questFailed.Add(QuestLookup.ItemTitle(item));
            _questProgress = $"Lookup failed for {item}: {ex.Message}. Use Retry lookups.";
            QuestLookupStatus.Text = _questProgress;
        }
        finally { _questWorking = false; _questActiveItem = null; UpdateQuestProgress(); }
    }

    private OverlayContent QuestOpportunities()
    {
        var rows = new List<OverlayRow>();
        var quests = _questInventory.Opportunities(_characterName ?? "", _questCache, _settings.QuestFilter, _settings.QuestRecentFirst);
        foreach (var quest in quests)
        {
            rows.Add(new(quest.Title, Bold: true, QuestHeading: true));
            var items = _settings.QuestRecentFirst
                ? quest.Items.OrderByDescending(i => i.LastLootUtc).ThenBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                : quest.Items.OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                rows.Add(new(item.Item, Wrap: true, LootItem: item.Item,
                    Details: $"Owned {item.Owned}  |  Carried / equipped {item.Carried}  |  Bank {item.Bank}  |  Collection {item.Collection}  |  Installed {item.Installed}  |  History {item.Historical} (unconfirmed)"));
        }
        if (rows.Count == 0) rows.Add(new(string.IsNullOrWhiteSpace(_settings.QuestFilter)
            ? "No quest opportunities found yet." : "No quests or items match your filter.", Wrap: true));
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
            Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 5),
            ToolTip = "Completed checks, including failed attempts. Failed items can be retried from Loot quests.",
        };
        toolbar.Children.Add(_questProgressBar);
        _questProgressText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _questProgressText.SetResourceReference(TextBlock.ForegroundProperty, "NightText");
        toolbar.Children.Add(_questProgressText);
        _questCurrentText = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 6),
        };
        _questCurrentText.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        toolbar.Children.Add(_questCurrentText);
        var caveat = new TextBlock { Text = "Quest references, not verified turn-ins", FontSize = 11 };
        caveat.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        toolbar.Children.Add(caveat);
        panel.SetToolbar(toolbar);
    }

    private void UpdateQuestProgress()
    {
        if (_questProgressBar is null || _questProgressText is null || _questCurrentText is null) return;
        var items = _questInventory.Items.Where(i => i.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
            .Select(i => QuestLookup.ItemTitle(i.Item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var now = DateTime.UtcNow;
        bool Cached(string item) => _questCache.TryGetValue(item, out var result) &&
            now - result.CheckedUtc < TimeSpan.FromDays(7);
        int cached = items.Count(Cached);
        int failed = items.Count(i => _questFailed.Contains(i) && !Cached(i));
        int completed = items.Count(i => Cached(i) || (_questAttempted.Contains(i) && i != _questActiveItem));
        int active = items.Count(i => i == _questActiveItem && !Cached(i));
        int queued = Math.Max(0, items.Length - completed - active);
        _questProgressBar.Maximum = Math.Max(1, items.Length);
        _questProgressBar.Value = completed;
        _questProgressText.Text = items.Length == 0 ? "Waiting for inventory or loot" :
            $"{completed:N0} / {items.Length:N0} checked ({100.0 * completed / items.Length:0}%)  |  {cached:N0} cached  |  {queued:N0} queued  |  {failed:N0} failed";
        _questCurrentText.Text = _questProgress;
        _questCurrentText.ToolTip = _questProgress;
    }

    private void SaveQuestInventory(bool throwOnError = false)
    {
        while (_questLoot.TryDequeue(out var note))
        {
            note();
            _questDirty = true;
        }
        if (!_questDirty) return;
        try
        {
            AtomicFile.WriteAllText(QuestInventoryPath, JsonSerializer.Serialize(_questInventory));
            _questDirty = false;
        }
        catch (Exception ex) when (!throwOnError && (ex is IOException or UnauthorizedAccessException))
        {
            QuestLookupStatus.Text = $"Could not save quest inventory: {ex.Message}";
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
        Dispatcher.BeginInvoke(() => { QuestOwnedGrid.CommitEdit(); SaveQuestInventory(); });
    }

    private void CompleteQuest_Click(object sender, RoutedEventArgs e)
    {
        if (QuestResults.SelectedItem is not QuestReference quest)
        {
            QuestLookupStatus.Text = "Select a quest reference first.";
            return;
        }
        string key = QuestInventory.CompletionKey(_characterName ?? "", quest.Title);
        bool completed = _questInventory.Completed.Add(key);
        if (!completed) _questInventory.Completed.Remove(key);
        _questDirty = true;
        SaveQuestInventory();
        QuestLookupStatus.Text = completed ? "Quest marked completed. Adjust owned counts after turning items in." : "Quest marked incomplete.";
    }

    private void RetryQuests_Click(object sender, RoutedEventArgs e)
    {
        _questAttempted.Clear();
        _questFailed.Clear();
        UpdateQuestProgress();
    }

    private void InitializeLootQuests()
    {
        _wikiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("EqLogOverlay/1.0");
        if (!File.Exists(QuestCachePath))
        {
            return;
        }

        try
        {
            foreach (var result in JsonSerializer.Deserialize<List<QuestLookupResult>>(File.ReadAllText(QuestCachePath)) ?? [])
            {
                string title = QuestLookup.ItemTitle(result.Item);
                if (!_questCache.TryGetValue(title, out var existing) || result.CheckedUtc > existing.CheckedUtc)
                    _questCache[title] = result with { Item = title };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            QuestLookupStatus.Text = $"Could not load wiki cache: {ex.Message}";
        }
    }

    private async void QuestItem_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (QuestOwnedGrid.SelectedItem is QuestInventoryItem selected)
            await LookupSelectedQuestItemAsync(selected);
        else
        {
            _questSelectionVersion++;
            QuestResults.ItemsSource = null;
        }
    }

    private async void LookupQuest_Click(object sender, RoutedEventArgs e)
    {
        if (QuestOwnedGrid.SelectedItem is QuestInventoryItem selected)
            await LookupSelectedQuestItemAsync(selected);
        else QuestLookupStatus.Text = "Select an inventory row first.";
    }

    private async Task LookupLootItemAsync(string item)
    {
        _dock.SetSidePanelOpen(true);
        Tabs.SelectedItem = LootQuestsTab;
        ShowDock();
        var selected = _questInventory.Items.FirstOrDefault(i =>
            i.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase) && ItemNames.Comparer.Equals(i.Item, item));
        if (selected is null)
        {
            // Other players' visible loot can be inspected without claiming ownership.
            selected = new QuestInventoryItem { Character = _characterName ?? "", Item = item };
            _questInventory.Items.Add(selected);
            _questDirty = true;
        }
        if (!_ownedQuestItems.Contains(selected)) _ownedQuestItems.Add(selected);
        if (ReferenceEquals(QuestOwnedGrid.SelectedItem, selected))
            await LookupSelectedQuestItemAsync(selected);
        else QuestOwnedGrid.SelectedItem = selected;
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
            if (version != _questSelectionVersion) return;
            QuestResults.ItemsSource = result.Quests;
            QuestLookupStatus.Text = result.Quests.Count == 0
                ? "No linked quest pages found. This does not prove the item has no quest use; wiki coverage may be incomplete."
                : $"{result.Quests.Count} quest reference(s). Open a page to check requirements versus rewards.";
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested)
        {
            if (version == _questSelectionVersion) QuestLookupStatus.Text = "Wiki lookup cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
            UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            if (version == _questSelectionVersion) QuestLookupStatus.Text = $"Wiki lookup failed: {ex.Message}";
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
            OpenWiki(QuestLookup.PageUrl(QuestLookup.ItemTitle(selected.Item)));
        else QuestLookupStatus.Text = "Select an inventory row first.";
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
