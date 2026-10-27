using System.Windows.Media;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;

namespace EqLog.Overlay;

public sealed record OverlayContent(string Title, string? Subtitle, IReadOnlyList<OverlayRow> Rows);

public sealed record OverlayDefinition(string Key, string Caption, double Width, double Height, bool AutoScroll, Func<EngineSnapshot, LogEngine, OverlayView, OverlayContent> Render);

/// <summary>An alert currently on screen.</summary>
public sealed record ActiveAlert(string Text, Brush Color, DateTime Expires);

/// <summary>Per-render options the presenters need from the app.</summary>
public sealed record OverlayView(ISet<ChatChannel> ChatChannels, IReadOnlyList<ActiveAlert> Alerts, int LootGrouping = 0);

/// <summary>Turns engine snapshots into overlay rows.</summary>
public static class OverlayPresenters
{
    private static readonly Brush YouBar = Frozen(Color.FromArgb(150, 40, 120, 220));
    private static readonly Brush OtherBar = Frozen(Color.FromArgb(110, 110, 110, 130));
    private static readonly Brush TakenBar = Frozen(Color.FromArgb(140, 190, 50, 50));
    private static readonly Brush HealBar = Frozen(Color.FromArgb(140, 40, 160, 70));
    private static readonly Brush TimerBar = Frozen(Color.FromArgb(150, 30, 150, 160));
    private static readonly Brush CharmBar = Frozen(Color.FromArgb(160, 150, 60, 200));
    private static readonly Brush WarnBar = Frozen(Color.FromArgb(180, 220, 120, 20));
    private static readonly Brush WornBar = Frozen(Color.FromArgb(170, 200, 40, 40));
    private static readonly Brush Dim = Frozen(Color.FromRgb(170, 170, 180));
    private static readonly Brush Header = Frozen(Color.FromRgb(255, 215, 120));
    private static readonly Brush Bad = Frozen(Color.FromRgb(255, 90, 90));
    private static readonly Brush Warn = Frozen(Color.FromRgb(255, 170, 60));
    private static readonly Brush Good = Frozen(Color.FromRgb(120, 230, 120));

    private static readonly Dictionary<ChatChannel, Brush> ChatColors = new()
    {
        [ChatChannel.Tell] = Frozen(Color.FromRgb(230, 120, 255)),
        [ChatChannel.Group] = Frozen(Color.FromRgb(120, 200, 255)),
        [ChatChannel.Guild] = Frozen(Color.FromRgb(120, 255, 140)),
        [ChatChannel.Raid] = Frozen(Color.FromRgb(255, 160, 80)),
        [ChatChannel.Fellowship] = Frozen(Color.FromRgb(160, 255, 220)),
        [ChatChannel.Say] = Brushes.White,
        [ChatChannel.Shout] = Frozen(Color.FromRgb(255, 100, 100)),
        [ChatChannel.Auction] = Frozen(Color.FromRgb(120, 230, 120)),
        [ChatChannel.OutOfCharacter] = Frozen(Color.FromRgb(140, 220, 140)),
        [ChatChannel.Channel] = Frozen(Color.FromRgb(200, 200, 160)),
    };

    public static readonly IReadOnlyList<OverlayDefinition> All =
    [
        new("dps", "DPS", 300, 220, false, Dps),
        new("taken", "Damage Taken", 280, 180, false, Taken),
        new("heals", "Healing", 280, 220, false, Heals),
        new("timers", "Spell Timers", 300, 200, false, Timers),
        new("session", "Session", 260, 230, false, Session),
        new("loot", "Loot", 320, 220, false, Loot),
        new("chat", "Chat", 420, 200, true, Chat),
        new("spells", "Spell Casting", 300, 220, false, Spells),
        new("alerts", "Alerts", 420, 140, false, Alerts),
        new("spawns", "Spawn Timers", 300, 200, false, Spawns),
        new("motes", "Motes", 300, 320, false, Motes),
        new("quests", "Quest opportunities", 420, 320, false, (_, _, _) => new("Quest opportunities", null, [])),
        new("sky", "Sky quest tracker", 380, 420, false, (_, _, _) => new("Sky quest tracker", null, [])),
    ];

