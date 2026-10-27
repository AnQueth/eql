# EqLog Overlay

A .NET 10 WPF app that reads EverQuest Legends log files (`eqlog_*.txt`) and shows live stats in
small borderless, always-on-top overlay windows over the game.

The settings, quest browser, and docked panels use a dark purple Halloween palette with pumpkin-orange
accents, dark input controls, and readable alternating inventory rows. Plane of Sky cards separate
each requirement's owned/needed count from its inventory locations and turn-in caveats.

### JSON themes

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

Copy one of the bundled files in `Themes` next to the executable (or
`src\EqLog.Overlay\Themes\Halloween.json` in the source) to create a theme. Keep its structure:
`Name` is a nonempty string up to 80 characters, and `Colors` supplies every listed color as
`#RRGGBB`. Keys are case-sensitive; unknown color names are rejected.
`NightBackground`, `NightSurface`, and `NightRaised` control the dark layers; `NightBorder`,
`NightText`, and `NightMuted` control outlines and text. `Pumpkin` is the accent color (not necessarily
orange), and `AccentText` is text on accent-filled controls. `Hover`, `Pressed`, `Selection`,
`Alternate`, `GridLine`, and `ScrollThumb` complete the interaction/table palette.
Use contrasting text and backgrounds. Combat/status colors and user-configured alert colors
retain their semantic meaning independently of the theme.

## Running

```powershell
dotnet run --project src\EqLog.Overlay                 # newest eqlog_* in the default Logs folder
dotnet run --project src\EqLog.Overlay -- "C:\path\to\eqlog_Name_server.txt"
```

Default log folder: `C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends\Logs`.
Logging must be on in game (`/log on`). Run the game **windowed or borderless**; overlays can't
draw over exclusive fullscreen.

On start the app reads the whole file for history, then follows new lines live. With
**Follow newest log** checked, it switches to whichever `eqlog_*` file is written to next (for
example after you change character).

## Overlays

Docked overlays have an accent-colored drag handle at the left of their header. Drag it (or
noninteractive panel space) onto the left/right half of another tile to place it before/after
that tile, or drop in empty dock space to move it to the end. The dock reflows tiles and saves
their order across starts. Buttons, filters, and the bottom-right resize grip remain interactive;
docked reordering works even when floating overlays are locked.
During dragging, a full-size highlighted placeholder previews the drop position and surrounding
panels reflow immediately. Drop to commit the previewed order, or press Escape to restore the
original layout. Drag near the top/bottom of the dock to scroll while positioning a panel.

The **Loot** overlay selector offers **Recent drops**, **Grouped: most recent**, and
**Grouped: item name (A-Z)**. Grouped rows show the session quantity and last-looted time,
combine numeric upgrade variants, and keep different looters separate. Counts cover all
observed session drops, not just the recent-drop buffer, and reset with session statistics.
Your selected grouping is saved across restarts. Clicking a grouped item still opens quest lookup.

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
| Spawn Timers | Respawn countdowns for the named mobs you track in the current zone (see below) |
| Motes | Motes of Potential looted per tier (session and all time), motes/hr, recent drops, drop rates here (see below) |

- **Lock overlays** (or **Ctrl+Shift+L**): when locked, overlays are click-through so they don't block the game.
  When unlocked they have a gold border; drag to move and drag the edges to resize.
- **Only show overlays while EverQuest is visible** (on by default): floating overlays hide while `eqgame.exe`
  isn't running, is minimized, or is mostly covered by another app's window, and come back when the game does.
  Docked panels always stay visible. Turn this off to arrange overlays without the game running.
- Opacity and font size sliders apply to every overlay. Positions are saved on exit.
- **Reset session** clears totals. You can also have them reset automatically when you log in.

### Main window: settings + docked panels (second monitor)

The app has one normal window you can put on another monitor, maximize, or keep on top. Settings sit in a
side panel on the left (toggle it with **⚙ Settings**, drag the divider to resize it); docked panels fill the rest.
Closing this window exits the app.

Tick **docked** next to an overlay to show it as a tile in this window instead of as a floating overlay.
Each panel also has its own button: **Dock** on a floating overlay (visible while overlays are unlocked) and
**Pop out** on a docked tile, to move just that one panel.
**Dock all** docks everything except Alerts; **Float all** turns every panel back into an overlay.
Drag a tile to reorder it and drag its gold bottom-right corner to resize it.
Tiles wrap to fit the window width. Tile order and sizes, the window's position and the side panel's width and
visibility are saved.
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

Automatic lookups run one at a time with a delay, cache results for seven days, and show errors explicitly.
Wiki response processing and cache serialization/writes run off the UI thread. Historical loot is
applied in short UI batches, and unchanged overlay rows are retained when lookup progress changes.
The Quest opportunities toolbar puts sorting and filtering side by side, with a horizontal progress
bar, checked/total and cached/queued/failed counts, and the current lookup text underneath. Failed
attempts count as processed, not cached; use Retry lookups to retry them.
Quest titles use accent-colored section headings. Clickable items sit beneath them in indented,
translucent cards with bold item names and separate muted inventory details.
Use **Retry lookups** after a network failure. Only item names are sent to EQLWiki.

For a manual lookup, open **Settings > Loot quests**, select a recently looted item (or type its exact name), then click
**Look up quests**. This uses EQLWiki's public MediaWiki API to find quest-category pages that link to
or transclude the item's page, including redirects. Select a result and click **Open selected quest**.
References may describe requirements, rewards, or other mentions; check the linked quest before selling
an item. No results means no linked quest pages were found, not that the item has no quest use.

You can also **click an item directly in the Loot panel** to open the Loot quests tab and start its lookup.
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
- **Override** sets the respawn yourself as seconds, m:ss or h:mm:ss. Clear it to use the learned value.

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

The log doesn't say how long a spell lasts, so the app learns durations from the gap between an
effect landing and its "worn off" message. Mez and charm often break early, so the estimate is the
80th percentile of the last 25 samples. Re-reading the same history doesn't count the same wear-off twice.

The **Spell durations** grid shows learned values. Type a number of seconds in *Override* to fix a
duration; clear it to go back to the learned value. Until a spell has a duration, its timer counts up
and is marked "(learning)".

## Files

Settings are stored in `%AppData%\EqLogOverlay\`:

- `settings.json`: log path, overlay positions/visibility/docking, docked window layout, opacity, font, chat channels, alert triggers
- `spell-durations.json`: learned samples and overrides
- `spawn-timers.json`: respawn samples, overrides, tracked mobs and the last kill/sighting per zone, so countdowns keep running across restarts and new log files
- `motes.json`: all-time mote totals per character and mote drops/kills per zone and mob

Everything is saved every ~10 seconds, on exit and on Windows logoff/shutdown. Writes go to a temp file first, so a crash can't leave a half-written file.

## Projects

- `src\EqLog.Core`: log parsing, file tailing, trackers, and `LogEngine`
- `src\EqLog.Overlay`: WPF overlay app
- `tests\EqLog.Core.Tests`: xUnit tests (`dotnet test`)
