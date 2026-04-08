[CmdletBinding()]
param(
    [Parameter()]
    [string]$ScenePath = "D:\root\AssetRipper\1.3.12\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox.unity",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\thirdpersongreybox-blockers.json",

    [Parameter()]
    [double]$MinimumBlockingTopY = 0.35,

    [Parameter()]
    [double]$MaximumBlockingBottomY = 2.35,

    [Parameter()]
    [double]$MinimumFootprintRadius = 0.15
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

function Convert-FlatVectorToObject {
    param(
        [Parameter(Mandatory = $true)]
        [double]$X,

        [Parameter(Mandatory = $true)]
        [double]$Z
    )

    return [ordered]@{
        x = [Math]::Round($X, 6)
        z = [Math]::Round($Z, 6)
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

function Transform-LocalPoint {
    param(
        [Parameter(Mandatory = $true)]
        [object]$WorldTransform,

        [Parameter(Mandatory = $true)]
        [System.Numerics.Vector3]$LocalPoint
    )

    $scaled = [System.Numerics.Vector3]::new(
        $LocalPoint.X * $WorldTransform.Scale.X,
        $LocalPoint.Y * $WorldTransform.Scale.Y,
        $LocalPoint.Z * $WorldTransform.Scale.Z
    )

    return $WorldTransform.Position + [System.Numerics.Vector3]::Transform($scaled, $WorldTransform.Rotation)
}

function Transform-LocalDirection {
    param(
        [Parameter(Mandatory = $true)]
        [object]$WorldTransform,

        [Parameter(Mandatory = $true)]
        [System.Numerics.Vector3]$LocalDirection
    )

    $scaled = [System.Numerics.Vector3]::new(
        $LocalDirection.X * $WorldTransform.Scale.X,
        $LocalDirection.Y * $WorldTransform.Scale.Y,
        $LocalDirection.Z * $WorldTransform.Scale.Z
    )

    return [System.Numerics.Vector3]::Transform($scaled, $WorldTransform.Rotation)
}

function Get-MaxAbsScale {
    param(
        [Parameter(Mandatory = $true)]
        [System.Numerics.Vector3]$Scale
    )

    return [Math]::Max([Math]::Abs($Scale.X), [Math]::Max([Math]::Abs($Scale.Y), [Math]::Abs($Scale.Z)))
}

function Convert-FlatDirectionToObject {
    param(
        [Parameter(Mandatory = $true)]
        [System.Numerics.Vector3]$Direction
    )

    $flatLength = [Math]::Sqrt(($Direction.X * $Direction.X) + ($Direction.Z * $Direction.Z))
    if ($flatLength -le 0.00001) {
        return Convert-FlatVectorToObject -X 0 -Z 0
    }

    return Convert-FlatVectorToObject -X ($Direction.X / $flatLength) -Z ($Direction.Z / $flatLength)
}

function Get-Bounds3DFromPoints {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[System.Numerics.Vector3]]$Points
    )

    if ($Points.Count -lt 1) {
        return $null
    }

    $minX = [double]::PositiveInfinity
    $minY = [double]::PositiveInfinity
    $minZ = [double]::PositiveInfinity
    $maxX = [double]::NegativeInfinity
    $maxY = [double]::NegativeInfinity
    $maxZ = [double]::NegativeInfinity

    foreach ($point in $Points) {
        $minX = [Math]::Min($minX, $point.X)
        $minY = [Math]::Min($minY, $point.Y)
        $minZ = [Math]::Min($minZ, $point.Z)
        $maxX = [Math]::Max($maxX, $point.X)
        $maxY = [Math]::Max($maxY, $point.Y)
        $maxZ = [Math]::Max($maxZ, $point.Z)
    }

    return [ordered]@{
        Min = Convert-Vector3ToObject ([System.Numerics.Vector3]::new($minX, $minY, $minZ))
        Max = Convert-Vector3ToObject ([System.Numerics.Vector3]::new($maxX, $maxY, $maxZ))
        Size = Convert-Vector3ToObject ([System.Numerics.Vector3]::new($maxX - $minX, $maxY - $minY, $maxZ - $minZ))
        Center = Convert-Vector3ToObject ([System.Numerics.Vector3]::new(($minX + $maxX) / 2.0, ($minY + $maxY) / 2.0, ($minZ + $maxZ) / 2.0))
    }
}

function Get-Bounds2DFromPoints {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[System.Numerics.Vector3]]$Points
    )

    if ($Points.Count -lt 1) {
        return $null
    }

    $minX = [double]::PositiveInfinity
    $minZ = [double]::PositiveInfinity
    $maxX = [double]::NegativeInfinity
    $maxZ = [double]::NegativeInfinity

    foreach ($point in $Points) {
        $minX = [Math]::Min($minX, $point.X)
        $minZ = [Math]::Min($minZ, $point.Z)
        $maxX = [Math]::Max($maxX, $point.X)
        $maxZ = [Math]::Max($maxZ, $point.Z)
    }

    return [ordered]@{
        MinX = [Math]::Round($minX, 6)
        MaxX = [Math]::Round($maxX, 6)
        MinZ = [Math]::Round($minZ, 6)
        MaxZ = [Math]::Round($maxZ, 6)
        Width = [Math]::Round($maxX - $minX, 6)
        Depth = [Math]::Round($maxZ - $minZ, 6)
    }
}

