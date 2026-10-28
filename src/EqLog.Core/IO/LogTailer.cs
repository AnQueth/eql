using System.Text;

namespace EqLog.Core.IO;

public readonly record struct TailProgress(long BytesRead, long TotalBytes, long LinesRead, bool CaughtUp);

/// <summary>
/// Reads a log file from a starting position and then keeps following it as the game appends lines.
/// The file is opened with full sharing so the game can keep writing to it.
/// </summary>
public sealed class LogTailer(string path)
{
    private static readonly Encoding LogEncoding = Encoding.Latin1;

    public string Path { get; } = path;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public int MaxBatchLines { get; init; } = 2000;

    /// <param name="fromStart">true to read existing history first; false to start at the end of the file.</param>
    /// <param name="onLines">Invoked with batches of complete lines, on the tailer's thread.</param>
    /// <param name="onProgress">Invoked after each batch and whenever the reader catches up to the end of the file.</param>
    public async Task RunAsync(bool fromStart, Action<IReadOnlyList<string>> onLines, Action<TailProgress>? onProgress, CancellationToken ct)
    {
        await using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        long position = fromStart ? 0 : fs.Length;
        fs.Seek(position, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        var pending = new List<byte>(512);
        var batch = new List<string>(MaxBatchLines);
        long lines = 0;
        bool reportedCaughtUp = false;

        while (!ct.IsCancellationRequested)
        {
            long length = fs.Length;
            if (length < position)
            {
                // The file was truncated or replaced; start again from the top.
                position = 0;
                fs.Seek(0, SeekOrigin.Begin);
                pending.Clear();
            }

            int read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                Flush();
                if (!reportedCaughtUp)
                {
                    onProgress?.Invoke(new TailProgress(position, length, lines, true));
                    reportedCaughtUp = true;
                }

                // The loop condition handles cancellation; avoid throwing on every shutdown.
                await Task.Delay(PollInterval, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            reportedCaughtUp = false;
            position += read;
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                string line;
                if (pending.Count > 0)
                {
                    pending.AddRange(buffer.AsSpan(start, i - start));
                    line = LogEncoding.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));
                    pending.Clear();
                }
                else
                {
                    line = LogEncoding.GetString(buffer, start, i - start);
                }

                start = i + 1;
                line = line.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                batch.Add(line);
                lines++;
                if (batch.Count >= MaxBatchLines)
                {
                    Flush();
                    onProgress?.Invoke(new TailProgress(position, Math.Max(length, position), lines, false));
                }
            }

            if (start < read)
            {
                pending.AddRange(buffer.AsSpan(start, read - start));
            }
        }

        void Flush()
        {
            if (batch.Count == 0)
            {
                return;
            }

            onLines(batch.ToArray());
            batch.Clear();
        }
    }
}
