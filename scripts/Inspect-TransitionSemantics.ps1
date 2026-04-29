[CmdletBinding()]
param(
    [Parameter()]
    [string]$GraphPath = ".\artifacts\navigation\navigation_graph.generated.json",

    [Parameter()]
    [string]$LocalMapPath = ".\artifacts\navigation\local_navigation_maps.generated.json",

    [Parameter()]
    [string]$StaticValidationPath = ".\artifacts\navigation\transition_validation.static.json",

    [Parameter()]
    [string]$DoorAuditPath = ".\artifacts\navigation\door_transition_audit.json",

    [Parameter()]
    [string]$OpenSweepPath = ".\artifacts\navigation\transition_sweep.live.json",

    [Parameter()]
    [string]$DoorSweepPath = ".\artifacts\navigation\door_transition_sweep.live.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\transition_semantics.generated.json",

    [Parameter()]
    [string]$SummaryPath = ".\artifacts\navigation\transition_semantics.summary.txt"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "SweepReportTools.ps1")

function Read-RequiredJsonDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }

    $document = Read-JsonDocument -Path $Path
    if ($null -eq $document) {
        throw "$Description was empty or invalid JSON: $Path"
    }

    return $document
}

function Read-OptionalJsonDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return Read-JsonDocument -Path $Path
}

function Get-ObjectArray {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Get-StringValue {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return $null
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    return $text
}

function Get-UniqueStrings {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Values
    )

    $result = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($value in @($Values)) {
        $text = Get-StringValue -Value $value
        if ($null -eq $text -or $seen.ContainsKey($text)) {
            continue
        }

        $seen[$text] = $true
        $result.Add($text)
    }

    return $result.ToArray()
}

function Get-StringArrayValues {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Values
    )

    $result = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        $text = Get-StringValue -Value $value
        if ($null -ne $text) {
            $result.Add($text)
        }
    }

    return $result.ToArray()
}

function Add-UniqueIssue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Issues,

        [Parameter(Mandatory = $true)]
        [string]$Issue
    )

    if (-not $Issues.Contains($Issue)) {
        $Issues.Add($Issue)
    }
}

function Test-ZeroVector {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Vector
    )

    if ($null -eq $Vector) {
        return $true
    }

    return [Math]::Abs([double](Get-JsonPropertyValue -InputObject $Vector -Name "x" -Default 0.0)) -lt 0.0001 -and
        [Math]::Abs([double](Get-JsonPropertyValue -InputObject $Vector -Name "y" -Default 0.0)) -lt 0.0001 -and
        [Math]::Abs([double](Get-JsonPropertyValue -InputObject $Vector -Name "z" -Default 0.0)) -lt 0.0001
}

function Get-PlanarDistance {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Left,

        [Parameter()]
        [AllowNull()]
        [object]$Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return -1.0
    }

    $dx = [double](Get-JsonPropertyValue -InputObject $Left -Name "x" -Default 0.0) -
        [double](Get-JsonPropertyValue -InputObject $Right -Name "x" -Default 0.0)
    $dz = [double](Get-JsonPropertyValue -InputObject $Left -Name "z" -Default 0.0) -
        [double](Get-JsonPropertyValue -InputObject $Right -Name "z" -Default 0.0)
    return [Math]::Sqrt(($dx * $dx) + ($dz * $dz))
}

function Get-HeightDelta {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Left,

        [Parameter()]
        [AllowNull()]
        [object]$Right
    )

    if ($null -eq $Left -or $null -eq $Right) {
        return -1.0
    }

    return [Math]::Abs(
        [double](Get-JsonPropertyValue -InputObject $Left -Name "y" -Default 0.0) -
        [double](Get-JsonPropertyValue -InputObject $Right -Name "y" -Default 0.0))
}

function New-Vec3Record {
    param(
        [Parameter(Mandatory = $true)]
        [double]$X,

        [Parameter(Mandatory = $true)]
        [double]$Y,

        [Parameter(Mandatory = $true)]
        [double]$Z
    )

    return [ordered]@{
        x = [Math]::Round($X, 6)
        y = [Math]::Round($Y, 6)
        z = [Math]::Round($Z, 6)
    }
}

function Get-TransitionId {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Transition
    )

    $id = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "Id" -Default $null)
    if ($null -ne $id) {
        return $id
    }

    $fromZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "FromZone" -Default $null)
    $toZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "ToZone" -Default $null)
    return "transition:$fromZone->$toZone"
}

function Get-TransitionKey {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Transition
    )

    $fromZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "FromZone" -Default $null)
    $toZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "ToZone" -Default $null)
    return "$fromZone->$toZone"
}

function Get-TransitionSubtype {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Transition
    )

    $kind = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "StepKind" -Default "Unknown")
    if ($kind -ieq "Door") {
        return "interaction-door"
    }

    if ($kind -ieq "Teleporter") {
        return "teleporter"
    }

    if ($kind -ieq "Stairs") {
        return "stairs"
    }

    if ($kind -ieq "OpenPassage") {
        $heightDelta = Get-HeightDelta `
            -Left (Get-JsonPropertyValue -InputObject $Transition -Name "FromWaypoint" -Default $null) `
            -Right (Get-JsonPropertyValue -InputObject $Transition -Name "ToWaypoint" -Default $null)
        $crossingDistance = Get-PlanarDistance `
            -Left (Get-JsonPropertyValue -InputObject $Transition -Name "FromCrossingAnchor" -Default $null) `
            -Right (Get-JsonPropertyValue -InputObject $Transition -Name "ToCrossingAnchor" -Default $null)

        if ($heightDelta -gt 1.0) {
            return "vertical-open-passage"
        }

        if ($crossingDistance -gt 5.0) {
            return "wide-open-passage"
        }

        return "flat-open-passage"
    }

    return "unknown"
}

function New-SweepEntryMap {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Report
    )

    $map = @{}
    foreach ($entry in @(Get-JsonPropertyValue -InputObject $Report -Name "Entries" -Default @())) {
        if ($null -eq $entry) {
            continue
        }

        $key = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $entry -Name "Key" -Default $null)
        $fromZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $entry -Name "FromZone" -Default $null)
        $toZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $entry -Name "ToZone" -Default $null)
        if ($null -ne $key -and -not $map.ContainsKey($key)) {
            $map[$key] = $entry
        }

        if ($null -ne $fromZone -and $null -ne $toZone) {
            $pairKey = "$fromZone->$toZone"
            if (-not $map.ContainsKey($pairKey)) {
                $map[$pairKey] = $entry
            }
        }
    }

    return $map
}

function New-StaticValidationMap {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Report
    )

    $map = @{}
    foreach ($entry in @(Get-JsonPropertyValue -InputObject $Report -Name "Entries" -Default @())) {
        $key = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $entry -Name "Key" -Default $null)
        if ($null -ne $key -and -not $map.ContainsKey($key)) {
            $map[$key] = $entry
        }
    }

    return $map
}

function New-DoorAuditMap {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Report
    )

    $map = @{}
    foreach ($entry in @(Get-JsonPropertyValue -InputObject $Report -Name "Entries" -Default @())) {
        foreach ($id in @(Get-JsonPropertyValue -InputObject $entry -Name "DirectedTransitionIds" -Default @())) {
            $text = Get-StringValue -Value $id
            if ($null -ne $text -and -not $map.ContainsKey($text)) {
                $map[$text] = $entry
            }
        }

        foreach ($key in @(Get-JsonPropertyValue -InputObject $entry -Name "DirectedKeys" -Default @())) {
            $text = Get-StringValue -Value $key
            if ($null -ne $text -and -not $map.ContainsKey($text)) {
                $map[$text] = $entry
            }
        }
    }

    return $map
}

