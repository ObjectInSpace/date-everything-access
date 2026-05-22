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
    # to its 2D footprint. Defaults cover the player capsule band on both floors:
    # ground at ~Y=-0.5 -> slice at 0.1, 1.0; upper at ~Y=12.5 -> slice at 13.1, 14.0.
    # See [[project-navigation-step-o2-outcome]] for the floor-band rationale.
    [Parameter()]
    [double[]]$MeshSlicePlanes = @(0.1, 1.0, 13.1, 14.0),

    [Parameter()]
    [double]$MaxMeshFootprintAreaSqM = 25.0,

    # Bypass MaxMeshFootprintAreaSqM when a mesh has a wall-like signature:
    # tall (vertical extent >= WallMinVerticalExtent), grounded
    # (BottomY <= WallMaxBottomY), capped (TopY <= WallMaxTopY to exclude
    # trees), AND thin in at least one horizontal direction
    # (min(Width, Depth) <= WallMaxThinDimension). The thinness check is the
    # critical one: combined-room wall meshes like SM_Walls_Hall1 satisfy the
    # tall/grounded criteria but their AABBs span an entire room interior, so
    # admitting them blocks all routes inside the room. Real wall meshes
    # (fences, single-wall segments) are thin in one direction.
    [Parameter()]
    [double]$WallMinVerticalExtent = 3.0,

    [Parameter()]
    [double]$WallMaxBottomY = 0.6,

    [Parameter()]
    [double]$WallMaxTopY = 25.0,

    [Parameter()]
    [double]$WallMaxThinDimension = 1.5,

    # Thin-frame admission: catches doorframe/closet-door/perimeter-fence-shaped
    # meshes whose AABBs span a doorway opening and would seal it. These pass
    # under MaxMeshFootprintAreaSqM (so the wall-FAT path never sees them) but
    # their AABBs still bridge across the opening. Signature: tall (vertical
    # extent >= ThinFrameMinVerticalExtent), thin in one direction
    # (min <= ThinFrameMaxThinDimension), spans the opening in the other
    # (max >= ThinFrameMinSpanDimension), grounded enough to sit on a floor band
    # (BottomY <= ThinFrameMaxBottomY), and has segments. When matched, the
    # AABB is suppressed and the segments are rasterized by the bake. See
    # [[project-navigation-bake-doorframe-gap]].
    [Parameter()]
    [double]$ThinFrameMinVerticalExtent = 5.0,

    [Parameter()]
    [double]$ThinFrameMaxThinDimension = 2.0,

    [Parameter()]
    [double]$ThinFrameMinSpanDimension = 2.5,

    [Parameter()]
    [double]$ThinFrameMaxBottomY = 14.0,

    [Parameter()]
    [int[]]$SkipMeshLayers = @(18, 31)
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

        $reason = $null
        $isWallLikeFatVictim = $false

        # Thin-frame check (evaluated before the area cap): doorframes / closet
        # doors / perimeter fences fall under the area cap but their AABBs
        # still seal doorway openings. When the signature matches, suppress
        # the AABB and flag the record for segment-trace rasterization. See
        # [[project-navigation-bake-doorframe-gap]].
        if ($null -ne $record.Bounds2D -and [int]$record.Footprint.SegmentCount -gt 0) {
            $verticalExtent = [double]$record.TopY - [double]$record.BottomY
            $thinDim = [Math]::Min([double]$record.Bounds2D.Width, [double]$record.Bounds2D.Depth)
            $spanDim = [Math]::Max([double]$record.Bounds2D.Width, [double]$record.Bounds2D.Depth)
            $isThinFrame = ($verticalExtent -ge $ThinFrameMinVerticalExtent) -and `
                ($thinDim -le $ThinFrameMaxThinDimension) -and `
                ($spanDim -ge $ThinFrameMinSpanDimension) -and `
                ([double]$record.BottomY -le $ThinFrameMaxBottomY)
            if ($isThinFrame) {
                $reason = "ThinFrameAABBSuppressed"
                $isWallLikeFatVictim = $true
            }
        }

        if ($null -ne $reason) {
            # Fall through to the shared victim emission path below.
        }
        elseif ($record.TopY -lt $MinimumBlockingTopY) { $reason = "BelowBlockingHeight" }
        elseif ($record.BottomY -gt $MaximumBlockingBottomY) { $reason = "AbovePlayerBand" }
        elseif ($null -eq $record.Bounds2D) { $reason = "MissingFootprintBounds" }
        elseif ([double]$record.Footprint.AreaSqM -gt $MaxMeshFootprintAreaSqM) {
            # Wall-like bypass: tall, grounded, capped (rejects trees/skyboxes),
            # AND thin in one horizontal direction. Thinness is the critical
            # check: combined-room wall meshes have huge XZ AABBs that span the
            # whole room interior; admitting them blocks routes inside the room.
            $verticalExtent = [double]$record.TopY - [double]$record.BottomY
            $thinDim = [Math]::Min([double]$record.Bounds2D.Width, [double]$record.Bounds2D.Depth)
            $isGroundedTallCapped = ($verticalExtent -ge $WallMinVerticalExtent) -and `
                ([double]$record.BottomY -le $WallMaxBottomY) -and `
                ([double]$record.TopY -le $WallMaxTopY)
            $isWallLike = $isGroundedTallCapped -and ($thinDim -le $WallMaxThinDimension)
            if (-not $isWallLike) {
                $reason = "FootprintAreaExceedsMax"
                # Wall-like-FAT victim: combined-room wall meshes whose slice
                # traces should rasterize into the bake bitmap. Three gates:
                # (1) TopY clusters at a known ceiling/roof band — ~12.5m
                #     (ground walls) or 25-33m (upper / full-height / roof).
                #     Filters out furniture/dateable colliders (Monitor 18.0,
                #     Body 6.0, Sofa 3.1, Bed 17.1, HVAC 20.1).
                # (2) Vertical extent <= 22m — filters trees (whose foliage
                #     extends well above the ceiling, vertical extent 26m+).
                #     Largest legitimate wall is SM_Walls_Attic at 19.4m.
                # (3) Must have produced at least one slice-plane segment.
                # The clusters are data-driven for this scene; if the mod is
                # re-targeted at another Unity scene the bands need to be
                # re-derived from that scene's ceiling Y values. See
                # [[project-navigation-bake-blocker-scope-2026-05-21]].
                $topY = [double]$record.TopY
                $inGroundCeilingBand = ($topY -ge 12.0 -and $topY -le 13.5)
                $inUpperCeilingBand  = ($topY -ge 25.0 -and $topY -le 33.0)
                $verticalReasonable  = ($verticalExtent -le 22.0)
                $verticalOk          = ($verticalExtent -ge $WallMinVerticalExtent)
                if ($verticalOk -and $verticalReasonable -and `
                    ($inGroundCeilingBand -or $inUpperCeilingBand) -and `
                    [int]$record.Footprint.SegmentCount -gt 0) {
                    $isWallLikeFatVictim = $true
                }
            }
            else {
                $flatRadius = [Math]::Max([double]$record.Bounds2D.Width, [double]$record.Bounds2D.Depth) / 2.0
                if ($flatRadius -lt $MinimumFootprintRadius) { $reason = "TinyFootprint" }
            }
        }
        else {
            $flatRadius = [Math]::Max([double]$record.Bounds2D.Width, [double]$record.Bounds2D.Depth) / 2.0
            if ($flatRadius -lt $MinimumFootprintRadius) { $reason = "TinyFootprint" }
        }

        if ($null -ne $reason) {
            $record.Footprint.RejectionReason = $reason
            $record.Footprint.IsWallLikeFatVictim = $isWallLikeFatVictim
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
        MaxMeshFootprintAreaSqM = [Math]::Round($MaxMeshFootprintAreaSqM, 4)
        WallMinVerticalExtent = [Math]::Round($WallMinVerticalExtent, 4)
        WallMaxBottomY = [Math]::Round($WallMaxBottomY, 4)
        WallMaxTopY = [Math]::Round($WallMaxTopY, 4)
        WallMaxThinDimension = [Math]::Round($WallMaxThinDimension, 4)
        ThinFrameMinVerticalExtent = [Math]::Round($ThinFrameMinVerticalExtent, 4)
        ThinFrameMaxThinDimension = [Math]::Round($ThinFrameMaxThinDimension, 4)
        ThinFrameMinSpanDimension = [Math]::Round($ThinFrameMinSpanDimension, 4)
        ThinFrameMaxBottomY = [Math]::Round($ThinFrameMaxBottomY, 4)
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
}

$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote blocker collider data to $OutputPath"
Write-Host ("MeshColliders parsed: {0}/{1}  Records: {2}  Total navigation blockers: {3}" -f $meshColliderResolvedCount, $meshColliderComponents.Count, $meshColliderRecords.Count, $navigationBlockers.Count)
