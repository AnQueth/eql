using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

/// <summary>
/// The "Mote upgrade planner" overlay: pick an item or spell and its current +rank, and see what each step to +10 takes,
/// spending owned motes and (for equipment) spare inventory copies first.
/// </summary>
public partial class MainWindow
{
    private ComboBox? _planKind;
    private TextBox? _planName;
    private ComboBox? _planInventoryName;
    private string? _planNameFilter;
    private bool _refreshingPlanNames;
    private ComboBox? _planRank;
    private TextBox? _planPoints;
    private CheckBox? _planUseMotes;
    private CheckBox? _planUseCopies;
    private TabControl? _planTabs;
    private TabItem? _planFarmTab;
    private TabItem? _planWhatIfTab;
    private CheckBox? _planFeed;
    private CheckBox? _planFeedUsable;
    private TextBox? _planFodder;
    private ComboBox? _planFodderRank;
    private FrameworkElement? _planFeedRow;
    private FrameworkElement? _planFodderRow;
    private UpgradeKind? _planNamesKind;
    private TextBlock? _planSummary;
    private StackPanel? _planHost;
    private OverlayPanel? _planPanel;
    private string _planSignature = "";
    private IReadOnlyList<InventoryExportItem>? _planNamesSource;
    private Expander? _planCopyEditor;
    private StackPanel? _planCopyControls;
    private readonly List<int> _planHypotheticalCopies = [];
    private List<int> _planOriginalCopies = [];
    private string _planCopyContext = "";
    private IReadOnlyList<InventoryExportItem>? _planCopySource;

