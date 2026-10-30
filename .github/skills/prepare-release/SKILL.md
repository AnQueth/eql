---
name: prepare-release
description: 'Mandatory EqLog Overlay pre-release workflow. Use before every release, version bump, release tag, installer publication, or request to make, publish, ship, or resume a release. Prepend the new release to the bundled What''s New history without deleting older entries, then run its single validation check before publishing.'
license: Apache-2.0
---

# Prepare an EqLog Overlay release

Run this skill **before every release**, including resumed releases. The What's New
page is a cumulative release history, newest first. Explain what players can do and
where to find it; do not copy raw commit logs.

## Workflow

1. **Establish the release scope.** Inspect the worktree, latest stable tag, and changes
   since that tag, including intended untracked files. Preserve unrelated work. Use the
   agreed release version, or the next patch version when none is specified and the
   scope is clear. Update `Version` in the
   [app project](../../../src/EqLog.Overlay/EqLog.Overlay.csproj).
2. **Prepend to the help history.** Update the bundled
   [What's New document](../../../src/EqLog.Overlay/Help/WhatsNew.xaml).
   Keep every existing release entry and its version heading. Never replace, delete,
   or rewrite older release notes when adding a release. If preparing the same unreleased
   version again, update its existing entry rather than duplicating it.
   Put the new version's heading and notes at the top, with older releases below in
   reverse chronological order. Set the document's `Tag` and first visible heading
   to the release version. Directly under each version heading, add a muted line with
   when that release was created: `Released <Month> <day>, <year> at <h:mm AM/PM> CT.`
   (use the tag creation time for existing releases). Summarize actual changes
   since the previous release, with short, plain-English sections describing benefits,
   exact UI paths, how to try the features, and important limitations. Cover fixes and
   intentional behavior changes. Do not invent features or copy raw commit messages.
   Keep the native FlowDocument format and dynamic theme resources; the page must work
   offline. Update directly related README help when needed.
3. **Use one What's New check.** After finalizing the page and version, run the existing
   [release-notes check](./scripts/Test-ReleaseNotes.ps1) once from the repository root:
   `& .\.github\skills\prepare-release\scripts\Test-ReleaseNotes.ps1 -Version <version>`.
   Do not add temporary WPF harnesses, screenshots, theme matrices, or repeated
   open/close/reopen tests for release-note updates. Rerun this check only if it fails
   or the notes/version change afterward. CI runs the same validator as its release guard.
   Follow the build, test, self-contained publish, installer packaging, install-hook,
   and update-smoke checks in the [release workflow](../../workflows/release.yml).
   These normal app release checks remain required; do not turn the What's New page
   into a separate UI-testing project.
4. **Publish using the existing release workflow.** Review the final diff and stage only
   the intended release files, including the help page and app version. Commit and tag
   only after validation; push the intended commit and stable `v<version>` tag.
   The workflow publishes the installer and update feed. Do not bypass its What's New
   validation, claim an unverified release succeeded, or publish a stale help page.
5. **Verify the result.** Confirm the workflow completed and the release has its setup,
   portable, and update-feed assets. Report the version, checks, release URL, and any
   remaining blocker. If authentication is unavailable, state what is ready locally
   and what still needs publishing; never retrieve or expose credentials.

## Gotchas

- **A skill cannot prove it was invoked in CI.** Repository instructions require it;
  the release workflow independently blocks notes with a stale or mismatched version.
- **The help belongs to the installed version.** Do not fetch a live "latest" page,
  require a browser, or leave notes outside the WPF resources that ship with the app.
- **History is append-only.** Add each new release above the older notes. A version
  bump changes the latest heading and document tag, not previous entries.
- **The title-area button belongs to DockWindow**, the visible main window.
  MainWindow hosts the settings panel; putting the button there alone hides it with Settings.
- **Uncommitted work is not automatically release scope.** Review it before staging;
  never reset user changes or silently include unrelated files.
- **The release tag triggers public publication.** Finish all checks before pushing it.
