using System.IO;
using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private InventoryFileStamp? _inventoryLoaded
    {
        get
        {
            return _inventoryView.Export?.Stamp;
        }
    }

    private void SynchronizeInventoryView()
    {
        if (_inventoryView.Revision == _inventoryService.Revision)
        {
            return;
        }
        var previous = _inventoryView.Export;
        _inventoryView.Apply(_inventoryService.Snapshot(), _characterName ?? "");
        if (!ReferenceEquals(previous, _inventoryView.Export))
        {
            _exaltationLocations = Exaltations.Locate(_inventoryRows).Concat(Exaltations.WikiDefaults(_inventoryRows)).ToArray();
            RefreshMergeView();
            RefreshMotePlan();
        }
    }

    private async Task CheckInventoryAsync()
    {
        if (_currentFile is null || _characterName is null)
        {
            return;
        }
        string log = _currentFile;
        try
        {
            var result = await _inventoryService.PollAsync(_wikiCts.Token);
            if (_currentFile != log || result.Status == InventoryImportStatus.Superseded)
            {
                return;
            }
            SynchronizeInventoryView();
            switch (result.Status)
            {
                case InventoryImportStatus.Missing:
                    _inventoryView.ImportStatus = "Waiting for this character's inventory export. Create/update it in game with /outputfile inventory.";
                    break;
                case InventoryImportStatus.Stabilizing:
                    _inventoryView.ImportStatus = "Inventory export detected; waiting for writing to finish.";
                    break;
                case InventoryImportStatus.Imported:
                    var export = result.Export!;
                    var items = export.Rows;
                    var unknown = InventoryExport.UnknownLocations(items);
                    string sections = unknown.Count == 0
                        ? "All sections imported."
                        : $"Unrecognised locations counted as Other: {string.Join(", ", unknown)}.";
                    _inventoryView.ImportStatus = $"Inventory updated {export.Stamp.Written.ToLocalTime():g}: {items.Count:N0} nonempty entries; " +
                        $"{items.Count(i => i.Collection):N0} collection entries, {items.Count(i => i.Installed):N0} installed augment entries. " +
                        $"{sections} Monitoring {export.Stamp.Path}";
                    await SaveQuestInventoryAsync();
                    break;
            }
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested)
        {
            // Shutdown intentionally cancels pending imports.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.InventoryImport);
            if (_currentFile == log)
            {
                _inventoryView.ImportStatus = $"Inventory import failed (previous counts kept): {ex.Message}. Retrying automatically.";
            }
        }
    }
}
