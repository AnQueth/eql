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
