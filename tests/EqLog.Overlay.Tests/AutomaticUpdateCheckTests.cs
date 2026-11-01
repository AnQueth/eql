using System.Text.Json;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class AutomaticUpdateCheckTests
{
    [Fact]
    public void Interval_IsExactlyFourHours()
    {
        Assert.Equal(TimeSpan.FromHours(4), MainWindow.AutomaticUpdateCheckInterval);
    }

    [Theory]
    [InlineData(true, true, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false, false)]
    [InlineData(true, true, false, true, false, false, false)]
    [InlineData(true, true, false, false, true, false, false)]
    [InlineData(true, true, false, false, false, true, false)]
    public void AutomaticChecks_RespectPreferenceInstallationAndUpdateState(
        bool enabled, bool installed, bool debugging, bool busy, bool downloaded,
        bool shuttingDown, bool expected)
    {
        Assert.Equal(expected, MainWindow.CanAutomaticallyCheckForUpdates(
            enabled, installed, debugging, busy, downloaded, shuttingDown));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingAutomaticCheckPreference_IsPreserved(bool enabled)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            $$"""{"CheckForUpdatesOnStartup": {{enabled.ToString().ToLowerInvariant()}}}""",
            AppSettings.Json)!;
        var saved = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings.Snapshot(), AppSettings.Json), AppSettings.Json)!;

        Assert.Equal(enabled, saved.CheckForUpdatesOnStartup);
        Assert.Equal(enabled, MainWindow.CanAutomaticallyCheckForUpdates(
            saved.CheckForUpdatesOnStartup, true, false, false, false, false));
    }
}
