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

Quest opportunities and Sky overlays use recycling virtualized lists. Other overlays update
changed rows incrementally. This avoids rebuilding all row visuals on progress-only updates.

## Quest processing and inventory

Quest references come from EQLWiki's public MediaWiki API: quest-category pages that link to
or transclude the item's page, including redirects. References are not verified turn-in requirements.
Automatic lookups are serialized with a delay and cached for seven days. Wiki response processing
and cache serialization/writes run off the UI thread; historical loot is applied in short UI batches.
Selection versioning prevents older lookup responses from replacing the latest selected item's results.
The Sky catalog has a separate one-day cache shared by the browser and compact overlay.

Historical loot is deduplicated by log path and line number. Moving or replacing a log may
reimport history as unconfirmed loot. New loot after the inventory export time is added without
double-counting loot represented in the export.

Item comparisons remove numeric upgrade modifiers wherever they occur, collapse whitespace,
and ignore case. Displays retain original names. The same normalization is used by loot totals,
inventory grouping, Sky requirements, searches, wiki links, and cache keys.

## Persistence

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
