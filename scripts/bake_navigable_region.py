"""Step O2 of [[project-navigation-object-first-plan]]: rasterize per-floor navigable region.

For each named floor band (ground, upper):
  1. Pick representative floor Y from the walkable export (area-weighted flat/step-up peaks).
  2. Rasterize at 0.2m cells across XZ extent of the floor's walkable footprint.
  3. Cell is walkable iff a walkable surface (VExt <= 1m slab) within the floor band covers it.
  4. Cell is blocked iff a blocker AABB intersects [floorY - STEP_UP_TOL, floorY + capsuleH] at that cell.
  5. Dilate blocked region by capsule radius (0.4m / 2 cells at 0.2m).
  6. Navigable = walkable AND NOT dilated-blocked.
Emits one bitmap per floor + debug PNG.

Crawlspace floor is missing from the walkable export (no slab at Y≈-9.6); skipped here, follow-up.

Run from repo root:
  python scripts/bake_navigable_region.py
"""
from __future__ import annotations
import json, math
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
WALK = REPO / "artifacts/navigation/thirdpersongreybox-walkable.json"
BLOCK = REPO / "artifacts/navigation/thirdpersongreybox-blockers.json"
INTER = REPO / "artifacts/navigation/thirdpersongreybox-interactables.json"
OUT_JSON = REPO / "artifacts/navigation/navigable_region.bake.json"
OUT_PNG_DIR = REPO / "artifacts/navigation"

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
    for b in blockers:
        # IsTrigger blockers don't physically block walking; skip
        if b.get("IsTrigger"): continue
        # Door / teleporter connectors are not impassable obstacles for the planner
        if b.get("IsDoorConnector") or b.get("IsTeleporterConnector"): continue
        if b["TopY"] < y_lo or b["BottomY"] > y_hi: continue
        bb = b.get("Bounds2D")
        if not bb: continue
        if not in_scene((bb["MinX"]+bb["MaxX"])/2, (bb["MinZ"]+bb["MaxZ"])/2): continue
        ix0 = max(0, int(math.floor((bb["MinX"] - minx) / CELL)))
        ix1 = min(nx, int(math.ceil((bb["MaxX"] - minx) / CELL)))
        iz0 = max(0, int(math.floor((bb["MinZ"] - minz) / CELL)))
        iz1 = min(nz, int(math.ceil((bb["MaxZ"] - minz) / CELL)))
        if ix0 >= ix1 or iz0 >= iz1: continue
        blocker_hits += 1
        for ix in range(ix0, ix1):
            row = blocked_bm[ix]
            for iz in range(iz0, iz1):
                row[iz] = True

    # Wall-mesh segment pass: combined-room wall meshes (SM_Walls_Living etc.)
    # are rejected from NavigationBlockers by the wall-like thinness gate (their
    # AABBs span room interiors). Their MeshSlicePlanes triangle-clipped segments
    # trace the actual walls — rasterize those directly. See
    # [[project-navigation-bake-blocker-gap]] and [[project-navigation-bake-blocker-scope-2026-05-21]].
    wall_meshes_used = 0
    wall_segments_rasterized = 0
    for m in mesh_colliders:
        fp = m.get("Footprint") or {}
        if not fp.get("IsWallLikeFatVictim"): continue
        # Per-floor band intersection (same Y test as the AABB pass).
        if m["TopY"] < y_lo or m["BottomY"] > y_hi: continue
        segs = fp.get("Segments")
        if not segs: continue
        mesh_had_in_band_segment = False
        for s in segs:
            # Only rasterize segments whose source slice plane is inside the
            # floor's Y band. This prevents ground-floor wall slice traces from
            # leaking onto the upper floor for walls whose AABB barely
            # straddles both bands (e.g. SM_Walls_Living TopY=12.55).
            py = s.get("PlaneY")
            if py is not None and (py < y_lo or py > y_hi): continue
            ax = s["AX"]; az = s["AZ"]; bx = s["BX"]; bz = s["BZ"]
            if not (in_scene(ax, az) or in_scene(bx, bz)): continue
            _rasterize_segment(blocked_bm, ax, az, bx, bz, minx, minz, nx, nz, CELL)
            wall_segments_rasterized += 1
            mesh_had_in_band_segment = True
        if mesh_had_in_band_segment:
            wall_meshes_used += 1

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
        "wall_meshes_rasterized": wall_meshes_used,
        "wall_segments_rasterized": wall_segments_rasterized,
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
        print(f"  wall_meshes_rasterized={result['wall_meshes_rasterized']}  "
              f"wall_segments_rasterized={result['wall_segments_rasterized']}  "
              f"door_carves={result['door_carves']}")
        png_path = OUT_PNG_DIR / f"navigable_region.{floor['label']}.ppm"
        write_png(result, png_path)
        print(f"  debug image: {png_path}")

    OUT_JSON.parent.mkdir(parents=True, exist_ok=True)
    OUT_JSON.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"\nWrote {OUT_JSON}")


if __name__ == "__main__":
    main()
