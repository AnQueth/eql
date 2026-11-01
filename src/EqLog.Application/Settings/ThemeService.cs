using EqLog.Core;

namespace EqLog.Application.Settings;

public interface IThemeRepository
{
    Task<IReadOnlyList<ThemePalette>> LoadBundledAsync(CancellationToken cancellationToken = default);
    Task<ThemePalette> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, ThemePalette palette, CancellationToken cancellationToken = default);
}

/// <summary>Theme document use cases; native dialogs, previews and brushes stay in the presentation layer.</summary>
public sealed class ThemeService(IThemeRepository repository)
{
    public Task<IReadOnlyList<ThemePalette>> LoadBundledAsync(CancellationToken cancellationToken = default)
    {
        return repository.LoadBundledAsync(cancellationToken);
    }
    public Task<ThemePalette> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        return repository.ImportAsync(path, cancellationToken);
    }
    public Task ExportAsync(string path, ThemePalette palette, CancellationToken cancellationToken = default)
    {
        palette.Validate();
        return repository.ExportAsync(path, palette.Clone(), cancellationToken);
    }
}
