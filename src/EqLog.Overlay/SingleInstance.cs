using System.Threading;

namespace EqLog.Overlay;

/// <summary>
/// Keeps one app instance per Windows session so two instances never overwrite each other's saved data.
/// A second launch brings the running instance forward, or waits for it to exit when it is already closing.
/// </summary>
internal static class SingleInstance
{
    private const string Prefix = @"Local\EqLogOverlay-";
    private static readonly TimeSpan ClosingWait = TimeSpan.FromSeconds(60);
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _closing;
    private static RegisteredWaitHandle? _registration;

    /// <summary>Claims the instance; when another instance keeps running, signals it and returns false.</summary>
    public static bool TryAcquire()
    {
        var mutex = new Mutex(false, Prefix + "Instance");
        var closing = new EventWaitHandle(false, EventResetMode.ManualReset, Prefix + "Closing");
        if (!Acquire(mutex, TimeSpan.Zero) && !(closing.WaitOne(0) && Acquire(mutex, ClosingWait)))
        {
            mutex.Dispose();
            closing.Dispose();
            SignalRunningInstance();
            return false;
        }
        closing.Reset();
        _mutex = mutex;
        _closing = closing;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + "Activate");
        return true;
    }

    /// <summary>Runs <paramref name="activate"/> on a pool thread whenever another launch asks to bring this instance forward.</summary>
    public static void ListenForActivation(Action activate)
    {
        if (_activate is null)
        {
            return;
        }
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        {
            activate();
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Tells later launches to wait for this instance to finish saving instead of exiting.</summary>
    public static void MarkClosing()
    {
        _closing?.Set();
    }

    /// <summary>Releases the instance; must run on the thread that called <see cref="TryAcquire"/>.</summary>
    public static void Release()
    {
        _registration?.Unregister(null);
        _activate?.Dispose();
        _closing?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        _registration = null;
        _activate = null;
        _closing = null;
        _mutex = null;
    }

    private static bool Acquire(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance ended without releasing; ownership still transfers to this one.
            return true;
        }
    }

    private static void SignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(Prefix + "Activate", out var activate))
        {
            return;
        }
        using (activate)
        {
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            activate.Set();
        }
    }
}
