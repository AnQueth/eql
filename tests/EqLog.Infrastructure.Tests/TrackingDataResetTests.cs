using EqLog.Infrastructure.Persistence;

namespace EqLog.Core.Tests;

public sealed class TrackingDataResetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"eq-reset-{Guid.NewGuid():N}");

    [Fact]
    public async Task ApplyPending_WithoutRequest_DoesNotChangeData()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "motes.json");
        await File.WriteAllTextAsync(path, "original");
        Assert.Null(await TrackingDataReset.ApplyPendingAsync(_folder));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPending_MovesAllTrackingAndLegacyHistory_PreservesSettingsAndCaches()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "npc-compendium", "npcs"));
        string[] tracking =
        [
            "spell-durations.json", "spawn-timers.json", "motes.json", "mob-resistances.json", "mob-resistances.json.journal",
            "mob-resistances.json.checkpoint",
            "quest-inventory.json", "npc-compendium.json", "npc-compendium.json.migrated",
            "leveling-history.json", "leveling-history.jsonl", "spawn-timers.json.tmp", "leveling-history.jsonl.tmp",
        ];
        string[] preserved = ["settings.json", "settings.json.tmp", "quest-lookups.jsonl", "sky-quests.json", "game-log.txt"];
        foreach (string name in tracking.Concat(preserved))
        {
            await File.WriteAllTextAsync(Path.Combine(_folder, name), name);
        }
        await File.WriteAllTextAsync(Path.Combine(_folder, "npc-compendium", "npcs", "history.jsonl"), "history");
        await TrackingDataReset.RequestAsync(_folder);
        Assert.True(File.Exists(Path.Combine(_folder, "motes.json")));

        string backup = Assert.IsType<string>(await TrackingDataReset.ApplyPendingAsync(_folder));
        foreach (string name in tracking)
        {
            Assert.False(File.Exists(Path.Combine(_folder, name)));
            Assert.Equal(name, await File.ReadAllTextAsync(Path.Combine(backup, name)));
        }
        foreach (string name in preserved)
        {
            Assert.Equal(name, await File.ReadAllTextAsync(Path.Combine(_folder, name)));
        }
        Assert.False(Directory.Exists(Path.Combine(_folder, "npc-compendium")));
        Assert.Equal("history", await File.ReadAllTextAsync(Path.Combine(backup, "npc-compendium", "npcs", "history.jsonl")));
        Assert.Null(await TrackingDataReset.ApplyPendingAsync(_folder));
    }

    [Fact]
    public async Task ApplyPending_PartialFailure_RetainsRequestAndResumesWithoutOverwritingBackup()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "motes.json"), "motes");
        await File.WriteAllTextAsync(Path.Combine(_folder, "spawn-timers.json"), "spawns");
        await TrackingDataReset.RequestAsync(_folder);
        var request = await AtomicFile.ReadJsonAsync<System.Text.Json.JsonElement>(Path.Combine(_folder, "tracking-reset.json"));
        string id = request.GetProperty("Id").GetString()!;
        string backup = Path.Combine(_folder, "tracking-backups", id);
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "motes.json"), "existing backup");

        await Assert.ThrowsAsync<IOException>(() => TrackingDataReset.ApplyPendingAsync(_folder));
        Assert.True(File.Exists(Path.Combine(_folder, "tracking-reset.json")));
        Assert.Equal("existing backup", await File.ReadAllTextAsync(Path.Combine(backup, "motes.json")));
        Assert.Equal("spawns", await File.ReadAllTextAsync(Path.Combine(backup, "spawn-timers.json")));

        File.Delete(Path.Combine(backup, "motes.json"));
        Assert.Equal(backup, await TrackingDataReset.ApplyPendingAsync(_folder));
        Assert.Equal("motes", await File.ReadAllTextAsync(Path.Combine(backup, "motes.json")));
        Assert.False(File.Exists(Path.Combine(_folder, "tracking-reset.json")));
    }

    [Fact]
    public async Task ApplyPending_IgnoreRequest_PersistsCutoffAndRebuildRequestRemovesIt()
    {
        Directory.CreateDirectory(_folder);
        var cutoff = new DateTime(2026, 10, 27, 12, 0, 5, DateTimeKind.Local);
        Assert.Null(await TrackingDataReset.LoadLogCutoffAsync(_folder));

        await TrackingDataReset.RequestAsync(_folder, cutoff);
        Assert.Null(await TrackingDataReset.LoadLogCutoffAsync(_folder));
        await TrackingDataReset.ApplyPendingAsync(_folder);
        Assert.Equal(cutoff, await TrackingDataReset.LoadLogCutoffAsync(_folder));

        await TrackingDataReset.RequestAsync(_folder);
        string backup = Assert.IsType<string>(await TrackingDataReset.ApplyPendingAsync(_folder));
        Assert.Null(await TrackingDataReset.LoadLogCutoffAsync(_folder));
        Assert.True(File.Exists(Path.Combine(backup, "tracking-log-cutoff.json")));
    }

    [Fact]
    public async Task ApplyPending_LegacyStringRequest_RebuildsFromWholeLogs()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "motes.json"), "motes");
        await AtomicFile.WriteJsonAsync(Path.Combine(_folder, "tracking-reset.json"), Guid.NewGuid().ToString("N"));
        string backup = Assert.IsType<string>(await TrackingDataReset.ApplyPendingAsync(_folder));
        Assert.Equal("motes", await File.ReadAllTextAsync(Path.Combine(backup, "motes.json")));
        Assert.Null(await TrackingDataReset.LoadLogCutoffAsync(_folder));
    }

    [Fact]
    public async Task ApplyPending_InvalidRequest_DoesNotMoveData()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "motes.json"), "original");
        await AtomicFile.WriteJsonAsync(Path.Combine(_folder, "tracking-reset.json"), "..");
        await Assert.ThrowsAsync<IOException>(() => TrackingDataReset.ApplyPendingAsync(_folder));
        Assert.True(File.Exists(Path.Combine(_folder, "motes.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
