using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Infrastructure.GameFiles;

/// <summary>Reads game-owned exports without writing them. Metadata work stays off the calling UI thread.</summary>
public sealed class InventoryExportSource : IInventoryExportSource, IDisposable
{
    private readonly object _gate = new();
    private FileChangeMonitor? _changes;
    private string? _path;
    private long _inspectedVersion = -1;
    private InventoryFileStamp? _stamp;

    /// <summary>Finds the export above Logs using the existing eqlog character/server naming convention.</summary>
    public static string PathForLog(string logPath)
    {
        string name = Path.GetFileNameWithoutExtension(logPath);
        if (!name.StartsWith("eqlog_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Expected an eqlog_ log filename.", nameof(logPath));
        }
        string folder = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
        return Path.Combine(Directory.GetParent(folder)!.FullName, name[6..] + "-Inventory.txt");
    }

    /// <inheritdoc />
    public Task<InventoryFileStamp?> InspectAsync(string logPath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                string path = PathForLog(logPath);
                if (_path != path)
                {
                    _changes?.Dispose();
                    _changes = new FileChangeMonitor(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                    _path = path;
                    _inspectedVersion = -1;
                }
                long version = _changes!.Version;
                if (_inspectedVersion != version)
                {
                    var stamp = Inspect(path);
                    if (_stamp != stamp)
                    {
                        _stamp = stamp;
                    }
                    _inspectedVersion = version;
                }
                return _stamp;
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InventoryExportItem>?> ReadAsync(InventoryFileStamp expected, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            lock (_gate)
            {
                // Commit validation must probe fresh metadata, even if notifications arrive late.
                _inspectedVersion = -1;
            }
            if (Inspect(expected.Path) != expected)
            {
                return null;
            }
            // Preserve File.ReadAllTextAsync's sharing contract: do not parse while a writer holds the file.
            string text = await File.ReadAllTextAsync(expected.Path, cancellationToken).ConfigureAwait(false);
            if (Inspect(expected.Path) != expected)
            {
                return null;
            }
            return (IReadOnlyList<InventoryExportItem>)Array.AsReadOnly(InventoryExport.Parse(text).ToArray());
        }, cancellationToken);
    }

    private static InventoryFileStamp? Inspect(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new(path, file.Length, file.LastWriteTimeUtc) : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _changes?.Dispose();
            _changes = null;
            _path = null;
        }
    }
}
