using System.IO;
using System.Text.Json;
using System.Windows;
using EqLog.Core;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private readonly HashSet<WikiLookupLease> _wikiLookups = [];
    private bool _wikiCacheResetting;

    private bool WikiLookupPaused
    {
        get
        {
            return !_dataReady || _wikiCacheResetting || _wikiCts.IsCancellationRequested;
        }
    }

    private WikiLookupLease BeginWikiLookup(CancellationToken token = default)
    {
        var lease = new WikiLookupLease(_wikiCts.Token, token, completed =>
        {
            _wikiLookups.Remove(completed);
        });
        _wikiLookups.Add(lease);
        return lease;
    }

    private async void ResetWikiCache_Click(object sender, RoutedEventArgs e)
    {
        if (WikiLookupPaused)
        {
            return;
        }
        if (!_mobSaveEnabled)
        {
            ResetWikiCacheStatus.Text = "Cannot safely reset wiki caches because mob tracking data could not be loaded. Fix the data-file error first.";
            return;
        }
        _wikiCacheResetting = true;
        ResetWikiCacheButton.IsEnabled = false;
        ResetWikiCacheStatus.Text = "Cancelling pending wiki lookups...";
        var lookups = _wikiLookups.ToArray();
        foreach (var lookup in lookups)
        {
            lookup.Cancel();
        }
        try
        {
            await _session.ResetWikiCachesAsync(_wikiRepository, () => Dispatcher.InvokeAsync(async () =>
            {
                await Task.WhenAll(lookups.Select(lookup => lookup.Completion));
                ResetWikiCacheStatus.Text = "Clearing wiki caches from memory and disk...";
                _questCache.Clear();
                _questCacheRevision++;
                _questAttempted.Clear();
                _questFailed.Clear();
                _questCacheCursor = 0;
                _questSelectionVersion++;
                QuestResults.ItemsSource = null;
                foreach (var item in _ownedQuestItems)
                {
                    item.HasQuestReferences = false;
                }
                _questProgress = "Wiki caches reset. Items will be checked again.";
                QuestLookupStatus.Text = _questProgress;
                if (_overlayViewModels.GetValueOrDefault("exaltations") is ExaltationsOverlayViewModel exaltations)
                {
                    exaltations.ResetWikiCache();
                }
                _skyCatalog = null;
                _skyOverlayLoadAttempted = false;
                _skyStatus = "Wiki cache reset. Reloading when next shown.";
                if (_overlayViewModels.GetValueOrDefault("tradeskills") is TradeskillsOverlayViewModel tradeskills)
                {
                    tradeskills.ResetWikiCache();
                }
                _spawnWikiPending.Clear();
                _spawnWikiRetry.Clear();
                if (_overlayViewModels.TryGetValue("npcs", out var npcViewModel) &&
                    npcViewModel is ViewModels.NpcBrowserOverlayViewModel npcs)
                {
                    npcs.ResetWikiState();
                }
                if (_overlayViewModels.TryGetValue("mobinfo", out var mobViewModel) &&
                    mobViewModel is ViewModels.MobInfoOverlayViewModel mobInfo)
                {
                    mobInfo.ReportStatus("Wiki cache reset.");
                }
                _spawnsSignature = "";
                _durationsSignature = "";
                _wikiIcons?.Reset();
                RefreshMergeView();
            }).Task.Unwrap(), _tracking);
            ResetWikiCacheStatus.Text = "All wiki caches reset. Next lookups fetch fresh information; tracking history and settings are unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            UsageTelemetry.TrackError(ex, UsageErrorSource.Wiki);
            ResetWikiCacheStatus.Text = $"Wiki cache reset did not finish: {ex.Message}. Some caches may already be cleared. Fix the error and retry.";
        }
        finally
        {
            _wikiCacheResetting = false;
            ResetWikiCacheButton.IsEnabled = true;
            if (_overlayViewModels.GetValueOrDefault("tradeskills") is TradeskillsOverlayViewModel tradeskills)
            {
                tradeskills.NotifyWikiLookupStateChanged();
            }
            if (_overlayViewModels.GetValueOrDefault("exaltations") is ExaltationsOverlayViewModel exaltations)
            {
                exaltations.NotifyWikiLookupStateChanged();
            }
        }
        if (!WikiLookupPaused && _overlayViewModels.TryGetValue("mobinfo", out var viewModel) &&
            viewModel is ViewModels.MobInfoOverlayViewModel mobInfo)
        {
            mobInfo.RefreshAfterWikiCacheReset();
        }
        if (!WikiLookupPaused)
        {
            _wikiIcons?.ResumePending();
        }
    }

    internal sealed class WikiLookupLease : IOverlayWikiLookupLease
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Action<WikiLookupLease> _finished;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WikiLookupLease(CancellationToken lifetime, CancellationToken caller, Action<WikiLookupLease> finished)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
            _finished = finished;
        }

        public CancellationToken Token
        {
            get
            {
                return _cancellation.Token;
            }
        }

        public Task Completion
        {
            get
            {
                return _completion.Task;
            }
        }

        public void Cancel()
        {
            _cancellation.Cancel();
        }

        public void Dispose()
        {
            _finished(this);
            _cancellation.Dispose();
            _completion.TrySetResult();
        }
    }
}
