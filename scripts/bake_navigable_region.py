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

# Player CapsuleCollider radius is 0.4m (Player.prefab; the exported value of
# 0.2 is multiplied by the 2x world scale). The earlier 0.50m setting was a
# safety margin in response to a runtime graze against the fireplace at
# ~0.44m, but it over-sealed narrow corridors — notably the hallway between
# z=5.7 and z=6.9 (1.2m wide), where 0.5m dilation leaves <1 cell of clearance
# and breaks the office→front-door route entirely. 0.4m leaves 0.4m / 2 cells
# of clearance — passable by the actual player capsule. If fireplace-style
# grazes return, address them with per-mesh inflation rather than a global
# bump that closes legitimate doorways.
CAPSULE_R = 0.40
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
        # The /House/Hallway/Stairs mesh: its ground-band (Y=0.5) slice segments
        # are the bottom-landing side walls, which must rasterize as wall traces
        # only. Routing it through the furniture path (convex-hull perimeter +
        # closed-region fill) would over-block, since the hull spans the full
        # 21m stair run. See [[project-navigation-stairs-runtime-collision]].
        "/hallway/stairs",
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


# Open-archway carve. Doorframe meshes (SM_Doorframe_*, Door_frame_*) are thin
# walls with an opening; the exporter flags them IsWallLikeFatVictim and routes
# them to segment-trace rasterization. But the frame's footprint is a CLOSED
# loop — its threshold/sill and lintel cross-pieces span the opening width and
# seal the doorway line at floor level, so a narrow archway (e.g.
# SM_Doorframe_Small_13, 1.23m throat) gets walled off, isolating whole rooms.
#
# Real doors are repaired by the door-position carve (Doors_* interactables) or
# the per-door freed-cells state machine (panel-based door_records). But ~18
# frames in this scene are open archways with NO associated door, so nothing
# opens them. For those, carve the frame's footprint bbox clear of dilation
# (bounded to the bbox + a small margin so it can't leak past the jambs) in the
# door-carve pass below. See [[project-navigation-upper-hall2-archway-seal]],
# [[project-navigation-bake-doorframe-gap-outcome]].
def _is_doorframe(record):
    text = f"{record.get('Name', '')} {record.get('GameObjectName', '')} {record.get('Path', '')}".lower()
    return "doorframe" in text or "door_frame" in text


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
    # Bounds-blowup meshes: MovingDateable and wiggle_MODEL_UPDATE_ORIGIN
    # parents animate their children between several poses. The exporter
    # records the world-space union of all poses, producing phantom blocker
    # geometry that can seal whole rooms:
    #
    # - ComputerMovingDateable / Monitor: combined 18m × 19m × 17m bounds
    #   sealed the Bathroom 1 doorway (local extent only 3.3m).
    # - towel1_2_sink_bathroom2_normal_wiggle_MODEL_UPDATE_ORIGIN /
    #   SM_Sink_Bathroom_2: combined 2.8 × 7.1m bounds sealed the Bathroom 2
    #   sink area from the main bathroom (local extent 2.4 × 1.2m).
    #
    # Most wiggle meshes are small props (mugs, plates) that don't bound-blow-
    # up. Only skip when the world XZ is materially larger than the local
    # mesh's XZ — measured against the largest two LocalAabbExtent axes (the
    # mesh's local Y can map to any world axis after rotation).
    #
    # Always skip MovingDateable (only 2 records in this scene, both
    # bound-blow-up). For wiggle paths, require the inflation ratio gate.
    #
    # See [[project-navigation-bounds-blowup-meshes]].
    path = record.get("Path") or ""
    if "MovingDateable" in path:
        return False
    if "wiggle" in path:
        ls = record.get("LocalShape") or {}
        le = ls.get("LocalAabbExtent") or {}
        bb = record.get("Bounds2D") or {}
        if le and bb:
            world_x = bb["MaxX"] - bb["MinX"]
            world_z = bb["MaxZ"] - bb["MinZ"]
            # Local extents are half-extents; double them. Take the two
            # largest axes since local rotation can re-orient them.
            extents = sorted([
                abs(le.get("x", 0)) * 2,
                abs(le.get("y", 0)) * 2,
                abs(le.get("z", 0)) * 2,
            ], reverse=True)
            local_xz_max = max(extents[0], 0.01)
            # If either world dimension exceeds local_xz_max by 2x or more,
            # this mesh is bounds-blown. Skip it as a blocker.
            if world_x / local_xz_max > 2.0 or world_z / local_xz_max > 2.0:
                return False

    # Ceiling void-plug colliders (e.g. CeilingStairsFix1/2 under
    # House/MultiRoom/Ceilings/). These are tall, floor-to-ceiling boxes the
    # game authored to plug the open stairwell void in the ceiling so the
    # player can't fall through from the attic side. Their body is centered
    # near the ceiling (~Y18), but their bottom face dips to ~12.38 — a hair
    # below the upper floor (12.5) — so they get admitted to the upper bake
    # band and rasterize as an 11.6m wall sealing the stair landing from the
    # upstairs hall (the entire upper floor isolates from the stairs).
    #
    # Discriminator (all three required, to avoid freeing real geometry):
    #   1. parented under a /Ceilings/ node — authored ceiling fixup, not a
    #      wall or furniture collider;
    #   2. a sliver footprint — one XZ axis <= 0.1m (CeilingStairsFix1 is
    #      0.04m deep, Fix2 is 0.02m wide). Real walls and furniture have
    #      substantial footprints on both axes. NOTE the */Fix suffix alone
    #      is NOT safe: TreadmillColliderFix is a real 8.5x2.2m equipment
    #      collider that must keep blocking.
    #   3. a tall body (>= 3m) — it spans floor to ceiling, confirming it's a
    #      vertical void-plug rather than a low lip.
    # See [[project-navigation-upper-hall2-archway-seal]].
    if "/ceilings/" in path.lower():
        bb = record.get("Bounds2D") or {}
        by = record.get("BottomY")
        ty = record.get("TopY")
        if bb and by is not None and ty is not None:
            width = bb.get("Width", bb.get("MaxX", 0) - bb.get("MinX", 0))
            depth = bb.get("Depth", bb.get("MaxZ", 0) - bb.get("MinZ", 0))
            if min(width, depth) <= 0.1 and (ty - by) >= 3.0:
                return False
    return True


