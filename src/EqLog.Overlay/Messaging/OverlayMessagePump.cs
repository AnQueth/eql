using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using EqLog.Application.Sessions;
using EqLog.Core.Parsing;
using EqLog.Core.Triggers;

namespace EqLog.Overlay.Messaging;

/// <summary>
/// The single entry point for log reader events. Reader-thread events are either handled synchronously by background
/// subscribers (raw-line buffers, observers) or queued; every pump cycle (250 ms by default) drains the queue on the UI
/// thread, captures one engine frame sized to what active receivers need, and dispatches it to the overlay view models.
/// </summary>
public sealed class OverlayMessagePump : IDisposable
{
    /// <summary>The default pump interval.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often overlays render while log history loads. Every render re-lays out each visible overlay, and the
    /// intermediate history states are not worth that cost.
    /// </summary>
    public static readonly TimeSpan ReplayFrameInterval = TimeSpan.FromSeconds(1);

    private readonly IOverlayLogFeed _feed;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentQueue<Action> _queued = new();
    private readonly Dictionary<Type, List<object>> _receivers = [];
    private readonly Dictionary<Type, List<object>> _backgroundReceivers = [];
    private readonly object _backgroundGate = new();
    private Stopwatch? _sinceReplayFrame;
    private bool _pumping;
    private bool _disposed;

