using System.Windows.Threading;

namespace EqLog.Overlay;

/// <summary>Coalesces typing and serializes background queries; only the newest request can publish.</summary>
internal sealed class AsyncTextFilter<T>(Dispatcher dispatcher, Action<Exception> reportError) : IDisposable
{
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly object _pendingGate = new();
    private CancellationTokenSource? _pending;
    private object? _key;
    private object? _context;
    private long _version;
    private bool _disposed;

    public T? Result
    {
        get; private set;
    }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public bool Matches(object key)
    {
        return Equals(_key, key);
    }

    public void Request(object key, Func<CancellationToken, T> query, Action<T>? apply = null,
        int delayMilliseconds = 180, object? context = null)
    {
        dispatcher.VerifyAccess();
        if (_disposed || Equals(_key, key))
        {
            return;
        }
        if (!Equals(_context, context))
        {
            Result = default;
            _context = context;
        }
        _key = key;
        var pending = new CancellationTokenSource();
        lock (_pendingGate)
        {
            _pending?.Cancel();
            _pending = pending;
        }
        long version = ++_version;
        Completion = RunAsync(query, apply, pending, version, delayMilliseconds);
    }

    private async Task RunAsync(Func<CancellationToken, T> query, Action<T>? apply,
        CancellationTokenSource pending, long version, int delayMilliseconds)
    {
        var token = pending.Token;
        try
        {
            await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
            await _worker.WaitAsync(token).ConfigureAwait(false);
            T result;
            try
            {
                result = await Task.Run(() => query(token), token).ConfigureAwait(false);
            }
            finally
            {
                _worker.Release();
            }
            token.ThrowIfCancellationRequested();
            await dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && version == _version)
                {
                    Result = result;
                    apply?.Invoke(result);
                }
            }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            if (dispatcher.HasShutdownStarted)
            {
                return;
            }
            await dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && version == _version)
                {
                    reportError(error);
                }
            });
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
                pending.Dispose();
            }
        }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        _disposed = true;
        _version++;
        lock (_pendingGate)
        {
            _pending?.Cancel();
        }
        Result = default;
    }
}

internal static class TextFilterBinding
{
    internal static void Bind(System.Windows.Controls.TextBox text, Action<string> apply)
    {
        AsyncTextFilter<string>? work = null;
        void Request()
        {
            work ??= new(text.Dispatcher, error => text.ToolTip = $"Filtering failed: {error.Message}");
            string value = text.Text;
            work.Request(value, _ => value, apply);
        }
        text.TextChanged += (_, _) => Request();
        text.Loaded += (_, _) => Request();
        text.Unloaded += (_, _) =>
        {
            work?.Dispose();
            work = null;
        };
    }
}
