"""Step O2 of [[project-navigation-object-first-plan]]: rasterize per-floor navigable region.

For each named floor band (ground, upper):
  1. Pick representative floor Y from the walkable export (area-weighted flat/step-up peaks).
  2. Rasterize at 0.2m cells across XZ extent of the floor's walkable footprint.
  3. Cell is walkable iff a walkable surface (VExt <= 1m slab) within the floor band covers it.
  4. Cell is blocked iff the player capsule cannot stand there:
     primitive colliders use their 2D bounds; mesh colliders use exported
     player-height triangle-slice segments when available.
  5. Dilate blocked region by capsule radius (0.4m / 2 cells at 0.2m).
  6. Navigable = walkable AND NOT dilated-blocked.
Emits one bitmap per floor + debug PNG.

Crawlspace floor is missing from the walkable export (no slab at Y≈-9.6); skipped here, follow-up.

Run from repo root:
  python scripts/bake_navigable_region.py

This script also runs the O3 inter-floor derivation post-pass before exiting,
so future bake regenerations do not silently drop stair / teleporter edges.
"""
from __future__ import annotations
import importlib.util
import json, math
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
WALK = REPO / "artifacts/navigation/thirdpersongreybox-walkable.json"
BLOCK = REPO / "artifacts/navigation/thirdpersongreybox-blockers.json"
INTER = REPO / "artifacts/navigation/thirdpersongreybox-interactables.json"
NAVDATA = REPO / "artifacts/navigation/thirdpersongreybox-navigation-data.json"
OUT_JSON = REPO / "artifacts/navigation/navigable_region.bake.json"
OUT_PNG_DIR = REPO / "artifacts/navigation"

# Player local CapsuleCollider radius is 0.4, but the exported Player object
# has 2x world scale and runtime proof showed collision at ~0.44m from the
# fireplace. Use a conservative standing-clearance radius that catches those
# near-grazes without immediately doubling every doorway clearance.
CAPSULE_R = 0.50
CAPSULE_H = 2.50
STEP_UP_TOL = 0.25
CELL = 0.20  # rasterization resolution
DILATE_CELLS = int(math.ceil(CAPSULE_R / CELL))  # 2 cells
# Surface vertical extent above which we treat the surface as a column/prop
# (not a floor slab). Lets SM_Ceiling_* slabs through while keeping lightbulbs,
# daemons, and plant pots out. Tall props that pass blocker selection re-block
# themselves; this gate only filters the walkable side.
MAX_FLOOR_SLAB_EXTENT = 1.0
# Door-position carve radius (meters). Several wall meshes have asymmetric
# doorway cuts -- the opening is only modeled on one face of the wall, so
# dilation re-seals the opening. Doors are first-class passages in the planner
# model; carve a disc at each Doors_* interactable position to guarantee the
# bake reflects that. 0.4m matches the capsule radius -- minimum needed to let
# the capsule through. Smallest authored doorway clearance is ~1.14m, so a
# 0.8m-diameter disc fits even the narrowest door.
DOOR_CARVE_RADIUS = 0.40
# Wider carve for real Door components exported from scene navigation data. This
# repairs doorway component splits caused by wall/doorframe dilation without
# widening every name-only door-like object.
DOOR_COMPONENT_CARVE_RADIUS = 1.50

# Floor bands: (label, target_Y, Y_tolerance_for_walkable_inclusion)
# Tolerance is ± around target_Y for which walkable TopY values count as "on this floor".
FLOORS = [
    {"label": "ground", "y": -0.50, "y_tol": 1.25},
    {"label": "upper",  "y": 12.50, "y_tol": 1.25},
]

# Scene bounds clip — exclude far skybox-stage surfaces
SCENE_MAX_ABS = 200.0


def in_scene(x, z):
    return abs(x) < SCENE_MAX_ABS and abs(z) < SCENE_MAX_ABS


def _rasterize_segment(blocked_bm, ax, az, bx, bz, minx, minz, nx, nz, cell):
    """Mark every cell that segment (A,B) crosses. Supercover variant of
    Bresenham — guarantees no diagonal pass-throughs that would let a
    rasterized wall leak."""
    # Convert to cell coordinates.
    fx0 = (ax - minx) / cell
    fz0 = (az - minz) / cell
    fx1 = (bx - minx) / cell
    fz1 = (bz - minz) / cell
    dx = abs(fx1 - fx0)
    dz = abs(fz1 - fz0)
    ix = int(math.floor(fx0))
    iz = int(math.floor(fz0))
    n = 1
    if dx == 0:
        x_inc = 0
        t_next_x = math.inf
    elif fx1 > fx0:
        x_inc = 1
        n += int(math.floor(fx1)) - ix
        t_next_x = (math.floor(fx0) + 1 - fx0) / dx
    else:
        x_inc = -1
        n += ix - int(math.floor(fx1))
        t_next_x = (fx0 - math.floor(fx0)) / dx
    if dz == 0:
        z_inc = 0
        t_next_z = math.inf
    elif fz1 > fz0:
        z_inc = 1
        n += int(math.floor(fz1)) - iz
        t_next_z = (math.floor(fz0) + 1 - fz0) / dz
    else:
        z_inc = -1
        n += iz - int(math.floor(fz1))
        t_next_z = (fz0 - math.floor(fz0)) / dz
    dt_x = (1.0 / dx) if dx > 0 else math.inf
    dt_z = (1.0 / dz) if dz > 0 else math.inf
    for _ in range(n):
        if 0 <= ix < nx and 0 <= iz < nz:
            blocked_bm[ix][iz] = True
        if t_next_x < t_next_z:
            ix += x_inc
            t_next_x += dt_x
        else:
            iz += z_inc
            t_next_z += dt_z


