using System.Text.Json;
using System.Text.Json.Nodes;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Infrastructure.Tests;

public sealed class JsonDeltaJournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"eq-journal-{Guid.NewGuid():N}");
    private static readonly JsonSerializerOptions Options = new();

    [Fact]
    public async Task Save_LargeBaseline_AppendsOnlyChangesAndReplays()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "data.json");
        var (journal, data) = await OpenAsync(path);
        var entries = new JsonObject();
        for (int i = 0; i < 1000; i++)
        {
            entries.Add($"item-{i}", new JsonObject { ["value"] = new string('x', 100) });
        }
        data["entries"] = entries;
        await journal.SaveAsync(data, default);
        byte[] baseline = await File.ReadAllBytesAsync(path);

        entries["item-500"]!["value"] = "changed";
        await journal.SaveAsync(data, default);

        Assert.Equal(baseline, await File.ReadAllBytesAsync(path));
        Assert.True(new FileInfo(path + ".journal").Length < baseline.Length / 100);
        var (_, loaded) = await OpenAsync(path);
        Assert.Equal("changed", loaded["entries"]!["item-500"]!["value"]!.GetValue<string>());
        Assert.Equal(new string('x', 100), loaded["entries"]!["item-999"]!["value"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_InterruptedCheckpoint_UsesCheckpointBeforeReplayingOldJournal(bool baselineReplaced)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "data.json");
        var (journal, data) = await OpenAsync(path);
        data["parent"] = new JsonObject { ["value"] = 1 };
        await journal.SaveAsync(data, default);
        data["parent"]!["value"] = 2;
        await journal.SaveAsync(data, default);
        data.Remove("parent");
        await journal.SaveAsync(data, default);
        await AtomicFile.WriteJsonAsync(path + ".checkpoint", data, Options);
        if (baselineReplaced)
        {
            await AtomicFile.WriteJsonAsync(path, data, Options);
        }

        var (_, loaded) = await OpenAsync(path);

        Assert.False(loaded.ContainsKey("parent"));
        Assert.False(File.Exists(path + ".journal"));
        Assert.False(File.Exists(path + ".checkpoint"));
    }

    [Fact]
    public async Task Open_PartialTail_RepairsBeforeNextSaveAndRejectsCompleteCorruption()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "data.json");
        var (journal, data) = await OpenAsync(path);
        data["value"] = 1;
        await journal.SaveAsync(data, default);
        data["value"] = 2;
        await journal.SaveAsync(data, default);
        await File.AppendAllTextAsync(path + ".journal", "{\"Changes\":");

        (journal, data) = await OpenAsync(path);
        Assert.Equal(2, data["value"]!.GetValue<int>());
        data["value"] = 3;
        await journal.SaveAsync(data, default);
        var (_, loaded) = await OpenAsync(path);
        Assert.Equal(3, loaded["value"]!.GetValue<int>());

        await File.AppendAllTextAsync(path + ".journal", "not-json\n");
        await Assert.ThrowsAsync<JsonException>(() => OpenAsync(path));
    }

    [Fact]
    public async Task Open_ChangedEntryValidationChecksEveryBatchAndObservesCancellation()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "data.json");
        var (journal, data) = await OpenAsync(path);
        data["value"] = 0;
        await journal.SaveAsync(data, default);
        for (int i = 1; i <= 3; i++)
        {
            data["value"] = i;
            await journal.SaveAsync(data, default);
        }
        using var cancellation = new CancellationTokenSource();
        int validated = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JsonDeltaJournal.OpenAsync(
            path, new JsonObject(), Options, Validate, cancellation.Token,
            (current, batch) =>
            {
                Assert.Equal(1, current["value"]!.GetValue<int>());
                validated++;
                cancellation.Cancel();
            }));
        Assert.Equal(1, validated);
    }

    [Fact]
    public async Task Journal_StreamsValidRecordsBeforeLaterCorruption()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "stream.jsonl");
        await PersistenceJournal.AppendAsync(path, new JsonDeltaJournal.Batch(
            [new JsonDeltaJournal.Change(["value"], false, JsonValue.Create(1))]), default);
        await File.AppendAllTextAsync(path, "not-json\n");

        await using var records = PersistenceJournal.ReadAsync<JsonDeltaJournal.Batch>(path, default).GetAsyncEnumerator();
        Assert.True(await records.MoveNextAsync());
        Assert.Equal(1, Assert.Single(records.Current.Changes).Value!.GetValue<int>());
        await Assert.ThrowsAsync<JsonException>(async () => await records.MoveNextAsync());
    }

    [Fact]
    public async Task Save_LargeJournal_CheckpointsDuringSessionAndRemainsReplayable()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "data.json");
        var (journal, data) = await OpenAsync(path);
        data["value"] = "initial";
        await journal.SaveAsync(data, default);
        data["value"] = new string('x', 1024 * 1024);
        await journal.SaveAsync(data, default);

        Assert.False(File.Exists(path + ".journal"));
        Assert.False(File.Exists(path + ".checkpoint"));
        var (_, loaded) = await OpenAsync(path);
        Assert.Equal(data["value"]!.GetValue<string>(), loaded["value"]!.GetValue<string>());
        data["value"] = "next";
        await journal.SaveAsync(data, default);
        (_, loaded) = await OpenAsync(path);
        Assert.Equal("next", loaded["value"]!.GetValue<string>());
    }

    private static Task<(JsonDeltaJournal Journal, JsonObject Data)> OpenAsync(string path)
    {
        return JsonDeltaJournal.OpenAsync(path, new JsonObject(), Options, Validate, default);
    }

    private static void Validate(JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
