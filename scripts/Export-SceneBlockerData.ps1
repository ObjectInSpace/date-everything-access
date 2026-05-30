[CmdletBinding()]
param(
    [Parameter()]
    [string]$ScenePath = "D:\root\AssetRipper\1.3.12-premium\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox.unity",

    [Parameter()]
    [string]$MeshAssetRoot = "D:\root\AssetRipper\1.3.12-premium\extracted\Ripped\ExportedProject\Assets\Mesh",

    [Parameter()]
    [string]$MeshGuidIndexCachePath = ".\artifacts\navigation\mesh_guid_index.cache.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\thirdpersongreybox-blockers.json",

    [Parameter()]
    [double]$MinimumBlockingTopY = 0.35,

    [Parameter()]
    [double]$MaximumBlockingBottomY = 15.0,

    [Parameter()]
    [double]$MinimumFootprintRadius = 0.15,

    # Mesh slice planes (world Y). Each plane intersects the mesh and contributes
    # to its 2D footprint. One plane per floor at floor_Y + 1.0m, which sits
    # squarely in the middle of the player capsule (r=0.4, h=2.5; center at
    # floor + 1.25m). Lower slice planes (e.g. floor + 0.6m) catch doorway
    # threshold geometry that seals the doorway in the bake even though the
    # opening is clear at capsule-middle height; see the bedroom-door analysis
    # in the slope-filter-retirement follow-up.
    [Parameter()]
    # Slice planes are positioned at player-eye height above each floor band.
    # 0.5 = ground floor (floor_y -0.5 + 1.0m); 12.5 catches walls whose tops
    # sit just above the upper floor (upper floor_y 12.5); 13.5 catches walls
    # that extend higher into the upper-floor band.
    # Adding 12.5: many ground-floor walls (SM_Walls_Living, SM_Walls_Hall1,
    # Fireplace, etc.) have TopY in [12.39, 12.59], i.e. between the upper
    # floor level and the next slice plane at 13.5. Without a slice at 12.5
    # they emit zero segments inside the upper-floor bake band [12.25, 15.0],
    # leaving the staircase region apparently wall-free in upper routing.
    # See [[project-navigation-walls-living-upper-slice-gap]].
    [double[]]$MeshSlicePlanes = @(0.5, 12.5, 13.5),

    # NOTE: the former MaxMeshFootprintAreaSqM / Wall* (wall-FAT bypass) and
    # ThinFrame* (doorframe AABB-suppression) parameters are retired. They tuned
    # whether a mesh collider's AABB was rasterized or suppressed in favour of
    # its slice segments. The bake now rasterizes ONLY slice segments for every
    # mesh collider (no AABB path at all), so those signatures no longer gate
    # anything. Wall-vs-slab handling moved to the bake's _is_vertical_wall shape
    # test. See [[project-navigation-iswalllikefatvictim-followup]].

    # Player physics layer (Unity layer index). The player capsule lives on
    # FPEPlayer (layer 10). A collider only blocks the player if Unity's layer
    # collision matrix lets layer 10 collide with the collider's layer. The
    # admissible-layer set is derived from DynamicsManager.asset below, replacing
    # a hand-maintained skip list. See [[project-navigation-capsule-radius-groundtruth-2026-05-29]].
    [Parameter()]
    [int]$PlayerLayer = 10,

    # ProjectSettings/DynamicsManager.asset — source of m_LayerCollisionMatrix.
    # The exporter parses it once and drops any collider whose layer the player
    # layer does NOT collide with (authoritative physics, not a name/layer
    # heuristic). Falls back to the SkipMeshLayers list if the asset is absent.
    [Parameter()]
    [string]$DynamicsManagerPath = "D:\root\AssetRipper\1.3.12-premium\extracted\Ripped\ExportedProject\ProjectSettings\DynamicsManager.asset",

    [Parameter()]
    # Fallback only (used when DynamicsManagerPath is unavailable): layers to
    # skip when the collision matrix cannot be read. Layer 31 = 3DUI (UI
    # elements, not physical). Layer 18 = Mirror; in this scene only
    # PF_PlanarReflection (reflection-probe helper, no MeshCollider) and
    # /House/Hallway/Stairs carry it, and only the Stairs has a MeshCollider.
    # The Stairs is admitted: at the ground slice plane (Y=0.5) its segments are
    # the bottom-landing SIDE WALLS (X=11.18-13.51, Z=-5.83..0.25), not the
    # climbable tread (the treads start higher and only appear at the upper
    # planes). Rasterizing those side walls makes A* route around the staircase
    # instead of straight into the rail, fixing the runtime stall. The baker
    # treats the Stairs as structural so only its slice traces rasterize.
    # See [[project-navigation-stairs-runtime-collision]].
    [int[]]$SkipMeshLayers = @(31)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Numerics

# Dot-source the mesh parser so Read-UnityMeshAsset is in-scope.
. (Join-Path $PSScriptRoot "Read-UnityMeshAsset.ps1")

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

function Get-PlayerCollidableLayers {
    <#
    .SYNOPSIS
    Returns the HashSet[int] of Unity layers the player layer collides with,
    derived from DynamicsManager.asset's m_LayerCollisionMatrix. Returns $null
    if the asset can't be read (caller falls back to the skip list).

    The matrix is serialized as 32 '/'-separated hex groups; group index = layer
    index, and each group is a hex mask where bit j set means "this layer
    collides with layer j". Trailing high layers (28-31) are trimmed from the
    hex (shorter group => those bits are 0). Verified against the FPEPlayer row
    (layer 10 = 'fffddff' => collides with 0-8,10-12,14-27; excludes 9,13,28-31).
    #>
    param(
        [Parameter(Mandatory = $true)] [string]$DynamicsManagerPath,
        [Parameter(Mandatory = $true)] [int]$PlayerLayer
    )

    if ([string]::IsNullOrWhiteSpace($DynamicsManagerPath) -or -not (Test-Path -LiteralPath $DynamicsManagerPath)) {
        return $null
    }

    $matrixLine = $null
    foreach ($line in [System.IO.File]::ReadLines($DynamicsManagerPath)) {
        $m = [regex]::Match($line, "^\s*m_LayerCollisionMatrix:\s*(\S+)\s*$")
        if ($m.Success) { $matrixLine = $m.Groups[1].Value; break }
    }
    if ($null -eq $matrixLine) { return $null }

    $groups = $matrixLine.Split('/')
    if ($PlayerLayer -lt 0 -or $PlayerLayer -ge $groups.Count) { return $null }

    $maskHex = $groups[$PlayerLayer]
    if ([string]::IsNullOrWhiteSpace($maskHex)) { return $null }

    # Parse as a 32-bit mask. The hex is most-significant-nibble-first; bit j
    # (counting from the low end) = collides-with-layer-j.
    $mask = [System.Convert]::ToUInt64($maskHex, 16)
    $result = [System.Collections.Generic.HashSet[int]]::new()
    for ($j = 0; $j -lt 32; $j++) {
        if ((($mask -shr $j) -band 1UL) -eq 1UL) { [void]$result.Add($j) }
    }
    return $result
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

function Test-GameObjectDescendsFromAny {
    param(
        [Parameter(Mandatory = $true)]
        [long]$GameObjectId,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.HashSet[long]]$AncestorGameObjectIds,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$TransformByGameObjectId,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[long, object]]$TransformsById
    )

    $currentGameObjectId = $GameObjectId
    while ($currentGameObjectId -ne 0) {
        if ($AncestorGameObjectIds.Contains($currentGameObjectId)) {
            return $true
        }

        if (-not $TransformByGameObjectId.ContainsKey($currentGameObjectId)) {
            return $false
        }

        $transformInfo = $TransformByGameObjectId[$currentGameObjectId]
        if ($transformInfo.ParentTransformId -eq 0 -or -not $TransformsById.ContainsKey($transformInfo.ParentTransformId)) {
            return $false
        }

        $parentTransform = $TransformsById[$transformInfo.ParentTransformId]
        $currentGameObjectId = $parentTransform.GameObjectId
    }

    return $false
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

function Get-ConvexHull2D {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[System.Numerics.Vector2]]$Points
    )

    if ($Points.Count -lt 3) {
        return $Points
    }

    $sorted = [System.Collections.Generic.List[System.Numerics.Vector2]]::new($Points)
    $sorted.Sort([System.Comparison[System.Numerics.Vector2]]{
        param($a, $b)
        if ($a.X -lt $b.X) { return -1 }
        if ($a.X -gt $b.X) { return 1 }
        if ($a.Y -lt $b.Y) { return -1 }
        if ($a.Y -gt $b.Y) { return 1 }
        return 0
    })

    $cross = {
        param($o, $a, $b)
        return ($a.X - $o.X) * ($b.Y - $o.Y) - ($a.Y - $o.Y) * ($b.X - $o.X)
    }

    $lower = [System.Collections.Generic.List[System.Numerics.Vector2]]::new()
    foreach ($p in $sorted) {
        while ($lower.Count -ge 2 -and ((& $cross $lower[$lower.Count - 2] $lower[$lower.Count - 1] $p) -le 0)) {
            $lower.RemoveAt($lower.Count - 1)
        }
        $lower.Add($p)
    }

    $upper = [System.Collections.Generic.List[System.Numerics.Vector2]]::new()
    for ($i = $sorted.Count - 1; $i -ge 0; $i--) {
        $p = $sorted[$i]
        while ($upper.Count -ge 2 -and ((& $cross $upper[$upper.Count - 2] $upper[$upper.Count - 1] $p) -le 0)) {
            $upper.RemoveAt($upper.Count - 1)
        }
        $upper.Add($p)
    }

    $hull = [System.Collections.Generic.List[System.Numerics.Vector2]]::new()
    for ($i = 0; $i -lt $lower.Count - 1; $i++) { $hull.Add($lower[$i]) }
    for ($i = 0; $i -lt $upper.Count - 1; $i++) { $hull.Add($upper[$i]) }
    return $hull
}

