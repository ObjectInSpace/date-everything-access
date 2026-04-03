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
    "hallway|bathroom1" = [ordered]@{
        StepKind = "Door"
        ConnectorName = "Doors_Bathroom1"
    }
    "upper_hallway|hallway" = [ordered]@{
        StepKind = "Stairs"
    }
    "laundry_room|laundry_room_closet" = [ordered]@{
        StepKind = "Door"
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

$generatedLinks = New-Object System.Collections.Generic.List[object]
foreach ($link in @($inputGraph.Links)) {
    $key = "$($link.FromZone)|$($link.ToZone)"
    $waypointPair = $null
    $metadata = $stepMetadata[$key]
    if ($null -eq $metadata) {
        $metadata = [ordered]@{
            StepKind = "OpenPassage"
        }
    }

    $stepKind = Get-MetadataValue -Metadata $metadata -Key "StepKind" -DefaultValue "OpenPassage"

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

    $generatedLinks.Add([ordered]@{
        FromZone = $link.FromZone
        ToZone = $link.ToZone
        FromWaypoint = $waypointPair.FromWaypoint
        ToWaypoint = $waypointPair.ToWaypoint
        FromCrossingAnchor = Get-MetadataValue -Metadata $waypointPair -Key "FromCrossingAnchor"
        ToCrossingAnchor = Get-MetadataValue -Metadata $waypointPair -Key "ToCrossingAnchor"
        Cost = $cost
        StepKind = $stepKind
        ConnectorName = Get-MetadataValue -Metadata $metadata -Key "ConnectorName"
        RequiresInteraction = [bool](Get-MetadataValue -Metadata $metadata -Key "RequiresInteraction" -DefaultValue $false)
        TransitionWaitSeconds = [double](Get-MetadataValue -Metadata $metadata -Key "TransitionWaitSeconds" -DefaultValue 0.0)
    })
}

$outputGraph = [ordered]@{
    SceneName = $inputGraph.SceneName
    Links = $generatedLinks
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$outputGraph | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote navigation graph to $OutputPath"