MIN_BORROW_HEIGHT_M = 0.75  # ~ a player's hip; below this, walk over it


def _segments_in_floor_band(mesh_record, y_lo, y_hi):
    fp = mesh_record.get("Footprint") or {}
    segs = fp.get("Segments") or []
    if not segs:
        return []

    # Top-lip gate. Several ground-floor walls (SM_Walls_Hall1/Kitchen/Living/
    # Office/Dining/Laundry/Closet_Office) have TopY ~12.54-12.59 — i.e. their
    # tops poke only 0.04-0.09m above the upper floor (12.5). The exporter
    # slices at PlaneY=12.5 (a plane added to catch walls whose tops sit just
    # above the upper floor), so these walls DO produce in-band segments at
    # 12.5 — but those segments are just the wall-top silhouette of a
    # ground-floor wall, a sub-knee lip the player capsule walks straight over.
    # Rasterizing them draws phantom upper-floor walls that seal real passages
    # (notably SM_Walls_Hall1 sealing the stair-landing→bedroom archway, which
    # isolated the whole stair landing). Same rationale and threshold as the
    # borrow gate below — this is its in-band analog. A wall whose TopY clears
    # the band's lower edge by < MIN_BORROW_HEIGHT_M is not a real obstacle on
    # this floor; skip it. Real upper walls (SM_Walls_Bedroom/Hall2, TopY~25.8)
    # clear by 13m and are unaffected. See
    # [[project-navigation-upper-hall2-archway-seal]], [[project-navigation-borrow-height-gate]].
    ty_lip = mesh_record.get("TopY")
    if (fp.get("IsWallLikeFatVictim") and ty_lip is not None
            and ty_lip - y_lo < MIN_BORROW_HEIGHT_M):
        return []

    in_band = []
    for segment in segs:
        py = segment.get("PlaneY")
        if py is not None and (py < y_lo or py > y_hi):
            continue
        ax = segment["AX"]; az = segment["AZ"]
        bx = segment["BX"]; bz = segment["BZ"]
        if not (in_scene(ax, az) or in_scene(bx, bz)):
            continue
        in_band.append(segment)
    if in_band:
        return in_band

    # Borrow-from-other-band fallback for wall-FAT meshes whose Y range
    # overlaps the band but whose only slice planes lie outside it.
    #
    # The exporter slices at fixed Y planes; a wall with TopY=12.55 produces
    # segments at PlaneY=0.5 (ground band) but none at PlaneY=13.5 (upper
    # band), even though the wall geometrically extends into the upper-floor
    # bake band [12.25, 15.0]. Walls are vertical extrusions, so their
    # silhouette at any Y inside [BottomY, TopY] is identical to their
    # silhouette at any slice plane inside that range. Reuse the closest
    # slice plane's segments. See [[project-navigation-walls-living-upper-slice-gap]].
    #
    # Gate: only borrow when the wall extends at least MIN_BORROW_HEIGHT
    # above the floor. A wall whose top is only 0.07m above the upper floor
    # (e.g. SM_Walls_Hall1, TopY=12.57 vs upper floor_y=12.5) is a knee-high
    # lip the player capsule walks over, not a real obstacle. Borrowing
    # ground-floor segments for such walls draws phantom upper-floor walls
    # that seal the upstairs hallway (CC split between bedroom and stairs).
    # MIN_BORROW_HEIGHT_M is module-level (shared with the top-lip gate above).
    if not fp.get("IsWallLikeFatVictim"):
        return []
    by = mesh_record.get("BottomY"); ty = mesh_record.get("TopY")
    if by is None or ty is None:
        return []
    # The wall must geometrically intersect the band.
    if max(by, y_lo) > min(ty, y_hi):
        return []
    # Gate the borrow: TopY must clear y_lo by at least MIN_BORROW_HEIGHT,
    # otherwise the wall is sub-knee and not a real obstacle on this floor.
    # y_lo is floor_y - STEP_UP_TOL, so MIN_BORROW_HEIGHT here = capsule
    # interception above the floor band's lower edge. Note this is intentionally
    # asymmetric: we don't gate the BottomY side because walls extending DOWN
    # into the band (e.g. ceiling beams hanging into the player's head) still
    # block — they just don't get borrow-fallback either way, because they have
    # in-band segments to start with.
    if ty - y_lo < MIN_BORROW_HEIGHT_M:
        return []
    band_mid = (y_lo + y_hi) / 2.0
    nearest_plane = None
    nearest_d = float("inf")
    plane_groups = {}
    for s in segs:
        py = s.get("PlaneY")
        if py is None:
            continue
        if py < by or py > ty:
            continue
        plane_groups.setdefault(py, []).append(s)
        d = abs(py - band_mid)
        if d < nearest_d:
            nearest_d = d; nearest_plane = py
    if nearest_plane is None:
        return []
    borrowed = []
    for s in plane_groups[nearest_plane]:
        ax = s["AX"]; az = s["AZ"]
        bx = s["BX"]; bz = s["BZ"]
        if not (in_scene(ax, az) or in_scene(bx, bz)):
            continue
        borrowed.append(s)
    return borrowed


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

    # Walkable rasterization with vaulted-ceiling gate.
    #
    # Some rooms in this scene are vaulted (Kitchen, LaundryRoom) — they have
    # no real upper floor above them, just a thin `SM_Ceiling_*` slab at
    # ~12.37m as a visual ceiling cap. Other rooms have a real upper-floor
    # walkable surface in the same band (often named `SM_Ceiling_<RoomBelow>`
    # too — e.g. SM_Ceiling_Hall is the upstairs hall floor, doubling as the
    # downstairs hall ceiling). Discriminator: real walkable surfaces sit
    # near the floor-band's target Y (within VAULTED_GATE_M of fy); pure
    # visual ceilings sit noticeably lower (~0.1-0.2m below fy).
    #
    # This filter only triggers above STEP_UP_TOL below fy — anything closer
    # than that to fy is admitted as before. Ground-floor band stays
    # unaffected because no vaulted-ceiling pattern exists there.
    # Vaulted-ceiling gate.
    #
    # Some rooms in this scene are vaulted (Kitchen, LaundryRoom). They have
    # no real upper-floor walkable area, just a thin visual `SM_Ceiling_*`
    # slab at TopY≈12.37m (0.5m below the actual upper-floor surfaces at
    # ~12.84-12.95). Without filtering, that visual ceiling rasterizes as
    # walkable upper-floor area and interactables snap into a phantom region.
    #
    # Discriminator: each band has a "true" floor Y close to its highest
    # large walkable surface. Visual ceilings sit noticeably lower. We
    # measure that distance per-band rather than against the band's mid-Y
    # because fy is a round number that doesn't always equal the actual
    # mesh height (ground SM_Floor_* sits at -0.57 vs fy=-0.5).
    #
    # Algorithm: the band's "true" floor Y is the highest TopY among LARGE
    # walkable surfaces (area > LARGE_SLAB_M2). Props and small objects (a
    # 0.5×0.5m book lying on a desk) don't qualify. Then gate surfaces whose
    # TopY is more than VAULTED_DROP_M below that.
    #
    # Calibration: vaulted Kitchen ceiling (TopY=12.37, area >>4 m² → large)
    # vs band-top 12.95 (SM_Ceiling_Hall = upstairs hall floor) → 0.58m drop
    # → gates. SM_Floor_Bedroom 12.84 vs 12.95 → 0.11m drop → passes.
    # Ground floor SM_Floor_Office −0.57 vs band-top −0.48 (rugs) → 0.09 drop
    # → passes.
    LARGE_SLAB_M2 = 50.0       # actual room floors are 100-800 m²
    SLAB_MAX_VEXT_M = 0.10     # actual floor/ceiling meshes are thin (VExt 0-0.04m);
                               # treadmills, rugs, beds have VExt 0.16-0.81m
    VAULTED_DROP_M = 0.30
    def _slab_area(w):
        fp = w["Footprint"]
        return (fp["MaxX"] - fp["MinX"]) * (fp["MaxZ"] - fp["MinZ"])
    large_walks = [
        w for w in floor_walks
        if _slab_area(w) >= LARGE_SLAB_M2 and w["VerticalExtent"] <= SLAB_MAX_VEXT_M
    ]
    if large_walks:
        band_top_y = max(w["TopY"] for w in large_walks)
    else:
        band_top_y = max(w["TopY"] for w in floor_walks)

    walkable_bm = [[False] * nz for _ in range(nx)]
    for w in floor_walks:
        if band_top_y - w["TopY"] > VAULTED_DROP_M:
            continue
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
    # Anchors for the "is this archway carved by a door?" test. A doorframe
    # within DOOR_COMPONENT_CARVE_RADIUS of any door anchor is a REAL door's
    # frame — its passability is governed by the door-position carve (for
    # Doors_* interactables) or the per-door freed-cells state machine (for
    # panel-based door_records like AtticDoor_11, BackDoorPivot, the front
    # door). Those frames must NOT be archway-carved: doing so would force the
    # doorway permanently open and bypass locked/closed-door state. Frames with
    # no nearby door anchor are genuine open archways and get carved.
    #
    # Both anchor sources matter: `doors` covers Doors_*-named carve anchors;
    # door_records covers panel-based doors whose names don't start with Doors_
    # (e.g. AtticDoor_11 frames SM_Doorframe_Small_12 — a LOCKED attic door
    # that must stay shut). Missing door_records here wrongly opened it.
    door_anchor_xz = [
        (d["x"], d["z"]) for d in doors
        if abs(d.get("y", fy) - fy) <= 2.0
    ]
    for dr in door_records:
        wp = dr.get("WorldPosition") or {}
        ax = wp.get("x")
        az = wp.get("z")
        ay = wp.get("y")
        if ax is None or az is None:
            continue
        if ay is not None and abs(ay - fy) > 2.0:
            continue
        door_anchor_xz.append((ax, az))

    def _frame_has_door(record):
        c = record.get("Footprint", {}).get("Center") or {}
        cx = c.get("x")
        cz = c.get("z")
        if cx is None or cz is None:
            return False
        return any(
            math.hypot(cx - ax, cz - az) <= DOOR_COMPONENT_CARVE_RADIUS
            for ax, az in door_anchor_xz
        )

    # Open-archway carve anchors. Doorframes with no associated door
    # (open archways) get a clearance disc carved at the frame center, exactly
    # like a real door — the door-carve below is proven to punch through a
    # doorway's asymmetric segment stubs + dilation. Collected here, applied in
    # the door-carve pass. See [[project-navigation-upper-hall2-archway-seal]].
    archway_carves = []

    for m in mesh_colliders:
        if not _is_solid_blocker(m):
            continue
        if m["TopY"] < y_lo or m["BottomY"] > y_hi:
            continue
        segments = _segments_in_floor_band(m, y_lo, y_hi)
        if not segments:
            continue
        if _is_doorframe(m) and not _frame_has_door(m):
            bb = m.get("Bounds2D")
            if bb:
                # Keep the frame's own in-band segments alongside its bbox: the
                # carve must open the threshold gap but PRESERVE the capsule-
                # clearance dilation around the frame's solid jamb posts, or the
                # player walks into a post the bake marked navigable.
                archway_carves.append((bb, segments))
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

    # Open-archway carve: for doorframes with no associated door, undo dilation
    # across the doorway throat so the passage opens. Masked to the frame's own
    # XZ bounding box (plus a margin) so the carve cannot leak far from the
    # frame. Like the door-carve, only dilated cells are cleared and the final
    # `walkable AND NOT dilated` keeps non-floor cells blocked.
    #
    # Margin is floor-aware. Ground frames use a tight 0.5m margin: ground
    # rooms are densely packed and a wide carve over-widens many doorways at
    # once (merging components that should stay doorway-gated). The upper floor
    # uses 1.2m to bridge the stair-newel dilation pinch that seals the stair
    # landing from the upstairs archway corridor — the newel post + jamb
    # dilation close a ~1m doorway about one capsule-width past the
    # SM_Doorframe_Small_13 frame, and a 0.5m box stops just short of it. The
    # upper floor is safe to carve wider because the top-lip gate (above) has
    # already removed the phantom ground-wall lips a wide carve would graze.
    # See [[project-navigation-upper-hall2-archway-seal]].
    # POST-CLEARANCE GUARD: a doorframe is not a clean hole — it has solid jamb
    # POSTS. The carve must open the threshold GAP between the posts but must NOT
    # remove the capsule-clearance dilation hugging the posts, or the planner
    # routes the player flush against a post and the runtime collider stops them
    # (e.g. SM_Doorframe_Small_7's east post: bake said navigable, player walked
    # into it and stalled). For each frame, re-rasterize its own segments and
    # dilate by the capsule radius; that post-halo is preserved (never cleared),
    # while the threshold gap — which is >1 capsule-width from either post — is
    # opened. See [[project-navigation-executor-corner-stall]].
    ARCHWAY_CARVE_MARGIN_M = 1.2 if fy > 6.0 else 0.5
    mgn = ARCHWAY_CARVE_MARGIN_M
    for bb, segments in archway_carves:
        bx0 = int((bb["MinX"] - mgn - minx) / CELL)
        bx1 = int((bb["MaxX"] + mgn - minx) / CELL)
        bz0 = int((bb["MinZ"] - mgn - minz) / CELL)
        bz1 = int((bb["MaxZ"] + mgn - minz) / CELL)

        # Build the frame's own post-halo (raw post cells dilated by capsule R).
        post_raw = [[False] * nz for _ in range(nx)]
        for s in segments:
            _rasterize_segment(post_raw, s["AX"], s["AZ"], s["BX"], s["BZ"],
                               minx, minz, nx, nz, CELL)
        post_halo = _dilate_disc(post_raw, nx, nz, DILATE_CELLS)

        for jx in range(max(0, bx0), min(nx, bx1 + 1)):
            for jz in range(max(0, bz0), min(nz, bz1 + 1)):
                if dilated[jx][jz] and not post_halo[jx][jz]:
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
        # Threshold cells: cells within DOOR_COMPONENT_CARVE_RADIUS of the
        # door anchor that are walkable + dilation-blocked. The doorway gap
        # in the wall mesh is often modelled on only one face (asymmetric
        # export), so dilation seals the gap even though the geometry has
        # it. Threshold cells re-open that gap.
        door_pos = door_rec.get("WorldPosition") or {}
        anchor_x = door_pos.get("x")
        anchor_z = door_pos.get("z")
        anchor_y = door_pos.get("y", fy)
        threshold_cells = []
        if (anchor_x is not None and anchor_z is not None
                and abs(anchor_y - fy) <= 2.0):
            cx = int((anchor_x - minx) / CELL)
            cz = int((anchor_z - minz) / CELL)
            cr = int(math.ceil(DOOR_COMPONENT_CARVE_RADIUS / CELL))
            # Connectivity gate against the original bug A: a threshold cell
            # must be reachable from the door's own panel through cells that
            # are EITHER navigable in the door-open world OR in the door's
            # closed-pose dilation. This prevents the carve from leaking
            # through an intervening wall into a neighbouring room's clearance
            # band (Doors_Office leaking into SM_Walls_Hall1 dilation).
            # Implemented as a BFS seeded from the panel_closed_dil cells,
            # bounded to the carve disc.
            from collections import deque
            seeds = []
            for ix in range(max(0, cx - cr), min(nx, cx + cr + 1)):
                for iz in range(max(0, cz - cr), min(nz, cz + cr + 1)):
                    if panel_closed_dil[ix][iz]:
                        seeds.append((ix, iz))
            if seeds:
                reach = set(seeds)
                queue = deque(seeds)
                while queue:
                    qx, qz = queue.popleft()
                    for dx in (-1, 0, 1):
                        for dz in (-1, 0, 1):
                            if dx == 0 and dz == 0:
                                continue
                            tx = qx + dx; tz = qz + dz
                            if tx < 0 or tx >= nx or tz < 0 or tz >= nz:
                                continue
                            # Stay inside the carve disc.
                            if (tx - cx) ** 2 + (tz - cz) ** 2 > cr * cr:
                                continue
                            if (tx, tz) in reach:
                                continue
                            # Walkable cells inside the disc that are EITHER
                            # navigable post-bake OR in the panel's closed
                            # dilation are reachable. Walls (dilated cells
                            # NOT in the panel) block the BFS.
                            if not walkable_bm[tx][tz]:
                                continue
                            if dilated[tx][tz] and not panel_closed_dil[tx][tz]:
                                continue
                            reach.add((tx, tz))
                            queue.append((tx, tz))
                # Threshold cells = reachable cells that are dilation-blocked
                # (so opening the door is what gives them passage). Cells
                # already navigable don't need to be re-added.
                for (jx, jz) in reach:
                    if dilated[jx][jz]:
                        threshold_cells.append((jx, jz))

        # Door-open dilation mask. A freed cell must be navigable in the world
        # where this door is open. The earlier "any non-door raw blocker
        # within DILATE_CELLS" check was too aggressive — it dropped the
        # entire doorway threshold (cells in the gap between the door's
        # surrounding walls) because the walls themselves are within DILATE
        # of the gap, even though the gap is wider than 2× capsule radius.
        #
        # The correct test: compute the would-be dilated bitmap if this door
        # were open (= blocked_bm minus this door's closed panel cells, plus
        # this door's open panel cells), then a cell is legitimately freed
        # iff it is NOT dilation-blocked in that alternative world. This
        # exactly captures "opening the door makes this cell reachable."
        #
        # Cost: one O(nx*nz*DILATE_CELLS^2) dilation per door. Doors are
        # sparse and only one floor at a time matters, so total cost is fine.
        door_open_raw = [
            [(blocked_bm[ix][iz] and not panel_closed_raw[ix][iz]) or panel_open_raw[ix][iz]
             for iz in range(nz)]
            for ix in range(nx)
        ]
        door_open_dil = _dilate_disc(door_open_raw, nx, nz, DILATE_CELLS)

        freed_set = set()
        for ix in range(nx):
            for iz in range(nz):
                # Candidate cells: those the closed-pose dilation covers but
                # the open-pose dilation does not (the door panel's swept
                # region) and the doorway threshold (added below).
                if not panel_closed_dil[ix][iz]:
                    continue
                if panel_open_dil[ix][iz]:
                    continue
                if not walkable_bm[ix][iz]:
                    continue
                # Final gate: in the door-open world, this cell must be
                # outside any blocker's capsule clearance.
                if door_open_dil[ix][iz]:
                    continue
                freed_set.add((ix, iz))
        # Threshold cells are exempt from the door_open_dil gate by design:
        # the doorway opening is exactly the place where the wall has a gap
        # that dilation seals over (the wall mesh is exported on one face
        # only). The adjacent-to-panel_closed_dil constraint above ensures
        # threshold cells sit in the door's own wall opening rather than in
        # a different wall's clearance band.
        for c in threshold_cells:
            freed_set.add(c)

        if not freed_set:
            continue
        freed = sorted([list(c) for c in freed_set])
        # Emit the door's own closed-pose dilation footprint so the post-bake
        # invariant can subtract it when checking freed_cells against the
        # global dilated bitmap (otherwise every freed cell looks "blocked"
        # because the door's own panel contributes to dilation).
        own_dil_cells = sorted(
            [ix, iz]
            for ix in range(nx) for iz in range(nz)
            if panel_closed_dil[ix][iz]
        )
        # Threshold cells emitted separately so the invariant can exempt them
        # from the "freed cells must not be dilation-blocked by another wall"
        # check. Threshold cells are by design in the surrounding wall's
        # dilation band — that's the wall opening dilation seals over. The
        # adjacency-to-panel_closed_dil constraint above keeps them legitimate.
        threshold_cells_list = sorted([list(c) for c in threshold_cells])
        doors_per_floor.append({
            "name": door_rec.get("Name"),
            "kind": door_rec.get("Kind"),
            "component_id": door_rec.get("ComponentId"),
            "panel_count": len(door_rec.get("Panels", [])),
            "closed_cells": sum(sum(row) for row in panel_closed_dil),
            "open_cells": sum(sum(row) for row in panel_open_dil),
            "threshold_cells": len(threshold_cells),
            "threshold_cells_list": threshold_cells_list,
            "freed_cells": freed,
            "freed_count": len(freed),
            "panel_dilated_cells": own_dil_cells,
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
        # Wall-released dilation mask, same shape as the door pass.
        # The original guard `dilated AND NOT wall_dil` was a no-op (the
        # outer loop already required wall_dil). The correct test: compute
        # the dilated bitmap as it would be if THIS wall were removed, then
        # the wall's freed cells are those navigable in that alternative
        # world. See [[project-navigation-door-carve-dilation-bug]].
        wall_released_raw = [
            [blocked_bm[ix][iz] and not wall_raw[ix][iz] for iz in range(nz)]
            for ix in range(nx)
        ]
        wall_released_dil = _dilate_disc(wall_released_raw, nx, nz, DILATE_CELLS)
        freed = []
        for ix in range(nx):
            for iz in range(nz):
                if not wall_dil[ix][iz]:
                    continue
                if not walkable_bm[ix][iz]:
                    continue
                if wall_released_dil[ix][iz]:
                    continue
                freed.append([ix, iz])
        if not freed:
            continue
        own_dil_cells_sw = sorted(
            [ix, iz]
            for ix in range(nx) for iz in range(nz)
            if wall_dil[ix][iz]
        )
        state_walls_per_floor.append({
            "name": wall.get("Name"),
            "component_id": wall.get("ComponentId"),
            "release_mechanism": wall.get("ReleaseMechanism"),
            "release_condition": wall.get("ReleaseCondition"),
            "default_active": wall.get("DefaultActive", True),
            "wall_cells": sum(sum(row) for row in wall_dil),
            "freed_cells": freed,
            "freed_count": len(freed),
            "panel_dilated_cells": own_dil_cells_sw,
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


def _verify_bake_invariants(report, mesh_colliders, slice_planes):
    """Assert structural invariants on a freshly-baked report. Each failure is
    a recurring bug shape we want to catch at bake time, not at runtime.

    Returns (errors, warnings) — caller decides whether to raise.
    """
    errors = []
    warnings = []

    # 1. door.freed_cells ∩ (dilated_blocked ∖ panel_dilated_cells) must be empty.
    # 2. state_wall.freed_cells ∩ (dilated_blocked ∖ panel_dilated_cells) must be empty.
    # A freed cell may legitimately sit in the global dilated bitmap because
    # the door's own closed-pose panel contributes to dilation — that's the
    # whole point of freed_cells. The invariant catches freed cells that are
    # closed by a *different* blocker (a neighbouring wall). Repro this would
    # catch: see [[project-navigation-door-carve-dilation-bug]] (Doors_Office
    # freeing cells inside SM_Walls_Hall1's clearance band).
    for floor in report["floors"]:
        if "error" in floor:
            continue
        label = floor["label"]
        rows = floor["bitmap_rows"]
        def _is_dilated_blocked(ix, iz):
            # 'X' = walkable AND blocked, 'B' = blocker only. Both are dilated-blocked.
            return rows[ix][iz] in ('X', 'B')
        for door in floor.get("doors", []):
            own = {(c[0], c[1]) for c in door.get("panel_dilated_cells", [])}
            # Threshold cells are exempt: they sit in the door's surrounding
            # wall's dilation by design (asymmetric wall-mesh export), and
            # the carve adjacency constraint keeps them inside the door's
            # actual opening rather than in an unrelated wall.
            thresholds = {(c[0], c[1]) for c in door.get("threshold_cells_list", [])}
            violating = [(c[0], c[1]) for c in door.get("freed_cells", [])
                         if _is_dilated_blocked(c[0], c[1])
                         and (c[0], c[1]) not in own
                         and (c[0], c[1]) not in thresholds]
            if violating:
                errors.append(
                    f"floor={label} door={door.get('name')!r}: "
                    f"{len(violating)} freed_cells closed by a non-door blocker "
                    f"(e.g. {violating[:5]})"
                )
        for wall in floor.get("state_walls", []):
            own = {(c[0], c[1]) for c in wall.get("panel_dilated_cells", [])}
            violating = [(c[0], c[1]) for c in wall.get("freed_cells", [])
                         if _is_dilated_blocked(c[0], c[1])
                         and (c[0], c[1]) not in own]
            if violating:
                errors.append(
                    f"floor={label} state_wall={wall.get('name')!r}: "
                    f"{len(violating)} freed_cells closed by a non-wall blocker "
                    f"(e.g. {violating[:5]})"
                )

    # 3. For every floor's bake band, every IsWallLikeFatVictim mesh whose Y
    # range overlaps the band must have *some* slice plane the bake can use —
    # either inside the intersection (perfect), or anywhere in the wall's Y
    # range (the borrow-from-other-band fallback in _segments_in_floor_band
    # will reuse those segments). The only way the wall ends up invisible is
    # if there is no slice plane anywhere in [BottomY, TopY].
    # See [[project-navigation-walls-living-upper-slice-gap]] for the upper-
    # floor slice-gap case the borrow fallback exists to handle.
    if slice_planes:
        for floor in report["floors"]:
            if "error" in floor:
                continue
            label = floor["label"]
            fy = floor["floor_y"]
            band_lo = fy - STEP_UP_TOL
            band_hi = fy + CAPSULE_H
            for m in mesh_colliders:
                fp = m.get("Footprint") or {}
                if not fp.get("IsWallLikeFatVictim"):
                    continue
                by = m.get("BottomY"); ty = m.get("TopY")
                if by is None or ty is None:
                    continue
                # Does the wall's Y range intersect this floor's band?
                if max(by, band_lo) > min(ty, band_hi):
                    continue  # no overlap, wall doesn't belong to this floor
                # The borrow fallback needs at least one slice plane in the
                # wall's Y range (not necessarily in the band intersection).
                if any(by <= p <= ty for p in slice_planes):
                    continue
                name = m.get("GameObjectName") or (m.get("Path") or "?").split("/")[-1]
                errors.append(
                    f"floor={label}: wall-FAT mesh {name!r} Y=[{by:.2f},{ty:.2f}] "
                    f"overlaps band [{band_lo:.2f},{band_hi:.2f}] but no slice plane "
                    f"lies in [{by:.2f},{ty:.2f}] either — borrow fallback cannot "
                    f"help (planes={slice_planes}). Wall invisible on this floor."
                )

    # 4. Interactable coverage smoke check intentionally omitted here. The
    # raw interactables list contains many sub-mesh entries (book pages,
    # monitor sub-parts, lighting variants) whose Position is buried inside
    # the parent's collider footprint, so a naive nearest-navigable check
    # produces hundreds of false positives every bake. scripts/reachability_matrix.py
    # already does the per-interactable check correctly (snapping by Path
    # and interaction radius); use that as the authoritative coverage tool.

    # 5. Every inter_floor_edge endpoint must land on a navigable cell.
    # If a stair/teleporter terminus falls into a sealed cell, the planner
    # silently drops the edge and cross-floor routing breaks. Edges live in
    # a dict keyed by category (stair_ramp, teleporter); each entry has
    # per-floor endpoints with {cell: [ix, iz]} or a deferred note.
    edges_doc = report.get("inter_floor_edges") or {}
    if isinstance(edges_doc, dict):
        floors_by_label = {f["label"]: f for f in report["floors"] if "error" not in f}
        edge_lists = []
        for category, lst in edges_doc.items():
            if isinstance(lst, list):
                edge_lists.append((category, lst))
        for category, lst in edge_lists:
            if category.endswith("rejected"):
                continue  # rejected entries are diagnostic, not active edges
            for edge in lst:
                if not isinstance(edge, dict):
                    continue
                for label, f in floors_by_label.items():
                    ep = edge.get(label)
                    if not isinstance(ep, dict):
                        continue
                    cell = ep.get("cell")
                    if not isinstance(cell, list) or len(cell) < 2:
                        continue  # deferred / no-cell endpoint
                    ix, iz = cell[0], cell[1]
                    fr = f["frame"]
                    if not (0 <= ix < fr["nx"] and 0 <= iz < fr["nz"]):
                        errors.append(
                            f"inter_floor_edge {category}/{edge.get('kind','?')} endpoint "
                            f"on floor {label} cell ({ix},{iz}) out of bounds"
                        )
                        continue
                    if f["bitmap_rows"][ix][iz] != 'N':
                        errors.append(
                            f"inter_floor_edge {category}/{edge.get('kind','?')} endpoint "
                            f"on floor {label} cell ({ix},{iz}) is not navigable "
                            f"(char={f['bitmap_rows'][ix][iz]!r})"
                        )

    # 6. Every door / state-wall with panel data must have a non-empty
    # freed_cells set. The carve passes have several layers of masking
    # (other-blocker dilation, door-open-dilation, threshold adjacency) and
    # a regression in any of them can wipe a door's contribution silently —
    # the runtime then unions an empty set when the door opens, the door
    # becomes a no-op overlay, and the room behind it stays unreachable.
    #
    # Distinction: doors with `panel_count == 0` are name-only entries
    # (interactables tagged Doors_* but no exported panel mesh, e.g. the
    # Camera_DorianBathroom2Door* placeholder objects). Those legitimately
    # have no freed cells. Warning, not error.
    for floor in report["floors"]:
        if "error" in floor:
            continue
        label = floor["label"]
        for door in floor.get("doors", []):
            if door.get("freed_count", 0) > 0:
                continue
            name = door.get("name") or "?"
            panels = door.get("panel_count", 0)
            if panels > 0:
                errors.append(
                    f"floor={label} door={name!r}: 0 freed_cells despite "
                    f"panel_count={panels}. Carve masks may be over-aggressive — "
                    f"opening this door has no effect on routing."
                )
            else:
                warnings.append(
                    f"floor={label} door={name!r}: 0 freed_cells (no panel data; "
                    f"name-only door entry — expected)."
                )
        for wall in floor.get("state_walls", []):
            if wall.get("freed_count", 0) > 0:
                continue
            name = wall.get("name") or "?"
            errors.append(
                f"floor={label} state_wall={name!r}: 0 freed_cells. Releasing "
                f"this wall has no effect on routing."
            )

    return errors, warnings


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

    # Re-load after the inter-floor pass so invariant 5 sees the edges.
    full_report = json.loads(OUT_JSON.read_text(encoding="utf-8"))
    slice_planes = (blok.get("Filtering") or {}).get("MeshSlicePlanes") or []
    errors, warnings = _verify_bake_invariants(
        full_report, mesh_colliders, slice_planes
    )
    if warnings:
        print("\nBake invariant warnings:")
        for w in warnings:
            print(f"  WARN: {w}")
    if errors:
        print("\nBake invariant errors:")
        for e in errors:
            print(f"  ERROR: {e}")
        raise SystemExit(
            f"Bake produced {len(errors)} invariant violation(s). "
            f"See errors above; do not consume this artifact."
        )
    print("\nBake invariants: OK")


if __name__ == "__main__":
    main()
