[CmdletBinding()]
param(
    [Parameter()]
    [string]$SceneDataPath = ".\artifacts\navigation\thirdpersongreybox-navigation-data.json",

    [Parameter()]
    [string]$InputGraphPath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\navigation_graph.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\navigation_graph.generated.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-Vec3 {
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

function Copy-Vec3 {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Source
    )

    return New-Vec3 -X ([double]$Source.x) -Y ([double]$Source.y) -Z ([double]$Source.z)
}

function Clamp-Value {
    param(
        [Parameter(Mandatory = $true)]
        [double]$Value,

        [Parameter(Mandatory = $true)]
        [double]$Minimum,

        [Parameter(Mandatory = $true)]
        [double]$Maximum
    )

    return [Math]::Max($Minimum, [Math]::Min($Maximum, $Value))
}

function Distance {
    param(
        [Parameter(Mandatory = $true)]
        [object]$A,

        [Parameter(Mandatory = $true)]
        [object]$B
    )

    $dx = [double]$A.x - [double]$B.x
    $dy = [double]$A.y - [double]$B.y
    $dz = [double]$A.z - [double]$B.z
    return [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
}

function Test-ZeroVector {
    param(
        [Parameter()]
        $Vector
    )

    if ($null -eq $Vector) {
        return $true
    }

    return [Math]::Abs([double]$Vector.x) -lt 0.0001 -and
        [Math]::Abs([double]$Vector.y) -lt 0.0001 -and
        [Math]::Abs([double]$Vector.z) -lt 0.0001
}

function Lerp-Vec3 {
    param(
        [Parameter(Mandatory = $true)]
        [object]$A,

        [Parameter(Mandatory = $true)]
        [object]$B,

        [Parameter(Mandatory = $true)]
        [double]$T
    )

    $clamped = Clamp-Value -Value $T -Minimum 0.0 -Maximum 1.0
    return New-Vec3 `
        -X ([double]$A.x + (([double]$B.x - [double]$A.x) * $clamped)) `
        -Y ([double]$A.y + (([double]$B.y - [double]$A.y) * $clamped)) `
        -Z ([double]$A.z + (([double]$B.z - [double]$A.z) * $clamped))
}

function Get-NormalizedFlatDirection {
    param(
        [Parameter(Mandatory = $true)]
        [object]$FromPoint,

        [Parameter(Mandatory = $true)]
        [object]$ToPoint
    )

    $dx = [double]$ToPoint.x - [double]$FromPoint.x
    $dz = [double]$ToPoint.z - [double]$FromPoint.z
    $length = [Math]::Sqrt(($dx * $dx) + ($dz * $dz))
    if ($length -le 0.0001) {
        return [ordered]@{
            x = 0.0
            z = 0.0
        }
    }

    return [ordered]@{
        x = $dx / $length
        z = $dz / $length
    }
}

function Get-OffsetPoint {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Origin,

        [Parameter(Mandatory = $true)]
        [object]$Direction,

        [Parameter(Mandatory = $true)]
        [double]$Distance
    )

    return New-Vec3 `
        -X ([double]$Origin.x + ([double]$Direction.x * $Distance)) `
        -Y ([double]$Origin.y) `
        -Z ([double]$Origin.z + ([double]$Direction.z * $Distance))
}

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

function Get-DeduplicatedVectorSequence {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Points
    )

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($point in @($Points)) {
        if (Test-ZeroVector $point) {
            continue
        }

        $duplicate = $false
        foreach ($existing in $result) {
            if ((Distance -A $existing -B $point) -le 0.05) {
                $duplicate = $true
                break
            }
        }

        if (-not $duplicate) {
            $result.Add((Copy-Vec3 $point))
        }
    }

    return $result.ToArray()
}

function Get-MetadataValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Specialized.OrderedDictionary]$Metadata,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter()]
        $DefaultValue = $null
    )

    if ($Metadata.Contains($Key)) {
        return $Metadata[$Key]
    }

    return $DefaultValue
}

function Get-BaseFamilyZones {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$CameraSpaces,

        [Parameter(Mandatory = $true)]
        [string]$BaseName
    )

    $pattern = '^' + [regex]::Escape($BaseName) + '(\d+)?$'
    return @($CameraSpaces | Where-Object { $_.Name -match $pattern })
}

function Get-ZoneBounds {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone
    )

    if ($null -eq $Zone) {
        return $null
    }

    $halfX = [Math]::Abs([double]$Zone.Scale.x) / 2.0
    $halfY = [Math]::Abs([double]$Zone.Scale.y) / 2.0
    $halfZ = [Math]::Abs([double]$Zone.Scale.z) / 2.0
    if ($halfX -le 0.0 -and $halfY -le 0.0 -and $halfZ -le 0.0) {
        return $null
    }

    return [ordered]@{
        MinX = [double]$Zone.Position.x - $halfX
        MaxX = [double]$Zone.Position.x + $halfX
        MinZ = [double]$Zone.Position.z - $halfZ
        MaxZ = [double]$Zone.Position.z + $halfZ
    }
}

