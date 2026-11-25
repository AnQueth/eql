using System.Windows.Controls;

namespace EqLog.Overlay.Views;

/// <summary>A docked panel's tile on the dock window's canvas, with a bottom-right resize grip.</summary>
public partial class DockTile : Grid
{
    public DockTile(OverlayPanel panel)
    {
        InitializeComponent();
        HostedPanel = panel;
        Tag = panel.Key;
        PanelHost.Child = panel;
    }

    /// <summary>The hosted panel's key.</summary>
    public string Key
    {
        get
        {
            return (string)Tag;
        }
    }

    /// <summary>The hosted panel, or null once released.</summary>
    public OverlayPanel? HostedPanel
    {
        get;
        private set;
    }

    /// <summary>Detaches the panel so it can be hosted elsewhere (for example a floating window).</summary>
    public void ReleasePanel()
    {
        PanelHost.Child = null;
        HostedPanel = null;
    }
}
