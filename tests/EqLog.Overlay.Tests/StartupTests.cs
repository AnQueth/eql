using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Application.Settings;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using EqLog.Overlay.Views;
using Xunit;

namespace EqLog.Overlay.Tests;

internal static class StartupTests
{
    internal static void WorkspaceOpensBeforeDataAndClosingDoesNotSavePlaceholderHistory()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            VerifyWorkspaceWhileLoading();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void VerifyWorkspaceWhileLoading()
    {
        var theme = new ThemePalette
        {
            Name = "Startup test",
            Colors = ThemePalette.ColorKeys.ToDictionary(key => key, _ => "#808080"),
        };
        var settings = new AppSettings { Theme = theme, OnlyWhenGameVisible = false, DisplayScale = 1.25 };
        foreach (var definition in OverlayPresenters.All)
        {
            settings.Overlays[definition.Key] = new OverlayPlacement
            {
                Visible = definition.Key is "inventory" or "tradeskills" or "npcs" or "exaltations",
                Docked = true,
                Width = 500,
                Height = 400,
            };
        }
        var settingsRepository = new RecordingSettingsRepository();
        var inventoryRepository = new RecordingInventoryRepository();
        var inventory = new InventoryService(new UnexpectedExportSource(), inventoryRepository, new());
        inventory.ObserveLoot(new("Tester", "log", 1, "Sword", 1, true, DateTime.UtcNow));
        inventory.ProcessPending(TimeSpan.FromSeconds(1));
        var state = new MainWindow.StartupState
        {
            Settings = settings,
            SettingsPersistence = new SettingsService(settingsRepository),
            Themes = [theme],
            Session = new(new(new(), new(), new(), new(), new(), [])),
            Inventory = inventory,
            QuestCache = [],
        };
        var pending = new TaskCompletionSource<MainWindow.StartupState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var available = new TaskCompletionSource<MainWindow.StartupState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new MainWindow(state, pending.Task, available.Task);
        var dock = Assert.IsType<DockWindow>(System.Windows.Application.Current.MainWindow);
        dock.UpdateLayout();
        Assert.True(dock.IsVisible);
        Assert.False(controller.StartupCompletion.IsCompleted);
        Assert.Equal(Visibility.Visible, controller.HistoryProgress.Visibility);
        Assert.Contains(Visuals(dock).OfType<ContentControl>(), control => control.Name == "HistoryLoadingNotice" && control.IsVisible);
        var panel = Visuals(dock).OfType<OverlayPanel>().Single(panel => panel.ViewModel.Key == "inventory");
        Assert.IsType<InventoryOverlayViewModel>(panel.ViewModel);
        Assert.NotNull(Visuals(panel).OfType<DataGrid>().Single(grid => grid.Name == "QuestOwnedGrid"));
        Assert.True(panel.BodyHost.IsEnabled);
        Assert.True(panel.Toolbar.IsEnabled);
        Assert.True(panel.Header.IsEnabled);
        Assert.True(controller.OverlayAppearance.IsEnabled);
        Assert.False(controller.BrowseLogButton.IsEnabled);
        VerifyDisplayScale(controller, dock, panel, settings, settingsRepository);
        VerifySettingsPanelBindings(controller, settings, panel);

        controller.DisplayScaleSlider.Value = 2;
        dock.UpdateLayout();
        panel.ViewModel.ToggleDockCommand.Execute(null);
        PumpUntil(() => !dock.HasPanel("inventory"), () => "Inventory remained docked after Pop out.");
        var floating = System.Windows.Application.Current.Windows.OfType<OverlayWindow>()
            .Single(window => ReferenceEquals(window.Panel, panel));
        Assert.True(floating.IsVisible);
        Assert.Same(panel, floating.Content);
        Assert.False(panel.ViewModel.IsDocked);
        var originalPosition = new Point(floating.Left, floating.Top);
        foreach (double scale in new[] { 0.5, 2 })
        {
            controller.DisplayScaleSlider.Value = scale;
            floating.UpdateLayout();
            Assert.Equal(scale, Assert.IsType<ScaleTransform>(panel.Frame.LayoutTransform).ScaleX);
            panel.DragHandle.RaiseEvent(new DragDeltaEventArgs(10, 12));
            Assert.InRange(floating.Left - originalPosition.X, 10 * scale - 1, 10 * scale + 1);
            Assert.InRange(floating.Top - originalPosition.Y, 12 * scale - 1, 12 * scale + 1);
            floating.Left = originalPosition.X;
            floating.Top = originalPosition.Y;
        }
        panel.ViewModel.ToggleDockCommand.Execute(null);
        PumpUntil(() => dock.HasPanel("inventory"), () => "Inventory did not dock again.");
        Assert.False(floating.IsVisible);
        Assert.Equal(2, Assert.IsType<ScaleTransform>(panel.Frame.LayoutTransform).ScaleX);
        controller.ResetDisplayScaleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var recipe = new TradeskillRecipe("restored", "Restored recipe", "Restored potion", "Restored potion",
            "Alchemy", "25", "Medicine bag", 1, []);
        var restoredInventory = new InventoryService(new UnexpectedExportSource(), inventoryRepository,
            new QuestInventory
            {
                Items = [new QuestInventoryItem { Character = "", Item = "Restored sword", Owned = 2 }],
            });
        available.SetResult(new MainWindow.StartupState
        {
            Settings = settings,
            SettingsPersistence = state.SettingsPersistence,
            Tracking = TrackingComposition.CreateService(),
            Themes = [theme],
            Session = new(new(new(), new(), new(), null, new(), [])),
            Inventory = restoredInventory,
            QuestCache = [],
            Tradeskills = [new(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow, [recipe], [])],
        });
        PumpUntil(() => Visuals(dock).OfType<OverlayPanel>()
            .Any(panel => panel.ViewModel is TradeskillsOverlayViewModel { Rows.Count: > 0, RecipeChoices.Count: > 0 }),
            () => controller.StatusText.Text + "; " + string.Join("; ", Visuals(dock).OfType<OverlayPanel>()
                .Select(panel => $"{panel.Key}: showing {panel.ViewModel.IsShowing}, rows {panel.ViewModel.Rows.Count}" +
                    (panel.ViewModel is TradeskillsOverlayViewModel tradeskills ? $", recipes {tradeskills.RecipeChoices.Count}" : ""))));
        Assert.False(pending.Task.IsCompleted);
        Assert.False(controller.StartupCompletion.IsCompleted);
        Assert.Equal("Restored sword", Assert.Single(Assert.IsType<InventoryOverlayViewModel>(panel.ViewModel).Inventory.Items).Item);
        var npc = Visuals(dock).OfType<OverlayPanel>().Single(panel => panel.Key == "npcs");
        Assert.False(Assert.IsType<NpcBrowserOverlayViewModel>(npc.ViewModel).RefreshWikiCommand.CanExecute(null));
        foreach (var list in Visuals(dock).OfType<VirtualRowList>())
        {
            Assert.True(list.IsEnabled);
            int width = (int)list.ActualWidth;
            int height = (int)list.ActualHeight;
            Assert.True(width > 2 && height > 2);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(list);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(1, height - 2, 1, 1), pixel, 4, 0);
            Assert.Equal(0, pixel[3]);
        }

