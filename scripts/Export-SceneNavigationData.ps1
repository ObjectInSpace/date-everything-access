[CmdletBinding()]
param(
    [Parameter()]
    [string]$ScenePath = "D:\root\AssetRipper\1.3.12\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox.unity",

    [Parameter()]
    [string]$GraphPath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\navigation_graph.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\thirdpersongreybox-navigation-data.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Numerics

function ConvertTo-Float {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return [float]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Parse-Vector3Literal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $match = [regex]::Match($Text, "\{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}")
    if (-not $match.Success) {
        return $null
    }

    return [System.Numerics.Vector3]::new(
        (ConvertTo-Float $match.Groups[1].Value),
        (ConvertTo-Float $match.Groups[2].Value),
        (ConvertTo-Float $match.Groups[3].Value)
    )
}

function Parse-QuaternionLiteral {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $match = [regex]::Match($Text, "\{x: ([^,]+), y: ([^,]+), z: ([^,]+), w: ([^}]+)\}")
    if (-not $match.Success) {
        return $null
    }

    return [System.Numerics.Quaternion]::new(
        (ConvertTo-Float $match.Groups[1].Value),
        (ConvertTo-Float $match.Groups[2].Value),
        (ConvertTo-Float $match.Groups[3].Value),
        (ConvertTo-Float $match.Groups[4].Value)
    )
}

function Convert-Vector3ToObject {
    param(
        [Parameter(Mandatory = $true)]
        [System.Numerics.Vector3]$Vector
    )

    return [ordered]@{
        x = [Math]::Round([double]$Vector.X, 6)
        y = [Math]::Round([double]$Vector.Y, 6)
        z = [Math]::Round([double]$Vector.Z, 6)
    }
}

function Convert-QuaternionToObject {
    param(
        [Parameter(Mandatory = $true)]
        [System.Numerics.Quaternion]$Quaternion
    )

    return [ordered]@{
        x = [Math]::Round([double]$Quaternion.X, 6)
        y = [Math]::Round([double]$Quaternion.Y, 6)
        z = [Math]::Round([double]$Quaternion.Z, 6)
        w = [Math]::Round([double]$Quaternion.W, 6)
    }
}

function New-SceneObject {
    param(
        [Parameter(Mandatory = $true)]
        [long]$Id
    )

    return [ordered]@{
        Id = $Id
    }
}

function Get-LineValue {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Lines,

        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    foreach ($line in $Lines) {
        $match = [regex]::Match($line, $Pattern)
        if ($match.Success) {
            return $match.Groups[1].Value
        }
    }

    return $null
}

function Get-SectionTypeInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Header
    )

    $match = [regex]::Match($Header, "^--- !u!(\d+) &(\d+)$")
    if (-not $match.Success) {
        return $null
    }

    return [ordered]@{
        Type = [int]$match.Groups[1].Value
        Id = [long]$match.Groups[2].Value
    }
}

