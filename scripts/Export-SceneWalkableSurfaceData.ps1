# Exports walkable-surface candidates (floors, ramps, stairs, step-ups) from a
# Unity scene for capsule-sweep navigation baking.
#
# Sibling of Export-SceneBlockerData.ps1. The blocker exporter retains
# obstacles the capsule must avoid; this exporter retains the surfaces the
# capsule stands on. Their filters are intentionally inverted: blockers favour
# tall, thin, grounded geometry; walkables favour short, broad, low-Y geometry.
#
# Output JSON mirrors the blocker exporter's top-level shape, with the
# domain-specific record list renamed to WalkableSurfaces.
#
# Implementation notes:
#   - Mesh footprint is taken as the axis-aligned XZ AABB of all world-space
#     vertices. Floor meshes are roughly slab-like so convex-hull projection
#     and AABB projection give nearly the same area, but AABB is cheaper and
#     more robust to sloped geometry where the convex hull would over-report.
#   - Slope classification combines geometry (vertical extent vs footprint)
#     with name hints. Geometry wins for ambiguous cases:
#       * vertical extent < 0.05 m  -> "flat"
#       * name matches stairs pattern OR vertical extent > 0.5 m -> "stairs"
#       * name matches ramp pattern -> "ramp"
#       * vertical extent <= 0.25 m -> "step-up"
#       * otherwise (0.25 m < extent <= 0.5 m, no name hint) -> "ramp"
#     The 0.25 m step-up threshold matches the player controller's implicit
#     step-up tolerance (BetterPlayerControl ground-clamp at heightdist = 0.25).
#     We cannot distinguish a single-sloped ramp face from a multi-step
#     staircase from AABB alone; the 0.5 m extent threshold is the cutover.
#   - Slope/vertical-extent gating is intentionally NOT applied here. The
#     player controller is Rigidbody-based with no slope limit -- any surface
#     the capsule (r=0.4 m, h=2.5 m) physically clears is walkable. Real
#     clearance is the baker's job (it has blocker geometry too); the exporter
#     just emits surface candidates with diagnostic SlopeKind labels.
#   - ContainingZones is emitted as an empty array; the downstream baker joins
#     this output against the navigation-data file's zone records. Keeping the
#     join downstream avoids duplicating the zone-containment helper here and
#     keeps the walkable record schema independent of zone authoring changes.

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ScenePath = "D:\root\AssetRipper\1.3.12-premium\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox.unity",

    [Parameter()]
    [string]$MeshAssetRoot = "D:\root\AssetRipper\1.3.12-premium\extracted\Ripped\ExportedProject\Assets\Mesh",

    [Parameter()]
    [string]$MeshGuidIndexCachePath = ".\artifacts\navigation\mesh_guid_index.cache.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\thirdpersongreybox-walkable.json",

    # Floor-aware lower clip. Reject surfaces whose TopY is below the LOWEST real interior
    # floor (the crawlspace, SM_Floor_Crawlspace at Y-9.89) by a margin, NOT just below
    # ground. The old -2.0 value clipped the crawlspace floor out (tallied BelowSceneFloor),
    # forcing a downstream recovery hack in bake_navigable_region.py. -12.0 admits the
    # crawlspace floor and its fall-prevention pan (Y-11.6) while still excluding the skybox
    # stage `Cube` (Y-29..-70) — which is far below any real floor. The footprint-area gate
    # and the bake's absolute-XZ scene clip remain the other guards.
    [Parameter()]
    [double]$MinimumWalkableTopY = -12.0,

    # Upper clip: admits the attic floor (Y12.86) and its contents; excludes the roof/skybox
    # above. 16.0 matches the value the shipping bake was generated with (the attic floor and
    # its ~200 surfaces sit at 12.8-15.99); a lower 15.0 silently dropped the highest attic
    # shelf items.
    [Parameter()]
    [double]$MaximumWalkableTopY = 16.0,

    [Parameter()]
    [double]$MinimumFootprintAreaSqM = 0.25,

    [Parameter()]
    [string]$StairsNamePattern = '(?i)stair',

    [Parameter()]
    [string]$RampNamePattern = '(?i)ramp',

    [Parameter()]
    [string]$FloorNamePattern = '(?i)floor',

    [Parameter()]
    [int[]]$SkipMeshLayers = @(18, 31)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Numerics

. (Join-Path $PSScriptRoot "Read-UnityMeshAsset.ps1")

