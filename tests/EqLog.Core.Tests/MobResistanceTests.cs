using System.Net;
using System.Text;
using System.Text.Json;
using EqLog.Core.IO;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class MobResistanceTests
{
    [Theory]
    [InlineData(7, 13, 0, "Partial \u2014 65% (13/20)")]
    [InlineData(0, 4, 0, "Resists \u2014 100% (4/4)")]
    [InlineData(10, 0, 0, "Not Resistant \u2014 0% (0/10)")]
    [InlineData(1, 1, 0, "Partially Resistant \u2014 50% (1/2)")]
    [InlineData(0, 0, 0, "Unknown \u2014 no assessed outcomes")]
    [InlineData(0, 0, 3, "Unknown \u2014 no assessed outcomes")]
    [InlineData(7, 13, 3, "Unknown \u2014 65% (13/20)")]
    public void Observation_DisplayText_ShowsLabelPercentageAndAssessedCounts(long landed, long resisted,
        long unassessed, string expected)
    {
        var observation = new ResistanceObservation(ResistanceType.Fire, landed, resisted, 50,
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
    [InlineData(89, 11, "Partially Resistant")]
    [InlineData(90, 10, "Partially Resistant")]
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

    private static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0);

    [Theory]
    [InlineData("Heart harpie judges you amiably -- what would you like your tombstone to say? (Lvl: 58)", "Heart harpie", 58, false)]
    [InlineData("A spinechiller spider scowls at you, ready to attack -- it appears to be quite formidable. (Lvl: 48)", "A spinechiller spider", 48, false)]
    [InlineData("Protector of Sky - a rare creature - scowls at you, ready to attack -- looks like it would wipe the floor with you! (Lvl: 55)", "Protector of Sky", 55, true)]
    [InlineData("A rat regards you indifferently -- You could probably win this fight. (Lvl: 1)", "A rat", 1, false)]
    [InlineData("A rat looks your way apprehensively -- You could probably win this fight. (Lvl: 1)", "A rat", 1, false)]
    public void Parse_ConsiderMessages_ExtractsMobFactionDifficultyAndLevel(string text, string mob, int level, bool rare)
    {
        var con = Assert.IsType<ConsiderEvent>(new LogEventParser().Parse(T0, text));
        Assert.Equal(mob, con.Mob);
        Assert.Equal(level, con.Level);
        Assert.Equal(rare, con.Rare);
        Assert.NotEmpty(con.Faction);
        Assert.NotEmpty(con.Difficulty);
    }

    [Fact]
    public void Parse_ChatContainingConsider_IsNotConsider()
    {
        var ev = new LogEventParser().Parse(T0,
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
        engine.MobConsidered += (con, location) =>
        {
            received = con;
            zone = location;
        };
        engine.ConsiderAssessment += con => assessment = con.Text;
        engine.Process(new(T0, "You have entered Plane of Sky."));
        engine.Process(new(T0, "Heart harpie judges you amiably -- what would you like your tombstone to say? (Lvl: 58)"));
        engine.Process(new(T0, "This creature would take considerable effort to defeat!"));
        Assert.Equal("Heart harpie", received?.Mob);
        Assert.Equal("Plane of Sky", zone);
        Assert.Equal("This creature would take considerable effort to defeat!", assessment);
    }

    [Fact]
    public async Task Tailer_InitialHistoryDoesNotTriggerPopup_NewConsiderDoes()
    {
        string path = Path.GetTempFileName();
        using var cts = new CancellationTokenSource();
        Task? tail = null;
        try
        {
            const string con = "Heart harpie judges you amiably -- what would you like your tombstone to say? (Lvl: 58)";
            string line = $"[{T0.ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {con}\r\n";
            await File.WriteAllTextAsync(path, line);
            var engine = new LogEngine();
            bool live = false;
            int popups = 0;
            var caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liveConsider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.MobConsidered += (_, _) =>
            {
                if (live)
                {
                    Interlocked.Increment(ref popups);
                    liveConsider.TrySetResult();
                }
            };
            tail = new LogTailer(path).RunAsync(true, engine.ProcessLines, progress =>
            {
                live |= progress.CaughtUp;
                if (live)
                {
                    caughtUp.TrySetResult();
                }
            }, cts.Token);
            await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref popups));
            await File.AppendAllTextAsync(path, line);
            await liveConsider.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref popups));
        }
        finally
        {
            await cts.CancelAsync();
            if (tail is not null)
            {
                try
                {
                    await tail;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
            }
            File.Delete(path);
        }
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
            "An azarack has taken 100 damage from your Ignite Blood IX.",
        };
        for (int replay = 0; replay < 2; replay++)
        {
            var engine = new LogEngine(resistances: store, logIdentity: "test-log") { CharacterName = "Tester" };
            for (int i = 0; i < lines.Length; i++)
            {
                engine.Process(new(T0.AddSeconds(i), lines[i]));
            }
        }
        var fire = store.Get("Plane of Sky", "an azarack").Observations.Single(o => o.Type == ResistanceType.Fire);
        Assert.Equal(0, fire.Landed);
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
        tracker.OnEvent(new DamageEvent(T0.AddSeconds(31), Names.You, "A rat", 100, DamageKind.DirectSpell, "Conflagration", null), 4);
        tracker.Advance(T0.AddSeconds(40));
        var fire = store.Get("(unknown zone)", "A rat").Observations.Single(o => o.Type == ResistanceType.Fire);
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
        Assert.Equal(2, store.Get("(unknown zone)", "A rat").Observations.Single(o => o.Type == ResistanceType.Fire).Resisted);
    }

    [Fact]
    public void Store_NonDirectSpells_RetainsResistCountsWithoutInventingRate()
    {
        var store = new MobResistanceStore();
        store.Record("log", 1, "Fear", "A spider", "Ignite Blood IX", true);
        store.SetSpell(new("Ignite Blood IX", ResistanceType.Fire, "Fire (-100)", T0));
        var fire = store.Get("Fear", "A spider").Observations.Single(o => o.Type == ResistanceType.Fire);
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
        Assert.All(store.Get("Zone A", "A rat").Observations, o => Assert.Equal(0, o.Resisted));
        store.SetSpell(new("Mystery", ResistanceType.Disease, "Disease", T0));
        Assert.Equal(1, store.Get("Zone A", "A rat").Observations.Single(o => o.Type == ResistanceType.Disease).OtherResisted);
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
    public async Task Store_SaveAndLoad_PreservesObservationsWikiAndReplayWatermarks()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "mob-resistances.json");
        Directory.CreateDirectory(folder);
        try
        {
            var store = new MobResistanceStore();
            store.Record("log", 4, "Sky", "Heart harpie", "Conflagration X", true);
            store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", T0, SingleTargetDirectDamage: true));
            store.SetWiki("Heart harpie", MobWikiLookup.ParseMob("Heart harpie",
                "{{Namedmobpage\n| level = 58\n| MR = 100\n| FR = 0\n}}", T0));
            await store.SaveAsync(path);
            var loaded = await MobResistanceStore.LoadAsync(path);
            loaded.Record("LOG", 4, "Sky", "Heart harpie", "Conflagration X", true);
            var info = loaded.Get("Sky", "Heart harpie");
            Assert.Equal(1, info.Observations.Single(o => o.Type == ResistanceType.Fire).Resisted);
            Assert.Equal("0", info.Wiki?.Resistances[ResistanceType.Fire]);
            Assert.Equal("100", info.Wiki?.Resistances[ResistanceType.Magic]);
            Assert.Equal(T0, info.Wiki?.CheckedUtc);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Store_SpellCache_RenamesEarlierSpellsFileToSpellInfo()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "mob-resistances.json");
        string legacy = Path.Combine(folder, "mob-resistances.spells.jsonl");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(legacy,
                JsonSerializer.Serialize(new SpellResistanceInfo("Rend X", ResistanceType.Magic, "Magic", T0)) + "\n");

            var store = await MobResistanceStore.LoadAsync(path);

            Assert.Equal(ResistanceType.Magic, store.GetSpell("Rend X")?.Type);
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(Path.Combine(folder, "spell-info.jsonl")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Store_SpellCache_IsAppendedToItsOwnFileAndLegacyInlineSpellsMove()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "mob-resistances.json");
        string spells = MobResistanceStore.SpellCachePath(path);
        try
        {
            Directory.CreateDirectory(folder);
            var legacy = new MobResistanceStore.Data();
            legacy.Spells["REND X"] = new("Rend X", ResistanceType.Magic, "Magic", T0);
            File.WriteAllText(path, JsonSerializer.Serialize(legacy));

            var store = await MobResistanceStore.LoadAsync(path);
            Assert.Equal(ResistanceType.Magic, store.GetSpell("Rend X")?.Type);
            store.Record("log", 1, "Sky", "Azarack", "Rend X", true, T0);
            store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", T0, SingleTargetDirectDamage: true));
            await store.SaveAsync(path);

            Assert.Equal(Path.Combine(folder, "spell-info.jsonl"), spells);
            Assert.Equal(2, File.ReadAllLines(spells).Length);
            Assert.Contains("\"Spells\":{}", File.ReadAllText(path));
            Assert.DoesNotContain("DamageSamples", File.ReadAllText(path));

            var reloaded = await MobResistanceStore.LoadAsync(path);
            Assert.Equal(ResistanceType.Magic, reloaded.GetSpell("Rend X")?.Type);
            Assert.True(reloaded.GetSpell("Conflagration X")?.SingleTargetDirectDamage);

            reloaded.SetSpell(new("Rend X", ResistanceType.Cold, "Cold", T0.AddDays(1)));
            await reloaded.SaveAsync(path);
            Assert.Equal(3, File.ReadAllLines(spells).Length);
            Assert.Equal(ResistanceType.Cold, (await MobResistanceStore.LoadAsync(path)).GetSpell("Rend X")?.Type);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"Wiki":{"RAT":{"Resistances":null}}}""")]
    public async Task Store_CorruptData_ThrowsRatherThanOverwriting(string json)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            await Assert.ThrowsAsync<JsonException>(() => MobResistanceStore.LoadAsync(path));
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Wiki_MobWithoutResistanceFields_DoesNotInferFromDescriptionOrSpecial()
    {
        var info = MobWikiLookup.ParseMob("A Spinechiller Spider",
            "{{Namedmobpage\n| level = 48-52\n| class = [[Warrior|War]]\n| HP = 15750\n| special = Cold based DOT\n| description = Fire resistant\n}}", T0);
        Assert.Empty(info.Resistances);
        Assert.Equal("48-52", info.Level);
        Assert.Equal("War", info.Class);
        Assert.Equal("15750", info.HitPoints);
    }

    [Fact]
    public void Wiki_CyndreelaSpecialTraits_PreservesTraitsWithoutInventingElementalResists()
    {
        var info = MobWikiLookup.ParseMob("Cyndreela",
            "{{Namedmobpage\n| level = 40\n| class = [[Necromancer]]\n| special = Immune to Flee, See Invis\n}}", T0);
        Assert.Equal("Immune to Flee, See Invis", info.Special);
        Assert.Empty(info.Resistances);
    }

    [Fact]
    public void Wiki_CombatStats_PreservesNumbersRangesEstimatesAndZero()
    {
        var info = MobWikiLookup.ParseMob("Cyndreela", """
            {{Namedmobpage
            | race = [[Erudite]]
            | class = [[Necromancer]]
            | level = 40
            | AC = 279
            | HP = ~1750 (Estimated with no buffs or shielding up)
            | HP_regen = 1
            | mana_regen = 0
            | attacks_per_round = 2
            | attack_speed = 91%
            | damage_per_hit = 32 - 120
            | agro_radius = 70
            | run_speed = 1.6
            | respawn_time = 6min 40sec
            | location = 100% @ (-2370, 2604)
            }}
            """, T0);
        Assert.Equal("~1750 (Estimated with no buffs or shielding up)", info.HitPoints);
        var stats = Assert.IsType<MobWikiStats>(info.Stats);
        Assert.Equal("Erudite", stats.Race);
        Assert.Equal("279", stats.ArmorClass);
        Assert.Equal("1", stats.HpRegen);
        Assert.Equal("0", stats.ManaRegen);
        Assert.Equal("2", stats.AttacksPerRound);
        Assert.Equal("91%", stats.AttackSpeed);
        Assert.Equal("32 - 120", stats.DamagePerHit);
        Assert.Equal("70", stats.AggroRadius);
        Assert.Equal("1.6", stats.RunSpeed);
        Assert.Equal("6min 40sec", stats.RespawnTime);
        Assert.Equal("100% @ (-2370, 2604)", stats.Location);
    }

    [Fact]
    public void Wiki_MissingCombatStats_RemainUnknown()
    {
        var info = MobWikiLookup.ParseMob("Unknown", "{{Namedmobpage\n| AC = \n| HP_regen = \n}}", T0);
        var stats = Assert.IsType<MobWikiStats>(info.Stats);
        Assert.Null(stats.ArmorClass);
        Assert.Null(stats.HpRegen);
        Assert.Null(stats.Race);
        Assert.Null(stats.DamagePerHit);
    }

    [Fact]
    public async Task Store_SpecialTraits_RoundTripAndOldCacheCompatibility()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "mob-resistances.json");
        Directory.CreateDirectory(folder);
        try
        {
            var store = new MobResistanceStore();
            store.SetWiki("Cyndreela", MobWikiLookup.ParseMob("Cyndreela",
                "{{Namedmobpage\n| special = Immune to Flee, See Invis\n| AC = 279\n| HP = ~1750\n}}", T0));
            await store.SaveAsync(path);
            Assert.Equal("Immune to Flee, See Invis", (await MobResistanceStore.LoadAsync(path)).Get("Feerrott", "Cyndreela").Wiki?.Special);
            Assert.Equal("279", (await MobResistanceStore.LoadAsync(path)).Get("Feerrott", "Cyndreela").Wiki?.Stats?.ArmorClass);
            var oldCache = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var cachedMob = Assert.IsType<System.Text.Json.Nodes.JsonObject>(oldCache["Wiki"]!["CYNDREELA"]);
            Assert.True(cachedMob.Remove("Special"));
            Assert.True(cachedMob.Remove("Stats"));
            File.WriteAllText(path, oldCache.ToJsonString());
            var loaded = (await MobResistanceStore.LoadAsync(path)).Get("Feerrott", "Cyndreela").Wiki;
            Assert.NotNull(loaded);
            Assert.Null(loaded.Special);
            Assert.Null(loaded.Stats);
            Assert.Equal("~1750", loaded.HitPoints);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Wiki_SpellRankMissing_UsesBaseSpellResistanceAndNotName()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            requests.Add(query);
            return Page(query.Contains("Conflagration X", StringComparison.Ordinal)
                ? null : "{{Spellpage\n| duration = Instant\n| target_type = Single\n| slots = Decrease Hitpoints by 900\n| resist = Fire (-10)\n}}");
        }));
        var info = await new MobWikiLookup(http).FindSpellAsync("Conflagration X");
        Assert.Equal(ResistanceType.Fire, info.Type);
        Assert.Equal("Fire (-10)", info.Detail);
        Assert.True(info.SingleTargetDirectDamage);
        Assert.Equal(2, requests.Count);
        Assert.Contains("titles=Conflagration|", requests[1]);
    }

    [Fact]
    public async Task Wiki_InstantLifetap_IsComparableAndReclassifiesExistingLandingsWithoutReplay()
    {
        using var http = new HttpClient(new Handler(_ => Page("""
            {{Spellpagesmart
            | duration = Instant
            | target_type = Lifetap
            | slots = Decrease Hitpoints by 250
            | resist = Magic (-200)
            }}
            """)));
        var metadata = await new MobWikiLookup(http).FindSpellAsync("Life Leech X");
        Assert.True(metadata.SingleTargetDirectDamage);
        Assert.Equal("Lifetap", metadata.TargetType);
        Assert.Null(metadata.AssessmentNote);
        Assert.False(metadata.NeedsRefresh);
        var store = new MobResistanceStore();
        store.Record("log", 1, "Fear", "A warder", "Life Leech X", false, T0);
        store.Record("log", 2, "Fear", "A warder", "Life Leech X", true, T0);
        store.SetSpell(metadata with
        {
            SingleTargetDirectDamage = false,
            MetadataVersion = 0
        });
        Assert.Equal("Unknown", store.Get("Fear", "A warder").Observations.Single(o => o.Type == ResistanceType.Magic).Status);
        store.SetSpell(metadata);
        var observation = store.Get("Fear", "A warder").Observations.Single(o => o.Type == ResistanceType.Magic);
        Assert.Equal("Partially Resistant \u2014 50% (1/2)", observation.DisplayText);
    }

    [Theory]
    [InlineData("Targeted AE", "Instant", "Magic", "Area-effect")]
    [InlineData("PB AE", "Instant", "Cold", "Area-effect")]
    [InlineData("Single", "3 ticks", "Poison", "Duration spell")]
    [InlineData("Group", "Instant", "Magic", "Wiki target: Group")]
    public async Task Wiki_NonComparableSpell_PreservesSchoolAndSpecificAssessmentReason(
        string target, string duration, string school, string reason)
    {
        using var http = new HttpClient(new Handler(_ => Page(
            $"{{{{Spellpagesmart\n| duration = {duration}\n| target_type = {target}\n| slots = Decrease Hitpoints by 250\n| resist = {school} (-10)\n}}}}")));
        var metadata = await new MobWikiLookup(http).FindSpellAsync("Test spell");
        Assert.Equal(Enum.Parse<ResistanceType>(school), metadata.Type);
        Assert.False(metadata.SingleTargetDirectDamage);
        Assert.Contains(reason, metadata.AssessmentNote);
        Assert.False(metadata.NeedsRefresh);
    }

    [Fact]
    public void SpellMetadata_OldCacheRefreshesWithoutLosingExistingObservations()
    {
        var old = JsonSerializer.Deserialize<SpellResistanceInfo>(
            """{"Spell":"Life Leech X","Type":0,"Detail":"Magic (-200)","CheckedUtc":"2099-01-01T00:00:00Z","SingleTargetDirectDamage":false}""")!;
        Assert.True(old.NeedsRefresh);
        Assert.Contains("missing", old.AssessmentNote);
        var refreshed = old with
        {
            SingleTargetDirectDamage = true,
            TargetType = "Lifetap",
            Duration = "Instant",
            MetadataVersion = SpellResistanceInfo.CurrentMetadataVersion,
            CheckedUtc = DateTime.UtcNow,
        };
        var loaded = JsonSerializer.Deserialize<SpellResistanceInfo>(JsonSerializer.Serialize(refreshed))!;
        Assert.False(loaded.NeedsRefresh);
        Assert.Null(loaded.AssessmentNote);
        Assert.True((loaded with
        {
            CheckedUtc = DateTime.UtcNow.AddDays(-8)
        }).NeedsRefresh);
    }

    [Fact]
    public async Task Wiki_MissingMobAndUnresistableSpell_RemainUnknown()
    {
        using var http = new HttpClient(new Handler(_ => Page(null)));
        var lookup = new MobWikiLookup(http);
        Assert.False((await lookup.FindMobAsync("Unknown NPC")).Found);
        Assert.Null((await lookup.FindSpellAsync("Fire sounding name")).Type);
        using var unresistable = new HttpClient(new Handler(_ => Page("{{Spellpage\n| resist = Unresistable\n}}")));
        Assert.Null((await new MobWikiLookup(unresistable).FindSpellAsync("Unknown")).Type);
    }

    [Fact]
    public async Task Wiki_ApiErrorsAndHttpErrors_AreExplicit()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new MobWikiLookup(http).FindMobAsync("A rat"));
        using var api = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":{"info":"maxlag exceeded"}}"""),
        }));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new MobWikiLookup(api).FindMobAsync("A rat"));
        Assert.Contains("maxlag exceeded", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"query":{"pages":[{"title":"Unreadable"}]}}""")]
    public async Task Wiki_UnexpectedPayload_ReportsExplicitFailure(string json)
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        }));
        await Assert.ThrowsAsync<JsonException>(() => new MobWikiLookup(http).FindMobAsync("A rat"));
    }

    [Fact]
    public async Task Wiki_NonSpellPage_DoesNotClassifySpell()
    {
        using var http = new HttpClient(new Handler(_ => Page("{{Namedmobpage\n| resist = Fire\n}}")));
        var result = await new MobWikiLookup(http).FindSpellAsync("A misleading name");
        Assert.Null(result.Type);
        Assert.False(result.SingleTargetDirectDamage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tracker_InterruptedOrFizzledCast_DoesNotCountLandedDamage(bool fizzled)
    {
        var store = new MobResistanceStore();
        var tracker = new MobResistanceTracker(store, "log");
        tracker.OnEvent(new CastBeginEvent(T0, "Conflagration"), 1);
        tracker.OnEvent(fizzled ? new CastFizzleEvent(T0.AddSeconds(1)) :
            new CastInterruptedEvent(T0.AddSeconds(1), null), 2);
        tracker.OnEvent(new DamageEvent(T0.AddSeconds(3), Names.You, "A rat", 100,
            DamageKind.DirectSpell, "Conflagration", null), 3);
        Assert.Empty(store.Get("(unknown zone)", "A rat").Spells);
    }

    private static HttpResponseMessage Page(string? text)
    {
        object page = text is null
            ? new
            {
                title = "Missing",
                missing = true
            }
            : new
            {
                title = "Resolved page",
                revisions = new[] { new { slots = new { main = new { content = text } } } },
            };
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                query = new
                {
                    pages = new[] { page }
                }
            }),
                Encoding.UTF8, "application/json"),
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
