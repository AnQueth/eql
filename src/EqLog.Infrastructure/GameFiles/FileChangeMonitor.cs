using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace EqLog.Infrastructure.GameFiles;

/// <summary>Coalesces filesystem notifications, with a slow recovery probe for missed events.</summary>
internal sealed class FileChangeMonitor : IDisposable
{
    internal static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);
    private readonly string _directory;
    private readonly string _filter;
    private readonly TimeSpan _recoveryInterval;
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private FileSystemWatcher? _watcher;
    private Exception? _error;
    private long _version;
    private long _replacementVersion;
    private long _lastRecovery = Stopwatch.GetTimestamp();

    internal FileChangeMonitor(string directory, string filter, TimeSpan? recoveryInterval = null)
    {
        _directory = directory;
        _filter = filter;
        _recoveryInterval = recoveryInterval ?? RecoveryInterval;
        TryStart();
    }

    internal long Version
    {
        get
        {
            if (Interlocked.Exchange(ref _error, null) is { } error)
            {
                _watcher?.Dispose();
                _watcher = null;
                _lastRecovery = 0;
                ExceptionDispatchInfo.Capture(error).Throw();
            }
            if (Stopwatch.GetElapsedTime(_lastRecovery) >= _recoveryInterval)
            {
                _lastRecovery = Stopwatch.GetTimestamp();
                TryStart();
                Interlocked.Increment(ref _replacementVersion);
                Interlocked.Increment(ref _version);
            }
            return Interlocked.Read(ref _version);
        }
    }

    internal long ReplacementVersion
    {
        get
        {
            return Interlocked.Read(ref _replacementVersion);
        }
    }

    private void TryStart()
    {
        if (_watcher is not null || !Directory.Exists(_directory))
        {
            return;
        }
        var watcher = new FileSystemWatcher(_directory, _filter)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => Signal(false);
        watcher.Created += (_, _) => Signal(true);
        watcher.Deleted += (_, _) => Signal(true);
        watcher.Renamed += (_, _) => Signal(true);
        watcher.Error += (_, args) =>
        {
            // Overflow means a notification was lost; the consumer must rescan.
            if (args.GetException() is not InternalBufferOverflowException)
            {
                Volatile.Write(ref _error, new IOException("Filesystem change monitoring failed; the next check will restart it.", args.GetException()));
            }
            Signal(true);
        };
        try
        {
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    private void Signal(bool replaced)
    {
        if (replaced)
        {
            Interlocked.Increment(ref _replacementVersion);
        }
        Interlocked.Increment(ref _version);
        _changes.Writer.TryWrite(true);
    }

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_recoveryInterval);
        try
        {
            await _changes.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Recovery timeout: inspect again even if no notification arrived.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
