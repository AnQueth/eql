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
dotnet test tests\EqLog.Overlay.Tests\EqLog.Overlay.Tests.csproj -c Release
dotnet run --project src\EqLog.Overlay
dotnet run --project src\EqLog.Overlay -- "C:\path\to\eqlog_Name_server.txt"
```

Running the app normally uses your real `%AppData%\EqLogOverlay` data. Use a disposable
Windows account or VM for destructive tests, installer tests, and full update/restart tests.

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
