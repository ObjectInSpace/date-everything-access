[CmdletBinding()]
param(
    [Parameter()]
    [string]$GraphPath = ".\artifacts\navigation\navigation_graph.generated.json",

    [Parameter()]
    [string]$SceneDataPath = ".\artifacts\navigation\thirdpersongreybox-navigation-data.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\door_transition_audit.json",

    [Parameter()]
    [string]$SummaryPath = ".\artifacts\navigation\door_transition_audit.summary.txt",

    [Parameter()]
    [double]$NamedConnectorPlanarThreshold = 0.75,

    [Parameter()]
    [double]$UnnamedConnectorPlanarThreshold = 1.5,

    [Parameter()]
    [double]$HeightMismatchThreshold = 1.5,

    [Parameter()]
    [int]$TopCandidateCount = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-UniqueStringArray {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Values
    )

    $result = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($value in @($Values)) {
        if ($null -eq $value) {
            continue
        }

        $text = [string]$value
        if ([string]::IsNullOrWhiteSpace($text) -or $seen.ContainsKey($text)) {
            continue
        }

        $seen[$text] = $true
        $result.Add($text)
    }

    return $result.ToArray()
}

function Get-CanonicalPairKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Left,

        [Parameter(Mandatory = $true)]
        [string]$Right
    )

    if ([string]::Compare($Left, $Right, [System.StringComparison]::OrdinalIgnoreCase) -le 0) {
        return "$Left<->$Right"
    }

    return "$Right<->$Left"
}

function Get-PlanarDistance {
    param(
        [Parameter()]
        $Left,

        [Parameter()]
        $Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return -1.0
    }

    $dx = [double]$Left.x - [double]$Right.x
    $dz = [double]$Left.z - [double]$Right.z
    return [Math]::Sqrt(($dx * $dx) + ($dz * $dz))
}

function Get-HeightDistance {
    param(
        [Parameter()]
        $Left,

        [Parameter()]
        $Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return -1.0
    }

    return [Math]::Abs([double]$Left.y - [double]$Right.y)
}

function Get-VectorDistance {
    param(
        [Parameter()]
        $Left,

        [Parameter()]
        $Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return -1.0
    }

    $dx = [double]$Left.x - [double]$Right.x
    $dy = [double]$Left.y - [double]$Right.y
    $dz = [double]$Left.z - [double]$Right.z
    return [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
}

function Get-TextTokens {
    param(
        [Parameter()]
        [AllowNull()]
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return @()
    }

    $normalized = $Text -creplace '([a-z])([A-Z])', '$1 $2'
    $normalized = $normalized -replace '[_\-]', ' '
    $normalized = $normalized -replace '(\d+)', ' $1 '

    $stopWords = @{
        "camera" = $true
        "door" = $true
        "doors" = $true
        "dorian" = $true
        "inner" = $true
        "master" = $true
        "model" = $true
        "outer" = $true
        "room" = $true
        "space" = $true
        "update" = $true
    }

    $tokens = New-Object System.Collections.Generic.List[string]
    foreach ($token in ($normalized -split '[^A-Za-z0-9]+')) {
        if ([string]::IsNullOrWhiteSpace($token)) {
            continue
        }

        $lower = $token.ToLowerInvariant()
        if ($lower -match '^\d+$' -or $stopWords.ContainsKey($lower)) {
            continue
        }

        if (-not $tokens.Contains($lower)) {
            $tokens.Add($lower)
        }
    }

    return $tokens.ToArray()
}

function Get-TokenOverlapScore {
    param(
        [Parameter()]
        [AllowNull()]
        [string[]]$LeftTokens,

        [Parameter()]
        [AllowNull()]
        [string[]]$RightTokens
    )

    if ($null -eq $LeftTokens -or $null -eq $RightTokens) {
        return 0
    }

    $rightSet = @{}
    foreach ($token in $RightTokens) {
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $rightSet[$token] = $true
        }
    }

    $score = 0
    foreach ($token in $LeftTokens) {
        if (-not [string]::IsNullOrWhiteSpace($token) -and $rightSet.ContainsKey($token)) {
            $score += 1
        }
    }

    return $score
}

function Get-DoorFamilyName {
    param(
        [Parameter()]
        [AllowNull()]
        [string]$DoorName
    )

    if ([string]::IsNullOrWhiteSpace($DoorName)) {
        return $null
    }

    return ($DoorName -replace '(_)?(Inner|Outer|Locked|Unlocked|Unlocking)$', '')
}

