using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Application.Settings;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using EqLog.Overlay.Views;
using System.Windows.Media.Imaging;
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
        var settings = new AppSettings { Theme = theme, OnlyWhenGameVisible = false };
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
        VerifySettingsPanelBindings(controller, settings, panel);

        panel.ViewModel.ToggleDockCommand.Execute(null);
        PumpUntil(() => !dock.HasPanel("inventory"), () => "Inventory remained docked after Pop out.");
        var floating = System.Windows.Application.Current.Windows.OfType<OverlayWindow>()
            .Single(window => ReferenceEquals(window.Panel, panel));
        Assert.True(floating.IsVisible);
        Assert.Same(panel, floating.Content);
        Assert.False(panel.ViewModel.IsDocked);
        panel.ViewModel.ToggleDockCommand.Execute(null);
        PumpUntil(() => dock.HasPanel("inventory"), () => "Inventory did not dock again.");
        Assert.False(floating.IsVisible);

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
        dock.Close();
        if (dock.IsVisible)
        {
            Dispatcher.PushFrame(frame);
        }
        timeout.Stop();
        Assert.False(dock.IsVisible);
        Assert.True(settingsRepository.Saves > 0);
        Assert.Equal(0, inventoryRepository.Saves);
        Assert.False(pending.Task.IsCompleted);
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

        var outer = (Expander)controller.OverlayAppearance.Parent;
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

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The shell already has its settings.");
        }

        public Task SaveAsync(AppSettings snapshot, CancellationToken cancellationToken = default)
        {
            Saves++;
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
