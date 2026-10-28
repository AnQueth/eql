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
                    if (received.Count == 4)
                    {
                        gotAppended.TrySetResult();
                    }
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
            // Cancellation ends the tail loop; it normally returns instead of throwing.
            await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            Assert.True(run.IsCompleted);

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

    [Fact]
    public void DetectsTheCandidateFolderWithTheNewestLog_AndResolvesAnInstallFolderToItsLogs()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string legends = Path.Combine(root, "EverQuest Legends", "Logs");
        string live = Path.Combine(root, "EverQuest", "Logs");
        string empty = Path.Combine(root, "Empty");
        try
        {
            Directory.CreateDirectory(legends);
            Directory.CreateDirectory(live);
            Directory.CreateDirectory(empty);
            Assert.Null(LogFileLocator.DetectDirectory([empty, Path.Combine(root, "Missing")]));

            File.WriteAllText(Path.Combine(legends, "eqlog_Old_server.txt"), "x");
            File.SetLastWriteTime(Path.Combine(legends, "eqlog_Old_server.txt"), DateTime.Now.AddDays(-1));
            File.WriteAllText(Path.Combine(live, "eqlog_New_server.txt"), "x");
            Assert.Equal(live, LogFileLocator.DetectDirectory([empty, legends, live]));

            Assert.Equal(live, LogFileLocator.ResolveChosenDirectory(Path.Combine(root, "EverQuest")));
            Assert.Equal(live, LogFileLocator.ResolveChosenDirectory(live));
            Assert.Equal(empty, LogFileLocator.ResolveChosenDirectory(empty));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
