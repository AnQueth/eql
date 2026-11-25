using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

/// <summary>The inventory/quest and Ready to merge overlays share the current character's inventory export.</summary>
public partial class MainWindow
{
    private IReadOnlyList<InventoryExportItem> _inventoryRows
    {
        get
        {
            return _inventoryView.Export?.Rows ?? [];
        }
    }
    private string? _inventoryRowsCharacter
    {
        get
        {
            return _inventoryView.Export?.Character;
        }
    }

    /// <summary>Filters only the displayed inventory rows, preserving ownership data and automatic lookups.</summary>
    internal static InventoryFilterWork ConfigureInventoryItemFilter(DataGrid grid, TextBox filter)
    {
        return InventoryFilterBinding.Attach(grid, filter);
    }

    /// <summary>Adds the split-out merge panel once, preserving existing inventory layouts and visibility.</summary>
    internal static void InitializeMergeOverlay(AppSettings settings, Rect work)
    {
        const string key = "merge";
        if (settings.Overlays.ContainsKey(key))
        {
            return;
        }

        var definition = OverlayPresenters.All.Single(def => def.Key == key);
        var placement = DefaultPlacement(definition, OverlayPresenters.All.Count - 1, work);
        if (settings.Overlays.TryGetValue("inventory", out var inventory))
        {
            var activeWorkspace = settings.DockWindow.EnsureActiveWorkspace(settings.Overlays);
            if (inventory.Docked && !settings.DockWindow.IsPanelInAnyWorkspace("inventory"))
            {
                activeWorkspace.Panels["inventory"] = DockPanelSettings.From(inventory);
            }
            placement = inventory.Clone();
            if (inventory.Left + inventory.Width + 8 + placement.Width <= work.Right)
            {
                placement.Left = inventory.Left + inventory.Width + 8;
            }
            else if (inventory.Top + inventory.Height + 8 + placement.Height <= work.Bottom)
            {
                placement.Top = inventory.Top + inventory.Height + 8;
            }
            else
            {
                placement.Left = work.Left + 40;
                placement.Top = work.Top + 40;
            }

            foreach (var workspace in settings.DockWindow.Workspaces)
            {
                if (!workspace.Panels.TryGetValue("inventory", out var inventoryTile) ||
                    workspace.Panels.ContainsKey(key))
                {
                    continue;
                }

                var bounds = workspace.Panels.Select(pair =>
                {
                    settings.Overlays.TryGetValue(pair.Key, out var floating);
                    var tile = pair.Value;
                    return new Rect(tile.DockLeft ?? 4, tile.DockTop ?? 4,
                        tile.DockWidth > 0 ? tile.DockWidth : floating?.Width ?? definition.Width,
                        tile.DockHeight > 0 ? tile.DockHeight : floating?.Height ?? definition.Height);
                });
                var position = DockWindow.AppendPosition(bounds);
                var mergeTile = inventoryTile.Clone();
                mergeTile.DockLeft = position.X;
                mergeTile.DockTop = position.Y;
                workspace.Panels[key] = mergeTile;
            }

            placement.Docked = settings.DockWindow.IsPanelInAnyWorkspace(key);
        }

        settings.Overlays[key] = placement;
    }

    /// <summary>Finds all ranks of an item while retaining container names from the complete export.</summary>
    internal static IReadOnlyList<InventoryItemLocation> ItemCopyLocations(
        IReadOnlyList<InventoryExportItem> items, string item)
    {
        return InventoryQueries.CopyLocations(items, item);
    }

    private async Task<string> ToggleInventoryQuestCompletionAsync(string questTitle)
    {
        bool completed = _inventoryService.ToggleCompletion(_characterName ?? "", questTitle);
        SynchronizeInventoryView();
        try
        {
            await _inventoryService.SaveAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
            return $"Could not save quest inventory: {error.Message}";
        }

        return completed
            ? "Quest marked completed. Run /outputfile inventory after turning items in to refresh counts."
            : "Quest marked incomplete.";
    }

    private void RetryInventoryQuestLookups()
    {
        _questAttempted.Clear();
        _questFailed.Clear();
        UpdateQuestProgress();
    }

    private async Task<QuestLookupResult> LookupInventoryQuestReferencesAsync(string item, CancellationToken cancellationToken)
    {
        if (WikiLookupPaused)
        {
            throw new InvalidOperationException("Wiki lookups are paused.");
        }

        using var lookup = BeginWikiLookup(cancellationToken);
        return await GetQuestResultAsync(item, lookup.Token);
    }

    private void RefreshInventoryItemLocations()
    {
        _inventoryView.SelectedItem = QuestOwnedGrid.SelectedItem as QuestInventoryItem;
        _inventoryView.RefreshCopies();
    }

    private void RefreshMergeView()
    {
        if (_overlayViewModels.TryGetValue("merge", out var viewModel) &&
            viewModel is MergeOverlayViewModel mergeViewModel)
        {
            mergeViewModel.Refresh();
        }
    }

    /// <summary>Call on the dispatcher when the inventory/session lifetime ends.</summary>
    internal void CancelInventoryFilters()
    {
        Dispatcher.VerifyAccess();
        _inventoryView.SuspendFilters();
        if (_overlayViewModels.TryGetValue("merge", out var viewModel) &&
            viewModel is MergeOverlayViewModel mergeViewModel)
        {
            mergeViewModel.SuspendFiltering();
        }
    }

    /// <summary>Shows the inventory overlay (docked or floating) for item and quest references.</summary>
    private void ShowInventoryOverlay()
    {
        const string key = "inventory";
        if (!_settings.Overlays[key].Visible)
        {
            _overlayToggles[key].IsChecked = true;
            SetOverlayVisible(key, true);
        }
        if (_settings.Overlays[key].Docked || _dock.HasPanel(key))
        {
            ShowDock();
            _dock.RevealPanel(key);
        }
        else
        {
            EnsureOnScreen(_overlays[key]);
            _overlays[key].ReassertTopmost();
        }
    }
}
