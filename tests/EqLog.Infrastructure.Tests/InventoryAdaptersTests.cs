using System.Text.Json;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Infrastructure.GameFiles;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Infrastructure.Tests;

public sealed class InventoryAdaptersTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eql-layered-inventory-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime At = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    public InventoryAdaptersTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Logs"));
    }

    [Fact]
    public void ResolvesMatchingCharacterAndServerAboveLogs()
    {
        Assert.Equal(@"C:\EverQuest\Quethrosar_qeynos-Inventory.txt",
            InventoryExportSource.PathForLog(@"C:\EverQuest\Logs\eqlog_Quethrosar_qeynos.txt"));
        Assert.Throws<ArgumentException>(() => InventoryExportSource.PathForLog(@"C:\EverQuest\Logs\other.txt"));
    }

    [Fact]
    public async Task ReadsAllSectionsAndNeverChangesGameFile()
    {
        string log = Path.Combine(_directory, "Logs", "eqlog_Que_server.txt");
        string export = InventoryExportSource.PathForLog(log);
        string content = "Location\tName\tID\tCount\tSlots\nHeld\tSword +3\t1\t2\t0\nBank1\tSword +10\t1\t1\t0\nKeyRing\tName\tID\t\nEquipment\tSword +9\t1\n";
        await File.WriteAllTextAsync(export, content);
        var source = new InventoryExportSource();
        var stamp = Assert.IsType<InventoryFileStamp>(await source.InspectAsync(log, default));
        var rows = Assert.IsAssignableFrom<IReadOnlyList<InventoryExportItem>>(await source.ReadAsync(stamp, default));
        Assert.Equal(3, rows.Count);
        Assert.True(rows[2].Collection);
        Assert.Equal(content, await File.ReadAllTextAsync(export));
        Assert.Equal(stamp, await source.InspectAsync(log, default));
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task ChangedMissingMalformedAndLockedExportsAreNotImported()
    {
        string log = Path.Combine(_directory, "Logs", "eqlog_Que_server.txt");
        string export = InventoryExportSource.PathForLog(log);
        var source = new InventoryExportSource();
        Assert.Null(await source.InspectAsync(log, default));
        await File.WriteAllTextAsync(export, "Location\tName\tID\tCount\tSlots\nHeld\tSword\t1\t1\t0\n");
        var stamp = Assert.IsType<InventoryFileStamp>(await source.InspectAsync(log, default));
        await File.AppendAllTextAsync(export, "Held\tOther\t2\t1\t0\n");
        Assert.Null(await source.ReadAsync(stamp, default));
        await File.WriteAllTextAsync(export, "Location\tName\tID\tCount\tSlots\nHeld\tbroken");
        stamp = Assert.IsType<InventoryFileStamp>(await source.InspectAsync(log, default));
        await Assert.ThrowsAsync<FormatException>(() => source.ReadAsync(stamp, default));
        using (var writer = new FileStream(export, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(stamp, default));
        }
        File.Delete(export);
        Assert.Null(await source.ReadAsync(stamp, default));
    }

    [Fact]
    public async Task LegacyImportedLinesBecomeWatermarksAndZeroCountsAreNotWritten()
    {
        string path = Path.Combine(_directory, "quest-inventory.json");
        await File.WriteAllTextAsync(path, """
            {"Items":[{"Character":"Que","Item":"Gnoll Fang","Owned":0,"Historical":2,"Carried":0,"Bank":0}],
             "ImportedLines":["Que\tC:\\logs\\eq.txt\t5","Que\tC:\\logs\\eq.txt\t9","Que\tC:\\logs\\eq.txt\tbad"],
             "LootWatermarks":null,"ItemRanks":null,"RecentUpgrades":null}
            """);
        var repository = new QuestInventoryRepository(path, new FixedTime(At));
        var loaded = await repository.LoadAsync();
        Assert.Null(loaded.ImportedLines);
        var watermark = Assert.Single(loaded.LootWatermarks).Value;
        Assert.Equal(9, watermark.Line);
        Assert.Equal(At, watermark.Time);
        loaded.NoteLoot("Que", @"C:\logs\eq.txt", 9, "Gnoll Fang", 2, false, At.AddDays(-1));
        Assert.Equal(2, loaded.Items.Single().Historical);
        loaded.NoteLoot("Que", @"C:\logs\eq.txt", 10, "Gnoll Fang", 1, false, At.AddDays(-1));
        Assert.Equal(3, loaded.Items.Single().Historical);
        await repository.SaveAsync(loaded.Clone());
        string saved = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("ImportedLines", saved);
        Assert.DoesNotContain("\"Bank\"", saved);
        Assert.Contains("\"Historical\":3", saved);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SavedEstimatedOwnedCountsAreReconciledToExportedStorageCountsOnLoad()
    {
        string path = Path.Combine(_directory, "quest-inventory.json");
        await File.WriteAllTextAsync(path, """
            {"Items":[
              {"Character":"Que","Item":"Umbral Platemail Bracer +10","Owned":3,"Carried":2},
              {"Character":"Que","Item":"Umbral Platemail Gauntlets +9","Owned":2,"Collection":1},
              {"Character":"Other","Item":"Sword","Owned":99,"Carried":1,"Bank":2,"Hoard":3,"Depot":4,"OtherStorage":5,"Installed":6}
            ],"RecentLoot":[{"Character":"Que","Item":"Umbral Platemail Bracer","Quantity":1,"TimeUtc":"2026-11-01T18:00:00Z"}]}
            """);
        var repository = new QuestInventoryRepository(path);
        var loaded = await repository.LoadAsync();
        Assert.Equal([2, 0, 15], loaded.Items.Select(item => item.Owned));
        Assert.Equal(1, loaded.Items[1].Collection);
        Assert.Equal(6, loaded.Items[2].Installed);
        await repository.SaveAsync(loaded.Clone());
        var reloaded = await repository.LoadAsync();
        Assert.Equal([2, 0, 15], reloaded.Items.Select(item => item.Owned));
        Assert.Single(reloaded.RecentLoot);
    }

    [Fact]
    public async Task MissingStateIsEmptyAndRepeatedRepositoryReloadsPreserveRankHistoryAndCompletion()
    {
        string path = Path.Combine(_directory, "quest-inventory.json");
        var repository = new QuestInventoryRepository(path);
        var state = await repository.LoadAsync();
        Assert.Empty(state.Items);
        state.ApplyExport("Que", [new("Held", "Sword +1", 1, 1, InventoryStorage.Carried)], At);
        state.ApplyExport("Que", [new("Held", "Sword +3", 1, 2, InventoryStorage.Carried)], At.AddMinutes(1));
        state.Completed.Add(QuestInventory.CompletionKey("Que", "A quest"));
        state.NoteLoot("Other", "log", 4, "Fang", 2, false, At);
        var expected = Assert.Single(state.RecentUpgrades);
        for (int restart = 0; restart < 3; restart++)
        {
            await repository.SaveAsync(state.Clone());
            state = await repository.LoadAsync();
            Assert.Equal(expected, Assert.Single(state.RecentUpgrades));
            Assert.Equal(3, Assert.Single(state.ItemRanks).Rank);
            Assert.Single(state.Completed);
            Assert.Equal(2, state.Items.Single(item => item.Character == "Other").Historical);
            Assert.Single(state.LootWatermarks);
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"Items\":null}")]
    [InlineData("{\"Items\":[null]}")]
    [InlineData("{\"Completed\":null}")]
    [InlineData("{\"ImportedLines\":[null]}")]
    [InlineData("{\"LootWatermarks\":{\"QUE\\tLOG\":null}}")]
    public async Task CorruptStateThrowsAndServiceNeverOverwritesIt(string document)
    {
        string path = Path.Combine(_directory, "quest-inventory.json");
        await File.WriteAllTextAsync(path, document);
        var repository = new QuestInventoryRepository(path);
        var error = await Assert.ThrowsAnyAsync<JsonException>(() => repository.LoadAsync());
        var service = new InventoryService(new InventoryExportSource(), repository, new(), error);
        service.TrackItem("Que", "Sword");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync());
        Assert.Equal(document, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EndToEndStableImportAndSaveUseOnlyIsolatedPaths()
    {
        string log = Path.Combine(_directory, "Logs", "eqlog_Que_server.txt");
        string export = InventoryExportSource.PathForLog(log);
        await File.WriteAllTextAsync(export, "Location\tName\tID\tCount\tSlots\nHeld\tSword +3\t1\t2\t0\n");
        string path = Path.Combine(_directory, "app-data", "quest-inventory.json");
        var repository = new QuestInventoryRepository(path);
        var service = new InventoryService(new InventoryExportSource(), repository, await repository.LoadAsync());
        service.SelectSession(log, "Que");
        Assert.Equal(InventoryImportStatus.Stabilizing, (await service.PollAsync()).Status);
        Assert.Equal(InventoryImportStatus.Imported, (await service.PollAsync()).Status);
        await service.SaveAsync();
        Assert.Equal("Sword +3", Assert.Single((await repository.LoadAsync()).Items).Item);
        Assert.Equal(2, Assert.Single(InventoryQueries.CopyLocations(service.Snapshot().Export!.Rows, "Sword")).Quantity);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return new(now);
        }
    }
}
