namespace EqLog.Core.Parsing;

public abstract record LogEvent(DateTime Time);

public enum DamageKind
{
    Melee,
    DirectSpell,
    DamageOverTime,
    DamageShield,
}

public sealed record DamageEvent(
    DateTime Time,
    string Attacker,
    string Target,
    long Amount,
    DamageKind Kind,
    string? Source,
    string? Modifier) : LogEvent(Time)
{
    public bool IsCritical => Modifier?.Contains("Critical", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record HealEvent(
    DateTime Time,
    string Healer,
    string Target,
    long Amount,
    long? FullAmount,
    string? Spell,
    bool OverTime,
    string? Modifier) : LogEvent(Time);

/// <summary>A creature died. <see cref="Killer"/> may be null when unknown.</summary>
public sealed record KillEvent(DateTime Time, string? Killer, string Victim) : LogEvent(Time);

public enum ExperienceKind
{
    Solo,
    Party,
    Raid,
    Reward,
}

public sealed record ExperienceEvent(DateTime Time, ExperienceKind Kind, double? Percent) : LogEvent(Time);

public sealed record AaCapEvent(DateTime Time) : LogEvent(Time);

public sealed record LevelEvent(DateTime Time, int Level) : LogEvent(Time);

public sealed record LootEvent(
    DateTime Time,
    string Looter,
    string Item,
    int Quantity,
    string? Corpse,
    Coin? SoldFor,
    bool Stored) : LogEvent(Time);

public sealed record CoinEvent(DateTime Time, Coin Amount) : LogEvent(Time);

public enum ChatChannel
{
    Tell,
    Group,
    Guild,
    Raid,
    Fellowship,
    Say,
    Shout,
    Auction,
    OutOfCharacter,
    Channel,
}

public sealed record ChatEvent(
    DateTime Time,
    ChatChannel Channel,
    string From,
    string? To,
    string? ChannelName,
    string Text) : LogEvent(Time);

public sealed record ZoneEvent(DateTime Time, string Zone) : LogEvent(Time);

public sealed record LoginEvent(DateTime Time) : LogEvent(Time);

public sealed record CastBeginEvent(DateTime Time, string Spell) : LogEvent(Time);

public sealed record CastInterruptedEvent(DateTime Time, string? Spell) : LogEvent(Time);

public sealed record CastFizzleEvent(DateTime Time) : LogEvent(Time);

/// <summary>One of your spells was resisted by <see cref="Target"/> (target may be null when unspecified).</summary>
public sealed record SpellResistedEvent(DateTime Time, string? Target, string Spell) : LogEvent(Time);

/// <summary>"Your X spell has worn off of Y."</summary>
public sealed record SpellWornOffEvent(DateTime Time, string Spell, string Target) : LogEvent(Time);

/// <summary>"Y has been mesmerized." / "Y has been charmed." - the landing message of a spell on Y.</summary>
public sealed record EffectLandedEvent(DateTime Time, string Target, string Effect) : LogEvent(Time)
{
    public bool IsCharm => Effect == "charmed";
}

/// <summary>"Y has been awakened by Z." - a mez was broken.</summary>
public sealed record AwakenedEvent(DateTime Time, string Target, string By) : LogEvent(Time);
