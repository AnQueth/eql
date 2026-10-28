using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private static readonly string NpcCompendiumPath = Path.Combine(AppSettings.Folder, "npc-compendium.json");
    private NpcCompendium _npcCompendium = new();
    private bool _npcSaveEnabled = true;
    private string? _npcStorageError;
    private TextBox _npcSearch = null!;
    private ComboBox _npcChoices = null!;
    private string _npcChoicesSignature = "";
    private DateTime _npcChoicesNextRefresh;
    private CancellationTokenSource? _npcWikiCts;
    private string _npcWikiStatus = "";
    private string? _npcWikiSelection;
    private string _npcSpellSignature = "";
    private int _npcHistoryPage;
    private DateTime _npcNextRender;
    private OverlayContent? _npcContent;
    private WrapPanel _npcPager = null!;
    private Button _npcPrevious = null!;
    private Button _npcNext = null!;
    private TextBlock _npcPageLabel = null!;
    private const int NpcHistoryPageSize = 10;

    private sealed record NpcChoice(string Mob, string Zone)
    {
        public string Label
        {
            get
            {
                return $"{Mob} - {Zone}";
            }
        }
    }

    private void InitializeNpcCompendium()
    {
        try
        {
            _npcCompendium = NpcCompendium.Load(NpcCompendiumPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _npcSaveEnabled = false;
            _npcStorageError = $"NPC compendium could not load: {ex.Message}. This session is not being saved; the original file is protected.";
            MobInfoStatusText.Text = _npcStorageError;
        }
    }

    private void ConfigureNpcToolbar(OverlayPanel panel)
    {
        var bar = new StackPanel();
        var searchRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var lookup = new Button { Content = "Look up name", ToolTip = "Look up the exact typed NPC name even without recorded encounters." };
        DockPanel.SetDock(lookup, Dock.Right);
        searchRow.Children.Add(lookup);
        _npcSearch = new TextBox
        {
            Text = _settings.NpcSearch,
            MinWidth = 100,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Filter saved NPC names; choose a name/zone below. Unlock floating panels to type.",
        };
        searchRow.Children.Add(_npcSearch);
        bar.Children.Add(searchRow);
        _npcChoices = new ComboBox
        {
            DisplayMemberPath = nameof(NpcChoice.Label),
            MaxDropDownHeight = 320,
            ToolTip = "Recorded NPCs, separated by the full logged zone name.",
        };
        bar.Children.Add(_npcChoices);
        _npcSearch.TextChanged += (_, _) =>
        {
            _settings.NpcSearch = _npcSearch.Text;
            _npcChoicesNextRefresh = default;
        };
        _npcChoices.SelectionChanged += (_, _) =>
        {
            if (_npcChoices.SelectedItem is NpcChoice choice)
            {
                SelectNpc(choice.Mob, choice.Zone);
            }
        };
        lookup.Click += (_, _) =>
        {
            string name = _npcSearch.Text.Trim();
            if (name.Length == 0)
            {
                _npcWikiStatus = "Enter an NPC name before looking it up.";
                return;
            }
            var matches = _npcCompendium.Search(name);
            var exact = matches.FirstOrDefault(e => string.Equals(e.Mob, name, StringComparison.OrdinalIgnoreCase));
            SelectNpc(exact?.Mob ?? name, exact?.Zone ?? "(unknown zone)");
            StartNpcWikiLookup(false);
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var refresh = new Button { Content = "Refresh wiki", FontSize = 11, Padding = new Thickness(10, 5, 10, 5) };
        refresh.Click += (_, _) => StartNpcWikiLookup(true);
        var wiki = new Button { Content = "Open wiki", FontSize = 11, Padding = new Thickness(10, 5, 10, 5) };
        wiki.Click += (_, _) =>
        {
            if (_settings.NpcSelectedMob is { } mob && _settings.NpcSelectedZone is { } zone)
            {
                OpenMobWiki(mob, zone, message =>
                {
                    _npcWikiStatus = message;
                    _npcNextRender = default;
                });
            }
            else
            {
                _npcWikiStatus = "Choose an NPC or enter an exact name first.";
                _npcNextRender = default;
            }
        };
        buttons.Children.Add(refresh);
        buttons.Children.Add(wiki);
        bar.Children.Add(buttons);
        var views = new TabControl
        {
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 4),
        };
        views.Items.Add(new TabItem { Header = "Overview" });
        views.Items.Add(new TabItem { Header = "Spell resists" });
        views.Items.Add(new TabItem { Header = "Encounters" });
        _settings.NpcView = Math.Clamp(_settings.NpcView, 0, 2);
        views.SelectedIndex = _settings.NpcView;
        views.SelectionChanged += (_, e) =>
        {
            if (e.Source == views)
            {
                _settings.NpcView = views.SelectedIndex;
                _npcNextRender = default;
            }
        };
        bar.Children.Add(views);
        _npcPager = new WrapPanel { Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
        _npcPrevious = new Button { Content = "Newer", FontSize = 11, Padding = new Thickness(10, 4, 10, 4) };
        _npcPrevious.Click += (_, _) =>
        {
            _npcHistoryPage = Math.Max(0, _npcHistoryPage - 1);
            _npcNextRender = default;
        };
        _npcNext = new Button { Content = "Older", FontSize = 11, Padding = new Thickness(10, 4, 10, 4) };
        _npcNext.Click += (_, _) =>
        {
            int count = SelectedNpc()?.Encounters.Count ?? 0;
            _npcHistoryPage = Math.Min(Math.Max(0, (count - 1) / NpcHistoryPageSize), _npcHistoryPage + 1);
            _npcNextRender = default;
        };
        _npcPageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 6), FontSize = 11 };
        _npcPageLabel.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        _npcPager.Children.Add(_npcPrevious);
        _npcPager.Children.Add(_npcNext);
        _npcPager.Children.Add(_npcPageLabel);
        bar.Children.Add(_npcPager);
        panel.SetToolbar(bar);
    }

    private NpcEntry? SelectedNpc()
    {
        return _settings.NpcSelectedMob is { } mob && _settings.NpcSelectedZone is { } zone
            ? _npcCompendium.Get(mob, zone) : null;
    }

    private void SelectNpc(string mob, string zone)
    {
        if (_settings.NpcSelectedMob == mob && _settings.NpcSelectedZone == zone)
        {
            return;
        }
        _npcWikiCts?.Cancel();
        _settings.NpcSelectedMob = mob;
        _settings.NpcSelectedZone = zone;
        _npcHistoryPage = 0;
        _npcWikiSelection = null;
        _npcWikiStatus = "";
        _npcNextRender = default;
    }

    private void RefreshNpcChoices()
    {
        if (_npcChoices is null || !IsShowing("npcs") || DateTime.Now < _npcChoicesNextRefresh)
        {
            return;
        }
        _npcChoicesNextRefresh = DateTime.Now.AddSeconds(2);
        var matches = _npcCompendium.Search(_npcSearch.Text).Select(e => new NpcChoice(e.Mob, e.Zone)).ToArray();
        string signature = string.Join("\n", matches.Select(m => m.Label));
        if (_npcChoicesSignature == signature)
        {
            return;
        }
        _npcChoicesSignature = signature;
        _npcChoices.ItemsSource = matches;
        _npcChoices.SelectedItem = matches.FirstOrDefault(m => m.Mob == _settings.NpcSelectedMob &&
            m.Zone == _settings.NpcSelectedZone);
        if (_settings.NpcSelectedMob is null && matches.Length > 0)
        {
            _npcChoices.SelectedIndex = 0;
        }
    }

    private void StartNpcWikiLookup(bool force)
    {
        if (_settings.NpcSelectedMob is not { } mob || _settings.NpcSelectedZone is not { } zone)
        {
            _npcWikiStatus = "Choose an NPC or enter an exact name first.";
            return;
        }
        _npcWikiCts?.Cancel();
        _npcWikiCts?.Dispose();
        _npcWikiCts = CancellationTokenSource.CreateLinkedTokenSource(_wikiCts.Token);
        _npcWikiSelection = mob + "\n" + zone;
        _npcSpellSignature = string.Join("\n", _mobResistances.Get(zone, mob).Spells);
        _ = LookupMobInfoAsync(mob, zone, force, _npcWikiCts.Token, WikiPriority.Interactive, status =>
        {
            _npcWikiStatus = status;
            _npcNextRender = default;
        });
    }

    private OverlayContent NpcCompendiumContent()
    {
        if (_npcContent is null || DateTime.Now >= _npcNextRender)
        {
            _npcContent = BuildNpcCompendiumContent();
            _npcNextRender = DateTime.Now.AddSeconds(1);
        }
        return _npcContent;
    }

    private OverlayContent BuildNpcCompendiumContent()
    {
        _npcPager.Visibility = _settings.NpcView == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.NpcSelectedMob is not { } mob || _settings.NpcSelectedZone is not { } zone)
        {
            return new("NPC compendium", "Search recorded NPCs or look up an exact name",
                [new(_npcStorageError ?? "Importing the selected log's history and recording new interactions. " +
                    "Choose a name/zone above. Unlock floating overlays to search.", Wrap: true)]);
        }
        var resistanceInfo = _mobResistances.Get(zone, mob);
        if (_npcWikiSelection != mob + "\n" + zone ||
            _npcSpellSignature != string.Join("\n", resistanceInfo.Spells))
        {
            StartNpcWikiLookup(false);
        }
        var entry = SelectedNpc();
        var rows = new List<OverlayRow>();
        if (_npcStorageError is not null)
        {
            rows.Add(new(_npcStorageError, Wrap: true, Bold: true));
        }
        if (_settings.NpcView == 1)
        {
            AddNpcSpellRows(rows, resistanceInfo, zone, mob);
        }
        else if (_settings.NpcView == 2)
        {
            AddNpcEncounterRows(rows, entry);
        }
        else
        {
            AddNpcOverviewRows(rows, entry, mob, zone);
        }
        return new(mob, zone, rows);
    }

    private void AddNpcOverviewRows(List<OverlayRow> rows, NpcEntry? entry, string mob, string zone)
    {
        if (entry is not null)
        {
            AddNpcCombatSummary(rows, entry);
        }
        rows.Add(new("NPC reference", Bold: true, QuestHeading: true));
        var shared = BuildMobInfoContent(mob, zone, entry?.LastConsider, entry?.LastAssessment, _npcWikiStatus, compact: true);
        rows.AddRange(shared.Rows);
        if (entry is null)
        {
            rows.Add(new("No recorded encounters", Details: "Wiki reference only. The selected log's history and new live interactions are recorded automatically.", Stacked: true));
        }
        else
        {
            rows.Add(new("Last seen", $"{entry.LastSeen:g}",
                Details: $"First seen {entry.FirstSeen:g}  |  {entry.Considers:N0} considers" +
                    (entry.LastConsider is { } con ? $"\nLast consider {con.Time:g}; historical, not your current game target." : ""),
                Stacked: true));
        }
    }

    private static void AddNpcCombatSummary(List<OverlayRow> rows, NpcEntry entry)
    {
        var encounters = entry.Encounters;
        rows.Add(new("Your combat record",
            encounters.Any(e => e.YourDamage > 0) ? $"{entry.YourAverageDps:N1} average DPS" : "No personal damage recorded",
            Details: $"{encounters.Sum(e => e.YourDamage):N0} damage  |  {encounters.Count:N0} encounters  |  " +
                $"{encounters.Count(e => e.Outcome == "NPC died"):N0} NPC deaths observed\n" +
                $"Damage taken: {encounters.Sum(e => e.IncomingDamage):N0}",
            Stacked: true));
        if (encounters.Any(e => e.OtherDamage > 0))
        {
            rows.Add(new("Other attackers", $"{entry.OtherAverageDps:N1} average DPS",
                Details: $"{encounters.Sum(e => e.OtherDamage):N0} observed damage; group membership is not verified.",
                Stacked: true));
        }
    }

    private void AddNpcEncounterRows(List<OverlayRow> rows, NpcEntry? entry)
    {
        var encounters = entry?.Encounters ?? [];
        int pages = Math.Max(1, (encounters.Count + NpcHistoryPageSize - 1) / NpcHistoryPageSize);
        _npcHistoryPage = Math.Clamp(_npcHistoryPage, 0, pages - 1);
        _npcPrevious.IsEnabled = _npcHistoryPage > 0;
        _npcNext.IsEnabled = _npcHistoryPage + 1 < pages;
        _npcPageLabel.Text = $"Page {_npcHistoryPage + 1} of {pages}  |  {encounters.Count:N0} encounters";
        if (entry is not null)
        {
            AddNpcCombatSummary(rows, entry);
        }
        if (encounters.Count == 0)
        {
            rows.Add(new("No recorded encounters", Details: "Interactions from the selected log will appear here. Missing deaths are never treated as confirmed kills.", Stacked: true));
        }
        foreach (var encounter in encounters.Skip(_npcHistoryPage * NpcHistoryPageSize).Take(NpcHistoryPageSize))
        {
            string spells = string.Join("\n", encounter.Spells.OrderByDescending(s => s.Damage).Select(s =>
                $"{s.Spell}  |  {s.Damage:N0} damage  |  {s.Landings:N0} direct landings  |  {s.Resists:N0} resists"));
            rows.Add(new($"{encounter.Start:g}  /  {encounter.Outcome}",
                $"{encounter.YourDps:N1} DPS  |  {encounter.YourDamage:N0} damage  |  {encounter.Seconds:0.#}s",
                Details: $"{encounter.Character}  |  {encounter.Classes ?? "classes unknown"}  |  Level {encounter.Level?.ToString() ?? "?"}\n" +
                    $"Others: {encounter.OtherDamage:N0} damage  |  Taken: {encounter.IncomingDamage:N0}" +
                    (spells.Length > 0 ? "\n\n" + spells : ""), Stacked: true));
        }
        rows.Add(new("How this history is measured", Details:
            "Average DPS is total damage / summed encounter seconds (last activity - start + 1, minimum 1s). " +
            "Your average uses fights where you dealt damage. Direct landings include partial/finishing hits; DoT ticks only add damage. " +
            "Same-name NPCs overlap; timeout and zone/loadout changes split summaries. No raw lines are stored.", Stacked: true));
    }

    private void AddNpcSpellRows(List<OverlayRow> rows, MobResistanceInfo resistanceInfo, string zone, string mob)
    {
        rows.Add(new("Spell resistance history", Bold: true, QuestHeading: true));
        rows.Add(new("Saved observations for this NPC and zone. School and damage rating are separate.", Wrap: true, Scale: 0.85));
        if (resistanceInfo.Spells.Count == 0)
        {
            rows.Add(new("No spell observations yet", Details: "Targeted resists and confirmed personal casts will appear here as the log is read.", Stacked: true));
        }
        foreach (string spell in resistanceInfo.Spells.Order(StringComparer.OrdinalIgnoreCase))
        {
            var details = _mobResistances.Get(zone, mob, spell);
            var counts = _mobResistances.GetRawSpellCounts(zone, mob, spell);
            var metadata = _mobResistances.GetSpell(spell);
            var observed = metadata?.Type is { } type ? details.Observations.Single(o => o.Type == type) : null;
            string school = metadata?.Type?.ToString() ?? "School not verified";
            string result;
            string explanation;
            string evidence = $"Full resists {counts.FullResists:N0}  |  Damage samples {counts.DamageHits:N0}" +
                (counts.LegacyLandings > 0 ? $"  |  Earlier landings {counts.LegacyLandings:N0}" : "");
            if (observed is not null && metadata?.SingleTargetDirectDamage == true)
            {
                result = $"{school}  /  {observed.DisplayText}";
                evidence = $"Full/near-full {observed.Landed:N0}  |  Full resists {counts.FullResists:N0}  |  Estimated partial {observed.EstimatedPartial:N0}";
                explanation = observed.UnassessedDamage > 0
                    ? $"{observed.UnassessedDamage:N0} hits need a reliable normal-damage baseline for this character/loadout/rank."
                    : observed.Landed + observed.Resisted == 0 ? "No assessed damage outcomes yet." : "";
                if (counts.LegacyLandings > 0)
                {
                    explanation += (explanation.Length > 0 ? "\n" : "") +
                        $"{counts.LegacyLandings:N0} earlier landings have no damage values; partial damage cannot be checked retrospectively.";
                }
            }
            else
            {
                result = $"{school}  /  {metadata?.TargetType ?? "Wiki details pending"}";
                explanation = metadata?.AssessmentNote ?? "Spell metadata has not been loaded. Use Refresh wiki to retry.";
            }
            rows.Add(new(spell, result, Details: evidence + (explanation.Length > 0 ? "\n" + explanation : ""), Stacked: true));
        }
        rows.Add(new("About damage ratings", Details:
            "Only verified single-target instant damage (including lifetaps) receives a full/partial rating. " +
            "Area waves and DoT ticks are not reliable cast-success denominators. " +
            "Reduced-damage ratings are estimates; critical/modified and recent finishing hits are excluded. " +
            "Missing school, insufficient baseline, and unsupported spell shape are not NPC immunity.", Stacked: true));
        if (!string.IsNullOrEmpty(_npcWikiStatus))
        {
            rows.Add(new(_npcWikiStatus, Wrap: true, Scale: 0.85));
        }
    }

    private void SaveNpcCompendium(bool throwOnError)
    {
        if (!_npcSaveEnabled)
        {
            if (throwOnError)
            {
                throw new IOException(_npcStorageError);
            }
            return;
        }
        try
        {
            _npcCompendium.Save(NpcCompendiumPath);
            _npcStorageError = null;
        }
        catch (Exception ex) when (!throwOnError && (ex is IOException or UnauthorizedAccessException))
        {
            _npcStorageError = $"NPC compendium save failed: {ex.Message}";
            MobInfoStatusText.Text = _npcStorageError;
        }
    }
}
