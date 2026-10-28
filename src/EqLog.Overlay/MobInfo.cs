using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private static readonly string MobResistancePath = Path.Combine(AppSettings.Folder, "mob-resistances.json");
    private MobResistanceStore _mobResistances = new();
    private bool _mobSaveEnabled = true;
    private readonly ConcurrentQueue<(LogEvent Event, string Zone)> _mobEvents = new();
    private ConsiderEvent? _consideredMob;
    private string _consideredZone = "";
    private string? _considerAssessment;
    private DateTime _mobVisibleUntil;
    private CancellationTokenSource? _mobLookupCts;
    private string _mobWikiStatus = "";
    private bool _mobShowing;
    private WikiRepository _wikiRepository = null!;
    private readonly HashSet<string> _spawnWikiPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _spawnWikiRetry = new(StringComparer.OrdinalIgnoreCase);

    private void CheckSpawnWiki()
    {
        if (_wikiCts.IsCancellationRequested)
        {
            return;
        }
        foreach (var candidate in _engine.SpawnCandidates().Where(c => c.Info is
        { Tracked: true, LearnedSeconds: null, OverrideSeconds: null }))
        {
            string mob = candidate.Mob;
            var cached = _mobResistances.Get("", mob).Wiki;
            if (cached is not null && DateTime.UtcNow - cached.CheckedUtc < TimeSpan.FromDays(1))
            {
                _engine.SetSpawnWikiRespawn(mob,
                    SpawnStore.TryParseWikiRespawn(cached.Stats?.RespawnTime, out double seconds) ? seconds : null);
                continue;
            }
            if (_spawnWikiRetry.GetValueOrDefault(mob) <= DateTime.UtcNow && _spawnWikiPending.Add(mob))
            {
                _ = LookupSpawnWikiAsync(mob);
            }
        }
    }

    private async Task LookupSpawnWikiAsync(string mob)
    {
        try
        {
            var wiki = await _wikiRepository.GetMobAsync(mob, WikiPriority.Background, false, _wikiCts.Token);
            _engine.SetSpawnWikiRespawn(mob,
                SpawnStore.TryParseWikiRespawn(wiki.Stats?.RespawnTime, out double seconds) ? seconds : null);
            _spawnsSignature = "";
        }
        catch (OperationCanceledException) when (_wikiCts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or OperationCanceledException)
        {
            _spawnWikiRetry[mob] = DateTime.UtcNow.AddMinutes(5);
            MobInfoStatusText.Text = $"Spawn wiki lookup failed for {mob}: {ex.Message}. Retrying after 5 minutes.";
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
            return _consideredMob is not null && DateTime.Now < _mobVisibleUntil;
        }
    }

    private void InitializeMobInfo()
    {
        try
        {
            _mobResistances = MobResistanceStore.Load(MobResistancePath);
            MobInfoStatusText.Text = "Only mob and spell names are sent to EQLWiki; no log files are uploaded.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _mobSaveEnabled = false;
            MobInfoStatusText.Text = $"Could not load mob data: {ex.Message}. Tracking this session only; the existing file will not be overwritten.";
        }
        if (!double.IsFinite(_settings.MobInfoTimeoutSeconds) || _settings.MobInfoTimeoutSeconds < 0 ||
            _settings.MobInfoTimeoutSeconds > 120)
        {
            _settings.MobInfoTimeoutSeconds = 5;
            MobInfoStatusText.Text += " Invalid display timeout reset to 5 seconds.";
        }
        MobTimeoutBox.Text = _settings.MobInfoTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        _wikiRepository = new WikiRepository(_wikiHttp, _mobResistances, () =>
        {
            if (_mobSaveEnabled)
            {
                _mobResistances.Save(MobResistancePath);
            }
        });
        foreach (var quest in _questCache.Values)
        {
            _wikiRepository.SeedQuest(quest);
        }
        _wikiRepository.BackgroundFailed += (spell, error) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                MobInfoStatusText.Text = $"Background spell lookup/save failed for {spell}: {error.Message}. " +
                    "Cached data kept; future observations retry after 5 minutes, or use Refresh wiki.";
            });
        };
    }

    private void MobTimeout_Changed(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(MobTimeoutBox.Text, out double seconds) || !double.IsFinite(seconds) ||
            seconds < 0 || seconds > 120)
        {
            MobInfoStatusText.Text = "Enter a display timeout from 0 to 120 seconds (0 keeps the panel until the next consider).";
            return;
        }
        _settings.MobInfoTimeoutSeconds = seconds;
        if (_mobSaveEnabled)
        {
            MobInfoStatusText.Text = seconds == 0 ? "Mob info stays visible until the next consider."
                : $"Mob info will show for {seconds:g} seconds after each new consider.";
        }
        if (MobInfoActive)
        {
            _mobVisibleUntil = seconds == 0 ? DateTime.MaxValue : DateTime.Now.AddSeconds(seconds);
        }
    }

    private void ConfigureMobToolbar(OverlayPanel panel)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "Refresh wiki", FontSize = 11 };
        refresh.Click += (_, _) =>
        {
            if (_consideredMob is not null)
            {
                StartMobWikiLookup(force: true, WikiPriority.Interactive);
            }
        };
        var wiki = new Button { Content = "Wiki", FontSize = 11 };
        wiki.Click += (_, _) =>
        {
            if (_consideredMob is null)
            {
                return;
            }
            OpenMobWiki(_consideredMob.Mob, _consideredZone, message => _mobWikiStatus = message);
        };
        bar.Children.Add(refresh);
        bar.Children.Add(wiki);
        panel.SetToolbar(bar);
    }

    private void OpenMobWiki(string mob, string zone, Action<string> report)
    {
        string url = _mobResistances.Get(zone, mob).Wiki?.Url ?? QuestLookup.PageUrl(mob);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            report($"Could not open wiki: {ex.Message}");
        }
    }

    private void PumpMobInfo()
    {
        bool newConsider = false;
        while (_mobEvents.TryDequeue(out var entry))
        {
            if (entry.Event is ConsiderEvent con)
            {
                _consideredMob = con;
                _consideredZone = entry.Zone;
                _considerAssessment = null;
                _mobVisibleUntil = _settings.MobInfoTimeoutSeconds == 0
                    ? DateTime.MaxValue : DateTime.Now.AddSeconds(_settings.MobInfoTimeoutSeconds);
                newConsider = true;
            }
            else if (entry.Event is ConsiderAssessmentEvent assessment && _consideredMob is not null &&
                assessment.Time >= _consideredMob.Time && assessment.Time - _consideredMob.Time <= TimeSpan.FromSeconds(2))
            {
                _considerAssessment = assessment.Text;
            }
        }
        if (newConsider)
        {
            if (_settings.Overlays["mobinfo"].Visible)
            {
                StartMobWikiLookup(force: false, WikiPriority.RealTime);
            }
            else
            {
                _mobLookupCts?.Cancel();
                _mobWikiStatus = "";
            }
        }
        bool showing = MobInfoActive;
        if (newConsider || _mobShowing != showing)
        {
            _mobShowing = showing;
            ApplyMode("mobinfo");
            if (newConsider && _settings.Overlays["mobinfo"].Visible)
            {
                if (_settings.Overlays["mobinfo"].Docked)
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
        _mobLookupCts?.Cancel();
        _mobEvents.Clear();
        _consideredMob = null;
        _mobVisibleUntil = default;
        _mobShowing = false;
        ApplyMode("mobinfo");
    }

    private void StartMobWikiLookup(bool force, WikiPriority priority)
    {
        _mobLookupCts?.Cancel();
        _mobLookupCts?.Dispose();
        _mobLookupCts = CancellationTokenSource.CreateLinkedTokenSource(_wikiCts.Token);
        if (_consideredMob is not null)
        {
            _ = LookupMobInfoAsync(_consideredMob.Mob, _consideredZone, force, _mobLookupCts.Token, priority);
        }
    }

    private async Task LookupMobInfoAsync(string mob, string zone, bool force, CancellationToken token, WikiPriority priority,
        Action<string>? report = null)
    {
        void Report(string message)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            if (report is not null)
            {
                report(message);
            }
            else
            {
                _mobWikiStatus = message;
            }
        }
        Report("Checking EQLWiki...");
        try
        {
            // Debounce rapidly repeated considers and keep parsing/serialization off the dispatcher.
            await Task.Delay(250, token);
            string status = await Task.Run(async () =>
            {
                var info = _mobResistances.Get(zone, mob);
                await _wikiRepository.GetMobAsync(mob, priority, force, token);
                foreach (string spell in info.Spells)
                {
                    token.ThrowIfCancellationRequested();
                    await _wikiRepository.GetSpellAsync(spell, priority, force, token);
                }
                if (_mobSaveEnabled)
                {
                    _mobResistances.Save(MobResistancePath);
                }
                var wikiInfo = _mobResistances.Get(zone, mob).Wiki;
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                string message = $"Mob wiki/save failed: {ex.Message}. Cached data kept; use Refresh wiki to retry.";
                Report(message);
                MobInfoStatusText.Text = message;
            }
        }
    }

    private OverlayContent MobInfoContent()
    {
        if (_consideredMob is not { } con)
        {
            return new("Mob info", "Use /consider in game", []);
        }
        return BuildMobInfoContent(con.Mob, _consideredZone, con, _considerAssessment, _mobWikiStatus);
    }

    private OverlayContent BuildMobInfoContent(string mob, string zone, ConsiderEvent? con, string? assessment, string wikiStatus,
        bool compact = false)
    {
        var info = _mobResistances.Get(zone, mob);
        var rows = new List<OverlayRow>();
        if (con is not null)
        {
            rows.Add(new($"Level {con.Level}" + (con.Rare ? "  RARE" : ""), Bold: true, Scale: 1.3));
            rows.Add(new(con.Faction, Wrap: true, Bold: true));
            rows.Add(new(con.Difficulty, Wrap: true));
        }
        if (assessment is not null)
        {
            rows.Add(new(assessment, Wrap: true));
        }
        if (info.Wiki is { Found: true } summary)
        {
            rows.Add(new($"Wiki: {summary.Stats?.Race ?? "race ?"} | {summary.Class ?? "class ?"} | level {summary.Level ?? "?"}",
                Wrap: true, Scale: 0.9));
            rows.Add(new($"HP: {summary.HitPoints ?? "?"} | AC: {summary.Stats?.ArmorClass ?? "?"}",
                Wrap: true, Bold: true));
            if (summary.Stats is { } combat)
            {
                rows.Add(new($"Damage/hit: {combat.DamagePerHit ?? "?"} | Attacks/round: {combat.AttacksPerRound ?? "?"}",
                    Wrap: true, Scale: 0.9));
                rows.Add(new($"Attack speed: {combat.AttackSpeed ?? "?"} | HP regen: {combat.HpRegen ?? "?"} | Mana regen: {combat.ManaRegen ?? "?"}",
                    Wrap: true, Scale: 0.9));
            }
        }
        if (info.Wiki is { Found: true, Special: not null } traits)
        {
            rows.Add(new($"Special (wiki): {traits.Special}", Wrap: true, Bold: true));
        }
        rows.Add(new("Resistance", "Observed result", Bold: true));
        foreach (var observation in info.Observations)
        {
            Brush color = observation.Type switch
            {
                ResistanceType.Magic => Brushes.Plum,
                ResistanceType.Fire => Brushes.Coral,
                ResistanceType.Cold => Brushes.LightSkyBlue,
                ResistanceType.Poison => Brushes.LightGreen,
                ResistanceType.Disease => Brushes.Khaki,
                _ => Brushes.White,
            };
            rows.Add(new(observation.Type.ToString(), observation.DisplayText, Bold: true, Foreground: color));
        }
        var wikiResists = info.Wiki?.Resistances;
        if (wikiResists is { Count: > 0 })
        {
            rows.Add(new("Wiki resistance stats: " + string.Join(" | ",
                Enum.GetValues<ResistanceType>().Where(wikiResists.ContainsKey)
                    .Select(type => $"{type}: {wikiResists[type]}")), Wrap: true, Scale: 0.8));
        }
        foreach (var observation in info.Observations.Where(o => o.OtherResisted > 0))
        {
            rows.Add(new($"{observation.Type}: {observation.OtherResisted} other-spell resists (overall resistance unknown)",
                Wrap: true, Scale: 0.8));
        }
        foreach (var observation in info.Observations.Where(o => o.EstimatedPartial > 0 || o.UnassessedDamage > 0))
        {
            rows.Add(new($"{observation.Type}: {observation.EstimatedPartial} estimated reduced-damage resists; " +
                $"{observation.UnassessedDamage} hits awaiting a reliable damage baseline", Wrap: true, Scale: 0.8));
        }
        if (info.Unclassified > 0)
        {
            rows.Add(new($"{info.Unclassified} outcomes with unclassified/other spell resistance type", Wrap: true, Scale: 0.85));
        }
        if (info.Wiki is { Found: true } stats)
        {
            if (stats.Stats is { } details)
            {
                rows.Add(new($"Aggro radius: {details.AggroRadius ?? "?"} | Run speed: {details.RunSpeed ?? "?"}",
                    Wrap: true, Scale: 0.85));
                rows.Add(new($"Respawn: {details.RespawnTime ?? "?"} | Location: {details.Location ?? "?"}",
                    Wrap: true, Scale: 0.85));
            }
            rows.Add(new($"Wiki checked {stats.CheckedUtc.ToLocalTime():d}; stats may differ for zone/rank variants.",
                Wrap: true, Scale: 0.8));
        }
        if (!compact)
        {
            rows.Add(new("Labels describe observed single-target direct spells, not guaranteed immunity. " +
                "Hits below 90% of learned normal damage count as estimated partial resists. " +
                "Critical/modified hits and hits followed by a logged death within 3 seconds are excluded. " +
                "Buffs and other mitigation can affect estimates. Same-name mobs in this zone are combined.",
                Wrap: true, Scale: 0.8));
        }
        rows.Add(new(wikiStatus, Wrap: true, Scale: 0.8));
        if (!_mobSaveEnabled)
        {
            rows.Add(new("Persistent mob storage unavailable; this session is NOT being saved. See General.",
                Wrap: true, Foreground: Brushes.Coral, Scale: 0.85));
        }
        return new(mob, zone, rows);
    }

    private void SaveMobResistances(bool throwOnError)
    {
        if (!_mobSaveEnabled)
        {
            if (throwOnError)
            {
                throw new IOException("Mob data was not loaded successfully. The original file is protected from overwrites.");
            }
            return;
        }
        try
        {
            _mobResistances.Save(MobResistancePath);
        }
        catch (Exception ex) when (!throwOnError && (ex is IOException or UnauthorizedAccessException))
        {
            MobInfoStatusText.Text = $"Could not save mob resistances: {ex.Message}";
        }
    }
}
