[CmdletBinding()]
param(
    [Parameter()]
    [string]$DoorSweepPath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\door_transition_sweep.live.json",

    [Parameter()]
    [string]$OpenSweepPath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\transition_sweep.live.json",

    [Parameter()]
    [string]$GraphPath = ".\artifacts\navigation\navigation_graph.generated.json",

    [Parameter()]
    [string]$LocalMapsPath = ".\artifacts\navigation\local_navigation_maps.generated.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\live_navigation_audit.live.json",

    [Parameter()]
    [string]$SummaryPath = ".\artifacts\navigation\live_navigation_audit.summary.txt"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "SweepReportTools.ps1")

function Test-PointInBounds2D {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Point,

        [Parameter()]
        [AllowNull()]
        [object]$Bounds
    )

    if ($null -eq $Point -or $null -eq $Bounds) {
        return [pscustomobject]@{
            Inside = $false
            Margin = $null
            Axis = $null
        }
    }

    $x = Convert-ToInvariantDouble $Point.x
    $z = Convert-ToInvariantDouble $Point.z
    $minX = Convert-ToInvariantDouble $Bounds.MinX
    $maxX = Convert-ToInvariantDouble $Bounds.MaxX
    $minZ = Convert-ToInvariantDouble $Bounds.MinZ
    $maxZ = Convert-ToInvariantDouble $Bounds.MaxZ

    $inside = $x -ge $minX -and $x -le $maxX -and $z -ge $minZ -and $z -le $maxZ
    if ($inside) {
        return [pscustomobject]@{
            Inside = $true
            Margin = 0.0
            Axis = $null
        }
    }

    $marginX = 0.0
    if ($x -lt $minX) {
        $marginX = $minX - $x
    }
    elseif ($x -gt $maxX) {
        $marginX = $x - $maxX
    }

    $marginZ = 0.0
    if ($z -lt $minZ) {
        $marginZ = $minZ - $z
    }
    elseif ($z -gt $maxZ) {
        $marginZ = $z - $maxZ
    }

    $axis = "x"
    $margin = $marginX
    if ($marginZ -gt $marginX) {
        $axis = "z"
        $margin = $marginZ
    }

    return [pscustomobject]@{
        Inside = $false
        Margin = [Math]::Round($margin, 3)
        Axis = $axis
    }
}

