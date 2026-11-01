using System.IO;
using System.Text.Json;
using EqLog.Infrastructure.Persistence;
using EqLog.Application.Settings;

namespace EqLog.Overlay;

internal sealed record SettingsLoadResult(AppSettings State, SettingsService Service, Exception? Error);

internal static class SettingsComposition
{
    internal static readonly ApplicationStoragePaths Paths = new();

    internal static SettingsService CreateService()
    {
        return new(new SettingsRepository(Paths.Settings));
    }

    internal static async Task<SettingsLoadResult> LoadAsync()
    {
        var repository = new SettingsRepository(Paths.Settings);
        try
        {
            return new(await repository.LoadAsync(), new(repository), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.StartupData);
            return new(new(), new(repository, ex), ex);
        }
    }
}