function ConvertTo-Float {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [float]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Parse-Vector3Literal {
    param([Parameter(Mandatory = $true)][string]$Text)
    $match = [regex]::Match($Text, "\{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}")
    if (-not $match.Success) { return $null }
    return [System.Numerics.Vector3]::new(
        (ConvertTo-Float $match.Groups[1].Value),
        (ConvertTo-Float $match.Groups[2].Value),
        (ConvertTo-Float $match.Groups[3].Value)
    )
}

function Parse-QuaternionLiteral {
    param([Parameter(Mandatory = $true)][string]$Text)
    $match = [regex]::Match($Text, "\{x: ([^,]+), y: ([^,]+), z: ([^,]+), w: ([^}]+)\}")
    if (-not $match.Success) { return $null }
    return [System.Numerics.Quaternion]::new(
        (ConvertTo-Float $match.Groups[1].Value),
        (ConvertTo-Float $match.Groups[2].Value),
        (ConvertTo-Float $match.Groups[3].Value),
        (ConvertTo-Float $match.Groups[4].Value)
    )
}

function Convert-Vector3ToObject {
    param([Parameter(Mandatory = $true)][System.Numerics.Vector3]$Vector)
    return [ordered]@{
        x = [Math]::Round([double]$Vector.X, 6)
        y = [Math]::Round([double]$Vector.Y, 6)
        z = [Math]::Round([double]$Vector.Z, 6)
    }
}

function Convert-QuaternionToObject {
    param([Parameter(Mandatory = $true)][System.Numerics.Quaternion]$Quaternion)
    return [ordered]@{
        x = [Math]::Round([double]$Quaternion.X, 6)
        y = [Math]::Round([double]$Quaternion.Y, 6)
        z = [Math]::Round([double]$Quaternion.Z, 6)
        w = [Math]::Round([double]$Quaternion.W, 6)
    }
}

function Get-LineValue {
    param(
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Pattern
    )
    foreach ($line in $Lines) {
        $match = [regex]::Match($line, $Pattern)
        if ($match.Success) { return $match.Groups[1].Value }
    }
    return $null
}

function Get-LineValueOrDefault {
    param(
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$DefaultValue
    )
    $value = Get-LineValue -Lines $Lines -Pattern $Pattern
    if ($null -eq $value) { return $DefaultValue }
    return $value
}

function Get-SectionTypeInfo {
    param([Parameter(Mandatory = $true)][string]$Header)
    $match = [regex]::Match($Header, "^--- !u!(\d+) &(\d+)$")
    if (-not $match.Success) { return $null }
    return [ordered]@{
        Type = [int]$match.Groups[1].Value
        Id = [long]$match.Groups[2].Value
    }
}

function Get-WorldTransform {
    param(
        [Parameter(Mandatory = $true)][long]$TransformId,
        [Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[long, object]]$TransformsById,
        [Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[long, object]]$WorldTransforms
    )

    if ($WorldTransforms.ContainsKey($TransformId)) { return $WorldTransforms[$TransformId] }
    if (-not $TransformsById.ContainsKey($TransformId)) { return $null }

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
        [Parameter(Mandatory = $true)][object]$WorldTransform,
        [Parameter(Mandatory = $true)][System.Numerics.Vector3]$LocalPoint
    )
    $scaled = [System.Numerics.Vector3]::new(
        $LocalPoint.X * $WorldTransform.Scale.X,
        $LocalPoint.Y * $WorldTransform.Scale.Y,
        $LocalPoint.Z * $WorldTransform.Scale.Z
    )
    return $WorldTransform.Position + [System.Numerics.Vector3]::Transform($scaled, $WorldTransform.Rotation)
}

function Get-GameObjectPath {
    param(
        [Parameter(Mandatory = $true)][long]$GameObjectId,
        [Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[long, object]]$GameObjects,
        [Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[long, object]]$TransformByGameObjectId,
        [Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[long, object]]$TransformsById
    )

    if (-not $GameObjects.ContainsKey($GameObjectId)) { return $null }

    $segments = New-Object System.Collections.Generic.List[string]
    $currentGameObjectId = $GameObjectId
    while ($currentGameObjectId -ne 0 -and $GameObjects.ContainsKey($currentGameObjectId)) {
        $gameObject = $GameObjects[$currentGameObjectId]
        $segments.Add($gameObject.Name)
        if (-not $TransformByGameObjectId.ContainsKey($currentGameObjectId)) { break }
        $transformInfo = $TransformByGameObjectId[$currentGameObjectId]
        if ($transformInfo.ParentTransformId -eq 0 -or -not $TransformsById.ContainsKey($transformInfo.ParentTransformId)) { break }
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
        [Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[string, int]]$Counter,
        [Parameter(Mandatory = $true)][string]$Reason
    )
    if ($Counter.ContainsKey($Reason)) { $Counter[$Reason]++ } else { $Counter[$Reason] = 1 }
}

function Convert-ReasonCounterToArray {
    param([Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[string, int]]$Counter)
    return @($Counter.Keys | Sort-Object | ForEach-Object {
        [ordered]@{ Reason = [string]$_; Count = $Counter[[string]$_] }
    })
}

