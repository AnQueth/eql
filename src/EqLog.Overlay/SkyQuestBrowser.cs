using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private SkyQuestCatalog? _skyCatalog;
    private bool _skyLoading;
    private WikiPriority _skyLoadPriority;
    private bool _skyForceRefresh;
    private bool _skyOverlayLoadAttempted;
    private ComboBox? _skyOverlayClass;
    private string _skyInventorySignature = "";
    private static string SkyCachePath
    {
        get
        {
            return Path.Combine(AppSettings.Folder, "sky-quests.json");
        }
    }

    private async void SkyTab_Selected(object sender, RoutedEventArgs e)
    {
        if (e.Source == Tabs && SkyTab?.IsSelected == true &&
            (_skyLoading || _skyCatalog is null || DateTime.UtcNow - _skyCatalog.CheckedUtc >= TimeSpan.FromDays(1)))
        {
            await LoadSkyQuestsAsync(false);
        }
    }

    private async void SkyRefresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadSkyQuestsAsync(true);
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
        SkyRefreshButton.IsEnabled = false;
        SkyStatus.Text = "Loading Plane of Sky class quests...";
        try
        {
            if (_skyCatalog is null && File.Exists(SkyCachePath))
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
                    SkyStatus.Text = $"Sky cache could not be read: {ex.Message}. Fetching from EQLWiki...";
                }
            }

            if (_skyForceRefresh || _skyCatalog is null ||
                _skyCatalog.Quests.Any(q => q.Requirements is null || q.RuneLinks is null || q.ItemLinks is null) ||
                DateTime.UtcNow - _skyCatalog.CheckedUtc >= TimeSpan.FromDays(1))
            {
                if (_skyCatalog is not null)
                {
                    _wikiRepository.SeedSky(_skyCatalog);
                }
                _skyCatalog = await _wikiRepository.GetSkyAsync(_skyLoadPriority, _skyForceRefresh, _wikiCts.Token);
                AtomicFile.WriteAllText(SkyCachePath, JsonSerializer.Serialize(_skyCatalog));
            }
            SkyStatus.Text = $"{_skyCatalog.Quests.Count} quests • EQLWiki • updated {_skyCatalog.CheckedUtc.ToLocalTime():g}";
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            HttpRequestException or OperationCanceledException or InvalidOperationException or FormatException)
        {
            SkyStatus.Text = $"Sky quest load failed: {ex.Message}" +
                (_skyCatalog is null ? " Click Refresh to retry." : " Showing previously loaded data.");
        }
        finally
        {
            if (_skyCatalog is not null)
            {
                string selected = SkyClassBox.SelectedItem as string ?? _settings.SkyClass;
                SkyClassBox.ItemsSource = new[] { "All classes" }.Concat(_skyCatalog.Quests.Select(q => q.Class).Distinct().Order());
                SkyClassBox.SelectedItem = SkyClassBox.Items.Contains(selected) ? selected : "All classes";
                if (_skyOverlayClass is not null)
                {
                    _skyOverlayClass.ItemsSource = SkyClassBox.ItemsSource;
                    _skyOverlayClass.SelectedItem = SkyClassBox.SelectedItem;
                }
                RenderSkyQuests();
            }
            _skyLoading = false;
            SkyRefreshButton.IsEnabled = true;
        }
    }

    private void SkyClass_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SkyClassBox.SelectedItem is string selected)
        {
            _settings.SkyClass = selected;
            if (_skyOverlayClass is not null && !Equals(_skyOverlayClass.SelectedItem, selected))
            {
                _skyOverlayClass.SelectedItem = selected;
            }

            RenderSkyQuests();
        }
    }

    private void ConfigureSkyToolbar(OverlayPanel panel)
    {
        var toolbar = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(6, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadSkyQuestsAsync(true);
        DockPanel.SetDock(refresh, Dock.Right);
        toolbar.Children.Add(refresh);
        _skyOverlayClass = new ComboBox
        {
            ItemsSource = new[] { "All classes", _settings.SkyClass }.Distinct().ToArray(),
            SelectedItem = _settings.SkyClass,
            ToolTip = "Class filter shared with the Plane of Sky tab. Only quests with an owned ingredient appear.",
        };
        _skyOverlayClass.SelectionChanged += (_, _) =>
        {
            if (_skyOverlayClass.SelectedItem is not string selected)
            {
                return;
            }

            _settings.SkyClass = selected;
            if (SkyClassBox.Items.Contains(selected))
            {
                SkyClassBox.SelectedItem = selected;
            }
        };
        toolbar.Children.Add(_skyOverlayClass);
        panel.SetToolbar(toolbar);
    }

    private OverlayContent SkyOverlayContent()
    {
        if (_skyCatalog is null && !_skyLoading && !_skyOverlayLoadAttempted)
        {
            _skyOverlayLoadAttempted = true;
            _ = LoadSkyQuestsAsync(false, WikiPriority.Background);
        }
        var rows = new List<OverlayRow>();
        if (_skyCatalog is null)
        {
            rows.Add(new(_skyLoading ? "Loading Plane of Sky quests..." : SkyStatus.Text, Wrap: true));
        }
        else
        {
            foreach (var entry in SkyQuests.OwnedQuests(_skyCatalog, _questInventory, _characterName ?? "", _settings.SkyClass))
            {
                var quest = entry.Quest;
                rows.Add(new(quest.Name, Bold: true, QuestHeading: true, Wrap: true));
                rows.Add(new($"Give to: {quest.Giver}", Wrap: true));
                rows.Add(new($"Reward: {quest.Reward}", Wrap: true, Bold: true, LootItem: quest.Reward));
                if (_settings.SkyClass == "All classes")
                {
                    rows.Add(new($"Class: {quest.Class}"));
                }

                foreach (var item in entry.Progress)
                {
                    string note = item.Collection > 0 ? $" | Collection {item.Collection}: verify turn-in availability" : "";
                    rows.Add(new(item.Item, $"{item.Owned} / {item.Required}", Wrap: true, LootItem: item.Item,
                        Details: $"{(item.Satisfied ? "Have" : "Missing")} | Carried {item.Carried} | Bank {item.Bank}{note}"));
                }
            }
            if (rows.Count == 0)
            {
                rows.Add(new("No quests with owned ingredients for this class. Import inventory or loot a required item.", Wrap: true));
            }

            if (_skyLoading)
            {
                rows.Insert(0, new("Refreshing wiki data...", Wrap: true));
            }
            else if (SkyStatus.Text.StartsWith("Sky quest load failed:", StringComparison.Ordinal))
            {
                rows.Insert(0, new(SkyStatus.Text, Wrap: true));
            }
        }
        return new("Sky quest tracker", $"{_settings.SkyClass} | {_characterName} | owned ingredients only", rows);
    }
    private void RenderSkyQuests()
    {
        if (_skyCatalog is null)
        {
            return;
        }

        SkyCards.Children.Clear();
        string selected = SkyClassBox.SelectedItem as string ?? "All classes";
        foreach (var group in _skyCatalog.Quests.Where(q => selected == "All classes" || q.Class == selected).GroupBy(q => q.Class))
        {
            SkyCards.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Pumpkin"),
                Margin = new Thickness(0, 18, 0, 10),
            });
            foreach (var quest in group)
            {
                var content = new StackPanel();
                var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
                var link = new Hyperlink(new Run(quest.Name)) { Foreground = (Brush)FindResource("Pumpkin"), ToolTip = "Open this class's quest table on EQLWiki" };
                link.Click += (_, _) => OpenSkyWiki(quest.Url);
                title.Inlines.Add(link);
                content.Children.Add(title);
                AddSkyField(content, "Reward", quest.Reward, (Brush)FindResource("Pumpkin"), [new(quest.Reward, quest.Reward)]);
                AddSkyField(content, "Quest giver", quest.Giver);
                AddSkyField(content, "Say", quest.Phrase);
                var progress = SkyQuests.Progress(quest, _questInventory, _characterName ?? "");
                AddSkyField(content, "Required rune", quest.Rune, items: quest.RuneLinks ?? FallbackSkyLinks(quest.Rune));
                AddSkyField(content, "Required items / drop sources", quest.Items, items: quest.ItemLinks ?? FallbackSkyLinks(quest.Items));
                bool ready = progress.Count > 0 && progress.All(p => p.Satisfied);
                AddSkyField(content, "Inventory progress",
                    progress.Count == 0 ? "Requirements unavailable; refresh wiki data." :
                    ready ? "All listed items owned (including bank and collections; verify turn-in availability)" :
                    $"{progress.Count(p => p.Satisfied)} / {progress.Count} listed requirements owned",
                    ready ? Brushes.LightGreen : Brushes.Orange);
                foreach (var item in progress)
                {
                    var requirement = new StackPanel();
                    var heading = new DockPanel();
                    var count = new TextBlock
                    {
                        Text = $"{(item.Satisfied ? "HAVE" : "NEED")}  {item.Owned} / {item.Required}",
                        Foreground = item.Satisfied ? Brushes.LightGreen : (Brush)FindResource("Pumpkin"),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(12, 0, 0, 0),
                    };
                    DockPanel.SetDock(count, Dock.Right);
                    heading.Children.Add(count);
                    var itemName = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("NightText"),
                    };
                    itemName.Inlines.Add(ItemWikiLink(item.Item, item.Item));
                    heading.Children.Add(itemName);
                    requirement.Children.Add(heading);
                    string detail = $"Carried / equipped {item.Carried}  |  Bank {item.Bank}";
                    if (!item.Satisfied)
                    {
                        detail += $"  |  Missing {item.Required - item.Owned}";
                    }

                    if (item.Collection > 0)
                    {
                        detail += $"  |  Collection {item.Collection} (verify turn-in availability)";
                    }

                    if (item.Installed > 0)
                    {
                        detail += $"  |  Installed {item.Installed} (not loose)";
                    }

                    if (item.Historical > 0)
                    {
                        detail += $"  |  History {item.Historical} (unconfirmed)";
                    }

                    requirement.Children.Add(new TextBlock
                    {
                        Text = detail,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = (Brush)FindResource("NightMuted"),
                        Margin = new Thickness(0, 5, 0, 0),
                    });
                    content.Children.Add(new Border
                    {
                        Child = requirement,
                        Background = (Brush)FindResource("NightBackground"),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(10),
                        Margin = new Thickness(0, 8, 0, 0),
                    });
                }
                SkyCards.Children.Add(new Border
                {
                    Child = content,
                    Background = (Brush)FindResource("NightSurface"),
                    BorderBrush = (Brush)FindResource("NightBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16),
                    Margin = new Thickness(0, 0, 0, 12),
                });
            }
        }
    }

    private void RefreshSkyInventory()
    {
        if (SkyTab.IsSelected != true || _skyCatalog is null)
        {
            return;
        }

        string signature = (_characterName ?? "") + "|" + string.Join("|", _questInventory.Items
            .Where(i => i.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"{i.Item}:{i.Owned}:{i.Carried}:{i.Bank}:{i.Historical}:{i.Collection}:{i.Installed}"));
        if (signature == _skyInventorySignature)
        {
            return;
        }

        _skyInventorySignature = signature;
        RenderSkyQuests();
    }

    private void AddSkyField(StackPanel parent, string label, string value, Brush? color = null,
        IReadOnlyList<SkyItemLink>? items = null)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = color ?? (Brush)FindResource("NightText"), Margin = new Thickness(0, 7, 0, 0) };
        text.Inlines.Add(new Run(label + ": ") { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("NightMuted") });
        if (items is null)
        {
            text.Inlines.Add(new Run(value));
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    text.Inlines.Add(new LineBreak());
                }
                text.Inlines.Add(ItemWikiLink(items[i].Label, items[i].Item));
            }
        }
        parent.Children.Add(text);
    }

    private static IReadOnlyList<SkyItemLink> FallbackSkyLinks(string value)
    {
        return value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new SkyItemLink(line.Trim(), line.Trim())).ToArray();
    }

    private Hyperlink ItemWikiLink(string label, string item)
    {
        var link = new Hyperlink(new Run(label))
        {
            ToolTip = $"Open {QuestLookup.ItemTitle(item)} on EQLWiki",
        };
        link.SetResourceReference(TextElement.ForegroundProperty, "Pumpkin");
        link.Click += (_, e) =>
        {
            OpenSkyWiki(QuestLookup.PageUrl(QuestLookup.ItemTitle(item)));
            e.Handled = true;
        };
        return link;
    }

    private void SkySource_Click(object sender, RoutedEventArgs e)
    {
        OpenSkyWiki(SkyQuests.SourceUrl);
    }

    private void OpenSkyWiki(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SkyStatus.Text = $"Could not open browser: {ex.Message}";
        }
    }
}
