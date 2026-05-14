[CmdletBinding()]
param(
    [Parameter()]
    [string]$NavigationDataPath = ".\artifacts\navigation\thirdpersongreybox-navigation-data.json",

    [Parameter()]
    [string]$BlockerDataPath = ".\artifacts\navigation\thirdpersongreybox-blockers.json",

    [Parameter()]
    [string]$GraphPath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\navigation_graph.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\local_navigation_maps.generated.json",

    [Parameter()]
    [double]$CellSize = 0.5,

    [Parameter()]
    [double]$ClearanceRadius = 0.45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-UniqueStrings {
    param([AllowNull()][object[]]$Values)

    $seen = @{}
    $result = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        if ($null -eq $value) { continue }
        $text = [string]$value
        if ([string]::IsNullOrWhiteSpace($text) -or $seen.ContainsKey($text)) { continue }
        $seen[$text] = $true
        $result.Add($text)
    }

    return $result.ToArray()
}

function Get-CameraSpaceBounds2D {
    param(
        [Parameter(Mandatory = $true)][object]$CameraSpace,
        [Parameter()][double]$MinimumHalfExtent = 0.0
    )

    $halfX = [Math]::Max([Math]::Abs([double]$CameraSpace.Scale.x) / 2.0, $MinimumHalfExtent)
    $halfZ = [Math]::Max([Math]::Abs([double]$CameraSpace.Scale.z) / 2.0, $MinimumHalfExtent)
    return [ordered]@{
        MinX = [double]$CameraSpace.Position.x - $halfX
        MaxX = [double]$CameraSpace.Position.x + $halfX
        MinZ = [double]$CameraSpace.Position.z - $halfZ
        MaxZ = [double]$CameraSpace.Position.z + $halfZ
    }
}

function Get-CameraSpaceBounds3D {
    param(
        [Parameter(Mandatory = $true)][object]$CameraSpace,
        [Parameter()][double]$MinimumHorizontalHalfExtent = 0.0
    )

    $halfX = [Math]::Max([Math]::Abs([double]$CameraSpace.Scale.x) / 2.0, $MinimumHorizontalHalfExtent)
    $halfY = [Math]::Abs([double]$CameraSpace.Scale.y) / 2.0
    $halfZ = [Math]::Max([Math]::Abs([double]$CameraSpace.Scale.z) / 2.0, $MinimumHorizontalHalfExtent)
    return [ordered]@{
        MinX = [double]$CameraSpace.Position.x - $halfX
        MaxX = [double]$CameraSpace.Position.x + $halfX
        MinY = [double]$CameraSpace.Position.y - $halfY
        MaxY = [double]$CameraSpace.Position.y + $halfY
        MinZ = [double]$CameraSpace.Position.z - $halfZ
        MaxZ = [double]$CameraSpace.Position.z + $halfZ
    }
}

function Test-BoundsContainsPoint2D {
    param([object]$Bounds, [double]$X, [double]$Z)

    return $X -ge $Bounds.MinX -and $X -le $Bounds.MaxX -and $Z -ge $Bounds.MinZ -and $Z -le $Bounds.MaxZ
}

function Test-BoundsIntersect2D {
    param([object]$Left, [object]$Right)

    return $Left.MinX -le $Right.MaxX -and $Left.MaxX -ge $Right.MinX -and $Left.MinZ -le $Right.MaxZ -and $Left.MaxZ -ge $Right.MinZ
}

function Test-BoundsIntersect3D {
    param([object]$Left, [object]$Right)

    if ($null -eq $Left -or $null -eq $Right) { return $false }
    return (
        $Left.MinX -le $Right.MaxX -and $Left.MaxX -ge $Right.MinX -and
        $Left.MinY -le $Right.MaxY -and $Left.MaxY -ge $Right.MinY -and
        $Left.MinZ -le $Right.MaxZ -and $Left.MaxZ -ge $Right.MinZ
    )
}

function Get-ZoneFamilyCameraSpaces {
    param([object[]]$CameraSpaces, [string]$ZoneName)

    $pattern = '^' + [regex]::Escape($ZoneName) + '(\d+)?$'
    return @($CameraSpaces | Where-Object { $_.Name -match $pattern })
}

