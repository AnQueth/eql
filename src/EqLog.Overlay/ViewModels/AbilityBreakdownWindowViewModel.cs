using EqLog.Overlay;

namespace EqLog.Overlay.ViewModels;

public sealed class AbilityBreakdownWindowViewModel
{
    public AbilityBreakdownWindowViewModel(AbilityBreakdown breakdown)
    {
        Breakdown = breakdown;
        Details = new AbilityDetailsViewModel(breakdown, compact: false);
        WindowTitle = $"{breakdown.Title} - ability breakdown";
        TotalSummary = $"Total {breakdown.Abilities.Sum(ability => ability.Total):N0} · " +
            $"{breakdown.Abilities.Sum(ability => ability.PerSecond):N1} {breakdown.RateLabel}";
    }

    public string WindowTitle
    {
        get;
    }

    public string TotalSummary
    {
        get;
    }

    public AbilityBreakdown Breakdown
    {
        get;
    }

    public AbilityDetailsViewModel Details
    {
        get;
    }
}
