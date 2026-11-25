using System.Windows;
using System.Windows.Automation.Peers;

namespace EqLog.Overlay.Views;

/// <summary>
/// Keeps overlays out of UI Automation (screen readers and other accessibility tools). When any automation client is
/// listening, WPF otherwise re-syncs and raises change events for every overlay row after each layout pass, which made
/// loading log history sluggish. The element reports no children and is excluded from the control and content views.
/// </summary>
internal sealed class HiddenAutomationPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    protected override List<AutomationPeer>? GetChildrenCore()
    {
        return null;
    }

    protected override bool IsControlElementCore()
    {
        return false;
    }

    protected override bool IsContentElementCore()
    {
        return false;
    }
}

/// <summary>The floating overlay window's peer: hidden like <see cref="HiddenAutomationPeer"/>.</summary>
internal sealed class HiddenWindowAutomationPeer(Window owner) : WindowAutomationPeer(owner)
{
    protected override List<AutomationPeer>? GetChildrenCore()
    {
        return null;
    }

    protected override bool IsControlElementCore()
    {
        return false;
    }

    protected override bool IsContentElementCore()
    {
        return false;
    }
}
