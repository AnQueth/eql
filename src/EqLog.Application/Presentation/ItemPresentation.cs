using EqLog.Core;

namespace EqLog.Application.Presentation;

/// <summary>Shared item identity and navigation queries for presentation, independent of views and adapters.</summary>
public static class ItemPresentation
{
    public static IEqualityComparer<string> Comparer { get; } = ItemNames.Comparer;

    public static string Name(string item)
    {
        return ItemNames.Normalize(item);
    }

    public static string Normalize(string item)
    {
        return Name(item);
    }

    public static (string BaseName, int Rank) Rank(string item)
    {
        return InventoryExport.SplitRank(item);
    }
}

public static class WikiLinks
{
    public static string ItemTitle(string item)
    {
        return QuestLookup.ItemTitle(item);
    }

    public static string ItemUrl(string item)
    {
        return QuestLookup.ItemUrl(item);
    }

    public static string PageUrl(string item)
    {
        return QuestLookup.PageUrl(item);
    }

    public static string SearchUrl(string item)
    {
        return QuestLookup.SearchUrl(item);
    }
}
