using System.IO;
using System.Text.Json;
using EqLog.Core;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private SkyQuestCatalog? _skyCatalog;
    private bool _skyLoading;
    private WikiPriority _skyLoadPriority;
    private bool _skyForceRefresh;
    private bool _skyOverlayLoadAttempted;
    private string _skyStatus = "";

    private async Task LoadSkyQuestsAsync(bool refresh, WikiPriority priority = WikiPriority.Interactive)
    {
        if (WikiLookupPaused)
        {
            return;
        }
        if (_skyLoading)
        {
            if (priority < _skyLoadPriority)
            {
                _skyLoadPriority = priority;
            }
            _skyForceRefresh |= refresh;
            _wikiRepository.PromoteSky(priority);
            return;
        }

        using var lookup = BeginWikiLookup();
        var token = lookup.Token;
        _skyLoading = true;
        _skyLoadPriority = priority;
        _skyForceRefresh = refresh;
        _skyStatus = "Loading Plane of Sky class quests...";
        try
        {
            _skyCatalog = await _wikiRepository.GetSkyAsync(_skyLoadPriority, _skyForceRefresh, token);
            token.ThrowIfCancellationRequested();
            _skyStatus = _wikiRepository.GetCacheError("sky") is { } cacheError
                ? $"Could not save Sky cache: {cacheError}. Showing session data." : "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            WikiTransportException or OperationCanceledException or InvalidOperationException or FormatException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Wiki);
            _skyStatus = $"Sky quest load failed: {ex.Message}" +
                (_skyCatalog is null ? " Click Refresh to retry." : " Showing previously loaded data.");
        }
        finally
        {
            _skyLoading = false;
        }
    }

    private SkyOverlayState GetSkyOverlayState()
    {
        return new SkyOverlayState(_questInventory, _characterName ?? "", _skyCatalog, _skyLoading,
            _skyStatus, _session.Revision);
    }

    private bool TryStartInitialSkyLoad()
    {
        if (_skyCatalog is not null || _skyLoading || _skyOverlayLoadAttempted || WikiLookupPaused)
        {
            return false;
        }
        _skyOverlayLoadAttempted = true;
        _ = LoadSkyQuestsAsync(false, WikiPriority.Background);
        return true;
    }

    private void SaveSkyWatched(IReadOnlyList<string> keys)
    {
        _settings.SkyWatched.Clear();
        _settings.SkyWatched.AddRange(keys);
        SaveSettingsInBackground(error => _skyStatus = $"Could not save watched quests: {error}");
    }
}
