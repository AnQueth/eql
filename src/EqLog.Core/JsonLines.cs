using System.Text;
using System.Text.Json;

namespace EqLog.Core;

/// <summary>
/// Append-only JSON Lines files: one record per line, where a later line for the same key replaces an earlier one.
/// Adding a record only appends a line instead of rewriting the whole file; the file is compacted on load
/// once superseded lines outnumber the live ones.
/// </summary>
public static class JsonLines
{
    private const int BufferSize = 64 * 1024;
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    /// <summary>
    /// Reads every record, keeping the last one per key. A partial last line left by a crash is skipped.
    /// When the file holds many superseded lines it is rewritten with just the latest records.
    /// </summary>
    public static async Task<Dictionary<string, T>> LoadLatestAsync<T>(string path, Func<T, string> key,
        IEqualityComparer<string>? comparer = null, JsonSerializerOptions? options = null, CancellationToken ct = default)
        where T : class
    {
        var latest = new Dictionary<string, T>(comparer ?? StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return latest;
        }

        int lines = 0;
        await using (var stream = AtomicFile.OpenRead(path))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                lines++;
                try
                {
                    if (JsonSerializer.Deserialize<T>(line, options ?? AtomicFile.Json) is { } record)
                    {
                        latest[key(record)] = record;
                    }
                }
                catch (JsonException)
                {
                    // A crash mid-append can leave one partial line; the rest of the file is still valid.
                }
            }
        }

        if (lines > latest.Count * 2 + 16)
        {
            await RewriteAsync(path, latest.Values, options, ct).ConfigureAwait(false);
        }
        return latest;
    }

    /// <summary>Streams one line per record onto the end of the file, first finishing any line a crash left partial.</summary>
    public static async Task AppendAsync<T>(string path, IEnumerable<T> records, JsonSerializerOptions? options = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            BufferSize, FileOptions.Asynchronous);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            byte[] last = new byte[1];
            if (await stream.ReadAsync(last, ct).ConfigureAwait(false) == 1 && last[0] != '\n')
            {
                await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
            }
        }
        stream.Seek(0, SeekOrigin.End);
        await WriteLinesAsync(stream, records, options, ct).ConfigureAwait(false);
    }

    /// <summary>Atomically replaces the file with one line per record.</summary>
    public static Task RewriteAsync<T>(string path, IEnumerable<T> records, JsonSerializerOptions? options = null,
        CancellationToken ct = default)
    {
        return AtomicFile.WriteAsync(path, (stream, token) => WriteLinesAsync(stream, records, options, token), ct);
    }

    private static async Task WriteLinesAsync<T>(Stream stream, IEnumerable<T> records, JsonSerializerOptions? options,
        CancellationToken ct)
    {
        foreach (var record in records)
        {
            await JsonSerializer.SerializeAsync(stream, record, options ?? AtomicFile.Json, ct).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
        }
    }
}
