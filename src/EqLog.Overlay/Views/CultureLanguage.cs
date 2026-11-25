using System.Globalization;
using System.Windows.Markup;

namespace EqLog.Overlay.Views;

/// <summary>
/// The user's culture as a XAML language. WPF elements default to en-US, so date pickers bind
/// <c>Language</c> to <see cref="Current"/> to show and parse dates the way the user types them.
/// </summary>
public static class CultureLanguage
{
    /// <summary>The language for <see cref="CultureInfo.CurrentCulture"/>.</summary>
    public static XmlLanguage Current
    {
        get
        {
            return XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        }
    }
}
