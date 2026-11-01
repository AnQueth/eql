using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Infrastructure.GameFiles;

/// <summary>Reads game-owned exports without writing them. Metadata work stays off the calling UI thread.</summary>
public sealed class InventoryExportSource : IInventoryExportSource
{
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
        return Task.Run(() => Inspect(PathForLog(logPath)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InventoryExportItem>?> ReadAsync(InventoryFileStamp expected, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
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
}
