using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
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
                    VerifyDragGrip(window, panel, headerContent);
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

                VerifyMoteColumns();
                VerifyAllMoteTiersVisible();
                VerifyHoverBackgroundOpacity();
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

    private static void VerifyHoverBackgroundOpacity()
    {
        var panel = new OverlayPanel("timers", "Spell Timers", false);
        var frame = Assert.IsType<Border>(panel.Content);
        panel.SetBackgroundOpacity(0.2);
        panel.SetHoverBackgroundOpacity(0.9);
        Assert.Equal((byte)(0.2 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);

        foreach (bool docked in new[] { false, true })
        {
            panel.SetDocked(docked);
            panel.SetLocked(true);
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            Assert.Equal((byte)(0.9 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            Assert.Equal(1, panel.Opacity);
            panel.SetBackgroundOpacity(0.3);
            Assert.Equal((byte)(0.9 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.SetHoverBackgroundOpacity(0.8);
            Assert.Equal((byte)(0.8 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Assert.Equal((byte)(0.3 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.SetBackgroundOpacity(0.2);
            panel.SetHoverBackgroundOpacity(0.9);
        }
    }

    private static void VerifyDragGrip(OverlayWindow window, OverlayPanel panel, DockPanel header)
    {
        panel.SetChromeHidden(false);
        window.UpdateLayout();
        var grip = Assert.Single(header.Children.OfType<Thumb>());
        Assert.Same(panel.DragHandle, grip);
        Assert.Equal(Visibility.Visible, grip.Visibility);
        Assert.True(OverlayPanel.IsInteractive(grip));
        grip.ApplyTemplate();
        var box = Assert.IsType<Border>(VisualTreeHelper.GetChild(grip, 0));
        Assert.Equal(new Thickness(1), box.BorderThickness);

        var original = new Point(window.Left, window.Top);
        var title = header.Children.OfType<StackPanel>()
            .SelectMany(stack => stack.Children.OfType<TextBlock>())
            .First();
        title.RaiseEvent(new DragDeltaEventArgs(20, 30));
        panel.RaiseEvent(new DragDeltaEventArgs(20, 30));
        Assert.Equal(original, new Point(window.Left, window.Top));

        grip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        });
        Assert.True(grip.IsDragging);
        Assert.True(grip.IsMouseCaptured);
        grip.RaiseEvent(new DragDeltaEventArgs(20, 30));
        Assert.InRange(window.Left - original.X, 19, 21);
        Assert.InRange(window.Top - original.Y, 29, 31);
        var moved = new Point(window.Left, window.Top);
        Assert.True(grip.IsMouseCaptured);
        grip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
        Assert.False(grip.IsDragging);
        Assert.False(grip.IsMouseCaptured);

        window.SetLocked(true);
        Assert.Equal(Visibility.Collapsed, grip.Visibility);
        Assert.False(grip.IsEnabled);
        grip.RaiseEvent(new DragDeltaEventArgs(20, 30));
        Assert.Equal(moved, new Point(window.Left, window.Top));
        panel.SetDocked(true);
        Assert.Equal(Visibility.Visible, grip.Visibility);
        Assert.True(grip.IsEnabled);
        panel.SetDocked(false);
        window.SetLocked(false);
    }

    private static void VerifyMoteColumns()
    {
        var panel = new OverlayPanel("motes", "Motes", false) { FontSize = 13 };
        OverlayRow[] content =
        [
            new("Tier", "session (all)", Bold: true, ExtraColumn: "Owned", ValueColumn: "Gear/Spell"),
            new("Infinitesimal", "1  (1)", LootItem: "Mote of Infinitesimal Potential",
                ExtraColumn: "7", ToolTip: "Tier value details", ValueColumn: "1/1 ≤+0"),
            new("Infinite", "12  (2222)", LootItem: "Mote of Infinite Potential",
                ExtraColumn: "3", ValueColumn: "10/512 ≤+9"),
            new("Total", "13  (2223)", Bold: true, ExtraColumn: "10", ValueColumn: ""),
        ];
        panel.Update("Motes", null, content);
        var rows = LayoutMoteRows(panel, 300);
        var name = Assert.IsType<TextBlock>(rows[1].Children[3]);
        var valueColumn = Assert.IsType<Grid>(rows[2].Children[2]);
        var value = Assert.Single(valueColumn.Children.OfType<TextBlock>());
        Assert.Equal("Infinitesimal", name.Text);
        Assert.Equal("10/512 ≤+9", value.Text);
        AssertTextFits(name);
        AssertTextFits(value);
        var itemGrid = Assert.IsType<Grid>(rows[1].Parent);
        var itemButton = Assert.IsType<Button>(itemGrid.Parent);
        Assert.Equal("Tier value details", itemGrid.ToolTip);
        Assert.NotNull(itemButton.ContextMenu);
        string? selectedItem = null;
        panel.ItemWikiRequested += item =>
        {
            selectedItem = item;
        };
        itemButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Mote of Infinitesimal Potential", selectedItem);

        var original = content;
        content = [.. content];
        content[1] = content[1] with
        {
            Right = "100000  (2000000)",
            ExtraColumn = "3000000"
        };
        panel.Update("Motes", null, content);
        rows = LayoutMoteRows(panel, 450);
        var countColumn = Assert.IsType<Grid>(rows[1].Children[1]);
        var count = Assert.Single(countColumn.Children.OfType<TextBlock>());
        Assert.Equal("100000  (2000000)", count.Text);
        AssertTextFits(count);

        panel.Update("Motes", null, original);
        LayoutMoteRows(panel, 300);

        panel.SetDocked(true);
        panel.FontSize = 18;
        panel.Update("Motes", null, content);
        rows = LayoutMoteRows(panel, 540);
        AssertTextFits(Assert.IsType<TextBlock>(rows[1].Children[3]));
        valueColumn = Assert.IsType<Grid>(rows[2].Children[2]);
        AssertTextFits(Assert.Single(valueColumn.Children.OfType<TextBlock>()));
    }

    private static DockPanel[] LayoutMoteRows(OverlayPanel panel, double width)
    {
        panel.Measure(new Size(width, 400));
        panel.Arrange(new Rect(0, 0, width, 400));
        panel.UpdateLayout();

        var scroll = PanelScroll(panel);
        var stack = Assert.IsType<StackPanel>(scroll.Content);
        var rows = stack.Children.Cast<UIElement>()
            .Select(element => element is Button button ? Assert.IsType<Grid>(button.Content) : Assert.IsType<Grid>(element))
            .Select(grid => Assert.Single(grid.Children.OfType<DockPanel>()))
            .ToArray();

        Assert.Equal(4, rows.Length);
        double nameX = Assert.IsType<TextBlock>(rows[0].Children[3]).TranslatePoint(new Point(0, 0), stack).X;
        double valueX = Assert.IsType<Grid>(rows[0].Children[2]).TranslatePoint(new Point(0, 0), stack).X;
        double countX = Assert.IsType<Grid>(rows[0].Children[1]).TranslatePoint(new Point(0, 0), stack).X;
        double ownedX = Assert.IsType<Grid>(rows[0].Children[0]).TranslatePoint(new Point(0, 0), stack).X;
        Assert.True(nameX < valueX && valueX < countX && countX < ownedX);

        for (int columnIndex = 0; columnIndex < 3; columnIndex++)
        {
            var header = Assert.IsType<Grid>(rows[0].Children[columnIndex]);
            double headerX = header.TranslatePoint(new Point(0, 0), stack).X;
            foreach (var row in rows)
            {
                var column = Assert.IsType<Grid>(row.Children[columnIndex]);
                Assert.Equal(header.ActualWidth, column.ActualWidth, 2);
                Assert.Equal(headerX, column.TranslatePoint(new Point(0, 0), stack).X, 2);
                AssertTextFits(Assert.Single(column.Children.OfType<TextBlock>()));
            }
        }
        foreach (var row in rows)
        {
            AssertTextFits(Assert.IsType<TextBlock>(row.Children[3]));
        }

        return rows;
    }

    private static void VerifyAllMoteTiersVisible()
    {
        var definition = Assert.Single(OverlayPresenters.All, item => item.Key == "motes");
        var engine = new LogEngine { CharacterName = "Que" };
        var inventory = new QuestInventory();
        inventory.ApplyExport("Que", [], DateTime.UtcNow);
        var view = new OverlayView(new HashSet<ChatChannel>(), [], Inventory: inventory);
        var content = definition.Render(engine.Snapshot(DateTime.UtcNow), engine, view);
        var panel = new OverlayPanel(definition.Key, definition.Caption, false) { FontSize = 13 };
        panel.Update(content.Title, content.Subtitle, content.Rows);

        foreach (bool docked in new[] { false, true })
        {
            panel.SetDocked(docked);
            panel.Measure(new Size(definition.Width, definition.Height));
            panel.Arrange(new Rect(0, 0, definition.Width, definition.Height));
            panel.UpdateLayout();

            var scroll = PanelScroll(panel);
            var stack = Assert.IsType<StackPanel>(scroll.Content);
            Assert.Equal(MoteValues.MaxRank + 2, stack.Children.Count);
            Assert.True(scroll.ScrollableHeight <= 1,
                $"The default {(docked ? "docked" : "floating")} overlay hides {scroll.ScrollableHeight:0.#} pixels.");
        }
    }

    private static ScrollViewer PanelScroll(OverlayPanel panel)
    {
        var frame = Assert.IsType<Border>(panel.Content);
        var frameContent = Assert.IsType<Grid>(frame.Child);
        var bevel = Assert.IsType<Border>(frameContent.Children[0]);
        var inset = Assert.IsType<Border>(bevel.Child);
        var layout = Assert.IsType<DockPanel>(inset.Child);
        return Assert.Single(layout.Children.OfType<ScrollViewer>());
    }

    private static void AssertTextFits(TextBlock actual)
    {
        var unbounded = new TextBlock
        {
            Text = actual.Text,
            FontFamily = actual.FontFamily,
            FontSize = actual.FontSize,
            FontWeight = actual.FontWeight,
            FontStyle = actual.FontStyle,
            FontStretch = actual.FontStretch,
        };
        TextOptions.SetTextFormattingMode(unbounded, TextFormattingMode.Display);
        unbounded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(actual.ActualWidth + 1 >= unbounded.DesiredSize.Width,
            $"'{actual.Text}' needs {unbounded.DesiredSize.Width:0.#} pixels but has {actual.ActualWidth:0.#}.");
    }
}
