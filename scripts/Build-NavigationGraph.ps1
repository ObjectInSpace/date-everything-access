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

function Find-NearestFamilyPair {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$CameraSpaces,

        [Parameter(Mandatory = $true)]
        [string]$FromBase,

        [Parameter(Mandatory = $true)]
        [string]$ToBase
    )

    $fromFamily = Get-BaseFamilyZones -CameraSpaces $CameraSpaces -BaseName $FromBase
    $toFamily = Get-BaseFamilyZones -CameraSpaces $CameraSpaces -BaseName $ToBase
    if ($fromFamily.Count -eq 0 -or $toFamily.Count -eq 0) {
        return $null
    }

    $bestPair = $null
    foreach ($fromZone in $fromFamily) {
        foreach ($toZone in $toFamily) {
            $distance = Distance -A $fromZone.Position -B $toZone.Position
            if ($null -eq $bestPair -or $distance -lt $bestPair.Distance) {
                $bestPair = [pscustomobject]@{
                    FromZone = $fromZone.Name
                    ToZone = $toZone.Name
                    FromWaypoint = Copy-Vec3 $fromZone.Position
                    ToWaypoint = Copy-Vec3 $toZone.Position
                    Distance = $distance
                }
            }
        }
    }

    return $bestPair
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

$openPassagePairs = @{
    "hallway|living_room" = Find-NearestFamilyPair -CameraSpaces $sceneData.CameraSpaces -FromBase "hallway" -ToBase "living_room"
    "living_room|dining_room" = Find-NearestFamilyPair -CameraSpaces $sceneData.CameraSpaces -FromBase "living_room" -ToBase "dining_room"
    "dining_room|piano_room" = Find-NearestFamilyPair -CameraSpaces $sceneData.CameraSpaces -FromBase "dining_room" -ToBase "piano_room"
    "dining_room|kitchen" = Find-NearestFamilyPair -CameraSpaces $sceneData.CameraSpaces -FromBase "dining_room" -ToBase "kitchen"
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

    if ($directedOverrides.ContainsKey($key)) {
        $waypointPair = $directedOverrides[$key]
    } elseif ($openPassagePairs.ContainsKey($key)) {
        $waypointPair = $openPassagePairs[$key]
    } else {
        $waypointPair = [ordered]@{
            FromWaypoint = Get-ZoneWaypoint $link.FromZone
            ToWaypoint = Get-ZoneWaypoint $link.ToZone
        }
    }

    $cost = [Math]::Round((Distance -A $waypointPair.FromWaypoint -B $waypointPair.ToWaypoint), 4)
    if ($cost -le 0.0) {
        $cost = 1.0
    }

    $metadata = $stepMetadata[$key]
    if ($null -eq $metadata) {
        $metadata = [ordered]@{
            StepKind = "OpenPassage"
        }
    }

    $generatedLinks.Add([ordered]@{
        FromZone = $link.FromZone
        ToZone = $link.ToZone
        FromWaypoint = $waypointPair.FromWaypoint
        ToWaypoint = $waypointPair.ToWaypoint
        Cost = $cost
        StepKind = Get-MetadataValue -Metadata $metadata -Key "StepKind" -DefaultValue "OpenPassage"
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