def _rasterize_bounds(blocked_bm, bb, minx, minz, nx, nz, cell):
    ix0 = max(0, int(math.floor((bb["MinX"] - minx) / cell)))
    ix1 = min(nx, int(math.ceil((bb["MaxX"] - minx) / cell)))
    iz0 = max(0, int(math.floor((bb["MinZ"] - minz) / cell)))
    iz1 = min(nz, int(math.ceil((bb["MaxZ"] - minz) / cell)))
    if ix0 >= ix1 or iz0 >= iz1:
        return False
    for ix in range(ix0, ix1):
        row = blocked_bm[ix]
        for iz in range(iz0, iz1):
            row[iz] = True
    return True


def _is_structural_mesh(record):
    text = f"{record.get('Name', '')} {record.get('Path', '')}".lower()
    structural_markers = (
        "/walls/",
        "/wall/",
        "/doors/",
        "sm_walls",
        "sm_wall",
        "sm_doorframe",
        "doorframe",
        "fence",
        "exterior",
    )
    return any(marker in text for marker in structural_markers)


def _rasterize_closed_segment_regions(blocked_bm, segments, minx, minz, nx, nz, cell):
    """Fill areas enclosed by a mesh's slice segments.

    Segment traces alone represent only the collider surface. For solid furniture
    with closed slice loops, the interior must be blocked too or the planner can
    thread a path through the object.
    """
    if not segments:
        return 0

    sx0 = min(min(s["AX"], s["BX"]) for s in segments)
    sx1 = max(max(s["AX"], s["BX"]) for s in segments)
    sz0 = min(min(s["AZ"], s["BZ"]) for s in segments)
    sz1 = max(max(s["AZ"], s["BZ"]) for s in segments)
    ix0 = max(0, int(math.floor((sx0 - minx) / cell)) - 2)
    ix1 = min(nx, int(math.ceil((sx1 - minx) / cell)) + 3)
    iz0 = max(0, int(math.floor((sz0 - minz) / cell)) - 2)
    iz1 = min(nz, int(math.ceil((sz1 - minz) / cell)) + 3)
    lx = ix1 - ix0
    lz = iz1 - iz0
    if lx <= 2 or lz <= 2:
        return 0

    local = [[False] * lz for _ in range(lx)]
    local_minx = minx + ix0 * cell
    local_minz = minz + iz0 * cell
    for s in segments:
        _rasterize_segment(
            local,
            s["AX"], s["AZ"],
            s["BX"], s["BZ"],
            local_minx, local_minz, lx, lz, cell,
        )

    outside = [[False] * lz for _ in range(lx)]
    queue = []
    for ix in range(lx):
        for iz in (0, lz - 1):
            if not local[ix][iz] and not outside[ix][iz]:
                outside[ix][iz] = True
                queue.append((ix, iz))
    for iz in range(lz):
        for ix in (0, lx - 1):
            if not local[ix][iz] and not outside[ix][iz]:
                outside[ix][iz] = True
                queue.append((ix, iz))

    head = 0
    while head < len(queue):
        ix, iz = queue[head]
        head += 1
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            jx = ix + dx
            jz = iz + dz
            if jx < 0 or jx >= lx or jz < 0 or jz >= lz:
                continue
            if local[jx][jz] or outside[jx][jz]:
                continue
            outside[jx][jz] = True
            queue.append((jx, jz))

    filled = 0
    for ix in range(lx):
        for iz in range(lz):
            if local[ix][iz] or outside[ix][iz]:
                continue
            gx = ix0 + ix
            gz = iz0 + iz
            if not blocked_bm[gx][gz]:
                filled += 1
            blocked_bm[gx][gz] = True
    return filled


FOOTPRINT_PERIMETER_MAX_DIM_M = 4.0


