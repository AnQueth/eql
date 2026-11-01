using System.Net;
using System.Text.Json;
using EqLog.Core.Tracking;

using EqLog.Core;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Infrastructure.Tests;

public sealed class WikiCacheResetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "EqLogWikiReset-" + Guid.NewGuid().ToString("N"));

    public WikiCacheResetTests()
    {
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public async Task ResetAsync_RemovesAllCurrentAndLegacyCaches_PreservesHistoryAndSettings()
    {
        string path = Path.Combine(_folder, "mob-resistances.json");
        var store = new MobResistanceStore();
        var time = new DateTime(2026, 10, 20, 12, 0, 0);
        store.Record("log", 1, "Zone", "NPC", "Spell", false, time, "Player", 1700);
        store.ResetResistances("Other zone", "Other NPC", time);
        store.SetWiki("NPC", MobWikiLookup.ParseMob("NPC", "{{Namedmobpage}}", DateTime.UtcNow));
        store.SetSpell(new("Spell", ResistanceType.Fire, "url", DateTime.UtcNow, true, "Single", "Instant",
            SpellResistanceInfo.CurrentMetadataVersion));
        await new MobResistanceRepository().SaveAsync(path, store);
        var before = await AtomicFile.ReadJsonAsync<MobResistanceStore.Data>(path);
        string[] caches =
        [
            "quest-lookups.jsonl", "quest-lookups.json", "sky-quests.json", "item-effects.json",
            "mob-resistances.spells.jsonl",
        ];
        foreach (string name in caches)
        {
            await File.WriteAllTextAsync(Path.Combine(_folder, name), "old cache");
        }
        string[] preserved = ["settings.json", "quest-inventory.json", "npc-compendium.json", "spawn-timers.json"];
        foreach (string name in preserved)
        {
            await File.WriteAllTextAsync(Path.Combine(_folder, name), "keep");
        }
        // Include an unsaved spell so a subsequent save cannot append stale pending metadata.
        store.SetSpell(new("Pending", ResistanceType.Cold, "url", DateTime.UtcNow));

        await WikiCacheReset.ResetAsync(_folder, store);
        await new MobResistanceRepository().SaveAsync(path, store);

        Assert.Null(store.Get("Zone", "NPC").Wiki);
        Assert.Null(store.GetSpell("Spell"));
        Assert.Null(store.GetSpell("Pending"));
        foreach (string name in caches.Append("spell-info.jsonl"))
        {
            Assert.False(File.Exists(Path.Combine(_folder, name)));
        }
        foreach (string name in preserved)
        {
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(_folder, name)));
        }
        var after = await AtomicFile.ReadJsonAsync<MobResistanceStore.Data>(path);
        Assert.Equal(JsonSerializer.Serialize(before!.Mobs), JsonSerializer.Serialize(after!.Mobs));
        Assert.Equal(JsonSerializer.Serialize(before.Watermarks), JsonSerializer.Serialize(after.Watermarks));
        Assert.Equal(JsonSerializer.Serialize(before.ResistanceResetTimes), JsonSerializer.Serialize(after.ResistanceResetTimes));
        Assert.Empty(after.Wiki);
        Assert.Empty(after.Spells);
        var loaded = await new MobResistanceRepository().LoadAsync(path);
        Assert.Null(loaded.GetSpell("Spell"));
        Assert.Null(loaded.Get("Zone", "NPC").Wiki);
        Assert.Equal(["Spell"], loaded.Get("Zone", "NPC").Spells);
        loaded.Record("copy", 1, "Other zone", "Other NPC", "Spell", true, time);
        Assert.Empty(loaded.Get("Other zone", "Other NPC").Spells);
    }

    [Fact]
    public async Task RepositoryReset_AllFiveTypesFetchAgainWithoutForce()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Response(request.RequestUri!.Query.Contains("action=parse")
                ? """{"error":{"info":"Sky unavailable"}}"""
                : """{"query":{"pages":[{"title":"Missing","missing":true}]}}"""));
        }));
        var mobs = new MobResistanceStore();
        var now = DateTime.UtcNow;
        mobs.SetWiki("NPC", MobWikiLookup.ParseMob("NPC", "{{Namedmobpage}}", now) with
        {
            Found = false
        });
        mobs.SetSpell(new("Spell", null, null, now, MetadataVersion: SpellResistanceInfo.CurrentMetadataVersion, Found: false));
        await using var repository = new WikiRepository(http, mobs, () => Task.CompletedTask, TimeSpan.Zero);
        repository.SeedQuest(new("Item", now, [], false));
        repository.SeedItemEffects(new("Effect", "url", now, false, [], ""));
        repository.SeedSky(new(now, [new("Bard", "Quest", "Reward", "Giver", "", "", "", "url", [], [], [])],
            new Dictionary<string, IReadOnlyList<string>>()));
        await repository.GetMobAsync("NPC", WikiPriority.Interactive, false, default);
        await repository.GetSpellAsync("Spell", WikiPriority.Interactive, false, default);
        await repository.GetQuestAsync("Item", WikiPriority.Interactive, false, default);
        await repository.GetItemEffectsAsync("Effect", WikiPriority.Interactive, false, default);
        await repository.GetSkyAsync(WikiPriority.Interactive, false, default);
        Assert.Equal(0, requests);

        await repository.ResetCachesAsync(() => WikiCacheReset.ResetAsync(_folder, mobs));

        Func<Task>[] lookups =
        [
            () => repository.GetMobAsync("NPC", WikiPriority.Interactive, false, default),
            () => repository.GetSpellAsync("Spell", WikiPriority.Interactive, false, default),
            () => repository.GetQuestAsync("Item", WikiPriority.Interactive, false, default),
            () => repository.GetItemEffectsAsync("Effect", WikiPriority.Interactive, false, default),
        ];
        foreach (var lookup in lookups)
        {
            int before = requests;
            await lookup();
            Assert.True(requests > before);
        }
        int beforeSky = requests;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetSkyAsync(WikiPriority.Interactive, false, default));
        Assert.True(requests > beforeSky);
    }

    [Fact]
    public async Task RepositoryReset_CancelsOldRequests_BlocksNewRequestsUntilStorageFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return Response("""{"query":{"pages":[{"title":"Missing","missing":true}]}}""");
        }));
        var mobs = new MobResistanceStore();
        await using var repository = new WikiRepository(http, mobs, () => Task.CompletedTask, TimeSpan.Zero);
        var old = repository.GetSpellAsync("Spell", WikiPriority.Background, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reset = repository.ResetCachesAsync(async () =>
        {
            clearing.TrySetResult();
            await release.Task;
            await WikiCacheReset.ResetAsync(_folder, mobs);
        });
        await clearing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.GetSpellAsync("New", WikiPriority.Interactive, false, default));
        Assert.Null(mobs.GetSpell("Spell"));
        release.TrySetResult();
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        await repository.GetSpellAsync("Spell", WikiPriority.Interactive, false, default);
        Assert.NotNull(mobs.GetSpell("Spell"));
    }

    [Fact]
    public async Task RepositoryReset_WaitsForAnAlreadyRunningSave_BeforeClearingMetadata()
    {
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) =>
            Task.FromResult(Response("""{"query":{"pages":[{"missing":true}]}}"""))));
        var mobs = new MobResistanceStore();
        await using var repository = new WikiRepository(http, mobs, async () =>
        {
            saving.TrySetResult();
            await release.Task;
        }, TimeSpan.Zero);
        var lookup = repository.GetSpellAsync("Spell", WikiPriority.Interactive, false, default);
        await saving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(mobs.GetSpell("Spell"));

        var reset = repository.ResetCachesAsync(() => WikiCacheReset.ResetAsync(_folder, mobs));

        Assert.False(reset.IsCompleted);
        release.TrySetResult();
        await lookup.WaitAsync(TimeSpan.FromSeconds(5));
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(mobs.GetSpell("Spell"));
        Assert.Null((await new MobResistanceRepository().LoadAsync(Path.Combine(_folder, "mob-resistances.json"))).GetSpell("Spell"));
    }

    [Fact]
    public async Task RepositoryReset_ClearsBackgroundFailureRetrySuppression()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            retried.TrySetResult();
            return Task.FromResult(Response("""{"query":{"pages":[{"missing":true}]}}"""));
        }));
        var mobs = new MobResistanceStore();
        await using var repository = new WikiRepository(http, mobs, () => Task.CompletedTask, TimeSpan.Zero);
        repository.BackgroundFailed += (_, _) => failed.TrySetResult();
        repository.ObserveSpell("Spell");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await repository.ResetCachesAsync(() => WikiCacheReset.ResetAsync(_folder, mobs));
        repository.ObserveSpell("Spell");

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await repository.GetSpellAsync("Spell", WikiPriority.Interactive, false, default);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ResetAsync_FileErrorIsReported_AndRetryCanFinish()
    {
        string blocked = Path.Combine(_folder, "quest-lookups.jsonl");
        Directory.CreateDirectory(blocked);
        var mobs = new MobResistanceStore();
        using var http = new HttpClient(new Handler((_, _) =>
            Task.FromResult(Response("""{"query":{"pages":[{"missing":true}]}}"""))));
        await using var repository = new WikiRepository(http, mobs, () => Task.CompletedTask, TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            repository.ResetCachesAsync(() => WikiCacheReset.ResetAsync(_folder, mobs)));
        Directory.Delete(blocked);
        await repository.ResetCachesAsync(() => WikiCacheReset.ResetAsync(_folder, mobs));
        await repository.GetQuestAsync("Item", WikiPriority.Interactive, false, default);
    }

    [Fact]
    public async Task DerivedTimerReset_PreservesLearnedSamplesAndOverrides_AcrossRestart()
    {
        var duration = new SpellDurationStore();
        duration.AddSample("Learned", 60, DateTime.Now);
        duration.SetOverride("Override", 70);
        duration.SetWikiDuration("Learned", 120);
        duration.SetWikiDuration("Override", 120);
        duration.SetWikiDuration("Wiki only", 120);
        var spawn = new SpawnStore();
        spawn.AddSample("Zone", "Learned", DateTime.Now, 60);
        spawn.SetOverride("Zone", "Override", 70);
        spawn.Remember("Zone", "Wiki only");
        foreach (string mob in new[] { "Learned", "Override", "Wiki only" })
        {
            spawn.SetWikiRespawn(mob, 120);
        }

        duration.ResetWikiCache();
        spawn.ResetWikiCache();
        string path = Path.Combine(_folder, "spell-durations.json");
        var persistence = new EqLog.Application.Tracking.TrackingPersistenceCoordinator(
            new SpellDurationRepository(), new SpawnRepository(), new MoteRepository());
        await persistence.SaveSpellDurationsAsync(new LogEngine(duration), path, onlyIfDirty: false);
        var loaded = await new SpellDurationRepository().LoadAsync(path);
        Assert.Equal(60, loaded.GetDuration("Learned"));
        Assert.Equal(70, loaded.GetDuration("Override"));
        Assert.Null(loaded.GetDuration("Wiki only"));
        Assert.All(loaded.List(), entry => Assert.Null(entry.WikiSeconds));
        Assert.Equal(60, spawn.Get("Zone", "Learned")!.RespawnSeconds);
        Assert.Equal(70, spawn.Get("Zone", "Override")!.RespawnSeconds);
        Assert.Null(spawn.Get("Zone", "Wiki only")!.RespawnSeconds);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private static HttpResponseMessage Response(string json)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }
}
