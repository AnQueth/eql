using EqLog.Core;
using EqLog.Core.Parsing;

namespace EqLog.Application.Sessions;

/// <summary>Detached input-file identity and discovery metadata.</summary>
public sealed record LogFileInfo(string Path, string CharacterName, string Server, DateTime LastWriteTime, long Length);

/// <summary>Reader progress after complete batches and on reaching the current end of the file.</summary>
public readonly record struct TailProgress(long BytesRead, long TotalBytes, long LinesRead, bool CaughtUp);

/// <summary>Startup discovery outcome; a waiting directory is distinct from finding no valid input folder.</summary>
public sealed record LogStartupSelection(string? Path, string? WaitingDirectory);

/// <summary>Read-only discovery and path policy for game log inputs.</summary>
public interface ILogDiscovery
{
    Task<LogFileInfo> DescribeAsync(string path, CancellationToken cancellationToken);
    Task<LogFileInfo?> FindNewestAsync(string directory, CancellationToken cancellationToken);
    Task<LogStartupSelection> FindInitialAsync(string[] arguments, string? savedPath, string? savedDirectory,
        bool followNewest, CancellationToken cancellationToken);
    Task<string> ResolveDirectoryAsync(string directory, CancellationToken cancellationToken);
    Task<bool> DirectoryExistsAsync(string directory, CancellationToken cancellationToken);
}

/// <summary>Feeds complete line batches with progress; callers own cancellation and await termination.</summary>
public interface ILogSource
{
    Task RunAsync(string path, bool fromStart, Action<IReadOnlyList<string>> lines,
        Action<TailProgress> progress, CancellationToken cancellationToken);
}

/// <summary>A processed session batch; initial replay never counts as live input.</summary>
public sealed record LogSessionBatch(string Source, string Character, IReadOnlyList<string> Lines, bool Live);

/// <summary>A render-time snapshot, independent of mutable trackers and source lifetimes.</summary>
public sealed record LogSessionFrame(EngineSnapshot Snapshot, IReadOnlyList<ChatEvent> Chat, bool Replay,
    EngineQuerySnapshot Queries);
