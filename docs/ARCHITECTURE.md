# Architecture and implementation notes

## Projects

- `src\EqLog.Core`: log parsing, file tailing, trackers, and `LogEngine`.
- `src\EqLog.Overlay`: .NET 10 Windows WPF application.
- `tests\EqLog.Core.Tests`: xUnit regression tests.
- `tests\EqLog.UpdateSmoke`: console validation of release discovery, download, staging,
  and version policy using an isolated Velopack locator.

## Windows and presentation

`MainWindow` is a hidden controller. Its settings UI is hosted by the visible `DockWindow`.
An `OverlayPanel` is reparented between a floating `OverlayWindow` and the dock canvas.
Floating and docked bounds are stored separately. Dock resize collision resolution preserves
the resized panel's anchor and saves displaced neighbors' positions.

Docked `OverlayPanel` visuals have layered metallic bevels, inset content, a raised title
strip and non-hit-testable corner studs. Gradients derive from the existing theme palette
and refresh with appearance changes. No bitmap/shader effects are applied to text or scrolling
content. Frame decoration collapses when a panel floats; docking and content behavior are unchanged.

`GameWindowWatcher` gates floating overlays on foreground ownership, not window-coverage
estimates. With the default visibility option enabled, a visible, non-minimized, non-cloaked
`eqgame.exe` window must have focus. Focus in a visible `OverlayWindow` is also allowed for
NPC search/interaction; the dock/settings and unrelated applications are not exempt.
The existing 500 ms timer reapplies visibility after focus changes. Docked panels and
explicit alert-test previews retain their existing behavior.

Quest opportunities and Sky overlays use recycling virtualized lists. Other overlays update
changed rows incrementally. This avoids rebuilding all row visuals on progress-only updates.

## Quest processing and inventory

Quest references come from EQLWiki's public MediaWiki API: quest-category pages that link to
or transclude the item's page, including redirects. References are not verified turn-in requirements.
Automatic lookups use the shared wiki repository's background priority and are cached for seven days. Wiki response processing
and cache serialization/writes run off the UI thread; historical loot is applied in short UI batches.
Selection versioning prevents older lookup responses from replacing the latest selected item's results.
The Sky catalog has a separate one-day cache used by the Sky quest tracker overlay (the only Sky UI).

Structured item displays open normalized, escaped EQLWiki item URLs directly in the browser.
`OverlayRow.LootItem` carries the raw item target separately from formatted labels; clicks
raise `ItemWikiRequested`, while the context menu preserves `LootLookupRequested` for
quest-reference navigation. This covers Loot, Quest opportunities, Sky rewards/ingredients
and Mote tier/recent rows. Inventory cells use WPF hyperlinks. Rows with `OverlayRow.WatchKey`
render a star toggle that raises `WatchToggleRequested`; the Sky tracker uses it for its watch list.
Filtering (`SkyQuests.Matches`), dropper grouping (`SkyQuests.ByDropper`) and batched progress
(`SkyQuests.ProgressAll`) live in Core; the overlay rebuilds its rows at most once per second.
Hyperlinks are excluded from overlay drag gestures; floating click-through locks are unchanged.
Sky caches retain per-entry labels and actual item-page targets, preserving aliases, quantities
and drop-source notes without including them in URLs. Older catalogs refresh to fill these
optional fields; failed refreshes retain previously loaded content.

Historical loot is deduplicated by log path and line number. Moving or replacing a log may
reimport history as unconfirmed loot. New loot after the inventory export time is added without
double-counting loot represented in the export.

Item comparisons remove numeric upgrade modifiers wherever they occur, collapse whitespace,
and ignore case. Displays retain original names. The same normalization is used by loot totals,
inventory grouping, Sky requirements, searches, wiki links, and cache keys.

## Consider and mob resistance processing

`WhoLoadoutEvent` is emitted only for an exact case-insensitive match to the logging character's
name in a structured `/who` entry. Class abbreviations and their order are retained.
The engine exposes the latest report as `CurrentLoadout` in its snapshot, clears it on login
or character change, and updates its level on a logged level change. Session resets preserve it.
The Session overlay and log status display this context. Damage learning captures the context
at cast start (character, classes, level) and stores samples under that composite key. An unknown
loadout disables damage learning; it does not prevent explicit resist counts. Changed reports
invalidate pending cast attribution without reassigning already buffered outcomes. Historical
reports reconstruct context during replay; no unlogged loadout change is inferred.

`LogEventParser` emits `ConsiderEvent` (mob, level, faction, difficulty, rare marker) and
`ConsiderAssessmentEvent` for the supplemental creature-assessment line. `LogEngine` exposes
these through events; the controller queues only events received after the initial tail catch-up.
The dispatcher drains the queue and owns the latest selection and its wall-clock expiry.
`ApplyMode` and rendering both gate `mobinfo` on this deadline, including docked panels.
The same overlay instance is reused, and switching logs cancels lookups and clears the selection.