function Format-Point {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Point
    )

    if ($null -eq $Point) {
        return $null
    }

    return "({0:N2}, {1:N2}, {2:N2})" -f `
        (Convert-ToInvariantDouble $Point.x),
        (Convert-ToInvariantDouble $Point.y),
        (Convert-ToInvariantDouble $Point.z)
}

function Test-ZeroVector {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Point
    )

    if ($null -eq $Point) {
        return $true
    }

    return [Math]::Abs((Convert-ToInvariantDouble $Point.x)) -lt 0.0001 -and
        [Math]::Abs((Convert-ToInvariantDouble $Point.y)) -lt 0.0001 -and
        [Math]::Abs((Convert-ToInvariantDouble $Point.z)) -lt 0.0001
}

function Add-EdgeToAdjacency {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Adjacency,

        [Parameter(Mandatory = $true)]
        [string]$FromZone,

        [Parameter(Mandatory = $true)]
        [string]$ToZone
    )

    if (-not $Adjacency.ContainsKey($FromZone)) {
        $Adjacency[$FromZone] = New-Object System.Collections.Generic.HashSet[string]
    }

    $null = $Adjacency[$FromZone].Add($ToZone)
}

function New-DirectedAdjacency {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Entries
    )

    $adjacency = @{}
    foreach ($entry in @($Entries)) {
        $fromZone = [string]$entry.FromZone
        $toZone = [string]$entry.ToZone
        if ([string]::IsNullOrWhiteSpace($fromZone) -or [string]::IsNullOrWhiteSpace($toZone)) {
            continue
        }

        Add-EdgeToAdjacency -Adjacency $adjacency -FromZone $fromZone -ToZone $toZone
    }

    return $adjacency
}

function New-UndirectedAdjacency {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Entries
    )

    $adjacency = @{}
    foreach ($entry in @($Entries)) {
        $fromZone = [string]$entry.FromZone
        $toZone = [string]$entry.ToZone
        if ([string]::IsNullOrWhiteSpace($fromZone) -or [string]::IsNullOrWhiteSpace($toZone)) {
            continue
        }

        Add-EdgeToAdjacency -Adjacency $adjacency -FromZone $fromZone -ToZone $toZone
        Add-EdgeToAdjacency -Adjacency $adjacency -FromZone $toZone -ToZone $fromZone
    }

    return $adjacency
}

function Get-ReachableZones {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Adjacency,

        [Parameter(Mandatory = $true)]
        [string]$StartZone
    )

    $visited = New-Object System.Collections.Generic.HashSet[string]
    $queue = New-Object System.Collections.Generic.Queue[string]
    $queue.Enqueue($StartZone)
    $null = $visited.Add($StartZone)

    while ($queue.Count -gt 0) {
        $zone = $queue.Dequeue()
        if (-not $Adjacency.ContainsKey($zone)) {
            continue
        }

        foreach ($neighbor in $Adjacency[$zone]) {
            if ($visited.Add($neighbor)) {
                $queue.Enqueue($neighbor)
            }
        }
    }

    return $visited
}

function Get-OrderedReachabilityCount {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Zones,

        [Parameter(Mandatory = $true)]
        [hashtable]$Adjacency
    )

    $count = 0
    foreach ($zone in $Zones) {
        $reachable = Get-ReachableZones -Adjacency $Adjacency -StartZone $zone
        $count += [Math]::Max(0, $reachable.Count - 1)
    }

    return $count
}

function Get-WeakComponentCount {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object[]]$Zones,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Adjacency
    )

    $visited = @{}
    $count = 0
    foreach ($rawZone in @($Zones)) {
        $startZone = [string]$rawZone
        if ([string]::IsNullOrWhiteSpace($startZone) -or $visited.ContainsKey($startZone)) {
            continue
        }

        $componentSet = Get-ReachableZones -Adjacency ([hashtable]$Adjacency) -StartZone $startZone
        foreach ($zone in $componentSet) {
            $visited[[string]$zone] = $true
        }
        $count++
    }

    return $count
}

function Get-CoverageMismatchRecords {
    param(
        [Parameter(Mandatory = $true)]
        $Entry,

        [Parameter(Mandatory = $true)]
        $LocalMapByZone,

        [Parameter()]
        [AllowNull()]
        [object]$TransitionMetadata
    )

    $records = @()
    $sourceZone = [string]$Entry.FromZone
    $destinationZone = [string]$Entry.ToZone
    $sourceMap = $LocalMapByZone[$sourceZone]
    $destinationMap = $LocalMapByZone[$destinationZone]
    $sourceBounds = $sourceMap ? $sourceMap.Bounds2D : $null
    $destinationBounds = $destinationMap ? $destinationMap.Bounds2D : $null

    $pointSpecs = @(
        @{ Name = "FromWaypoint"; Zone = $sourceZone; Bounds = $sourceBounds; Point = $Entry.FromWaypoint; Side = "source" },
        @{ Name = "SourceApproachPoint"; Zone = $sourceZone; Bounds = $sourceBounds; Point = $Entry.SourceApproachPoint; Side = "source" },
        @{ Name = "SourceClearPoint"; Zone = $sourceZone; Bounds = $sourceBounds; Point = $Entry.SourceClearPoint; Side = "source" },
        @{ Name = "FromCrossingAnchor"; Zone = $sourceZone; Bounds = $sourceBounds; Point = $Entry.FromCrossingAnchor; Side = "source" },
        @{ Name = "LastTargetPosition"; Zone = $sourceZone; Bounds = $sourceBounds; Point = $Entry.LastTargetPosition; Side = "source-active" },
        @{ Name = "ToCrossingAnchor"; Zone = $destinationZone; Bounds = $destinationBounds; Point = $Entry.ToCrossingAnchor; Side = "destination" },
        @{ Name = "DestinationClearPoint"; Zone = $destinationZone; Bounds = $destinationBounds; Point = $Entry.DestinationClearPoint; Side = "destination" },
        @{ Name = "DestinationApproachPoint"; Zone = $destinationZone; Bounds = $destinationBounds; Point = $Entry.DestinationApproachPoint; Side = "destination" },
        @{ Name = "ToWaypoint"; Zone = $destinationZone; Bounds = $destinationBounds; Point = $Entry.ToWaypoint; Side = "destination" }
    )

    foreach ($spec in $pointSpecs) {
        if ($null -eq $spec.Point -or $null -eq $spec.Bounds -or (Test-ZeroVector -Point $spec.Point)) {
            continue
        }

        $test = Test-PointInBounds2D -Point $spec.Point -Bounds $spec.Bounds
        if ($test.Inside -or ($test.Margin ?? 0.0) -lt 0.05) {
            continue
        }

        $records += [pscustomobject]@{
            PointName = $spec.Name
            Zone = $spec.Zone
            Side = $spec.Side
            Axis = $test.Axis
            Margin = $test.Margin
            Point = [pscustomobject]$spec.Point
            Text = ("{0} outside {1} bounds by {2:N2} on {3}: {4}" -f `
                    $spec.Name,
                    $spec.Zone,
                    $test.Margin,
                    $test.Axis,
                    (Format-Point $spec.Point))
        }
    }

    if ($null -ne $TransitionMetadata) {
        $sourceSceneZoneName = [string](Get-JsonPropertyValue -InputObject $TransitionMetadata -Name "SourceSceneZoneName" -Default "")
        if (-not [string]::IsNullOrWhiteSpace($sourceSceneZoneName) -and $sourceMap -and @($sourceMap.SceneZoneNames) -notcontains $sourceSceneZoneName) {
            $records += [pscustomobject]@{
                PointName = "SourceSceneZoneName"
                Zone = $sourceZone
                Side = "source"
                Axis = $null
                Margin = $null
                Point = $null
                Text = ("Source scene zone {0} not present in local map family {1}" -f $sourceSceneZoneName, $sourceZone)
            }
        }

        $destinationSceneZoneName = [string](Get-JsonPropertyValue -InputObject $TransitionMetadata -Name "DestinationSceneZoneName" -Default "")
        if (-not [string]::IsNullOrWhiteSpace($destinationSceneZoneName) -and $destinationMap -and @($destinationMap.SceneZoneNames) -notcontains $destinationSceneZoneName) {
            $records += [pscustomobject]@{
                PointName = "DestinationSceneZoneName"
                Zone = $destinationZone
                Side = "destination"
                Axis = $null
                Margin = $null
                Point = $null
                Text = ("Destination scene zone {0} not present in local map family {1}" -f $destinationSceneZoneName, $destinationZone)
            }
        }
    }

    return @($records)
}