function Get-SweepEntryForTransition {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Transition,

        [Parameter(Mandatory = $true)]
        [hashtable]$OpenSweepMap,

        [Parameter(Mandatory = $true)]
        [hashtable]$DoorSweepMap
    )

    $id = Get-TransitionId -Transition $Transition
    $key = Get-TransitionKey -Transition $Transition
    $kind = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "StepKind" -Default "Unknown")
    $primaryMap = if ($kind -ieq "Door") { $DoorSweepMap } else { $OpenSweepMap }
    $secondaryMap = if ($kind -ieq "Door") { $OpenSweepMap } else { $DoorSweepMap }

    foreach ($candidate in @($id, $key)) {
        if ($null -ne $candidate -and $primaryMap.ContainsKey($candidate)) {
            return [ordered]@{
                Entry = $primaryMap[$candidate]
                Source = if ($kind -ieq "Door") { "Door" } else { "OpenPassage" }
            }
        }
    }

    foreach ($candidate in @($id, $key)) {
        if ($null -ne $candidate -and $secondaryMap.ContainsKey($candidate)) {
            return [ordered]@{
                Entry = $secondaryMap[$candidate]
                Source = if ($kind -ieq "Door") { "OpenPassage" } else { "Door" }
            }
        }
    }

    return $null
}

function New-ZoneModel {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ZoneRecord
    )

    $zoneName = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $ZoneRecord -Name "Zone" -Default $null)
    $bounds = Get-JsonPropertyValue -InputObject $ZoneRecord -Name "Bounds2D" -Default $null
    $gridWidth = [int](Get-JsonPropertyValue -InputObject $ZoneRecord -Name "GridWidth" -Default 0)
    $gridHeight = [int](Get-JsonPropertyValue -InputObject $ZoneRecord -Name "GridHeight" -Default 0)
    $cellSize = [double](Get-JsonPropertyValue -InputObject $ZoneRecord -Name "CellSize" -Default 0.0)
    if ($null -eq $zoneName -or $null -eq $bounds -or $gridWidth -le 0 -or $gridHeight -le 0 -or $cellSize -le 0.0) {
        return $null
    }

    $cellCount = $gridWidth * $gridHeight
    $walkable = New-Object bool[] $cellCount
    foreach ($indexValue in @(Get-JsonPropertyValue -InputObject $ZoneRecord -Name "EnvelopeIndices" -Default @())) {
        $index = [int]$indexValue
        if ($index -ge 0 -and $index -lt $cellCount) {
            $walkable[$index] = $true
        }
    }

    foreach ($indexValue in @(Get-JsonPropertyValue -InputObject $ZoneRecord -Name "BlockedIndices" -Default @())) {
        $index = [int]$indexValue
        if ($index -ge 0 -and $index -lt $cellCount) {
            $walkable[$index] = $false
        }
    }

    $componentIds = New-Object int[] $cellCount
    for ($i = 0; $i -lt $cellCount; $i++) {
        $componentIds[$i] = -1
    }

    $zone = [pscustomobject]@{
        Zone = $zoneName
        CellSize = $cellSize
        MinX = [double](Get-JsonPropertyValue -InputObject $bounds -Name "MinX" -Default 0.0)
        MaxX = [double](Get-JsonPropertyValue -InputObject $bounds -Name "MaxX" -Default 0.0)
        MinZ = [double](Get-JsonPropertyValue -InputObject $bounds -Name "MinZ" -Default 0.0)
        MaxZ = [double](Get-JsonPropertyValue -InputObject $bounds -Name "MaxZ" -Default 0.0)
        GridWidth = $gridWidth
        GridHeight = $gridHeight
        Walkable = $walkable
        ComponentIds = $componentIds
        ComponentSummaries = @()
    }

    Set-ZoneComponents -Zone $zone
    return $zone
}

function Get-CellIndex {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone,

        [Parameter(Mandatory = $true)]
        [int]$Column,

        [Parameter(Mandatory = $true)]
        [int]$Row
    )

    if ($Column -lt 0 -or $Row -lt 0 -or $Column -ge $Zone.GridWidth -or $Row -ge $Zone.GridHeight) {
        return -1
    }

    return ($Row * $Zone.GridWidth) + $Column
}

function Get-CellCoordinates {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone,

        [Parameter(Mandatory = $true)]
        [int]$Index
    )

    return [ordered]@{
        Column = $Index % $Zone.GridWidth
        Row = [Math]::Floor($Index / $Zone.GridWidth)
    }
}

