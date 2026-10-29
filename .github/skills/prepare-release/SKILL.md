---
name: prepare-release
description: 'Mandatory EqLog Overlay pre-release workflow. Use before every release, version bump, release tag, installer publication, or request to make, publish, ship, or resume a release. Write the bundled What''s New help page, validate it against the release version, and verify the title-area button before publishing.'
license: Apache-2.0
---

# Prepare an EqLog Overlay release

Run this skill **before every release**, including resumed releases. The What's New
page is release help, not a commit log: explain what players can do and where to find it.

## Workflow

1. **Establish the release scope.** Inspect the worktree, latest stable tag, and changes
   since that tag, including intended untracked files. Preserve unrelated work. Use the
   agreed release version, or the next patch version when none is specified and the
   scope is clear. Update `Version` in the
   [app project](../../../src/EqLog.Overlay/EqLog.Overlay.csproj).
2. **Write the help page.** Update the bundled
   [What's New document](../../../src/EqLog.Overlay/Help/WhatsNew.xaml).
   Set its `Tag` and visible heading to the release version. Summarize actual changes
   since the previous release, with short, plain-English sections describing benefits,
   exact UI paths, how to try the features, and important limitations. Cover fixes and
   intentional behavior changes. Do not invent features or copy raw commit messages.
   Keep the native FlowDocument format and dynamic theme resources; the page must work
   offline. Update directly related README help when needed.
3. **Validate before publishing.** Run the
   [release-notes check](./scripts/Test-ReleaseNotes.ps1) from the repository root:
   `& .\.github\skills\prepare-release\scripts\Test-ReleaseNotes.ps1 -Version <version>`.
   Follow the build, test, self-contained publish, installer packaging, install-hook,
   and update-smoke checks in the [release workflow](../../workflows/release.yml).
   In the built app, open **What's New** from the main window's top bar with Settings
   hidden. Confirm the correct version and readable, scrollable content, in dark and
   light themes. Check reopening, Close, and Escape. Verify the bundled document in the
   published build, not just its source. Stop on any failure and fix it before tagging.
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
- **The title-area button belongs to DockWindow**, the visible main window.
  MainWindow hosts the settings panel; putting the button there alone hides it with Settings.
- **Uncommitted work is not automatically release scope.** Review it before staging;
  never reset user changes or silently include unrelated files.
- **The release tag triggers public publication.** Finish all checks before pushing it.
