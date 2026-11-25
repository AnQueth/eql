using System.Windows;
using System.Windows.Controls;
using EqLog.Core;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay.Views;

/// <summary>
/// A wiki item icon or mob portrait beside its name. The name is the content, so it can be plain text, a link, or a
/// heading. Set <see cref="Subject"/> (and <see cref="Kind"/>) to look the icon up through the inherited
/// <see cref="IconSourceProperty"/>, or bind a ready <see cref="Icon"/>. The look is the implicit style in
/// Styles/WikiLabel.xaml.
/// </summary>
public sealed class WikiLabel : ContentControl
{
    /// <summary>Where labels below an element get their icons; panels and windows set it once.</summary>
    public static readonly DependencyProperty IconSourceProperty = DependencyProperty.RegisterAttached(
        "IconSource", typeof(IWikiIconSource), typeof(WikiLabel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnLookupChanged));

    /// <summary>The item or mob name to show an icon for.</summary>
    public static readonly DependencyProperty SubjectProperty = DependencyProperty.Register(
        nameof(Subject), typeof(string), typeof(WikiLabel), new PropertyMetadata(null, OnLookupChanged));

    /// <summary>Whether <see cref="Subject"/> is an item or a mob.</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(WikiIconKind), typeof(WikiLabel), new PropertyMetadata(WikiIconKind.Item, OnLookupChanged));

    /// <summary>The icon shown; looked up from <see cref="Subject"/> or bound directly.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(WikiIconViewModel), typeof(WikiLabel), new PropertyMetadata(null));

    /// <summary>The icon's width and height.</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(WikiLabel), new PropertyMetadata(18.0));

    /// <summary>How the icon aligns beside multi-line content.</summary>
    public static readonly DependencyProperty IconVerticalAlignmentProperty = DependencyProperty.Register(
        nameof(IconVerticalAlignment), typeof(VerticalAlignment), typeof(WikiLabel),
        new PropertyMetadata(VerticalAlignment.Center));

    /// <inheritdoc cref="SubjectProperty" />
    public string? Subject
    {
        get
        {
            return (string?)GetValue(SubjectProperty);
        }
        set
        {
            SetValue(SubjectProperty, value);
        }
    }

    /// <inheritdoc cref="KindProperty" />
    public WikiIconKind Kind
    {
        get
        {
            return (WikiIconKind)GetValue(KindProperty);
        }
        set
        {
            SetValue(KindProperty, value);
        }
    }

    /// <inheritdoc cref="IconProperty" />
    public WikiIconViewModel? Icon
    {
        get
        {
            return (WikiIconViewModel?)GetValue(IconProperty);
        }
        set
        {
            SetValue(IconProperty, value);
        }
    }

    /// <inheritdoc cref="IconSizeProperty" />
    public double IconSize
    {
        get
        {
            return (double)GetValue(IconSizeProperty);
        }
        set
        {
            SetValue(IconSizeProperty, value);
        }
    }

    /// <inheritdoc cref="IconVerticalAlignmentProperty" />
    public VerticalAlignment IconVerticalAlignment
    {
        get
        {
            return (VerticalAlignment)GetValue(IconVerticalAlignmentProperty);
        }
        set
        {
            SetValue(IconVerticalAlignmentProperty, value);
        }
    }

    /// <summary>Gets the icon source inherited at <paramref name="element"/>.</summary>
    public static IWikiIconSource? GetIconSource(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IWikiIconSource?)element.GetValue(IconSourceProperty);
    }

    /// <summary>Sets the icon source for <paramref name="element"/> and everything below it.</summary>
    public static void SetIconSource(DependencyObject element, IWikiIconSource? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IconSourceProperty, value);
    }

    private static void OnLookupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WikiLabel label)
        {
            return;
        }
        if (e.Property == SubjectProperty && string.IsNullOrWhiteSpace(label.Subject) &&
            !string.IsNullOrWhiteSpace(e.OldValue as string))
        {
            label.SetCurrentValue(IconProperty, null);
            return;
        }
        label.Lookup();
    }

    private void Lookup()
    {
        string? subject = Subject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }
        WikiIconViewModel? icon = null;
        if (GetIconSource(this) is { } source)
        {
            icon = source.GetWikiIcon(new WikiIconKey(Kind, subject));
        }
        SetCurrentValue(IconProperty, icon);
    }
}
