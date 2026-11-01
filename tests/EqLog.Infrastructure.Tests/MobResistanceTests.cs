using System.Net;
using System.Text;
using System.Text.Json;
using EqLog.Infrastructure.GameFiles;
using EqLog.Infrastructure.Persistence;
using EqLog.Infrastructure.Wiki;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class MobResistanceTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 19, 0, 0);

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
            var loaded = await TrackingTestPersistence.LoadResistancesAsync(path);
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

            var store = await TrackingTestPersistence.LoadResistancesAsync(path);

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
        string spells = MobResistanceRepository.SpellCachePath(path);
        try
        {
            Directory.CreateDirectory(folder);
            var legacy = new MobResistanceStore.Data();
            legacy.Spells["REND X"] = new("Rend X", ResistanceType.Magic, "Magic", T0);
            File.WriteAllText(path, JsonSerializer.Serialize(legacy));

            var store = await TrackingTestPersistence.LoadResistancesAsync(path);
            Assert.Equal(ResistanceType.Magic, store.GetSpell("Rend X")?.Type);
            store.Record("log", 1, "Sky", "Azarack", "Rend X", true, T0);
            store.SetSpell(new("Conflagration X", ResistanceType.Fire, "Fire", T0, SingleTargetDirectDamage: true));
            await store.SaveAsync(path);

            Assert.Equal(Path.Combine(folder, "spell-info.jsonl"), spells);
            Assert.Equal(2, File.ReadAllLines(spells).Length);
            Assert.Contains("\"Spells\":{}", File.ReadAllText(path));
            Assert.DoesNotContain("DamageSamples", File.ReadAllText(path));

            var reloaded = await TrackingTestPersistence.LoadResistancesAsync(path);
            Assert.Equal(ResistanceType.Magic, reloaded.GetSpell("Rend X")?.Type);
            Assert.True(reloaded.GetSpell("Conflagration X")?.SingleTargetDirectDamage);

            reloaded.SetSpell(new("Rend X", ResistanceType.Cold, "Cold", T0.AddDays(1)));
            await reloaded.SaveAsync(path);
            Assert.Equal(3, File.ReadAllLines(spells).Length);
            Assert.Equal(ResistanceType.Cold, (await TrackingTestPersistence.LoadResistancesAsync(path)).GetSpell("Rend X")?.Type);
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
            await Assert.ThrowsAsync<JsonException>(() => TrackingTestPersistence.LoadResistancesAsync(path));
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
            Assert.Equal("Immune to Flee, See Invis", (await TrackingTestPersistence.LoadResistancesAsync(path)).Get("Feerrott", "Cyndreela").Wiki?.Special);
            Assert.Equal("279", (await TrackingTestPersistence.LoadResistancesAsync(path)).Get("Feerrott", "Cyndreela").Wiki?.Stats?.ArmorClass);
            var oldCache = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var cachedMob = Assert.IsType<System.Text.Json.Nodes.JsonObject>(oldCache["Wiki"]!["CYNDREELA"]);
            Assert.True(cachedMob.Remove("Special"));
            Assert.True(cachedMob.Remove("Stats"));
            File.WriteAllText(path, oldCache.ToJsonString());
            var loaded = (await TrackingTestPersistence.LoadResistancesAsync(path)).Get("Feerrott", "Cyndreela").Wiki;
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
        var info = await new MobWikiClient(http).FindSpellAsync("Conflagration X");
        Assert.Equal(ResistanceType.Fire, info.Type);
        Assert.Equal("Fire (-10)", info.Detail);
        Assert.True(info.SingleTargetDirectDamage);
        Assert.Equal(2, requests.Count);
        Assert.Contains("titles=Conflagration|", requests[1]);
    }

    [Theory]
    [InlineData("Bond of Death X", "9 ticks", "{{SpellSlotRow | 3 | Decrease Hitpoints by 80 per tick }}", "Magic (-200)", ResistanceType.Magic)]
    [InlineData("Envenomed Bolt X", "36 Sec", "{{SpellSlotRow | 2 | Decrease Current Hit Points by 41}}\n{{SpellSlotRow | 3 | Decrease Current Hit Points by 351 per Tick }}", "Poison (0)", ResistanceType.Poison)]
    [InlineData("Dark Soul IV", "30 Sec", "{{SpellSlotRowSmart | 2 | Decrease Hitpoints between 96 and 140 per tick. }}", "Magic", ResistanceType.Magic)]
    [InlineData("Ignite Blood IX", "42 Sec", "{{SpellSlotRow | 1 | Decrease Hitpoints by 56 per tick }}", "Fire (-100)", ResistanceType.Fire)]
    public async Task Wiki_DamageOverTimeSpells_AreDurationSpellsWithResistType(string spell, string duration, string slots, string resist, ResistanceType type)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            return Page(query.Contains(spell, StringComparison.Ordinal) ? null :
                $"{{{{Spellpage|\n| slots = \n\n{slots}\n\n| duration = {duration}\n| target_type = Single\n| spell_type = Detrimental\n| resist = {resist}\n}}}}");
        }));
        var info = await new MobWikiClient(http).FindSpellAsync(spell);
        Assert.True(info.Found);
        Assert.Equal(type, info.Type);
        Assert.Equal(resist, info.Detail);
        Assert.Equal(duration, info.Duration);
        Assert.False(info.SingleTargetDirectDamage);
        Assert.True(info.DamageOverTime);
        Assert.Equal(SpellAssessmentKind.DamageOverTime, info.AssessmentKind);
    }

    [Fact]
    public async Task Wiki_DurationDebuffWithoutDamage_IsNotDamageOverTime()
    {
        using var http = new HttpClient(new Handler(_ => Page(
            "{{Spellpage|\n| slots = \n{{SpellSlotRow | 1 | Decrease Movement by 40% }}\n| duration = 2 Min\n| target_type = Single\n| resist = Magic\n}}")));
        var info = await new MobWikiClient(http).FindSpellAsync("Clinging Darkness");
        Assert.False(info.DamageOverTime);
        Assert.Equal(SpellAssessmentKind.Duration, info.AssessmentKind);
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
        var metadata = await new MobWikiClient(http).FindSpellAsync("Life Leech X");
        Assert.True(metadata.SingleTargetDirectDamage);
        Assert.Equal("Lifetap", metadata.TargetType);
        Assert.Equal(SpellAssessmentKind.DirectDamage, metadata.AssessmentKind);
        Assert.False(metadata.NeedsRefresh);
        var store = new MobResistanceStore();
        store.Record("log", 1, "Fear", "A warder", "Life Leech X", false, T0);
        store.Record("log", 2, "Fear", "A warder", "Life Leech X", true, T0);
        store.SetSpell(metadata with
        {
            SingleTargetDirectDamage = false,
            MetadataVersion = 0
        });
        Assert.Equal(ResistanceStatus.Unknown, store.Get("Fear", "A warder").Observations.Single(o => o.Type == ResistanceType.Magic).Status);
        store.SetSpell(metadata);
        var observation = store.Get("Fear", "A warder").Observations.Single(o => o.Type == ResistanceType.Magic);
        Assert.Equal(ResistanceStatus.PartiallyResistant, observation.Status);
        Assert.Equal(50m, observation.ResistPercent);
    }

    [Theory]
    [InlineData("Targeted AE", "Instant", "Magic", SpellAssessmentKind.AreaEffect)]
    [InlineData("PB AE", "Instant", "Cold", SpellAssessmentKind.AreaEffect)]
    [InlineData("Single", "3 ticks", "Poison", SpellAssessmentKind.Duration)]
    [InlineData("Group", "Instant", "Magic", SpellAssessmentKind.UnverifiedTarget)]
    public async Task Wiki_NonComparableSpell_PreservesSchoolAndSpecificAssessmentReason(
        string target, string duration, string school, SpellAssessmentKind reason)
    {
        using var http = new HttpClient(new Handler(_ => Page(
            $"{{{{Spellpagesmart\n| duration = {duration}\n| target_type = {target}\n| slots = Decrease Hitpoints by 250\n| resist = {school} (-10)\n}}}}")));
        var metadata = await new MobWikiClient(http).FindSpellAsync("Test spell");
        Assert.Equal(Enum.Parse<ResistanceType>(school), metadata.Type);
        Assert.False(metadata.SingleTargetDirectDamage);
        Assert.Equal(reason, metadata.AssessmentKind);
        Assert.False(metadata.NeedsRefresh);
    }

    [Fact]
    public void SpellMetadata_OldCacheRefreshesWithoutLosingExistingObservations()
    {
        var old = JsonSerializer.Deserialize<SpellResistanceInfo>(
            """{"Spell":"Life Leech X","Type":0,"Detail":"Magic (-200)","CheckedUtc":"2099-01-01T00:00:00Z","SingleTargetDirectDamage":false}""")!;
        Assert.True(old.NeedsRefresh);
        Assert.Equal(SpellAssessmentKind.MissingDetails, old.AssessmentKind);
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
        Assert.Equal(SpellAssessmentKind.DirectDamage, loaded.AssessmentKind);
        Assert.True((loaded with
        {
            CheckedUtc = DateTime.UtcNow.AddDays(-8)
        }).NeedsRefresh);
    }

    [Fact]
    public async Task Wiki_MissingMobAndUnresistableSpell_RemainUnknown()
    {
        using var http = new HttpClient(new Handler(_ => Page(null)));
        var lookup = new MobWikiClient(http);
        Assert.False((await lookup.FindMobAsync("Unknown NPC")).Found);
        Assert.Null((await lookup.FindSpellAsync("Fire sounding name")).Type);
        using var unresistable = new HttpClient(new Handler(_ => Page("{{Spellpage\n| resist = Unresistable\n}}")));
        Assert.Null((await new MobWikiClient(unresistable).FindSpellAsync("Unknown")).Type);
    }

    [Fact]
    public async Task Wiki_ApiErrorsAndHttpErrors_AreExplicit()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new MobWikiClient(http).FindMobAsync("A rat"));
        using var api = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":{"info":"maxlag exceeded"}}"""),
        }));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new MobWikiClient(api).FindMobAsync("A rat"));
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
        await Assert.ThrowsAsync<JsonException>(() => new MobWikiClient(http).FindMobAsync("A rat"));
    }

    [Fact]
    public async Task Wiki_NonSpellPage_DoesNotClassifySpell()
    {
        using var http = new HttpClient(new Handler(_ => Page("{{Namedmobpage\n| resist = Fire\n}}")));
        var result = await new MobWikiClient(http).FindSpellAsync("A misleading name");
        Assert.Null(result.Type);
        Assert.False(result.SingleTargetDirectDamage);
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
