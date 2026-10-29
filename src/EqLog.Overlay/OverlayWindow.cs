using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace EqLog.Overlay;

/// <summary>
/// Borderless, transparent, always-on-top window hosting one <see cref="OverlayPanel"/>.
/// Unlocked: drag to move, drag edges to resize. Locked: clicks pass through to the game.
/// </summary>
public sealed class OverlayWindow : Window
{
    private bool _locked;
    private bool _hoverOnlyChrome;
    private bool _sliderInteraction;

    public OverlayWindow(OverlayPanel panel)
    {
        Panel = panel;
        Title = $"EQ Log Overlay - {panel.Caption}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 120;
        MinHeight = 40;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        MouseLeftButtonDown += (_, e) =>
        {
            if (!_locked && e.ButtonState == MouseButtonState.Pressed &&
                !OverlayPanel.IsInteractive(e.OriginalSource as DependencyObject))
            {
                DragMove();
            }
        };

        SourceInitialized += (_, _) => ApplyExStyle();
        PreviewMouseDown += (_, e) =>
        {
            if (!_locked && e.ChangedButton == MouseButton.Left &&
                FindSlider(e.OriginalSource as DependencyObject) is not null)
            {
                _sliderInteraction = true;
                ApplyExStyle();
                Activate();
            }
        };
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => EndSliderInteraction()), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) =>
        {
            if (FindSlider(Mouse.Captured as DependencyObject) is null)
            {
                EndSliderInteraction();
            }
        }), true);
        Deactivated += (_, _) => EndSliderInteraction();
        MouseEnter += (_, _) => UpdateChrome();
        MouseLeave += (_, _) => UpdateChrome();
        SetLocked(false);
    }

    public OverlayPanel Panel
    {
        get;
    }

    /// <summary>Takes the panel back from the dock window.</summary>
    public void AttachPanel()
    {
        if (Content != Panel)
        {
            Panel.SetDocked(false);
            Content = Panel;
        }
    }

    public void DetachPanel()
    {
        Content = null;
    }

    public void SetHoverOnlyChrome(bool enabled)
    {
        _hoverOnlyChrome = enabled;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        if (!_hoverOnlyChrome || !IsVisible || Content != Panel)
        {
            Panel.SetChromeHidden(false);
            return;
        }

        // Locked windows do not receive mouse events; sample the screen cursor as well.
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        var position = PointFromScreen(new Point(cursor.X, cursor.Y));
        bool hovered = new Rect(0, 0, ActualWidth, ActualHeight).Contains(position);
        Panel.SetChromeHidden(!hovered);
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (locked)
        {
            EndSliderInteraction();
        }
        Panel.SetLocked(locked);
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness = new Thickness(locked ? 0 : 6);
        ApplyExStyle();
    }

    private void EndSliderInteraction()
    {
        if (!_sliderInteraction)
        {
            return;
        }
        _sliderInteraction = false;
        ApplyExStyle();
    }

    private static Slider? FindSlider(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Slider slider)
            {
                return slider;
            }
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void ApplyExStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int always = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        bool typing = Panel.Key is "npcs" or "inventory" or "moteplan" || _sliderInteraction;
        if (_locked || !typing)
        {
            always |= NativeMethods.WS_EX_NOACTIVATE;
        }
        int remove = _locked ? 0 : NativeMethods.WS_EX_TRANSPARENT;
        if (!_locked && typing)
        {
            remove |= NativeMethods.WS_EX_NOACTIVATE;
        }
        NativeMethods.SetExStyle(hwnd, _locked ? always | NativeMethods.WS_EX_TRANSPARENT : always, remove);
    }

    /// <summary>Games that grab topmost (borderless fullscreen) can cover us; push ourselves back on top.</summary>
    public void ReassertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }
}