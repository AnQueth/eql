using EqLog.Core.Triggers;

namespace EqLog.Core.Tests;

public class TriggerMatcherTests
{
    private const string Fell = "[Tue Oct 27 12:00:05 2026] Quethrosar has fallen to the ground.";

    [Fact]
    public void DefaultFeignDeathTriggerMatchesOnlyYourCharacter()
    {
        var matcher = new TriggerMatcher(AlertTrigger.Defaults(), "Quethrosar");

        var alert = Assert.Single(matcher.Match([Fell, "[Tue Oct 27 12:00:06 2026] Xath has fallen to the ground."]));
        Assert.Equal("FEIGN DEATH FAILED!", alert.Text);
        Assert.Equal(new DateTime(2026, 10, 27, 12, 0, 5), alert.LogTime);
        Assert.True(alert.Trigger.Sound);
    }

    [Fact]
    public void CapturedGroupsAndNameAreSubstitutedIntoText()
    {
        var matcher = new TriggerMatcher(
        [
            new AlertTrigger { Pattern = @"^You are no longer feigning death, because (.+)\.$", Text = "{C}: FD broken by $1" },
            new AlertTrigger { Pattern = @"^(?<who>\w+) tells you, '(?<msg>.*)'$", Text = "Tell from ${who}" },
        ], "Quethrosar");

        var alerts = matcher.Match(
        [
            "[Tue Oct 27 12:00:05 2026] You are no longer feigning death, because a spell hit you.",
            "[Tue Oct 27 12:00:06 2026] Jagon tells you, 'inc'",
        ]);

        Assert.Equal(["Quethrosar: FD broken by a spell hit you", "Tell from Jagon"], alerts.Select(a => a.Text));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndEmptyTextShowsTheMessage()
    {
        var matcher = new TriggerMatcher([new AlertTrigger { Pattern = "FALLEN TO THE GROUND" }], null);
        Assert.Equal("Quethrosar has fallen to the ground.", Assert.Single(matcher.Match([Fell])).Text);
    }

    [Fact]
    public void DisabledAndInvalidTriggersAreSkipped()
    {
        var matcher = new TriggerMatcher(
        [
            new AlertTrigger { Name = "off", Pattern = "fallen", Enabled = false },
            new AlertTrigger { Name = "bad", Pattern = "fallen(" },
            new AlertTrigger { Name = "ok", Pattern = "fallen" },
        ], "Quethrosar");

        Assert.Equal(1, matcher.Count);
        Assert.StartsWith("bad:", Assert.Single(matcher.Errors));
        Assert.Equal("ok", Assert.Single(matcher.Match([Fell])).Trigger.Name);
    }

    [Fact]
    public void PatternFromMessageMatchesTheLineAndSimilarOnes()
    {
        string pattern = TriggerMatcher.PatternFromMessage("Quethrosar hit a goblin (x2) for 345 points of damage.", "Quethrosar");
        Assert.Equal(@"^{C} hit a goblin \(x\d+\) for \d+ points of damage\.$", pattern);

        var matcher = new TriggerMatcher([new AlertTrigger { Pattern = pattern, Text = "hit" }], "Quethrosar");
        Assert.Single(matcher.Match(["[Tue Oct 27 12:00:05 2026] Quethrosar hit a goblin (x3) for 9 points of damage."]));
        Assert.Empty(matcher.Match(["[Tue Oct 27 12:00:05 2026] Xath hit a goblin (x3) for 9 points of damage."]));
    }

    [Fact]
    public void UnknownCharacterNameMatchesAnyName()
    {
        var matcher = new TriggerMatcher(AlertTrigger.Defaults(), null);
        Assert.Single(matcher.Match(["[Tue Oct 27 12:00:06 2026] Xath has fallen to the ground."]));
    }
}
