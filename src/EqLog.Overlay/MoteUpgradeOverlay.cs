using System.Windows;
using System.Windows.Controls;
using EqLog.Application.Motes;
using EqLog.Core;

namespace EqLog.Overlay;

/// <summary>
/// The "Mote upgrade planner" overlay: pick an item or spell, its current +rank, and a target up to +10,
/// displaying typed upgrade calculations with presentation-owned wording and layout.
/// </summary>
public partial class MainWindow
{
    private ComboBox? _planKind;
    private TextBox? _planName;
    private ComboBox? _planInventoryName;
    private string? _planNameFilter;
    private bool _refreshingPlanNames;
    private ComboBox? _planRank;
    private ComboBox? _planTargetRank;
    private bool _refreshingPlanTarget;
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
    private MoteUpgradeKind? _planNamesKind;
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
            ItemsSource = MotePlanningQueries.Ranks.Select(rank => $"+{rank}").ToArray(),
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
        _planTargetRank = new ComboBox
        {
            ItemStringFormat = "+{0}",
            MinWidth = 56,
            Margin = new Thickness(4, 0, 10, 0),
            ToolTip = "Stop planning at this rank. Applies to Upgrade, Farm copies, What if, Math, and spare-copy feeding.",
        };
        UpdatePlanTargetRanks(_planTargetRank, 0);
        _planUseMotes = new CheckBox
        {
            Content = "Use owned motes",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Spend the motes confirmed by your last inventory export before listing missing ones. Run /outputfile inventory after looting or spending motes.",
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
            IsChecked = false,
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
            ItemsSource = MotePlanningQueries.FodderRanks.Select(rank => $"+{rank}").ToArray(),
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
        optionsRow.Children.Add(PlanGroup("Stop at", _planTargetRank));
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
                nameRow, _planInventoryName, optionsRow, resourcesRow, feedRow,
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        _planHost = new StackPanel();
        var tabTop = new StackPanel { Children = { fodderRow, _planCopyEditor, _planSummary } };
        var tabBody = CreatePlanScrollBody(tabTop, _planHost);
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
                MotePlanningQueries.ContainsTarget(PlanInventoryRows(), _planName.Text.Trim()))
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
        _planName.TextChanged += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planRank.SelectionChanged += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planTargetRank.SelectionChanged += (_, _) =>
        {
            if (!_refreshingPlanTarget)
            {
                RefreshMotePlan();
            }
        };
        _planPoints.TextChanged += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planUseMotes.Click += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planUseCopies.Click += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planFeed.Click += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planFeedUsable.Click += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planFodder.TextChanged += (_, _) =>
        {
            RefreshMotePlan();
        };
        _planFodderRank.SelectionChanged += (_, _) =>
        {
            RefreshMotePlan();
        };