function Build-MeshGuidIndex {
    param(
        [Parameter(Mandatory = $true)][string]$MeshRoot,
        [Parameter(Mandatory = $true)][string]$CachePath
    )

    $latestMetaWrite = [datetime]::MinValue
    $metaFiles = Get-ChildItem -Path $MeshRoot -Filter "*.asset.meta" -File
    foreach ($mf in $metaFiles) {
        if ($mf.LastWriteTimeUtc -gt $latestMetaWrite) { $latestMetaWrite = $mf.LastWriteTimeUtc }
    }

    if (Test-Path -LiteralPath $CachePath) {
        $cacheFile = Get-Item -LiteralPath $CachePath
        if ($cacheFile.LastWriteTimeUtc -ge $latestMetaWrite) {
            $cached = Get-Content -LiteralPath $CachePath -Raw | ConvertFrom-Json
            if ($cached.MeshRoot -eq $MeshRoot -and $cached.MetaCount -eq $metaFiles.Count) {
                $dict = [System.Collections.Generic.Dictionary[string, string]]::new()
                foreach ($prop in $cached.Entries.PSObject.Properties) {
                    $dict[$prop.Name] = [string]$prop.Value
                }
                return $dict
            }
        }
    }

    $dict = [System.Collections.Generic.Dictionary[string, string]]::new()
    foreach ($mf in $metaFiles) {
        $lines = [System.IO.File]::ReadAllLines($mf.FullName)
        foreach ($line in $lines) {
            if ($line.StartsWith("guid: ")) {
                $guid = $line.Substring(6).Trim()
                $assetPath = $mf.FullName.Substring(0, $mf.FullName.Length - 5)
                $dict[$guid] = $assetPath
                break
            }
        }
    }

    $cacheDir = Split-Path -Parent $CachePath
    if (-not [string]::IsNullOrWhiteSpace($cacheDir)) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    }
    $entries = [ordered]@{}
    foreach ($kvp in $dict.GetEnumerator()) { $entries[$kvp.Key] = $kvp.Value }
    $cacheObj = [ordered]@{
        MeshRoot = $MeshRoot
        MetaCount = $metaFiles.Count
        GeneratedAt = (Get-Date).ToString("o")
        Entries = $entries
    }
    $cacheObj | ConvertTo-Json -Depth 4 -Compress | Set-Content -LiteralPath $CachePath -Encoding UTF8
    return $dict
}

# Classify a surface's slope kind from its world-space extents and game object
# name. See file header for full rules. Geometry takes precedence over name
# except when names explicitly disambiguate stairs vs ramp.
function Get-SlopeKind {
    param(
        [Parameter(Mandatory = $true)][double]$VerticalExtent,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$StairsPattern,
        [Parameter(Mandatory = $true)][string]$RampPattern
    )

    if ($VerticalExtent -lt 0.05) { return "flat" }

    $nameIsStairs = ($Name -match $StairsPattern)
    $nameIsRamp = ($Name -match $RampPattern)

    if ($nameIsStairs) { return "stairs" }
    if ($VerticalExtent -gt 0.5) { return "stairs" }
    if ($nameIsRamp) { return "ramp" }
    if ($VerticalExtent -le 0.25) { return "step-up" }
    return "ramp"
}

# Build the walkable-surface record for a candidate that has already been
# reduced to its world-space XZ AABB plus Y range. Common to primitive and
# mesh colliders so the JSON shape stays uniform.
function New-WalkableSurfaceRecord {
    param(
        [Parameter(Mandatory = $true)][object]$GameObject,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ColliderKind,
        [Parameter(Mandatory = $true)][long]$ComponentId,
        [Parameter(Mandatory = $true)][long]$TransformId,
        [Parameter()][AllowNull()][string]$MeshAssetGuid,
        [Parameter(Mandatory = $true)][double]$MinX,
        [Parameter(Mandatory = $true)][double]$MaxX,
        [Parameter(Mandatory = $true)][double]$MinZ,
        [Parameter(Mandatory = $true)][double]$MaxZ,
        [Parameter(Mandatory = $true)][double]$BottomY,
        [Parameter(Mandatory = $true)][double]$TopY,
        [Parameter(Mandatory = $true)][string]$SlopeKind
    )

    $width = $MaxX - $MinX
    $depth = $MaxZ - $MinZ
    $area = [Math]::Max(0.0, $width) * [Math]::Max(0.0, $depth)
    $centerX = ($MinX + $MaxX) / 2.0
    $centerZ = ($MinZ + $MaxZ) / 2.0

    return [ordered]@{
        GameObjectId = $GameObject.Id
        GameObjectName = $GameObject.Name
        Path = $Path
        Layer = $GameObject.Layer
        IsActive = $GameObject.IsActive
        ColliderKind = $ColliderKind
        ComponentId = $ComponentId
        TransformId = $TransformId
        MeshAssetGuid = $MeshAssetGuid
        SlopeKind = $SlopeKind
        BottomY = [Math]::Round($BottomY, 6)
        TopY = [Math]::Round($TopY, 6)
        VerticalExtent = [Math]::Round($TopY - $BottomY, 6)
        Footprint = [ordered]@{
            AreaSqM = [Math]::Round($area, 6)
            Width = [Math]::Round($width, 6)
            Depth = [Math]::Round($depth, 6)
            CenterX = [Math]::Round($centerX, 6)
            CenterZ = [Math]::Round($centerZ, 6)
            MinX = [Math]::Round($MinX, 6)
            MinZ = [Math]::Round($MinZ, 6)
            MaxX = [Math]::Round($MaxX, 6)
            MaxZ = [Math]::Round($MaxZ, 6)
        }
        # TODO: ContainingZones is intentionally empty here. The downstream
        # navmesh baker performs the zone-containment join against the
        # navigation-data file so this script stays independent of zone
        # authoring. See Export-SceneNavigationData.ps1 for the join helper.
        ContainingZones = @()
    }
}

