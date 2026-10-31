using System.Text.Json;
using System.Windows;
using EqLog.Overlay;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class DockWorkspaceSettingsTests
{
    [Fact]
    public void EnsureActiveWorkspace_MigratesLegacyDockPlacementsToDashboard()
    {
        var overlays = new Dictionary<string, OverlayPlacement>
        {
            ["dps"] = new OverlayPlacement
            {
                Docked = true,
                DockLeft = 42,
                DockTop = 73,
                DockWidth = 480,
                DockHeight = 260,
            },
            ["alerts"] = new OverlayPlacement { Docked = false },
        };
        var settings = new DockWindowSettings();

        var workspace = settings.EnsureActiveWorkspace(overlays);

        Assert.Equal("Dashboard", workspace.Name);
        Assert.Equal(workspace.Id, settings.ActiveWorkspaceId);
        Assert.Equal(42, workspace.Panels["dps"].DockLeft);
        Assert.Equal(73, workspace.Panels["dps"].DockTop);
        Assert.Equal(480, workspace.Panels["dps"].DockWidth);
        Assert.Equal(260, workspace.Panels["dps"].DockHeight);
        Assert.DoesNotContain("alerts", workspace.Panels.Keys);
    }

    [Fact]
    public void DeleteAndRestoreWorkspace_PreservePanelMembershipAndPlacement()
    {
        var settings = new DockWindowSettings();
        var original = settings.CreateWorkspace("Raid");
        original.Panels["dps"] = new DockPanelSettings
        {
            DockLeft = 120,
            DockTop = 80,
            DockWidth = 500,
            DockHeight = 300,
        };
        var other = settings.CreateWorkspace("Trading");

        Assert.True(settings.DeleteWorkspace(original.Id));
        Assert.Equal(other.Id, settings.ActiveWorkspaceId);
        Assert.True(original.Deleted);
        Assert.Equal(120, original.Panels["dps"].DockLeft);
        Assert.True(settings.RestoreWorkspace(original.Id));
        Assert.Equal(original.Id, settings.ActiveWorkspaceId);
        Assert.False(original.Deleted);
        Assert.Equal(500, settings.ActiveWorkspace.Panels["dps"].DockWidth);
    }

    [Fact]
    public void DeleteWorkspace_DoesNotRemoveLastActiveWorkspace()
    {
        var settings = new DockWindowSettings();
        var workspace = settings.CreateWorkspace("Only");

        Assert.False(settings.DeleteWorkspace(workspace.Id));
        Assert.False(workspace.Deleted);
        Assert.Equal(workspace.Id, settings.ActiveWorkspaceId);
    }

    [Fact]
    public void MovePanelToWorkspace_MovesMembershipAndPreservesSavedPlacement()
    {
        var settings = new DockWindowSettings();
        var source = settings.CreateWorkspace("Dashboard");
        source.Panels["dps"] = new DockPanelSettings
        {
            DockLeft = 120,
            DockTop = 80,
            DockWidth = 500,
            DockHeight = 300,
        };
        var target = settings.CreateWorkspace("Raid");
        settings.ActiveWorkspaceId = source.Id;

        settings.MovePanelToWorkspace("dps", target.Id, new DockPanelSettings());

        Assert.DoesNotContain("dps", source.Panels.Keys);
        Assert.Equal(120, target.Panels["dps"].DockLeft);
        Assert.Equal(80, target.Panels["dps"].DockTop);
        Assert.Equal(500, target.Panels["dps"].DockWidth);
        Assert.Equal(target.Id, settings.ActiveWorkspaceId);
    }

    [Fact]
    public void ActiveWorkspace_RecoversWhenSavedWorkspaceIdIsStale()
    {
        var workspace = new DockWorkspaceSettings { Name = "Raid" };
        var settings = new DockWindowSettings
        {
            Workspaces = [workspace],
            ActiveWorkspaceId = "missing-workspace",
        };

        Assert.Same(workspace, settings.ActiveWorkspace);
        Assert.Equal(workspace.Id, settings.ActiveWorkspaceId);
    }

    [Fact]
    public void ActiveWorkspace_CreatesDashboardWhenSavedWorkspacesAreAllDeleted()
    {
        var deleted = new DockWorkspaceSettings { Name = "Old", Deleted = true };
        var settings = new DockWindowSettings
        {
            Workspaces = [deleted],
            ActiveWorkspaceId = deleted.Id,
        };

        var active = settings.ActiveWorkspace;

        Assert.NotSame(deleted, active);
        Assert.Equal("Dashboard", active.Name);
        Assert.False(active.Deleted);
        Assert.Equal(active.Id, settings.ActiveWorkspaceId);
    }

    [Fact]
    public void Clone_CopiesWorkspacePanelsWithoutSharingMutablePlacements()
    {
        var settings = new DockWindowSettings();
        var workspace = settings.CreateWorkspace("Raid");
        workspace.Panels["dps"] = new DockPanelSettings { DockLeft = 10 };

        var clone = settings.Clone();
        clone.Workspaces[0].Panels["dps"].DockLeft = 99;

        Assert.Equal(10, settings.Workspaces[0].Panels["dps"].DockLeft);
    }

    [Fact]
    public void AppendPosition_PlacesNewPanelBelowAllExistingPanels()
    {
        var existing = new[]
        {
            new Rect(4, 4, 300, 200),
            new Rect(320, 4, 150, 100),
            new Rect(4, 212, 300, 200),
        };

        Point position = DockWindow.AppendPosition(existing);

        Assert.Equal(new Point(4, 420), position);
        var newPanel = new Rect(position, new Size(280, 180));
        var packed = DockWindow.Pack(
            [
                ("first", existing[0]),
                ("second", existing[1]),
                ("third", existing[2]),
                ("new", newPanel),
            ],
            ("new", newPanel));

        Assert.Equal(newPanel, packed["new"]);
        Assert.All(existing.Select((_, index) => packed[index switch
        {
            0 => "first",
            1 => "second",
            _ => "third",
        }]), rect => Assert.True(rect.Bottom <= newPanel.Top));
    }

    [Fact]
    public void UndockedPlacement_SurvivesSavingAndRestoresPositionAndSize()
    {
        var settings = new DockWindowSettings();
        var workspace = settings.CreateWorkspace("Dashboard");
        workspace.Panels["dps"] = new DockPanelSettings
        {
            DockLeft = 120,
            DockTop = 80,
            DockWidth = 500,
            DockHeight = 300,
        };

        settings.MovePanelToWorkspace("dps", null, new DockPanelSettings());
        Assert.False(settings.IsPanelInAnyWorkspace("dps"));
        var loaded = JsonSerializer.Deserialize<DockWindowSettings>(JsonSerializer.Serialize(settings))!;
        loaded.ActiveWorkspace.DockPanel("dps", new DockPanelSettings { DockLeft = 4, DockTop = 4 });

        var placement = loaded.ActiveWorkspace.Panels["dps"];
        Assert.Equal(120, placement.DockLeft);
        Assert.Equal(80, placement.DockTop);
        Assert.Equal(500, placement.DockWidth);
        Assert.Equal(300, placement.DockHeight);
        Assert.Empty(loaded.ActiveWorkspace.UndockedPanels);
    }

    [Fact]
    public void UndockedPlacements_AreIndependentPerWorkspaceAndCloned()
    {
        var settings = new DockWindowSettings();
        var first = settings.CreateWorkspace("First");
        var second = settings.CreateWorkspace("Second");
        first.Panels["dps"] = new DockPanelSettings { DockLeft = 120 };
        second.Panels["dps"] = new DockPanelSettings { DockLeft = 420 };
        first.UndockPanel("dps");
        second.UndockPanel("dps");

        var clone = settings.Clone();
        clone.Workspaces[0].UndockedPanels["dps"].DockLeft = 999;
        first.DockPanel("dps", new DockPanelSettings());
        second.DockPanel("dps", new DockPanelSettings());

        Assert.Equal(120, first.Panels["dps"].DockLeft);
        Assert.Equal(420, second.Panels["dps"].DockLeft);
        Assert.Equal(999, clone.Workspaces[0].UndockedPanels["dps"].DockLeft);
    }

    [Theory]
    [InlineData(false, 120, 80)]
    [InlineData(true, 4, 212)]
    public void Redocking_RestoresManualPositionOrAppendsWhenAutoArrangeIsEnabled(
        bool autoArrange, double expectedLeft, double expectedTop)
    {
        var workspace = new DockWorkspaceSettings();
        workspace.Panels["dps"] = new DockPanelSettings
        {
            DockLeft = 120,
            DockTop = 80,
            DockWidth = 500,
            DockHeight = 300,
        };
        workspace.UndockPanel("dps");
        workspace.DockPanel("dps", new DockPanelSettings());
        var placement = workspace.Panels["dps"];

        var position = DockWindow.RedockPosition(placement, appendRequested: true, autoArrange,
            [new Rect(4, 4, 300, 200)]);

        Assert.Equal(new Point(expectedLeft, expectedTop), position);
        Assert.Equal(500, placement.DockWidth);
        Assert.Equal(300, placement.DockHeight);
    }
}
