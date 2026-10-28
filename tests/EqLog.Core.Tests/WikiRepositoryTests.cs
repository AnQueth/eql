using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using EqLog.Core.Parsing;

namespace EqLog.Core.Tests;

public sealed class WikiRepositoryTests
{
    [Theory]
    [InlineData(false, 23, false)]
    [InlineData(false, 25, true)]
    [InlineData(true, 25, false)]
    [InlineData(true, 169, true)]
    public void MissingPageCache_ExpiresAfterOneDay_NotSeven(bool found, int hours, bool expired)
    {
        var date = DateTime.UtcNow.AddHours(-hours);
        Assert.Equal(expired, new QuestLookupResult("Item", date, [], found).NeedsRefresh);
        Assert.Equal(expired, new SpellResistanceInfo("Spell", null, null, date,
            MetadataVersion: SpellResistanceInfo.CurrentMetadataVersion, Found: found).NeedsRefresh);
    }

    [Fact]
    public async Task MissingQuest_IsPersistable_ReusedForDay_AndRetriedAfterExpiry()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(Response("""{"query":{"pages":[{"title":"Missing","missing":true}]}}"""));
        }));
        await using var repository = new WikiRepository(http, new MobResistanceStore(), () =>
        {
        }, TimeSpan.Zero);
        var missing = await repository.GetQuestAsync("Missing", WikiPriority.Background, false, default);
        Assert.False(missing.Found);
        var persisted = JsonSerializer.Deserialize<QuestLookupResult>(JsonSerializer.Serialize(missing))!;
        Assert.False(persisted.Found);
        repository.SeedQuest(persisted with
        {
            CheckedUtc = DateTime.UtcNow.AddHours(-23)
        });
        Assert.False((await repository.GetQuestAsync("Missing", WikiPriority.Background, false, default)).Found);
        Assert.Equal(1, requests);
        repository.SeedQuest(persisted with
        {
            CheckedUtc = DateTime.UtcNow.AddHours(-25)
        });
        await repository.GetQuestAsync("Missing", WikiPriority.Background, false, default);
        Assert.Equal(2, requests);
        await repository.GetQuestAsync("Missing", WikiPriority.Background, true, default);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task Activity_ReportsSendingQueuePromotionAndCompletion()
    {
        var entered = Signal();
        var release = Signal();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response("{}");
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        using var first = Request("first");
        using var second = Request("second");
        var sending = scheduler.SendAsync(first, new(WikiPriority.Background, "spell:first"), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = new WikiRequestContext(WikiPriority.Background, "mob:second");
        var queued = scheduler.SendAsync(second, context, default);
        context.Promote(WikiPriority.RealTime);
        var snapshot = scheduler.Snapshot();
        Assert.Equal(2, snapshot.Requests.Count);
        Assert.Equal("Sending", snapshot.Requests[0].State);
        var pending = Assert.Single(snapshot.Requests, r => r.Name == "mob:second");
        Assert.Equal("Queued / pacing", pending.State);
        Assert.Equal(WikiPriority.RealTime, pending.Priority);
        Assert.EndsWith("/second", pending.Url);
        release.TrySetResult();
        using var a = await sending.WaitAsync(TimeSpan.FromSeconds(5));
        using var b = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(scheduler.Snapshot().Requests);
        Assert.Equal(2, scheduler.Snapshot().Recent.Count);
        Assert.All(scheduler.Snapshot().Recent, e => Assert.False(e.IsError));
        Assert.Equal(2, snapshot.Requests.Count);
    }

    [Fact]
    public async Task Activity_ReportsRetryBackoffAndRemovesCanceledRequest()
    {
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        using var message = Request("retry");
        using var cancel = new CancellationTokenSource();
        var work = scheduler.SendAsync(message, new(WikiPriority.Background, "spell:retry"), cancel.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!scheduler.Snapshot().Recent.Any(e => e.Message.Contains("retrying")))
        {
            await Task.Delay(10, timeout.Token);
        }
        var snapshot = scheduler.Snapshot();
        Assert.Contains("Retry backoff", Assert.Single(snapshot.Requests).State);
        Assert.Contains(snapshot.Recent, e => e.IsError && e.Message.Contains("429"));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Empty(scheduler.Snapshot().Requests);
    }

    [Fact]
    public async Task Activity_BoundsRecentErrorsAndKeepsNewestFirst()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{}"))));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        for (int i = 0; i < 50; i++)
        {
            scheduler.RecordFailure("quest:" + i, new InvalidOperationException("Bad wiki data " + i));
        }
        var snapshot = scheduler.Snapshot();
        Assert.Equal(30, snapshot.Recent.Count);
        Assert.Equal("quest:49", snapshot.Recent[0].Name);
        Assert.All(snapshot.Recent, e => Assert.True(e.IsError));
    }

    [Fact]
    public async Task Activity_RepositoryReportsParserErrors()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("not json"))));
        await using var repository = new WikiRepository(http, new MobResistanceStore(), () =>
        {
        }, TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            repository.GetMobAsync("broken", WikiPriority.Interactive, true, default));
        Assert.Contains(repository.GetActivity().Recent, e => e.IsError && e.Name == "mob:BROKEN");
        Assert.Empty(repository.GetActivity().Requests);
    }

    [Theory]
    [InlineData(408, 4)]
    [InlineData(429, 4)]
    [InlineData(500, 4)]
    [InlineData(503, 4)]
    [InlineData(400, 1)]
    [InlineData(404, 1)]
    [InlineData(401, 1)]
    public async Task Scheduler_PollyRetriesOnlyTransientStatuses_AndBoundsAttempts(int status, int expected)
    {
        int attempts = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        using var request = Request("retry");
        using var response = await scheduler.SendAsync(request, new(WikiPriority.Background), default);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(expected, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduler_PollyRetriesNetworkFailuresAndHttpTimeouts(bool timeout)
    {
        int attempts = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
            {
                if (timeout)
                {
                    throw new TaskCanceledException("HTTP timeout", new TimeoutException());
                }
                throw new HttpRequestException("Temporary connection failure");
            }
            return Task.FromResult(Response("{}"));
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        using var request = Request("retry");
        using var response = await scheduler.SendAsync(request, new(WikiPriority.Background), default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduler_RetryAfterYieldsLane_AndCancellationStopsBackoff(bool dateHeader)
    {
        var failed = Signal();
        int attempts = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/retry")
            {
                Interlocked.Increment(ref attempts);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = dateHeader
                    ? new(DateTimeOffset.UtcNow.AddMinutes(1)) : new(TimeSpan.FromSeconds(60));
                failed.TrySetResult();
                return Task.FromResult(response);
            }
            return Task.FromResult(Response("{}"));
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        using var request = Request("retry");
        using var con = Request("con");
        using var cancel = new CancellationTokenSource();
        var retry = scheduler.SendAsync(request, new(WikiPriority.Background), cancel.Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var urgent = await scheduler.SendAsync(con, new(WikiPriority.RealTime), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.False(retry.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Scheduler_PollyDisposesRetriedResponses_ButLeavesFinalResponseToCaller()
    {
        var responses = new List<HttpResponseMessage>();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(responses.Count < 2
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
            responses.Add(response);
            return Task.FromResult(response);
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        using var request = Request("retry");
        using var result = await scheduler.SendAsync(request, new(WikiPriority.Background), default);
        Assert.Equal(3, responses.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => responses[0].Content.ReadAsStringAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => responses[1].Content.ReadAsStringAsync());
        Assert.Equal("{}", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Scheduler_ExhaustedNetworkRetriesPropagateOriginalFailure()
    {
        int attempts = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("Still offline");
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        using var request = Request("retry");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            scheduler.SendAsync(request, new(WikiPriority.Background), default));
        Assert.Equal("Still offline", error.Message);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task Scheduler_RealTimeThenInteractiveThenBackground_WithFifoAndPromotion()
    {
        var entered = Signal();
        var release = Signal();
        var order = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string name = request.RequestUri!.AbsolutePath;
            order.Enqueue(name);
            if (name == "/active")
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return Response("{}");
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        using var active = new HttpRequestMessage(HttpMethod.Get, "https://eqlwiki.com/active");
        var first = scheduler.SendAsync(active, new(WikiPriority.Background), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var promoted = new WikiRequestContext(WikiPriority.Background);
        using var low1 = Request("low1");
        using var low2 = Request("low2");
        using var user = Request("user");
        using var urgent = Request("urgent");
        using var duplicate = Request("promoted");
        var tasks = new[]
        {
            first,
            scheduler.SendAsync(low1, new(WikiPriority.Background), default),
            scheduler.SendAsync(low2, new(WikiPriority.Background), default),
            scheduler.SendAsync(user, new(WikiPriority.Interactive), default),
            scheduler.SendAsync(urgent, new(WikiPriority.RealTime), default),
            scheduler.SendAsync(duplicate, promoted, default),
        };
        promoted.Promote(WikiPriority.RealTime);
        promoted.Promote(WikiPriority.Background);
        release.SetResult();
        foreach (var response in await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            response.Dispose();
        }
        Assert.Equal(["/active", "/urgent", "/promoted", "/user", "/low1", "/low2"], order);
    }

    [Fact]
    public async Task Scheduler_AgesBackgroundWithoutLettingOldBacklogBlockRealTime()
    {
        var entered = Signal();
        var release = Signal();
        var order = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            order.Enqueue(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/first")
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return Response("{}");
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.Zero, TimeSpan.FromMilliseconds(40));
        using var first = Request("first");
        using var low = Request("old-background");
        using var low2 = Request("old-background-2");
        var initial = scheduler.SendAsync(first, new(WikiPriority.RealTime), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var background = scheduler.SendAsync(low, new(WikiPriority.Background), default);
        var background2 = scheduler.SendAsync(low2, new(WikiPriority.Background), default);
        var urgent = Enumerable.Range(1, 10).Select(i => Request("con" + i)).ToArray();
        try
        {
            var tasks = urgent.Select(r => scheduler.SendAsync(r, new(WikiPriority.RealTime), default)).ToArray();
            await Task.Delay(70);
            release.SetResult();
            foreach (var response in await Task.WhenAll(tasks.Append(initial).Append(background).Append(background2))
                .WaitAsync(TimeSpan.FromSeconds(5)))
            {
                response.Dispose();
            }
            Assert.Equal("/old-background", order.ToArray()[8]);
            Assert.Equal("/con8", order.ToArray()[9]);
            Assert.Equal("/old-background-2", order.Last());
        }
        finally
        {
            foreach (var request in urgent)
            {
                request.Dispose();
            }
        }
    }

    [Fact]
    public async Task Scheduler_UrgentArrivingDuringDelay_PassesWaitingBackground()
    {
        var order = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            order.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response("{}"));
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.FromMilliseconds(150), TimeSpan.FromMinutes(1));
        using var first = Request("first");
        using var low = Request("background");
        using var high = Request("con");
        (await scheduler.SendAsync(first, new(WikiPriority.Background), default)).Dispose();
        var background = scheduler.SendAsync(low, new(WikiPriority.Background), default);
        var con = scheduler.SendAsync(high, new(WikiPriority.RealTime), default);
        (await con.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        (await background.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(["/first", "/con", "/background"], order);
    }

    [Fact]
    public async Task Repository_DuplicateLookupSharesWork_OneCancelledCallerDoesNotCancelAnother()
    {
        var entered = Signal();
        var release = Signal();
        int requests = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return SpellPage();
        }));
        var store = new MobResistanceStore();
        int saves = 0;
        await using var repository = new WikiRepository(http, store, () => saves++, TimeSpan.Zero);
        using var canceled = new CancellationTokenSource();
        var background = repository.GetSpellAsync("Flame X", WikiPriority.Background, false, canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var con = repository.GetSpellAsync("Flame X", WikiPriority.RealTime, false, default);
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background);
        release.SetResult();
        Assert.Equal(ResistanceType.Fire, (await con.WaitAsync(TimeSpan.FromSeconds(5))).Type);
        Assert.Equal(1, requests);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Repository_BackgroundDiscovery_PromotionPassesUnrelatedQueuedWork()
    {
        var entered = Signal();
        var release = Signal();
        var order = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string name = Title(request);
            order.Enqueue(name);
            if (name.StartsWith("Blocking|", StringComparison.Ordinal))
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return SpellPage();
        }));
        await using var repository = new WikiRepository(http, new(), () => { }, TimeSpan.Zero);
        var blocking = repository.GetSpellAsync("Blocking", WikiPriority.Background, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var low = repository.GetSpellAsync("Background", WikiPriority.Background, false, default);
        var con = repository.GetSpellAsync("Promoted", WikiPriority.Background, false, default);
        var urgent = repository.GetSpellAsync("Promoted", WikiPriority.RealTime, false, default);
        await UntilAsync(() => repository.PendingRequestCount == 2);
        release.SetResult();
        await Task.WhenAll(blocking, low, con, urgent).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, order.Count);
        Assert.StartsWith("Promoted|", order.ToArray()[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Repository_RealTimeRequestInterruptsQuestPaginationAndRankFallback(bool quest)
    {
        var entered = Signal();
        var release = Signal();
        var calls = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            calls.Enqueue(query);
            if (calls.Count == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (!quest)
                {
                    return Page(null);
                }
                return Response("""{"query":{"pages":[{"title":"Gnoll Fang"}]}}""");
            }
            if (query.Contains("generator="))
            {
                return Response("""{"query":{"pages":[]}}""");
            }
            return SpellPage();
        }));
        await using var repository = new WikiRepository(http, new(), () => { }, TimeSpan.Zero);
        Task background = quest
            ? repository.GetQuestAsync("Gnoll Fang", WikiPriority.Background, false, default)
            : repository.GetSpellAsync("Flame X", WikiPriority.Background, false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var con = repository.GetMobAsync("Urgent NPC", WikiPriority.RealTime, false, default);
        await UntilAsync(() => repository.PendingRequestCount == 1);
        release.SetResult();
        await Task.WhenAll(background, con).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("titles=Urgent NPC|", calls.ToArray()[1]);
        Assert.True(calls.Count >= 3);
    }

    [Fact]
    public async Task Scheduler_PacesFromCompletionAndDoesNotOverlapHttp()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timings = new ConcurrentQueue<(double Started, double Finished)>();
        int active = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            double start = clock.Elapsed.TotalMilliseconds;
            await Task.Delay(25, token);
            timings.Enqueue((start, clock.Elapsed.TotalMilliseconds));
            Interlocked.Decrement(ref active);
            return Response("{}");
        }));
        await using var scheduler = new WikiRequestScheduler(http, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(1));
        using var one = Request("one");
        using var two = Request("two");
        using var three = Request("three");
        var tasks = new[]
        {
            scheduler.SendAsync(one, new(WikiPriority.RealTime), default),
            scheduler.SendAsync(two, new(WikiPriority.Interactive), default),
            scheduler.SendAsync(three, new(WikiPriority.Background), default),
        };
        foreach (var response in await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            response.Dispose();
        }
        var measured = timings.ToArray();
        Assert.True(measured[1].Started - measured[0].Finished >= 95);
        Assert.True(measured[2].Started - measured[1].Finished >= 95);
    }

    [Fact]
    public async Task Repository_BackgroundSpellFailure_ReportsAndPreservesCache_RefreshRetries()
    {
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            int attempt = Interlocked.Increment(ref requests);
            return Task.FromResult(attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) : SpellPage());
        }));
        var store = new MobResistanceStore();
        var old = new SpellResistanceInfo("Flame", ResistanceType.Fire, "old", DateTime.UtcNow.AddDays(-8), true);
        store.SetSpell(old);
        await using var repository = new WikiRepository(http, store, () => { }, TimeSpan.Zero);
        repository.BackgroundFailed += (_, error) => failed.TrySetResult(error);
        repository.ObserveSpell("Flame");
        Assert.IsType<HttpRequestException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(old, store.GetSpell("Flame"));
        for (int i = 0; i < 100; i++)
        {
            repository.ObserveSpell("Flame");
        }
        await repository.GetSpellAsync("Flame", WikiPriority.Interactive, true, default);
        Assert.Equal(2, requests);
        Assert.False(store.GetSpell("Flame")!.NeedsRefresh);
    }

    [Fact]
    public async Task Repository_CacheExpiryMissingPagesAndForce_ReuseDiskMetadata()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Title(request).StartsWith("Missing|", StringComparison.Ordinal)
                ? Page(null) : SpellPage());
        }));
        var store = new MobResistanceStore();
        store.SetSpell(new("Fresh", ResistanceType.Fire, "Fire", DateTime.UtcNow, true, "Single", "Instant", 1));
        store.SetSpell(new("Old", ResistanceType.Fire, "Fire", DateTime.UtcNow, true));
        store.SetSpell(new("Expired", ResistanceType.Fire, "Fire", DateTime.UtcNow.AddDays(-8), true, "Single", "Instant", 1));
        await using var repository = new WikiRepository(http, store, () => { }, TimeSpan.Zero);
        await repository.GetSpellAsync("Fresh", WikiPriority.Interactive, false, default);
        Assert.Equal(0, requests);
        await repository.GetSpellAsync("Old", WikiPriority.Background, false, default);
        await repository.GetSpellAsync("Expired", WikiPriority.Background, false, default);
        await repository.GetSpellAsync("Missing", WikiPriority.Background, false, default);
        await repository.GetSpellAsync("Missing", WikiPriority.Interactive, false, default);
        await repository.GetSpellAsync("Fresh", WikiPriority.Interactive, true, default);
        Assert.Equal(4, requests);
        string path = Path.Combine(Path.GetTempPath(), $"eq-wiki-{Guid.NewGuid():N}.json");
        try
        {
            store.Save(path);
            await using var restarted = new WikiRepository(http, MobResistanceStore.Load(path), () => { }, TimeSpan.Zero);
            await restarted.GetSpellAsync("Fresh", WikiPriority.Interactive, false, default);
            Assert.Equal(4, requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Repository_DiscoveryFromHistory_RequiresNoConsiderOrSelection()
    {
        var completed = Signal();
        var store = new MobResistanceStore();
        int saved = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(SpellPage())));
        await using var repository = new WikiRepository(http, store, () =>
        {
            if (Interlocked.Increment(ref saved) == 6)
            {
                completed.TrySetResult();
            }
        }, TimeSpan.Zero);
        var engine = new LogEngine();
        engine.SpellObserved += repository.ObserveSpell;
        DateTime now = DateTime.Now;
        foreach (string message in new[]
        {
            "You begin casting Flame.", "A rat resisted your Frost!",
            "You hit A rat for 100 points of fire damage by Bolt.",
            "A rat has taken 50 damage from your Poison.",
            "Your Mez spell has worn off of A rat.",
            "You healed Tester for 20 hit points by Life Leech.",
        })
        {
            engine.Process(new(now, message));
        }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(store.GetSpell("Life Leech"));
        Assert.NotNull(store.GetSpell("Poison"));
    }

    [Fact]
    public async Task Repository_LastCallerCancellation_CancelsHttp_ShutdownCancelsQueuedWork()
    {
        var entered = Signal();
        var canceled = Signal();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                canceled.TrySetResult();
            }
            return SpellPage();
        }));
        var repository = new WikiRepository(http, new(), () => { }, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var first = repository.GetSpellAsync("One", WikiPriority.Background, false, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = repository.GetSpellAsync("Two", WikiPriority.Background, false, default);
        var third = repository.GetSpellAsync("Three", WikiPriority.RealTime, false, default);
        await repository.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
    }

    [Fact]
    public async Task Repository_QuestPaginationAndSpellFallback_UseSameHttpLane()
    {
        var calls = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            calls.Enqueue(query);
            if (query.Contains("generator=backlinks"))
            {
                return Task.FromResult(query.Contains("gblcontinue=")
                    ? Response("""{"query":{"pages":[]}}""")
                    : Response("""{"continue":{"gblcontinue":"next"},"query":{"pages":[]}}"""));
            }
            if (query.Contains("generator=embeddedin"))
            {
                return Task.FromResult(Response("""{"query":{"pages":[]}}"""));
            }
            if (query.Contains("titles=Gnoll Fang"))
            {
                return Task.FromResult(Response("""{"query":{"pages":[{"title":"Gnoll Fang"}]}}"""));
            }
            return Task.FromResult(query.Contains("Flame X") ? Page(null) : SpellPage());
        }));
        await using var repository = new WikiRepository(http, new(), () => { }, TimeSpan.Zero);
        await repository.GetQuestAsync("Gnoll Fang +1", WikiPriority.Background, false, default);
        await repository.GetSpellAsync("Flame X", WikiPriority.Interactive, false, default);
        Assert.Equal(6, calls.Count);
        Assert.Contains(calls, q => q.Contains("gblcontinue=next"));
        Assert.Contains(calls, q => q.Contains("titles=Flame|"));
        await repository.GetQuestAsync("Gnoll Fang", WikiPriority.Interactive, false, default);
        Assert.Equal(6, calls.Count);
    }

    [Fact]
    public async Task Repository_MobAndSkyCaches_KeepExistingTtlsAndForceRefresh()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Query.Contains("action=parse"))
            {
                return Task.FromResult(Response("""{"error":{"info":"Sky unavailable"}}"""));
            }
            return Task.FromResult(Page("{{Namedmobpage\n| level = 50\n| HP = 18000\n}}"));
        }));
        var store = new MobResistanceStore();
        var cachedMob = MobWikiLookup.ParseMob("A rat", "{{Namedmobpage\n| HP = 10\n}}", DateTime.UtcNow);
        store.SetWiki("A rat", cachedMob);
        var sky = new SkyQuestCatalog(DateTime.UtcNow,
            [new("Bard", "Test", "Reward", "Giver", "phrase", "rune", "items", "url", [], [], [])]);
        await using var repository = new WikiRepository(http, store, () => { }, TimeSpan.Zero);
        repository.SeedSky(sky);
        Assert.Same(cachedMob, await repository.GetMobAsync("A rat", WikiPriority.RealTime, false, default));
        var cachedActivity = repository.GetActivity();
        Assert.Empty(cachedActivity.Requests);
        Assert.Contains(cachedActivity.Recent, e => e.Name == "mob:A RAT" &&
            !e.IsError && e.Message.Contains("Cache hit"));
        Assert.Equal("18000", (await repository.GetMobAsync("A rat", WikiPriority.RealTime, true, default)).HitPoints);
        Assert.Same(sky, await repository.GetSkyAsync(WikiPriority.Background, false, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetSkyAsync(WikiPriority.Interactive, true, default));
        Assert.Same(sky, await repository.GetSkyAsync(WikiPriority.Interactive, false, default));
    }

    private static TaskCompletionSource Signal()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static HttpRequestMessage Request(string name)
    {
        return new(HttpMethod.Get, "https://eqlwiki.com/" + name);
    }

    private static string Title(HttpRequestMessage request)
    {
        string query = Uri.UnescapeDataString(request.RequestUri!.Query);
        return query[(query.IndexOf("&titles=", StringComparison.Ordinal) + 8)..];
    }

    private static HttpResponseMessage SpellPage()
    {
        return Page("{{Spellpage\n| duration = Instant\n| target_type = Single\n| slots = Decrease Hitpoints by 100\n| resist = Fire\n}}");
    }

    private static HttpResponseMessage Page(string? content)
    {
        object page = content is null ? new
        {
            title = "Missing",
            missing = true
        } :
            new
            {
                title = "Result",
                revisions = new[] { new { slots = new { main = new { content } } } }
            };
        return Response(JsonSerializer.Serialize(new
        {
            query = new
            {
                pages = new[] { page }
            }
        }));
    }

    private static HttpResponseMessage Response(string text)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(text)
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
