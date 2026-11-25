using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class WikiIconProviderTests
{
    [Fact]
    public void SharesIconsWaitsWhilePausedAndDoesNotReloadResolvedIcons()
    {
        int loads = 0;
        bool paused = true;
        var provider = new WikiIconProvider((_, _) =>
        {
            loads++;
            return Task.FromResult<string?>(null);
        }, () => paused);
        var key = new WikiIconKey(WikiIconKind.Mob, "Cazic Thule");

        var icon = provider.Get(key);
        Assert.Equal(0, loads);
        paused = false;
        provider.ResumePending();
        Assert.Same(icon, provider.Get(new WikiIconKey(WikiIconKind.Mob, "cazic thule")));

        Assert.Equal(1, loads);
        Assert.False(icon.HasImage);
        provider.Reset();
        provider.ResumePending();
        Assert.Equal(2, loads);
    }
}
