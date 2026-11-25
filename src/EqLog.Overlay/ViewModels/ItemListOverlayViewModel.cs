using EqLog.Overlay.Messaging;

namespace EqLog.Overlay.ViewModels;

/// <summary>An item overlay (loot, forage, item upgrades) with a name filter and, optionally, a grouping choice.</summary>
public sealed class ItemListOverlayViewModel : PresenterOverlayViewModel
{
    private readonly Action<int>? _groupingChanged;
    private string _filterText = "";
    private int _grouping;

    /// <summary>Creates an item overlay.</summary>
    /// <param name="definition">The presenter.</param>
    /// <param name="actions">Shell actions.</param>
    /// <param name="source">Shared shell state.</param>
    /// <param name="groupingOptions">Grouping choices, or empty for no grouping control.</param>
    /// <param name="grouping">The initial grouping index.</param>
    /// <param name="groupingToolTip">The grouping control's tool tip.</param>
    /// <param name="groupingChanged">Persists a new grouping index.</param>
    public ItemListOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source,
        IReadOnlyList<string>? groupingOptions = null, int grouping = 0, string groupingToolTip = "",
        Action<int>? groupingChanged = null)
        : base(definition, actions, source)
    {
        GroupingOptions = groupingOptions ?? [];
        GroupingToolTip = groupingToolTip;
        _grouping = GroupingOptions.Count == 0 ? 0 : Math.Clamp(grouping, 0, GroupingOptions.Count - 1);
        _groupingChanged = groupingChanged;
    }

    /// <summary>The item name filter (case-insensitive).</summary>
    public string FilterText
    {
        get
        {
            return _filterText;
        }
        set
        {
            SetProperty(ref _filterText, value ?? "");
        }
    }

    /// <summary>The grouping choices.</summary>
    public IReadOnlyList<string> GroupingOptions
    {
        get;
    }

    /// <summary>Whether the overlay offers a grouping choice.</summary>
    public bool HasGrouping
    {
        get
        {
            return GroupingOptions.Count > 0;
        }
    }

    /// <summary>The grouping control's tool tip.</summary>
    public string GroupingToolTip
    {
        get;
    }

    /// <summary>The selected grouping index.</summary>
    public int Grouping
    {
        get
        {
            return _grouping;
        }
        set
        {
            if (SetProperty(ref _grouping, value))
            {
                _groupingChanged?.Invoke(value);
            }
        }
    }

    /// <inheritdoc />
    protected override OverlayView CreateView(OverlayFrameMessage frame)
    {
        var view = base.CreateView(frame) with { ItemFilter = _filterText };
        return Key == "forage" ? view with { ForageGrouping = _grouping } : view with { LootGrouping = _grouping };
    }

    /// <inheritdoc />
    protected override OverlayContent Decorate(OverlayContent content)
    {
        return Key is "loot" or "forage" ? WithQuestReferences(content) : content;
    }
}

/// <summary>The spell timers overlay: the known-spells switch and filter, and spell tracking.</summary>
public sealed class SpellTimersOverlayViewModel : PresenterOverlayViewModel
{
    private readonly Action<bool> _showKnownSpellsChanged;
    private readonly Action<SpellTrackChange> _trackChanged;
    private bool _showKnownSpells;
    private string _filterText = "";

    /// <summary>Creates the spell timers overlay.</summary>
    public SpellTimersOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source,
        bool showKnownSpells, Action<bool> showKnownSpellsChanged, Action<SpellTrackChange> trackChanged)
        : base(definition, actions, source)
    {
        ArgumentNullException.ThrowIfNull(showKnownSpellsChanged);
        ArgumentNullException.ThrowIfNull(trackChanged);
        _showKnownSpells = showKnownSpells;
        _showKnownSpellsChanged = showKnownSpellsChanged;
        _trackChanged = trackChanged;
    }

    /// <summary>Whether known (inactive) spells are listed under the running timers.</summary>
    public bool ShowKnownSpells
    {
        get
        {
            return _showKnownSpells;
        }
        set
        {
            if (SetProperty(ref _showKnownSpells, value))
            {
                _showKnownSpellsChanged(value);
            }
        }
    }

    /// <summary>The known-spells name filter.</summary>
    public string FilterText
    {
        get
        {
            return _filterText;
        }
        set
        {
            SetProperty(ref _filterText, value ?? "");
        }
    }

    /// <inheritdoc />
    public override FrameRequirements Requirements
    {
        get
        {
            return new FrameRequirements(SpellDurations: true);
        }
    }

    /// <inheritdoc />
    protected override OverlayView CreateView(OverlayFrameMessage frame)
    {
        return base.CreateView(frame) with { ShowKnownSpells = _showKnownSpells, ItemFilter = _filterText };
    }

    /// <inheritdoc />
    protected override void OnSpellTrackChanged(SpellTrackChange change)
    {
        _trackChanged(change);
    }
}
