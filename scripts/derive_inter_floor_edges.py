"""Step O3 of [[project-navigation-object-first-plan]]: derive inter-floor edges.

Approach:
  - From walkable export, find surfaces whose BottomY is near the lower floor
    and TopY is near the upper floor — candidate stairs/ramps.
  - Filter by SlopeKind (stairs/ramp) and a sane area cap to exclude walls
    that happen to have a tall vertical extent (e.g. SM_Walls_Living).
  - For each surviving surface: find a navigable cell on each floor whose XZ
    lies within the surface footprint. Emit an edge endpoint pair.
  - Append the crawlspace teleporter as an explicit kind=teleporter edge.

Output: extends artifacts/navigation/navigable_region.bake.json in-place
with an `inter_floor_edges` field.
"""
from __future__ import annotations
import json, math
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
BAKE = REPO / "artifacts/navigation/navigable_region.bake.json"
WALK = REPO / "artifacts/navigation/thirdpersongreybox-walkable.json"
NAV  = REPO / "artifacts/navigation/thirdpersongreybox-navigation-data.json"
BLOCK = REPO / "artifacts/navigation/thirdpersongreybox-blockers.json"

# A stair mesh's two landings are at OPPOSITE ENDS of the run, not the footprint
# centre. The old code picked the navigable cell nearest the footprint centre for
# BOTH floors, collapsing a long diagonal staircase to a single mid-stair point —
# so descending/ascending routes had no correct stair direction and the follower
# wedged against the side wall (the SM_Walls_Hall1 stair-exit stall). Instead we
# locate each landing from the stair COLLIDER's player-height slice segments: the
# segments sliced at ~ground-Y cluster at the bottom landing, those at ~upper-Y at
# the top landing. Centroid of the floor-plane slice → that floor's landing XZ.
# Endpoints are symmetric, so they serve ascent and descent identically.
# See [[project-navigation-hall1-runtime-truth-2026-05-29]].
STAIR_SLICE_PLANE_TOL = 1.5   # |segment.PlaneY - floor_Y| ≤ this counts as that floor's slice

# Stair surface filters
MAX_STAIR_AREA = 200.0        # exclude wall-meshes mis-tagged as stairs
MIN_STAIR_AREA = 1.0          # exclude micro-segments
FLOOR_TOUCH_TOL = 1.5         # |surface_endY - floor_Y| ≤ this counts as "touching"
RAMP_SAMPLE_BIN_M = 1.5       # ramp-path sample spacing (one waypoint per ~1.5m of rise)
# The walkable exporter's SlopeKind classifier marks any tall-vertical-extent mesh
# as "stairs" (it uses VerticalExtent only). Walls and fireplaces match. Restrict
# to surfaces whose authored path mentions stairs/ramp to filter the false positives.
import re as _re
NAME_PATTERN = _re.compile(r"(?i)(stair|ramp)")


def find_floor(bake, label):
    for f in bake["floors"]:
        if f.get("label") == label:
            return f
    return None


def navigable_cell_in_footprint(floor, surface_fp):
    """Find a navigable cell whose center lies within the surface XZ footprint.
    Prefer the cell nearest the footprint center for stability."""
    frame = floor["frame"]
    rows = floor["bitmap_rows"]
    ox, oz, cs = frame["origin_x"], frame["origin_z"], frame["cell_size"]
    nx, nz = frame["nx"], frame["nz"]

    minx = max(surface_fp["MinX"], frame["extent_x"][0])
    maxx = min(surface_fp["MaxX"], frame["extent_x"][1])
    minz = max(surface_fp["MinZ"], frame["extent_z"][0])
    maxz = min(surface_fp["MaxZ"], frame["extent_z"][1])
    if minx >= maxx or minz >= maxz:
        return None

    ix0 = max(0, int(math.floor((minx - ox) / cs)))
    ix1 = min(nx, int(math.ceil((maxx - ox) / cs)))
    iz0 = max(0, int(math.floor((minz - oz) / cs)))
    iz1 = min(nz, int(math.ceil((maxz - oz) / cs)))

    cx_target = (minx + maxx) / 2
    cz_target = (minz + maxz) / 2
    best = None
    best_d = float("inf")
    for ix in range(ix0, ix1):
        row = rows[ix]
        cx = ox + (ix + 0.5) * cs
        for iz in range(iz0, iz1):
            if row[iz] != 'N':
                continue
            cz = oz + (iz + 0.5) * cs
            d = (cx - cx_target) ** 2 + (cz - cz_target) ** 2
            if d < best_d:
                best_d = d
                best = (ix, iz, cx, cz)
    return best


def stair_collider_segments(blockers, source_path):
    """Player-height slice segments of the stair mesh collider matching source_path.
    Returns list of (ax, az, bx, bz, plane_y), or [] if not found."""
    for m in blockers.get("MeshColliders", []):
        if m.get("Path") != source_path:
            continue
        segs = (m.get("Footprint") or {}).get("Segments") or []
        out = []
        for s in segs:
            py = s.get("PlaneY")
            if py is None:
                continue
            out.append((s["AX"], s["AZ"], s["BX"], s["BZ"], py))
        return out
    return []