    private void ConfigureMotePlanOverlay(OverlayPanel panel)
    {
        _planPanel = panel;
        _planKind = new ComboBox
        {
            ItemsSource = new[] { "Equipment", "Spell" },
            SelectedIndex = 0,
            MinWidth = 100,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Equipment motes are worth 1-10 points and only work up to a +rank limit; spell motes double in value and have no limit.",
        };
        _planName = new TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Type an item name to plan or search the inventory dropdown below.",
        };
        _planInventoryName = new ComboBox
        {
            IsEditable = false,
            IsTextSearchEnabled = false,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Pick a matching item from your last inventory export. Picking an item sets the rank to your best copy.",
        };
        _planRank = new ComboBox
        {
            ItemsSource = Enumerable.Range(0, MoteValues.MaxRank + 1).Select(r => $"+{r}").ToArray(),
            SelectedIndex = 0,
            MinWidth = 56,
            Margin = new Thickness(4, 0, 10, 0),
            ToolTip = "The rank it is at now",
        };
        _planPoints = new TextBox
        {
            Text = "0",
            Width = 52,
            Margin = new Thickness(4, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Points already put toward the next rank (0 if unknown). Going from +N to +N+1 takes 2^N points.",
        };
        _planUseMotes = new CheckBox
        {
            Content = "Use owned motes",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Spend the motes you own (from your inventory export plus newer loot) before listing missing ones",
        };
        _planUseCopies = new CheckBox
        {
            Content = "Use inventory copies",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Merge spare copies of this item from your inventory export. A +N copy counts as exactly +N (2^N points).",
        };
        _planFeed = new CheckBox
        {
            Content = "Feed copies with motes first",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Raise spare copies before merging them. A lower-rank copy may still take motes the main item can no longer use, " +
                "so it can be worth raising first. The planner uses feeding only when it helps.",
        };
        _planFeedUsable = new CheckBox
        {
            Content = "Include motes the item can still use",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Also feed copies with motes the main item could take now. Helps when you have more of a tier than the main item " +
                "can use before it outgrows them. Off: only feed motes the main item can no longer use.",
        };
        _planFodder = new TextBox
        {
            Text = "0",
            Width = 52,
            Margin = new Thickness(4, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Extra copies you could farm (0-1023). The planner counts them whether you feed or merge them.",
        };
        _planFodderRank = new ComboBox
        {
            ItemsSource = Enumerable.Range(0, MoteValues.MaxRank).Select(r => $"+{r}").ToArray(),
            SelectedIndex = 0,
            MinWidth = 56,
            Margin = new Thickness(4, 0, 10, 0),
            ToolTip = "The rank of the copies you plan to farm.",
        };

        var nameRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(_planKind, Dock.Left);
        nameRow.Children.Add(_planKind);
        nameRow.Children.Add(_planName);

        var optionsRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        optionsRow.Children.Add(PlanLabel("Current"));
        optionsRow.Children.Add(_planRank);
        optionsRow.Children.Add(PlanLabel("Points"));
        optionsRow.Children.Add(_planPoints);
        var resourcesRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        resourcesRow.Children.Add(_planUseMotes);
        resourcesRow.Children.Add(_planUseCopies);

        var feedRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        feedRow.Children.Add(_planFeed);
        feedRow.Children.Add(_planFeedUsable);
        _planFeedRow = feedRow;

        // Each label stays with its input so the row never wraps between them.
        var fodderRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        fodderRow.Children.Add(PlanGroup("Copy level to farm", _planFodderRank));
        fodderRow.Children.Add(PlanGroup("Extra copies already planned", _planFodder));
        _planFodderRow = fodderRow;

        _planSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        _planSummary.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        _planCopyControls = new StackPanel();
        _planCopyEditor = new Expander
        {
            Header = "What if: change spare copy ranks",
            IsExpanded = true,
            Content = _planCopyControls,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Only changes this plan. Your inventory and recorded item upgrades are never changed.",
        };
        var top = new StackPanel
        {
            Children =
            {
                nameRow, _planInventoryName, optionsRow,
                new Expander
                {
                    Header = "Resources and feeding (shared settings)",
                    Content = new StackPanel { Children = { resourcesRow, feedRow } },
                    Margin = new Thickness(0, 4, 0, 6),
                },
            },
        };
        DockPanel.SetDock(top, Dock.Top);

        _planHost = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _planHost,
        };
        var tabBody = new DockPanel();
        var tabTop = new StackPanel { Children = { fodderRow, _planCopyEditor, _planSummary } };
        DockPanel.SetDock(tabTop, Dock.Top);
        tabBody.Children.Add(tabTop);
        tabBody.Children.Add(scroll);
        _planTabs = CreatePlanTabs(tabBody, RefreshMotePlan);
        _planFarmTab = (TabItem)_planTabs.Items[1];
        _planWhatIfTab = (TabItem)_planTabs.Items[2];
        var body = new DockPanel { Margin = new Thickness(2, 4, 2, 2) };
        body.Children.Add(top);
        body.Children.Add(_planTabs);

        _planKind.SelectionChanged += (_, _) =>
        {
            // An item name left over from Equipment mode is not a spell, so start the spell blank.
            if (_planKind.SelectedIndex == 1 && _planName.Text.Trim().Length > 0 &&
                CopyRanks(_planName.Text.Trim()).Count > 0)
            {
                _planName.Text = "";
            }

            RefreshMotePlan();
        };
        _planInventoryName.SelectionChanged += (_, _) =>
        {
            if (!_refreshingPlanNames && _planKind.SelectedIndex == 0 && _planInventoryName.SelectedItem is string name)
            {
                _planName.Text = name;
                SelectBestCopy(name);
                RefreshMotePlan();
            }
        };
        _planName.TextChanged += (_, _) => RefreshMotePlan();
        _planRank.SelectionChanged += (_, _) => RefreshMotePlan();
        _planPoints.TextChanged += (_, _) => RefreshMotePlan();
        _planUseMotes.Click += (_, _) => RefreshMotePlan();
        _planUseCopies.Click += (_, _) => RefreshMotePlan();
        _planFeed.Click += (_, _) => RefreshMotePlan();
        _planFeedUsable.Click += (_, _) => RefreshMotePlan();
        _planFodder.TextChanged += (_, _) => RefreshMotePlan();
        _planFodderRank.SelectionChanged += (_, _) => RefreshMotePlan();

        panel.SetBody(body);
        RefreshMotePlan();
    }

    internal static TabControl CreatePlanTabs(FrameworkElement content, Action refresh)
    {
        var tabs = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
        foreach (string header in new[] { "Upgrade", "Farm copies", "What if", "Math" })
        {
            tabs.Items.Add(new TabItem { Header = header });
        }
        tabs.SelectedIndex = 0;
        ((TabItem)tabs.Items[0]).Content = content;
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs)
            {
                return;
            }

