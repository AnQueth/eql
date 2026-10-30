using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EqLog.Core.Tracking;
using Xunit;

namespace EqLog.Overlay.Tests;

public class DpsFightPickerTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0);

    private static FightSnapshot Fight(string target, DateTime start)
    {
        return new FightSnapshot(target, start, start, 1, 0, 0, false, true, []);
    }

    [Fact]
    public void Build_ListsCurrentFirst_ThenTenNewestFightsByStart()
    {
        var fights = Enumerable.Range(0, 12).Select(i => Fight($"mob {i}", Start.AddMinutes(i))).Reverse().Skip(1).Append(Fight("mob 11", Start.AddMinutes(11)));

        var choices = DpsFightChoice.Build(fights);

        Assert.Equal(1 + DpsFightChoice.MaxFights, choices.Count);
        Assert.Null(choices[0].Key);
        Assert.Equal(new DpsFightKey("mob 11", Start.AddMinutes(11)), choices[1].Key);
        Assert.Equal("mob 2", choices[^1].Key!.Target);
        Assert.Equal(choices.Skip(1).OrderByDescending(c => c.Key!.Start).Select(c => c.Key), choices.Skip(1).Select(c => c.Key));
    }

    [Fact]
    public void Key_MatchesSameTargetAndStart_Only()
    {
        var key = new DpsFightKey("A Goblin", Start);

        Assert.True(key.Matches(Fight("a goblin", Start)));
        Assert.False(key.Matches(Fight("a goblin", Start.AddSeconds(1))));
        Assert.False(key.Matches(Fight("a rat", Start)));
    }

    [Fact]
    public void BreakdownWindow_WithTimeline_AddsDamageTimelineTab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var breakdown = new AbilityBreakdown("You", "a goblin", "DPS", [], [new(0, 100), new(4, 50)], 5, Start,
                    [new AbilityTimeline("Melee", [new(0, 100)]), new AbilityTimeline("Fire", [new(1, 25), new(4, 25)])]);
                var window = new AbilityBreakdownWindow(breakdown);
                var root = Assert.IsType<DockPanel>(window.Content);
                var tabs = Assert.Single(root.Children.OfType<TabControl>());
                var timeline = Assert.IsType<TabItem>(tabs.Items[1]);
                Assert.Equal("Damage timeline", timeline.Header);
                var bars = FindChild<UniformGrid>((DependencyObject)timeline.Content);
                Assert.Equal(5, bars!.Children.Count);
                var types = Assert.IsType<TabItem>(tabs.Items[2]);
                Assert.Equal("Damage types", types.Header);
                var lanes = FindChild<ScrollViewer>((DependencyObject)types.Content)!.Content as Grid;
                Assert.Equal(["Melee", "Fire"], lanes!.Children.OfType<TextBlock>().Select(t => t.Text));

                var plain = new AbilityBreakdownWindow(new AbilityBreakdown("Heals", "", "HPS", []));
                Assert.Empty(((DockPanel)plain.Content).Children.OfType<TabControl>());
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

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match)
            {
                return match;
            }

            if (child is DependencyObject node && FindChild<T>(node) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
