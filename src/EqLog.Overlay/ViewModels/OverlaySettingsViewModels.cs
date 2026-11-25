using System.Collections.ObjectModel;
using EqLog.Core.Parsing;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>One entry in an overlay's workspace selector.</summary>
public sealed record DockWorkspaceChoice(string? Id, string Name, bool IsMultiple = false);

/// <summary>The shell actions behind the overlay settings rows.</summary>
internal interface IOverlaySettingsHost
{
    void SetOverlayVisible(string key, bool visible);

    void SetDocked(string key, bool docked);

    void SetOverlayLocked(string key, bool locked);

    void MoveOverlayToWorkspace(string key, string? workspaceId);

    void SetOverlayFontSize(string key, double value);

    void SetOverlayBackgroundOpacity(string key, double value);

    void SetOverlayHoverBackgroundOpacity(string key, double value);
}

/// <summary>An overlay's visibility, docking, lock, and appearance settings in the settings panel.</summary>
public sealed class OverlaySettingViewModel : ObservableObject
{
    private readonly IOverlaySettingsHost _host;
    private bool _isVisible;
    private bool _isDocked;
    private bool _isLocked;
    private double _fontSize;
    private double _backgroundOpacity;
    private double _hoverBackgroundOpacity;
    private IReadOnlyList<DockWorkspaceChoice> _workspaceChoices = [];
    private DockWorkspaceChoice? _selectedWorkspace;
    private bool _refreshingWorkspaces;

    internal OverlaySettingViewModel(string key, string caption, OverlayPlacement placement, bool isDocked,
        IOverlaySettingsHost host)
    {
        Key = key;
        Caption = caption;
        _host = host;
        _isVisible = placement.Visible;
        _isDocked = isDocked;
        _isLocked = placement.Locked == true;
        _fontSize = placement.FontSize ?? 13;
        _backgroundOpacity = placement.BackgroundOpacity ?? 1;
        _hoverBackgroundOpacity = placement.HoverBackgroundOpacity ?? _backgroundOpacity;
    }

    public string Key
    {
        get;
    }

    public string Caption
    {
        get;
    }

    public string FontSizeToolTip
    {
        get
        {
            return $"Text size for {Caption}";
        }
    }

    public string BackgroundToolTip
    {
        get
        {
            return $"Background for {Caption}";
        }
    }

    public string HoverBackgroundToolTip
    {
        get
        {
            return $"Mouse-over background for {Caption}";
        }
    }

    /// <summary>Whether the overlay is shown; setting it shows or hides the overlay.</summary>
    public bool IsVisible
    {
        get
        {
            return _isVisible;
        }
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                _host.SetOverlayVisible(Key, value);
            }
        }
    }

    /// <summary>Whether the panel is in the active workspace; setting it docks or undocks the panel there.</summary>
    public bool IsDocked
    {
        get
        {
            return _isDocked;
        }
        set
        {
            if (SetProperty(ref _isDocked, value))
            {
                _host.SetDocked(Key, value);
            }
        }
    }

    /// <summary>Whether the floating overlay is click-through; setting it locks or unlocks the overlay.</summary>
    public bool IsLocked
    {
        get
        {
            return _isLocked;
        }
        set
        {
            if (SetProperty(ref _isLocked, value))
            {
                _host.SetOverlayLocked(Key, value);
            }
        }
    }

    public double FontSize
    {
        get
        {
            return _fontSize;
        }
        set
        {
            if (SetProperty(ref _fontSize, value))
            {
                _host.SetOverlayFontSize(Key, value);
            }
        }
    }

    public double BackgroundOpacity
    {
        get
        {
            return _backgroundOpacity;
        }
        set
        {
            if (SetProperty(ref _backgroundOpacity, value))
            {
                _host.SetOverlayBackgroundOpacity(Key, value);
            }
        }
    }

    public double HoverBackgroundOpacity
    {
        get
        {
            return _hoverBackgroundOpacity;
        }
        set
        {
            if (SetProperty(ref _hoverBackgroundOpacity, value))
            {
                _host.SetOverlayHoverBackgroundOpacity(Key, value);
            }
        }
    }

    public IReadOnlyList<DockWorkspaceChoice> WorkspaceChoices
    {
        get
        {
            return _workspaceChoices;
        }
        private set
        {
            SetProperty(ref _workspaceChoices, value);
        }
    }

    /// <summary>The selected workspace; choosing a real workspace (or "Not docked") moves the overlay.</summary>
    public DockWorkspaceChoice? SelectedWorkspace
    {
        get
        {
            return _selectedWorkspace;
        }
        set
        {
            if (!SetProperty(ref _selectedWorkspace, value) ||
                _refreshingWorkspaces ||
                value is null ||
                value.IsMultiple)
            {
                return;
            }

            _host.MoveOverlayToWorkspace(Key, value.Id);
        }
    }

    /// <summary>Shows visibility the shell already applied.</summary>
    internal void SyncVisible(bool visible)
    {
        if (_isVisible != visible)
        {
            _isVisible = visible;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    /// <summary>Shows active-workspace membership the shell already applied.</summary>
    internal void SyncDocked(bool docked)
    {
        if (_isDocked != docked)
        {
            _isDocked = docked;
            OnPropertyChanged(nameof(IsDocked));
        }
    }

    /// <summary>Shows a lock the shell already applied.</summary>
    internal void SyncLocked(bool locked)
    {
        if (_isLocked != locked)
        {
            _isLocked = locked;
            OnPropertyChanged(nameof(IsLocked));
        }
    }

    /// <summary>Replaces the selector's choices without treating the new selection as a move.</summary>
    public void SetWorkspaceChoices(IReadOnlyList<DockWorkspaceChoice> choices, DockWorkspaceChoice? selected)
    {
        _refreshingWorkspaces = true;
        try
        {
            WorkspaceChoices = choices;
            SelectedWorkspace = selected;
        }
        finally
        {
            _refreshingWorkspaces = false;
        }
    }
}

/// <summary>A chat channel shown or hidden by the chat overlay.</summary>
public sealed class ChatChannelOptionViewModel : ObservableObject
{
    private readonly Action<ChatChannel, bool> _changed;
    private bool _isEnabled;

    public ChatChannelOptionViewModel(ChatChannel channel, bool isEnabled, Action<ChatChannel, bool> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        Channel = channel;
        _isEnabled = isEnabled;
        _changed = changed;
    }

    public ChatChannel Channel
    {
        get;
    }

    public string Name
    {
        get
        {
            return Channel.ToString();
        }
    }

    public bool IsEnabled
    {
        get
        {
            return _isEnabled;
        }
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _changed(Channel, value);
            }
        }
    }
}

/// <summary>The overlay and chat channel lists in the settings panel.</summary>
public sealed class SettingsPanelViewModel
{
    public ObservableCollection<OverlaySettingViewModel> Overlays { get; } = [];

    public ObservableCollection<ChatChannelOptionViewModel> ChatChannels { get; } = [];
}
