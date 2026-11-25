using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.ViewModels;
using EqLog.Overlay.Views;
using Xunit;

namespace EqLog.Overlay.Tests;

[CollectionDefinition("WPF application", DisableParallelization = true)]
public sealed class WpfApplicationCollection
{
}

[Collection("WPF application")]
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
                bool startupDispatched = false;
                var application = new System.Windows.Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = new ResourceDictionary
                    {
                        Source = new Uri("/EqLogOverlay;component/AppResources.xaml", UriKind.Relative),
                    },
                };
                application.Startup += (_, _) => startupDispatched = true;
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () =>
                {
                });
                Assert.True(startupDispatched);
                Assert.Empty(application.Windows);
                var comboItemStyle = Assert.IsType<Style>(application.Resources[typeof(ComboBoxItem)]);
                var buttonStyle = Assert.IsType<Style>(application.Resources[typeof(Button)]);
                var menuItemStyle = Assert.IsType<Style>(application.Resources[typeof(MenuItem)]);
                var contextMenuStyle = Assert.IsType<Style>(application.Resources[typeof(ContextMenu)]);
                application.Resources = new ResourceDictionary();
                foreach (string key in new[] { "NightBackground", "NightBorder", "Pumpkin", "NightRaised",
                    "NightSurface", "NightText", "NightMuted", "Hover", "Selection" })
                {
                    application.Resources[key] = Brushes.Gray;
                }
                application.Resources[typeof(ComboBoxItem)] = comboItemStyle;
                try
                {
                    VerifyComboBoxItemBindings();
                }
                finally
                {
                    application.Resources.Remove(typeof(ComboBoxItem));
                }
                application.Resources[typeof(MenuItem)] = menuItemStyle;
                application.Resources[typeof(ContextMenu)] = contextMenuStyle;
                try
                {
                    VerifyMenuItemBindings();
                }
                finally
                {
                    application.Resources.Remove(typeof(MenuItem));
                    application.Resources.Remove(typeof(ContextMenu));
                }
                application.Resources[typeof(Button)] = buttonStyle;
                try
                {
                    VerifyHeaderButtonTheme(buttonStyle);
                }
                finally
                {
                    application.Resources.Remove(typeof(Button));
                }
                AsyncTextFilterTests.VerifyQuestVirtualization();
                VerifyOverlayAutomationHidesContent();
                VerifyQuestAndSkyTemplates();
                TradeskillOverlayTests.VerifyMerchantListScrolling();
                TradeskillOverlayTests.VerifyCompactToolbarAndRecipeLoading();
                LevelingOverlayTests.VerifyGridAndDatePickerInteraction();
                VerifyMobInfoTitleAlwaysVisible();
                VerifyMobAndNpcOverlayTemplates();
                VerifyBreakdownButtonsUsePressClickMode();
                VerifyDpsRecentFightsScrolling();
                VerifyFloatingPlacement();
                VerifyDockWindowBindings();
                VerifyCompactCards();
                VerifyCardTableBindings();
                VerifyTableLayout();
                VerifyFloatingChrome();
                VerifyMoteColumns();
                VerifyAllMoteTiersVisible();
                VerifyHoverBackgroundOpacity();
                VerifyItemFilterToolbars();
                VerifyKnownSpellsVisibilityControl();
                VerifyInventoryMergeMoteTemplates();
                VerifyWikiLabelLooksUpIcons();
                VerifyViewModelReplacement();
                VerifyOptionalTemplateLookups();
                VerifyStandaloneRowSelectors();
                MotePlanRestoredTests.PlannerScrollBody_LongCopyEditorAndResultsAreReachableInEveryTab();
                MotePlanRestoredTests.PlannerTabs_SwitchTasksWithoutMixingPagesOrRefreshingForNestedControls();
                StartupTests.WorkspaceOpensBeforeDataAndClosingDoesNotSavePlaceholderHistory();
                // Later test classes build WPF trees on other threads; leave no resources owned by this one.
                application.Resources = new ResourceDictionary();
                ReleaseProcessWideApplication();
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

    private static void ReleaseProcessWideApplication()
    {
        // Closing the dock in StartupTests requests app shutdown, which sets WPF's static shutting-down flag for the
        // rest of the process and makes Window.Show a no-op on every thread. Completing the shutdown is worse: it
        // unregisters the pack://application package, so later InitializeComponent calls fail fast. Leave the idle
        // Application in place and clear only the flag so later test classes can show windows.
        typeof(System.Windows.Application)
            .GetProperty("IsShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, false);
    }

    private static void VerifyStandaloneRowSelectors()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.ResourceDictionarySource;
        var previousLevel = source.Switch.Level;
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Warning;
        try
        {
            foreach (var (file, key) in new[]
            {
                ("TradeskillsOverlay", "TradeskillsOverlayViewModel.Body"),
                ("ExaltationsOverlay", "ExaltationsOverlayViewModel.Body"),
                ("DpsOverlay", "DpsFight"),
            })
            {
                var resources = new ResourceDictionary
                {
                    Source = new Uri($"/EqLogOverlay;component/Views/Overlays/{file}.xaml", UriKind.Relative),
                };
                var template = Assert.IsType<DataTemplate>(resources[key]);
                var content = Assert.IsAssignableFrom<FrameworkElement>(template.LoadContent());
                var window = new Window { Content = content, Width = 300, Height = 200, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var rows = file == "DpsOverlay"
                        ? Named<ItemsControl>(content, "AttackerRows")
                        : Assert.IsType<VirtualRowList>(content);
                    var selector = Assert.IsType<OverlayRowTemplateSelector>(rows.ItemTemplateSelector);
                    var owner = new OverlayViewModel("test", "Test");
                    owner.Show("Test", null, [new OverlayRow("Standalone row")]);
                    var row = Assert.Single(owner.Rows);
                    Assert.NotNull(selector.SelectTemplate(row, rows));
                    rows.ItemsSource = owner.Rows;
                    window.UpdateLayout();
                }
                finally
                {
                    window.Close();
                }
            }
            listener.Flush();
            Assert.True(
                !output.ToString().Contains("OverlayRowSelector", StringComparison.Ordinal),
                output.ToString());
        }
        finally
        {
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
        }
    }

    private static void VerifyOptionalTemplateLookups()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.ResourceDictionarySource;
        var previousLevel = source.Switch.Level;
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Warning;
        var root = new Grid();
        var window = new Window { Content = root, Width = 200, Height = 100, ShowInTaskbar = false };
        try
        {
            var fallback = new DataTemplate();
            var virtualFallback = new DataTemplate();
            var baseTemplate = new DataTemplate();
            var derivedTemplate = new DataTemplate();
            var container = new ContentControl();
            root.Children.Add(container);
            window.Show();
            window.UpdateLayout();
            output.GetStringBuilder().Clear();
            var merged = new ResourceDictionary
            {
                ["OverlayViewModel.Body"] = baseTemplate,
            };
            root.Resources.MergedDictionaries.Add(merged);
            var selector = new OverlayPartTemplateSelector
            {
                Part = "Body",
                Fallback = fallback,
                VirtualFallback = virtualFallback,
            };
            var normal = new SelectorTestViewModel("alerts");
            var virtualRows = new SelectorTestViewModel("quests");

            Assert.Same(baseTemplate, selector.SelectTemplate(normal, container));
            root.Resources["SelectorTestViewModel.Body"] = derivedTemplate;
            Assert.Same(derivedTemplate, selector.SelectTemplate(normal, container));
            root.Resources["SelectorTestViewModel.Body"] = "not a template";
            Assert.Same(baseTemplate, selector.SelectTemplate(normal, container));
            root.Resources.Remove("SelectorTestViewModel.Body");
            merged.Remove("OverlayViewModel.Body");
            Assert.Same(fallback, selector.SelectTemplate(normal, container));
            Assert.Same(virtualFallback, selector.SelectTemplate(virtualRows, container));
            System.Windows.Application.Current.Resources["SelectorTestViewModel.Body"] = derivedTemplate;
            Assert.Same(derivedTemplate, selector.SelectTemplate(normal, container));
            System.Windows.Application.Current.Resources.Remove("SelectorTestViewModel.Body");
            selector.Part = "Toolbar";
            Assert.Same(virtualFallback, selector.SelectTemplate(virtualRows, container));
            listener.Flush();
            Assert.DoesNotContain("Resource not found", output.ToString(), StringComparison.OrdinalIgnoreCase);

            container.TryFindResource("MissingRealResourceForTest");
            listener.Flush();
            Assert.Contains("MissingRealResourceForTest", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            System.Windows.Application.Current.Resources.Remove("SelectorTestViewModel.Body");
            window.Close();
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
        }
    }

    private sealed class SelectorTestViewModel(string key) : OverlayViewModel(key, "Test")
    {
    }

    private static void VerifyMenuItemBindings()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Error;
        var item = new MenuItem { Header = "Standalone" };
        var window = new Window { Content = item, Width = 200, Height = 100, ShowInTaskbar = false };
        var menu = new ContextMenu();
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
            {
            });

            window.Content = null;
            var parent = new MenuItem { Header = "Move to" };
            int executions = 0;
            item.Command = new EqLog.Overlay.Mvvm.RelayCommand(() =>
            {
                executions++;
            });
            parent.Items.Add(item);
            menu.Items.Add(parent);
            menu.PlacementTarget = window;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                menu.IsOpen = true;
                parent.IsSubmenuOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
                {
                });
                item.ApplyTemplate();
                Assert.NotNull(item.Template.FindName("Frame", item));
                var peer = new MenuItemAutomationPeer(item);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
                {
                });
                Assert.Equal(attempt + 1, executions);
                parent.IsSubmenuOpen = false;
                menu.IsOpen = false;
            }
            listener.Flush();
            Assert.True(
                !output.ToString().Contains("target element is 'MenuItem'", StringComparison.Ordinal),
                output.ToString());
        }
        finally
        {
            menu.IsOpen = false;
            window.Close();
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
        }
    }

    private static void VerifyComboBoxItemBindings()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Error;
        var item = new ComboBoxItem { Content = "Standalone" };
        var window = new Window { Content = item, Width = 200, Height = 100, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
            {
            });

            var resources = new ResourceDictionary
            {
                Source = new Uri("/EqLogOverlay;component/Views/Overlays/TradeskillsOverlay.xaml", UriKind.Relative),
            };
            var template = Assert.IsType<DataTemplate>(resources["TradeskillsOverlayViewModel.Toolbar"]);
            var toolbar = Assert.IsType<StackPanel>(template.LoadContent());
            var recipePicker = Assert.Single(toolbar.Children.OfType<ComboBox>(), combo => combo.Name == "TradeskillRecipes");
            var recipeItem = new ComboBoxItem
            {
                Style = recipePicker.ItemContainerStyle,
                DataContext = new { IsEnabled = false },
            };
            var recipeWindow = new Window
            {
                Content = recipeItem,
                Width = 200,
                Height = 100,
                ShowInTaskbar = false,
            };
            try
            {
                recipeWindow.Show();
                recipeWindow.UpdateLayout();
                Assert.False(recipeItem.IsEnabled);
            }
            finally
            {
                recipeWindow.Close();
            }

            var combo = new ComboBox();
            combo.Items.Add("First");
            var comboWindow = new Window { Content = combo, Width = 200, Height = 100, ShowInTaskbar = false };
            try
            {
                comboWindow.Show();
                combo.IsDropDownOpen = true;
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
                {
                });
                comboWindow.UpdateLayout();
                var generated = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Equal(combo.HorizontalContentAlignment, generated.HorizontalContentAlignment);
                Assert.Equal(combo.VerticalContentAlignment, generated.VerticalContentAlignment);
                generated.IsSelected = true;
                Assert.Equal(0, combo.SelectedIndex);
                combo.Items.Clear();
                combo.Items.Add("Second");
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
                {
                });
                comboWindow.UpdateLayout();
                Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
            }
            finally
            {
                comboWindow.Close();
            }
            listener.Flush();
            Assert.True(
                !output.ToString().Contains("target element is 'ComboBoxItem'", StringComparison.Ordinal),
                output.ToString());
        }
        finally
        {
            window.Close();
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
        }
    }

    private static void VerifyViewModelReplacement()
    {
        var previous = new OverlayViewModel("timers", "Spell timers");
        var panel = new OverlayPanel(previous);
        var restored = new OverlayViewModel("timers", "Spell timers") { FontSize = 16 };
        restored.Show("Restored timers", null, [new OverlayRow("Restored spell")]);

        panel.ReplaceViewModel(restored);
        Layout(panel, 350, 200);
        Assert.Same(restored, panel.DataContext);
        Assert.Equal("Restored spell", Named<TextBlock>(panel, "LeftText").Text);
        previous.FontSize = 10;
        Assert.Equal(16, panel.FontSize);
        panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        Assert.True(restored.IsBackgroundHovered);
        Assert.False(previous.IsBackgroundHovered);
        var slider = Assert.Single(panel.BackgroundControls.Children.OfType<Slider>());
        slider.Value = 0.6;
        Assert.Equal(0.6, restored.BackgroundOpacity);
        Assert.NotEqual(0.6, previous.BackgroundOpacity);
    }

    private static void VerifyQuestAndSkyTemplates()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var quests = new QuestsOverlayViewModel(
            OverlayPresenters.All.Single(definition => definition.Key == "quests"),
            NullOverlayActions.Instance,
            dispatcher,
            () => new QuestsOverlayState(EqLog.Application.Inventory.InventoryReadModel.Empty, "Que",
                new Dictionary<string, QuestLookupResult>(), 0, 0),
            "",
            true,
            _ => { },
            _ => { });
        var questsPanel = new OverlayPanel(quests);
        Layout(questsPanel, 500, 400);
        Assert.NotNull(Named<TextBox>(questsPanel, "QuestFilter"));
        Assert.NotNull(Named<ComboBox>(questsPanel, "QuestSort"));
        Assert.NotNull(Named<ProgressBar>(questsPanel, "QuestProgress"));
        Assert.NotNull(Named<ListBox>(questsPanel, "RowList"));
        quests.Dispose();

        var sky = new SkyOverlayViewModel(
            OverlayPresenters.All.Single(definition => definition.Key == "sky"),
            NullOverlayActions.Instance,
            dispatcher,
            () => new SkyOverlayState(EqLog.Application.Inventory.InventoryReadModel.Empty, "Que", null, false, "", 0),
            (_, _) => Task.CompletedTask,
            () => false,
            "",
            "Wizard",
            2,
            [],
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { });
        var skyPanel = new OverlayPanel(sky);
        Layout(skyPanel, 500, 400);
        Assert.NotNull(Named<TextBox>(skyPanel, "SkyFilter"));
        Assert.Equal("Wizard", Named<ComboBox>(skyPanel, "SkyClass").SelectedItem);
        Assert.Equal(3, Named<TabControl>(skyPanel, "SkyViews").SelectedIndex);
        Assert.NotNull(Named<Button>(skyPanel, "SkyRefresh").Command);
        Assert.NotNull(Named<ListBox>(skyPanel, "RowList"));
        sky.Dispose();
    }

    private static void VerifyWikiLabelLooksUpIcons()
    {
        var provider = new WikiIconProvider((_, _) => Task.FromResult<string?>(null), () => false);
        var host = new StackPanel();
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/EqLogOverlay;component/Styles/WikiLabel.xaml", UriKind.Relative),
        });
        WikiLabel.SetIconSource(host, provider);
        var label = new WikiLabel { Kind = WikiIconKind.Mob, Content = "Cazic Thule" };
        host.Children.Add(label);

        label.Subject = "Cazic Thule";
        Assert.Same(provider.Get(new WikiIconKey(WikiIconKind.Mob, "cazic thule")), label.Icon);
        Layout(host, 200, 40);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)label.Template.FindName("PART_Icon", label)).Visibility);

        label.Subject = null;
        Assert.Null(label.Icon);
    }

    private static void VerifyInventoryMergeMoteTemplates()
    {
        var inventory = new InventoryViewModel();
        var inventoryDefinition = Assert.Single(OverlayPresenters.All, definition => definition.Key == "inventory");
        var inventoryViewModel = new InventoryOverlayViewModel(inventoryDefinition, NullOverlayActions.Instance, inventory);
        var inventoryPanel = new OverlayPanel(inventoryViewModel);
        Layout(inventoryPanel, 650, 520);
        var ownedGrid = Named<DataGrid>(inventoryPanel, "QuestOwnedGrid");
        var itemColumn = Assert.IsType<DataGridTemplateColumn>(ownedGrid.Columns[0]);
        var inventoryCell = Assert.IsType<DockPanel>(itemColumn.CellTemplate.LoadContent());
        var inventoryItem = new QuestInventoryItem { Item = "Sword" };
        inventoryCell.DataContext = inventoryItem;
        Layout(inventoryCell, 250, 30);
        var inventoryLabel = Assert.IsType<WikiLabel>(inventoryCell.Children[1]);
        Assert.Equal("Sword", inventoryLabel.Subject);
        Assert.Null(inventoryLabel.Icon);
        var inventoryQuestLink = Assert.IsType<TextBlock>(inventoryCell.Children[0]);
        Assert.Equal(Visibility.Collapsed, inventoryQuestLink.Visibility);
        inventoryItem.HasQuestReferences = true;
        Layout(inventoryCell, 250, 30);
        Assert.Equal(Visibility.Visible, inventoryQuestLink.Visibility);
        TabControl inventoryTabs = Named<TabControl>(inventoryPanel, "InventoryDetailsTabs");
        Assert.Equal(2, inventoryTabs.Items.Count);
        inventoryTabs.SelectedIndex = 1;
        Layout(inventoryPanel, 650, 520);
        _ = Named<ListBox>(inventoryPanel, "QuestResults");
        inventoryTabs.SelectedIndex = 0;
        Layout(inventoryPanel, 650, 520);
        _ = Named<DataGrid>(inventoryPanel, "InventoryCopiesGrid");

        InventorySnapshot snapshot = CreateOverlayInventorySnapshot("Que");
        var mergeDefinition = Assert.Single(OverlayPresenters.All, definition => definition.Key == "merge");
        var mergeViewModel = new MergeOverlayViewModel(mergeDefinition, NullOverlayActions.Instance,
            () => snapshot, () => "Que");
        var mergePanel = new OverlayPanel(mergeViewModel);
        Layout(mergePanel, 450, 520);
        _ = Named<TextBox>(mergePanel, "MergeFilter");
        var mergeGroups = Named<ListBox>(mergePanel, "MergeGroups");
        var mergeGroup = Assert.IsType<StackPanel>(mergeGroups.ItemTemplate.LoadContent());
        mergeGroup.DataContext = new MergeGroupViewModel("Sword", 2, false, []);
        Layout(mergeGroup, 450, 100);
        var mergeQuestLink = Assert.IsType<TextBlock>(mergeGroup.Children[1]);
        Assert.Equal(Visibility.Collapsed, mergeQuestLink.Visibility);
        mergeGroup.DataContext = new MergeGroupViewModel("Sword", 2, true, []);
        Layout(mergeGroup, 450, 100);
        Assert.Equal(Visibility.Visible, mergeQuestLink.Visibility);

        var moteDefinition = Assert.Single(OverlayPresenters.All, definition => definition.Key == "moteplan");
        var moteViewModel = new MotePlanOverlayViewModel(moteDefinition, NullOverlayActions.Instance, () => snapshot);
        moteViewModel.Receive(CreateOverlayFrame("Que"));
        var motePanel = new OverlayPanel(moteViewModel);
        Layout(motePanel, 450, 600);
        var planTabs = Named<TabControl>(motePanel, "PlanTabs");
        Assert.Equal(4, planTabs.Items.Count);
        moteViewModel.Kind = MoteUpgradeKind.Spell;
        Layout(motePanel, 450, 600);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<TabItem>(planTabs.Items[1]).Visibility);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<TabItem>(planTabs.Items[2]).Visibility);
        moteViewModel.Kind = MoteUpgradeKind.Equipment;
        Layout(motePanel, 450, 600);
        Assert.Equal(Visibility.Visible, Assert.IsType<TabItem>(planTabs.Items[1]).Visibility);
        Assert.Equal(Visibility.Visible, Assert.IsType<TabItem>(planTabs.Items[2]).Visibility);
        Assert.Contains(moteViewModel.PlanBlocks, block => block.Kind == MotePlanBlockKind.Heading);
        moteViewModel.SelectedPageIndex = (int)MotePlanPage.Math;
        Layout(motePanel, 450, 600);
        Assert.Contains(Descendants(motePanel).OfType<OverlayTableView>(), table => table.Table is not null);
    }

    private static InventorySnapshot CreateOverlayInventorySnapshot(string character)
    {
        DateTime written = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var state = new QuestInventory();
        state.ApplyExport(character, [], written);
        return new InventorySnapshot(1, state,
            new InventoryExportSnapshot(character, new InventoryFileStamp("export", 0, written), []));
    }

    private static OverlayFrameMessage CreateOverlayFrame(string character)
    {
        var engine = new LogEngine { CharacterName = character };
        DateTime now = DateTime.UtcNow;
        var frame = new LogSessionFrame(engine.Snapshot(now), [], false, EngineQuerySnapshot.Capture(engine));
        return new OverlayFrameMessage(frame, now, new TailProgress(0, 0, 0, true));
    }

    private static void VerifyOverlayAutomationHidesContent()
    {
        var panel = new OverlayPanel("dps", "DPS", false);
        var window = new OverlayWindow(panel);
        try
        {
            window.AttachPanel();
            window.Show();
            window.UpdateLayout();
            foreach (var element in new UIElement[] { window, panel })
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                Assert.False(peer.IsControlElement());
                Assert.False(peer.IsContentElement());
                Assert.Null(peer.GetChildren());
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyHeaderButtonTheme(Style buttonStyle)
    {
        var panel = new OverlayPanel("dps", "DPS", false);
        var window = new OverlayWindow(panel);
        var template = Assert.IsType<ControlTemplate>(buttonStyle.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.TemplateProperty).Value);
        try
        {
            window.AttachPanel();
            window.Show();
            window.UpdateLayout();
            foreach (var button in new[] { panel.DockButton, panel.WorkspaceButton })
            {
                button.ApplyTemplate();
                Assert.Same(template, button.Template);
                Assert.Equal(new Thickness(5, 0, 5, 0), button.Padding);
                var border = Assert.IsType<Border>(template.FindName("Frame", button));
                Assert.Same(button.Background, border.Background);
                button.IsEnabled = false;
                Assert.Equal(0.45, border.Opacity);
                button.IsEnabled = true;
                Assert.Equal(1, border.Opacity);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifyFloatingChrome()
    {
        var panel = new OverlayPanel("timers", "Spell Timers", false);
        var vm = panel.ViewModel;
        var frame = Assert.IsType<Border>(panel.Content);
        VerifyRoundedFrame(panel, frame);
        var frameContent = panel.FrameContent;
        var header = panel.TitleFrame;
        var headerContent = panel.Header;
        var backgroundControls = panel.BackgroundControls;
        var slider = Assert.Single(backgroundControls.Children.OfType<Slider>());
        var dockButton = panel.DockButton;
        Assert.Contains("Dock", Assert.IsType<string>(dockButton.Content), StringComparison.Ordinal);
        Assert.Equal(headerContent.Children.IndexOf(dockButton) + 1, headerContent.Children.IndexOf(backgroundControls));
        Assert.Equal(Dock.Right, DockPanel.GetDock(backgroundControls));
        Assert.Equal(56, slider.Width);
        Assert.True(OverlayPanel.IsInteractive(slider));

        slider.Value = 0.3;
        Assert.Equal(0.3, vm.BackgroundOpacity);
        Assert.Equal(0.3, Assert.IsType<SolidColorBrush>(frame.Background).Opacity);
        Assert.Equal(0, panel.Inset.Background.Opacity);
        Assert.Equal(new Thickness(0), panel.Inset.BorderThickness);
        vm.BackgroundOpacity = 0.8;
        Assert.Equal(0.8, slider.Value);
        Assert.Equal("Background opacity: 80%", slider.ToolTip);
        Assert.Equal(Visibility.Visible, backgroundControls.Visibility);
        var thickness = frame.BorderThickness;
        var padding = frame.Padding;

        vm.IsChromeHidden = true;

        Assert.Equal(Visibility.Hidden, header.Visibility);
        Assert.Equal(Brushes.Transparent, frame.BorderBrush);
        Assert.Equal(thickness, frame.BorderThickness);
        Assert.Equal(padding, frame.Padding);

        vm.IsLocked = true;
        Assert.Equal(Visibility.Collapsed, backgroundControls.Visibility);
        Assert.Equal(Visibility.Hidden, header.Visibility);
        vm.IsChromeHidden = false;
        Assert.Equal(Visibility.Visible, header.Visibility);

        vm.IsLocked = false;
        Assert.NotEqual(Brushes.Transparent, frame.BorderBrush);
        vm.IsChromeHidden = true;
        vm.SetDocked(true);
        Assert.Equal(Visibility.Collapsed, backgroundControls.Visibility);
        Assert.Equal(Visibility.Visible, header.Visibility);
        Assert.Equal(Visibility.Visible, frameContent.Children[1].Visibility);
        Assert.NotEqual(Brushes.Transparent, frame.BorderBrush);
        Assert.Equal(new Thickness(2), frame.BorderThickness);
        Assert.Equal(new Thickness(1), panel.Inset.BorderThickness);
        Assert.Equal(new Thickness(0, 0, 0, 2), header.BorderThickness);
        Assert.Equal(vm.BackgroundOpacity, panel.Inset.Background.Opacity);
        Assert.Equal(vm.BackgroundOpacity, header.Background.Opacity);
        Assert.Equal(new Thickness(10, 5, 10, 5), new Button { Style = (Style)panel.FindResource(typeof(Button)) }.Padding);

        var window = new OverlayWindow(panel);
        try
        {
            window.AttachPanel();
            window.Show();
            VerifyDragGrip(window, panel, headerContent);
            var handle = new WindowInteropHelper(window).Handle;
            Assert.NotEqual(0, ExStyle(handle) & NativeMethods.WS_EX_NOACTIVATE);

            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
            });
            Assert.Equal(0, ExStyle(handle) & NativeMethods.WS_EX_NOACTIVATE);

            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent,
            });
            Assert.NotEqual(0, ExStyle(handle) & NativeMethods.WS_EX_NOACTIVATE);

            var spellFilter = new TextBox();
            panel.Toolbar.ContentTemplateSelector = null;
            panel.Toolbar.Content = spellFilter;
            window.UpdateLayout();
            spellFilter.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
            });
            Assert.Equal(0, ExStyle(handle) & NativeMethods.WS_EX_NOACTIVATE);
            window.SetLocked(true);
            window.SetLocked(false);
            Assert.NotEqual(0, ExStyle(handle) & NativeMethods.WS_EX_NOACTIVATE);

            window.SetLocked(true);
            Assert.True(vm.IsLocked);
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
            });
            long lockedStyle = ExStyle(handle);
            Assert.NotEqual(0, lockedStyle & NativeMethods.WS_EX_NOACTIVATE);
            Assert.NotEqual(0, lockedStyle & NativeMethods.WS_EX_TRANSPARENT);
        }
        finally
        {
            window.Close();
        }
    }

    private static long ExStyle(IntPtr handle)
    {
        return NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
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
        var settings = new AppSettings();
        int visibilityChanges = 0;
        int trackingChanges = 0;
        var vm = new SpellTimersOverlayViewModel(definition, NullOverlayActions.Instance, EmptyOverlayViewSource.Instance,
            settings.SpellTimersShowKnownSpells,
            visible =>
            {
                settings.SpellTimersShowKnownSpells = visible;
                visibilityChanges++;
            },
            _ =>
            {
                trackingChanges++;
            });
        var panel = new OverlayPanel(vm);

        void Render()
        {
            var view = new OverlayView(new HashSet<ChatChannel>(), [], ShowKnownSpells: vm.ShowKnownSpells);
            vm.Show(definition.Render(snapshot, queries, view));
        }

        var window = new OverlayWindow(panel) { Width = 300, Height = 200 };
        window.AttachPanel();
        window.Show();
        try
        {
            window.UpdateLayout();
            var showKnown = Named<CheckBox>(panel, "KnownSpellsBox");
            Assert.Same(panel.HeaderExtras, panel.Header.Children[0]);
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(panel.HeaderExtras));
            Assert.Equal(0, visibilityChanges);
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
                vm.SetDocked(docked);
                Render();
                window.UpdateLayout();
                var shownRows = RowsControl(panel);
                Assert.Equal(3, shownRows.Items.Count);
                Assert.True(showKnown.IsChecked);
                Assert.True(OverlayPanel.IsInteractive(showKnown));
                Assert.Equal(UsageAction.ToggleKnownSpells, UsageTracking.GetAction(showKnown));
                double rowsTop = shownRows.TranslatePoint(new Point(0, 0), panel).Y;
                vm.IsChromeHidden = true;
                window.UpdateLayout();
                Assert.Equal(docked ? Visibility.Visible : Visibility.Hidden, panel.TitleFrame.Visibility);
                Assert.Equal(docked, showKnown.IsVisible);
                Assert.True(shownRows.IsVisible);
                Assert.Equal(rowsTop, shownRows.TranslatePoint(new Point(0, 0), panel).Y, 3);
                Assert.Equal(3, shownRows.Items.Count);
                vm.IsChromeHidden = false;
                window.UpdateLayout();
                Assert.True(showKnown.IsVisible);

                var toggle = Assert.IsAssignableFrom<IToggleProvider>(
                    new CheckBoxAutomationPeer(showKnown).GetPattern(PatternInterface.Toggle));
                toggle.Toggle();
                Assert.False(settings.SpellTimersShowKnownSpells);
                Render();
                Assert.Single(shownRows.Items);
                Assert.False(showKnown.IsChecked);

                toggle.Toggle();
                Assert.True(settings.SpellTimersShowKnownSpells);
                Render();
                Assert.Equal(3, shownRows.Items.Count);
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

        int initialChanges = 0;
        var hiddenVm = new SpellTimersOverlayViewModel(definition, NullOverlayActions.Instance, EmptyOverlayViewSource.Instance,
            false,
            _ =>
            {
                initialChanges++;
            },
            _ =>
            {
            });
        var hiddenPanel = new OverlayPanel(hiddenVm);
        Layout(hiddenPanel, 300, 200);
        var initiallyHidden = Named<CheckBox>(hiddenPanel, "KnownSpellsBox");
        Assert.False(initiallyHidden.IsChecked);
        Assert.Equal(0, initialChanges);

        var hiddenFilter = Named<TextBox>(hiddenPanel, "KnownSpellsFilter");
        Assert.Equal("Known spells filter", System.Windows.Automation.AutomationProperties.GetName(hiddenFilter));
        Assert.True(OverlayPanel.IsInteractive(hiddenFilter));
        Assert.Equal(Visibility.Collapsed, hiddenFilter.Visibility);
        initiallyHidden.IsChecked = true;
        Assert.True(hiddenVm.ShowKnownSpells);
        Assert.Equal(1, initialChanges);
        Assert.Equal(Visibility.Visible, hiddenFilter.Visibility);
        hiddenFilter.Text = "Bite";
        WaitForFilter(() => hiddenVm.FilterText == "Bite");
        initiallyHidden.IsChecked = false;
        Assert.Equal(Visibility.Collapsed, hiddenFilter.Visibility);
    }

    private static void VerifyMobAndNpcOverlayTemplates()
    {
        var mobDefinition = OverlayPresenters.All.Single(definition => definition.Key == "mobinfo");
        var mobViewModel = new MobInfoOverlayViewModel(mobDefinition, NullOverlayActions.Instance,
            new MobInfoOverlayViewModelTests.FakeServices(), 5, "ready");
        var mobPanel = new OverlayPanel(mobViewModel);
        Layout(mobPanel, 540, 620);

        var refresh = Named<Button>(mobPanel, "RefreshMobWikiButton");
        var reset = Named<Button>(mobPanel, "ResetResistanceButton");
        Assert.True(OverlayPanel.IsInteractive(refresh));
        Assert.True(OverlayPanel.IsInteractive(reset));
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(mobPanel, "ResetConfirmation").Visibility);

        var npcDefinition = OverlayPresenters.All.Single(definition => definition.Key == "npcs");
        var npcViewModel = new NpcBrowserOverlayViewModel(npcDefinition, NullOverlayActions.Instance,
            new NpcBrowserOverlayViewModelTests.FakeServices(), "", null, null, 0, null);
        var npcPanel = new OverlayPanel(npcViewModel);
        Layout(npcPanel, 620, 720);

        var search = Named<TextBox>(npcPanel, "NpcSearch");
        var choices = Named<ComboBox>(npcPanel, "NpcChoices");
        var tabs = Named<TabControl>(npcPanel, "NpcViews");
        Assert.True(OverlayPanel.IsInteractive(search));
        Assert.Same(npcViewModel.Choices, choices.ItemsSource);
        Assert.Equal(3, tabs.Items.Count);
        search.Text = "sphinx";
        Assert.Equal("sphinx", npcViewModel.SearchText);

        var icons = new CountingIconSource();
        WikiLabel.SetIconSource(choices, icons);
        var appResources = new ResourceDictionary
        {
            Source = new Uri("/EqLogOverlay;component/AppResources.xaml", UriKind.Relative),
        };
        choices.Style = Assert.IsType<Style>(appResources[typeof(ComboBox)]);
        npcViewModel.Choices.ReplaceAll(Enumerable.Range(0, 300).Select(i => new NpcChoice("a sphinx " + i, "Zone")));
        var npcWindow = new Window { Content = npcPanel, Width = 620, Height = 720, ShowInTaskbar = false };
        try
        {
            npcWindow.Show();
            choices.IsDropDownOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
            {
            });
            var popup = (Popup)choices.Template.FindName("PART_Popup", choices);
            popup.Child.UpdateLayout();
            Assert.True(popup.IsOpen);
            Assert.Contains(new WikiIconKey(WikiIconKind.Mob, "a sphinx 0"), icons.Requested);
            Assert.InRange(icons.Requested.Count, 1, 60);
        }
        finally
        {
            choices.IsDropDownOpen = false;
            npcWindow.Content = null;
            npcWindow.Close();
        }
    }

    private sealed class CountingIconSource : IWikiIconSource
    {
        public List<WikiIconKey> Requested { get; } = [];

        public WikiIconViewModel? GetWikiIcon(WikiIconKey key)
        {
            Requested.Add(key);
            return null;
        }
    }

    private static void VerifyBreakdownButtonsUsePressClickMode()
    {
        var actions = new RecordingOverlayActions();
        var vm = new OverlayViewModel("dps", "DPS", false, actions);
        var panel = new OverlayPanel(vm);
        var at = new DateTime(2026, 10, 21, 18, 0, 0);
        var hit = new DamageEvent(at, "Bob", "a goblin", 10, DamageKind.Melee, null, null);
        var breakdown = new AbilityBreakdown("Bob", "", "DPS",
            [new AbilityStats("Melee", 10, 10, 100, 1, 0, 10, 0)],
            [new DamageTimelinePoint(0, 10)], 1, at,
            [new AbilityTimeline("Melee", [new DamageTimelinePoint(0, 10)])], [hit], [hit]);
        vm.Show("a goblin", "active", [new OverlayRow("Bob", Breakdown: breakdown)]);
        Layout(panel, 300, 200);
        var subtitle = panel.SubtitleText;
        Assert.Equal("active", subtitle.Text);
        Assert.Equal(Visibility.Visible, subtitle.Visibility);

        Button BreakdownButton()
        {
            Layout(panel, 300, 200);
            return Assert.Single(Descendants(panel).OfType<Button>(),
                candidate => Equals(candidate.ToolTip, "Click for ability breakdown"));
        }

        var button = BreakdownButton();
        Assert.Equal(ClickMode.Press, button.ClickMode);
        Click(button);
        Assert.Same(breakdown, Assert.Single(actions.Breakdowns));

        var unchanged = breakdown with
        {
            Abilities = breakdown.Abilities.ToArray(),
            Timeline = breakdown.Timeline!.ToArray(),
            AbilityTimelines = [new AbilityTimeline("Melee", [new DamageTimelinePoint(0, 10)])],
            DamageEvents = breakdown.DamageEvents!.ToArray(),
            IncomingDamageEvents = breakdown.IncomingDamageEvents!.ToArray(),
        };
        var rowVm = Assert.Single(vm.Rows);
        vm.Show("a goblin", "active", [new OverlayRow("Bob", Breakdown: unchanged)]);
        Assert.Same(rowVm, Assert.Single(vm.Rows));
        Assert.Same(button, BreakdownButton());
        Assert.Equal(Visibility.Visible, subtitle.Visibility);

        var changed = unchanged with
        {
            DamageEvents = [hit, hit with { Amount = 20 }]
        };
        vm.Show("a goblin", "active", [new OverlayRow("Bob", Breakdown: changed)]);
        var updatedButton = BreakdownButton();
        Click(updatedButton);
        Assert.Equal(2, actions.Breakdowns[^1].DamageEvents!.Count);

        vm.Show("a goblin", null, [new OverlayRow("Bob", "20 dps", Breakdown: breakdown with { Title = "Updated" })]);
        Assert.Equal(Visibility.Hidden, subtitle.Visibility);
        var refreshedButton = BreakdownButton();
        Assert.Equal(ClickMode.Press, refreshedButton.ClickMode);
        Click(refreshedButton);
        Assert.Equal("Updated", actions.Breakdowns[^1].Title);
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

        var actions = new RecordingOverlayActions();
        bool? expanded = null;
        var vm = new DpsOverlayViewModel(definition, actions, EmptyOverlayViewSource.Instance, false, value => expanded = value);
        var panel = new OverlayPanel(vm);

        void Arrange()
        {
            Layout(panel, definition.Width, definition.Height);
        }

        vm.Show(content);
        vm.UpdateFights(snapshot with
        {
            CurrentFight = null,
            RecentFights = []
        });
        Arrange();
        var checkbox = Named<CheckBox>(panel, "FullDetailsBox");
        Assert.Equal("Full details", checkbox.Content);
        Assert.Null(expanded);
        var noFight = Named<TextBlock>(panel, "NoFight");
        Assert.Equal("No fight yet", noFight.Text);
        Assert.Equal(Visibility.Visible, noFight.Visibility);

        vm.UpdateFights(snapshot);
        Arrange();
        Assert.Equal(Visibility.Collapsed, noFight.Visibility);
        var list = Named<ItemsControl>(panel, "FightList");
        Assert.Equal(20, list.Items.Count);

        FrameworkElement Fight(int index)
        {
            return Assert.IsAssignableFrom<FrameworkElement>(list.ItemContainerGenerator.ContainerFromIndex(index));
        }

        var first = Fight(0);
        var oldest = Fight(19);
        Assert.Equal("Guardian Y", Named<TextBlock>(first, "FightTarget").Text);
        Assert.Equal("Guardian F", Named<TextBlock>(oldest, "FightTarget").Text);

        Click(Named<Button>(oldest, "FightButton"));
        Arrange();
        var details = Named<ContentControl>(oldest, "FightDetails");
        Assert.Equal(Visibility.Visible, details.Visibility);
        var attackers = Named<StackPanel>(oldest, "FightAttackers");
        Assert.Equal(Visibility.Visible, attackers.Visibility);
        var attacker = Assert.Single(Descendants(attackers).OfType<Button>(), button => button.Name == "RowButton");
        Assert.Contains(Descendants(attacker).OfType<TextBlock>(), text => text.Text == "You");
        Click(attacker);
        Assert.Equal("You", Assert.Single(actions.Breakdowns).Title);
        var attackerRow = vm.Fights[19].Attackers[0];
        vm.UpdateFights(snapshot);
        Assert.Same(attackerRow, vm.Fights[19].Attackers[0]);

        checkbox.IsChecked = true;
        Assert.True(expanded);
        Assert.True(vm.FullDetails);
        Arrange();
        Assert.Equal(Visibility.Collapsed, attackers.Visibility);
        var host = Named<ContentControl>(oldest, "FightBreakdown");
        Assert.Equal(Visibility.Visible, host.Visibility);
        var tabs = Descendants(host).OfType<TabControl>().First();
        Assert.Equal(20, list.Items.Count);
        Assert.Same(oldest, Fight(19));
        tabs.SelectedIndex = 1;
        vm.UpdateFights(snapshot);
        Assert.Same(tabs, Descendants(host).OfType<TabControl>().First());
        Assert.Equal(1, tabs.SelectedIndex);

        Click(Named<Button>(first, "FightButton"));
        Arrange();
        var firstHost = Named<ContentControl>(first, "FightBreakdown");
        var firstTabs = Descendants(firstHost).OfType<TabControl>().First();
        var abilityTable = Descendants(firstTabs).OfType<DataGrid>().First(grid => grid.Name == "AbilityTable");
        Assert.Equal(100, Assert.Single(abilityTable.Items.Cast<AbilityStats>()).Total);
        Assert.True(firstTabs.ActualHeight > 0);
        var scroll = Named<ScrollViewer>(panel, "FightScroll");
        Assert.True(scroll.ScrollableHeight > 0);

        string additionalHit = $"[{start.AddSeconds(25):ddd MMM dd HH:mm:ss yyyy}] You slash Guardian Y for 200 points of damage.";
        engine.ProcessLines([additionalHit]);
        var updatedSnapshot = engine.Snapshot(start.AddSeconds(30));
        vm.UpdateFights(updatedSnapshot);
        Arrange();
        Assert.Same(first, Fight(0));
        Assert.Same(firstTabs, Descendants(firstHost).OfType<TabControl>().First());
        Assert.Equal(300, Assert.Single(abilityTable.Items.Cast<AbilityStats>()).Total);
        firstTabs.SelectedIndex = 1;

        string nextFight = $"[{start.AddSeconds(26):ddd MMM dd HH:mm:ss yyyy}] You slash Guardian Z for 50 points of damage.";
        engine.ProcessLines([nextFight]);
        updatedSnapshot = engine.Snapshot(start.AddSeconds(30));
        vm.UpdateFights(updatedSnapshot);
        Arrange();
        Assert.Equal(20, list.Items.Count);
        Assert.Same(first, Fight(1));
        Assert.Same(firstTabs, Descendants(firstHost).OfType<TabControl>().First());
        Assert.Equal(1, firstTabs.SelectedIndex);

        checkbox.IsChecked = false;
        Assert.False(expanded);
        Arrange();
        Assert.Equal(Visibility.Collapsed, firstHost.Visibility);
        Assert.Equal(Visibility.Visible, Named<StackPanel>(first, "FightAttackers").Visibility);
        var firstDetails = Named<ContentControl>(first, "FightDetails");
        Click(Named<Button>(first, "FightButton"));
        Assert.Equal(Visibility.Collapsed, firstDetails.Visibility);

        foreach (bool docked in new[] { false, true })
        {
            vm.SetDocked(docked);
            Arrange();
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.True(scroll.ScrollableHeight > 0);
            scroll.ScrollToEnd();
            panel.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
            double offset = scroll.VerticalOffset;
            vm.UpdateFights(updatedSnapshot);
            panel.UpdateLayout();
            Assert.Equal(offset, scroll.VerticalOffset, 1);
        }

        vm.UpdateFights(snapshot with
        {
            RecentFights = [snapshot.RecentFights[0], snapshot.RecentFights[0]]
        });
        Arrange();
        Assert.Equal(2, list.Items.Count);
        Assert.NotSame(Fight(0), Fight(1));
        Assert.NotSame(vm.Fights[0], vm.Fights[1]);
    }

    private static void VerifyDockWindowBindings()
    {
        var settings = new DockWindowSettings();
        var first = settings.CreateWorkspace("First");
        var second = settings.CreateWorkspace("Second");
        var deleted = settings.CreateWorkspace("Old");
        Assert.True(settings.DeleteWorkspace(deleted.Id));
        settings.ActiveWorkspaceId = first.Id;
        var dock = new DockWindow(settings)
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
        };
        int changes = 0;
        dock.WorkspaceChanged += (_, _) =>
        {
            changes++;
        };
        try
        {
            dock.Show();
            dock.UpdateLayout();
            var tabs = Descendants(Named<ItemsControl>(dock, "WorkspaceTabs")).OfType<Button>().ToArray();
            Assert.Equal(["First", "Second"], tabs.Select(tab => (string)tab.Content));
            Assert.Contains(Descendants(dock).OfType<TextBlock>(),
                text => text.Text.StartsWith("No panels are in this workspace.", StringComparison.Ordinal) && text.IsVisible);

            tabs[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(second.Id, settings.ActiveWorkspaceId);
            Assert.Equal(1, changes);
            Assert.True(dock.ViewModel.Workspaces.Single(tab => tab.Id == second.Id).IsSelected);

            var restore = Named<Button>(dock, "RestoreWorkspaceButton");
            Assert.True(restore.IsEnabled);
            restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var menu = restore.ContextMenu!;
            Assert.True(menu.IsOpen);
            menu.UpdateLayout();
            var item = Assert.IsType<MenuItem>(menu.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Equal("Old", Assert.IsType<DockWorkspaceTab>(item.DataContext).Name);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Assert.False(deleted.Deleted);
            Assert.Equal(2, changes);
            dock.UpdateLayout();
            Assert.False(restore.IsEnabled);
            Assert.Equal(3, Descendants(Named<ItemsControl>(dock, "WorkspaceTabs")).OfType<Button>().Count());

            int downloads = 0;
            var download = new EqLog.Overlay.Mvvm.RelayCommand(() =>
            {
                downloads++;
            });
            dock.SetNotice(UpdateNoticeViewModel.For("1.2.3", downloaded: false, busy: false, download, download));
            dock.UpdateLayout();
            var notice = Named<ContentControl>(dock, "Notice");
            Assert.True(notice.IsVisible);
            Assert.Contains(Descendants(notice).OfType<TextBlock>(), text => text.Text == "\u2B06 Version 1.2.3 is available");
            var action = Descendants(notice).OfType<Button>().Single();
            Assert.Equal("Download update", action.Content);
            action.Command.Execute(null);
            Assert.Equal(1, downloads);
            dock.SetNotice(null);
            dock.UpdateLayout();
            Assert.False(notice.IsVisible);

            var loading = Named<ContentControl>(dock, "HistoryLoadingNotice");
            Assert.False(loading.IsVisible);
            dock.SetLoadingNotice(new ProgressBar { Tag = "Loading saved data...", IsIndeterminate = true });
            dock.UpdateLayout();
            Assert.True(loading.IsVisible);
            Assert.Contains(Descendants(loading).OfType<TextBlock>(), text => text.Text == "Loading saved data...");
            Assert.True(Descendants(loading).OfType<ProgressBar>().Single().IsIndeterminate);
        }
        finally
        {
            dock.Close();
        }
    }

    private static void VerifyFloatingPlacement()
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
                Assert.False(window.IsVisible);
                Assert.False(dock.HasPanel("dps"));
                Assert.False(panel.ViewModel.IsDocked);
                Assert.False(panel.ViewModel.IsTemporarilyDocked);
                Assert.Same(panel, window.Content);
                Assert.False(settings.IsPanelInAnyWorkspace("dps"));

                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, true, false);
                Assert.False(dock.HasPanel("dps"));
                Assert.True(window.IsVisible);
                Assert.Same(panel, window.Content);
                Assert.False(panel.ViewModel.IsDocked);
                Assert.False(placement.Docked);
                Assert.False(settings.IsPanelInAnyWorkspace("dps"));
                Assert.Equal(42, placement.DockLeft);
                Assert.Equal(73, placement.DockTop);
                Assert.Equal(savedLeft, firstWorkspace.Panels["session"].DockLeft);
                Assert.Equal(savedTop, firstWorkspace.Panels["session"].DockTop);

                MainWindow.ApplyOverlayPlacement(dock, window, placement, false, true, false);
                Assert.False(dock.HasPanel("dps"));
                Assert.False(window.IsVisible);
                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, true);
                Assert.True(window.IsVisible);
                Assert.False(dock.HasPanel("dps"));

                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                Assert.False(window.IsVisible);
                Assert.False(dock.HasPanel("dps"));
                Assert.False(placement.Docked);
                Assert.Equal(savedLeft, firstWorkspace.Panels["session"].DockLeft);
                Assert.Equal(savedTop, firstWorkspace.Panels["session"].DockTop);

                var secondWorkspace = settings.CreateWorkspace("Second");
                dock.ClearPanelsForWorkspaceChange();
                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                Assert.False(window.IsVisible);
                Assert.False(dock.HasPanel("dps"));
                Assert.Empty(secondWorkspace.Panels);
                Assert.False(settings.IsPanelInAnyWorkspace("dps"));

                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, true, false);
                Assert.True(window.IsVisible);
                Assert.Same(panel, window.Content);
                Assert.False(dock.HasPanel("dps"));

                dock.SetPanelInActiveWorkspace("dps", true, placement);
                MainWindow.ApplyOverlayPlacement(dock, window, placement, true, false, false);
                Assert.False(dock.IsTemporaryPanel("dps"));
                Assert.True(placement.Docked);
                Assert.True(secondWorkspace.Panels.ContainsKey("dps"));
                Assert.True(dock.HasPanel("dps"));
                Assert.False(window.IsVisible);
                Assert.Null(window.Content);
            }
            finally
            {
                window.Close();
                dock.Close();
            }
        }
    }

    private static void VerifyItemFilterToolbars()
    {
        foreach (string key in new[] { "loot", "itemupgrades" })
        {
            var definition = Assert.Single(OverlayPresenters.All, item => item.Key == key);
            int? grouping = null;
            var vm = key == "loot"
                ? new ItemListOverlayViewModel(definition, NullOverlayActions.Instance, EmptyOverlayViewSource.Instance,
                    ["Recent", "Grouped"], 0, "Grouping", value => grouping = value)
                : new ItemListOverlayViewModel(definition, NullOverlayActions.Instance, EmptyOverlayViewSource.Instance);
            var panel = new OverlayPanel(vm);
            Layout(panel, 300, 220);
            var filter = Named<TextBox>(panel, "ItemFilter");
            var options = Named<ComboBox>(panel, "ItemGrouping");

            Assert.Equal("Item name filter", System.Windows.Automation.AutomationProperties.GetName(filter));
            Assert.IsType<StackPanel>(filter.Parent);
            Assert.Same(filter.Parent, options.Parent);
            Assert.True(OverlayPanel.IsInteractive(filter));
            if (key == "loot")
            {
                Assert.Equal(Visibility.Visible, options.Visibility);
                Assert.Equal(2, options.Items.Count);
                options.SelectedIndex = 1;
                Assert.Equal(1, grouping);
                Assert.Equal(1, vm.Grouping);
            }
            else
            {
                Assert.Equal(Visibility.Collapsed, options.Visibility);
            }
            foreach (bool docked in new[] { false, true })
            {
                vm.SetDocked(docked);
                filter.Text = "Sword";
                WaitForFilter(() => vm.FilterText == "Sword");
                filter.Clear();
                WaitForFilter(() => vm.FilterText == "");
            }
        }
    }

    private static void VerifyMobInfoTitleAlwaysVisible()
    {
        var definition = OverlayPresenters.All.Single(definition => definition.Key == "mobinfo");
        var vm = new MobInfoOverlayViewModel(definition, NullOverlayActions.Instance,
            new MobInfoOverlayViewModelTests.FakeServices(), 5, "ready");
        var panel = new OverlayPanel(vm);
        var frame = Assert.IsType<Border>(panel.Content);
        var header = panel.TitleFrame;
        foreach (bool docked in new[] { false, true, false })
        {
            vm.SetDocked(docked);
            foreach (bool locked in new[] { false, true })
            {
                vm.IsLocked = locked;
                vm.IsChromeHidden = true;
                Assert.Equal(Visibility.Visible, header.Visibility);
                if (!docked)
                {
                    Assert.Equal(Brushes.Transparent, frame.BorderBrush);
                }
                vm.IsChromeHidden = false;
                Assert.Equal(Visibility.Visible, header.Visibility);
            }
        }
    }

    private static void VerifyHoverBackgroundOpacity()
    {
        var panel = new OverlayPanel("timers", "Spell Timers", false);
        var vm = panel.ViewModel;
        var frame = Assert.IsType<Border>(panel.Content);
        var hoverChanges = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.IsBackgroundHovered))
            {
                hoverChanges.Add(vm.IsBackgroundHovered);
            }
        };
        vm.BackgroundOpacity = 0.2;
        vm.HoverBackgroundOpacity = 0.9;
        Assert.Equal((byte)(0.2 * 255), Alpha(frame));

        foreach (bool docked in new[] { false, true })
        {
            vm.SetDocked(docked);
            vm.IsLocked = true;
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            Assert.Equal((byte)(0.9 * 255), Alpha(frame));
            Assert.Equal(1, panel.Opacity);
            vm.BackgroundOpacity = 0.3;
            Assert.Equal((byte)(0.9 * 255), Alpha(frame));
            vm.HoverBackgroundOpacity = 0.8;
            Assert.Equal((byte)(0.8 * 255), Alpha(frame));
            vm.BackgroundOpacity = 0.9;
            Assert.Equal((byte)(0.9 * 255), Alpha(frame));
            vm.HoverBackgroundOpacity = 0.1;
            Assert.Equal((byte)(0.9 * 255), Alpha(frame));
            vm.HoverBackgroundOpacity = 0.9;
            Assert.Equal((byte)(0.9 * 255), Alpha(frame));
            vm.HoverBackgroundOpacity = 0.8;
            vm.BackgroundOpacity = 0.3;
            Assert.Equal((byte)(0.8 * 255), Alpha(frame));
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Assert.Equal((byte)(0.3 * 255), Alpha(frame));
            vm.BackgroundOpacity = 0.9;
            vm.HoverBackgroundOpacity = 0.1;
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            Assert.Equal((byte)(0.9 * 255), Alpha(frame));
            panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Assert.Equal((byte)(0.9 * 255), Alpha(frame));
            vm.BackgroundOpacity = 0.2;
            vm.HoverBackgroundOpacity = 0.9;
        }
        Assert.Equal(new[] { true, false, true, false, true, false, true, false }, hoverChanges);
    }

    private static byte Alpha(Border frame)
    {
        return (byte)(Assert.IsType<SolidColorBrush>(frame.Background).Opacity * 255);
    }

    private static void VerifyRoundedFrame(OverlayPanel panel, Border frame)
    {
        foreach (bool docked in new[] { false, true, false })
        {
            panel.ViewModel.SetDocked(docked);
            Assert.Equal(new CornerRadius(12), frame.CornerRadius);
            foreach (var size in new[] { new Size(300, 220), new Size(420, 320) })
            {
                Layout(panel, size.Width, size.Height);
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
        panel.ViewModel.IsChromeHidden = false;
        window.UpdateLayout();
        var grip = Assert.Single(header.Children.OfType<Thumb>());
        Assert.Same(panel.DragHandle, grip);
        Assert.Equal(Visibility.Visible, grip.Visibility);
        Assert.True(OverlayPanel.IsInteractive(grip));
        grip.ApplyTemplate();
        var box = Assert.IsType<Border>(VisualTreeHelper.GetChild(grip, 0));
        Assert.Equal(new Thickness(1), box.BorderThickness);

        var original = new Point(window.Left, window.Top);
        panel.TitleText.RaiseEvent(new DragDeltaEventArgs(20, 30));
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
        panel.ViewModel.SetDocked(true);
        Assert.Equal(Visibility.Visible, grip.Visibility);
        Assert.True(grip.IsEnabled);
        panel.ViewModel.SetDocked(false);
        window.SetLocked(false);
    }

    private static void VerifyMoteColumns()
    {
        var actions = new RecordingOverlayActions();
        var vm = new OverlayViewModel("motes", "Motes", false, actions) { FontSize = 13 };
        var panel = new OverlayPanel(vm);
        OverlayRow[] content =
        [
            new("Tier", "Session (all)", Bold: true, ExtraColumn: "Owned", ValueColumn: "Gear/Spell",
                Tone: OverlayRowTone.TableHeader),
            new("Infinitesimal", "1  (1)", LootItem: "Mote of Infinitesimal Potential",
                ExtraColumn: "7", ToolTip: "Tier value details", ValueColumn: "1/1 ≤+0", Tone: OverlayRowTone.TableRow),
            new("Infinite", "12  (2222)", LootItem: "Mote of Infinite Potential",
                ExtraColumn: "3", ValueColumn: "10/512 ≤+9", Tone: OverlayRowTone.TableRowBanded),
            new("Total", "13  (2223)", Bold: true, ExtraColumn: "10", ValueColumn: "", Tone: OverlayRowTone.TableTotal),
        ];
        vm.Show("Motes", null, content);
        var rows = LayoutMoteRows(panel, 300);
        var name = Named<TextBlock>(rows[1], "LeftText");
        var value = ColumnText(rows[2], "ValueColumn");
        Assert.Equal("Infinitesimal", name.Text);
        Assert.Equal("10/512 ≤+9", value.Text);
        AssertTextFits(name);
        AssertTextFits(value);
        Assert.Null(name.TextDecorations);
        Assert.Equal(Visibility.Collapsed, Named<Border>(rows[1], "TableStripe").Visibility);
        Assert.Equal(Visibility.Visible, Named<Border>(rows[2], "TableStripe").Visibility);
        Assert.Equal(new Thickness(0, 0, 0, 1), Named<Border>(rows[0], "TableStripe").BorderThickness);
        Assert.Equal(new Thickness(0, 1, 0, 0), Named<Border>(rows[3], "TableStripe").BorderThickness);
        var itemGrid = Named<Grid>(rows[1], "RowRoot");
        var itemButton = Named<Button>(rows[1], "RowButton");
        Assert.Equal("Tier value details", itemGrid.ToolTip);
        Assert.NotNull(itemButton.ContextMenu);
        Click(itemButton);
        Assert.Equal("Mote of Infinitesimal Potential", Assert.Single(actions.ItemWikis));

        var original = content;
        content = [.. content];
        content[1] = content[1] with
        {
            Right = "100000  (2000000)",
            ExtraColumn = "3000000"
        };
        vm.Show("Motes", null, content);
        rows = LayoutMoteRows(panel, 450);
        var count = ColumnText(rows[1], "RightColumn");
        Assert.Equal("100000  (2000000)", count.Text);
        AssertTextFits(count);

        vm.Show("Motes", null, original);
        LayoutMoteRows(panel, 300);

        vm.SetDocked(true);
        vm.FontSize = 18;
        vm.Show("Motes", null, content);
        rows = LayoutMoteRows(panel, 540);
        AssertTextFits(Named<TextBlock>(rows[1], "LeftText"));
        AssertTextFits(ColumnText(rows[2], "ValueColumn"));
    }

    private static void VerifyCompactCards()
    {
        var vm = new OverlayViewModel("timers", "Timers") { FontSize = 13 };
        var panel = new OverlayPanel(vm);
        vm.Show("Timers", null, [new OverlayRow("First", Details: "A detail")]);
        foreach (bool docked in new[] { false, true })
        {
            vm.SetDocked(docked);
            Layout(panel, 300, 200);
            var card = Named<Grid>(Assert.Single(RowContainers(panel)), "RowRoot");
            Assert.InRange(card.ActualHeight, 1, 56);
            var heading = Named<TextBlock>(card, "LeftText");
            var detail = Named<TextBlock>(card, "CardDetails");
            double headingTop = heading.TranslatePoint(new Point(), card).Y;
            double headingBottom = headingTop + heading.ActualHeight;
            double detailTop = detail.TranslatePoint(new Point(), card).Y;
            Assert.True(headingTop >= 7);
            Assert.True(detailTop - headingBottom >= 5);
            Assert.InRange(card.ActualHeight - detailTop - detail.ActualHeight, 6.5, 8);
        }

        vm.FontSize = 18;
        Layout(panel, 300, 200);
        var content = Named<StackPanel>(Assert.Single(RowContainers(panel)), "RowContent");
        foreach (var text in content.Children.OfType<TextBlock>().Where(text => text.IsVisible))
        {
            var unbounded = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily };
            unbounded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(text.ActualHeight + 1 >= unbounded.DesiredSize.Height);
        }
    }

    private static void VerifyCardTableBindings()
    {
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Error;
        try
        {
            foreach (string key in new[] { "alerts", "quests" })
            {
                var actions = new RecordingOverlayActions();
                var vm = new OverlayViewModel(key, "Test", actions: actions) { FontSize = 16 };
                var table = OverlayTable.Pairs([("Wiki", "Target")])
                    .WithLinks([("Target", "https://example.test/target")]);
                var panel = new OverlayPanel(vm);
                var window = new Window { Content = panel, Width = 400, Height = 300, ShowInTaskbar = false };
                try
                {
                    vm.Show("Test", null, [new OverlayRow("Card", Table: table)]);
                    window.Show();
                    window.UpdateLayout();
                    var view = Assert.Single(Descendants(panel).OfType<OverlayTableView>());
                    Assert.Same(vm.OpenLinkCommand, view.LinkCommand);
                    Assert.Equal(15, view.FontSize);
                    var link = Assert.Single(Descendants(view).OfType<TextBlock>(), text => text.Text == "Target");
                    Assert.Same(vm.OpenLinkCommand, ClickCommand.GetCommand(link));
                    ClickCommand.GetCommand(link)!.Execute(ClickCommand.GetCommandParameter(link));
                    Assert.Equal("https://example.test/target", Assert.Single(actions.Links));
                    vm.FontSize = 21;
                    Assert.Equal(20, view.FontSize);

                    var template = Named<ItemsControl>(panel, "CardTables").ItemTemplate;
                    var independent = Assert.IsType<OverlayTableView>(template.LoadContent());
                    independent.DataContext = Assert.Single(vm.Rows).Tables[0];
                    var detachedWindow = new Window
                    {
                        Content = independent,
                        Width = 200,
                        Height = 100,
                        ShowInTaskbar = false,
                    };
                    try
                    {
                        detachedWindow.Show();
                        detachedWindow.UpdateLayout();
                        Assert.Same(vm.OpenLinkCommand, independent.LinkCommand);
                        Assert.Equal(20, independent.FontSize);
                    }
                    finally
                    {
                        detachedWindow.Close();
                    }

                    vm.Show("Test", null, []);
                    window.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
                    {
                    });
                    vm.Show("Test", null, [new OverlayRow("New card", Table: table)]);
                    window.UpdateLayout();
                    var replaced = Assert.Single(Descendants(panel).OfType<OverlayTableView>());
                    Assert.Same(vm.OpenLinkCommand, replaced.LinkCommand);
                    Assert.Equal(20, replaced.FontSize);
                }
                finally
                {
                    window.Close();
                }
            }
            listener.Flush();
            Assert.DoesNotContain("target element is 'OverlayTableView'", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
        }
    }

    private static void VerifyTableLayout()
    {
        var table = new OverlayTable(["Item", "Count"], [["Bone", "12"], ["Silk", "-3"], ["Gem", "?"]])
            .WithLinks([("Bone", "https://example.test/bone")]);
        var view = new OverlayTableView { Table = table };
        var window = new Window { Content = view, Width = 300, Height = 200, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var grid = Assert.Single(Descendants(view).OfType<Grid>(), g => g.IsItemsHost);
            Assert.Equal([GridUnitType.Star, GridUnitType.Auto], grid.ColumnDefinitions.Select(c => c.Width.GridUnitType));
            Assert.Equal(4, grid.RowDefinitions.Count);

            var cells = Descendants(view).OfType<TextBlock>().ToDictionary(text => text.Text);
            Assert.Equal(FontWeights.SemiBold, cells["Count"].FontWeight);
            Assert.Equal(TextTrimming.CharacterEllipsis, cells["Count"].TextTrimming);
            Assert.Equal(TextAlignment.Right, cells["Count"].TextAlignment);
            Assert.Equal(TextAlignment.Right, cells["-3"].TextAlignment);
            Assert.Equal(TextAlignment.Left, cells["Silk"].TextAlignment);
            Assert.Equal(TextWrapping.Wrap, cells["Silk"].TextWrapping);
            Assert.Equal(2, Grid.GetRow(VisualTreeHelper.GetParent(cells["Silk"]) as UIElement));
            Assert.Null(ClickCommand.GetCommand(cells["Bone"]));
            Assert.Empty(cells["Bone"].TextDecorations);

            var stripe = Assert.Single(Descendants(view).OfType<Border>(), border => border.Opacity == 0.7);
            Assert.Equal(2, Grid.GetRow(VisualTreeHelper.GetParent(stripe) as UIElement));
            Assert.Equal(2, Grid.GetColumnSpan(VisualTreeHelper.GetParent(stripe) as UIElement));

            var opened = new List<string?>();
            view.LinkCommand = new EqLog.Overlay.Mvvm.RelayCommand<string>(url => opened.Add(url));
            window.UpdateLayout();
            var link = Assert.Single(Descendants(view).OfType<TextBlock>(), text => text.Text == "Bone");
            Assert.NotEmpty(link.TextDecorations);
            Assert.Equal("Open on EQLWiki", link.ToolTip);
            Assert.Null(ClickCommand.GetCommand(Assert.Single(Descendants(view).OfType<TextBlock>(), text => text.Text == "Silk")));
            ClickCommand.GetCommand(link)!.Execute(ClickCommand.GetCommandParameter(link));
            Assert.Equal("https://example.test/bone", Assert.Single(opened));

            view.Table = OverlayTable.Pairs([("Zone", "Nexus"), ("Level", "50")], pairsPerRow: 2);
            window.UpdateLayout();
            grid = Assert.Single(Descendants(view).OfType<Grid>(), g => g.IsItemsHost);
            Assert.Equal([GridUnitType.Auto, GridUnitType.Star, GridUnitType.Auto, GridUnitType.Star],
                grid.ColumnDefinitions.Select(c => c.Width.GridUnitType));
            cells = Descendants(view).OfType<TextBlock>().ToDictionary(text => text.Text);
            Assert.Equal(TextWrapping.NoWrap, cells["Zone"].TextWrapping);
            Assert.Equal(FontWeights.SemiBold, cells["50"].FontWeight);
            Assert.Equal(TextAlignment.Left, cells["50"].TextAlignment);
            Assert.DoesNotContain(Descendants(view).OfType<Border>(), border => border.Opacity == 0.7);
        }
        finally
        {
            window.Close();
        }
    }

    private static readonly string[] MoteColumnNames = ["ExtraColumn", "RightColumn", "ValueColumn"];

    private static FrameworkElement[] LayoutMoteRows(OverlayPanel panel, double width)
    {
        Layout(panel, width, 400);
        var rows = RowContainers(panel);
        var stack = Named<ItemsControl>(panel, "RowItems");

        Assert.Equal(4, rows.Length);
        double X(FrameworkElement element)
        {
            return element.TranslatePoint(new Point(0, 0), stack).X;
        }

        double nameX = X(Named<TextBlock>(rows[0], "LeftText"));
        double valueX = X(Named<Grid>(rows[0], "ValueColumn"));
        double countX = X(Named<Grid>(rows[0], "RightColumn"));
        double ownedX = X(Named<Grid>(rows[0], "ExtraColumn"));
        Assert.True(nameX < valueX && valueX < countX && countX < ownedX);

        foreach (string columnName in MoteColumnNames)
        {
            var header = Named<Grid>(rows[0], columnName);
            double headerX = X(header);
            foreach (var row in rows)
            {
                var column = Named<Grid>(row, columnName);
                Assert.Equal(header.ActualWidth, column.ActualWidth, 2);
                Assert.Equal(headerX, X(column), 2);
                AssertTextFits(ColumnText(row, columnName));
            }
        }
        foreach (var row in rows)
        {
            AssertTextFits(Named<TextBlock>(row, "LeftText"));
        }

        return rows;
    }

    private static TextBlock ColumnText(FrameworkElement row, string column)
    {
        return Assert.Single(Named<Grid>(row, column).Children.OfType<TextBlock>());
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
        var vm = new OverlayViewModel(definition.Key, definition.Caption) { FontSize = 13 };
        var panel = new OverlayPanel(vm);
        vm.Show(content);

        foreach (bool docked in new[] { false, true })
        {
            vm.SetDocked(docked);
            Layout(panel, definition.Width, definition.Height);
            Assert.Equal(MoteValues.MaxRank * 2 + 2, RowContainers(panel).Length);
            var scroll = Named<ScrollViewer>(panel, "RowScroll");
            Assert.True(scroll.ScrollableHeight > 1,
                $"The {(docked ? "docked" : "floating")} overlay must scroll to show every mote and fragment.");
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        }
    }

    private static FrameworkElement[] RowContainers(OverlayPanel panel)
    {
        var items = RowsControl(panel);
        return Enumerable.Range(0, items.Items.Count)
            .Select(index => Assert.IsAssignableFrom<FrameworkElement>(items.ItemContainerGenerator.ContainerFromIndex(index)))
            .ToArray();
    }

    /// <summary>The overlay's row list: the plain <c>RowItems</c> or the virtualized <c>RowList</c>.</summary>
    private static ItemsControl RowsControl(OverlayPanel panel)
    {
        var found = Descendants(panel).OfType<ItemsControl>()
            .FirstOrDefault(element => element.Name is "RowItems" or "RowList");
        Assert.True(found is not null, "No overlay row list was found.");
        return found;
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static T Named<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var found = Descendants(root).OfType<T>().FirstOrDefault(element => element.Name == name);
        Assert.True(found is not null, $"No {typeof(T).Name} named '{name}' was found.");
        return found;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Clicks like a user would, so the button's command runs, then lets queued input work finish.</summary>
    private static void Click(ButtonBase button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
        {
        });
    }

    private static void WaitForFilter(Func<bool> completed)
    {
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer
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
            Dispatcher.PushFrame(frame);
            Assert.True(completed(), "The latest text filter was not applied.");
        }
        finally
        {
            timer.Stop();
        }
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

    private sealed class RecordingOverlayActions : IOverlayActions
    {
        public List<AbilityBreakdown> Breakdowns { get; } = [];

        public List<string> ItemWikis { get; } = [];

        public List<string> Links { get; } = [];

        public void OpenLink(string url)
        {
            Links.Add(url);
        }

        public void OpenItemWiki(string item)
        {
            ItemWikis.Add(item);
        }

        public void LookupQuestReferences(string item)
        {
        }

        public bool HasQuestReferences(string item)
        {
            return false;
        }

        public void ShowBreakdown(AbilityBreakdown breakdown)
        {
            Breakdowns.Add(breakdown);
        }

        public void ToggleDock(OverlayViewModel overlay)
        {
        }

        public void MoveToWorkspace(OverlayViewModel overlay, string workspaceId)
        {
        }
    }
}
