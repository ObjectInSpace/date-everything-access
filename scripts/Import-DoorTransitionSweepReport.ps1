[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourcePath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\door_transition_sweep.live.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\door_transition_sweep.live.json",

    [Parameter()]
    [string]$SummaryPath = ".\artifacts\navigation\door_transition_sweep.summary.txt"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "SweepReportTools.ps1")

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Door transition sweep report not found: $SourcePath"
}

$existingReport = Read-JsonDocument -Path $OutputPath
$importedReport = Read-JsonDocument -Path $SourcePath
if ($null -eq $importedReport) {
    throw "Door transition sweep report was empty or invalid JSON: $SourcePath"
}

$mergedReport = Merge-SweepReportDocuments -ExistingReport $existingReport -ImportedReport $importedReport -DefaultSweepKind "Door"
$statusChanges = Get-SweepStatusChangeSummary -ExistingReport $existingReport -ImportedReport $importedReport
$summaryLines = Format-SweepSummaryLines `
    -SourcePath $SourcePath `
    -OutputPath $OutputPath `
    -ImportedReport $importedReport `
    -MergedReport $mergedReport `
    -StatusChanges $statusChanges `
    -SummaryKind "Door"

Write-JsonDocument -Path $OutputPath -Document $mergedReport
Write-TextDocument -Path $SummaryPath -Lines $summaryLines

Write-Host "Imported door transition sweep report to $OutputPath"
Write-Host "Wrote door transition sweep summary to $SummaryPath"
Write-Host "Merged totals: total=$($mergedReport.TotalCount) passed=$($mergedReport.PassedCount) failed=$($mergedReport.FailedCount) pending=$($mergedReport.PendingCount)"
