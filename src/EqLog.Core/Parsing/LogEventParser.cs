using System.Globalization;
using System.Text.RegularExpressions;

namespace EqLog.Core.Parsing;

/// <summary>
/// Converts EverQuest log messages into typed <see cref="LogEvent"/>s.
/// References to the logging character ("You", "YOU", "yourself", the character's name) are normalized to <see cref="Names.You"/>.
/// </summary>
public sealed partial class LogEventParser
{
    public LogEventParser(string? characterName = null)
    {
        CharacterName = characterName ?? string.Empty;
    }

    public string CharacterName
    {
        get; set;
    }

    public LogEvent? Parse(LogLine line)
    {
        return Parse(line.Timestamp, line.Message);
    }

    public LogEvent? Parse(DateTime t, string m)
    {
        if (m.Length < 4)
        {
            return null;
        }

        // Chat first so player text can never be mistaken for combat lines.
        if (m[^1] == '\'' && m.Contains(", '", StringComparison.Ordinal))
        {
            return ParseChat(t, m);
        }

        if (m[0] == '[' && !string.IsNullOrWhiteSpace(CharacterName))
        {
            var who = WhoLoadoutRegex().Match(m);
            if (who.Success && string.Equals(who.Groups["name"].Value, CharacterName, StringComparison.OrdinalIgnoreCase))
            {
                string classes = who.Groups["classes"].Value.ToUpperInvariant();
                return new WhoLoadoutEvent(t, CharacterName,
                    int.Parse(who.Groups["level"].Value, CultureInfo.InvariantCulture), classes);
            }
        }

        if (m.StartsWith("This creature would ", StringComparison.Ordinal))
        {
            return new ConsiderAssessmentEvent(t, m);
        }

        if (m.Contains(" -- ", StringComparison.Ordinal) && m.Contains("(Lvl:", StringComparison.Ordinal))
        {
            var con = ConsiderRegex().Match(m);
            if (con.Success)
            {
                string mob = con.Groups["mob"].Value;
                bool rare = mob.EndsWith(" - a rare creature -", StringComparison.OrdinalIgnoreCase);
                if (rare)
                {
                    mob = mob[..^" - a rare creature -".Length];
                }
                return new ConsiderEvent(t, Name(mob), con.Groups["faction"].Value,
                    con.Groups["difficulty"].Value, int.Parse(con.Groups["level"].Value, CultureInfo.InvariantCulture), rare);
            }
        }

        if (m.Contains(" damage", StringComparison.Ordinal))
        {
            return ParseDamage(t, m);
        }

        if (m.Contains(" healed ", StringComparison.Ordinal))
        {
            return ParseHeal(t, m);
        }

        if (m.StartsWith("You begin ", StringComparison.Ordinal))
        {
            var cm = CastBeginRegex().Match(m);
            return cm.Success ? new CastBeginEvent(t, cm.Groups["s"].Value) : null;
        }

        if (m.Contains("slain", StringComparison.Ordinal) || m.EndsWith(" died.", StringComparison.Ordinal))
        {
            return ParseKill(t, m);
        }

        if (m.Contains("spell has worn off of ", StringComparison.Ordinal))
        {
            var wm = WornOffRegex().Match(m);
            return wm.Success ? new SpellWornOffEvent(t, wm.Groups["s"].Value, Name(wm.Groups["t"].Value)) : null;
        }

        if (m.Contains(" has been ", StringComparison.Ordinal) || m.EndsWith(" begins to spin.", StringComparison.Ordinal))
        {
            var em = EffectLandedRegex().Match(m);
            if (em.Success)
            {
                return new EffectLandedEvent(t, Name(em.Groups["t"].Value), em.Groups["e"].Value);
            }

            em = AwakenedRegex().Match(m);
            if (em.Success)
            {
                return new AwakenedEvent(t, Name(em.Groups["t"].Value), Name(em.Groups["b"].Value));
            }
        }

        if (m.Contains("interrupted", StringComparison.Ordinal))
        {
            if (m == "Your spell is interrupted." || m == "Your melody has been interrupted!")
            {
                return new CastInterruptedEvent(t, null);
            }

            var im = InterruptRegex().Match(m);
            return im.Success ? new CastInterruptedEvent(t, im.Groups["s"].Value) : null;
        }

        if (m == "Your spell fizzles!" || m.StartsWith("You miss a note", StringComparison.Ordinal))
        {
            return new CastFizzleEvent(t);
        }

        if (m.Contains("resisted", StringComparison.Ordinal))
        {
            var rm = ResistedRegex().Match(m);
            if (rm.Success)
            {
                return new SpellResistedEvent(t, Name(rm.Groups["t"].Value), rm.Groups["s"].Value);
            }

            rm = TargetResistedRegex().Match(m);
            return rm.Success ? new SpellResistedEvent(t, null, rm.Groups["s"].Value) : null;
        }

        if (m.StartsWith("You have reached the AA point cap", StringComparison.Ordinal))
        {
            return new AaCapEvent(t);
        }

        if (m.Contains("experience", StringComparison.Ordinal))
        {
            return ParseExperience(t, m);
        }

        if (m.Contains("looted", StringComparison.Ordinal))
        {
            return ParseLoot(t, m);
        }

        if (m.StartsWith("You receive ", StringComparison.Ordinal))
        {
            var coin = Coin.Parse(m);
            return coin.Copper > 0 ? new CoinEvent(t, coin) : null;
        }

        if (m.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            if (m.StartsWith("You have entered an area", StringComparison.Ordinal) || m[^1] != '.')
            {
                return null;
            }

            return new ZoneEvent(t, m["You have entered ".Length..^1]);
        }

        if (m.StartsWith("You have gained a level!", StringComparison.Ordinal))
        {
            var lm = LevelRegex().Match(m);
            return lm.Success ? new LevelEvent(t, int.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
        }

        if (m.StartsWith("Welcome to EverQuest", StringComparison.Ordinal))
        {
            return new LoginEvent(t);
        }

        return null;
    }

    private LogEvent? ParseDamage(DateTime t, string m)
    {
        if (m.EndsWith("damage.", StringComparison.Ordinal) || m.EndsWith(')'))
        {
            var mm = MeleeRegex().Match(m);
            if (mm.Success)
            {
                return Damage(t, mm.Groups["a"].Value, mm.Groups["t"].Value, mm.Groups["n"].Value, DamageKind.Melee, null, mm.Groups["m"]);
            }
        }

        if (m.Contains(" damage by ", StringComparison.Ordinal))
        {
            var sm = SpellDamageRegex().Match(m);
            if (sm.Success)
            {
                return Damage(t, sm.Groups["a"].Value, sm.Groups["t"].Value, sm.Groups["n"].Value, DamageKind.DirectSpell, sm.Groups["s"].Value, sm.Groups["m"]);
            }
        }

        if (m.Contains(" taken ", StringComparison.Ordinal))
        {
            var dm = DotRegex().Match(m);
            if (dm.Success)
            {
                string rest = dm.Groups["rest"].Value;
                string attacker;
                string spell;
                if (dm.Groups["via"].Value == "by")
                {
                    // "Pet has taken N damage by Spell." names no caster.
                    attacker = Names.Unknown;
                    spell = rest;
                }
                else if (rest.StartsWith("your ", StringComparison.Ordinal))
                {
                    attacker = Names.You;
                    spell = rest[5..];
                }
                else
                {
                    int by = rest.LastIndexOf(" by ", StringComparison.Ordinal);
                    if (by > 0)
                    {
                        spell = rest[..by];
                        attacker = rest[(by + 4)..];
                    }
                    else
                    {
                        spell = rest;
                        attacker = Names.Unknown;
                    }
                }

                return Damage(t, attacker, dm.Groups["t"].Value, dm.Groups["n"].Value, DamageKind.DamageOverTime, spell, dm.Groups["m"]);
            }
        }

        if (m.Contains("non-melee damage", StringComparison.Ordinal))
        {
            var ds = DamageShieldRegex().Match(m);
            if (ds.Success)
            {
                string attacker = ds.Groups["a"].Value;
                if (attacker.EndsWith("'s", StringComparison.Ordinal))
                {
                    attacker = attacker[..^2];
                }

                return Damage(t, attacker, ds.Groups["t"].Value, ds.Groups["n"].Value, DamageKind.DamageShield, "Damage Shield", ds.Groups["m"]);
            }
        }

        return null;
    }

    private DamageEvent? Damage(DateTime t, string attacker, string target, string amount, DamageKind kind, string? source, Group modifier)
    {
        string a = Name(attacker);
        string tg = Name(target);
        if (tg == Names.Self)
        {
            // "You hit yourself..." - self-inflicted, not useful for parsing.
            return null;
        }

        return new DamageEvent(t, a, tg, long.Parse(amount, CultureInfo.InvariantCulture), kind, source,
            modifier.Success ? modifier.Value : null);
    }

    private HealEvent? ParseHeal(DateTime t, string m)
    {
        var hm = HealRegex().Match(m);
        if (!hm.Success)
        {
            return null;
        }

        string healer = Name(hm.Groups["h"].Value);
        string target = Name(hm.Groups["t"].Value);
        if (target == Names.Self)
        {
            target = healer;
        }

        long? full = hm.Groups["f"].Success ? long.Parse(hm.Groups["f"].Value, CultureInfo.InvariantCulture) : null;
        return new HealEvent(t, healer, target, long.Parse(hm.Groups["n"].Value, CultureInfo.InvariantCulture), full,
            hm.Groups["s"].Success ? hm.Groups["s"].Value : null,
            hm.Groups["ot"].Success,
            hm.Groups["m"].Success ? hm.Groups["m"].Value : null);
    }

    [GeneratedRegex(@"^(?<mob>.+?) (?<faction>regards you .+?|judges you .+?|looks .+?|glowers at you .+?|glares at you .+?|scowls at you, ready to attack) -- (?<difficulty>.+?) \(Lvl:\s*(?<level>\d{1,3})\)$")]
    private static partial Regex ConsiderRegex();

    [GeneratedRegex(@"^\[(?<level>[1-9]\d{0,2})\s+(?<classes>(?:WAR|CLR|PAL|RNG|SHD|DRU|MNK|BRD|ROG|SHM|NEC|WIZ|MAG|ENC|BST|BER)(?:/(?:WAR|CLR|PAL|RNG|SHD|DRU|MNK|BRD|ROG|SHM|NEC|WIZ|MAG|ENC|BST|BER))*)\]\s+(?<name>[A-Za-z]+)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WhoLoadoutRegex();

    private KillEvent? ParseKill(DateTime t, string m)
    {
        if (m.StartsWith("You have slain ", StringComparison.Ordinal) && m[^1] == '!')
        {
            return new KillEvent(t, Names.You, Name(m["You have slain ".Length..^1]));
        }

        var km = SlainByRegex().Match(m);
        if (km.Success)
        {
            return new KillEvent(t, Name(km.Groups["k"].Value), Name(km.Groups["v"].Value));
        }

        if (m.EndsWith(" died.", StringComparison.Ordinal) && m != "You died.")
        {
            string victim = m[..^" died.".Length];
            if (!victim.Contains(' ', StringComparison.Ordinal) || victim.StartsWith("Your ", StringComparison.Ordinal))
            {
                return null;
            }

            return new KillEvent(t, null, Name(victim));
        }

        return null;
    }

    private static LogEvent? ParseExperience(DateTime t, string m)
    {
        var xm = ExperienceRegex().Match(m);
        if (xm.Success)
        {
            var kind = xm.Groups["k"].Value switch
            {
                "party" => ExperienceKind.Party,
                "raid" => ExperienceKind.Raid,
                _ => ExperienceKind.Solo,
            };
            double? pct = xm.Groups["p"].Success ? double.Parse(xm.Groups["p"].Value, CultureInfo.InvariantCulture) : null;
            return new ExperienceEvent(t, kind, pct);
        }

        if (m.StartsWith("You gained reward experience", StringComparison.Ordinal))
        {
            return new ExperienceEvent(t, ExperienceKind.Reward, null);
        }

        return null;
    }

    private LogEvent? ParseLoot(DateTime t, string m)
    {
        var lm = LootedDashRegex().Match(m);
        if (lm.Success)
        {
            var (item, qty) = SplitQuantity(lm.Groups["i"].Value);
            return new LootEvent(t, Name(lm.Groups["w"].Value), item, qty, Name(lm.Groups["c"].Value), null, false);
        }

        lm = LootedRegex().Match(m);
        if (lm.Success)
        {
            var (item, qty) = SplitQuantity(lm.Groups["i"].Value);
            string rest = lm.Groups["r"].Value;
            Coin? sold = null;
            int soldAt = rest.IndexOf("sold it for ", StringComparison.Ordinal);
            if (soldAt >= 0)
            {
                sold = Coin.Parse(rest[soldAt..]);
            }

            bool stored = rest.Contains("stored it", StringComparison.Ordinal);
            return new LootEvent(t, Names.You, item, qty, Name(lm.Groups["c"].Value), sold, stored);
        }

        return null;
    }

    private ChatEvent? ParseChat(DateTime t, string m)
    {
        Match cm;
        if ((cm = TellYouRegex().Match(m)).Success)
        {
            return new ChatEvent(t, ChatChannel.Tell, Name(cm.Groups["f"].Value), Names.You, null, cm.Groups["x"].Value);
        }

        if ((cm = YouToldRegex().Match(m)).Success)
        {
            return new ChatEvent(t, ChatChannel.Tell, Names.You, cm.Groups["to"].Value, null, cm.Groups["x"].Value);
        }

        if ((cm = YouSayToRegex().Match(m)).Success)
        {
            return new ChatEvent(t, ChannelFromName(cm.Groups["ch"].Value), Names.You, null, null, cm.Groups["x"].Value);
        }

        if ((cm = TellsChannelRegex().Match(m)).Success)
        {
            string ch = cm.Groups["ch"].Value;
            var channel = ChannelFromName(ch);
            return new ChatEvent(t, channel, Name(cm.Groups["f"].Value), null,
                channel == ChatChannel.Channel ? ch : null, cm.Groups["x"].Value);
        }

        if ((cm = SaysRegex().Match(m)).Success)
        {
            string v = cm.Groups["v"].Value;
            var channel = v.Contains("out of character", StringComparison.Ordinal) ? ChatChannel.OutOfCharacter
                : v.StartsWith("shout", StringComparison.Ordinal) ? ChatChannel.Shout
                : v.StartsWith("auction", StringComparison.Ordinal) ? ChatChannel.Auction
                : ChatChannel.Say;
            return new ChatEvent(t, channel, Name(cm.Groups["f"].Value), null, null, cm.Groups["x"].Value);
        }

        return null;
    }

    private static ChatChannel ChannelFromName(string ch)
    {
        return ch switch
        {
            "the group" or "your party" or "group" => ChatChannel.Group,
            "the guild" or "guild" => ChatChannel.Guild,
            "the raid" or "raid" => ChatChannel.Raid,
            "the fellowship" or "fellowship" => ChatChannel.Fellowship,
            _ => ChatChannel.Channel,
        };
    }

    private static (string Item, int Quantity) SplitQuantity(string text)
    {
        if (text.StartsWith("a ", StringComparison.Ordinal))
        {
            return (text[2..], 1);
        }

        if (text.StartsWith("an ", StringComparison.Ordinal))
        {
            return (text[3..], 1);
        }

        int sp = text.IndexOf(' ');
        if (sp > 0 && int.TryParse(text.AsSpan(0, sp), NumberStyles.None, CultureInfo.InvariantCulture, out int q))
        {
            return (text[(sp + 1)..], q);
        }

        return (text, 1);
    }

    /// <summary>Normalizes a creature/player name for consistent keying.</summary>
    public string Name(string raw)
    {
        switch (raw)
        {
            case "You" or "YOU" or "you" or "YOUR" or "Your" or "your":
                return Names.You;
            case "yourself" or "itself" or "himself" or "herself" or "themselves":
                return Names.Self;
        }

        if (CharacterName.Length > 0 && string.Equals(raw, CharacterName, StringComparison.OrdinalIgnoreCase))
        {
            return Names.You;
        }

        if (raw.Length > 0 && char.IsLower(raw[0]))
        {
            return string.Concat(char.ToUpperInvariant(raw[0]).ToString(), raw.AsSpan(1));
        }

        return raw;
    }

    private const string MeleeVerbs =
        "hits?|slash(?:es)?|pierces?|crush(?:es)?|punch(?:es)?|kicks?|bash(?:es)?|backstabs?|bites?|claws?|gores?|stings?|" +
        "mauls?|slams?|strikes?|smash(?:es)?|rends?|reaves?|shoots?|frenz(?:y|ies) on|sweeps?|slices?|cleaves?|stabs?|whips?|" +
        "chops?|tramples?|thrusts?|hacks?|batters?|smites?|bludgeons?|lacerates?|impales?";

    [GeneratedRegex(@"^(?<a>.+?) (?:" + MeleeVerbs + @") (?<t>.+?) for (?<n>\d+) points? of damage\.(?: \((?<m>[^)]+)\))?$")]
    private static partial Regex MeleeRegex();

    [GeneratedRegex(@"^(?<a>.+?) hit (?<t>.+?) for (?<n>\d+) points? of (?:[\w-]+ )?damage by (?<s>.+?)\.(?: \((?<m>[^)]+)\))?$")]
    private static partial Regex SpellDamageRegex();

    [GeneratedRegex(@"^(?<t>.+?) (?:has|have) taken (?<n>\d+) damage (?<via>from|by) (?<rest>.+?)\.(?: \((?<m>[^)]+)\))?$")]
    private static partial Regex DotRegex();

    [GeneratedRegex(@"^(?<t>.+?) (?:is|are) \w+ by (?<a>.+?) \w+ for (?<n>\d+) points? of non-melee damage[.!](?: \((?<m>[^)]+)\))?$")]
    private static partial Regex DamageShieldRegex();

    [GeneratedRegex(@"^(?<h>.+?) healed (?<t>.+?)(?<ot> over time)? for (?<n>\d+)(?: \((?<f>\d+)\))? hit points?(?: by (?<s>.+?))?\.(?: \((?<m>[^)]+)\))?$")]
    private static partial Regex HealRegex();

    [GeneratedRegex(@"^(?<v>.+?) (?:has|have) been slain by (?<k>.+)!$")]
    private static partial Regex SlainByRegex();

    [GeneratedRegex(@"^You gain (?:(?<k>party|raid) )?experience!(?: \((?<p>[\d.]+)%\))?")]
    private static partial Regex ExperienceRegex();

    [GeneratedRegex(@"Welcome to level (\d+)")]
    private static partial Regex LevelRegex();

    [GeneratedRegex(@"^--(?<w>.+?) (?:have|has) looted (?<i>.+?) from (?<c>.+?)(?:'s? corpse)?\.--$")]
    private static partial Regex LootedDashRegex();

    [GeneratedRegex(@"^You looted (?<i>.+?) from (?<c>.+?)'s? corpse(?<r>.*)$")]
    private static partial Regex LootedRegex();

    [GeneratedRegex(@"^You begin (?:casting|singing) (?<s>.+)\.$")]
    private static partial Regex CastBeginRegex();

    [GeneratedRegex(@"^Your (?<s>.+) spell is interrupted\.$")]
    private static partial Regex InterruptRegex();

    [GeneratedRegex(@"^(?<t>.+?) resisted your (?<s>.+?)!$")]
    private static partial Regex ResistedRegex();

    [GeneratedRegex(@"^Your target resisted the (?<s>.+?) spell\.$")]
    private static partial Regex TargetResistedRegex();

    [GeneratedRegex(@"^Your (?<s>.+) spell has worn off of (?<t>.+)\.$")]
    private static partial Regex WornOffRegex();

    [GeneratedRegex(@"^(?<t>.+?) (?:has been (?<e>mesmerized|charmed|rooted|stunned|enthralled|feared|snared|slowed|pacified|calmed)|begins to (?<e>spin))\.$")]
    private static partial Regex EffectLandedRegex();

    [GeneratedRegex(@"^(?<t>.+?) has been awakened by (?<b>.+)\.$")]
    private static partial Regex AwakenedRegex();

    [GeneratedRegex(@"^(?<f>.+?) tells you, '(?<x>.*)'$")]
    private static partial Regex TellYouRegex();

    [GeneratedRegex(@"^You told (?<to>.+?), '(?<x>.*)'$")]
    private static partial Regex YouToldRegex();

    [GeneratedRegex(@"^You say to your (?<ch>guild|raid|fellowship|group), '(?<x>.*)'$")]
    private static partial Regex YouSayToRegex();

    [GeneratedRegex(@"^(?<f>[^,']+?) tells? (?<ch>the group|the guild|the raid|the fellowship|your party|[^,' ]+:\d+), '(?<x>.*)'$")]
    private static partial Regex TellsChannelRegex();

    [GeneratedRegex(@"^(?<f>[^,']+?) (?<v>says? out of character|shouts? out of character|says?|shouts?|auctions?)(?: to [^,']+)?, '(?<x>.*)'$")]
    private static partial Regex SaysRegex();
}

public static class Names
{
    public const string You = "You";
    public const string Self = "\u0001self";
    public const string Unknown = "Unknown";
}
