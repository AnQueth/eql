using System.Text.Json;
using System.Text.Json.Nodes;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Infrastructure.Tests;

public sealed class TrackingRepositoryLoadTests
{
    [Fact]
    public async Task MissingTrackingDocuments_ReturnFreshStores()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            Assert.False((await new SpellDurationRepository().LoadAsync(Path.Combine(folder, "durations.json"))).IsDirty);
            Assert.False((await new SpawnRepository().LoadAsync(Path.Combine(folder, "spawns.json"))).IsDirty);
            Assert.False((await new MoteRepository().LoadAsync(Path.Combine(folder, "motes.json"))).IsDirty);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptTrackingDocuments_ThrowJsonException()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string durationsPath = WriteCorruptDocument(folder, "durations.json");
            string spawnsPath = WriteCorruptDocument(folder, "spawns.json");
            string motesPath = WriteCorruptDocument(folder, "motes.json");

            await Assert.ThrowsAsync<JsonException>(() => new SpellDurationRepository().LoadAsync(durationsPath));
            await Assert.ThrowsAsync<JsonException>(() => new SpawnRepository().LoadAsync(spawnsPath));
            await Assert.ThrowsAsync<JsonException>(() => new MoteRepository().LoadAsync(motesPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingNullDocuments_AreRejectedRatherThanTreatedAsEmpty()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string durationsPath = WriteDocument(folder, "durations.json", "null");
            string spawnsPath = WriteDocument(folder, "spawns.json", "null");
            string motesPath = WriteDocument(folder, "motes.json", "null");

            await Assert.ThrowsAsync<JsonException>(() => new SpellDurationRepository().LoadAsync(durationsPath));
            await Assert.ThrowsAsync<JsonException>(() => new SpawnRepository().LoadAsync(spawnsPath));
            await Assert.ThrowsAsync<JsonException>(() => new MoteRepository().LoadAsync(motesPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task UnreadableExistingDocuments_PropagateIoException()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string durationsPath = WriteDocument(folder, "durations.json", "{}");
            string spawnsPath = WriteDocument(folder, "spawns.json", "{}");
            string motesPath = WriteDocument(folder, "motes.json", "{}");

            using (var stream = new FileStream(
                durationsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => new SpellDurationRepository().LoadAsync(durationsPath));
            }

            using (var stream = new FileStream(
                spawnsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => new SpawnRepository().LoadAsync(spawnsPath));
            }

            using (var stream = new FileStream(
                motesPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => new MoteRepository().LoadAsync(motesPath));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ResistanceHistory_MaterializesOnlyRequestedMobsAndPreservesUnloadedEntriesOnSave()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string path = Path.Combine(folder, "resistances.json");
            var repository = new MobResistanceRepository();
            var original = new MobResistanceStore();
            for (int i = 1; i <= 500; i++)
            {
                original.Record("log", i, "Sky", $"Mob {i}", "Rend", true);
            }
            original.SetWiki("Mob 1", new MobWikiInfo("Mob 1", "url", DateTime.UtcNow, true, null, null, null,
                new Dictionary<ResistanceType, string>()));
            await repository.SaveAsync(path, original);
            byte[] baseline = await File.ReadAllBytesAsync(path);

            var loaded = await repository.LoadAsync(path);
            loaded.Record("log", 500, "Sky", "Mob 500", "Rend", true);
            Assert.Null(loaded.CapturePersistenceSnapshot());
            loaded.SetSpell(new("Rend", ResistanceType.Fire, "Fire", DateTime.UtcNow));
            Assert.Empty(loaded.CapturePersistenceSnapshot()!.Data.Mobs);
            Assert.Empty(loaded.CapturePersistenceSnapshot()!.Data.Wiki);

            Assert.Equal(1, loaded.GetRawSpellCounts("Sky", "Mob 500", "Rend").FullResists);
            loaded.Record("log", 501, "Sky", "Mob 500", "Rend", true);
            Assert.Single(loaded.CapturePersistenceSnapshot()!.Data.Mobs);
            await new MobResistanceRepository().SaveAsync(path, loaded);
            loaded.ResetResistances("Sky", "Mob 499", DateTime.Now);
            await new MobResistanceRepository().SaveAsync(path, loaded);

            Assert.Equal(baseline, await File.ReadAllBytesAsync(path));
            Assert.True(new FileInfo(path + ".journal").Length < baseline.Length / 10);
            var reloaded = await repository.LoadAsync(path);
            Assert.Equal(2, reloaded.GetRawSpellCounts("Sky", "Mob 500", "Rend").FullResists);
            Assert.Equal(0, reloaded.GetRawSpellCounts("Sky", "Mob 499", "Rend").FullResists);
            Assert.Equal(1, reloaded.GetRawSpellCounts("Sky", "Mob 1", "Rend").FullResists);
            Assert.NotNull(reloaded.Get("Sky", "Mob 1").Wiki);
            await repository.ResetWikiCachesAsync(path, reloaded);
            var reset = await repository.LoadAsync(path);
            Assert.Null(reset.Get("Sky", "Mob 1").Wiki);
            Assert.Equal(1, reset.GetRawSpellCounts("Sky", "Mob 1", "Rend").FullResists);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("count")]
    [InlineData("histogram")]
    [InlineData("wiki")]
    [InlineData("spell")]
    [InlineData("watermark")]
    [InlineData("reset")]
    [InlineData("null-root")]
    [InlineData("array-root")]
    public async Task ResistanceJournal_RejectsInvalidIntermediateChangesEvenWhenLaterRemoved(string kind)
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string path = Path.Combine(folder, "resistances.json");
            var store = new MobResistanceStore();
            store.Record("log", 1, "Sky", "Target", "Rend", true);
            await new MobResistanceRepository().SaveAsync(path, store);
            var change = kind switch
            {
                "count" => new JsonDeltaJournal.Change(["Mobs", "SKY\nTARGET", "REND", "Landed"], false,
                    JsonValue.Create(-1)),
                "histogram" => new JsonDeltaJournal.Change(["Mobs", "SKY\nTARGET", "REND", "DamageSamples"], false,
                    new JsonObject { ["TESTER"] = new JsonObject { ["0"] = 1 } }),
                "wiki" => new JsonDeltaJournal.Change(["Wiki", "TARGET"], false,
                    new JsonObject { ["Resistances"] = null }),
                "spell" => new JsonDeltaJournal.Change(["Spells", "REND"], false,
                    new JsonObject { ["Spell"] = "" }),
                "watermark" => new JsonDeltaJournal.Change(["Watermarks", "LOG"], false, null),
                "reset" => new JsonDeltaJournal.Change(["ResistanceResetTimes", "SKY\nTARGET"], false,
                    JsonValue.Create("not-a-time")),
                "null-root" => new JsonDeltaJournal.Change(["Mobs"], false, null),
                "array-root" => new JsonDeltaJournal.Change(["Mobs"], false, new JsonArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            await PersistenceJournal.AppendAsync(path + ".journal", new JsonDeltaJournal.Batch([change]), default);
            await PersistenceJournal.AppendAsync(path + ".journal",
                new JsonDeltaJournal.Batch([new JsonDeltaJournal.Change(change.Path, true, null)]), default);
            byte[] baseline = await File.ReadAllBytesAsync(path);
            byte[] journal = await File.ReadAllBytesAsync(path + ".journal");

            await Assert.ThrowsAsync<JsonException>(() => new MobResistanceRepository().LoadAsync(path));

            Assert.Equal(baseline, await File.ReadAllBytesAsync(path));
            Assert.Equal(journal, await File.ReadAllBytesAsync(path + ".journal"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ResistanceHistory_DamagedUnrequestedEntryIsDeferredAndProtectsSavingWhenRequested()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string path = WriteDocument(folder, "resistances.json",
                """{"Mobs":{"SKY\nBROKEN":{"REND":{"Spell":"Rend","Resisted":-1}}}}""");
            byte[] baseline = await File.ReadAllBytesAsync(path);
            var repository = new MobResistanceRepository();
            var store = await repository.LoadAsync(path);
            store.Record("new-log", 1, "Sky", "Other", "Rend", true);
            Assert.Single(store.CapturePersistenceSnapshot()!.Data.Mobs);

            await Assert.ThrowsAsync<JsonException>(() => Task.Run(() => store.Get("Sky", "Broken")));
            await Assert.ThrowsAsync<JsonException>(() => repository.SaveAsync(path, store));

            Assert.Equal(baseline, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(path + ".journal"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string CreateTemporaryFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"eql-tracking-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string WriteCorruptDocument(string folder, string name)
    {
        return WriteDocument(folder, name, "{");
    }

    private static string WriteDocument(string folder, string name, string contents)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
