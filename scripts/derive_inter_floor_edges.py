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

# Stair surface filters
MAX_STAIR_AREA = 200.0        # exclude wall-meshes mis-tagged as stairs
MIN_STAIR_AREA = 1.0          # exclude micro-segments
FLOOR_TOUCH_TOL = 1.5         # |surface_endY - floor_Y| ≤ this counts as "touching"
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


def main():
    bake = json.load(open(BAKE, encoding="utf-8"))
    walk = json.load(open(WALK, encoding="utf-8"))
    nav = json.load(open(NAV, encoding="utf-8"))

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
        g_cell = navigable_cell_in_footprint(ground, fp)
        u_cell = navigable_cell_in_footprint(upper, fp)
        edge_record = {
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
        # Cost estimate: straight-line 3D distance between endpoints
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
