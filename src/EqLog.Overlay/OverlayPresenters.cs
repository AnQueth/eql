using System.Windows.Media;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Tracking;
using EqLog.Core.Triggers;

namespace EqLog.Overlay;

public sealed record OverlayContent(string Title, string? Subtitle, IReadOnlyList<OverlayRow> Rows, string? TitleLink = null);

public sealed record OverlayDefinition(string Key, string Caption, double Width, double Height, bool AutoScroll, Func<EngineSnapshot, LogEngine, OverlayView, OverlayContent> Render);

/// <summary>An alert currently on screen.</summary>
public sealed record ActiveAlert(string Text, Brush Color, DateTime Expires, AlertEffect Effect = AlertEffect.None);

/// <summary>Per-render options the presenters need from the app.</summary>
public sealed record OverlayView(ISet<ChatChannel> ChatChannels, IReadOnlyList<ActiveAlert> Alerts, int LootGrouping = 0,
    QuestInventory? Inventory = null, WikiActivitySnapshot? WikiActivity = null, QuestWikiActivity? QuestWiki = null,
    IReadOnlyList<ChatEvent>? RecentChat = null, DpsFightKey? DpsFight = null);

/// <summary>Identifies one fight chosen in the DPS overlay picker.</summary>
public sealed record DpsFightKey(string Target, DateTime Start)
{
    public bool Matches(FightSnapshot fight)
    {
        return fight.Start == Start && string.Equals(fight.Target, Target, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Quest enrichment work waiting outside the shared HTTP scheduler.</summary>
public sealed record QuestWikiActivity(string Summary, string Status, string? ActiveItem, IReadOnlyList<string> Queued, bool HasError);

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
    private static Brush Dim
    {
        get { return ThemeBrush("OverlayDim"); }
    }

    private static Brush Header
    {
        get { return ThemeBrush("OverlayHeader"); }
    }

    private static Brush Bad
    {
        get { return ThemeBrush("OverlayBad"); }
    }

    private static Brush Warn
    {
        get { return ThemeBrush("OverlayWarn"); }
    }

    private static Brush Good
    {
        get { return ThemeBrush("OverlayGood"); }
    }

    /// <summary>The active theme's overlay text brush, falling back to the built-in default color.</summary>
    private static Brush ThemeBrush(string key)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return Frozen((Color)ColorConverter.ConvertFromString(ThemePalette.OptionalColorDefaults[key]));
    }

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
        new("mobinfo", "Mob info", 540, 620, false, (_, _, _) => new("Mob info", null, [])),
        new("npcs", "NPC compendium", 620, 720, false, (_, _, _) => new("NPC compendium", null, [])),
        new("wiki", "Wiki activity", 480, 360, false, WikiActivity),
        new("inventory", "Inventory & loot quests", 680, 620, false, (_, _, _) => new("Inventory & loot quests", null, [])),
        new("moteplan", "Mote upgrade planner", 520, 560, false, (_, _, _) => new("Mote upgrade planner", null, [])),
        new("exaltations", "Exaltations", 620, 560, false, (_, _, _) => new("Exaltations", null, [])),
    ];

    private const int MaxRequestRows = 15;
    private const int MaxQueuedTooltipItems = 40;