            foreach (TabItem tab in tabs.Items)
            {
                tab.Content = null;
            }
            if (tabs.SelectedItem is TabItem selected)
            {
                selected.Content = content;
            }
            refresh();
        };
        return tabs;
    }

    private static TextBlock PlanLabel(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        return label;
    }

    private static StackPanel PlanGroup(string label, FrameworkElement input)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal };
        group.Children.Add(PlanLabel(label));
        group.Children.Add(input);
        return group;
    }

    /// <summary>Re-plans when the owned motes, inventory export or character change (called from housekeeping).</summary>
    private void CheckMotePlan()
    {
        if (_planPanel is null || !IsShowing("moteplan"))
        {
            return;
        }

        string signature = _characterName + "|" + string.Join(",", OwnedMotesByRank().OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
        if (signature != _planSignature || !ReferenceEquals(_planNamesSource, _inventoryRows))
        {
            RefreshMotePlan();
        }
    }

    private Dictionary<int, int> OwnedMotesByRank()
    {
        var owned = new Dictionary<int, int>();
        foreach (var item in _questInventory.Items)
        {
            if (!item.Character.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MoteTiers.TierOf(ItemNames.Normalize(item.Item)) is string tier && MoteValues.RankOf(tier) is int rank && item.Owned > 0)
            {
                owned[rank] = owned.GetValueOrDefault(rank) + item.Owned;
            }
        }

        return owned;
    }

    /// <summary>This character's loose export rows (no collections or installed augments), or none if the export is for someone else.</summary>
    private IReadOnlyList<InventoryExportItem> PlanInventoryRows()
    {
        bool current = _inventoryRowsCharacter is not null &&
            _inventoryRowsCharacter.Equals(_characterName, StringComparison.OrdinalIgnoreCase);
        if (!current)
        {
            return [];
        }

        return _inventoryRows.Where(i => !i.Collection && !i.Installed).ToArray();
    }

    /// <summary>The +rank of every copy of <paramref name="baseName"/> (one entry per item, so stacks count each copy).</summary>
    private List<int> CopyRanks(string baseName)
    {
        var ranks = new List<int>();
        foreach (var row in PlanInventoryRows())
        {
            var (name, rank) = InventoryExport.SplitRank(row.Name);
            if (name.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            {
                ranks.AddRange(Enumerable.Repeat(rank, Math.Max(1, row.Count)));
            }
        }

        ranks.Sort();
        return ranks;
    }

    private void SelectBestCopy(string baseName)
    {
        if (_planRank is null || _planPoints is null)
        {
            return;
        }

        var ranks = CopyRanks(baseName);
        if (ranks.Count > 0)
        {
            _planRank.SelectedIndex = Math.Min(ranks[^1], MoteValues.MaxRank);
            _planPoints.Text = "0";
        }
    }

    private void RefreshHypotheticalCopies(string name, int startRank, List<int> copies, bool equipment)
    {
        if (_planCopyEditor is null || _planCopyControls is null)
        {
            return;
        }

        _planCopyEditor.Visibility = equipment && _planTabs?.SelectedIndex == 2 && copies.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        _planCopyEditor.IsEnabled = _planUseCopies?.IsChecked == true;
        string context = $"{_characterName}|{name.ToUpperInvariant()}|{startRank}|{equipment}";
        if (_planCopyContext == context && ReferenceEquals(_planCopySource, _inventoryRows) &&
            _planOriginalCopies.SequenceEqual(copies))
        {
            return;
        }

        _planCopyContext = context;
        _planCopySource = _inventoryRows;
        _planOriginalCopies = [.. copies];
        _planHypotheticalCopies.Clear();
        _planHypotheticalCopies.AddRange(copies);
        _planCopyControls.Children.Clear();
        var explanation = new TextBlock
        {
            Text = $"Keep the main item at +{startRank}; change only the spare copies below. " +
                "Hypothetical ranks cost nothing in this simulation. Feeding still follows your selected options.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
        };
        _planCopyControls.Children.Add(explanation);
        for (int index = 0; index < copies.Count; index++)
        {
            int copyIndex = index;
            var rank = new ComboBox
            {
                ItemsSource = Enumerable.Range(0, startRank + 1).Select(value => $"+{value}").ToArray(),
                SelectedIndex = copies[index],
                MinWidth = 60,
                Margin = new Thickness(4, 0, 0, 0),
            };
            rank.SelectionChanged += (_, _) =>
            {
                _planHypotheticalCopies[copyIndex] = rank.SelectedIndex;
                RefreshMotePlan();
            };
            _planCopyControls.Children.Add(PlanGroup($"Copy {index + 1} (export +{copies[index]})", rank));
        }

        var reset = new Button { Content = "Reset to inventory ranks", Margin = new Thickness(0, 4, 0, 0) };
        reset.Click += (_, _) =>
        {
            _planCopyContext = "";
            RefreshMotePlan();
        };
        _planCopyControls.Children.Add(reset);
    }

    private void RefreshPlanNames(UpgradeKind kind)
    {
        if (_planName is null || _planInventoryName is null ||
            (ReferenceEquals(_planNamesSource, _inventoryRows) && _planNamesKind == kind && _planNameFilter == _planName.Text.Trim()))
        {
            return;
        }

        _planNamesSource = _inventoryRows;
        _planNamesKind = kind;
        _planNameFilter = _planName.Text.Trim();
        // The inventory export only lists items, so spells are typed in by name.
        var names = kind == UpgradeKind.Spell
            ? Array.Empty<string>()
            : PlanInventoryRows()
                .Select(r => InventoryExport.SplitRank(r.Name).BaseName)
                .Where(n => MoteTiers.TierOf(n) is null)
                .Where(n => n.Contains(_planNameFilter, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        _refreshingPlanNames = true;
        try
        {
            _planInventoryName.ItemsSource = names;
            _planInventoryName.SelectedItem = names.FirstOrDefault(name =>
                name.Equals(_planNameFilter, StringComparison.OrdinalIgnoreCase));
            _planInventoryName.IsEnabled = names.Length > 0;
            _planInventoryName.Visibility = kind == UpgradeKind.Equipment ? Visibility.Visible : Visibility.Collapsed;
            _planInventoryName.ToolTip = names.Length == 0
                ? "No matching inventory items. Type a name above to plan manually, or clear the search to see all exported items."
                : "Pick a matching inventory item to set its name and best-copy rank.";
        }
        finally
        {
            _refreshingPlanNames = false;
        }
    }

    private void RefreshMotePlan()
    {
        if (_planHost is null || _planSummary is null || _planKind is null || _planName is null || _planRank is null ||
            _planPoints is null || _planUseMotes is null || _planUseCopies is null)
        {
            return;
        }

        var kind = _planKind.SelectedIndex == 1 ? UpgradeKind.Spell : UpgradeKind.Equipment;
        RefreshPlanNames(kind);
        bool equipment = kind == UpgradeKind.Equipment;
        int activeTab = _planTabs?.SelectedIndex ?? 0;
        if (_planFarmTab is not null && _planWhatIfTab is not null)
        {
            _planFarmTab.Visibility = equipment ? Visibility.Visible : Visibility.Collapsed;
            _planWhatIfTab.Visibility = equipment ? Visibility.Visible : Visibility.Collapsed;
            if (!equipment && activeTab is 1 or 2 && _planTabs is not null)
            {
                _planTabs.SelectedIndex = 0;
                return;
            }
        }
        var equipmentOnly = equipment ? Visibility.Visible : Visibility.Collapsed;
        _planUseCopies.Visibility = equipmentOnly;
        if (_planFeedRow is not null && _planFodderRow is not null)
        {
            _planFeedRow.Visibility = equipmentOnly;
            _planFodderRow.Visibility = equipment && activeTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        _planName.ToolTip = equipment
            ? "Type an item name to plan or search the separate inventory dropdown below. Clear the text to see all exported items."
            : "Type the spell name.";
        if (_planFeed is not null && _planFeedUsable is not null)
        {
            _planFeedUsable.IsEnabled = _planFeed.IsChecked == true;
        }

        bool fodderOk = int.TryParse(_planFodder?.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int fodder) &&
            fodder <= 1023;
        if (_planFodder is null || _planFodder.Text.Trim().Length == 0)
        {
            fodder = 0;
            fodderOk = true;
        }

        int fodderRank = Math.Clamp(_planFodderRank?.SelectedIndex ?? 0, 0, MoteValues.MaxRank - 1);
        FeedOptions? feed = equipment
            ? new FeedOptions(_planFeed?.IsChecked == true, _planFeedUsable?.IsChecked == true,
                fodderOk && activeTab == 1 ? fodder : 0, fodderRank)
            : null;
        int startRank = Math.Max(0, _planRank.SelectedIndex);
        int maxPoints = startRank >= MoteValues.MaxRank ? 0 : MoteValues.PointsToNext(startRank) - 1;
        _planPoints.IsEnabled = startRank < MoteValues.MaxRank;
        bool pointsOk = int.TryParse(_planPoints.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int points) &&
            points <= maxPoints;
        if (_planPoints.Text.Trim().Length == 0)
        {
            points = 0;
            pointsOk = true;
        }

        var owned = OwnedMotesByRank();
        _planSignature = _characterName + "|" + string.Join(",", owned.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
        string name = _planName.Text.Trim();
        var copies = new List<int>();
        bool removedTarget = false;
        var higherCopies = new List<int>();
        if (kind == UpgradeKind.Equipment && name.Length > 0)
        {
            copies = CopyRanks(name);
            // One copy at the current rank is the item being upgraded, not a spare.
            removedTarget = copies.Remove(startRank);
            higherCopies = copies.Where(c => c > startRank).ToList();
            copies = copies.Where(c => c <= startRank).ToList();
        }

        RefreshHypotheticalCopies(name, startRank, copies, equipment);
        bool hypothetical = equipment && activeTab == 2 && _planUseCopies.IsChecked == true &&
            !_planHypotheticalCopies.SequenceEqual(copies);
        var spare = _planUseCopies.IsChecked == true
            ? (activeTab == 2 ? _planHypotheticalCopies.ToList() : copies)
            : [];
        var plannerMotes = _planUseMotes.IsChecked == true ? owned : new Dictionary<int, int>();
        var plan = MoteUpgradePlanner.Plan(kind, startRank, pointsOk ? points : 0, plannerMotes, spare, feed);

        var notes = new List<string>();
        if (hypothetical)
        {
            int originalPoints = copies.Sum(MoteValues.CopyPoints);
            int hypotheticalPoints = spare.Sum(MoteValues.CopyPoints);
            notes.Add($"WHAT IF: spare merge value {originalPoints:N0} to {hypotheticalPoints:N0} points " +
                $"before feeding; main item stays +{startRank}. Inventory is unchanged.");
        }
        if (!pointsOk)
        {
            notes.Add($"Points must be a whole number from 0 to {maxPoints} at +{startRank}; using 0.");
        }

        if (!fodderOk && activeTab == 1)
        {
            notes.Add("Extra copies must be a whole number from 0 to 1023; using 0.");
        }

        if (kind == UpgradeKind.Equipment)
        {
            if (_inventoryRowsCharacter is null || !_inventoryRowsCharacter.Equals(_characterName, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("No inventory export for this character yet, so no copies are counted. Run /outputfile inventory in game.");
            }
            else if (name.Length > 0)
            {
                notes.Add(copies.Count == 0
                    ? (removedTarget ? "No spare copies in inventory." : "No copies of this item in inventory.")
                    : $"{copies.Count} spare cop{(copies.Count == 1 ? "y" : "ies")} in inventory: {string.Join(", ", copies.Select(c => $"+{c}"))}" +
                        (removedTarget ? $" (one +{startRank} is the item being upgraded)." : "."));
                if (higherCopies.Count > 0)
                {
                    notes.Add($"You already have a +{higherCopies.Max()} copy, so it isn't counted as a spare. Set Current to +{higherCopies.Max()} to plan from it.");
                }
            }

            if (feed is { Enabled: true } && plan.Baseline is not null && plan.FedCopies.Count == 0)
            {
                notes.Add("Feeding copies wouldn't save anything here, so it isn't used.");
            }

            _planSummary.ToolTip = "Each step uses its matching mote tier. Bigger motes are saved until a higher rank needs them. " +
                "Spare copies are merged after motes. Condensing and Void-Touched motes are not used.";
        }
        else
        {
            _planSummary.ToolTip = "Spell motes double in value each tier (1 to 512 points) and work at any +level. " +
                "Smallest motes are spent first. Condensing and Void-Touched motes are not used.";
        }

        _planSummary.Text = string.Join(Environment.NewLine, notes);
        _planSummary.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _planHost.Children.Clear();
        double fontSize = _planPanel?.FontSize ?? 13;
        if (plan.Steps.Count == 0)
        {
            _planHost.Children.Add(new TextBlock { Text = "Already +10.", Margin = new Thickness(2, 6, 0, 0) });
            return;
        }

        if (activeTab is 0 or 2)
        {
            AddPlanGuide(plan, startRank);
            return;
        }

        if (activeTab == 1 && feed is not null)
        {
            AddGuideHeading($"How many +{fodderRank} copies should I farm?");
            AddGuideLine($"Your item starts at +{startRank}. Pick the level you want:", muted: true);
            for (int target = startRank + 1; target <= MoteValues.MaxRank; target++)
            {
                int additional = MoteUpgradePlanner.MinimumAdditionalCopiesToFinish(startRank, pointsOk ? points : 0,
                    plannerMotes, spare, feed, targetRank: target);
                AddGuideLine(FarmTargetSummary(target, fodderRank, additional), bold: true);
            }

            AddGuideLine("Each number is the TOTAL to farm from where you are now. Do not add the numbers together.",
                muted: true);
            AddGuideLine("These numbers use the motes, spare copies and progress selected above.", muted: true);
            if (feed.FodderCopies > 0)
            {
                AddGuideLine($"The {feed.FodderCopies:N0} extra copies you entered are already counted. " +
                    "Only copies beyond those are listed here.", muted: true);
            }
            if (feed.Enabled)
            {
                AddGuideLine("Feeding is on: the plan may spend your motes to raise farmed copies before merging them.",
                    muted: true);
            }

            int copyPoints = MoteValues.CopyPoints(fodderRank);
            var copiesOnly = new StackPanel();
            copiesOnly.Children.Add(new TextBlock
            {
                Text = $"No motes, no spare copies, no feeding, and no saved progress. " +
                    $"Just merge +{fodderRank} copies for ONE level at a time:",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 0, 4),
            });
            for (int rank = startRank; rank < MoteValues.MaxRank; rank++)
            {
                int count = (MoteValues.PointsToNext(rank) + copyPoints - 1) / copyPoints;
                copiesOnly.Children.Add(new TextBlock
                {
                    Text = $"+{rank} to +{rank + 1}: {count:N0} cop{(count == 1 ? "y" : "ies")} at +{fodderRank}.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 4, 0, 4),
                });
            }
            _planHost.Children.Add(new Expander
            {
                Header = "Compare: copies only, one level at a time",
                Content = copiesOnly,
                Margin = new Thickness(2, 8, 0, 6),
            });
            return;
        }

        AddGuideHeading("The numbers behind the Upgrade tab");
        var details = new StackPanel();
        _planHost.Children.Add(details);

        if (plan.FedCopies.Count > 0)
        {
            var heading = new TextBlock
            {
                Text = "Feed copies",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 6, 0, 0),
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "NightText");
            details.Children.Add(heading);
            var feedRows = plan.FedCopies
                .Select(f => (IReadOnlyList<string>)
                [
                    f.IsFodder ? $"new +{f.StartRank}" : $"+{f.StartRank}",
                    $"+{f.EndRank}",
                    DescribeUses(f.Motes),
                    f.MergePoints.ToString("N0"),
                    f.Wasted > 0 ? f.Wasted.ToString("N0") : "",
                ])
                .ToArray();
            var feedTable = OverlayPanel.BuildTable(new OverlayTable(["Copy", "To", "Feed", "Merge pts", "Wasted"], feedRows), fontSize);
            feedTable.ToolTip = "Feed each copy these motes, using only the tier that matches each rank, then merge it into the main item. " +
                "Wasted: points past the copy's last rank, which merging doesn't pass on.";
            details.Children.Add(feedTable);
            var stepsHeading = new TextBlock
            {
                Text = "Upgrade the main item",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 8, 0, 0),
            };
            stepsHeading.SetResourceReference(TextBlock.ForegroundProperty, "NightText");
            details.Children.Add(stepsHeading);
        }

        var rows = plan.Steps
            .Select(s => (IReadOnlyList<string>)
            [
                $"+{s.FromRank} → +{s.ToRank}",
                s.Needed.ToString("N0"),
                s.StartPoints.ToString("N0"),
                DescribeUses(s.Used),
                s.Missing > 0 ? $"{s.MissingTier} ×{s.Missing}" : "",
                s.CarryAfter.ToString("N0"),
            ])
            .ToArray();
        var table = new OverlayTable(["Step", "Need", "Have", "Use", "Missing", "Carry"], rows);
        var steps = OverlayPanel.BuildTable(table, fontSize);
        steps.ToolTip = "Need: points for the step. Have: points carried in. Use: owned motes and spare copies spent. " +
            "Missing: motes still to get. Carry: extra points that roll into the next step.";
        details.Children.Add(steps);

        var totals = new List<(string, string)>
        {
            ("Owned motes used", DescribeCounts(plan.MotesUsed, r => MoteValues.Tiers[r - 1])),
        };
        if (kind == UpgradeKind.Equipment)
        {
            totals.Add(("Copies merged", DescribeCounts(plan.CopiesUsed, r => $"+{r}")));
        }

        totals.Add(("Still missing", DescribeCounts(plan.Missing, r => MoteValues.Tiers[r - 1])));
        if (plan.FedCopies.Count > 0)
        {
            var saved = plan.SavedVersusBaseline;
            var fewer = saved.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            var more = saved.Where(kv => kv.Value < 0).ToDictionary(kv => kv.Key, kv => -kv.Value);
            totals.Add(("Feeding saves", DescribeCounts(fewer, r => MoteValues.Tiers[r - 1])));
            totals.Add(("…by spending", DescribeCounts(more, r => MoteValues.Tiers[r - 1])));
            int fodderUsed = plan.FedCopies.Count(f => f.IsFodder);
            if (fodderUsed > 0)
            {
                totals.Add(("Extra copies fed", fodderUsed.ToString("N0")));
            }

            int wasted = plan.FedCopies.Sum(f => f.Wasted);
            if (wasted > 0)
            {
                totals.Add(("Points wasted feeding", wasted.ToString("N0")));
            }
        }
        totals.Add(("Points to +10", plan.Steps.Sum(s => s.Needed).ToString("N0") +
            (startRank > 0 || points > 0 ? $" (from +{startRank}, {(pointsOk ? points : 0):N0} in)" : "")));
        details.Children.Add(OverlayPanel.BuildTable(OverlayTable.Pairs(totals), fontSize));
    }

    /// <summary>Adds the plain-language "What to do" list above the detailed tables.</summary>
    private void AddPlanGuide(UpgradePlan plan, int startRank)
    {
        if (_planHost is null)
        {
            return;
        }

        bool equipment = plan.Kind == UpgradeKind.Equipment;
        string thing = equipment ? "item" : "spell";
        int total = plan.Steps.Sum(s => s.Needed) - plan.Steps[0].StartPoints;
        AddGuideHeading("What to do");
        AddGuideLine($"Your {thing} is +{startRank}. It needs {total:N0} more points to reach +10.", muted: true);

        int number = 1;
        if (plan.FedCopies.Count > 0)
        {
            AddGuideLine($"{number}. Level up your spare copies first. Small motes stop working on your item once it's too high, " +
                "but a lower copy can still use them, and a higher copy is worth more when merged.");
            number++;
            var groups = plan.FedCopies
                .GroupBy(f => (f.IsFodder, f.StartRank, f.EndRank, Feed: DescribeUses(f.Motes)))
                .OrderBy(g => g.Key.StartRank)
                .ThenBy(g => g.Key.EndRank);
            foreach (var group in groups)
            {
                int count = group.Count();
                string copy = group.Key.IsFodder
                    ? $"new +{group.Key.StartRank} item"
                    : $"+{group.Key.StartRank} item";
                string who = count == 1 ? $"One {copy}" : $"{count} {copy}s";
                string each = count == 1 ? "" : " each";
                AddGuideLine($"• {who}: feed {group.Key.Feed}{each} → +{group.Key.EndRank}", indent: true);
            }
        }

        AddGuideLine($"{number}. Level up your {thing}:");
        foreach (var step in plan.Steps)
        {
            var parts = new List<string>();
            var motes = step.Used.Where(u => !u.IsCopy).ToList();
            var copiesUsed = step.Used.Where(u => u.IsCopy).ToList();
            int copyPoints = copiesUsed.Sum(u => MoteValues.CopyPoints(u.Rank) * u.Count);
            if (motes.Count > 0 && copiesUsed.Count > 0 && step.StartPoints + copyPoints >= step.Needed)
            {
                // The copies alone finish this rank; the motes only go in first because they stop working after it.
                int motePoints = motes.Sum(u => MoteValues.ItemPoints(u.Rank) * u.Count);
                string copyText = DescribeCopies(copiesUsed);
                AddGuideLine($"• +{step.FromRank} → +{step.ToRank}: merge {copyText} (that alone makes +{step.ToRank}). " +
                    $"Use your {DescribeUses(motes)} first anyway: they stop working at +{step.ToRank}, " +
                    $"so their {motePoints:N0} points carry into +{step.ToRank} → +{step.ToRank + 1}", indent: true);
                continue;
            }

            if (motes.Count > 0)
            {
                parts.Add($"use your {DescribeUses(motes)}");
            }

            if (copiesUsed.Count > 0)
            {
                parts.Add("merge " + DescribeCopies(copiesUsed));
            }

            if (step.Missing > 0)
            {
                parts.Add($"add {step.MissingTier} ×{step.Missing} you don't have yet");
            }

            if (parts.Count == 0)
            {
                parts.Add("leftover points cover it");
            }

            AddGuideLine($"• +{step.FromRank} → +{step.ToRank}: {string.Join(", then ", parts)}", indent: true);
        }

        var missingSteps = plan.Steps.Where(s => s.Missing > 0).ToList();
        if (missingSteps.Count > 0)
        {
            var tiers = missingSteps.Select(s => s.MissingTier).Distinct().ToList();
            string why;
            if (!equipment)
            {
                why = $"Why {string.Join(", ", tiers)}? One of that mote is exactly what each step needs, so nothing is wasted.";
            }
            else if (missingSteps.Count == 1)
            {
                var step = missingSteps[0];
                why = step.FromRank == MoteValues.MaxRank - 1
                    ? $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the only one that still works on a +{step.FromRank} item."
                    : $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the smallest one that still works on a +{step.FromRank} item.";
            }
            else
            {
                why = "Why these motes? Motes stop working once an item gets too high, so each step asks for the smallest one that still works: " +
                    string.Join(", ", missingSteps.Select(s => $"+{s.FromRank} needs {s.MissingTier}")) + ".";
            }

            AddGuideLine(why, muted: true);
            AddGuideLine("If you finish with motes, get: " + DescribeCounts(plan.Missing, r => MoteValues.Tiers[r - 1]), bold: true);
        }
        else
        {
            AddGuideLine("You already have everything you need!", bold: true);
        }

        if (plan.FedCopies.Count > 0)
        {
            var saved = plan.SavedVersusBaseline;
            var fewer = saved.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            var more = saved.Where(kv => kv.Value < 0).ToDictionary(kv => kv.Key, kv => -kv.Value);
            if (fewer.Count > 0)
            {
                string instead = more.Count > 0 ? $" by using {DescribeCounts(more, r => MoteValues.Tiers[r - 1])} instead" : "";
                AddGuideLine($"Leveling copies saves you {DescribeCounts(fewer, r => MoteValues.Tiers[r - 1])}{instead}.", muted: true);
            }

        }
    }

    private void AddGuideHeading(string text)
    {
        var heading = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 6, 0, 2),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "NightText");
        _planHost?.Children.Add(heading);
    }

    internal static string FarmTargetSummary(int targetRank, int farmRank, int additional)
    {
        return additional == 0
            ? $"Reach +{targetRank}: no more copies needed."
            : $"Reach +{targetRank}: farm {additional:N0} more +{farmRank} cop{(additional == 1 ? "y" : "ies")}.";
    }

    private void AddGuideLine(string text, bool indent = false, bool muted = false, bool bold = false)
    {
        var line = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent ? 16 : 2, 2, 0, 2),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, muted ? "NightMuted" : "NightText");
        _planHost?.Children.Add(line);
    }

    private static string DescribeCopies(IReadOnlyList<UpgradeUse> copies)
    {
        int total = copies.Sum(u => u.Count);
        string list = string.Join(", ", copies.Select(u => u.Count > 1 ? $"{u.Count} × +{u.Rank}" : $"+{u.Rank}"));
        return total == 1 ? $"your {list} copy" : $"your copies {list}";
    }

    private static string DescribeUses(IReadOnlyList<UpgradeUse> used)
    {
        return string.Join(", ", used.Select(u => u.IsCopy
            ? $"copy +{u.Rank}{(u.Count > 1 ? $" ×{u.Count}" : "")}"
            : $"{MoteValues.Tiers[u.Rank - 1]} ×{u.Count}"));
    }

    private static string DescribeCounts(IReadOnlyDictionary<int, int> counts, Func<int, string> name)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{name(kv.Key)} ×{kv.Value:N0}"));
    }
}