function Get-ZoneUnionBounds {
    param(
        [AllowEmptyCollection()][object[]]$CameraSpaces,
        [Parameter()][double]$MinimumHalfExtent = 0.0
    )

    if ($CameraSpaces.Count -lt 1) { return $null }
    $minX = [double]::PositiveInfinity
    $maxX = [double]::NegativeInfinity
    $minZ = [double]::PositiveInfinity
    $maxZ = [double]::NegativeInfinity
    foreach ($cameraSpace in $CameraSpaces) {
        $bounds = Get-CameraSpaceBounds2D -CameraSpace $cameraSpace -MinimumHalfExtent $MinimumHalfExtent
        $minX = [Math]::Min($minX, $bounds.MinX)
        $maxX = [Math]::Max($maxX, $bounds.MaxX)
        $minZ = [Math]::Min($minZ, $bounds.MinZ)
        $maxZ = [Math]::Max($maxZ, $bounds.MaxZ)
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

function Get-AuthoredZoneEnvelopeExtensions {
    param([string]$ZoneName)

    switch ($ZoneName) {
        "living_room" {
            return @(
                [ordered]@{ MinX = 4.50; MaxX = 10.10; MinZ = -6.90; MaxZ = -6.05; Reason = "living_room_hallway_threshold" }
            )
        }
        "hallway" {
            return @(
                [ordered]@{ MinX = 4.50; MaxX = 10.10; MinZ = -7.20; MaxZ = -5.35; Reason = "hallway_living_room_threshold" },
                [ordered]@{ MinX = 1.50; MaxX = 2.80; MinZ = 5.20; MaxZ = 5.80; Reason = "bathroom1_hallway_threshold" }
            )
        }
        "dining_room" {
            return @(
                [ordered]@{ MinX = -18.10; MaxX = -16.20; MinZ = -10.55; MaxZ = -9.90; Reason = "dining_room_piano_room_threshold" }
            )
        }
        "upper_hallway" {
            return @(
                [ordered]@{ MinX = 0.40; MaxX = 3.10; MinZ = 3.10; MaxZ = 4.20; Reason = "upper_hallway_attic_threshold" }
            )
        }
        "bathroom1" {
            return @(
                [ordered]@{ MinX = 1.20; MaxX = 2.30; MinZ = 3.80; MaxZ = 7.30; Reason = "bathroom1_hallway_door_lane" },
                [ordered]@{ MinX = -0.60; MaxX = 2.50; MinZ = 9.80; MaxZ = 11.60; Reason = "bathroom1_hallway_threshold" }
            )
        }
        "gym_closet" {
            return @(
                [ordered]@{ MinX = -6.10; MaxX = -2.40; MinZ = 17.30; MaxZ = 20.20; Reason = "gym_closet_gym_threshold" }
            )
        }
        default {
            return @()
        }
    }
}

function Add-Bounds2DExtensions {
    param(
        [object]$Bounds,
        [AllowNull()][object[]]$Extensions
    )

    if ($null -eq $Bounds) { return $null }
    foreach ($extension in @($Extensions)) {
        if ($null -eq $extension) { continue }
        $Bounds.MinX = [Math]::Min([double]$Bounds.MinX, [double]$extension.MinX)
        $Bounds.MaxX = [Math]::Max([double]$Bounds.MaxX, [double]$extension.MaxX)
        $Bounds.MinZ = [Math]::Min([double]$Bounds.MinZ, [double]$extension.MinZ)
        $Bounds.MaxZ = [Math]::Max([double]$Bounds.MaxZ, [double]$extension.MaxZ)
    }

    $Bounds.MinX = [Math]::Round([double]$Bounds.MinX, 6)
    $Bounds.MaxX = [Math]::Round([double]$Bounds.MaxX, 6)
    $Bounds.MinZ = [Math]::Round([double]$Bounds.MinZ, 6)
    $Bounds.MaxZ = [Math]::Round([double]$Bounds.MaxZ, 6)
    $Bounds.Width = [Math]::Round([double]$Bounds.MaxX - [double]$Bounds.MinX, 6)
    $Bounds.Depth = [Math]::Round([double]$Bounds.MaxZ - [double]$Bounds.MinZ, 6)
    return $Bounds
}

function New-AuthoredAxisAlignedBlocker {
    param(
        [string]$Name,
        [string]$Path,
        [double]$MinX,
        [double]$MaxX,
        [double]$MinY,
        [double]$MaxY,
        [double]$MinZ,
        [double]$MaxZ,
        [string]$Reason
    )

    $centerX = ($MinX + $MaxX) / 2.0
    $centerY = ($MinY + $MaxY) / 2.0
    $centerZ = ($MinZ + $MaxZ) / 2.0
    return [pscustomobject]([ordered]@{
        ComponentId = -1
        GameObjectId = -1
        Name = $Name
        Path = $Path
        Layer = 0
        StaticEditorFlags = 0
        IsActive = $true
        ColliderType = "AuthoredMeshCollider"
        Enabled = $true
        IsTrigger = $false
        IsDoorConnector = $true
        IsTeleporterConnector = $false
        HasRigidbody = $false
        RigidbodyIsKinematic = $false
        Bounds2D = [ordered]@{
            MinX = [Math]::Round($MinX, 6)
            MaxX = [Math]::Round($MaxX, 6)
            MinZ = [Math]::Round($MinZ, 6)
            MaxZ = [Math]::Round($MaxZ, 6)
            Width = [Math]::Round($MaxX - $MinX, 6)
            Depth = [Math]::Round($MaxZ - $MinZ, 6)
        }
        Bounds3D = [ordered]@{
            Min = [ordered]@{ x = [Math]::Round($MinX, 6); y = [Math]::Round($MinY, 6); z = [Math]::Round($MinZ, 6) }
            Max = [ordered]@{ x = [Math]::Round($MaxX, 6); y = [Math]::Round($MaxY, 6); z = [Math]::Round($MaxZ, 6) }
            Size = [ordered]@{ x = [Math]::Round($MaxX - $MinX, 6); y = [Math]::Round($MaxY - $MinY, 6); z = [Math]::Round($MaxZ - $MinZ, 6) }
            Center = [ordered]@{ x = [Math]::Round($centerX, 6); y = [Math]::Round($centerY, 6); z = [Math]::Round($centerZ, 6) }
        }
        BottomY = [Math]::Round($MinY, 6)
        TopY = [Math]::Round($MaxY, 6)
        Footprint = [ordered]@{
            Kind = "OrientedBox"
            Center = [ordered]@{ x = [Math]::Round($centerX, 6); y = [Math]::Round($centerY, 6); z = [Math]::Round($centerZ, 6) }
            HalfExtents = [ordered]@{ x = [Math]::Round(($MaxX - $MinX) / 2.0, 6); z = [Math]::Round(($MaxZ - $MinZ) / 2.0, 6) }
            Right = [ordered]@{ x = 1.0; z = 0.0 }
            Forward = [ordered]@{ x = 0.0; z = 1.0 }
        }
        LocalShape = [ordered]@{ Source = "RuntimeMovementProbe"; Reason = $Reason }
        AuthoredRuntimeMeshBlocker = $true
        AppliesToZones = @()
    })
}

function Get-AuthoredRuntimeMeshBlockers {
    return @(
        New-AuthoredAxisAlignedBlocker `
            -Name "Table_LivingRoom_TV runtime mesh blocker" `
            -Path "===SCENE===/House/LivingRoom/Table_LivingRoom_TV" `
            -MinX 5.81 -MaxX 13.69 -MinY -0.61 -MaxY 0.87 -MinZ -9.14 -MaxZ -7.09 `
            -Reason "runtime-probe:living_room->hallway:open-passage-override-source" |
            ForEach-Object { $_.AppliesToZones = @("living_room"); $_ }
        New-AuthoredAxisAlignedBlocker `
            -Name "Doors_Bathroom1 runtime mesh blocker" `
            -Path "===SCENE===/House/MultiRoom/Doors/Doors_Bathroom1" `
            -MinX 0.29 -MaxX 2.13 -MinY -0.62 -MaxY 7.35 -MinZ 6.30 -MaxZ 9.89 `
            -Reason "runtime-probe:bathroom1->hallway:door-threshold-handoff-local" |
            ForEach-Object { $_.AppliesToZones = @("bathroom1", "hallway"); $_ }
        New-AuthoredAxisAlignedBlocker `
            -Name "Doors_Gym_ClosetInner runtime mesh blocker" `
            -Path "===SCENE===/House/MultiRoom/Doors/Doors_Gym_ClosetInner" `
            -MinX -4.34 -MaxX -4.11 -MinY 12.82 -MaxY 20.64 -MinZ 20.27 -MaxZ 23.91 `
            -Reason "runtime-probe:gym_closet->gym:door-push-through-local" |
            ForEach-Object { $_.AppliesToZones = @("gym_closet", "gym"); $_ }
        New-AuthoredAxisAlignedBlocker `
            -Name "Doors_Gym_ClosetOuter runtime mesh blocker" `
            -Path "===SCENE===/House/MultiRoom/Doors/Doors_Gym_ClosetOuter" `
            -MinX -4.55 -MaxX -4.32 -MinY 12.82 -MaxY 20.64 -MinZ 16.92 -MaxZ 20.58 `
            -Reason "runtime-probe:gym_closet->gym:door-entry-advance-local" |
            ForEach-Object { $_.AppliesToZones = @("gym_closet", "gym"); $_ }
        New-AuthoredAxisAlignedBlocker `
            -Name "Doors_Office runtime mesh blocker" `
            -Path "===SCENE===/House/MultiRoom/Doors/OfficeDoors/Doors_Office" `
            -MinX 8.49 -MaxX 9.87 -MinY -0.62 -MaxY 7.35 -MinZ 6.20 -MaxZ 9.93 `
            -Reason "runtime-probe:office->hallway:door-entry-advance-local" |
            ForEach-Object { $_.AppliesToZones = @("office", "hallway"); $_ }
    )
}

function Distance-ToSegment2D {
    param([double]$PointX, [double]$PointZ, [double]$StartX, [double]$StartZ, [double]$EndX, [double]$EndZ)

    $segmentX = $EndX - $StartX
    $segmentZ = $EndZ - $StartZ
    $segmentLengthSquared = ($segmentX * $segmentX) + ($segmentZ * $segmentZ)
    if ($segmentLengthSquared -le 0.0000001) {
        return [Math]::Sqrt((($PointX - $StartX) * ($PointX - $StartX)) + (($PointZ - $StartZ) * ($PointZ - $StartZ)))
    }

    $t = (($PointX - $StartX) * $segmentX + ($PointZ - $StartZ) * $segmentZ) / $segmentLengthSquared
    $t = [Math]::Max(0.0, [Math]::Min(1.0, $t))
    $projectionX = $StartX + ($segmentX * $t)
    $projectionZ = $StartZ + ($segmentZ * $t)
    return [Math]::Sqrt((($PointX - $projectionX) * ($PointX - $projectionX)) + (($PointZ - $projectionZ) * ($PointZ - $projectionZ)))
}

function Test-BlockerContainsPoint2D {
    param([object]$Blocker, [double]$X, [double]$Z, [double]$ClearanceRadius)

    if ($null -eq $Blocker.Footprint -or $null -eq $Blocker.Footprint.Kind) { return $false }
    switch ([string]$Blocker.Footprint.Kind) {
        "Circle" {
            $centerX = [double]$Blocker.Footprint.Center.x
            $centerZ = [double]$Blocker.Footprint.Center.z
            $radius = [double]$Blocker.Footprint.Radius + $ClearanceRadius
            return ((($X - $centerX) * ($X - $centerX)) + (($Z - $centerZ) * ($Z - $centerZ))) -le ($radius * $radius)
        }
        "OrientedBox" {
            $centerX = [double]$Blocker.Footprint.Center.x
            $centerZ = [double]$Blocker.Footprint.Center.z
            $rightX = [double]$Blocker.Footprint.Right.x
            $rightZ = [double]$Blocker.Footprint.Right.z
            $forwardX = [double]$Blocker.Footprint.Forward.x
            $forwardZ = [double]$Blocker.Footprint.Forward.z
            $halfExtentX = [double]$Blocker.Footprint.HalfExtents.x + $ClearanceRadius
            $halfExtentZ = [double]$Blocker.Footprint.HalfExtents.z + $ClearanceRadius
            $offsetX = $X - $centerX
            $offsetZ = $Z - $centerZ
            $alongRight = [Math]::Abs(($offsetX * $rightX) + ($offsetZ * $rightZ))
            $alongForward = [Math]::Abs(($offsetX * $forwardX) + ($offsetZ * $forwardZ))
            return $alongRight -le $halfExtentX -and $alongForward -le $halfExtentZ
        }
        "Capsule2D" {
            $distance = Distance-ToSegment2D -PointX $X -PointZ $Z -StartX ([double]$Blocker.Footprint.Start.x) -StartZ ([double]$Blocker.Footprint.Start.z) -EndX ([double]$Blocker.Footprint.End.x) -EndZ ([double]$Blocker.Footprint.End.z)
            return $distance -le ([double]$Blocker.Footprint.Radius + $ClearanceRadius)
        }
        default { return $false }
    }
}

function Convert-IndicesToIntArray {
    param([System.Collections.Generic.List[int]]$Indices)
    if ($null -eq $Indices) { return @() }
    return $Indices.ToArray()
}

if (-not (Test-Path -LiteralPath $NavigationDataPath)) { throw "Navigation data file not found: $NavigationDataPath" }
if (-not (Test-Path -LiteralPath $BlockerDataPath)) { throw "Blocker data file not found: $BlockerDataPath" }
if (-not (Test-Path -LiteralPath $GraphPath)) { throw "Navigation graph file not found: $GraphPath" }

$navigationData = Get-Content -LiteralPath $NavigationDataPath -Raw | ConvertFrom-Json
$blockerData = Get-Content -LiteralPath $BlockerDataPath -Raw | ConvertFrom-Json
$graphData = Get-Content -LiteralPath $GraphPath -Raw | ConvertFrom-Json

$graphZoneNames = New-Object System.Collections.Generic.List[string]
if ($null -ne $graphData.Zones) {
    foreach ($zone in @($graphData.Zones)) {
        if ($null -ne $zone -and -not [string]::IsNullOrWhiteSpace([string]$zone.Name)) { $graphZoneNames.Add([string]$zone.Name) }
        elseif ($null -ne $zone -and -not [string]::IsNullOrWhiteSpace([string]$zone.Id)) { $graphZoneNames.Add(([string]$zone.Id) -replace '^zone:', '') }
    }
} else {
    foreach ($link in @($graphData.Links)) {
        if ($null -eq $link) { continue }
        if (-not [string]::IsNullOrWhiteSpace([string]$link.FromZone)) { $graphZoneNames.Add([string]$link.FromZone) }
        if (-not [string]::IsNullOrWhiteSpace([string]$link.ToZone)) { $graphZoneNames.Add([string]$link.ToZone) }
    }
}

$cameraSpaces = @($navigationData.CameraSpaces)
$authoredRuntimeMeshBlockers = @(Get-AuthoredRuntimeMeshBlockers)
$navigationBlockers = @(@($blockerData.NavigationBlockers) + @($authoredRuntimeMeshBlockers))
$zonesOutput = New-Object System.Collections.Generic.List[object]

foreach ($zoneName in (Get-UniqueStrings -Values $graphZoneNames)) {
    $sceneZones = @(Get-ZoneFamilyCameraSpaces -CameraSpaces $cameraSpaces -ZoneName $zoneName)
    if ($sceneZones.Count -lt 1) {
        $zonesOutput.Add([ordered]@{ Zone = $zoneName; SceneZoneNames = @(); CellSize = [Math]::Round($CellSize, 4); ClearanceRadius = [Math]::Round($ClearanceRadius, 4); Notes = @("NoCameraSpaces") })
        continue
    }

    $minimumHorizontalHalfExtent = [double]$CellSize / 2.0
    $authoredEnvelopeExtensions = @(Get-AuthoredZoneEnvelopeExtensions -ZoneName $zoneName)
    $sceneZoneBounds2D = @(
        @($sceneZones | ForEach-Object { Get-CameraSpaceBounds2D -CameraSpace $_ -MinimumHalfExtent $minimumHorizontalHalfExtent }) +
        @($authoredEnvelopeExtensions)
    )
    $sceneZoneBounds3D = @($sceneZones | ForEach-Object { Get-CameraSpaceBounds3D -CameraSpace $_ -MinimumHorizontalHalfExtent $minimumHorizontalHalfExtent })
    $unionBounds = Add-Bounds2DExtensions `
        -Bounds (Get-ZoneUnionBounds -CameraSpaces $sceneZones -MinimumHalfExtent $minimumHorizontalHalfExtent) `
        -Extensions $authoredEnvelopeExtensions
    $intersectingBlockers = @(
        $navigationBlockers | Where-Object {
            if ($_.Bounds2D -eq $null -or $_.Bounds3D -eq $null) { return $false }
            $isAuthoredRuntimeMeshBlocker = $null -ne $_.PSObject.Properties["AuthoredRuntimeMeshBlocker"] -and $_.AuthoredRuntimeMeshBlocker
            if ($isAuthoredRuntimeMeshBlocker) {
                return @($_.AppliesToZones) -contains $zoneName
            }

            foreach ($sceneZoneBounds3DEntry in $sceneZoneBounds3D) {
                $blockerBounds3D = [ordered]@{
                    MinX = [double]$_.Bounds3D.Min.x
                    MaxX = [double]$_.Bounds3D.Max.x
                    MinY = [double]$_.Bounds3D.Min.y
                    MaxY = [double]$_.Bounds3D.Max.y
                    MinZ = [double]$_.Bounds3D.Min.z
                    MaxZ = [double]$_.Bounds3D.Max.z
                }

                if (Test-BoundsIntersect3D -Left $blockerBounds3D -Right $sceneZoneBounds3DEntry) {
                    return $true
                }
            }

            return $false
        }
    )
    $width = [Math]::Max(1, [int][Math]::Ceiling($unionBounds.Width / $CellSize))
    $height = [Math]::Max(1, [int][Math]::Ceiling($unionBounds.Depth / $CellSize))
    $envelopeIndices = New-Object System.Collections.Generic.List[int]
    $blockedIndices = New-Object System.Collections.Generic.List[int]

    for ($row = 0; $row -lt $height; $row++) {
        $cellZ = [double]$unionBounds.MinZ + ($row * $CellSize) + ($CellSize / 2.0)
        for ($column = 0; $column -lt $width; $column++) {
            $cellX = [double]$unionBounds.MinX + ($column * $CellSize) + ($CellSize / 2.0)
            $index = ($row * $width) + $column
            $insideEnvelope = $false
            foreach ($sceneZoneBoundsEntry in $sceneZoneBounds2D) {
                if (Test-BoundsContainsPoint2D -Bounds $sceneZoneBoundsEntry -X $cellX -Z $cellZ) { $insideEnvelope = $true; break }
            }
            if (-not $insideEnvelope) { continue }
            $envelopeIndices.Add($index)
            foreach ($blocker in $intersectingBlockers) {
                if (Test-BlockerContainsPoint2D -Blocker $blocker -X $cellX -Z $cellZ -ClearanceRadius $ClearanceRadius) { $blockedIndices.Add($index); break }
            }
        }
    }

    $zonesOutput.Add([ordered]@{
        Zone = $zoneName
        SceneZoneNames = @($sceneZones | ForEach-Object { $_.Name })
        CellSize = [Math]::Round($CellSize, 4)
        ClearanceRadius = [Math]::Round($ClearanceRadius, 4)
        Bounds2D = $unionBounds
        GridWidth = $width
        GridHeight = $height
        EnvelopeCellCount = $envelopeIndices.Count
        BlockedCellCount = $blockedIndices.Count
        WalkableCellCount = $envelopeIndices.Count - $blockedIndices.Count
        IntersectingBlockerCount = $intersectingBlockers.Count
        EnvelopeIndices = @(Convert-IndicesToIntArray -Indices $envelopeIndices)
        BlockedIndices = @(Convert-IndicesToIntArray -Indices $blockedIndices)
        SampleBlockers = @($intersectingBlockers | Select-Object -First 12 | ForEach-Object { [ordered]@{ ComponentId = $_.ComponentId; Name = $_.Name; ColliderType = $_.ColliderType } })
        Notes = @(
            "PrimitiveBlockersOnly"
            @($intersectingBlockers | Where-Object { $null -ne $_.PSObject.Properties["AuthoredRuntimeMeshBlocker"] -and $_.AuthoredRuntimeMeshBlocker } | ForEach-Object { "AuthoredRuntimeMeshBlocker:" + $_.Name })
            @($authoredEnvelopeExtensions | ForEach-Object { "AuthoredEnvelopeExtension:" + $_.Reason })
        )
    })
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }

$zoneArray = $zonesOutput.ToArray()
$primitiveColliderCount = @($blockerData.PrimitiveColliders).Count
$authoredRuntimeMeshBlockerCount = $authoredRuntimeMeshBlockers.Count

$result = [ordered]@{
    SchemaVersion = 1
    GeneratedAt = (Get-Date).ToString("o")
    Inputs = [ordered]@{ NavigationDataPath = $NavigationDataPath; BlockerDataPath = $BlockerDataPath; GraphPath = $GraphPath }
    PlannerSettings = [ordered]@{
        CellSize = [Math]::Round($CellSize, 4)
        ClearanceRadius = [Math]::Round($ClearanceRadius, 4)
        BlockerSource = "NavigationBlockers"
        Notes = @(
            "Uses CameraSpaces family bounds as the zone envelope.",
            "Static generation uses filtered primitive colliders only. Runtime physics-sampled exports provide mesh, wall, door, furniture, and opening occupancy."
        )
    }
    Counts = [ordered]@{
        GraphZones = $zonesOutput.Count
        NavigationBlockers = $navigationBlockers.Count
        PrimitiveColliders = $primitiveColliderCount
        AuthoredRuntimeMeshBlockers = $authoredRuntimeMeshBlockerCount
    }
    Zones = $zoneArray
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote local navigation maps to $OutputPath"
