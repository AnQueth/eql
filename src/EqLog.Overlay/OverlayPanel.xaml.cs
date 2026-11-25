using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

/// <summary>
/// The shared overlay view: frame, header chrome and the body chosen for the <see cref="OverlayViewModel"/>. It lives
/// either in its own floating <see cref="OverlayWindow"/> or as a tile in the <see cref="DockWindow"/>.
/// </summary>
public partial class OverlayPanel : UserControl
{

    /// <summary>Creates the view for <paramref name="viewModel"/>.</summary>
    public OverlayPanel(OverlayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        MouseEnter += (_, _) => ViewModel.IsBackgroundHovered = true;
        MouseLeave += (_, _) => ViewModel.IsBackgroundHovered = false;
    }

    /// <summary>Creates a view with a new plain view model.</summary>
    public OverlayPanel(string key, string caption, bool autoScrollToEnd)
        : this(new OverlayViewModel(key, caption, autoScrollToEnd))
    {
    }

    /// <summary>The overlay state this view renders.</summary>
    public OverlayViewModel ViewModel
    {
        get; private set;
    }

    internal void ReplaceViewModel(OverlayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (viewModel.Key != Key)
        {
            throw new ArgumentException("The replacement must represent the same overlay.", nameof(viewModel));
        }
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>The settings and placement key.</summary>
    public string Key
    {
        get
        {
            return ViewModel.Key;
        }
    }

    /// <summary>The overlay's display name.</summary>
    public string Caption
    {
        get
        {
            return ViewModel.Caption;
        }
    }

    internal Thumb DragHandle
    {
        get
        {
            return DragHandleThumb;
        }
    }


    /// <summary>Hides the overlay, floating or docked, from accessibility tools; see <see cref="Views.HiddenAutomationPeer"/>.</summary>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new Views.HiddenAutomationPeer(this);
    }

    /// <summary>Whether a press on <paramref name="source"/> belongs to a control rather than starting a drag.</summary>
    internal static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or ScrollBar or Thumb or ComboBox or TabItem or Hyperlink or
                DataGrid or DatePicker or GridSplitter or Slider)
            {
                return true;
            }
            if (source is ListBox and not Views.VirtualRowList)
            {
                return true;
            }

            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
