using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using EqLog.Core;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class SkyClassSelectorTests
{
    [Fact]
    public void PopulateSkyClasses_UsesCachedClassesAndPreservesSelectionAcrossRefresh()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var selector = new ComboBox();
                var catalog = new SkyQuestCatalog(DateTime.UtcNow,
                    [Quest("Wizard"), Quest("Bard"), Quest("wizard"), Quest("Cleric")]);
                MainWindow.PopulateSkyClasses(selector, catalog, "wizard");
                Assert.Equal(["All classes", "Bard", "Cleric", "Wizard"], selector.Items.Cast<string>());
                Assert.Equal("Wizard", selector.SelectedItem);

                MainWindow.PopulateSkyClasses(selector, catalog, (string)selector.SelectedItem);
                Assert.Equal("Wizard", selector.SelectedItem);
                MainWindow.PopulateSkyClasses(selector, catalog, "Missing");
                Assert.Equal("All classes", selector.SelectedItem);

                MainWindow.PopulateSkyClasses(selector, null, "Bard");
                Assert.Equal(["All classes", "Bard"], selector.Items.Cast<string>());
                Assert.Equal("Bard", selector.SelectedItem);
                MainWindow.PopulateSkyClasses(selector, catalog, (string)selector.SelectedItem);
                Assert.Equal("Bard", selector.SelectedItem);
                Assert.Equal(4, selector.Items.Count);
            }
            catch (Exception error)
            {
                failure = error;
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

    private static SkyQuest Quest(string className)
    {
        return new(className, "Test", "Reward", "Giver", "", "", "", "url");
    }
}
