using EqLog.Core;

namespace EqLog.Application.Wiki;

/// <summary>Cache-state queries evaluate domain freshness outside presentation.</summary>
public static class WikiQueries
{
    public static bool HasQuestReferences(QuestLookupResult? result)
    {
        return result?.HasQuestReferences == true;
    }

    public static bool NeedsRefresh(QuestLookupResult result)
    {
        return result.NeedsRefresh;
    }

    public static bool NeedsRefresh(ItemEffectInfo result)
    {
        return result.NeedsRefresh;
    }
}
