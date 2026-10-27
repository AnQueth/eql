using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EqLog.Overlay;

/// <summary>
/// Works out whether the EverQuest window (eqgame.exe) is visible on screen: running, not minimized and
/// not mostly covered by another application's window. Windows belonging to this app never count as covering it.
/// </summary>
internal static partial class GameWindowWatcher
{
    private const string ProcessName = "eqgame";
    private const uint GW_HWNDPREV = 3;
    private const int DWMWA_CLOAKED = 14;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly uint OwnPid = (uint)Environment.ProcessId;
    private static HashSet<uint> _gamePids = [];
    private static DateTime _pidsCheckedAt = DateTime.MinValue;

    public static bool IsGameVisible()
    {
        RefreshPids();
        if (_gamePids.Count == 0)
        {
            return false;
        }

        foreach (var hwnd in GameWindows())
        {
            if (!IsIconic(hwnd) && GetWindowRect(hwnd, out var rect) && rect.Area > 0 && !IsCovered(hwnd, rect))
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

    /// <summary>True when windows above the game in z-order cover more than half of it.</summary>
    private static bool IsCovered(IntPtr game, Rect gameRect)
    {
        long covered = 0;
        for (var h = GetWindow(game, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
        {
            if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h))
            {
                continue;
            }

            GetWindowThreadProcessId(h, out uint pid);
            if (pid == OwnPid || _gamePids.Contains(pid))
            {
                continue;
            }

            // Skip always-on-top helpers (other overlays, tooltips, notifications) and click-through windows.
            long ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((ex & (WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
            {
                continue;
            }

            if (GetWindowRect(h, out var r))
            {
                // Overlapping windows can double count, which only errs towards "covered" when it really is.
                covered += gameRect.Intersect(r);
                if (covered * 2 > gameRect.Area)
                {
                    return true;
                }
            }
        }

        return false;
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

        public readonly long Intersect(Rect o)
        {
            return Math.Max(0L, Math.Min(Right, o.Right) - Math.Max(Left, o.Left)) * Math.Max(0L, Math.Min(Bottom, o.Bottom) - Math.Max(Top, o.Top));
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
    private static partial IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