function Get-NearestPointOnZoneGeometry {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Zone,

        [Parameter(Mandatory = $true)]
        [object]$TargetPosition
    )

    $bounds = Get-ZoneBounds -Zone $Zone
    if ($null -eq $bounds) {
        return Copy-Vec3 $Zone.Position
    }

    return New-Vec3 `
        -X (Clamp-Value -Value ([double]$TargetPosition.x) -Minimum $bounds.MinX -Maximum $bounds.MaxX) `
        -Y ([double]$Zone.Position.y) `
        -Z (Clamp-Value -Value ([double]$TargetPosition.z) -Minimum $bounds.MinZ -Maximum $bounds.MaxZ)
}

function Get-ZoneFamilyCandidates {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZoneName
    )

    $candidates = New-Object System.Collections.Generic.List[object]
    $seen = @{}

    if ($zonesByName.ContainsKey($ZoneName) -and -not $seen.ContainsKey($ZoneName)) {
        $seen[$ZoneName] = $true
        $candidates.Add($zonesByName[$ZoneName])
    }

    foreach ($familyZone in @(Get-BaseFamilyZones -CameraSpaces $sceneData.CameraSpaces -BaseName $ZoneName)) {
        if ($null -ne $familyZone -and -not $seen.ContainsKey($familyZone.Name)) {
            $seen[$familyZone.Name] = $true
            $candidates.Add($familyZone)
        }
    }

    return $candidates.ToArray()
}

function Get-NearestGeometryPointPair {
    param(
        [Parameter(Mandatory = $true)]
        [object]$FromZone,

        [Parameter(Mandatory = $true)]
        [object]$ToZone
    )

    $fromBounds = Get-ZoneBounds -Zone $FromZone
    $toBounds = Get-ZoneBounds -Zone $ToZone

    if ($null -eq $fromBounds -and $null -eq $toBounds) {
        $fromPoint = Copy-Vec3 $FromZone.Position
        $toPoint = Copy-Vec3 $ToZone.Position
    } elseif ($null -eq $fromBounds) {
        $fromPoint = Copy-Vec3 $FromZone.Position
        $toPoint = Get-NearestPointOnZoneGeometry -Zone $ToZone -TargetPosition $FromZone.Position
    } elseif ($null -eq $toBounds) {
        $fromPoint = Get-NearestPointOnZoneGeometry -Zone $FromZone -TargetPosition $ToZone.Position
        $toPoint = Copy-Vec3 $ToZone.Position
    } else {
        $fromMidX = ([double]$FromZone.Position.x + [double]$ToZone.Position.x) / 2.0
        $fromMidZ = ([double]$FromZone.Position.z + [double]$ToZone.Position.z) / 2.0

        if ($fromBounds.MaxX -lt $toBounds.MinX) {
            $fromX = $fromBounds.MaxX
            $toX = $toBounds.MinX
        } elseif ($toBounds.MaxX -lt $fromBounds.MinX) {
            $fromX = $fromBounds.MinX
            $toX = $toBounds.MaxX
        } else {
            $sharedMinX = [Math]::Max($fromBounds.MinX, $toBounds.MinX)
            $sharedMaxX = [Math]::Min($fromBounds.MaxX, $toBounds.MaxX)
            $sharedX = Clamp-Value -Value $fromMidX -Minimum $sharedMinX -Maximum $sharedMaxX
            $fromX = $sharedX
            $toX = $sharedX
        }

        if ($fromBounds.MaxZ -lt $toBounds.MinZ) {
            $fromZ = $fromBounds.MaxZ
            $toZ = $toBounds.MinZ
        } elseif ($toBounds.MaxZ -lt $fromBounds.MinZ) {
            $fromZ = $fromBounds.MinZ
            $toZ = $toBounds.MaxZ
        } else {
            $sharedMinZ = [Math]::Max($fromBounds.MinZ, $toBounds.MinZ)
            $sharedMaxZ = [Math]::Min($fromBounds.MaxZ, $toBounds.MaxZ)
            $sharedZ = Clamp-Value -Value $fromMidZ -Minimum $sharedMinZ -Maximum $sharedMaxZ
            $fromZ = $sharedZ
            $toZ = $sharedZ
        }

        $fromPoint = New-Vec3 -X $fromX -Y ([double]$FromZone.Position.y) -Z $fromZ
        $toPoint = New-Vec3 -X $toX -Y ([double]$ToZone.Position.y) -Z $toZ
    }

    $dx = [double]$fromPoint.x - [double]$toPoint.x
    $dz = [double]$fromPoint.z - [double]$toPoint.z
    $gap2d = [Math]::Sqrt(($dx * $dx) + ($dz * $dz))

    return [ordered]@{
        FromPoint = $fromPoint
        ToPoint = $toPoint
        Gap2d = $gap2d
    }
}

function Get-OpenPassageGeometry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FromZoneName,

        [Parameter(Mandatory = $true)]
        [string]$ToZoneName
    )

    $fromCandidates = @(Get-ZoneFamilyCandidates -ZoneName $FromZoneName)
    $toCandidates = @(Get-ZoneFamilyCandidates -ZoneName $ToZoneName)
    if ($fromCandidates.Count -eq 0 -or $toCandidates.Count -eq 0) {
        throw "Unable to resolve open-passage geometry for '$FromZoneName' -> '$ToZoneName'"
    }

    $bestMatch = $null
    foreach ($fromZone in $fromCandidates) {
        foreach ($toZone in $toCandidates) {
            $pointPair = Get-NearestGeometryPointPair -FromZone $fromZone -ToZone $toZone
            $centerDistance = Distance -A $fromZone.Position -B $toZone.Position
            if ($null -eq $bestMatch -or
                $pointPair.Gap2d -lt $bestMatch.Gap2d -or
                ($pointPair.Gap2d -eq $bestMatch.Gap2d -and $centerDistance -lt $bestMatch.CenterDistance)) {
                $bestMatch = [ordered]@{
                    FromZone = $fromZone
                    ToZone = $toZone
                    FromPoint = $pointPair.FromPoint
                    ToPoint = $pointPair.ToPoint
                    Gap2d = $pointPair.Gap2d
                    CenterDistance = $centerDistance
                }
            }
        }
    }

    return [ordered]@{
        FromWaypoint = Copy-Vec3 $bestMatch.FromZone.Position
        ToWaypoint = Copy-Vec3 $bestMatch.ToZone.Position
        FromCrossingAnchor = $bestMatch.FromPoint
        ToCrossingAnchor = $bestMatch.ToPoint
    }
}

if (-not (Test-Path -LiteralPath $SceneDataPath)) {
    throw "Scene navigation data not found: $SceneDataPath"
}

if (-not (Test-Path -LiteralPath $InputGraphPath)) {
    throw "Input navigation graph not found: $InputGraphPath"
}

$sceneData = Get-Content -LiteralPath $SceneDataPath -Raw | ConvertFrom-Json
$inputGraph = Get-Content -LiteralPath $InputGraphPath -Raw | ConvertFrom-Json

$inputLinks = @()
$linksProperty = $inputGraph.PSObject.Properties["Links"]
if ($null -ne $linksProperty -and $null -ne $linksProperty.Value) {
    $inputLinks = @($linksProperty.Value)
} else {
    $transitionsProperty = $inputGraph.PSObject.Properties["Transitions"]
    if ($null -eq $transitionsProperty -or $null -eq $transitionsProperty.Value) {
        throw "Input navigation graph does not expose Links or Transitions."
    }

    $links = New-Object System.Collections.ArrayList
    $seenUndirectedKeys = @{}
    foreach ($transition in @($transitionsProperty.Value)) {
        if ($null -eq $transition -or
            [string]::IsNullOrWhiteSpace($transition.FromZone) -or
            [string]::IsNullOrWhiteSpace($transition.ToZone)) {
            continue
        }

        $fromZone = [string]$transition.FromZone
        $toZone = [string]$transition.ToZone
        if ([System.StringComparer]::OrdinalIgnoreCase.Compare($fromZone, $toZone) -le 0) {
            $undirectedKey = $fromZone + "|" + $toZone
        } else {
            $undirectedKey = $toZone + "|" + $fromZone
        }

        if ($seenUndirectedKeys.ContainsKey($undirectedKey)) {
            continue
        }

        $seenUndirectedKeys[$undirectedKey] = $true
        [void]$links.Add([pscustomobject]@{
            FromZone = $fromZone
            ToZone = $toZone
        })
    }

    $inputLinks = @($links)
}
$sceneNameProperty = $inputGraph.PSObject.Properties["SceneName"]
$inputSceneName = if ($null -ne $sceneNameProperty -and -not [string]::IsNullOrWhiteSpace($sceneNameProperty.Value)) {
    [string]$sceneNameProperty.Value
} else {
    "Date Everything House"
}

$zonesByName = @{}
foreach ($zone in $sceneData.CameraSpaces) {
    $zonesByName[$zone.Name] = $zone
}

$camerasByName = @{}
foreach ($camera in $sceneData.CameraObjects) {
    $camerasByName[$camera.Name] = $camera
}

$teleporterByName = @{}
foreach ($teleporter in @($sceneData.Teleporters)) {
    $teleporterByName[$teleporter.Name] = $teleporter
}

$doorByName = @{}
foreach ($door in @($sceneData.DoorObjects)) {
    if ([string]::IsNullOrWhiteSpace($door.Name) -or $doorByName.ContainsKey($door.Name)) {
        continue
    }

    $doorByName[$door.Name] = $door
}

function Get-ZoneWaypoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZoneName
    )

    if (-not $zonesByName.ContainsKey($ZoneName)) {
        throw "Unknown zone '$ZoneName'"
    }

    return Copy-Vec3 $zonesByName[$ZoneName].Position
}

function Get-CameraWaypoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CameraName
    )

    if (-not $camerasByName.ContainsKey($CameraName)) {
        throw "Unknown camera '$CameraName'"
    }

    return Copy-Vec3 $camerasByName[$CameraName].Position
}

function Get-TeleporterWaypoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TeleporterName,

        [Parameter(Mandatory = $true)]
        [ValidateSet("LocationDown", "LocationUp")]
        [string]$PropertyName
    )

    if (-not $teleporterByName.ContainsKey($TeleporterName)) {
        throw "Unknown teleporter '$TeleporterName'"
    }

    $teleporter = $teleporterByName[$TeleporterName]
    return Copy-Vec3 $teleporter.$PropertyName.Position
}

function Get-GraphZoneSceneCandidates {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZoneName
    )

    return @(Get-ZoneFamilyCandidates -ZoneName $ZoneName)
}

function Get-ConnectorObjectPosition {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StepKind,

        [Parameter()]
        [AllowNull()]
        [string]$ConnectorName,

        [Parameter(Mandatory = $true)]
        [object]$FromWaypoint,

        [Parameter(Mandatory = $true)]
        [object]$ToWaypoint
    )

    if (-not [string]::IsNullOrWhiteSpace($ConnectorName)) {
        if ($doorByName.ContainsKey($ConnectorName) -and $null -ne $doorByName[$ConnectorName].Position) {
            return Copy-Vec3 $doorByName[$ConnectorName].Position
        }

        if ($teleporterByName.ContainsKey($ConnectorName) -and $null -ne $teleporterByName[$ConnectorName].Position) {
            return Copy-Vec3 $teleporterByName[$ConnectorName].Position
        }
    }

    return Lerp-Vec3 -A $FromWaypoint -B $ToWaypoint -T 0.5
}

function Get-SceneZoneNodeId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GraphZoneName,

        [Parameter(Mandatory = $true)]
        [string]$SceneZoneName
    )

    return "zone:$GraphZoneName|scene:$SceneZoneName"
}

function Resolve-NearestSceneZoneNodeId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GraphZoneName,

        [Parameter(Mandatory = $true)]
        [object]$ReferencePoint,

        [Parameter(Mandatory = $true)]
        [hashtable]$SceneZoneNodeIdByName
    )

    $candidates = @(Get-GraphZoneSceneCandidates -ZoneName $GraphZoneName)
    if ($candidates.Count -eq 0) {
        return $null
    }

    $bestNodeId = $null
    $bestDistance = [double]::PositiveInfinity
    foreach ($candidate in $candidates) {
        if ($null -eq $candidate -or [string]::IsNullOrWhiteSpace($candidate.Name)) {
            continue
        }

        if (-not $SceneZoneNodeIdByName.ContainsKey($candidate.Name)) {
            continue
        }

        $distance = Distance -A $candidate.Position -B $ReferencePoint
        if ($distance -lt $bestDistance) {
            $bestDistance = $distance
            $bestNodeId = $SceneZoneNodeIdByName[$candidate.Name]
        }
    }

    return $bestNodeId
}

function Get-ZoneValidationCandidates {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GraphZoneName,

        [Parameter()]
        [AllowNull()]
        [string]$PrimarySceneZoneName
    )

    $names = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($PrimarySceneZoneName)) {
        $names.Add($PrimarySceneZoneName)
    }

    foreach ($candidate in @(Get-GraphZoneSceneCandidates -ZoneName $GraphZoneName)) {
        if ($null -eq $candidate -or [string]::IsNullOrWhiteSpace($candidate.Name)) {
            continue
        }

        $names.Add($candidate.Name)
    }

    if ($names.Count -eq 0) {
        $names.Add($GraphZoneName)
    }

    return Get-UniqueStringArray -Values $names.ToArray()
}

function Get-ConnectorGeometry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StepKind,

        [Parameter(Mandatory = $true)]
        [object]$WaypointPair,

        [Parameter(Mandatory = $true)]
        [System.Collections.Specialized.OrderedDictionary]$Metadata
    )

    $fromWaypoint = Copy-Vec3 $WaypointPair.FromWaypoint
    $toWaypoint = Copy-Vec3 $WaypointPair.ToWaypoint
    $fromCrossingAnchor = Get-MetadataValue -Metadata $WaypointPair -Key "FromCrossingAnchor"
    $toCrossingAnchor = Get-MetadataValue -Metadata $WaypointPair -Key "ToCrossingAnchor"
    $connectorName = Get-MetadataValue -Metadata $Metadata -Key "ConnectorName"
    $connectorPosition = Get-ConnectorObjectPosition -StepKind $StepKind -ConnectorName $connectorName -FromWaypoint $fromWaypoint -ToWaypoint $toWaypoint
    $direction = Get-NormalizedFlatDirection -FromPoint $fromWaypoint -ToPoint $toWaypoint
    $clearDistance = if ($StepKind -eq "Door") { 0.8 } elseif ($StepKind -eq "Stairs") { 1.1 } else { 0.65 }
    $sourceApproachPoint = $fromWaypoint
    $destinationApproachPoint = $toWaypoint
    $sourceClearPoint = $null
    $destinationClearPoint = $null
    $assetDerivationSource = "ZoneCenterFallback"

    switch ($StepKind) {
        "OpenPassage" {
            $sourceClearPoint = if (-not (Test-ZeroVector $fromCrossingAnchor)) { Copy-Vec3 $fromCrossingAnchor } else { Lerp-Vec3 -A $fromWaypoint -B $toWaypoint -T 0.4 }
            $destinationClearPoint = if (-not (Test-ZeroVector $toCrossingAnchor)) { Copy-Vec3 $toCrossingAnchor } else { Lerp-Vec3 -A $fromWaypoint -B $toWaypoint -T 0.6 }
            $assetDerivationSource = "CameraSpaceBoundaryGeometry"
            break
        }
        "Teleporter" {
            $sourceClearPoint = $fromWaypoint
            $destinationClearPoint = $toWaypoint
            $assetDerivationSource = "TeleporterEndpoints"
            break
        }
        "Door" {
            $sourceClearPoint = Get-OffsetPoint -Origin $connectorPosition -Direction $direction -Distance (-1.0 * $clearDistance)
            $destinationClearPoint = Get-OffsetPoint -Origin $connectorPosition -Direction $direction -Distance $clearDistance
            $assetDerivationSource = if (-not [string]::IsNullOrWhiteSpace($connectorName)) { "DoorObjectAndCameraWaypoints" } else { "DoorCameraWaypoints" }
            break
        }
        "Stairs" {
            $sourceClearPoint = Lerp-Vec3 -A $fromWaypoint -B $toWaypoint -T 0.4
            $destinationClearPoint = Lerp-Vec3 -A $fromWaypoint -B $toWaypoint -T 0.6
            $assetDerivationSource = "StairCameraWaypoints"
            break
        }
        default {
            $sourceClearPoint = Lerp-Vec3 -A $fromWaypoint -B $toWaypoint -T 0.4
            $destinationClearPoint = Lerp-Vec3 -A $fromWaypoint -B $toWaypoint -T 0.6
            break
        }
    }

    return [ordered]@{
        SourceApproachPoint = $sourceApproachPoint
        SourceClearPoint = $sourceClearPoint
        DestinationClearPoint = $destinationClearPoint
        DestinationApproachPoint = $destinationApproachPoint
        ConnectorObjectPosition = $connectorPosition
        NavigationPoints = @(Get-DeduplicatedVectorSequence -Points @(
                $sourceApproachPoint,
                $sourceClearPoint,
                $destinationClearPoint,
                $destinationApproachPoint))
        AssetDerivationSource = $assetDerivationSource
    }
}

function Get-TransitionValidationMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$TransitionData
    )

    $issues = New-Object System.Collections.Generic.List[string]
    $score = 0

    $fromWaypointMissing = Test-ZeroVector $TransitionData.FromWaypoint
    $toWaypointMissing = Test-ZeroVector $TransitionData.ToWaypoint
    $fromCrossMissing = Test-ZeroVector $TransitionData.FromCrossingAnchor
    $toCrossMissing = Test-ZeroVector $TransitionData.ToCrossingAnchor
    $sourceClearMissing = Test-ZeroVector $TransitionData.SourceClearPoint
    $destinationClearMissing = Test-ZeroVector $TransitionData.DestinationClearPoint

    if ($fromWaypointMissing) { $issues.Add("MissingFromWaypoint"); $score += 4 }
    if ($toWaypointMissing) { $issues.Add("MissingToWaypoint"); $score += 4 }
    if ($sourceClearMissing) { $issues.Add("MissingSourceClearPoint"); $score += 2 }
    if ($destinationClearMissing) { $issues.Add("MissingDestinationClearPoint"); $score += 2 }

    $waypointDistance = if ($fromWaypointMissing -or $toWaypointMissing) { -1 } else { Distance -A $TransitionData.FromWaypoint -B $TransitionData.ToWaypoint }
    $crossingDistance = if ($fromCrossMissing -or $toCrossMissing) { -1 } else { Distance -A $TransitionData.FromCrossingAnchor -B $TransitionData.ToCrossingAnchor }
    $heightDelta = if ($fromWaypointMissing -or $toWaypointMissing) { -1 } else { [Math]::Abs([double]$TransitionData.ToWaypoint.y - [double]$TransitionData.FromWaypoint.y) }
    $sourceApproachToClearDistance = if ($fromWaypointMissing -or $sourceClearMissing) { -1 } else { Distance -A $TransitionData.FromWaypoint -B $TransitionData.SourceClearPoint }
    $destinationClearToApproachDistance = if ($toWaypointMissing -or $destinationClearMissing) { -1 } else { Distance -A $TransitionData.DestinationClearPoint -B $TransitionData.ToWaypoint }

    if ($waypointDistance -gt 18) { $issues.Add("LongWaypointSpan"); $score += 2 }
    if ($heightDelta -gt 4) { $issues.Add("LargeHeightDelta"); $score += 2 }

    if ($TransitionData.StepKind -eq "OpenPassage") {
        if ($fromCrossMissing) { $issues.Add("MissingFromCrossingAnchor"); $score += 3 }
        if ($toCrossMissing) { $issues.Add("MissingToCrossingAnchor"); $score += 3 }
        if ($crossingDistance -gt 5) { $issues.Add("WideCrossingGap"); $score += 2 }
        if ($crossingDistance -ge 0 -and $crossingDistance -lt 0.25) { $issues.Add("CollapsedCrossingGap"); $score += 2 }
    } else {
        if (-not (Test-ZeroVector $TransitionData.ConnectorObjectPosition) -and $sourceApproachToClearDistance -gt 8) {
            $issues.Add("FarFromConnectorSourceApproach")
            $score += 1
        }

        if (-not (Test-ZeroVector $TransitionData.ConnectorObjectPosition) -and $destinationClearToApproachDistance -gt 8) {
            $issues.Add("FarFromConnectorDestinationApproach")
            $score += 1
        }
    }

    if (($TransitionData.StepKind -eq "Door" -or $TransitionData.StepKind -eq "Teleporter") -and -not $TransitionData.RequiresInteraction) {
        $issues.Add("InteractionStepWithoutInteractionFlag")
        $score += 2
    }

    if ($TransitionData.StepKind -eq "Stairs" -and $heightDelta -lt 1 -and $heightDelta -ge 0) {
        $issues.Add("StairsWithSmallHeightDelta")
        $score += 1
    }

    $recommendedTimeoutSeconds = 5.0
    if ($TransitionData.TransitionWaitSeconds -gt 0.0) {
        $recommendedTimeoutSeconds = [Math]::Round([Math]::Max(5.0, [double]$TransitionData.TransitionWaitSeconds + 3.0), 2)
    } elseif ($TransitionData.StepKind -eq "Door") {
        $recommendedTimeoutSeconds = 8.0
    } elseif ($TransitionData.StepKind -eq "Stairs") {
        $recommendedTimeoutSeconds = 7.0
    }

    return [ordered]@{
        AcceptedSourceZones = @(Get-ZoneValidationCandidates -GraphZoneName $TransitionData.FromZone -PrimarySceneZoneName $TransitionData.SourceSceneZoneName)
        AcceptedDestinationZones = @(Get-ZoneValidationCandidates -GraphZoneName $TransitionData.ToZone -PrimarySceneZoneName $TransitionData.DestinationSceneZoneName)
        StepTimeoutSeconds = $recommendedTimeoutSeconds
        StaticSuspicionScore = $score
        StaticIssues = @($issues)
        AssetDerivationSource = $TransitionData.AssetDerivationSource
        SourceSceneZoneName = $TransitionData.SourceSceneZoneName
        DestinationSceneZoneName = $TransitionData.DestinationSceneZoneName
    }
}

$directedOverrides = @{
    "hallway|office" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianOfficeDoor2"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianOfficeDoor1"
    }
    "office|office_closet" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianOfficeClosetDoor1"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianOfficeClosetDoor2"
    }
    "office|crawlspace" = [ordered]@{
        FromWaypoint = Get-TeleporterWaypoint -TeleporterName "CrawlspaceLadder" -PropertyName "LocationUp"
        ToWaypoint = Get-TeleporterWaypoint -TeleporterName "CrawlspaceLadder" -PropertyName "LocationDown"
    }
    "upper_hallway|bedroom" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianBedroomDoor1"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianBedroomDoor2"
    }
    "bedroom|bedroom_closet" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianBedroomClosetDoor1"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianBedroomClosetDoor2"
    }
    "bedroom|bathroom2" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianBathroom2Door2"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianBathroom2Door1"
    }
    "hallway|bathroom1" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianBathroomDoor2"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianBathroomDoor1"
    }
    "upper_hallway|hallway" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_StairsDown"
        ToWaypoint = Get-CameraWaypoint "Camera_StairsUp"
    }
    "hallway_arma|hallway" = [ordered]@{
        FromWaypoint = Get-ZoneWaypoint "hallway4"
        ToWaypoint = Get-ZoneWaypoint "hallway_arma"
    }
    "laundry_room|laundry_room_closet" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianLaundryCloset1"
        ToWaypoint = Get-CameraWaypoint "Camera_Laundry Room_Closet"
    }
    "upper_hallway|gym" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianGymDoor2"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianGymDoor1"
    }
    "gym|gym_closet" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianGymClosetDoor1"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianGymClosetDoor2"
    }
    "upper_hallway|attic" = [ordered]@{
        FromWaypoint = Get-CameraWaypoint "Camera_DorianAtticDoor1"
        ToWaypoint = Get-CameraWaypoint "Camera_DorianAtticDoor2"
    }
}

$stepMetadata = @{
    "living_room_tutorial|living_room" = [ordered]@{
        StepKind = "OpenPassage"
    }
    "frontdoor|living_room_tutorial" = [ordered]@{
        StepKind = "OpenPassage"
    }
    "hallway|living_room" = [ordered]@{
        StepKind = "OpenPassage"
    }
    "living_room|dining_room" = [ordered]@{
        StepKind = "OpenPassage"
    }
    "dining_room|piano_room" = [ordered]@{
        StepKind = "OpenPassage"
    }
    "dining_room|kitchen" = [ordered]@{
        StepKind = "OpenPassage"
    }
    "hallway|office" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Office"
    }
    "office|office_closet" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Office_Closet"
    }
    "office|crawlspace" = [ordered]@{
        StepKind = "Teleporter"
        ConnectorName = "CrawlspaceLadder"
        RequiresInteraction = $true
        TransitionWaitSeconds = 12.0
    }
    "upper_hallway|bedroom" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Bedroom"
    }
    "bedroom|bedroom_closet" = [ordered]@{
        StepKind = "Door"
    }
    "bedroom|bathroom2" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Bedroom_Bathroom"
    }
    "dorian_bathroom2_2|bathroom2" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Bedroom_Bathroom"
    }
    "hallway|bathroom1" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Bathroom1"
    }
    "upper_hallway|hallway" = [ordered]@{
        StepKind = "Stairs"
    }
    "hallway_arma|hallway" = [ordered]@{
        StepKind = "Stairs"
    }
    "laundry_room|laundry_room_closet" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Laundry_Closet"
    }
    "upper_hallway|gym" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Gym_Hall"
    }
    "gym|gym_closet" = [ordered]@{
        StepKind = "Door"
    }
    "upper_hallway|attic" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "AtticDoors"
    }
}

$graphZoneNames = New-Object System.Collections.Generic.List[string]
$seenGraphZones = @{}
foreach ($link in $inputLinks) {
    foreach ($zoneName in @($link.FromZone, $link.ToZone)) {
        if ([string]::IsNullOrWhiteSpace($zoneName) -or $seenGraphZones.ContainsKey($zoneName)) {
            continue
        }

        $seenGraphZones[$zoneName] = $true
        $graphZoneNames.Add($zoneName)
    }
}

$generatedZones = New-Object System.Collections.Generic.List[object]
$generatedNodes = New-Object System.Collections.Generic.List[object]
$nodeById = @{}
$sceneZoneNodeIdByName = @{}

foreach ($graphZoneName in $graphZoneNames) {
    $sceneCandidates = @(Get-GraphZoneSceneCandidates -ZoneName $graphZoneName)
    $nodeIds = New-Object System.Collections.Generic.List[string]
    $sceneZoneNames = New-Object System.Collections.Generic.List[string]

    foreach ($sceneZone in $sceneCandidates) {
        if ($null -eq $sceneZone -or [string]::IsNullOrWhiteSpace($sceneZone.Name)) {
            continue
        }

        $nodeId = Get-SceneZoneNodeId -GraphZoneName $graphZoneName -SceneZoneName $sceneZone.Name
        $sceneZoneNodeIdByName[$sceneZone.Name] = $nodeId
        $sceneZoneNames.Add($sceneZone.Name)
        $nodeIds.Add($nodeId)

        $node = [ordered]@{
            Id = $nodeId
            Zone = $graphZoneName
            SceneZoneName = $sceneZone.Name
            Kind = if ($sceneZone.Name -eq $graphZoneName) { "ZoneCenter" } else { "RoomSubZone" }
            Position = Copy-Vec3 $sceneZone.Position
            Scale = Copy-Vec3 $sceneZone.Scale
            Source = "CameraSpaces"
        }

        $generatedNodes.Add($node)
        $nodeById[$nodeId] = $node
    }

    $exactNodeId = $null
    if ($zonesByName.ContainsKey($graphZoneName)) {
        $exactNodeId = Get-SceneZoneNodeId -GraphZoneName $graphZoneName -SceneZoneName $graphZoneName
    }

    $generatedZones.Add([ordered]@{
        Id = "zone:$graphZoneName"
        Name = $graphZoneName
        NodeIds = @($nodeIds)
        CenterNodeId = if (-not [string]::IsNullOrWhiteSpace($exactNodeId)) { $exactNodeId } elseif ($nodeIds.Count -gt 0) { $nodeIds[0] } else { $null }
        SceneZoneNames = @(Get-UniqueStringArray -Values $sceneZoneNames.ToArray())
    })
}

$generatedTransitions = New-Object System.Collections.Generic.List[object]
foreach ($link in $inputLinks) {
    $key = "$($link.FromZone)|$($link.ToZone)"
    $waypointPair = $null
    $metadata = $stepMetadata[$key]
    if ($null -eq $metadata) {
        $metadata = [ordered]@{
            StepKind = "OpenPassage"
        }
    }

    $stepKind = Get-MetadataValue -Metadata $metadata -Key "StepKind" -DefaultValue "OpenPassage"
    $requiresInteraction = [bool](Get-MetadataValue -Metadata $metadata -Key "RequiresInteraction" -DefaultValue ($stepKind -eq "Door" -or $stepKind -eq "Teleporter"))

    if ($directedOverrides.ContainsKey($key)) {
        $waypointPair = $directedOverrides[$key]
    } elseif ($stepKind -eq "OpenPassage") {
        $waypointPair = Get-OpenPassageGeometry -FromZoneName $link.FromZone -ToZoneName $link.ToZone
    } else {
        $waypointPair = [ordered]@{
            FromWaypoint = Get-ZoneWaypoint $link.FromZone
            ToWaypoint = Get-ZoneWaypoint $link.ToZone
            FromCrossingAnchor = $null
            ToCrossingAnchor = $null
        }
    }

    $cost = [Math]::Round((Distance -A $waypointPair.FromWaypoint -B $waypointPair.ToWaypoint), 4)
    if ($cost -le 0.0) {
        $cost = 1.0
    }

    $connectorGeometry = Get-ConnectorGeometry -StepKind $stepKind -WaypointPair $waypointPair -Metadata $metadata
    $fromNodeId = Resolve-NearestSceneZoneNodeId -GraphZoneName $link.FromZone -ReferencePoint $connectorGeometry.SourceApproachPoint -SceneZoneNodeIdByName $sceneZoneNodeIdByName
    $toNodeId = Resolve-NearestSceneZoneNodeId -GraphZoneName $link.ToZone -ReferencePoint $connectorGeometry.DestinationApproachPoint -SceneZoneNodeIdByName $sceneZoneNodeIdByName
    $sourceSceneZoneName = if ($null -ne $fromNodeId -and $nodeById.ContainsKey($fromNodeId)) { $nodeById[$fromNodeId].SceneZoneName } else { $null }
    $destinationSceneZoneName = if ($null -ne $toNodeId -and $nodeById.ContainsKey($toNodeId)) { $nodeById[$toNodeId].SceneZoneName } else { $null }

    $transition = [ordered]@{
        Id = "transition:$($link.FromZone)->$($link.ToZone)"
        FromZone = $link.FromZone
        ToZone = $link.ToZone
        FromNodeId = $fromNodeId
        ToNodeId = $toNodeId
        FromWaypoint = $waypointPair.FromWaypoint
        ToWaypoint = $waypointPair.ToWaypoint
        FromCrossingAnchor = Get-MetadataValue -Metadata $waypointPair -Key "FromCrossingAnchor"
        ToCrossingAnchor = Get-MetadataValue -Metadata $waypointPair -Key "ToCrossingAnchor"
        SourceApproachPoint = $connectorGeometry.SourceApproachPoint
        SourceClearPoint = $connectorGeometry.SourceClearPoint
        DestinationClearPoint = $connectorGeometry.DestinationClearPoint
        DestinationApproachPoint = $connectorGeometry.DestinationApproachPoint
        NavigationPoints = $connectorGeometry.NavigationPoints
        ConnectorObjectPosition = $connectorGeometry.ConnectorObjectPosition
        Cost = $cost
        StepKind = $stepKind
        ConnectorName = Get-MetadataValue -Metadata $metadata -Key "ConnectorName"
        RequiresInteraction = $requiresInteraction
        TransitionWaitSeconds = [double](Get-MetadataValue -Metadata $metadata -Key "TransitionWaitSeconds" -DefaultValue 0.0)
        Connector = [ordered]@{
            Name = Get-MetadataValue -Metadata $metadata -Key "ConnectorName"
            ObjectPosition = $connectorGeometry.ConnectorObjectPosition
            SourceApproachPoint = $connectorGeometry.SourceApproachPoint
            SourceClearPoint = $connectorGeometry.SourceClearPoint
            DestinationClearPoint = $connectorGeometry.DestinationClearPoint
            DestinationApproachPoint = $connectorGeometry.DestinationApproachPoint
            NavigationPoints = $connectorGeometry.NavigationPoints
            AssetDerivationSource = $connectorGeometry.AssetDerivationSource
        }
        SourceSceneZoneName = $sourceSceneZoneName
        DestinationSceneZoneName = $destinationSceneZoneName
        AssetDerivationSource = $connectorGeometry.AssetDerivationSource
    }

    $transition.Validation = Get-TransitionValidationMetadata -TransitionData $transition
    $generatedTransitions.Add($transition)

    $reverseTransition = [ordered]@{
        Id = "transition:$($link.ToZone)->$($link.FromZone)"
        FromZone = $link.ToZone
        ToZone = $link.FromZone
        FromNodeId = $toNodeId
        ToNodeId = $fromNodeId
        FromWaypoint = Copy-Vec3 $waypointPair.ToWaypoint
        ToWaypoint = Copy-Vec3 $waypointPair.FromWaypoint
        FromCrossingAnchor = Get-MetadataValue -Metadata $waypointPair -Key "ToCrossingAnchor"
        ToCrossingAnchor = Get-MetadataValue -Metadata $waypointPair -Key "FromCrossingAnchor"
        SourceApproachPoint = $connectorGeometry.DestinationApproachPoint
        SourceClearPoint = $connectorGeometry.DestinationClearPoint
        DestinationClearPoint = $connectorGeometry.SourceClearPoint
        DestinationApproachPoint = $connectorGeometry.SourceApproachPoint
        NavigationPoints = @(Get-DeduplicatedVectorSequence -Points @([array]$connectorGeometry.NavigationPoints[-1..0]))
        ConnectorObjectPosition = $connectorGeometry.ConnectorObjectPosition
        Cost = $cost
        StepKind = $stepKind
        ConnectorName = Get-MetadataValue -Metadata $metadata -Key "ConnectorName"
        RequiresInteraction = $requiresInteraction
        TransitionWaitSeconds = [double](Get-MetadataValue -Metadata $metadata -Key "TransitionWaitSeconds" -DefaultValue 0.0)
        Connector = [ordered]@{
            Name = Get-MetadataValue -Metadata $metadata -Key "ConnectorName"
            ObjectPosition = $connectorGeometry.ConnectorObjectPosition
            SourceApproachPoint = $connectorGeometry.DestinationApproachPoint
            SourceClearPoint = $connectorGeometry.DestinationClearPoint
            DestinationClearPoint = $connectorGeometry.SourceClearPoint
            DestinationApproachPoint = $connectorGeometry.SourceApproachPoint
            NavigationPoints = @(Get-DeduplicatedVectorSequence -Points @([array]$connectorGeometry.NavigationPoints[-1..0]))
            AssetDerivationSource = $connectorGeometry.AssetDerivationSource
        }
        SourceSceneZoneName = $destinationSceneZoneName
        DestinationSceneZoneName = $sourceSceneZoneName
        AssetDerivationSource = $connectorGeometry.AssetDerivationSource
    }

    $reverseTransition.Validation = Get-TransitionValidationMetadata -TransitionData $reverseTransition
    $generatedTransitions.Add($reverseTransition)
}

$generatedTransitionIds = @{}
foreach ($transition in [object[]]$generatedTransitions.ToArray()) {
    if ($null -eq $transition -or [string]::IsNullOrWhiteSpace($transition.Id)) {
        continue
    }

    $generatedTransitionIds[$transition.Id] = $true
}

foreach ($transition in [object[]]$generatedTransitions.ToArray()) {
    if ($null -eq $transition -or
        [string]::IsNullOrWhiteSpace($transition.FromZone) -or
        [string]::IsNullOrWhiteSpace($transition.ToZone)) {
        continue
    }

    $reverseId = "transition:$($transition.ToZone)->$($transition.FromZone)"
    if ($generatedTransitionIds.ContainsKey($reverseId)) {
        continue
    }

    $reverseTransition = [ordered]@{
        Id = $reverseId
        FromZone = $transition.ToZone
        ToZone = $transition.FromZone
        FromNodeId = $transition.ToNodeId
        ToNodeId = $transition.FromNodeId
        FromWaypoint = Copy-Vec3 $transition.ToWaypoint
        ToWaypoint = Copy-Vec3 $transition.FromWaypoint
        FromCrossingAnchor = if (-not (Test-ZeroVector $transition.ToCrossingAnchor)) { Copy-Vec3 $transition.ToCrossingAnchor } else { $null }
        ToCrossingAnchor = if (-not (Test-ZeroVector $transition.FromCrossingAnchor)) { Copy-Vec3 $transition.FromCrossingAnchor } else { $null }
        SourceApproachPoint = Copy-Vec3 $transition.DestinationApproachPoint
        SourceClearPoint = Copy-Vec3 $transition.DestinationClearPoint
        DestinationClearPoint = Copy-Vec3 $transition.SourceClearPoint
        DestinationApproachPoint = Copy-Vec3 $transition.SourceApproachPoint
        NavigationPoints = @(Get-DeduplicatedVectorSequence -Points @([array]$transition.NavigationPoints[-1..0]))
        ConnectorObjectPosition = Copy-Vec3 $transition.ConnectorObjectPosition
        Cost = $transition.Cost
        StepKind = $transition.StepKind
        ConnectorName = $transition.ConnectorName
        RequiresInteraction = [bool]$transition.RequiresInteraction
        TransitionWaitSeconds = [double]$transition.TransitionWaitSeconds
        Connector = [ordered]@{
            Name = $transition.ConnectorName
            ObjectPosition = Copy-Vec3 $transition.ConnectorObjectPosition
            SourceApproachPoint = Copy-Vec3 $transition.DestinationApproachPoint
            SourceClearPoint = Copy-Vec3 $transition.DestinationClearPoint
            DestinationClearPoint = Copy-Vec3 $transition.SourceClearPoint
            DestinationApproachPoint = Copy-Vec3 $transition.SourceApproachPoint
            NavigationPoints = @(Get-DeduplicatedVectorSequence -Points @([array]$transition.NavigationPoints[-1..0]))
            AssetDerivationSource = $transition.AssetDerivationSource
        }
        SourceSceneZoneName = $transition.DestinationSceneZoneName
        DestinationSceneZoneName = $transition.SourceSceneZoneName
        AssetDerivationSource = $transition.AssetDerivationSource
    }

    $reverseTransition.Validation = Get-TransitionValidationMetadata -TransitionData $reverseTransition
    $generatedTransitions.Add($reverseTransition)
    $generatedTransitionIds[$reverseId] = $true
}

$outputGraph = [ordered]@{
    SchemaVersion = 2
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("o")
    SceneName = $inputSceneName
    SourceSceneDataPath = (Resolve-Path -LiteralPath $SceneDataPath).Path
    SourceGraphPath = (Resolve-Path -LiteralPath $InputGraphPath).Path
    Zones = $generatedZones
    Nodes = $generatedNodes
    Transitions = $generatedTransitions
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$outputGraph | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote navigation graph to $OutputPath (zones=$($generatedZones.Count) nodes=$($generatedNodes.Count) transitions=$($generatedTransitions.Count))"