function Get-FailureFamily {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Entry,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$CoverageMismatches,

        [Parameter()]
        [AllowNull()]
        [object]$TransitionMetadata,

        [Parameter(Mandatory = $true)]
        [bool]$AlternativePathExists
    )

    $detail = [string]($Entry.FailureReason ?? $Entry.StatusDetail ?? "")
    $detailLower = $detail.ToLowerInvariant()
    $assetDerivationSource = ($null -ne $TransitionMetadata) ? [string](Get-JsonPropertyValue -InputObject $TransitionMetadata -Name "AssetDerivationSource" -Default "") : ""

    $sourceCoverage = @($CoverageMismatches | Where-Object { $_.Side -like "source*" })
    $destinationCoverage = @($CoverageMismatches | Where-Object { $_.Side -eq "destination" })
    $sceneFamilyMismatch = @($CoverageMismatches | Where-Object { $_.PointName -like "*SceneZoneName" })

    if ($sceneFamilyMismatch.Count -gt 0) {
        return [pscustomobject]@{
            Label = "zone assignment mismatch"
            Confidence = "high"
            Reason = $sceneFamilyMismatch[0].Text
        }
    }

    if ($sourceCoverage.Count -gt 0 -and ($detailLower.Contains("no-source-bridge") -or $detailLower.Contains("targetkind=entrywaypoint") -or $detailLower.Contains("local-goal-unresolved"))) {
        $largest = $sourceCoverage | Sort-Object Margin -Descending | Select-Object -First 1
        return [pscustomobject]@{
            Label = "coverage gap"
            Confidence = (($largest.Margin ?? 0.0) -ge 1.0) ? "high" : "medium"
            Reason = $largest.Text
        }
    }

    if ($destinationCoverage.Count -gt 0 -and -not $AlternativePathExists) {
        $largest = $destinationCoverage | Sort-Object Margin -Descending | Select-Object -First 1
        return [pscustomobject]@{
            Label = "coverage gap"
            Confidence = (($largest.Margin ?? 0.0) -ge 1.0) ? "high" : "medium"
            Reason = $largest.Text
        }
    }

    if ($detailLower.Contains("loop") -or $detailLower.Contains("exhausted") -or $detailLower.Contains("no progress")) {
        return [pscustomobject]@{
            Label = "runtime loop/no progress"
            Confidence = $AlternativePathExists ? "medium" : "high"
            Reason = (-not [string]::IsNullOrWhiteSpace($assetDerivationSource)) ? "Failure loops after $assetDerivationSource handling path." : "Failure loops without completing traversal."
        }
    }

    if ($sourceCoverage.Count -gt 0 -or $destinationCoverage.Count -gt 0) {
        $largest = @($CoverageMismatches | Sort-Object Margin -Descending | Select-Object -First 1)
        return [pscustomobject]@{
            Label = "coverage gap"
            Confidence = "medium"
            Reason = $largest[0].Text
        }
    }

    return [pscustomobject]@{
        Label = "unknown"
        Confidence = "low"
        Reason = "No strong coverage mismatch; inspect runtime handling path."
    }
}

