using System.Text.Json;
using EqLog.Application.Settings;
using EqLog.Core;

namespace EqLog.Infrastructure.Persistence;

public sealed class ThemeRepository(string bundledFolder) : IThemeRepository
{
    public async Task<IReadOnlyList<ThemePalette>> LoadBundledAsync(CancellationToken cancellationToken = default)
    {
        string[] paths = await Task.Run(() => Directory.GetFiles(bundledFolder, "*.json").Order().ToArray(),
            cancellationToken).ConfigureAwait(false);
        var themes = new List<ThemePalette>();
        foreach (string path in paths)
        {
            themes.Add(await ImportAsync(path, cancellationToken).ConfigureAwait(false));
        }
        return themes;
    }
    public async Task<ThemePalette> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        return ThemePalette.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }
    public Task ExportAsync(string path, ThemePalette palette, CancellationToken cancellationToken = default)
    {
        return AtomicFile.WriteJsonAsync(path, palette, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }
}
