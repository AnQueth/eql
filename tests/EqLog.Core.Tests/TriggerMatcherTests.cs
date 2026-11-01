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
        Assert.Equal("FEIGN DEATH FAILED!", alert.Trigger.Text);
        Assert.Equal(new DateTime(2026, 10, 27, 12, 0, 5), alert.LogTime);
        Assert.True(alert.Trigger.Sound);
    }

    [Fact]
    public void CapturedGroupsAndCharacterAreReturnedWithoutFormattingText()
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

        Assert.Equal("a spell hit you", alerts[0].Captures.Groups[1].Value);
        Assert.Equal("Jagon", alerts[1].Captures.Groups["who"].Value);
        Assert.All(alerts, alert => Assert.Equal("Quethrosar", alert.CharacterName));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndEmptyTextShowsTheMessage()
    {
        var matcher = new TriggerMatcher([new AlertTrigger { Pattern = "FALLEN TO THE GROUND" }], null);
        Assert.Equal("Quethrosar has fallen to the ground.", Assert.Single(matcher.Match([Fell])).Message);
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
        Assert.Equal("bad", Assert.Single(matcher.Errors).Name);
        Assert.NotEmpty(Assert.Single(matcher.Errors).Diagnostic);
        Assert.Equal("ok", Assert.Single(matcher.Match([Fell])).Trigger.Name);
    }

    [Fact]
    public void SimpleTextMatchesLiterallyAnywhereIgnoringCase()
    {
        var matcher = new TriggerMatcher(
        [
            new AlertTrigger { Name = "simple", Simple = "{C} HAS FALLEN", Text = "down" },
            new AlertTrigger { Name = "literal", Simple = "ground." },
            new AlertTrigger { Name = "dots", Simple = "ground..." },
        ], "Quethrosar");

        var names = matcher.Match([Fell]).Select(a => a.Trigger.Name).ToList();
        Assert.Equal(["simple", "literal"], names);
        Assert.Empty(matcher.Errors);
    }

    [Fact]
    public void SimpleTextOrRegexFiresOnceAndPrefersRegexCaptures()
    {
        var matcher = new TriggerMatcher(
        [
            new AlertTrigger { Simple = "fallen to the ground", Pattern = @"^(\w+) has fallen", Text = "$1 fell" },
            new AlertTrigger { Simple = "no such text", Pattern = "fallen", Text = "regex only" },
            new AlertTrigger { Simple = "fallen", Pattern = "no such text", Text = "simple only" },
            new AlertTrigger { Simple = "nope", Pattern = "nope", Text = "neither" },
        ], null);

        var alerts = matcher.Match([Fell]);
        Assert.Equal(["$1 fell", "regex only", "simple only"], alerts.Select(a => a.Trigger.Text));
        Assert.Equal("Quethrosar", alerts[0].Captures.Groups[1].Value);
    }

    [Fact]
    public void InvalidRegexStillAllowsSimpleText()
    {
        var matcher = new TriggerMatcher([new AlertTrigger { Name = "mixed", Simple = "fallen", Pattern = "fallen(" }], null);

        Assert.Equal("mixed", Assert.Single(matcher.Errors).Name);
        Assert.Single(matcher.Match([Fell]));
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

    [Fact]
    public void MatchedTemplatesAreDetachedFromEditsAndOtherAlerts()
    {
        var trigger = new AlertTrigger { Pattern = "fallen", Text = "original" };
        var matcher = new TriggerMatcher([trigger], "Que");
        var first = Assert.Single(matcher.Match([Fell]));
        trigger.Text = "edited";
        first.Trigger.Text = "changed result";
        var second = Assert.Single(matcher.Match([Fell]));
        Assert.Equal("original", second.Trigger.Text);
        Assert.Equal("Que", first.CharacterName);
    }
}
