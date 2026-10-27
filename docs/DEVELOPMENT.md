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
