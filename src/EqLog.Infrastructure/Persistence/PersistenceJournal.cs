using System.Text;
using System.Text.Json;

namespace EqLog.Infrastructure.Persistence;

/// <summary>Appends complete idempotent batches and recovers only an interrupted final record.</summary>
internal static class PersistenceJournal
{
    internal static async Task<IReadOnlyList<T>> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(path))
        {
            return [];
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0)
        {
            return [];
        }
        stream.Seek(-1, SeekOrigin.End);
        bool complete = stream.ReadByte() == '\n';
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var records = new List<T>();
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        while (line is not null)
        {
            string? next = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (next is null && !complete)
            {
                System.Diagnostics.Trace.TraceWarning("An interrupted final persistence journal record was ignored.");
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new JsonException("Persistence journal contains an empty record.");
            }
            records.Add(JsonSerializer.Deserialize<T>(line, AtomicFile.Json)
                ?? throw new JsonException("Persistence journal contains an empty batch."));
            line = next;
        }
        return records;
    }

    internal static async Task AppendAsync<T>(string path, T record, CancellationToken cancellationToken)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(record, AtomicFile.Json);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous);
        long length = stream.Length;
        if (length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                long offset = length - 1;
                while (offset > 0)
                {
                    stream.Seek(--offset, SeekOrigin.Begin);
                    if (stream.ReadByte() == '\n')
                    {
                        offset++;
                        break;
                    }
                }
                stream.SetLength(offset);
                System.Diagnostics.Trace.TraceWarning("An interrupted final persistence journal record was removed before retry.");
            }
        }
        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
