using EqLog.Core;

namespace EqLog.Application.Inventory;

public sealed record SkyItemProgress(string Item, int Required, int Owned, int Carried, int Bank, int Historical,
    int Collection, int Installed, int Hoard, int Depot, int OtherStorage, bool Satisfied);

public sealed record SkyNeededItem(string Item, string Label, int Owned, int Required, IReadOnlyList<SkyQuest> Quests)
{
    public int Missing { get; } = Math.Max(0, Required - Owned);
}
public sealed record SkyNeededGroup(string Mob, IReadOnlyList<SkyNeededItem> Items);

/// <summary>Quest progress, item identities, filtering and combined shortfalls computed before presentation.</summary>
public static class SkyQueries
{
    public const string SourceUrl = SkyQuests.SourceUrl;

    public static string Key(SkyQuest quest)
    {
        return SkyQuests.Key(quest);
    }

    public static string Label(SkyQuest quest, string item)
    {
        return SkyQuests.Label(quest, item);
    }

    public static bool Matches(SkyQuest quest, string filter, SkyQuestCatalog catalog)
    {
        return SkyQuests.Matches(quest, filter, catalog);
    }

    public static IReadOnlyList<SkyDropGroup> ByDropper(IEnumerable<SkyQuest> quests, SkyQuestCatalog catalog, string? filter = null)
    {
        return SkyQuests.ByDropper(quests, catalog, filter);
    }

    public static IReadOnlyDictionary<SkyQuest, IReadOnlyList<SkyItemProgress>> ProgressAll(
        IEnumerable<SkyQuest> quests, InventoryReadModel inventory, string character)
    {
        return inventory.SkyProgress(quests, character);
    }

    public static IReadOnlyList<string> DroppedBy(SkyQuestCatalog catalog, string item)
    {
        return catalog.DroppedBy(item);
    }

    public static IReadOnlyList<SkyNeededGroup> Needed(IReadOnlyList<SkyDropGroup> groups,
        IReadOnlyDictionary<SkyQuest, IReadOnlyList<SkyItemProgress>> progress)
    {
        return groups.Select(group => new SkyNeededGroup(group.Mob, group.Items.Select(item =>
        {
            var entries = item.Quests.Select(quest => progress[quest].First(entry =>
                ItemNames.Comparer.Equals(entry.Item, item.Item))).ToArray();
            return new SkyNeededItem(item.Item, item.Label, entries[0].Owned, entries.Sum(entry => entry.Required), item.Quests);
        }).Where(item => item.Missing > 0).ToArray())).Where(group => group.Items.Count > 0).ToArray();
    }
}
