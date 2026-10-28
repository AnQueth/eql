using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EqLog.Overlay;

public sealed class AbilityBreakdownWindow : Window
{
    public AbilityBreakdownWindow(AbilityBreakdown breakdown)
    {
        Title = $"{breakdown.Title} - ability breakdown";
        Width = 850;
        Height = 480;
        MinWidth = 540;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "NightBackground");
        SetResourceReference(ForegroundProperty, "NightText");
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = breakdown.Title, FontSize = 21, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Pumpkin");
        header.Children.Add(title);
        var context = new TextBlock { Text = breakdown.Context, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        context.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        header.Children.Add(context);
        header.Children.Add(new TextBlock
        {
            Text = $"Total {breakdown.Abilities.Sum(a => a.Total):N0} · {breakdown.Abilities.Sum(a => a.PerSecond):N1} {breakdown.RateLabel}",
            Margin = new Thickness(0, 8, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var note = new TextBlock
        {
            Text = "Snapshot captured when clicked. Rates use the displayed fight/session healing span, not the rolling 30-second meter. Only events visible in your log are included; unnamed melee is grouped as Melee.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "NightMuted");
        DockPanel.SetDock(note, Dock.Bottom);
        root.Children.Add(note);
        var table = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            ItemsSource = breakdown.Abilities,
            SelectionMode = DataGridSelectionMode.Single,
        };
        AddColumn(table, "Ability", "Name", null, new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn(table, "Total", "Total", "N0");
        AddColumn(table, breakdown.RateLabel, "PerSecond", "N1");
        AddColumn(table, "Share %", "Percent", "N1");
        AddColumn(table, "Hits", "Hits", "N0");
        AddColumn(table, "Crits", "Crits", "N0");
        AddColumn(table, "Max", "MaxHit", "N0");
        if (breakdown.RateLabel == "HPS")
        {
            AddColumn(table, "Overheal", "Overheal", "N0");
        }

        root.Children.Add(table);
        Content = root;
    }

    private static void AddColumn(DataGrid grid, string header, string path, string? format, DataGridLength? width = null)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { StringFormat = format },
            Width = width ?? DataGridLength.Auto,
            MinWidth = path == "Name" ? 160 : 65,
        });
    }
}
