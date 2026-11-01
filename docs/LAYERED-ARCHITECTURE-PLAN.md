# Refactor Plan: Layered application architecture

Status: Implementation authorized on `refactor/layered-architecture`; inventory
checkpoint implemented. Automated validation and isolated full-window review are
recorded below. Remaining migrations await the inventory review gate.

## Objective

Separate game-file access, application workflows, domain rules, persistence, and
WPF presentation without changing shipped behavior or rewriting the application.
Implement one complete inventory vertical slice first, then apply the proven
boundaries to log sessions, tracking, wiki enrichment, and other overlays.

The user authorized implementing this plan on a new branch. No framework upgrade,
database migration, version bump, or publication is included.

## Inventory checkpoint

- Baseline: released commit `7f419b3` (v1.0.34), clean worktree at branch creation;
  Release build and 517 Core + 115 Overlay tests passed.
- Added Application/Infrastructure and xUnit projects with the planned reference directions.
- Migrated inventory input, reconciliation/state, save sequencing and load normalization.
  The old WPF direct export read/save queue is removed.
- Bound an inventory view model; existing Sky/Mote/quest consumers use detached
  domain snapshots, while inventory bindings use immutable summaries.
- Preserved document shape, legacy fields, atomic writes, read-only columns and
  confirmed ownership; corrupt inventory storage disables writes until restart.
- Kept JSON attributes/shared `AtomicFile` in Core temporarily; the general
  persistence phase removes these alongside the other stores, not piecemeal.
- Validation: Release build (0 warnings/errors), 657 tests (514 Core, 12 Application,
  14 Infrastructure, 117 Overlay), plus real XAML bindings, isolated file import/save,
  character-switch races, cancellation, save ordering/retry and legacy normalization.
- Release CI explicitly includes both new suites; app version stays 1.0.34.
- Self-contained Windows x64 publish succeeded and includes both new layer assemblies;
  no app startup, installer publication, version change or new release tag was performed.
- Still required at the gate: manual full-window docked/floating inventory review
  in an isolated account/VM. No normal startup against the user's real data.
- Phases 3-7 have not started.

## Baseline state (before this refactor)

- `EqLog.Core` contains parsing and tracking, but also file tailing, JSON storage,
  HTTP clients, cache scheduling, and storage migrations.
- `EqLog.Overlay` contains WPF views and presenters, but also import workflows,
  background scheduling, mutable application state, startup loading, and saves.
- `MainWindow` is a hidden controller. Its partial files separate source files,
  not ownership or dependencies.
- `InventoryMonitoring` reads and stabilizes exports, reconciles inventory,
  refreshes controls, and persists the result in one workflow.
- `LootQuests` owns inventory queues, quest lookups, selection versioning,
  save sequencing, and UI error messages.
- `LogEngine` owns synchronized tracking state and also exposes path-based saves.
- Tracking stores combine in-memory rules, persisted documents, and load behavior.
- Presenters receive detached snapshots but can also access the live engine.

Existing strengths to retain: atomic writes, detached snapshots, ordered saves,
dirty-state recovery, stable-export checks, cancellation and stale-result guards,
short processing batches, incremental rendering, and virtualized lists.

The inventory and overlay changes were released as v1.0.34 before branch creation.
They remain the behavior baseline; do not revert them.

## Target state

### Projects and dependency rules

| Project | Responsibilities | Allowed project references |
|---|---|---|
| `EqLog.Core` | Parsing, tracking rules, inventory reconciliation, quest progress, upgrade calculations | None |
| `EqLog.Application` | Use cases, state ownership, application read models, scheduling and persistence coordination, external-service contracts | Core |
| `EqLog.Infrastructure` | Game-file inputs, JSON/JSONL persistence, HTTP/wiki clients, cache-file handling | Application, Core |
| `EqLog.Overlay` | WPF views, view models, rendering, themes, docking, Windows integration, composition root | Application, Core; Infrastructure only for composition |

Core, Application, and Infrastructure target `net10.0`. Overlay keeps
`net10.0-windows`. Preserve existing executable identity and packaging.

Core and Application must not depend on WPF, filesystem operations, HTTP clients,
or concrete storage implementations. Domain operations receive data, not paths.
Application ports may carry source identifiers and metadata, but do not open files.
Avoid JSON attributes on domain state; persisted-document mapping belongs to
Infrastructure.