def _rasterize_footprint_perimeter(blocked_bm, record, minx, minz, nx, nz, cell):
    fp = record.get("Footprint") or {}
    vertices = fp.get("Vertices") or []
    if len(vertices) < 2:
        return 0

    xs = [v.get("x") for v in vertices if v.get("x") is not None]
    zs = [v.get("z") for v in vertices if v.get("z") is not None]
    if xs and zs:
        width = max(xs) - min(xs)
        depth = max(zs) - min(zs)
        # Footprint perimeter tracing assumes the vertices describe a
        # furniture-sized convex hull. Multi-meter hulls (kitchen counter run,
        # fireplace, monitor) trace phantom walls across walkable space, so
        # skip them and rely on the actual slice segments instead.
        if max(width, depth) > FOOTPRINT_PERIMETER_MAX_DIM_M:
            return 0

    hits_before = 0
    for ix in range(nx):
        hits_before += sum(1 for blocked in blocked_bm[ix] if blocked)

    for i, a in enumerate(vertices):
        b = vertices[(i + 1) % len(vertices)]
        ax = a.get("x")
        az = a.get("z")
        bx = b.get("x")
        bz = b.get("z")
        if ax is None or az is None or bx is None or bz is None:
            continue
        if not (in_scene(ax, az) or in_scene(bx, bz)):
            continue
        _rasterize_segment(blocked_bm, ax, az, bx, bz, minx, minz, nx, nz, cell)

    hits_after = 0
    for ix in range(nx):
        hits_after += sum(1 for blocked in blocked_bm[ix] if blocked)
    return max(0, hits_after - hits_before)


def _is_solid_blocker(record):
    if record.get("IsTrigger"):
        return False
    if record.get("IsDoorConnector") or record.get("IsTeleporterConnector"):
        return False
    if record.get("Enabled") is False:
        return False
    if record.get("IsActive") is False:
        return False
    return True


def _segments_in_floor_band(mesh_record, y_lo, y_hi):
    fp = mesh_record.get("Footprint") or {}
    segs = fp.get("Segments") or []
    if not segs:
        return []
    result = []
    for segment in segs:
        py = segment.get("PlaneY")
        if py is not None and (py < y_lo or py > y_hi):
            continue
        ax = segment["AX"]; az = segment["AZ"]
        bx = segment["BX"]; bz = segment["BZ"]
        if not (in_scene(ax, az) or in_scene(bx, bz)):
            continue
        result.append(segment)
    return result


def _dilate_disc(bm, nx, nz, d):
    if d <= 0:
        return [row[:] for row in bm]
    out = [[False] * nz for _ in range(nx)]
    offsets = [(dx, dz) for dx in range(-d, d+1) for dz in range(-d, d+1)
               if dx*dx + dz*dz <= d*d]
    for ix in range(nx):
        for iz in range(nz):
            if not bm[ix][iz]:
                continue
            for dx, dz in offsets:
                jx = ix + dx
                if jx < 0 or jx >= nx: continue
                jz = iz + dz
                if jz < 0 or jz >= nz: continue
                out[jx][jz] = True
    return out


def _rasterize_segments_into(bm, segments, minx, minz, nx, nz, cell):
    for s in segments:
        _rasterize_segment(bm, s["AX"], s["AZ"], s["BX"], s["BZ"], minx, minz, nx, nz, cell)


def _door_panels_with_floor_segments(door_records, floor_y_lo, floor_y_hi):
    """Yield (door_record, panel) pairs where the panel has at least one closed
    or open segment whose PlaneY sits inside the floor band. PlaneY values come
    straight from the exporter's slice planes (0.5 and 13.5)."""
    for d in door_records:
        for p in d.get("Panels", []):
            def _band(segs):
                return [s for s in segs
                        if floor_y_lo <= s.get("PlaneY", -1e9) <= floor_y_hi]
            closed = _band(p.get("SegmentsClosed", []))
            open_sets = []
            for os in p.get("OpenSegmentSets", []):
                bs = _band(os.get("Segments", []))
                open_sets.append({"Tag": os.get("Tag"), "Segments": bs})
            if not closed and not any(s["Segments"] for s in open_sets):
                continue
            yield d, p, closed, open_sets