function Get-GameObjectPath {
    param(
        [Parameter(Mandatory = $true)]
        [long]$GameObjectId,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$GameObjects,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$TransformByGameObjectId,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$TransformsById
    )

    if (-not $GameObjects.ContainsKey($GameObjectId)) {
        return $null
    }

    $segments = New-Object System.Collections.Generic.List[string]
    $currentGameObjectId = $GameObjectId
    while ($currentGameObjectId -ne 0 -and $GameObjects.ContainsKey($currentGameObjectId)) {
        $gameObject = $GameObjects[$currentGameObjectId]
        $segments.Add($gameObject.Name)

        if (-not $TransformByGameObjectId.ContainsKey($currentGameObjectId)) {
            break
        }

        $transformInfo = $TransformByGameObjectId[$currentGameObjectId]
        if ($transformInfo.ParentTransformId -eq 0 -or -not $TransformsById.ContainsKey($transformInfo.ParentTransformId)) {
            break
        }

        $parentTransform = $TransformsById[$transformInfo.ParentTransformId]
        $currentGameObjectId = $parentTransform.GameObjectId
    }

    $pathSegments = $segments.ToArray()
    [array]::Reverse($pathSegments)
    return [string]::Join("/", $pathSegments)
}

function New-ReasonCounter {
    return [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
}

function Add-ReasonCount {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[string, int]]$Counter,

        [Parameter(Mandatory = $true)]
        [string]$Reason
    )

    if ($Counter.ContainsKey($Reason)) {
        $Counter[$Reason]++
    } else {
        $Counter[$Reason] = 1
    }
}

function Convert-ReasonCounterToArray {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[string, int]]$Counter
    )

    return @($Counter.Keys | Sort-Object | ForEach-Object {
        [ordered]@{
            Reason = [string]$_
            Count = $Counter[[string]$_]
        }
    })
}

function Get-LineValueOrDefault {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Lines,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$DefaultValue
    )

    $value = Get-LineValue -Lines $Lines -Pattern $Pattern
    if ($null -eq $value) {
        return $DefaultValue
    }

    return $value
}

