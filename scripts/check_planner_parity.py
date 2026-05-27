"""Offline parity check between the Python planner (scripts/plan_object_route.py)
and the C# runtime planner (SimpleNavPlanner.cs).

Why this exists: the sweep manifest and offline route validation use the Python
planner, but the game runs the C# planner. They implement A* + smoothing
independently. If one fixes a bug the other doesn't, the sweep stops predicting
runtime behavior and we discover the gap only by stalling at runtime.

How it works:

  1. Read every captured C# plan from artifacts/navigation/c_sharp_routes/*.json.
     Each file is one C# plan output (target name, start XYZ, start floor,
     waypoint list, door overlay state).
  2. For each capture, re-plan with the Python planner using the same start,
     target, and overlay state.
  3. Compare the waypoint sequences cell-by-cell. Report any divergence.

Capture format (one file per plan, written by SimpleNavPlanner at runtime —
gated behind a settings flag so we don't spam disk every plan):

  {
    "target_name": "SM_Gift_Lid_MODEL_UPDATE_ORIGIN",
    "target_position": [20.17, 0.78, 0.00],
    "target_interaction_radius": 7.5,
    "start_position": [12.29, -0.50, 7.56],
    "start_floor": "ground",
    "doors_open": ["Doors_Office"],
    "state_walls_open": [],
    "waypoints": [
      [12.29, -0.50, 7.56],
      [11.29, -0.50, 7.56],
      ...
    ],
    "cost_m": 14.10
  }

If artifacts/navigation/c_sharp_routes/ doesn't exist (no captures yet) the
script exits 0 silently — the parity check is a no-op until runtime starts
producing captures. That keeps it safe to add to CI / bake pipelines today
without a hard dependency on runtime data.

CLI:
  python scripts/check_planner_parity.py
      [--captures DIR]
      [--tolerance-m 0.21]   # one cell at default cell_size
      [--max-report 20]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# Default capture locations, in priority order:
# 1. BepInEx plugins dir (where the live mod writes when CaptureNavRoutes=true)
# 2. artifacts/navigation/c_sharp_routes (manual copies for CI / archiving)
DEFAULT_CAPTURE_DIRS = [
    Path("D:/SteamLibrary/steamapps/Common/Date Everything/BepInEx/plugins/c_sharp_routes"),
    ROOT / "artifacts" / "navigation" / "c_sharp_routes",
]

sys.path.insert(0, str(Path(__file__).resolve().parent))
import plan_object_route as planner_mod  # noqa: E402


def compare_waypoints(py_wps, cs_wps, tol):
    """Return list of (index, py_wp, cs_wp, dist) for divergent waypoints.

    Both lists are world XYZ tuples. Compared by index after padding the
    shorter list with None. Distance is XZ-plane Euclidean; Y is informational
    only because the planner emits floor_y, not exact target Y."""
    diffs = []
    n = max(len(py_wps), len(cs_wps))
    for i in range(n):
        py = py_wps[i] if i < len(py_wps) else None
        cs = cs_wps[i] if i < len(cs_wps) else None
        if py is None or cs is None:
            diffs.append((i, py, cs, None))
            continue
        dxz = ((py[0] - cs[0]) ** 2 + (py[2] - cs[2]) ** 2) ** 0.5
        if dxz > tol:
            diffs.append((i, py, cs, dxz))
    return diffs


def replan_with_python(capture, bake):
    """Run the Python planner with the same inputs as the captured C# plan."""
    planner = planner_mod.Planner(
        bake,
        doors_open=capture.get("doors_open") or None,
        state_walls_open=capture.get("state_walls_open") or None,
    )

    start = capture["start_position"]
    target_pos = capture["target_position"]
    target_y = target_pos[1]
    start_floor = capture.get("start_floor") or "ground"

    # Resolve start cell same way plan() does.
    f_start = planner.floors[start_floor]
    n = f_start.nearest_navigable(start[0], start[2], max_radius_m=4.0)
    if n is None:
        return None, "python: no nav cell near start"
    start_node = (start_floor, n[0], n[1])

    # Resolve target cell(s) same way plan() does.
    target_floor_label = planner._floor_for_y(target_y)
    if target_floor_label is None:
        return None, f"python: target Y={target_y} not on a baked floor"
    radius = capture.get("target_interaction_radius") or 1.0
    radius = max(planner_mod.MIN_INTERACTION_RADIUS_M,
                 min(radius, planner_mod.MAX_INTERACTION_RADIUS_M))
    goals = planner_mod.goal_cells_around(
        planner.floors[target_floor_label], target_pos[0], target_pos[2], radius
    )
    if not goals:
        nf = planner.floors[target_floor_label].nearest_navigable(
            target_pos[0], target_pos[2], max_radius_m=4.0
        )
        if nf is None:
            return None, "python: no nav cell near target"
        goals = [nf]
    goal_nodes = [(target_floor_label, ix, iz) for ix, iz in goals]

    path, total_cost, _ = planner.astar(
        start_node, goal_nodes, target_floor_label, target_pos[0], target_pos[2]
    )
    if path is None:
        return None, "python: no_path"
    waypoints = planner_mod.smooth_path(path, planner)

    out = []
    for w in waypoints:
        floor_label, ix, iz = w
        f = planner.floors.get(floor_label)
        if f is None:
            continue
        wx, wz = f.cell_to_world(ix, iz)
        out.append((wx, f.floor_y, wz))
    return out, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--captures", type=Path, default=None,
                    help="Directory of C# capture files. Default tries the live "
                         "BepInEx plugins dir then artifacts/navigation/c_sharp_routes.")
    ap.add_argument("--tolerance-m", type=float, default=0.21,
                    help="Per-waypoint XZ tolerance in meters. Default is one cell.")
    ap.add_argument("--max-report", type=int, default=20)
    args = ap.parse_args()

    candidate_dirs = [args.captures] if args.captures else DEFAULT_CAPTURE_DIRS
    captures = []
    used_dir = None
    for d in candidate_dirs:
        if d and d.exists():
            found = sorted(d.glob("*.json"))
            if found:
                captures = found
                used_dir = d
                break
    if not captures:
        print("No C# capture files found. Enable ModConfig.CaptureNavRoutes "
              "(BepInEx/config/<plugin>.cfg, [Diagnostics] CaptureNavRoutes=true), "
              "play long enough to plan some routes, then re-run.")
        print(f"Searched: {[str(d) for d in candidate_dirs]}")
        return 0
    print(f"Reading captures from {used_dir}")

    bake = planner_mod.load_bake()

    total = 0; matched = 0; diverged = 0; errored = 0
    reports = []

    for cap_path in captures:
        total += 1
        try:
            cap = json.loads(cap_path.read_text(encoding="utf-8"))
        except Exception as e:
            errored += 1
            reports.append(f"{cap_path.name}: parse error: {e}")
            continue

        py_wps, err = replan_with_python(cap, bake)
        if err:
            errored += 1
            reports.append(f"{cap_path.name}: {err}")
            continue

        cs_wps = [(w[0], w[1], w[2]) for w in cap.get("waypoints", [])]
        diffs = compare_waypoints(py_wps, cs_wps, args.tolerance_m)
        if not diffs:
            matched += 1
            continue
        diverged += 1
        reports.append(
            f"{cap_path.name}: target={cap.get('target_name')!r} "
            f"py_wps={len(py_wps)} cs_wps={len(cs_wps)} divergent={len(diffs)} "
            f"first_divergence_idx={diffs[0][0]} "
            f"py={diffs[0][1]} cs={diffs[0][2]}"
        )

    print(f"checked: {total}  matched: {matched}  diverged: {diverged}  errored: {errored}")
    if reports:
        print(f"\nFirst {min(len(reports), args.max_report)} reports:")
        for r in reports[:args.max_report]:
            print(f"  {r}")

    return 0 if (diverged == 0 and errored == 0) else 1


if __name__ == "__main__":
    raise SystemExit(main())