if (-not (Test-Path -LiteralPath $ScenePath)) { throw "Scene file not found: $ScenePath" }

$gameObjects = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformsById = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
$doorGameObjectIds = New-Object System.Collections.Generic.HashSet[long]
$teleporterGameObjectIds = New-Object System.Collections.Generic.HashSet[long]
$rigidbodyByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
$primitiveColliderComponents = New-Object System.Collections.Generic.List[object]
$meshColliderComponents = New-Object System.Collections.Generic.List[object]
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
        64 {
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -ne $gameObjectIdText) {
                $meshGuid = Get-LineValue -Lines $linesArray -Pattern "^  m_Mesh: \{fileID: -?\d+, guid: ([0-9a-f]+), type: \d+\}\s*$"
                $script:meshColliderComponents.Add([pscustomobject]@{
                    ComponentId = $sectionInfo.Id; ColliderType = "MeshCollider"; GameObjectId = [long]$gameObjectIdText
                    Enabled = ((Get-LineValue -Lines $linesArray -Pattern "^  m_Enabled: (\d+)$") -ne "0")
                    IsTrigger = ((Get-LineValue -Lines $linesArray -Pattern "^  m_IsTrigger: (\d+)$") -eq "1")
                    IsConvex = ((Get-LineValue -Lines $linesArray -Pattern "^  m_Convex: (\d+)$") -eq "1")
                    MeshGuid = $meshGuid
                })
            }
        }
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
    if ($null -ne $currentHeader) { $currentLines.Add($line) }
}

if ($null -ne $currentHeader) {
    Process-SceneSection -Header $currentHeader -Lines $currentLines
}

$worldTransforms = [System.Collections.Generic.Dictionary[long, object]]::new()
foreach ($transformId in $transformsById.Keys) {
    [void](Get-WorldTransform -TransformId $transformId -TransformsById $transformsById -WorldTransforms $worldTransforms)
}

$primitiveColliders = New-Object System.Collections.Generic.List[object]
$walkableSurfaces = New-Object System.Collections.Generic.List[object]
$ignoredPrimitiveReasons = New-ReasonCounter

# Helper: returns @{ MinX; MaxX; MinZ; MaxZ; BottomY; TopY } from a list of
# world-space corner points. Inlined here to avoid the PowerShell single-
# element list-return unrolling foot-gun.
function Get-WorldAabbFromPoints {
    param([Parameter(Mandatory = $true)][System.Collections.Generic.List[System.Numerics.Vector3]]$Points)
    $minX = [double]::PositiveInfinity; $maxX = [double]::NegativeInfinity
    $minY = [double]::PositiveInfinity; $maxY = [double]::NegativeInfinity
    $minZ = [double]::PositiveInfinity; $maxZ = [double]::NegativeInfinity
    foreach ($p in $Points) {
        if ($p.X -lt $minX) { $minX = $p.X }
        if ($p.X -gt $maxX) { $maxX = $p.X }
        if ($p.Y -lt $minY) { $minY = $p.Y }
        if ($p.Y -gt $maxY) { $maxY = $p.Y }
        if ($p.Z -lt $minZ) { $minZ = $p.Z }
        if ($p.Z -gt $maxZ) { $maxZ = $p.Z }
    }
    return [ordered]@{
        MinX = [double]$minX; MaxX = [double]$maxX
        MinZ = [double]$minZ; MaxZ = [double]$maxZ
        BottomY = [double]$minY; TopY = [double]$maxY
    }
}