function Add-Issue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Issues,

        [Parameter(Mandatory = $true)]
        [ref]$Score,

        [Parameter(Mandatory = $true)]
        [string]$Issue,

        [Parameter(Mandatory = $true)]
        [int]$Weight
    )

    $Issues.Add($Issue)
    $Score.Value += $Weight
}

function Get-TransitionConnectorNames {
    param(
        [Parameter(Mandatory = $true)]
        $Transition
    )

    $values = New-Object System.Collections.Generic.List[string]
    if ($Transition.PSObject.Properties.Match("ConnectorName").Count -gt 0) {
        $values.Add([string]$Transition.ConnectorName)
    }

    if ($Transition.PSObject.Properties.Match("ConnectorNames").Count -gt 0) {
        foreach ($value in @($Transition.ConnectorNames)) {
            $values.Add([string]$value)
        }
    }

    if ($Transition.PSObject.Properties.Match("Connector").Count -gt 0 -and $null -ne $Transition.Connector) {
        if ($Transition.Connector.PSObject.Properties.Match("Name").Count -gt 0) {
            $values.Add([string]$Transition.Connector.Name)
        }

        if ($Transition.Connector.PSObject.Properties.Match("Names").Count -gt 0) {
            foreach ($value in @($Transition.Connector.Names)) {
                $values.Add([string]$value)
            }
        }
    }

    return @(Get-UniqueStringArray -Values $values.ToArray())
}

if (-not (Test-Path -LiteralPath $GraphPath)) {
    throw "Navigation graph not found: $GraphPath"
}

if (-not (Test-Path -LiteralPath $SceneDataPath)) {
    throw "Scene data not found: $SceneDataPath"
}

$graph = Get-Content -LiteralPath $GraphPath -Raw | ConvertFrom-Json
$sceneData = Get-Content -LiteralPath $SceneDataPath -Raw | ConvertFrom-Json

$transitions = @()
if ($graph.PSObject.Properties.Match("Transitions").Count -gt 0) {
    $transitions = @($graph.Transitions)
}

if ($transitions.Count -eq 0 -and $graph.PSObject.Properties.Match("Links").Count -gt 0) {
    $transitions = @($graph.Links)
}

if ($transitions.Count -eq 0) {
    throw "Navigation graph did not contain any transitions: $GraphPath"
}

$doorObjects = @($sceneData.DoorObjects | Where-Object {
        $null -ne $_ -and
        -not [string]::IsNullOrWhiteSpace($_.Name) -and
        $null -ne $_.Position -and
        ($_.Name -like 'Doors_*' -or $_.Name -eq 'AtticDoors')
    } | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            FamilyName = Get-DoorFamilyName -DoorName $_.Name
            Position = $_.Position
            Tokens = @(Get-TextTokens -Text $_.Name)
        }
    })

if ($doorObjects.Count -eq 0) {
    throw "Scene data did not contain any door objects: $SceneDataPath"
}

$pairBuckets = @{}
foreach ($transition in @($transitions | Where-Object { $_.StepKind -eq "Door" })) {
    $pairKey = Get-CanonicalPairKey -Left ([string]$transition.FromZone) -Right ([string]$transition.ToZone)
    if (-not $pairBuckets.ContainsKey($pairKey)) {
        $pairBuckets[$pairKey] = New-Object System.Collections.Generic.List[object]
    }

    $pairBuckets[$pairKey].Add($transition)
}

