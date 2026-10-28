using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EqLog.Overlay;

/// <summary>
/// Floating overlays require an active EverQuest window, or focus in one of our floating overlays.
/// </summary>
internal static partial class GameWindowWatcher
{
    private const string ProcessName = "eqgame";
    private const int DWMWA_CLOAKED = 14;

    private static HashSet<uint> _gamePids = [];
    private static DateTime _pidsCheckedAt = DateTime.MinValue;

    public static bool IsGameVisible()
    {
        RefreshPids();
        if (_gamePids.Count == 0)
        {
            return false;
        }

        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        bool overlayFocused = HwndSource.FromHwnd(foreground)?.RootVisual is OverlayWindow { IsVisible: true };
        if (!_gamePids.Contains(foregroundPid) && !overlayFocused)
        {
            return false;
        }

        foreach (var hwnd in GameWindows())
        {
            if (!IsIconic(hwnd) && !IsCloaked(hwnd) && GetWindowRect(hwnd, out var rect) && rect.Area > 0 &&
                (overlayFocused || GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == foregroundPid))
            {
                return true;
            }
        }

        return false;
    }

    private static void RefreshPids()
    {
        // Looking up processes is relatively slow; the window checks below are cheap.
        if (DateTime.UtcNow - _pidsCheckedAt < TimeSpan.FromSeconds(3) && _gamePids.Count > 0)
        {
            return;
        }

        _pidsCheckedAt = DateTime.UtcNow;
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }

        _gamePids = pids;
    }

    private static List<IntPtr> GameWindows()
    {
        var found = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowThreadProcessId(hwnd, out uint pid) != 0 && _gamePids.Contains(pid))
            {
                found.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;

        public readonly long Area
        {
            get
            {
                return Math.Max(0L, Right - Left) * Math.Max(0L, Bottom - Top);
            }
        }

    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
