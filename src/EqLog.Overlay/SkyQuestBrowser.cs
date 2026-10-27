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
    private string _skyInventorySignature = "";
    private static string SkyCachePath => Path.Combine(AppSettings.Folder, "sky-quests.json");

    private async void SkyTab_Selected(object sender, RoutedEventArgs e)
    {
        if (e.Source == Tabs && SkyTab?.IsSelected == true && !_skyLoading &&
            (_skyCatalog is null || DateTime.UtcNow - _skyCatalog.CheckedUtc >= TimeSpan.FromDays(1)))
            await LoadSkyQuestsAsync(false);
    }

    private async void SkyRefresh_Click(object sender, RoutedEventArgs e) => await LoadSkyQuestsAsync(true);

    private async Task LoadSkyQuestsAsync(bool refresh)
    {
        if (_skyLoading) return;
        _skyLoading = true;
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
                    if (_skyCatalog.Quests.Count == 0) throw new JsonException("Sky cache contains no quests.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    SkyStatus.Text = $"Sky cache could not be read: {ex.Message}. Fetching from EQLWiki...";
                }
            }

            if (refresh || _skyCatalog is null || _skyCatalog.Quests.Any(q => q.Requirements is null) ||
                DateTime.UtcNow - _skyCatalog.CheckedUtc >= TimeSpan.FromDays(1))
            {
                _skyCatalog = await SkyQuests.FetchAsync(_wikiHttp, _wikiCts.Token);
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
            RenderSkyQuests();
        }
    }

    private void RenderSkyQuests()
    {
        if (_skyCatalog is null) return;
        SkyCards.Children.Clear();
        string selected = SkyClassBox.SelectedItem as string ?? "All classes";
        foreach (var group in _skyCatalog.Quests.Where(q => selected == "All classes" || q.Class == selected).GroupBy(q => q.Class))
        {
            SkyCards.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(), FontSize = 17, FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Pumpkin"), Margin = new Thickness(0, 18, 0, 10),
            });
            foreach (var quest in group)
            {
                var content = new StackPanel();
                var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
                var link = new Hyperlink(new Run(quest.Name)) { Foreground = (Brush)FindResource("Pumpkin"), ToolTip = "Open this class's quest table on EQLWiki" };
                link.Click += (_, _) => OpenSkyWiki(quest.Url);
                title.Inlines.Add(link);
                content.Children.Add(title);
                AddSkyField(content, "Reward", quest.Reward, (Brush)FindResource("Pumpkin"));
                AddSkyField(content, "Quest giver", quest.Giver);
                AddSkyField(content, "Say", quest.Phrase);
                AddSkyField(content, "Required rune", quest.Rune);
                AddSkyField(content, "Required items / drop sources", quest.Items);
                var progress = SkyQuests.Progress(quest, _questInventory, _characterName ?? "");
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
                        FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0),
                    };
                    DockPanel.SetDock(count, Dock.Right);
                    heading.Children.Add(count);
                    heading.Children.Add(new TextBlock
                    {
                        Text = item.Item, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("NightText"),
                    });
                    requirement.Children.Add(heading);
                    string detail = $"Carried / equipped {item.Carried}  |  Bank {item.Bank}";
                    if (!item.Satisfied) detail += $"  |  Missing {item.Required - item.Owned}";
                    if (item.Collection > 0) detail += $"  |  Collection {item.Collection} (verify turn-in availability)";
                    if (item.Installed > 0) detail += $"  |  Installed {item.Installed} (not loose)";
                    if (item.Historical > 0) detail += $"  |  History {item.Historical} (unconfirmed)";
                    requirement.Children.Add(new TextBlock
                    {
                        Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                        Foreground = (Brush)FindResource("NightMuted"), Margin = new Thickness(0, 5, 0, 0),
                    });
                    content.Children.Add(new Border
                    {
                        Child = requirement, Background = (Brush)FindResource("NightBackground"),
                        CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0),
                    });
                }
                SkyCards.Children.Add(new Border
                {
                    Child = content, Background = (Brush)FindResource("NightSurface"),
                    BorderBrush = (Brush)FindResource("NightBorder"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12),
                });
            }
        }
    }

    private void RefreshSkyInventory()
    {
        if (SkyTab.IsSelected != true || _skyCatalog is null) return;
        string signature = (_characterName ?? "") + "|" + string.Join("|", _questInventory.Items
            .Where(i => i.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"{i.Item}:{i.Owned}:{i.Carried}:{i.Bank}:{i.Historical}:{i.Collection}:{i.Installed}"));
        if (signature == _skyInventorySignature) return;
        _skyInventorySignature = signature;
        RenderSkyQuests();
    }

    private void AddSkyField(StackPanel parent, string label, string value, Brush? color = null)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = color ?? (Brush)FindResource("NightText"), Margin = new Thickness(0, 7, 0, 0) };
        text.Inlines.Add(new Run(label + ": ") { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("NightMuted") });
        text.Inlines.Add(new Run(value));
        parent.Children.Add(text);
    }

    private void SkySource_Click(object sender, RoutedEventArgs e) => OpenSkyWiki(SkyQuests.SourceUrl);

    private void OpenSkyWiki(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SkyStatus.Text = $"Could not open browser: {ex.Message}";
        }
    }
}
