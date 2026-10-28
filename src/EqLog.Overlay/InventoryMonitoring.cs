using System.IO;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private bool _inventoryReading;
    private (string Path, long Size, DateTime Written)? _inventoryObserved;
    private (string Path, long Size, DateTime Written)? _inventoryLoaded;

    private async Task CheckInventoryAsync()
    {
        if (_inventoryReading || _currentFile is null || _characterName is null)
        {
            return;
        }

        string path = InventoryExport.PathForLog(_currentFile);
        string character = _characterName;
        string log = _currentFile;
        _inventoryReading = true;
        try
        {
            // File metadata has no async API; keep it off the UI thread.
            if (await Task.Run(() => Stamp(path)) is not { } stamp)
            {
                InventoryStatus.Text = $"Waiting for {path}. Create/update it in game with /outputfile inventory.";
                _inventoryObserved = null;
                _inventoryLoaded = null;
                SetInventoryRows(character, []);
                return;
            }
            if (_inventoryLoaded == stamp)
            {
                return;
            }

            if (_inventoryObserved != stamp)
            {
                _inventoryObserved = stamp;
                InventoryStatus.Text = $"Inventory export detected; waiting for writing to finish: {path}";
                return;
            }
            string text = await File.ReadAllTextAsync(path, _wikiCts.Token);
            var items = InventoryExport.Parse(text);
            if (await Task.Run(() => Stamp(path)) != stamp || _currentFile != log)
            {
                return;
            }
            // Apply queued loot first so the snapshot can reconcile it by export time.
            while (_questLoot.TryDequeue(out var note))
            {
                note();
            }

            _questInventory.ApplyExport(character, items, stamp.Written);
            _questDirty = true;
            _inventoryLoaded = stamp;
            SetInventoryRows(character, items);
            QuestOwnedGrid.Items.Refresh();
            var unknown = InventoryExport.UnknownLocations(items);
            string sections = unknown.Count == 0
                ? "All sections imported."
                : $"Unrecognised locations counted as Other: {string.Join(", ", unknown)}.";
            InventoryStatus.Text = $"Inventory updated {stamp.Written.ToLocalTime():g}: {items.Count:N0} nonempty entries; " +
                $"{items.Count(i => i.Collection):N0} collection entries, {items.Count(i => i.Installed):N0} installed augment entries. " +
                $"{sections} Monitoring {path}";
            await SaveQuestInventoryAsync();
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            InventoryStatus.Text = $"Inventory import failed (previous counts kept): {ex.Message}. Retrying automatically.";
        }
        finally
        {
            _inventoryReading = false;
        }
    }

    private static (string Path, long Size, DateTime Written)? Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (path, file.Length, file.LastWriteTimeUtc) : null;
    }
}
