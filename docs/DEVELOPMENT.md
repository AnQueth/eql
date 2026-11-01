# Development

This document covers building and changing EqLog Overlay. See the [user guide](../README.md)
for installation and app usage, [architecture notes](ARCHITECTURE.md) for implementation details,
and [release instructions](RELEASING.md) for packaging and publishing.

## Requirements and running from source

Use Windows and the .NET 10 SDK.

```powershell
dotnet restore EqLogOverlay.slnx
dotnet build EqLogOverlay.slnx -c Release
dotnet test tests\EqLog.Core.Tests\EqLog.Core.Tests.csproj -c Release
dotnet test tests\EqLog.Application.Tests\EqLog.Application.Tests.csproj -c Release
dotnet test tests\EqLog.Infrastructure.Tests\EqLog.Infrastructure.Tests.csproj -c Release
dotnet test tests\EqLog.Overlay.Tests\EqLog.Overlay.Tests.csproj -c Release
dotnet run --project src\EqLog.Overlay
dotnet run --project src\EqLog.Overlay -- "C:\path\to\eqlog_Name_server.txt"
```

Running the app normally uses your real `%AppData%\EqLogOverlay` data. Use a disposable
Windows account or VM for destructive tests, installer tests, and full update/restart tests.

## Layered development

Inventory uses Core rules, Application-owned state and workflows, Infrastructure
input/storage adapters, and Overlay view-model bindings. Construct concrete adapters
only in the explicit `*Composition` factories; views must not open files, construct
HTTP clients or call concrete repositories. Log lifecycle, settings/tracking saves,
wiki policy and maintenance cadence live in Application; HTTP, storage, migrations
and game-file adapters live in Infrastructure. Core stays package-free and pure.
See the [implementation plan](LAYERED-ARCHITECTURE-PLAN.md).

Add fake-port workflow tests to Application, pure rule/tracker tests to Core,
temporary-file/fake-HTTP tests to Infrastructure and binding/presenter tests to Overlay.
Presenters receive `EngineQuerySnapshot`, never the live `LogEngine`.
`LayerBoundaryTests` verify both source access and actual/project references.
Use `GameSession` commands for engine changes and Application inventory, Sky,
combat, mote and NPC queries for rule-derived values. Do not retain live Core
engines/stores or call domain helpers from views. Portable snapshot records/enums
may be shared, but calculated domain getters must be evaluated in Application.
`PresentationBoundaryTests` inspect compiled calls, including lambdas, to enforce
this distinction. Keep display formatting, sorting/paging and chart geometry in WPF.
Return typed counts, rates, ranks, classifications and validation statuses from
Application. Build sentences, labels, explanations, tooltips and formatted tables
in Overlay; do not package these as strings or UI-shaped block models in Application.
Source names/wiki text, user templates, stable identifiers and diagnostics remain
data, not generated display wording. Query source checks guard formatting ownership;
assert typed values in Application tests and rendered wording in Overlay tests.

Application tests use fake ports to exercise stabilization, superseded sessions,
cancellation, detached snapshots, ordered saves, retries and confirmed counts.
Infrastructure tests create/delete explicitly owned temporary directories and verify
legacy JSON, rank history, locked/partial exports and end-to-end import/save.
Overlay tests exercise the actual inventory XAML bindings, read-only columns,
selection retention, filters, copy-location refresh, splitter and long-list scrolling.
These do not start the real app or write to your profile.

Settings lists overlay visibility checkboxes and per-overlay appearance options
alphabetically by displayed caption. Default placement and runtime presenter order
remain independent of this settings-only ordering.

The Sky class dropdown populates from the startup catalog as well as refreshed
catalogs, preserving a valid saved selection. Cached quests must not require a
network refresh before their classes become selectable.

Text searches debounce typing for approximately 180-200 ms. Quest opportunities,
Sky quests, exaltations, inventory/merge, raw logs, saved NPCs and mote target names
prepare filtered results away from the WPF dispatcher. Capture detached input on
the dispatcher; never read controls or mutable UI caches from a worker. Only the
newest request may publish, and shutdown cancels pending work. Formatting remains
in Overlay, even when performed on a worker; only frozen brushes may cross threads.
Shared item filter toolbars debounce their callbacks. WPF layout stays on the
dispatcher: virtualized overlays replace large row changes with one collection
reset instead of notifying once per item, and merge-table rendering yields between
small batches. Filter tests verify coalescing, responsiveness and stale-result rejection.