        panel.SetBody(body);
        RefreshMotePlan();
    }

    internal static ScrollViewer CreatePlanScrollBody(FrameworkElement controls, FrameworkElement results)
    {
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { controls, results } },
        };
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

    private IReadOnlyDictionary<int, int> OwnedMotesByRank()
    {
        return MoteInventoryReadModel.Capture(_questInventory.Items, _characterName).OwnedByRank;
    }

    /// <summary>All export rows for this character, or none if the export is for someone else.</summary>
    private IReadOnlyList<InventoryExportItem> PlanInventoryRows()
    {
        bool current = _inventoryRowsCharacter is not null &&
            _inventoryRowsCharacter.Equals(_characterName, StringComparison.OrdinalIgnoreCase);
        if (!current)
        {
            return [];
        }

        return _inventoryRows;
    }

    internal static List<int> PlanCopyRanks(IEnumerable<InventoryExportItem> rows, string baseName)
    {
        return MotePlanningQueries.CopyRanks(rows, baseName);
    }

    private void SelectBestCopy(string baseName)
    {
        if (_planRank is null || _planPoints is null)
        {
            return;
        }

        int? rank = MotePlanningQueries.BestCopyRank(PlanInventoryRows(), baseName);
        if (rank is not null)
        {
            _planRank.SelectedIndex = rank.Value;
            _planPoints.Text = "0";
        }
    }

    internal static int? PlanTargetRank(IEnumerable<InventoryExportItem> rows, string baseName)
    {
        return MotePlanningQueries.TargetRank(rows, baseName);
    }

    internal static string[] PlanTargetNames(IEnumerable<InventoryExportItem> rows, string filter)
    {
        return MotePlanningQueries.TargetNames(rows, filter);
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
            var rank = CreateHypotheticalCopyRankSelector(copies[index]);
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

    internal static ComboBox CreateHypotheticalCopyRankSelector(int exportedRank)
    {
        return new ComboBox
        {
            ItemsSource = MotePlanningQueries.Ranks.Select(rank => $"+{rank}").ToArray(),
            SelectedIndex = exportedRank,
            MinWidth = 60,
            Margin = new Thickness(4, 0, 0, 0),
        };
    }

    private void RefreshPlanNames(MoteUpgradeKind kind)
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
        var names = kind == MoteUpgradeKind.Spell
            ? Array.Empty<string>()
            : PlanTargetNames(PlanInventoryRows(), _planNameFilter);
        _refreshingPlanNames = true;
        try
        {
            _planInventoryName.ItemsSource = names;
            _planInventoryName.SelectedItem = names.FirstOrDefault(name =>
                name.Equals(_planNameFilter, StringComparison.OrdinalIgnoreCase));
            _planInventoryName.IsEnabled = names.Length > 0;
            _planInventoryName.Visibility = kind == MoteUpgradeKind.Equipment ? Visibility.Visible : Visibility.Collapsed;
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
            _planPoints is null || _planUseMotes is null || _planUseCopies is null || _planTargetRank is null)
        {
            return;
        }

        var kind = _planKind.SelectedIndex == 1 ? MoteUpgradeKind.Spell : MoteUpgradeKind.Equipment;
        RefreshPlanNames(kind);
        bool equipment = kind == MoteUpgradeKind.Equipment;
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

        int startRank = MotePlanningQueries.CurrentRank(_planRank.SelectedIndex);
        _refreshingPlanTarget = true;
        int targetRank;
        try
        {
            targetRank = UpdatePlanTargetRanks(_planTargetRank, startRank);
        }
        finally
        {
            _refreshingPlanTarget = false;
        }

        _planPoints.IsEnabled = MotePlanningQueries.CanAddProgress(startRank);
        var owned = OwnedMotesByRank();
        _planSignature = _characterName + "|" + string.Join(",", owned.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
        string name = _planName.Text.Trim();
        var inventory = PlanInventoryRows();
        var copies = equipment ? MotePlanningQueries.SelectCopies(inventory, name, startRank).Spares.ToList() : [];
        RefreshHypotheticalCopies(name, startRank, copies, equipment);
        var request = new MotePlanningRequest(kind, startRank, targetRank,
            _planPoints.Text, _planFodder?.Text ?? "", _planFodderRank?.SelectedIndex ?? 0,
            _planFeed?.IsChecked == true, _planFeedUsable?.IsChecked == true,
            _planUseMotes.IsChecked == true, _planUseCopies.IsChecked == true,
            (MotePlanPage)activeTab, name,
            _inventoryRowsCharacter is not null && _inventoryRowsCharacter.Equals(_characterName, StringComparison.OrdinalIgnoreCase),
            _planHypotheticalCopies.ToArray());
        var model = MotePlanningService.Prepare(request, owned, inventory);
        _planSummary.ToolTip = PlanSummaryToolTip(model.Kind);
        _planSummary.Text = PlanSummary(model, request.Page);
        _planSummary.Visibility = _planSummary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _planHost.Children.Clear();
        RenderMotePlan(model, _planHost, _planPanel?.FontSize ?? 13, request.Page);
    }

    internal static int UpdatePlanTargetRanks(ComboBox selector, int currentRank)
    {
        var model = MotePlanningQueries.TargetRanks(currentRank, selector.SelectedItem is int selected ? selected : null);
        if (selector.ItemsSource is not IReadOnlyList<int> existing || !existing.SequenceEqual(model.Ranks))
        {
            selector.ItemsSource = model.Ranks;
        }

        selector.SelectedItem = model.Selected;
        return model.Selected;
    }

    internal static string FarmTargetSummary(int targetRank, int farmRank, int additional)
    {
        return additional == 0
            ? $"Reach +{targetRank}: no more copies needed."
            : $"Reach +{targetRank}: farm {additional:N0} more +{farmRank} cop{(additional == 1 ? "y" : "ies")}.";
    }

    internal static string PlanSummaryToolTip(MoteUpgradeKind kind)
    {
        return kind == MoteUpgradeKind.Equipment
            ? "Each step uses its matching mote tier. Bigger motes are saved until a higher rank needs them. " +
                "Spare copies are merged after motes. Condensing and Void-Touched motes are not used."
            : "Spell motes double in value each tier (1 to 512 points) and work at any +level. " +
                "Smallest motes are spent first. Condensing and Void-Touched motes are not used.";
    }

    internal static string PlanSummary(MotePlanReadModel model, MotePlanPage page)
    {
        var notes = new List<string>();
        if (model.WhatIf is { } hypothetical)
        {
            notes.Add($"WHAT IF: spare merge value {hypothetical.OriginalMergePoints:N0} to {hypothetical.HypotheticalMergePoints:N0} points " +
                $"before feeding; main item stays +{model.StartRank}. Inventory is unchanged.");
        }

        if (model.PointsStatus == MoteInputStatus.Invalid)
        {
            notes.Add($"Points must be a whole number from 0 to {model.MaximumProgress} at +{model.StartRank}; using 0.");
        }

        if (model.FodderStatus == MoteInputStatus.Invalid && page == MotePlanPage.Farm)
        {
            notes.Add($"Extra copies must be a whole number from 0 to {model.MaximumFodderCopies}; using 0.");
        }

        if (model.Kind == MoteUpgradeKind.Equipment)
        {
            var selection = model.CopySelection;
            var copies = selection.Spares;
            if (!model.HasInventoryExport)
            {
                notes.Add("No inventory export for this character yet, so no copies are counted. Run /outputfile inventory in game.");
            }
            else if (model.ItemName.Length > 0)
            {
                notes.Add(copies.Count == 0
                    ? (selection.RemovedTarget ? "No spare copies in inventory." : "No copies of this item in inventory.")
                    : $"{copies.Count} spare cop{(copies.Count == 1 ? "y" : "ies")} in inventory: {string.Join(", ", copies.Select(c => $"+{c}"))}" +
                        (selection.RemovedTarget ? $" (one +{model.StartRank} is the item being upgraded)." : "."));
                if (selection.HigherCopies.Count > 0)
                {
                    int highest = selection.HigherCopies.Max();
                    notes.Add($"You already have a +{highest} copy, so it isn't counted as a spare. Set Current to +{highest} to plan from it.");
                }
            }

            if (model.FeedingStatus == MoteFeedingStatus.NotBeneficial)
            {
                notes.Add("Feeding copies wouldn't save anything here, so it isn't used.");
            }
        }

        return string.Join(Environment.NewLine, notes);
    }

    private abstract record MotePlanBlock;

    private sealed record MotePlanHeading(string Text, bool Detail = false, bool Subsequent = false) : MotePlanBlock;

    private sealed record MotePlanLine(
        string Text, bool Indent = false, bool Muted = false, bool Bold = false) : MotePlanBlock;

    private sealed record MotePlanTable(
        IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<string>> Rows,
        string? ToolTip = null, bool RowLabels = false) : MotePlanBlock;

    private sealed record MotePlanComparison(string Heading, IReadOnlyList<string> Lines) : MotePlanBlock;

    private sealed record MotePlanComplete(string Text) : MotePlanBlock;

    private static void AddFarmGuide(List<MotePlanBlock> blocks, MotePlanReadModel model)
    {
        int fodderRank = model.FodderRank;
        blocks.Add(new MotePlanHeading($"How many +{fodderRank} copies should I farm?"));
        blocks.Add(new MotePlanLine($"Your item starts at +{model.StartRank}. Pick the level you want:", Muted: true));
        foreach (var target in model.FarmTargets)
        {
            blocks.Add(new MotePlanLine(FarmTargetSummary(target.TargetRank, fodderRank, target.AdditionalCopies), Bold: true));
        }

        blocks.Add(new MotePlanLine("Each number is the TOTAL to farm from where you are now. Do not add the numbers together.",
            Muted: true));
        blocks.Add(new MotePlanLine("These numbers use the motes, spare copies and progress selected above.", Muted: true));
        if (model.FodderCopies > 0)
        {
            blocks.Add(new MotePlanLine($"The {model.FodderCopies:N0} extra copies you entered are already counted. " +
                "Only copies beyond those are listed here.", Muted: true));
        }

        if (model.FeedingStatus is MoteFeedingStatus.Used or MoteFeedingStatus.NotBeneficial or MoteFeedingStatus.NotNeeded)
        {
            blocks.Add(new MotePlanLine("Feeding is on: the plan may spend your motes to raise farmed copies before merging them.",
                Muted: true));
        }

        var comparison = new List<string>
        {
            $"No motes, no spare copies, no feeding, and no saved progress. " +
                $"Just merge +{fodderRank} copies for ONE level at a time:",
        };
        foreach (var step in model.CopyOnlySteps)
        {
            comparison.Add($"+{step.FromRank} to +{step.ToRank}: {step.Copies:N0} cop{(step.Copies == 1 ? "y" : "ies")} at +{fodderRank}.");
        }

        blocks.Add(new MotePlanComparison("Compare: copies only, one level at a time", comparison.AsReadOnly()));
    }

    private static void AddMath(List<MotePlanBlock> blocks, MotePlanReadModel plan)
    {
        if (plan.FedCopies.Count > 0)
        {
            blocks.Add(new MotePlanHeading("Feed copies", Detail: true));
            var feedRows = plan.FedCopies.Select(f => (IReadOnlyList<string>)Array.AsReadOnly(new[]
            {
                f.IsFodder ? $"new +{f.StartRank}" : $"+{f.StartRank}",
                $"+{f.EndRank}",
                DescribeUses(f.Motes, plan.TierIds),
                f.MergePoints.ToString("N0"),
                f.Wasted > 0 ? f.Wasted.ToString("N0") : "",
            })).ToArray();
            blocks.Add(Table(["Copy", "To", "Feed", "Merge pts", "Wasted"], feedRows,
                "Feed each copy these motes, using only the tier that matches each rank, then merge it into the main item. " +
                "Wasted: points past the copy's last rank, which merging doesn't pass on."));
            blocks.Add(new MotePlanHeading("Upgrade the main item", Detail: true, Subsequent: true));
        }

        var rows = plan.Steps.Select(s => (IReadOnlyList<string>)Array.AsReadOnly(new[]
        {
            $"+{s.FromRank} → +{s.ToRank}",
            s.Needed.ToString("N0"),
            s.StartPoints.ToString("N0"),
            DescribeUses(s.Used, plan.TierIds),
            s.Missing > 0 ? $"{s.MissingTier} ×{s.Missing}" : "",
            s.CarryAfter.ToString("N0"),
        })).ToArray();
        blocks.Add(Table(["Step", "Need", "Have", "Use", "Missing", "Carry"], rows,
            "Need: points for the step. Have: points carried in. Use: owned motes and spare copies spent. " +
            "Missing: motes still to get. Carry: extra points that roll into the next step."));

        var totals = new List<(string, string)>
        {
            ("Owned motes used", DescribeCounts(plan.MotesUsed)),
        };
        if (plan.Kind == MoteUpgradeKind.Equipment)
        {
            totals.Add(("Copies merged", DescribeCounts(plan.CopiesUsed, copies: true)));
        }

        totals.Add(("Still missing", DescribeCounts(plan.Missing)));
        if (plan.FedCopies.Count > 0)
        {
            totals.Add(("Feeding saves", DescribeCounts(plan.FeedingSavings)));
            totals.Add(("…by spending", DescribeCounts(plan.FeedingCosts)));
            if (plan.ExtraCopiesFed > 0)
            {
                totals.Add(("Extra copies fed", plan.ExtraCopiesFed.ToString("N0")));
            }

            if (plan.FeedingWaste > 0)
            {
                totals.Add(("Points wasted feeding", plan.FeedingWaste.ToString("N0")));
            }
        }

        totals.Add(($"Points to +{plan.TargetRank}", plan.TotalRequiredPoints.ToString("N0") +
            (plan.StartRank > 0 || plan.ParsedPoints > 0 ? $" (from +{plan.StartRank}, {plan.Progress:N0} in)" : "")));
        var totalRows = totals.Select(pair => (IReadOnlyList<string>)Array.AsReadOnly(new[] { pair.Item1, pair.Item2 })).ToArray();
        blocks.Add(new MotePlanTable(null, Array.AsReadOnly(totalRows), RowLabels: true));
    }

    private static MotePlanTable Table(string[] headers, IReadOnlyList<string>[] rows, string? tip = null)
    {
        return new MotePlanTable(Array.AsReadOnly(headers), Array.AsReadOnly(rows), tip);
    }

    private static void AddGuide(List<MotePlanBlock> blocks, MotePlanReadModel plan)
    {
        bool equipment = plan.Kind == MoteUpgradeKind.Equipment;
        string thing = equipment ? "item" : "spell";
        int targetRank = plan.Steps[^1].ToRank;
        blocks.Add(new MotePlanHeading("What to do"));
        blocks.Add(new MotePlanLine($"Your {thing} is +{plan.StartRank}. It needs {plan.RemainingPoints:N0} more points to reach +{targetRank}.",
            Muted: true));
        int number = 1;
        if (plan.FedCopies.Count > 0)
        {
            blocks.Add(new MotePlanLine($"{number}. Level up your spare copies first. Small motes stop working on your item once it's too high, " +
                "but a lower copy can still use them, and a higher copy is worth more when merged."));
            number++;
            var groups = plan.FedCopies
                .GroupBy(f => (f.IsFodder, f.StartRank, f.EndRank, Feed: DescribeUses(f.Motes, plan.TierIds)))
                .OrderBy(g => g.Key.StartRank)
                .ThenBy(g => g.Key.EndRank);
            foreach (var group in groups)
            {
                int count = group.Count();
                string copy = group.Key.IsFodder ? $"new +{group.Key.StartRank} item" : $"+{group.Key.StartRank} item";
                string who = count == 1 ? $"One {copy}" : $"{count} {copy}s";
                string each = count == 1 ? "" : " each";
                blocks.Add(new MotePlanLine($"• {who}: feed {group.Key.Feed}{each} → +{group.Key.EndRank}", Indent: true));
            }
        }

        blocks.Add(new MotePlanLine($"{number}. Level up your {thing}:"));
        foreach (var step in plan.Steps)
        {
            var parts = new List<string>();
            var motes = step.Used.Where(u => !u.IsCopy).ToList();
            var copiesUsed = step.Used.Where(u => u.IsCopy).ToList();
            if (step.Classification == MoteStepClassification.OutgrownMotesBeforeSufficientCopies)
            {
                string copyText = DescribeCopies(copiesUsed);
                blocks.Add(new MotePlanLine($"• +{step.FromRank} → +{step.ToRank}: merge {copyText} (that alone makes +{step.ToRank}). " +
                    $"Use your {DescribeUses(motes, plan.TierIds)} first anyway: they stop working at +{step.ToRank}, " +
                    $"so their {step.MotePoints:N0} points carry into +{step.ToRank} → +{step.ToRank + 1}", Indent: true));
                continue;
            }

            if (motes.Count > 0)
            {
                parts.Add($"use your {DescribeUses(motes, plan.TierIds)}");
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

            blocks.Add(new MotePlanLine($"• +{step.FromRank} → +{step.ToRank}: {string.Join(", then ", parts)}", Indent: true));
        }

        blocks.Add(new MotePlanLine($"Stop at +{targetRank}. No further upgrades are planned.", Muted: true));
        if (plan.Steps[^1].CarryAfter > 0)
        {
            blocks.Add(new MotePlanLine($"The last mote or merge leaves {plan.Steps[^1].CarryAfter:N0} extra points. " +
                "Resources add fixed amounts; do not add more points after reaching your target.", Muted: true));
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
                why = step.MissingReason == MoteMissingReason.OnlyUsableEquipmentTier
                    ? $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the only one that still works on a +{step.FromRank} item."
                    : $"Why {step.MissingTier}? Motes stop working once an item gets too high, and {step.MissingTier} is the smallest one that still works on a +{step.FromRank} item.";
            }
            else
            {
                why = "Why these motes? Motes stop working once an item gets too high, so each step asks for the smallest one that still works: " +
                    string.Join(", ", missingSteps.Select(s => $"+{s.FromRank} needs {s.MissingTier}")) + ".";
            }

            blocks.Add(new MotePlanLine(why, Muted: true));
            blocks.Add(new MotePlanLine("If you finish with motes, get: " + DescribeCounts(plan.Missing), Bold: true));
        }
        else
        {
            blocks.Add(new MotePlanLine("You already have everything you need!", Bold: true));
        }

        if (plan.FedCopies.Count > 0 && plan.FeedingSavings.Count > 0)
        {
            string instead = plan.FeedingCosts.Count > 0 ? $" by using {DescribeCounts(plan.FeedingCosts)} instead" : "";
            blocks.Add(new MotePlanLine($"Leveling copies saves you {DescribeCounts(plan.FeedingSavings)}{instead}.", Muted: true));
        }
    }

    private static string DescribeCopies(IReadOnlyList<MoteResourceUse> copies)
    {
        int total = copies.Sum(u => u.Count);
        string list = string.Join(", ", copies.Select(u => u.Count > 1 ? $"{u.Count} × +{u.Rank}" : $"+{u.Rank}"));
        return total == 1 ? $"your {list} copy" : $"your copies {list}";
    }

    private static string DescribeUses(IReadOnlyList<MoteResourceUse> used, IReadOnlyList<string> tierIds)
    {
        return string.Join(", ", used.Select(u => u.IsCopy
            ? $"copy +{u.Rank}{(u.Count > 1 ? $" ×{u.Count}" : "")}"
            : $"{tierIds[u.Rank - 1]} ×{u.Count}"));
    }

    private static string DescribeCounts(IReadOnlyList<MoteResourceCount> counts, bool copies = false)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", counts.Select(count => $"{(copies ? $"+{count.Rank}" : count.Tier)} ×{count.Count:N0}"));
    }

    internal static void RenderMotePlan(
        MotePlanReadModel model, StackPanel host, double fontSize, MotePlanPage page = MotePlanPage.Upgrade)
    {
        var blocks = new List<MotePlanBlock>();
        if (model.Steps.Count == 0)
        {
            blocks.Add(new MotePlanComplete($"Already at the +{model.TargetRank} target. No points needed."));
        }
        else if (page is MotePlanPage.Upgrade or MotePlanPage.WhatIf)
        {
            AddGuide(blocks, model);
        }
        else
        {
            if (page == MotePlanPage.Farm && model.Kind == MoteUpgradeKind.Equipment)
            {
                AddFarmGuide(blocks, model);
            }

            AddMath(blocks, model);
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case MotePlanHeading heading:
                    {
                        var text = new TextBlock
                        {
                            Text = heading.Text,
                            FontWeight = FontWeights.SemiBold,
                            Margin = heading.Detail
                                ? new Thickness(2, heading.Subsequent ? 8 : 6, 0, 0)
                                : new Thickness(2, 6, 0, 2),
                        };
                        text.SetResourceReference(TextBlock.ForegroundProperty, "NightText");
                        host.Children.Add(text);
                        break;
                    }
                case MotePlanLine line:
                    {
                        var text = new TextBlock
                        {
                            Text = line.Text,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(line.Indent ? 16 : 2, 2, 0, 2),
                            FontWeight = line.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                        };
                        text.SetResourceReference(TextBlock.ForegroundProperty, line.Muted ? "NightMuted" : "NightText");
                        host.Children.Add(text);
                        break;
                    }
                case MotePlanTable table:
                    {
                        var control = OverlayPanel.BuildTable(new OverlayTable(table.Headers, table.Rows, RowLabels: table.RowLabels), fontSize);
                        control.ToolTip = table.ToolTip;
                        host.Children.Add(control);
                        break;
                    }
                case MotePlanComparison comparison:
                    {
                        var content = new StackPanel();
                        foreach (string line in comparison.Lines)
                        {
                            content.Children.Add(new TextBlock
                            {
                                Text = line,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(2, 4, 0, 4),
                            });
                        }

                        host.Children.Add(new Expander
                        {
                            Header = comparison.Heading,
                            Content = content,
                            Margin = new Thickness(2, 8, 0, 6),
                        });
                        break;
                    }
                case MotePlanComplete complete:
                    {
                        host.Children.Add(new TextBlock
                        {
                            Text = complete.Text,
                            Margin = new Thickness(2, 6, 0, 0),
                        });
                        break;
                    }
            }
        }
    }
}
