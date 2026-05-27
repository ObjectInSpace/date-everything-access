"""Replay bake_navigable_region.bake_floor for one floor while recording
which collider + which code path closed each cell of blocked_bm.

Targeted at investigating the ground-floor Kitchen+Laundry seal. Pass a
world (x, z) cell of interest with --probe; the script prints every write
that landed in that cell.

Usage:
  uv run python scripts/debug_blocker_provenance.py --floor ground --probe -17.913 14.961
"""
from __future__ import annotations
import argparse
import importlib.util
import json
import math
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "bake_navigable_region", REPO / "scripts/bake_navigable_region.py"
)
BAKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BAKE)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--floor", default="ground", choices=("ground", "upper"))
    ap.add_argument("--probe", nargs=2, type=float, metavar=("X", "Z"),
                    default=[-17.913, 14.961])
    ap.add_argument("--probe-radius-cells", type=int, default=0,
                    help="Report writes to cells within this Chebyshev radius of the probe cell")
    args = ap.parse_args()

    walk = json.loads(BAKE.WALK.read_text(encoding="utf-8-sig"))
    blockers_doc = json.loads(BAKE.BLOCK.read_text(encoding="utf-8-sig"))
    walkables = walk["WalkableSurfaces"]
    blockers = blockers_doc["NavigationBlockers"]
    mesh_colliders = blockers_doc.get("MeshColliders", [])

    nav_doc = json.loads(BAKE.NAVDATA.read_text(encoding="utf-8-sig"))
    door_objects = nav_doc.get("DoorObjects", []) or []
    doors = []
    for d in door_objects:
        pos = d.get("Position") or {}
        if "x" not in pos or "z" not in pos or "y" not in pos:
            continue
        doors.append({
            "x": pos["x"], "y": pos["y"], "z": pos["z"],
            "radius": BAKE.DOOR_COMPONENT_CARVE_RADIUS if d.get("DoorComponent") else BAKE.DOOR_CARVE_RADIUS,
            "name": d.get("Name"),
        })

    floor_def = next(f for f in BAKE.FLOORS if f["label"] == args.floor)
    fy = floor_def["y"]
    ytol = floor_def["y_tol"]
    floor_walks = [
        w for w in walkables
        if BAKE.in_scene(w["Footprint"]["CenterX"], w["Footprint"]["CenterZ"])
        and abs(w["TopY"] - fy) <= ytol
        and w["VerticalExtent"] <= BAKE.MAX_FLOOR_SLAB_EXTENT
    ]
    minx = min(w["Footprint"]["MinX"] for w in floor_walks)
    maxx = max(w["Footprint"]["MaxX"] for w in floor_walks)
    minz = min(w["Footprint"]["MinZ"] for w in floor_walks)
    maxz = max(w["Footprint"]["MaxZ"] for w in floor_walks)
    pad = BAKE.CAPSULE_R + BAKE.CELL
    minx -= pad; maxx += pad; minz -= pad; maxz += pad
    nx = int(math.ceil((maxx - minx) / BAKE.CELL))
    nz = int(math.ceil((maxz - minz) / BAKE.CELL))
    cell = BAKE.CELL

    probe_x, probe_z = args.probe
    probe_ix = int((probe_x - minx) / cell)
    probe_iz = int((probe_z - minz) / cell)
    r = args.probe_radius_cells
    target_cells = {
        (probe_ix + dx, probe_iz + dz)
        for dx in range(-r, r + 1)
        for dz in range(-r, r + 1)
    }
    print(f"probe cell=({probe_ix},{probe_iz}) center=({minx + (probe_ix+0.5)*cell:.3f},{minz + (probe_iz+0.5)*cell:.3f}) tracking {len(target_cells)} cell(s)")

    # Walkable bitmap
    walkable_bm = [[False] * nz for _ in range(nx)]
    for w in floor_walks:
        fp = w["Footprint"]
        ix0 = max(0, int(math.floor((fp["MinX"] - minx) / cell)))
        ix1 = min(nx, int(math.ceil((fp["MaxX"] - minx) / cell)))
        iz0 = max(0, int(math.floor((fp["MinZ"] - minz) / cell)))
        iz1 = min(nz, int(math.ceil((fp["MaxZ"] - minz) / cell)))
        for ix in range(ix0, ix1):
            for iz in range(iz0, iz1):
                walkable_bm[ix][iz] = True

    # Trace blocked_bm: wrap a list-of-rows in a proxy that records writes
    writes = []  # (ix, iz, source_tag)
    class TrackedRow:
        __slots__ = ("data", "ix", "writes", "targets")
        def __init__(self, data, ix, writes, targets):
            self.data = data; self.ix = ix; self.writes = writes; self.targets = targets
        def __getitem__(self, iz): return self.data[iz]
        def __setitem__(self, iz, v):
            if v and not self.data[iz]:
                if (self.ix, iz) in self.targets:
                    self.writes.append((self.ix, iz, current_tag[0]))
            self.data[iz] = v
        def __iter__(self): return iter(self.data)
        def __len__(self): return len(self.data)
    class TrackedBM:
        def __init__(self, rows, writes, targets):
            self.rows = [TrackedRow(r, ix, writes, targets) for ix, r in enumerate(rows)]
        def __getitem__(self, ix): return self.rows[ix]
        def __len__(self): return len(self.rows)

    current_tag = ["?"]  # mutable to thread through helper calls
    raw_bm = [[False] * nz for _ in range(nx)]
    blocked_bm = TrackedBM(raw_bm, writes, target_cells)

    y_lo = fy - BAKE.STEP_UP_TOL
    y_hi = fy + BAKE.CAPSULE_H

    mesh_records_by_id = {m.get("ComponentId"): m for m in mesh_colliders if m.get("ComponentId") is not None}
    mesh_records_with_segments = set()

    print("phase 1: mesh segment rasterization + per-mesh fill")
    for m in mesh_colliders:
        if not BAKE._is_solid_blocker(m): continue
        if m["TopY"] < y_lo or m["BottomY"] > y_hi: continue
        segments = BAKE._segments_in_floor_band(m, y_lo, y_hi)
        if not segments: continue
        mesh_records_with_segments.add(m.get("ComponentId"))
        name = m.get("GameObjectName") or m.get("Name") or "?"
        path = m.get("Path") or ""
        current_tag[0] = f"mesh-seg | {name} | {path}"
        for s in segments:
            BAKE._rasterize_segment(blocked_bm, s["AX"], s["AZ"], s["BX"], s["BZ"], minx, minz, nx, nz, cell)
        if not BAKE._is_structural_mesh(m):
            current_tag[0] = f"mesh-footprint-edge | {name} | {path}"
            BAKE._rasterize_footprint_perimeter(blocked_bm, m, minx, minz, nx, nz, cell)
            current_tag[0] = f"mesh-closed-region | {name} | {path}"
            BAKE._rasterize_closed_segment_regions(blocked_bm, segments, minx, minz, nx, nz, cell)

    print("phase 2: primitive + mesh-bounds-fallback blockers")
    for b in blockers:
        if not BAKE._is_solid_blocker(b): continue
        if b["TopY"] < y_lo or b["BottomY"] > y_hi: continue
        bb = b.get("Bounds2D")
        if not bb: continue
        if not BAKE.in_scene((bb["MinX"]+bb["MaxX"])/2, (bb["MinZ"]+bb["MaxZ"])/2): continue
        name = b.get("GameObjectName") or b.get("Name") or "?"
        path = b.get("Path") or ""
        if b.get("ColliderType") == "MeshCollider":
            if b.get("ComponentId") in mesh_records_with_segments:
                continue
            current_tag[0] = f"mesh-bounds-fallback | {name} | {path}"
            BAKE._rasterize_bounds(blocked_bm, bb, minx, minz, nx, nz, cell)
            continue
        current_tag[0] = f"primitive-{b.get('ColliderType')} | {name} | {path}"
        BAKE._rasterize_bounds(blocked_bm, bb, minx, minz, nx, nz, cell)

    print()
    if not writes:
        print(f"NO writes to probe cell(s). Cell is NOT closed by any blocker path.")
    else:
        print(f"{len(writes)} write(s) to probe cell(s):")
        for ix, iz, tag in writes:
            wx = minx + (ix+0.5)*cell
            wz = minz + (iz+0.5)*cell
            print(f"  ({ix},{iz}) world=({wx:.3f},{wz:.3f}) <- {tag}")

    # Also check final state vs walkable at the probe cell, post-dilation
    blocked_raw_at_probe = raw_bm[probe_ix][probe_iz]
    walk_at_probe = walkable_bm[probe_ix][probe_iz]
    print()
    print(f"post-rasterize at probe ({probe_ix},{probe_iz}): walkable={walk_at_probe} blocked_raw={blocked_raw_at_probe}")
    # Dilation
    d = BAKE.DILATE_CELLS
    offsets = [(dx, dz) for dx in range(-d, d+1) for dz in range(-d, d+1) if dx*dx + dz*dz <= d*d]
    dilation_source = None
    for dx, dz in offsets:
        jx = probe_ix - dx; jz = probe_iz - dz
        if 0 <= jx < nx and 0 <= jz < nz and raw_bm[jx][jz]:
            dilation_source = (jx, jz)
            break
    if dilation_source:
        jx, jz = dilation_source
        wx = minx + (jx+0.5)*cell; wz = minz + (jz+0.5)*cell
        print(f"dilation closure source for probe: ({jx},{jz}) world=({wx:.3f},{wz:.3f})")
    else:
        print(f"NO dilation source within {d} cells of probe — probe should remain navigable")

    print(f"\nground frame: origin=({minx:.3f},{minz:.3f}) nx={nx} nz={nz} cell={cell}")


if __name__ == "__main__":
    main()
