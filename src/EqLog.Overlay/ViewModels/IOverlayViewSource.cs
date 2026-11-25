using EqLog.Application.Inventory;
using EqLog.Application.Motes;
using EqLog.Core;
using EqLog.Core.Parsing;

namespace EqLog.Overlay.ViewModels;

/// <summary>Shell state that presenter overlays read when they build an <see cref="OverlayView"/> for a frame.</summary>
public interface IOverlayViewSource
{
    /// <summary>The chat channels the chat overlay shows.</summary>
    ISet<ChatChannel> ChatChannels
    {
        get;
    }

    /// <summary>The current character, if known.</summary>
    string? CharacterName
    {
        get;
    }

    /// <summary>The shared inventory read model.</summary>
    InventoryReadModel? Inventory
    {
        get;
    }

    /// <summary>The current character's mote inventory.</summary>
    MoteInventoryReadModel? Motes();

    /// <summary>Current wiki request activity.</summary>
    WikiActivitySnapshot? WikiActivity();

    /// <summary>Current quest enrichment activity.</summary>
    QuestWikiActivity? QuestWikiActivity();
}

/// <summary>An <see cref="IOverlayViewSource"/> with no state, for tests and design time.</summary>
public sealed class EmptyOverlayViewSource : IOverlayViewSource
{
    private readonly HashSet<ChatChannel> _channels = [];

    /// <summary>The shared instance.</summary>
    public static EmptyOverlayViewSource Instance { get; } = new();

    /// <inheritdoc />
    public ISet<ChatChannel> ChatChannels
    {
        get
        {
            return _channels;
        }
    }

    /// <inheritdoc />
    public string? CharacterName
    {
        get
        {
            return null;
        }
    }

    /// <inheritdoc />
    public InventoryReadModel? Inventory
    {
        get
        {
            return null;
        }
    }

    /// <inheritdoc />
    public MoteInventoryReadModel? Motes()
    {
        return null;
    }

    /// <inheritdoc />
    public WikiActivitySnapshot? WikiActivity()
    {
        return null;
    }

    /// <inheritdoc />
    public QuestWikiActivity? QuestWikiActivity()
    {
        return null;
    }
}
