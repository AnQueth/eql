using EqLog.Core;

namespace EqLog.Infrastructure.Wiki;

public sealed class ItemEffectClient(HttpClient http)
{
    public async Task<ItemEffectInfo> FindAsync(string item, CancellationToken token = default)
    {
        item = ItemNames.Normalize(item);
        var pages = new MobWikiClient(http);
        var page = await pages.FindPageAsync(item, token).ConfigureAwait(false);
        if (page is null)
        {
            return new(item, QuestLookup.PageUrl(item), DateTime.UtcNow, false, [], "");
        }
        var parsed = ItemEffectLookup.Parse(item, page.Value.Title, page.Value.Text, DateTime.UtcNow);
        var effects = new List<ItemEffect>();
        foreach (var effect in parsed.Effects)
        {
            if (effect.Url is null)
            {
                effects.Add(effect);
                continue;
            }
            string title = Uri.UnescapeDataString(new Uri(effect.Url).AbsolutePath.TrimStart('/')).Replace('_', ' ');
            var spell = await pages.FindPageAsync(title, token).ConfigureAwait(false);
            effects.Add(ItemEffectLookup.DescribeEffect(effect, spell));
        }
        return parsed with
        {
            Effects = effects
        };
    }
}