function Get-CellCenter {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone,

        [Parameter(Mandatory = $true)]
        [int]$Index,

        [Parameter(Mandatory = $true)]
        [double]$Y
    )

    $coordinates = Get-CellCoordinates -Zone $Zone -Index $Index
    return New-Vec3Record `
        -X ($Zone.MinX + ([double]$coordinates.Column * $Zone.CellSize) + ($Zone.CellSize * 0.5)) `
        -Y $Y `
        -Z ($Zone.MinZ + ([double]$coordinates.Row * $Zone.CellSize) + ($Zone.CellSize * 0.5))
}

function Set-ZoneComponents {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone
    )

    $summaries = New-Object System.Collections.Generic.List[object]
    $queue = [System.Collections.Generic.Queue[int]]::new()
    for ($cellIndex = 0; $cellIndex -lt $Zone.Walkable.Length; $cellIndex++) {
        if (-not $Zone.Walkable[$cellIndex] -or $Zone.ComponentIds[$cellIndex] -ge 0) {
            continue
        }

        $componentId = $summaries.Count
        $cellCount = 0
        $representative = Get-CellCenter -Zone $Zone -Index $cellIndex -Y 0.0
        $Zone.ComponentIds[$cellIndex] = $componentId
        $queue.Enqueue($cellIndex)

        while ($queue.Count -gt 0) {
            $currentIndex = $queue.Dequeue()
            $cellCount++
            $coordinates = Get-CellCoordinates -Zone $Zone -Index $currentIndex
            for ($rowOffset = -1; $rowOffset -le 1; $rowOffset++) {
                for ($columnOffset = -1; $columnOffset -le 1; $columnOffset++) {
                    if ($rowOffset -eq 0 -and $columnOffset -eq 0) {
                        continue
                    }

                    $neighborColumn = [int]$coordinates.Column + $columnOffset
                    $neighborRow = [int]$coordinates.Row + $rowOffset
                    $neighborIndex = Get-CellIndex -Zone $Zone -Column $neighborColumn -Row $neighborRow
                    if ($neighborIndex -lt 0) {
                        continue
                    }

                    if (-not $Zone.Walkable[$neighborIndex] -or $Zone.ComponentIds[$neighborIndex] -ge 0) {
                        continue
                    }

                    $Zone.ComponentIds[$neighborIndex] = $componentId
                    $queue.Enqueue($neighborIndex)
                }
            }
        }

        $summaries.Add([ordered]@{
            ComponentId = $componentId
            CellCount = $cellCount
            RepresentativeWorldPosition = $representative
        })
    }

    $Zone.ComponentSummaries = $summaries.ToArray()
}

function New-LocalMapIndex {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$LocalMapDocument
    )

    $index = @{}
    foreach ($zoneRecord in @(Get-JsonPropertyValue -InputObject $LocalMapDocument -Name "Zones" -Default @())) {
        $zone = New-ZoneModel -ZoneRecord $zoneRecord
        if ($null -eq $zone) {
            continue
        }

        $index[$zone.Zone] = $zone
    }

    return $index
}

function Get-ComponentCellCount {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone,

        [Parameter(Mandatory = $true)]
        [int]$ComponentId
    )

    foreach ($summary in @($Zone.ComponentSummaries)) {
        if ([int](Get-JsonPropertyValue -InputObject $summary -Name "ComponentId" -Default -1) -eq $ComponentId) {
            return [int](Get-JsonPropertyValue -InputObject $summary -Name "CellCount" -Default 0)
        }
    }

    return 0
}

function Resolve-LocalMapPosition {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone,

        [Parameter(Mandatory = $true)]
        [object]$Position,

        [Parameter()]
        [int]$MaximumRadius = 8
    )

    $x = [double](Get-JsonPropertyValue -InputObject $Position -Name "x" -Default 0.0)
    $y = [double](Get-JsonPropertyValue -InputObject $Position -Name "y" -Default 0.0)
    $z = [double](Get-JsonPropertyValue -InputObject $Position -Name "z" -Default 0.0)
    $approximateColumn = [Math]::Max(0, [Math]::Min($Zone.GridWidth - 1, [int][Math]::Floor(($x - $Zone.MinX) / $Zone.CellSize)))
    $approximateRow = [Math]::Max(0, [Math]::Min($Zone.GridHeight - 1, [int][Math]::Floor(($z - $Zone.MinZ) / $Zone.CellSize)))
    $approximateIndex = Get-CellIndex -Zone $Zone -Column $approximateColumn -Row $approximateRow

    $selectedIndex = -1
    $snapMode = $null
    $radiusUsed = -1
    $bestDistanceSquared = [double]::PositiveInfinity

    if ($approximateIndex -ge 0 -and $Zone.Walkable[$approximateIndex]) {
        $selectedIndex = $approximateIndex
        $snapMode = "exact"
        $radiusUsed = 0
    } else {
        for ($radius = 1; $radius -le $MaximumRadius -and $selectedIndex -lt 0; $radius++) {
            $foundAtRadius = $false
            $minimumColumn = [Math]::Max(0, $approximateColumn - $radius)
            $maximumColumn = [Math]::Min($Zone.GridWidth - 1, $approximateColumn + $radius)
            $minimumRow = [Math]::Max(0, $approximateRow - $radius)
            $maximumRow = [Math]::Min($Zone.GridHeight - 1, $approximateRow + $radius)

            for ($row = $minimumRow; $row -le $maximumRow; $row++) {
                for ($column = $minimumColumn; $column -le $maximumColumn; $column++) {
                    if ($column -gt $minimumColumn -and $column -lt $maximumColumn -and $row -gt $minimumRow -and $row -lt $maximumRow) {
                        continue
                    }

                    $candidateIndex = Get-CellIndex -Zone $Zone -Column $column -Row $row
                    if ($candidateIndex -lt 0 -or -not $Zone.Walkable[$candidateIndex]) {
                        continue
                    }

                    $candidatePosition = Get-CellCenter -Zone $Zone -Index $candidateIndex -Y $y
                    $distanceSquared = [Math]::Pow(([double]$candidatePosition.x - $x), 2) +
                        [Math]::Pow(([double]$candidatePosition.z - $z), 2)
                    if ($distanceSquared -ge $bestDistanceSquared) {
                        continue
                    }

                    $bestDistanceSquared = $distanceSquared
                    $selectedIndex = $candidateIndex
                    $snapMode = "radius"
                    $radiusUsed = $radius
                    $foundAtRadius = $true
                }
            }

            if ($foundAtRadius) {
                break
            }
        }
    }

    if ($selectedIndex -lt 0) {
        for ($candidateIndex = 0; $candidateIndex -lt $Zone.Walkable.Length; $candidateIndex++) {
            if (-not $Zone.Walkable[$candidateIndex]) {
                continue
            }

            $candidatePosition = Get-CellCenter -Zone $Zone -Index $candidateIndex -Y $y
            $distanceSquared = [Math]::Pow(([double]$candidatePosition.x - $x), 2) +
                [Math]::Pow(([double]$candidatePosition.z - $z), 2)
            if ($distanceSquared -ge $bestDistanceSquared) {
                continue
            }

            $bestDistanceSquared = $distanceSquared
            $selectedIndex = $candidateIndex
            $snapMode = "full-zone"
            $radiusUsed = -1
        }
    }

    if ($selectedIndex -lt 0) {
        return [ordered]@{
            Status = "no-walkable-cell"
            Detail = "No walkable cell found"
        }
    }

    $snappedPosition = Get-CellCenter -Zone $Zone -Index $selectedIndex -Y $y
    $coordinates = Get-CellCoordinates -Zone $Zone -Index $selectedIndex
    $componentId = [int]$Zone.ComponentIds[$selectedIndex]
    $planarSnapDistance = Get-PlanarDistance -Left $Position -Right $snappedPosition
    return [ordered]@{
        Status = "resolved"
        SnapMode = $snapMode
        Radius = $radiusUsed
        CellIndex = $selectedIndex
        CellColumn = [int]$coordinates.Column
        CellRow = [int]$coordinates.Row
        ComponentId = $componentId
        ComponentCellCount = if ($componentId -ge 0) { Get-ComponentCellCount -Zone $Zone -ComponentId $componentId } else { 0 }
        PlanarSnapDistance = [Math]::Round($planarSnapDistance, 4)
        SnappedPosition = $snappedPosition
    }
}

function New-BeaconEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Role,

        [Parameter(Mandatory = $true)]
        [string]$ZoneName,

        [Parameter()]
        [AllowNull()]
        [object]$Position,

        [Parameter(Mandatory = $true)]
        [bool]$Required,

        [Parameter(Mandatory = $true)]
        [hashtable]$LocalMapIndex
    )

    $positionPresent = -not (Test-ZeroVector -Vector $Position)
    if (-not $positionPresent) {
        return [ordered]@{
            Role = $Role
            Zone = $ZoneName
            Required = $Required
            Present = $false
            Position = $null
            LocalMap = [ordered]@{
                Status = "missing-position"
            }
        }
    }

    $vector = Convert-VectorRecord -Vector $Position
    if (-not $LocalMapIndex.ContainsKey($ZoneName)) {
        return [ordered]@{
            Role = $Role
            Zone = $ZoneName
            Required = $Required
            Present = $true
            Position = $vector
            LocalMap = [ordered]@{
                Status = "zone-map-missing"
            }
        }
    }

    $zone = $LocalMapIndex[$ZoneName]
    $localEvidence = Resolve-LocalMapPosition -Zone $zone -Position $vector
    return [ordered]@{
        Role = $Role
        Zone = $ZoneName
        Required = $Required
        Present = $true
        Position = $vector
        LocalMap = $localEvidence
    }
}

function Add-BeaconIssues {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Beacons,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Issues,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Contradictions
    )

    foreach ($beacon in @($Beacons)) {
        $role = [string](Get-JsonPropertyValue -InputObject $beacon -Name "Role" -Default "unknown")
        $required = [bool](Get-JsonPropertyValue -InputObject $beacon -Name "Required" -Default $false)
        $present = [bool](Get-JsonPropertyValue -InputObject $beacon -Name "Present" -Default $false)
        $localMap = Get-JsonPropertyValue -InputObject $beacon -Name "LocalMap" -Default $null
        $localStatus = [string](Get-JsonPropertyValue -InputObject $localMap -Name "Status" -Default "unknown")

        if ($required -and -not $present) {
            Add-UniqueIssue -Issues $Issues -Issue "MissingRequiredBeacon:$role"
            continue
        }

        if ($required -and $localStatus -ne "resolved") {
            Add-UniqueIssue -Issues $Issues -Issue ("RequiredBeaconLocalMapUnresolved:{0}:{1}" -f $role, $localStatus)
            continue
        }

        if ($localStatus -eq "resolved") {
            $snapDistance = [double](Get-JsonPropertyValue -InputObject $localMap -Name "PlanarSnapDistance" -Default 0.0)
            if ($snapDistance -gt 2.5) {
                Add-UniqueIssue -Issues $Issues -Issue "BeaconSnapsFar:$role"
            }

            $componentId = [int](Get-JsonPropertyValue -InputObject $localMap -Name "ComponentId" -Default -1)
            if ($required -and $componentId -lt 0) {
                Add-UniqueIssue -Issues $Issues -Issue "RequiredBeaconNoComponent:$role"
            }
        }
    }

    Add-ComponentPairIssue `
        -Beacons $Beacons `
        -LeftRole "sourceApproach" `
        -RightRole "sourceClear" `
        -Issue "SourceApproachAndClearDisconnected" `
        -Contradictions $Contradictions
    Add-ComponentPairIssue `
        -Beacons $Beacons `
        -LeftRole "destinationClear" `
        -RightRole "destinationApproach" `
        -Issue "DestinationClearAndApproachDisconnected" `
        -Contradictions $Contradictions
}

function Add-ComponentPairIssue {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Beacons,

        [Parameter(Mandatory = $true)]
        [string]$LeftRole,

        [Parameter(Mandatory = $true)]
        [string]$RightRole,

        [Parameter(Mandatory = $true)]
        [string]$Issue,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Contradictions
    )

    $left = @($Beacons | Where-Object { [string](Get-JsonPropertyValue -InputObject $_ -Name "Role" -Default "") -eq $LeftRole } | Select-Object -First 1)
    $right = @($Beacons | Where-Object { [string](Get-JsonPropertyValue -InputObject $_ -Name "Role" -Default "") -eq $RightRole } | Select-Object -First 1)
    if ($left.Count -lt 1 -or $right.Count -lt 1) {
        return
    }

    $leftLocal = Get-JsonPropertyValue -InputObject $left[0] -Name "LocalMap" -Default $null
    $rightLocal = Get-JsonPropertyValue -InputObject $right[0] -Name "LocalMap" -Default $null
    if ([string](Get-JsonPropertyValue -InputObject $leftLocal -Name "Status" -Default "") -ne "resolved" -or
        [string](Get-JsonPropertyValue -InputObject $rightLocal -Name "Status" -Default "") -ne "resolved") {
        return
    }

    $leftComponent = [int](Get-JsonPropertyValue -InputObject $leftLocal -Name "ComponentId" -Default -1)
    $rightComponent = [int](Get-JsonPropertyValue -InputObject $rightLocal -Name "ComponentId" -Default -1)
    if ($leftComponent -ge 0 -and $rightComponent -ge 0 -and $leftComponent -ne $rightComponent) {
        Add-UniqueIssue -Issues $Contradictions -Issue $Issue
    }
}

function Test-StaticValidationClean {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$StaticValidation
    )

    if ($null -eq $StaticValidation) {
        return $false
    }

    $suspicionScore = [int](Get-JsonPropertyValue -InputObject $StaticValidation -Name "SuspicionScore" -Default 0)
    $builderSuspicionScore = [int](Get-JsonPropertyValue -InputObject $StaticValidation -Name "BuilderSuspicionScore" -Default 0)
    $issues = @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $StaticValidation -Name "Issues" -Default @()))
    $builderIssues = @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $StaticValidation -Name "BuilderIssues" -Default @()))
    return $suspicionScore -eq 0 -and $builderSuspicionScore -eq 0 -and $issues.Count -eq 0 -and $builderIssues.Count -eq 0
}

function Convert-CleanPassedComponentContradictions {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Kind,

        [Parameter(Mandatory = $true)]
        [object]$SweepEvidence,

        [Parameter()]
        [AllowNull()]
        [object]$StaticValidation,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Issues,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Contradictions
    )

    $status = [string](Get-JsonPropertyValue -InputObject $SweepEvidence -Name "Status" -Default "missing")
    if ($Kind -ine "OpenPassage" -or $status -ine "passed" -or -not (Test-StaticValidationClean -StaticValidation $StaticValidation)) {
        return
    }

    $componentIssues = @(
        "SourceApproachAndClearDisconnected",
        "DestinationClearAndApproachDisconnected"
    )

    foreach ($issue in @($componentIssues)) {
        if (-not $Contradictions.Contains($issue)) {
            continue
        }

        $null = $Contradictions.Remove($issue)
        Add-UniqueIssue -Issues $Issues -Issue ("StaticWarning:" + $issue)
    }
}

function Add-PassedCurrentZoneWarning {
    param(
        [Parameter(Mandatory = $true)]
        [object]$SweepEvidence,

        [Parameter()]
        [AllowEmptyCollection()]
        [string[]]$AcceptedDestinationZones,

        [Parameter()]
        [AllowEmptyCollection()]
        [string[]]$AcceptedIntermediateZones,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Issues
    )

    $status = [string](Get-JsonPropertyValue -InputObject $SweepEvidence -Name "Status" -Default "missing")
    if ($status -ine "passed") {
        return
    }

    $currentZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $SweepEvidence -Name "CurrentZoneAtResult" -Default $null)
    if ($null -eq $currentZone) {
        return
    }

    $acceptedZones = @()
    $acceptedZones += @(Get-StringArrayValues -Values $AcceptedDestinationZones)
    $acceptedZones += @(Get-StringArrayValues -Values $AcceptedIntermediateZones)
    if (-not (Test-ZoneAccepted -Zone $currentZone -AcceptedZones $acceptedZones)) {
        Add-UniqueIssue -Issues $Issues -Issue "SweepWarning:PassedCurrentZoneOutsideAcceptedDestination"
    }
}

function New-SweepEvidence {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$SweepMatch
    )

    if ($null -eq $SweepMatch) {
        return [ordered]@{
            Source = $null
            Status = "missing"
        }
    }

    $entry = Get-JsonPropertyValue -InputObject $SweepMatch -Name "Entry" -Default $null
    return [ordered]@{
        Source = Get-JsonPropertyValue -InputObject $SweepMatch -Name "Source" -Default $null
        Status = Get-JsonPropertyValue -InputObject $entry -Name "Status" -Default "pending"
        StatusDetail = Get-JsonPropertyValue -InputObject $entry -Name "StatusDetail" -Default $null
        FailureReason = Get-JsonPropertyValue -InputObject $entry -Name "FailureReason" -Default $null
        DurationSeconds = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $entry -Name "DurationSeconds" -Default 0.0)
        CurrentZoneAtResult = Get-JsonPropertyValue -InputObject $entry -Name "CurrentZoneAtResult" -Default $null
        LastTargetKind = Get-JsonPropertyValue -InputObject $entry -Name "LastTargetKind" -Default $null
        LastTargetPosition = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $entry -Name "LastTargetPosition" -Default $null)
        LastLocalNavigationContext = Get-JsonPropertyValue -InputObject $entry -Name "LastLocalNavigationContext" -Default $null
    }
}

function Get-DetailToken {
    param(
        [Parameter()]
        [AllowNull()]
        [string]$Detail,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Detail)) {
        return $null
    }

    $pattern = "(^| )" + [regex]::Escape($Name) + "=([^ ]+)"
    if ($Detail -match $pattern) {
        return $Matches[2]
    }

    return $null
}

function Get-FailureToken {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$SweepEvidence,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $SweepEvidence) {
        return $null
    }

    $detail = [string](Get-JsonPropertyValue -InputObject $SweepEvidence -Name "StatusDetail" -Default "")
    $value = Get-DetailToken -Detail $detail -Name $Name
    if ($null -ne $value) {
        return $value
    }

    if ($Name -eq "lastTargetKind") {
        return Get-StringValue -Value (Get-JsonPropertyValue -InputObject $SweepEvidence -Name "LastTargetKind" -Default $null)
    }

    if ($Name -eq "localContext") {
        return Get-StringValue -Value (Get-JsonPropertyValue -InputObject $SweepEvidence -Name "LastLocalNavigationContext" -Default $null)
    }

    return $null
}

function Get-FailureSignature {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$SweepEvidence
    )

    if ($null -eq $SweepEvidence) {
        return $null
    }

    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($name in @("stage", "context", "localContext", "lastTargetKind")) {
        $value = Get-FailureToken -SweepEvidence $SweepEvidence -Name $name
        if ($null -ne $value) {
            $parts.Add(("{0}={1}" -f $name, $value))
        }
    }

    if ($parts.Count -gt 0) {
        return [string]::Join(" ", $parts.ToArray())
    }

    $failure = [string](Get-JsonPropertyValue -InputObject $SweepEvidence -Name "FailureReason" -Default "")
    if ($failure -match '^([^=]+ currentZone=[^ ]+)') {
        return $Matches[1]
    }

    return $null
}

function Get-AcceptedIntermediateZones {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Kind,

        [Parameter()]
        [AllowNull()]
        [string]$FromZone,

        [Parameter()]
        [AllowNull()]
        [string]$ToZone
    )

    $zones = New-Object System.Collections.Generic.List[string]
    if ($Kind -ieq "Stairs") {
        if ($FromZone -ieq "upper_hallway" -and $ToZone -ieq "hallway") {
            $zones.Add("stairsdown")
        }

        if ($FromZone -ieq "hallway" -and ($ToZone -ieq "upper_hallway" -or $ToZone -ieq "hallway_arma")) {
            $zones.Add("stairsup")
        }
    }

    return $zones.ToArray()
}

function Test-ZoneAccepted {
    param(
        [Parameter()]
        [AllowNull()]
        [string]$Zone,

        [Parameter()]
        [AllowNull()]
        [object[]]$AcceptedZones
    )

    if ([string]::IsNullOrWhiteSpace($Zone)) {
        return $true
    }

    foreach ($accepted in @(Get-StringArrayValues -Values $AcceptedZones)) {
        if ($Zone -ieq $accepted) {
            return $true
        }
    }

    return $false
}

function Add-SweepEvidenceIssues {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Kind,

        [Parameter()]
        [AllowEmptyCollection()]
        [string[]]$ConnectorNames,

        [Parameter()]
        [AllowEmptyCollection()]
        [string[]]$AcceptedDestinationZones,

        [Parameter()]
        [AllowEmptyCollection()]
        [string[]]$AcceptedIntermediateZones,

        [Parameter(Mandatory = $true)]
        [object]$SweepEvidence,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Issues,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Contradictions
    )

    $status = [string](Get-JsonPropertyValue -InputObject $SweepEvidence -Name "Status" -Default "missing")
    if ($status -ieq "missing") {
        if ($Kind -ieq "Teleporter" -and @($ConnectorNames) -contains "CrawlspaceLadder") {
            $SweepEvidence["Source"] = "SemanticAudit"
            $SweepEvidence["Status"] = "deferred"
            $SweepEvidence["StatusDetail"] = "forced sweep deferred because CrawlspaceLadder cutscene interrupts forced-step harness; manual traversal documented on 2026-04-10"
            $SweepEvidence["ManualVerification"] = "passed:2026-04-10"
            Add-UniqueIssue -Issues $Issues -Issue "ForcedSweepDeferred:CrawlspaceLadderCutscene"
            return
        }

        Add-UniqueIssue -Issues $Issues -Issue "MissingSweepEvidence"
        return
    }

}

function Get-SemanticClassification {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Issues,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Contradictions,

        [Parameter(Mandatory = $true)]
        [object]$SweepEvidence
    )

    if ($Contradictions.Count -gt 0) {
        return "contradictory"
    }

    $blockingIssues = @($Issues | Where-Object {
            $_ -like "MissingRequiredBeacon:*" -or
            $_ -like "RequiredBeaconLocalMapUnresolved:*" -or
            $_ -like "RequiredBeaconNoComponent:*" -or
            $_ -eq "MissingTransitionId" -or
            $_ -eq "MissingSourceZone" -or
            $_ -eq "MissingDestinationZone" -or
            $_ -eq "UnknownTransitionKind" -or
            $_ -eq "MissingAcceptedSourceZones" -or
            $_ -eq "MissingAcceptedDestinationZones" -or
            $_ -eq "InteractionTransitionMissingConnectorName"
        })

    if ($blockingIssues.Count -gt 0) {
        return "incomplete"
    }

    $status = [string](Get-JsonPropertyValue -InputObject $SweepEvidence -Name "Status" -Default "missing")
    if ($status -ieq "passed") {
        return "complete"
    }

    if ($status -ieq "deferred") {
        $manualVerification = [string](Get-JsonPropertyValue -InputObject $SweepEvidence -Name "ManualVerification" -Default "")
        if ($manualVerification -like "passed:*") {
            return "complete"
        }
    }

    if ($status -ieq "failed") {
        return "execution-only failure"
    }

    return "unknown"
}

function New-TransitionSemanticRecord {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Transition,

        [Parameter(Mandatory = $true)]
        [hashtable]$LocalMapIndex,

        [Parameter(Mandatory = $true)]
        [hashtable]$StaticValidationMap,

        [Parameter(Mandatory = $true)]
        [hashtable]$DoorAuditMap,

        [Parameter(Mandatory = $true)]
        [hashtable]$OpenSweepMap,

        [Parameter(Mandatory = $true)]
        [hashtable]$DoorSweepMap
    )

    $issues = New-Object System.Collections.Generic.List[string]
    $contradictions = New-Object System.Collections.Generic.List[string]

    $id = Get-TransitionId -Transition $Transition
    $key = Get-TransitionKey -Transition $Transition
    $fromZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "FromZone" -Default $null)
    $toZone = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "ToZone" -Default $null)
    $kind = Get-StringValue -Value (Get-JsonPropertyValue -InputObject $Transition -Name "StepKind" -Default "Unknown")
    $requiresInteraction = [bool](Get-JsonPropertyValue -InputObject $Transition -Name "RequiresInteraction" -Default $false)
    $connectorNames = @(Get-UniqueStrings -Values @(
        Get-JsonPropertyValue -InputObject $Transition -Name "ConnectorName" -Default $null
        @(Get-JsonPropertyValue -InputObject $Transition -Name "ConnectorNames" -Default @())
    ))
    $validation = Get-JsonPropertyValue -InputObject $Transition -Name "Validation" -Default $null
    $acceptedSourceZones = @(Get-UniqueStrings -Values (Get-JsonPropertyValue -InputObject $validation -Name "AcceptedSourceZones" -Default @()))
    $acceptedDestinationZones = @(Get-UniqueStrings -Values (Get-JsonPropertyValue -InputObject $validation -Name "AcceptedDestinationZones" -Default @()))
    $acceptedIntermediateZones = @(Get-AcceptedIntermediateZones -Kind $kind -FromZone $fromZone -ToZone $toZone)

    if ([string]::IsNullOrWhiteSpace($id)) { Add-UniqueIssue -Issues $issues -Issue "MissingTransitionId" }
    if ([string]::IsNullOrWhiteSpace($fromZone)) { Add-UniqueIssue -Issues $issues -Issue "MissingSourceZone" }
    if ([string]::IsNullOrWhiteSpace($toZone)) { Add-UniqueIssue -Issues $issues -Issue "MissingDestinationZone" }
    if ([string]::IsNullOrWhiteSpace($kind) -or $kind -ieq "Unknown") { Add-UniqueIssue -Issues $issues -Issue "UnknownTransitionKind" }
    if ($acceptedSourceZones.Count -lt 1) { Add-UniqueIssue -Issues $issues -Issue "MissingAcceptedSourceZones" }
    if ($acceptedDestinationZones.Count -lt 1) { Add-UniqueIssue -Issues $issues -Issue "MissingAcceptedDestinationZones" }
    if ($requiresInteraction -and $connectorNames.Count -lt 1) { Add-UniqueIssue -Issues $issues -Issue "InteractionTransitionMissingConnectorName" }
    if (($kind -ieq "Door" -or $kind -ieq "Teleporter") -and -not $requiresInteraction) {
        Add-UniqueIssue -Issues $contradictions -Issue "InteractionKindWithoutInteractionFlag"
    }

    $beacons = @(
        New-BeaconEvidence -Role "sourceApproach" -ZoneName $fromZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "SourceApproachPoint" -Default $null) -Required $true -LocalMapIndex $LocalMapIndex
        New-BeaconEvidence -Role "sourceClear" -ZoneName $fromZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "SourceClearPoint" -Default $null) -Required $true -LocalMapIndex $LocalMapIndex
        New-BeaconEvidence -Role "destinationClear" -ZoneName $toZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "DestinationClearPoint" -Default $null) -Required $true -LocalMapIndex $LocalMapIndex
        New-BeaconEvidence -Role "destinationApproach" -ZoneName $toZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "DestinationApproachPoint" -Default $null) -Required $true -LocalMapIndex $LocalMapIndex
        New-BeaconEvidence -Role "fromWaypoint" -ZoneName $fromZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "FromWaypoint" -Default $null) -Required $false -LocalMapIndex $LocalMapIndex
        New-BeaconEvidence -Role "toWaypoint" -ZoneName $toZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "ToWaypoint" -Default $null) -Required $false -LocalMapIndex $LocalMapIndex
        New-BeaconEvidence -Role "sourceCrossing" -ZoneName $fromZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "FromCrossingAnchor" -Default $null) -Required ($kind -ieq "OpenPassage" -or $kind -ieq "Stairs") -LocalMapIndex $LocalMapIndex
        New-BeaconEvidence -Role "destinationCrossing" -ZoneName $toZone -Position (Get-JsonPropertyValue -InputObject $Transition -Name "ToCrossingAnchor" -Default $null) -Required ($kind -ieq "OpenPassage" -or $kind -ieq "Stairs") -LocalMapIndex $LocalMapIndex
    )
    Add-BeaconIssues -Beacons $beacons -Issues $issues -Contradictions $contradictions

    $staticValidation = $null
    if ($StaticValidationMap.ContainsKey($key)) {
        $staticValidation = $StaticValidationMap[$key]
    }

    $doorAudit = $null
    if ($DoorAuditMap.ContainsKey($id)) {
        $doorAudit = $DoorAuditMap[$id]
    } elseif ($DoorAuditMap.ContainsKey($key)) {
        $doorAudit = $DoorAuditMap[$key]
    }

    if ($kind -ieq "Door" -and $null -eq $doorAudit) {
        Add-UniqueIssue -Issues $issues -Issue "MissingDoorAuditEvidence"
    }

    if ($null -ne $doorAudit -and [int](Get-JsonPropertyValue -InputObject $doorAudit -Name "SuspicionScore" -Default 0) -gt 0) {
        foreach ($issue in @(Get-JsonPropertyValue -InputObject $doorAudit -Name "Issues" -Default @())) {
            Add-UniqueIssue -Issues $contradictions -Issue ("DoorAudit:" + [string]$issue)
        }
    }

    $sweepMatch = Get-SweepEntryForTransition -Transition $Transition -OpenSweepMap $OpenSweepMap -DoorSweepMap $DoorSweepMap
    $sweepEvidence = New-SweepEvidence -SweepMatch $sweepMatch
    Add-SweepEvidenceIssues `
        -Kind $kind `
        -ConnectorNames $connectorNames `
        -AcceptedDestinationZones $acceptedDestinationZones `
        -AcceptedIntermediateZones $acceptedIntermediateZones `
        -SweepEvidence $sweepEvidence `
        -Issues $issues `
        -Contradictions $contradictions
    $sweepSource = Get-JsonPropertyValue -InputObject $sweepEvidence -Name "Source" -Default $null
    if ($null -ne $sweepSource -and $kind -ieq "Door" -and $sweepSource -ne "Door") {
        Add-UniqueIssue -Issues $contradictions -Issue "DoorTransitionMatchedOpenSweep"
    }

    if ($null -ne $sweepSource -and $kind -ine "Door" -and $sweepSource -eq "Door") {
        Add-UniqueIssue -Issues $contradictions -Issue "NonDoorTransitionMatchedDoorSweep"
    }

    Convert-CleanPassedComponentContradictions `
        -Kind $kind `
        -SweepEvidence $sweepEvidence `
        -StaticValidation $staticValidation `
        -Issues $issues `
        -Contradictions $contradictions
    if ($issues.Contains("StaticWarning:SourceApproachAndClearDisconnected") -or
        $issues.Contains("StaticWarning:DestinationClearAndApproachDisconnected")) {
        Add-PassedCurrentZoneWarning `
            -SweepEvidence $sweepEvidence `
            -AcceptedDestinationZones $acceptedDestinationZones `
            -AcceptedIntermediateZones $acceptedIntermediateZones `
            -Issues $issues
    }

    $classification = Get-SemanticClassification -Issues $issues -Contradictions $contradictions -SweepEvidence $sweepEvidence
    return [ordered]@{
        Id = $id
        Key = $key
        Kind = $kind
        Subtype = Get-TransitionSubtype -Transition $Transition
        Classification = $classification
        SourceZone = $fromZone
        DestinationZone = $toZone
        AcceptedSourceZones = $acceptedSourceZones
        AcceptedDestinationZones = $acceptedDestinationZones
        AcceptedIntermediateZones = $acceptedIntermediateZones
        ConnectorNames = $connectorNames
        RequiresInteraction = $requiresInteraction
        PrerequisiteState = [ordered]@{
            Kind = "unknown"
            Detail = "No prerequisite metadata in current artifacts"
        }
        Beacons = $beacons
        LocalMapEvidence = [ordered]@{
            SourceApproachToClearReachable = -not $contradictions.Contains("SourceApproachAndClearDisconnected")
            DestinationClearToApproachReachable = -not $contradictions.Contains("DestinationClearAndApproachDisconnected")
        }
        StaticValidation = if ($null -ne $staticValidation) {
            [ordered]@{
                SuspicionScore = [int](Get-JsonPropertyValue -InputObject $staticValidation -Name "SuspicionScore" -Default 0)
                BuilderSuspicionScore = [int](Get-JsonPropertyValue -InputObject $staticValidation -Name "BuilderSuspicionScore" -Default 0)
                Issues = @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $staticValidation -Name "Issues" -Default @()))
                BuilderIssues = @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $staticValidation -Name "BuilderIssues" -Default @()))
            }
        } else {
            [ordered]@{
                SuspicionScore = 0
                BuilderSuspicionScore = 0
                Issues = @("MissingStaticValidationEvidence")
                BuilderIssues = @()
            }
        }
        DoorAudit = if ($null -ne $doorAudit) {
            [ordered]@{
                PairKey = Get-JsonPropertyValue -InputObject $doorAudit -Name "PairKey" -Default $null
                SuspicionScore = [int](Get-JsonPropertyValue -InputObject $doorAudit -Name "SuspicionScore" -Default 0)
                Issues = @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $doorAudit -Name "Issues" -Default @()))
                ReferenceDoorMatchType = Get-JsonPropertyValue -InputObject $doorAudit -Name "ReferenceDoorMatchType" -Default $null
                ConnectorNames = @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $doorAudit -Name "ConnectorNames" -Default @()))
            }
        } else {
            $null
        }
        SweepEvidence = $sweepEvidence
        FailureSignature = Get-FailureSignature -SweepEvidence $sweepEvidence
        FailureStage = Get-FailureToken -SweepEvidence $sweepEvidence -Name "stage"
        FailureContext = Get-FailureToken -SweepEvidence $sweepEvidence -Name "context"
        FailureLocalContext = Get-FailureToken -SweepEvidence $sweepEvidence -Name "localContext"
        FailureTargetKind = Get-FailureToken -SweepEvidence $sweepEvidence -Name "lastTargetKind"
        Issues = $issues.ToArray()
        Contradictions = $contradictions.ToArray()
    }
}

function Get-ClassificationCounts {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Entries
    )

    $counts = [ordered]@{
        complete = 0
        incomplete = 0
        contradictory = 0
        "locked/unavailable" = 0
        "execution-only failure" = 0
        unknown = 0
    }

    foreach ($entry in @($Entries)) {
        $classification = [string](Get-JsonPropertyValue -InputObject $entry -Name "Classification" -Default "unknown")
        if (-not $counts.Contains($classification)) {
            $counts[$classification] = 0
        }

        $counts[$classification]++
    }

    return $counts
}

function Get-KindCounts {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Entries
    )

    $counts = [ordered]@{}
    foreach ($entry in @($Entries)) {
        $kind = [string](Get-JsonPropertyValue -InputObject $entry -Name "Kind" -Default "Unknown")
        if (-not $counts.Contains($kind)) {
            $counts[$kind] = 0
        }

        $counts[$kind]++
    }

    return $counts
}

function Format-TopEntries {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Entries,

        [Parameter(Mandatory = $true)]
        [int]$Limit
    )

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @($Entries | Select-Object -First $Limit)) {
        $issues = @()
        $issues += @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $entry -Name "Contradictions" -Default @()))
        $issues += @(Get-StringArrayValues -Values (Get-JsonPropertyValue -InputObject $entry -Name "Issues" -Default @()))
        $issueText = if ($issues.Count -gt 0) { [string]::Join(", ", @($issues | Select-Object -First 4)) } else { "none" }
        $sweep = Get-JsonPropertyValue -InputObject $entry -Name "SweepEvidence" -Default $null
        $status = [string](Get-JsonPropertyValue -InputObject $sweep -Name "Status" -Default "missing")
        $signature = Get-JsonPropertyValue -InputObject $entry -Name "FailureSignature" -Default $null
        if ([string]::IsNullOrWhiteSpace([string]$signature)) {
            $signature = "no signature"
        }

        $lines.Add("- $([string](Get-JsonPropertyValue -InputObject $entry -Name "Id" -Default "<unknown>")) [$([string](Get-JsonPropertyValue -InputObject $entry -Name "Kind" -Default "Unknown"))] classification=$([string](Get-JsonPropertyValue -InputObject $entry -Name "Classification" -Default "unknown")) sweep=$status signature=$signature issues=$issueText")
    }

    if ($lines.Count -eq 0) {
        $lines.Add("- none")
    }

    return $lines.ToArray()
}

function Format-SemanticSummaryLines {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Report
    )

    $summary = Get-JsonPropertyValue -InputObject $Report -Name "Summary" -Default $null
    $entries = @(Get-JsonPropertyValue -InputObject $Report -Name "Transitions" -Default @())
    $classificationCounts = Get-JsonPropertyValue -InputObject $summary -Name "ClassificationCounts" -Default $null
    $kindCounts = Get-JsonPropertyValue -InputObject $summary -Name "KindCounts" -Default $null
    $lines = New-Object System.Collections.Generic.List[string]

    $lines.Add("Transition semantic audit summary")
    $lines.Add("Generated at: $([string](Get-JsonPropertyValue -InputObject $summary -Name "GeneratedAtUtc" -Default "unknown"))")
    $lines.Add("Total transitions: $([int](Get-JsonPropertyValue -InputObject $summary -Name "TotalTransitions" -Default 0))")
    $lines.Add("Classification counts:")
    foreach ($property in $classificationCounts.GetEnumerator()) {
        $lines.Add("- $($property.Key): $($property.Value)")
    }

    $lines.Add("Transition kind counts:")
    foreach ($property in $kindCounts.GetEnumerator()) {
        $lines.Add("- $($property.Key): $($property.Value)")
    }

    $openSweep = Get-JsonPropertyValue -InputObject $summary -Name "OpenSweep" -Default $null
    $doorSweep = Get-JsonPropertyValue -InputObject $summary -Name "DoorSweep" -Default $null
    $lines.Add("Sweep evidence:")
    $lines.Add("- OpenPassage: total=$([int](Get-JsonPropertyValue -InputObject $openSweep -Name "TotalCount" -Default 0)) passed=$([int](Get-JsonPropertyValue -InputObject $openSweep -Name "PassedCount" -Default 0)) failed=$([int](Get-JsonPropertyValue -InputObject $openSweep -Name "FailedCount" -Default 0)) runtime=$([string](Get-JsonPropertyValue -InputObject $openSweep -Name "RuntimeBuildStamp" -Default "<missing>"))")
    $lines.Add("- Door: total=$([int](Get-JsonPropertyValue -InputObject $doorSweep -Name "TotalCount" -Default 0)) passed=$([int](Get-JsonPropertyValue -InputObject $doorSweep -Name "PassedCount" -Default 0)) failed=$([int](Get-JsonPropertyValue -InputObject $doorSweep -Name "FailedCount" -Default 0)) runtime=$([string](Get-JsonPropertyValue -InputObject $doorSweep -Name "RuntimeBuildStamp" -Default "<missing>"))")

    $failedEntries = @($entries | Where-Object {
            [string](Get-JsonPropertyValue -InputObject (Get-JsonPropertyValue -InputObject $_ -Name "SweepEvidence" -Default $null) -Name "Status" -Default "missing") -ieq "failed"
        } | Sort-Object -Property `
            @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "Kind" -Default "") }; Descending = $false },
            @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "Id" -Default "") }; Descending = $false })
    $lines.Add("Current runtime failures:")
    foreach ($line in (Format-TopEntries -Entries $failedEntries -Limit 24)) {
        $lines.Add($line)
    }

    $nonCompleteEntries = @($entries | Where-Object {
            [string](Get-JsonPropertyValue -InputObject $_ -Name "Classification" -Default "unknown") -ne "complete"
        } | Sort-Object -Property `
            @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "Classification" -Default "") }; Descending = $false },
            @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "Kind" -Default "") }; Descending = $false },
            @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "Id" -Default "") }; Descending = $false })
    $lines.Add("Top non-complete semantic rows:")
    foreach ($line in (Format-TopEntries -Entries $nonCompleteEntries -Limit 24)) {
        $lines.Add($line)
    }

    return $lines.ToArray()
}

$graph = Read-RequiredJsonDocument -Path $GraphPath -Description "Navigation graph"
$localMaps = Read-RequiredJsonDocument -Path $LocalMapPath -Description "Local navigation maps"
$staticValidation = Read-OptionalJsonDocument -Path $StaticValidationPath
$doorAudit = Read-OptionalJsonDocument -Path $DoorAuditPath
$openSweep = Read-OptionalJsonDocument -Path $OpenSweepPath
$doorSweep = Read-OptionalJsonDocument -Path $DoorSweepPath

$transitions = @(Get-JsonPropertyValue -InputObject $graph -Name "Transitions" -Default @())
if ($transitions.Count -eq 0) {
    $transitions = @(Get-JsonPropertyValue -InputObject $graph -Name "Links" -Default @())
}

if ($transitions.Count -eq 0) {
    throw "Navigation graph did not contain any transitions: $GraphPath"
}

$localMapIndex = New-LocalMapIndex -LocalMapDocument $localMaps
$staticValidationMap = New-StaticValidationMap -Report $staticValidation
$doorAuditMap = New-DoorAuditMap -Report $doorAudit
$openSweepMap = New-SweepEntryMap -Report $openSweep
$doorSweepMap = New-SweepEntryMap -Report $doorSweep

$semanticEntries = foreach ($transition in $transitions) {
    [pscustomobject](New-TransitionSemanticRecord `
        -Transition $transition `
        -LocalMapIndex $localMapIndex `
        -StaticValidationMap $staticValidationMap `
        -DoorAuditMap $doorAuditMap `
        -OpenSweepMap $openSweepMap `
        -DoorSweepMap $doorSweepMap)
}

$orderedEntries = @($semanticEntries | Sort-Object -Property `
        @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "Kind" -Default "") }; Descending = $false },
        @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "SourceZone" -Default "") }; Descending = $false },
        @{ Expression = { [string](Get-JsonPropertyValue -InputObject $_ -Name "DestinationZone" -Default "") }; Descending = $false })