function New-ColliderRecord {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Component,

        [Parameter(Mandatory = $true)]
        [string]$ColliderType,

        [Parameter(Mandatory = $true)]
        [object]$GameObject,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$WorldTransform,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[System.Numerics.Vector3]]$Points,

        [Parameter(Mandatory = $true)]
        [hashtable]$Footprint
    )

    return [ordered]@{
        ComponentId = $Component.ComponentId
        GameObjectId = $GameObject.Id
        Name = $GameObject.Name
        Path = $Path
        Layer = $GameObject.Layer
        StaticEditorFlags = $GameObject.StaticEditorFlags
        IsActive = $GameObject.IsActive
        ColliderType = $ColliderType
        Enabled = $Component.Enabled
        IsTrigger = $Component.IsTrigger
        IsDoorConnector = $Component.IsDoorConnector
        IsTeleporterConnector = $Component.IsTeleporterConnector
        HasRigidbody = $Component.HasRigidbody
        RigidbodyIsKinematic = $Component.RigidbodyIsKinematic
        TransformId = $Component.TransformId
        WorldPosition = Convert-Vector3ToObject $WorldTransform.Position
        WorldRotation = Convert-QuaternionToObject $WorldTransform.Rotation
        WorldScale = Convert-Vector3ToObject $WorldTransform.Scale
        WorldCenter = Convert-Vector3ToObject $Component.WorldCenter
        Bounds3D = Get-Bounds3DFromPoints -Points $Points
        Bounds2D = Get-Bounds2DFromPoints -Points $Points
        BottomY = [Math]::Round($Component.BottomY, 6)
        TopY = [Math]::Round($Component.TopY, 6)
        Footprint = $Footprint
        LocalShape = $Component.LocalShape
    }
}

function Get-BoxColliderRecord {
    param([object]$Component, [object]$GameObject, [string]$Path, [object]$WorldTransform)

    $halfSize = [System.Numerics.Vector3]::new($Component.Size.X / 2.0, $Component.Size.Y / 2.0, $Component.Size.Z / 2.0)
    $points = New-Object System.Collections.Generic.List[System.Numerics.Vector3]
    foreach ($sx in @(-1, 1)) {
        foreach ($sy in @(-1, 1)) {
            foreach ($sz in @(-1, 1)) {
                $localPoint = [System.Numerics.Vector3]::new(
                    $Component.Center.X + ($halfSize.X * $sx),
                    $Component.Center.Y + ($halfSize.Y * $sy),
                    $Component.Center.Z + ($halfSize.Z * $sz))
                $points.Add((Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $localPoint))
            }
        }
    }

    $worldCenter = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $Component.Center
    return New-ColliderRecord -Component ([ordered]@{
            ComponentId = $Component.ComponentId
            Enabled = $Component.Enabled
            IsTrigger = $Component.IsTrigger
            IsDoorConnector = $Component.IsDoorConnector
            IsTeleporterConnector = $Component.IsTeleporterConnector
            HasRigidbody = $Component.HasRigidbody
            RigidbodyIsKinematic = $Component.RigidbodyIsKinematic
            TransformId = $Component.TransformId
            WorldCenter = $worldCenter
            BottomY = ($points | Measure-Object -Property Y -Minimum).Minimum
            TopY = ($points | Measure-Object -Property Y -Maximum).Maximum
            LocalShape = [ordered]@{ Center = Convert-Vector3ToObject $Component.Center; Size = Convert-Vector3ToObject $Component.Size }
        }) -ColliderType "BoxCollider" -GameObject $GameObject -Path $Path -WorldTransform $WorldTransform -Points $points -Footprint ([ordered]@{
            Kind = "OrientedBox"
            Center = Convert-Vector3ToObject $worldCenter
            HalfExtents = [ordered]@{
                x = [Math]::Round([Math]::Abs($Component.Size.X * $WorldTransform.Scale.X) / 2.0, 6)
                z = [Math]::Round([Math]::Abs($Component.Size.Z * $WorldTransform.Scale.Z) / 2.0, 6)
            }
            Right = Convert-FlatDirectionToObject (Transform-LocalDirection -WorldTransform $WorldTransform -LocalDirection ([System.Numerics.Vector3]::new(1, 0, 0)))
            Forward = Convert-FlatDirectionToObject (Transform-LocalDirection -WorldTransform $WorldTransform -LocalDirection ([System.Numerics.Vector3]::new(0, 0, 1)))
        })
}

