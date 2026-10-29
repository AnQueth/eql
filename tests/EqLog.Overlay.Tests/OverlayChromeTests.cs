using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
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
                var thickness = frame.BorderThickness;
                var padding = frame.Padding;

                panel.SetChromeHidden(true);

                Assert.Equal(Visibility.Hidden, header.Visibility);
                Assert.Equal(Brushes.Transparent, frame.BorderBrush);
                Assert.Equal(thickness, frame.BorderThickness);
                Assert.Equal(padding, frame.Padding);

                panel.SetLocked(true);
                Assert.Equal(Visibility.Hidden, header.Visibility);
                panel.SetChromeHidden(false);
                Assert.Equal(Visibility.Visible, header.Visibility);

                panel.SetLocked(false);
                Assert.NotEqual(Brushes.Transparent, frame.BorderBrush);
                panel.SetChromeHidden(true);
                panel.SetDocked(true);
                Assert.Equal(Visibility.Visible, header.Visibility);
                Assert.Equal(Visibility.Visible, frameContent.Children[1].Visibility);
                Assert.NotEqual(Brushes.Transparent, frame.BorderBrush);
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