`MobResistanceTracker` records explicit personal targeted resists and confirmed direct damage
matching the current uninterrupted cast within 20 seconds, once per cast/target name.
It does not infer a target from /consider, count DoT ticks, or guess landing messages.
Outcomes are buffered for three seconds so a subsequent kill can remove the latest candidate
damage hit on that target before it enters the baseline or denominator. This works across input
batches and with kills credited to others. Single-word death lines omitted by the general parser
are checked against buffered targets without changing global kill parsing. Parsed and unparsed
lines advance the buffer; snapshots advance it during live idle periods. Pending final samples
are conservatively omitted if the process exits before they can be checked.
`MobResistanceStore` aggregates by normalized zone/name and exact ranked spell name, retaining
the full logged zone's difficulty/instance suffix instead of merging potentially different variants.
Per-log timestamp/line watermarks prevent replay inflation while accepting newer timestamps
after a log is truncated. A moved log is a different source. Same-name NPCs are necessarily combined.

`MobWikiLookup` uses the public EQLWiki API with redirects and title-case variants. Ranked spell
pages may fall back to their base spell page for resistance school and instant-damage metadata,
not inferred from names or damage colors. Only explicit NPC template resistance fields become
wiki stats. Missing values and unresistable/unknown spell schools are not assigned to Magic.
The rate denominator includes only wiki-confirmed single-target instant-damage spell outcomes
(target types `Single` or `Lifetap`, instant duration, and a decrease-hitpoints effect); other
classified resists remain separate counts without a fabricated success denominator.
Spell metadata retains target type, duration and a parser version. Old cache entries refresh on
observation or selection even inside the seven-day TTL so corrected lifetap classification applies to existing
observations without replaying or rewriting their counts. Missing details, area-effect/wave
spells, and duration spells receive distinct assessment explanations; known school is never
presented as unknown merely because damage assessment is unsupported.
Unmodified non-critical damage histograms are persisted by character/exact ranked spell inside
each mob's outcomes. The upper observed damage becomes a baseline only after three near-peak
samples (at least 90% of maximum). Histograms are re-evaluated against this baseline: below 90%
is one estimated partial-resist outcome, otherwise landed. Without enough baseline evidence,
damage samples remain unassessed and excluded from label thresholds. Baselines are cached and
invalidated when matching character/spell samples change. This is an estimate, not proof:
buffs, mitigation and all-partial training samples may bias it. Legacy landed counts lack damage
history and retain their prior classification.

Automatic enrichment is debounced and cancellable when the selection changes. Network parsing
and enrichment saves run off the dispatcher; cancelled requests cannot replace the new selection's
status or reopen an expired panel. NPC/spell caches expire after one/seven days. Errors are visible
and preserve cached results. The thread-safe store serializes periodic, enrichment and final saves
through its gate and uses atomic replacement. Invalid saved data disables writes rather than
silently replacing the original file.

`LogEngine.SpellObserved` emits named spells from casts, direct/DoT damage, heals, resists and
wear-offs during both history import and live tailing. Its callback only enqueues work in the
app-scoped `WikiRepository`; no network or disk operation runs inside log processing.
Every unique pending spell is submitted immediately, rather than hidden behind a separate
spell FIFO. Fresh disk/memory metadata (including missing-page results) is reused. Successful
results use the existing thread-safe atomic store save. Expected failures are reported through
the dispatcher and automatic retries for that name are suppressed for five minutes; explicit
refresh bypasses that suppression. Log switches do not cancel the shared queue.

### Wiki repository and scheduling

`WikiRepository` owns NPC, spell, item-quest and Sky lookup operations and cache policy.
`MobWikiLookup`, `QuestLookup` and `SkyQuests.FetchAsync` remain parsing/API adapters, but
production callers access them only through the repository. Browser links and app-update
HTTP access are unrelated and do not enter this queue.

All programmatic EQLWiki HTTP steps use one `WikiRequestScheduler` priority queue:

- `RealTime` (0): live considers and their spell metadata.
- `Interactive` (100): NPC/item selection and manual refreshes (including Sky **Refresh**).
- `Background` (200): discovered spells, automatic quest enrichment and automatic Sky loads.

Numeric gaps permit adding priorities without changing the scheduler. Ties use HTTP enqueue
order. Matching normalized operations share a result; a higher-priority caller promotes
the shared context, including subsequent pagination/rank-fallback steps. Force refresh
bypasses cached results but shares a pending download. The scheduler reevaluates priority
after pacing, not before. An active HTTP request is not preempted; quest pagination and
spell-rank fallback resubmit each HTTP step independently.