def bake_floor(floor, walkables, blockers, mesh_colliders, doors, door_records, state_walls):
    fy = floor["y"]
    ytol = floor["y_tol"]
    floor_walks = [
        w for w in walkables
        if in_scene(w["Footprint"]["CenterX"], w["Footprint"]["CenterZ"])
        and abs(w["TopY"] - fy) <= ytol
        and w["VerticalExtent"] <= MAX_FLOOR_SLAB_EXTENT
    ]
    if not floor_walks:
        return {"error": "no walkable surfaces", "floor": floor}

    # Floor XZ extents from walkable footprints
    minx = min(w["Footprint"]["MinX"] for w in floor_walks)
    maxx = max(w["Footprint"]["MaxX"] for w in floor_walks)
    minz = min(w["Footprint"]["MinZ"] for w in floor_walks)
    maxz = max(w["Footprint"]["MaxZ"] for w in floor_walks)
    # Pad by capsule radius so the grid covers dilated regions too
    pad = CAPSULE_R + CELL
    minx -= pad; maxx += pad; minz -= pad; maxz += pad

    nx = int(math.ceil((maxx - minx) / CELL))
    nz = int(math.ceil((maxz - minz) / CELL))

    def cell_center(ix, iz):
        return (minx + (ix + 0.5) * CELL, minz + (iz + 0.5) * CELL)

    walkable_bm = [[False] * nz for _ in range(nx)]
    # Rasterize walkable footprints (AABB-fill)
    for w in floor_walks:
        fp = w["Footprint"]
        ix0 = max(0, int(math.floor((fp["MinX"] - minx) / CELL)))
        ix1 = min(nx, int(math.ceil((fp["MaxX"] - minx) / CELL)))
        iz0 = max(0, int(math.floor((fp["MinZ"] - minz) / CELL)))
        iz1 = min(nz, int(math.ceil((fp["MaxZ"] - minz) / CELL)))
        for ix in range(ix0, ix1):
            for iz in range(iz0, iz1):
                walkable_bm[ix][iz] = True

    # Y range for blocker intersection
    y_lo = fy - STEP_UP_TOL
    y_hi = fy + CAPSULE_H

    blocked_bm = [[False] * nz for _ in range(nx)]
    blocker_hits = 0
    primitive_blocker_hits = 0
    mesh_bounds_fallback_hits = 0
    mesh_segment_blocker_hits = 0
    mesh_segments_rasterized = 0
    mesh_closed_region_blocker_hits = 0
    mesh_closed_region_cells = 0
    mesh_footprint_edge_blocker_hits = 0
    mesh_footprint_edge_cells = 0
    mesh_records_by_id = {
        m.get("ComponentId"): m
        for m in mesh_colliders
        if m.get("ComponentId") is not None
    }
    mesh_records_with_segments = set()

    # Mesh collider pass: this is the 2.5D capsule-clearance approximation.
    # Any active, enabled, non-trigger mesh collider that has player-height
    # triangle-slice segments contributes its actual surface traces, regardless
    # of whether it is a wall, fireplace, table, counter, bookshelf, etc.
    # Dilation below expands those traces by the player capsule radius.
    for m in mesh_colliders:
        if not _is_solid_blocker(m):
            continue
        if m["TopY"] < y_lo or m["BottomY"] > y_hi:
            continue
        segments = _segments_in_floor_band(m, y_lo, y_hi)
        if not segments:
            continue
        mesh_records_with_segments.add(m.get("ComponentId"))
        mesh_had_segment = False
        for s in segments:
            _rasterize_segment(
                blocked_bm,
                s["AX"], s["AZ"],
                s["BX"], s["BZ"],
                minx, minz, nx, nz, CELL,
            )
            mesh_segments_rasterized += 1
            mesh_had_segment = True
        if mesh_had_segment:
            mesh_segment_blocker_hits += 1
            if not _is_structural_mesh(m):
                edge_cells = _rasterize_footprint_perimeter(
                    blocked_bm,
                    m,
                    minx, minz, nx, nz, CELL,
                )
                if edge_cells > 0:
                    mesh_footprint_edge_blocker_hits += 1
                    mesh_footprint_edge_cells += edge_cells
                filled = _rasterize_closed_segment_regions(
                    blocked_bm,
                    segments,
                    minx, minz, nx, nz, CELL,
                )
                if filled > 0:
                    mesh_closed_region_blocker_hits += 1
                    mesh_closed_region_cells += filled

    # Door panels in CLOSED pose: rasterize as blockers. The regular mesh-
    # collider pass excludes door-connector meshes (IsDoorConnector filter)
    # because the legacy bake treated all doors as always-open. Now that we
    # track per-door open/closed state via freed-cells, the closed-pose panel
    # must contribute to the blocked bitmap — otherwise the doorway is always
    # passable in the bake and "freed when open" is meaningless. Both
    # MeshCollider- and MeshFilter-sourced panels go through this path; the
    # exporter's slicing is identical for both.
    for door_rec in door_records:
        for panel in door_rec.get("Panels", []):
            for s in panel.get("SegmentsClosed", []):
                py = s.get("PlaneY")
                if py is None or py < y_lo or py > y_hi:
                    continue
                _rasterize_segment(blocked_bm,
                                   s["AX"], s["AZ"], s["BX"], s["BZ"],
                                   minx, minz, nx, nz, CELL)

    for b in blockers:
        if not _is_solid_blocker(b): continue
        if b["TopY"] < y_lo or b["BottomY"] > y_hi: continue
        bb = b.get("Bounds2D")
        if not bb: continue
        if not in_scene((bb["MinX"]+bb["MaxX"])/2, (bb["MinZ"]+bb["MaxZ"])/2): continue

        # Mesh colliders with slice segments already used actual collider
        # surface traces above. Falling back to their AABB would reintroduce
        # the over-blocking that the capsule-clearance pass is replacing.
        if b.get("ColliderType") == "MeshCollider":
            mesh_record = mesh_records_by_id.get(b.get("ComponentId"))
            if mesh_record is not None and b.get("ComponentId") in mesh_records_with_segments:
                continue
            if _rasterize_bounds(blocked_bm, bb, minx, minz, nx, nz, CELL):
                blocker_hits += 1
                mesh_bounds_fallback_hits += 1
            continue

        if _rasterize_bounds(blocked_bm, bb, minx, minz, nx, nz, CELL):
            blocker_hits += 1
            primitive_blocker_hits += 1

    # Dilate blocked by capsule radius. Use Euclidean disc instead of
    # Chebyshev box: a 2-cell box gives 0.57m corner reach (sqrt(2)*0.4m)
    # and overshrinks doorway gaps for wall-segment rasterizations; the
    # Euclidean disc respects the actual capsule radius (0.4m) in all
    # directions, freeing diagonal corners and recovering 0.8m doorways.
    if DILATE_CELLS > 0:
        dilated = [[False] * nz for _ in range(nx)]
        d = DILATE_CELLS
        offsets = [(dx, dz) for dx in range(-d, d+1) for dz in range(-d, d+1)
                   if dx*dx + dz*dz <= d*d]
        for ix in range(nx):
            for iz in range(nz):
                if not blocked_bm[ix][iz]: continue
                for dx, dz in offsets:
                    jx = ix + dx
                    if jx < 0 or jx >= nx: continue
                    jz = iz + dz
                    if jz < 0 or jz >= nz: continue
                    dilated[jx][jz] = True
    else:
        dilated = blocked_bm

    # Door-position carve: undo dilation in a disc around each door on this
    # floor. Several wall meshes cut doorways on only one face; dilation
    # re-seals them.
    #
    # Doors that have per-door freed-cells exported (Panels[] with
    # SegmentsClosed/OpenSegmentSets) are skipped here -- their closed-pose
    # panel mesh is now a blocker, and consumers apply freed-cells when the
    # door opens. The carve still runs for doors WITHOUT per-door data
    # (older datable Doors_* interactables that lack a DoorComponent and
    # therefore have no panel mesh association in the exporter).
    doors_with_panel_data = set()
    for door_rec in door_records:
        name = door_rec.get("Name")
        if name and door_rec.get("Panels"):
            doors_with_panel_data.add(name)

    door_carves = 0
    for d in doors:
        if d.get("name") in doors_with_panel_data:
            continue
        dy = d["y"]
        if abs(dy - fy) > 2.0: continue  # different floor
        dx_w, dz_w = d["x"], d["z"]
        ix = int((dx_w - minx) / CELL)
        iz = int((dz_w - minz) / CELL)
        radius = d.get("radius", DOOR_CARVE_RADIUS)
        cr = int(math.ceil(radius / CELL))
        carve_offsets = [(dx, dz) for dx in range(-cr, cr+1) for dz in range(-cr, cr+1)
                         if dx*dx + dz*dz <= cr*cr]
        for dx, dz in carve_offsets:
            jx = ix + dx; jz = iz + dz
            if jx < 0 or jx >= nx or jz < 0 or jz >= nz: continue
            if dilated[jx][jz]:
                dilated[jx][jz] = False
                door_carves += 1

    # Navigable = walkable AND NOT dilated
    navigable_bm = [[walkable_bm[ix][iz] and not dilated[ix][iz]
                     for iz in range(nz)] for ix in range(nx)]

    # Per-door freed-cells pass. For each door, rasterize all its panels'
    # closed-pose floor segments into a per-door bitmap, dilate, and do the
    # same for the union of all open poses. The freed-cells set is:
    #     freed = panel_closed_dil AND NOT panel_open_dil_union
    #             AND walkable AND NOT (dilated AND NOT panel_closed_dil)
    # The last factor masks out cells that would still be blocked by something
    # OTHER than this door's panels — freeing the door doesn't help if a wall
    # also sits there. Consumers OR the freed cells into navigable_bm at
    # door-open time. BothWays hinges union both signed open poses (the door
    # is passable either way, so the consumer doesn't need to pick a side).
    doors_per_floor = []
    for door_rec in door_records:
        panel_closed_raw = [[False] * nz for _ in range(nx)]
        panel_open_raw = [[False] * nz for _ in range(nx)]
        has_closed_in_band = False
        has_open_in_band = False
        for panel in door_rec.get("Panels", []):
            for s in panel.get("SegmentsClosed", []):
                py = s.get("PlaneY")
                if py is None or py < y_lo or py > y_hi:
                    continue
                _rasterize_segment(panel_closed_raw,
                                   s["AX"], s["AZ"], s["BX"], s["BZ"],
                                   minx, minz, nx, nz, CELL)
                has_closed_in_band = True
            for os in panel.get("OpenSegmentSets", []):
                for s in os.get("Segments", []):
                    py = s.get("PlaneY")
                    if py is None or py < y_lo or py > y_hi:
                        continue
                    _rasterize_segment(panel_open_raw,
                                       s["AX"], s["AZ"], s["BX"], s["BZ"],
                                       minx, minz, nx, nz, CELL)
                    has_open_in_band = True
        if not has_closed_in_band and not has_open_in_band:
            continue

        panel_closed_dil = _dilate_disc(panel_closed_raw, nx, nz, DILATE_CELLS)
        panel_open_dil = _dilate_disc(panel_open_raw, nx, nz, DILATE_CELLS)

        # Doorway-threshold cells. The panel diff alone tells us where the
        # panel's mesh moved when opening. But the actual passable space when
        # the door swings open also includes the doorway threshold itself —
        # the gap between the surrounding wall meshes. After dilation by the
        # capsule radius, wall meshes re-seal that gap. The blanket carve was
        # solving that problem; we now apply a per-door carve gated on door
        # state. Cells within DOOR_COMPONENT_CARVE_RADIUS of the door's anchor
        # that are currently dilated-blocked AND walkable count as threshold
        # cells that opening this door unblocks.
        door_pos = door_rec.get("WorldPosition") or {}
        anchor_x = door_pos.get("x")
        anchor_z = door_pos.get("z")
        anchor_y = door_pos.get("y", fy)
        threshold_cells = []
        if (anchor_x is not None and anchor_z is not None
                and abs(anchor_y - fy) <= 2.0):
            cx = int((anchor_x - minx) / CELL)
            cz = int((anchor_z - minz) / CELL)
            radius_m = DOOR_COMPONENT_CARVE_RADIUS
            cr = int(math.ceil(radius_m / CELL))
            for dx in range(-cr, cr + 1):
                for dz in range(-cr, cr + 1):
                    if dx * dx + dz * dz > cr * cr:
                        continue
                    jx = cx + dx
                    jz = cz + dz
                    if jx < 0 or jx >= nx or jz < 0 or jz >= nz:
                        continue
                    if not walkable_bm[jx][jz]:
                        continue
                    if not dilated[jx][jz]:
                        continue  # already navigable, no help
                    threshold_cells.append((jx, jz))

        freed_set = set()
        for ix in range(nx):
            for iz in range(nz):
                if not panel_closed_dil[ix][iz]:
                    continue
                if panel_open_dil[ix][iz]:
                    continue
                if not walkable_bm[ix][iz]:
                    continue
                freed_set.add((ix, iz))
        for c in threshold_cells:
            freed_set.add(c)

        if not freed_set:
            continue
        freed = sorted([list(c) for c in freed_set])
        doors_per_floor.append({
            "name": door_rec.get("Name"),
            "kind": door_rec.get("Kind"),
            "component_id": door_rec.get("ComponentId"),
            "panel_count": len(door_rec.get("Panels", [])),
            "closed_cells": sum(sum(row) for row in panel_closed_dil),
            "open_cells": sum(sum(row) for row in panel_open_dil),
            "threshold_cells": len(threshold_cells),
            "freed_cells": freed,
            "freed_count": len(freed),
        })

    # Per-state-wall freed-cells pass. State-gated walls (currently just the
    # DresserWall) are active in the closed-pose bake by default. Consumers can
    # opt into the post-release state via the same overlay mechanism as doors.
    # Each state-wall's freed-cells = wall_dil ∩ walkable ∩ ¬(dilated ∩ ¬wall_dil)
    # — cells the wall covers in dilation that would be navigable absent the
    # wall (i.e. nothing else also blocks them).
    state_walls_per_floor = []
    for wall in state_walls or []:
        b2 = wall.get("Bounds2D") or {}
        if not b2:
            continue
        if wall.get("TopY") is None or wall.get("BottomY") is None:
            continue
        if wall["TopY"] < y_lo or wall["BottomY"] > y_hi:
            continue
        wall_raw = [[False] * nz for _ in range(nx)]
        if not _rasterize_bounds(wall_raw, b2, minx, minz, nx, nz, CELL):
            continue
        wall_dil = _dilate_disc(wall_raw, nx, nz, DILATE_CELLS)
        freed = []
        for ix in range(nx):
            for iz in range(nz):
                if not wall_dil[ix][iz]:
                    continue
                if not walkable_bm[ix][iz]:
                    continue
                # If dilated AND not in wall_dil, another blocker covers this
                # cell too — freeing the wall doesn't help.
                if dilated[ix][iz] and not wall_dil[ix][iz]:
                    continue
                freed.append([ix, iz])
        if not freed:
            continue
        state_walls_per_floor.append({
            "name": wall.get("Name"),
            "component_id": wall.get("ComponentId"),
            "release_mechanism": wall.get("ReleaseMechanism"),
            "release_condition": wall.get("ReleaseCondition"),
            "default_active": wall.get("DefaultActive", True),
            "wall_cells": sum(sum(row) for row in wall_dil),
            "freed_cells": freed,
            "freed_count": len(freed),
        })

    walk_count = sum(sum(row) for row in walkable_bm)
    block_count = sum(sum(row) for row in blocked_bm)
    dil_count = sum(sum(row) for row in dilated)
    nav_count = sum(sum(row) for row in navigable_bm)

    return {
        "label": floor["label"],
        "floor_y": fy,
        "frame": {
            "origin_x": minx, "origin_z": minz,
            "cell_size": CELL,
            "nx": nx, "nz": nz,
            "extent_x": [minx, maxx],
            "extent_z": [minz, maxz],
        },
        "walkable_surface_count": len(floor_walks),
        "blocker_hits": blocker_hits,
        "primitive_blocker_hits": primitive_blocker_hits,
        "mesh_bounds_fallback_hits": mesh_bounds_fallback_hits,
        "mesh_segment_blocker_hits": mesh_segment_blocker_hits,
        "mesh_segments_rasterized": mesh_segments_rasterized,
        "mesh_closed_region_blocker_hits": mesh_closed_region_blocker_hits,
        "mesh_closed_region_cells": mesh_closed_region_cells,
        "mesh_footprint_edge_blocker_hits": mesh_footprint_edge_blocker_hits,
        "mesh_footprint_edge_cells": mesh_footprint_edge_cells,
        # Legacy metric names retained for older diagnostics. These now mean
        # all mesh segment traces, not only path/name-classified walls.
        "wall_meshes_rasterized": mesh_segment_blocker_hits,
        "wall_segments_rasterized": mesh_segments_rasterized,
        "door_carves": door_carves,
        "doors": doors_per_floor,
        "state_walls": state_walls_per_floor,
        "cells": {
            "walkable": walk_count,
            "blocked_raw": block_count,
            "blocked_dilated": dil_count,
            "navigable": nav_count,
        },
        # Pack bitmap as one row-string per ix (chars '.', 'W', 'B', 'N')
        # '.' = void, 'W' = walkable-only (blocked), 'B' = blocker-only, 'N' = navigable
        "bitmap_rows": _pack(walkable_bm, dilated, navigable_bm, nx, nz),
    }


