using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
                var application = new System.Windows.Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
                foreach (string key in new[] { "NightBackground", "NightBorder", "Pumpkin", "NightRaised",
                    "NightSurface", "NightText", "NightMuted", "Hover", "Selection" })
                {
                    application.Resources[key] = Brushes.Gray;
                }
                AsyncTextFilterTests.VerifyQuestVirtualization();
                TradeskillOverlayTests.VerifyMerchantListScrolling();
                TradeskillOverlayTests.VerifyCompactToolbarAndRecipeLoading();
                LevelingOverlayTests.VerifyGridAndDatePickerInteraction();
                VerifyMobInfoTitleAlwaysVisible();
                VerifyBreakdownButtonsUsePressClickMode();
                VerifyDpsRecentFightsScrolling();
                VerifyTemporaryDockFallback();
                VerifyCompactCards();

                var panel = new OverlayPanel("timers", "Spell Timers", false);
                var frame = Assert.IsType<Border>(panel.Content);
                VerifyRoundedFrame(panel, frame);
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

                    var spellFilter = new TextBox();
                    panel.SetToolbar(spellFilter);
                    window.UpdateLayout();
                    spellFilter.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = Mouse.PreviewMouseDownEvent,
                    });
                    Assert.Equal(0, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64() &
                        NativeMethods.WS_EX_NOACTIVATE);
                    window.SetLocked(true);
                    window.SetLocked(false);
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
                VerifyItemFilterToolbars();
                VerifyKnownSpellsVisibilityControl();
                application.Shutdown();
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

    private static void VerifyKnownSpellsVisibilityControl()
    {
        var now = new DateTime(2026, 11, 1, 17, 0, 0);
        var durations = new SpellDurationStore();
        durations.SetOverride("Active", 60);
        durations.SetOverride("Known", 30);
        var engine = new LogEngine(durations: durations);
        var snapshot = engine.Snapshot(now) with
        {
            Timers =
            [
                new("Active", "a kobold", true, false, now.AddSeconds(-10), 60, 10, 50, TimerState.Running)
            ]
        };
        var queries = EngineQuerySnapshot.Capture(engine);
        var definition = Assert.Single(OverlayPresenters.All, item => item.Key == "timers");
        var panel = new OverlayPanel("timers", "Spell Timers", false);
        var settings = new AppSettings();
        int visibilityChanges = 0;
        int trackingChanges = 0;
        var showKnown = MainWindow.ConfigureKnownSpellsControl(panel, settings.SpellTimersShowKnownSpells, visible =>
        {
            settings.SpellTimersShowKnownSpells = visible;
            visibilityChanges++;
        });
        panel.SpellTrackToggleRequested += _ =>
        {
            trackingChanges++;
        };
        var headerContent = Assert.IsType<DockPanel>(showKnown.Parent);
        var header = Assert.IsType<Border>(headerContent.Parent);
        Assert.Same(showKnown, headerContent.Children[0]);
        Assert.Equal(Dock.Bottom, DockPanel.GetDock(showKnown));
        Assert.Equal(0, visibilityChanges);

        void Render()
        {
            var view = new OverlayView(new HashSet<ChatChannel>(), [], ShowKnownSpells: settings.SpellTimersShowKnownSpells);
            var content = definition.Render(snapshot, queries, view);
            panel.Update(content.Title, content.Subtitle, content.Rows);
        }

        var window = new OverlayWindow(panel) { Width = 300, Height = 200 };
        window.AttachPanel();
        window.Show();
        try
        {
            var label = new TextBlock
            {
                Text = "Known spells",
                FontFamily = showKnown.FontFamily,
                FontSize = showKnown.FontSize,
                FontWeight = showKnown.FontWeight,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            foreach (double width in new[] { 120.0, 300.0 })
            {
                window.Width = width;
                window.UpdateLayout();
                Assert.True(showKnown.ActualWidth >= label.DesiredSize.Width + 15,
                    $"The known-spells control is clipped at overlay width {width}.");
            }

            foreach (bool docked in new[] { false, true })
            {
                panel.SetDocked(docked);
                Render();
                var shownRows = Assert.IsType<StackPanel>(PanelScroll(panel).Content);
                Assert.Equal(3, shownRows.Children.Count);
                Assert.True(showKnown.IsChecked);
                Assert.True(OverlayPanel.IsInteractive(showKnown));
                Assert.Equal(UsageAction.ToggleKnownSpells, UsageTracking.GetAction(showKnown));
                window.UpdateLayout();
                double rowsTop = shownRows.TranslatePoint(new Point(0, 0), panel).Y;
                panel.SetChromeHidden(true);
                window.UpdateLayout();
                Assert.Equal(docked ? Visibility.Visible : Visibility.Hidden, header.Visibility);
                Assert.Equal(docked, showKnown.IsVisible);
                Assert.True(shownRows.IsVisible);
                Assert.Equal(rowsTop, shownRows.TranslatePoint(new Point(0, 0), panel).Y, 3);
                Assert.Equal(3, shownRows.Children.Count);
                panel.SetChromeHidden(false);
                Assert.True(showKnown.IsVisible);

                var toggle = Assert.IsAssignableFrom<IToggleProvider>(
                    new CheckBoxAutomationPeer(showKnown).GetPattern(PatternInterface.Toggle));
                toggle.Toggle();
                Assert.False(settings.SpellTimersShowKnownSpells);
                Render();
                Assert.Single(shownRows.Children);
                Assert.False(showKnown.IsChecked);

                toggle.Toggle();
                Assert.True(settings.SpellTimersShowKnownSpells);
                Render();
                Assert.Equal(3, shownRows.Children.Count);
                Assert.True(showKnown.IsChecked);
            }
        }
        finally
        {
            window.Close();
        }

        Assert.Equal(4, visibilityChanges);
        Assert.Equal(0, trackingChanges);
        Assert.True(durations.IsTracked("Active"));
        Assert.True(durations.IsTracked("Known"));

        var hiddenPanel = new OverlayPanel("timers", "Spell Timers", false);
        int initialChanges = 0;
        var initiallyHidden = MainWindow.ConfigureKnownSpellsControl(hiddenPanel, false, _ =>
        {
            initialChanges++;
        });
        Assert.False(initiallyHidden.IsChecked);
        Assert.Equal(0, initialChanges);

        string spellFilter = "";
        var hiddenFilter = MainWindow.ConfigureKnownSpellsFilter(hiddenPanel, initiallyHidden, text => spellFilter = text);
        Assert.Equal("Known spells filter", System.Windows.Automation.AutomationProperties.GetName(hiddenFilter));
        Assert.True(OverlayPanel.IsInteractive(hiddenFilter));
        Assert.Equal(Visibility.Collapsed, hiddenFilter.Visibility);
        initiallyHidden.IsChecked = true;
        Assert.Equal(Visibility.Visible, hiddenFilter.Visibility);
        hiddenFilter.Text = "Bite";
        WaitForFilter(() => spellFilter == "Bite");
        Assert.Equal("Bite", spellFilter);
        initiallyHidden.IsChecked = false;
        Assert.Equal(Visibility.Collapsed, hiddenFilter.Visibility);

        var shownFilter = MainWindow.ConfigureKnownSpellsFilter(panel, showKnown, _ =>
        {
        });
        Assert.Equal(showKnown.IsChecked == true ? Visibility.Visible : Visibility.Collapsed, shownFilter.Visibility);
    }

    private static void VerifyBreakdownButtonsUsePressClickMode()
    {
        var panel = new OverlayPanel("dps", "DPS", false);
        var at = new DateTime(2026, 10, 21, 18, 0, 0);
        var hit = new DamageEvent(at, "Bob", "a goblin", 10, DamageKind.Melee, null, null);
        var breakdown = new AbilityBreakdown("Bob", "", "DPS",
            [new AbilityStats("Melee", 10, 10, 100, 1, 0, 10, 0)],
            [new DamageTimelinePoint(0, 10)], 1, at,
            [new AbilityTimeline("Melee", [new DamageTimelinePoint(0, 10)])], [hit], [hit]);
        panel.Update("a goblin", "active", [new OverlayRow("Bob", Breakdown: breakdown)]);
        var subtitle = FindLogicalChild<TextBlock>(panel, text => text.Text == "active")!;
        Assert.Equal(Visibility.Visible, subtitle.Visibility);

        var button = FindLogicalChild<Button>(panel, candidate => Equals(candidate.ToolTip, "Click for ability breakdown"))!;
        Assert.Equal(ClickMode.Press, button.ClickMode);
        int opened = 0;
        panel.BreakdownRequested += _ => opened++;

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, opened);

        var unchanged = breakdown with
        {
            Abilities = breakdown.Abilities.ToArray(),
            Timeline = breakdown.Timeline!.ToArray(),
            AbilityTimelines = [new AbilityTimeline("Melee", [new DamageTimelinePoint(0, 10)])],
            DamageEvents = breakdown.DamageEvents!.ToArray(),
            IncomingDamageEvents = breakdown.IncomingDamageEvents!.ToArray(),
        };
        panel.Update("a goblin", "active", [new OverlayRow("Bob", Breakdown: unchanged)]);
        Assert.Same(button, FindLogicalChild<Button>(panel, candidate => Equals(candidate.ToolTip, "Click for ability breakdown")));
        Assert.Equal(Visibility.Visible, subtitle.Visibility);

        var changed = unchanged with { DamageEvents = [hit, hit with { Amount = 20 }] };
        panel.Update("a goblin", "active", [new OverlayRow("Bob", Breakdown: changed)]);
        var updatedButton = FindLogicalChild<Button>(panel, candidate => Equals(candidate.ToolTip, "Click for ability breakdown"))!;
        Assert.NotSame(button, updatedButton);
        AbilityBreakdown? clicked = null;
        panel.BreakdownRequested += result => clicked = result;
        updatedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, clicked!.DamageEvents!.Count);

        panel.Update("a goblin", null, [new OverlayRow("Bob", "20 dps", Breakdown: breakdown with { Title = "Updated" })]);
        Assert.Equal(Visibility.Hidden, subtitle.Visibility);
        var refreshedButton = FindLogicalChild<Button>(panel, candidate => Equals(candidate.ToolTip, "Click for ability breakdown"))!;
        Assert.NotSame(updatedButton, refreshedButton);
        Assert.Equal(ClickMode.Press, refreshedButton.ClickMode);
    }

    private static void VerifyDpsRecentFightsScrolling()
    {
        var start = new DateTime(2026, 10, 21, 18, 0, 0);
        var engine = new LogEngine { CharacterName = "Que" };
        var lines = Enumerable.Range(0, 25)
            .Select(i => $"[{start.AddSeconds(i):ddd MMM dd HH:mm:ss yyyy}] You slash Guardian {(char)('A' + i)} for 100 points of damage.")
            .ToArray();
        engine.ProcessLines(lines);
        var snapshot = engine.Snapshot(start.AddSeconds(30));
        Assert.Equal(20, snapshot.RecentFights.Count);
        Assert.Equal("Guardian Y", snapshot.RecentFights[0].Target);
        Assert.Equal("Guardian F", snapshot.RecentFights[^1].Target);

        var definition = Assert.Single(OverlayPresenters.All, item => item.Key == "dps");
        var view = new OverlayView(new HashSet<ChatChannel>(), []);
        var queries = EngineQuerySnapshot.Capture(engine);
        var content = definition.Render(snapshot, queries, view);
        Assert.Empty(content.Rows);

        var panel = new OverlayPanel("dps", "DPS", false);
        var list = new DpsFightList(panel, false);
        panel.SetBody(list);
        bool? expanded = null;
        var checkbox = MainWindow.ConfigureDpsToolbar(panel, false, value =>
        {
            expanded = value;
            list.SetFullDetails(value);
        });
        var toolbar = Assert.IsType<ContentControl>(checkbox.Parent);
        Assert.Same(checkbox, toolbar.Content);
        Assert.Equal("Full details", checkbox.Content);
        Assert.Null(expanded);
        panel.Update(content.Title, content.Subtitle, content.Rows);
        list.Update(snapshot with { CurrentFight = null, RecentFights = [] });
        var rows = Assert.IsType<StackPanel>(list.Content);
        Assert.Equal("No fight yet", Assert.IsType<TextBlock>(Assert.Single(rows.Children)).Text);
        list.Update(snapshot);
        Assert.Equal(20, rows.Children.Count);
        var first = Assert.IsType<StackPanel>(rows.Children[0]);
        var oldest = Assert.IsType<StackPanel>(rows.Children[^1]);
        var firstButton = Assert.IsType<Button>(first.Children[0]);
        var oldestButton = Assert.IsType<Button>(oldest.Children[0]);
        Assert.NotNull(FindLogicalChild<TextBlock>(firstButton, text => text.Text == "Guardian Y"));
        Assert.NotNull(FindLogicalChild<TextBlock>(oldestButton, text => text.Text == "Guardian F"));

        oldestButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var details = Assert.IsType<ContentControl>(oldest.Children[1]);
        Assert.Equal(Visibility.Visible, details.Visibility);
        var compact = Assert.IsType<StackPanel>(details.Content);
        var attacker = Assert.Single(compact.Children.OfType<Button>());
        Assert.NotNull(FindLogicalChild<TextBlock>(attacker, text => text.Text == "You"));
        AbilityBreakdown? clicked = null;
        panel.BreakdownRequested += breakdown => clicked = breakdown;
        attacker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("You", clicked?.Title);
        list.Update(snapshot);
        Assert.Same(compact, details.Content);

        checkbox.IsChecked = true;
        Assert.True(expanded);
        var tabs = Assert.IsType<TabControl>(details.Content);
        Assert.Equal(20, rows.Children.Count);
        Assert.Same(oldest, rows.Children[^1]);
        tabs.SelectedIndex = 1;
        list.Update(snapshot);
        Assert.Same(tabs, details.Content);
        Assert.Equal(1, tabs.SelectedIndex);
        firstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var firstDetails = Assert.IsType<ContentControl>(first.Children[1]);
        var firstTabs = Assert.IsType<TabControl>(firstDetails.Content);
        var abilityTable = Assert.IsType<DataGrid>(((TabItem)firstTabs.Items[0]).Content);
        Assert.Equal(100, Assert.Single(abilityTable.Items.Cast<AbilityStats>()).Total);
        panel.Measure(new Size(definition.Width, definition.Height));
        panel.Arrange(new Rect(0, 0, definition.Width, definition.Height));
        panel.UpdateLayout();
        Assert.True(firstTabs.ActualHeight > 0);
        Assert.True(list.ScrollableHeight > 0);
        string additionalHit = $"[{start.AddSeconds(25):ddd MMM dd HH:mm:ss yyyy}] You slash Guardian Y for 200 points of damage.";
        engine.ProcessLines([additionalHit]);
        var updatedSnapshot = engine.Snapshot(start.AddSeconds(30));
        list.Update(updatedSnapshot);
        Assert.Same(first, rows.Children[0]);
        Assert.Same(firstTabs, firstDetails.Content);
        Assert.Equal(300, Assert.Single(abilityTable.Items.Cast<AbilityStats>()).Total);
        firstTabs.SelectedIndex = 1;
        string nextFight = $"[{start.AddSeconds(26):ddd MMM dd HH:mm:ss yyyy}] You slash Guardian Z for 50 points of damage.";
        engine.ProcessLines([nextFight]);
        updatedSnapshot = engine.Snapshot(start.AddSeconds(30));
        list.Update(updatedSnapshot);
        Assert.Equal(20, rows.Children.Count);
        Assert.Same(first, rows.Children[1]);
        Assert.Same(firstTabs, firstDetails.Content);
        Assert.Equal(1, firstTabs.SelectedIndex);

        checkbox.IsChecked = false;
        Assert.False(expanded);
        Assert.IsType<StackPanel>(firstDetails.Content);
        firstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, firstDetails.Visibility);

        foreach (bool docked in new[] { false, true })
        {
            panel.SetDocked(docked);
            panel.Measure(new Size(definition.Width, definition.Height));
            panel.Arrange(new Rect(0, 0, definition.Width, definition.Height));
            panel.UpdateLayout();
            Assert.Equal(ScrollBarVisibility.Auto, list.VerticalScrollBarVisibility);
            Assert.True(list.ScrollableHeight > 0);
            list.ScrollToEnd();
            panel.UpdateLayout();
            Assert.True(list.VerticalOffset > 0);
            Assert.Equal(list.ScrollableHeight, list.VerticalOffset, 1);
            double offset = list.VerticalOffset;
            list.Update(updatedSnapshot);
            panel.UpdateLayout();
            Assert.Equal(offset, list.VerticalOffset, 1);
        }

        list.Update(snapshot with { RecentFights = [snapshot.RecentFights[0], snapshot.RecentFights[0]] });
        Assert.Equal(2, rows.Children.Count);
        Assert.NotSame(rows.Children[0], rows.Children[1]);
    }

    private static void VerifyTemporaryDockFallback()
    {
        foreach (bool autoArrange in new[] { false, true })
        {
            var settings = new DockWindowSettings { AutoArrange = autoArrange };
            var firstWorkspace = settings.CreateWorkspace("First");
            firstWorkspace.Panels["session"] = new DockPanelSettings
            {
                DockLeft = 4,
                DockTop = 4,
                DockWidth = 250,
                DockHeight = 180,
            };
            var dock = new DockWindow(settings);
            var existing = new OverlayPanel("session", "Session", false);
            var placement = new OverlayPlacement
            {
                Visible = true,
                Width = 300,
                Height = 220,
                DockLeft = 42,
                DockTop = 73,
            };
            var panel = new OverlayPanel("dps", "DPS", false);
            var window = new OverlayWindow(panel);
            try
            {
                dock.AddPanel(existing, new OverlayPlacement { Width = 250, Height = 180 });
                double? savedLeft = firstWorkspace.Panels["session"].DockLeft;
                double? savedTop = firstWorkspace.Panels["session"].DockTop;

                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                Assert.True(dock.HasPanel("dps"));
                Assert.True(dock.IsTemporaryPanel("dps"));
                Assert.False(window.IsVisible);
                Assert.Null(window.Content);
                Assert.False(placement.Docked);
                Assert.False(settings.IsPanelInAnyWorkspace("dps"));
                Assert.Equal(42, placement.DockLeft);
                Assert.Equal(73, placement.DockTop);
                Assert.Equal(savedLeft, firstWorkspace.Panels["session"].DockLeft);
                Assert.Equal(savedTop, firstWorkspace.Panels["session"].DockTop);
                var button = FindLogicalChild<Button>(panel, candidate => Equals(candidate.ToolTip, "Keep this panel in the current workspace"));
                Assert.NotNull(button);

                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                Assert.True(dock.IsTemporaryPanel("dps"));
                var temporaryTile = Assert.IsType<Grid>(panel.Parent);
                firstWorkspace.Panels["loot"] = new DockPanelSettings
                {
                    DockLeft = Canvas.GetLeft(temporaryTile),
                    DockTop = Canvas.GetTop(temporaryTile),
                    DockWidth = 300,
                    DockHeight = 220,
                };
                var loot = new OverlayPanel("loot", "Loot", false);
                dock.AddPanel(loot, new OverlayPlacement { Width = 300, Height = 220 });
                var lootTile = Assert.IsType<Grid>(loot.Parent);
                var temporaryBounds = new Rect(Canvas.GetLeft(temporaryTile), Canvas.GetTop(temporaryTile), temporaryTile.Width, temporaryTile.Height);
                var lootBounds = new Rect(Canvas.GetLeft(lootTile), Canvas.GetTop(lootTile), lootTile.Width, lootTile.Height);
                Assert.False(temporaryBounds.IntersectsWith(lootBounds));

                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, true, false);
                Assert.False(dock.HasPanel("dps"));
                Assert.True(window.IsVisible);
                Assert.Same(panel, window.Content);
                Assert.Equal(savedLeft, firstWorkspace.Panels["session"].DockLeft);
                Assert.Equal(savedTop, firstWorkspace.Panels["session"].DockTop);

                MainWindow.ApplyOverlayPlacement(dock, window, placement, false, false, false);
                Assert.False(dock.HasPanel("dps"));
                Assert.False(window.IsVisible);
                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, true);
                Assert.True(window.IsVisible);
                Assert.False(dock.HasPanel("dps"));

                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                var secondWorkspace = settings.CreateWorkspace("Second");
                dock.ClearPanelsForWorkspaceChange();
                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                Assert.True(dock.IsTemporaryPanel("dps"));
                Assert.Empty(secondWorkspace.Panels);
                Assert.False(settings.IsPanelInAnyWorkspace("dps"));

                dock.SetPanelInActiveWorkspace("dps", true, placement);
                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                Assert.False(dock.IsTemporaryPanel("dps"));
                Assert.True(placement.Docked);
                Assert.True(secondWorkspace.Panels.ContainsKey("dps"));
                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, true, false);
                Assert.True(dock.HasPanel("dps"));
                Assert.False(window.IsVisible);
            }
            finally
            {
                window.Close();
                dock.Close();
            }
        }
    }

    private static T? FindLogicalChild<T>(DependencyObject parent, Func<T, bool> predicate)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match && predicate(match))
            {
                return match;
            }

            if (child is DependencyObject node && FindLogicalChild(node, predicate) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void VerifyItemFilterToolbars()
    {
        foreach (string key in new[] { "loot", "itemupgrades" })
        {
            var panel = new OverlayPanel(key, key, false);
            var options = key == "loot" ? new ComboBox { ItemsSource = new[] { "Recent", "Grouped" } } : null;
            string value = "";
            var filter = MainWindow.ConfigureItemFilterToolbar(panel, text => value = text, options);

            Assert.Equal("Item name filter", System.Windows.Automation.AutomationProperties.GetName(filter));
            Assert.IsType<StackPanel>(filter.Parent);
            Assert.True(OverlayPanel.IsInteractive(filter));
            if (options is not null)
            {
                Assert.Same(filter.Parent, options.Parent);
                options.SelectedIndex = 1;
            }
            foreach (bool docked in new[] { false, true })
            {
                panel.SetDocked(docked);
                filter.Text = "Sword";
                WaitForFilter(() => value == "Sword");
                Assert.Equal("Sword", value);
                filter.Clear();
                WaitForFilter(() => value == "");
                Assert.Equal("", value);
            }
        }
    }

    private static void VerifyMobInfoTitleAlwaysVisible()
    {
        var panel = new OverlayPanel("mobinfo", "Mob info", false);
        var frame = Assert.IsType<Border>(panel.Content);
        var frameContent = Assert.IsType<Grid>(frame.Child);
        var bevel = Assert.IsType<Border>(frameContent.Children[0]);
        var inset = Assert.IsType<Border>(bevel.Child);
        var layout = Assert.IsType<DockPanel>(inset.Child);
        var header = Assert.IsType<Border>(layout.Children[0]);
        foreach (bool docked in new[] { false, true, false })
        {
            panel.SetDocked(docked);
            foreach (bool locked in new[] { false, true })
            {
                panel.SetLocked(locked);
                panel.SetChromeHidden(true);
                Assert.Equal(Visibility.Visible, header.Visibility);
                if (!docked)
                {
                    Assert.Equal(Brushes.Transparent, frame.BorderBrush);
                }
                panel.SetChromeHidden(false);
                Assert.Equal(Visibility.Visible, header.Visibility);
            }
        }
    }

    private static void WaitForFilter(Func<bool> completed)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        timer.Tick += (_, _) =>
        {
            if (completed() || elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        try
        {
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Assert.True(completed(), "The latest text filter was not applied.");
        }
        finally
        {
            timer.Stop();
        }
    }
    private static void VerifyHoverBackgroundOpacity()
    {
        var panel = new OverlayPanel("timers", "Spell Timers", false);
        var frame = Assert.IsType<Border>(panel.Content);
        var hoverChanges = new List<bool>();
        panel.BackgroundHoverChanged += hovered => hoverChanges.Add(hovered);
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
            panel.SetBackgroundOpacity(0.9);
            Assert.Equal((byte)(0.9 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.SetHoverBackgroundOpacity(0.1);
            Assert.Equal((byte)(0.9 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.SetHoverBackgroundOpacity(0.9);
            Assert.Equal((byte)(0.9 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.SetHoverBackgroundOpacity(0.8);
            panel.SetBackgroundOpacity(0.3);
            Assert.Equal((byte)(0.8 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Assert.Equal((byte)(0.3 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.SetBackgroundOpacity(0.9);
            panel.SetHoverBackgroundOpacity(0.1);
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            Assert.Equal((byte)(0.9 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Assert.Equal((byte)(0.9 * 255), Assert.IsType<SolidColorBrush>(frame.Background).Color.A);
            panel.SetBackgroundOpacity(0.2);
            panel.SetHoverBackgroundOpacity(0.9);
        }
        Assert.Equal(new[] { true, false, true, false, true, false, true, false }, hoverChanges);
    }

    private static void VerifyRoundedFrame(OverlayPanel panel, Border frame)
    {
        foreach (bool docked in new[] { false, true, false })
        {
            panel.SetDocked(docked);
            Assert.Equal(new CornerRadius(12), frame.CornerRadius);
            foreach (var size in new[] { new Size(300, 220), new Size(420, 320) })
            {
                panel.Measure(size);
                panel.Arrange(new Rect(size));
                panel.UpdateLayout();
                var clip = Assert.IsType<RectangleGeometry>(frame.Clip);
                Assert.Equal(12, clip.RadiusX);
                Assert.Equal(12, clip.RadiusY);
                Assert.Equal(frame.ActualWidth, clip.Rect.Width);
                Assert.Equal(frame.ActualHeight, clip.Rect.Height);
                Assert.False(clip.FillContains(new Point(0, 0)));
                Assert.True(clip.FillContains(new Point(frame.ActualWidth / 2, frame.ActualHeight / 2)));
            }
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

    private static void VerifyCompactCards()
    {
        var panel = new OverlayPanel("timers", "Timers", false) { FontSize = 13 };
        panel.Update("Timers", null, [new OverlayRow("First", Details: "A detail")]);
        foreach (bool docked in new[] { false, true })
        {
            panel.SetDocked(docked);
            panel.Measure(new Size(300, 200));
            panel.Arrange(new Rect(0, 0, 300, 200));
            panel.UpdateLayout();
            var stack = Assert.IsType<StackPanel>(PanelScroll(panel).Content);
            var card = Assert.IsType<Grid>(Assert.Single(stack.Children));
            Assert.InRange(card.ActualHeight, 1, 45);
        }

        panel.FontSize = 18;
        panel.Measure(new Size(300, 200));
        panel.Arrange(new Rect(0, 0, 300, 200));
        panel.UpdateLayout();
        var rows = Assert.IsType<StackPanel>(PanelScroll(panel).Content);
        var largeCard = Assert.IsType<Grid>(Assert.Single(rows.Children));
        var content = Assert.IsType<StackPanel>(Assert.Single(Assert.Single(largeCard.Children.OfType<DockPanel>()).Children));
        foreach (var text in content.Children.OfType<TextBlock>())
        {
            var unbounded = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily };
            unbounded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(text.ActualHeight + 1 >= unbounded.DesiredSize.Height);
        }
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
        var read = new EqLog.Application.Inventory.InventoryReadModel(inventory);
        var view = new OverlayView(new HashSet<ChatChannel>(), [], Inventory: read, MoteInventory: read.Motes("Que"));
        var content = definition.Render(engine.Snapshot(DateTime.UtcNow), EngineQuerySnapshot.Capture(engine), view);
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
            Assert.Equal(MoteValues.MaxRank * 2 + 2, stack.Children.Count);
            Assert.True(scroll.ScrollableHeight > 1,
                $"The {(docked ? "docked" : "floating")} overlay must scroll to show every mote and fragment.");
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
