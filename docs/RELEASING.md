# Release checklist

1. Load and follow the [prepare-release skill](../.github/skills/prepare-release/SKILL.md),
   including when resuming a release.
2. Set the app version and prepend its entry to the bundled
   [What's New history](../src/EqLog.Overlay/Help/WhatsNew.xaml).
   Preserve older entries. Describe shipped features and exact UI paths; align
   the latest heading/document tag, app version and intended `v<version>` tag.
3. After finalizing the notes, run the validator once:

   ```powershell
   & .\.github\skills\prepare-release\scripts\Test-ReleaseNotes.ps1 -Version <version>
   ```

   Rerun only if it fails or notes/version change. No extra What's New UI smoke checks.
4. Complete Release build/tests and packaging, install-hook and update validation
   from the [release workflow](../.github/workflows/release.yml).
   Use a disposable account/VM for a real install A -> update B -> restart test;
   verify retained data, cancellation, offline behavior and save failures.
5. With permission, commit/push and create a **new, higher stable** `v<version>` tag.
   Never replace published tags or assets.

## Publication

The tag-triggered workflow tests, publishes self-contained Windows x64 binaries,
packages with the [pinned Velopack tool](../.config/dotnet-tools.json), validates
the local feed, and publishes the GitHub release. Actions needs `contents: write`.
Keep installer, portable ZIP, full package and feed metadata together.
Finish uploading an incomplete draft before publishing, or delete it and rerun.

Downloads are public and unauthenticated; never embed access tokens.
Installers are unsigned; signing credentials belong in protected CI configuration.
Updates require user-confirmed restart and successful saves. Development builds
and attached debugging sessions do not update.

[User guide](../README.md) | [Development](DEVELOPMENT.md)
