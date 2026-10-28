# EqLog Overlay

An app for Windows that reads EverQuest Legends log files (`eqlog_*.txt`) and shows live stats in
small borderless, always-on-top overlay windows over the game.

Track damage, healing, loot, spell and spawn timers, quest ingredients, chat, and custom alerts.
Use floating overlays over the game or arrange panels in the main window on another monitor.

## Installation

1. Download **EqLogOverlay-win-Setup.exe** from the [latest stable release](https://github.com/AnQueth/eql/releases/latest).
2. Close any running copy of the app, then run the installer.
3. Launch **EverQuest Log Overlay**.

The Windows x64 release includes .NET; you do not need to install a separate runtime.
Installers are currently unsigned, so Windows may display a SmartScreen warning. Only install
files from a source you trust.

For a portable copy, download **EqLogOverlay-win-Portable.zip**, extract the entire archive,
and launch **EverQuest Log Overlay.exe**. Keep the launcher, `Update.exe`, and `current` directory
together; do not copy just the executable.

## First-time setup

1. Turn logging on in EverQuest with **`/log on`**.
2. Use **windowed or borderless** mode; overlays cannot draw over exclusive fullscreen.
3. In **General > Log file**, use **Browse...** if your log is not selected automatically.
4. In **General > Overlays**, enable the panels you want and choose floating or **docked**.
5. Unlock floating overlays to arrange them, then lock them to pass clicks through to the game.

The default log folder is:

```text
C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends\Logs
```

On startup the app reads the selected file's history, then follows new lines live.
With **Follow newest log** checked, it switches to whichever `eqlog_*` file is written to next,
such as when you change characters. Startup history does not fire alert triggers.

If floating overlays are missing while arranging them, temporarily turn off
**Only show floating overlays while EverQuest is active**.

### Legends class loadouts

Use **/who** so your own entry appears in the log, for example
`[50 DRU/WIZ/ENC] Quethrosar (Erudite)`. The app matches your character name exactly
(ignoring case), tracks the listed classes in their logged order and level, and updates them
when another entry for you appears. Other players' entries and anonymous entries do not change
your loadout. The character, detected class loadout, and level appear in the **app title bar**,
as well as the **Session** overlay and log status. The title bar retains the app version and
updates automatically when your own /who entry changes.

Learned normal spell damage is kept separate by **character, class loadout, level, and exact
spell rank**, so different Legends builds do not distort each other's reduced-damage estimates.
Use **/who after changing loadouts**: the app cannot detect an unlogged switch and uses the
last reported loadout until updated. After a login or changing character, classes are unknown
until your own /who entry is read; damage learning waits rather than guessing. Historical
/who entries are read in order on startup, reconstructing the last reported loadout.
Saved damage samples retain their loadout context across restarts. Earlier samples collected
before loadout tracking cannot be assigned to a class build retroactively.

## Appearance and themes

Settings tabs sit at the top of the side panel, with no global settings stacked above them.
**General** contains log, overlay, and chat settings; each tool uses the remaining panel height.
**Appearance** contains theme selection, JSON import, and the theme editor.

The **Appearance** tab edits a draft of your selected palette. Give it a name, enter `#RRGGBB`
colors, and watch the swatches and sample card update. **Apply & save** activates the draft and
persists it across restarts. **Export JSON...** writes an importable theme file without changing
the active theme. **Reset draft** discards edits and reloads the last selected/applied palette;
choosing a different theme also replaces the draft. Invalid colors are reported and block apply/export.

Use **Settings → Appearance → Theme** to switch between **Halloween** (the default), **Midnight Ocean**, and
**Enchanted Forest**, or choose **Import JSON...** for a custom palette. Changes apply immediately
and the complete selected palette is saved in `%AppData%\EqLogOverlay\settings.json`; the original
import file is not needed on subsequent starts. Importing another file with the same name is allowed.
Invalid JSON, missing colors, and invalid color values are reported without replacing your theme.

Use contrasting text and backgrounds. Combat/status colors and custom alert colors are separate
from the selected theme. Export a theme from the editor to share it with another user.

## Automatic updates

**General > Application updates** shows the current version and update status. Startup checks and
automatic downloads are enabled by default and can be disabled independently. **Check for updates**
and **Download update** also work manually. Only stable releases from `AnQueth/eql` are considered;
drafts, prereleases, and downgrades are excluded. Downloads display progress and are verified by
the updater against the release feed. Failures are shown without stopping normal log reading.

Updates **never restart the app automatically**. Choose **Restart to update**, then confirm.
The app saves tracking data, stops the log reader, saves the final state, and exits so the updater
can replace application files and restart. A failed data save blocks the restart.
Settings, triggers, inventory, and learned timers remain in `%AppData%\EqLogOverlay`, outside
the application installation. Development/unpackaged builds and attached debugging sessions are
not updated; existing plain builds must install an updater-enabled release once.

## Overlays

Enable **Sky quest tracker** under **General → Overlays** for a compact Plane of Sky tracker.
It can be docked or floating and has a class selector shared with the Plane of Sky tab.
Only quests with at least one currently owned ingredient appear. Each entry shows the quest,
turn-in NPC, reward, and ingredient owned/required counts. Historical loot and installed augmentations
do not qualify; collection ownership does, with a turn-in availability warning. Inventory and newer
loot update counts automatically. The scrollable list is virtualized. **Refresh** reloads wiki data;
the existing 24-hour Sky cache is shared with the full browser. The overlay is off by default.

Docked overlays live on a **freeform canvas**, not a wrapping grid. Drag the accent-colored header
handle (or noninteractive panel space) to place a panel anywhere, including empty space, without
moving any other panels. A full-size highlighted preview follows the pointer, preserving where
you grabbed the panel. Drop to place it; Escape or dropping outside the dock cancels the move.
Panels may overlap; dragging a panel brings it to the front. Drag near any edge to scroll.
Each panel's canvas position and bottom-right-grip size are saved independently of its floating
window position and size. Resizing a panel moves overlapping neighbors into the nearest clear
space (left, right, above, or below), with a small gap. Unaffected panels stay put where possible;
the resized panel stays anchored, and displaced positions are saved. Shrinking pulls adjacent
panels and connected rows or columns closer to fill the freed space; deliberately separated
panels stay put. Resizing the dock window itself never rearranges panels.
Newly docked panels without saved positions are placed in available space automatically.
Buttons, filters, and scrollbars remain interactive even when floating overlays are locked.

The **Loot** overlay selector offers **Recent drops**, **Grouped: most recent**, and
**Grouped: item name (A-Z)**. Grouped rows show the session quantity and last-looted time,
combine numeric upgrade variants, and keep different looters separate. Counts cover all
observed session drops, not just the recent-drop buffer, and reset with session statistics.
Your selected grouping is saved across restarts. Clicking a grouped item opens its EQLWiki page;
right-click for quest references.

Click a combatant in **DPS** for an ability breakdown of that fight, or click the target row
for all attackers combined. Recent fight rows also open a combined breakdown. In **Healing**,
click a recipient, incoming healer, or **Observed healers** name for session ability totals.
Details show total, DPS/HPS, share, hits, crits, maximum, and healing overheal. They are snapshots
at click time, not live windows; click again for an updated view. Healing detail rates use the
span from first to last observed session heal (minimum one second), not the rolling 30-second
meter. Only logged events are available; unnamed melee is grouped as Melee and unnamed heals
as unknown. Unlock floating overlays with Ctrl+Shift+L to click; docked panels remain interactive.

| Overlay | Shows |
|---|---|
| DPS | Current or last fight: your and others' damage, DPS, and share |
| Damage Taken | Incoming damage by attacker/spell (encounter and session) |
| Heals | Heals you cast (with overheal) and heals you received |
| Spell Timers | Countdowns for mez, charm, DoTs, debuffs and similar effects you cast |
| Session | Session and zone time, kills/hr, deaths, XP/AA progress, level, AA-cap warning |
| Loot | Recently looted items and coin |
| Chat | Selected chat channels |
| Spells | Casts per spell, interrupts, resists and fizzles |
| Alerts | Text from your custom alert triggers (see below) |
| Mob info | Your latest live /consider result, wiki stats, and saved Magic/Fire/Cold/Poison/Disease observations |
| NPC compendium | Search saved NPCs by name/zone; browse wiki stats, per-spell resists, encounter history and average DPS |
| Spawn Timers | Respawn countdowns for the named mobs you track in the current zone (see below) |
| Motes | Motes of Potential looted per tier (session and all time), motes/hr, recent drops, drop rates here (see below) |

- **Lock all overlays** (or **Ctrl+Shift+L**): when locked, overlays are click-through so they don't block the game.
  When unlocked they have a gold border; drag to move and drag the edges to resize.
  A dash in the checkbox means some overlays are locked; clicking it or pressing the hotkey locks all.
  When all are locked, the same control unlocks all. Docked panels remain interactive.
- **Only show floating overlays while EverQuest is active** (on by default): floating overlays hide
  when you Alt+Tab to another app (including the dock/settings), even if the game is still visible
  on another monitor. They return when EverQuest has focus again. An unlocked floating overlay
  can keep focus while you interact with it. A minimized or unavailable game never enables them.
  Docked panels always stay visible. Turn this off to arrange overlays without the game running.
- Expand **General > Overlays > Individual overlay appearance and locks**, then an overlay's name,
  to adjust its text size, background opacity, and floating-window lock independently.
  Size and opacity apply to both floating and docked panels, update live, and are saved with positions.
  Existing settings retain the previous global appearance and lock values until individually changed.
- **Reset session** clears totals. You can also have them reset automatically when you log in.

### Main window: settings + docked panels (second monitor)

The app has one normal window you can put on another monitor, maximize, or keep on top. Settings sit in a
side panel on the left (toggle it with **⚙ Settings**, drag the divider to resize it); docked panels fill the rest.
Closing this window exits the app.

Tick **docked** next to an overlay to show it as a tile in this window instead of as a floating overlay.
Each panel also has its own button: **Dock** on a floating overlay (visible while overlays are unlocked) and
**Pop out** on a docked tile, to move just that one panel.
**Dock all** docks everything except Alerts; **Float all** turns every panel back into an overlay.
Drag a tile anywhere on the canvas and drag its gold bottom-right corner to resize it.
Docked panels use fantasy-style metallic frames with beveled edges, inset content, raised
title strips, and corner studs. Frame colors follow your theme; floating overlays keep their
compact appearance.

Canvas positions and panel sizes, the window's position, and the side panel's width and visibility
are saved.
## Mob info and resistance observations

The **Mob info** overlay is enabled by default. A **new /consider result** in the live log shows
the mob's name, level, faction reaction, difficulty text, and any following creature assessment.
The same window updates for your next target; reading startup history does not open it.

Five color-coded rows show **Magic, Fire, Cold, Poison, and Disease**:

- **Wiki** is an explicit resistance stat from EQLWiki, when available. `?` means unknown, not zero.
  Many NPC pages, including Heart harpie and A Spinechiller Spider, do not list resistance stats.
  Wiki race, class, level, **HP and AC**, damage per hit, attacks per round, attack speed,
  HP/mana regeneration, aggro radius, run speed, respawn time, and location are also shown.
  These are reference stats, not live remaining health; estimates and ranges stay as written on
  the wiki. Missing values show `?`. The cache date is displayed, and older caches automatically
  refresh on the next consider to include the additional stats.
  **Special (wiki)** highlights listed traits such as Cyndreela's **Immune to Flee, See Invis**.
  These traits are separate from elemental resistance values. Use **Refresh wiki** to update
  an older cached page that does not yet include special traits.
- **Observed result** shows a plain label followed by the percentage and resisted/assessed counts,
  for example **Partial — 65% (13/20)**. Estimated reduced-damage resists are included in the
  resisted count; hits still awaiting a baseline and other-spell resists are not in this denominator.
  With no assessed outcomes it shows **Unknown — no assessed outcomes**.
  **Resists** (all recorded outcomes resisted), **Partial** (more than half but not all),
  **Partially Resistant** (10% through 50%), and **Not Resistant** (less than 10%).
  **Unknown** means no comparable observations, not zero resistance.
  Labels cover your spells whose wiki metadata identifies
  single-target instant direct damage, including instant **Lifetap** spells such as Life Leech.
  Ranked spells fall back to their base wiki page if needed.
  A successful outcome must match a recent, uninterrupted personal cast.
  Repeated damage lines for that cast and target do not inflate the count.
  Reduced-damage hits now count as **estimated partial resists** if they deal less than **90%**
  of learned normal damage. Normal damage is learned separately for each character, reported
  class loadout, level, and exact spell rank from non-critical, unmodified hits. At least three hits within 90% of the highest
  observed damage are needed before using that baseline. Until then, damage observations stay
  unassessed rather than being assumed full hits; saved samples are re-evaluated as learning improves.
  Each estimated partial resist counts as one resisted outcome for the labels above.
  Buff changes, mitigation, and an entirely partially resisted training sample can skew this
  estimate, so it is not proof of a resist.
  Damage observations wait briefly for death messages. The latest candidate hit on that mob is
  excluded from both learning and resistance outcomes when a death is logged within **3 seconds**,
  even if another player gets the kill. Without a death message (or with one delayed longer than
  this window), finishing blows cannot be identified reliably. Very recent buffered hits may be
  omitted if the app closes before they can be checked.
  Previously saved landed counts contain no damage amounts and cannot be reclassified retroactively.
- Resists of DoTs, debuffs, area spells, and other spells appear as separate counts with an **unknown rate**.
  DoT ticks and guessed landings are not counted as successful casts. Spells with missing or
  unrecognized wiki resistance types remain unclassified rather than being assumed Magic.

Observed labels are **not exact NPC resistance values or guaranteed immunity**. Your level, debuffs, spell modifiers,
missed log messages, and small sample sizes affect them. Same-name mobs in the same zone share
observations; logs do not identify individual NPC instances. Different logged zones (including
difficulty/instance suffixes) stay separate. Wiki values may not reflect a custom rank or zone variant.
Observations accumulate across characters and sessions; **Reset session** does not clear them.
Reopening the same log does not count its recorded history again. Moving a log to a different
path can reimport its observations.

Under **General > Consider / mob information**, set the display timeout (default **5 seconds**;
`0` keeps it until your next consider). Mob info supports the usual docking, independent text
size, opacity, and lock controls. Timed expiry hides it in both docked and floating modes.
The normal EverQuest visibility rule still applies to floating windows.

Unlock the panel to use **Wiki** or **Refresh wiki**; docked buttons are always interactive.
NPC lookups are cached for one day and spell metadata for seven days. Only mob and spell names
are sent to EQLWiki, never the log file. Lookup failures keep cached data and show an error;
use **Refresh wiki** to retry. New observations are still saved while offline.

Spell metadata is also fetched **automatically in the background as spells appear in the log**:
casts, named spell damage/DoT ticks, heals, targeted resists, and wear-off messages. This works
during startup history import and live reading, even with both NPC overlays hidden; no consider
or NPC selection is required. Repeated names share one lookup, fresh saved entries are reused,
and requests run one at a time with at least one second between requests. Results are saved
as they arrive and reused across character/log switches. Failed automatic lookups preserve
cached data and report an error under General; another observation can retry after five minutes,
or **Refresh wiki** retries explicitly. Closing the app cancels unfinished work; replaying the
log rediscovers any spells that still need metadata.

All automatic wiki lookups share a single paced queue. **Live /consider information goes
first**, followed by NPC/item selections and manual refreshes, then background spell,
quest, and Sky lookups. Selecting something already queued promotes that lookup instead of
downloading it twice. A download already in progress finishes before the next request;
long quest searches yield between pages. Occasional turns for older background work keep
it progressing without letting a backlog block live considers.

Temporary connection failures, timeouts, and busy-server responses retry automatically
up to three times, with increasing delays and respect for the server's requested wait.
Waiting retries do not hold up other wiki work. If retries are exhausted, the existing
cached data stays available and the lookup error is shown.

## NPC compendium

Enable **NPC compendium** under **General > Overlays**. It starts docked and is off by default;
it can also float over the game with its own size, opacity, text size, and lock settings.
Unlike **Mob info**, it stays open until you hide it: searching never changes or reopens the
timed /consider panel. Unlock a floating compendium to type and interact; docking keeps it interactive.
The search text, selected NPC/zone, and selected view are remembered. Use **Overview** for combat
totals, NPC stats, and resistance-school summaries; **Spell resists** for individual spell cards;
and **Encounters** for paged fight history. Cards wrap on narrow panels instead of squeezing
spell names against long status text.

- Type part of a name to filter recorded NPCs, then choose the **name and zone** in the list.
  Full logged zones, including difficulty/instance suffixes, remain separate.
- **Look up name** opens the exact typed NPC even without local history. Wiki stats, HP, AC,
  special traits, resistance labels/numbers, and cached spell metadata are shared with Mob info.
  **Open wiki** opens the page; **Refresh wiki** retries or refreshes cached data. Missing values stay unknown.
- Historical considers retain the faction reaction, level, difficulty, and supplemental assessment.
  They are identified as historical, not your currently selected game target.
- The selected log's history is imported automatically, followed by new live interactions.
  Use **Browse...** to import another log; previous NPC history remains saved.
- Encounter summaries include the date, character, reported loadout/level, duration,
  your damage/DPS, other attackers' damage, incoming damage, and a logged death or end reason.
  Use **Older** / **Newer** in the **Encounters** view to page through history.
- **Your average DPS** is total damage divided by summed durations of encounters where you
  dealt damage, not a simple average of fight DPS values. Durations use last activity minus
  start plus one second, minimum one second. Other-attacker damage/DPS is separate and includes
  all observed attackers, not guaranteed group members. These are log-observed damage rates,
  not estimates of NPC health or remaining HP.
- **Per-spell resistance history** separates full resists, estimated reduced-damage resists,
  full/near-full landings, and hits awaiting a reliable baseline. It uses the same saved
  observations and finishing-hit exclusions as Mob info. Encounter spell rows separately show
  damage, confirmed direct landings (including partial/finishing hits), and explicit resists;
  DoT ticks contribute damage, not additional direct landings.
- A known **school** does not mean a reliable **damage rating** is available. Denon's Desperate
  Dirge and Frost Storm use **Targeted AE** on the wiki; their cards show Magic/Cold, saved
  landings/resists, and the AE limitation instead of incorrectly implying their schools are unknown.
  Multi-target/wave spells and DoTs do not get a fabricated full/partial rating.
  Missing wiki details and insufficient normal-damage samples have separate explanations.
  Older cached spell metadata refreshes automatically when observed or selected after an app upgrade;
  saved observations are preserved. Earlier landings lack damage amounts and cannot be
  checked retrospectively for reduced damage.

Summaries end on a logged death, zone/login/loadout/level change, or more than 15 seconds
without interaction. Missing deaths show **Timed out**, not an assumed kill. Active summaries
are saved too. Reopening or reimporting the **same log path** does not duplicate its encounters;
moving/copying a log to a different path counts as a different source. Overlapping same-name
NPCs cannot be distinguished: they merge until a death/end boundary, and subsequent interaction
starts a new summary. Unknown/unlogged class changes still require **/who**.
No raw log lines are stored in the compendium. Saved history survives **Reset session** and restart.
Corrupt history is reported and protected from overwrites; new observations then stay session-only.

## Loot quest lookup

### Plane of Sky class quests

Open **Settings > Plane of Sky** to browse the wiki's class tests. Choose a class or **All classes**;
each card shows the reward, quest giver, trigger phrase, required rune, and quest items with drop-source
notes. Click a quest title to open its class table on EQLWiki (the source table does not provide separate
quest-page links). The class filter is saved. The catalog is cached for one day in `sky-quests.json`;
opening the tab after it expires fetches updates, and **Refresh** forces an update at any time.
Network/format errors appear above the cards, keeping previously loaded data available.
Each card tracks the active character's owned/missing quantities for the rune and required items,
using inventory exports, newer loot, and manual owned-count corrections. Upgrade suffixes are ignored
when matching. Export carried/equipped and bank counts are shown separately; unconfirmed historical
loot does not satisfy requirements. Progress updates while the tab is open. Items may count toward
multiple quests (they are not reserved), and all listed items owned does not verify faction or other
prerequisites. Refresh the inventory export after turn-ins to update counts.
Sky's owned totals also include collection entries: an item in your Equipment collection is shown as
**Have**, not **Missing**, with the collection count and an explicit turn-in-availability warning.
Installed augmentations and unconfirmed historical loot do not satisfy requirements. The editable
Owned column in Loot quests still represents non-collection counts; Sky adds the separate Collection
column when evaluating ownership.

### Inventory export monitoring

The app checks for `<Character>_<server>-Inventory.txt` in the **parent of the Logs folder** every two
seconds. For `Logs\eqlog_Quethrosar_qeynos.txt`, it reads `Quethrosar_qeynos-Inventory.txt` beside
the Logs folder. Generate/update the export with **`/outputfile inventory`** in game.
The inventory status in **Settings > Loot quests** shows the expected path, last import, and errors.
No inventory file is uploaded.

After the file stops changing, a successful import replaces the character's owned counts, including
carried/equipped and bank/shared-bank items. All nonempty entries are imported, including the
three-column KeyRing section (Equipment, Activated, Augmentation, and Equipment-* entries).
Collection and installed-augmentation counts are displayed separately and included in wiki lookup
processing, but do not count as loose quest turn-in items. Each collection row represents one entry.
Empty slots are ignored. Item names are preserved exactly. The status shows imported entry totals.
Loot newer than the export time is added without double-counting loot already in the export.
Refresh the export after selling, banking, or turning items in; the game does not continuously update
this file. Manual owned-count edits last until the next export. Missing/invalid/locked exports keep
previous counts and are retried automatically. Quest references are still not verified requirements.

The **Quest opportunities** panel automatically looks up looted items and groups their quest references.
Use its sort selector for **Recent items** (latest recorded loot first, inventory-only items last) or
**Quest name (A-Z)**. The search box filters by item or quest name, ignoring case; matching quests retain
all their linked items for context. Clear the box to show everything. Sort and filter persist across restarts.
These controls work docked or in an unlocked floating panel.
It docks by default and can also float. Historical loot is imported as **unconfirmed**, not owned.
In **Settings > Loot quests**, edit **Owned** to confirm your inventory. Newly observed loot increases
owned counts; correct them manually after sales, banking, depot transfers or turn-ins. Auto-sold and
depot loot are excluded. Select a quest reference to mark it complete (or reopen it); completion hides it
from the opportunities panel without assuming which items were consumed. Counts and completions are
stored per character in `quest-inventory.json`. Historical lines are deduplicated per log path and line
number; replacing or moving a log can import it again as unconfirmed history.

Automatic lookups run one at a time, cache results for seven days, and show errors explicitly.
The Quest opportunities toolbar puts sorting and filtering side by side, with a horizontal progress
bar, checked/total and cached/queued/failed counts, and the current lookup text underneath. Failed
attempts count as processed, not cached; use Retry lookups to retry them.
Quest titles use accent-colored section headings. Clickable items sit beneath them in indented,
translucent cards with bold item names and separate muted inventory details.
Use the vertical scrollbar to browse quest opportunities without moving the panel.
Use **Retry lookups** after a network failure. Only item names are sent to EQLWiki.

For a manual lookup, open **Settings > Loot quests** and select an inventory grid row to load its
quest references automatically. **Look up quests** repeats the selected row's lookup, and **Item wiki page**
opens that same item's page. Select a result and click **Open selected quest**.
References may describe requirements, rewards, or other mentions; check the linked quest before selling
an item. No results means no linked quest pages were found, not that the item has no quest use.
“Wiki references checked” means the lookup succeeded. This list shows wiki mentions, not a
ready-to-turn-in checklist; hover the explanation beneath the progress text for details.

**Click item names to open their EQLWiki pages** in Loot, Quest opportunities, inventory,
Sky rewards/runes/requirements, and Motes (including tier summaries and recent drops).
Numeric upgrades are removed from wiki page names; Sky's displayed aliases retain their actual wiki targets.
In an overlay, **right-click > Look up quest references** opens the Loot quests tab, selects its
inventory row, scrolls to it, and starts its lookup. Matching ignores numeric upgrades.
Items not yet in the inventory grid are added as zero-owned lookup candidates; looking up someone else's
loot never adds ownership. Quickly changing selection cannot replace the latest item's results with an older response.
This works in the docked panel and in unlocked floating overlays. Press **Ctrl+Shift+L** to unlock a
floating overlay first; locked overlays pass clicks through to the game.

Only the item name is sent, never log files or character information.
Results are cached for seven days in `%AppData%\EqLogOverlay\quest-lookups.json`.
All item identity comparisons ignore upgrade modifiers such as `+1`, `+ 1`, or `+10`, wherever they
appear in a name, using the same normalization for loot totals, inventory grouping, item searches,
Sky requirements, wiki lookups, item-page links, and cache keys. Comparisons also ignore case and
extra whitespace. Displays retain original item names; quantities from upgrade variants are combined.
Network/API/cache errors appear in the tab rather than being treated as an item with no quests.

## Alert triggers

The **Alert triggers** tab lets you show a message in the Alerts overlay when a log line matches a regex.
Triggers fire only for new lines written while the app is running, never for history it reads at startup.

| Column | Meaning |
|---|---|
| On | Enable or disable the trigger |
| Regex | Matched case-insensitively against the text after `[timestamp] `. `{C}` is your character's name |
| Alert text | Text to show. `$1` or `${name}` insert captured groups, `{C}` your name. Leave it empty to show the log line |
| Secs | How long the alert stays on screen |
| Sound | Play the Windows exclamation sound |
| Color | A color name (`Orange`) or `#RRGGBB` |

Built-in examples:
- `^{C} has fallen to the ground\.$` → **FEIGN DEATH FAILED!**
- `^You are no longer feigning death, because (.+)\.$` → **Feign broken: $1**

Use **Test** to try a line against your triggers. Invalid regexes are listed under the grid and skipped.
Testing automatically enables and reveals the Alerts panel. Floating test alerts remain visible even
without EverQuest on screen until the test duration expires, then the usual game-visibility rule resumes.
Docked Alerts are brought forward and scrolled into view.
To remove triggers, select one or more rows and click **Delete selected** or press **Delete**.
Both methods ask for confirmation, defaulting to **No**. Confirmed deletions are saved.

### Log lines

The **Log lines** tab shows the raw log as it is read (the last 5,000 lines) so you can find lines to build triggers from.

- **Filter** shows only lines containing the text. Tick **Regex** to filter with trigger syntax (including `{C}`), which is a quick way to check a pattern.
- The list follows new lines while it is scrolled to the bottom with nothing selected. Scroll up, select a line or tick **Pause** to hold it still.
- Double-click a line (or select it and press **Make trigger**) to add a trigger from it. Your name becomes `{C}`, numbers become `\d+`, and the regex is anchored with `^...$`. The app then switches to the Alert triggers tab with the line in the Test box, ready for you to type the alert text.

## Spawn timers

The app learns how long named mobs take to respawn. When you kill a mob and later see the same name
active again (it hits, casts, says something, or takes damage) in the same zone without zoning out,
that gap is a sample. A sighting can come well after the actual respawn, so the estimate is the lowest
consistent cluster of samples. Instances share what is learned (Nektropos Castle 4 (Refined) counts
as Nektropos Castle), but zoning or logging in drops kills that haven't been timed yet, because an
instance may have reset. Mobs that keep the same name across several copies (activity right after the
kill) aren't timed.

The **Spawn timers** tab lists the likely named mobs for the zone you're in, from this log and from
earlier sessions. Tick **Show all mobs** to include "a skeleton" style mobs and anything that looked like
a player or mercenary.

- **Track** adds the mob to the Spawn Timers overlay for that zone.
- **Override** sets the respawn yourself as seconds, m:ss or h:mm:ss. Clear it to use the learned value or wiki fallback.
- **Wiki** shows a fallback respawn time from the mob's EQLWiki page. Tracked mobs in your
  current zone are checked in the background when no learned time or override exists, even
  when the settings tab is closed. Existing one-day NPC wiki caches are reused across restarts.
  Only explicit durations are used (for example `6min 40sec` or `16:00`); missing values,
  ranges and conditional descriptions remain unknown. Wiki values may differ on Legends or
  instance variants. Lookup errors appear under General and retry after five minutes.

Timer priority is **Override > Learned > Wiki**. Wiki countdowns are marked **(wiki)** and
never count as learned samples. Once local learning supplies a time, it takes precedence.

The overlay shows each tracked mob as a countdown after you kill it, then **DUE** once the time has
passed, **up** once it's been seen alive again, and the learned time when it hasn't been killed yet.

## Motes

The **Motes** overlay counts the Motes of Potential you loot, from corpses and Reward Chests:

- One row per tier, taken from the item name ("Mote of <Tier> Potential", so new tiers appear automatically), most collected first, showing this
  session's count and, in brackets, the all-time count. The subtitle shows the session total and motes per hour.
- **Recent**: the last few drops with tier, quantity, source and time.
- **Drops here**: mobs in the current zone that have dropped motes, as drops / kills and a percentage. Kills by
  anyone in view count, and instance versions of a zone are combined. Reward chests are left out of the rates.

All-time totals and drop rates are saved per character in motes.json. The app remembers the last line it
counted, so reading the same log again on the next launch doesn't count drops twice.

## Spell durations

Spell timers for a mob are removed when its death appears in the log, including kills by
other players. Timers on other targets are kept. Unconfirmed casts do not recreate timers for
the dead target; deaths outside your log's visibility cannot be detected.

The log doesn't say how long a spell lasts, so the app learns durations from the gap between an
effect landing and its "worn off" message. Mez and charm often break early, so the estimate is the
80th percentile of the last 25 samples. Re-reading the same history doesn't count the same wear-off twice.

The **Spell durations** grid shows learned values. Type a number of seconds in *Override* to fix a
duration; clear it to go back to the learned value. Until a spell has a duration, its timer counts up
and is marked "(learning)".

## Saved data and backups

Settings are stored in `%AppData%\EqLogOverlay\`:

- `settings.json`: log path, overlay positions/visibility/docking, docked window layout, opacity, font, chat channels, alert triggers
- `spell-durations.json`: learned samples and overrides
- `spawn-timers.json`: respawn samples, overrides, tracked mobs and the last kill/sighting per zone, so countdowns keep running across restarts and new log files
- `motes.json`: all-time mote totals per character and mote drops/kills per zone and mob
- `mob-resistances.json`: resistance observations by zone/mob, replay watermarks, and cached EQLWiki NPC/spell data
- `npc-compendium.json`: NPC name/zone catalog, historical considers, active/completed encounter summaries, per-spell damage/outcome counts, and import watermarks (no raw log lines)
- `quest-inventory.json`: quest inventory counts and completions per character
- `quest-lookups.json`: cached item quest references
- `sky-quests.json`: cached Plane of Sky quest catalog

Tracking data is saved periodically, on exit, and on Windows logoff/shutdown.
To back up your data, close the app and copy the entire `%AppData%\EqLogOverlay` folder.
To restore a backup, close the app before replacing the folder's contents.
Application updates do not remove this folder.

## Developer documentation

- [Building, testing, and coding conventions](docs/DEVELOPMENT.md)
- [Architecture and implementation details](docs/ARCHITECTURE.md)
- [Packaging, signing, and publishing releases](docs/RELEASING.md)
