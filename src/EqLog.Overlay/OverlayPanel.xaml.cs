using System.ComponentModel;
using System.Windows;
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
    private static readonly HashSet<string> ChromePropertyNames =
    [
        nameof(OverlayViewModel.EffectiveBackgroundOpacity),
        nameof(OverlayViewModel.BackgroundOpacity),
        nameof(OverlayViewModel.IsDocked),
        nameof(OverlayViewModel.IsLocked),
        nameof(OverlayViewModel.IsChromeHidden),
    ];

    /// <summary>Creates the view for <paramref name="viewModel"/>.</summary>
    public OverlayPanel(OverlayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ApplyCompactStyles();
        MouseEnter += (_, _) => ViewModel.IsBackgroundHovered = true;
        MouseLeave += (_, _) => ViewModel.IsBackgroundHovered = false;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Frame.SizeChanged += (_, _) => UpdateFrameClip();
        ApplyChrome();
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
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = viewModel;
        ApplyChrome();
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

    /// <summary>Re-reads theme colors into the frame brushes after a theme change.</summary>
    public void RefreshChrome()
    {
        ApplyChrome();
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || ChromePropertyNames.Contains(e.PropertyName))
        {
            ApplyChrome();
        }
    }

    private void ApplyCompactStyles()
    {
        SetCompactStyle(typeof(Button),
            (Control.PaddingProperty, new Thickness(10, 5, 10, 5)),
            (MarginProperty, new Thickness(0, 0, 6, 5)));
        SetCompactStyle(typeof(TextBox), (Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        SetCompactStyle(typeof(ComboBox), (MinHeightProperty, 30.0));
        SetCompactStyle(typeof(CheckBox), (MarginProperty, new Thickness(0, 4, 10, 4)));
        SetCompactStyle(typeof(DatePicker), (MinHeightProperty, 30.0));
        SetCompactStyle(typeof(DatePickerTextBox), (Control.PaddingProperty, new Thickness(8, 5, 0, 5)));
        SetCompactStyle(typeof(TabItem), (Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        SetCompactStyle(typeof(DataGrid),
            (DataGrid.RowHeightProperty, double.NaN),
            (DataGrid.MinRowHeightProperty, 28.0));
        SetCompactStyle(typeof(DataGridCell), (Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        SetCompactStyle(typeof(DataGridColumnHeader), (Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
    }

    private void SetCompactStyle(Type targetType, params (DependencyProperty Property, object Value)[] setters)
    {
        var style = new Style(targetType, TryFindResource(targetType) as Style);
        foreach (var (property, value) in setters)
        {
            style.Setters.Add(new Setter(property, value));
        }
        Resources[targetType] = style;
    }

    private void ApplyChrome()
    {
        var vm = ViewModel;
        bool docked = vm.IsDocked;
        var color = ((SolidColorBrush)FindResource("NightBackground")).Color;
        var background = new SolidColorBrush(Color.FromArgb((byte)(vm.EffectiveBackgroundOpacity * 255), color.R, color.G, color.B));
        background.Freeze();
        Frame.Background = background;
        ApplyDockFrame(docked, vm.BackgroundOpacity);
        Frame.SetResourceReference(Border.BorderBrushProperty, docked ? "NightBorder" : "Pumpkin");
        if ((!docked && vm.IsLocked) || vm.IsChromeDecorationHidden)
        {
            Frame.BorderBrush = Brushes.Transparent;
        }
    }

    private void ApplyDockFrame(bool docked, double opacity)
    {
        Frame.Padding = docked ? new Thickness(2) : new Thickness(4, 3, 4, 3);
        Frame.BorderThickness = new Thickness(docked ? 2 : 1);
        UpdateFrameClip();
        Bevel.BorderThickness = docked ? new Thickness(1, 1, 2, 2) : new Thickness(0);
        Inset.Margin = docked ? new Thickness(3) : new Thickness(0);
        Inset.Padding = docked ? new Thickness(3, 2, 3, 3) : new Thickness(0);
        Inset.BorderThickness = docked ? new Thickness(1) : new Thickness(0);
        TitleFrame.Padding = docked ? new Thickness(3, 3, 3, 4) : new Thickness(0);
        TitleFrame.Margin = docked ? new Thickness(-3, -2, -3, 2) : new Thickness(0);
        TitleFrame.BorderThickness = docked ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        if (!docked)
        {
            Bevel.Background = null;
            Bevel.BorderBrush = null;
            Inset.Background = null;
            Inset.BorderBrush = null;
            TitleFrame.Background = null;
            TitleFrame.BorderBrush = null;
            return;
        }

        Color metal = ((SolidColorBrush)FindResource("NightBorder")).Color;
        Color accent = ((SolidColorBrush)FindResource("Pumpkin")).Color;
        Color raised = ((SolidColorBrush)FindResource("NightRaised")).Color;
        Color back = ((SolidColorBrush)FindResource("NightBackground")).Color;
        Bevel.Background = null;
        Bevel.BorderBrush = Gradient(Blend(accent, Colors.White, 0.35), metal, Blend(metal, Colors.Black, 0.8));
        Inset.Background = Gradient(back, back, Blend(back, Colors.Black, 0.25), opacity);
        Inset.BorderBrush = Gradient(Colors.Black, metal, Blend(metal, Colors.White, 0.2));
        TitleFrame.Background = Gradient(Blend(raised, accent, 0.12), raised, Blend(raised, Colors.Black, 0.3), opacity);
        TitleFrame.BorderBrush = Gradient(accent, metal, Blend(metal, Colors.Black, 0.5));
    }

    private void UpdateFrameClip()
    {
        var radius = Frame.CornerRadius.TopLeft;
        var clip = new RectangleGeometry(new Rect(0, 0, Frame.ActualWidth, Frame.ActualHeight), radius, radius);
        clip.Freeze();
        Frame.Clip = clip;
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        return Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));
    }

    private static Brush Gradient(Color top, Color middle, Color bottom, double opacity = 1)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.35, 1),
            Opacity = opacity,
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(middle, 0.45),
                new GradientStop(bottom, 1),
            },
        };
        brush.Freeze();
        return brush;
    }
}
