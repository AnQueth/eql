using System.Windows;

namespace EqLog.Overlay.Views;

/// <summary>
/// Carries a data context into elements outside the visual tree, such as DataGrid columns. Declare it in a resource
/// dictionary with <c>Data="{Binding}"</c> and bind to <c>Data.Property</c> with it as the binding source.
/// </summary>
public sealed class BindingProxy : Freezable
{
    /// <summary>The proxied value.</summary>
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy));

    /// <summary>The proxied value.</summary>
    public object? Data
    {
        get
        {
            return GetValue(DataProperty);
        }
        set
        {
            SetValue(DataProperty, value);
        }
    }

    protected override Freezable CreateInstanceCore()
    {
        return new BindingProxy();
    }
}