function Add-ZonesFromComponent {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Zones,

        [Parameter(Mandatory = $true)]
        [string[]]$Lines
    )

    $inZones = $false
    $currentZone = $null

    foreach ($line in $Lines) {
        if (-not $inZones) {
            if ($line -eq "  zones:") {
                $inZones = $true
            }

            continue
        }

        if ($line -match "^  currentarea:") {
            break
        }

        $zoneMatch = [regex]::Match($line, "^  - Name: (.+)$")
        if ($zoneMatch.Success) {
            if ($null -ne $currentZone) {
                $Zones.Add([pscustomobject]$currentZone)
            }

            $currentZone = [ordered]@{
                Name = $zoneMatch.Groups[1].Value
                CameraFileId = 0L
                DialogueCameraFileId = 0L
                DirectionFileId = 0L
                Position = [System.Numerics.Vector3]::Zero
                Scale = [System.Numerics.Vector3]::Zero
                Rotation = [System.Numerics.Quaternion]::Identity
            }

            continue
        }

        if ($null -eq $currentZone) {
            continue
        }

        $cameraMatch = [regex]::Match($line, "^    Camera: \{fileID: (\d+)\}$")
        if ($cameraMatch.Success) {
            $currentZone.CameraFileId = [long]$cameraMatch.Groups[1].Value
            continue
        }

        $dialogueCameraMatch = [regex]::Match($line, "^    DialogueCamera: \{fileID: (\d+)\}$")
        if ($dialogueCameraMatch.Success) {
            $currentZone.DialogueCameraFileId = [long]$dialogueCameraMatch.Groups[1].Value
            continue
        }

        $directionMatch = [regex]::Match($line, "^    direction: \{fileID: (\d+)\}$")
        if ($directionMatch.Success) {
            $currentZone.DirectionFileId = [long]$directionMatch.Groups[1].Value
            continue
        }

        $positionMatch = [regex]::Match($line, "^    Position: (.+)$")
        if ($positionMatch.Success) {
            $currentZone.Position = Parse-Vector3Literal $positionMatch.Groups[1].Value
            continue
        }

        $scaleMatch = [regex]::Match($line, "^    Scale: (.+)$")
        if ($scaleMatch.Success) {
            $currentZone.Scale = Parse-Vector3Literal $scaleMatch.Groups[1].Value
            continue
        }

        $rotationMatch = [regex]::Match($line, "^    Rotation: (.+)$")
        if ($rotationMatch.Success) {
            $currentZone.Rotation = Parse-QuaternionLiteral $rotationMatch.Groups[1].Value
        }
    }

    if ($null -ne $currentZone) {
        $Zones.Add([pscustomobject]$currentZone)
    }
}

function Get-SceneObjectInfo {
    param(
        [Parameter(Mandatory = $true)]
        [long]$GameObjectId,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$GameObjects,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$TransformByGameObjectId,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$WorldTransforms
    )

    if (-not $GameObjects.ContainsKey($GameObjectId)) {
        return $null
    }

    $gameObject = $GameObjects[$GameObjectId]
    $transformInfo = $null
    if ($TransformByGameObjectId.ContainsKey($GameObjectId)) {
        $transformInfo = $TransformByGameObjectId[$GameObjectId]
    }

    $worldInfo = $null
    if ($null -ne $transformInfo -and $WorldTransforms.ContainsKey($transformInfo.Id)) {
        $worldInfo = $WorldTransforms[$transformInfo.Id]
    }

    return [ordered]@{
        Id = $gameObject.Id
        Name = $gameObject.Name
        TransformId = if ($null -ne $transformInfo) { $transformInfo.Id } else { 0L }
        Position = if ($null -ne $worldInfo) { Convert-Vector3ToObject $worldInfo.Position } else { $null }
        Rotation = if ($null -ne $worldInfo) { Convert-QuaternionToObject $worldInfo.Rotation } else { $null }
        ParentTransformId = if ($null -ne $transformInfo) { $transformInfo.ParentTransformId } else { 0L }
    }
}

function Get-NearestZones {
    param(
        [Parameter(Mandatory = $true)]
        [System.Numerics.Vector3]$Position,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Zones,

        [Parameter()]
        [int]$Count = 4
    )

    return $Zones |
        ForEach-Object {
            $delta = $_.Position - $Position
            $distance = [Math]::Sqrt(($delta.X * $delta.X) + ($delta.Y * $delta.Y) + ($delta.Z * $delta.Z))
            [pscustomobject]@{
                Name = $_.Name
                Distance = [Math]::Round([double]$distance, 4)
            }
        } |
        Sort-Object Distance, Name |
        Select-Object -First $Count
}