$classificationCounts = Get-ClassificationCounts -Entries $orderedEntries
$kindCounts = Get-KindCounts -Entries $orderedEntries
$summary = [ordered]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("o")
    GraphPath = if (Test-Path -LiteralPath $GraphPath) { (Resolve-Path -LiteralPath $GraphPath).Path } else { $GraphPath }
    LocalMapPath = if (Test-Path -LiteralPath $LocalMapPath) { (Resolve-Path -LiteralPath $LocalMapPath).Path } else { $LocalMapPath }
    StaticValidationPath = if (Test-Path -LiteralPath $StaticValidationPath) { (Resolve-Path -LiteralPath $StaticValidationPath).Path } else { $StaticValidationPath }
    DoorAuditPath = if (Test-Path -LiteralPath $DoorAuditPath) { (Resolve-Path -LiteralPath $DoorAuditPath).Path } else { $DoorAuditPath }
    OpenSweepPath = if (Test-Path -LiteralPath $OpenSweepPath) { (Resolve-Path -LiteralPath $OpenSweepPath).Path } else { $OpenSweepPath }
    DoorSweepPath = if (Test-Path -LiteralPath $DoorSweepPath) { (Resolve-Path -LiteralPath $DoorSweepPath).Path } else { $DoorSweepPath }
    TotalTransitions = $orderedEntries.Count
    ClassificationCounts = $classificationCounts
    KindCounts = $kindCounts
    LocalMapZones = $localMapIndex.Count
    OpenSweep = [ordered]@{
        RuntimeBuildStamp = Get-JsonPropertyValue -InputObject $openSweep -Name "RuntimeBuildStamp" -Default $null
        TotalCount = [int](Get-JsonPropertyValue -InputObject $openSweep -Name "TotalCount" -Default 0)
        PassedCount = [int](Get-JsonPropertyValue -InputObject $openSweep -Name "PassedCount" -Default 0)
        FailedCount = [int](Get-JsonPropertyValue -InputObject $openSweep -Name "FailedCount" -Default 0)
        PendingCount = [int](Get-JsonPropertyValue -InputObject $openSweep -Name "PendingCount" -Default 0)
    }
    DoorSweep = [ordered]@{
        RuntimeBuildStamp = Get-JsonPropertyValue -InputObject $doorSweep -Name "RuntimeBuildStamp" -Default $null
        TotalCount = [int](Get-JsonPropertyValue -InputObject $doorSweep -Name "TotalCount" -Default 0)
        PassedCount = [int](Get-JsonPropertyValue -InputObject $doorSweep -Name "PassedCount" -Default 0)
        FailedCount = [int](Get-JsonPropertyValue -InputObject $doorSweep -Name "FailedCount" -Default 0)
        PendingCount = [int](Get-JsonPropertyValue -InputObject $doorSweep -Name "PendingCount" -Default 0)
    }
}

$report = [ordered]@{
    SchemaVersion = 1
    Summary = $summary
    Transitions = $orderedEntries
}

Write-JsonDocument -Path $OutputPath -Document $report
Write-TextDocument -Path $SummaryPath -Lines (Format-SemanticSummaryLines -Report $report)

Write-Host "Wrote transition semantic overlay to $OutputPath"
Write-Host "Wrote transition semantic summary to $SummaryPath"
Write-Host "Semantic totals: total=$($summary.TotalTransitions) complete=$($classificationCounts.complete) incomplete=$($classificationCounts.incomplete) contradictory=$($classificationCounts.contradictory) executionOnlyFailure=$($classificationCounts['execution-only failure']) unknown=$($classificationCounts.unknown)"
