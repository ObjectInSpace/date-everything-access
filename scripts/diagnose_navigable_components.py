"""Diagnose connected-component splits in the object-first navigation bake.

This script is read-only. It compares the snapped start cell, inter-floor
stair endpoints, and target interaction goal cells against same-floor
navigable components, then reports nearest component gaps and likely static
blockers around those gaps.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from collections import deque
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import plan_object_route as routes  # noqa: E402

BLOCKERS = ROOT / "artifacts/navigation/thirdpersongreybox-blockers.json"


NEIGHBORS_8 = [
    (-1, -1), (-1, 0), (-1, 1),
    (0, -1),           (0, 1),
    (1, -1),  (1, 0),  (1, 1),
]


def label_components(floor: routes.Floor):
    labels = [[-1] * floor.nz for _ in range(floor.nx)]
    sizes = []
    for ix in range(floor.nx):
        for iz in range(floor.nz):
            if labels[ix][iz] != -1 or not floor.navigable(ix, iz):
                continue
            comp_id = len(sizes)
            labels[ix][iz] = comp_id
            q = deque([(ix, iz)])
            count = 0
            while q:
                cx, cz = q.popleft()
                count += 1
                for dx, dz in NEIGHBORS_8:
                    nx = cx + dx
                    nz = cz + dz
                    if not floor.in_bounds(nx, nz):
                        continue
                    if labels[nx][nz] != -1 or not floor.navigable(nx, nz):
                        continue
                    # Corner-cut prevention, mirroring Planner.neighbors: a
                    # diagonal connection requires both orthogonal cells open, so
                    # components reflect what the planner can actually traverse
                    # (no sub-capsule pinhole leaks). See
                    # [[project-navigation-executor-corner-stall]].
                    if dx != 0 and dz != 0:
                        if not (floor.navigable(cx + dx, cz) and floor.navigable(cx, cz + dz)):
                            continue
                    labels[nx][nz] = comp_id
                    q.append((nx, nz))
            sizes.append(count)
    return labels, sizes


def component_cells(labels, comp_id):
    for ix, row in enumerate(labels):
        for iz, value in enumerate(row):
            if value == comp_id:
                yield ix, iz


def boundary_cells(floor, labels, comp_id):
    out = []
    for ix, iz in component_cells(labels, comp_id):
        for dx, dz in NEIGHBORS_8:
            nx = ix + dx
            nz = iz + dz
            if not floor.in_bounds(nx, nz) or labels[nx][nz] != comp_id:
                out.append((ix, iz))
                break
    return out


def nearest_boundary_gap(floor, labels, comp_a, comp_b):
    a_cells = boundary_cells(floor, labels, comp_a)
    b_cells = boundary_cells(floor, labels, comp_b)
    if not a_cells or not b_cells:
        return None

    bucket_size = 10
    buckets = {}
    for ix, iz in b_cells:
        key = (ix // bucket_size, iz // bucket_size)
        buckets.setdefault(key, []).append((ix, iz))

    best = None
    best_d2 = math.inf
    for ax, az in a_cells:
        bx0 = ax // bucket_size
        bz0 = az // bucket_size
        search_r = int(math.ceil(math.sqrt(best_d2) / bucket_size)) if best_d2 < math.inf else 999999
        if search_r == 999999:
            candidate_keys = buckets.keys()
        else:
            candidate_keys = [
                (bx0 + dx, bz0 + dz)
                for dx in range(-search_r - 1, search_r + 2)
                for dz in range(-search_r - 1, search_r + 2)
            ]
        for key in candidate_keys:
            for bx, bz in buckets.get(key, ()):
                d2 = (ax - bx) ** 2 + (az - bz) ** 2
                if d2 < best_d2:
                    best_d2 = d2
                    best = (ax, az, bx, bz)
    if best is None:
        return None
    ax, az, bx, bz = best
    aw = floor.cell_to_world(ax, az)
    bw = floor.cell_to_world(bx, bz)
    return {
        "from_cell": [ax, az],
        "to_cell": [bx, bz],
        "from_world": [round(aw[0], 3), round(aw[1], 3)],
        "to_world": [round(bw[0], 3), round(bw[1], 3)],
        "distance_m": round(math.hypot(aw[0] - bw[0], aw[1] - bw[1]), 3),
        "midpoint": [(aw[0] + bw[0]) / 2.0, (aw[1] + bw[1]) / 2.0],
    }


def blocker_hits_near(wx, wz, floor_y, radius_m):
    if not BLOCKERS.exists():
        return []
    data = json.loads(BLOCKERS.read_text(encoding="utf-8-sig"))
    y_lo = floor_y - 0.25
    y_hi = floor_y + 2.5
    hits = []
    for b in data.get("NavigationBlockers", []):
        if b.get("IsTrigger") or b.get("IsDoorConnector") or b.get("IsTeleporterConnector"):
            continue
        if b.get("TopY", -math.inf) < y_lo or b.get("BottomY", math.inf) > y_hi:
            continue
        bb = b.get("Bounds2D")
        if not bb:
            continue
        dx = max(bb["MinX"] - wx, 0.0, wx - bb["MaxX"])
        dz = max(bb["MinZ"] - wz, 0.0, wz - bb["MaxZ"])
        dist = math.hypot(dx, dz)
        if dist <= radius_m:
            hits.append({
                "distance_m": round(dist, 3),
                "path": b.get("Path") or b.get("GameObjectName") or b.get("Name"),
                "type": b.get("ColliderType"),
                "bounds2d": [
                    round(bb["MinX"], 3), round(bb["MinZ"], 3),
                    round(bb["MaxX"], 3), round(bb["MaxZ"], 3),
                ],
            })
    hits.sort(key=lambda h: (h["distance_m"], h["path"] or ""))
    return hits[:10]


def node_component(labels, node):
    return labels[node[1]][node[2]]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", default="20031", help="GameObjectId or path substring")
    ap.add_argument("--start-xz", nargs=2, type=float, metavar=("X", "Z"))
    ap.add_argument("--start-floor", default="ground", choices=("ground", "upper"))
    ap.add_argument("--interaction-radius", type=float)
    args = ap.parse_args()

    bake = routes.load_bake()
    planner = routes.Planner(bake)
    components = {}
    for label, floor in planner.floors.items():
        labels, sizes = label_components(floor)
        components[label] = (labels, sizes)
        largest = max(sizes) if sizes else 0
        print(f"{label}: components={len(sizes)} largest_cells={largest} navigable_cells={sum(sizes)}")

    start_floor = planner.floors[args.start_floor]
    if args.start_xz is None:
        start_cell = start_floor.nearest_navigable(0.0, 0.0, max_radius_m=15.0)
    else:
        start_cell = start_floor.nearest_navigable(args.start_xz[0], args.start_xz[1], max_radius_m=4.0)
    if start_cell is None:
        raise SystemExit("No start cell resolved")
    start_node = (args.start_floor, start_cell[0], start_cell[1])
    start_comp = node_component(components[args.start_floor][0], start_node)
    sx, sz = start_floor.cell_to_world(start_cell[0], start_cell[1])
    print(f"start: floor={args.start_floor} cell={list(start_cell)} world=({sx:.3f},{sz:.3f}) component={start_comp} size={components[args.start_floor][1][start_comp]}")

    items = routes.load_interactables()
    target = routes.find_interactable(items, args.target)
    if target is None:
        raise SystemExit(f"No interactable matches {args.target!r}")
    tx = target["Position"]["x"]
    ty = target["Position"]["y"]
    tz = target["Position"]["z"]
    tfloor_label = planner._floor_for_y(ty)
    if tfloor_label is None:
        raise SystemExit(f"Target Y={ty} is not on a baked floor")
    tfloor = planner.floors[tfloor_label]
    radius = args.interaction_radius or min(target.get("InteractionRadius", 1.0), 2.0)
    goals = routes.goal_cells_around(tfloor, tx, tz, radius)
    target_labels, target_sizes = components[tfloor_label]
    goal_comps = {}
    for ix, iz in goals:
        comp_id = target_labels[ix][iz]
        goal_comps[comp_id] = goal_comps.get(comp_id, 0) + 1
    print(f"target: id={target.get('GameObjectId')} name={target.get('GameObjectName')} floor={tfloor_label} pos=({tx:.3f},{tz:.3f}) radius={radius:.2f} goal_cells={len(goals)}")
    for comp_id, count in sorted(goal_comps.items(), key=lambda item: (-item[1], item[0]))[:10]:
        print(f"  goal_component={comp_id} goal_cells={count} component_size={target_sizes[comp_id]}")

    for edge in bake.get("inter_floor_edges", {}).get("stair_ramp", []):
        for endpoint_name in ("ground", "upper"):
            endpoint = edge[endpoint_name]
            label = endpoint_name
            node = (label, endpoint["cell"][0], endpoint["cell"][1])
            comp_id = node_component(components[label][0], node)
            size = components[label][1][comp_id]
            print(f"stair_{endpoint_name}: cell={endpoint['cell']} world={endpoint['world_xz']} component={comp_id} size={size} path={edge.get('source_path')}")

    if start_node[0] == "ground":
        stair_edges = bake.get("inter_floor_edges", {}).get("stair_ramp", [])
        if stair_edges:
            stair_ground = stair_edges[0]["ground"]["cell"]
            stair_ground_comp = components["ground"][0][stair_ground[0]][stair_ground[1]]
            if stair_ground_comp != start_comp:
                gap = nearest_boundary_gap(start_floor, components["ground"][0], start_comp, stair_ground_comp)
                print_gap("ground start-to-stair gap", start_floor, gap)

            stair_upper = stair_edges[0]["upper"]["cell"]
            stair_upper_comp = components["upper"][0][stair_upper[0]][stair_upper[1]]
            for comp_id in sorted(goal_comps):
                if comp_id != stair_upper_comp:
                    gap = nearest_boundary_gap(planner.floors["upper"], components["upper"][0], stair_upper_comp, comp_id)
                    print_gap(f"upper stair-to-goal component {comp_id} gap", planner.floors["upper"], gap)


def print_gap(label, floor, gap):
    if gap is None:
        print(f"{label}: no boundary gap found")
        return
    print(f"{label}: distance={gap['distance_m']}m from={gap['from_cell']} {gap['from_world']} to={gap['to_cell']} {gap['to_world']}")
    mx, mz = gap["midpoint"]
    hits = blocker_hits_near(mx, mz, floor.floor_y, radius_m=max(1.0, gap["distance_m"] + 0.5))
    for hit in hits:
        print(f"  blocker distance={hit['distance_m']} type={hit['type']} path={hit['path']} bounds={hit['bounds2d']}")


if __name__ == "__main__":
    main()
