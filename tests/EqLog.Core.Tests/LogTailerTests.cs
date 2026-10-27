using EqLog.Core.IO;

namespace EqLog.Core.Tests;

public class LogTailerTests
{
    [Fact]
    public async Task ReadsHistoryThenFollowsAppendedLines()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eqlog_Test_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "[Wed Oct 21 17:07:04 2026] line one\r\n[Wed Oct 21 17:07:05 2026] line two\r\n");
        try
        {
            var received = new List<string>();
            var gotAppended = new TaskCompletionSource();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tailer = new LogTailer(path) { PollInterval = TimeSpan.FromMilliseconds(20) };
            var run = tailer.RunAsync(true, lines =>
            {
                lock (received)
                {
                    received.AddRange(lines);
                    if (received.Count == 4) gotAppended.TrySetResult();
                }
            }, null, cts.Token);

            await Task.Delay(200);
            await using (var w = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                // Write a line in two pieces to make sure partial lines are held back until complete.
                await w.WriteAsync("[Wed Oct 21 17:07:06 2026] line th"u8.ToArray());
                await w.FlushAsync();
                await Task.Delay(100);
                await w.WriteAsync("ree\r\n[Wed Oct 21 17:07:07 2026] line four\r\n"u8.ToArray());
            }

            await gotAppended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            Assert.Equal(
                ["[Wed Oct 21 17:07:04 2026] line one", "[Wed Oct 21 17:07:05 2026] line two", "[Wed Oct 21 17:07:06 2026] line three", "[Wed Oct 21 17:07:07 2026] line four"],
                received);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DescribesCharacterFromFileName()
    {
        var info = LogFileLocator.Describe(@"C:\x\eqlog_Quethrosar_qeynos.txt");
        Assert.Equal("Quethrosar", info.CharacterName);
        Assert.Equal("qeynos", info.Server);
    }
}
