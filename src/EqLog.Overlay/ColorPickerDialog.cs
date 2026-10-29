using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EqLog.Overlay;

/// <summary>Outcome of <see cref="ColorPickerDialog.Pick"/>: the chosen color (null when cancelled) and any problem to report.</summary>
public sealed record ColorPickResult(Color? Color, string? Message);

/// <summary>
/// The Windows color picker, opened beside the button that requested it. Custom colors are shared by every
/// picker in the app for the rest of the session.
/// </summary>
public static class ColorPickerDialog
{
    private static readonly uint[] CustomColors = new uint[16];

    public static ColorPickResult Pick(FrameworkElement anchor, Color initial)
    {
        var owner = Window.GetWindow(anchor);
        NativeMethods.ScreenRect? anchorBounds = null;
        int gap = 0;
        if (PresentationSource.FromVisual(anchor) is not null)
        {
            var topLeft = anchor.PointToScreen(new Point(0, 0));
            var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            anchorBounds = new NativeMethods.ScreenRect
            {
                Left = (int)Math.Round(topLeft.X),
                Top = (int)Math.Round(topLeft.Y),
                Right = (int)Math.Round(bottomRight.X),
                Bottom = (int)Math.Round(bottomRight.Y),
            };
            gap = Math.Max(1, (int)Math.Round(6 * VisualTreeHelper.GetDpi(anchor).DpiScaleX));
        }

        string? placementError = null;
        NativeMethods.ChooseColorHook hook = (handle, message, _, _) =>
        {
            if (message == 0x0110 && anchorBounds is { } bounds) // WM_INITDIALOG
            {
                placementError = PositionColorPicker(handle, bounds, gap);
            }
            return UIntPtr.Zero;
        };
        var pinned = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
        try
        {
            var dialog = new NativeMethods.ChooseColorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.ChooseColorInfo>(),
                Owner = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).EnsureHandle(),
                Color = (uint)(initial.R | (initial.G << 8) | (initial.B << 16)),
                CustomColors = pinned.AddrOfPinnedObject(),
                Flags = 0x00000001 | 0x00000002 | 0x00000010, // CC_RGBINIT | CC_FULLOPEN | CC_ENABLEHOOK
                Hook = Marshal.GetFunctionPointerForDelegate(hook),
            };
            if (NativeMethods.ChooseColor(ref dialog))
            {
                uint selected = dialog.Color;
                var color = Color.FromRgb((byte)(selected & 0xFF), (byte)((selected >> 8) & 0xFF), (byte)((selected >> 16) & 0xFF));
                return new ColorPickResult(color, placementError);
            }

            uint error = NativeMethods.CommDlgExtendedError();
            if (error != 0)
            {
                return new ColorPickResult(null, $"Could not open the color picker (dialog error 0x{error:X}). You can still enter a color.");
            }
            return new ColorPickResult(null, placementError);
        }
        finally
        {
            GC.KeepAlive(hook);
            pinned.Free();
        }
    }

    public static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string? PositionColorPicker(IntPtr dialog, NativeMethods.ScreenRect anchor, int gap)
    {
        if (!NativeMethods.GetWindowRect(dialog, out var bounds))
        {
            return $"Could not read the color picker bounds (Windows error {Marshal.GetLastPInvokeError()}). Windows used its default position.";
        }

        var monitor = NativeMethods.MonitorFromRect(in anchor, 2); // MONITOR_DEFAULTTONEAREST
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return $"Could not read the monitor work area (Windows error {Marshal.GetLastPInvokeError()}). Windows used its default position.";
        }

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        var work = info.WorkArea;
        int x = anchor.Right + gap;
        if (x + width > work.Right)
        {
            x = anchor.Left - width - gap;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(anchor.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (!NativeMethods.SetWindowPos(dialog, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE))
        {
            return "Could not move the color picker beside the button. Windows used its default position.";
        }
        return null;
    }
}
