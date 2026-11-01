using EqLog.Application.Sessions;

namespace EqLog.Infrastructure.GameFiles;

/// <summary>Read-only log transport and discovery; filesystem probes never run on a WPF dispatcher.</summary>
public sealed class GameLogSource : ILogSource, ILogDiscovery
{
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
        return Task.Run(() => LogFileLocator.FindNewest(directory), cancellationToken);
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
}
