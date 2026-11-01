using System.Globalization;
using EqLog.Application.Tracking;
using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;

namespace EqLog.Application.Sessions;

/// <summary>Application-owned engine and tracking state. Views receive queries and issue commands, never the live engine.</summary>
public sealed partial class GameSession
{
    private readonly SpellDurationStore _durations;
    private readonly SpawnStore _spawns;
    private readonly MoteStore _motes;
    internal MobResistanceStore Mobs
    {
        get;
    }
    internal NpcCompendium Npcs
    {
        get;
    }
    internal LogEngine Engine
    {
        get; private set;
    }
    private volatile TriggerMatcher _triggers = TriggerMatcher.Empty;

    public GameSession(TrackingStartup startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        _durations = startup.Durations;
        _spawns = startup.Spawns;
        _motes = startup.Motes;
        Mobs = startup.Mobs ?? new();
        Npcs = startup.Npcs ?? new();
        MobsWritable = startup.Mobs is not null;
        NpcsWritable = startup.Npcs is not null;
        Engine = new(_durations, _spawns, _motes, Mobs, npcs: Npcs);
    }

    public long Revision
    {
        get; private set;
    }
    public bool MobsWritable
    {
        get;
    }
    public bool NpcsWritable
    {
        get;
    }

    public void Start(LogSessionService source, LogFileInfo file, bool resetOnLogin)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        var engine = new LogEngine(_durations, _spawns, _motes, Mobs, file.Path, Npcs)
        {
            CharacterName = file.CharacterName,
            ResetOnLogin = resetOnLogin,
        };
        source.Start(file, engine);
        Engine = engine;
        Revision++;
    }

    public Task<LogSessionFrame> CaptureFrameAsync(LogSessionService source, bool includeChat,
        HashSet<ChatChannel> channels, bool includeSpawns = true, bool includeSpellDurations = true)
    {
        return source.CaptureFrameAsync(Engine, includeChat, channels, includeSpawns, includeSpellDurations);
    }

    public IReadOnlyList<SpellDurationInfo> SpellDurations()
    {
        return Engine.SpellDurations(highestRanksOnly: true);
    }

    public EngineQuerySnapshot SpawnQueries(bool showAll = true)
    {
        return new(Engine.SpawnZone, Engine.SpawnCandidates()
            .Where(candidate => showAll || candidate.LikelyNamed || candidate.Info?.Tracked == true)
            .OrderByDescending(candidate => candidate.Info?.Tracked == true)
            .ThenBy(candidate => candidate.Mob, StringComparer.OrdinalIgnoreCase).ToArray(), [], []);
    }

    public void SetSpellTracked(string spell, bool tracked)
    {
        Engine.SetSpellTracked(spell, tracked);
    }

    public void SetSpellDurationOverride(string spell, double? seconds)
    {
        Engine.SetSpellDurationOverride(spell, seconds);
    }

    public void AddSpellDuration(string spell, double seconds)
    {
        Engine.SetSpellDurationOverride(spell, seconds);
        Engine.SetSpellTracked(spell, true);
    }

    public void RemoveSpellDuration(string spell)
    {
        Engine.RemoveSpellDuration(spell);
    }

    public void RelearnSpellDuration(string spell)
    {
        Engine.RelearnSpellDuration(spell);
    }

    public void SetSpawnTracked(string zone, string mob, bool tracked)
    {
        Engine.SetSpawnTracked(zone, mob, tracked);
    }

    public void RelearnSpawn(string zone, string mob)
    {
        Engine.RelearnSpawn(zone, mob);
    }

    public void SetSpawnOverride(string zone, string mob, double? seconds)
    {
        Engine.SetSpawnOverride(zone, mob, seconds);
    }

    public void ApplySpawnWiki(string mob, MobWikiInfo wiki)
    {
        Engine.SetSpawnWikiRespawn(mob,
            SpawnStore.TryParseWikiRespawn(wiki.Stats?.RespawnTime, out double seconds) ? seconds : null);
    }

    public IReadOnlyList<string> SpawnWikiRequests(DateTime nowUtc)
    {
        var requests = new List<string>();
        foreach (var candidate in Engine.SpawnCandidates().Where(candidate => candidate.Info is
        { Tracked: true, LearnedSeconds: null, OverrideSeconds: null }))
        {
            var wiki = Mobs.Get("", candidate.Mob).Wiki;
            if (wiki is not null && nowUtc - wiki.CheckedUtc < TimeSpan.FromDays(1))
            {
                ApplySpawnWiki(candidate.Mob, wiki);
            }
            else
            {
                requests.Add(candidate.Mob);
            }
        }
        return requests;
    }

    public void ResetSession()
    {
        Engine.ResetSession();
    }

    public void SetResetOnLogin(bool enabled)
    {
        Engine.ResetOnLogin = enabled;
    }

    public Task SaveSpellDurationsAsync(TrackingService tracking)
    {
        return tracking.SaveSpellDurationsAsync(Engine);
    }

    public Task SaveSpawnsAsync(TrackingService tracking)
    {
        return tracking.SaveSpawnsAsync(Engine);
    }

    public Task SaveMobsAsync(TrackingService tracking, CancellationToken cancellationToken = default)
    {
        return tracking.SaveMobsAsync(Mobs, cancellationToken);
    }

    public Task SaveNpcsAsync(TrackingService tracking)
    {
        return tracking.SaveNpcsAsync(Npcs);
    }

    public async Task<NpcReadModel?> GetNpcAsync(TrackingService tracking, string mob, string zone)
    {
        var entry = await tracking.GetNpcAsync(Npcs, mob, zone).ConfigureAwait(false);
        return entry is null ? null : NpcReadModel.From(entry);
    }

    public IReadOnlyList<NpcCatalogEntry> SearchNpcs(string name)
    {
        return Npcs.Search(name);
    }

    public MobDetails GetMob(string zone, string mob)
    {
        var info = Mobs.Get(zone, mob);
        return new(info.Wiki, info.Unclassified, info.Spells,
            info.Observations.Select(ResistanceDetails.From).ToArray());
    }

    public void ResetResistances(string zone, string mob, DateTime now)
    {
        Mobs.ResetResistances(zone, mob, now);
    }

    public WikiService CreateWikiService(IWikiTransport transport, IWikiCacheRepository caches)
    {
        return new(transport, Mobs, caches);
    }

    public Task ResetWikiCachesAsync(WikiService wiki, Func<Task> refresh, TrackingService tracking)
    {
        return wiki.ResetCachesAsync(refresh, Engine, tracking);
    }

    public IReadOnlyList<TriggerCompileError> ConfigureTriggers(IEnumerable<AlertTrigger> triggers, string? character)
    {
        var matcher = new TriggerMatcher(triggers, character);
        _triggers = matcher;
        return matcher.Errors.AsReadOnly();
    }

    public IReadOnlyList<TriggerAlert> MatchTriggers(IReadOnlyList<string> lines)
    {
        return _triggers.Match(lines);
    }

    public static bool TryReadSpellDuration(string text, out double seconds)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds) &&
            double.IsFinite(seconds) && seconds > 0 && seconds <= TimeSpan.MaxValue.TotalSeconds - 60;
    }

    public static bool TryReadSpawnDuration(string text, out double seconds)
    {
        return SpawnStore.TryParseDuration(text, out seconds);
    }
}

public sealed record ResistanceDetails(ResistanceType Type, ResistanceStatus Status, long Landed, long Resisted,
    decimal AssessedOutcomes, decimal? ResistPercent, long OtherResisted, long EstimatedPartial, long UnassessedDamage)
{
    public int MinimumDamageSamples { get; } = MobResistanceStore.MinimumDamageSamples;
    public decimal ReducedDamageThresholdPercent { get; } = MobResistanceStore.ReducedDamageRatio * 100;

    internal static ResistanceDetails From(ResistanceObservation observation)
    {
        return new(observation.Type, observation.Status, observation.Landed, observation.Resisted,
            observation.AssessedOutcomes, observation.ResistPercent, observation.OtherResisted,
            observation.EstimatedPartial, observation.UnassessedDamage);
    }
}
public sealed record MobDetails(MobWikiInfo? Wiki, long Unclassified, IReadOnlyList<string> Spells,
    IReadOnlyList<ResistanceDetails> Observations);
