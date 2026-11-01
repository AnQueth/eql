using EqLog.Application.Inventory;
using EqLog.Application.Presentation;
using EqLog.Application.Sessions;
using EqLog.Application.Tracking;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;

namespace EqLog.Application.Tests;

public sealed class PresentationQueryTests
{
    [Fact]
    public void SessionQueriesPreserveNormalizedLootOrderingAndDetachTheResult()
    {
        var at = new DateTime(2026, 11, 1);
        var groups = new List<LootGroup>
        {
            new("Zed", "Gem +2", 2, at),
            new("Amy", "Apple", 1, at.AddSeconds(-1)),
            new("Amy", "Gem", 3, at),
        };
        var snapshot = new LootSnapshot(default, default, 6, [], []) { Groups = groups };
        var recent = SessionQueries.LootGroups(snapshot, true);
        Assert.Equal(["Amy", "Zed", "Amy"], recent.Select(group => group.Looter));
        Assert.Equal(["Apple", "Gem", "Gem +2"], SessionQueries.LootGroups(snapshot, false).Select(group => group.Item));
        groups.Clear();
        Assert.Equal(3, recent.Count);
        Assert.Equal(3, recent[0].Quantity);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(60, 0)]
    [InlineData(120, 150)]
    [InlineData(3600, 5)]
    public void SessionQueriesPreserveTheExperienceRateWarmup(int elapsedSeconds, double expected)
    {
        var at = new DateTime(2026, 11, 1);
        var snapshot = new SessionSnapshot(null, null, at, 0, 0, null, 1, 5, 0, null, 0, false, []);
        Assert.Equal(expected, SessionQueries.ExperiencePercentPerHour(snapshot, at.AddSeconds(elapsedSeconds)), 6);
        Assert.Equal(0, SessionQueries.ExperiencePercentPerHour(snapshot with
        {
            SessionStart = null
        }, at.AddHours(1)));
    }

    [Fact]
    public void SessionCommandsPreserveHighestRankQueriesAndDetachedResults()
    {
        var durations = new SpellDurationStore();
        durations.AddSample("Fire +2", 60, DateTime.Now);
        var session = new GameSession(new(durations, new(), new(), new(), new(), []));
        session.AddSpellDuration("Fire +2", 90);
        var before = Assert.Single(session.SpellDurations());
        session.SetSpellTracked("Fire +2", false);
        session.SetSpellDurationOverride("Fire +2", 120);
        Assert.True(before.Tracked);
        Assert.Equal(90, before.OverrideSeconds);
        Assert.False(Assert.Single(session.SpellDurations()).Tracked);
        Assert.Equal(120, Assert.Single(session.SpellDurations()).OverrideSeconds);
        session.RemoveSpellDuration("Fire +2");
        Assert.Empty(session.SpellDurations());
    }

    [Fact]
    public void TriggerCommandsAndRawLogQueriesUseCharacterAwareRules()
    {
        var session = new GameSession(new(new(), new(), new(), new(), new(), []));
        Assert.Empty(session.ConfigureTriggers([new AlertTrigger { Pattern = @"^{C} waves\.$", Text = "Wave" }], "Que"));
        var alert = Assert.Single(session.MatchTriggers(["[Tue Oct 27 12:00:00 2026] Que waves."]));
        Assert.Equal("Wave", alert.Text);
        var filtered = RawLogQueries.Filter(["[Tue Oct 27 12:00:00 2026] Que waves.", "other"], "^{C}", true, "Que", 10);
        Assert.Single(filtered.Lines);
        Assert.Null(filtered.Error);
        var invalid = RawLogQueries.Filter(["one", "two"], "[", true, "Que", 1);
        Assert.Contains("Invalid regex", invalid.Error);
        Assert.Equal(["two"], invalid.Lines);
    }

    [Fact]
    public void InventoryReadModelAndCombinedSkyShortfallsCannotChangeWithOriginalState()
    {
        var state = new QuestInventory();
        state.ApplyExport("Que", [new("Held", "Gem +4", 1, 3, InventoryStorage.Carried)], DateTime.UtcNow);
        var read = new InventoryReadModel(state);
        var one = new SkyQuest("Wizard", "First", "Reward", "Giver", "", "", "", "url", [new("Gem", 2)]);
        var two = one with
        {
            Name = "Second"
        };
        var catalog = new SkyQuestCatalog(DateTime.UtcNow, [one, two], new Dictionary<string, IReadOnlyList<string>> { ["Gem"] = ["Boss"] });
        var progress = SkyQueries.ProgressAll(catalog.Quests, read, "Que");
        Assert.True(Assert.Single(progress[one]).Satisfied);
        var needed = Assert.Single(SkyQueries.Needed(SkyQueries.ByDropper(catalog.Quests, catalog), progress));
        var item = Assert.Single(needed.Items);
        Assert.Equal(4, item.Required);
        Assert.Equal(3, item.Owned);
        Assert.Equal(1, item.Missing);
        state.Items[0].Owned = 99;
        Assert.Equal(3, Assert.Single(read.Items).Owned);
        Assert.Equal(3, Assert.Single(read.SkyProgress([one], "Que")[one]).Owned);
    }

    [Fact]
    public void NpcReadModelPrecomputesOnlyPersonalFightTimeForPersonalDps()
    {
        var at = new DateTime(2026, 11, 1);
        var encounters = new NpcEncounter[]
        {
            new("one", at, at.AddSeconds(9), "Que", null, null, "NPC died", 100, 200, 50, []),
            new("two", at, at.AddSeconds(19), "Other", null, null, "Active", 0, 100, 0, []),
        };
        var read = NpcReadModel.From(new("Mob", "Zone", at, at, 1, null, encounters));
        Assert.Equal(10, read.YourAverageDps);
        Assert.Equal(10, read.OtherAverageDps);
        Assert.Equal(100, read.YourDamage);
        Assert.Equal(300, read.OtherDamage);
        Assert.Equal(1, read.ObservedDeaths);
        Assert.Equal(10, read.Encounters[0].Seconds);
        Assert.Equal(10, read.Encounters[0].YourDps);
    }

    [Fact]
    public void SpellReadModelsSeparateDotCastRatesFromAllExplicitResists()
    {
        var mobs = new MobResistanceStore();
        mobs.SetSpell(new("Poison", ResistanceType.Poison, null, DateTime.UtcNow, DamageOverTime: true));
        var at = DateTime.UtcNow.AddMinutes(-1);
        mobs.Record("log", 1, "Zone", "Mob", "Poison", false, at, damageOverTime: true);
        mobs.Record("log", 2, "Zone", "Mob", "Poison", true, at.AddSeconds(1), damageOverTime: true);
        mobs.Record("log", 3, "Zone", "Mob", "Poison", true, at.AddSeconds(2));
        var session = new GameSession(new(new(), new(), new(), mobs, new(), []));
        var card = Assert.Single(session.NpcSpellDetails("Zone", "Mob"));
        Assert.Contains("50% resisted (1/2)", card.Result);
        Assert.Contains(card.Evidence, field => field.Label == "All resists" && field.Value == "2");
        session.ResetResistances("Zone", "Mob", DateTime.Now);
        Assert.Empty(session.GetMob("Zone", "Mob").Spells);
        Assert.Contains("50%", card.Result);
    }

    [Theory]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("-1", false)]
    [InlineData("60", true)]
    public void SpellDurationValidationLivesInApplication(string text, bool valid)
    {
        Assert.Equal(valid, GameSession.TryReadSpellDuration(text, out _));
    }
}
