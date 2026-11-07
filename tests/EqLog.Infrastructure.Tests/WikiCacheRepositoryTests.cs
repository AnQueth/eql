using System.Text.Json;

namespace EqLog.Infrastructure.Tests;

public sealed class WikiCacheRepositoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eql-wiki-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("quest-lookups.jsonl", "not json")]
    [InlineData("item-effects.json", "null")]
    [InlineData("sky-quests.json", "{\"Quests\":[]}")]
    public async Task CorruptCacheIsReportedAndCannotBeOverwritten(string name, string damaged)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name);
        await File.WriteAllTextAsync(path, damaged);
        var repository = new WikiCacheRepository(_folder);
        if (name == "quest-lookups.jsonl")
        {
            await Assert.ThrowsAsync<JsonException>(() => repository.LoadQuestsAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveQuestAsync(Quest()));
        }
        else if (name == "item-effects.json")
        {
            await Assert.ThrowsAsync<JsonException>(() => repository.LoadItemEffectsAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SaveItemEffectsAsync(new("Item", "url", DateTime.UtcNow, true, [], "")));
        }
        else
        {
            await Assert.ThrowsAsync<JsonException>(() => repository.LoadSkyAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SaveSkyAsync(new(DateTime.UtcNow, [])));
        }
        Assert.Equal(damaged, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FailedWriteRemainsDirtyAndFlushRetriesWithoutRefetching()
    {
        await File.WriteAllTextAsync(_folder, "blocks directory creation");
        var repository = new WikiCacheRepository(_folder);
        var result = Quest();
        await Assert.ThrowsAnyAsync<IOException>(() => repository.SaveQuestAsync(result));
        File.Delete(_folder);
        Directory.CreateDirectory(_folder);
        await repository.FlushAsync();
        var loaded = await new WikiCacheRepository(_folder).LoadQuestsAsync();
        Assert.Equal(result.Item, Assert.Single(loaded).Item);
        Assert.Equal(result.CheckedUtc, loaded[0].CheckedUtc);
    }

    [Fact]
    public async Task OlderWritesCannotReplaceNewerResultsAndCacheHitsDoNotAppend()
    {
        var repository = new WikiCacheRepository(_folder);
        var newer = Quest();
        await repository.SaveQuestAsync(newer);
        await repository.SaveQuestAsync(newer with
        {
            CheckedUtc = newer.CheckedUtc.AddDays(-1)
        });
        await repository.SaveQuestAsync(newer);
        await repository.FlushAsync();
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_folder, "quest-lookups.jsonl")));
        Assert.Equal(newer.CheckedUtc, Assert.Single(await repository.LoadQuestsAsync()).CheckedUtc);
    }

    [Fact]
    public async Task ResetPreventsDirtyResultsFromReturningOnLaterFlush()
    {
        var repository = new WikiCacheRepository(_folder);
        await repository.SaveQuestAsync(Quest());
        await repository.ResetAsync(new());
        await repository.FlushAsync();
        Assert.False(File.Exists(Path.Combine(_folder, "quest-lookups.jsonl")));
    }

    [Fact]
    public async Task TradeskillCacheRoundTripsAllKindsAndResetDoesNotRestoreDirtyPages()
    {
        var repository = new WikiCacheRepository(_folder);
        var ingredient = new TradeskillIngredient("Test Leaf", "Test Leaf", 2, "Bought");
        var recipe = new TradeskillRecipe("key", "Practice Combine", "Practice Potion", "Practice Potion",
            "Alchemy", "25", "Test Bag", 1, [ingredient]);
        var merchant = new TradeskillMerchant("Test Merchant", "Test Merchant", "Halas", "Halas", "Shop", "(12, -34)");
        await repository.SaveTradeskillAsync(new(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow, [recipe], []));
        await repository.SaveTradeskillAsync(new(TradeskillPageKind.Recipe, "Practice Potion", DateTime.UtcNow, [recipe], []));
        await repository.SaveTradeskillAsync(new(TradeskillPageKind.Merchants, "Test Leaf", DateTime.UtcNow, [], [merchant]));

        var pages = await new WikiCacheRepository(_folder).LoadTradeskillsAsync();

        Assert.Equal(3, pages.Count);
        Assert.Equal(2, Assert.Single(pages.Single(page => page.Kind == TradeskillPageKind.Recipe).Recipes).Ingredients[0].Quantity);
        Assert.Equal(merchant, Assert.Single(pages.Single(page => page.Kind == TradeskillPageKind.Merchants).Merchants));
        await repository.ResetAsync(new());
        await repository.FlushAsync();
        Assert.False(File.Exists(Path.Combine(_folder, "tradeskills.json")));
    }

    [Fact]
    public async Task CorruptTradeskillCacheIsProtectedUntilExplicitReset()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "tradeskills.json");
        await File.WriteAllTextAsync(path, "[{\"Kind\":0,\"Title\":\"Alchemy\",\"Recipes\":null}]");
        var repository = new WikiCacheRepository(_folder);
        await Assert.ThrowsAsync<JsonException>(() => repository.LoadTradeskillsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveTradeskillAsync(new(TradeskillPageKind.Skill, "Alchemy", DateTime.UtcNow, [], [])));
        Assert.Contains("\"Recipes\":null", await File.ReadAllTextAsync(path));
        await repository.ResetAsync(new());
        await repository.SaveTradeskillAsync(new(TradeskillPageKind.Merchants, "Test Leaf", DateTime.UtcNow, [], []));
        Assert.Single(await repository.LoadTradeskillsAsync());
    }

    private static QuestLookupResult Quest()
    {
        return new("Item", DateTime.UtcNow, []);
    }
    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
        else if (File.Exists(_folder))
        {
            File.Delete(_folder);
        }
    }
}