    private static OverlayContent WikiActivity(EngineSnapshot _, LogEngine __, OverlayView view)
    {
        var activity = view.WikiActivity;
        var rows = new List<OverlayRow>();
        if (activity is null)
        {
            rows.Add(new("Wiki monitor unavailable", Foreground: Warn));
            return new("Wiki activity", null, rows);
        }
        if (view.QuestWiki is { } quest)
        {
            rows.Add(new("Quest enrichment", Foreground: Header, Bold: true));
            rows.Add(new(quest.Summary, Wrap: true, Foreground: Dim));
            rows.Add(new(quest.Status, Wrap: true, Foreground: quest.HasError ? Bad : Dim));
            if (quest.ActiveItem is not null)
            {
                rows.Add(new(quest.ActiveItem, "Checking / saving", LootItem: quest.ActiveItem));
            }
        }
        rows.Add(new("HTTP requests", $"{activity.Requests.Count} pending / active", Foreground: Header, Bold: true));
        // Plain text rows (URL in the tooltip) keep the per-second elapsed refresh cheap; a table per request
        // meant rebuilding a grid for every request every second.
        foreach (var request in activity.Requests.Take(MaxRequestRows))
        {
            string priority = request.Priority == WikiPriority.RealTime ? "Real-time" : request.Priority.ToString();
            double elapsed = (DateTime.UtcNow - request.StartedUtc).TotalSeconds;
            rows.Add(new(request.Name, $"{request.State} · {priority} · {request.Attempt}/4 · {elapsed:0}s",
                ToolTip: request.Url));
        }
        if (activity.Requests.Count > MaxRequestRows)
        {
            rows.Add(new($"{activity.Requests.Count - MaxRequestRows:N0} more requests waiting", Foreground: Dim));
        }
        if (activity.Requests.Count == 0)
        {
            rows.Add(new(activity.Operations.Count > 0 ? "Processing wiki data / cache" :
                view.QuestWiki is { ActiveItem: not null } or { Queued.Count: > 0 }
                    ? "No HTTP in flight - quest work / cache checks pending" : "Idle", Wrap: true, Foreground: Dim));
        }
        var requestNames = activity.Requests.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string operation in activity.Operations.Where(o => !requestNames.Contains(o)))
        {
            rows.Add(new(operation, "Processing / cache", Foreground: Dim));
        }
        if (view.QuestWiki is { Queued.Count: > 0 } backlog)
        {
            rows.Add(new("Quest work queue", $"{backlog.Queued.Count:N0} waiting", Foreground: Header,
                Bold: true, QuestHeading: true));
            foreach (string item in backlog.Queued.Take(10))
            {
                rows.Add(new(item, "Background", LootItem: item));
            }
            if (backlog.Queued.Count > 10)
            {
                int hidden = backlog.Queued.Count - 10;
                string tooltip = string.Join("\n", backlog.Queued.Skip(10).Take(MaxQueuedTooltipItems));
                if (hidden > MaxQueuedTooltipItems)
                {
                    tooltip += $"\n… and {hidden - MaxQueuedTooltipItems:N0} more";
                }
                rows.Add(new($"{hidden:N0} more waiting (hover to view)", Foreground: Dim, ToolTip: tooltip));
            }
        }
        rows.Add(new("Recent activity / errors", Foreground: Header, Bold: true, QuestHeading: true));
        foreach (var entry in activity.Recent)
        {
            rows.Add(new($"{entry.TimeUtc.ToLocalTime():HH:mm:ss} {entry.Name}", entry.IsError ? "ERROR" : "",
                Foreground: entry.IsError ? Bad : Dim, Details: entry.IsError ? entry.Message : null,
                ToolTip: entry.IsError ? null : entry.Message));
        }
        return new("Wiki activity", "Priority queue · paced HTTP · Polly retries", rows);
    }

    private const string MoteValuesTip =
        "Values from the EQLWiki Mote Guide: equipment points / spell points, and the highest equipment +rank the mote works on.\n" +
        "Going from +N to +N+1 takes 2^N points; extra points carry over. Spells have no rank limit.";

    private static OverlayContent Motes(EngineSnapshot s, LogEngine _, OverlayView view)
    {
        var m = s.Motes;
        var rows = new List<OverlayRow>();
        var inventory = view.Inventory;
        var held = inventory?.Items.Where(i => i.Character.Equals(s.CharacterName, StringComparison.OrdinalIgnoreCase))
            .Select(i => (Item: i, Tier: MoteTiers.TierOf(ItemNames.Normalize(i.Item))))
            .Where(i => i.Tier is not null)
            .GroupBy(i => i.Tier!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        bool hasExport = inventory is not null &&
            inventory.ExportTimes.ContainsKey(s.CharacterName.ToUpperInvariant());
        string inventoryTip = (hasExport
            ? $"Export {inventory!.ExportTimes[s.CharacterName.ToUpperInvariant()].ToLocalTime():g}; owned includes newer loot."
            : "No inventory export: owned counts only reflect tracked loot/manual adjustments.") +
            "\nOwned excludes collection, installed items and historical drops." +
            "\nRun /outputfile inventory after spending or moving motes to refresh.";
        var ownedByTier = held?.ToDictionary(g => g.Key, g => g.Sum(i => i.Item.Owned), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        rows.Add(new("Tier  item/spell pts ≤max", "session (all)", Foreground: Header, Bold: true,
            ExtraColumn: "Owned", ToolTip: MoteValuesTip + "\n\n" + inventoryTip));
        var allTime = m.AllTime.ToDictionary(c => c.Tier, c => c.Count, StringComparer.OrdinalIgnoreCase);
        var session = m.Session.ToDictionary(c => c.Tier, c => c.Count, StringComparer.OrdinalIgnoreCase);
        var tiers = allTime.Keys.Union(session.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(ownedByTier.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => allTime.GetValueOrDefault(t)).ThenByDescending(t => session.GetValueOrDefault(t))
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase);
        foreach (var tier in tiers)
        {
            int n = session.GetValueOrDefault(tier);
            string owned = hasExport || ownedByTier.ContainsKey(tier)
                ? ownedByTier.GetValueOrDefault(tier).ToString("N0") : "?";
            string label = tier;
            string tip = inventoryTip;
            if (MoteValues.RankOf(tier) is int rank)
            {
                label = $"{tier}  {MoteValues.ItemPoints(rank)}/{MoteValues.SpellPoints(rank)} ≤+{MoteValues.ItemMaxTier(rank)}";
                tip = $"{MoteTiers.ItemName(tier)} (rank {rank})\n" +
                    $"Equipment: +{MoteValues.ItemPoints(rank)} pts, usable on items up to +{MoteValues.ItemMaxTier(rank)}\n" +
                    $"Spells: +{MoteValues.SpellPoints(rank)} pts, any rank\n\n" + inventoryTip;
            }
            rows.Add(new(label, $"{n}  ({allTime.GetValueOrDefault(tier)})", Foreground: n > 0 ? Good : Dim,
                LootItem: MoteTiers.ItemName(tier), ExtraColumn: owned, ToolTip: tip));
        }

        rows.Add(new("Total", $"{m.SessionTotal}  ({m.AllTimeTotal})", Bold: true,
            ExtraColumn: hasExport || ownedByTier.Count > 0 ? ownedByTier.Values.Sum().ToString("N0") : "?",
            ToolTip: inventoryTip));

        if (m.Recent.Count > 0)
        {
            rows.Add(new("Recent", "", Foreground: Header, Bold: true));
            foreach (var d in m.Recent.Take(5))
            {
                string who = d.Looter == Names.You ? "" : $"[{d.Looter}] ";
                string qty = d.Quantity > 1 ? $" x{d.Quantity}" : "";
                rows.Add(new($"{who}{d.Tier}{qty}  {d.Source}", d.Time.ToString("HH:mm"),
                    Foreground: d.Looter == Names.You ? null : Dim, LootItem: MoteTiers.ItemName(d.Tier)));
            }
        }

        if (m.ZoneRates.Count > 0)
        {
            rows.Add(new("Drops here", "drops / kills", Foreground: Header, Bold: true));
            foreach (var r in m.ZoneRates.Take(10))
            {
                string rate = r.DropRate is double d ? $"  {d:P0}" : "";
                rows.Add(new(r.Mob, $"{r.Drops}/{r.Kills}{rate}", Link: QuestLookup.SearchUrl(r.Mob)));
            }
        }

        string sub = $"{m.SessionTotal} this session  {m.PerHour(s.Now):0.#}/hr";
        return new("Motes", sub, rows);
    }

    private static OverlayContent Spawns(EngineSnapshot s, LogEngine engine, OverlayView __)
    {
        var sp = s.Spawns;
        var rows = new List<OverlayRow>();
        var ordered = sp.Timers
            .OrderBy(t => t.State switch { SpawnState.Due => 0, SpawnState.Dead => 1, SpawnState.Up => 2, _ => 3 })
            .ThenBy(t => t.RemainingSeconds ?? double.MaxValue)
            .ThenBy(t => t.Mob, StringComparer.OrdinalIgnoreCase);
        foreach (var t in ordered)
        {
            string source = t.UsesWiki ? " (wiki)" : "";
            string link = QuestLookup.SearchUrl(t.Mob);
            var tracking = sp.Zone is { } trackedZone
                ? new SpawnTrackChange(trackedZone, t.Mob, true)
                : null;
            switch (t.State)
            {
                case SpawnState.Due:
                    rows.Add(new(t.Mob, $"DUE +{Clock(-(t.RemainingSeconds ?? 0))}{source}", 1, WarnBar, Foreground: Warn, Bold: true, Link: link, SpawnTrack: tracking));
                    break;
                case SpawnState.Dead when t.RemainingSeconds is double rem && t.RespawnSeconds is double r:
                    rows.Add(new(t.Mob, Clock(rem) + source, 1 - (rem / r), rem <= 60 ? WarnBar : TimerBar, Link: link, SpawnTrack: tracking));
                    break;
                case SpawnState.Dead:
                    double ago = t.KilledAt is DateTime k ? (s.Now - k).TotalSeconds : 0;
                    rows.Add(new(t.Mob, $"dead {Clock(ago)} (learning)", Foreground: Dim, Link: link, SpawnTrack: tracking));
                    break;
                case SpawnState.Up:
                    rows.Add(new(t.Mob, $"up  seen {t.SeenAt:HH:mm}", Foreground: Good, Link: link, SpawnTrack: tracking));
                    break;
                default:
                    rows.Add(new(t.Mob, t.RespawnSeconds is double rs ? $"~{Clock(rs)}{source}" : "-", Foreground: Dim, Link: link, SpawnTrack: tracking));
                    break;
            }
        }

        if (sp.Zone is { } zone && string.Equals(engine.SpawnZone, zone, StringComparison.OrdinalIgnoreCase))
        {
            var candidates = engine.SpawnCandidates()
                .Where(c => c.LikelyNamed && c.Info?.Tracked != true)
                .OrderBy(c => c.Mob, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count > 0)
            {
                rows.Add(new("Track mobs", Foreground: Header, Bold: true, QuestHeading: true));
                foreach (var candidate in candidates)
                {
                    rows.Add(new(candidate.Mob, "Track",
                        Foreground: Dim,
                        SpawnTrack: new SpawnTrackChange(zone, candidate.Mob, false)));
                }
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new(sp.Zone is null ? "No zone yet" : "No mobs available to track yet.", Foreground: Dim, Wrap: true));
        }

        return new("Spawn Timers", sp.Zone, rows);
    }

    private static OverlayContent Alerts(EngineSnapshot _, LogEngine __, OverlayView view)
    {
        var rows = view.Alerts.Reverse()
            .Select(a => new OverlayRow(a.Text, Foreground: a.Color, Wrap: true, Bold: true, Scale: 1.6, Effect: a.Effect))
            .ToList();
        return new("Alerts", null, rows);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static string Short(double n)
    {
        return n switch
        {
            >= 1_000_000_000 => $"{n / 1_000_000_000:0.##}B",
            >= 1_000_000 => $"{n / 1_000_000:0.##}M",
            >= 10_000 => $"{n / 1_000:0.#}K",
            _ => $"{n:N0}",
        };
    }

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string Who(string name)
    {
        return name == Names.You ? "You" : name;
    }

    /// <summary>The fight the DPS overlay shows: the picked one while it is still among the recent fights, else the current fight.</summary>
    public static FightSnapshot? SelectedFight(EngineSnapshot s, DpsFightKey? key)
    {
        if (key is not null)
        {
            if (s.CurrentFight is { } current && key.Matches(current))
            {
                return current;
            }

            var picked = s.RecentFights.FirstOrDefault(key.Matches);
            if (picked is not null)
            {
                return picked;
            }
        }

        return s.CurrentFight;
    }

    private static OverlayContent Dps(EngineSnapshot s, LogEngine _, OverlayView view)
    {
        var rows = new List<OverlayRow>();
        var f = SelectedFight(s, view.DpsFight);
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
                    Bold: you, Breakdown: new(Who(a.Name), $"{f.Target} · {Clock(f.DurationSeconds)} fight · snapshot at click", "DPS", a.Abilities,
                        a.Timeline, f.DurationSeconds, f.Start, a.AbilityTimelines)));
            }
        }

        rows.Add(new($"Session dmg {Short(s.SessionDamageByYou)}", s.SessionMaxHitByYou > 0 ? $"max {Short(s.SessionMaxHitByYou)}" : "", Foreground: Dim));
        var recent = s.RecentFights.Where(r => f is null || !(r.Start == f.Start && r.Target == f.Target)).Take(3).ToList();
        foreach (var r in recent)
        {
            rows.Add(new($"{r.Target}", $"{Short(r.You?.Dps ?? 0)} dps  {Clock(r.DurationSeconds)}", Foreground: Dim,
                Breakdown: FightBreakdown(r)));
        }

        string title = f is null ? "DPS" : $"{f.Target}";
        string? sub = f is null
            ? null
            : $"{Short(f.Total)} in {Clock(f.DurationSeconds)}  {Short(f.Dps)} dps  {(f.Killed ? "killed" : f.IsActive ? "active" : "ended")}";
        return new(title, sub, rows, f is null ? null : QuestLookup.SearchUrl(f.Target));
    }

    private static AbilityBreakdown FightBreakdown(FightSnapshot fight)
    {
        var abilities = fight.Attackers.SelectMany(a => a.Abilities).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AbilityStats(g.Key, g.Sum(a => a.Total), g.Sum(a => a.Total) / fight.DurationSeconds,
                fight.Total == 0 ? 0 : 100.0 * g.Sum(a => a.Total) / fight.Total,
                g.Sum(a => a.Hits), g.Sum(a => a.Crits), g.Max(a => a.MaxHit), 0))
            .OrderByDescending(a => a.Total).ToArray();
        return new(fight.Target, $"All attackers · {Clock(fight.DurationSeconds)} fight · snapshot at click", "DPS", abilities,
            fight.Timeline, fight.DurationSeconds, fight.Start, DamageTimeline.Merge(fight.Attackers.SelectMany(a => a.AbilityTimelines)));
    }

    private static OverlayContent Taken(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var d = s.DamageTaken;
        var rows = new List<OverlayRow>();
        foreach (var a in d.EncounterByAttacker.Take(6))
        {
            rows.Add(new(a.Name, $"{Short(a.Total)}  {a.Percent:0}%", a.Percent / 100, TakenBar, Link: QuestLookup.SearchUrl(a.Name)));
        }

        rows.Add(new("Session", $"{Short(d.SessionTotal)}", Foreground: Header, Bold: true));
        if (d.SessionMaxHit > 0)
        {
            rows.Add(new($"Max hit: {d.SessionMaxHitFrom}", Short(d.SessionMaxHit), Foreground: Dim));
        }

        foreach (var a in d.SessionByAttacker.Take(4))
        {
            rows.Add(new(a.Name, $"{Short(a.Total)}  {a.Percent:0}%", Foreground: Dim, Link: QuestLookup.SearchUrl(a.Name)));
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
                    Breakdown: new(Who(x.Name), $"Your healing to this target · session {Clock(h.DurationSeconds)} · snapshot at click",
                        "HPS", h.OutgoingTargetAbilities.GetValueOrDefault(x.Name) ?? [])));
            }
        }

        rows.Add(new("Incoming", $"{Short(h.IncomingTotal)}", Foreground: Header, Bold: true));
        foreach (var x in h.IncomingByHealer.Take(5))
        {
            rows.Add(new(Who(x.Name), $"{Short(x.Total)}  {x.Percent:0}%", x.Percent / 100, HealBar,
                Breakdown: new(Who(x.Name), $"Healing received by you · session {Clock(h.DurationSeconds)} · snapshot at click",
                    "HPS", h.IncomingHealerAbilities.GetValueOrDefault(x.Name) ?? [])));
        }

        if (h.Healers.Count > 0)
        {
            rows.Add(new("Observed healers", "click for abilities", Foreground: Header, Bold: true));
            foreach (var healer in h.Healers)
            {
                rows.Add(new(Who(healer.Name), Short(healer.Abilities.Sum(a => a.Total)),
                    Breakdown: new(Who(healer.Name), $"All observed healing · session {Clock(h.DurationSeconds)} · snapshot at click",
                        "HPS", healer.Abilities)));
            }
        }
        return new("Healing", $"out {Short(h.OutgoingRecentHps)} hps  in {Short(h.IncomingRecentHps)} hps", rows);
    }

    private static OverlayContent Timers(EngineSnapshot s, LogEngine engine, OverlayView __)
    {
        var rows = new List<OverlayRow>();
        foreach (var t in s.Timers)
        {
            string target = t.TargetConfirmed ? t.Target : $"{t.Target}?";
            string left = string.IsNullOrWhiteSpace(t.Target) || t.Target == "(target)"
                ? t.Spell
                : $"{t.Spell} \u2192 {target}";
            var tracking = new SpellTrackChange(t.Spell, true);
            switch (t.State)
            {
                case TimerState.WornOff:
                    rows.Add(new(left, "WORE OFF", 1, WornBar, Bold: true, SpellTrack: tracking));
                    break;
                case TimerState.Broken:
                    rows.Add(new(left, "BROKE", 1, WarnBar, Bold: true, SpellTrack: tracking));
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
                            Bold: t.IsCharm,
                            SpellTrack: tracking));
                    }
                    else
                    {
                        rows.Add(new(left, $"{Clock(t.ElapsedSeconds)} (learning)", Foreground: Dim, Bold: t.IsCharm, SpellTrack: tracking));
                    }

                    break;
            }
        }

        if (s.Timers.Count == 0)
        {
            rows.Add(new("No active timers", Foreground: Dim));
        }

        var activeSpells = s.Timers
            .Select(timer => timer.Spell)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = engine.SpellDurations(highestRanksOnly: true)
            .Where(duration =>
                !activeSpells.Contains(duration.Spell) &&
                (duration.LearnedSeconds is not null || duration.OverrideSeconds is not null || duration.WikiSeconds is not null))
            .OrderBy(duration => duration.Spell, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (known.Count > 0)
        {
            rows.Add(new("Known spells", Foreground: Header, Bold: true, QuestHeading: true));
            foreach (var duration in known)
            {
                rows.Add(new(duration.Spell, duration.Tracked ? "Tracked" : "Not tracked",
                    Foreground: duration.Tracked ? null : Dim,
                    ToolTip: duration.Tracked
                        ? "Stop tracking this spell"
                        : "Track this spell in the Spell Timers overlay",
                    SpellTrack: new SpellTrackChange(duration.Spell, duration.Tracked)));
            }
        }

        return new("Spell Timers", null, rows);
    }

    private static OverlayContent Session(EngineSnapshot s, LogEngine _, OverlayView __)
    {
        var x = s.Session;
        var now = s.Now;
        var rows = new List<OverlayRow>();
        rows.Add(s.CurrentLoadout is { } loadout
            ? new("Classes (/who)", $"{loadout.Classes}  (level {loadout.Level})", Bold: true)
            : new("Classes", "Unknown - use /who", Foreground: Dim));
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

        rows.Add(new("Deaths", x.Deaths == 0 ? "0" : $"{x.Deaths}  (last: {x.LastKilledBy})", Foreground: x.Deaths > 0 ? Warn : null,
            Link: x.Deaths > 0 && !string.IsNullOrWhiteSpace(x.LastKilledBy) ? QuestLookup.SearchUrl(x.LastKilledBy) : null,
            ToolTip: x.Deaths > 0 ? $"Open {x.LastKilledBy} on EQLWiki" : null));
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
            rows.Add(new(k.Victim, k.Time.ToString("HH:mm:ss"), Foreground: Dim, Link: QuestLookup.SearchUrl(k.Victim)));
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
        else
        {
            foreach (var r in l.Recent.Take(12))
            {
                string qty = r.Quantity > 1 ? $"{r.Quantity} x " : "";
                string who = r.Looter == Names.You ? "" : $"[{r.Looter}] ";
                string note = r.SoldFor is { } c ? $"sold {c}" : r.Stored ? "depot" : r.Time.ToString("HH:mm");
                rows.Add(new($"{who}{qty}{r.Item}", note, Foreground: r.Looter == Names.You ? null : Dim, LootItem: r.Item));
            }
        }

        return new("Loot", $"{l.ItemsLooted} items this session", rows);
    }

    private static OverlayContent Chat(EngineSnapshot s, LogEngine engine, OverlayView view)
    {
        var rows = new List<OverlayRow>();
        foreach (var c in view.RecentChat ?? engine.RecentChat(60, view.ChatChannels))
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
