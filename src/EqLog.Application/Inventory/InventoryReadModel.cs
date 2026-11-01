using EqLog.Core;
using EqLog.Application.Motes;

namespace EqLog.Application.Inventory;

/// <summary>Detached inventory query boundary. Its private domain snapshot cannot be mutated by a view.</summary>
public sealed class InventoryReadModel
{
    public static InventoryReadModel Empty { get; } = new(new QuestInventory());
    private readonly QuestInventory _state;

    public InventoryReadModel(QuestInventory state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state.Clone();
        Items = Array.AsReadOnly(_state.Items.Select(item => new InventoryItemSummary(item.Character, item.Item,
            item.Owned, item.Historical, item.Carried, item.Bank, item.Hoard, item.Depot, item.OtherStorage,
            item.Collection, item.Installed, item.LastLootUtc)).ToArray());
        RecentUpgrades = Array.AsReadOnly(_state.RecentUpgrades.ToArray());
        ExportTimes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, DateTime>(_state.ExportTimes);
    }

    public IReadOnlyList<InventoryItemSummary> Items
    {
        get;
    }
    public IReadOnlyList<InventoryUpgradeEvent> RecentUpgrades
    {
        get;
    }
    public IReadOnlyDictionary<string, DateTime> ExportTimes
    {
        get;
    }

    public MoteInventoryReadModel Motes(string? character)
    {
        DateTime? exported = character is not null && ExportTimes.TryGetValue(character.ToUpperInvariant(), out var at) ? at : null;
        return MoteInventoryReadModel.Capture(Items, character, exported);
    }

    public IReadOnlyList<QuestOpportunity> Opportunities(string character,
        IReadOnlyDictionary<string, QuestLookupResult> cache, string filter, bool recentFirst)
    {
        return _state.Opportunities(character, cache, filter, recentFirst).ToArray();
    }

    public IReadOnlyDictionary<SkyQuest, IReadOnlyList<SkyItemProgress>> SkyProgress(IEnumerable<SkyQuest> quests, string character)
    {
        return SkyQuests.ProgressAll(quests, _state, character).ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<SkyItemProgress>)pair.Value.Select(item => new SkyItemProgress(item.Item,
                item.Required, item.Owned, item.Carried, item.Bank, item.Historical, item.Collection, item.Installed,
                item.Hoard, item.Depot, item.OtherStorage, item.Satisfied)).ToArray());
    }
}