The Overlay composition root constructs Infrastructure adapters and Application
services. Views and view models depend on Application contracts, not concrete
repositories. WPF resource types and window settings can remain presentation-owned;
do not force them into platform-neutral domain models.

### Separate file inputs from persistence

| Game-owned inputs | App-owned storage |
|---|---|
| Logs and inventory exports | Settings, tracking data, wiki caches |
| Sharing, partial writes, truncation, discovery, stable metadata | Serialization, atomic writes, migrations, backup, corruption handling |
| Read-only access | Controlled read/write access |
| Produce line batches or stable export documents | Load/save detached persisted documents |

Continue using existing JSON/JSONL files. A database, generic CRUD repository,
message bus, mediator framework, and interface for every helper are not required.

### Application state and data flow

```text
Game export -> stable input adapter -> parser -> inventory import service
                                                   |
                                         committed inventory snapshot
                                            /                 \
                                  persistence coordinator    inventory query
                                          |                       |
                                     JSON repository          view model
                                                                  |
                                                            WPF controls
```

- Give inventory one Application-owned mutation boundary. Serialize export
  reconciliation, loot observations, and completion changes through that owner.
- Keep the engine's existing synchronization initially; do not replace working
  locks while simultaneously moving responsibilities.
- Publish detached, effectively immutable read models. Never expose live lists,
  dictionaries, or mutable stores to views or repositories.
- UI selection, filters, and tabs remain presentation state.
- Application results carry explicit progress/error information; only Overlay
  turns it into user-facing controls and notifications.
- Saves operate on consistent snapshots, not objects still being mutated.

## Affected files and ownership map

Paths below are relative to the repository root. New directories are proposed.

| File or area | Change type | Responsibility and dependencies |
|---|---|---|
| `EqLogOverlay.slnx` and project files | Modify/create | Add Application, Infrastructure, and their tests before migrating callers |
| `src/EqLog.Application/Inventory/` | Create | Input contracts, import service, state owner, inventory queries/read models |
| `src/EqLog.Infrastructure/GameFiles/` | Create | Export stabilization and log discovery/tailing adapters |
| `src/EqLog.Infrastructure/Persistence/` | Create | Specific repositories, storage paths, persisted documents and mapping |
| `src/EqLog.Core/QuestInventory.cs` | Modify | Preserve reconciliation; extract JSON load normalization and persistence metadata |
| `src/EqLog.Core/InventoryExport.cs` | Modify | Retain pure parsing/location rules; move filesystem path discovery out |
| `src/EqLog.Overlay/InventoryMonitoring.cs` | Modify/remove after migration | Replace direct import/storage work with Application service calls |
| `src/EqLog.Overlay/InventoryOverlay.cs` | Modify | Bind item/copy read models; retain WPF-specific rendering |
| `src/EqLog.Overlay/LootQuests.cs` | Modify | Extract inventory saves, state queues, lookup orchestration and completion operations |
| `src/EqLog.Overlay/MainWindow.xaml` | Modify | Bind inventory view model while preserving columns, filter and detail tabs |
| `src/EqLog.Overlay/MainWindow.xaml.cs` | Modify | Incrementally remove lifecycle, scheduling and storage ownership |
| `src/EqLog.Overlay/Startup.cs` and `App.xaml.cs` | Modify | Separate bootstrap workflow from WPF composition and notification |
| `src/EqLog.Overlay/AppSettings.cs` | Modify | Separate settings persistence from presentation settings types |
| `src/EqLog.Core/IO/` | Move/adapt | Concrete file access becomes Infrastructure |
| `src/EqLog.Core/LogEngine.cs` | Modify | Keep processing/snapshots; move path-based saves to Application/Infrastructure |
| `src/EqLog.Core/Tracking/*Store.cs`, `AtomicFile.cs`, `JsonLines.cs` | Split/move | Separate tracking state from persistence mechanisms |
| `src/EqLog.Core/NpcCompendium.cs`, `MobResistanceStore.cs` | Split | Retain rules/read models; extract append storage, migrations and load/save work |
| `src/EqLog.Core/WikiRepository.cs`, `WikiRequestScheduler.cs`, wiki lookup files | Split/move | Separate enrichment policy, network adapters and persistent caches |
| `src/EqLog.Core/TrackingDataReset.cs`, `WikiCacheReset.cs` | Split/move | Application reset workflow; Infrastructure file operations |
| `src/EqLog.Overlay/WikiCache.cs`, `SkyQuestBrowser.cs`, `ExaltationsOverlay.cs` | Modify | Replace embedded cache persistence/network orchestration with service calls |
| `src/EqLog.Overlay/OverlayPresenters.cs`, `OverlayPanel.cs` | Modify as needed | Consume detached query results; keep Brush/control types in Overlay |
| Existing Core and Overlay tests | Modify selectively | Preserve behavior assertions; relocate I/O tests with implementations |
| `tests/EqLog.Application.Tests/`, `tests/EqLog.Infrastructure.Tests/` | Create | Fake-adapter workflow tests and isolated adapter integration tests |
| `docs/ARCHITECTURE.md`, `docs/DEVELOPMENT.md`, `README.md` | Modify during implementation | Document actual boundaries, test commands, and any approved UX changes |
| `.github/workflows/release.yml` | Inspect; modify only if needed | Ensure explicit build/test selections include new projects after local validation |