def _pack(walk, dil, nav, nx, nz):
    rows = []
    for ix in range(nx):
        chars = []
        for iz in range(nz):
            n = nav[ix][iz]
            w = walk[ix][iz]
            b = dil[ix][iz]
            if n: c = 'N'
            elif w and b: c = 'X'  # walkable but blocked
            elif w: c = 'W'        # walkable, somehow not navigable (shouldn't happen if not blocked)
            elif b: c = 'B'        # blocker outside walkable
            else: c = '.'
            chars.append(c)
        rows.append(''.join(chars))
    return rows


def write_png(floor_result, path):
    """Write a debug PPM (no deps). Renamed .png for convenience but PPM-formatted."""
    rows = floor_result["bitmap_rows"]
    nx = floor_result["frame"]["nx"]
    nz = floor_result["frame"]["nz"]
    # Render Z increasing upward (image row 0 = max Z)
    # Each pixel = one cell
    palette = {
        '.': (24, 24, 24),
        'W': (180, 180, 60),
        'X': (110, 50, 50),
        'B': (60, 60, 60),
        'N': (80, 200, 120),
    }
    with open(path, 'wb') as f:
        f.write(f"P6\n{nx} {nz}\n255\n".encode())
        for iz in range(nz - 1, -1, -1):
            line = bytearray()
            for ix in range(nx):
                c = rows[ix][iz]
                r, g, b = palette.get(c, (255, 0, 255))
                line.append(r); line.append(g); line.append(b)
            f.write(bytes(line))