There is a one-second minimum gap after each HTTP attempt completes, shared across every
lookup type. After eight regular attempts, one lower-priority request waiting at least
30 seconds may run ahead of urgent work. Only one aged request gets that turn, so an old
backlog cannot monopolize the real-time lane. Individual cancellation detaches that caller;
the shared operation is canceled only when its last caller leaves, or on shutdown.

`Polly.Core` provides three retries (four total attempts) for connection errors, HTTP-client
timeouts, HTTP 408/429 and 5xx responses. Other 4xx responses and caller cancellation are
not retried. Backoff starts at one second, is exponential with jitter, and is capped at
30 seconds unless the server supplies `Retry-After` (delta or date), which is honored.
Retried responses are disposed. Backoff runs outside the HTTP lane and every attempt
reenters the shared priority queue with the operation's current priority, preserving global
pacing and allowing live considers to proceed while background requests wait. Exhausted
failures follow the existing visible-error and cache-preservation paths. MediaWiki errors
inside successful HTTP responses continue through the API adapters' explicit error handling.

`WikiRepository.GetActivity` exposes copied request/operation snapshots for the optional
Wiki activity overlay. Scheduler-owned request state survives between attempts during
Polly backoff, reflects context priority promotion, and is removed on completion or
cancellation. A bounded in-memory history retains the latest 30 HTTP outcomes, transient
failures, and repository parsing/cache errors; monitoring does not submit requests or write
diagnostic files. The normal render loop only obtains snapshots while the panel is showing.
Repository completion records include cache hits so sub-frame operations remain observable.
Quest enrichment has a dispatcher-owned work list before repository submission; both quest
progress displays use the same progress calculation, and the wiki panel shows that backlog
separately from actual HTTP work.

NPC metadata uses a one-day TTL; found spell and quest metadata use seven days, while
confirmed missing results use one day. Spell/quest results persist a `Found` flag (older
cache entries default to found). API/network failures do not produce negative cache entries.
The UI quest batch and progress paths share `QuestLookupResult.NeedsRefresh` with the repository.
Sky caches retain their one-day TTL and existing files. Quest persistence has its own save-only gate,
never a network lock; interacting with an automatically queued item can promote it.
The Sky loading guard explicitly promotes an existing operation when Refresh is clicked.
Application shutdown and restart-to-update dispose the repository before canceling caller tokens:
waiters are released immediately, queued and in-flight HTTP is abandoned (never completed) rather
than canceled, so a cancellation is not rethrown through every lookup layer, and workers that are
parsing or saving are joined before the final save; remaining spells are rediscovered on log
replay. Repository continuations do not wait on the UI dispatcher during shutdown.

## Persistence

### Spawn wiki fallback

The UI housekeeping pass submits background `WikiRepository.GetMobAsync` lookups for tracked
current-zone spawn candidates without an override or learned duration. Fresh cached NPC metadata
is reused immediately; expected failures are surfaced under General with a five-minute retry
cooldown. Explicit respawn durations are parsed conservatively; ranges and conditional text are
not guessed. Repository requests retain global priority, deduplication, pacing and Polly retries.

`SpawnStore` keeps wiki seconds as a runtime-only fallback, hydrated from the persisted NPC
wiki cache rather than copied into learned samples or the spawn JSON. `LogEngine` serializes
fallback updates with tracking. `SpawnInfo.RespawnSeconds` selects override, learned, then wiki.
The settings grid shows a separate Wiki column and snapshots flag wiki-backed timers for
source labels. Returning local samples supersedes wiki immediately, including active countdowns.

### NPC compendium

`NpcInteractionTracker` feeds the independent, persistent `NpcCompendium` from both history
and live engine events. It retains full zone names and cast/target attribution; direct spell
landings require a matching uninterrupted cast within 20 seconds, once per cast/target.
Damage (including finishing hits and DoTs) remains available for encounter DPS without being
mistaken for reliable full-damage resistance evidence. Unknown one-word targets from other
attackers are ignored unless already known locally; player/pet versus NPC identity is not
reliably available in text logs.

The store keeps original display names, latest historical consider/assessment, encounter
dates/context, incoming/personal/other damage, raw per-spell landing/resist counts and outcomes.
Encounter durations use `max(1, lastActivity - start + 1s)`. Personal weighted DPS uses only
encounters with personal damage; other-attacker weighted DPS uses all observed encounters.
Death, zone/login/context changes and a fixed 15-second idle timeout close summaries.
No raw chat/log archive or individual same-name NPC identity is inferred.

Per-source timestamp/line watermarks deduplicate all processed events. Active summaries and
watermarks are saved atomically together, so replay can skip a recorded prefix and continue an
unfinished encounter after restart. Encounters have stable source/start-time/line IDs.
An in-memory active-encounter index avoids scanning unlimited completed history on each event;
history does not depend on the transient fight tracker's capped recent-fight list.
Parsed and unparsed log timestamps expire idle summaries. Snapshot-driven wall-clock expiration
is gated until the tail reader catches up, preventing recent history from splitting according
to dispatcher timing. Single-word death lines are interpreted only for an already active target.
Moving a log is a new source; replacing/truncating it with older timestamps is not a new import.