    /// <summary>Creates a pump on the current dispatcher and subscribes to <paramref name="feed"/>.</summary>
    /// <param name="feed">The log reader and session events and frame source.</param>
    /// <param name="interval">Pump interval; defaults to <see cref="DefaultInterval"/>.</param>
    public OverlayMessagePump(IOverlayLogFeed feed, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        _feed = feed;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = interval ?? DefaultInterval };
        _timer.Tick += Timer_Tick;
        _feed.BatchProcessed += Feed_BatchProcessed;
        _feed.LiveMobConsidered += Feed_LiveMobConsidered;
        _feed.LiveConsiderAssessment += Feed_LiveConsiderAssessment;
        _feed.SpellObserved += Feed_SpellObserved;
    }

    /// <summary>Raised when a pump cycle throws; the pump keeps running.</summary>
    public event Action<Exception>? CycleFailed;

    /// <summary>Starts the periodic pump.</summary>
    public void Start()
    {
        _dispatcher.VerifyAccess();
        _timer.Start();
    }

    /// <summary>Stops the periodic pump. Queued messages are kept until the next cycle.</summary>
    public void Stop()
    {
        _dispatcher.VerifyAccess();
        _timer.Stop();
    }

    /// <summary>Subscribes <paramref name="receiver"/> to UI-thread messages of type <typeparamref name="TMessage"/>.</summary>
    /// <returns>A handle that unsubscribes when disposed.</returns>
    public IDisposable Subscribe<TMessage>(IMessageReceiver<TMessage> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        _dispatcher.VerifyAccess();
        return Add(_receivers, typeof(TMessage), receiver, null);
    }

    /// <summary>
    /// Subscribes <paramref name="receiver"/> to reader-thread messages of type <typeparamref name="TMessage"/>.
    /// Background receivers must be thread-safe and fast; they run while the reader waits.
    /// </summary>
    /// <returns>A handle that unsubscribes when disposed.</returns>
    public IDisposable SubscribeBackground<TMessage>(IMessageReceiver<TMessage> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        return Add(_backgroundReceivers, typeof(TMessage), receiver, _backgroundGate);
    }

    /// <summary>Queues <paramref name="message"/> for UI-thread delivery at the start of the next pump cycle. Thread-safe.</summary>
    public void Post<TMessage>(TMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _queued.Enqueue(() => Publish(message));
    }

    /// <summary>Delivers <paramref name="message"/> to UI-thread subscribers immediately.</summary>
    public void Publish<TMessage>(TMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _dispatcher.VerifyAccess();
        foreach (var receiver in Receivers<TMessage>(_receivers, null))
        {
            receiver.Receive(message);
        }
    }

    /// <summary>Runs one pump cycle: drains queued messages, captures a frame and dispatches it.</summary>
    /// <returns><see langword="true"/> when a frame was dispatched to completion.</returns>
    public async Task<bool> PumpOnceAsync()
    {
        _dispatcher.VerifyAccess();
        if (_pumping || _disposed)
        {
            return false;
        }

        _pumping = true;
        try
        {
            return await RunCycleAsync();
        }
        finally
        {
            _pumping = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _feed.BatchProcessed -= Feed_BatchProcessed;
        _feed.LiveMobConsidered -= Feed_LiveMobConsidered;
        _feed.LiveConsiderAssessment -= Feed_LiveConsiderAssessment;
        _feed.SpellObserved -= Feed_SpellObserved;
    }

    private async Task<bool> RunCycleAsync()
    {
        while (_queued.TryDequeue(out var deliver))
        {
            deliver();
        }

        var now = DateTime.Now;
        var progress = _feed.Progress;
        Publish(new OverlayTickMessage(now, progress));

        if (progress.CaughtUp)
        {
            _sinceReplayFrame = null;
        }
        else if (_sinceReplayFrame is { } sinceReplayFrame && sinceReplayFrame.Elapsed < ReplayFrameInterval)
        {
            return false;
        }

        var frameReceivers = Receivers<OverlayFrameMessage>(_receivers, null);
        var requirements = FrameRequirements.None;
        foreach (var receiver in frameReceivers)
        {
            if (receiver is IOverlayFrameReceiver { WantsFrames: true } overlay)
            {
                requirements = requirements.Combine(overlay.Requirements);
            }
        }

        long revision = _feed.Revision;
        var captured = await _feed.CaptureFrameAsync(requirements);
        if (_disposed || revision != _feed.Revision)
        {
            return false;
        }

        if (!progress.CaughtUp)
        {
            _sinceReplayFrame = Stopwatch.StartNew();
        }

        var frame = new OverlayFrameMessage(captured, now, progress);
        foreach (var receiver in frameReceivers)
        {
            if (receiver is IOverlayFrameReceiver { WantsFrames: false })
            {
                continue;
            }

            receiver.Receive(frame);
            // While history loads, yield after each overlay so WPF lays out (and updates automation for) one
            // overlay at a time with input handled in between, instead of one long pass over every overlay.
            if (!progress.CaughtUp)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (_disposed || revision != _feed.Revision)
                {
                    return false;
                }
            }
        }

        Publish(new OverlayFrameCompletedMessage(frame, _feed.Completion));
        return true;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await PumpOnceAsync();
        }
        catch (Exception error) when (CycleFailed is not null)
        {
            CycleFailed(error);
        }
    }

    private void Feed_BatchProcessed(LogSessionBatch batch)
    {
        PublishBackground(new LogBatchMessage(batch));
        if (!batch.Live)
        {
            return;
        }

        var alerts = _feed.MatchTriggers(batch.Lines);
        if (alerts.Count > 0)
        {
            Post(new TriggerAlertsMessage(alerts));
        }
    }

    private void Feed_LiveMobConsidered(ConsiderEvent consider, string zone)
    {
        Post(new MobConsideredMessage(consider, zone));
    }

    private void Feed_LiveConsiderAssessment(ConsiderAssessmentEvent assessment)
    {
        Post(new ConsiderAssessmentMessage(assessment));
    }

    private void Feed_SpellObserved(string spell)
    {
        PublishBackground(new SpellObservedMessage(spell));
    }

    private void PublishBackground<TMessage>(TMessage message)
    {
        foreach (var receiver in Receivers<TMessage>(_backgroundReceivers, _backgroundGate))
        {
            receiver.Receive(message);
        }
    }

    private static IDisposable Add(Dictionary<Type, List<object>> registry, Type type, object receiver, object? gate)
    {
        lock (gate ?? registry)
        {
            if (!registry.TryGetValue(type, out var list))
            {
                list = [];
                registry[type] = list;
            }
            list.Add(receiver);
        }
        return new Subscription(() =>
        {
            lock (gate ?? registry)
            {
                if (registry.TryGetValue(type, out var list))
                {
                    list.Remove(receiver);
                }
            }
        });
    }

    private static IMessageReceiver<TMessage>[] Receivers<TMessage>(Dictionary<Type, List<object>> registry, object? gate)
    {
        lock (gate ?? registry)
        {
            return registry.TryGetValue(typeof(TMessage), out var list)
                ? list.Cast<IMessageReceiver<TMessage>>().ToArray()
                : [];
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