        var frame = new DispatcherFrame();
        dock.Closed += (_, _) => frame.Continue = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        controller.DisplayScaleSlider.Value = 1.4;
        dock.Close();
        if (dock.IsVisible)
        {
            Dispatcher.PushFrame(frame);
        }
        timeout.Stop();
        Assert.False(dock.IsVisible);
        Assert.True(settingsRepository.Saves > 0);
        Assert.Equal(1.4, settingsRepository.LastDisplayScale);
        Assert.Equal(0, inventoryRepository.Saves);
        Assert.False(pending.Task.IsCompleted);
    }

    private static void VerifyDisplayScale(MainWindow controller, DockWindow dock, OverlayPanel panel,
        AppSettings settings, RecordingSettingsRepository repository)
    {
        var applicationStyles = new ResourceDictionary
        {
            Source = new Uri("/EqLogOverlay;component/AppResources.xaml", UriKind.Relative),
        };
        foreach (var key in applicationStyles.Keys.OfType<Type>())
        {
            controller.SettingsRoot.Resources[key] = applicationStyles[key];
        }
        dock.UpdateLayout();
        Assert.Equal(1.25, controller.DisplayScaleSlider.Value);
        Assert.Equal(1.25, Assert.IsType<ScaleTransform>(controller.SettingsRoot.LayoutTransform).ScaleX);
        Assert.Equal(1.25, Assert.IsType<ScaleTransform>(dock.MainToolbar.LayoutTransform).ScaleX);
        Assert.Equal(1.25, Assert.IsType<ScaleTransform>(dock.WorkspaceToolbar.LayoutTransform).ScaleX);
        Assert.Equal(1.25, Assert.IsType<ScaleTransform>(panel.Frame.LayoutTransform).ScaleX);

        panel.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/EqLogOverlay;component/Styles/WikiLabel.xaml", UriKind.Relative),
        });
        var icon = new WikiIconViewModel(new WikiIconKey(WikiIconKind.Item, "test"));
        icon.SetImage(new DrawingImage(new GeometryDrawing(Brushes.Gold, null,
            new RectangleGeometry(new Rect(0, 0, 10, 10)))));
        panel.TitleLabel.Icon = icon;
        dock.UpdateLayout();
        var iconFrame = Assert.IsType<Border>(panel.TitleLabel.Template.FindName("PART_Icon", panel.TitleLabel));
        var image = Assert.IsType<Image>(panel.TitleLabel.Template.FindName("PART_Image", panel.TitleLabel));
        Assert.Equal(Visibility.Visible, iconFrame.Visibility);
        Assert.NotNull(image.Source);
        double initialImageHeight = BoundsIn(image, panel).Height;
        double initialTextHeight = BoundsIn(panel.TitleText, panel).Height;
        double fontSize = panel.ViewModel.FontSize;

        double sideWidth = settings.DockWindow.SidePanelWidth;
        settings.DockWindow.SidePanelWidth = 380;
        dock.SetSidePanelOpen(true);
        controller.DisplayScaleSlider.BringIntoView();
        dock.UpdateLayout();
        bool autoArrange = settings.DockWindow.AutoArrange;
        settings.DockWindow.AutoArrange = true;
        var placement = settings.DockWindow.ActiveWorkspace.Panels["inventory"].Clone();
        controller.DisplayScaleSlider.Value = 2;
        dock.UpdateLayout();

        Assert.Equal(2, settings.DisplayScale);
        Assert.Equal(fontSize, panel.ViewModel.FontSize);
        Assert.InRange(BoundsIn(image, panel).Height / initialImageHeight, 1.59, 1.61);
        Assert.InRange(BoundsIn(panel.TitleText, panel).Height / initialTextHeight, 1.59, 1.61);
        var current = settings.DockWindow.ActiveWorkspace.Panels["inventory"];
        Assert.Equal(placement.DockLeft, current.DockLeft);
        Assert.Equal(placement.DockTop, current.DockTop);
        Assert.Equal(placement.DockWidth, current.DockWidth);
        Assert.Equal(placement.DockHeight, current.DockHeight);

        settings.DockWindow.AutoArrange = false;
        var tile = FindAncestor<DockTile>(panel);
        foreach (double scale in new[] { 0.5, 2 })
        {
            controller.DisplayScaleSlider.Value = scale;
            dock.UpdateLayout();
            Assert.Equal(scale, Assert.IsType<ScaleTransform>(tile.ResizeGrip.LayoutTransform).ScaleX);
            double tileWidth = tile.Width;
            double tileHeight = tile.Height;
            tile.ResizeGrip.RaiseEvent(new DragDeltaEventArgs(10, 12));
            Assert.InRange(tile.Width - tileWidth, 10 * scale - 1, 10 * scale + 1);
            Assert.InRange(tile.Height - tileHeight, 12 * scale - 1, 12 * scale + 1);
            tile.ResizeGrip.RaiseEvent(new DragDeltaEventArgs(-10, -12));
        }
        settings.DockWindow.AutoArrange = autoArrange;

        var sidebar = BoundsIn(dock.SidePanel, dock);
        var slider = BoundsIn(controller.DisplayScaleSlider, dock);
        var reset = BoundsIn(controller.ResetDisplayScaleButton, dock);
        Assert.True(slider.Width >= 30 && slider.Right <= sidebar.Right && reset.Right <= sidebar.Right &&
            slider.Top >= sidebar.Top && slider.Bottom <= sidebar.Bottom && reset.Bottom <= sidebar.Bottom,
            $"Display size controls exceeded the sidebar: sidebar {sidebar}, slider {slider}, reset {reset}.");
        var settingsScroll = FindAncestor<ScrollViewer>(controller.DisplayScaleSlider);
        foreach (var control in new FrameworkElement[] { controller.DisplayScaleSlider, controller.ResetDisplayScaleButton })
        {
            control.BringIntoView();
            dock.UpdateLayout();
            var bounds = BoundsIn(control, settingsScroll);
            Assert.True(bounds.Left >= -1 && bounds.Right <= settingsScroll.ViewportWidth + 1 &&
                bounds.Top >= -1 && bounds.Bottom <= settingsScroll.ViewportHeight + 1,
                $"{control.Name} is outside the settings viewport: {bounds}, viewport {settingsScroll.ViewportWidth}x{settingsScroll.ViewportHeight}.");
        }
        AssertReachable(controller.BrowseLogButton, controller.LogFileScroll, dock);
        AssertReachable(controller.ChooseFolderButton, controller.LogFileScroll, dock);
        var workspaceChoice = Visuals(controller.OverlayToggles).OfType<ComboBox>().First();
        AssertReachable(workspaceChoice, controller.OverlayTogglesScroll, dock);
        var appearanceSection = Assert.IsType<Expander>(controller.OverlayAppearanceScroll.Parent);
        appearanceSection.IsExpanded = true;
        dock.UpdateLayout();
        var overlayAppearance = Visuals(controller.OverlayAppearance).OfType<Expander>().First();
        overlayAppearance.IsExpanded = true;
        dock.UpdateLayout();
        var fontSlider = Visuals(overlayAppearance).OfType<Slider>().First();
        AssertReachable(fontSlider, controller.OverlayAppearanceScroll, dock);
        appearanceSection.IsExpanded = false;
        AssertReachable(controller.MobTimeoutBox, controller.MobOptionsScroll, dock);
        PumpUntil(() => repository.LastDisplayScale == 2, () => "Display size was not saved.");

        controller.Tabs.ApplyTemplate();
        var tabsScroll = Assert.IsType<ScrollViewer>(controller.Tabs.Template.FindName("SettingsTabScroll", controller.Tabs));
        foreach (var (tab, content) in new (TabItem Tab, FrameworkElement Content)[]
        {
            (controller.TriggersTab, controller.TriggersGrid),
            (controller.LogLinesTab, controller.RawLinesList),
            (controller.SpawnsTab, controller.SpawnsGrid),
            (controller.DurationsTab, controller.DurationsGrid),
            (controller.AppearanceTab, controller.ThemeEditorHost),
        })
        {
            tab.IsSelected = true;
            tab.BringIntoView();
            dock.UpdateLayout();
            var tabBounds = BoundsIn(tab, tabsScroll);
            Assert.True(tabBounds.Left >= -1 && tabBounds.Right <= tabsScroll.ViewportWidth + 1,
                $"{tab.Header} tab is outside its scrolling header: {tabBounds}, width {tabsScroll.ViewportWidth}.");
            var bounds = BoundsIn(content, dock);
            string children = tab.Content is DockPanel layout
                ? string.Join("; ", layout.Children.OfType<FrameworkElement>().Select(child => $"{child.GetType().Name}: {BoundsIn(child, dock)}"))
                : "";
            Assert.True(bounds.Height >= 40,
                $"{tab.Header} content has no usable height at 200%: {bounds}; settings {BoundsIn(controller.SettingsRoot, dock)}; {children}");
            if (ReferenceEquals(tab, controller.TriggersTab))
            {
                foreach (var help in new[] { controller.TriggerHelpScroll, controller.TriggerNotesScroll })
                {
                    var message = Visuals(help).OfType<TextBlock>().First();
                    var messageBounds = BoundsIn(message, help);
                    double lineHeight = message.FontFamily.LineSpacing * message.FontSize;
                    Assert.True(messageBounds.Top + lineHeight <= help.ViewportHeight + 1,
                        $"Alert help does not show a complete line: {messageBounds}; viewport {help.ViewportHeight}.");
                }
                var row = Assert.IsType<DataGridRow>(controller.TriggersGrid.ItemContainerGenerator.ContainerFromIndex(0));
                var name = Visuals(row).OfType<TextBox>().Single(box => Equals(box.Tag, "Name"));
                var text = Visuals(row).OfType<TextBox>().Single(box => Equals(box.Tag, "AlertText"));
                Assert.True(name.ActualWidth >= 40 && text.ActualWidth >= 40,
                    $"Alert fields are too narrow at 200%: name {name.ActualWidth}, text {text.ActualWidth}.");
                controller.DeleteTriggersButton.BringIntoView();
                dock.UpdateLayout();
                var delete = BoundsIn(controller.DeleteTriggersButton, controller.TriggerActionsScroll);
                Assert.True(delete.Width > 10 && delete.Left >= -1 && delete.Right <= controller.TriggerActionsScroll.ViewportWidth + 1,
                    $"Delete triggers is outside its scrolling toolbar: {delete}; width {controller.TriggerActionsScroll.ViewportWidth}.");
            }
            else if (ReferenceEquals(tab, controller.LogLinesTab))
            {
                AssertReachable(controller.RawFilterBox, controller.LogActionsScroll, dock);
                var copy = Visuals(controller.LogActionsScroll).OfType<Button>().Single(button => Equals(button.Content, "Copy message"));
                AssertReachable(copy, controller.LogActionsScroll, dock);
            }
            else if (ReferenceEquals(tab, controller.DurationsTab))
            {
                AssertReachable(controller.NewDurationSpellBox, controller.DurationEntryScroll, dock);
                AssertReachable(controller.NewDurationSecondsBox, controller.DurationEntryScroll, dock);
            }
            else if (ReferenceEquals(tab, controller.AppearanceTab))
            {
                var checkMark = Assert.IsType<Border>(
                    controller.FollowWindowsThemeBox.Template.FindName("Box", controller.FollowWindowsThemeBox));
                AssertReachable(checkMark, controller.ThemeSettingsScroll, dock);
                AssertReachable(controller.ThemePicker, controller.ThemeSettingsScroll, dock);
                var editor = Assert.IsType<ThemeEditor>(controller.ThemeEditorHost.Content);
                dock.UpdateLayout();
                var input = Visuals(editor).OfType<TextBox>().Single(box => Equals(box.Tag, "NightText"));
                AssertReachable(input, editor.ThemeEditorScroll, dock);
            }
        }
        controller.Tabs.SelectedIndex = 0;
        dock.UpdateLayout();

        repository.FailSaves = true;
        controller.DisplayScaleSlider.Value = 1.5;
        PumpUntil(() => controller.DisplayScaleStatus.Text.Contains("Could not save display size"),
            () => "The display-size save failure was not shown.");
        repository.FailSaves = false;
        int saves = repository.Saves;
        controller.DisplayScaleSlider.Value = 2;
        PumpUntil(() => repository.Saves > saves && repository.LastDisplayScale == 2,
            () => "Display size did not save after retrying.");
        Assert.Equal("", controller.DisplayScaleStatus.Text);

        double width = dock.Width;
        foreach (double narrowWidth in new[] { 750d, 420d })
        {
            dock.Width = narrowWidth;
            dock.UpdateLayout();
            foreach (var control in new FrameworkElement[] { dock.SideToggle, dock.AutoArrangeBox, dock.WhatsNewButton })
            {
                control.BringIntoView();
                dock.UpdateLayout();
                var bounds = BoundsIn(control, dock.MainToolbarScroll);
                Assert.True(bounds.Width > 10 && bounds.Left >= -1 && bounds.Right <= dock.MainToolbarScroll.ViewportWidth + 1,
                    $"{control.Name} is not reachable at 200% in a narrow window: {bounds}, viewport {dock.MainToolbarScroll.ViewportWidth}.");
            }
        }
        dock.Width = width;
        dock.UpdateLayout();

        controller.DisplayScaleSlider.Value = 0.5;
        dock.UpdateLayout();
        Assert.InRange(BoundsIn(image, panel).Height / initialImageHeight, 0.39, 0.41);
        controller.ResetDisplayScaleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, settings.DisplayScale);
        Assert.Equal(1, Assert.IsType<ScaleTransform>(panel.Frame.LayoutTransform).ScaleX);

        settings.DockWindow.SidePanelWidth = sideWidth;
        dock.SetSidePanelOpen(true);
        dock.UpdateLayout();
    }

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor)
    {
        return element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    }

    private static void AssertReachable(FrameworkElement element, ScrollViewer viewer, DockWindow dock)
    {
        element.BringIntoView();
        dock.UpdateLayout();
        var bounds = BoundsIn(element, viewer);
        if (bounds.Right > viewer.ViewportWidth)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + bounds.Right - viewer.ViewportWidth);
            dock.UpdateLayout();
            bounds = BoundsIn(element, viewer);
        }
        double visibleWidth = Math.Min(bounds.Right, viewer.ViewportWidth) - Math.Max(bounds.Left, 0);
        Assert.True(bounds.Width > 10 && visibleWidth >= Math.Min(bounds.Width, viewer.ViewportWidth) - 1 &&
            bounds.Right <= viewer.ViewportWidth + 1 &&
            bounds.Top >= -1 && bounds.Bottom <= viewer.ViewportHeight + 1,
            $"{element.Name} is outside its scrolling viewport: {bounds}; viewport {viewer.ViewportWidth}x{viewer.ViewportHeight}.");
    }

    private static T FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T match)
            {
                return match;
            }
        }
        throw new InvalidOperationException($"No {typeof(T).Name} ancestor was found.");
    }

    private static void VerifySettingsPanelBindings(MainWindow controller, AppSettings settings, OverlayPanel inventory)
    {
        var toggles = Visuals(controller.OverlayToggles).OfType<CheckBox>().ToArray();
        var visible = toggles.Single(box => Equals(box.Tag, "inventory"));
        Assert.True(visible.IsChecked);
        ToggleLikeUser(visible);
        Assert.False(settings.Overlays["inventory"].Visible);
        ToggleLikeUser(visible);
        Assert.True(settings.Overlays["inventory"].Visible);
        var row = (FrameworkElement)VisualTreeHelper.GetParent(visible);
        Assert.True(Visuals(row).OfType<CheckBox>().Single(box => Equals(box.Content, "docked")).IsChecked);
        var workspace = Visuals(row).OfType<ComboBox>().Single();
        Assert.Equal(settings.DockWindow.Workspaces.Single(item => !item.Deleted).Name,
            Assert.IsType<DockWorkspaceChoice>(workspace.SelectedItem).Name);

        var channel = Visuals(controller.ChatToggles).OfType<CheckBox>().First();
        var name = Enum.Parse<EqLog.Core.Parsing.ChatChannel>((string)channel.Content);
        bool wasEnabled = settings.ChatChannels.Contains(name);
        Assert.Equal(wasEnabled, channel.IsChecked == true);
        ToggleLikeUser(channel);
        Assert.Equal(!wasEnabled, settings.ChatChannels.Contains(name));
        ToggleLikeUser(channel);
        Assert.Equal(wasEnabled, settings.ChatChannels.Contains(name));

        var outer = (Expander)controller.OverlayAppearanceScroll.Parent;
        outer.IsExpanded = true;
        controller.SettingsRoot.UpdateLayout();
        var appearance = Visuals(controller.OverlayAppearance).OfType<Expander>()
            .Single(expander => Equals(expander.Header, inventory.ViewModel.Caption));
        appearance.IsExpanded = true;
        controller.SettingsRoot.UpdateLayout();
        var sliders = Visuals(appearance).OfType<Slider>().ToArray();
        Assert.Equal(3, sliders.Length);
        sliders[0].Value = 16;
        Assert.Equal(16, settings.Overlays["inventory"].FontSize);
        Assert.Equal(16, inventory.ViewModel.FontSize);
        inventory.ViewModel.BackgroundOpacity = 0.5;
        controller.SettingsRoot.UpdateLayout();
        Assert.Equal(0.5, sliders[1].Value);
        Assert.Equal(0.5, settings.Overlays["inventory"].BackgroundOpacity);
        Assert.Contains(Visuals(appearance).OfType<TextBlock>(),
            text => text.Text == 0.5.ToString("P0", System.Globalization.CultureInfo.CurrentCulture));
        Assert.Contains(Visuals(appearance).OfType<TextBlock>(), text => text.Text == "16 pt");
        var locked = Visuals(appearance).OfType<CheckBox>().Single();
        Assert.Equal(settings.Overlays["inventory"].Locked == true, locked.IsChecked == true);
        ToggleLikeUser(locked);
        Assert.Equal(locked.IsChecked == true, settings.Overlays["inventory"].Locked);
        ToggleLikeUser(locked);
        appearance.IsExpanded = false;
        outer.IsExpanded = false;
    }

    private static void ToggleLikeUser(CheckBox box)
    {
        var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(box);
        ((System.Windows.Automation.Provider.IToggleProvider)peer).Toggle();
    }

    private static void PumpUntil(Func<bool> completed, Func<string> diagnostic)
    {
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (completed() || elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(completed(), "Cached data did not appear while resistance history was still pending. " + diagnostic());
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private sealed class RecordingSettingsRepository : ISettingsRepository
    {
        public int Saves { get; private set; }

        public double LastDisplayScale
        {
            get; private set;
        }

        public bool FailSaves
        {
            get; set;
        }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The shell already has its settings.");
        }

        public Task SaveAsync(AppSettings snapshot, CancellationToken cancellationToken = default)
        {
            if (FailSaves)
            {
                throw new IOException("Test settings write failure.");
            }
            Saves++;
            LastDisplayScale = snapshot.DisplayScale;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingInventoryRepository : IQuestInventoryRepository
    {
        public int Saves { get; private set; }

        public Task<QuestInventory> LoadAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("History loading must remain pending in this test.");
        }

        public Task SaveAsync(QuestInventory snapshot, CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class UnexpectedExportSource : IInventoryExportSource
    {
        public Task<InventoryFileStamp?> InspectAsync(string logPath, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Log reading must not start before history is restored.");
        }

        public Task<IReadOnlyList<InventoryExportItem>?> ReadAsync(InventoryFileStamp expected, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Log reading must not start before history is restored.");
        }
    }
}
