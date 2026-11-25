using System.Windows.Media;
using EqLog.Overlay.Messaging;

namespace EqLog.Overlay.ViewModels;

/// <summary>The alerts overlay: keeps recent trigger alerts until they expire and renders them.</summary>
public sealed class AlertsOverlayViewModel : PresenterOverlayViewModel, IMessageReceiver<TriggerAlertsMessage>
{
    /// <summary>The most alerts shown at once; older ones are dropped.</summary>
    public const int MaxAlerts = 6;

    private const double DefaultSeconds = 8;

    private readonly List<ActiveAlert> _active = [];
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

    /// <summary>The longest display time among <paramref name="alerts"/>, in seconds.</summary>
    public static double DisplaySeconds(IEnumerable<EqLog.Core.Triggers.TriggerAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        return alerts.Select(alert => Seconds(alert.Trigger.Seconds)).DefaultIfEmpty(DefaultSeconds).Max();
    }

    /// <inheritdoc />
    public void Receive(TriggerAlertsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = _clock();
        bool sound = false;
        foreach (var alert in message.Alerts)
        {
            var text = TriggerPresentation.Format(alert);
            if (text.Error is not null)
            {
                _reportTemplateError(
                    $"Invalid alert text template: {text.Error.Message}. Showing the template without capture substitutions.");
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
        Prune(frame.Now);
        return base.CreateView(frame) with { Alerts = [.. _active] };
    }

    private static double Seconds(double seconds)
    {
        return seconds > 0 ? seconds : DefaultSeconds;
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
}
