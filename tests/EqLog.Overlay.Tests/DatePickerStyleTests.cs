using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class DatePickerStyleTests
{
    [Fact]
    public void WatermarkHidesWhileFocusedOrHoldingText()
    {
        RunSta(() =>
        {
            var resources = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/EqLogOverlay;component/Styles/DatePicker.xaml", UriKind.Relative));
            foreach (string key in new[] { "NightBackground", "NightBorder", "Pumpkin", "NightRaised", "NightSurface",
                "NightText", "NightMuted", "Hover", "Selection" })
            {
                resources[key] = Brushes.Gray;
            }
            var picker = new DatePicker();
            var other = new TextBox();
            var window = new Window
            {
                Resources = resources,
                Content = new StackPanel { Children = { other, picker } },
                ShowInTaskbar = false,
                ShowActivated = true,
                Left = -5000,
            };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            var box = Assert.IsType<DatePickerTextBox>(picker.Template.FindName("PART_TextBox", picker));
            var watermark = Assert.IsType<ContentControl>(box.Template.FindName("PART_Watermark", box));
            Assert.Equal(Visibility.Visible, watermark.Visibility);

            box.Focus();
            Keyboard.Focus(box);
            Assert.True(box.IsKeyboardFocusWithin);
            Assert.Equal(Visibility.Collapsed, watermark.Visibility);

            box.Text = "11/2";
            other.Focus();
            Keyboard.Focus(other);
            Assert.False(box.IsKeyboardFocusWithin);
            Assert.Equal(Visibility.Collapsed, watermark.Visibility);

            box.Text = "";
            Assert.Equal(Visibility.Visible, watermark.Visibility);
            window.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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