## Execution plan

### Phase 0: Establish baseline and characterization

- [x] Review existing uncommitted changes and preserve them as the baseline.
- [x] Record successful Release build and existing test results.
- [x] Characterize import, save, shutdown, character-switch and corruption behavior.
- [ ] Inventory every storage document/path and its compatibility requirements.
- [x] Capture current threading/ownership guarantees and error presentation.
- [x] Correct architecture documentation that still describes newer loot increasing
  inventory ownership; current confirmed-export behavior is the baseline.

Verify: existing suites pass before refactoring. Record pre-existing failures
separately rather than expanding scope to unrelated fixes.

### Phase 1: Contracts and project boundaries

- [x] Create Application and Infrastructure class libraries using SDK tooling.
- [x] Add projects to the solution and establish the reference directions above.
- [x] Define focused inventory contracts: `IInventoryExportSource`,
  `IQuestInventoryRepository`, and an inventory application service/query boundary.
- [x] Define export metadata, import outcome, item-summary and copy-location models.
- [x] Use `TimeProvider` where time affects polling, recency or retries.
- [x] Define ownership, cancellation, disposal and error contracts explicitly.
- [x] Add test projects following the repository's xUnit conventions.
- [x] Wire construction at the WPF composition root without switching behavior yet.

Verify: build all projects; Core/Application load and test without WPF. No new
runtime dependency packages unless actually needed.

### Phase 2: Complete the inventory vertical slice

Dependency: Phase 1.

- [x] Implement the stable export reader in Infrastructure using current sharing,
  metadata checks, partial-write handling and character/log identity validation.
- [x] Retain `InventoryExport.Parse` and copy-location rules as pure functions.
- [x] Implement the inventory repository using current file names and document shape.
- [x] Move saved-data normalization into an explicit load mapping/normalization step.
- [x] Implement serialized inventory application state and import reconciliation.
- [x] Preserve last-good inventory on read/parse failure; expose the failure and retry state.
- [x] Move save sequencing and snapshot creation out of WPF code-behind.
- [x] Create an inventory view model and wire filters, item selection, copy locations,
  quest references, completion operations and freshness/error messages.
- [x] Refresh selection details when the committed export changes.
- [x] Remove the old direct read/write path only after callers use the new service.

Required invariants:

- Highest displayed rank comes from current exported copies, independent of row order.
- Confirmed loose/worn ownership equals Carried + Bank + Hoard + Depot + Other.
- Loot observations update recency/lookups, not confirmed ownership.
- Collection and installed counts remain separate.
- Inventory grid retains no Owned or History column; storage counts stay read-only.
- All-copy details include every matching rank, quantity and readable location.
- Filters, selections and tabs do not trigger filesystem reads.
- Stale imports/lookups cannot overwrite a newer character or selection.
- Quest lookups may fail or pause without hiding local copy locations.

Verify: Application tests with fake inputs/repositories; Infrastructure tests with
temporary files; existing inventory, rank, filter, location and WPF layout tests.
Manually verify docked and floating inventory in an isolated account/VM.

Gate: do not migrate the other features until inventory works end to end through
the new boundaries and the old import/save path is removed.

### Phase 3: Extract log-session lifecycle

Dependency: successful inventory gate.

- [ ] Introduce `ILogSource` and a log-discovery boundary.
- [ ] Move `LogTailer`/`LogFileLocator` concrete file behavior into Infrastructure.
- [ ] Extract session start/stop, replay/live state, follow-newest and cancellation
  from MainWindow into `LogSessionService`.
