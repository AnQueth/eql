using System.Windows;
using System.Windows.Controls;

namespace EqLog.Overlay;

/// <summary>
/// A status line that always reserves the same number of text lines, so changing messages never push
/// nearby buttons or lists around. Overflow is trimmed and the full message is available as a tooltip.
/// </summary>
public sealed class FixedLineTextBlock : TextBlock
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines),
        typeof(int),
        typeof(FixedLineTextBlock),
        new FrameworkPropertyMetadata(2, OnMetricsChanged));

    static FixedLineTextBlock()
    {
        FontSizeProperty.OverrideMetadata(typeof(FixedLineTextBlock), new FrameworkPropertyMetadata(OnMetricsChanged));
        FontFamilyProperty.OverrideMetadata(typeof(FixedLineTextBlock), new FrameworkPropertyMetadata(OnMetricsChanged));
        LineHeightProperty.OverrideMetadata(typeof(FixedLineTextBlock), new FrameworkPropertyMetadata(OnMetricsChanged));
        PaddingProperty.OverrideMetadata(typeof(FixedLineTextBlock), new FrameworkPropertyMetadata(OnMetricsChanged));
        TextProperty.OverrideMetadata(typeof(FixedLineTextBlock), new FrameworkPropertyMetadata(OnTextChanged));
    }

    public FixedLineTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        TextTrimming = TextTrimming.CharacterEllipsis;
        UpdateHeight();
    }

    public int Lines
    {
        get
        {
            return (int)GetValue(LinesProperty);
        }
        set
        {
            SetValue(LinesProperty, value);
        }
    }

    private static void OnMetricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((FixedLineTextBlock)d).UpdateHeight();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (FixedLineTextBlock)d;
        block.ToolTip = string.IsNullOrEmpty(block.Text) ? null : block.Text;
    }

    private void UpdateHeight()
    {
        double lineHeight = double.IsNaN(LineHeight)
            ? FontFamily.LineSpacing * FontSize
            : LineHeight;
        Height = Math.Ceiling(Math.Max(1, Lines) * lineHeight + Padding.Top + Padding.Bottom) + 1;
    }
}
