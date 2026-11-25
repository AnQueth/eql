using EqLog.Infrastructure.GameFiles;

namespace EqLog.Infrastructure.Tests;

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
            var tailer = new LogTailer(path) { PollInterval = TimeSpan.FromHours(1) };
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
    public async Task TrimsTrailingCarriageReturnsAcrossBufferBoundaryAndPreservesOtherContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eqlog_Test_{Guid.NewGuid():N}.txt");
        byte[] initial = new byte[1 << 16];
        Array.Fill(initial, (byte)'x');
        initial[^1] = (byte)'\r';
        await File.WriteAllBytesAsync(path, initial);

        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var gotCompleteLines = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = new LogTailer(path) { PollInterval = TimeSpan.FromHours(1) }.RunAsync(true, lines =>
        {
            foreach (string line in lines)
            {
                received.Enqueue(line);
            }

            if (received.Count >= 2)
            {
                gotCompleteLines.TrySetResult();
            }
        }, null, cts.Token);

        try
        {
            await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                byte[] appended = System.Text.Encoding.Latin1.GetBytes("\r\n\r\r\nmiddle\rcode\r\r\npartial");
                await stream.WriteAsync(appended);
            }

            await gotCompleteLines.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await cts.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            File.Delete(path);
        }

        Assert.Equal([new string('x', (1 << 16) - 1), "middle\rcode"], received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowsTruncatedAndReplacedLogsWithoutWaitingForRecovery(bool replace)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"eql-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "eqlog_Test_server.txt");
        await File.WriteAllTextAsync(path, "old line with a partial suffix\npartial");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var run = new LogTailer(path) { PollInterval = TimeSpan.FromHours(1) }.RunAsync(true, lines =>
        {
            foreach (string line in lines)
            {
                received.Enqueue(line);
                if (line == "new")
                {
                    updated.TrySetResult();
                }
            }
        }, progress =>
        {
            if (progress.CaughtUp)
            {
                caughtUp.TrySetResult();
            }
        }, cts.Token);
        try
        {
            await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (replace)
            {
                string replacement = Path.Combine(folder, "replacement.txt");
                await File.WriteAllTextAsync(replacement, "new\n");
                File.Move(path, Path.Combine(folder, "old.txt"));
                File.Move(replacement, path);
            }
            else
            {
                await File.WriteAllTextAsync(path, "new\n");
            }
            await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await cts.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            Directory.Delete(folder, recursive: true);
        }
        Assert.Equal(["old line with a partial suffix", "new"], received);
    }

    [Fact]
    public async Task ChangeMonitorWaitsWhileIdleAndCoalescesNotifications()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"eql-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using var monitor = new FileChangeMonitor(folder, "*.txt", TimeSpan.FromHours(1));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            long version = monitor.Version;
            var waiting = monitor.WaitAsync(cancellation.Token);
            await Task.Delay(250);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(version, monitor.Version);
            await File.WriteAllTextAsync(Path.Combine(folder, "changed.txt"), "changed");
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(monitor.Version > version);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoveryCachesIdleResultsAndRefreshesAfterFileChanges()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"eql-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string firstPath = Path.Combine(folder, "eqlog_First_server.txt");
            string secondPath = Path.Combine(folder, "eqlog_Second_server.txt");
            await File.WriteAllTextAsync(firstPath, "first");
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddDays(-1));
            using var source = new GameLogSource();
            var first = await source.FindNewestAsync(folder, default);
            Assert.Equal(firstPath, first!.Path);
            Assert.Same(first, await source.FindNewestAsync(folder, default));
            await File.WriteAllTextAsync(secondPath, "second");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EqLog.Application.Sessions.LogFileInfo? newest;
            do
            {
                await Task.Delay(20, timeout.Token);
                newest = await source.FindNewestAsync(folder, timeout.Token);
            }
            while (newest?.Path != secondPath);
            Assert.Same(newest, await source.FindNewestAsync(folder, default));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryProbeStartsMonitoringAnInitiallyMissingFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"eql-recovery-{Guid.NewGuid():N}");
        try
        {
            using var monitor = new FileChangeMonitor(folder, "*.txt", TimeSpan.FromMilliseconds(50));
            long first = monitor.Version;
            Directory.CreateDirectory(folder);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await monitor.WaitAsync(timeout.Token);
            Assert.True(monitor.Version > first);
            long recovered = monitor.Version;
            await File.WriteAllTextAsync(Path.Combine(folder, "created.txt"), "new");
            await monitor.WaitAsync(timeout.Token);
            Assert.True(monitor.Version > recovered);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
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
