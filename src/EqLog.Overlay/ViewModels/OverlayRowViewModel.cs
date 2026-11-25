using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Core.Triggers;

namespace EqLog.Overlay.ViewModels;

/// <summary>One table in a row card, with whether it is the first visible part of the card (no top margin).</summary>
/// <param name="Table">The table.</param>
/// <param name="IsFirst">Whether nothing precedes the table in its card.</param>
/// <param name="Owner">The overlay providing commands and font size.</param>
public sealed record OverlayTablePart(OverlayTable Table, bool IsFirst, OverlayViewModel Owner);

/// <summary>
/// An immutable presentation of one <see cref="OverlayRow"/>. A changed row is replaced, never mutated, so the row
/// templates bind one time. Actions go through the owning <see cref="OverlayViewModel"/>'s commands with the row as parameter.
/// </summary>
public sealed class OverlayRowViewModel
{
    private static readonly GridLength Zero = new(0, GridUnitType.Star);

    /// <summary>Wraps <paramref name="row"/> for <paramref name="owner"/>.</summary>
    public OverlayRowViewModel(OverlayViewModel owner, OverlayRow row)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(row);
        Owner = owner;
        Row = row;
        double fraction = row.Bar is not null && row.Fraction > 0 ? Math.Clamp(row.Fraction, 0, 1) : 0;
        BarWidth = fraction > 0 ? new GridLength(fraction, GridUnitType.Star) : Zero;
        BarRemainder = new GridLength(1 - fraction, GridUnitType.Star);
        var parts = new List<OverlayTablePart>();
        bool first = !HasCardLeft && !ShowStackedRight;
        for (var table = row.Table; table is not null; table = table.Then)
        {
            parts.Add(new OverlayTablePart(table, first, owner));
            first = false;
        }
        Tables = parts;
    }

    /// <summary>The overlay whose commands the row invokes.</summary>
    public OverlayViewModel Owner
    {
        get;
    }

    /// <summary>The underlying row.</summary>
    public OverlayRow Row
    {
        get;
    }

    /// <summary>The primary text.</summary>
    public string Left
    {
        get
        {
            return Row.Left;
        }
    }

    /// <summary>The right-aligned value text.</summary>
    public string Right
    {
        get
        {
            return Row.Right;
        }
    }

    /// <summary>The optional extra right-most column.</summary>
    public string? ExtraColumn
    {
        get
        {
            return Row.ExtraColumn;
        }
    }

    /// <summary>The optional value column between Left and Right.</summary>
    public string? ValueColumn
    {
        get
        {
            return Row.ValueColumn;
        }
    }

    /// <summary>The card details paragraph.</summary>
    public string? Details
    {
        get
        {
            return Row.Details;
        }
    }

    /// <summary>The row tooltip.</summary>
    public string? ToolTip
    {
        get
        {
            return Row.ToolTip;
        }
    }

    /// <summary>The alert effect animating the left text.</summary>
    public AlertEffect Effect
    {
        get
        {
            return Row.Effect;
        }
    }

    /// <summary>The bar brush.</summary>
    public Brush? Bar
    {
        get
        {
            return Row.Bar;
        }
    }

    /// <summary>Whether a proportional bar is drawn behind the row.</summary>
    public bool HasBar
    {
        get
        {
            return Row.Bar is not null && Row.Fraction > 0;
        }
    }

    /// <summary>The bar's star width.</summary>
    public GridLength BarWidth
    {
        get;
    }

    /// <summary>The star width after the bar.</summary>
    public GridLength BarRemainder
    {
        get;
    }

    /// <summary>The text brush override.</summary>
    public Brush? Foreground
    {
        get
        {
            return Row.Foreground;
        }
    }

    /// <summary>Whether <see cref="Foreground"/> overrides the theme text color.</summary>
    public bool HasForeground
    {
        get
        {
            return Row.Foreground is not null;
        }
    }

    /// <summary>Whether this row is a quest heading (accent color, extra spacing).</summary>
    public bool IsQuestHeading
    {
        get
        {
            return Row.QuestHeading;
        }
    }

    /// <summary>The weight of the row's column texts.</summary>
    public FontWeight Weight
    {
        get
        {
            return Row.Bold ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    /// <summary>Whether the row renders as a card (details or tables).</summary>
    public bool IsCard
    {
        get
        {
            return Row.Details is not null || Row.Table is not null;
        }
    }

    /// <summary>Whether the row renders as a single line.</summary>
    public bool IsPlain
    {
        get
        {
            return !IsCard;
        }
    }

    /// <summary>The weight of the left text: semi-bold titles in cards.</summary>
    public FontWeight LeftWeight
    {
        get
        {
            return IsCard ? FontWeights.SemiBold : Weight;
        }
    }

    /// <summary>Whether the left text wraps.</summary>
    public TextWrapping LeftWrapping
    {
        get
        {
            return Row.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
    }

    /// <summary>How the left text trims.</summary>
    public TextTrimming LeftTrimming
    {
        get
        {
            return Row.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        }
    }

    /// <summary>The left text alignment; alerts are centered.</summary>
    public TextAlignment LeftAlignment
    {
        get
        {
            return Owner.Key == "alerts" ? TextAlignment.Center : TextAlignment.Left;
        }
    }

    /// <summary>Whether the left text is underlined as a link.</summary>
    public bool IsLeftUnderlined
    {
        get
        {
            return Row.LootItem is not null || Row.Link is not null;
        }
    }

    /// <summary>Whether the left text starts with a clickable [Quest] badge.</summary>
    public bool ShowQuestBadge
    {
        get
        {
            return Row.HasQuestReferences && Row.LootItem is not null;
        }
    }

    /// <summary>The text after the quest badge.</summary>
    public string QuestBadgeText
    {
        get
        {
            return " " + Row.Left;
        }
    }

    /// <summary>Whether the card shows its left title.</summary>
    public bool HasCardLeft
    {
        get
        {
            return !string.IsNullOrEmpty(Row.Left);
        }
    }

    /// <summary>Whether the plain left text shows (single-line rows, or titled cards without a quest badge).</summary>
    public bool ShowLeftText
    {
        get
        {
            return (IsPlain || HasCardLeft) && !ShowQuestBadge;
        }
    }

    /// <summary>Whether the left text shows with its [Quest] badge.</summary>
    public bool ShowBadgeText
    {
        get
        {
            return (IsPlain || HasCardLeft) && ShowQuestBadge;
        }
    }

    /// <summary>Whether a stacked card shows its right text under the title.</summary>
    public bool ShowStackedRight
    {
        get
        {
            return Row.Stacked && !string.IsNullOrEmpty(Row.Right);
        }
    }

    /// <summary>The card tables, in order.</summary>
    public IReadOnlyList<OverlayTablePart> Tables
    {
        get;
    }

    /// <summary>Whether the card shows details text.</summary>
    public bool ShowDetails
    {
        get
        {
            return !string.IsNullOrEmpty(Row.Details);
        }
    }

    /// <summary>Whether nothing precedes the details in the card.</summary>
    public bool DetailsIsFirst
    {
        get
        {
            return !HasCardLeft && !ShowStackedRight && Tables.Count == 0;
        }
    }

    /// <summary>The row's outer margin.</summary>
    public Thickness RowMargin
    {
        get
        {
            if (IsCard && Row.Stacked)
            {
                return new Thickness(0, 0, 0, 6);
            }
            return Row.QuestHeading ? new Thickness(0, 12, 0, 5) : new Thickness(0, 0, 0, 3);
        }
    }

    /// <summary>The margin around the row's text.</summary>
    public Thickness TextMargin
    {
        get
        {
            return IsCard ? new Thickness(9, 7, 9, 7) : new Thickness(4, 2, 4, 2);
        }
    }

    /// <summary>Whether the row text is scaled relative to the overlay font size.</summary>
    public bool IsScaled
    {
        get
        {
            return Row.Scale != 1;
        }
    }

    /// <summary>The text scale.</summary>
    public double Scale
    {
        get
        {
            return Row.Scale;
        }
    }

    /// <summary>Whether a spawn-tracking checkbox shows.</summary>
    public bool ShowSpawnTrack
    {
        get
        {
            return Row.SpawnTrack is not null;
        }
    }

    /// <summary>Whether the mob is tracked.</summary>
    public bool IsSpawnTracked
    {
        get
        {
            return Row.SpawnTrack?.Tracked == true;
        }
    }

    /// <summary>The spawn-tracking checkbox tooltip.</summary>
    public string SpawnTrackToolTip
    {
        get
        {
            return IsSpawnTracked ? "Stop tracking this mob" : "Track this mob in the Spawn Timers overlay";
        }
    }

    /// <summary>Whether a spell-tracking checkbox shows.</summary>
    public bool ShowSpellTrack
    {
        get
        {
            return Row.SpellTrack is not null;
        }
    }

    /// <summary>Whether the spell is tracked.</summary>
    public bool IsSpellTracked
    {
        get
        {
            return Row.SpellTrack?.Tracked == true;
        }
    }

    /// <summary>The spell-tracking checkbox tooltip.</summary>
    public string SpellTrackToolTip
    {
        get
        {
            return IsSpellTracked ? "Stop tracking this spell" : "Track this spell in the Spell Timers overlay";
        }
    }

    /// <summary>Whether a watch star shows.</summary>
    public bool ShowWatch
    {
        get
        {
            return Row.WatchKey is not null;
        }
    }

    /// <summary>The watch star glyph.</summary>
    public string WatchGlyph
    {
        get
        {
            return Row.Watched ? "\u2605" : "\u2606";
        }
    }

    /// <summary>The watch star tooltip.</summary>
    public string WatchToolTip
    {
        get
        {
            return Row.Watched ? "Stop watching this quest" : "Watch this quest (shown in the Watching tab)";
        }
    }

    /// <summary>Whether the extra column shows.</summary>
    public bool ShowExtra
    {
        get
        {
            return Row.ExtraColumn is not null;
        }
    }

    /// <summary>The extra column width: fixed unless aligned with a value column.</summary>
    public double ExtraWidth
    {
        get
        {
            return Row.ValueColumn is null ? 48 : double.NaN;
        }
    }

    /// <summary>The shared size group aligning the extra column across rows.</summary>
    public string? ExtraSizeGroup
    {
        get
        {
            return Row.ValueColumn is null ? null : "OverlayExtraColumn";
        }
    }

    /// <summary>Whether the right column shows inline.</summary>
    public bool ShowRight
    {
        get
        {
            return (!string.IsNullOrEmpty(Row.Right) || Row.ValueColumn is not null) && !Row.Stacked;
        }
    }

    /// <summary>The right column's minimum width.</summary>
    public double RightMinWidth
    {
        get
        {
            return Row.ExtraColumn is not null && Row.ValueColumn is null ? 100 : 0;
        }
    }

    /// <summary>The right column alignment.</summary>
    public TextAlignment RightAlignment
    {
        get
        {
            return Row.ExtraColumn is not null ? TextAlignment.Right : TextAlignment.Left;
        }
    }

    /// <summary>The shared size group aligning the right column across rows.</summary>
    public string? RightSizeGroup
    {
        get
        {
            return Row.ValueColumn is null ? null : "OverlayRightColumn";
        }
    }

    /// <summary>Whether the value column shows.</summary>
    public bool ShowValue
    {
        get
        {
            return Row.ValueColumn is not null;
        }
    }

    /// <summary>Whether the whole row is a click target.</summary>
    public bool IsClickable
    {
        get
        {
            return Row.LootItem is not null || Row.Breakdown is not null || Row.Link is not null;
        }
    }

    /// <summary>Whether the row offers the item context menu.</summary>
    public bool HasLootItem
    {
        get
        {
            return Row.LootItem is not null;
        }
    }

    /// <summary>Breakdowns open on press so a moving bar cannot swallow the click.</summary>
    public ClickMode ClickMode
    {
        get
        {
            return Row.Breakdown is null ? ClickMode.Release : ClickMode.Press;
        }
    }

    /// <summary>The click target tooltip.</summary>
    public string ClickToolTip
    {
        get
        {
            if (Row.Breakdown is not null)
            {
                return "Click for ability breakdown";
            }
            if (Row.LootItem is not null)
            {
                return $"Open {Row.LootItem} on EQLWiki (right-click for quest references)";
            }
            return Row.ToolTip ?? "Open on EQLWiki";
        }
    }

    /// <summary>The click target margin.</summary>
    public Thickness ClickMargin
    {
        get
        {
            return IsCard ? new Thickness(8, 0, 0, 3) : new Thickness(0);
        }
    }
}
