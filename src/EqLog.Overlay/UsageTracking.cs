using System.Windows;
using System.Windows.Controls.Primitives;

namespace EqLog.Overlay;

/// <summary>Fixed, privacy-safe action labels. Never use control text or user input as a label.</summary>
public enum UsageAction
{
    None,
    CheckUpdates,
    DownloadUpdate,
    RestartToUpdate,
    BrowseLog,
    ChooseLogFolder,
    ResetSession,
    ResetOverlayPositions,
    DockAll,
    FloatAll,
    ResetTrackingData,
    AddTrigger,
    DeleteTriggers,
    TestTrigger,
    MakeTriggerFromLog,
    RelearnSpawn,
    AddDuration,
    RelearnDuration,
    RemoveDuration,
    ImportTheme,
    OpenItemWiki,
    OpenQuest,
    CompleteQuest,
    RetryQuestLookups,
    ToggleSettings,
    ToggleAutoArrange,
    RefreshExaltationEffects,
}

/// <summary>Opt-in click instrumentation for WPF buttons, checkboxes, and toggle buttons.</summary>
public static class UsageTracking
{
    /// <summary>The fixed action recorded when a control is clicked, including keyboard activation.</summary>
    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(UsageAction), typeof(UsageTracking),
        new PropertyMetadata(UsageAction.None, ActionChanged),
        value => value is UsageAction action && Enum.IsDefined(action));

    /// <summary>Gets the action assigned to a control.</summary>
    /// <param name="element">The control to inspect.</param>
    /// <returns>The configured action, or None.</returns>
    public static UsageAction GetAction(DependencyObject element)
    {
        return (UsageAction)element.GetValue(ActionProperty);
    }

    /// <summary>Assigns click tracking to a button, checkbox, or toggle button.</summary>
    /// <param name="element">The control to instrument.</param>
    /// <param name="value">A fixed action label, or None to remove tracking.</param>
    public static void SetAction(DependencyObject element, UsageAction value)
    {
        element.SetValue(ActionProperty, value);
    }

    /// <summary>Records a non-button action using the same consent-gated event pipeline.</summary>
    /// <param name="action">The fixed action label.</param>
    public static void Track(UsageAction action)
    {
        UsageTelemetry.TrackAction(action);
    }

    private static void ActionChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ButtonBase button)
        {
            throw new InvalidOperationException("UsageTracking.Action requires a ButtonBase control.");
        }

        button.RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Clicked));
        if ((UsageAction)e.NewValue != UsageAction.None)
        {
            button.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Clicked), handledEventsToo: true);
        }
    }

    private static void Clicked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.Source))
        {
            Track(GetAction((DependencyObject)sender));
        }
    }
}