    private static OverlayContent Motes(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var m = s.Motes;
        var rows = new List<OverlayRow>
        {
            new("Tier", "session  (all time)", Foreground: Header, Bold: true),
        };
        var allTime = m.AllTime.ToDictionary(c => c.Tier, c => c.Count, StringComparer.OrdinalIgnoreCase);
        var session = m.Session.ToDictionary(c => c.Tier, c => c.Count, StringComparer.OrdinalIgnoreCase);
        var tiers = allTime.Keys.Union(session.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => allTime.GetValueOrDefault(t)).ThenByDescending(t => session.GetValueOrDefault(t))
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase);
        foreach (var tier in tiers)
        {
            int n = session.GetValueOrDefault(tier);
            rows.Add(new(tier, $"{n}  ({allTime.GetValueOrDefault(tier)})", Foreground: n > 0 ? Good : Dim));
        }

        rows.Add(new("Total", $"{m.SessionTotal}  ({m.AllTimeTotal})", Bold: true));

        if (m.Recent.Count > 0)
        {
            rows.Add(new("Recent", "", Foreground: Header, Bold: true));
            foreach (var d in m.Recent.Take(5))
            {
                string who = d.Looter == Names.You ? "" : $"[{d.Looter}] ";
                string qty = d.Quantity > 1 ? $" x{d.Quantity}" : "";
                rows.Add(new($"{who}{d.Tier}{qty}  {d.Source}", d.Time.ToString("HH:mm"), Foreground: d.Looter == Names.You ? null : Dim));
            }
        }

        if (m.ZoneRates.Count > 0)
        {
            rows.Add(new("Drops here", "drops / kills", Foreground: Header, Bold: true));
            foreach (var r in m.ZoneRates.Take(10))
            {
                string rate = r.DropRate is double d ? $"  {d:P0}" : "";
                rows.Add(new(r.Mob, $"{r.Drops}/{r.Kills}{rate}"));
            }
        }