function Get-SphereColliderRecord {
    param([object]$Component, [object]$GameObject, [string]$Path, [object]$WorldTransform)

    $worldCenter = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $Component.Center
    $flatRadius = $Component.Radius * [Math]::Max([Math]::Abs($WorldTransform.Scale.X), [Math]::Abs($WorldTransform.Scale.Z))
    $verticalRadius = $Component.Radius * (Get-MaxAbsScale -Scale $WorldTransform.Scale)
    $points = New-Object System.Collections.Generic.List[System.Numerics.Vector3]
    $points.Add([System.Numerics.Vector3]::new($worldCenter.X - $flatRadius, $worldCenter.Y - $verticalRadius, $worldCenter.Z - $flatRadius))
    $points.Add([System.Numerics.Vector3]::new($worldCenter.X + $flatRadius, $worldCenter.Y + $verticalRadius, $worldCenter.Z + $flatRadius))
    return New-ColliderRecord -Component ([ordered]@{
            ComponentId = $Component.ComponentId
            Enabled = $Component.Enabled
            IsTrigger = $Component.IsTrigger
            IsDoorConnector = $Component.IsDoorConnector
            IsTeleporterConnector = $Component.IsTeleporterConnector
            HasRigidbody = $Component.HasRigidbody
            RigidbodyIsKinematic = $Component.RigidbodyIsKinematic
            TransformId = $Component.TransformId
            WorldCenter = $worldCenter
            BottomY = $worldCenter.Y - $verticalRadius
            TopY = $worldCenter.Y + $verticalRadius
            LocalShape = [ordered]@{ Center = Convert-Vector3ToObject $Component.Center; Radius = [Math]::Round($Component.Radius, 6) }
        }) -ColliderType "SphereCollider" -GameObject $GameObject -Path $Path -WorldTransform $WorldTransform -Points $points -Footprint ([ordered]@{
            Kind = "Circle"
            Center = Convert-Vector3ToObject $worldCenter
            Radius = [Math]::Round($flatRadius, 6)
        })
}

function Get-CapsuleColliderRecord {
    param([object]$Component, [object]$GameObject, [string]$Path, [object]$WorldTransform)

    $directionAxis = switch ($Component.Direction) { 0 { [System.Numerics.Vector3]::new(1, 0, 0) } 2 { [System.Numerics.Vector3]::new(0, 0, 1) } default { [System.Numerics.Vector3]::new(0, 1, 0) } }
    $segmentHalf = [Math]::Max(0.0, ($Component.Height / 2.0) - $Component.Radius)
    $flatRadius = $Component.Radius * [Math]::Max([Math]::Abs($WorldTransform.Scale.X), [Math]::Abs($WorldTransform.Scale.Z))
    $verticalRadius = $Component.Radius * (Get-MaxAbsScale -Scale $WorldTransform.Scale)
    $worldCenter = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $Component.Center
    $worldStart = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint ($Component.Center + ($directionAxis * [float]$segmentHalf))
    $worldEnd = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint ($Component.Center - ($directionAxis * [float]$segmentHalf))
    $points = New-Object System.Collections.Generic.List[System.Numerics.Vector3]
    $points.Add([System.Numerics.Vector3]::new($worldStart.X - $flatRadius, $worldStart.Y - $verticalRadius, $worldStart.Z - $flatRadius))
    $points.Add([System.Numerics.Vector3]::new($worldStart.X + $flatRadius, $worldStart.Y + $verticalRadius, $worldStart.Z + $flatRadius))
    $points.Add([System.Numerics.Vector3]::new($worldEnd.X - $flatRadius, $worldEnd.Y - $verticalRadius, $worldEnd.Z - $flatRadius))
    $points.Add([System.Numerics.Vector3]::new($worldEnd.X + $flatRadius, $worldEnd.Y + $verticalRadius, $worldEnd.Z + $flatRadius))
    $footprint = if ($Component.Direction -eq 1) {
        [ordered]@{ Kind = "Circle"; Center = Convert-Vector3ToObject $worldCenter; Radius = [Math]::Round($flatRadius, 6) }
    } else {
        [ordered]@{ Kind = "Capsule2D"; Start = Convert-Vector3ToObject $worldStart; End = Convert-Vector3ToObject $worldEnd; Radius = [Math]::Round($flatRadius, 6) }
    }
    return New-ColliderRecord -Component ([ordered]@{
            ComponentId = $Component.ComponentId
            Enabled = $Component.Enabled
            IsTrigger = $Component.IsTrigger
            IsDoorConnector = $Component.IsDoorConnector
            IsTeleporterConnector = $Component.IsTeleporterConnector
            HasRigidbody = $Component.HasRigidbody
            RigidbodyIsKinematic = $Component.RigidbodyIsKinematic
            TransformId = $Component.TransformId
            WorldCenter = $worldCenter
            BottomY = [Math]::Min($worldStart.Y, $worldEnd.Y) - $verticalRadius
            TopY = [Math]::Max($worldStart.Y, $worldEnd.Y) + $verticalRadius
            LocalShape = [ordered]@{
                Center = Convert-Vector3ToObject $Component.Center
                Radius = [Math]::Round($Component.Radius, 6)
                Height = [Math]::Round($Component.Height, 6)
                Direction = $Component.Direction
            }
        }) -ColliderType "CapsuleCollider" -GameObject $GameObject -Path $Path -WorldTransform $WorldTransform -Points $points -Footprint $footprint
}

