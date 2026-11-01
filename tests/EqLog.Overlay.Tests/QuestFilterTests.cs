using EqLog.Application.Inventory;
using EqLog.Core;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class QuestFilterTests
{
    [Fact]
    public void BackgroundBuilderPreservesFilteringOrderAndOwnedColumns()
    {
        var state = new QuestInventory();
        var at = DateTime.UtcNow;
        state.NoteLoot("Que", "log", 1, "Old Fang", 1, false, at.AddMinutes(-1));
        state.NoteLoot("Que", "log", 2, "New Mask +1", 1, true, at);
        var inventory = new InventoryReadModel(state);
        var cache = new Dictionary<string, QuestLookupResult>
        {
            ["Old Fang"] = new("Old Fang", at, [new("Alpha quest", "url")]),
            ["New Mask"] = new("New Mask", at, [new("Zulu quest", "url")]),
        };

        var recent = MainWindow.BuildQuestOpportunities(inventory, "Que", cache, "", true, CancellationToken.None);
        Assert.Equal(["Zulu quest", "Alpha quest"], recent.Rows.Where(row => row.QuestHeading).Select(row => row.Left));
        var filtered = MainWindow.BuildQuestOpportunities(inventory, "Que", cache, " MASK ", true, CancellationToken.None);
        Assert.Equal("Zulu quest", filtered.Rows[0].Left);
        Assert.Equal("New Mask +1", filtered.Rows[1].LootItem);
        Assert.NotNull(filtered.Rows[1].Table);
        Assert.NotNull(filtered.Rows[0].Link);
        var alphabetical = MainWindow.BuildQuestOpportunities(inventory, "Que", cache, "", false, CancellationToken.None);
        Assert.Equal("Alpha quest", alphabetical.Rows[0].Left);
        var missing = MainWindow.BuildQuestOpportunities(inventory, "Other", cache, "missing", true, CancellationToken.None);
        Assert.Equal("No quests or items match your filter.", Assert.Single(missing.Rows).Left);
    }

    [Fact]
    public void CancelledRequestsDoNotBuildRows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MainWindow.BuildQuestOpportunities(
            InventoryReadModel.Empty, "Que", new Dictionary<string, QuestLookupResult>(), "", true, cancellation.Token));
    }
}
