using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class OverlayChromeTests
{
    [Fact]
    public void SetChromeHidden_PreservesLayoutAndRestoresDockedChrome()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application();
                foreach (string key in new[] { "NightBackground", "NightBorder", "Pumpkin", "NightRaised" })
                {
                    application.Resources[key] = Brushes.Gray;
                }

                var panel = new OverlayPanel("timers", "Spell Timers", false);
                var frame = Assert.IsType<Border>(panel.Content);
                var frameContent = Assert.IsType<Grid>(frame.Child);
                var bevel = Assert.IsType<Border>(frameContent.Children[0]);
                var inset = Assert.IsType<Border>(bevel.Child);
                var layout = Assert.IsType<DockPanel>(inset.Child);
                var header = Assert.IsType<Border>(layout.Children[0]);
                var headerContent = Assert.IsType<DockPanel>(header.Child);
                var backgroundControls = Assert.Single(headerContent.Children.OfType<StackPanel>(),
                    child => child.Children.OfType<Slider>().Any());
                var slider = Assert.Single(backgroundControls.Children.OfType<Slider>());
                var dockButton = Assert.Single(headerContent.Children.OfType<Button>(),
                    button => button.Content is string text && text.Contains("Dock", StringComparison.Ordinal));
                Assert.Equal(headerContent.Children.IndexOf(dockButton) + 1,
                    headerContent.Children.IndexOf(backgroundControls));
                Assert.Equal(Dock.Right, DockPanel.GetDock(backgroundControls));
                Assert.Equal(56, slider.Width);
                double? changedOpacity = null;
                panel.BackgroundOpacityChanged += value => changedOpacity = value;
                Assert.True(OverlayPanel.IsInteractive(slider));
                slider.Value = 0.3;
                Assert.Equal(0.3, changedOpacity);
                Assert.Equal((byte)(0.3 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
                panel.SetBackgroundOpacity(0.8);
                Assert.Equal(0.8, slider.Value);
                Assert.Equal("Background opacity: 80%", slider.ToolTip);
                Assert.Equal(Visibility.Visible, backgroundControls.Visibility);
                var thickness = frame.BorderThickness;
                var padding = frame.Padding;

                panel.SetChromeHidden(true);

                Assert.Equal(Visibility.Hidden, header.Visibility);
                Assert.Equal(Brushes.Transparent, frame.BorderBrush);
                Assert.Equal(thickness, frame.BorderThickness);
                Assert.Equal(padding, frame.Padding);

                panel.SetLocked(true);
                Assert.Equal(Visibility.Collapsed, backgroundControls.Visibility);
                Assert.Equal(Visibility.Hidden, header.Visibility);
                panel.SetChromeHidden(false);
                Assert.Equal(Visibility.Visible, header.Visibility);

                panel.SetLocked(false);
                Assert.NotEqual(Brushes.Transparent, frame.BorderBrush);
                panel.SetChromeHidden(true);
                panel.SetDocked(true);
                Assert.Equal(Visibility.Collapsed, backgroundControls.Visibility);
                Assert.Equal(Visibility.Visible, header.Visibility);
                Assert.Equal(Visibility.Visible, frameContent.Children[1].Visibility);
                Assert.NotEqual(Brushes.Transparent, frame.BorderBrush);

                var window = new OverlayWindow(panel);
                try
                {
                    window.AttachPanel();
                    window.Show();
                    var handle = new WindowInteropHelper(window).Handle;
                    Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64() &
                        NativeMethods.WS_EX_NOACTIVATE);

                    slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Mouse.PreviewMouseDownEvent,
                    });
                    Assert.Equal(0, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64() &
                        NativeMethods.WS_EX_NOACTIVATE);

                    slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Mouse.PreviewMouseUpEvent,
                    });
                    Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64() &
                        NativeMethods.WS_EX_NOACTIVATE);

                    window.SetLocked(true);
                    slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Mouse.PreviewMouseDownEvent,
                    });
                    long lockedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                    Assert.NotEqual(0, lockedStyle & NativeMethods.WS_EX_NOACTIVATE);
                    Assert.NotEqual(0, lockedStyle & NativeMethods.WS_EX_TRANSPARENT);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
