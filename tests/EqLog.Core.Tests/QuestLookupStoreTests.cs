using EqLog.Core;

namespace EqLog.Core.Tests;

public sealed class QuestLookupStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private string JsonlPath => Path.Combine(_folder, "quest-lookups.jsonl");
    private string LegacyPath => Path.Combine(_folder, "quest-lookups.json");

    public QuestLookupStoreTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private static QuestLookupResult Result(string item, DateTime checkedUtc, params string[] quests)
    {
        return new(item, checkedUtc, quests.Select(q => new QuestReference(q, QuestLookup.PageUrl(q))).ToArray());
    }

    [Fact]
    public async Task LegacyArray_IsMigratedToJsonLinesKeepingNewestPerItem()
    {
        await AtomicFile.WriteJsonAsync(LegacyPath, new[]
        {
            Result("Wind Rune Fana", T0, "Old Quest"),
            Result("Wind Rune Fana", T0.AddDays(1), "Bard Plane of Sky Tests"),
            Result("Gnoll Fang", T0, "Fang Quest") with { Found = false },
        });

        var loaded = await QuestLookupStore.LoadAsync(JsonlPath, LegacyPath);

        Assert.False(File.Exists(LegacyPath));
        Assert.Equal(2, File.ReadAllLines(JsonlPath).Length);
        Assert.Equal("Bard Plane of Sky Tests", loaded.Single(r => r.Item == "Wind Rune Fana").Quests.Single().Title);
        Assert.False(loaded.Single(r => r.Item == "Gnoll Fang").Found);
    }

    [Fact]
    public async Task Append_AddsOneLineAndLaterLinesWin()
    {
        await QuestLookupStore.AppendAsync(JsonlPath, Result("Gnoll Fang", T0, "First"));
        await QuestLookupStore.AppendAsync(JsonlPath, Result("Wind Rune Fana", T0, "Sky"));
        await QuestLookupStore.AppendAsync(JsonlPath, Result("gnoll fang", T0.AddDays(1), "Second"));

        Assert.Equal(3, File.ReadAllLines(JsonlPath).Length);
        var loaded = await QuestLookupStore.LoadAsync(JsonlPath);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Second", loaded.Single(r => r.Item.Equals("Gnoll Fang", StringComparison.OrdinalIgnoreCase)).Quests.Single().Title);
    }

    [Fact]
    public async Task PartialLineFromACrash_IsSkippedAndTheNextAppendStartsOnANewLine()
    {
        await QuestLookupStore.AppendAsync(JsonlPath, Result("Gnoll Fang", T0, "First"));
        string line = File.ReadAllLines(JsonlPath).Single();
        File.AppendAllText(JsonlPath, line[..(line.Length / 2)]);

        await QuestLookupStore.AppendAsync(JsonlPath, Result("Wind Rune Fana", T0, "Sky"));

        var loaded = await QuestLookupStore.LoadAsync(JsonlPath);
        Assert.Equal(["Gnoll Fang", "Wind Rune Fana"], loaded.Select(r => r.Item).Order());
    }

    [Fact]
    public async Task ManySupersededLines_AreCompactedOnLoad()
    {
        for (int i = 0; i < 40; i++)
        {
            await QuestLookupStore.AppendAsync(JsonlPath, Result("Gnoll Fang", T0.AddMinutes(i), $"Quest {i}"));
        }

        var loaded = await QuestLookupStore.LoadAsync(JsonlPath);

        Assert.Equal("Quest 39", Assert.Single(loaded).Quests.Single().Title);
        Assert.Single(File.ReadAllLines(JsonlPath));
    }
}
