using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EqLog.Overlay;

/// <summary>A snapshot comparison of saved loadouts with selectable encounter and effect details.</summary>
public sealed class NpcDpsHistoryWindow : Window
{
    internal DataGrid Loadouts
    {
        get;
    }
    internal DataGrid Encounters
    {
        get;
    }
    internal DataGrid Abilities
    {
        get;
    }
    private readonly TextBlock _details;

    public NpcDpsHistoryWindow(NpcReadModel npc)
    {
        Title = $"{npc.Mob} - DPS by loadout";
        Width = 1100;
        Height = 800;
        MinWidth = 680;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "NightBackground");
        SetResourceReference(ForegroundProperty, "NightText");
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = $"{npc.Mob} - {npc.Zone}", FontSize = 20, TextWrapping = TextWrapping.Wrap });
        var groups = NpcDpsHistory.From(npc);
        header.Children.Add(new TextBlock
        {
            Text = groups.Count == 0
                ? "No personal damage history for this mob and zone yet."
                : "Click a loadout, then a fight for details. Average DPS = total personal damage / total encounter seconds for observed NPC deaths only.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var note = new TextBlock
        {
            Text = "Snapshot when opened; reopen for new fights. Character, reported classes, and level stay separate; unknown loadouts are not guessed. " +
                "Active, zoned, death, and loadout-change segments appear in history but do not enter comparison averages. " +
                "Mean gives each completed fight equal weight. P90/P99 are nearest-rank percentiles of completed fight DPS; small samples may equal best DPS. " +
                "Durations include incoming damage and run through last activity (+1 second). Gear, buffs, debuffs and other attackers can affect comparisons. " +
                "Named effects retain exact ranks; identical proc/cast names share a row. Older saved physical damage may still be combined as Melee. " +
                "Landings are confirmed direct casts, not hit counts. Crits, max hits, and timelines were not saved in this history.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        DockPanel.SetDock(note, Dock.Bottom);
        root.Children.Add(note);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Loadouts = Table();
        Column(Loadouts, "Character", "Character");
        Column(Loadouts, "Loadout", "Classes", unknown: "Unknown");
        Column(Loadouts, "Level", "Level", unknown: "?");
        Column(Loadouts, "Encounters", "Encounters", "N0");
        Column(Loadouts, "NPC deaths", "CompletedFights", "N0");
        Column(Loadouts, "Damage (deaths)", "Damage", "N0");
        Column(Loadouts, "Seconds (deaths)", "Seconds", "N1");
        Column(Loadouts, "Weighted average DPS", "AverageDps", "N1", "No completed fights");
        Column(Loadouts, "Mean DPS", "MeanDps", "N1", "-");
        Column(Loadouts, "P90 DPS", "P90Dps", "N1", "-");
        Column(Loadouts, "P99 DPS", "P99Dps", "N1", "-");
        Column(Loadouts, "Best DPS", "BestDps", "N1", "-");
        grid.Children.Add(Loadouts);
        Add(grid, new TextBlock { Text = "Encounters for selected loadout", Margin = new Thickness(0, 8, 0, 4) }, 1);
        Encounters = Table();
        Column(Encounters, "Started", "Start", "g");
        Column(Encounters, "Outcome", "Outcome");
        Column(Encounters, "Your damage", "Damage", "N0");
        Column(Encounters, "Seconds", "Seconds", "N1");
        Column(Encounters, "Your DPS", "Dps", "N1");
        Column(Encounters, "Others' damage", "OtherDamage", "N0");
        Column(Encounters, "Damage taken", "IncomingDamage", "N0");
        Add(grid, Encounters, 2);
        _details = new TextBlock { Text = "Select a fight for its saved effect details.", Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap };
        Add(grid, _details, 3);
        Abilities = Table();
        Column(Abilities, "Effect / damage type", "Name");
        Column(Abilities, "Damage", "Damage", "N0");
        Column(Abilities, "DPS", "Dps", "N1");
        Column(Abilities, "Share %", "Percent", "N1");
        Column(Abilities, "Direct landings", "Landings", "N0");
        Column(Abilities, "Full resists", "Resists", "N0");
        Add(grid, Abilities, 4);
        root.Children.Add(grid);
        Content = root;
        Loadouts.SelectionChanged += (_, _) => SelectLoadout();
        Encounters.SelectionChanged += (_, _) => SelectEncounter();
        Loadouts.ItemsSource = groups;
        Loadouts.SelectedIndex = groups.Count > 0 ? 0 : -1;
    }

    private void SelectLoadout()
    {
        Encounters.ItemsSource = (Loadouts.SelectedItem as NpcLoadoutDps)?.History
            .Select(item => new EncounterRow(item.Encounter.Start, MainWindow.NpcOutcomeText(item.Encounter),
                item.Encounter.YourDamage, item.Encounter.Seconds, item.Encounter.YourDps,
                item.Encounter.OtherDamage, item.Encounter.IncomingDamage, item)).ToArray();
        Encounters.SelectedIndex = Encounters.Items.Count > 0 ? 0 : -1;
        SelectEncounter();
    }

    private void SelectEncounter()
    {
        var selected = Encounters.SelectedItem as EncounterRow;
        Abilities.ItemsSource = selected?.Details.Abilities;
        _details.Text = selected is null ? "Select a fight for its saved effect details."
            : $"{selected.Start:g} - {selected.Outcome} - {selected.Damage:N0} damage / {selected.Seconds:N1}s = {selected.Dps:N1} DPS";
    }

    private static DataGrid Table()
    {
        return new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
        };
    }

    private static void Column(DataGrid table, string title, string path, string? format = null, string? unknown = null)
    {
        table.Columns.Add(new DataGridTextColumn
        {
            Header = title,
            Binding = new Binding(path) { StringFormat = format, TargetNullValue = unknown },
            Width = DataGridLength.Auto,
        });
    }

    private static void Add(Grid grid, UIElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private sealed record EncounterRow(DateTime Start, string Outcome, long Damage, double Seconds, double Dps,
        long OtherDamage, long IncomingDamage, NpcDpsEncounter Details);
}
