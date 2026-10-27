using Velopack;
using Velopack.Locators;
using Velopack.Sources;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: EqLog.UpdateSmoke <local-release-directory> <installed-version> <expected-update-version>");
    return 2;
}

string scratch = Path.Combine(Path.GetTempPath(), $"EqLogUpdateSmoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
var elapsed = System.Diagnostics.Stopwatch.StartNew();
try
{
    Console.WriteLine("Phase 1/3: checking a local feed using an isolated simulated installation");
    var locator = new TestVelopackLocator("EqLogOverlay", args[1], scratch);
    var source = new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(args[0])));
    var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win" }, locator);
    var update = await manager.CheckForUpdatesAsync()
        ?? throw new InvalidOperationException("The feed did not offer a newer release.");
    if (update.TargetFullRelease.Version.ToString() != args[2])
        throw new InvalidOperationException($"Expected {args[2]}, got {update.TargetFullRelease.Version}.");

    Console.WriteLine($"Phase 2/3: downloading and verifying {args[1]} -> {args[2]}");
    await manager.DownloadUpdatesAsync(update, percent => Console.WriteLine($"Download {args[2]}: {percent}%"));
    var pending = manager.UpdatePendingRestart
        ?? throw new InvalidOperationException("The downloaded update is not ready to apply.");
    if (pending.Version.ToString() != args[2])
        throw new InvalidOperationException("The staged version does not match the offered update.");
    if (!File.Exists(Path.Combine(scratch, update.TargetFullRelease.FileName)))
        throw new InvalidOperationException("The full update package was not staged.");

    Console.WriteLine("Phase 3/3: rejecting unnecessary updates/downgrades");
    var current = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win" },
        new TestVelopackLocator("EqLogOverlay", args[2], scratch));
    if (await current.CheckForUpdatesAsync() is not null)
        throw new InvalidOperationException("The current version must not be offered an update.");
    var newer = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win" },
        new TestVelopackLocator("EqLogOverlay", "9999.0.0", scratch));
    if (await newer.CheckForUpdatesAsync() is not null)
        throw new InvalidOperationException("A downgrade must not be offered.");
    Console.WriteLine($"Processed 5, succeeded 5, failed 0, skipped 0. Elapsed: {elapsed.Elapsed}.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Update smoke test failed: {ex}");
    return 1;
}
finally
{
    Directory.Delete(scratch, recursive: true);
}
