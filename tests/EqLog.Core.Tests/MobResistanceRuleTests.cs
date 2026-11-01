using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public sealed class MobResistanceRuleTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0);

    [Theory]
    [InlineData(7, 13, 0, "Partial \u2014 65% (13/20)")]
    [InlineData(0, 4, 0, "Resists \u2014 100% (4/4)")]
    [InlineData(10, 0, 0, "Not Resistant \u2014 0% (0/10)")]
    [InlineData(1, 1, 0, "Partially Resistant \u2014 50% (1/2)")]
    [InlineData(80, 20, 0, "Partially Resistant \u2014 20% (20/100)")]
    [InlineData(81, 19, 0, "Not Resistant \u2014 19% (19/100)")]
    [InlineData(0, 0, 0, "Unknown \u2014 no assessed outcomes")]
    [InlineData(0, 0, 3, "Unknown \u2014 no assessed outcomes")]
    [InlineData(7, 13, 3, "Partial \u2014 65% (13/20)")]
    public void Observation_DisplayText_ShowsLabelPercentageAndAssessedCounts(
        long landed,
        long resisted,
        long unassessed,
        string expected)
    {
        var observation = new ResistanceObservation(
            ResistanceType.Fire,
            landed,
            resisted,
            50,
            UnassessedDamage: unassessed);
        Assert.Equal(expected, observation.DisplayText);
    }

    [Theory]
    [InlineData(0, 0, "Unknown")]
    [InlineData(0, 1, "Resists")]
    [InlineData(0, 100, "Resists")]
    [InlineData(1, 99, "Partial")]
    [InlineData(49, 51, "Partial")]
    [InlineData(50, 50, "Partially Resistant")]
    [InlineData(79, 21, "Partially Resistant")]
    [InlineData(80, 20, "Partially Resistant")]
    [InlineData(801, 199, "Not Resistant")]
    [InlineData(81, 19, "Not Resistant")]
    [InlineData(89, 11, "Not Resistant")]
    [InlineData(90, 10, "Not Resistant")]
    [InlineData(91, 9, "Not Resistant")]
    [InlineData(100, 0, "Not Resistant")]
    public void Observation_Status_UsesExactConfirmedThresholds(long landed, long resisted, string expected)
    {
        Assert.Equal(expected, new ResistanceObservation(ResistanceType.Magic, landed, resisted, 0).Status);
    }

    [Fact]
    public void Observation_OnlyOtherSpellResists_DoesNotInventOverallStatus()
    {
        Assert.Equal("Unknown", new ResistanceObservation(ResistanceType.Fire, 0, 0, 100).Status);
    }

    [Theory]
    [InlineData("Heart harpie judges you amiably -- what would you like your tombstone to say? (Lvl: 58)", "Heart harpie", 58, false)]
    [InlineData("A spinechiller spider scowls at you, ready to attack -- it appears to be quite formidable. (Lvl: 48)", "A spinechiller spider", 48, false)]
    [InlineData("Protector of Sky - a rare creature - scowls at you, ready to attack -- looks like it would wipe the floor with you! (Lvl: 55)", "Protector of Sky", 55, true)]
    [InlineData("A rat regards you indifferently -- You could probably win this fight. (Lvl: 1)", "A rat", 1, false)]
    [InlineData("A rat looks your way apprehensively -- You could probably win this fight. (Lvl: 1)", "A rat", 1, false)]
    public void Parse_ConsiderMessages_ExtractsMobFactionDifficultyAndLevel(
        string text,
        string mob,
        int level,
        bool rare)
    {
        var consider = Assert.IsType<ConsiderEvent>(new LogEventParser().Parse(T0, text));
        Assert.Equal(mob, consider.Mob);
        Assert.Equal(level, consider.Level);
        Assert.Equal(rare, consider.Rare);
        Assert.NotEmpty(consider.Faction);
        Assert.NotEmpty(consider.Difficulty);
    }

    [Fact]
    public void Parse_ChatContainingConsider_IsNotConsider()
    {
        var ev = new LogEventParser().Parse(
            T0,
            "Bob says, 'Heart harpie judges you amiably -- what would you like your tombstone to say? (Lvl: 58)'");
        Assert.IsType<ChatEvent>(ev);
    }

    [Fact]
    public void Engine_ConsiderAndAssessment_NotifiesWithCurrentZone()
    {
        var engine = new LogEngine();
        ConsiderEvent? received = null;
        string? zone = null;
        string? assessment = null;
        engine.MobConsidered += (consider, location) =>
        {
            received = consider;
            zone = location;
        };
        engine.ConsiderAssessment += consider => assessment = consider.Text;
        engine.Process(new(T0, "You have entered Plane of Sky."));
        engine.Process(new(T0, "Heart harpie judges you amiably -- what would you like your tombstone to say? (Lvl: 58)"));
        engine.Process(new(T0, "This creature would take considerable effort to defeat!"));
        Assert.Equal("Heart harpie", received?.Mob);
        Assert.Equal("Plane of Sky", zone);
        Assert.Equal("This creature would take considerable effort to defeat!", assessment);
    }

    [Fact]
    public void Engine_Outcomes_DeduplicatesReplaysAndIgnoresOthersDotsAndUnknownTargets()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire (-10)", T0, SingleTargetDirectDamage: true));
        var lines = new[]
        {
            "You have entered Plane of Sky.",
            "[50 DRU/WIZ/ENC] Tester (Erudite) <Guild> ZONE: Plane of Sky",
            "You begin casting Conflagration X.",
            "You hit An azarack for 578 points of fire damage by Conflagration X.",
            "You hit An azarack for 578 points of fire damage by Conflagration X.",
            "You begin casting Conflagration X.",
            "An azarack resisted your Conflagration X!",
            "An azarack resisted an azarack's Conflagration X!",
            "Your target resisted the Conflagration X spell.",
            "You begin casting Ignite Blood IX.",
            "An azarack has taken 100 damage from your Ignite Blood IX.",
            "An azarack has taken 100 damage from your Ignite Blood IX."
        };
        for (int replay = 0; replay < 2; replay++)
        {
            var engine = new LogEngine(resistances: store, logIdentity: "test-log")
            {
                CharacterName = "Tester"
            };
            for (int i = 0; i < lines.Length; i++)
            {
                engine.Process(new(T0.AddSeconds(i), lines[i]));
            }
        }

        var fire = store.Get("Plane of Sky", "an azarack").Observations
            .Single(observation => observation.Type == ResistanceType.Fire);
        Assert.Equal(1, fire.Landed);
        Assert.Equal(1, fire.Resisted);
        Assert.Equal(1, fire.UnassessedDamage);
        Assert.Equal(0, store.Get("Plane of Sky", "An azarack").Unclassified);
    }

    [Fact]
    public void Tracker_ExpiredCast_DoesNotSuppressLaterResistsOrAttributeUnrelatedHits()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration", ResistanceType.Fire, "Fire", T0, SingleTargetDirectDamage: true));
        var tracker = new MobResistanceTracker(store, "log");
        tracker.OnEvent(new CastBeginEvent(T0, "Conflagration"), 1);
        tracker.OnEvent(new SpellResistedEvent(T0.AddSeconds(3), "A rat", "Conflagration"), 2);
        tracker.OnEvent(new SpellResistedEvent(T0.AddSeconds(30), "A rat", "Conflagration"), 3);
        tracker.OnEvent(
            new DamageEvent(T0.AddSeconds(31), Names.You, "A rat", 100, DamageKind.DirectSpell, "Conflagration", null),
            4);
        tracker.Advance(T0.AddSeconds(40));
        var fire = store.Get("(unknown zone)", "A rat").Observations.Single(observation => observation.Type == ResistanceType.Fire);
        Assert.Equal(2, fire.Resisted);
        Assert.Equal(0, fire.Landed);
    }

    [Fact]
    public void Tracker_TruncatedLogWithNewTimestamps_ContinuesCounting()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Conflagration", ResistanceType.Fire, "Fire", T0, SingleTargetDirectDamage: true));
        var tracker = new MobResistanceTracker(store, "same-log");
        tracker.OnEvent(new SpellResistedEvent(T0, "A rat", "Conflagration"), 10000);
        tracker.Advance(T0.AddSeconds(4));
        var restarted = new MobResistanceTracker(store, "same-log");
        restarted.OnEvent(new SpellResistedEvent(T0.AddHours(1), "A rat", "Conflagration"), 2);
        restarted.Advance(T0.AddHours(1).AddSeconds(4));
        Assert.Equal(
            2,
            store.Get("(unknown zone)", "A rat").Observations.Single(observation => observation.Type == ResistanceType.Fire).Resisted);
    }

    [Fact]
    public void Store_NonDirectSpells_RetainsResistCountsWithoutInventingRate()
    {
        var store = new MobResistanceStore();
        store.Record("log", 1, "Fear", "A spider", "Ignite Blood IX", true);
        store.SetSpell(new("Ignite Blood IX", ResistanceType.Fire, "Fire (-100)", T0));
        var fire = store.Get("Fear", "A spider").Observations.Single(observation => observation.Type == ResistanceType.Fire);
        Assert.Equal(0, fire.Landed + fire.Resisted);
        Assert.Equal(1, fire.OtherResisted);
    }

    [Fact]
    public void Store_UnknownSpellAndZones_DoesNotGuessMagicOrMixZones()
    {
        var store = new MobResistanceStore();
        store.Record("log", 1, "Zone A", "A rat", "Mystery", true);
        store.Record("log", 2, "Zone B", "A rat", "Mystery", true);
        Assert.Equal(1, store.Get("Zone A", "A RAT").Unclassified);
        Assert.All(store.Get("Zone A", "A rat").Observations, observation => Assert.Equal(0, observation.Resisted));
        store.SetSpell(new("Mystery", ResistanceType.Disease, "Disease", T0));
        Assert.Equal(
            1,
            store.Get("Zone A", "A rat").Observations.Single(observation => observation.Type == ResistanceType.Disease).OtherResisted);
        Assert.Equal(0, store.Get("Zone C", "A rat").Unclassified);
    }

    [Fact]
    public void Store_ZoneDifficultyVariants_StaySeparate()
    {
        var store = new MobResistanceStore();
        store.Record("log", 1, "The Plane of Sky 4 (Refined)", "Heart harpie", "Mystery", true);
        Assert.Equal(1, store.Get("The Plane of Sky 4 (Refined)", "Heart harpie").Unclassified);
        Assert.Equal(0, store.Get("The Plane of Sky", "Heart harpie").Unclassified);
        Assert.Equal(0, store.Get("The Plane of Sky 3 (Pristine)", "Heart harpie").Unclassified);
    }

    [Fact]
    public void PerSpellAssessment_ReusesBaselineAndKeepsExactRanksSeparate()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Flame X", ResistanceType.Fire, null, T0, true));
        store.SetSpell(new("Flame IX", ResistanceType.Fire, null, T0, true));
        for (int i = 1; i <= 3; i++)
        {
            store.Record("log", i, "Sky", "Target", "Flame X", false, T0, "Tester", 1000);
        }

        store.Record("log", 4, "Sky", "Target", "Flame X", false, T0, "Tester", 400);
        store.Record("log", 5, "Sky", "Target", "Flame X", true, T0);
        store.Record("log", 6, "Sky", "Target", "Flame IX", true, T0);
        var exact = store.Get("Sky", "Target", "flame x");
        var fire = exact.Observations.Single(observation => observation.Type == ResistanceType.Fire);
        Assert.Equal(4, fire.Landed);
        Assert.Equal(1, fire.Resisted);
        Assert.Equal(1, fire.EstimatedPartial);
        Assert.Equal("Partially Resistant \u2014 20% (1/5)", fire.DisplayText);
        Assert.Single(exact.Spells);
        Assert.Equal(2, store.Get("Sky", "Target").Observations.Single(observation => observation.Type == ResistanceType.Fire).Resisted);
    }

    [Fact]
    public void PerSpellUnknownMetadata_RawResistsAndUnassessedHitsRemainAvailable()
    {
        var store = new MobResistanceStore();
        store.Record("log", 1, "Sky", "Target", "Unknown X", true, T0);
        store.Record("log", 2, "Sky", "Target", "Unknown X", false, T0, "Tester", 400);
        var counts = store.GetRawSpellCounts("sky", "target", "unknown x");
        Assert.Equal(1, counts.FullResists);
        Assert.Equal(1, counts.DamageHits);
        Assert.Equal(0, counts.LegacyLandings);
        Assert.Equal(2, store.Get("Sky", "Target", "Unknown X").Unclassified);
    }

    [Fact]
    public void Tracker_DotFirstTickAfterNextCast_CountsOneLandingUnderSchool()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Ignite Blood IX", ResistanceType.Fire, "Fire (-100)", T0, DamageOverTime: true));
        store.SetSpell(new("Envenomed Bolt X", ResistanceType.Poison, "Poison (0)", T0, DamageOverTime: true));
        var tracker = new MobResistanceTracker(store, "log")
        {
            CharacterName = "Me"
        };
        tracker.OnEvent(new CastBeginEvent(T0, "Ignite Blood IX"), 1);
        tracker.OnEvent(new CastBeginEvent(T0.AddSeconds(4), "Envenomed Bolt X"), 2);
        tracker.OnEvent(new DamageEvent(T0.AddSeconds(6), Names.You, "A rat", 56, DamageKind.DamageOverTime, "Ignite Blood IX", null), 3);
        tracker.OnEvent(new DamageEvent(T0.AddSeconds(12), Names.You, "A rat", 56, DamageKind.DamageOverTime, "Ignite Blood IX", null), 4);
        tracker.OnEvent(new SpellResistedEvent(T0.AddSeconds(7), "A rat", "Envenomed Bolt X"), 5);
        tracker.Advance(T0.AddSeconds(60));

        var info = store.Get("(unknown zone)", "A rat");
        var fire = info.Observations.Single(observation => observation.Type == ResistanceType.Fire);
        var poison = info.Observations.Single(observation => observation.Type == ResistanceType.Poison);
        Assert.Equal((1, 0, 0L), (fire.Landed, fire.Resisted, fire.OtherResisted));
        Assert.Equal((0, 1, 0L), (poison.Landed, poison.Resisted, poison.OtherResisted));
        Assert.Equal(
            new RawSpellCounts(1, 0, 0, 0, 1),
            store.GetRawSpellCounts("(unknown zone)", "A rat", "Envenomed Bolt X"));
    }

    [Fact]
    public void Store_HistoryReadBeforeDotAssessment_BackfillsDotOutcomesOnceWithoutRecountingResists()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Ignite Blood IX", ResistanceType.Fire, "Fire", T0, DamageOverTime: true));
        store.Record("log", 1, "Zone", "A rat", "Ignite Blood IX", true, T0);
        store.Record("log", 9, "Zone", "A rat", "Conflagration", true, T0.AddMinutes(5));
        for (int replay = 0; replay < 2; replay++)
        {
            store.Record("log", 1, "Zone", "A rat", "Ignite Blood IX", true, T0, damageOverTime: true);
            store.Record("log", 2, "Zone", "A rat", "Ignite Blood IX", false, T0.AddMinutes(1), "Me", damageOverTime: true);
        }

        Assert.Equal(new RawSpellCounts(1, 0, 0, 1, 1), store.GetRawSpellCounts("Zone", "A rat", "Ignite Blood IX"));
    }

    [Fact]
    public void Tracker_RecastOnTargetAlreadyTicking_IsNotCounted()
    {
        var store = new MobResistanceStore();
        store.SetSpell(new("Ignite Blood IX", ResistanceType.Fire, "Fire", T0, DamageOverTime: true));
        var tracker = new MobResistanceTracker(store, "log")
        {
            CharacterName = "Me"
        };
        tracker.OnEvent(new DamageEvent(T0, Names.You, "A rat", 56, DamageKind.DamageOverTime, "Ignite Blood IX", null), 1);
        tracker.OnEvent(new CastBeginEvent(T0.AddSeconds(2), "Ignite Blood IX"), 2);
        tracker.OnEvent(new DamageEvent(T0.AddSeconds(6), Names.You, "A rat", 56, DamageKind.DamageOverTime, "Ignite Blood IX", null), 3);
        tracker.OnEvent(new DamageEvent(T0.AddSeconds(7), "Someone", "A bat", 56, DamageKind.DamageOverTime, "Ignite Blood IX", null), 4);
        tracker.Advance(T0.AddSeconds(60));

        Assert.Empty(store.Get("(unknown zone)", "A rat").Spells);
        Assert.Empty(store.Get("(unknown zone)", "A bat").Spells);
    }

    [Fact]
    public void Tracker_InterruptedDot_DoesNotCountLaterTicks()
    {
        var store = new MobResistanceStore();
        var tracker = new MobResistanceTracker(store, "log")
        {
            CharacterName = "Me"
        };
        tracker.OnEvent(new CastBeginEvent(T0, "Ignite Blood IX"), 1);
        tracker.OnEvent(new CastInterruptedEvent(T0.AddSeconds(1), "Ignite Blood IX"), 2);
        tracker.OnEvent(new DamageEvent(T0.AddSeconds(6), Names.You, "A rat", 56, DamageKind.DamageOverTime, "Ignite Blood IX", null), 3);
        tracker.Advance(T0.AddSeconds(60));

        Assert.Empty(store.Get("(unknown zone)", "A rat").Spells);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tracker_InterruptedOrFizzledCast_DoesNotCountLandedDamage(bool fizzled)
    {
        var store = new MobResistanceStore();
        var tracker = new MobResistanceTracker(store, "log");
        tracker.OnEvent(new CastBeginEvent(T0, "Conflagration"), 1);
        tracker.OnEvent(
            fizzled
                ? new CastFizzleEvent(T0.AddSeconds(1))
                : new CastInterruptedEvent(T0.AddSeconds(1), null),
            2);
        tracker.OnEvent(
            new DamageEvent(T0.AddSeconds(3), Names.You, "A rat", 100, DamageKind.DirectSpell, "Conflagration", null),
            3);

        Assert.Empty(store.Get("(unknown zone)", "A rat").Spells);
    }
}
