using System.Text.Json;
using System.Windows;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MergeOverlayTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitializeMergeOverlay_MigratesLegacyDockedInventoryWithMatchingVisibility(bool visible)
    {
        var inventory = InventoryPlacement();
        inventory.Visible = visible;
        inventory.Docked = true;
        inventory.DockLeft = 42;
        inventory.DockTop = 73;
        inventory.DockWidth = 480;
        inventory.DockHeight = 260;
        var settings = new AppSettings { Overlays = new() { ["inventory"] = inventory } };

        MainWindow.InitializeMergeOverlay(settings, WorkArea);

        var merge = settings.Overlays["merge"];
        Assert.Equal(visible, merge.Visible);
        Assert.True(merge.Docked);
        Assert.Equal(inventory.FontSize, merge.FontSize);
        Assert.Equal(inventory.BackgroundOpacity, merge.BackgroundOpacity);
        Assert.Equal(inventory.HoverBackgroundOpacity, merge.HoverBackgroundOpacity);
        Assert.Equal(inventory.Locked, merge.Locked);
        Assert.Equal(inventory.Width, merge.Width);
        Assert.Equal(inventory.Height, merge.Height);
        var workspace = settings.DockWindow.ActiveWorkspace;
        Assert.Equal(42, workspace.Panels["inventory"].DockLeft);
        Assert.Equal(73, workspace.Panels["inventory"].DockTop);
        Assert.Equal(4, workspace.Panels["merge"].DockLeft);
        Assert.Equal(341, workspace.Panels["merge"].DockTop);
        Assert.Equal(480, workspace.Panels["merge"].DockWidth);
        Assert.Equal(260, workspace.Panels["merge"].DockHeight);
        Assert.NotSame(inventory, merge);
        Assert.NotSame(workspace.Panels["inventory"], workspace.Panels["merge"]);
    }

    [Fact]
    public void InitializeMergeOverlay_AddsToEveryInventoryWorkspaceBelowExistingPanels()
    {
        var settings = new AppSettings
        {
            Overlays = new() { ["inventory"] = InventoryPlacement() },
        };
        var first = settings.DockWindow.CreateWorkspace("First");
        first.Panels["inventory"] = new DockPanelSettings
        {
            DockLeft = 20,
            DockTop = 40,
            DockWidth = 500,
            DockHeight = 300,
        };
        first.Panels["dps"] = new DockPanelSettings
        {
            DockLeft = 550,
            DockTop = 600,
            DockWidth = 300,
            DockHeight = 200,
        };
        var second = settings.DockWindow.CreateWorkspace("Second");
        second.Panels["inventory"] = new DockPanelSettings
        {
            DockLeft = 10,
            DockTop = 20,
            DockWidth = 600,
            DockHeight = 400,
        };
        var unrelated = settings.DockWindow.CreateWorkspace("Other");
        var deleted = settings.DockWindow.CreateWorkspace("Deleted");
        deleted.Panels["inventory"] = new DockPanelSettings { DockTop = 10, DockHeight = 100 };
        deleted.Deleted = true;
        settings.DockWindow.ActiveWorkspaceId = first.Id;

        MainWindow.InitializeMergeOverlay(settings, WorkArea);

        Assert.True(settings.Overlays["merge"].Docked);
        Assert.Equal(first.Id, settings.DockWindow.ActiveWorkspaceId);
        Assert.Equal(808, first.Panels["merge"].DockTop);
        Assert.Equal(500, first.Panels["merge"].DockWidth);
        Assert.Equal(428, second.Panels["merge"].DockTop);
        Assert.Equal(600, second.Panels["merge"].DockWidth);
        Assert.DoesNotContain("merge", unrelated.Panels.Keys);
        Assert.Contains("merge", deleted.Panels.Keys);
        Assert.Equal(40, first.Panels["inventory"].DockTop);
        Assert.Equal(600, first.Panels["dps"].DockTop);
    }

    [Fact]
    public void InitializeMergeOverlay_MigratesOrphanedLegacyDockFlagIntoActiveWorkspace()
    {
        var inventory = InventoryPlacement();
        inventory.Docked = true;
        var settings = new AppSettings { Overlays = new() { ["inventory"] = inventory } };
        var workspace = settings.DockWindow.CreateWorkspace("Dashboard");

        MainWindow.InitializeMergeOverlay(settings, WorkArea);

        Assert.True(settings.Overlays["merge"].Docked);
        Assert.Contains("inventory", workspace.Panels.Keys);
        Assert.Contains("merge", workspace.Panels.Keys);
    }

    [Theory]
    [InlineData(1920, 1080, 428, 30)]
    [InlineData(700, 1080, 20, 338)]
    [InlineData(700, 500, 40, 40)]
    public void InitializeMergeOverlay_FloatingInventoryGetsIndependentFloatingMerge(
        double workWidth, double workHeight, double expectedLeft, double expectedTop)
    {
        var inventory = InventoryPlacement();
        var settings = new AppSettings { Overlays = new() { ["inventory"] = inventory } };

        MainWindow.InitializeMergeOverlay(settings, new Rect(0, 0, workWidth, workHeight));

        var merge = settings.Overlays["merge"];
        Assert.True(merge.Visible);
        Assert.False(merge.Docked);
        Assert.False(settings.DockWindow.IsPanelInAnyWorkspace("merge"));
        Assert.Equal(expectedLeft, merge.Left);
        Assert.Equal(expectedTop, merge.Top);
        Assert.Equal(20, inventory.Left);
        Assert.Equal(30, inventory.Top);
        merge.FontSize = 22;
        Assert.Equal(15, inventory.FontSize);
    }

    [Fact]
    public void InitializeMergeOverlay_NewSettingsKeepMergeHiddenAndIndependentlySelectable()
    {
        var settings = new AppSettings();

        MainWindow.InitializeMergeOverlay(settings, WorkArea);

        var merge = settings.Overlays["merge"];
        Assert.False(merge.Visible);
        Assert.True(merge.Docked);
        Assert.False(merge.Locked);
        Assert.Equal(680, merge.Width);
        Assert.Equal(620, merge.Height);
        Assert.Equal("Ready to merge", OverlayPresenters.All.Single(def => def.Key == "merge").Caption);
        Assert.Empty(settings.DockWindow.Workspaces);
    }

    [Fact]
    public void InitializeMergeOverlay_RestartPreservesHiddenUndockedMergeAndDoesNotReAddIt()
    {
        var inventory = InventoryPlacement();
        inventory.Docked = true;
        var settings = new AppSettings { Overlays = new() { ["inventory"] = inventory } };
        MainWindow.InitializeMergeOverlay(settings, WorkArea);
        var merge = settings.Overlays["merge"];
        merge.Visible = false;
        merge.Docked = false;
        merge.Left = 800;
        merge.FontSize = 19;
        settings.DockWindow.MovePanelToWorkspace("merge", null, new DockPanelSettings());
        var loaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings.Snapshot(), AppSettings.Json), AppSettings.Json)!;

        MainWindow.InitializeMergeOverlay(loaded, WorkArea);

        var saved = loaded.Overlays["merge"];
        Assert.False(saved.Visible);
        Assert.False(saved.Docked);
        Assert.Equal(800, saved.Left);
        Assert.Equal(19, saved.FontSize);
        Assert.False(loaded.DockWindow.IsPanelInAnyWorkspace("merge"));
        Assert.Contains("merge", loaded.DockWindow.ActiveWorkspace.UndockedPanels.Keys);
        Assert.Contains("inventory", loaded.DockWindow.ActiveWorkspace.Panels.Keys);
    }

    [Fact]
    public void InitializeMergeOverlay_DoesNotOverwriteExistingMergeSettings()
    {
        var saved = new OverlayPlacement { Visible = false, Left = 900, FontSize = 20 };
        var settings = new AppSettings
        {
            Overlays = new() { ["inventory"] = InventoryPlacement(), ["merge"] = saved },
        };

        MainWindow.InitializeMergeOverlay(settings, WorkArea);

        Assert.Same(saved, settings.Overlays["merge"]);
        Assert.False(saved.Visible);
        Assert.Equal(900, saved.Left);
        Assert.Equal(20, saved.FontSize);
        Assert.Empty(settings.DockWindow.Workspaces);
    }

    private static OverlayPlacement InventoryPlacement()
    {
        return new OverlayPlacement
        {
            Left = 20,
            Top = 30,
            Width = 400,
            Height = 300,
            Visible = true,
            FontSize = 15,
            BackgroundOpacity = 0.3,
            HoverBackgroundOpacity = 0.8,
            Locked = false,
        };
    }
}
