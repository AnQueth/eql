using EqLog.Application.Sessions;

namespace EqLog.Infrastructure.GameFiles;

/// <summary>Read-only log transport and discovery; filesystem probes never run on a WPF dispatcher.</summary>
public sealed class GameLogSource : ILogSource, ILogDiscovery, IDisposable
{
    private readonly object _gate = new();
    private FileChangeMonitor? _changes;
    private string? _directory;
    private long _scannedVersion = -1;
    private LogFileInfo? _newest;

    public Task RunAsync(string path, bool fromStart, Action<IReadOnlyList<string>> lines,
        Action<TailProgress> progress, CancellationToken cancellationToken)
    {
        return new LogTailer(path).RunAsync(fromStart, lines, progress, cancellationToken);
    }

    public Task<LogFileInfo> DescribeAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() => LogFileLocator.Describe(path), cancellationToken);
    }

    public Task<LogFileInfo?> FindNewestAsync(string directory, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                directory = Path.GetFullPath(directory);
                if (_directory != directory)
                {
                    _changes?.Dispose();
                    _changes = new FileChangeMonitor(directory, LogFileLocator.SearchPattern);
                    _directory = directory;
                    _scannedVersion = -1;
                }
                long version = _changes!.Version;
                if (_scannedVersion != version)
                {
                    var newest = LogFileLocator.FindNewest(directory);
                    if (_newest != newest)
                    {
                        _newest = newest;
                    }
                    _scannedVersion = version;
                }
                return _newest;
            }
        }, cancellationToken);
    }

    public Task<string> ResolveDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        return Task.Run(() => LogFileLocator.ResolveChosenDirectory(directory), cancellationToken);
    }

    public Task<bool> DirectoryExistsAsync(string directory, CancellationToken cancellationToken)
    {
        return Task.Run(() => Directory.Exists(directory), cancellationToken);
    }

    public Task<LogStartupSelection> FindInitialAsync(string[] arguments, string? savedPath, string? savedDirectory,
        bool followNewest, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            string? path = arguments.Skip(1).FirstOrDefault(File.Exists);
            if (path is null && savedPath is not null && File.Exists(savedPath))
            {
                path = savedPath;
            }
            if (path is null || followNewest)
            {
                string? directory = (path is not null ? Path.GetDirectoryName(path) : null) ??
                    savedDirectory ?? LogFileLocator.DetectDirectory();
                if (arguments.Length <= 1 && directory is not null && LogFileLocator.FindNewest(directory) is { } newest)
                {
                    path = newest.Path;
                }
            }
            string? waiting = path is null && savedDirectory is not null && Directory.Exists(savedDirectory)
                ? savedDirectory : null;
            return new LogStartupSelection(path, waiting);
        }, cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _changes?.Dispose();
            _changes = null;
            _directory = null;
        }
    }
}
