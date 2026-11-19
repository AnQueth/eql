using System.Text.Json;
using EqLog.Application.Tracking;
using EqLog.Core;
using EqLog.Core.Tracking;

namespace EqLog.Infrastructure.Persistence;

public sealed class LevelingHistoryRepository : ILevelingHistoryRepository
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public async Task<LevelingHistory> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        LevelingHistory.Data? data;
        try
        {
            data = await AtomicFile.ReadJsonAsync<LevelingHistory.Data>(path, ct: cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (DirectoryNotFoundException)
        {
            return new();
        }
        if (data?.Minutes is null || data.Watermarks is null ||
            data.Watermarks.Any(pair => pair.Value is null || pair.Value.Index < 1) ||
            data.Minutes.Any(row => row is null || string.IsNullOrWhiteSpace(row.Source) ||
                string.IsNullOrWhiteSpace(row.Character) || row.Context is null ||
                string.IsNullOrWhiteSpace(row.Context.Visit) || row.Context.Level is <= 0 ||
                row.Minute != LevelingHistory.FloorMinute(row.Minute) || row.Minute > DateTime.MaxValue.AddMinutes(-1) ||
                row.Start < row.Minute || row.End < row.Start || row.End > row.Minute.AddMinutes(1) ||
                (row.LevelStarted.HasValue && (row.Context.Level is null ||
                    row.LevelStarted < row.Start || row.LevelStarted > row.End)) ||
                !double.IsFinite(row.Seconds) || row.Seconds < 0 || row.Seconds > 60.001 ||
                !double.IsFinite(row.Experience) || row.Experience < 0 ||
                row.Gains < 0 || row.UnknownGains < 0 || row.UnknownGains > row.Gains ||
                row.Rewards < 0 || row.Kills < 0 || row.Deaths < 0))
        {
            throw new JsonException("Leveling history contains invalid or empty data.");
        }
        if (data.Minutes.Select(row => (row.Source, row.Character, row.Context, row.Minute)).Distinct().Count() != data.Minutes.Count)
        {
            throw new JsonException("Leveling history contains duplicate minutes.");
        }
        var history = new LevelingHistory();
        history.Import(data);
        return history;
    }

    public async Task SaveAsync(string path, LevelingHistory history, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = history.Capture();
            if (snapshot is null)
            {
                return;
            }
            await AtomicFile.WriteJsonAsync(path, snapshot.Value.Data, ct: cancellationToken).ConfigureAwait(false);
            history.CompleteSave(snapshot.Value.Version);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