def append_inter_floor_edges():
    """Run the O3 post-pass against the freshly-written bake."""
    script_path = Path(__file__).resolve().with_name("derive_inter_floor_edges.py")
    spec = importlib.util.spec_from_file_location("derive_inter_floor_edges", script_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load {script_path}")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.main()

    refreshed = json.load(open(OUT_JSON, encoding="utf-8"))
    if len(refreshed.get("floors", [])) > 1 and "inter_floor_edges" not in refreshed:
        raise RuntimeError("Bake has multiple floors but no inter_floor_edges after O3 derivation")


def main():
    walk = json.load(open(WALK, encoding="utf-8"))
    blok = json.load(open(BLOCK, encoding="utf-8"))
    walkables = walk["WalkableSurfaces"]
    blockers = blok["NavigationBlockers"]
    mesh_colliders = blok.get("MeshColliders", [])
    door_records = blok.get("Doors", [])
    state_walls = blok.get("StateWalls", [])

    # Doors from interactables. Each entry: {x, y, z, name}. Used to carve
    # navigability discs that survive wall-mesh asymmetric-cut artifacts.
    doors = []
    door_keys = set()
    if NAVDATA.exists():
        nav = json.load(open(NAVDATA, encoding="utf-8-sig"))
        for door in nav.get("DoorObjects", []):
            component = door.get("DoorComponent")
            if not component:
                continue

            name = door.get("Name") or ""
            if not name.startswith("Doors_"):
                continue

            pos = door.get("Position") or {}
            key = (name, round(pos.get("x", 0.0), 3), round(pos.get("y", 0.0), 3), round(pos.get("z", 0.0), 3))
            door_keys.add(key)
            doors.append({
                "name": name,
                "x": pos.get("x", 0.0),
                "y": pos.get("y", 0.0),
                "z": pos.get("z", 0.0),
                "radius": DOOR_COMPONENT_CARVE_RADIUS,
            })

    if INTER.exists():
        inter = json.load(open(INTER, encoding="utf-8"))
        recs = inter.get("Interactables") or inter.get("Records") or []
        for it in recs:
            name = it.get("GameObjectName") or it.get("Name") or ""
            if not name.startswith("Doors_"): continue
            pos = it.get("WorldPosition") or it.get("Position") or {}
            key = (name, round(pos.get("x", 0.0), 3), round(pos.get("y", 0.0), 3), round(pos.get("z", 0.0), 3))
            if key in door_keys:
                continue
            # Doors_* datable interactables driven by dorian_door.* ink scripts
            # are functionally doors even when they lack a DoorComponent (e.g.
            # the Bedroom/Gym closet doors). The player can always open them
            # from outside and they don't latch from inside, so for routing
            # they should be treated as passable. Use the full DoorComponent
            # carve radius -- the 0.4m default isn't wide enough to punch
            # through the dilated mesh-segment trace of the door panel.
            ink = (it.get("InkFileName") or "")
            is_dorian_door = ink.startswith("dorian_door.") or it.get("IsDatable")
            radius = DOOR_COMPONENT_CARVE_RADIUS if is_dorian_door else DOOR_CARVE_RADIUS
            doors.append({
                "name": name,
                "x": pos.get("x", 0.0),
                "y": pos.get("y", 0.0),
                "z": pos.get("z", 0.0),
                "radius": radius,
            })

    report = {
        "params": {
            "capsule_radius_m": CAPSULE_R,
            "capsule_height_m": CAPSULE_H,
            "step_up_tolerance_m": STEP_UP_TOL,
            "cell_size_m": CELL,
            "dilation_cells": DILATE_CELLS,
            "door_component_carve_radius_m": DOOR_COMPONENT_CARVE_RADIUS,
        },
        "floors": [],
    }
    for floor in FLOORS:
        print(f"Baking floor: {floor['label']} (Y={floor['y']})...")
        result = bake_floor(floor, walkables, blockers, mesh_colliders, doors, door_records, state_walls)
        report["floors"].append(result)
        if "error" in result:
            print(f"  ERROR: {result['error']}")
            continue
        c = result["cells"]
        f = result["frame"]
        print(f"  grid: {f['nx']}x{f['nz']} cells ({f['nx']*f['nz']} total)")
        print(f"  walkable={c['walkable']}  blocked_raw={c['blocked_raw']}  "
              f"blocked_dilated={c['blocked_dilated']}  navigable={c['navigable']}")
        print(f"  primitive_blockers={result['primitive_blocker_hits']}  "
              f"mesh_segment_blockers={result['mesh_segment_blocker_hits']}  "
              f"mesh_segments={result['mesh_segments_rasterized']}  "
              f"mesh_footprint_edges={result['mesh_footprint_edge_blocker_hits']}  "
              f"mesh_closed_regions={result['mesh_closed_region_blocker_hits']}  "
              f"mesh_bounds_fallback={result['mesh_bounds_fallback_hits']}  "
              f"door_carves={result['door_carves']}")
        png_path = OUT_PNG_DIR / f"navigable_region.{floor['label']}.ppm"
        write_png(result, png_path)
        print(f"  debug image: {png_path}")

    OUT_JSON.parent.mkdir(parents=True, exist_ok=True)
    OUT_JSON.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"\nWrote {OUT_JSON}")
    append_inter_floor_edges()


if __name__ == "__main__":
    main()
