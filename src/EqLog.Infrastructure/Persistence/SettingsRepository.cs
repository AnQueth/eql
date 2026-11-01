using System.Text.Json;
using System.Text.Json.Serialization;
using EqLog.Application.Settings;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Settings JSON storage, preserving unknown fields, enum spelling, indentation and atomic replacement.</summary>
public sealed class SettingsRepository(string path) : ISettingsRepository
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private byte[]? _lastSavedHash;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!await Task.Run(() => File.Exists(path), cancellationToken).ConfigureAwait(false))
        {
            return new();
        }
        return await AtomicFile.ReadJsonAsync<AppSettings>(path, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Empty settings document.");
    }

    public async Task SaveAsync(AppSettings snapshot, CancellationToken cancellationToken = default)
    {
        byte[] hash = await Task.Run(() => AtomicFile.HashJson(snapshot, Json), cancellationToken).ConfigureAwait(false);
        if (_lastSavedHash is not null && hash.AsSpan().SequenceEqual(_lastSavedHash) &&
            await Task.Run(() => File.Exists(path), cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        await AtomicFile.WriteJsonAsync(path, snapshot, Json, cancellationToken).ConfigureAwait(false);
        _lastSavedHash = hash;
    }
}
