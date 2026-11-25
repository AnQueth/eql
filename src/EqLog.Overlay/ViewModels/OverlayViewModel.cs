using System.Collections.ObjectModel;
using System.Windows.Input;
using EqLog.Core.Parsing;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>
/// The state of one overlay panel: header, rows, appearance and placement. Subclasses own per-overlay options and turn
/// pump frames into content; the shared <c>OverlayPanel</c> view renders the chrome and the rows.
/// </summary>
public class OverlayViewModel : ObservableObject, IOverlayFrameReceiver
{
    private static readonly string[] ChromeProperties =
    [
        nameof(ShowMoveHint), nameof(ShowDragHandle), nameof(ShowWorkspaceButton), nameof(ShowDockButton),
        nameof(DockButtonText), nameof(DockButtonToolTip), nameof(ShowBackgroundControls), nameof(IsHeaderHidden),
        nameof(IsChromeDecorationHidden),
    ];

    private static readonly IReadOnlySet<ChatChannel> NoChannels = new HashSet<ChatChannel>();

    private string _title;
    private string _subtitle = "";
    private bool _subtitleReserved;
    private string? _titleLink;
    private bool _isShowing;
    private double _fontSize = 13;
    private double _backgroundOpacity = 0.55;
    private double? _hoverBackgroundOpacity;
    private bool _isBackgroundHovered;
    private bool _isLocked;
    private bool _isDocked;
    private bool _isTemporarilyDocked;
    private bool _isChromeHidden;

    /// <summary>Creates an overlay that renders <paramref name="definition"/> on every frame.</summary>
    public OverlayViewModel(OverlayDefinition definition, IOverlayActions actions)
        : this(definition.Key, definition.Caption, definition.AutoScroll, actions)
    {
        Definition = definition;
    }

    /// <summary>Creates an overlay without a presenter; content is supplied through <see cref="Show(OverlayContent)"/>.</summary>
    public OverlayViewModel(string key, string caption, bool autoScrollToEnd = false, IOverlayActions? actions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(caption);
        Key = key;
        Caption = caption;
        AutoScrollToEnd = autoScrollToEnd;
        Actions = actions ?? NullOverlayActions.Instance;
        _title = caption;
        ToggleDockCommand = new RelayCommand(() => Actions.ToggleDock(this));
        MoveToWorkspaceCommand = new RelayCommand<WorkspaceOption>(
            option => Actions.MoveToWorkspace(this, option.Id), option => option.IsEnabled);
        OpenTitleLinkCommand = new RelayCommand(OpenTitleLink, () => _titleLink is not null);
        ActivateRowCommand = new RelayCommand<OverlayRowViewModel>(ActivateRow, row => row.IsClickable);
        OpenItemWikiCommand = new RelayCommand<OverlayRowViewModel>(
            row => Actions.OpenItemWiki(row.Row.LootItem!), row => row.Row.LootItem is not null);
        LookupQuestReferencesCommand = new RelayCommand<OverlayRowViewModel>(
            row => Actions.LookupQuestReferences(row.Row.LootItem!), row => row.Row.LootItem is not null);
        ToggleWatchCommand = new RelayCommand<OverlayRowViewModel>(
            row => OnWatchToggled(row.Row.WatchKey!), row => row.Row.WatchKey is not null);
        ToggleSpawnTrackCommand = new RelayCommand<OverlayRowViewModel>(
            row => OnSpawnTrackChanged(row.Row.SpawnTrack! with { Tracked = !row.Row.SpawnTrack.Tracked }),
            row => row.Row.SpawnTrack is not null);
        ToggleSpellTrackCommand = new RelayCommand<OverlayRowViewModel>(
            row => OnSpellTrackChanged(row.Row.SpellTrack! with { Tracked = !row.Row.SpellTrack.Tracked }),
            row => row.Row.SpellTrack is not null);
        OpenLinkCommand = new RelayCommand<string>(url => Actions.OpenLink(url), url => url.Length > 0);
    }

    /// <summary>The settings and placement key.</summary>
    public string Key
    {
        get;
    }

    /// <summary>The display name used in settings and the dock.</summary>
    public string Caption
    {
        get;
    }

    /// <summary>Whether the row list follows new rows when it was scrolled to the end.</summary>
    public bool AutoScrollToEnd
    {
        get;
    }

    /// <summary>The presenter this overlay renders, if any.</summary>
    public OverlayDefinition? Definition
    {
        get;
    }

    /// <summary>Whether the rows are virtualized; long lists (quests, recipes, NPCs) only build visible rows.</summary>
    public bool UsesVirtualRows
    {
        get
        {
            return Key is "quests" or "sky" or "npcs" or "exaltations" or "tradeskills" or "leveling";
        }
    }

