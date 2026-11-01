using System.IO;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Infrastructure.GameFiles;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Overlay;

/// <summary>The inventory composition root; only this file knows its concrete input/storage adapters.</summary>
internal static class InventoryComposition
{
    private static IQuestInventoryRepository CreateRepository()
    {
        return new QuestInventoryRepository(Path.Combine(SettingsComposition.Paths.Folder, "quest-inventory.json"));
    }

    internal static Task<QuestInventory> LoadAsync()
    {
        return CreateRepository().LoadAsync();
    }

    internal static InventoryService CreateService(QuestInventory state, Exception? loadError)
    {
        return new(new InventoryExportSource(), CreateRepository(), state, loadError);
    }
}
