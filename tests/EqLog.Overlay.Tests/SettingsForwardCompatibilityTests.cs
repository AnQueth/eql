using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class SettingsForwardCompatibilityTests
{
    [Fact]
    public void Snapshot_PreservesIndependentHoverOpacityAcrossSerialization()
    {
        var settings = new AppSettings
        {
            Overlays = new()
            {
                ["dps"] = new OverlayPlacement { BackgroundOpacity = 0.2, HoverBackgroundOpacity = 0.9 },
                ["loot"] = new OverlayPlacement { BackgroundOpacity = 0.4, HoverBackgroundOpacity = 0.6 },
            },
        };

        var loaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings.Snapshot(), AppSettings.Json), AppSettings.Json)!;

        Assert.Equal(0.2, loaded.Overlays["dps"].BackgroundOpacity);
        Assert.Equal(0.9, loaded.Overlays["dps"].HoverBackgroundOpacity);
        Assert.Equal(0.4, loaded.Overlays["loot"].BackgroundOpacity);
        Assert.Equal(0.6, loaded.Overlays["loot"].HoverBackgroundOpacity);
        Assert.Null(JsonSerializer.Deserialize<OverlayPlacement>("{}")!.HoverBackgroundOpacity);
    }

    private const string NewerVersionSettings = """
        {
          "FontSize": 15,
          "FutureTopLevel": { "Enabled": true },
          "Theme": { "Name": "Custom", "Colors": {}, "FutureThemeOption": 3 },
          "Overlays": {
            "dps": { "Left": 10, "Top": 20, "FutureOverlayOption": "keep" }
          },
          "DockWindow": {
            "Width": 900,
            "FutureDockOption": [1, 2, 3],
            "ActiveWorkspace": { "Id": "stale" },
            "Workspaces": [
              {
                "Id": "w1",
                "Name": "Raid",
                "FutureWorkspaceOption": "pinned",
                "Panels": { "dps": { "DockWidth": 400, "FuturePanelOption": 7 } }
              }
            ]
          },
          "Triggers": [ { "Name": "Tell", "FutureTriggerOption": "voice" } ]
        }
        """;

    [Fact]
    public void Save_PreservesSettingsUnknownToThisVersion()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(NewerVersionSettings, AppSettings.Json)!;
        settings.FontSize = 16;

        var saved = JsonNode.Parse(JsonSerializer.Serialize(settings.Snapshot(), AppSettings.Json))!;

        Assert.Equal(16, saved["FontSize"]!.GetValue<double>());
        Assert.True(saved["FutureTopLevel"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(3, saved["Theme"]!["FutureThemeOption"]!.GetValue<int>());
        Assert.Equal("keep", saved["Overlays"]!["dps"]!["FutureOverlayOption"]!.GetValue<string>());
        Assert.Equal(3, saved["DockWindow"]!["FutureDockOption"]!.AsArray().Count);
        var workspace = saved["DockWindow"]!["Workspaces"]![0]!;
        Assert.Equal("pinned", workspace["FutureWorkspaceOption"]!.GetValue<string>());
        Assert.Equal(7, workspace["Panels"]!["dps"]!["FuturePanelOption"]!.GetValue<int>());
        Assert.Equal("voice", saved["Triggers"]![0]!["FutureTriggerOption"]!.GetValue<string>());
    }

    [Fact]
    public void Save_DoesNotWriteComputedActiveWorkspace()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(NewerVersionSettings, AppSettings.Json)!;

        var saved = JsonNode.Parse(JsonSerializer.Serialize(settings.Snapshot(), AppSettings.Json))!;

        Assert.Null(saved["DockWindow"]!["ActiveWorkspace"]);
    }

    [Fact]
    public void Snapshot_CopiesUnknownSettingsIndependently()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(NewerVersionSettings, AppSettings.Json)!;

        var snapshot = settings.Snapshot();
        settings.ExtensionData!.Remove("FutureTopLevel");
        settings.DockWindow.Workspaces[0].ExtensionData!.Clear();

        Assert.True(snapshot.ExtensionData!.ContainsKey("FutureTopLevel"));
        Assert.True(snapshot.DockWindow.Workspaces[0].ExtensionData!.ContainsKey("FutureWorkspaceOption"));
    }
}