if (-not (Test-Path -LiteralPath $ScenePath)) { throw "Scene file not found: $ScenePath" }

$gameObjects = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformsById = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
$doorGameObjectIds = New-Object System.Collections.Generic.HashSet[long]
$teleporterGameObjectIds = New-Object System.Collections.Generic.HashSet[long]
$rigidbodyByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
$primitiveColliderComponents = New-Object System.Collections.Generic.List[object]
$meshColliderCount = 0
$terrainColliderCount = 0
$currentHeader = $null
$currentLines = [System.Collections.Generic.List[string]]::new()

function Process-SceneSection {
    param([string]$Header, [System.Collections.Generic.List[string]]$Lines)

    $sectionInfo = Get-SectionTypeInfo $Header
    if ($null -eq $sectionInfo) { return }
    $linesArray = $Lines.ToArray()
    switch ($sectionInfo.Type) {
        1 {
            $name = Get-LineValue -Lines $linesArray -Pattern "^  m_Name: (.*)$"
            if ($null -eq $name) { return }
            $componentIds = New-Object System.Collections.Generic.List[long]
            foreach ($line in $linesArray) {
                $componentMatch = [regex]::Match($line, "^  - component: \{fileID: (\d+)\}$")
                if ($componentMatch.Success) { $componentIds.Add([long]$componentMatch.Groups[1].Value) }
            }
            $gameObjects[$sectionInfo.Id] = [pscustomobject]@{
                Id = $sectionInfo.Id
                Name = $name
                Layer = [int](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  m_Layer: (\d+)$" -DefaultValue "0")
                StaticEditorFlags = [long](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  m_StaticEditorFlags: (\d+)$" -DefaultValue "0")
                IsActive = ((Get-LineValue -Lines $linesArray -Pattern "^  m_IsActive: (\d+)$") -ne "0")
                ComponentIds = $componentIds.ToArray()
            }
        }
        4 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -eq $gameObjectIdText) { return }
            $transformInfo = [pscustomobject]@{
                Id = $sectionInfo.Id
                GameObjectId = [long]$gameObjectIdText
                ParentTransformId = [long](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  m_Father: \{fileID: (\d+)\}$" -DefaultValue "0")
                LocalPosition = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  m_LocalPosition: (.+)$")
                LocalRotation = Parse-QuaternionLiteral (Get-LineValue -Lines $linesArray -Pattern "^  m_LocalRotation: (.+)$")
                LocalScale = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  m_LocalScale: (.+)$")
            }
            $transformsById[$sectionInfo.Id] = $transformInfo
            $transformByGameObjectId[$transformInfo.GameObjectId] = $transformInfo
        }
        54 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -ne $gameObjectIdText) {
                $rigidbodyByGameObjectId[[long]$gameObjectIdText] = [pscustomobject]@{
                    ComponentId = $sectionInfo.Id
                    IsKinematic = ((Get-LineValue -Lines $linesArray -Pattern "^  m_IsKinematic: (\d+)$") -eq "1")
                }
            }
        }
        64 { $script:meshColliderCount++ }
        65 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -ne $gameObjectIdText) {
                $primitiveColliderComponents.Add([pscustomobject]@{
                    ComponentId = $sectionInfo.Id; ColliderType = "BoxCollider"; GameObjectId = [long]$gameObjectIdText
                    Enabled = ((Get-LineValue -Lines $linesArray -Pattern "^  m_Enabled: (\d+)$") -ne "0")
                    IsTrigger = ((Get-LineValue -Lines $linesArray -Pattern "^  m_IsTrigger: (\d+)$") -eq "1")
                    Center = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  m_Center: (.+)$")
                    Size = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  m_Size: (.+)$")
                })
            }
        }
        114 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -eq $gameObjectIdText) { return }
            if ($linesArray -match "^  LocationDown: \{fileID: \d+\}$") { [void]$teleporterGameObjectIds.Add([long]$gameObjectIdText); return }
            if ($linesArray -match "^  doorOpenType: ") { [void]$doorGameObjectIds.Add([long]$gameObjectIdText) }
        }
        135 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -ne $gameObjectIdText) {
                $primitiveColliderComponents.Add([pscustomobject]@{
                    ComponentId = $sectionInfo.Id; ColliderType = "SphereCollider"; GameObjectId = [long]$gameObjectIdText
                    Enabled = ((Get-LineValue -Lines $linesArray -Pattern "^  m_Enabled: (\d+)$") -ne "0")
                    IsTrigger = ((Get-LineValue -Lines $linesArray -Pattern "^  m_IsTrigger: (\d+)$") -eq "1")
                    Center = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  m_Center: (.+)$")
                    Radius = [double](Get-LineValue -Lines $linesArray -Pattern "^  m_Radius: ([^ ]+)$")
                })
            }
        }
        136 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -ne $gameObjectIdText) {
                $primitiveColliderComponents.Add([pscustomobject]@{
                    ComponentId = $sectionInfo.Id; ColliderType = "CapsuleCollider"; GameObjectId = [long]$gameObjectIdText
                    Enabled = ((Get-LineValue -Lines $linesArray -Pattern "^  m_Enabled: (\d+)$") -ne "0")
                    IsTrigger = ((Get-LineValue -Lines $linesArray -Pattern "^  m_IsTrigger: (\d+)$") -eq "1")
                    Center = Parse-Vector3Literal (Get-LineValue -Lines $linesArray -Pattern "^  m_Center: (.+)$")
                    Radius = [double](Get-LineValue -Lines $linesArray -Pattern "^  m_Radius: ([^ ]+)$")
                    Height = [double](Get-LineValue -Lines $linesArray -Pattern "^  m_Height: ([^ ]+)$")
                    Direction = [int](Get-LineValue -Lines $linesArray -Pattern "^  m_Direction: (\d+)$")
                })
            }
        }
        154 { $script:terrainColliderCount++ }
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