    /// <summary>Whether the row list shows its scroll bar; other overlays scroll with the wheel only.</summary>
    public bool ShowsRowScrollBar
    {
        get
        {
            return Key == "dps";
        }
    }

    /// <summary>Whether rows are vertically centered (alerts).</summary>
    public bool CentersRows
    {
        get
        {
            return Key == "alerts";
        }
    }

    /// <summary>The shell actions rows and chrome invoke.</summary>
    protected IOverlayActions Actions
    {
        get;
    }

    /// <summary>The rows, reconciled in place so unchanged rows keep their visuals.</summary>
    public BulkObservableCollection<OverlayRowViewModel> Rows { get; } = [];

    /// <summary>Other workspaces this docked overlay can move to.</summary>
    public ObservableCollection<WorkspaceOption> WorkspaceOptions { get; } = [];

    /// <summary>The header title.</summary>
    public string Title
    {
        get
        {
            return _title;
        }
        set
        {
            SetProperty(ref _title, value ?? "");
        }
    }

    /// <summary>The header subtitle.</summary>
    public string Subtitle
    {
        get
        {
            return _subtitle;
        }
        set
        {
            string text = value ?? "";
            if (SetProperty(ref _subtitle, text))
            {
                OnPropertyChanged(nameof(HasSubtitle));
            }
            // Once a subtitle has appeared, keep its line reserved so rows below never jump under the mouse.
            if (text.Length > 0 && !_subtitleReserved)
            {
                _subtitleReserved = true;
                OnPropertyChanged(nameof(IsSubtitleReserved));
            }
        }
    }

    /// <summary>Whether the subtitle has text.</summary>
    public bool HasSubtitle
    {
        get
        {
            return _subtitle.Length > 0;
        }
    }

    /// <summary>Whether a subtitle has ever been shown, so its line stays reserved when it is empty.</summary>
    public bool IsSubtitleReserved
    {
        get
        {
            return _subtitleReserved;
        }
    }