        string sub = $"{m.SessionTotal} this session  {m.PerHour(s.Now):0.#}/hr";
        return new("Motes", sub, rows);
    }

    private static OverlayContent Spawns(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var sp = s.Spawns;
        var rows = new List<OverlayRow>();
        var ordered = sp.Timers
            .OrderBy(t => t.State switch { SpawnState.Due => 0, SpawnState.Dead => 1, SpawnState.Up => 2, _ => 3 })
            .ThenBy(t => t.RemainingSeconds ?? double.MaxValue)
            .ThenBy(t => t.Mob, StringComparer.OrdinalIgnoreCase);
        foreach (var t in ordered)
        {
            switch (t.State)
            {
                case SpawnState.Due:
                    rows.Add(new(t.Mob, $"DUE +{Clock(-(t.RemainingSeconds ?? 0))}", 1, WarnBar, Foreground: Warn, Bold: true));
                    break;
                case SpawnState.Dead when t.RemainingSeconds is double rem && t.RespawnSeconds is double r:
                    rows.Add(new(t.Mob, Clock(rem), 1 - (rem / r), rem <= 60 ? WarnBar : TimerBar));
                    break;
                case SpawnState.Dead:
                    double ago = t.KilledAt is DateTime k ? (s.Now - k).TotalSeconds : 0;
                    rows.Add(new(t.Mob, $"dead {Clock(ago)} (learning)", Foreground: Dim));
                    break;
                case SpawnState.Up:
                    rows.Add(new(t.Mob, $"up  seen {t.SeenAt:HH:mm}", Foreground: Good));
                    break;
                default:
                    rows.Add(new(t.Mob, t.RespawnSeconds is double rs ? $"~{Clock(rs)}" : "-", Foreground: Dim));
                    break;
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new(sp.Zone is null ? "No zone yet" : "No mobs tracked here. Pick them on the Spawn timers tab.", Foreground: Dim, Wrap: true));
        }

        return new("Spawn Timers", sp.Zone, rows);
    }

    private static OverlayContent Alerts(EngineSnapshot _, LogEngine __, OverlayView view)
    {
        var rows = view.Alerts.Count == 0
            ? new List<OverlayRow> { new("No alerts", Foreground: Dim) }
            : view.Alerts.Reverse().Select(a => new OverlayRow(a.Text, Foreground: a.Color, Wrap: true, Bold: true, Scale: 1.6)).ToList();
        return new("Alerts", null, rows);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static string Short(double n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{n / 1_000_000:0.##}M",
        >= 10_000 => $"{n / 1_000:0.#}K",
        _ => $"{n:N0}",
    };

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string Who(string name) => name == Names.You ? "You" : name;

    private static OverlayContent Dps(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var rows = new List<OverlayRow>();
        var f = s.CurrentFight;
        if (f is null)
        {
            rows.Add(new("No fight yet", Foreground: Dim));
        }
        else
        {
            rows.Add(new(f.Target, "All abilities", Foreground: Header, Bold: true, Breakdown: FightBreakdown(f)));
            foreach (var a in f.Attackers.Take(10))
            {
                bool you = a.Name == Names.You;
                rows.Add(new(
                    Who(a.Name),
                    $"{Short(a.Dps)} dps  {Short(a.Total)}  {a.Percent:0}%",
                    a.Percent / 100,
                    you ? YouBar : OtherBar,
                    Bold: you, Breakdown: new(Who(a.Name), $"{f.Target} | {Clock(f.DurationSeconds)} fight | snapshot at click", "DPS", a.Abilities)));
            }
        }

        rows.Add(new($"Session dmg {Short(s.SessionDamageByYou)}", s.SessionMaxHitByYou > 0 ? $"max {Short(s.SessionMaxHitByYou)}" : "", Foreground: Dim));
        var recent = s.RecentFights.Where(r => r != f).Take(3).ToList();
        foreach (var r in recent)
        {
            rows.Add(new($"{r.Target}", $"{Short(r.You?.Dps ?? 0)} dps  {Clock(r.DurationSeconds)}", Foreground: Dim,
                Breakdown: FightBreakdown(r)));
        }

        string title = f is null ? "DPS" : $"{f.Target}";
        string? sub = f is null
            ? null
            : $"{Short(f.Total)} in {Clock(f.DurationSeconds)}  {Short(f.Dps)} dps  {(f.Killed ? "killed" : f.IsActive ? "active" : "ended")}";
        return new(title, sub, rows);
    }

    private static AbilityBreakdown FightBreakdown(FightSnapshot fight)
    {
        var abilities = fight.Attackers.SelectMany(a => a.Abilities).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AbilityStats(g.Key, g.Sum(a => a.Total), g.Sum(a => a.Total) / fight.DurationSeconds,
                fight.Total == 0 ? 0 : 100.0 * g.Sum(a => a.Total) / fight.Total,
                g.Sum(a => a.Hits), g.Sum(a => a.Crits), g.Max(a => a.MaxHit), 0))
            .OrderByDescending(a => a.Total).ToArray();
        return new(fight.Target, $"All attackers | {Clock(fight.DurationSeconds)} fight | snapshot at click", "DPS", abilities);
    }

    private static OverlayContent Taken(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var d = s.DamageTaken;
        var rows = new List<OverlayRow>();
        foreach (var a in d.EncounterByAttacker.Take(6))
        {
            rows.Add(new(a.Name, $"{Short(a.Total)}  {a.Percent:0}%", a.Percent / 100, TakenBar));
        }

        rows.Add(new("Session", $"{Short(d.SessionTotal)}", Foreground: Header, Bold: true));
        if (d.SessionMaxHit > 0)
        {
            rows.Add(new($"Max hit: {d.SessionMaxHitFrom}", Short(d.SessionMaxHit), Foreground: Dim));
        }

        foreach (var a in d.SessionByAttacker.Take(4))
        {
            rows.Add(new(a.Name, $"{Short(a.Total)}  {a.Percent:0}%", Foreground: Dim));
        }

        string? sub = d.EncounterStart is null ? null : $"{Short(d.EncounterTotal)} in {Clock(d.EncounterSeconds)}  {Short(d.EncounterDtps)} dtps";
        return new("Damage Taken", sub, rows);
    }

    private static OverlayContent Heals(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var h = s.Heals;
        var rows = new List<OverlayRow>();
        if (h.OutgoingTotal > 0)
        {
            double over = h.OutgoingTotal + h.OutgoingOverheal == 0 ? 0 : 100.0 * h.OutgoingOverheal / (h.OutgoingTotal + h.OutgoingOverheal);
            rows.Add(new("Outgoing", $"{Short(h.OutgoingTotal)}  {over:0}% over", Foreground: Header, Bold: true));
            foreach (var x in h.OutgoingBySpell.Take(4))
            {
                rows.Add(new(x.Name, $"{Short(x.Total)}  {x.Percent:0}%", x.Percent / 100, HealBar));
            }

            foreach (var x in h.OutgoingByTarget.Take(4))
            {
                rows.Add(new($"\u2192 {Who(x.Name)}", $"{Short(x.Total)}", Foreground: Dim,
                    Breakdown: new(Who(x.Name), $"Your healing to this target | session {Clock(h.DurationSeconds)} | snapshot at click",
                        "HPS", h.OutgoingTargetAbilities.GetValueOrDefault(x.Name) ?? [])));
            }
        }

        rows.Add(new("Incoming", $"{Short(h.IncomingTotal)}", Foreground: Header, Bold: true));
        foreach (var x in h.IncomingByHealer.Take(5))
        {
            rows.Add(new(Who(x.Name), $"{Short(x.Total)}  {x.Percent:0}%", x.Percent / 100, HealBar,
                Breakdown: new(Who(x.Name), $"Healing received by you | session {Clock(h.DurationSeconds)} | snapshot at click",
                    "HPS", h.IncomingHealerAbilities.GetValueOrDefault(x.Name) ?? [])));
        }

        if (h.Healers.Count > 0)
        {
            rows.Add(new("Observed healers", "click for abilities", Foreground: Header, Bold: true));
            foreach (var healer in h.Healers)
                rows.Add(new(Who(healer.Name), Short(healer.Abilities.Sum(a => a.Total)),
                    Breakdown: new(Who(healer.Name), $"All observed healing | session {Clock(h.DurationSeconds)} | snapshot at click",
                        "HPS", healer.Abilities)));
        }
        return new("Healing", $"out {Short(h.OutgoingRecentHps)} hps  in {Short(h.IncomingRecentHps)} hps", rows);
    }

    private static OverlayContent Timers(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var rows = new List<OverlayRow>();
        foreach (var t in s.Timers)
        {
            string target = t.TargetConfirmed ? t.Target : $"{t.Target}?";
            string left = $"{t.Spell} \u2192 {target}";
            switch (t.State)
            {
                case TimerState.WornOff:
                    rows.Add(new(left, "WORE OFF", 1, WornBar, Bold: true));
                    break;
                case TimerState.Broken:
                    rows.Add(new(left, "BROKE", 1, WarnBar, Bold: true));
                    break;
                default:
                    if (t.RemainingSeconds is double rem && t.DurationSeconds is double dur)
                    {
                        bool expired = rem <= 0;
                        bool soon = rem <= Math.Min(10, dur * 0.2);
                        rows.Add(new(
                            left,
                            expired ? $"due +{Clock(-rem)}" : Clock(rem),
                            expired ? 0 : rem / dur,
                            soon ? WarnBar : t.IsCharm ? CharmBar : TimerBar,
                            Foreground: expired ? Warn : null,
                            Bold: t.IsCharm));
                    }
                    else
                    {
                        rows.Add(new(left, $"{Clock(t.ElapsedSeconds)} (learning)", Foreground: Dim, Bold: t.IsCharm));
                    }

                    break;
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new("No active timers", Foreground: Dim));
        }

        return new("Spell Timers", null, rows);
    }

    private static OverlayContent Session(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var x = s.Session;
        var now = s.Now;
        var rows = new List<OverlayRow>();
        if (x.ZoneEnteredAt is DateTime z)
        {
            rows.Add(new("In zone", Clock((now - z).TotalSeconds)));
        }

        if (x.SessionStart is DateTime start)
        {
            double hours = Math.Max((now - start).TotalHours, 1 / 60.0);
            rows.Add(new("Session", Clock((now - start).TotalSeconds)));
            rows.Add(new("Kills", $"{x.YourKills}  ({x.YourKills / hours:0}/hr)"));
        }

        rows.Add(new("Deaths", x.Deaths == 0 ? "0" : $"{x.Deaths}  (last: {x.LastKilledBy})", Foreground: x.Deaths > 0 ? Warn : null));
        rows.Add(new("XP gains", $"{x.ExperienceGains}  ({x.PartyExperienceGains} party)"));
        if (x.ExperiencePercent > 0)
        {
            rows.Add(new("XP", $"{x.ExperiencePercent:0.##}%  ({x.ExperiencePercentPerHour(now):0.#}%/hr)"));
        }

        if (x.Level is int lvl)
        {
            rows.Add(new("Level", x.LevelsGained > 0 ? $"{lvl}  (+{x.LevelsGained})" : $"{lvl}"));
        }

        if (x.AaCapped)
        {
            rows.Add(new("AA POINTS CAPPED - spend AAs!", Foreground: Bad, Bold: true, Wrap: true));
        }

        foreach (var k in x.RecentKills.Take(5))
        {
            rows.Add(new(k.Victim, k.Time.ToString("HH:mm:ss"), Foreground: Dim));
        }

        return new(x.Zone ?? "Session", s.CharacterName.Length > 0 ? s.CharacterName : null, rows);
    }

    private static OverlayContent Loot(EngineSnapshot s, LogEngine _, OverlayView view)
    {
        var l = s.Loot;
        var rows = new List<OverlayRow>
        {
            new("Coin looted", l.CoinFromCorpses.ToString(), Foreground: Good),
        };
        if (l.CoinFromAutoSell.Copper > 0)
        {
            rows.Add(new("Auto-sold", l.CoinFromAutoSell.ToString(), Foreground: Good));
        }

        if (view.LootGrouping is 1 or 2)
        {
            foreach (var group in l.OrderedGroups(view.LootGrouping == 1))
            {
                string who = group.Looter == Names.You ? "" : $"[{group.Looter}] ";
                rows.Add(new($"{who}{ItemNames.Normalize(group.Item)}", $"x{group.Quantity:N0}  {group.LastLoot:HH:mm}",
                    Foreground: group.Looter == Names.You ? null : Dim, LootItem: group.Item));
            }
        }
        else foreach (var r in l.Recent.Take(12))
        {
            string qty = r.Quantity > 1 ? $"{r.Quantity} x " : "";
            string who = r.Looter == Names.You ? "" : $"[{r.Looter}] ";
            string note = r.SoldFor is { } c ? $"sold {c}" : r.Stored ? "depot" : r.Time.ToString("HH:mm");
            rows.Add(new($"{who}{qty}{r.Item}", note, Foreground: r.Looter == Names.You ? null : Dim, LootItem: r.Item));
        }

        return new("Loot", $"{l.ItemsLooted} items this session", rows);
    }

    private static OverlayContent Chat(EngineSnapshot s, LogEngine engine, OverlayView view)
    {
        var rows = new List<OverlayRow>();
        foreach (var c in engine.RecentChat(60, view.ChatChannels))
        {
            string where = c.Channel switch
            {
                ChatChannel.Tell => c.From == Names.You ? $"-> {c.To}" : $"{c.From} tells you",
                ChatChannel.Channel => $"{Who(c.From)} [{c.ChannelName}]",
                _ => $"{Who(c.From)} [{c.Channel}]",
            };
            rows.Add(new($"{c.Time:HH:mm} {where}: {c.Text}", Foreground: ChatColors.GetValueOrDefault(c.Channel, Brushes.White), Wrap: true));
        }

        return new("Chat", null, rows);
    }

    private static OverlayContent Spells(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var sp = s.Spells;
        var rows = new List<OverlayRow>();
        int max = sp.Spells.Count == 0 ? 1 : sp.Spells.Max(x => x.Casts);
        foreach (var x in sp.Spells.Take(12))
        {
            string extra = (x.Interrupts, x.Resists) switch
            {
                (0, 0) => "",
                (var i, 0) => $"  {i} int",
                (0, var r) => $"  {r} res",
                var (i, r) => $"  {i} int {r} res",
            };
            rows.Add(new(x.Spell, $"{x.Casts}{extra}", (double)x.Casts / max, TimerBar));
        }

        string sub = $"{sp.TotalCasts} casts  {sp.TotalInterrupts} interrupted  {sp.TotalResists} resisted  {sp.Fizzles} fizzled";
        return new("Spell Casting", sub, rows);
    }
}
