using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EqLog.Core;
using EqLog.Overlay;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class UsageTelemetryTests : IDisposable
{
    private readonly RecordingLoggerFactory _factory = new();

    public UsageTelemetryTests()
    {
        UsageTelemetry.SetEnabled(true, () => _factory);
    }

    public void Dispose()
    {
        UsageTelemetry.Shutdown();
    }

    [Fact]
    public void SetEnabled_Disabled_DoesNotCollectOrCreateExporter()
    {
        UsageTelemetry.SetEnabled(false);
        UsageTelemetry.SetEnabled(false, () => throw new InvalidOperationException("Must not create exporter"));

        UsageTelemetry.TrackApplicationStarted();
        UsageTracking.Track(UsageAction.ResetSession);
        UsageTelemetry.TrackPanelState("chat", true, false, changed: false);
        UsageTelemetry.TrackTheme(Palette("Private name"), false, [], changed: false);
        UsageTelemetry.TrackError(new IOException("Private path"), UsageErrorSource.Startup);

        Assert.Empty(_factory.Events);
        Assert.True(_factory.Disposed);
    }

    [Fact]
    public void SetEnabled_Reenabled_StartsNewAnonymousSession()
    {
        UsageTelemetry.TrackApplicationStarted();
        string previous = Assert.IsType<string>(_factory.Events.Single()["session.id"]);
        UsageTelemetry.SetEnabled(false);
        UsageTelemetry.SetEnabled(true, () => _factory);
        UsageTelemetry.TrackApplicationStarted();

        Assert.NotEqual(previous, _factory.Events.Last()["session.id"]);
        Assert.Equal(32, previous.Length);
    }

    [Theory]
    [InlineData(true, true, false, "panel_state", "Docked")]
    [InlineData(true, false, true, "panel_changed", "Floating")]
    [InlineData(false, true, true, "panel_changed", "Docked")]
    public void TrackPanelState_Configuration_ReportsPlacementAndVisibility(
        bool visible, bool docked, bool changed, string eventName, string placement)
    {
        UsageTelemetry.TrackPanelState("dps", visible, docked, changed);

        var properties = Assert.Single(_factory.Events);
        Assert.Equal(eventName, properties["microsoft.custom_event.name"]);
        Assert.Equal("DpsPanel", properties["feature"]);
        Assert.Equal(visible, properties["panel.visible"]);
        Assert.Equal(placement, properties["panel.placement"]);
        Assert.Equal(6, properties.Count);
        Assert.False(string.IsNullOrEmpty(Assert.IsType<string>(properties["app.version"])));
    }

    [Fact]
    public void TrackPanelState_UnknownKey_DoesNotSendKey()
    {
        UsageTelemetry.TrackPanelState("Private character name", false, false, changed: false);

        Assert.Equal("OtherPanel", Assert.Single(_factory.Events)["feature"]);
        Assert.DoesNotContain(_factory.Events.Single().Values, value => Equals(value, "Private character name"));
    }

    [Theory]
    [InlineData("Neutral Dark", false, "Neutral Dark", "Fixed")]
    [InlineData("Neutral Light", true, "Neutral Light", "Windows")]
    [InlineData("Private theme name", false, "Custom", "Fixed")]
    public void TrackTheme_ThemeChoice_UsesAllowlistedLabels(string name, bool windows, string label, string mode)
    {
        var theme = Palette(name);
        UsageTelemetry.TrackTheme(theme, windows, [theme.Clone()], changed: false);

        var properties = Assert.Single(_factory.Events);
        Assert.Equal("theme_state", properties["microsoft.custom_event.name"]);
        Assert.Equal(label, properties["theme"]);
        Assert.Equal(mode, properties["theme.mode"]);
        Assert.Equal(5, properties.Count);
    }

    [Fact]
    public void TrackTheme_EditedBuiltInName_IsCustom()
    {
        var bundled = Palette("Neutral Dark");
        var edited = bundled.Clone();
        edited.Colors["NightText"] = "#123456";

        UsageTelemetry.TrackTheme(edited, false, [bundled], changed: true);

        var properties = Assert.Single(_factory.Events);
        Assert.Equal("Custom", properties["theme"]);
        Assert.Equal("theme_changed", properties["microsoft.custom_event.name"]);
    }

    [Fact]
    public void TrackTheme_AllShippedPalettes_AreRecognized()
    {
        var files = Directory.GetFiles(ThemeManager.BuiltInFolder, "*.json");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            var theme = ThemePalette.Parse(File.ReadAllText(file));
            UsageTelemetry.TrackTheme(theme, false, [theme.Clone()], changed: false);
            Assert.Equal(theme.Name, _factory.Events.Last()["theme"]);
        }
    }

    [Fact]
    public void TrackPanelState_AllPanelDefinitions_HaveSpecificLabels()
    {
        foreach (var panel in OverlayPresenters.All)
        {
            UsageTelemetry.TrackPanelState(panel.Key, true, true, changed: false);
            Assert.NotEqual("OtherPanel", _factory.Events.Last()["feature"]);
        }
        Assert.Equal(OverlayPresenters.All.Count, _factory.Events.Count);
        Assert.Equal(_factory.Events.Count, _factory.Events.Select(properties => properties["feature"]).Distinct().Count());
    }

    [Fact]
    public void TrackError_Exception_DoesNotSendMessageOrStack()
    {
        UsageTelemetry.TrackError(new IOException("Private path and log contents"), UsageErrorSource.InventoryImport);

        var properties = Assert.Single(_factory.Events);
        Assert.Equal("IOException", properties["error.type"]);
        Assert.Equal("InventoryImport", properties["error.source"]);
        Assert.Equal(5, properties.Count);
    }

    [Fact]
    public void TrackAction_InvalidOrNone_DoesNotEmit()
    {
        UsageTracking.Track(UsageAction.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => UsageTracking.Track((UsageAction)9999));
        Assert.Empty(_factory.Events);
    }

    [Fact]
    public void Action_ClickHandledByExistingHandler_IsTrackedExactlyOnce()
    {
        OnStaThread(() =>
        {
            var button = new Button { Content = "Private item name" };
            int existingHandlerCalls = 0;
            button.Click += (_, e) =>
            {
                existingHandlerCalls++;
                e.Handled = true;
            };
            UsageTracking.SetAction(button, UsageAction.ResetSession);
            UsageTracking.SetAction(button, UsageAction.AddTrigger);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            UsageTracking.SetAction(button, UsageAction.None);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

            Assert.Equal(2, existingHandlerCalls);
        });

        var properties = Assert.Single(_factory.Events);
        Assert.Equal("feature_action", properties["microsoft.custom_event.name"]);
        Assert.Equal("AddTrigger", properties["action"]);
        Assert.Equal(4, properties.Count);
    }

    [Fact]
    public void Action_CheckBoxClick_TracksWithoutCapturingContent()
    {
        OnStaThread(() =>
        {
            var checkbox = new CheckBox { Content = "Private name" };
            UsageTracking.SetAction(checkbox, UsageAction.ToggleAutoArrange);
            checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, checkbox));
        });

        Assert.Equal("ToggleAutoArrange", Assert.Single(_factory.Events)["action"]);
    }

    [Fact]
    public void Action_NestedButton_DoesNotTrackParentAction()
    {
        OnStaThread(() =>
        {
            var child = new Button();
            var parent = new Button { Content = child };
            UsageTracking.SetAction(parent, UsageAction.ResetSession);
            UsageTracking.SetAction(child, UsageAction.AddTrigger);
            child.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, child));
        });

        Assert.Equal("AddTrigger", Assert.Single(_factory.Events)["action"]);
    }

    private static ThemePalette Palette(string name)
    {
        return new ThemePalette
        {
            Name = name,
            Colors = ThemePalette.AllColorKeys.ToDictionary(key => key, _ => "#FFFFFF"),
        };
    }

    private static void OnStaThread(Action action)
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

    private sealed class RecordingLoggerFactory : ILoggerFactory, ILogger
    {
        public List<Dictionary<string, object?>> Events { get; } = [];
        public bool Disposed
        {
            get; private set;
        }

        public void AddProvider(ILoggerProvider provider)
        {
            throw new NotSupportedException();
        }

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Events.Add(properties.ToDictionary());
        }
    }
}