function Get-PolygonArea {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[System.Numerics.Vector2]]$Points
    )

    if ($Points.Count -lt 3) { return 0.0 }
    $sum = 0.0
    for ($i = 0; $i -lt $Points.Count; $i++) {
        $j = ($i + 1) % $Points.Count
        $sum += ($Points[$i].X * $Points[$j].Y) - ($Points[$j].X * $Points[$i].Y)
    }
    return [Math]::Abs($sum) / 2.0
}

function Get-TrianglePlaneIntersections {
    param(
        [Parameter(Mandatory = $true)][System.Numerics.Vector3]$A,
        [Parameter(Mandatory = $true)][System.Numerics.Vector3]$B,
        [Parameter(Mandatory = $true)][System.Numerics.Vector3]$C,
        [Parameter(Mandatory = $true)][double]$PlaneY,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[System.Numerics.Vector2]]$Out
    )

    $verts = @($A, $B, $C)
    $signs = @(
        ($A.Y - $PlaneY),
        ($B.Y - $PlaneY),
        ($C.Y - $PlaneY)
    )

    $hits = 0
    for ($i = 0; $i -lt 3; $i++) {
        $j = ($i + 1) % 3
        $si = $signs[$i]
        $sj = $signs[$j]
        if (($si -ge 0 -and $sj -lt 0) -or ($si -lt 0 -and $sj -ge 0)) {
            $denom = $si - $sj
            if ([Math]::Abs($denom) -lt 1e-9) { continue }
            $t = $si / $denom
            $vi = $verts[$i]
            $vj = $verts[$j]
            $x = $vi.X + ($vj.X - $vi.X) * $t
            $z = $vi.Z + ($vj.Z - $vi.Z) * $t
            $Out.Add([System.Numerics.Vector2]::new([float]$x, [float]$z))
            $hits++
            if ($hits -eq 2) { return }
        }
    }
}

function Get-MeshSegmentsAtPlanes {
    # Slice mesh triangles at horizontal Y planes; emit one segment per
    # triangle/plane crossing. Used both by Get-MeshColliderRecord and by the
    # door open/closed pose slicing. Vertices are passed in world space.
    param(
        [Parameter(Mandatory = $true)][System.Numerics.Vector3[]]$WorldVerts,
        [Parameter(Mandatory = $true)][int[]]$Triangles,
        [Parameter(Mandatory = $true)][double[]]$SlicePlanes
    )

    $minY = [double]::PositiveInfinity
    $maxY = [double]::NegativeInfinity
    foreach ($v in $WorldVerts) {
        if ($v.Y -lt $minY) { $minY = $v.Y }
        if ($v.Y -gt $maxY) { $maxY = $v.Y }
    }

    $segments = New-Object System.Collections.Generic.List[object]
    $planes = @($SlicePlanes.Where({ $_ -ge $minY -and $_ -le $maxY }))
    if ($planes.Count -eq 0) { return ,$segments }

    $triCount = [int]($Triangles.Length / 3)
    foreach ($plane in $planes) {
        for ($t = 0; $t -lt $triCount; $t++) {
            $a = $WorldVerts[$Triangles[$t * 3]]
            $b = $WorldVerts[$Triangles[$t * 3 + 1]]
            $c = $WorldVerts[$Triangles[$t * 3 + 2]]
            $tmp = New-Object System.Collections.Generic.List[System.Numerics.Vector2]
            Get-TrianglePlaneIntersections -A $a -B $b -C $c -PlaneY $plane -Out $tmp
            if ($tmp.Count -eq 2) {
                $segments.Add([ordered]@{
                    AX = [Math]::Round([double]$tmp[0].X, 4)
                    AZ = [Math]::Round([double]$tmp[0].Y, 4)
                    BX = [Math]::Round([double]$tmp[1].X, 4)
                    BZ = [Math]::Round([double]$tmp[1].Y, 4)
                    PlaneY = [Math]::Round([double]$plane, 4)
                })
            }
        }
    }
    return ,$segments
}

function Transform-MeshVertsToWorld {
    param(
        [Parameter(Mandatory = $true)][object]$WorldTransform,
        [Parameter(Mandatory = $true)][System.Numerics.Vector3[]]$LocalVerts
    )
    $out = New-Object System.Numerics.Vector3[] $LocalVerts.Length
    for ($i = 0; $i -lt $LocalVerts.Length; $i++) {
        $out[$i] = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $LocalVerts[$i]
    }
    return ,$out
}

