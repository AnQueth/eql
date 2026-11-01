using EqLog.Application.Inventory;
using EqLog.Application.Sessions;
using EqLog.Infrastructure.GameFiles;

namespace EqLog.Overlay;

internal static class LogSessionComposition
{
    internal static string DefaultDirectory
    {
        get
        {
            return LogFileLocator.DefaultDirectory;
        }
    }

    internal static LogSessionService Create(InventoryService inventory)
    {
        var source = new GameLogSource();
        return new(source, source, inventory);
    }
}
