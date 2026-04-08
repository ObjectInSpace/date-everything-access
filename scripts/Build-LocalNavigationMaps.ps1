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
    param([Parameter(Mandatory = $true)][object]$CameraSpace)

    $halfX = [Math]::Abs([double]$CameraSpace.Scale.x) / 2.0
    $halfZ = [Math]::Abs([double]$CameraSpace.Scale.z) / 2.0
    return [ordered]@{
        MinX = [double]$CameraSpace.Position.x - $halfX
        MaxX = [double]$CameraSpace.Position.x + $halfX
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

function Get-ZoneFamilyCameraSpaces {
    param([object[]]$CameraSpaces, [string]$ZoneName)

    $pattern = '^' + [regex]::Escape($ZoneName) + '(\d+)?$'
    return @($CameraSpaces | Where-Object { $_.Name -match $pattern })
}

function Get-ZoneUnionBounds {
    param([AllowEmptyCollection()][object[]]$CameraSpaces)

    if ($CameraSpaces.Count -lt 1) { return $null }
    $minX = [double]::PositiveInfinity
    $maxX = [double]::NegativeInfinity
    $minZ = [double]::PositiveInfinity
    $maxZ = [double]::NegativeInfinity
    foreach ($cameraSpace in $CameraSpaces) {
        $bounds = Get-CameraSpaceBounds2D -CameraSpace $cameraSpace
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
    return @($Indices.ToArray())
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
$navigationBlockers = @($blockerData.NavigationBlockers)
$zonesOutput = New-Object System.Collections.Generic.List[object]

foreach ($zoneName in (Get-UniqueStrings -Values $graphZoneNames)) {
    $sceneZones = @(Get-ZoneFamilyCameraSpaces -CameraSpaces $cameraSpaces -ZoneName $zoneName)
    if ($sceneZones.Count -lt 1) {
        $zonesOutput.Add([ordered]@{ Zone = $zoneName; SceneZoneNames = @(); CellSize = [Math]::Round($CellSize, 4); ClearanceRadius = [Math]::Round($ClearanceRadius, 4); Notes = @("NoCameraSpaces") })
        continue
    }

    $sceneZoneBounds = @($sceneZones | ForEach-Object { Get-CameraSpaceBounds2D -CameraSpace $_ })
    $unionBounds = Get-ZoneUnionBounds -CameraSpaces $sceneZones
    $intersectingBlockers = @($navigationBlockers | Where-Object { $_.Bounds2D -ne $null -and (Test-BoundsIntersect2D -Left $_.Bounds2D -Right $unionBounds) })
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
            foreach ($sceneZoneBoundsEntry in $sceneZoneBounds) {
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
        EnvelopeIndices = Convert-IndicesToIntArray -Indices $envelopeIndices
        BlockedIndices = Convert-IndicesToIntArray -Indices $blockedIndices
        SampleBlockers = @($intersectingBlockers | Select-Object -First 12 | ForEach-Object { [ordered]@{ ComponentId = $_.ComponentId; Name = $_.Name; ColliderType = $_.ColliderType } })
        Notes = @("PrimitiveBlockersOnly")
    })
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }

$zoneArray = $zonesOutput.ToArray()
$primitiveColliderCount = @($blockerData.PrimitiveColliders).Count

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
            "Uses filtered primitive colliders only; mesh and terrain colliders are not yet rasterized."
        )
    }
    Counts = [ordered]@{
        GraphZones = $zonesOutput.Count
        NavigationBlockers = $navigationBlockers.Count
        PrimitiveColliders = $primitiveColliderCount
    }
    Zones = $zoneArray
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote local navigation maps to $OutputPath"
