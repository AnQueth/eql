using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class OverlaySettingsOrderTests
{
    [Fact]
    public void SettingsOverlayDefinitions_SortsEveryOverlayByCaptionWithoutChangingRuntimeOrder()
    {
        var original = OverlayPresenters.All.ToArray();
        var settings = MainWindow.SettingsOverlayDefinitions();

        Assert.Equal(original.Length, settings.Count);
        Assert.Equal(original.OrderBy(def => def.Caption, StringComparer.CurrentCultureIgnoreCase), settings);
        Assert.Equal(original, OverlayPresenters.All);
        Assert.Equal(original.Select(def => def.Key).Order(), settings.Select(def => def.Key).Order());
    }
}
