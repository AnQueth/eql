<#
.SYNOPSIS
Validates the bundled What's New page against the app and stable release version.
.EXAMPLE
.\.github\skills\prepare-release\scripts\Test-ReleaseNotes.ps1 -Version 1.0.12
#>
[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$projectPath = Join-Path $root 'src\EqLog.Overlay\EqLog.Overlay.csproj'
$notesPath = Join-Path $root 'src\EqLog.Overlay\Help\WhatsNew.xaml'

Write-Host "Phase 1/2: Reading app version from $projectPath"
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$appVersion = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $appVersion
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Release version '$Version' must be a stable version such as 1.0.12."
}
if ($appVersion -cne $Version) {
    throw "App version '$appVersion' does not match release version '$Version'."
}

Write-Host "Phase 2/2: Checking What's New $Version at $notesPath"
[xml]$notes = Get-Content -LiteralPath $notesPath -Raw
$namespaces = [System.Xml.XmlNamespaceManager]::new($notes.NameTable)
$namespaces.AddNamespace('wpf', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$document = $notes.SelectSingleNode('/wpf:FlowDocument', $namespaces)
if ($null -eq $document -or $document.GetAttribute('Tag') -cne $Version) {
    throw "What's New must be a FlowDocument with Tag='$Version'. Update the help page before releasing."
}
$heading = $document.SelectSingleNode('wpf:Paragraph[1]', $namespaces)
if ($null -eq $heading -or $heading.InnerText.Trim() -cne "What's New in $Version") {
    throw "What's New must have the visible heading: What's New in $Version."
}
$sections = $document.SelectNodes('wpf:Section', $namespaces)
if ($sections.Count -eq 0) {
    throw "What's New needs user-facing sections explaining the release."
}
foreach ($section in $sections) {
    if ($section.SelectNodes('wpf:Paragraph | wpf:List/wpf:ListItem/wpf:Paragraph', $namespaces).Count -lt 2) {
        throw "Each What's New section needs a heading and help content."
    }
}
if ($document.InnerText -match '\b(TODO|TBD|PLACEHOLDER)\b') {
    throw "What's New still contains placeholder text."
}

Write-Host "Completed: processed 1 page; succeeded 1; failed 0; skipped 0. What's New matches release $Version."