    /// <summary>A URL the title opens when clicked.</summary>
    public string? TitleLink
    {
        get
        {
            return _titleLink;
        }
        set
        {
            if (SetProperty(ref _titleLink, value))
            {
                OnPropertyChanged(nameof(HasTitleLink));
                OpenTitleLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether the title is a link.</summary>
    public bool HasTitleLink
    {
        get
        {
            return _titleLink is not null;
        }
    }

    /// <summary>Whether the overlay is enabled and on screen; hidden overlays receive no frames.</summary>
    public bool IsShowing
    {
        get
        {
            return _isShowing;
        }
        set
        {
            if (SetProperty(ref _isShowing, value))
            {
                OnShowingChanged(value);
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
            SetProperty(ref _fontSize, value);
        }
    }

    /// <summary>The floating background opacity, 0-1.</summary>
    public double BackgroundOpacity
    {
        get
        {
            return _backgroundOpacity;
        }
        set
        {
            if (SetProperty(ref _backgroundOpacity, Math.Clamp(value, 0, 1)))
            {
                OnPropertiesChanged(nameof(EffectiveBackgroundOpacity), nameof(BackgroundOpacityToolTip));
            }
        }
    }

    /// <summary>The opacity used while the mouse is over the overlay, when higher than <see cref="BackgroundOpacity"/>.</summary>
    public double? HoverBackgroundOpacity
    {
        get
        {
            return _hoverBackgroundOpacity;
        }
        set
        {
            if (SetProperty(ref _hoverBackgroundOpacity, value is { } opacity ? Math.Clamp(opacity, 0, 1) : null))
            {
                OnPropertyChanged(nameof(EffectiveBackgroundOpacity));
            }
        }
    }

    /// <summary>Whether the mouse is over the overlay.</summary>
    public bool IsBackgroundHovered
    {
        get
        {
            return _isBackgroundHovered;
        }
        set
        {
            if (SetProperty(ref _isBackgroundHovered, value))
            {
                OnPropertyChanged(nameof(EffectiveBackgroundOpacity));
            }
        }
    }

    /// <summary>The background opacity currently drawn.</summary>
    public double EffectiveBackgroundOpacity
    {
        get
        {
            return _isBackgroundHovered
                ? Math.Max(_backgroundOpacity, _hoverBackgroundOpacity ?? _backgroundOpacity)
                : _backgroundOpacity;
        }
    }

    /// <summary>The background slider tooltip.</summary>
    public string BackgroundOpacityToolTip
    {
        get
        {
            return $"Background opacity: {_backgroundOpacity:P0}";
        }
    }

    /// <summary>Whether the floating overlay is locked (click-through, no chrome controls).</summary>
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
                OnPropertiesChanged(ChromeProperties);
            }
        }
    }

    /// <summary>Whether the overlay is a tile in the dock window.</summary>
    public bool IsDocked
    {
        get
        {
            return _isDocked;
        }
        private set
        {
            if (SetProperty(ref _isDocked, value))
            {
                OnPropertiesChanged(ChromeProperties);
            }
        }
    }

    /// <summary>Whether the overlay is only temporarily shown in the current workspace.</summary>
    public bool IsTemporarilyDocked
    {
        get
        {
            return _isTemporarilyDocked;
        }
        private set
        {
            if (SetProperty(ref _isTemporarilyDocked, value))
            {
                OnPropertiesChanged(ChromeProperties);
            }
        }
    }

    /// <summary>Whether the floating overlay hides its header and frame until hovered.</summary>
    public bool IsChromeHidden
    {
        get
        {
            return _isChromeHidden;
        }
        set
        {
            if (SetProperty(ref _isChromeHidden, value))
            {
                OnPropertiesChanged(ChromeProperties);
            }
        }
    }

    /// <summary>Whether the unlocked-move hint glyph shows.</summary>
    public bool ShowMoveHint
    {
        get
        {
            return !_isLocked && !_isDocked;
        }
    }

    /// <summary>Whether the drag grip shows and accepts drags.</summary>
    public bool ShowDragHandle
    {
        get
        {
            return _isDocked || !_isLocked;
        }
    }

    /// <summary>Whether the "Move to" workspace button shows.</summary>
    public bool ShowWorkspaceButton
    {
        get
        {
            return _isDocked;
        }
    }

    /// <summary>Whether the dock / pop-out button shows. Locked floating overlays are click-through.</summary>
    public bool ShowDockButton
    {
        get
        {
            return _isDocked || !_isLocked;
        }
    }

    /// <summary>The dock button caption.</summary>
    public string DockButtonText
    {
        get
        {
            return _isDocked && !_isTemporarilyDocked ? "\u2197 Pop out" : "\u2199 Dock";
        }
    }

    /// <summary>The dock button tooltip.</summary>
    public string DockButtonToolTip
    {
        get
        {
            if (_isTemporarilyDocked)
            {
                return "Keep this panel in the current workspace";
            }
            return _isDocked ? "Pop this panel out into its own floating overlay" : "Move this overlay into the docked window";
        }
    }

    /// <summary>Whether the floating background slider shows.</summary>
    public bool ShowBackgroundControls
    {
        get
        {
            return !_isDocked && !_isLocked;
        }
    }

    /// <summary>Whether hidden chrome hides the frame border and ornaments.</summary>
    public bool IsChromeDecorationHidden
    {
        get
        {
            return _isChromeHidden && !_isDocked;
        }
    }

    /// <summary>Whether hidden chrome hides the header (keeping its space). Mob info keeps its header.</summary>
    public bool IsHeaderHidden
    {
        get
        {
            return IsChromeDecorationHidden && Key != "mobinfo";
        }
    }

    /// <summary>Whether this overlay wants pump frames.</summary>
    public virtual bool WantsFrames
    {
        get
        {
            return _isShowing;
        }
    }

    /// <summary>Optional data this overlay's frames need.</summary>
    public virtual FrameRequirements Requirements
    {
        get
        {
            return FrameRequirements.None;
        }
    }

    /// <summary>Docks or pops out the overlay.</summary>
    public RelayCommand ToggleDockCommand
    {
        get;
    }

    /// <summary>Moves the docked overlay to the <see cref="WorkspaceOption"/> parameter.</summary>
    public RelayCommand<WorkspaceOption> MoveToWorkspaceCommand
    {
        get;
    }

    /// <summary>Opens <see cref="TitleLink"/>.</summary>
    public RelayCommand OpenTitleLinkCommand
    {
        get;
    }

    /// <summary>Activates a clickable <see cref="OverlayRowViewModel"/>: breakdown, then item wiki, then link.</summary>
    public RelayCommand<OverlayRowViewModel> ActivateRowCommand
    {
        get;
    }

    /// <summary>Opens the wiki page of the row's item.</summary>
    public RelayCommand<OverlayRowViewModel> OpenItemWikiCommand
    {
        get;
    }

    /// <summary>Looks up quest references for the row's item.</summary>
    public RelayCommand<OverlayRowViewModel> LookupQuestReferencesCommand
    {
        get;
    }

    /// <summary>Toggles the watch star on the row.</summary>
    public RelayCommand<OverlayRowViewModel> ToggleWatchCommand
    {
        get;
    }

    /// <summary>Toggles spawn tracking for the row's mob.</summary>
    public RelayCommand<OverlayRowViewModel> ToggleSpawnTrackCommand
    {
        get;
    }

    /// <summary>Toggles spell tracking for the row's spell.</summary>
    public RelayCommand<OverlayRowViewModel> ToggleSpellTrackCommand
    {
        get;
    }

    /// <summary>Opens the URL parameter, for example a table cell link.</summary>
    public ICommand OpenLinkCommand
    {
        get;
    }

    /// <summary>Sets whether the overlay is docked and whether only temporarily.</summary>
    public void SetDocked(bool docked, bool temporary = false)
    {
        IsDocked = docked;
        IsTemporarilyDocked = docked && temporary;
    }

    /// <summary>Replaces the "Move to" choices with every workspace except the active one.</summary>
    public void SetWorkspaceOptions(IEnumerable<(string Id, string Name)> workspaces, string activeWorkspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        WorkspaceOptions.Clear();
        foreach (var (id, name) in workspaces)
        {
            if (id != activeWorkspaceId)
            {
                WorkspaceOptions.Add(new WorkspaceOption(id, name));
            }
        }
        if (WorkspaceOptions.Count == 0)
        {
            WorkspaceOptions.Add(new WorkspaceOption("", "No other workspaces"));
        }
    }

    /// <summary>Shows new header text and rows, keeping unchanged rows.</summary>
    public void Show(OverlayContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Show(content.Title, content.Subtitle, content.Rows, content.TitleLink);
    }

    /// <summary>Shows new header text and rows, keeping unchanged rows.</summary>
    public void Show(string title, string? subtitle, IReadOnlyList<OverlayRow> rows, string? titleLink = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Title = title;
        TitleLink = titleLink;
        Subtitle = subtitle ?? "";
        Rows.Reconcile(rows, static (existing, row) => existing.Row == row, row => new OverlayRowViewModel(this, row));
    }

    /// <inheritdoc />
    public void Receive(OverlayFrameMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        OnFrame(message);
    }

    /// <summary>Renders a frame. The default renders <see cref="Definition"/> with <see cref="CreateView"/>.</summary>
    protected virtual void OnFrame(OverlayFrameMessage frame)
    {
        if (Definition is not { } definition)
        {
            return;
        }
        Show(Decorate(definition.Render(frame.Snapshot, frame.Queries, CreateView(frame))));
    }

    /// <summary>The presenter options for a frame. Subclasses fill in what their presenter reads.</summary>
    protected virtual OverlayView CreateView(OverlayFrameMessage frame)
    {
        return new OverlayView(new HashSet<ChatChannel>(NoChannels), []);
    }

    /// <summary>Adjusts rendered content before it is shown.</summary>
    protected virtual OverlayContent Decorate(OverlayContent content)
    {
        return content;
    }

    /// <summary>Marks item rows that have quest references so they show the quest badge.</summary>
    protected OverlayContent WithQuestReferences(OverlayContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var rows = content.Rows
            .Select(row => row.LootItem is { } item ? row with { HasQuestReferences = Actions.HasQuestReferences(item) } : row)
            .ToArray();
        return content with { Rows = rows };
    }

    /// <summary>Called when <see cref="IsShowing"/> changes, so hidden overlays can pause background work.</summary>
    protected virtual void OnShowingChanged(bool showing)
    {
    }

    /// <summary>Handles a watch star click. The default ignores it.</summary>
    protected virtual void OnWatchToggled(string watchKey)
    {
    }

    /// <summary>Handles a spawn-tracking checkbox. The default ignores it.</summary>
    protected virtual void OnSpawnTrackChanged(SpawnTrackChange change)
    {
    }

    /// <summary>Handles a spell-tracking checkbox. The default ignores it.</summary>
    protected virtual void OnSpellTrackChanged(SpellTrackChange change)
    {
    }

    private void OpenTitleLink()
    {
        if (_titleLink is { } url)
        {
            Actions.OpenLink(url);
        }
    }

    private void ActivateRow(OverlayRowViewModel row)
    {
        if (row.Row.Breakdown is { } breakdown)
        {
            Actions.ShowBreakdown(breakdown);
        }
        else if (row.Row.LootItem is { } item)
        {
            Actions.OpenItemWiki(item);
        }
        else if (row.Row.Link is { } link)
        {
            Actions.OpenLink(link);
        }
    }
}