def landing_from_slice(floor, segments, floor_y):
    """The stair landing on `floor`: centroid of the stair collider's slice segments
    nearest that floor's Y, snapped to the nearest navigable cell. Falls back to None
    if no slice near that plane or no navigable cell nearby. This finds the true END
    of the stair run on each floor (bottom vs top), not the footprint centre."""
    pts = []
    for ax, az, bx, bz, py in segments:
        if abs(py - floor_y) <= STAIR_SLICE_PLANE_TOL:
            pts.append((ax, az)); pts.append((bx, bz))
    if not pts:
        return None
    cx = sum(p[0] for p in pts) / len(pts)
    cz = sum(p[1] for p in pts) / len(pts)
    frame = floor["frame"]
    ox, oz, cs = frame["origin_x"], frame["origin_z"], frame["cell_size"]
    nx, nz = frame["nx"], frame["nz"]
    rows = floor["bitmap_rows"]
    # Spiral out from the centroid cell to the nearest navigable cell.
    ccx = int((cx - ox) / cs); ccz = int((cz - oz) / cs)
    max_r = int(math.ceil(3.0 / cs))
    for r in range(0, max_r + 1):
        for dx in range(-r, r + 1):
            for dz in range(-r, r + 1):
                if max(abs(dx), abs(dz)) != r:
                    continue
                ix, iz = ccx + dx, ccz + dz
                if 0 <= ix < nx and 0 <= iz < nz and rows[ix][iz] == 'N':
                    return (ix, iz, ox + (ix + 0.5) * cs, oz + (iz + 0.5) * cs)
    return None


def ramp_path_between_landings(g_world, u_world, gy, uy):
    """Synthesize an ordered bottom->top polyline of (x, y, z) points between the two
    resolved stair landings by linear interpolation in X, Z, and Y.

    WHY interpolate instead of trace the collider: the blocker export only slices the
    stair collider at the two FLOOR planes (PlaneY ∈ {ground, upper}) — there is no
    mid-incline cross-section in the data to sample (confirmed: the Stairs collider
    exports segments only at Y≈0.5/12.5/13.5). The Date Everything staircase is a
    single straight diagonal run (one 131m² mesh, ~18.8m along X at near-constant Z),
    so a straight interpolation faithfully traces it. The point of the polyline is to
    give the follower an XZ-PROGRESSING line to pursue up the run instead of one
    18.8m diagonal jump across the stairwell (the cause of the stair loop/stall): the
    player walks the diagonal while Y rises smoothly, rather than being told to reach
    a waypoint stacked far away with no intermediate heading.

    Emits one interior sample per ~RAMP_SAMPLE_BIN_M of rise. Returns >= 2 points
    ordered bottom->top, endpoints exactly the resolved landings so the path meets
    the floor-plane A* nodes."""
    gx, gz = g_world
    ux, uz = u_world
    rise = abs(uy - gy)
    n = max(2, int(round(rise / RAMP_SAMPLE_BIN_M)) + 1)
    path = []
    for i in range(n):
        t = i / (n - 1)
        path.append([
            round(gx + (ux - gx) * t, 3),
            round(gy + (uy - gy) * t, 3),
            round(gz + (uz - gz) * t, 3),
        ])
    return path


