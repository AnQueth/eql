using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EqLog.Application.Tracking;
using EqLog.Core;
using Microsoft.Win32.SafeHandles;

namespace EqLog.Infrastructure.Persistence;

public sealed class NpcCompendiumRepository : INpcCompendiumRepository
{
    private static readonly JsonSerializerOptions HistoryJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static readonly JsonSerializerOptions IndexJson = CreateIndexOptions();
    private readonly ConditionalWeakTable<NpcCompendium, RepositoryState> _states = new();
    private readonly ConditionalWeakTable<NpcCompendium, SemaphoreSlim> _saveGates = new();

    public async Task<NpcCompendium> LoadAsync(
        string folder,
        string? legacyPath = null,
        CancellationToken cancellationToken = default)
    {
        folder = Path.GetFullPath(folder);
        var compendium = new NpcCompendium();

        string indexPath = Path.Combine(folder, NpcCompendium.IndexFileName);
        string? sourcePath = File.Exists(indexPath)
            ? indexPath
            : legacyPath is not null && File.Exists(legacyPath)
                ? legacyPath
                : null;
        bool migrate = sourcePath is not null && !string.Equals(sourcePath, indexPath, StringComparison.OrdinalIgnoreCase);
        var data = new NpcCompendium.Data();
        if (migrate)
        {
            data = await AtomicFile.ReadJsonAsync<NpcCompendium.Data>(
                sourcePath!,
                ct: cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("NPC compendium data is empty.");
            Validate(data);
        }
        var (journal, loaded) = await JsonDeltaJournal.OpenAsync(
            indexPath, data, IndexJson, Validate, cancellationToken).ConfigureAwait(false);
        data = loaded;
        _states.Add(compendium, new RepositoryState(folder) { Journal = journal });

        compendium.ImportData(data);
        var recoveredIds = new Dictionary<string, IReadOnlyCollection<string>>();
        foreach (var (key, entry) in data.Entries.Where(pair => pair.Value.Encounters.Count > 0))
        {
            string historyName = entry.File ?? NpcCompendium.HistoryFileName(key);
            string historyPath = HistoryPath(folder, historyName);
            var records = await ReadHistoryAsync(historyPath, cancellationToken).ConfigureAwait(false);
            if (records is null || records.Count == 0)
            {
                continue;
            }

            var ids = records
                .Select(compendium.GetHistoryIdentity)
                .Where(id => id is not null)
                .Select(id => id!)
                .ToHashSet(StringComparer.Ordinal);
            recoveredIds[key] = ids;
        }
        compendium.ImportData(data, recoveredIds);

        if (migrate)
        {
            compendium.MarkDirty();
            await SaveAsync(folder, compendium, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(legacyPath!, legacyPath + ".migrated", overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The new index takes precedence; the legacy file is only left behind.
            }
        }

        return compendium;
    }

    public async Task SaveAsync(
        string folder,
        NpcCompendium compendium,
        CancellationToken cancellationToken = default)
    {
        folder = Path.GetFullPath(folder);
        RepositoryState state = GetOrBind(compendium, folder);
        EnsureFolder(state, folder);
        SemaphoreSlim gate = _saveGates.GetValue(compendium, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string indexPath = Path.Combine(folder, NpcCompendium.IndexFileName);
            bool initialIndex = !File.Exists(indexPath) && !File.Exists(JsonDeltaJournal.JournalPath(indexPath));
            if (state.Journal is null)
            {
                var (journal, _) = await JsonDeltaJournal.OpenAsync(
                    indexPath, new NpcCompendium.Data(), IndexJson, Validate, cancellationToken).ConfigureAwait(false);
                state.Journal = journal;
            }
            var batch = compendium.CapturePersistenceBatch();
            if (batch is null)
            {
                return;
            }

            if (batch.SourcesIndex is not null)
            {
                // History records refer to source numbers; persist the mapping before appending any line.
                await state.Journal.SaveAsync(batch.SourcesIndex, cancellationToken).ConfigureAwait(false);
            }

            foreach (var append in batch.HistoryAppends)
            {
                state.Histories.Remove(HistoryPath(folder, append.FileName));
                await JsonLines.AppendAsync(
                    HistoryPath(folder, append.FileName),
                    append.Records,
                    HistoryJson,
                    cancellationToken).ConfigureAwait(false);
                compendium.CompleteHistoryAppend(append.Key, append.EncounterIds);
            }

            await state.Journal.SaveAsync(compendium.ExportIndex(), cancellationToken).ConfigureAwait(false);
            if (initialIndex)
            {
                await state.Journal.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            }
            compendium.CompletePersistence(batch.Version);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NpcEntry?> GetAsync(
        NpcCompendium compendium,
        string mob,
        string zone,
        CancellationToken cancellationToken = default)
    {
        string? historyName = compendium.HistoryFileFor(mob, zone);
        if (historyName is null)
        {
            return null;
        }

        RepositoryState state = _states.TryGetValue(compendium, out var savedState)
            ? savedState
            : throw new InvalidOperationException("The NPC compendium has not been loaded or saved by this repository.");
        string historyPath = HistoryPath(state.Folder, historyName);
        SemaphoreSlim gate = _saveGates.GetValue(compendium, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(historyPath))
            {
                state.Histories.Remove(historyPath);
                compendium.ImportHistory(mob, zone, []);
                return compendium.Get(mob, zone);
            }

            try
            {
                await using var stream = AtomicFile.OpenRead(historyPath);
                HistoryStamp? stamp = GetHistoryStamp(stream);
                if (stamp is not null &&
                    state.Histories.TryGetValue(historyPath, out var cached) &&
                    cached.Stamp == stamp &&
                    cached.Token.TryGetTarget(out var cachedToken) &&
                    compendium.TryGetCachedHistory(mob, zone, cachedToken, out var snapshot))
                {
                    return snapshot;
                }

                state.Histories.Remove(historyPath);
                var records = await ReadHistoryAsync(stream, cancellationToken).ConfigureAwait(false);
                object? token = compendium.ImportHistory(mob, zone, records);
                if (token is not null && stamp is not null && stamp == GetHistoryStamp(stream))
                {
                    if (state.Histories.Count >= 8)
                    {
                        state.Histories.Remove(state.Histories.Keys.First());
                    }
                    state.Histories[historyPath] = new CachedHistory(stamp.Value, new WeakReference<object>(token));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                state.Histories.Remove(historyPath);
                Trace.TraceWarning("NPC history read or metadata check failed for '{0}': {1}", historyPath, exception);
            }

            return compendium.Get(mob, zone);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<List<NpcCompendium.EncounterRecord>?> ReadHistoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var records = new List<NpcCompendium.EncounterRecord>();
        if (!File.Exists(path))
        {
            return records;
        }

        try
        {
            await using var stream = AtomicFile.OpenRead(path);
            return await ReadHistoryAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<List<NpcCompendium.EncounterRecord>> ReadHistoryAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var records = new List<NpcCompendium.EncounterRecord>();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<NpcCompendium.EncounterRecord>(line, HistoryJson) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // A partial final line or malformed record does not hide the rest of the history.
            }
        }

        return records;
    }

    private static HistoryStamp? GetHistoryStamp(FileStream stream)
    {
        // Without a stable file identity and change time, always read rather than trust timestamps alone.
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 18, out FileIdentity identity, 24))
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error());
            Trace.TraceWarning("NPC history file identity check failed for '{0}': {1}",
                stream.Name, exception);
            return null;
        }

        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 0, out FileBasicInfo basic, 40))
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error());
            Trace.TraceWarning("NPC history file metadata check failed for '{0}': {1}",
                stream.Name, exception);
            return null;
        }

        if ((identity.IdLow == 0 && identity.IdHigh == 0) || basic.ChangeTime == 0)
        {
            Trace.TraceWarning("NPC history file metadata is incomplete for '{0}'; history will be reread.",
                stream.Name);
            return null;
        }

        return new HistoryStamp(identity, basic.LastWriteTime, basic.ChangeTime, stream.Length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileBasicInfo(
        long CreationTime, long LastAccessTime, long LastWriteTime, long ChangeTime, uint Attributes);

    private readonly record struct HistoryStamp(FileIdentity Identity, long LastWriteTime, long ChangeTime, long Length);

    private sealed record CachedHistory(HistoryStamp Stamp, WeakReference<object> Token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileIdentity information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileBasicInfo information, uint size);

    private static JsonSerializerOptions CreateIndexOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    typeInfo =>
                    {
                        if (typeInfo.Type != typeof(NpcCompendium.EntryData))
                        {
                            return;
                        }

                        foreach (var property in typeInfo.Properties.Where(
                            property => property.Name == nameof(NpcCompendium.EntryData.Encounters)))
                        {
                            property.ShouldSerialize = (_, value) => value is List<NpcCompendium.EncounterData>
                            {
                                Count: > 0,
                            };
                        }
                    },
                },
            },
        };
        return options;
    }

    private static string HistoryPath(string folder, string file)
    {
        return Path.Combine(folder, NpcCompendium.HistoryFolderName, Path.GetFileName(file));
    }

    private static void Validate(NpcCompendium.Data data)
    {
        if (data.Entries is null || data.Watermarks is null || data.LastConsiders is null || data.Sources is null ||
            data.Sources.Any(source => source is null) ||
            data.Watermarks.Values.Any(watermark => watermark is null) ||
            data.Entries.Values.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Mob) || entry.Zone is null ||
                entry.Encounters is null || entry.Encounters.Any(encounter => encounter is null ||
                    encounter.Spells is null || encounter.Id is null || encounter.Source is null ||
                    encounter.Character is null || encounter.Outcome is null || encounter.YourDamage < 0 ||
                    encounter.OtherDamage < 0 || encounter.IncomingDamage < 0 ||
                    encounter.Spells.Values.Any(spell => spell is null || spell.Spell is null ||
                        spell.Damage < 0 || spell.Resists < 0 || spell.Landings < 0))))
        {
            throw new JsonException("NPC compendium contains invalid or null entries.");
        }
    }

    private RepositoryState GetOrBind(NpcCompendium compendium, string folder)
    {
        return _states.GetValue(compendium, _ => new RepositoryState(folder));
    }

    private static void EnsureFolder(RepositoryState state, string folder)
    {
        if (!string.Equals(state.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The NPC compendium is already stored in another folder.");
        }
    }

    private sealed record RepositoryState(string Folder)
    {
        public Dictionary<string, CachedHistory> Histories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public JsonDeltaJournal? Journal
        {
            get; set;
        }
    }
}
