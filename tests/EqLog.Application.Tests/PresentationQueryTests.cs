using EqLog.Application.Inventory;
using EqLog.Application.Presentation;
using EqLog.Application.Sessions;
using EqLog.Application.Tracking;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;

namespace EqLog.Application.Tests;

public sealed class PresentationQueryTests
{
    [Fact]
    public void IncomingDamageFiltersRecipientsAndIncludesPullAndTrailingHitsInItsSpan()
    {
        var start = new DateTime(2026, 11, 6);
        var events = new DamageEvent[]
        {
            new(start.AddSeconds(5), "a goblin", "Pet", 40, DamageKind.DirectSpell, "Fire", "Critical"),
            new(start.AddSeconds(-2), "a goblin", "Pet", 20, DamageKind.Melee, null, null),
            new(start, "a goblin", Names.You, 999, DamageKind.Melee, null, null),
            new(start.AddSeconds(5), "a goblin", "pet", 10, DamageKind.DirectSpell, "fire", null),
        };
        var details = CombatQueries.IncomingDamage(events, "PET", start, 3);
        Assert.Equal(start.AddSeconds(-2), details.Start);
        Assert.Equal(8, details.Seconds);
        Assert.Equal(3, details.Events.Count);
        Assert.Equal(70, details.Abilities.Sum(a => a.Total));
        Assert.Equal(70.0 / 8, details.Abilities.Sum(a => a.PerSecond));
        Assert.Equal(new[] { 0, 7 }, details.Timeline.Select(p => p.Second));
        var fire = Assert.Single(details.Abilities, a => a.Name == "Fire");
        Assert.Equal(2, fire.Hits);
        Assert.Equal(1, fire.Crits);
        Assert.Equal(2, details.Timelines.Count);
        Assert.Empty(CombatQueries.IncomingDamage(events, "Cleric", start, 3).Events);
    }

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
        Assert.Equal("Wave", alert.Trigger.Text);
        Assert.Equal("Que waves.", alert.Message);
        var filtered = RawLogQueries.Filter(["[Tue Oct 27 12:00:00 2026] Que waves.", "other"], "^{C}", true, "Que", 10);
        Assert.Single(filtered.Lines);
        Assert.Equal(RawLogFilterStatus.Success, filtered.Status);
        var invalid = RawLogQueries.Filter(["one", "two"], "[", true, "Que", 1);
        Assert.Equal(RawLogFilterStatus.InvalidRegex, invalid.Status);
        Assert.NotEmpty(invalid.Diagnostic!);
        Assert.Equal(["two"], invalid.Lines);
    }

    [Fact]
    public void RawLogFilterTimeoutReturnsExplicitTypedFailureWithoutInventingDisplayText()
    {
        string line = new string('a', 10_000) + "!";
        var result = RawLogQueries.Filter([line, "a"], "^(a+)+$", true, null, 10);
        Assert.Equal(RawLogFilterStatus.TimedOut, result.Status);
        Assert.Equal(["a"], result.Lines);
        Assert.Null(result.Diagnostic);
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
        Assert.Equal(NpcSpellResultKind.DamageOverTime, card.Kind);
        Assert.Equal(50m, card.DotResistPercent);
        Assert.Equal(2m, card.DotAssessedCasts);
        Assert.Equal(2, card.Counts.FullResists);
        session.ResetResistances("Zone", "Mob", DateTime.Now);
        Assert.Empty(session.GetMob("Zone", "Mob").Spells);
        Assert.Equal(50m, card.DotResistPercent);
    }

    [Fact]
    public void RendResistanceHistoryLearnsDamageWithUnknownAndKnownLoadouts()
    {
        var at = new DateTime(2026, 11, 3, 16, 27, 0);
        const string zone = "The Plane of Hate - Solo 4 (Refined)";
        const string mob = "Innoruuk`s Chosen";
        var mobs = new MobResistanceStore();
        mobs.SetSpell(new("Rend X", ResistanceType.Magic, "Magic", at, SingleTargetDirectDamage: true));
        var engine = new LogEngine(resistances: mobs, logIdentity: "log") { CharacterName = "Que" };
        engine.Process(new(at, $"You have entered {zone}."));
        engine.Process(new(at.AddSeconds(1), "You begin casting Rend X."));
        engine.Process(new(at.AddSeconds(3), $"You hit {mob} for 1753 points of magic damage by Rend X."));
        engine.Process(new(at.AddSeconds(8), "[50 DRU/WIZ/ENC] Que (Erudite)"));
        engine.Process(new(at.AddSeconds(9), "You begin casting Rend X."));
        engine.Process(new(at.AddSeconds(11), $"You hit {mob} for 1500 points of magic damage by Rend X."));
        engine.Process(new(at.AddSeconds(16), "[50 SHD/BRD/WIZ] Que (Erudite)"));
        engine.Process(new(at.AddSeconds(17), "You begin casting Rend X."));
        engine.Process(new(at.AddSeconds(19), $"{mob} resisted your Rend X!"));
        engine.Snapshot(at.AddSeconds(25));

        var session = new GameSession(new(new(), new(), new(), mobs, new(), []));
        var card = Assert.Single(session.NpcSpellDetails(zone, mob));
        var resistance = Assert.IsType<ResistanceDetails>(card.Resistance);
        Assert.Equal(ResistanceType.Magic, card.School);
        Assert.Equal(NpcSpellResultKind.DirectDamage, card.Kind);
        Assert.Equal(2, resistance.Landed);
        Assert.Equal(1, resistance.Resisted);
        Assert.Equal(3m, resistance.AssessedOutcomes);
        Assert.Equal(2, resistance.UnassessedDamage);
        Assert.Equal(new RawSpellCounts(1, 0, 2, DotResisted: 1), card.Counts);
    }

    [Fact]
    public void DirectSpellReadModelCarriesTypedCountsAndClassifications()
    {
        var mobs = new MobResistanceStore();
        var at = DateTime.UtcNow.AddMinutes(-1);
        mobs.SetSpell(new("Fire", ResistanceType.Fire, null, at, SingleTargetDirectDamage: true, TargetType: "Single"));
        mobs.Record("log", 1, "Zone", "Mob", "Fire", false, at);
        mobs.Record("log", 2, "Zone", "Mob", "Fire", true, at.AddSeconds(1));
        var session = new GameSession(new(new(), new(), new(), mobs, new(), []));
        var card = Assert.Single(session.NpcSpellDetails("Zone", "Mob"));
        Assert.Equal(NpcSpellResultKind.DirectDamage, card.Kind);
        Assert.Equal(SpellAssessmentKind.DirectDamage, card.Assessment);
        Assert.Equal(ResistanceStatus.PartiallyResistant, card.Resistance!.Status);
        Assert.Equal(50m, card.Resistance.ResistPercent);
        Assert.Equal(1, card.Resistance.Landed);
        Assert.Equal(1, card.Counts.LegacyLandings);
        Assert.Equal(1, card.Counts.FullResists);
        Assert.Equal(3, card.Resistance.MinimumDamageSamples);
        Assert.Equal(90m, card.Resistance.ReducedDamageThresholdPercent);
        Assert.Equal("Single", card.TargetType);
    }

    [Theory]
    [InlineData("Active", NpcEncounterOutcome.Active)]
    [InlineData("NPC died", NpcEncounterOutcome.NpcDied)]
    [InlineData("You died", NpcEncounterOutcome.PlayerDied)]
    [InlineData("Loadout changed", NpcEncounterOutcome.LoadoutChanged)]
    [InlineData("Zoned", NpcEncounterOutcome.Zoned)]
    [InlineData("Login", NpcEncounterOutcome.Login)]
    [InlineData("future outcome", NpcEncounterOutcome.Unknown)]
    public void NpcReadModelMapsStoredOutcomeIdentifiersToTypedValues(string stored, NpcEncounterOutcome expected)
    {
        var at = DateTime.UtcNow;
        var encounter = new NpcEncounter("one", at, at, "Que", null, null, stored, 100, 0, 0, []);
        var read = NpcReadModel.From(new("Mob", "Zone", at, at, 0, null, [encounter]));
        var result = Assert.Single(read.Encounters);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected == NpcEncounterOutcome.NpcDied ? 1 : 0, read.ObservedDeaths);
        Assert.Equal(expected == NpcEncounterOutcome.Unknown ? stored : null, result.UnrecognizedOutcome);
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