function Get-PrimitiveAabb {
    param(
        [Parameter(Mandatory = $true)][object]$Component,
        [Parameter(Mandatory = $true)][object]$WorldTransform
    )

    $points = New-Object System.Collections.Generic.List[System.Numerics.Vector3]
    switch ($Component.ColliderType) {
        "BoxCollider" {
            $halfX = $Component.Size.X / 2.0
            $halfY = $Component.Size.Y / 2.0
            $halfZ = $Component.Size.Z / 2.0
            foreach ($sx in @(-1, 1)) {
                foreach ($sy in @(-1, 1)) {
                    foreach ($sz in @(-1, 1)) {
                        $localPoint = [System.Numerics.Vector3]::new(
                            $Component.Center.X + ($halfX * $sx),
                            $Component.Center.Y + ($halfY * $sy),
                            $Component.Center.Z + ($halfZ * $sz))
                        $points.Add((Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $localPoint))
                    }
                }
            }
        }
        "SphereCollider" {
            $center = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $Component.Center
            $maxScale = [Math]::Max([Math]::Abs($WorldTransform.Scale.X), [Math]::Max([Math]::Abs($WorldTransform.Scale.Y), [Math]::Abs($WorldTransform.Scale.Z)))
            $r = $Component.Radius * $maxScale
            $points.Add([System.Numerics.Vector3]::new($center.X - $r, $center.Y - $r, $center.Z - $r))
            $points.Add([System.Numerics.Vector3]::new($center.X + $r, $center.Y + $r, $center.Z + $r))
        }
        "CapsuleCollider" {
            $directionAxis = switch ($Component.Direction) {
                0 { [System.Numerics.Vector3]::new(1, 0, 0) }
                2 { [System.Numerics.Vector3]::new(0, 0, 1) }
                default { [System.Numerics.Vector3]::new(0, 1, 0) }
            }
            $segmentHalf = [Math]::Max(0.0, ($Component.Height / 2.0) - $Component.Radius)
            $maxScale = [Math]::Max([Math]::Abs($WorldTransform.Scale.X), [Math]::Max([Math]::Abs($WorldTransform.Scale.Y), [Math]::Abs($WorldTransform.Scale.Z)))
            $r = $Component.Radius * $maxScale
            $worldStart = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint ($Component.Center + ($directionAxis * [float]$segmentHalf))
            $worldEnd = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint ($Component.Center - ($directionAxis * [float]$segmentHalf))
            $points.Add([System.Numerics.Vector3]::new($worldStart.X - $r, $worldStart.Y - $r, $worldStart.Z - $r))
            $points.Add([System.Numerics.Vector3]::new($worldStart.X + $r, $worldStart.Y + $r, $worldStart.Z + $r))
            $points.Add([System.Numerics.Vector3]::new($worldEnd.X - $r, $worldEnd.Y - $r, $worldEnd.Z - $r))
            $points.Add([System.Numerics.Vector3]::new($worldEnd.X + $r, $worldEnd.Y + $r, $worldEnd.Z + $r))
        }
        default { return $null }
    }

    return Get-WorldAabbFromPoints -Points $points
}