- [ ] Feed line batches to the existing engine; expose detached snapshots/progress.
- [ ] Preserve watermark identities, replay timing, alert suppression during history,
  complete-line handling, truncation behavior and shutdown cancellation.
- [ ] Keep game-window focus, hotkeys and window placement in Overlay.

Verify: tailer and engine-concurrency tests plus fake-source session-switch tests.
Test a slow previous session finishing after a new session starts.

### Phase 4: Extract tracking and settings persistence

Dependency: Phase 3 for session-controlled saves; reuse Phase 2 save contracts.

- [ ] Separate in-memory tracking state from persisted DTOs and serializers.
- [ ] Remove filesystem paths/save methods from the engine.
- [ ] Move AtomicFile/JSONL mechanisms into Infrastructure and reuse them.
- [ ] Add specific repositories for durations, spawns, motes, NPCs and resistances.
- [ ] Separate settings data from its serializer/path handling without losing
  unknown-field preservation or dock/workspace compatibility.
- [ ] Move startup loads, periodic saves, shutdown flush and reset orchestration
  into Application services.
- [ ] Preserve snapshot ordering, dirty-state retry and corruption protection.
- [ ] Do not consolidate independent stores into one broad lock or rewrite all
  persisted formats as part of this extraction.

Verify: adapter load/save round trips, legacy document fixtures, malformed files,
write failures, overlapping saves and shutdown flush. Older snapshots must never
overwrite newer ones; failed saves must remain retryable.

### Phase 5: Separate wiki enrichment, HTTP and caches

Dependency: repository/save coordination established.

- [ ] Define wiki-client and cache-repository contracts for actual use cases.
- [ ] Keep HTTP scheduling, throttling and transport in Infrastructure.
- [ ] Keep enrichment priorities, refresh decisions and workflow cancellation in
  Application; explicitly assign in-flight deduplication to one owner.
- [ ] Replace persistence callbacks and embedded cache writes with repositories.
- [ ] Migrate quest, Sky, mob/spell and item-effect callers one feature at a time.
- [ ] Preserve cache lifetimes, force-refresh behavior, failed-refresh last-good data,
  shared-request cancellation and corrupt-cache overwrite protection.
- [ ] Preserve telemetry consent and existing allowlisted labels; do not transmit
  inventory locations, source paths, search text or new sensitive data.

Verify: fake HTTP and fake cache tests; retain scheduler/repository regression tests.
Validation must not depend on live EQLWiki availability.

### Phase 6: Finish presentation separation

Dependency: each feature's Application services are available.

- [ ] Introduce feature view models incrementally, not a wholesale XAML rewrite.
- [ ] Replace live engine/store access in presenters with application query results.
- [ ] Keep WPF formatting, brushes, links, chrome and visual incremental updates in Overlay.
- [ ] Move application housekeeping timers out of MainWindow; retain render/focus
  timers where they are explicitly UI concerns.
- [ ] Ensure view models dispatch UI-bound changes without making Application
  depend on the WPF Dispatcher.
- [ ] Keep docking/reparenting behavior and existing control interaction intact.
- [ ] Remove obsolete partial-controller state and transitional bridges.

Verify: fake-service view-model tests, presenter tests, dock/floating interaction,
selection retention, virtualization and long-list scrolling.
Preserve current 250 ms render cadence and short replay processing budgets unless
an explicitly approved and measured change replaces them.

### Phase 7: Enforce boundaries and complete cleanup

Dependency: all migrated slices pass their gates.

- [ ] Add automated reference/architecture checks for forbidden dependencies.
- [ ] Ensure Core/Application contain no direct filesystem, HTTP or WPF usage.
- [ ] Ensure view/view-model code contains no direct repositories or file access;
  exempt only clearly identified composition code.
- [ ] Move tests into the layer they verify; retain domain tests in Core.
- [ ] Remove compatibility bridges that are no longer needed, not persisted legacy
  fields still required for document compatibility.
- [ ] Update architecture/development docs and cross-reference this completed plan.
- [ ] Inspect packaging/update-smoke references for assemblies moved across projects.
- [ ] Verify publishing includes new assemblies without a version bump or release.

## Validation commands

Run builds/tests sequentially when they share project outputs; concurrent builds
can contend for the same intermediate assemblies.

Baseline:

