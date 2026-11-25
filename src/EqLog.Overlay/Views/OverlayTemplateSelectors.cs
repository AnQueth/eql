using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay.Views;

/// <summary>Chooses the clickable (button) or plain template for an <see cref="OverlayRowViewModel"/>.</summary>
public sealed class OverlayRowTemplateSelector : DataTemplateSelector
{
    /// <summary>The template for rows without a click action.</summary>
    public DataTemplate? Plain { get; set; }

    /// <summary>The template for rows that open a breakdown, item or link.</summary>
    public DataTemplate? Clickable { get; set; }

    /// <inheritdoc />
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item is OverlayRowViewModel { IsClickable: true } ? Clickable : Plain;
    }
}

/// <summary>
/// Chooses one part (header extras, toolbar, body) of an overlay view. Per-overlay XAML supplies a template keyed
/// <c>{ViewModelTypeName}.{Part}</c>; base view model types are tried in turn, then <see cref="Fallback"/>.
/// </summary>
public sealed class OverlayPartTemplateSelector : DataTemplateSelector
{
    private static readonly DataTemplate Empty = CreateEmpty();

    /// <summary>The part name, for example <c>Toolbar</c>.</summary>
    public string Part { get; set; } = "";

    /// <summary>The template when no overlay supplies the part; empty when unset.</summary>
    public DataTemplate? Fallback { get; set; }

    /// <summary>The fallback for overlays with virtualized rows.</summary>
    public DataTemplate? VirtualFallback { get; set; }

    /// <inheritdoc />
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is not OverlayViewModel overlay)
        {
            return Empty;
        }
        var element = container as FrameworkElement;
        for (var type = item.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            string key = $"{type.Name}.{Part}";
            if (element is not null && HasResource(element, key) &&
                element.TryFindResource(key) is DataTemplate template)
            {
                return template;
            }
        }
        if (overlay.UsesVirtualRows && VirtualFallback is not null)
        {
            return VirtualFallback;
        }
        return Fallback ?? Empty;
    }

    private static bool HasResource(FrameworkElement element, string key)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement frameworkElement)
            {
                if (frameworkElement.Resources.Contains(key) ||
                    frameworkElement.Style?.Resources.Contains(key) == true ||
                    (frameworkElement is Control control && control.Template?.Resources.Contains(key) == true))
                {
                    return true;
                }
                current = frameworkElement.Parent ??
                    (current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : null) ??
                    frameworkElement.TemplatedParent;
            }
            else if (current is FrameworkContentElement contentElement)
            {
                if (contentElement.Resources.Contains(key) || contentElement.Style?.Resources.Contains(key) == true)
                {
                    return true;
                }
                current = contentElement.Parent ?? contentElement.TemplatedParent;
            }
            else
            {
                current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : null;
            }
        }
        return System.Windows.Application.Current?.Resources.Contains(key) == true;
    }

    private static DataTemplate CreateEmpty()
    {
        var template = new DataTemplate();
        template.Seal();
        return template;
    }
}

/// <summary>A virtualized row list. Unlike other list boxes it is not treated as an interactive control by the drag logic.</summary>
public sealed class VirtualRowList : ListBox
{
}