function Get-ConfidenceRank {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Confidence
    )

    switch ($Confidence.ToLowerInvariant()) {
        "high" { return 3 }
        "medium" { return 2 }
        "low" { return 1 }
        default { return 0 }
    }
}

if (-not (Test-Path -LiteralPath $DoorSweepPath)) {
    throw "Door sweep report not found: $DoorSweepPath"
}

if (-not (Test-Path -LiteralPath $OpenSweepPath)) {
    throw "Open-passage sweep report not found: $OpenSweepPath"
}

if (-not (Test-Path -LiteralPath $GraphPath)) {
    throw "Navigation graph not found: $GraphPath"
}

if (-not (Test-Path -LiteralPath $LocalMapsPath)) {
    throw "Local navigation maps not found: $LocalMapsPath"
}

$doorReport = Read-JsonDocument -Path $DoorSweepPath
$openReport = Read-JsonDocument -Path $OpenSweepPath
$graphDocument = Read-JsonDocument -Path $GraphPath
$localMapsDocument = Read-JsonDocument -Path $LocalMapsPath

if ($null -eq $doorReport -or $null -eq $openReport -or $null -eq $graphDocument -or $null -eq $localMapsDocument) {
    throw "One or more audit inputs were empty or invalid JSON."
}

$doorEntries = @(Get-OrderedSweepEntries -Entries @(Get-JsonPropertyValue -InputObject $doorReport -Name "Entries" -Default @()))
$openEntries = @(Get-OrderedSweepEntries -Entries @(Get-JsonPropertyValue -InputObject $openReport -Name "Entries" -Default @()))
$allEntries = @($doorEntries + $openEntries)

$failedEntries = @($allEntries | Where-Object { $_.Status -ieq "failed" })
$passedEntries = @($allEntries | Where-Object { $_.Status -ieq "passed" })