foreach ($component in $primitiveColliderComponents) {
    if (-not $gameObjects.ContainsKey($component.GameObjectId) -or -not $transformByGameObjectId.ContainsKey($component.GameObjectId)) {
        Add-ReasonCount -Counter $ignoredPrimitiveReasons -Reason "MissingSceneObject"
        continue
    }

    $gameObject = $gameObjects[$component.GameObjectId]
    $transformInfo = $transformByGameObjectId[$component.GameObjectId]
    if (-not $worldTransforms.ContainsKey($transformInfo.Id)) {
        Add-ReasonCount -Counter $ignoredPrimitiveReasons -Reason "MissingWorldTransform"
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

    $aabb = Get-PrimitiveAabb -Component $component -WorldTransform $worldTransform
    if ($null -eq $aabb) {
        Add-ReasonCount -Counter $ignoredPrimitiveReasons -Reason "UnsupportedPrimitiveShape"
        continue
    }

    $primitiveColliders.Add([ordered]@{
        ComponentId = $component.ComponentId
        GameObjectId = $gameObject.Id
        Name = $gameObject.Name
        Layer = $gameObject.Layer
        ColliderType = $component.ColliderType
        Enabled = $component.Enabled
        IsTrigger = $component.IsTrigger
        IsActive = $gameObject.IsActive
        BottomY = [Math]::Round($aabb.BottomY, 6)
        TopY = [Math]::Round($aabb.TopY, 6)
        MinX = [Math]::Round($aabb.MinX, 6)
        MaxX = [Math]::Round($aabb.MaxX, 6)
        MinZ = [Math]::Round($aabb.MinZ, 6)
        MaxZ = [Math]::Round($aabb.MaxZ, 6)
    })

    $reason = $null
    if (-not $component.Enabled) { $reason = "DisabledCollider" }
    elseif ($component.IsTrigger) { $reason = "TriggerCollider" }
    elseif (-not $gameObject.IsActive) { $reason = "InactiveGameObject" }
    elseif ($component.IsDoorConnector) { $reason = "DoorConnector" }
    elseif ($component.IsTeleporterConnector) { $reason = "TeleporterConnector" }
    elseif ($component.HasRigidbody) { $reason = "RigidbodyObject" }
    elseif ($aabb.TopY -lt $MinimumWalkableTopY) { $reason = "BelowSceneFloor" }
    elseif ($aabb.TopY -gt $MaximumWalkableTopY) { $reason = "AboveWalkableCeiling" }
    else {
        $width = [double]($aabb.MaxX - $aabb.MinX)
        $depth = [double]($aabb.MaxZ - $aabb.MinZ)
        $horizontalMax = [Math]::Max($width, $depth)
        $area = [Math]::Max(0.0, $width) * [Math]::Max(0.0, $depth)
        if ($area -lt $MinimumFootprintAreaSqM) { $reason = "FootprintTooSmall" }
        elseif ($horizontalMax -le 0) { $reason = "DegenerateFootprint" }
    }

    if ($null -ne $reason) {
        Add-ReasonCount -Counter $ignoredPrimitiveReasons -Reason $reason
        continue
    }

    $verticalExtent = [double]($aabb.TopY - $aabb.BottomY)
    $slopeKind = Get-SlopeKind -VerticalExtent $verticalExtent -Name $gameObject.Name -StairsPattern $StairsNamePattern -RampPattern $RampNamePattern

    $path = Get-GameObjectPath -GameObjectId $gameObject.Id -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById
    $record = New-WalkableSurfaceRecord -GameObject $gameObject -Path $path -ColliderKind "Primitive" `
        -ComponentId $component.ComponentId -TransformId $transformInfo.Id -MeshAssetGuid $null `
        -MinX $aabb.MinX -MaxX $aabb.MaxX -MinZ $aabb.MinZ -MaxZ $aabb.MaxZ `
        -BottomY $aabb.BottomY -TopY $aabb.TopY -SlopeKind $slopeKind

    $walkableSurfaces.Add($record)
}

$meshSupported = $false
$meshGuidIndexCount = 0
$meshColliderRecords = New-Object System.Collections.Generic.List[object]
$meshColliderIgnoredReasons = New-ReasonCounter
$meshLayerHistogram = [System.Collections.Generic.Dictionary[int, int]]::new()
$meshColliderResolvedCount = 0
$meshColliderUnresolvedCount = 0
$meshParseFailures = New-Object System.Collections.Generic.List[object]
$meshCache = [System.Collections.Generic.Dictionary[string, object]]::new()

if ($meshColliderComponents.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($MeshAssetRoot) -and (Test-Path -LiteralPath $MeshAssetRoot)) {
    $meshSupported = $true
    Write-Host ("Building mesh GUID index from {0}" -f $MeshAssetRoot)
    $guidIndex = Build-MeshGuidIndex -MeshRoot $MeshAssetRoot -CachePath $MeshGuidIndexCachePath
    $meshGuidIndexCount = $guidIndex.Count
    Write-Host ("Mesh GUID index entries: {0}" -f $meshGuidIndexCount)

    $skipLayerSet = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($lyr in $SkipMeshLayers) { [void]$skipLayerSet.Add([int]$lyr) }

    foreach ($component in $meshColliderComponents) {
        if (-not $gameObjects.ContainsKey($component.GameObjectId) -or -not $transformByGameObjectId.ContainsKey($component.GameObjectId)) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "MissingSceneObject"
            continue
        }
        $gameObject = $gameObjects[$component.GameObjectId]
        if ($meshLayerHistogram.ContainsKey($gameObject.Layer)) { $meshLayerHistogram[$gameObject.Layer]++ } else { $meshLayerHistogram[$gameObject.Layer] = 1 }

        $transformInfo = $transformByGameObjectId[$component.GameObjectId]
        if (-not $worldTransforms.ContainsKey($transformInfo.Id)) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "MissingWorldTransform"
            continue
        }

        if ($null -eq $component.MeshGuid) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "MissingMeshReference"
            $script:meshColliderUnresolvedCount++
            continue
        }

        if (-not $guidIndex.ContainsKey($component.MeshGuid)) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "MeshGuidUnresolved"
            $script:meshColliderUnresolvedCount++
            continue
        }

        $script:meshColliderResolvedCount++

        $rigidbodyIsKinematic = $false
        if ($rigidbodyByGameObjectId.ContainsKey($gameObject.Id)) { $rigidbodyIsKinematic = $rigidbodyByGameObjectId[$gameObject.Id].IsKinematic }
        $component | Add-Member -NotePropertyName TransformId -NotePropertyValue $transformInfo.Id -Force
        $component | Add-Member -NotePropertyName IsDoorConnector -NotePropertyValue ($doorGameObjectIds.Contains($gameObject.Id)) -Force
        $component | Add-Member -NotePropertyName IsTeleporterConnector -NotePropertyValue ($teleporterGameObjectIds.Contains($gameObject.Id)) -Force
        $component | Add-Member -NotePropertyName HasRigidbody -NotePropertyValue ($rigidbodyByGameObjectId.ContainsKey($gameObject.Id)) -Force
        $component | Add-Member -NotePropertyName RigidbodyIsKinematic -NotePropertyValue $rigidbodyIsKinematic -Force

        if (-not $component.Enabled) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "DisabledCollider"; continue }
        if ($component.IsTrigger) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "TriggerCollider"; continue }
        if (-not $gameObject.IsActive) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "InactiveGameObject"; continue }
        if ($component.IsDoorConnector) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "DoorConnector"; continue }
        if ($component.IsTeleporterConnector) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "TeleporterConnector"; continue }
        if ($component.HasRigidbody) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "RigidbodyObject"; continue }
        if ($skipLayerSet.Contains($gameObject.Layer)) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason ("SkippedLayer:{0}" -f $gameObject.Layer); continue }

        $assetPath = $guidIndex[$component.MeshGuid]
        $meshData = $null
        if ($meshCache.ContainsKey($assetPath)) {
            $meshData = $meshCache[$assetPath]
        } else {
            try {
                $meshData = Read-UnityMeshAsset -Path $assetPath
                $meshCache[$assetPath] = $meshData
            } catch {
                $meshCache[$assetPath] = $null
                $meshParseFailures.Add([ordered]@{ AssetPath = $assetPath; Guid = $component.MeshGuid; Error = $_.Exception.Message })
            }
        }
        if ($null -eq $meshData) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "MeshParseFailed"
            continue
        }

        $worldTransform = $worldTransforms[$transformInfo.Id]

        # Project every mesh vertex to world space and take the world-space
        # AABB. Floor/ramp/stairs meshes are slab-like so AABB area is a good
        # approximation of footprint coverage, and there's no slicing involved
        # the way the blocker exporter does for tall obstacles.
        $worldPoints = New-Object System.Collections.Generic.List[System.Numerics.Vector3]
        foreach ($v in $meshData.Vertices) {
            $worldPoints.Add((Transform-LocalPoint -WorldTransform $worldTransform -LocalPoint $v))
        }

        if ($worldPoints.Count -lt 1) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "EmptyMesh"
            continue
        }

        $aabb = Get-WorldAabbFromPoints -Points $worldPoints

        $meshColliderRecords.Add([ordered]@{
            ComponentId = $component.ComponentId
            GameObjectId = $gameObject.Id
            Name = $gameObject.Name
            Layer = $gameObject.Layer
            MeshName = $meshData.Name
            MeshGuid = $component.MeshGuid
            VertexCount = $meshData.VertexCount
            TriangleCount = $meshData.TriangleCount
            BottomY = [Math]::Round($aabb.BottomY, 6)
            TopY = [Math]::Round($aabb.TopY, 6)
            MinX = [Math]::Round($aabb.MinX, 6)
            MaxX = [Math]::Round($aabb.MaxX, 6)
            MinZ = [Math]::Round($aabb.MinZ, 6)
            MaxZ = [Math]::Round($aabb.MaxZ, 6)
        })

        $reason = $null
        if ($aabb.TopY -lt $MinimumWalkableTopY) { $reason = "BelowSceneFloor" }
        elseif ($aabb.TopY -gt $MaximumWalkableTopY) { $reason = "AboveWalkableCeiling" }
        else {
            $width = [double]($aabb.MaxX - $aabb.MinX)
            $depth = [double]($aabb.MaxZ - $aabb.MinZ)
            $horizontalMax = [Math]::Max($width, $depth)
            $area = [Math]::Max(0.0, $width) * [Math]::Max(0.0, $depth)
            if ($area -lt $MinimumFootprintAreaSqM) { $reason = "FootprintTooSmall" }
            elseif ($horizontalMax -le 0) { $reason = "DegenerateFootprint" }
        }

        if ($null -ne $reason) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason $reason
            continue
        }

        $verticalExtent = [double]($aabb.TopY - $aabb.BottomY)
        $slopeKind = Get-SlopeKind -VerticalExtent $verticalExtent -Name $gameObject.Name -StairsPattern $StairsNamePattern -RampPattern $RampNamePattern

        $path = Get-GameObjectPath -GameObjectId $gameObject.Id -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById
        $record = New-WalkableSurfaceRecord -GameObject $gameObject -Path $path -ColliderKind "Mesh" `
            -ComponentId $component.ComponentId -TransformId $transformInfo.Id -MeshAssetGuid $component.MeshGuid `
            -MinX $aabb.MinX -MaxX $aabb.MaxX -MinZ $aabb.MinZ -MaxZ $aabb.MaxZ `
            -BottomY $aabb.BottomY -TopY $aabb.TopY -SlopeKind $slopeKind

        $walkableSurfaces.Add($record)
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$ignoredPrimitiveColliderCounts = @(Convert-ReasonCounterToArray -Counter $ignoredPrimitiveReasons)
$ignoredMeshColliderCounts = @(Convert-ReasonCounterToArray -Counter $meshColliderIgnoredReasons)
$meshLayerHistogramArray = @($meshLayerHistogram.Keys | Sort-Object | ForEach-Object { [ordered]@{ Layer = [int]$_; Count = [int]$meshLayerHistogram[$_] } })
$primitiveColliderArray = $primitiveColliders.ToArray()
$meshColliderArray = $meshColliderRecords.ToArray()
$walkableSurfaceArray = $walkableSurfaces.ToArray()

# Layer inventory mirrors the blocker exporter so cross-export comparisons
# stay apples-to-apples (same layers seen as candidates, even if filters reject).
$primitiveGameObjectIds = [System.Collections.Generic.HashSet[long]]::new()
foreach ($component in $primitiveColliderComponents) { [void]$primitiveGameObjectIds.Add([long]$component.GameObjectId) }
$meshGameObjectIds = [System.Collections.Generic.HashSet[long]]::new()
foreach ($component in $meshColliderComponents) { [void]$meshGameObjectIds.Add([long]$component.GameObjectId) }

$layerInventory = [System.Collections.Generic.Dictionary[int, object]]::new()
foreach ($entry in $gameObjects.GetEnumerator()) {
    $go = $entry.Value
    $layer = [int]$go.Layer
    if ($layer -lt 8) { continue }
    if (-not $layerInventory.ContainsKey($layer)) {
        $layerInventory[$layer] = [pscustomobject]@{
            Layer = $layer
            Count = 0
            CountWithPrimitiveCollider = 0
            CountWithMeshCollider = 0
            CountActive = 0
            SamplePaths = New-Object System.Collections.Generic.List[string]
        }
    }
    $bucket = $layerInventory[$layer]
    $bucket.Count++
    if ($go.IsActive) { $bucket.CountActive++ }
    $hasPrim = $primitiveGameObjectIds.Contains([long]$go.Id)
    $hasMesh = $meshGameObjectIds.Contains([long]$go.Id)
    if ($hasPrim) { $bucket.CountWithPrimitiveCollider++ }
    if ($hasMesh) { $bucket.CountWithMeshCollider++ }
    if ($bucket.SamplePaths.Count -lt 25 -and ($hasPrim -or $hasMesh)) {
        $path = Get-GameObjectPath -GameObjectId $go.Id -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById
        if (-not [string]::IsNullOrWhiteSpace($path)) { [void]$bucket.SamplePaths.Add($path) }
    }
}

$layerInventoryArray = @(
    $layerInventory.Keys | Sort-Object | ForEach-Object {
        $b = $layerInventory[$_]
        [ordered]@{
            Layer = $b.Layer
            Count = $b.Count
            CountActive = $b.CountActive
            CountWithPrimitiveCollider = $b.CountWithPrimitiveCollider
            CountWithMeshCollider = $b.CountWithMeshCollider
            SamplePaths = @($b.SamplePaths)
        }
    }
)

# Slope-kind histogram over retained surfaces; cheap to compute and useful for
# eyeballing the export at a glance ("did we actually find any stairs?").
$slopeKindCounts = [System.Collections.Generic.Dictionary[string, int]]::new()
foreach ($surface in $walkableSurfaceArray) {
    $kind = [string]$surface.SlopeKind
    if ($slopeKindCounts.ContainsKey($kind)) { $slopeKindCounts[$kind]++ } else { $slopeKindCounts[$kind] = 1 }
}
$slopeKindHistogram = @($slopeKindCounts.Keys | Sort-Object | ForEach-Object {
    [ordered]@{ SlopeKind = [string]$_; Count = [int]$slopeKindCounts[$_] }
})

$result = [ordered]@{
    ScenePath = $ScenePath
    MeshAssetRoot = $MeshAssetRoot
    GeneratedAt = (Get-Date).ToString("o")
    Filtering = [ordered]@{
        MinimumWalkableTopY = [Math]::Round($MinimumWalkableTopY, 4)
        MaximumWalkableTopY = [Math]::Round($MaximumWalkableTopY, 4)
        MinimumFootprintAreaSqM = [Math]::Round($MinimumFootprintAreaSqM, 4)
        StairsNamePattern = $StairsNamePattern
        RampNamePattern = $RampNamePattern
        FloorNamePattern = $FloorNamePattern
        DoorAndTeleporterCollidersExcluded = $true
        RigidbodyObjectsExcluded = $true
        MeshCollidersUnsupported = (-not $meshSupported)
        SkipMeshLayers = @($SkipMeshLayers | ForEach-Object { [int]$_ })
        TerrainCollidersUnsupported = $true
    }
    Counts = [ordered]@{
        GameObjects = $gameObjects.Count
        PrimitiveColliders = $primitiveColliders.Count
        WalkableSurfaces = $walkableSurfaces.Count
        MeshColliders = $meshColliderComponents.Count
        MeshCollidersResolved = $meshColliderResolvedCount
        MeshCollidersUnresolved = $meshColliderUnresolvedCount
        MeshColliderRecords = $meshColliderRecords.Count
        MeshGuidIndexEntries = $meshGuidIndexCount
        TerrainColliders = $terrainColliderCount
        DoorGameObjects = $doorGameObjectIds.Count
        TeleporterGameObjects = $teleporterGameObjectIds.Count
        RigidbodyObjects = $rigidbodyByGameObjectId.Count
    }
    SlopeKindHistogram = $slopeKindHistogram
    MeshColliderLayerHistogram = $meshLayerHistogramArray
    LayerInventory = $layerInventoryArray
    IgnoredPrimitiveColliderCounts = $ignoredPrimitiveColliderCounts
    IgnoredMeshColliderCounts = $ignoredMeshColliderCounts
    MeshParseFailures = $meshParseFailures.ToArray()
    PrimitiveColliders = $primitiveColliderArray
    MeshColliders = $meshColliderArray
    WalkableSurfaces = $walkableSurfaceArray
}

$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote walkable surface data to $OutputPath"
Write-Host ("MeshColliders parsed: {0}/{1}  Records: {2}  Total walkable surfaces: {3}" -f $meshColliderResolvedCount, $meshColliderComponents.Count, $meshColliderRecords.Count, $walkableSurfaces.Count)
