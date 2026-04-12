[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourcePath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\transition_sweep.live.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\transition_sweep.live.json",

    [Parameter()]
    [string]$SummaryPath = ".\artifacts\navigation\transition_sweep.summary.txt",

    [Parameter()]
    [string]$OverridePath = ".\navigation_transition_overrides.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "SweepReportTools.ps1")

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Transition sweep report not found: $SourcePath"
}

$existingReport = Read-JsonDocument -Path $OutputPath
$importedReport = Read-JsonDocument -Path $SourcePath
if ($null -eq $importedReport) {
    throw "Transition sweep report was empty or invalid JSON: $SourcePath"
}

$latestReport = Merge-SweepReportDocuments -ExistingReport $null -ImportedReport $importedReport -DefaultSweepKind "OpenPassage"
$statusChanges = Get-SweepStatusChangeSummary -ExistingReport $existingReport -ImportedReport $importedReport
$overrideUpdate = Update-OpenPassageOverridesFromReport -MergedReport $latestReport -OverridePath $OverridePath
$summaryLines = Format-SweepSummaryLines `
    -SourcePath $SourcePath `
    -OutputPath $OutputPath `
    -ImportedReport $importedReport `
    -MergedReport $latestReport `
    -StatusChanges $statusChanges `
    -OverrideUpdate $overrideUpdate `
    -SummaryKind "OpenPassage"

Write-JsonDocument -Path $OutputPath -Document $latestReport
Write-TextDocument -Path $SummaryPath -Lines $summaryLines

Write-Host "Imported transition sweep report to $OutputPath"
Write-Host "Wrote transition sweep summary to $SummaryPath"
Write-Host "Updated navigation overrides at $OverridePath"
Write-Host "Latest report totals: total=$($latestReport.TotalCount) passed=$($latestReport.PassedCount) failed=$($latestReport.FailedCount) pending=$($latestReport.PendingCount)"
Write-Host "Override refresh: added=$($overrideUpdate.Added) refreshed=$($overrideUpdate.Refreshed) preservedManualWaypoints=$($overrideUpdate.PreservedManualWaypoints)"
