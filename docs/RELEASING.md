# Packaging and publishing releases

See the [user guide](../README.md#installation) for installing a published release.

## GitHub Actions

The tag-triggered [release workflow](../.github/workflows/release.yml) uses Windows and .NET 10
to run tests, publish self-contained `win-x64` binaries (including bundled themes), and package
them with the pinned Velopack tool in [.config/dotnet-tools.json](../.config/dotnet-tools.json).
It validates the install hook and checks update discovery, package download, and no-update/downgrade
behavior against the generated local feed before publishing. Full packages are used; delta packages
are not generated.

Commit and push changes first, then push a new stable semantic-version tag. For example,
if `v1.0.2` has not already been published:

```powershell
git tag v1.0.2
git push origin v1.0.2
```

The tag sets the published application version. Use a higher version for every release; do not
replace existing tags or published assets. Keep the development project version current too.

Actions must be enabled and allowed `contents: write`. The workflow uses its built-in
`GITHUB_TOKEN`, not an embedded application token. It creates a draft, uploads installer, portable
ZIP, full package, and feed metadata, then publishes as latest. Never remove the feed/package assets.
If uploading fails after creating the draft, finish uploading all assets before publishing it,
or delete that incomplete draft and rerun the workflow.

## Update integration

The client uses public, unauthenticated GitHub downloads from `AnQueth/eql`; private distribution
requires a separate authentication design. Never embed a personal access token in the shipped app.
GitHub API rate limits and offline failures appear in update status.

Velopack initialization runs before WPF startup so installation/update hooks exit without starting
the controller or log reader. Automatic application of updates at startup is disabled; the user
confirms restart. Before handing off, the app performs strict saves, stops tailing, and saves again.
Development/unpackaged builds and attached debugging sessions are excluded.

Installers are currently unsigned. Configure code signing through Velopack's packaging step
using protected CI credentials before distributing broadly. Signing does not guarantee immediate
SmartScreen reputation.

## Local release validation

Run from the repository root, using an unused version:

```powershell
dotnet tool restore
dotnet test tests\EqLog.Core.Tests\EqLog.Core.Tests.csproj -c Release
dotnet publish src\EqLog.Overlay\EqLog.Overlay.csproj -c Release -r win-x64 --self-contained true -p:Version=1.0.2 -o artifacts\publish
dotnet tool run vpk -- pack --packId EqLogOverlay --packVersion 1.0.2 --packDir artifacts\publish --mainExe EqLogOverlay.exe --packTitle "EverQuest Log Overlay" --icon src\EqLog.Overlay\AppIcon.ico --runtime win-x64 --channel win --delta None --outputDir artifacts\releases
dotnet run --project tests\EqLog.UpdateSmoke -- artifacts\releases 1.0.1 1.0.2
```

The smoke check uses an isolated simulated installation and never accesses live overlay settings.
It validates discovery/download/staging, not a GUI restart. Before broad distribution, also exercise
two real versions in a disposable Windows account/VM: install A, save settings and triggers,
publish B, check/download, restart, and verify B and retained data. Test cancellation, offline
operation, an unwritable settings folder, and skipped updates.
