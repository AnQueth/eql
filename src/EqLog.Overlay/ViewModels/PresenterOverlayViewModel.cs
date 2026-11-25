using EqLog.Overlay.Messaging;

namespace EqLog.Overlay.ViewModels;

/// <summary>An overlay that renders its <see cref="OverlayDefinition"/> from each frame and the shared shell state.</summary>
public class PresenterOverlayViewModel : OverlayViewModel
{
    /// <summary>Creates a presenter overlay.</summary>
    public PresenterOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }

    /// <summary>The shared shell state.</summary>
    protected IOverlayViewSource Source
    {
        get;
    }

    /// <inheritdoc />
    protected override OverlayView CreateView(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new OverlayView(Source.ChatChannels, [], Inventory: Source.Inventory, RecentChat: frame.Chat,
            CharacterName: Source.CharacterName);
    }
}

/// <summary>The chat overlay: asks the pump to capture its channels.</summary>
public sealed class ChatOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source)
    : PresenterOverlayViewModel(definition, actions, source)
{
    /// <inheritdoc />
    public override FrameRequirements Requirements
    {
        get
        {
            return new FrameRequirements(new HashSet<EqLog.Core.Parsing.ChatChannel>(Source.ChatChannels));
        }
    }
}

/// <summary>The spawn overlay: asks for spawn data and reports tracking changes.</summary>
public sealed class SpawnsOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source,
    Action<SpawnTrackChange> trackChanged) : PresenterOverlayViewModel(definition, actions, source)
{
    /// <inheritdoc />
    public override FrameRequirements Requirements
    {
        get
        {
            return new FrameRequirements(Spawns: true);
        }
    }

    /// <inheritdoc />
    protected override void OnSpawnTrackChanged(SpawnTrackChange change)
    {
        trackChanged(change);
    }
}

/// <summary>The wiki activity overlay: refreshes at most once a second because activity changes slowly.</summary>
public sealed class WikiOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source)
    : PresenterOverlayViewModel(definition, actions, source)
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromSeconds(1);
    private DateTime _lastRender;

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Now - _lastRender < RenderInterval)
        {
            return;
        }
        _lastRender = frame.Now;
        base.OnFrame(frame);
    }

    /// <inheritdoc />
    protected override OverlayView CreateView(OverlayFrameMessage frame)
    {
        return base.CreateView(frame) with
        {
            WikiActivity = Source.WikiActivity(),
            QuestWiki = Source.QuestWikiActivity(),
        };
    }

    /// <inheritdoc />
    protected override OverlayContent Decorate(OverlayContent content)
    {
        return WithQuestReferences(content);
    }
}

/// <summary>The motes overlay: reads the character's mote inventory.</summary>
public sealed class MotesOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source)
    : PresenterOverlayViewModel(definition, actions, source)
{
    /// <inheritdoc />
    protected override OverlayView CreateView(OverlayFrameMessage frame)
    {
        return base.CreateView(frame) with { MoteInventory = Source.Motes() };
    }
}