function Get-EulerAnglesYXZ {
    # Approximate Unity's Quaternion.eulerAngles (returns x,y,z in degrees, ZXY
    # intrinsic). For door panels at non-extreme orientations this matches
    # Unity to within a few decimal places, which is sufficient since we feed
    # the angles right back into a quaternion via the same convention.
    param([Parameter(Mandatory = $true)][System.Numerics.Quaternion]$Q)
    $sinX = 2.0 * ($Q.W * $Q.X - $Q.Y * $Q.Z)
    if ($sinX -gt 1.0) { $sinX = 1.0 }
    if ($sinX -lt -1.0) { $sinX = -1.0 }
    $x = [Math]::Asin($sinX)
    $cosX = [Math]::Cos($x)
    if ([Math]::Abs($cosX) -lt 1e-6) {
        $y = [Math]::Atan2(-2.0 * ($Q.X * $Q.Z - $Q.W * $Q.Y), 1.0 - 2.0 * ($Q.Y * $Q.Y + $Q.Z * $Q.Z))
        $z = 0.0
    } else {
        $y = [Math]::Atan2(2.0 * ($Q.W * $Q.Y + $Q.X * $Q.Z), 1.0 - 2.0 * ($Q.X * $Q.X + $Q.Y * $Q.Y))
        $z = [Math]::Atan2(2.0 * ($Q.W * $Q.Z + $Q.X * $Q.Y), 1.0 - 2.0 * ($Q.X * $Q.X + $Q.Z * $Q.Z))
    }
    $rad2deg = 180.0 / [Math]::PI
    return [System.Numerics.Vector3]::new([float]($x * $rad2deg), [float]($y * $rad2deg), [float]($z * $rad2deg))
}

function Get-QuaternionFromEulerYXZ {
    # Build a quaternion from (x,y,z) degrees using Unity's ZXY (Z then X then
    # Y) intrinsic order: q = Ry * Rx * Rz. Inverse of Get-EulerAnglesYXZ.
    param([Parameter(Mandatory = $true)][System.Numerics.Vector3]$DegEuler)
    $deg2rad = [Math]::PI / 180.0
    $hx = [float]($DegEuler.X * $deg2rad * 0.5)
    $hy = [float]($DegEuler.Y * $deg2rad * 0.5)
    $hz = [float]($DegEuler.Z * $deg2rad * 0.5)
    $qx = [System.Numerics.Quaternion]::new([float][Math]::Sin($hx), 0, 0, [float][Math]::Cos($hx))
    $qy = [System.Numerics.Quaternion]::new(0, [float][Math]::Sin($hy), 0, [float][Math]::Cos($hy))
    $qz = [System.Numerics.Quaternion]::new(0, 0, [float][Math]::Sin($hz), [float][Math]::Cos($hz))
    return [System.Numerics.Quaternion]::Normalize($qy * $qx * $qz)
}

