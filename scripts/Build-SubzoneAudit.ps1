[CmdletBinding()]
param(
    [Parameter()]
    [string]$NavigationDataPath = ".\artifacts\navigation\thirdpersongreybox-navigation-data.json",

    [Parameter()]
    [string]$BlockersPath = ".\artifacts\navigation\thirdpersongreybox-blockers.json",

    [Parameter()]
    [string]$OutputJsonPath = ".\artifacts\navigation\subzone_audit.json",

    [Parameter()]
    [string]$OutputSummaryPath = ".\artifacts\navigation\subzone_audit.summary.txt",

    # Two AABBs are "adjacent" if their separation on the shared axis is <= AdjacencyTolerance
    # and they overlap on the other two axes by at least AdjacencyOverlap. Tolerance accommodates
    # tiny gaps and slight numeric noise between tiled camera regions; overlap rejects pairs that
    # merely touch a corner.
    # Default 3.0m: camera tiles in this scene leave gaps up to ~2.5m between adjacent rooms;
    # see audit run 2026-05-19. Lower values fragment correctly-tiled rooms.
    [Parameter()]
    [double]$AdjacencyTolerance = 3.0,

    [Parameter()]
    [double]$AdjacencyOverlap = 0.5,

    # A blocker counts as wall-like (room-dividing) only if its vertical extent reaches at least
    # this height. Filters out chairs/tables/beds and other furniture-class colliders.
    #
    # Scope: this filter applies ONLY to deciding whether two camera regions are separate rooms.
    # A chair on the line between two subzones doesn't make them different rooms — the player
    # walks around it within the same room. Later passes that compute walkable paths (Step 5,
    # connector derivation) must treat ALL blockers including furniture as obstacles.
    [Parameter()]
    [double]$WallMinHeight = 1.5,

    # Fraction of the shared boundary segment that must be covered by wall-like blockers for
    # the boundary to count as walled.
    [Parameter()]
    [double]$BoundaryBlockedFraction = 0.6,

    # Door collider must lie within this perpendicular distance of the boundary face plane
    # (and within the face's parallel span) to gate the boundary. Tight tolerance because the
    # door pivot sits on the wall, not floating near it.
    [Parameter()]
    [double]$DoorBoundaryDistance = 0.75,

    # A door's pivot Y must lie within [floor-Y - PadDown, floor-Y + MaxAboveFloor] of the
    # boundary's containing zones to be considered a room-passable door. This rejects cupboard,
    # cabinet, fridge, shower, and breaker-box doors, which all sit well above floor level on a
    # wall or counter. Determined from the door population in this scene (2026-05-19):
    # passable doors sit within 1m of their zone floor; furniture doors sit at 1.6m and up.
    [Parameter()]
    [double]$DoorMaxAboveFloor = 1.5,

    [Parameter()]
    [double]$DoorPadDown = 1.0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-Aabb {
    param($CameraSpace)
    $hx = [Math]::Abs($CameraSpace.Scale.x) / 2.0
    $hy = [Math]::Abs($CameraSpace.Scale.y) / 2.0
    $hz = [Math]::Abs($CameraSpace.Scale.z) / 2.0
    return [pscustomobject]@{
        Name = $CameraSpace.Name
        MinX = $CameraSpace.Position.x - $hx
        MaxX = $CameraSpace.Position.x + $hx
        MinY = $CameraSpace.Position.y - $hy
        MaxY = $CameraSpace.Position.y + $hy
        MinZ = $CameraSpace.Position.z - $hz
        MaxZ = $CameraSpace.Position.z + $hz
        CenterY = $CameraSpace.Position.y
    }
}

function Get-SubzoneGroupKey {
    param([string]$Name, [System.Collections.Generic.HashSet[string]]$PrimarySet)
    # Mirror runtime SimpleNavSceneData.ResolvePrimaryZone: if the name is already a primary,
    # it groups under itself. Otherwise strip a trailing digit run; if THAT is a primary, group
    # there. Otherwise the zone is its own group (and the audit will surface that it never
    # rejoins its parent — e.g. "bathroom1_2" strips to "bathroom1_", which is not primary, so
    # it stays orphaned. Showing this in the output reveals a real runtime gap to fix in Step 2
    # / the suffix audit, not something to paper over here).
    if ($PrimarySet.Contains($Name)) { return $Name }
    if ($Name -match '^(.*?)(\d+)$') {
        $stripped = $Matches[1]
        if ($PrimarySet.Contains($stripped)) { return $stripped }
    }
    return $Name
}

# Compute adjacency between two AABBs. Returns $null if not adjacent on any axis.
# Otherwise returns the shared boundary face described as:
#   Axis          - 'X', 'Y', or 'Z' (the axis along which the two boxes are separated)
#   FacePlane     - coordinate on $Axis where the face sits (mean of the two facing surfaces)
#   Separation    - signed gap between the two facing surfaces (negative = overlap)
#   Span1Min/Max  - extent of the shared face on the second axis
#   Span2Min/Max  - extent of the shared face on the third axis
#   YMin/YMax     - vertical extent of the shared face (used to scope blocker scan)
function Test-AabbAdjacency {
    param($A, $B, [double]$Tolerance, [double]$Overlap)

    # Per-axis: overlap if MaxA >= MinB AND MaxB >= MinA. Gap if not.
    $sepX = [Math]::Max($A.MinX, $B.MinX) - [Math]::Min($A.MaxX, $B.MaxX)
    $sepY = [Math]::Max($A.MinY, $B.MinY) - [Math]::Min($A.MaxY, $B.MaxY)
    $sepZ = [Math]::Max($A.MinZ, $B.MinZ) - [Math]::Min($A.MaxZ, $B.MaxZ)

    # The "boundary axis" is the one with the largest separation (smallest overlap). The other
    # two must be overlapping (sep <= 0) by at least $Overlap for the boxes to share a face.
    $candidates = @(
        [pscustomobject]@{ Axis='X'; Sep=$sepX; A_Min=$A.MinX; A_Max=$A.MaxX; B_Min=$B.MinX; B_Max=$B.MaxX },
        [pscustomobject]@{ Axis='Y'; Sep=$sepY; A_Min=$A.MinY; A_Max=$A.MaxY; B_Min=$B.MinY; B_Max=$B.MaxY },
        [pscustomobject]@{ Axis='Z'; Sep=$sepZ; A_Min=$A.MinZ; A_Max=$A.MaxZ; B_Min=$B.MinZ; B_Max=$B.MaxZ }
    ) | Sort-Object -Property Sep -Descending

    $boundary = $candidates[0]
    $otherTwo = @($candidates[1], $candidates[2])

    if ($boundary.Sep -gt $Tolerance) { return $null }
    foreach ($o in $otherTwo) {
        if ($o.Sep -gt -$Overlap) { return $null }  # need overlap (negative sep) of at least $Overlap
    }

    # Face plane: midpoint between the two facing surfaces along the boundary axis.
    $facePlane = ($boundary.A_Max + $boundary.B_Min) / 2.0
    if ($boundary.A_Min -gt $boundary.B_Max) {
        $facePlane = ($boundary.B_Max + $boundary.A_Min) / 2.0
    }

    # Face extents on the other two axes = intersection range.
    $faceSpan = @{}
    foreach ($o in $otherTwo) {
        $faceSpan[$o.Axis] = [pscustomobject]@{
            Min = [Math]::Max($o.A_Min, $o.B_Min)
            Max = [Math]::Min($o.A_Max, $o.B_Max)
        }
    }

    # Map face extents onto the XZ plane (for blocker scan) and Y extent (for height scoping).
    # If the boundary is X or Z, the face lies in the YZ or XY plane and its XZ "trace" is a
    # line segment we'll sweep. If the boundary is Y (rare for room boundaries — floor/ceiling),
    # the face is a horizontal slab; we treat it as the XZ rectangle intersection.
    $result = [pscustomobject]@{
        Axis       = $boundary.Axis
        FacePlane  = $facePlane
        Separation = $boundary.Sep
        XMin = $null; XMax = $null
        YMin = $null; YMax = $null
        ZMin = $null; ZMax = $null
    }

    switch ($boundary.Axis) {
        'X' {
            $result.XMin = $facePlane; $result.XMax = $facePlane
            $result.YMin = $faceSpan['Y'].Min; $result.YMax = $faceSpan['Y'].Max
            $result.ZMin = $faceSpan['Z'].Min; $result.ZMax = $faceSpan['Z'].Max
        }
        'Z' {
            $result.ZMin = $facePlane; $result.ZMax = $facePlane
            $result.YMin = $faceSpan['Y'].Min; $result.YMax = $faceSpan['Y'].Max
            $result.XMin = $faceSpan['X'].Min; $result.XMax = $faceSpan['X'].Max
        }
        'Y' {
            $result.YMin = $facePlane; $result.YMax = $facePlane
            $result.XMin = $faceSpan['X'].Min; $result.XMax = $faceSpan['X'].Max
            $result.ZMin = $faceSpan['Z'].Min; $result.ZMax = $faceSpan['Z'].Max
        }
    }
    return $result
}

# Of all wall-like blockers that touch the boundary segment, compute the fraction of the segment's
# length that is covered. Returns a value in [0,1]. For X or Z boundary axes the segment runs
# along the other horizontal axis; for Y (horizontal slab) we fall back to area coverage on XZ.
function Get-BoundaryBlockerCoverage {
    param($Face, [object[]]$WallBlockers, [double]$WallMinHeight)

    # Vertical scoping: blocker must overlap the face's Y range. If the face is a horizontal slab
    # (Axis Y), we instead require blocker Y span to bracket the face plane.
    $yLo = $Face.YMin
    $yHi = $Face.YMax
    $yPad = 0.1  # accept blockers that almost reach the face

    $relevant = @($WallBlockers | Where-Object {
        ($_.TopY - $_.BottomY) -ge $WallMinHeight -and
        $_.TopY -ge ($yLo - $yPad) -and
        $_.BottomY -le ($yHi + $yPad)
    })

    if ($Face.Axis -eq 'X' -or $Face.Axis -eq 'Z') {
        # Segment runs along the *other* horizontal axis. Determine which.
        if ($Face.Axis -eq 'X') {
            $segLo = $Face.ZMin; $segHi = $Face.ZMax
        } else {
            $segLo = $Face.XMin; $segHi = $Face.XMax
        }
        $perpCoord = $Face.FacePlane
        $segLen = $segHi - $segLo
        if ($segLen -le 0) { return 0.0 }

        $intervals = New-Object System.Collections.Generic.List[pscustomobject]
        foreach ($b in $relevant) {
            if ($Face.Axis -eq 'X') {
                # Blocker must straddle the face plane in X.
                if ($b.Bounds2D.MinX -gt $perpCoord -or $b.Bounds2D.MaxX -lt $perpCoord) { continue }
                $bLo = [Math]::Max($segLo, $b.Bounds2D.MinZ)
                $bHi = [Math]::Min($segHi, $b.Bounds2D.MaxZ)
            } else {
                if ($b.Bounds2D.MinZ -gt $perpCoord -or $b.Bounds2D.MaxZ -lt $perpCoord) { continue }
                $bLo = [Math]::Max($segLo, $b.Bounds2D.MinX)
                $bHi = [Math]::Min($segHi, $b.Bounds2D.MaxX)
            }
            if ($bHi -gt $bLo) {
                $intervals.Add([pscustomobject]@{ Lo=$bLo; Hi=$bHi })
            }
        }

        if ($intervals.Count -eq 0) { return 0.0 }
        $sorted = $intervals | Sort-Object Lo
        $covered = 0.0
        $curLo = $sorted[0].Lo; $curHi = $sorted[0].Hi
        for ($i = 1; $i -lt $sorted.Count; $i++) {
            if ($sorted[$i].Lo -le $curHi) {
                if ($sorted[$i].Hi -gt $curHi) { $curHi = $sorted[$i].Hi }
            } else {
                $covered += ($curHi - $curLo)
                $curLo = $sorted[$i].Lo; $curHi = $sorted[$i].Hi
            }
        }
        $covered += ($curHi - $curLo)
        return [Math]::Min(1.0, $covered / $segLen)
    } else {
        # Horizontal slab. Treat coverage as XZ-area fraction. Rare for room boundaries; we
        # report the raw figure and let the caller decide.
        $area = ($Face.XMax - $Face.XMin) * ($Face.ZMax - $Face.ZMin)
        if ($area -le 0) { return 0.0 }
        $covered = 0.0
        foreach ($b in $relevant) {
            $lx = [Math]::Max($Face.XMin, $b.Bounds2D.MinX)
            $hx = [Math]::Min($Face.XMax, $b.Bounds2D.MaxX)
            $lz = [Math]::Max($Face.ZMin, $b.Bounds2D.MinZ)
            $hz = [Math]::Min($Face.ZMax, $b.Bounds2D.MaxZ)
            if ($hx -gt $lx -and $hz -gt $lz) { $covered += (($hx - $lx) * ($hz - $lz)) }
        }
        return [Math]::Min(1.0, $covered / $area)
    }
}

function Find-DoorOnBoundary {
    param(
        $Face,
        [object[]]$Doors,
        [double]$PerpDistance,
        [double]$FloorY,
        [double]$DoorMaxAboveFloor,
        [double]$DoorPadDown
    )
    # Door qualifies iff:
    #   1. Perpendicular distance from door pivot to face plane <= $PerpDistance.
    #   2. Door pivot lies within the face segment along the parallel axis.
    #   3. Door pivot Y is room-passable: within [FloorY - DoorPadDown, FloorY + DoorMaxAboveFloor].
    # The Y test rejects cupboard / fridge / cabinet doors that sit on the boundary wall but at
    # counter or wall-mount height.
    $yLo = $FloorY - $DoorPadDown
    $yHi = $FloorY + $DoorMaxAboveFloor
    foreach ($d in $Doors) {
        $px = $d.Position.x; $pz = $d.Position.z; $py = $d.Position.y
        if ($py -lt $yLo -or $py -gt $yHi) { continue }

        if ($Face.Axis -eq 'X') {
            $perp = [Math]::Abs($px - $Face.FacePlane)
            if ($perp -gt $PerpDistance) { continue }
            if ($pz -lt $Face.ZMin -or $pz -gt $Face.ZMax) { continue }
            return $d.Name
        } elseif ($Face.Axis -eq 'Z') {
            $perp = [Math]::Abs($pz - $Face.FacePlane)
            if ($perp -gt $PerpDistance) { continue }
            if ($px -lt $Face.XMin -or $px -gt $Face.XMax) { continue }
            return $d.Name
        } else {
            # Horizontal slab (floor/ceiling boundary) — door must sit within XZ rect and near
            # the Y plane.
            if ($px -lt $Face.XMin -or $px -gt $Face.XMax) { continue }
            if ($pz -lt $Face.ZMin -or $pz -gt $Face.ZMax) { continue }
            if ([Math]::Abs($py - $Face.FacePlane) -gt $PerpDistance) { continue }
            return $d.Name
        }
    }
    return $null
}

function Get-ConnectedComponents {
    param([string[]]$Nodes, [hashtable]$AdjacencySet)
    $visited = @{}
    $components = New-Object System.Collections.Generic.List[object]
    foreach ($n in $Nodes) {
        if ($visited.ContainsKey($n)) { continue }
        $stack = New-Object System.Collections.Generic.Stack[string]
        $stack.Push($n)
        $comp = New-Object System.Collections.Generic.List[string]
        while ($stack.Count -gt 0) {
            $cur = $stack.Pop()
            if ($visited.ContainsKey($cur)) { continue }
            $visited[$cur] = $true
            $comp.Add($cur)
            if ($AdjacencySet.ContainsKey($cur)) {
                $nbrs = $AdjacencySet[$cur]
                if ($null -ne $nbrs) {
                    foreach ($nb in $nbrs) {
                        if (-not $visited.ContainsKey($nb)) { $stack.Push($nb) }
                    }
                }
            }
        }
        $components.Add($comp.ToArray())
    }
    # Comma prevents PowerShell from unrolling the List on return — otherwise a 1-element list
    # containing a 3-element string[] gets re-emitted as 3 separate components at the call site.
    return ,$components
}

Write-Host "Loading navigation data..."
$nav = Get-Content $NavigationDataPath -Raw | ConvertFrom-Json
$blockerData = Get-Content $BlockersPath -Raw | ConvertFrom-Json

# Filter to volumetric camera spaces only — zero-scale anchors carry no boundary geometry and
# are handled by the suffix-subzone audit (Step 2), not here. Zones with one zero horizontal
# extent (e.g. office9 has Scale.x=0) are degenerate slabs/lines, not floors, and would
# fragment their group spuriously — drop them too.
$volumetric = @($nav.CameraSpaces | Where-Object { $_.Scale.x -ne 0 -and $_.Scale.z -ne 0 })
Write-Host "Volumetric zones: $($volumetric.Count) of $($nav.CameraSpaces.Count) total camera spaces."

# Wall-like blocker set: tall AND thin. Height alone catches armchairs, bookshelves, and other
# large furniture (an armchair with blankets on it is ~3m tall in this scene). A real wall is
# long-thin: its smaller XZ dimension is much less than its longer one. Filter by both axes.
$wallBlockers = @($blockerData.NavigationBlockers | Where-Object {
    if (($_.TopY - $_.BottomY) -lt $WallMinHeight) { return $false }
    $w = $_.Bounds2D.Width
    $d = $_.Bounds2D.Depth
    $thin = [Math]::Min($w, $d)
    $long = [Math]::Max($w, $d)
    # Wall-like: thin dimension <= 1.5m (matches the exporter's own WallMaxThinDimension) and
    # the long dimension covers a meaningful span. Aspect ratio caps furniture-shaped colliders
    # that happen to be tall.
    return $thin -le 1.5 -and $long -ge 1.5
})
Write-Host "Wall-like blockers (tall + thin): $($wallBlockers.Count) of $($blockerData.NavigationBlockers.Count)."

# DoorObjects in the export includes 277 entries — cameras, TRS transform nodes, and other
# door-named scenery. Only entries with a non-null DoorComponent are real interactable doors
# (the blocker export reports 30 of these in this scene). Restrict the boundary-gating set to
# real doors so a "Camera_BedroomDoor" doesn't accidentally gate a wall.
$doors = @($nav.DoorObjects | Where-Object { $null -ne $_.Position -and $null -ne $_.DoorComponent })
Write-Host "Door objects (real, DoorComponent != null): $($doors.Count) of $($nav.DoorObjects.Count)."

# Classify doors by name pattern as a sanity check against the geometric floor-Y filter.
# Doors named "Doors_*" follow the game's "room-passable door" naming convention; the SM_*,
# Cabinet_*, BreakerBox_*, ShowerDoor, BackDoorPivot etc. are furniture. We don't use the names
# to filter — the geometric test is the source of truth — but mismatches between the two are
# worth reporting so we can audit the geometric filter against named ground truth.
$namedPassable = @($doors | Where-Object { $_.Name -match '^Doors_' })
Write-Host "Doors with passable naming convention (Doors_*): $($namedPassable.Count)."

# Build primary-zone set from InNavigationGraph flag — the same authoritative source the
# runtime uses (SimpleNavSceneData.cs ~L397).
$primarySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($cs in $nav.CameraSpaces) {
    if ($cs.InNavigationGraph) { [void]$primarySet.Add($cs.Name) }
}
Write-Host "Primary zones (InNavigationGraph=true): $($primarySet.Count)."

# Group volumetric subzones by their resolved primary.
$groupsRaw = @{}
foreach ($cs in $volumetric) {
    $key = Get-SubzoneGroupKey $cs.Name $primarySet
    if (-not $groupsRaw.ContainsKey($key)) { $groupsRaw[$key] = New-Object System.Collections.Generic.List[object] }
    [void]$groupsRaw[$key].Add($cs)
}

$groupResults = New-Object System.Collections.Generic.List[object]
$doorsUsed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($key in ($groupsRaw.Keys | Sort-Object)) {
    $members = $groupsRaw[$key].ToArray()
    if ($members.Count -lt 2) {
        $singleNames = New-Object System.Collections.Generic.List[string]
        foreach ($m in $members) { $singleNames.Add([string]$m.Name) }
        $groupResults.Add([pscustomobject]@{
            Primary = $key
            Members = $singleNames.ToArray()
            Decision = 'singleton'
            Confidence = 1.0
            Clusters = @(,$singleNames.ToArray())
            InternalEdges = @()
            Evidence = @()
        })
        continue
    }

    $aabbs = @{}
    foreach ($m in $members) { $aabbs[$m.Name] = Get-Aabb $m }

    # Pairwise adjacency + boundary analysis.
    $adjacency = @{}
    foreach ($m in $members) { $adjacency[$m.Name] = New-Object System.Collections.Generic.List[string] }
    $evidence = New-Object System.Collections.Generic.List[object]
    $internalEdges = New-Object System.Collections.Generic.List[object]

    for ($i = 0; $i -lt $members.Count; $i++) {
        for ($j = $i + 1; $j -lt $members.Count; $j++) {
            $a = $members[$i]; $b = $members[$j]
            $face = Test-AabbAdjacency $aabbs[$a.Name] $aabbs[$b.Name] $AdjacencyTolerance $AdjacencyOverlap
            if ($null -eq $face) { continue }

            $coverage = Get-BoundaryBlockerCoverage $face $wallBlockers $WallMinHeight
            # Boundary floor Y = lower of the two zone-AABB floors. A door is "room-passable"
            # only if its pivot is near floor level; the cutoff is relative to this floor Y.
            $boundaryFloorY = [Math]::Min($aabbs[$a.Name].MinY, $aabbs[$b.Name].MinY)
            $door = Find-DoorOnBoundary $face $doors $DoorBoundaryDistance $boundaryFloorY $DoorMaxAboveFloor $DoorPadDown
            if ($null -ne $door) { [void]$doorsUsed.Add($door) }

            $segLen = 0.0
            if ($face.Axis -eq 'X') { $segLen = $face.ZMax - $face.ZMin }
            elseif ($face.Axis -eq 'Z') { $segLen = $face.XMax - $face.XMin }

            $classification = 'open'
            if ($null -ne $door) { $classification = 'door-gated' }
            elseif ($coverage -ge $BoundaryBlockedFraction) { $classification = 'walled' }

            $evidence.Add([pscustomobject]@{
                Pair = @($a.Name, $b.Name)
                BoundaryAxis = $face.Axis
                SegmentLength = [Math]::Round($segLen, 3)
                Separation = [Math]::Round($face.Separation, 3)
                BlockerCoverage = [Math]::Round($coverage, 3)
                Door = $door
                Classification = $classification
            })

            if ($classification -eq 'open') {
                [void]$adjacency[$a.Name].Add($b.Name)
                [void]$adjacency[$b.Name].Add($a.Name)
            } else {
                # Door-gated and walled boundaries become internal edges (keep-split); walled
                # edges with no door are unusual and worth flagging in the summary.
                $internalEdges.Add([pscustomobject]@{
                    From = $a.Name
                    To = $b.Name
                    Classification = $classification
                    Door = $door
                    SegmentLength = [Math]::Round($segLen, 3)
                    BlockerCoverage = [Math]::Round($coverage, 3)
                })
            }
        }
    }

    # Connected components over "open" adjacencies define merge clusters.
    $nodeNames = New-Object System.Collections.Generic.List[string]
    foreach ($m in $members) { $nodeNames.Add([string]$m.Name) }
    $components = Get-ConnectedComponents -Nodes $nodeNames.ToArray() -AdjacencySet $adjacency

    # Add edges between *different* components that arose from blocked/door boundaries already
    # captured above; isolated singleton components (no edges at all) flag as 'orphan' for review.
    $componentOf = @{}
    for ($c = 0; $c -lt $components.Count; $c++) {
        foreach ($n in $components[$c]) { $componentOf[$n] = $c }
    }

    # Decision.
    $decision = 'mixed'
    $confidence = 0.7
    if ($components.Count -eq 1 -and $internalEdges.Count -eq 0) {
        $decision = 'merge'; $confidence = 0.95
    } elseif ($components.Count -eq $members.Count -and $internalEdges.Count -eq 0) {
        $decision = 'disjoint'; $confidence = 0.4  # no adjacencies at all — suspicious for one primary stem
    } elseif ($components.Count -ge 2 -and $internalEdges.Count -gt 0) {
        $decision = 'keep-split'; $confidence = 0.85
    } elseif ($components.Count -eq 1 -and $internalEdges.Count -gt 0) {
        # Single connected component via open edges, but blocked/door edges also exist — likely
        # a closet or alcove off the main room.
        $decision = 'merge-with-internal-doors'; $confidence = 0.8
    }

    # Confidence damper: any walled-without-door edge is unusual; bump toward review.
    $oddEdges = @($internalEdges | Where-Object { $_.Classification -eq 'walled' })
    if ($oddEdges.Count -gt 0) { $confidence = [Math]::Min($confidence, 0.6) }

    $clusters = New-Object System.Collections.Generic.List[object]
    foreach ($c in $components) { $clusters.Add(@($c)) }

    $memberNames = New-Object System.Collections.Generic.List[string]
    foreach ($m in $members) { $memberNames.Add([string]$m.Name) }

    $groupResults.Add([pscustomobject]@{
        Primary = $key
        Members = $memberNames.ToArray()
        Decision = $decision
        Confidence = [Math]::Round($confidence, 2)
        Clusters = $clusters.ToArray()
        InternalEdges = $internalEdges.ToArray()
        Evidence = $evidence.ToArray()
    })
}

# Write JSON artifact.
$payload = [ordered]@{
    GeneratedAt = (Get-Date).ToString("o")
    Inputs = [ordered]@{
        NavigationData = $NavigationDataPath
        Blockers = $BlockersPath
    }
    Thresholds = [ordered]@{
        AdjacencyTolerance = $AdjacencyTolerance
        AdjacencyOverlap = $AdjacencyOverlap
        WallMinHeight = $WallMinHeight
        BoundaryBlockedFraction = $BoundaryBlockedFraction
        DoorBoundaryDistance = $DoorBoundaryDistance
    }
    Counts = [ordered]@{
        VolumetricZones = $volumetric.Count
        WallLikeBlockers = $wallBlockers.Count
        Doors = $doors.Count
        Groups = $groupResults.Count
    }
    Groups = $groupResults.ToArray()
}

$outDir = Split-Path -Parent $OutputJsonPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$payload | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputJsonPath -Encoding UTF8
Write-Host "Wrote $OutputJsonPath"

# Write summary.
$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add("Subzone audit — generated $(Get-Date -Format 'u')")
$summaryLines.Add("")
$summaryLines.Add("Thresholds: adj-tol=$AdjacencyTolerance adj-overlap=$AdjacencyOverlap wall-min-h=$WallMinHeight blocked-frac=$BoundaryBlockedFraction door-dist=$DoorBoundaryDistance")
$summaryLines.Add("Volumetric zones: $($volumetric.Count) ; wall-like blockers: $($wallBlockers.Count) ; doors: $($doors.Count)")
$summaryLines.Add("")
$decisionGroups = $groupResults | Group-Object Decision | Sort-Object Name
$summaryLines.Add("Decisions:")
foreach ($g in $decisionGroups) {
    $summaryLines.Add(("  {0,-30} {1}" -f $g.Name, $g.Count))
}
$summaryLines.Add("")
foreach ($g in ($groupResults | Sort-Object Primary)) {
    if ($g.Decision -eq 'singleton') { continue }
    $line = "  {0,-22} {1,-26} conf={2}  members={3}  clusters={4}  internal-edges={5}" -f `
        $g.Primary, $g.Decision, $g.Confidence, $g.Members.Count, $g.Clusters.Count, $g.InternalEdges.Count
    $summaryLines.Add($line)
    foreach ($e in $g.InternalEdges) {
        $summaryLines.Add(("      edge {0} <-> {1}  [{2}]  door={3}  cover={4}" -f $e.From, $e.To, $e.Classification, ($e.Door ?? '-'), $e.BlockerCoverage))
    }
}
$summaryLines.Add("")
$summaryLines.Add("Passable-named doors (Doors_*) not attached to any audited boundary:")
$summaryLines.Add("  (These are inter-room doors; this audit only inspects intra-primary boundaries.")
$summaryLines.Add("  The list belongs to a later inter-primary pass — see Step 7 of the unification plan.)")
foreach ($d in ($namedPassable | Sort-Object Name)) {
    if (-not $doorsUsed.Contains($d.Name)) {
        $summaryLines.Add(("  {0,-40} y={1,6:N2} pos=({2,7:N2},{3,7:N2})" -f $d.Name, $d.Position.y, $d.Position.x, $d.Position.z))
    }
}
$summaryLines.Add("")
$summaryLines.Add("Flagged (low confidence or 'disjoint'):")
foreach ($g in ($groupResults | Where-Object { $_.Decision -ne 'singleton' -and ($_.Confidence -lt 0.7 -or $_.Decision -eq 'disjoint') } | Sort-Object Primary)) {
    $summaryLines.Add(("  {0,-22} {1,-26} conf={2}" -f $g.Primary, $g.Decision, $g.Confidence))
}

$summaryLines | Set-Content -Path $OutputSummaryPath -Encoding UTF8
Write-Host "Wrote $OutputSummaryPath"