$zoneSet = New-Object System.Collections.Generic.HashSet[string]
foreach ($entry in $allEntries) {
    if (-not [string]::IsNullOrWhiteSpace([string]$entry.FromZone)) {
        $null = $zoneSet.Add([string]$entry.FromZone)
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$entry.ToZone)) {
        $null = $zoneSet.Add([string]$entry.ToZone)
    }
}

$allZones = [string[]]@($zoneSet | Sort-Object)
$passedAdjacency = New-DirectedAdjacency -Entries $passedEntries
$passedUndirectedAdjacency = New-UndirectedAdjacency -Entries $passedEntries
$baselineOrderedReachability = Get-OrderedReachabilityCount -Zones $allZones -Adjacency $passedAdjacency
$baselineWeakComponentCount = Get-WeakComponentCount -Zones $allZones -Adjacency $passedUndirectedAdjacency

$transitionMetadataById = @{}
foreach ($transition in @(Get-JsonPropertyValue -InputObject $graphDocument -Name "Transitions" -Default @())) {
    $id = [string](Get-JsonPropertyValue -InputObject $transition -Name "Id" -Default "")
    if (-not [string]::IsNullOrWhiteSpace($id)) {
        $transitionMetadataById[$id] = $transition
    }
}

$localMapByZone = @{}
foreach ($zoneRecord in @(Get-JsonPropertyValue -InputObject $localMapsDocument -Name "Zones" -Default @())) {
    $zoneName = [string](Get-JsonPropertyValue -InputObject $zoneRecord -Name "Zone" -Default "")
    if (-not [string]::IsNullOrWhiteSpace($zoneName)) {
        $localMapByZone[$zoneName] = $zoneRecord
    }
}

$rankedFailures = New-Object System.Collections.Generic.List[object]
foreach ($entry in $failedEntries) {
    $transitionMetadata = $transitionMetadataById[[string]$entry.Key]
    $coverageMismatches = @(Get-CoverageMismatchRecords -Entry $entry -LocalMapByZone $localMapByZone -TransitionMetadata $transitionMetadata)

    $reachableBefore = Get-ReachableZones -Adjacency $passedAdjacency -StartZone ([string]$entry.FromZone)
    $alternativePathExists = $reachableBefore.Contains([string]$entry.ToZone)

    $withEdgeEntries = @($passedEntries + $entry)
    $withEdgeAdjacency = New-DirectedAdjacency -Entries $withEdgeEntries
    $withEdgeUndirectedAdjacency = New-UndirectedAdjacency -Entries $withEdgeEntries
    $orderedReachabilityAfter = Get-OrderedReachabilityCount -Zones $allZones -Adjacency $withEdgeAdjacency
    $orderedReachabilityDelta = $orderedReachabilityAfter - $baselineOrderedReachability

    $beforeSourceReachable = Get-ReachableZones -Adjacency $passedAdjacency -StartZone ([string]$entry.FromZone)
    $afterSourceReachable = Get-ReachableZones -Adjacency $withEdgeAdjacency -StartZone ([string]$entry.FromZone)
    $newSourceReachableZones = @($afterSourceReachable | Where-Object { -not $beforeSourceReachable.Contains($_) } | Sort-Object)

    $undirectedReachableBefore = Get-ReachableZones -Adjacency $passedUndirectedAdjacency -StartZone ([string]$entry.FromZone)
    $componentDelta = $undirectedReachableBefore.Contains([string]$entry.ToZone) ? 0 : 1

    $failureFamily = Get-FailureFamily `
        -Entry $entry `
        -CoverageMismatches $coverageMismatches `
        -TransitionMetadata $transitionMetadata `
        -AlternativePathExists $alternativePathExists

    $impactTier = "low"
    if ($componentDelta -gt 0 -or $orderedReachabilityDelta -ge 25) {
        $impactTier = "hard-blocker"
    }
    elseif (-not $alternativePathExists -or $orderedReachabilityDelta -gt 0) {
        $impactTier = "likely-blocker"
    }

    $assetDerivationSource = ($null -ne $transitionMetadata) ? [string](Get-JsonPropertyValue -InputObject $transitionMetadata -Name "AssetDerivationSource" -Default "") : ""

    $staticIssues = @()
    if ($null -ne $transitionMetadata) {
        $staticIssues = @(
            @([string[]](Get-JsonPropertyValue -InputObject $transitionMetadata -Name "StaticIssues" -Default @())) |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
        )
    }

    $rankedFailures.Add([pscustomobject]@{
        Key = [string]$entry.Key
        StepKind = [string]$entry.StepKind
        FromZone = [string]$entry.FromZone
        ToZone = [string]$entry.ToZone
        StatusDetail = [string]$entry.StatusDetail
        FailureReason = [string]$entry.FailureReason
        ImpactTier = $impactTier
        AlternativePassedPathExists = $alternativePathExists
        OrderedReachabilityDelta = $orderedReachabilityDelta
        UndirectedComponentDelta = $componentDelta
        NewReachableFromSource = $newSourceReachableZones
        LastTargetKind = [string]$entry.LastTargetKind
        LastLocalNavigationContext = [string]$entry.LastLocalNavigationContext
        CurrentZoneAtResult = [string]$entry.CurrentZoneAtResult
        AssetDerivationSource = $assetDerivationSource
        StaticIssues = $staticIssues
        SuspectedFailureFamily = $failureFamily.Label
        Confidence = $failureFamily.Confidence
        FailureFamilyReason = $failureFamily.Reason
        CoverageMismatchCount = $coverageMismatches.Count
        CoverageMismatches = $coverageMismatches
    })
}

