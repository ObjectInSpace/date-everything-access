[CmdletBinding()]
param(
    [Parameter()]
    [string]$DoorSweepPath = ".\artifacts\navigation\door_transition_sweep.live.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\door_executor_contract.validation.json",

    [Parameter()]
    [string]$SummaryPath = ".\artifacts\navigation\door_executor_contract.validation.summary.txt",

    [Parameter()]
    [string]$ExpectedRuntimeBuildStamp,

    [Parameter()]
    [switch]$RequireFullSweep,

    [Parameter()]
    [switch]$WarnOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-ZoneEquivalent {
    param(
        [string]$CurrentZone,
        [string]$ExpectedZone
    )

    if ([string]::IsNullOrWhiteSpace($CurrentZone) -or [string]::IsNullOrWhiteSpace($ExpectedZone)) {
        return $false
    }

    if ([string]::Equals($CurrentZone, $ExpectedZone, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $escaped = [Regex]::Escape($ExpectedZone)
    return $CurrentZone -match ("(?i)^" + $escaped + "\d+$")
}

function Add-Violation {
    param(
        [System.Collections.Generic.List[object]]$Violations,
        [string]$Rule,
        $Entry,
        [string]$Detail
    )

    $Violations.Add([pscustomobject]@{
        Rule = $Rule
        Key = if ($null -ne $Entry) { $Entry.Key } else { $null }
        FromZone = if ($null -ne $Entry) { $Entry.FromZone } else { $null }
        ToZone = if ($null -ne $Entry) { $Entry.ToZone } else { $null }
        Status = if ($null -ne $Entry) { $Entry.Status } else { $null }
        CurrentZoneAtResult = if ($null -ne $Entry) { $Entry.CurrentZoneAtResult } else { $null }
        LastTargetKind = if ($null -ne $Entry) { $Entry.LastTargetKind } else { $null }
        LastLocalNavigationContext = if ($null -ne $Entry) { $Entry.LastLocalNavigationContext } else { $null }
        Detail = $Detail
    })
}

if (-not (Test-Path -LiteralPath $DoorSweepPath)) {
    throw "Door sweep not found: $DoorSweepPath"
}

$sweep = Get-Content -LiteralPath $DoorSweepPath -Raw | ConvertFrom-Json
$violations = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[object]]::new()
$runtimeBuildStamp = if ($sweep.PSObject.Properties.Match("RuntimeBuildStamp").Count -gt 0) { [string]$sweep.RuntimeBuildStamp } else { $null }

function Add-Warning {
    param(
        [System.Collections.Generic.List[object]]$Warnings,
        [string]$Rule,
        $Entry,
        [string]$Detail
    )

    $Warnings.Add([pscustomobject]@{
        Rule = $Rule
        Key = if ($null -ne $Entry) { $Entry.Key } else { $null }
        FromZone = if ($null -ne $Entry) { $Entry.FromZone } else { $null }
        ToZone = if ($null -ne $Entry) { $Entry.ToZone } else { $null }
        Status = if ($null -ne $Entry) { $Entry.Status } else { $null }
        CurrentZoneAtResult = if ($null -ne $Entry) { $Entry.CurrentZoneAtResult } else { $null }
        LastTargetKind = if ($null -ne $Entry) { $Entry.LastTargetKind } else { $null }
        LastLocalNavigationContext = if ($null -ne $Entry) { $Entry.LastLocalNavigationContext } else { $null }
        Detail = $Detail
    })
}

if ($sweep.PSObject.Properties.Match("IsComplete").Count -eq 0 -or -not [bool]$sweep.IsComplete) {
    Add-Violation $violations "SweepComplete" $null "Latest door sweep must be complete."
}

if ([string]::IsNullOrWhiteSpace($runtimeBuildStamp)) {
    Add-Violation $violations "SweepBuildStamped" $null "Latest door sweep must include RuntimeBuildStamp."
}

$isStaleBuild = $false
if (-not [string]::IsNullOrWhiteSpace($ExpectedRuntimeBuildStamp) -and
    -not [string]::IsNullOrWhiteSpace($runtimeBuildStamp) -and
    -not [string]::Equals($runtimeBuildStamp, $ExpectedRuntimeBuildStamp, [StringComparison]::Ordinal)) {
    $isStaleBuild = $true
    Add-Warning $warnings `
        "StaleArtifact" `
        $null `
        "Door sweep runtime build does not match expected build. RuntimeBuildStamp=$runtimeBuildStamp ExpectedRuntimeBuildStamp=$ExpectedRuntimeBuildStamp"
}

$entries = @($sweep.Entries)
$failedEntries = @($entries | Where-Object { $_.Status -eq "failed" })

$passingControls = @(
    "transition:bathroom1->hallway",
    "transition:bedroom->bathroom2",
    "transition:dorian_bathroom2_2->bathroom2",
    "transition:office->hallway",
    "transition:office_closet->office",
    "transition:upper_hallway->gym"
)

$isFullSweep = $entries.Count -ge 20
if ($RequireFullSweep -and -not $isFullSweep) {
    Add-Violation $violations `
        "FullSweepRequired" `
        $null `
        "Door executor contract requires a full door sweep, but artifact contains only $($entries.Count) entries."
}
elseif (-not $isFullSweep) {
    Add-Warning $warnings `
        "PartialSweep" `
        $null `
        "Artifact appears to be a focused or partial door sweep with $($entries.Count) entries; missing passing controls are warnings only."
}

foreach ($controlKey in $passingControls) {
    $control = $entries | Where-Object { $_.Key -eq $controlKey } | Select-Object -First 1
    if ($null -eq $control) {
        if ($isFullSweep -and -not $isStaleBuild) {
            Add-Violation $violations "PassingControlPresent" $null "Passing control missing: $controlKey"
        }
        else {
            Add-Warning $warnings "PassingControlPresent" $null "Passing control missing from stale or partial artifact: $controlKey"
        }
        continue
    }

    if ($control.Status -ne "passed") {
        if ($isStaleBuild) {
            Add-Warning $warnings "PassingControlStillPassed" $control "Passing control is no longer passed in stale artifact."
        }
        else {
            Add-Violation $violations "PassingControlStillPassed" $control "Passing control is no longer passed."
        }
    }
}

foreach ($entry in $failedEntries) {
    $detail = [string]$entry.StatusDetail
    $currentZone = [string]$entry.CurrentZoneAtResult
    if ([string]::IsNullOrWhiteSpace($currentZone) -and $detail -match "currentZone=([^\s]+)") {
        $currentZone = $Matches[1]
    }

    $localContext = [string]$entry.LastLocalNavigationContext
    if ([string]::IsNullOrWhiteSpace($localContext) -and $detail -match "localContext=([^\s]+)") {
        $localContext = $Matches[1]
    }

    $rawContext = $null
    if ($detail -match "rawContext=([^\s]+)") {
        $rawContext = $Matches[1]
    }

    $stage = $null
    if ($detail -match "stage=([^\s]+)") {
        $stage = $Matches[1]
    }

    $targetKind = [string]$entry.LastTargetKind
    if ([string]::IsNullOrWhiteSpace($targetKind) -and $detail -match "targetKind=([^\s]+)") {
        $targetKind = $Matches[1]
    }

    $inSourceZone = Test-ZoneEquivalent $currentZone ([string]$entry.FromZone)

    if ($inSourceZone -and
        $stage -eq "DoorEntryAdvance" -and
        $rawContext -eq "door-entry-advance" -and
        $localContext -eq "<null>" -and
        $targetKind -eq "EntryWaypoint" -and
        $detail -notmatch "bridge (was )?not constructible|no source-zone bridge") {
        Add-Violation $violations `
            "NoRawFinalEntryWaypointInSourceZone" `
            $entry `
            "Failed door ended in source zone on raw final DoorEntryAdvance EntryWaypoint without bridge-impossible diagnostic."
    }

    if ($inSourceZone -and
        $stage -eq "DoorEntryAdvance" -and
        $localContext -eq "door-entry-advance-local") {
        Add-Violation $violations `
            "NoSourceLocalEntryLoop" `
            $entry `
            "Failed door ended in source zone on door-entry-advance-local; this needs source-zone bridge proof or non-loop diagnostic."
    }

    if ($inSourceZone -and
        $stage -eq "DoorThresholdAdvance" -and
        $detail -match "local-goal-unresolved" -and
        $detail -notmatch "release evidence|next fallback|fallback target") {
        Add-Violation $violations `
            "ThresholdAdvanceNeedsFallbackOrProof" `
            $entry `
            "DoorThresholdAdvance local-goal-unresolved has no release-evidence or next-fallback diagnostic."
    }
}

$summary = [pscustomobject]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("o")
    DoorSweepPath = (Resolve-Path -LiteralPath $DoorSweepPath).Path
    RuntimeBuildStamp = $runtimeBuildStamp
    ExpectedRuntimeBuildStamp = $ExpectedRuntimeBuildStamp
    IsStaleBuild = $isStaleBuild
    IsFullSweep = $isFullSweep
    IsAuthoritative = (-not $isStaleBuild -and $isFullSweep)
    TotalCount = if ($sweep.PSObject.Properties.Match("TotalCount").Count -gt 0) { [int]$sweep.TotalCount } else { $entries.Count }
    PassedCount = if ($sweep.PSObject.Properties.Match("PassedCount").Count -gt 0) { [int]$sweep.PassedCount } else { @($entries | Where-Object { $_.Status -eq "passed" }).Count }
    FailedCount = if ($sweep.PSObject.Properties.Match("FailedCount").Count -gt 0) { [int]$sweep.FailedCount } else { $failedEntries.Count }
    ViolationCount = $violations.Count
    WarningCount = $warnings.Count
}

