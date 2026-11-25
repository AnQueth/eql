using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>One workspace tab (or one deleted workspace in the restore menu).</summary>
public sealed record DockWorkspaceTab(string Id, string Name, bool IsSelected);

/// <summary>Top bar, workspace tabs and notices of the dock window.</summary>
public sealed class DockWindowViewModel : ObservableObject
{
    private bool _autoArrange;
    private bool _isSidePanelOpen;
    private bool _canRestoreWorkspace;
    private object? _notice;
    private object? _loadingNotice;
    private bool _isWorkspaceEmpty = true;

    /// <summary>Workspaces shown as tabs.</summary>
    public BulkObservableCollection<DockWorkspaceTab> Workspaces { get; } = [];

    /// <summary>Deleted workspaces that can be restored.</summary>
    public BulkObservableCollection<DockWorkspaceTab> DeletedWorkspaces { get; } = [];

    /// <summary>Whether docked panels reflow to fill empty space.</summary>
    public bool AutoArrange
    {
        get
        {
            return _autoArrange;
        }
        set
        {
            if (SetProperty(ref _autoArrange, value))
            {
                OnPropertyChanged(nameof(DragHint));
            }
        }
    }

    /// <summary>How dragging panels behaves in the current arrange mode.</summary>
    public string DragHint
    {
        get
        {
            return AutoArrange
                ? "Drag header grips to reorder panels; panels reflow to fill empty space."
                : "Drag header grips to move panels; panels stay exactly where you put them.";
        }
    }

    /// <summary>Whether the settings side panel is shown.</summary>
    public bool IsSidePanelOpen
    {
        get
        {
            return _isSidePanelOpen;
        }
        set
        {
            SetProperty(ref _isSidePanelOpen, value);
        }
    }

    /// <summary>Whether any deleted workspace can be restored.</summary>
    public bool CanRestoreWorkspace
    {
        get
        {
            return _canRestoreWorkspace;
        }
        private set
        {
            SetProperty(ref _canRestoreWorkspace, value);
        }
    }

    /// <summary>A notice such as an available update, rendered by its data template; null hides it.</summary>
    public object? Notice
    {
        get
        {
            return _notice;
        }
        set
        {
            SetProperty(ref _notice, value);
        }
    }

    /// <summary>The source of the history loading notice; null hides it.</summary>
    public object? LoadingNotice
    {
        get
        {
            return _loadingNotice;
        }
        set
        {
            SetProperty(ref _loadingNotice, value);
        }
    }

    /// <summary>Whether the active workspace shows no panels.</summary>
    public bool IsWorkspaceEmpty
    {
        get
        {
            return _isWorkspaceEmpty;
        }
        set
        {
            SetProperty(ref _isWorkspaceEmpty, value);
        }
    }

    /// <summary>Rebuilds the tabs and restore choices from the saved workspaces.</summary>
    public void SetWorkspaces(IEnumerable<DockWorkspaceSettings> workspaces, string? activeWorkspaceId)
    {
        var all = workspaces.ToArray();
        Workspaces.ReplaceAll(all
            .Where(workspace => !workspace.Deleted)
            .Select(workspace => new DockWorkspaceTab(workspace.Id, workspace.Name, workspace.Id == activeWorkspaceId)));
        DeletedWorkspaces.ReplaceAll(all
            .Where(workspace => workspace.Deleted)
            .Select(workspace => new DockWorkspaceTab(workspace.Id, workspace.Name, IsSelected: false)));
        CanRestoreWorkspace = DeletedWorkspaces.Count > 0;
    }
}