On disk the compendium is split so completed history is never held in memory or rewritten.
`npc-compendium\index.json` holds the catalog, considers, watermarks, interned source paths and
active/unflushed encounters. Finished encounters are appended once to per-NPC JSONL files in
`npc-compendium\npcs\` (named deterministically from mob/zone plus a SHA-256 suffix, so a crash
cannot orphan them). History lines reference sources by index and omit default values; IDs are
rebuilt from source/start/line. Appends are written before the index; on load, unflushed
encounters already present in history are dropped, a torn final line is skipped/repaired, and
duplicate IDs keep the last copy. `GetAsync` reads one NPC's history through a small LRU cache
(cache hits complete synchronously; a miss reads the JSONL file on the thread pool, and the NPC
view renders briefly empty until it arrives).
A legacy `npc-compendium.json` is migrated on first load and renamed to `.json.migrated`.

All file IO is asynchronous and streamed. JSON stores use `AtomicFile.WriteJsonAsync/ReadJsonAsync`:
compact, shared serializer options serialized straight into a temp file (no intermediate JSON
string or byte array) and atomically moved. Each store exposes `LoadAsync`/`SaveAsync`; saves take a
detached object snapshot under the store's lock (containers copied, immutable records and strings
shared; never holding a lock across `await`), then stream it outside the lock behind a per-store
`SemaphoreSlim` gate, so overlapping saves always leave the newest snapshot on disk. NPC history
records are streamed onto the end of their `.jsonl` file one line at a time. `settings.json` follows
the same pattern: `AppSettings.SaveAsync` snapshots the settings graph on the UI thread and streams
it from a worker. Unchanged settings are skipped by comparing a SHA-256 of the snapshot
(`AtomicFile.HashJson`, which streams the serializer into the hash), so the serialized document is
never built or retained. File-system
metadata probes from the UI (`File.Exists`, log discovery, `FileInfo`) go through `Task.Run`.

Startup loads every store concurrently on the thread pool (`MainWindow.StartupState.LoadAsync`)
before the window is built. Housekeeping calls `SaveAllInBackground` every few seconds (skipped
while the previous save is still writing). Closing the dock cancels the close, awaits
`ShutdownAsync` (stop the tailer, final `SaveAllAsync`) and then closes again. Windows logoff is
the single exception: `SessionEnding` must be synchronous, so `SaveAllBlocking` waits (up to 15 s)
on the saves running on the thread pool, which never resume on the dispatcher and so cannot
deadlock.

The `npcs` overlay has independent selection, cancellation, status and settings from `mobinfo`.
Its persisted Overview/Spell resists/Encounters views separate reference data from spell cards
and paged fights. NPC cards use the shared themed `Details` treatment with opt-in stacked
title/result/details; wrapped result text avoids the former long right-column collision.
Other overlay row layouts retain their behavior. Paging controls appear only in the encounter
view, with bounded navigation and a page indicator. Tab headers are treated as interactive
elements rather than drag handles.
They share cached wiki enrichment and stat row presentation; `MobResistanceStore.Get` can
filter by exact spell rank while reusing baseline classification across all saved samples.
Historical encounter landing counts are explicitly distinct from these assessed resistance
outcomes. Search works on lightweight catalog snapshots; selected history is copied under
the store lock and rendered at most once per second, with paged encounters and virtual rows.
Unlocked floating NPC windows may activate for keyboard input; other floating overlays retain
their nonactivating behavior.

The compendium participates in periodic, log-switch, shutdown and strict updater saves.
Load errors disable writes and preserve the original file; save errors are displayed and
strict updater saves throw instead of proceeding.

User data lives in `%AppData%\EqLogOverlay`, outside the installation.
Periodic saves also run on exit and Windows session shutdown. Atomic file replacement avoids
partially written destination files; users should still back up important tracking data.

## Theme file format

The Appearance editor exports import-compatible JSON. `Name` is nonempty and at most 80 characters.
`Colors` requires every key below with a `#RRGGBB` value; unknown keys are rejected.

- `NightBackground`, `NightSurface`, `NightRaised`: background layers.
- `NightBorder`, `NightText`, `NightMuted`: borders and text.
- `Pumpkin`: accent color; `AccentText`: text on accent-filled controls.
- `Hover`, `Pressed`, `Selection`, `Alternate`, `GridLine`, `ScrollThumb`: interaction/table palette.

Resource replacement and dynamic resource references support live theme changes.
Combat/status colors and user alert colors retain their separate semantic meaning.
