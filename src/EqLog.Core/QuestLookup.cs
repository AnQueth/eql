namespace EqLog.Core;

public sealed record QuestReference(string Title, string Url);
public sealed record QuestLookupResult(string Item, DateTime CheckedUtc, IReadOnlyList<QuestReference> Quests, bool Found = true)
{
    /// <summary>Whether the wiki found an item page with at least one linked quest reference.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasQuestReferences
    {
        get
        {
            return Found && Quests.Count > 0;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsRefresh
    {
        get
        {
            return DateTime.UtcNow - CheckedUtc >= TimeSpan.FromDays(Found ? 7 : 1);
        }
    }
}

/// <summary>Finds quest pages that link to or transclude an exact item page on EQLWiki.</summary>
public static class QuestLookup
{
    public static string ItemTitle(string item)
    {
        return ItemNames.Normalize(item);
    }

    public static string PageUrl(string title)
    {
        return "https://eqlwiki.com/" + Uri.EscapeDataString(title.Replace(' ', '_'));
    }

    public static string ItemUrl(string item)
    {
        return PageUrl(ItemTitle(item));
    }

    /// <summary>
    /// Wiki "go" search: opens the page directly when a title matches (MediaWiki also tries capitalisation
    /// variants such as "a gnoll pup" / "A Gnoll Pup") and falls back to search results otherwise. Used for NPCs,
    /// whose log names don't always match the page title exactly.
    /// </summary>
    public static string SearchUrl(string name)
    {
        return "https://eqlwiki.com/index.php?title=Special:Search&go=Go&search=" + Uri.EscapeDataString(name.Trim());
    }

}
