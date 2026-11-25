namespace EqLog.Overlay.ViewModels;

/// <summary>Application actions an overlay can request. Implemented by the shell; overlays never open windows themselves.</summary>
public interface IOverlayActions
{
    /// <summary>Opens a URL, normally an EQLWiki page.</summary>
    void OpenLink(string url);

    /// <summary>Opens the EQLWiki page for an item.</summary>
    void OpenItemWiki(string item);

    /// <summary>Shows quest references for an item.</summary>
    void LookupQuestReferences(string item);

    /// <summary>Whether cached quest references exist for an item.</summary>
    bool HasQuestReferences(string item);

    /// <summary>Shows a per-ability breakdown window.</summary>
    void ShowBreakdown(AbilityBreakdown breakdown);

    /// <summary>Docks a floating overlay or pops a docked one out.</summary>
    void ToggleDock(OverlayViewModel overlay);

    /// <summary>Moves a docked overlay into another workspace.</summary>
    void MoveToWorkspace(OverlayViewModel overlay, string workspaceId);

    /// <summary>The shared icon for an item or mob, or null when icons are unavailable.</summary>
    WikiIconViewModel? GetWikiIcon(EqLog.Core.WikiIconKey key)
    {
        return null;
    }
}

/// <summary>An <see cref="IOverlayActions"/> that ignores every request, for previews and tests.</summary>
public sealed class NullOverlayActions : IOverlayActions
{
    /// <summary>The shared instance.</summary>
    public static NullOverlayActions Instance { get; } = new();

    private NullOverlayActions()
    {
    }

    /// <inheritdoc />
    public void OpenLink(string url)
    {
    }

    /// <inheritdoc />
    public void OpenItemWiki(string item)
    {
    }

    /// <inheritdoc />
    public void LookupQuestReferences(string item)
    {
    }

    /// <inheritdoc />
    public bool HasQuestReferences(string item)
    {
        return false;
    }

    /// <inheritdoc />
    public void ShowBreakdown(AbilityBreakdown breakdown)
    {
    }

    /// <inheritdoc />
    public void ToggleDock(OverlayViewModel overlay)
    {
    }

    /// <inheritdoc />
    public void MoveToWorkspace(OverlayViewModel overlay, string workspaceId)
    {
    }
}

/// <summary>A destination in an overlay's "Move to" workspace menu.</summary>
/// <param name="Id">The workspace id, or empty for the disabled placeholder.</param>
/// <param name="Name">The display name.</param>
public sealed record WorkspaceOption(string Id, string Name)
{
    /// <summary>Whether choosing this option moves the overlay.</summary>
    public bool IsEnabled
    {
        get
        {
            return Id.Length > 0;
        }
    }
}
