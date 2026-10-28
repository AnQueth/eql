using System.Security.Cryptography;
using System.Text.Json;

namespace EqLog.Core;

/// <summary>Asynchronous, crash-safe file helpers shared by every persisted store.</summary>
public static class AtomicFile
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Shared compact options for persisted stores; reusing one instance keeps System.Text.Json's metadata cache warm.</summary>
    public static readonly JsonSerializerOptions Json = new();

    /// <summary>
    /// Streams content into a temp file through <paramref name="write"/> and then replaces the target,
    /// so a crash mid-write can't leave a truncated file and nothing is buffered beyond the file stream's buffer.
    /// </summary>
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct = default)
    {
        string tmp = PrepareTemp(path);
        try
        {
            await using (var stream = OpenWrite(tmp))
            {
                await write(stream, ct).ConfigureAwait(false);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// SHA-256 of the JSON <paramref name="value"/> would serialize to, computed by streaming the serializer into the hash,
    /// so unchanged-content checks don't need to keep (or build) the serialized document.
    /// </summary>
    public static byte[] HashJson<T>(T value, JsonSerializerOptions? options = null)
    {
        using var hashing = new HashingStream();
        JsonSerializer.Serialize(hashing, value, options ?? Json);
        return hashing.GetHash();
    }

    /// <summary>
    /// Serializes straight into the file stream; System.Text.Json flushes as its buffer fills, so the JSON is never held in memory.
    /// <paramref name="value"/> must not be mutated during the write; pass a detached snapshot for shared state.
    /// </summary>
    public static Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        return WriteAsync(path, (stream, token) => JsonSerializer.SerializeAsync(stream, value, options ?? Json, token), ct);
    }

    /// <summary>Deserializes straight from the file without reading it into a string first.</summary>
    public static async Task<T?> ReadJsonAsync<T>(string path, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        await using var stream = OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, options ?? Json, ct).ConfigureAwait(false);
    }

    /// <summary>Opens a file for overlapped (truly asynchronous) sequential reading.</summary>
    public static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static FileStream OpenWrite(string path)
    {
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
    }

    private static string PrepareTemp(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return path + ".tmp";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next save overwrites it.
        }
    }

    /// <summary>Write-only sink that feeds everything written into an incremental SHA-256.</summary>
    private sealed class HashingStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] GetHash()
        {
            return _hash.GetHashAndReset();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}