$reportEntries = foreach ($pairKey in @($pairBuckets.Keys | Sort-Object)) {
    $pairTransitions = @($pairBuckets[$pairKey].ToArray())
    $firstTransition = $pairTransitions[0]
    $pairTokens = @(Get-UniqueStringArray -Values (
            @((Get-TextTokens -Text ([string]$firstTransition.FromZone))) +
            @((Get-TextTokens -Text ([string]$firstTransition.ToZone)))
        ))
    $issues = [System.Collections.Generic.List[string]]::new()
    $score = 0

    $connectorNames = @(Get-UniqueStringArray -Values @($pairTransitions | ForEach-Object { @(Get-TransitionConnectorNames -Transition $_) }))
    $assetSources = @(Get-UniqueStringArray -Values @($pairTransitions | ForEach-Object { $_.AssetDerivationSource }))
    $connectorPosition = $firstTransition.ConnectorObjectPosition
    $candidateDoors = @($doorObjects | ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name
                FamilyName = $_.FamilyName
                Position = $_.Position
                TokenOverlapScore = Get-TokenOverlapScore -LeftTokens $pairTokens -RightTokens $_.Tokens
                PlanarDistance = [Math]::Round((Get-PlanarDistance -Left $connectorPosition -Right $_.Position), 4)
                HeightDistance = [Math]::Round((Get-HeightDistance -Left $connectorPosition -Right $_.Position), 4)
                Distance3D = [Math]::Round((Get-VectorDistance -Left $connectorPosition -Right $_.Position), 4)
            }
        } | Sort-Object -Property `
            @{ Expression = "TokenOverlapScore"; Descending = $true }, `
            @{ Expression = "PlanarDistance"; Descending = $false }, `
            @{ Expression = "HeightDistance"; Descending = $false }, `
            @{ Expression = "Name"; Descending = $false })

    $topCandidates = @($candidateDoors | Select-Object -First $TopCandidateCount)
    $topFamilies = @(Get-UniqueStringArray -Values @($topCandidates | ForEach-Object { $_.FamilyName }))
    $referenceMatchType = "NearestDoorByZoneTokens"
    $referenceDoor = $null
    $exactMatches = New-Object System.Collections.Generic.List[object]

    if ($connectorNames.Count -eq 0) {
        Add-Issue -Issues $issues -Score ([ref]$score) -Issue "MissingConnectorName" -Weight 4
    }

    if ($assetSources -contains "DoorCameraWaypoints") {
        Add-Issue -Issues $issues -Score ([ref]$score) -Issue "DoorCameraWaypointsFallback" -Weight 2
    }

    if ($connectorNames.Count -gt 0) {
        $referenceMatchType = "ExactNamedDoor"
        foreach ($connectorName in $connectorNames) {
            $matchingDoors = @($doorObjects | Where-Object { $_.Name -eq $connectorName })
            if ($matchingDoors.Count -eq 0) {
                Add-Issue -Issues $issues -Score ([ref]$score) -Issue "ConnectorNameNotFoundInSceneData" -Weight 4
                continue
            }

            if ($matchingDoors.Count -gt 1) {
                Add-Issue -Issues $issues -Score ([ref]$score) -Issue "MultipleSceneDoorsWithSameConnectorName" -Weight 1
            }

            $bestNamedMatch = @($matchingDoors | Sort-Object -Property `
                    @{ Expression = { Get-PlanarDistance -Left $connectorPosition -Right $_.Position }; Descending = $false }, `
                    @{ Expression = { Get-HeightDistance -Left $connectorPosition -Right $_.Position }; Descending = $false })[0]
            $exactMatches.Add([pscustomobject]@{
                    Name = $connectorName
                    FamilyName = $bestNamedMatch.FamilyName
                    Position = $bestNamedMatch.Position
                    PlanarDistance = [Math]::Round((Get-PlanarDistance -Left $connectorPosition -Right $bestNamedMatch.Position), 4)
                    HeightDistance = [Math]::Round((Get-HeightDistance -Left $connectorPosition -Right $bestNamedMatch.Position), 4)
                    Distance3D = [Math]::Round((Get-VectorDistance -Left $connectorPosition -Right $bestNamedMatch.Position), 4)
                })
        }

        if ($exactMatches.Count -gt 0) {
            $referenceDoor = @($exactMatches | Sort-Object -Property `
                    @{ Expression = "PlanarDistance"; Descending = $false }, `
                    @{ Expression = "HeightDistance"; Descending = $false }, `
                    @{ Expression = "Name"; Descending = $false })[0]

            if ($referenceDoor.PlanarDistance -gt $NamedConnectorPlanarThreshold) {
                Add-Issue -Issues $issues -Score ([ref]$score) -Issue "NamedConnectorPlanarMismatch" -Weight 3
            }

            if ($referenceDoor.HeightDistance -gt $HeightMismatchThreshold) {
                Add-Issue -Issues $issues -Score ([ref]$score) -Issue "NamedConnectorHeightMismatch" -Weight 2
            }
        }
    } else {
        if ($topCandidates.Count -gt 0) {
            $referenceDoor = $topCandidates[0]
        }

        if ($null -ne $referenceDoor) {
            if ($referenceDoor.PlanarDistance -gt $UnnamedConnectorPlanarThreshold) {
                Add-Issue -Issues $issues -Score ([ref]$score) -Issue "UnnamedConnectorFarFromLikelyDoor" -Weight 4
            }

            if ($referenceDoor.HeightDistance -gt $HeightMismatchThreshold) {
                Add-Issue -Issues $issues -Score ([ref]$score) -Issue "UnnamedConnectorHeightMismatch" -Weight 2
            }
        }

        if ($topFamilies.Count -gt 1) {
            Add-Issue -Issues $issues -Score ([ref]$score) -Issue "MultipleLikelyDoorFamilies" -Weight 1
        }
    }

    [pscustomobject]@{
        PairKey = $pairKey
        DirectedKeys = @($pairTransitions | ForEach-Object { "{0}->{1}" -f $_.FromZone, $_.ToZone })
        DirectedTransitionIds = @($pairTransitions | ForEach-Object { $_.Id })
        ConnectorNames = $connectorNames
        AssetDerivationSources = $assetSources
        ConnectorObjectPosition = $connectorPosition
        FromWaypoint = $firstTransition.FromWaypoint
        ToWaypoint = $firstTransition.ToWaypoint
        SourceClearPoint = $firstTransition.SourceClearPoint
        DestinationClearPoint = $firstTransition.DestinationClearPoint
        ReferenceDoorMatchType = $referenceMatchType
        ReferenceDoor = $referenceDoor
        ExactConnectorMatches = @($exactMatches.ToArray())
        TopCandidateDoors = $topCandidates
        TopCandidateFamilies = $topFamilies
        SuspicionScore = $score
        Issues = @($issues.ToArray())
    }
}

$orderedEntries = @($reportEntries | Sort-Object -Property `
        @{ Expression = "SuspicionScore"; Descending = $true }, `
        @{ Expression = "PairKey"; Descending = $false })

$summary = [pscustomobject]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("o")
    GraphPath = (Resolve-Path -LiteralPath $GraphPath).Path
    SceneDataPath = (Resolve-Path -LiteralPath $SceneDataPath).Path
    TotalDoorPairs = $orderedEntries.Count
    SuspiciousDoorPairs = @($orderedEntries | Where-Object { $_.SuspicionScore -gt 0 }).Count
    MissingConnectorNamePairs = @($orderedEntries | Where-Object { $_.Issues -contains "MissingConnectorName" }).Count
    CameraWaypointFallbackPairs = @($orderedEntries | Where-Object { $_.Issues -contains "DoorCameraWaypointsFallback" }).Count
    LargeMismatchPairs = @($orderedEntries | Where-Object {
            $_.Issues -contains "NamedConnectorPlanarMismatch" -or
            $_.Issues -contains "NamedConnectorHeightMismatch" -or
            $_.Issues -contains "UnnamedConnectorFarFromLikelyDoor" -or
            $_.Issues -contains "UnnamedConnectorHeightMismatch"
        }).Count
}

$report = [pscustomobject]@{
    Summary = $summary
    Entries = $orderedEntries
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

$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add("Door Transition Metadata Audit")
$summaryLines.Add(("GeneratedAtUtc: {0}" -f $summary.GeneratedAtUtc))
$summaryLines.Add(("GraphPath: {0}" -f $summary.GraphPath))
$summaryLines.Add(("SceneDataPath: {0}" -f $summary.SceneDataPath))
$summaryLines.Add(("TotalDoorPairs: {0}" -f $summary.TotalDoorPairs))
$summaryLines.Add(("SuspiciousDoorPairs: {0}" -f $summary.SuspiciousDoorPairs))
$summaryLines.Add(("MissingConnectorNamePairs: {0}" -f $summary.MissingConnectorNamePairs))
$summaryLines.Add(("CameraWaypointFallbackPairs: {0}" -f $summary.CameraWaypointFallbackPairs))
$summaryLines.Add(("LargeMismatchPairs: {0}" -f $summary.LargeMismatchPairs))
$summaryLines.Add("")
$summaryLines.Add("Top suspicious pairs:")

foreach ($entry in @($orderedEntries | Select-Object -First 10)) {
    $referenceDoorText = if ($null -ne $entry.ReferenceDoor) {
        "{0} planar={1} height={2}" -f $entry.ReferenceDoor.Name, $entry.ReferenceDoor.PlanarDistance, $entry.ReferenceDoor.HeightDistance
    } else {
        "<none>"
    }

    $summaryLines.Add((
            "{0} score={1} issues={2} connector={3} reference={4} families={5}" -f
            $entry.PairKey,
            $entry.SuspicionScore,
            ([string]::Join(",", @($entry.Issues))),
            ([string]::Join(",", @($entry.ConnectorNames))),
            $referenceDoorText,
            ([string]::Join(",", @($entry.TopCandidateFamilies)))
        ))
}

$summaryLines | Set-Content -LiteralPath $SummaryPath -Encoding UTF8
Write-Host "Wrote door transition audit to $OutputPath"
Write-Host "Wrote door transition audit summary to $SummaryPath"
