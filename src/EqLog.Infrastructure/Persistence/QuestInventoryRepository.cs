using System.Text.Json;
using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Preserves quest-inventory.json and its existing compact JSON shape and atomic replacement.</summary>
public sealed class QuestInventoryRepository : IQuestInventoryRepository
{
    private readonly string _path;
    private readonly TimeProvider _time;

    /// <summary>Uses an explicit app-owned path; never discovers or modifies game files.</summary>
    public QuestInventoryRepository(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<QuestInventory> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new();
        }
        var state = await AtomicFile.ReadJsonAsync<QuestInventory>(_path, ct: cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Empty quest inventory.");
        Normalize(state, _time.GetUtcNow().UtcDateTime);
        return state;
    }

    /// <inheritdoc />
    public Task SaveAsync(QuestInventory snapshot, CancellationToken cancellationToken = default)
    {
        return AtomicFile.WriteJsonAsync(_path, snapshot, ct: cancellationToken);
    }

    private static void Normalize(QuestInventory state, DateTime now)
    {
        if (state.Items is null || state.Items.Any(item => item is null || item.Character is null || item.Item is null) ||
            state.Completed is null || state.ExportTimes is null || state.RecentLoot is null)
        {
            throw new JsonException("Quest inventory contains null required fields.");
        }
        state.LootWatermarks ??= [];
        state.ItemRanks ??= [];
        state.RecentUpgrades ??= [];
        if (state.LootWatermarks.Values.Any(watermark => watermark is null) ||
            state.RecentLoot.Any(item => item is null || item.Character is null || item.Item is null) ||
            state.ItemRanks.Any(item => item is null || item.Character is null || item.Item is null) ||
            state.RecentUpgrades.Any(item => item is null || item.Character is null || item.Item is null) ||
            state.ImportedLines?.Any(line => line is null) == true)
        {
            throw new JsonException("Quest inventory contains null history entries.");
        }
        foreach (var item in state.Items)
        {
            item.Owned = item.Carried + item.Bank + item.Hoard + item.Depot + item.OtherStorage;
        }
        if (state.ImportedLines is null)
        {
            return;
        }
        foreach (string imported in state.ImportedLines)
        {
            int last = imported.LastIndexOf('\t');
            if (last <= 0 || !long.TryParse(imported.AsSpan(last + 1), out long line))
            {
                continue;
            }
            string key = imported[..last].ToUpperInvariant();
            if (!state.LootWatermarks.TryGetValue(key, out var watermark) || line > watermark.Line)
            {
                state.LootWatermarks[key] = new(now, line);
            }
        }
        state.ImportedLines = null;
    }
}
