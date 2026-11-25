using EqLog.Application.Sessions;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

public sealed class NpcDpsHistoryViewModel : ObservableObject
{
    private NpcLoadoutDps? _selectedLoadout;
    private NpcDpsHistoryEncounterViewModel? _selectedEncounter;
    private IReadOnlyList<NpcDpsHistoryEncounterViewModel> _encounters = [];

    public NpcDpsHistoryViewModel(NpcReadModel npc)
    {
        Title = $"{npc.Mob} - DPS by loadout";
        Heading = $"{npc.Mob} - {npc.Zone}";
        Loadouts = NpcDpsHistory.From(npc);
        Summary = Loadouts.Count == 0
            ? "No personal damage history for this mob and zone yet."
            : "Click a loadout, then a fight for details. Average DPS = total personal damage / total encounter seconds for observed NPC deaths only.";
        SelectedLoadout = Loadouts.FirstOrDefault();
    }

    public string Title
    {
        get;
    }
    public string Heading
    {
        get;
    }
    public string Summary
    {
        get;
    }
    public IReadOnlyList<NpcLoadoutDps> Loadouts
    {
        get;
    }

    public IReadOnlyList<NpcDpsHistoryEncounterViewModel> Encounters
    {
        get
        {
            return _encounters;
        }
    }

    public IReadOnlyList<NpcDpsAbility> Abilities
    {
        get
        {
            return SelectedEncounter?.Details.Abilities ?? [];
        }
    }

    public string Details
    {
        get
        {
            return SelectedEncounter is { } selected
                ? $"{selected.Start:g} - {selected.Outcome} - {selected.Damage:N0} damage / {selected.Seconds:N1}s = {selected.Dps:N1} DPS"
                : "Select a fight for its saved effect details.";
        }
    }

    public NpcLoadoutDps? SelectedLoadout
    {
        get
        {
            return _selectedLoadout;
        }
        set
        {
            if (!SetProperty(ref _selectedLoadout, value))
            {
                return;
            }
            _encounters = value?.History.Select(item => new NpcDpsHistoryEncounterViewModel(
                item.Encounter.Start, NpcBrowserOverlayViewModel.NpcOutcomeText(item.Encounter),
                item.Encounter.YourDamage, item.Encounter.Seconds, item.Encounter.YourDps,
                item.Encounter.OtherDamage, item.Encounter.IncomingDamage, item)).ToArray() ?? [];
            OnPropertyChanged(nameof(Encounters));
            SelectedEncounter = _encounters.FirstOrDefault();
        }
    }

    public NpcDpsHistoryEncounterViewModel? SelectedEncounter
    {
        get
        {
            return _selectedEncounter;
        }
        set
        {
            if (SetProperty(ref _selectedEncounter, value))
            {
                OnPropertiesChanged(nameof(Abilities), nameof(Details));
            }
        }
    }
}

public sealed record NpcDpsHistoryEncounterViewModel(DateTime Start, string Outcome, long Damage,
    double Seconds, double Dps, long OtherDamage, long IncomingDamage, NpcDpsEncounter Details);