def main():
    bake = json.load(open(BAKE, encoding="utf-8"))
    walk = json.load(open(WALK, encoding="utf-8"))
    nav = json.load(open(NAV, encoding="utf-8"))
    blockers = json.load(open(BLOCK, encoding="utf-8"))

    ground = find_floor(bake, "ground")
    upper = find_floor(bake, "upper")
    if ground is None or upper is None:
        raise RuntimeError("ground/upper floor missing from bake")

    gy = ground["floor_y"]
    uy = upper["floor_y"]

    # Find spanning candidate surfaces
    candidates = []
    for s in walk["WalkableSurfaces"]:
        if s["SlopeKind"] not in ("stairs", "ramp"):
            continue
        fp = s["Footprint"]
        if abs(fp["CenterX"]) > 200 or abs(fp["CenterZ"]) > 200:
            continue
        area = fp["AreaSqM"]
        if area < MIN_STAIR_AREA or area > MAX_STAIR_AREA:
            continue
        # Must touch ground AND upper within tolerance
        if s["BottomY"] > gy + FLOOR_TOUCH_TOL: continue
        if s["TopY"] < uy - FLOOR_TOUCH_TOL: continue
        # Path-name filter: the walkable exporter mis-tags walls/fireplaces as
        # stairs by vertical extent alone. Real stair geometry is named.
        if not NAME_PATTERN.search(s.get("Path","")):
            continue
        candidates.append(s)

    edges = []
    rejected = []
    for s in candidates:
        fp = s["Footprint"]
        # Prefer slice-plane landings (true ends of the stair run); fall back to the
        # footprint-centre method only if the collider has no usable slice segments.
        segments = stair_collider_segments(blockers, s.get("Path", ""))
        g_cell = landing_from_slice(ground, segments, gy) if segments else None
        u_cell = landing_from_slice(upper, segments, uy) if segments else None
        landing_source = "slice_plane"
        if g_cell is None:
            g_cell = navigable_cell_in_footprint(ground, fp); landing_source = "footprint_center"
        if u_cell is None:
            u_cell = navigable_cell_in_footprint(upper, fp); landing_source = "footprint_center"
        edge_record = {
            "landing_source": landing_source,
            "kind": "stairs" if s["SlopeKind"] == "stairs" else "ramp",
            "source_path": s["Path"],
            "source_slope_kind": s["SlopeKind"],
            "source_area_sqm": round(fp["AreaSqM"], 2),
            "source_bottom_y": round(s["BottomY"], 3),
            "source_top_y": round(s["TopY"], 3),
            "footprint_xz": [round(fp["MinX"],3), round(fp["MinZ"],3),
                             round(fp["MaxX"],3), round(fp["MaxZ"],3)],
        }
        if g_cell is None or u_cell is None:
            edge_record["error"] = "no_navigable_cell_under_footprint"
            edge_record["ground_cell"] = g_cell
            edge_record["upper_cell"] = u_cell
            rejected.append(edge_record)
            continue
        gix, giz, gcx, gcz = g_cell
        uix, uiz, ucx, ucz = u_cell
        edge_record["ground"] = {
            "cell": [gix, giz], "world_xz": [round(gcx,3), round(gcz,3)],
            "floor_y": gy,
        }
        edge_record["upper"] = {
            "cell": [uix, uiz], "world_xz": [round(ucx,3), round(ucz,3)],
            "floor_y": uy,
        }
        # Ramp polyline: the ordered bottom->top run of (x,y,z) samples the follower
        # walks up, so it has a real XZ-progressing line instead of one diagonal jump
        # across the stairwell (the cause of the stair loop/stall). Falls back to the
        # bare two-endpoint edge when the slices are too coarse to improve on it.
        ramp_path = ramp_path_between_landings((gcx, gcz), (ucx, ucz), gy, uy)
        if ramp_path:
            edge_record["path"] = ramp_path
        # Cost estimate: length along the ramp polyline if we have one, else the
        # straight-line 3D distance between endpoints.
        if ramp_path:
            total = 0.0
            for i in range(len(ramp_path) - 1):
                a, b = ramp_path[i], ramp_path[i + 1]
                total += math.sqrt((b[0]-a[0])**2 + (b[1]-a[1])**2 + (b[2]-a[2])**2)
            edge_record["cost_m"] = round(total, 3)
        else:
            dxz = math.hypot(ucx - gcx, ucz - gcz)
            dy = abs(uy - gy)
            edge_record["cost_m"] = round(math.hypot(dxz, dy), 3)
        edges.append(edge_record)

    # Crawlspace teleporter
    teleporter_edges = []
    if nav.get("Teleporters"):
        t = nav["Teleporters"][0]
        down = t["LocationDown"]
        up = t["LocationUp"]
        teleporter_edges.append({
            "kind": "teleporter",
            "source_name": t["Name"],
            "down": {
                "world_xyz": [round(down["Position"]["x"],3),
                              round(down["Position"]["y"],3),
                              round(down["Position"]["z"],3)],
                "note": "crawlspace floor not in walkable bake; endpoint deferred",
            },
            "up": {
                "world_xyz": [round(up["Position"]["x"],3),
                              round(up["Position"]["y"],3),
                              round(up["Position"]["z"],3)],
            },
            "cost_m": 0.0,
            "note": "executor triggers teleporter interaction; distance not walked",
        })

    bake["inter_floor_edges"] = {
        "stair_ramp": edges,
        "stair_ramp_rejected": rejected,
        "teleporter": teleporter_edges,
        "params": {
            "max_stair_area_sqm": MAX_STAIR_AREA,
            "min_stair_area_sqm": MIN_STAIR_AREA,
            "floor_touch_tolerance_m": FLOOR_TOUCH_TOL,
        },
    }
    BAKE.write_text(json.dumps(bake, indent=2), encoding="utf-8")

    print(f"Inter-floor stair/ramp edges: {len(edges)}")
    for e in edges:
        print(f"  {e['kind']}  area={e['source_area_sqm']:.1f}  "
              f"g={e['ground']['world_xz']}  u={e['upper']['world_xz']}  "
              f"cost={e['cost_m']}  path={e['source_path'][-60:]}")
    print(f"Rejected (no nav cell under footprint): {len(rejected)}")
    for e in rejected:
        print(f"  {e['source_slope_kind']}  area={e['source_area_sqm']:.1f}  "
              f"path={e['source_path'][-80:]}")
    print(f"Teleporter edges: {len(teleporter_edges)}")
    for e in teleporter_edges:
        print(f"  {e['source_name']}  down={e['down']['world_xyz']}  up={e['up']['world_xyz']}")


if __name__ == "__main__":
    main()