```powershell
dotnet build EqLogOverlay.slnx -c Release
dotnet test tests\EqLog.Core.Tests\EqLog.Core.Tests.csproj -c Release --no-build
dotnet test tests\EqLog.Overlay.Tests\EqLog.Overlay.Tests.csproj -c Release --no-build
```

Inventory gate, before relocating the existing tests:

```powershell
dotnet test tests\EqLog.Core.Tests\EqLog.Core.Tests.csproj -c Release --filter "FullyQualifiedName~QuestInventoryTests|FullyQualifiedName~InventoryExportTests|FullyQualifiedName~ItemNamesTests|FullyQualifiedName~SkyQuestsTests"
dotnet test tests\EqLog.Overlay.Tests\EqLog.Overlay.Tests.csproj -c Release --filter "FullyQualifiedName~ItemFilterTests|FullyQualifiedName~InventoryLayoutTests|FullyQualifiedName~ItemUpgradesOverlayTests|FullyQualifiedName~MoteOverlayTests|FullyQualifiedName~OverlayChromeTests"
```

Final gate after adding the new test projects:

```powershell
dotnet build EqLogOverlay.slnx -c Release
dotnet test tests\EqLog.Core.Tests\EqLog.Core.Tests.csproj -c Release --no-build
dotnet test tests\EqLog.Application.Tests\EqLog.Application.Tests.csproj -c Release --no-build
dotnet test tests\EqLog.Infrastructure.Tests\EqLog.Infrastructure.Tests.csproj -c Release --no-build
dotnet test tests\EqLog.Overlay.Tests\EqLog.Overlay.Tests.csproj -c Release --no-build
git diff --check
```

Additionally run existing update-smoke validation according to its supported
arguments if project/reference or packaging changes affect it. Do not run installer,
reset, or app-start integration tests against the user's real tracking data.

## Rollback and recovery

1. Deliver cohesive, independently buildable phases on a dedicated refactoring
   branch once implementation is authorized. Preserve the user's working changes.
2. Keep the existing adapter path until each replacement passes its tests, but
   never run old and new mutation/save paths simultaneously.
3. If a slice fails, switch its callers back through a narrow reversible change;
   do not reset the repository or discard unrelated work.
4. Preserve file names and formats so code rollback does not require data rollback.
5. Use temporary directories for persistence tests and an isolated account/VM for
   full UI startup/shutdown. Back up isolated test data before migration/reset tests.
6. If a required document change emerges, stop and separately approve a
   versioned migration and restore procedure before deploying it.
7. On read/parse/corruption failures, retain last-good state, surface the error,
   and do not overwrite the damaged file with empty success-shaped data.

## Risks and mitigation

| Risk | Mitigation |
|---|---|
| Partial MainWindow files hide shared state and initialization ordering | Trace callers and move one owned workflow at a time |
| Async results arrive after character/session changes | Carry generation/source identity; reject stale commits |
| Save coordination is weakened during extraction | Reuse ordered snapshot saves; test overlap and failure recovery |
| UI receives mutable state | Publish detached snapshots; test that snapshots remain stable after updates |
| Legacy fields disappear during DTO mapping | Round-trip fixtures and preserve intentional compatibility fields |
| Filtering or location queries rebuild/import data unnecessarily | Query committed in-memory snapshots only |
| Wiki shared-request cancellation changes | Preserve existing deduplication and caller-vs-operation cancellation tests |
| New view models lose virtualization or scrolling | Keep current panels and validate long-list behavior before cleanup |
| Storage extraction silently changes ownership rules | Test confirmed-export arithmetic, collections and legacy reconciliation |
| Too many abstractions slow delivery | Use only boundary contracts required by each migrated use case |

## Completion criteria

- [ ] Inventory, log sessions, persistence and wiki enrichment have explicit owners.
- [ ] Core/Application can be exercised without WPF, real files or live HTTP.
- [ ] Views display injected snapshots and invoke Application operations only.
- [ ] Current item ranks, storage counts, copy locations, filters and quest behavior remain intact.
- [ ] Existing files, migrations, settings extensions and watermark semantics remain compatible.
- [ ] Startup, shutdown, saves, cancellation and corruption recovery are verified.
- [ ] No obsolete direct I/O path remains behind migrated views.
- [ ] Final build, tests and dependency checks pass.

## Approval checkpoint

Approve Phase 1 only after reviewing the ownership/contracts and inventory gate.
Phase 0 baseline verification precedes implementation. Review the completed inventory
slice before approving migration of the remaining application.
