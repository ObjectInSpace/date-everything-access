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


def _rasterize_footprint_perimeter(blocked_bm, record, minx, minz, nx, nz, cell):
    fp = record.get("Footprint") or {}
    vertices = fp.get("Vertices") or []
    if len(vertices) < 2:
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


def bake_floor(floor, walkables, blockers, mesh_colliders, doors):
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
    # re-seals them. Doors are explicit passages in the planner -- the bake
    # should reflect their authored presence.
    door_carves = 0
    cr = int(math.ceil(DOOR_CARVE_RADIUS / CELL))
    carve_offsets = [(dx, dz) for dx in range(-cr, cr+1) for dz in range(-cr, cr+1)
                     if dx*dx + dz*dz <= cr*cr]
    for d in doors:
        dy = d["y"]
        if abs(dy - fy) > 2.0: continue  # different floor
        dx_w, dz_w = d["x"], d["z"]
        ix = int((dx_w - minx) / CELL)
        iz = int((dz_w - minz) / CELL)
        for dx, dz in carve_offsets:
            jx = ix + dx; jz = iz + dz
            if jx < 0 or jx >= nx or jz < 0 or jz >= nz: continue
            if dilated[jx][jz]:
                dilated[jx][jz] = False
                door_carves += 1

    # Navigable = walkable AND NOT dilated
    navigable_bm = [[walkable_bm[ix][iz] and not dilated[ix][iz]
                     for iz in range(nz)] for ix in range(nx)]

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

    # Doors from interactables. Each entry: {x, y, z, name}. Used to carve
    # navigability discs that survive wall-mesh asymmetric-cut artifacts.
    doors = []
    if INTER.exists():
        inter = json.load(open(INTER, encoding="utf-8"))
        recs = inter.get("Interactables") or inter.get("Records") or []
        for it in recs:
            name = it.get("GameObjectName") or it.get("Name") or ""
            if not name.startswith("Doors_"): continue
            pos = it.get("WorldPosition") or it.get("Position") or {}
            doors.append({"name": name, "x": pos.get("x", 0.0),
                          "y": pos.get("y", 0.0), "z": pos.get("z", 0.0)})

    report = {
        "params": {
            "capsule_radius_m": CAPSULE_R,
            "capsule_height_m": CAPSULE_H,
            "step_up_tolerance_m": STEP_UP_TOL,
            "cell_size_m": CELL,
            "dilation_cells": DILATE_CELLS,
        },
        "floors": [],
    }
    for floor in FLOORS:
        print(f"Baking floor: {floor['label']} (Y={floor['y']})...")
        result = bake_floor(floor, walkables, blockers, mesh_colliders, doors)
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
