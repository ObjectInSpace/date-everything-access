"""Step O6 of [[project-navigation-object-first-plan]]: offline coverage-sweep planner.

Generates the route catalogue the in-game O6 harness will consume. For a single fixed
start position, plans a route to one representative cell per 1m^2 grid cluster of the
bake's navigable region (both floors). Writes:

    artifacts/navigation/sweep/<run-id>/route.<floor>.<ix>.<iz>.json    (one per planned route)
    artifacts/navigation/sweep/<run-id>/sweep_manifest.json             (index + no-path entries)

The in-game harness reads the manifest, teleports the player to the start, loads each
route, calls SimpleNavBridge.BeginRoute, then waits for the autowalk to arrive or fail.

The planner here re-uses plan_object_route's Planner / smooth_path / tag_doors so the
route schema is identical to what the C# loader already understands.

CLI:
    python scripts/sweep_coverage_planner.py
        [--start-xz X Z] [--start-floor ground|upper]
        [--grid-m 1.0]
        [--run-id default]
        [--floors ground,upper]
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SWEEP_DIR = ROOT / "artifacts" / "navigation" / "sweep"

# Make sibling scripts importable. plan_object_route.py lives next to this file.
sys.path.insert(0, str(Path(__file__).resolve().parent))
import plan_object_route as _mod  # noqa: E402


def stratified_targets(floor, grid_m):
    """Pick one representative navigable cell per grid_m x grid_m cluster on floor.

    A cluster's representative is the navigable cell closest to the cluster centre.
    Clusters with no navigable cells are skipped (the bake's `navigable` count gives
    a hard upper bound on output size)."""
    cells_per_bucket = max(1, int(round(grid_m / floor.cell_size)))
    bucket_to_cells = {}
    for ix in range(floor.nx):
        for iz in range(floor.nz):
            if not floor.navigable(ix, iz):
                continue
            key = (ix // cells_per_bucket, iz // cells_per_bucket)
            bucket_to_cells.setdefault(key, []).append((ix, iz))

    targets = []
    for (bx, bz), cells in bucket_to_cells.items():
        # Centre of bucket in cell coords.
        ccx = bx * cells_per_bucket + cells_per_bucket / 2.0
        ccz = bz * cells_per_bucket + cells_per_bucket / 2.0
        best = min(cells, key=lambda c: (c[0] - ccx) ** 2 + (c[1] - ccz) ** 2)
        targets.append(best)
    targets.sort()
    return targets


def plan_to_cell(planner, start_node, target_floor_label, target_ix, target_iz):
    """Plan a route to a specific navigable cell. Returns the same dict shape as
    Plan-ObjectRoute.plan() but with a synthetic target stanza, or a no_path entry."""
    target_floor = planner.floors[target_floor_label]
    if not target_floor.navigable(target_ix, target_iz):
        return {"status": "target_not_navigable"}
    wx, wz = target_floor.cell_to_world(target_ix, target_iz)
    goal_node = (target_floor_label, target_ix, target_iz)
    if start_node == goal_node:
        return {"status": "trivially_at_target"}

    path, total_cost, edges = planner.astar(
        start_node, [goal_node], target_floor_label, wx, wz
    )
    if path is None:
        return {"status": "no_path"}

    waypoints = _mod.smooth_path(path, planner)
    segments = _mod.tag_doors(waypoints, planner, _mod.door_positions())
    target_stanza = {
        "GameObjectId": 0,
        "Name": f"sweep_cell:{target_floor_label}:{target_ix}:{target_iz}",
        "Path": None,
        "Position": {"x": wx, "y": target_floor.floor_y, "z": wz},
        "IsDatable": False,
        "InkFileName": None,
        "InteractionRadius": planner.cell_size * 1.5,  # arrival within ~1.5 cells (~0.3m)
    }
    return {
        "status": "ok",
        "target": target_stanza,
        "start": _mod._node_to_dict(start_node, planner),
        "goal_cell_count": 1,
        "path_length_cells": len(path),
        "waypoint_count": len(waypoints),
        "total_cost_m": round(total_cost, 3),
        "waypoints": [_mod._node_to_dict(w, planner) for w in waypoints],
        "segments": segments,
        "edge_kinds_used": sorted({e.get("kind", "walk") for e in edges if e}),
        "params": {
            "cell_size_m": planner.cell_size,
            "interaction_radius_used_m": target_stanza["InteractionRadius"],
            "corner_waypoint_deg": _mod.CORNER_WAYPOINT_DEG,
            "door_tag_radius_m": _mod.DOOR_TAG_RADIUS_M,
        },
    }


def resolve_start(planner, start_xz, start_floor_label):
    """Same convention as Plan-ObjectRoute's CLI: snap to nearest navigable cell on
    the requested floor, with a small fallback radius."""
    floor = planner.floors[start_floor_label]
    if start_xz is None:
        # Default: nearest navigable to (0,0) on ground floor.
        n = floor.nearest_navigable(0.0, 0.0, max_radius_m=15.0)
    else:
        n = floor.nearest_navigable(start_xz[0], start_xz[1], max_radius_m=4.0)
    if n is None:
        raise SystemExit(f"no navigable cell near start {start_xz} on {start_floor_label}")
    return (start_floor_label, n[0], n[1])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--start-xz", nargs=2, type=float, metavar=("X", "Z"))
    ap.add_argument("--start-floor", choices=("ground", "upper"), default="ground")
    ap.add_argument("--grid-m", type=float, default=1.0,
                    help="Side length of each stratified-sampling bucket (default 1.0m).")
    ap.add_argument("--floors", default="ground,upper",
                    help="Comma-separated floor labels to sweep (default ground,upper).")
    ap.add_argument("--run-id", default="default",
                    help="Subdirectory name under artifacts/navigation/sweep/.")
    ap.add_argument("--doors-open", nargs="*", metavar="NAME",
                    help="Override the doors-open set; default opens every door.")
    ap.add_argument("--doors-closed", action="store_true",
                    help="Sweep with every door closed (matches scene-load state).")
    ap.add_argument("--state-walls-open", nargs="*", metavar="NAME",
                    help="Override the state-walls-open set; default releases every wall.")
    ap.add_argument("--state-walls-active", action="store_true",
                    help="Sweep with state-gated walls active (matches scene-load state).")
    args = ap.parse_args()

    bake = _mod.load_bake()
    # Sweep precondition: every door is open AND every state-wall released.
    # The sweep verifies max-coverage routing — "could the planner reach this
    # target if every door it needs were unlocked and every story-gate cleared."
    # Per-route door state is tagged in the artifact via tag_doors(), so
    # consumers can still see which doors a route requires.
    if args.doors_closed:
        doors_open = None
    elif args.doors_open is not None:
        doors_open = args.doors_open
    else:
        doors_open = "all"
    if args.state_walls_active:
        state_walls_open = None
    elif args.state_walls_open is not None:
        state_walls_open = args.state_walls_open
    else:
        state_walls_open = "all"
    planner = _mod.Planner(bake, doors_open=doors_open, state_walls_open=state_walls_open)

    start_node = resolve_start(planner, args.start_xz, args.start_floor)
    start_world = planner.floors[start_node[0]].cell_to_world(start_node[1], start_node[2])
    print(f"start: floor={start_node[0]} cell=({start_node[1]},{start_node[2]}) world=({start_world[0]:.3f},{start_world[1]:.3f})")

    out_dir = SWEEP_DIR / args.run_id
    out_dir.mkdir(parents=True, exist_ok=True)
    # Clear any old route files so the manifest stays in sync with disk.
    for old in out_dir.glob("route.*.json"):
        old.unlink()

    floors_to_sweep = [f.strip() for f in args.floors.split(",") if f.strip()]
    # Per-floor frame info so the in-game harness can convert player world position back
    # into (ix, iz) cells for the coverage-stamping bitmap without reloading the bake file.
    floor_frames = {}
    for label in floors_to_sweep:
        if label not in planner.floors:
            continue
        f = planner.floors[label]
        floor_frames[label] = {
            "origin_x": f.origin_x,
            "origin_z": f.origin_z,
            "cell_size": f.cell_size,
            "nx": f.nx,
            "nz": f.nz,
            "floor_y": f.floor_y,
        }

    manifest = {
        "run_id": args.run_id,
        "generated_at_unix": int(time.time()),
        "start": {
            "floor": start_node[0],
            "cell": [start_node[1], start_node[2]],
            "world_xz": [round(start_world[0], 4), round(start_world[1], 4)],
            "wx": round(start_world[0], 4),
            "wz": round(start_world[1], 4),
            "floor_y": planner.floors[start_node[0]].floor_y,
        },
        "floor_frames": floor_frames,
        "params": {
            "grid_m": args.grid_m,
            "floors": floors_to_sweep,
        },
        "entries": [],
    }

    counts = {"ok": 0, "no_path": 0, "trivially_at_target": 0, "target_not_navigable": 0}
    t0 = time.time()
    for floor_label in floors_to_sweep:
        if floor_label not in planner.floors:
            print(f"WARN: floor {floor_label!r} not in bake; skipping")
            continue
        floor = planner.floors[floor_label]
        targets = stratified_targets(floor, args.grid_m)
        nav_total = sum(1 for ix in range(floor.nx) for iz in range(floor.nz) if floor.navigable(ix, iz))
        print(f"floor {floor_label}: {len(targets)} stratified targets ({nav_total} navigable cells)")
        for (ix, iz) in targets:
            result = plan_to_cell(planner, start_node, floor_label, ix, iz)
            status = result["status"]
            counts[status] = counts.get(status, 0) + 1
            entry = {
                "floor": floor_label,
                "cell": [ix, iz],
                "world_xz": [round(floor.cell_to_world(ix, iz)[0], 4),
                             round(floor.cell_to_world(ix, iz)[1], 4)],
                "status": status,
            }
            if status == "ok":
                route_name = f"route.{floor_label}.{ix}.{iz}.json"
                route_path = out_dir / route_name
                route_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
                entry["route"] = route_name
                entry["cost_m"] = result["total_cost_m"]
                entry["waypoint_count"] = result["waypoint_count"]
                entry["edge_kinds"] = result["edge_kinds_used"]
            manifest["entries"].append(entry)

    # Global farthest-point dispersion (no cost bands).
    #
    # Banding by cost clusters early attempts in whichever region happens to host the
    # longest routes, so a single wall bug there burns dozens of attempts before any
    # other part of the house gets a turn. Instead, disperse across the whole house:
    #   - Seed with the route whose target is closest to the start (cheap warm-up).
    #   - Then farthest-point traversal against the running centroid of picked targets.
    #   - Break ties toward higher cost_m, so when two candidates are equidistant the
    #     longer route wins (long routes still credit more coverage per success).
    # No-path entries go last in stable order.
    entries = manifest["entries"]
    planned = [e for e in entries if "cost_m" in e]
    unplanned = [e for e in entries if "cost_m" not in e]
    if planned:
        sx, sz = start_world
        planned.sort(key=lambda e: (e["world_xz"][0] - sx) ** 2 + (e["world_xz"][1] - sz) ** 2)
        picked = [planned.pop(0)]
        cx = picked[0]["world_xz"][0]
        cz = picked[0]["world_xz"][1]
        while planned:
            best_idx = 0
            best_key = (-1.0, -1.0)
            for i, e in enumerate(planned):
                dx = e["world_xz"][0] - cx
                dz = e["world_xz"][1] - cz
                d2 = dx * dx + dz * dz
                key = (d2, e["cost_m"])
                if key > best_key:
                    best_key = key; best_idx = i
            nxt = planned.pop(best_idx)
            picked.append(nxt)
            n = len(picked)
            cx = ((n - 1) * cx + nxt["world_xz"][0]) / n
            cz = ((n - 1) * cz + nxt["world_xz"][1]) / n
        manifest["entries"] = picked + unplanned
    else:
        manifest["entries"] = unplanned

    manifest["counts"] = counts
    manifest["elapsed_s"] = round(time.time() - t0, 2)
    manifest_path = out_dir / "sweep_manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print(f"wrote {manifest_path}")
    print(f"counts: {counts}  elapsed: {manifest['elapsed_s']}s")


if __name__ == "__main__":
    main()
