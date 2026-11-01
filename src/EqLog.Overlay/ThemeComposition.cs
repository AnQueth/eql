using System.IO;
using EqLog.Application.Settings;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Overlay;

internal static class ThemeComposition
{
    public static string BuiltInFolder { get; } = Path.Combine(AppContext.BaseDirectory, "Themes");
    public static ThemeService Service { get; } = new(new ThemeRepository(BuiltInFolder));
}