function Get-ContainingZones {
    param(
        [Parameter(Mandatory = $true)]
        [System.Numerics.Vector3]$Position,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Zones
    )

    $containing = New-Object System.Collections.Generic.List[string]

    foreach ($zone in $Zones) {
        $halfScale = [System.Numerics.Vector3]::new(
            [Math]::Abs($zone.Scale.X) / 2,
            [Math]::Abs($zone.Scale.Y) / 2,
            [Math]::Abs($zone.Scale.Z) / 2
        )

        if ($halfScale.X -le 0 -and $halfScale.Y -le 0 -and $halfScale.Z -le 0) {
            continue
        }

        $min = $zone.Position - $halfScale
        $max = $zone.Position + $halfScale
        if ($Position.X -ge $min.X -and $Position.X -le $max.X -and
            $Position.Y -ge $min.Y -and $Position.Y -le $max.Y -and
            $Position.Z -ge $min.Z -and $Position.Z -le $max.Z) {
            $containing.Add($zone.Name)
        }
    }

    return $containing.ToArray()
}

function Get-WorldTransform {
    param(
        [Parameter(Mandatory = $true)]
        [long]$TransformId,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$TransformsById,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$WorldTransforms
    )

    if ($WorldTransforms.ContainsKey($TransformId)) {
        return $WorldTransforms[$TransformId]
    }

    if (-not $TransformsById.ContainsKey($TransformId)) {
        return $null
    }

    $transformInfo = $TransformsById[$TransformId]
    if ($transformInfo.ParentTransformId -eq 0) {
        $worldTransform = [ordered]@{
            Position = $transformInfo.LocalPosition
            Rotation = [System.Numerics.Quaternion]::Normalize($transformInfo.LocalRotation)
            Scale = $transformInfo.LocalScale
        }

        $WorldTransforms[$TransformId] = $worldTransform
        return $worldTransform
    }

    $parentWorld = Get-WorldTransform -TransformId $transformInfo.ParentTransformId -TransformsById $TransformsById -WorldTransforms $WorldTransforms
    if ($null -eq $parentWorld) {
        $worldTransform = [ordered]@{
            Position = $transformInfo.LocalPosition
            Rotation = [System.Numerics.Quaternion]::Normalize($transformInfo.LocalRotation)
            Scale = $transformInfo.LocalScale
        }

        $WorldTransforms[$TransformId] = $worldTransform
        return $worldTransform
    }

    $scaledLocalPosition = [System.Numerics.Vector3]::new(
        $transformInfo.LocalPosition.X * $parentWorld.Scale.X,
        $transformInfo.LocalPosition.Y * $parentWorld.Scale.Y,
        $transformInfo.LocalPosition.Z * $parentWorld.Scale.Z
    )

    $rotatedLocalPosition = [System.Numerics.Vector3]::Transform($scaledLocalPosition, $parentWorld.Rotation)
    $worldPosition = $parentWorld.Position + $rotatedLocalPosition
    $worldRotation = [System.Numerics.Quaternion]::Normalize($parentWorld.Rotation * $transformInfo.LocalRotation)
    $worldScale = [System.Numerics.Vector3]::new(
        $transformInfo.LocalScale.X * $parentWorld.Scale.X,
        $transformInfo.LocalScale.Y * $parentWorld.Scale.Y,
        $transformInfo.LocalScale.Z * $parentWorld.Scale.Z
    )

    $worldTransform = [ordered]@{
        Position = $worldPosition
        Rotation = $worldRotation
        Scale = $worldScale
    }

    $WorldTransforms[$TransformId] = $worldTransform
    return $worldTransform
}

if (-not (Test-Path -LiteralPath $ScenePath)) {
    throw "Scene file not found: $ScenePath"
}

if (-not (Test-Path -LiteralPath $GraphPath)) {
    throw "Navigation graph file not found: $GraphPath"
}

$gameObjects = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformsById = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
$zones = [System.Collections.Generic.List[object]]::new()
$teleporterComponents = [System.Collections.Generic.List[object]]::new()
$doorComponents = [System.Collections.Generic.List[object]]::new()

$currentHeader = $null
$currentLines = [System.Collections.Generic.List[string]]::new()

