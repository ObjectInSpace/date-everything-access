# Export all InteractableObj instances from the scene as nav targets.
#
# These are the targets the object-first planner navigates to. See
# project_navigation_object_first_plan.md (Step O1).
#
# InteractableManager picks these up at runtime via
# FindObjectsOfType<InteractableObj>(includeInactive: true), so we include
# inactive GameObjects too.
#
# Each record:
#   - World position (XZ for planning, Y for floor band classification).
#   - GameObject id / name / hierarchy path.
#   - InteractionRadius (from the serialized field; default 7.5).
#   - inkFileName (empty for non-dateable interactables).
#   - internalCharacterName (datable internal name when authored).
#   - InteractOnLoad / allowActivationOnLoad / bumpable flags.
#   - HasAlternateInteractions flag (true if a non-null entry in the list).
#   - IsActive.

[CmdletBinding()]
param(
    [string]$ScenePath = "D:\root\AssetRipper\1.3.12-premium\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox.unity",
    [string]$OutputPath = ".\artifacts\navigation\thirdpersongreybox-interactables.json",
    [string]$InteractableObjScriptGuid = "be0aae912dc4cf5f4134485b9d59d925",

    # Blocker export (produced first by Export-SceneBlockerData.ps1). When
    # present, each interactable gets world-space Bounds3D/Bounds2D derived from
    # the union of its PHYSICAL collider bounds — every blocker record whose
    # GameObject is the interactable's own or a descendant. The blocker export
    # already filters out triggers, non-player-collidable layers, and the
    # animated dialog-rig (SkinnedMeshRigCollider), so these bounds reflect the
    # static physical object the player routes to/around, not the dating-scene
    # animation. Absent => Bounds fields are null (Position/Radius still emitted).
    [string]$BlockersPath = ".\artifacts\navigation\thirdpersongreybox-blockers.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Numerics

function ConvertTo-Float([string]$v) {
    return [float]::Parse($v, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Parse-Vector3([string]$text) {
    if ($null -eq $text) { return $null }
    $m = [regex]::Match($text, "\{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}")
    if (-not $m.Success) { return $null }
    return [System.Numerics.Vector3]::new(
        (ConvertTo-Float $m.Groups[1].Value),
        (ConvertTo-Float $m.Groups[2].Value),
        (ConvertTo-Float $m.Groups[3].Value))
}

function Parse-Quaternion([string]$text) {
    if ($null -eq $text) { return $null }
    $m = [regex]::Match($text, "\{x: ([^,]+), y: ([^,]+), z: ([^,]+), w: ([^}]+)\}")
    if (-not $m.Success) { return $null }
    return [System.Numerics.Quaternion]::new(
        (ConvertTo-Float $m.Groups[1].Value),
        (ConvertTo-Float $m.Groups[2].Value),
        (ConvertTo-Float $m.Groups[3].Value),
        (ConvertTo-Float $m.Groups[4].Value))
}

function Get-Line([string[]]$lines, [string]$pattern) {
    foreach ($l in $lines) {
        $m = [regex]::Match($l, $pattern)
        if ($m.Success) { return $m.Groups[1].Value }
    }
    return $null
}

function Get-LineOrDefault([string[]]$lines, [string]$pattern, [string]$default) {
    $v = Get-Line $lines $pattern
    if ($null -eq $v) { return $default }
    return $v
}

if (-not (Test-Path -LiteralPath $ScenePath)) { throw "Scene file not found: $ScenePath" }

$gameObjects = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformsById = [System.Collections.Generic.Dictionary[long, object]]::new()
$transformByGameObject = [System.Collections.Generic.Dictionary[long, object]]::new()
$interactableComponents = New-Object System.Collections.Generic.List[object]

$currentHeader = $null
$currentLines = $null

function Process-Section([string]$header, [System.Collections.Generic.List[string]]$lines) {
    $h = [regex]::Match($header, "^--- !u!(\d+) &(\d+)$")
    if (-not $h.Success) { return }
    $type = [int]$h.Groups[1].Value
    $id = [long]$h.Groups[2].Value
    $arr = $lines.ToArray()

    switch ($type) {
        1 {
            $name = Get-Line $arr "^  m_Name: (.*)$"
            if ($null -eq $name) { return }
            $gameObjects[$id] = [pscustomobject]@{
                Id = $id
                Name = $name
                Layer = [int](Get-LineOrDefault $arr "^  m_Layer: (\d+)$" "0")
                IsActive = ((Get-Line $arr "^  m_IsActive: (\d+)$") -ne "0")
            }
        }
        4 {
            $goIdText = Get-Line $arr "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -eq $goIdText) { return }
            $info = [pscustomobject]@{
                Id = $id
                GameObjectId = [long]$goIdText
                ParentTransformId = [long](Get-LineOrDefault $arr "^  m_Father: \{fileID: (\d+)\}$" "0")
                LocalPosition = Parse-Vector3 (Get-Line $arr "^  m_LocalPosition: (.+)$")
                LocalRotation = Parse-Quaternion (Get-Line $arr "^  m_LocalRotation: (.+)$")
                LocalScale = Parse-Vector3 (Get-Line $arr "^  m_LocalScale: (.+)$")
            }
            if ($null -eq $info.LocalPosition) { $info.LocalPosition = [System.Numerics.Vector3]::Zero }
            if ($null -eq $info.LocalRotation) { $info.LocalRotation = [System.Numerics.Quaternion]::Identity }
            if ($null -eq $info.LocalScale) { $info.LocalScale = [System.Numerics.Vector3]::One }
            $transformsById[$id] = $info
            $transformByGameObject[$info.GameObjectId] = $info
        }
        114 {
            $scriptGuid = Get-Line $arr "^  m_Script: \{fileID: \d+, guid: ([0-9a-f]+), type: \d+\}\s*$"
            if ($scriptGuid -ne $script:InteractableObjScriptGuid) { return }
            $goIdText = Get-Line $arr "^  m_GameObject: \{fileID: (\d+)\}$"
            if ($null -eq $goIdText) { return }

            $hasAltInteraction = $false
            $inAlt = $false
            foreach ($l in $arr) {
                if ($l -match "^  AlternateInteractions:") { $inAlt = $true; continue }
                if ($inAlt) {
                    if ($l -match "^  [A-Za-z]") { break }
                    if ($l -match "^\s*-\s*\{fileID: (-?\d+)") {
                        if ([long]$Matches[1] -ne 0) { $hasAltInteraction = $true }
                    }
                }
            }

            $script:interactableComponents.Add([pscustomobject]@{
                ComponentId = $id
                GameObjectId = [long]$goIdText
                InteractionRadius = [double](Get-LineOrDefault $arr "^  InteractionRadius: ([^ ]+)$" "7.5")
                InkFileName = (Get-LineOrDefault $arr "^  inkFileName: (.*)$" "").Trim()
                InternalCharacterName = (Get-LineOrDefault $arr "^  internalCharacterName: (.*)$" "").Trim()
                InteractOnLoad = ((Get-LineOrDefault $arr "^  InteractOnLoad: (\d+)$" "0") -eq "1")
                AllowActivationOnLoad = ((Get-LineOrDefault $arr "^  allowActivationOnLoad: (\d+)$" "1") -eq "1")
                Bumpable = ((Get-LineOrDefault $arr "^  bumpable: (\d+)$" "1") -eq "1")
                HasAlternateInteractions = $hasAltInteraction
            })
        }
    }
}

foreach ($line in [System.IO.File]::ReadLines($ScenePath)) {
    if ($line.StartsWith("--- !u!")) {
        if ($null -ne $currentHeader) { Process-Section $currentHeader $currentLines }
        $currentHeader = $line
        $currentLines = [System.Collections.Generic.List[string]]::new()
        continue
    }
    if ($null -ne $currentHeader) { $currentLines.Add($line) }
}
if ($null -ne $currentHeader) { Process-Section $currentHeader $currentLines }

# Resolve world transforms (position + rotation) for every transform we touch.
$worldByTransform = [System.Collections.Generic.Dictionary[long, object]]::new()

function Get-WorldTransform([long]$tid) {
    if ($script:worldByTransform.ContainsKey($tid)) { return $script:worldByTransform[$tid] }
    if (-not $script:transformsById.ContainsKey($tid)) { return $null }
    $t = $script:transformsById[$tid]
    if ($t.ParentTransformId -eq 0 -or -not $script:transformsById.ContainsKey($t.ParentTransformId)) {
        $w = [ordered]@{
            Position = $t.LocalPosition
            Rotation = [System.Numerics.Quaternion]::Normalize($t.LocalRotation)
            Scale = $t.LocalScale
        }
        $script:worldByTransform[$tid] = $w
        return $w
    }
    $p = Get-WorldTransform $t.ParentTransformId
    if ($null -eq $p) {
        $w = [ordered]@{
            Position = $t.LocalPosition
            Rotation = [System.Numerics.Quaternion]::Normalize($t.LocalRotation)
            Scale = $t.LocalScale
        }
        $script:worldByTransform[$tid] = $w
        return $w
    }
    $scaled = [System.Numerics.Vector3]::new(
        $t.LocalPosition.X * $p.Scale.X,
        $t.LocalPosition.Y * $p.Scale.Y,
        $t.LocalPosition.Z * $p.Scale.Z)
    $rotated = [System.Numerics.Vector3]::Transform($scaled, $p.Rotation)
    $w = [ordered]@{
        Position = $p.Position + $rotated
        Rotation = [System.Numerics.Quaternion]::Normalize($p.Rotation * $t.LocalRotation)
        Scale = [System.Numerics.Vector3]::new(
            $t.LocalScale.X * $p.Scale.X,
            $t.LocalScale.Y * $p.Scale.Y,
            $t.LocalScale.Z * $p.Scale.Z)
    }
    $script:worldByTransform[$tid] = $w
    return $w
}

function Get-GameObjectPath([long]$goId) {
    $segments = New-Object System.Collections.Generic.List[string]
    $cur = $goId
    while ($cur -ne 0 -and $script:gameObjects.ContainsKey($cur)) {
        $segments.Add($script:gameObjects[$cur].Name)
        if (-not $script:transformByGameObject.ContainsKey($cur)) { break }
        $t = $script:transformByGameObject[$cur]
        if ($t.ParentTransformId -eq 0 -or -not $script:transformsById.ContainsKey($t.ParentTransformId)) { break }
        $cur = $script:transformsById[$t.ParentTransformId].GameObjectId
    }
    $a = $segments.ToArray()
    [array]::Reverse($a)
    return [string]::Join("/", $a)
}

function ToEulerDegrees([System.Numerics.Quaternion]$q) {
    $q = [System.Numerics.Quaternion]::Normalize($q)
    # Yaw (Y), pitch (X), roll (Z) Unity-style.
    $sinp = 2.0 * ($q.W * $q.X - $q.Z * $q.Y)
    $pitch = if ([Math]::Abs($sinp) -ge 1) { [Math]::Sign($sinp) * ([Math]::PI / 2.0) } else { [Math]::Asin($sinp) }
    $yaw = [Math]::Atan2(2.0 * ($q.W * $q.Y + $q.Z * $q.X), 1.0 - 2.0 * ($q.X * $q.X + $q.Y * $q.Y))
    $roll = [Math]::Atan2(2.0 * ($q.W * $q.Z + $q.X * $q.Y), 1.0 - 2.0 * ($q.Z * $q.Z + $q.X * $q.X))
    return [ordered]@{
        Pitch = [Math]::Round($pitch * 180.0 / [Math]::PI, 4)
        Yaw   = [Math]::Round($yaw   * 180.0 / [Math]::PI, 4)
        Roll  = [Math]::Round($roll  * 180.0 / [Math]::PI, 4)
    }
}

# Load the blocker export and index physical-collider Bounds3D by GameObject
# path. Each interactable's world dimensions are the union of all blocker bounds
# at-or-under its path (its own collider plus descendant *_MODEL_UPDATE nodes).
$blockerRecordsByPath = $null
if (-not [string]::IsNullOrWhiteSpace($BlockersPath) -and (Test-Path -LiteralPath $BlockersPath)) {
    $blockerJson = Get-Content -LiteralPath $BlockersPath -Raw | ConvertFrom-Json
    $blockerRecordsByPath = New-Object System.Collections.Generic.List[object]
    foreach ($b in @($blockerJson.NavigationBlockers)) {
        if ($null -eq $b.Path -or $null -eq $b.Bounds3D) { continue }
        $blockerRecordsByPath.Add($b)
    }
    # Sort once by path so the per-datable prefix scan is a contiguous range.
    $blockerRecordsByPath = @($blockerRecordsByPath | Sort-Object -Property Path)
    Write-Host ("Loaded {0} physical blocker records for datable dimensions from {1}" -f $blockerRecordsByPath.Count, $BlockersPath)
} else {
    Write-Host ("Blocker export not found at {0}; datable Bounds fields will be null" -f $BlockersPath)
}

function Get-DatableBounds {
    <#
    Unions the world AABBs (Bounds3D) of every physical blocker record whose
    GameObject is $Path itself or a descendant of it. Returns @{ Bounds3D;
    Bounds2D } or $null when the datable has no physical collider (e.g. a
    look-only interactable with no blocking geometry).
    #>
    param([Parameter(Mandatory = $true)] [string]$Path)
    if ($null -eq $script:blockerRecordsByPath) { return $null }

    $descPrefix = $Path + "/"
    $minX = [double]::PositiveInfinity; $minY = [double]::PositiveInfinity; $minZ = [double]::PositiveInfinity
    $maxX = [double]::NegativeInfinity; $maxY = [double]::NegativeInfinity; $maxZ = [double]::NegativeInfinity
    $found = $false
    foreach ($b in $script:blockerRecordsByPath) {
        $bp = [string]$b.Path
        if ($bp -ne $Path -and -not $bp.StartsWith($descPrefix, [System.StringComparison]::Ordinal)) { continue }
        $bn = $b.Bounds3D
        $minX = [Math]::Min($minX, [double]$bn.Min.x); $minY = [Math]::Min($minY, [double]$bn.Min.y); $minZ = [Math]::Min($minZ, [double]$bn.Min.z)
        $maxX = [Math]::Max($maxX, [double]$bn.Max.x); $maxY = [Math]::Max($maxY, [double]$bn.Max.y); $maxZ = [Math]::Max($maxZ, [double]$bn.Max.z)
        $found = $true
    }
    if (-not $found) { return $null }

    return [ordered]@{
        Bounds3D = [ordered]@{
            Min    = [ordered]@{ x = [Math]::Round($minX, 6); y = [Math]::Round($minY, 6); z = [Math]::Round($minZ, 6) }
            Max    = [ordered]@{ x = [Math]::Round($maxX, 6); y = [Math]::Round($maxY, 6); z = [Math]::Round($maxZ, 6) }
            Size   = [ordered]@{ x = [Math]::Round($maxX - $minX, 6); y = [Math]::Round($maxY - $minY, 6); z = [Math]::Round($maxZ - $minZ, 6) }
            Center = [ordered]@{ x = [Math]::Round(($minX + $maxX) / 2.0, 6); y = [Math]::Round(($minY + $maxY) / 2.0, 6); z = [Math]::Round(($minZ + $maxZ) / 2.0, 6) }
        }
        Bounds2D = [ordered]@{
            MinX = [Math]::Round($minX, 6); MaxX = [Math]::Round($maxX, 6)
            MinZ = [Math]::Round($minZ, 6); MaxZ = [Math]::Round($maxZ, 6)
            Width = [Math]::Round($maxX - $minX, 6); Depth = [Math]::Round($maxZ - $minZ, 6)
        }
    }
}

$records = New-Object System.Collections.Generic.List[object]
$missingTransform = 0
$missingGameObject = 0
$withBounds = 0

foreach ($c in $interactableComponents) {
    if (-not $gameObjects.ContainsKey($c.GameObjectId)) { $missingGameObject++; continue }
    if (-not $transformByGameObject.ContainsKey($c.GameObjectId)) { $missingTransform++; continue }
    $go = $gameObjects[$c.GameObjectId]
    $t = $transformByGameObject[$c.GameObjectId]
    $w = Get-WorldTransform $t.Id
    if ($null -eq $w) { $missingTransform++; continue }

    $isDatable = -not [string]::IsNullOrWhiteSpace($c.InkFileName)
    $path = Get-GameObjectPath $go.Id
    $bounds = Get-DatableBounds -Path $path
    if ($null -ne $bounds) { $withBounds++ }
    $records.Add([ordered]@{
        GameObjectId = $go.Id
        GameObjectName = $go.Name
        Path = $path
        Layer = $go.Layer
        IsActive = $go.IsActive
        ComponentId = $c.ComponentId
        TransformId = $t.Id
        Position = [ordered]@{
            x = [Math]::Round([double]$w.Position.X, 6)
            y = [Math]::Round([double]$w.Position.Y, 6)
            z = [Math]::Round([double]$w.Position.Z, 6)
        }
        RotationEuler = ToEulerDegrees $w.Rotation
        InteractionRadius = [Math]::Round([double]$c.InteractionRadius, 4)
        # World-space extent of the physical object (union of its blocking
        # colliders). Null when the interactable has no player-blocking geometry.
        Bounds3D = $(if ($null -ne $bounds) { $bounds.Bounds3D } else { $null })
        Bounds2D = $(if ($null -ne $bounds) { $bounds.Bounds2D } else { $null })
        IsDatable = $isDatable
        InkFileName = $c.InkFileName
        InternalCharacterName = $c.InternalCharacterName
        InteractOnLoad = $c.InteractOnLoad
        AllowActivationOnLoad = $c.AllowActivationOnLoad
        Bumpable = $c.Bumpable
        HasAlternateInteractions = $c.HasAlternateInteractions
    })
}

$outDir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$datableCount = ($records | Where-Object { $_.IsDatable }).Count
$activeCount = ($records | Where-Object { $_.IsActive }).Count

$result = [ordered]@{
    ScenePath = $ScenePath
    GeneratedAt = (Get-Date).ToString("o")
    InteractableObjScriptGuid = $InteractableObjScriptGuid
    Counts = [ordered]@{
        TotalInteractables = $records.Count
        Datable = $datableCount
        NonDatable = $records.Count - $datableCount
        ActiveGameObjects = $activeCount
        InactiveGameObjects = $records.Count - $activeCount
        WithPhysicalBounds = $withBounds
        SkippedMissingGameObject = $missingGameObject
        SkippedMissingTransform = $missingTransform
    }
    Interactables = $records.ToArray()
}

$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host ("Wrote {0} interactables to {1} (datable: {2}, active: {3}, with physical bounds: {4})" -f $records.Count, $OutputPath, $datableCount, $activeCount, $withBounds)