function Get-MeshColliderRecord {
    param(
        [Parameter(Mandatory = $true)][object]$Component,
        [Parameter(Mandatory = $true)][object]$GameObject,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$WorldTransform,
        [Parameter(Mandatory = $true)][object]$MeshData,
        [Parameter(Mandatory = $true)][double[]]$SlicePlanes,
        [Parameter(Mandatory = $true)][double]$MinTopY,
        [Parameter(Mandatory = $true)][double]$MaxBottomY
    )

    $worldVerts = New-Object System.Numerics.Vector3[] $MeshData.Vertices.Length
    for ($i = 0; $i -lt $MeshData.Vertices.Length; $i++) {
        $worldVerts[$i] = Transform-LocalPoint -WorldTransform $WorldTransform -LocalPoint $MeshData.Vertices[$i]
    }

    $minY = [double]::PositiveInfinity
    $maxY = [double]::NegativeInfinity
    foreach ($v in $worldVerts) {
        if ($v.Y -lt $minY) { $minY = $v.Y }
        if ($v.Y -gt $maxY) { $maxY = $v.Y }
    }

    $pointBag = [ordered]@{
        Points = New-Object System.Collections.Generic.List[System.Numerics.Vector2]
        TrianglePoints = New-Object System.Collections.Generic.List[System.Numerics.Vector3]
        IntersectedTriangleCount = 0
    }
    # Triangle/slice-plane crossings come in pairs (the edge a triangle traces on a plane).
    # Capture each pair as a segment so wall-shaped meshes can be rasterized as line traces
    # instead of being collapsed to their useless convex-hull footprint.
    $segments = New-Object System.Collections.Generic.List[object]

    $tris = $MeshData.Triangles
    $triCount = [int]($tris.Length / 3)
    $slicePlanes = @($SlicePlanes.Where({ $_ -ge $minY -and $_ -le $maxY }))
    if ($slicePlanes.Count -eq 0 -and $minY -ge $MinTopY -and $minY -le $MaxBottomY) {
        # Whole mesh sits inside the band but neither slice plane clips it (very thin object).
        # Use vertex projection as fallback. No segments emitted in this branch.
        foreach ($v in $worldVerts) {
            if ($v.Y -ge $MinTopY -and $v.Y -le $MaxBottomY) {
                $pointBag.Points.Add([System.Numerics.Vector2]::new([float]$v.X, [float]$v.Z))
            }
        }
    } else {
        foreach ($plane in $slicePlanes) {
            for ($t = 0; $t -lt $triCount; $t++) {
                $a = $worldVerts[$tris[$t * 3]]
                $b = $worldVerts[$tris[$t * 3 + 1]]
                $c = $worldVerts[$tris[$t * 3 + 2]]
                $before = $pointBag.Points.Count
                Get-TrianglePlaneIntersections -A $a -B $b -C $c -PlaneY $plane -Out $pointBag.Points
                $added = $pointBag.Points.Count - $before
                if ($added -gt 0) { $pointBag.IntersectedTriangleCount++ }
                if ($added -eq 2) {
                    $p0 = $pointBag.Points[$before]
                    $p1 = $pointBag.Points[$before + 1]
                    $segments.Add([ordered]@{
                        AX = [Math]::Round([double]$p0.X, 4)
                        AZ = [Math]::Round([double]$p0.Y, 4)
                        BX = [Math]::Round([double]$p1.X, 4)
                        BZ = [Math]::Round([double]$p1.Y, 4)
                        PlaneY = [Math]::Round([double]$plane, 4)
                    })
                }
            }
        }
    }

    if ($pointBag.Points.Count -lt 1) {
        return $null
    }

    $hull = Get-ConvexHull2D -Points $pointBag.Points
    $area = Get-PolygonArea -Points $hull

    $hullArray = New-Object System.Collections.Generic.List[object]
    $minX = [double]::PositiveInfinity
    $maxX = [double]::NegativeInfinity
    $minZ = [double]::PositiveInfinity
    $maxZ = [double]::NegativeInfinity
    $sumX = 0.0
    $sumZ = 0.0
    foreach ($p in $hull) {
        $hullArray.Add((Convert-FlatVectorToObject -X ([double]$p.X) -Z ([double]$p.Y)))
        if ($p.X -lt $minX) { $minX = $p.X }
        if ($p.X -gt $maxX) { $maxX = $p.X }
        if ($p.Y -lt $minZ) { $minZ = $p.Y }
        if ($p.Y -gt $maxZ) { $maxZ = $p.Y }
        $sumX += $p.X
        $sumZ += $p.Y
    }
    $centerX = $sumX / $hull.Count
    $centerZ = $sumZ / $hull.Count
    $worldCenter = [System.Numerics.Vector3]::new([float]$centerX, [float](($minY + $maxY) / 2.0), [float]$centerZ)

    $points3D = New-Object System.Collections.Generic.List[System.Numerics.Vector3]
    $points3D.Add([System.Numerics.Vector3]::new([float]$minX, [float]$minY, [float]$minZ))
    $points3D.Add([System.Numerics.Vector3]::new([float]$maxX, [float]$maxY, [float]$maxZ))

    $componentForRecord = [ordered]@{
        ComponentId = $Component.ComponentId
        Enabled = $Component.Enabled
        IsTrigger = $Component.IsTrigger
        IsDoorConnector = $Component.IsDoorConnector
        IsTeleporterConnector = $Component.IsTeleporterConnector
        HasRigidbody = $Component.HasRigidbody
        RigidbodyIsKinematic = $Component.RigidbodyIsKinematic
        TransformId = $Component.TransformId
        WorldCenter = $worldCenter
        BottomY = $minY
        TopY = $maxY
        LocalShape = [ordered]@{
            MeshName = $MeshData.Name
            MeshGuid = $Component.MeshGuid
            VertexCount = $MeshData.VertexCount
            TriangleCount = $MeshData.TriangleCount
            IsConvex = [bool]$Component.IsConvex
            LocalAabbCenter = if ($null -ne $MeshData.LocalAabbCenter) { Convert-Vector3ToObject $MeshData.LocalAabbCenter } else { $null }
            LocalAabbExtent = if ($null -ne $MeshData.LocalAabbExtent) { Convert-Vector3ToObject $MeshData.LocalAabbExtent } else { $null }
        }
    }

    $footprint = [ordered]@{
        Kind = "ConvexHull2D"
        Center = Convert-Vector3ToObject $worldCenter
        Vertices = $hullArray.ToArray()
        AreaSqM = [Math]::Round($area, 6)
        IntersectedTriangleCount = $pointBag.IntersectedTriangleCount
        SegmentCount = $segments.Count
    }
    if ($segments.Count -gt 0) {
        $footprint.Segments = $segments.ToArray()
    }

    $record = New-ColliderRecord -Component $componentForRecord `
        -ColliderType "MeshCollider" `
        -GameObject $GameObject `
        -Path $Path `
        -WorldTransform $WorldTransform `
        -Points $points3D `
        -Footprint $footprint

    return $record
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
$doorComponents = New-Object System.Collections.Generic.List[object]
$teleporterGameObjectIds = New-Object System.Collections.Generic.HashSet[long]
$rigidbodyByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
$primitiveColliderComponents = New-Object System.Collections.Generic.List[object]
$meshColliderComponents = New-Object System.Collections.Generic.List[object]
# GameObject ids that carry a SkinnedMeshRenderer (section type 137). A
# MeshCollider sharing a GameObject with a SkinnedMeshRenderer is the animated
# dating-character/dialog rig body: the collider mesh is the skinned mesh, which
# the exporter slices in its bind/animated-pose union, producing phantom sprawl
# (Monitor 18x17m, cars 12x27m). These never move in the walked world and their
# physical object is blocked by a separate static-MeshRenderer collider (desk,
# chair, tower). Drop them as blockers. See [[project-navigation-model-update-meshes-2026-05-29]].
$skinnedMeshGameObjectIds = New-Object System.Collections.Generic.HashSet[long]
# MeshFilter records (section type 33). Used as a fallback panel source for
# doors whose visual panel has no collider (e.g. closet SlidingDoors).
$meshFilterComponents = New-Object System.Collections.Generic.List[object]
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
        33 {
            # MeshFilter — captures the visual mesh assigned to a GameObject.
            # Most have a sibling MeshCollider that we already export, but
            # collider-less panels (closet SlidingDoors) are visible only here.
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -ne $gameObjectIdText) {
                $meshGuid = Get-LineValue -Lines $linesArray -Pattern "^  m_Mesh: \{fileID: -?\d+, guid: ([0-9a-f]+), type: \d+\}\s*$"
                $script:meshFilterComponents.Add([pscustomobject]@{
                    ComponentId = $sectionInfo.Id
                    GameObjectId = [long]$gameObjectIdText
                    MeshGuid = $meshGuid
                })
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
            if ($linesArray -match "^  doorOpenType: ") {
                [void]$doorGameObjectIds.Add([long]$gameObjectIdText)
                $doorComponents.Add([pscustomobject]@{
                    Kind = "Door"
                    ComponentId = $sectionInfo.Id
                    GameObjectId = [long]$gameObjectIdText
                    Enabled = ((Get-LineValue -Lines $linesArray -Pattern "^  m_Enabled: (\d+)$") -ne "0")
                    Open = ((Get-LineValue -Lines $linesArray -Pattern "^  open: (\d+)$") -eq "1")
                    Locked = ((Get-LineValue -Lines $linesArray -Pattern "^  locked: (\d+)$") -eq "1")
                    OverrideNodeLock = ((Get-LineValue -Lines $linesArray -Pattern "^  overrideNodeLock: (\d+)$") -eq "1")
                    NodeLockAlt = $(if ($nla = Get-LineValue -Lines $linesArray -Pattern "^  NodeLockAlt: (.*)$") { $nla } else { "" })
                    Range = [double](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  range: ([-0-9.eE+]+)$" -DefaultValue "85")
                    DoorOpenType = [int](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  doorOpenType: (\d+)$" -DefaultValue "0")
                    BlockInteraction = ((Get-LineValue -Lines $linesArray -Pattern "^  blockInteraction: (\d+)$") -eq "1")
                    StopOnCollision = ((Get-LineValue -Lines $linesArray -Pattern "^  stopOnCollision: (\d+)$") -eq "1")
                    # SlidingDoor-only fields (left null for hinge doors)
                    Dist = $null
                    Sign = $null
                    Axis = $null
                })
            }
            # SlidingDoor MonoBehaviour: closet doors etc. Discriminated by
            # serialized fields `dist`, `sign`, `axis` rather than by GUID so the
            # rule survives an asset re-rip.
            elseif ($linesArray -match "^  dist: " -and $linesArray -match "^  axis: " -and $linesArray -match "^  sign: ") {
                [void]$doorGameObjectIds.Add([long]$gameObjectIdText)
                $doorComponents.Add([pscustomobject]@{
                    Kind = "SlidingDoor"
                    ComponentId = $sectionInfo.Id
                    GameObjectId = [long]$gameObjectIdText
                    Enabled = ((Get-LineValue -Lines $linesArray -Pattern "^  m_Enabled: (\d+)$") -ne "0")
                    Open = ((Get-LineValue -Lines $linesArray -Pattern "^  open: (\d+)$") -eq "1")
                    Locked = ((Get-LineValue -Lines $linesArray -Pattern "^  locked: (\d+)$") -eq "1")
                    OverrideNodeLock = ((Get-LineValue -Lines $linesArray -Pattern "^  overrideNodeLock: (\d+)$") -eq "1")
                    NodeLockAlt = $(if ($nla2 = Get-LineValue -Lines $linesArray -Pattern "^  nodeLockAlt: (.*)$") { $nla2 } else { "" })
                    Range = $null
                    DoorOpenType = $null
                    BlockInteraction = $false
                    StopOnCollision = ((Get-LineValue -Lines $linesArray -Pattern "^  stopOnCollision: (\d+)$") -eq "1")
                    Dist = [double](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  dist: ([-0-9.eE+]+)$" -DefaultValue "0")
                    Sign = [int](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  sign: (-?\d+)$" -DefaultValue "1")
                    Axis = [int](Get-LineValueOrDefault -Lines $linesArray -Pattern "^  axis: (\d+)$" -DefaultValue "0")
                })
            }
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
        137 {
            # SkinnedMeshRenderer — marks the animated dialog/character rig body.
            $gameObjectIdText = Get-LineValue -Lines $linesArray -Pattern "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -ne $gameObjectIdText) {
                [void]$skinnedMeshGameObjectIds.Add([long]$gameObjectIdText)
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

# Authoritative blocker layer filter. A collider blocks the player only if the
# Unity collision matrix lets the player layer (FPEPlayer) collide with the
# collider's layer. Derive that set from DynamicsManager.asset; fall back to the
# legacy skip list if the asset is unavailable. The predicate
# Test-LayerCollidesWithPlayer returns $true when the layer should be ADMITTED.
$playerCollidableLayers = Get-PlayerCollidableLayers -DynamicsManagerPath $DynamicsManagerPath -PlayerLayer $PlayerLayer
$skipLayerSet = [System.Collections.Generic.HashSet[int]]::new()
foreach ($lyr in $SkipMeshLayers) { [void]$skipLayerSet.Add([int]$lyr) }
if ($null -ne $playerCollidableLayers) {
    Write-Host ("Player layer {0} collides with {1} layers (from collision matrix): {2}" -f `
        $PlayerLayer, $playerCollidableLayers.Count, (($playerCollidableLayers | Sort-Object) -join ","))
} else {
    Write-Host ("DynamicsManager.asset unavailable; falling back to SkipMeshLayers: {0}" -f ($SkipMeshLayers -join ","))
}

function Test-LayerCollidesWithPlayer {
    param([Parameter(Mandatory = $true)] [int]$Layer)
    if ($null -ne $script:playerCollidableLayers) {
        return $script:playerCollidableLayers.Contains($Layer)
    }
    return -not $script:skipLayerSet.Contains($Layer)
}

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
    $component | Add-Member -NotePropertyName IsDoorConnector -NotePropertyValue (Test-GameObjectDescendsFromAny -GameObjectId $gameObject.Id -AncestorGameObjectIds $doorGameObjectIds -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById) -Force
    $component | Add-Member -NotePropertyName IsTeleporterConnector -NotePropertyValue (Test-GameObjectDescendsFromAny -GameObjectId $gameObject.Id -AncestorGameObjectIds $teleporterGameObjectIds -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById) -Force
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
    elseif (-not (Test-LayerCollidesWithPlayer -Layer $gameObject.Layer)) { $reason = ("LayerNotPlayerCollidable:{0}" -f $gameObject.Layer) }
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
        $component | Add-Member -NotePropertyName IsDoorConnector -NotePropertyValue (Test-GameObjectDescendsFromAny -GameObjectId $gameObject.Id -AncestorGameObjectIds $doorGameObjectIds -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById) -Force
        $component | Add-Member -NotePropertyName IsTeleporterConnector -NotePropertyValue (Test-GameObjectDescendsFromAny -GameObjectId $gameObject.Id -AncestorGameObjectIds $teleporterGameObjectIds -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById) -Force
        $component | Add-Member -NotePropertyName HasRigidbody -NotePropertyValue ($rigidbodyByGameObjectId.ContainsKey($gameObject.Id)) -Force
        $component | Add-Member -NotePropertyName RigidbodyIsKinematic -NotePropertyValue $rigidbodyIsKinematic -Force

        if (-not $component.Enabled) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "DisabledCollider"; continue }
        if ($component.IsTrigger) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "TriggerCollider"; continue }
        if (-not $gameObject.IsActive) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "InactiveGameObject"; continue }
        if ($component.IsDoorConnector) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "DoorConnector"; continue }
        if ($component.IsTeleporterConnector) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "TeleporterConnector"; continue }
        if ($component.HasRigidbody) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "RigidbodyObject"; continue }
        if (-not (Test-LayerCollidesWithPlayer -Layer $gameObject.Layer)) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason ("LayerNotPlayerCollidable:{0}" -f $gameObject.Layer); continue }
        # Drop the animated dialog/character rig body: a MeshCollider sharing a
        # GameObject with a SkinnedMeshRenderer is the skinned mesh sliced across
        # its bind/animated-pose union, which sprawls (Monitor 18x17m, cars
        # 12x27m). The physical object is blocked by a separate static-
        # MeshRenderer collider (desk, chair, tower). 17/17 sprawl cases this
        # scene. See [[project-navigation-model-update-meshes-2026-05-29]].
        if ($skinnedMeshGameObjectIds.Contains($gameObject.Id)) { Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "SkinnedMeshRigCollider"; continue }

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
        $path = Get-GameObjectPath -GameObjectId $gameObject.Id -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById
        $record = Get-MeshColliderRecord -Component $component -GameObject $gameObject -Path $path `
            -WorldTransform $worldTransform -MeshData $meshData `
            -SlicePlanes $MeshSlicePlanes `
            -MinTopY $MinimumBlockingTopY -MaxBottomY $MaximumBlockingBottomY

        if ($null -eq $record) {
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason "EmptyFootprint"
            continue
        }

        $meshColliderRecords.Add($record)

        # Mesh colliders are rasterized by the bake from their collision-SLICE
        # SEGMENTS only (never their AABB), so the old thin-frame / wall-FAT /
        # footprint-area gates — which existed solely to decide AABB suppression
        # vs rasterization and to tag IsWallLikeFatVictim — are retired. Every
        # mesh record that resolved a footprint flows through; the bake's
        # _segments_in_floor_band + _is_vertical_wall handle wall geometry, and
        # the dilation pass handles clearance. We keep two cheap sanity gates so
        # out-of-band / degenerate records don't pollute NavigationBlockers (the
        # primitive bounds pass and the datable-dimension join read it).
        # See [[project-navigation-iswalllikefatvictim-followup]],
        # [[project-navigation-collision-only-blockers-2026-05-29]].
        $reason = $null
        if ($record.TopY -lt $MinimumBlockingTopY) { $reason = "BelowBlockingHeight" }
        elseif ($record.BottomY -gt $MaximumBlockingBottomY) { $reason = "AbovePlayerBand" }
        elseif ($null -eq $record.Bounds2D) { $reason = "MissingFootprintBounds" }

        if ($null -ne $reason) {
            $record.Footprint.RejectionReason = $reason
            Add-ReasonCount -Counter $meshColliderIgnoredReasons -Reason $reason
            continue
        }

        $navigationBlockers.Add($record)
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$ignoredPrimitiveColliderCounts = @(Convert-ReasonCounterToArray -Counter $ignoredReasons)
$ignoredMeshColliderCounts = @(Convert-ReasonCounterToArray -Counter $meshColliderIgnoredReasons)
$meshLayerHistogramArray = @($meshLayerHistogram.Keys | Sort-Object | ForEach-Object { [ordered]@{ Layer = [int]$_; Count = [int]$meshLayerHistogram[$_] } })
$primitiveColliderArray = $primitiveColliders.ToArray()
$meshColliderArray = $meshColliderRecords.ToArray()
$navigationBlockerArray = $navigationBlockers.ToArray()

# Layer inventory: every non-default layer (>=8) gets its GameObject paths
# enumerated so we can identify what layer 31 (and friends) actually contain.
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

# Door panel discovery + per-pose slicing.
#
# For each Door / SlidingDoor MonoBehaviour, walk its transform's descendants
# (including itself) and collect every panel-bearing GameObject: any that has a
# MeshCollider or, failing that, a MeshFilter. The panel's mesh vertices are
# resolved via the same GUID index used by the mesh-collider pass. We slice the
# panel mesh in two world poses:
#
#   closed: each panel's current scene world transform (== closed pose, since
#           we already verified Open=false for every door in the scene).
#   open:   re-anchor each panel by computing its position/rotation in the
#           door's local frame, then rebuilding world from the open-pose door
#           transform:
#             v_doorLocal = inv(doorWorldRot) * (v_world - doorWorldPos)
#             v_worldOpen = doorWorldOpen.Pos + doorWorldOpen.Rot * v_doorLocal
#
# Hinge doors (Door.cs): transform.eulerAngles = startRot + correctUP * to
#   where correctUP = (0,1,0) and to = startRot.z + (±range or 0). We compute
#   open-pose eulers via Get-EulerAnglesYXZ/Get-QuaternionFromEulerYXZ. For
#   BothWays we emit both signs as separate SegmentsOpen sets.
#
# SlidingDoors (SlidingDoor.cs Initialize() with open=false at scene load):
#   openPos = closedPos + worldAxis * sign * dist
#   where worldAxis is the SCENE-FRAME axis (right/up/forward), per the
#   decompiled source.
#
# Sliced segments use the existing $MeshSlicePlanes — same slicing convention
# as the regular MeshCollider pass so the bake can rasterize and diff with the
# same code path.

# Build child index: for each transform, list its child transform ids.
$childrenByParentTransformId = [System.Collections.Generic.Dictionary[long, System.Collections.Generic.List[long]]]::new()
foreach ($transformInfo in $transformsById.Values) {
    $parentId = $transformInfo.ParentTransformId
    if ($parentId -eq 0) { continue }
    if (-not $childrenByParentTransformId.ContainsKey($parentId)) {
        $childrenByParentTransformId[$parentId] = New-Object System.Collections.Generic.List[long]
    }
    [void]$childrenByParentTransformId[$parentId].Add($transformInfo.Id)
}

function Get-DescendantTransformIds {
    param(
        [Parameter(Mandatory = $true)][long]$RootTransformId,
        [Parameter(Mandatory = $true)][System.Collections.Generic.Dictionary[long, System.Collections.Generic.List[long]]]$ChildIndex
    )
    $result = New-Object System.Collections.Generic.List[long]
    $stack = New-Object System.Collections.Generic.Stack[long]
    $stack.Push($RootTransformId)
    while ($stack.Count -gt 0) {
        $tid = $stack.Pop()
        $result.Add($tid)
        if ($ChildIndex.ContainsKey($tid)) {
            foreach ($child in $ChildIndex[$tid]) {
                $stack.Push($child)
            }
        }
    }
    return ,$result
}

# Index colliders and mesh-filters by GameObjectId so panel discovery can pick
# the best mesh source per descendant (MeshCollider preferred; MeshFilter as
# fallback for collider-less panels).
$meshColliderByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
foreach ($mc in $meshColliderComponents) {
    if (-not $meshColliderByGameObjectId.ContainsKey($mc.GameObjectId)) {
        $meshColliderByGameObjectId[$mc.GameObjectId] = $mc
    }
}
$meshFilterByGameObjectId = [System.Collections.Generic.Dictionary[long, object]]::new()
foreach ($mf in $meshFilterComponents) {
    if (-not $meshFilterByGameObjectId.ContainsKey($mf.GameObjectId)) {
        $meshFilterByGameObjectId[$mf.GameObjectId] = $mf
    }
}

function Compute-DoorOpenWorldTransform {
    # Returns @($worldTransformOpen, ...) — one entry per open pose. BothWays
    # hinge doors return two entries (positive and negative range); everything
    # else returns one.
    param(
        [Parameter(Mandatory = $true)][object]$DoorComponent,
        [Parameter(Mandatory = $true)][object]$DoorWorldClosed
    )

    $results = New-Object System.Collections.Generic.List[object]
    if ($DoorComponent.Kind -eq "Door") {
        $startEuler = Get-EulerAnglesYXZ -Q $DoorWorldClosed.Rotation
        $range = [double]$DoorComponent.Range
        $signs = switch ($DoorComponent.DoorOpenType) {
            0 { @(1.0, -1.0) }   # BothWays — emit both
            1 { @(1.0) }         # OnlyPositive
            2 { @(-1.0) }        # OnlyNegative
            default { @(1.0) }
        }
        foreach ($s in $signs) {
            $signed = $range * $s
            # transform.eulerAngles = startRot + correctUP * (startRot.z + signed)
            $toY = [double]$startEuler.Z + $signed
            $openEuler = [System.Numerics.Vector3]::new(
                [float]$startEuler.X,
                [float]([double]$startEuler.Y + $toY),
                [float]$startEuler.Z
            )
            $openRot = Get-QuaternionFromEulerYXZ -DegEuler $openEuler
            $results.Add([ordered]@{
                Position = $DoorWorldClosed.Position
                Rotation = $openRot
                Scale = $DoorWorldClosed.Scale
                Tag = $(if ($s -gt 0) { "Positive" } else { "Negative" })
            })
        }
    } else {
        # SlidingDoor — world-axis translation. Per decompiled Initialize() with
        # open=false: openPos = closedPos + axisVec * sign * dist (world axis).
        $axisVec = switch ($DoorComponent.Axis) {
            0 { [System.Numerics.Vector3]::new(1, 0, 0) }
            1 { [System.Numerics.Vector3]::new(0, 1, 0) }
            2 { [System.Numerics.Vector3]::new(0, 0, 1) }
            default { [System.Numerics.Vector3]::new(0, 0, 1) }
        }
        $delta = $axisVec * [float]([double]$DoorComponent.Sign * [double]$DoorComponent.Dist)
        $results.Add([ordered]@{
            Position = $DoorWorldClosed.Position + $delta
            Rotation = $DoorWorldClosed.Rotation
            Scale = $DoorWorldClosed.Scale
            Tag = "Slide"
        })
    }
    return ,$results
}

function Slice-PanelInDoorPose {
    # Re-anchor panel verts from world (closed pose) into door-local space,
    # then rebuild into world space using the open-pose door transform, then
    # slice at the given Y planes. The door-local transform is the natural
    # frame for panels because both hinge rotation and slider translation
    # operate on the door's transform — children inherit automatically.
    param(
        [Parameter(Mandatory = $true)][System.Numerics.Vector3[]]$PanelWorldVertsClosed,
        [Parameter(Mandatory = $true)][object]$DoorWorldClosed,
        [Parameter(Mandatory = $true)][object]$DoorWorldOpen,
        [Parameter(Mandatory = $true)][int[]]$Triangles,
        [Parameter(Mandatory = $true)][double[]]$SlicePlanes
    )

    $invDoorRot = [System.Numerics.Quaternion]::Conjugate([System.Numerics.Quaternion]::Normalize($DoorWorldClosed.Rotation))
    $doorPos = $DoorWorldClosed.Position
    $openRot = [System.Numerics.Quaternion]::Normalize($DoorWorldOpen.Rotation)
    $openPos = $DoorWorldOpen.Position

    $n = $PanelWorldVertsClosed.Length
    $worldOpen = New-Object System.Numerics.Vector3[] $n
    for ($i = 0; $i -lt $n; $i++) {
        $rel = $PanelWorldVertsClosed[$i] - $doorPos
        $localToDoor = [System.Numerics.Vector3]::Transform($rel, $invDoorRot)
        $worldOpen[$i] = $openPos + [System.Numerics.Vector3]::Transform($localToDoor, $openRot)
    }

    return ,(Get-MeshSegmentsAtPlanes -WorldVerts $worldOpen -Triangles $Triangles -SlicePlanes $SlicePlanes)
}

function Get-DoorPanels {
    param(
        [Parameter(Mandatory = $true)][long]$DoorTransformId,
        [Parameter(Mandatory = $true)][object]$DoorComponent,
        [Parameter(Mandatory = $true)][object]$DoorWorldClosed
    )

    $panels = New-Object System.Collections.Generic.List[object]
    $descendantTransformIds = Get-DescendantTransformIds -RootTransformId $DoorTransformId -ChildIndex $childrenByParentTransformId

    $openPoses = Compute-DoorOpenWorldTransform -DoorComponent $DoorComponent -DoorWorldClosed $DoorWorldClosed

    foreach ($tid in $descendantTransformIds) {
        if (-not $transformsById.ContainsKey($tid)) { continue }
        $goId = $transformsById[$tid].GameObjectId
        if (-not $gameObjects.ContainsKey($goId)) { continue }

        $meshSourceKind = $null
        $meshGuid = $null
        $meshSourceComponentId = $null
        if ($meshColliderByGameObjectId.ContainsKey($goId)) {
            $mc = $meshColliderByGameObjectId[$goId]
            if ($null -ne $mc.MeshGuid) {
                $meshSourceKind = "MeshCollider"
                $meshGuid = $mc.MeshGuid
                $meshSourceComponentId = $mc.ComponentId
            }
        }
        if ($null -eq $meshGuid -and $meshFilterByGameObjectId.ContainsKey($goId)) {
            $mf = $meshFilterByGameObjectId[$goId]
            if ($null -ne $mf.MeshGuid) {
                $meshSourceKind = "MeshFilter"
                $meshGuid = $mf.MeshGuid
                $meshSourceComponentId = $mf.ComponentId
            }
        }
        if ($null -eq $meshGuid) { continue }
        if (-not $script:guidIndex.ContainsKey($meshGuid)) { continue }
        if (-not $worldTransforms.ContainsKey($tid)) { continue }

        $assetPath = $script:guidIndex[$meshGuid]
        $meshData = $null
        if ($script:meshCache.ContainsKey($assetPath)) {
            $meshData = $script:meshCache[$assetPath]
        } else {
            try {
                $meshData = Read-UnityMeshAsset -Path $assetPath
                $script:meshCache[$assetPath] = $meshData
            } catch {
                $script:meshCache[$assetPath] = $null
            }
        }
        if ($null -eq $meshData) { continue }

        $panelWorld = $worldTransforms[$tid]
        $worldVertsClosed = Transform-MeshVertsToWorld -WorldTransform $panelWorld -LocalVerts $meshData.Vertices

        $segmentsClosed = Get-MeshSegmentsAtPlanes -WorldVerts $worldVertsClosed -Triangles $meshData.Triangles -SlicePlanes $MeshSlicePlanes

        $openSliceSets = New-Object System.Collections.Generic.List[object]
        foreach ($openPose in $openPoses) {
            try {
                $segs = Slice-PanelInDoorPose `
                    -PanelWorldVertsClosed $worldVertsClosed `
                    -DoorWorldClosed $DoorWorldClosed `
                    -DoorWorldOpen $openPose `
                    -Triangles $meshData.Triangles `
                    -SlicePlanes $script:MeshSlicePlanes
            } catch {
                Write-Host ("Slice-PanelInDoorPose threw: {0}" -f $_.Exception.Message)
                throw
            }
            if ($null -eq $segs) { $segs = New-Object System.Collections.Generic.List[object] }
            $segsArr = $segs.ToArray()
            $entry = [pscustomobject]@{
                Tag = [string]$openPose["Tag"]
                SegmentCount = $segsArr.Length
                Segments = $segsArr
            }
            [void]$openSliceSets.Add($entry)
        }

        $panelPath = Get-GameObjectPath -GameObjectId $goId -GameObjects $gameObjects -TransformByGameObjectId $transformByGameObjectId -TransformsById $transformsById
        $panelEntry = [pscustomobject]@{
            GameObjectId = $goId
            TransformId = $tid
            Name = $gameObjects[$goId].Name
            Path = $panelPath
            MeshSourceKind = $meshSourceKind
            MeshSourceComponentId = $meshSourceComponentId
            MeshName = $meshData.Name
            MeshGuid = $meshGuid
            VertexCount = $meshData.VertexCount
            TriangleCount = [int]($meshData.Triangles.Length / 3)
            ClosedSegmentCount = $segmentsClosed.Count
            SegmentsClosed = $segmentsClosed.ToArray()
            OpenSegmentSets = $openSliceSets.ToArray()
        }
        [void]$panels.Add($panelEntry)
    }
    return ,$panels
}

# Door records: combine MonoBehaviour Door fields with GameObject name + world transform.
# Consumers rotate the door's mesh by (range, doorOpenType, correctUP=Y) around the
# door's world position to compute open-pose blocker cells. See Door.OpenDoor() in
# decompiled source for the exact rotation rule.
$doorRecordList = New-Object System.Collections.Generic.List[object]
foreach ($doorComponent in $doorComponents) {
    $gameObjectId = $doorComponent.GameObjectId
    if (-not $gameObjects.ContainsKey($gameObjectId)) { continue }
    if (-not $transformByGameObjectId.ContainsKey($gameObjectId)) { continue }
    $gameObject = $gameObjects[$gameObjectId]
    $transformInfo = $transformByGameObjectId[$gameObjectId]
    if (-not $worldTransforms.ContainsKey($transformInfo.Id)) { continue }
    $worldTransform = $worldTransforms[$transformInfo.Id]
    $path = Get-GameObjectPath -GameObjectId $gameObjectId -GameObjects $gameObjects -TransformsById $transformsById -TransformByGameObjectId $transformByGameObjectId
    $record = [ordered]@{
        Kind = $doorComponent.Kind
        Name = $gameObject.Name
        Path = $path
        GameObjectId = $gameObjectId
        ComponentId = $doorComponent.ComponentId
        TransformId = $transformInfo.Id
        Enabled = $doorComponent.Enabled
        IsActive = $gameObject.IsActive
        Open = $doorComponent.Open
        Locked = $doorComponent.Locked
        OverrideNodeLock = $doorComponent.OverrideNodeLock
        NodeLockAlt = $doorComponent.NodeLockAlt
        BlockInteraction = $doorComponent.BlockInteraction
        StopOnCollision = $doorComponent.StopOnCollision
        WorldPosition = Convert-Vector3ToObject $worldTransform.Position
        WorldRotation = Convert-QuaternionToObject $worldTransform.Rotation
    }
    if ($doorComponent.Kind -eq "Door") {
        $record["Range"] = [Math]::Round($doorComponent.Range, 6)
        $record["DoorOpenType"] = $doorComponent.DoorOpenType
        $record["DoorOpenTypeName"] = @("BothWays", "OnlyPositive", "OnlyNegative")[$doorComponent.DoorOpenType]
    } else {
        # SlidingDoor: translation along world axis. closedPos = transform.position
        # at scene load (Initialize() runs at Start), openPos = closedPos + axisVec * sign * dist.
        $record["Dist"] = [Math]::Round($doorComponent.Dist, 6)
        $record["Sign"] = $doorComponent.Sign
        $record["Axis"] = $doorComponent.Axis
        $record["AxisName"] = @("X", "Y", "Z")[$doorComponent.Axis]
    }

    # Panel discovery + per-pose slicing. Skipped if the mesh index is
    # unavailable (no MeshAssetRoot). Each panel emits closed-pose segments
    # and one open-pose segment set per open direction (two for BothWays).
    if ($meshSupported) {
        $panels = Get-DoorPanels -DoorTransformId $transformInfo.Id -DoorComponent $doorComponent -DoorWorldClosed $worldTransform
        if ($null -eq $panels) { $panels = New-Object System.Collections.Generic.List[object] }
        $panelsArr = $panels.ToArray()
        $record["Panels"] = $panelsArr
        $record["PanelCount"] = $panelsArr.Length
    } else {
        $record["Panels"] = @()
        $record["PanelCount"] = 0
    }

    $doorRecordList.Add($record)
}
$doorRecordArray = $doorRecordList.ToArray()

# State-gated walls. Currently one known mechanism:
#   DresserWall (decompiled/DresserWall.cs): collider disabled when Ink variable
#   leave_house_hint == true. Default scene-load state: hint=false ⇒ collider
#   active ⇒ bedroom sealed. Detected by parent-path /MultiRoom/Walls/DaemonWall/
#   AND name ending in "_Daemon" (sibling _no-suffix wall is architectural and
#   stays permanent).
#
# Future state-gated mechanisms (MovingDateable Locked/Unlocked variants, etc.)
# can be appended to this list with their own detector + release condition.
$stateWallList = New-Object System.Collections.Generic.List[object]
$dresserWallPathFragment = "/MultiRoom/Walls/DaemonWall/"
foreach ($record in $navigationBlockerArray) {
    $path = [string]$record.Path
    $name = [string]$record.Name
    if ($path.Contains($dresserWallPathFragment) -and $name.EndsWith("_Daemon")) {
        $stateWallList.Add([ordered]@{
            Name = $name
            ComponentId = $record.ComponentId
            GameObjectId = $record.GameObjectId
            TransformId = $record.TransformId
            Path = $path
            ColliderType = $record.ColliderType
            Bounds3D = $record.Bounds3D
            Bounds2D = $record.Bounds2D
            BottomY = $record.BottomY
            TopY = $record.TopY
            # Release condition. The C# component class that flips the collider
            # off; bake/consumer logic uses this to decide whether the wall is
            # currently active (and therefore whether the bedroom is sealed).
            ReleaseMechanism = "DresserWall"
            ReleaseCondition = "InkVariable:leave_house_hint==true"
            DefaultActive = $true
        })
    }
}
$stateWallArray = $stateWallList.ToArray()

$result = [ordered]@{
    ScenePath = $ScenePath
    MeshAssetRoot = $MeshAssetRoot
    GeneratedAt = (Get-Date).ToString("o")
    Filtering = [ordered]@{
        MinimumBlockingTopY = [Math]::Round($MinimumBlockingTopY, 4)
        MaximumBlockingBottomY = [Math]::Round($MaximumBlockingBottomY, 4)
        MinimumFootprintRadius = [Math]::Round($MinimumFootprintRadius, 4)
        DoorAndTeleporterCollidersExcluded = $true
        RigidbodyObjectsExcluded = $true
        MeshCollidersUnsupported = (-not $meshSupported)
        MeshSlicePlanes = @($MeshSlicePlanes | ForEach-Object { [Math]::Round([double]$_, 4) })
        PlayerLayer = $PlayerLayer
        PlayerCollidableLayers = $(if ($null -ne $playerCollidableLayers) { @($playerCollidableLayers | Sort-Object) } else { $null })
        SkipMeshLayers = @($SkipMeshLayers | ForEach-Object { [int]$_ })
        TerrainCollidersUnsupported = $true
    }
    Counts = [ordered]@{
        GameObjects = $gameObjects.Count
        PrimitiveColliders = $primitiveColliders.Count
        NavigationBlockers = $navigationBlockers.Count
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
    MeshColliderLayerHistogram = $meshLayerHistogramArray
    LayerInventory = $layerInventoryArray
    IgnoredPrimitiveColliderCounts = $ignoredPrimitiveColliderCounts
    IgnoredMeshColliderCounts = $ignoredMeshColliderCounts
    MeshParseFailures = $meshParseFailures.ToArray()
    PrimitiveColliders = $primitiveColliderArray
    MeshColliders = $meshColliderArray
    NavigationBlockers = $navigationBlockerArray
    Doors = $doorRecordArray
    StateWalls = $stateWallArray
}

$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote blocker collider data to $OutputPath"
Write-Host ("MeshColliders parsed: {0}/{1}  Records: {2}  Total navigation blockers: {3}" -f $meshColliderResolvedCount, $meshColliderComponents.Count, $meshColliderRecords.Count, $navigationBlockers.Count)