$report = [pscustomobject]@{
    Summary = $summary
    Violations = @($violations)
    Warnings = @($warnings)
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$summaryDirectory = Split-Path -Parent $SummaryPath
if (-not [string]::IsNullOrWhiteSpace($summaryDirectory)) {
    New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

$summaryLines = [System.Collections.Generic.List[string]]::new()
$expectedRuntimeBuildStampText = if ([string]::IsNullOrWhiteSpace($summary.ExpectedRuntimeBuildStamp)) { '<none>' } else { $summary.ExpectedRuntimeBuildStamp }
$summaryLines.Add("Door executor contract validation")
$summaryLines.Add("GeneratedAtUtc: $($summary.GeneratedAtUtc)")
$summaryLines.Add("Runtime build: $($summary.RuntimeBuildStamp)")
$summaryLines.Add("Expected runtime build: $expectedRuntimeBuildStampText")
$summaryLines.Add("Authoritative: $($summary.IsAuthoritative) staleBuild=$($summary.IsStaleBuild) fullSweep=$($summary.IsFullSweep)")
$summaryLines.Add("Door totals: total=$($summary.TotalCount) passed=$($summary.PassedCount) failed=$($summary.FailedCount)")
$summaryLines.Add("Violations: $($summary.ViolationCount)")
if ($violations.Count -gt 0) {
    $summaryLines.Add("Violation details:")
    foreach ($violation in $violations) {
        $summaryLines.Add("- $($violation.Rule): $($violation.Key) - $($violation.Detail)")
    }
}
$summaryLines.Add("Warnings: $($summary.WarningCount)")
if ($warnings.Count -gt 0) {
    $summaryLines.Add("Warning details:")
    foreach ($warning in $warnings) {
        $summaryLines.Add("- $($warning.Rule): $($warning.Key) - $($warning.Detail)")
    }
}

$summaryLines | Set-Content -LiteralPath $SummaryPath -Encoding UTF8

Write-Host "Wrote door executor contract validation to $OutputPath"
Write-Host "Wrote door executor contract summary to $SummaryPath"
Write-Host "Violations: $($violations.Count)"
Write-Host "Warnings: $($warnings.Count)"

if ($violations.Count -gt 0 -and -not $WarnOnly) {
    throw "Door executor contract validation failed with $($violations.Count) violation(s)."
}