$impactRankMap = @{
    "hard-blocker" = 3
    "likely-blocker" = 2
    "low" = 1
}

$sortedFailures = @(
    $rankedFailures |
        Sort-Object `
            @{ Expression = { $impactRankMap[$_.ImpactTier] } ; Descending = $true },
            @{ Expression = { $_.OrderedReachabilityDelta } ; Descending = $true },
            @{ Expression = { $_.UndirectedComponentDelta } ; Descending = $true },
            @{ Expression = { Get-ConfidenceRank $_.Confidence } ; Descending = $true },
            @{ Expression = { $_.CoverageMismatchCount } ; Descending = $true },
            @{ Expression = { $_.Key } ; Descending = $false }
)

$topFailure = $sortedFailures | Select-Object -First 1
$recommendation = $null
if ($null -ne $topFailure) {
    $recommendedAction = "Inspect data source and runtime handling path side-by-side; root cause remains unclear."
    if ($topFailure.SuspectedFailureFamily -in @("coverage gap", "zone assignment mismatch")) {
        $recommendedAction = "Inspect and fix navigation data source or zone-family coverage before runtime heuristics."
    }
    elseif ($topFailure.SuspectedFailureFamily -eq "runtime loop/no progress") {
        $recommendedAction = "Inspect runtime traversal logic first; current data does not show a dominant coverage defect."
    }

    $recommendation = [pscustomobject]@{
        TargetKey = $topFailure.Key
        TargetKind = $topFailure.StepKind
        Recommendation = $recommendedAction
        Reason = ("{0}; impact={1}; orderedReachabilityDelta={2}; derivation={3}" -f `
                $topFailure.FailureFamilyReason,
                $topFailure.ImpactTier,
                $topFailure.OrderedReachabilityDelta,
                ([string]::IsNullOrWhiteSpace($topFailure.AssetDerivationSource) ? "<unknown>" : $topFailure.AssetDerivationSource))
    }
}

