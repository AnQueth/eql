using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Core.Parsing;

namespace EqLog.Application.Sessions;

/// <summary>Owns source lifetime, replay/live policy, progress, observations and follow-newest decisions.</summary>
public sealed class LogSessionService : IAsyncDisposable
{
    private readonly ILogSource _source;
    private readonly ILogDiscovery _discovery;
    private readonly InventoryService _inventory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _reader;
    private long _generation;
    private LogFileInfo? _file;
    private Action? _unsubscribe;
    private Task? _disposeTask;
    private TailProgress _progress;
    private bool _live;
    private bool _finding;
    private DateTime _followBaseline;
    private static readonly TimeSpan ReplayThreshold = TimeSpan.FromMinutes(30);

    public LogSessionService(ILogSource source, ILogDiscovery discovery, InventoryService inventory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(inventory);
        _source = source;
        _discovery = discovery;
        _inventory = inventory;
        _time = timeProvider ?? TimeProvider.System;
        _followBaseline = _time.GetLocalNow().DateTime;
    }

    /// <summary>Raised on the source worker after processing; subscribers must marshal UI work themselves.</summary>
    public event Action<LogSessionBatch>? BatchProcessed;
    public event Action<ConsiderEvent, string>? LiveMobConsidered;
    public event Action<ConsiderAssessmentEvent>? LiveConsiderAssessment;
    public event Action<string>? SpellObserved;

    public TailProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    public Task? Completion
    {
        get
        {
            lock (_gate)
            {
                return _reader;
            }
        }
    }

    public Task<LogFileInfo> DescribeAsync(string path, CancellationToken cancellationToken = default)
    {
        return _discovery.DescribeAsync(path, cancellationToken);
    }

    public Task<LogStartupSelection> FindInitialAsync(string[] arguments, string? savedPath, string? savedDirectory,
        bool followNewest, CancellationToken cancellationToken = default)
    {
        return _discovery.FindInitialAsync(arguments, savedPath, savedDirectory, followNewest, cancellationToken);
    }

    public Task<string> ResolveDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        return _discovery.ResolveDirectoryAsync(directory, cancellationToken);
    }

    public Task<bool> DirectoryExistsAsync(string directory, CancellationToken cancellationToken = default)
    {
        return _discovery.DirectoryExistsAsync(directory, cancellationToken);
    }

    public Task<LogFileInfo?> FindNewestAsync(string directory, CancellationToken cancellationToken = default)
    {
        return _discovery.FindNewestAsync(directory, cancellationToken);
    }

    /// <summary>Starts an already configured engine after the previous source has stopped. Does not perform UI work.</summary>
    public void Start(LogFileInfo file, LogEngine engine)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(engine);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_cancellation is not null)
            {
                throw new InvalidOperationException("Stop the previous log session before starting another.");
            }
            _file = file;
            _progress = default;
            _live = false;
            _generation++;
            _followBaseline = _time.GetLocalNow().DateTime;
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            long generation = _generation;
            _inventory.SelectSession(file.Path, file.CharacterName);
            Action<string> spellObserved = spell => SpellObserved?.Invoke(spell);
            Action<ConsiderEvent, string> mobConsidered = (consider, zone) =>
            {
                if (IsLive(generation))
                {
                    LiveMobConsidered?.Invoke(consider, zone);
                }
            };
            Action<ConsiderAssessmentEvent> considerAssessment = assessment =>
            {
                if (IsLive(generation))
                {
                    LiveConsiderAssessment?.Invoke(assessment);
                }
            };
            Action<LootEvent, long> lootObserved = (loot, line) =>
            {
                if (loot.Looter == Names.You && loot.SoldFor is null && !loot.Stored)
                {
                    _inventory.ObserveLoot(new(file.CharacterName, file.Path, line, loot.Item,
                        loot.Quantity, IsLive(generation), loot.Time.ToUniversalTime()));
                }
            };
            engine.SpellObserved += spellObserved;
            engine.MobConsidered += mobConsidered;
            engine.ConsiderAssessment += considerAssessment;
            engine.LootObserved += lootObserved;
            _unsubscribe = () =>
            {
                engine.SpellObserved -= spellObserved;
                engine.MobConsidered -= mobConsidered;
                engine.ConsiderAssessment -= considerAssessment;
                engine.LootObserved -= lootObserved;
            };
            _reader = Task.Run(() => _source.RunAsync(file.Path, true, lines =>
            {
                lock (_gate)
                {
                    if (generation != _generation)
                    {
                        return;
                    }
                }
                engine.ProcessLines(lines);
                BatchProcessed?.Invoke(new(file.Path, file.CharacterName, lines, IsLive(generation)));
            }, progress =>
            {
                lock (_gate)
                {
                    if (generation == _generation)
                    {
                        _live |= progress.CaughtUp;
                        _progress = progress;
                    }
                }
            }, cancellation.Token));
        }
    }

    /// <summary>Awaits the last batch before releasing source lifetime. Unexpected failures propagate to the caller.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cancellation;
            Task? reader;
            lock (_gate)
            {
                cancellation = _cancellation;
                reader = _reader;
            }
            if (cancellation is null || reader is null)
            {
                return;
            }
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                await reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Intentional source termination.
            }
            finally
            {
                lock (_gate)
                {
                    _cancellation = null;
                    _reader = null;
                    _unsubscribe?.Invoke();
                    _unsubscribe = null;
                    _generation++;
                }
                cancellation.Dispose();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void ResetFollowBaseline()
    {
        lock (_gate)
        {
            _followBaseline = _time.GetLocalNow().DateTime;
        }
    }

    public void ForgetSource()
    {
        lock (_gate)
        {
            if (_cancellation is not null)
            {
                throw new InvalidOperationException("Stop the source before clearing its selection.");
            }
            _file = null;
            _generation++;
        }
    }

    public async Task<LogFileInfo?> FindNewerAsync(string directory, bool followNewest, CancellationToken cancellationToken = default)
    {
        long generation;
        LogFileInfo? file;
        DateTime baseline;
        lock (_gate)
        {
            if (_finding || (!followNewest && _file is not null))
            {
                return null;
            }
            _finding = true;
            generation = _generation;
            file = _file;
            baseline = _followBaseline;
        }
        try
        {
            var newest = await _discovery.FindNewestAsync(directory, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation || newest is null)
                {
                    return null;
                }
                return file is null || (!file.Path.Equals(newest.Path, StringComparison.OrdinalIgnoreCase) && newest.LastWriteTime > baseline)
                    ? newest : null;
            }
        }
        finally
        {
            lock (_gate)
            {
                _finding = false;
            }
        }
    }

    public Task<LogSessionFrame> CaptureFrameAsync(LogEngine engine, bool includeChat, HashSet<ChatChannel> channels,
        bool includeSpawns = true, bool includeSpellDurations = true)
    {
        return Task.Run(() =>
        {
            var now = _time.GetLocalNow().DateTime;
            var last = engine.LastLogTime;
            bool replay = last is DateTime logged && now - logged > ReplayThreshold;
            var snapshot = engine.Snapshot(replay ? last!.Value : now);
            var queries = EngineQuerySnapshot.Capture(engine, channels, includeChat, includeSpawns, includeSpellDurations);
            return new LogSessionFrame(snapshot, queries.Chat, replay, queries);
        });
    }

    private bool IsLive(long generation)
    {
        lock (_gate)
        {
            return generation == _generation && _live;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= StopAsync();
            return new(_disposeTask);
        }
    }
}
