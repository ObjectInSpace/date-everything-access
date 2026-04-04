[CmdletBinding()]
param(
    [Parameter()]
    [string]$InputPath = ".\artifacts\navigation\navigation_graph.generated.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\transition_validation.static.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-VectorDistance {
    param(
        $Left,
        $Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return -1
    }

    $dx = [double]$Left.x - [double]$Right.x
    $dy = [double]$Left.y - [double]$Right.y
    $dz = [double]$Left.z - [double]$Right.z
    return [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
}

function Test-ZeroVector {
    param($Vector)

    if ($null -eq $Vector) {
        return $true
    }

    return [Math]::Abs([double]$Vector.x) -lt 0.0001 -and
        [Math]::Abs([double]$Vector.y) -lt 0.0001 -and
        [Math]::Abs([double]$Vector.z) -lt 0.0001
}

function Add-Issue {
    param(
        [System.Collections.Generic.List[string]]$Issues,
        [ref]$Score,
        [string]$Issue,
        [int]$Weight
    )

    $Issues.Add($Issue)
    $Score.Value += $Weight
}

if (-not (Test-Path -LiteralPath $InputPath)) {
    throw "Navigation graph not found: $InputPath"
}

$graph = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
$links = @($graph.Links)
if ($links.Count -eq 0) {
    throw "Navigation graph did not contain any links: $InputPath"
}

$reportEntries = foreach ($link in $links) {
    $issues = [System.Collections.Generic.List[string]]::new()
    $score = 0

    $fromWaypointMissing = Test-ZeroVector $link.FromWaypoint
    $toWaypointMissing = Test-ZeroVector $link.ToWaypoint
    $fromCrossMissing = Test-ZeroVector $link.FromCrossingAnchor
    $toCrossMissing = Test-ZeroVector $link.ToCrossingAnchor

    if ($fromWaypointMissing) { Add-Issue $issues ([ref]$score) "MissingFromWaypoint" 4 }
    if ($toWaypointMissing) { Add-Issue $issues ([ref]$score) "MissingToWaypoint" 4 }

    $waypointDistance = if ($fromWaypointMissing -or $toWaypointMissing) { -1 } else { Get-VectorDistance $link.FromWaypoint $link.ToWaypoint }
    $crossingDistance = if ($fromCrossMissing -or $toCrossMissing) { -1 } else { Get-VectorDistance $link.FromCrossingAnchor $link.ToCrossingAnchor }
    $heightDelta = if ($fromWaypointMissing -or $toWaypointMissing) { -1 } else { [Math]::Abs([double]$link.ToWaypoint.y - [double]$link.FromWaypoint.y) }
    $fromWaypointToCrossing = if ($fromWaypointMissing -or $fromCrossMissing) { -1 } else { Get-VectorDistance $link.FromWaypoint $link.FromCrossingAnchor }
    $toWaypointToCrossing = if ($toWaypointMissing -or $toCrossMissing) { -1 } else { Get-VectorDistance $link.ToWaypoint $link.ToCrossingAnchor }

    if ($waypointDistance -gt 18) { Add-Issue $issues ([ref]$score) "LongWaypointSpan" 2 }
    if ($heightDelta -gt 4) { Add-Issue $issues ([ref]$score) "LargeHeightDelta" 2 }

    if ($link.StepKind -eq "OpenPassage") {
        if ($fromCrossMissing) { Add-Issue $issues ([ref]$score) "MissingFromCrossingAnchor" 3 }
        if ($toCrossMissing) { Add-Issue $issues ([ref]$score) "MissingToCrossingAnchor" 3 }
        if ($crossingDistance -gt 5) { Add-Issue $issues ([ref]$score) "WideCrossingGap" 2 }
        if ($crossingDistance -ge 0 -and $crossingDistance -lt 0.25) { Add-Issue $issues ([ref]$score) "CollapsedCrossingGap" 2 }
        if ($fromWaypointToCrossing -gt 8) { Add-Issue $issues ([ref]$score) "FarFromSourceWaypoint" 1 }
        if ($toWaypointToCrossing -gt 8) { Add-Issue $issues ([ref]$score) "FarFromDestinationWaypoint" 1 }
    }

    if (($link.StepKind -eq "Door" -or $link.StepKind -eq "Teleporter") -and -not $link.RequiresInteraction) {
        Add-Issue $issues ([ref]$score) "InteractionStepWithoutInteractionFlag" 2
    }

    if ($link.StepKind -eq "Stairs" -and $heightDelta -lt 1 -and $heightDelta -ge 0) {
        Add-Issue $issues ([ref]$score) "StairsWithSmallHeightDelta" 1
    }

    [pscustomobject]@{
        Key = "{0}->{1}" -f $link.FromZone, $link.ToZone
        FromZone = $link.FromZone
        ToZone = $link.ToZone
        StepKind = $link.StepKind
        ConnectorName = $link.ConnectorName
        RequiresInteraction = [bool]$link.RequiresInteraction
        SuspicionScore = $score
        WaypointDistance = $waypointDistance
        CrossingDistance = $crossingDistance
        HeightDelta = $heightDelta
        FromWaypointToCrossingDistance = $fromWaypointToCrossing
        ToWaypointToCrossingDistance = $toWaypointToCrossing
        Issues = @($issues)
    }
}

$orderedEntries = @($reportEntries | Sort-Object -Property @{ Expression = "SuspicionScore"; Descending = $true }, @{ Expression = "FromZone"; Descending = $false }, @{ Expression = "ToZone"; Descending = $false })
$summary = [pscustomobject]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("o")
    InputPath = (Resolve-Path -LiteralPath $InputPath).Path
    TotalTransitions = $orderedEntries.Count
    SuspiciousTransitions = @($orderedEntries | Where-Object { $_.SuspicionScore -gt 0 }).Count
    HighSuspicionTransitions = @($orderedEntries | Where-Object { $_.SuspicionScore -ge 4 }).Count
}

$report = [pscustomobject]@{
    Summary = $summary
    Entries = $orderedEntries
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Wrote transition validation report to $OutputPath"