function Process-SceneSection {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Header,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Lines
    )

    $sectionInfo = Get-SectionTypeInfo $Header
    if ($null -eq $sectionInfo) {
        return
    }

    $linesArray = $Lines.ToArray()
    switch ($sectionInfo.Type) {
        1 {
            $name = Get-LineValue -Lines $linesArray -Pattern "^  m_Name: (.*)$"
            if ($null -eq $name) {
                return
            }

            $componentIds = New-Object System.Collections.Generic.List[long]
            foreach ($line in $linesArray) {
                $componentMatch = [regex]::Match($line, "^  - component: \{fileID: (\d+)\}$")
                if ($componentMatch.Success) {
                    $componentIds.Add([long]$componentMatch.Groups[1].Value)
                }
            }

            $gameObjects[$sectionInfo.Id] = [pscustomobject]@{
                Id = $sectionInfo.Id
                Name = $name
                ComponentIds = $componentIds.ToArray()
            }
        }
        4 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            $parentTransformIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_Father: \{fileID: (\d+)\}$"
            $localPositionText = Get-LineValue -Lines $linesArray -Pattern "^  m_LocalPosition: (.+)$"
            $localRotationText = Get-LineValue -Lines $linesArray -Pattern "^  m_LocalRotation: (.+)$"
            $localScaleText = Get-LineValue -Lines $linesArray -Pattern "^  m_LocalScale: (.+)$"

            if ($null -eq $gameObjectIdText -or $null -eq $localPositionText -or $null -eq $localRotationText -or $null -eq $localScaleText) {
                return
            }

            $transformInfo = [pscustomobject]@{
                Id = $sectionInfo.Id
                GameObjectId = [long]$gameObjectIdText
                ParentTransformId = if ($null -eq $parentTransformIdText) { 0L } else { [long]$parentTransformIdText }
                LocalPosition = Parse-Vector3Literal $localPositionText
                LocalRotation = Parse-QuaternionLiteral $localRotationText
                LocalScale = Parse-Vector3Literal $localScaleText
            }

            $transformsById[$sectionInfo.Id] = $transformInfo
            $transformByGameObjectId[$transformInfo.GameObjectId] = $transformInfo
        }
        114 {
            if ($linesArray -contains "  zones:") {
                Add-ZonesFromComponent -Zones $zones -Lines $linesArray
                return
            }

            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -eq $gameObjectIdText) {
                return
            }

            if ($linesArray -match "^  LocationDown: \{fileID: \d+\}$") {
                $teleporterComponents.Add([pscustomobject]@{
                    ComponentId = $sectionInfo.Id
                    GameObjectId = [long]$gameObjectIdText
                    LocationDownId = [long](Get-LineValue -Lines $linesArray -Pattern "^  LocationDown: \{fileID: (\d+)\}$")
                    LocationUpId = [long](Get-LineValue -Lines $linesArray -Pattern "^  LocationUp: \{fileID: (\d+)\}$")
                    CameraGoingDownId = [long](Get-LineValue -Lines $linesArray -Pattern "^  cameraGoingDown: \{fileID: (\d+)\}$")
                    CameraGoingUpId = [long](Get-LineValue -Lines $linesArray -Pattern "^  cameraGoingUp: \{fileID: (\d+)\}$")
                    IsCrawlspace = ((Get-LineValue -Lines $linesArray -Pattern "^  isCrawlspace: (\d+)$") -eq "1")
                    TeleportInRotation = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  teleportInRotation: (.+)$")
                    TeleportOutRotation = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  teleportOutRotation: (.+)$")
                })

                return
            }

            if ($linesArray -match "^  doorOpenType: ") {
                $doorComponents.Add([pscustomobject]@{
                    ComponentId = $sectionInfo.Id
                    GameObjectId = [long]$gameObjectIdText
                    DoorOpenType = Get-LineValue -Lines $linesArray -Pattern "^  doorOpenType: (\d+)$"
                    OverrideNodeLock = ((Get-LineValue -Lines $linesArray -Pattern "^  overrideNodeLock: (\d+)$") -eq "1")
                    Locked = ((Get-LineValue -Lines $linesArray -Pattern "^  locked: (\d+)$") -eq "1")
                    NodeLockAlt = Get-LineValue -Lines $linesArray -Pattern "^  NodeLockAlt: (.*)$"
                    AltInteractableId = [long](Get-LineValue -Lines $linesArray -Pattern "^  altInteractable: \{fileID: (\d+)\}$")
                    InteractableObjId = [long](Get-LineValue -Lines $linesArray -Pattern "^  interactableObj: \{fileID: (\d+)\}$")
                })
            }
        }
    }
}

