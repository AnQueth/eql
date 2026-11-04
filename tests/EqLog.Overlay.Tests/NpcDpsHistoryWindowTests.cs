using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using EqLog.Application.Sessions;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class NpcDpsHistoryWindowTests
{
    [Fact]
    public void SelectingLoadoutAndFight_UpdatesSavedEffectDetailsAndKeepsRates()
    {
        RunSta(() =>
        {
            var start = new DateTime(2026, 11, 4);
            NpcEncounterDetails[] fights =
            [
                new("one", start, "Que", "DRU/WIZ/ENC", 50, NpcEncounterOutcome.NpcDied,
                    1000, 100, 20, 10, 100, [new("Ranged", 600, 0, 0), new("Whirlwind", 400, 0, 0)]),
                new("two", start.AddMinutes(1), "Que", "DRU/WIZ/ENC", 50, NpcEncounterOutcome.NpcDied,
                    500, 100, 10, 10, 50, [new("Melee", 500, 0, 0)]),
                new("three", start.AddMinutes(2), "Que", "SHD/BRD/WIZ", 50, NpcEncounterOutcome.NpcDied,
                    200, 0, 0, 10, 20, [new("Soul Leech", 200, 0, 0)]),
                new("unknown", start.AddMinutes(3), "Que", null, null, NpcEncounterOutcome.Active,
                    10, 0, 0, 5, 2, [new("Melee", 10, 0, 0)]),
            ];
            var npc = new NpcReadModel("Sphinx", "Sky", start, start, 0, null, null,
                fights, 0, 0, 1710, 200, 30, 3, true, true);
            var window = new NpcDpsHistoryWindow(npc);
            window.Show();
            window.UpdateLayout();
            Assert.Equal(3, window.Loadouts.Items.Count);
            foreach (var (header, expected) in new[] { ("Mean DPS", "75.0"), ("P90 DPS", "100.0"), ("P99 DPS", "100.0") })
            {
                var column = window.Loadouts.Columns.Single(c => Equals(c.Header, header));
                window.Loadouts.ScrollIntoView(window.Loadouts.Items[0], column);
                window.UpdateLayout();
                Assert.Equal(expected, Assert.IsType<TextBlock>(column.GetCellContent(window.Loadouts.Items[0])).Text);
            }
            Assert.Equal(2, window.Encounters.Items.Count);
            Assert.Equal("Melee", Assert.Single(window.Abilities.Items.OfType<NpcDpsAbility>()).Name);
            window.Encounters.SelectedIndex = 1;
            Assert.Equal(["Ranged", "Whirlwind"], window.Abilities.Items.OfType<NpcDpsAbility>().Select(a => a.Name));
            Assert.Equal(60, window.Abilities.Items.OfType<NpcDpsAbility>().First().Dps);
            window.Loadouts.SelectedIndex = 1;
            Assert.Single(window.Encounters.Items);
            Assert.Equal("Soul Leech", Assert.Single(window.Abilities.Items.OfType<NpcDpsAbility>()).Name);
            window.UpdateLayout();
            Assert.Equal("Soul Leech", Assert.IsType<TextBlock>(
                window.Abilities.Columns[0].GetCellContent(window.Abilities.Items[0])).Text);
            Assert.Equal("20.0", Assert.IsType<TextBlock>(
                window.Encounters.Columns[4].GetCellContent(window.Encounters.Items[0])).Text);
            window.Loadouts.SelectedIndex = 2;
            Assert.Null(((NpcLoadoutDps)window.Loadouts.SelectedItem).AverageDps);
            Assert.Equal("Melee", Assert.Single(window.Abilities.Items.OfType<NpcDpsAbility>()).Name);
            window.Encounters.SelectedIndex = -1;
            Assert.Empty(window.Abilities.Items);
            Assert.True(window.Loadouts.EnableRowVirtualization);
            Assert.True(window.Encounters.EnableRowVirtualization);
            window.Close();
        });
    }

    [Fact]
    public void EmptyHistory_ShowsExplicitMessageAndNoSelection()
    {
        RunSta(() =>
        {
            var npc = new NpcReadModel("Sphinx", "Sky", DateTime.Now, DateTime.Now, 0, null, null,
                [], 0, 0, 0, 0, 0, 0, false, false);
            var window = new NpcDpsHistoryWindow(npc);
            var root = Assert.IsType<DockPanel>(window.Content);
            var header = root.Children.OfType<StackPanel>().Single();
            Assert.Contains(header.Children.OfType<TextBlock>(), text => text.Text.Contains("No personal damage history"));
            Assert.Empty(window.Loadouts.Items);
            Assert.Empty(window.Encounters.Items);
            Assert.Empty(window.Abilities.Items);
            window.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
