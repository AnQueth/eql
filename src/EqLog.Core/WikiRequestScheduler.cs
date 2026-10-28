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

internal sealed class WikiRequestContext(WikiPriority priority)
{
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
        // Backoff happens outside the HTTP lane; every retry reenters the priority queue.
        return await _retry.ExecuteAsync(ct => new ValueTask<HttpResponseMessage>(
            SendAttemptAsync(message, context, ct)), linked.Token).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage message, WikiRequestContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new Request(message, context, token, Interlocked.Increment(ref _sequence));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    private sealed class Request(HttpRequestMessage message, WikiRequestContext context, CancellationToken token, long sequence)
    {
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
