using System.Text.Json;
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
