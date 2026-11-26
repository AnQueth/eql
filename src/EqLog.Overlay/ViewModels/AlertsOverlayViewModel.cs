using System.Windows.Media;
using EqLog.Core.Triggers;
using EqLog.Overlay.Messaging;

namespace EqLog.Overlay.ViewModels;

/// <summary>The alerts overlay: keeps recent trigger alerts until they expire and renders them.</summary>
public sealed class AlertsOverlayViewModel : PresenterOverlayViewModel, IMessageReceiver<TriggerAlertsMessage>
{
    /// <summary>The most alerts shown at once; older ones are dropped.</summary>
    public const int MaxAlerts = 6;

    private const double DefaultSeconds = 8;

    /// <summary>The longest allowed alert delay (one day).</summary>
    private const double MaxDelaySeconds = 86_400;

    private readonly List<ActiveAlert> _active = [];
    private readonly List<PendingAlert> _pending = [];
    private readonly Dictionary<string, DateTime> _lastMatched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _playSound;
    private readonly Action<string> _reportTemplateError;
    private readonly Func<DateTime> _clock;

    /// <summary>Creates the alerts overlay.</summary>
    /// <param name="definition">The presenter.</param>
    /// <param name="actions">Shell actions.</param>
    /// <param name="source">Shared shell state.</param>
    /// <param name="playSound">Plays the alert sound.</param>
    /// <param name="reportTemplateError">Shows an invalid alert template message.</param>
    /// <param name="clock">The current time; defaults to <see cref="DateTime.Now"/>.</param>
    public AlertsOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source,
        Action playSound, Action<string> reportTemplateError, Func<DateTime>? clock = null)
        : base(definition, actions, source)
    {
        ArgumentNullException.ThrowIfNull(playSound);
        ArgumentNullException.ThrowIfNull(reportTemplateError);
        _playSound = playSound;
        _reportTemplateError = reportTemplateError;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>The alerts currently shown, oldest first.</summary>
    public IReadOnlyList<ActiveAlert> ActiveAlerts
    {
        get
        {
            return [.. _active];
        }
    }

    /// <summary>The longest time, in seconds, until all of <paramref name="alerts"/> have finished showing.</summary>
    public static double DisplaySeconds(IEnumerable<EqLog.Core.Triggers.TriggerAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        return alerts.Select(alert => Delay(alert.Trigger.DelaySeconds) + Seconds(alert.Trigger.Seconds))
            .DefaultIfEmpty(DefaultSeconds).Max();
    }

    /// <summary>Delayed alerts still counting down, soonest first.</summary>
    public IReadOnlyList<ActiveAlert> WaitingAlerts
    {
        get
        {
            return Waiting(_clock()).Reverse().ToArray();
        }
    }

    /// <inheritdoc />
    public void Receive(TriggerAlertsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = _clock();
        bool sound = Release(now);
        foreach (var alert in message.Alerts)
        {
            var text = TriggerPresentation.Format(alert);
            if (text.Error is not null)
            {
                _reportTemplateError(
                    $"Invalid alert text template: {text.Error.Message}. Showing the template without capture substitutions.");
            }

            double delay = Delay(alert.Trigger.DelaySeconds);
            if (InCooldown(alert.Trigger, now))
            {
                continue;
            }

            if (delay > 0)
            {
                Schedule(alert.Trigger, text.Text, now, delay);
                continue;
            }

            _active.Add(new ActiveAlert(text.Text, Brush(alert.Trigger.Color), now.AddSeconds(Seconds(alert.Trigger.Seconds)),
                alert.Trigger.Effect));
            sound |= alert.Trigger.Sound;
        }
        Prune(now);
        if (sound)
        {
            _playSound();
        }
    }

    /// <inheritdoc />
    protected override OverlayView CreateView(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Release(frame.Now))
        {
            _playSound();
        }
        Prune(frame.Now);
        return base.CreateView(frame) with { Alerts = [.. Waiting(frame.Now), .. _active] };
    }

    private static double Seconds(double seconds)
    {
        return seconds > 0 ? seconds : DefaultSeconds;
    }

    private static double Delay(double seconds)
    {
        return double.IsFinite(seconds) && seconds > 0 ? Math.Min(seconds, MaxDelaySeconds) : 0;
    }

    private static string TimerKey(AlertTrigger trigger)
    {
        return string.Join('\u001f', trigger.Name, trigger.Simple, trigger.Pattern);
    }

    /// <summary>Whether <paramref name="trigger"/> fired too recently; otherwise records this match as its latest.</summary>
    private bool InCooldown(AlertTrigger trigger, DateTime now)
    {
        string key = TimerKey(trigger);
        double cooldown = Delay(trigger.CooldownSeconds);
        if (cooldown > 0 && _lastMatched.TryGetValue(key, out var last) && now >= last && (now - last).TotalSeconds < cooldown)
        {
            return true;
        }

        _lastMatched[key] = now;
        return false;
    }

    private void Schedule(AlertTrigger trigger, string text, DateTime now, double delay)
    {
        string key = TimerKey(trigger);
        int index = _pending.FindIndex(p => p.Key == key);
        if (index >= 0 && !trigger.RestartDelay)
        {
            return;
        }

        string label = string.IsNullOrWhiteSpace(trigger.Name) ? text : trigger.Name.Trim();
        var pending = new PendingAlert(key, label, text, Brush(trigger.Color), now, now.AddSeconds(delay),
            Seconds(trigger.Seconds), trigger.Effect, trigger.Sound);
        if (index >= 0)
        {
            _pending[index] = pending;
        }
        else
        {
            _pending.Add(pending);
        }
    }

    /// <summary>Moves delayed alerts whose wait is over onto the screen; returns whether one wants a sound.</summary>
    private bool Release(DateTime now)
    {
        bool sound = false;
        foreach (var pending in _pending.Where(p => p.Due <= now).OrderBy(p => p.Due).ToArray())
        {
            _pending.Remove(pending);
            var expires = pending.Due.AddSeconds(pending.Seconds);
            if (expires <= now)
            {
                continue;
            }

            _active.Add(new ActiveAlert(pending.Text, pending.Color, expires, pending.Effect));
            sound |= pending.Sound;
        }
        return sound;
    }

    /// <summary>Countdown rows for delayed alerts, latest due first so they read soonest-first once reversed for display.</summary>
    private IEnumerable<ActiveAlert> Waiting(DateTime now)
    {
        return _pending.Where(p => p.Due > now).OrderByDescending(p => p.Due).Select(p =>
        {
            double total = (p.Due - p.Started).TotalSeconds;
            double left = (p.Due - now).TotalSeconds;
            double progress = total > 0 ? Math.Clamp(1 - (left / total), 0, 1) : 1;
            return new ActiveAlert(p.Label, p.Color, p.Due, Countdown: $"{Math.Ceiling(left):0}s", Progress: progress);
        });
    }

    private void Prune(DateTime now)
    {
        _active.RemoveAll(alert => alert.Expires <= now);
        if (_active.Count > MaxAlerts)
        {
            _active.RemoveRange(0, _active.Count - MaxAlerts);
        }
    }

    private Brush Brush(string? color)
    {
        string key = string.IsNullOrWhiteSpace(color) ? AlertColors.Default : color.Trim();
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = AlertColors.ToBrush(key);
            _brushes[key] = brush;
        }
        return brush;
    }

    private sealed record PendingAlert(string Key, string Label, string Text, Brush Color, DateTime Started, DateTime Due,
        double Seconds, AlertEffect Effect, bool Sound);
}