The mote planner's inventory selection is independent of its typed name filter.
Picking a matching item preserves the search text and plans for the selected item
at its highest owned rank. Editing the search clears that selection; refreshing
matching choices preserves it while the selected item still matches.

If shared build outputs are damaged, use one isolated artifact directory for both
build and tests. Allow restore on the first build: a new artifact directory has no
`project.assets.json`. Do not run simultaneous builds against shared outputs.

```powershell
dotnet build EqLogOverlay.slnx -c Release --artifacts-path artifacts\layered-baseline
dotnet test EqLogOverlay.slnx -c Release --artifacts-path artifacts\layered-baseline --no-build
```

The inventory checkpoint was approved for continuation. Still manually check docked/floating inventory,
character switching and final saves in a disposable account/VM. Automated binding
tests are not a substitute for this full-window review.

## C# formatting

C# uses Allman braces, braced control-flow bodies, and one statement per line.
Methods and accessors use expanded block bodies rather than compact one-line implementations.
Auto-properties and expression lambdas used as predicates/selectors may remain concise.
The repository [.editorconfig](../.editorconfig) records these preferences.

```powershell
dotnet format style EqLogOverlay.slnx --no-restore --diagnostics IDE0011 IDE0021 IDE0022 IDE0023 IDE0024 IDE0025 IDE0026 IDE0027 IDE0061 IDE0062 IDE2001 IDE2002 --severity info
dotnet format whitespace EqLogOverlay.slnx --no-restore
```

Add `--verify-no-changes` to either command to check without editing.

## Usage instrumentation

Telemetry is off by default and all events go through the consent gate in
[`UsageTelemetry`](../src/EqLog.Overlay/UsageTelemetry.cs). Do not build another exporter
or capture control content, search text, game data, paths, URLs, exception messages, or
user-defined names. Test instrumentation with the recording logger, not live ingestion.

For a button, checkbox, or toggle button, add a fixed member to
[`UsageAction`](../src/EqLog.Overlay/UsageTracking.cs) and opt the control in:

```xml
<Button Content="Reset session stats" Click="ResetSession_Click"
        local:UsageTracking.Action="ResetSession" />
```

For code-created buttons use `UsageTracking.SetAction(button, UsageAction.ResetSession)`.
For commands or non-button actions call `UsageTracking.Track(UsageAction.ResetSession)`
in the shared action handler instead. Do not instrument both the button and its handler
for the same action. Attached tracking includes keyboard activation and handled clicks,
does not mark events handled, and can be removed with `UsageAction.None`.
Click events measure attempts (including cancelled dialogs), not successful operations.
Existing `feature_used` events describe feature entry points; do not sum them with
`feature_action` as if they were the same measurement.

Events share `app.version` and an anonymous `session.id`, regenerated on each opt-in run.
`theme_state` / `theme_changed` have `theme` and `theme.mode` (`Windows` / `Fixed`).
Only allowlisted bundled names with matching palettes are reported; all others are `Custom`.
`panel_state` / `panel_changed` have the fixed `feature` label, `panel.visible` (configured
enabled state), and `panel.placement` (`Docked` / `Floating`). Startup state is not a
feature click. State snapshots include disabled panels so denominators are available.
Game focus changes and temporary auto-hide do not emit panel changes.

Example Application Insights queries (workspace tables use `AppEvents`, `Name`,
`Properties`, and `TimeGenerated` instead of `customEvents`, `name`, `customDimensions`,
and `timestamp`):

```kusto
// Latest theme per opted-in session, not unique users.
customEvents
| where timestamp > ago(7d) and name in ("theme_state", "theme_changed")
| extend session = tostring(customDimensions["session.id"])
| where isnotempty(session)
| summarize arg_max(timestamp, *) by session
| summarize sessions = count() by theme = tostring(customDimensions["theme"]),
    mode = tostring(customDimensions["theme.mode"])
| order by sessions desc
```

```kusto
// Latest enabled-panel placement per session.
customEvents
| where timestamp > ago(7d) and name in ("panel_state", "panel_changed")
| extend session = tostring(customDimensions["session.id"]),
    panel = tostring(customDimensions["feature"])
| where isnotempty(session)
| summarize arg_max(timestamp, *) by session, panel
| where tobool(customDimensions["panel.visible"])
| summarize sessions = count() by panel,
    placement = tostring(customDimensions["panel.placement"])
```

```kusto
// Action frequency and number of opted-in sessions using each action.
customEvents
| where timestamp > ago(7d) and name == "feature_action"
| summarize actions = count(), sessions = dcount(tostring(customDimensions["session.id"]))
    by action = tostring(customDimensions["action"])
| order by actions desc
```