$auditDocument = [ordered]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("o")
    Inputs = [ordered]@{
        DoorSweepPath = (Resolve-Path -LiteralPath $DoorSweepPath).Path
        OpenSweepPath = (Resolve-Path -LiteralPath $OpenSweepPath).Path
        GraphPath = (Resolve-Path -LiteralPath $GraphPath).Path
        LocalMapsPath = (Resolve-Path -LiteralPath $LocalMapsPath).Path
    }
    Runtime = [ordered]@{
        DoorBuildStamp = [string](Get-JsonPropertyValue -InputObject $doorReport -Name "RuntimeBuildStamp" -Default "")
        OpenBuildStamp = [string](Get-JsonPropertyValue -InputObject $openReport -Name "RuntimeBuildStamp" -Default "")
        DoorGeneratedAtUtc = [string](Get-JsonPropertyValue -InputObject $doorReport -Name "GeneratedAtUtc" -Default "")
        OpenGeneratedAtUtc = [string](Get-JsonPropertyValue -InputObject $openReport -Name "GeneratedAtUtc" -Default "")
    }
    Summary = [ordered]@{
        TotalZones = $allZones.Count
        TotalEdges = $allEntries.Count
        PassedEdges = $passedEntries.Count
        FailedEdges = $failedEntries.Count
        DoorFailedEdges = @($failedEntries | Where-Object { $_.StepKind -ieq "Door" }).Count
        OpenFailedEdges = @($failedEntries | Where-Object { $_.StepKind -ieq "OpenPassage" }).Count
        BaselineOrderedReachablePairs = $baselineOrderedReachability
        WeaklyConnectedComponentCount = $baselineWeakComponentCount
    }
    Recommendation = $recommendation
    FailedEdges = $sortedFailures
}

$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add("Live navigation audit")
$summaryLines.Add("Door build: $([string](Get-JsonPropertyValue -InputObject $doorReport -Name 'RuntimeBuildStamp' -Default ''))")
$summaryLines.Add("Open build: $([string](Get-JsonPropertyValue -InputObject $openReport -Name 'RuntimeBuildStamp' -Default ''))")
$summaryLines.Add("Zones=$($allZones.Count) edges=$($allEntries.Count) passed=$($passedEntries.Count) failed=$($failedEntries.Count) weakComponents=$baselineWeakComponentCount orderedReachablePairs=$baselineOrderedReachability")
$summaryLines.Add(" ")

if ($null -ne $recommendation) {
    $summaryLines.Add("Recommended next target: $($recommendation.TargetKey)")
    $summaryLines.Add($recommendation.Recommendation)
    $summaryLines.Add("Why: $($recommendation.Reason)")
    $summaryLines.Add(" ")
}

foreach ($failure in $sortedFailures) {
    $summaryLines.Add(("{0} [{1}] impact={2} altPath={3} pairDelta={4} compDelta={5}" -f `
        $failure.Key,
        $failure.StepKind,
        $failure.ImpactTier,
        $failure.AlternativePassedPathExists,
        $failure.OrderedReachabilityDelta,
        $failure.UndirectedComponentDelta))
    $summaryLines.Add(("  family={0} confidence={1} derivation={2}" -f `
        $failure.SuspectedFailureFamily,
        $failure.Confidence,
        $(([string]::IsNullOrWhiteSpace($failure.AssetDerivationSource) ? "<unknown>" : $failure.AssetDerivationSource))))
    $summaryLines.Add("  reason=$($failure.FailureFamilyReason)")
    if ($failure.NewReachableFromSource.Count -gt 0) {
        $summaryLines.Add("  restores source reachability to: $([string]::Join(', ', $failure.NewReachableFromSource))")
    }

    foreach ($mismatch in @($failure.CoverageMismatches | Select-Object -First 3)) {
        $summaryLines.Add("  coverage: $($mismatch.Text)")
    }

    $summaryLines.Add(" ")
}

Write-JsonDocument -Path $OutputPath -Document $auditDocument
Write-TextDocument -Path $SummaryPath -Lines ([string[]]$summaryLines.ToArray())

Write-Host "Wrote live navigation audit JSON to $OutputPath"
Write-Host "Wrote live navigation audit summary to $SummaryPath"
if ($null -ne $recommendation) {
    Write-Host "Recommended next target: $($recommendation.TargetKey)"
}
