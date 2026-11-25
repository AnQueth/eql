# Development

## Build and run

Use Windows and the .NET 10 SDK. From the repository root:

```powershell
dotnet build EqLogOverlay.slnx
dotnet test EqLogOverlay.slnx --no-restore
dotnet run --project src\EqLog.Overlay
```

An optional argument selects a log:

```powershell
dotnet run --project src\EqLog.Overlay -- "C:\game\Logs\eqlog_Name_server.txt"
```

**Source builds use real `%AppData%\EqLogOverlay` data.** Close other copies first.
Use a disposable Windows account or VM for destructive, installer, and update tests.

## Architecture

| Project | Responsibility |
|---|---|
| [Core](../src/EqLog.Core) | Pure parsing, tracking, combat and inventory rules; no I/O or WPF |
| [Application](../src/EqLog.Application) | Sessions, commands, detached queries, workflows and persistence policy; references Core |
| [Infrastructure](../src/EqLog.Infrastructure) | Read-only game inputs, JSON/JSONL storage, migrations and HTTP |
| [Overlay](../src/EqLog.Overlay) | WPF views/view models, formatting, native windows and composition |
| [Tests](../tests) | Matching layer suites and update smoke checks |

Concrete adapters are wired only in explicit `*Composition` factories.
`GameSession` owns the engine and tracking state; views use Application commands
and detached read models, never live engines, stores, or repositories.
Application returns typed values; Overlay owns wording, sorting, and visual layout.
Boundary tests enforce these rules.

### Log to UI

Game-file reader -> `LogSessionService` / `GameSession` ->
[OverlayMessagePump](../src/EqLog.Overlay/Messaging/OverlayMessagePump.cs) ->
view models -> XAML bindings.

The pump handles reader-thread observations and queued UI events. Every 250 ms it
captures a detached frame asynchronously and dispatches to interested receivers.
Only one cycle runs at a time; stale session revisions are rejected.
Periodic capture also advances countdowns and expiry, even without new log lines.

### Presentation entry points

- [OverlayPanel.xaml](../src/EqLog.Overlay/OverlayPanel.xaml): shared chrome.
- [Views/Overlays](../src/EqLog.Overlay/Views/Overlays): specialized XAML templates.
- [ViewModels](../src/EqLog.Overlay/ViewModels): state, filters and commands.
- [Mvvm](../src/EqLog.Overlay/Mvvm): observable objects/collections and commands.
- [MainWindow.Overlays.cs](../src/EqLog.Overlay/MainWindow.Overlays.cs): composition.
- [DockWindow.cs](../src/EqLog.Overlay/DockWindow.cs) and
  [OverlayWindow.cs](../src/EqLog.Overlay/OverlayWindow.cs): placement and native behavior.

`MainWindow` is a hidden controller; `DockWindow` hosts settings and workspaces.
The same panel moves between dock and floating window, with separate saved bounds.
Game-focus hiding must not change workspace membership.
Use property notifications, observable collections and `ICommand`; preserve existing
rows where possible to retain selection and scrolling. Keep drag/focus/rendering in views.

## Data and background work

- App data lives under `%AppData%\EqLogOverlay`; game inputs are read-only.
  Back up the entire folder, including journals and checkpoints.
- Repositories order saves, write detached snapshots, retain dirty retries and
  protect unreadable data from overwrite. Preserve formats and replay watermarks.
- NPC history loads per NPC; resistance outcomes hydrate on demand from a resident
  index. Do not copy or rewrite all history for a small update.
- Startup opens the workspace before history loads. Never save placeholder history
  or let late background completion reopen a closed workspace.
- Capture detached inputs before worker operations. Publish on the dispatcher only
  if session, selection and filter still match; cancel superseded work.
- Use the shared wiki service and paced priority queue, not independent HTTP calls.
  Failures preserve cached data and remain visible.

## Tests and conventions

Run the smallest affected project/filter while iterating:

```powershell
dotnet test tests\EqLog.Overlay.Tests\EqLog.Overlay.Tests.csproj --no-restore --filter "FullyQualifiedName~OverlayChromeTests"
```

Use pure-rule tests in Core, fake ports in Application, temporary files/fake HTTP
in Infrastructure, and actual bindings/commands in Overlay. Test shared changes
across the affected suites. Full-window and updater checks use a disposable profile.
Do not run simultaneous builds against shared outputs.

C# uses Allman braces, braced control flow and expanded method bodies.
Follow [.editorconfig](../.editorconfig); keep comments for non-obvious behavior.
Telemetry must use the existing consent gate and fixed `UsageAction` values:
never capture control contents, game data, paths or user-defined names.
Use recording loggers in tests, not live ingestion.

See the [user guide](../README.md) for controls and the
[release checklist](RELEASING.md) for publication.