$primitiveColliders = New-Object System.Collections.Generic.List[object]
$navigationBlockers = New-Object System.Collections.Generic.List[object]
$ignoredReasons = New-ReasonCounter

foreach ($component in $primitiveColliderComponents) {
    if (-not $gameObjects.ContainsKey($component.GameObjectId) -or -not $transformByGameObjectId.ContainsKey($component.GameObjectId)) {
        Add-ReasonCount -Counter $ignoredReasons -Reason "MissingSceneObject"
        continue
    }

    $gameObject = $gameObjects[$component.GameObjectId]
    $transformInfo = $transformByGameObjectId[$component.GameObjectId]
    if (-not $worldTransforms.ContainsKey($transformInfo.Id)) {
        Add-ReasonCount -Counter $ignoredReasons -Reason "MissingWorldTransform"
        continue
    }

    $worldTransform = $worldTransforms[$transformInfo.Id]
    $rigidbodyIsKinematic = $false
    if ($rigidbodyByGameObjectId.ContainsKey($gameObject.Id)) {
        $rigidbodyIsKinematic = $rigidbodyByGameObjectId[$gameObject.Id].IsKinematic
    }
    $component | Add-Member -NotePropertyName TransformId -NotePropertyValue $transformInfo.Id -Force
    $component | Add-Member -NotePropertyName IsDoorConnector -NotePropertyValue ($doorGameObjectIds.Contains($gameObject.Id)) -Force
    $component | Add-Member -NotePropertyName IsTeleporterConnector -NotePropertyValue ($teleporterGameObjectIds.Contains($gameObject.Id)) -Force
    $component | Add-Member -NotePropertyName HasRigidbody -NotePropertyValue ($rigidbodyByGameObjectId.ContainsKey($gameObject.Id)) -Force
    $component | Add-Member -NotePropertyName RigidbodyIsKinematic -NotePropertyValue $rigidbodyIsKinematic -Force

    $path = Get-GameObjectPath -GameObjectId $gameObject.Id -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById
    $record = switch ($component.ColliderType) {
        "BoxCollider" { Get-BoxColliderRecord -Component $component -GameObject $gameObject -Path $path -WorldTransform $worldTransform }
        "SphereCollider" { Get-SphereColliderRecord -Component $component -GameObject $gameObject -Path $path -WorldTransform $worldTransform }
        "CapsuleCollider" { Get-CapsuleColliderRecord -Component $component -GameObject $gameObject -Path $path -WorldTransform $worldTransform }
        default { $null }
    }

    if ($null -eq $record) {
        Add-ReasonCount -Counter $ignoredReasons -Reason "UnsupportedPrimitiveShape"
        continue
    }

    $primitiveColliders.Add($record)

    $reason = $null
    if (-not $record.Enabled) { $reason = "DisabledCollider" }
    elseif ($record.IsTrigger) { $reason = "TriggerCollider" }
    elseif (-not $record.IsActive) { $reason = "InactiveGameObject" }
    elseif ($record.IsDoorConnector) { $reason = "DoorConnector" }
    elseif ($record.IsTeleporterConnector) { $reason = "TeleporterConnector" }
    elseif ($record.HasRigidbody) { $reason = "RigidbodyObject" }
    elseif ($record.TopY -lt $MinimumBlockingTopY) { $reason = "BelowBlockingHeight" }
    elseif ($record.BottomY -gt $MaximumBlockingBottomY) { $reason = "AbovePlayerBand" }
    elseif ($null -eq $record.Bounds2D) { $reason = "MissingFootprintBounds" }
    else {
        $flatRadius = [Math]::Max([double]$record.Bounds2D.Width, [double]$record.Bounds2D.Depth) / 2.0
        if ($flatRadius -lt $MinimumFootprintRadius) { $reason = "TinyFootprint" }
    }

    if ($null -ne $reason) {
        Add-ReasonCount -Counter $ignoredReasons -Reason $reason
        continue
    }

    $navigationBlockers.Add($record)
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$ignoredPrimitiveColliderCounts = @(Convert-ReasonCounterToArray -Counter $ignoredReasons)
$primitiveColliderArray = $primitiveColliders.ToArray()
$navigationBlockerArray = $navigationBlockers.ToArray()

$result = [ordered]@{
    ScenePath = $ScenePath
    GeneratedAt = (Get-Date).ToString("o")
    Filtering = [ordered]@{
        MinimumBlockingTopY = [Math]::Round($MinimumBlockingTopY, 4)
        MaximumBlockingBottomY = [Math]::Round($MaximumBlockingBottomY, 4)
        MinimumFootprintRadius = [Math]::Round($MinimumFootprintRadius, 4)
        DoorAndTeleporterCollidersExcluded = $true
        RigidbodyObjectsExcluded = $true
        MeshCollidersUnsupported = $true
        TerrainCollidersUnsupported = $true
    }
    Counts = [ordered]@{
        GameObjects = $gameObjects.Count
        PrimitiveColliders = $primitiveColliders.Count
        NavigationBlockers = $navigationBlockers.Count
        MeshColliders = $meshColliderCount
        TerrainColliders = $terrainColliderCount
        DoorGameObjects = $doorGameObjectIds.Count
        TeleporterGameObjects = $teleporterGameObjectIds.Count
        RigidbodyObjects = $rigidbodyByGameObjectId.Count
    }
    IgnoredPrimitiveColliderCounts = $ignoredPrimitiveColliderCounts
    PrimitiveColliders = $primitiveColliderArray
    NavigationBlockers = $navigationBlockerArray
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote blocker collider data to $OutputPath"
