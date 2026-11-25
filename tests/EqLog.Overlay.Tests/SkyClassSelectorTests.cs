using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Verifies the Sky class options exposed by the overlay view model.</summary>
public sealed class SkyClassSelectorTests
{
    /// <summary>Class choices are stable, sorted and retain the user's class selection.</summary>
    [Fact]
    public void BuildClassOptionsUsesCachedClassesAndPreservesSelectionAcrossRefresh()
    {
        var catalog = new SkyQuestCatalog(DateTime.UtcNow,
            [Quest("Wizard"), Quest("Bard"), Quest("wizard"), Quest("Cleric")]);
        IReadOnlyList<string> options = SkyOverlayViewModel.BuildClassOptions(catalog, "wizard");

        Assert.Equal(["All classes", "Bard", "Cleric", "Wizard"], options);
        Assert.Equal("Wizard", options[3]);
        Assert.Equal("All classes", SkyOverlayViewModel.BuildClassOptions(catalog, "Missing")[0]);

        Assert.Equal(["All classes", "Bard"], SkyOverlayViewModel.BuildClassOptions(null, "Bard"));
        Assert.Equal(4, SkyOverlayViewModel.BuildClassOptions(catalog, "Bard").Count);
    }

    private static SkyQuest Quest(string className)
    {
        return new SkyQuest(className, "Test", "Reward", "Giver", "", "", "", "url");
    }
}
