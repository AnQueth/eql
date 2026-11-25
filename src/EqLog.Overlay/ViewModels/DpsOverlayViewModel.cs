using System.Collections.ObjectModel;
using EqLog.Application.Presentation;
using EqLog.Core;
using EqLog.Core.Tracking;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>The DPS overlay: a list of recent fights that expand to attacker rows or full ability details.</summary>
public sealed class DpsOverlayViewModel : PresenterOverlayViewModel
{
    private readonly Action<bool> _fullDetailsChanged;
    private bool _fullDetails;

    /// <summary>Creates the DPS overlay.</summary>
    public DpsOverlayViewModel(OverlayDefinition definition, IOverlayActions actions, IOverlayViewSource source,
        bool fullDetails, Action<bool> fullDetailsChanged)
        : base(definition, actions, source)
    {
        ArgumentNullException.ThrowIfNull(fullDetailsChanged);
        _fullDetails = fullDetails;
        _fullDetailsChanged = fullDetailsChanged;
    }

    /// <summary>Recent fights, newest first.</summary>
    public ObservableCollection<DpsFightViewModel> Fights { get; } = [];

    /// <summary>Whether there is no fight to show.</summary>
    public bool HasNoFights
    {
        get
        {
            return Fights.Count == 0;
        }
    }

    /// <summary>Whether expanded fights show ability tables and graphs instead of attacker rows.</summary>
    public bool FullDetails
    {
        get
        {
            return _fullDetails;
        }
        set
        {
            if (SetProperty(ref _fullDetails, value))
            {
                foreach (var fight in Fights)
                {
                    fight.FullDetails = value;
                }
                _fullDetailsChanged(value);
            }
        }
    }

    /// <summary>Updates the fight list from <paramref name="snapshot"/>, keeping existing fights and their state.</summary>
    public void UpdateFights(EngineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<FightSnapshot> fights = snapshot.RecentFights;
        if (fights.Count == 0 && snapshot.CurrentFight is { } current)
        {
            fights = [current];
        }

        var counts = new Dictionary<(string Target, DateTime Start), int>();
        var keys = new (string Target, DateTime Start, int Occurrence)[fights.Count];
        for (int index = 0; index < fights.Count; index++)
        {
            var identity = (fights[index].Target.ToUpperInvariant(), fights[index].Start);
            int occurrence = counts.GetValueOrDefault(identity);
            counts[identity] = occurrence + 1;
            keys[index] = (identity.Item1, identity.Start, occurrence);
        }

        var wanted = keys.ToHashSet();
        bool hadNone = Fights.Count == 0;
        for (int index = Fights.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(Fights[index].Key))
            {
                Fights.RemoveAt(index);
            }
        }

        for (int index = 0; index < fights.Count; index++)
        {
            int existing = IndexOf(keys[index], index);
            DpsFightViewModel fight;
            if (existing < 0)
            {
                fight = new DpsFightViewModel(this, keys[index], fights[index], _fullDetails);
                Fights.Insert(index, fight);
            }
            else
            {
                if (existing != index)
                {
                    Fights.Move(existing, index);
                }
                fight = Fights[index];
            }
            fight.Update(fights[index]);
        }

        if (hadNone != (Fights.Count == 0))
        {
            OnPropertyChanged(nameof(HasNoFights));
        }
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        base.OnFrame(frame);
        UpdateFights(frame.Snapshot);
    }

    private int IndexOf((string, DateTime, int) key, int start)
    {
        for (int index = start; index < Fights.Count; index++)
        {
            if (Fights[index].Key == key)
            {
                return index;
            }
        }
        return -1;
    }
}

/// <summary>One fight in the DPS overlay.</summary>
public sealed class DpsFightViewModel : ObservableObject
{
    private readonly OverlayViewModel _owner;
    private FightSnapshot _fight;
    private string _target = "";
    private string _summary = "";
    private bool _isExpanded;
    private bool _fullDetails;
    private IReadOnlyList<OverlayRow> _attackerRows = [];
    private AbilityDetailsViewModel? _details;

    internal DpsFightViewModel(OverlayViewModel owner, (string Target, DateTime Start, int Occurrence) key,
        FightSnapshot fight, bool fullDetails)
    {
        _owner = owner;
        Key = key;
        _fight = fight;
        _fullDetails = fullDetails;
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    internal (string Target, DateTime Start, int Occurrence) Key
    {
        get;
    }

    /// <summary>The overlay the fight belongs to; attacker rows invoke its commands.</summary>
    public OverlayViewModel Owner
    {
        get
        {
            return _owner;
        }
    }

    /// <summary>The fight's target.</summary>
    public string Target
    {
        get
        {
            return _target;
        }
        private set
        {
            SetProperty(ref _target, value);
        }
    }

    /// <summary>Personal DPS and duration.</summary>
    public string Summary
    {
        get
        {
            return _summary;
        }
        private set
        {
            SetProperty(ref _summary, value);
        }
    }

    /// <summary>Whether the fight's details are shown.</summary>
    public bool IsExpanded
    {
        get
        {
            return _isExpanded;
        }
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
            {
                RefreshDetails();
            }
        }
    }

    /// <summary>Whether details are full ability tables rather than attacker rows.</summary>
    public bool FullDetails
    {
        get
        {
            return _fullDetails;
        }
        set
        {
            if (SetProperty(ref _fullDetails, value))
            {
                OnPropertyChanged(nameof(ShowAttackers));
                if (_isExpanded)
                {
                    RefreshDetails();
                }
            }
        }
    }

    /// <summary>Whether the attacker rows are shown.</summary>
    public bool ShowAttackers
    {
        get
        {
            return !_fullDetails;
        }
    }

    /// <summary>The attacker rows shown when details are not full.</summary>
    public BulkObservableCollection<OverlayRowViewModel> Attackers { get; } = [];

    /// <summary>Whether no attackers were recorded.</summary>
    public bool HasNoAttackers
    {
        get
        {
            return _attackerRows.Count == 0;
        }
    }

    /// <summary>The full ability details, while expanded with full details; updated in place across refreshes.</summary>
    public AbilityDetailsViewModel? Details
    {
        get
        {
            return _details;
        }
        private set
        {
            SetProperty(ref _details, value);
        }
    }

    /// <summary>Expands or collapses the fight.</summary>
    public RelayCommand ToggleExpandedCommand
    {
        get;
    }

    internal void Update(FightSnapshot fight)
    {
        _fight = fight;
        Target = fight.Target;
        Summary = $"{OverlayPresenters.Short(CombatQueries.PersonalDps(fight))} dps  {OverlayPresenters.Clock(fight.DurationSeconds)}";
        if (_isExpanded)
        {
            RefreshDetails();
        }
    }

    private void RefreshDetails()
    {
        if (_fullDetails)
        {
            var breakdown = OverlayPresenters.FightBreakdown(_fight);
            if (Details is null)
            {
                Details = new AbilityDetailsViewModel(breakdown, compact: true);
            }
            else
            {
                Details.Update(breakdown);
            }

            return;
        }

        Details = null;
        var rows = OverlayPresenters.DpsAttackerRows(_fight);
        if (Attackers.Count == rows.Count && _attackerRows.SequenceEqual(rows))
        {
            return;
        }
        bool hadNone = HasNoAttackers;
        _attackerRows = rows;
        Attackers.Reconcile(rows, static (existing, row) => existing.Row == row, row => new OverlayRowViewModel(_owner, row));
        if (hadNone != HasNoAttackers)
        {
            OnPropertyChanged(nameof(HasNoAttackers));
        }
    }
}
