using System.Globalization;
using System.IO;
using System.Windows.Media;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>Application operations used by the mob information overlay.</summary>
public interface IMobInfoOverlayServices
{
    /// <summary>Whether observed mob resistance data can be saved.</summary>
    bool CanSaveMobData { get; }

    /// <summary>Whether the mob information panel is enabled in settings.</summary>
    bool IsMobInfoEnabled { get; }

    /// <summary>Returns current resistance and wiki data for a mob and zone.</summary>
    MobDetails GetMob(string zone, string mob);

    /// <summary>Looks up the mob and its observed spells, reporting progress.</summary>
    Task LookupMobAsync(string mob, string zone, bool force, CancellationToken token,
        Action<string> report, WikiPriority priority);

    /// <summary>Opens the best known wiki page for the mob.</summary>
    void OpenMobWiki(string mob, string zone, Action<string> report);

    /// <summary>Clears this mob's resistance observations and persists the change.</summary>
    Task ResetResistancesAsync(string zone, string mob);

    /// <summary>Persists the configured display timeout.</summary>
    void PersistTimeout(double seconds);
}

/// <summary>Displays the current consider, wiki facts and locally observed resistances.</summary>
public sealed class MobInfoOverlayViewModel : OverlayViewModel,
    IMessageReceiver<MobConsideredMessage>, IMessageReceiver<ConsiderAssessmentMessage>, IDisposable
{
    private readonly IMobInfoOverlayServices _services;
    private readonly MobInfoVisibility _visibility = new();
    private readonly Action<Exception>? _reportResetError;
    private CancellationTokenSource? _wikiLookup;
    private ConsiderEvent? _consideredMob;
    private string _consideredZone = "";
    private string? _considerAssessment;
    private string _wikiStatus = "";
    private string _statusText;
    private string _timeoutText;
    private string _resetConfirmationText = "";
    private string _resetResultText = "";
    private ConsiderEvent? _resetTarget;
    private string _resetZone = "";
    private bool _canSaveData;
    private bool _isResetConfirmationVisible;
    private bool _isResetInProgress;
    private bool _newConsiderPending;
    private double _timeoutSeconds;

    /// <summary>Creates a mob information overlay.</summary>
    public MobInfoOverlayViewModel(OverlayDefinition definition, IOverlayActions actions,
        IMobInfoOverlayServices services, double timeoutSeconds, string statusText,
        Action<Exception>? reportResetError = null)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(statusText);
        _services = services;
        _reportResetError = reportResetError;
        _canSaveData = services.CanSaveMobData;
        _timeoutSeconds = timeoutSeconds;
        _timeoutText = timeoutSeconds.ToString(CultureInfo.CurrentCulture);
        _statusText = statusText;
        RefreshWikiCommand = new RelayCommand(() =>
        {
            StartWikiLookup(true);
        });
        OpenWikiCommand = new RelayCommand(OpenWiki);
        BeginResetCommand = new RelayCommand(BeginReset, CanReset);
        ConfirmResetCommand = new AsyncRelayCommand(ConfirmResetAsync, CanConfirmReset);
        CancelResetCommand = new RelayCommand(CancelReset);
        PropertyChanged += MobInfoOverlayViewModel_PropertyChanged;
    }

    /// <summary>Whether persistence for mob resistance observations is available.</summary>
    public bool CanSaveData
    {
        get
        {
            return _canSaveData;
        }
        private set
        {
            if (SetProperty(ref _canSaveData, value))
            {
                BeginResetCommand.RaiseCanExecuteChanged();
                ConfirmResetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The status shown in the mob settings panel.</summary>
    public string StatusText
    {
        get
        {
            return _statusText;
        }
        private set
        {
            SetProperty(ref _statusText, value);
        }
    }

    /// <summary>The timeout text, validated and persisted when the text box loses focus.</summary>
    public string TimeoutText
    {
        get
        {
            return _timeoutText;
        }
        set
        {
            if (!SetProperty(ref _timeoutText, value ?? ""))
            {
                return;
            }
            ApplyTimeoutText();
        }
    }

    /// <summary>Current wiki lookup status included in the overlay rows.</summary>
    public string WikiStatus
    {
        get
        {
            return _wikiStatus;
        }
        private set
        {
            if (SetProperty(ref _wikiStatus, value))
            {
                RenderCurrentMob();
            }
        }
    }

    /// <summary>The confirmation prompt for resetting resistance observations.</summary>
    public string ResetConfirmationText
    {
        get
        {
            return _resetConfirmationText;
        }
        private set
        {
            SetProperty(ref _resetConfirmationText, value);
        }
    }

    /// <summary>The result of the most recent resistance reset.</summary>
    public string ResetResultText
    {
        get
        {
            return _resetResultText;
        }
        private set
        {
            if (SetProperty(ref _resetResultText, value))
            {
                OnPropertyChanged(nameof(HasResetResult));
            }
        }
    }

    /// <summary>Whether the reset result contains text.</summary>
    public bool HasResetResult
    {
        get
        {
            return _resetResultText.Length > 0;
        }
    }

    /// <summary>Whether cancel remains enabled during reset confirmation.</summary>
    public bool CanCancelReset
    {
        get
        {
            return !_isResetInProgress;
        }
    }

    /// <summary>Whether the reset confirmation is visible.</summary>
    public bool IsResetConfirmationVisible
    {
        get
        {
            return _isResetConfirmationVisible;
        }
        private set
        {
            SetProperty(ref _isResetConfirmationVisible, value);
        }
    }

    /// <summary>Whether a resistance reset is in progress.</summary>
    public bool IsResetInProgress
    {
        get
        {
            return _isResetInProgress;
        }
        private set
        {
            if (SetProperty(ref _isResetInProgress, value))
            {
                ConfirmResetCommand.RaiseCanExecuteChanged();
                CancelResetCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanCancelReset));
            }
        }
    }

    /// <summary>The currently considered mob, if any.</summary>
    public ConsiderEvent? ConsideredMob
    {
        get
        {
            return _consideredMob;
        }
    }

    /// <summary>The zone associated with the current consider.</summary>
    public string ConsideredZone
    {
        get
        {
            return _consideredZone;
        }
    }

    /// <summary>Whether a consider or reset confirmation currently keeps the overlay visible.</summary>
    public bool IsActive
    {
        get
        {
            return _consideredMob is not null &&
                (_isResetConfirmationVisible || _visibility.IsActive(DateTime.Now));
        }
    }

    /// <summary>Refreshes the current mob and spell wiki data.</summary>
    public RelayCommand RefreshWikiCommand
    {
        get;
    }

    /// <summary>Opens the current mob's wiki page.</summary>
    public RelayCommand OpenWikiCommand
    {
        get;
    }

    /// <summary>Begins a resistance reset confirmation.</summary>
    public RelayCommand BeginResetCommand
    {
        get;
    }

    /// <summary>Confirms the resistance reset and saves it.</summary>
    public AsyncRelayCommand ConfirmResetCommand
    {
        get;
    }

    /// <summary>Cancels a resistance reset confirmation.</summary>
    public RelayCommand CancelResetCommand
    {
        get;
    }

    /// <inheritdoc />
    public void Receive(MobConsideredMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _consideredMob = message.Consider;
        _consideredZone = message.Zone;
        _considerAssessment = null;
        _visibility.Show(DateTime.Now, _timeoutSeconds);
        _newConsiderPending = true;
        OnPropertiesChanged(nameof(ConsideredMob), nameof(ConsideredZone), nameof(IsActive));
        RenderCurrentMob();
        if (_services.IsMobInfoEnabled)
        {
            StartWikiLookup(false);
        }
        else
        {
            CancelWikiLookup();
            WikiStatus = "";
        }
        BeginResetCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc />
    public void Receive(ConsiderAssessmentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_consideredMob is not { } current || message.Assessment.Time < current.Time ||
            message.Assessment.Time - current.Time > TimeSpan.FromSeconds(2))
        {
            return;
        }
        _considerAssessment = message.Assessment.Text;
        RenderCurrentMob();
    }

    /// <summary>Updates the visibility timeout at the beginning of a pump cycle.</summary>
    public void UpdateVisibility(DateTime now)
    {
        OnPropertyChanged(nameof(IsActive));
    }

    /// <summary>Consumes whether a new consider arrived since the previous pump cycle.</summary>
    public bool ConsumeNewConsider()
    {
        bool pending = _newConsiderPending;
        _newConsiderPending = false;
        return pending;
    }

    /// <summary>Clears the current mob, its pending lookup and visibility state.</summary>
    public void Reset()
    {
        CancelWikiLookup();
        _consideredMob = null;
        _consideredZone = "";
        _considerAssessment = null;
        _wikiStatus = "";
        _newConsiderPending = false;
        _resetTarget = null;
        _isResetConfirmationVisible = false;
        _isResetInProgress = false;
        _visibility.Reset();
        OnPropertiesChanged(nameof(ConsideredMob), nameof(ConsideredZone), nameof(WikiStatus),
            nameof(IsResetConfirmationVisible), nameof(IsResetInProgress), nameof(CanCancelReset), nameof(IsActive));
        RenderCurrentMob();
    }

    /// <summary>Refreshes wiki state after the application clears its cache.</summary>
    public void RefreshAfterWikiCacheReset()
    {
        WikiStatus = "Wiki cache reset.";
        if (_consideredMob is not null)
        {
            StartWikiLookup(false);
        }
    }

    /// <summary>Cancels pending lookups when the overlay shell shuts down.</summary>
    public void Dispose()
    {
        CancelWikiLookup();
    }

    /// <summary>Updates storage availability and the associated explanatory status.</summary>
    public void SetStorageAvailability(bool canSave, string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        CanSaveData = canSave;
        StatusText = status;
    }

    /// <summary>Replaces the settings-panel status with an application-level persistence error.</summary>
    public void ReportStatus(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        StatusText = status;
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RenderCurrentMob();
    }

    private void MobInfoOverlayViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBackgroundHovered))
        {
            _visibility.SetHovered(IsBackgroundHovered, DateTime.Now, _timeoutSeconds);
            OnPropertyChanged(nameof(IsActive));
        }
    }

    private void ApplyTimeoutText()
    {
        if (!double.TryParse(_timeoutText, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > 120)
        {
            StatusText = "Enter a display timeout from 0 to 120 seconds (0 keeps the panel until the next consider).";
            return;
        }
        _timeoutSeconds = seconds;
        _services.PersistTimeout(seconds);
        if (CanSaveData)
        {
            StatusText = seconds == 0 ? "Mob info stays visible until the next consider." :
                $"Mob info will show for {seconds:g} seconds after each new consider.";
        }
        if (IsActive)
        {
            _visibility.Show(DateTime.Now, seconds);
        }
        OnPropertyChanged(nameof(IsActive));
    }

    private bool CanReset()
    {
        return CanSaveData && !_isResetInProgress && !_isResetConfirmationVisible;
    }

    private bool CanConfirmReset()
    {
        return CanSaveData && _resetTarget is not null && !_isResetInProgress;
    }

    private void BeginReset()
    {
        if (_consideredMob is not { } considered)
        {
            return;
        }
        _resetTarget = considered;
        _resetZone = _consideredZone;
        ResetConfirmationText = $"Reset resistance observations for {considered.Mob} in {_resetZone}?\n" +
            "Spell outcomes and damage samples will be cleared. Wiki data, NPC encounters, and other mobs " +
            "are kept. Old log events stay excluded; new events resume learning.";
        ResetResultText = "";
        IsResetConfirmationVisible = true;
        _isResetInProgress = false;
        BeginResetCommand.RaiseCanExecuteChanged();
        ConfirmResetCommand.RaiseCanExecuteChanged();
        CancelResetCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsActive));
    }

    private async Task ConfirmResetAsync()
    {
        if (_resetTarget is not { } target)
        {
            return;
        }
        string mob = target.Mob;
        string zone = _resetZone;
        IsResetInProgress = true;
        ResetResultText = $"Resetting resistances for {mob} in {zone}...";
        try
        {
            await _services.ResetResistancesAsync(zone, mob);
            ResetResultText = $"Resistance history reset for {mob} in {zone}. Learning from new events only.";
            StatusText = ResetResultText;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _reportResetError?.Invoke(error);
            StatusText = $"Resistances cleared in memory for {mob} in {zone}, but saving failed: " +
                $"{error.Message}. Use Reset resistances to retry.";
            ResetResultText = StatusText;
        }
        finally
        {
            FinishReset();
        }
    }

    private void CancelReset()
    {
        if (_isResetInProgress)
        {
            return;
        }
        FinishReset();
    }

    private void FinishReset()
    {
        _isResetInProgress = false;
        if (_consideredMob == _resetTarget && _consideredZone == _resetZone)
        {
            _visibility.Show(DateTime.Now, _timeoutSeconds);
        }
        _resetTarget = null;
        IsResetConfirmationVisible = false;
        OnPropertyChanged(nameof(IsResetInProgress));
        OnPropertyChanged(nameof(CanCancelReset));
        OnPropertyChanged(nameof(IsActive));
        BeginResetCommand.RaiseCanExecuteChanged();
        ConfirmResetCommand.RaiseCanExecuteChanged();
        CancelResetCommand.RaiseCanExecuteChanged();
    }

    private void StartWikiLookup(bool force)
    {
        if (_consideredMob is not { } considered)
        {
            return;
        }
        CancelWikiLookup();
        _wikiLookup = new CancellationTokenSource();
        _ = LookupWikiAsync(considered.Mob, _consideredZone, force, _wikiLookup.Token);
    }

    private async Task LookupWikiAsync(string mob, string zone, bool force, CancellationToken token)
    {
        try
        {
            await _services.LookupMobAsync(mob, zone, force, token, message =>
            {
                WikiStatus = message;
            }, force ? WikiPriority.Interactive : WikiPriority.RealTime);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void CancelWikiLookup()
    {
        _wikiLookup?.Cancel();
        _wikiLookup?.Dispose();
        _wikiLookup = null;
    }

    private void OpenWiki()
    {
        if (_consideredMob is { } considered)
        {
            _services.OpenMobWiki(considered.Mob, _consideredZone, message =>
            {
                WikiStatus = message;
                ReportStatus(message);
            });
        }
    }

    private void RenderCurrentMob()
    {
        if (_consideredMob is not { } considered)
        {
            Show("Mob info", "Use /consider in game", []);
            return;
        }
        Show(BuildMobInfoContent(_services.GetMob(_consideredZone, considered.Mob), considered.Mob,
            _consideredZone, considered, _considerAssessment, _wikiStatus, CanSaveData));
    }

    /// <summary>Creates the shared mob-reference content used by mob info and NPC compendium views.</summary>
    internal static OverlayContent BuildMobInfoContent(MobDetails info, string mob, string zone, ConsiderEvent? consider,
        string? assessment, string wikiStatus, bool canSaveData, bool compact = false)
    {
        var rows = new List<OverlayRow>();
        if (consider is not null)
        {
            rows.Add(new($"Level {consider.Level}" + (consider.Rare ? "  RARE" : ""), Bold: true, Scale: 1.3));
            rows.Add(new(consider.Faction, Wrap: true, Bold: true));
            rows.Add(new(consider.Difficulty, Wrap: true));
        }
        if (assessment is not null)
        {
            rows.Add(new(assessment, Wrap: true));
        }
        if (info.Wiki is { Found: true } summary)
        {
            var wikiStats = new List<(string Label, string Value)>
            {
                ("Race", summary.Stats?.Race ?? "?"),
                ("Class", summary.Class ?? "?"),
                ("Level", summary.Level ?? "?"),
                ("HP", summary.HitPoints ?? "?"),
                ("AC", summary.Stats?.ArmorClass ?? "?"),
            };
            if (summary.Stats is { } combat)
            {
                wikiStats.Add(("Damage/hit", combat.DamagePerHit ?? "?"));
                wikiStats.Add(("Attacks/round", combat.AttacksPerRound ?? "?"));
                wikiStats.Add(("Attack speed", combat.AttackSpeed ?? "?"));
                wikiStats.Add(("HP regen", combat.HpRegen ?? "?"));
                wikiStats.Add(("Mana regen", combat.ManaRegen ?? "?"));
            }
            rows.Add(new("Wiki stats", Bold: true, Table: OverlayTable.Pairs(wikiStats, pairsPerRow: 2)));
        }
        if (info.Wiki is { Found: true, Special: not null } traits)
        {
            rows.Add(new($"Special (wiki): {traits.Special}", Wrap: true, Bold: true));
        }
        rows.AddRange(MobQuestRows(info.Wiki));
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
            rows.Add(new(observation.Type.ToString(), ResistancePresentation.Describe(observation),
                Bold: true, Foreground: color));
        }
        var wikiResists = info.Wiki?.Resistances;
        if (wikiResists is { Count: > 0 })
        {
            rows.Add(new("Wiki resistance stats", Scale: 0.9, Table: OverlayTable.Columns(
                Enum.GetValues<ResistanceType>().Where(wikiResists.ContainsKey)
                    .Select(type => (type.ToString(), $"{wikiResists[type]}")))));
        }
        foreach (var observation in info.Observations.Where(value => value.OtherResisted > 0))
        {
            rows.Add(new($"{observation.Type}: {observation.OtherResisted} other-spell resists (overall resistance unknown)",
                Wrap: true, Scale: 0.8));
        }
        foreach (var observation in info.Observations.Where(value => value.EstimatedPartial > 0 || value.UnassessedDamage > 0))
        {
            rows.Add(new($"{observation.Type}: {observation.EstimatedPartial} reduced-damage hits (cause unknown); " +
                $"{observation.UnassessedDamage} hits awaiting a typical-damage baseline. " +
                "These hits count as landed, not resisted.", Wrap: true, Scale: 0.8));
        }
        if (info.Unclassified > 0)
        {
            rows.Add(new($"{info.Unclassified} outcomes with unclassified/other spell resistance type", Wrap: true, Scale: 0.85));
        }
        if (info.Wiki is { Found: true } stats)
        {
            if (stats.Stats is { } details)
            {
                rows.Add(new("Wiki details", Scale: 0.9, Table: OverlayTable.Pairs(
                [
                    ("Aggro radius", details.AggroRadius ?? "?"),
                    ("Run speed", details.RunSpeed ?? "?"),
                    ("Respawn", details.RespawnTime ?? "?"),
                    ("Location", details.Location ?? "?"),
                ], pairsPerRow: 2)));
            }
            rows.Add(new($"Wiki checked {stats.CheckedUtc.ToLocalTime():d}; stats may differ for zone/rank variants.",
                Wrap: true, Scale: 0.8));
        }
        if (!compact)
        {
            rows.Add(new("Labels describe observed single-target direct spells and DoT casts (first tick = landed), not guaranteed immunity. " +
                "Only explicit resists enter the resist percentage. Hits below 90% of the sample-weighted median damage " +
                "are shown separately as reduced damage, not proof of resistance. The baseline requires at least 3 hits per character/loadout/rank. " +
                "Critical/modified hits and hits followed by a logged death within 3 seconds are excluded. " +
                "Buffs and other mitigation can affect estimates. Same-name mobs in this zone are combined.",
                Wrap: true, Scale: 0.8));
        }
        rows.Add(new(wikiStatus, Wrap: true, Scale: 0.8));
        if (!canSaveData)
        {
            rows.Add(new("Persistent mob storage unavailable; this session is NOT being saved. See General.",
                Wrap: true, Foreground: Brushes.Coral, Scale: 0.85));
        }
        return new(mob, zone, rows, WikiLinks.SearchUrl(mob), mob);
    }

    /// <summary>Builds quest-reference rows from cached wiki data.</summary>
    public static IReadOnlyList<OverlayRow> MobQuestRows(MobWikiInfo? wiki)
    {
        if (wiki is not { Found: true })
        {
            return [];
        }
        var rows = new List<OverlayRow>
        {
            new("Associated quests (wiki)", Bold: true, QuestHeading: true),
        };
        if (wiki.Quests is null)
        {
            rows.Add(new("Quest references have not been loaded yet. Use Refresh wiki.", Wrap: true, Scale: 0.85));
        }
        else if (wiki.Quests.Count == 0)
        {
            rows.Add(new("No associated quests found in the cached wiki references.", Wrap: true, Scale: 0.85));
        }
        else
        {
            rows.AddRange(wiki.Quests.Select(quest => new OverlayRow(quest.Title, Link: quest.Url,
                ToolTip: "Open quest on EQLWiki", Wrap: true)));
            rows.Add(new("Wiki associations may identify quest givers, enemies, or other mentions; not a turn-in checklist.",
                Wrap: true, Scale: 0.8));
        }
        return rows;
    }
}