foreach ($line in [System.IO.File]::ReadLines($ScenePath)) {
    if ($line.StartsWith("--- !u!")) {
        if ($null -ne $currentHeader) {
            Process-SceneSection -Header $currentHeader -Lines $currentLines
        }

        $currentHeader = $line
        $currentLines = [System.Collections.Generic.List[string]]::new()
        continue
    }

    if ($null -ne $currentHeader) {
        $currentLines.Add($line)
    }
}

if ($null -ne $currentHeader) {
    Process-SceneSection -Header $currentHeader -Lines $currentLines
}

$worldTransforms = [System.Collections.Generic.Dictionary[long, object]]::new()
foreach ($transformId in $transformsById.Keys) {
    [void](Get-WorldTransform -TransformId $transformId -TransformsById $transformsById -WorldTransforms $worldTransforms)
}

$graphJson = Get-Content -LiteralPath $GraphPath -Raw | ConvertFrom-Json
$graphLinks = @($graphJson.Links)
$graphZoneNames = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
foreach ($link in $graphLinks) {
    [void]$graphZoneNames.Add($link.FromZone)
    [void]$graphZoneNames.Add($link.ToZone)
}

$graphZones = @($zones | Where-Object { $graphZoneNames.Contains($_.Name) })
$extraZones = @($zones | Where-Object { -not $graphZoneNames.Contains($_.Name) })

$doorNamePattern = '(?i)(^doors?_.*|.*door.*|.*trapdoor.*|.*stairs.*)'
$cameraNamePattern = '^(Camera_.+|Camera [^ ].+)$'

$doorObjects = New-Object System.Collections.Generic.List[object]
foreach ($gameObject in $gameObjects.Values | Sort-Object Name, Id) {
    $include = $false
    $doorComponent = $doorComponents | Where-Object { $_.GameObjectId -eq $gameObject.Id } | Select-Object -First 1
    if ($null -ne $doorComponent) {
        $include = $true
    } elseif ($gameObject.Name -match $doorNamePattern) {
        $include = $true
    }

    if (-not $include) {
        continue
    }

    if (-not $transformByGameObjectId.ContainsKey($gameObject.Id)) {
        continue
    }

    $transformInfo = $transformByGameObjectId[$gameObject.Id]
    $worldInfo = $worldTransforms[$transformInfo.Id]
    $containingGraphZones = Get-ContainingZones -Position $worldInfo.Position -Zones $graphZones
    $nearestGraphZones = Get-NearestZones -Position $worldInfo.Position -Zones $graphZones -Count 4
    $nearestExtraZones = Get-NearestZones -Position $worldInfo.Position -Zones $extraZones -Count 4

    $doorObjects.Add([ordered]@{
        Name = $gameObject.Name
        Id = $gameObject.Id
        TransformId = $transformInfo.Id
        Position = Convert-Vector3ToObject $worldInfo.Position
        Rotation = Convert-QuaternionToObject $worldInfo.Rotation
        ContainingGraphZones = $containingGraphZones
        NearestGraphZones = @($nearestGraphZones | ForEach-Object {
            [ordered]@{
                Name = $_.Name
                Distance = $_.Distance
            }
        })
        NearestExtraZones = @($nearestExtraZones | ForEach-Object {
            [ordered]@{
                Name = $_.Name
                Distance = $_.Distance
            }
        })
        DoorComponent = if ($null -ne $doorComponent) {
            [ordered]@{
                ComponentId = $doorComponent.ComponentId
                DoorOpenType = $doorComponent.DoorOpenType
                OverrideNodeLock = $doorComponent.OverrideNodeLock
                Locked = $doorComponent.Locked
                NodeLockAlt = $doorComponent.NodeLockAlt
            }
        } else {
            $null
        }
    })
}

