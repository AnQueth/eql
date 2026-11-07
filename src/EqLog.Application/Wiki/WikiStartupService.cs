using System.Text.Json;
using EqLog.Core;

namespace EqLog.Application.Wiki;

public sealed record WikiCacheLoad<T>(T? Value, Exception? Error);
public sealed record WikiStartup(WikiCacheLoad<IReadOnlyList<QuestLookupResult>> Quests,
    WikiCacheLoad<IReadOnlyList<ItemEffectInfo>> Effects, WikiCacheLoad<SkyQuestCatalog?> Sky,
    IReadOnlyList<string> Warnings, WikiCacheLoad<IReadOnlyList<TradeskillWikiPage>> Tradeskills);

public sealed class WikiStartupService(IWikiCacheRepository repository)
{
    public async Task<WikiStartup> LoadAsync()
    {
        var quests = AttemptAsync(repository.LoadQuestsAsync());
        var effects = AttemptAsync(repository.LoadItemEffectsAsync());
        var sky = AttemptAsync(repository.LoadSkyAsync());
        var tradeskills = AttemptAsync(repository.LoadTradeskillsAsync());
        await Task.WhenAll(quests, effects, sky, tradeskills).ConfigureAwait(false);
        return new(quests.Result, effects.Result, sky.Result, repository.LoadWarnings, tradeskills.Result);
    }

    private static async Task<WikiCacheLoad<T>> AttemptAsync<T>(Task<T> load)
    {
        try
        {
            return new(await load.ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return new(default, ex);
        }
    }
}
