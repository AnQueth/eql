using System.Net;
using Polly;
using Polly.Retry;

namespace EqLog.Core;

public enum WikiPriority
{
    RealTime = 0,
    Interactive = 100,
    Background = 200,
}

internal sealed class WikiRequestContext(WikiPriority priority, string name = "Wiki request")
{
    public string Name { get; } = name;
    private int _priority = (int)priority;

    public WikiPriority Priority
    {
        get
        {
            return (WikiPriority)Volatile.Read(ref _priority);
        }
    }

    public void Promote(WikiPriority priority)
    {
        int previous;
        do
        {
            previous = Volatile.Read(ref _priority);
            if (previous <= (int)priority)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _priority, (int)priority, previous) != previous);
    }
}

/// <summary>One paced HTTP lane. Priority is reevaluated after pacing and between pagination/fallback requests.</summary>
internal sealed class WikiRequestScheduler : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PriorityQueue<Request, (int Priority, long Sequence)> _pending = new();
    private readonly SemaphoreSlim _ready = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _maximumWait;
    private readonly Task _worker;
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;
    private long _sequence;
    private int _servedSinceFairness;
    private bool _disposed;
    private readonly Dictionary<long, Activity> _activities = [];
    private readonly Queue<WikiActivityEvent> _recent = [];
    private long _activitySequence;

    public WikiActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(_activities.Values.OrderBy(a => a.State == "Sending" ? 0 : 1)
                .ThenBy(a => a.Context.Priority).ThenBy(a => a.Id)
                .Select(a => new WikiRequestActivity(a.Id, a.Context.Name, a.Url, a.Context.Priority,
                    a.State, a.Attempt, a.StartedUtc)).ToArray(), _recent.Reverse().ToArray(), []);
        }
    }

    public void RecordFailure(string name, Exception error)
    {
        RecordEvent(name, error.Message, true);
    }

    public void RecordCompletion(string name, bool cacheHit)
    {
        RecordEvent(name, cacheHit ? "Cache hit - no HTTP request needed" : "Lookup completed", false);
    }

    private void RecordEvent(string name, string message, bool error)
    {
        lock (_gate)
        {
            _recent.Enqueue(new(DateTime.UtcNow, name, message, error));
            while (_recent.Count > 30)
            {
                _recent.Dequeue();
            }
        }
    }

    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public WikiRequestScheduler(HttpClient http, TimeSpan interval, TimeSpan maximumWait,
        TimeSpan? retryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumWait, TimeSpan.Zero);
        _http = http;
        _interval = interval;
        _maximumWait = maximumWait;
        _retry = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = retryDelay ?? TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxDelay = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<OperationCanceledException>(error => error.InnerException is TimeoutException)
                    .HandleResult(response => response.StatusCode == HttpStatusCode.RequestTimeout ||
                        response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500),
                DelayGenerator = args =>
                {
                    var retryAfter = args.Outcome.Result?.Headers.RetryAfter;
                    TimeSpan? delay = retryAfter?.Delta;
                    if (retryAfter?.Date is { } date)
                    {
                        delay = date - DateTimeOffset.UtcNow;
                    }
                    return new ValueTask<TimeSpan?>(delay is { } value && value < TimeSpan.Zero
                        ? TimeSpan.Zero : delay);
                },
                OnRetry = args =>
                {
                    args.Outcome.Result?.Dispose();
                    return default;
                },
            })
            .Build();
        _worker = Task.Run(RunAsync);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, WikiRequestContext context,
        CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        var activity = new Activity(Interlocked.Increment(ref _activitySequence), context,
            message.RequestUri?.ToString() ?? "");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activities.Add(activity.Id, activity);
        }
        try
        {
            // Backoff happens outside the HTTP lane; every retry reenters the priority queue.
            var response = await _retry.ExecuteAsync(ct => new ValueTask<HttpResponseMessage>(
                SendAttemptAsync(message, context, activity, ct)), linked.Token).ConfigureAwait(false);
            RecordEvent(context.Name, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} " +
                $"(attempt {activity.Attempt}/4)", !response.IsSuccessStatusCode);
            return response;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            RecordEvent(context.Name, "Canceled", false);
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(context.Name, ex);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _activities.Remove(activity.Id);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage message, WikiRequestContext context,
        Activity activity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new Request(message, context, activity, token, Interlocked.Increment(ref _sequence));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            activity.State = "Queued / pacing";
            activity.Attempt++;
            _pending.Enqueue(request, ((int)context.Priority, request.Sequence));
            _ready.Release();
        }
        using var registration = token.Register(() => request.Completion.TrySetCanceled(token));
        return await request.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        DateTime next = DateTime.MinValue;
        try
        {
            while (true)
            {
                await _ready.WaitAsync(_stop.Token).ConfigureAwait(false);
                TimeSpan delay;
                while ((delay = next - DateTime.UtcNow) > TimeSpan.Zero)
                {
                    await Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay,
                        _stop.Token).ConfigureAwait(false);
                }
                Request request;
                bool servedFairness;
                lock (_gate)
                {
                    // Pending contexts can be promoted while another HTTP request or the pacing delay is active.
                    var queued = _pending.UnorderedItems.Select(i => i.Element).ToArray();
                    _pending.Clear();
                    DateTime now = DateTime.UtcNow;
                    Request? fair = _servedSinceFairness >= 8
                        ? queued.Where(r => !r.Token.IsCancellationRequested &&
                            r.Context.Priority > WikiPriority.RealTime && now - r.Enqueued >= _maximumWait)
                            .MinBy(r => r.Sequence)
                        : null;
                    foreach (var item in queued)
                    {
                        int priority = item == fair ? -1 : (int)item.Context.Priority;
                        _pending.Enqueue(item, (priority, item.Sequence));
                    }
                    request = _pending.Dequeue();
                    request.Activity.State = "Sending";
                    servedFairness = request == fair;
                }
                if (request.Token.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.Token);
                    continue;
                }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, request.Token);
                _servedSinceFairness = servedFairness ? 0 : Math.Min(8, _servedSinceFairness + 1);
                try
                {
                    using var outgoing = new HttpRequestMessage(request.Message.Method, request.Message.RequestUri);
                    foreach (var header in request.Message.Headers)
                    {
                        outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    var response = await _http.SendAsync(outgoing, HttpCompletionOption.ResponseContentRead,
                        linked.Token).ConfigureAwait(false);
                    bool retry = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                        (int)response.StatusCode >= 500;
                    if (retry && request.Activity.Attempt < 4)
                    {
                        lock (_gate)
                        {
                            request.Activity.State = $"Retry backoff: HTTP {(int)response.StatusCode}";
                        }
                        RecordEvent(request.Context.Name, $"HTTP {(int)response.StatusCode}; retrying " +
                            $"(attempt {request.Activity.Attempt}/4)", true);
                    }
                    if (!request.Completion.TrySetResult(response))
                    {
                        response.Dispose();
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(linked.Token);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        request.Activity.State = "Retry backoff / failed";
                    }
                    RecordFailure(request.Context.Name, ex);
                    // Propagate the original failure to the repository caller, then keep the shared lane alive.
                    request.Completion.TrySetException(ex);
                }
                finally
                {
                    next = DateTime.UtcNow + _interval;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                while (_pending.TryDequeue(out var pending, out _))
                {
                    pending.Completion.TrySetCanceled(_stop.Token);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _ready.Dispose();
        _stop.Dispose();
    }

    private sealed class Activity(long id, WikiRequestContext context, string url)
    {
        public long Id { get; } = id;
        public WikiRequestContext Context { get; } = context;
        public string Url { get; } = url;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public string State { get; set; } = "Queued";
        public int Attempt
        {
            get; set;
        }
    }

    private sealed class Request(HttpRequestMessage message, WikiRequestContext context, Activity activity, CancellationToken token, long sequence)
    {
        public Activity Activity { get; } = activity;
        public HttpRequestMessage Message { get; } = message;
        public WikiRequestContext Context { get; } = context;
        public CancellationToken Token { get; } = token;
        public long Sequence { get; } = sequence;
        public DateTime Enqueued { get; } = DateTime.UtcNow;
        public TaskCompletionSource<HttpResponseMessage> Completion
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