$cameraObjects = New-Object System.Collections.Generic.List[object]
foreach ($gameObject in $gameObjects.Values | Sort-Object Name, Id) {
    if ($gameObject.Name -notmatch $cameraNamePattern) {
        continue
    }

    if (-not $transformByGameObjectId.ContainsKey($gameObject.Id)) {
        continue
    }

    $transformInfo = $transformByGameObjectId[$gameObject.Id]
    $worldInfo = $worldTransforms[$transformInfo.Id]
    $cameraObjects.Add([ordered]@{
        Name = $gameObject.Name
        Id = $gameObject.Id
        TransformId = $transformInfo.Id
        Position = Convert-Vector3ToObject $worldInfo.Position
        Rotation = Convert-QuaternionToObject $worldInfo.Rotation
        NearestGraphZones = @((Get-NearestZones -Position $worldInfo.Position -Zones $graphZones -Count 4) | ForEach-Object {
            [ordered]@{
                Name = $_.Name
                Distance = $_.Distance
            }
        })
    })
}

$teleporterObjects = New-Object System.Collections.Generic.List[object]
foreach ($teleporter in $teleporterComponents) {
    $teleporterGameObject = Get-SceneObjectInfo -GameObjectId $teleporter.GameObjectId -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -WorldTransforms $worldTransforms
    $locationDown = Get-SceneObjectInfo -GameObjectId $teleporter.LocationDownId -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -WorldTransforms $worldTransforms
    $locationUp = Get-SceneObjectInfo -GameObjectId $teleporter.LocationUpId -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -WorldTransforms $worldTransforms
    $cameraDown = Get-SceneObjectInfo -GameObjectId $teleporter.CameraGoingDownId -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -WorldTransforms $worldTransforms
    $cameraUp = Get-SceneObjectInfo -GameObjectId $teleporter.CameraGoingUpId -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -WorldTransforms $worldTransforms

    $teleporterObjects.Add([ordered]@{
        Name = $teleporterGameObject.Name
        Id = $teleporterGameObject.Id
        IsCrawlspace = $teleporter.IsCrawlspace
        Position = $teleporterGameObject.Position
        TeleportInRotation = if ($null -ne $teleporter.TeleportInRotation) { Convert-Vector3ToObject $teleporter.TeleportInRotation } else { $null }
        TeleportOutRotation = if ($null -ne $teleporter.TeleportOutRotation) { Convert-Vector3ToObject $teleporter.TeleportOutRotation } else { $null }
        LocationDown = $locationDown
        LocationUp = $locationUp
        CameraGoingDown = $cameraDown
        CameraGoingUp = $cameraUp
    })
}

$zoneObjects = @($zones | Sort-Object Name | ForEach-Object {
    [ordered]@{
        Name = $_.Name
        Position = Convert-Vector3ToObject $_.Position
        Scale = Convert-Vector3ToObject $_.Scale
        Rotation = Convert-QuaternionToObject $_.Rotation
        InNavigationGraph = $graphZoneNames.Contains($_.Name)
    }
})

$graphLinksOutput = @($graphLinks | ForEach-Object {
    [ordered]@{
        FromZone = $_.FromZone
        ToZone = $_.ToZone
    }
})

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$result = [ordered]@{
    ScenePath = $ScenePath
    GraphPath = $GraphPath
    GeneratedAt = (Get-Date).ToString("o")
    Counts = [ordered]@{
        CameraSpaces = $zoneObjects.Count
        GraphZones = $graphZones.Count
        ExtraZones = $extraZones.Count
        DoorObjects = $doorObjects.Count
        CameraObjects = $cameraObjects.Count
        Teleporters = $teleporterObjects.Count
    }
    GraphLinks = $graphLinksOutput
    CameraSpaces = $zoneObjects
    DoorObjects = $doorObjects
    CameraObjects = $cameraObjects
    Teleporters = $teleporterObjects
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote navigation scene data to $OutputPath"
