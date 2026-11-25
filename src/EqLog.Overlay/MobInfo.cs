using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private bool _mobSaveEnabled = true;
    private string _mobStartupStatus = "";
    private readonly HashSet<string> _spawnWikiPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _spawnWikiRetry = new(StringComparer.OrdinalIgnoreCase);
    private WikiService _wikiRepository = null!;
    private bool _mobShowing;

    private void CheckSpawnWiki()
    {
        if (WikiLookupPaused)
        {
            return;
        }
        foreach (string mob in _session.SpawnWikiRequests(DateTime.UtcNow))
        {
            if (_spawnWikiRetry.GetValueOrDefault(mob) <= DateTime.UtcNow && _spawnWikiPending.Add(mob))
            {
                _ = LookupSpawnWikiAsync(mob);
            }
        }
    }

    private async Task LookupSpawnWikiAsync(string mob)
    {
        long generation = _session.Revision;
        using var lookup = BeginWikiLookup();
        var token = lookup.Token;
        try
        {
            var wiki = await _wikiRepository.GetMobAsync(mob, WikiPriority.Background, false, token);
            token.ThrowIfCancellationRequested();
            if (generation == _session.Revision)
            {
                _session.ApplySpawnWiki(mob, wiki);
            }
            _spawnsSignature = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is WikiTransportException or IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or OperationCanceledException)
        {
            _spawnWikiRetry[mob] = DateTime.UtcNow.AddMinutes(5);
            SetMobInfoStatus($"Spawn wiki lookup failed for {mob}: {error.Message}. Retrying after 5 minutes.");
        }
        finally
        {
            _spawnWikiPending.Remove(mob);
        }
    }

    private bool MobInfoActive
    {
        get
        {
            return _overlayViewModels.TryGetValue("mobinfo", out var viewModel) &&
                viewModel is MobInfoOverlayViewModel mobInfo && mobInfo.IsActive;
        }
    }

    private void InitializeMobInfo(StartupState state)
    {
        if (_session.MobsWritable)
        {
            _mobStartupStatus = "Only mob and spell names are sent to EQLWiki; no log files are uploaded.";
        }
        else
        {
            _mobSaveEnabled = false;
            _mobStartupStatus = !_dataReady && state.MobResistancesError is null
                ? "Restoring saved resistance history..."
                : $"Could not load mob data: {state.MobResistancesError?.Message}. Tracking this session only; the existing file will not be overwritten.";
        }
        if (!double.IsFinite(_settings.MobInfoTimeoutSeconds) || _settings.MobInfoTimeoutSeconds < 0 ||
            _settings.MobInfoTimeoutSeconds > 120)
        {
            _settings.MobInfoTimeoutSeconds = 5;
            _mobStartupStatus += " Invalid display timeout reset to 5 seconds.";
        }
        _wikiRepository = WikiComposition.CreateService(_session);
        _wikiRepository.CacheWriteFailed += (operation, error) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                UsageTelemetry.TrackError(error, UsageErrorSource.Wiki);
                ResetWikiCacheStatus.Text = $"Could not save {operation}: {error.Message}. Results are available this session; saves retry.";
            });
        };
        foreach (var quest in _questCache.Values)
        {
            _wikiRepository.SeedQuest(quest);
        }
        _wikiRepository.BackgroundFailed += (spell, error) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                SetMobInfoStatus($"Background spell lookup/save failed for {spell}: {error.Message}. " +
                    "Cached data kept; future observations retry after 5 minutes, or use Refresh wiki.");
            });
        };
    }

    private void PumpMobInfo(DateTime now)
    {
        if (!_overlayViewModels.TryGetValue("mobinfo", out var viewModel) ||
            viewModel is not MobInfoOverlayViewModel mobInfo)
        {
            return;
        }
        mobInfo.UpdateVisibility(now);
        bool newConsider = mobInfo.ConsumeNewConsider();
        bool showing = mobInfo.IsActive;
        if (newConsider || _mobShowing != showing)
        {
            _mobShowing = showing;
            ApplyMode("mobinfo");
            if (newConsider && _settings.Overlays["mobinfo"].Visible)
            {
                if (_settings.Overlays["mobinfo"].Docked || _dock.HasPanel("mobinfo"))
                {
                    _dock.RevealPanel("mobinfo");
                }
                else
                {
                    EnsureOnScreen(_overlays["mobinfo"]);
                    _overlays["mobinfo"].ReassertTopmost();
                }
            }
        }
    }

    private void ResetMobInfo()
    {
        if (_overlayViewModels.TryGetValue("mobinfo", out var viewModel) &&
            viewModel is MobInfoOverlayViewModel mobInfo)
        {
            mobInfo.Reset();
        }
        _mobShowing = false;
        ApplyMode("mobinfo");
    }

    private async Task LookupMobInfoAsync(string mob, string zone, bool force, CancellationToken token,
        WikiPriority priority, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (WikiLookupPaused)
        {
            return;
        }
        using var lookup = BeginWikiLookup(token);
        token = lookup.Token;
        void Report(string message)
        {
            if (!token.IsCancellationRequested)
            {
                report(message);
            }
        }
        Report("Checking EQLWiki...");
        try
        {
            await Task.Delay(250, token);
            string status = await Task.Run(async () =>
            {
                var info = _session.GetMob(zone, mob);
                await _wikiRepository.GetMobAsync(mob, priority, force, token);
                foreach (string spell in info.Spells)
                {
                    token.ThrowIfCancellationRequested();
                    await _wikiRepository.GetSpellAsync(spell, priority, force, token);
                }
                if (_mobSaveEnabled)
                {
                    await _session.SaveMobsAsync(_tracking, token);
                }
                var wikiInfo = _session.GetMob(zone, mob).Wiki;
                return wikiInfo?.Found == true
                    ? "EQLWiki cached. Missing stats are unknown, not zero."
                    : "No exact mob page found on EQLWiki. Local observations are shown separately.";
            }, token);
            if (!token.IsCancellationRequested)
            {
                Report(status);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is WikiTransportException or IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                string message = $"Mob wiki/save failed: {error.Message}. Cached data kept; use Refresh wiki to retry.";
                Report(message);
                SetMobInfoStatus(message);
            }
        }
    }

    private void OpenMobWiki(string mob, string zone, Action<string> report)
    {
        string url = _session.GetMob(zone, mob).Wiki?.Url ?? WikiLinks.PageUrl(mob);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            report($"Could not open wiki: {error.Message}");
        }
    }

    private async Task ResetMobResistancesAsync(string zone, string mob)
    {
        _session.ResetResistances(zone, mob, DateTime.Now);
        await _session.SaveMobsAsync(_tracking);
    }

    private void SetMobInfoStatus(string message)
    {
        _mobStartupStatus = message;
        if (_overlayViewModels.TryGetValue("mobinfo", out var viewModel) &&
            viewModel is MobInfoOverlayViewModel mobInfo)
        {
            mobInfo.ReportStatus(message);
        }
    }
}

/// <summary>Holds an active consider display while hovered and restarts its timeout on departure.</summary>
internal sealed class MobInfoVisibility
{
    private DateTime _visibleUntil;
    private bool _hovered;
    private bool _heldOpen;

    internal bool IsActive(DateTime now)
    {
        return _heldOpen || now < _visibleUntil;
    }

    internal void Show(DateTime now, double seconds)
    {
        _visibleUntil = seconds == 0 ? DateTime.MaxValue : now.AddSeconds(seconds);
        _heldOpen = _hovered;
    }

    internal void SetHovered(bool hovered, DateTime now, double seconds)
    {
        if (_hovered == hovered)
        {
            return;
        }
        bool active = IsActive(now);
        _hovered = hovered;
        if (hovered)
        {
            _heldOpen = active;
        }
        else if (_heldOpen)
        {
            Show(now, seconds);
        }
    }

    internal void Reset()
    {
        _visibleUntil = default;
        _heldOpen = false;
    }
}
