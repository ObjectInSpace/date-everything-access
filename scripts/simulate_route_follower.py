"""Offline simulation of the in-game route follower (pure-pursuit + alignment
speed gate), to predict where autowalk would STALL against a wall — without
launching the game.

Why this exists: validate_offline_routes.py checks the planner's straight
segments stay on navigable cells. But the executor doesn't walk the straight
segments — it follows a pure-pursuit CURVE with a facing-alignment speed gate
(AccessibilityWatcher.ApplyAutoWalkSimpleRoute + SimpleNavBridge.PursuitTarget).
That curve can swing wider than a segment on corners and graze a wall the
straight-segment check passes. This script mirrors the C# follower kinematically
and flags any simulated player position that lands on a NON-navigable cell — the
bake's dilated-blocked region already encodes the 0.4m capsule clearance, so a
position there is exactly where the real Rigidbody would stall against a
collider (the same condition the in-game progress-timeout reports as a blocker).

It is an APPROXIMATION (no Unity physics/momentum), but it reproduces the
controller's geometry and catches the corner/doorway grazes that bite in-game.

Usage:
  uv run python scripts/simulate_route_follower.py --target 2200 --start-xz -23 -12.3 --start-floor upper
  uv run python scripts/simulate_route_follower.py --sweep artifacts/navigation/sweep/default
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import plan_object_route as planner_mod  # noqa: E402

# --- Controller constants, mirroring AccessibilityWatcher.cs / SimpleNavBridge.cs ---
PURSUIT_LOOKAHEAD_M = 1.5      # AutoWalkPursuitLookahead
LOOK_SCALE_DEG = 45.0         # AutoWalkLookScaleDegrees (max turn input at this angle)
# Kinematic model params (the game is Rigidbody-driven; these approximate it):
STEP_DT = 1.0 / 30.0          # sim tick ~ a frame
MOVE_SPEED_MPS = 6.0          # observed autowalk speed (log velocity ~6-10 m/s; conservative)
MAX_TURN_DEG_PER_S = 360.0    # player yaw rate (DORotate is fast; generous)
WAYPOINT_ARRIVE_M = 1.35      # AutoWalkBridge WaypointArrivalRadius
TARGET_ARRIVE_M = 0.6         # close enough to the final waypoint
MAX_TICKS = 4000


def project_param_xz(ax, az, bx, bz, px, pz):
    abx, abz = bx - ax, bz - az
    l2 = abx * abx + abz * abz
    if l2 <= 1e-6:
        return 0.0
    t = ((px - ax) * abx + (pz - az) * abz) / l2
    return max(0.0, min(1.0, t))


def pursuit_target(wps, wp_index, px, pz, lookahead):
    """Mirror SimpleNavBridge.PursuitTarget: project player onto the remaining
    polyline, return the point `lookahead` m forward along it."""
    n = len(wps)
    start_seg = max(0, wp_index - 1)
    if start_seg >= n - 1:
        return wps[-1]
    best_seg, best_t, best_d2 = start_seg, 0.0, math.inf
    for i in range(start_seg, n - 1):
        ax, az = wps[i]
        bx, bz = wps[i + 1]
        t = project_param_xz(ax, az, bx, bz, px, pz)
        cx, cz = ax + (bx - ax) * t, az + (bz - az) * t
        d2 = (cx - px) ** 2 + (cz - pz) ** 2
        if d2 < best_d2:
            best_d2, best_seg, best_t = d2, i, t
    rem = lookahead
    seg = best_seg
    cx = wps[seg][0] + (wps[seg + 1][0] - wps[seg][0]) * best_t
    cz = wps[seg][1] + (wps[seg + 1][1] - wps[seg][1]) * best_t
    while seg < n - 1:
        ex, ez = wps[seg + 1]
        dx, dz = ex - cx, ez - cz
        sl = math.hypot(dx, dz)
        if sl >= rem:
            return (ex, ez) if sl <= 1e-4 else (cx + dx / sl * rem, cz + dz / sl * rem)
        rem -= sl
        seg += 1
        cx, cz = wps[seg]
    return wps[-1]


# Radius around a door-tagged position within which an off-navigable position is
# attributed to the door opening rather than a wall stall. The in-game executor
# opens any door on the route as it reaches it (see
# [[project-navigation-door-handling-rules]]), so the capsule legitimately
# overlaps the closed-pose doorway cells for a moment while the door swings. A
# little wider than the planner's DOOR_TAG_RADIUS_M (0.8m) to cover the threshold
# span the player crosses during the swing.
DOOR_OPEN_SPAN_M = 1.5


def simulate(floor, wps, door_xz=None):
    """Run the follower from wps[0] to wps[-1]. Returns (status, stall_pos, trace).

    `door_xz` is the list of (x, z) positions of doors this route is tagged to
    pass through. Near one of those, an off-navigable position is treated as the
    executor opening the door (not a wall stall), so the stall counter resets —
    this is how a route through a door the player must open gets verified rather
    than false-flagged at the doorway."""
    if len(wps) < 2:
        return "trivial", None, []
    door_xz = door_xz or []
    px, pz = wps[0]
    facing = math.atan2(wps[1][1] - pz, wps[1][0] - px)  # start facing 1st segment
    wp_index = 1
    offcells = []
    for _ in range(MAX_TICKS):
        # Advance discrete waypoint when within arrival radius (mirrors TryAdvanceWaypoint).
        while wp_index < len(wps) - 1 and math.hypot(wps[wp_index][0] - px, wps[wp_index][1] - pz) <= WAYPOINT_ARRIVE_M:
            wp_index += 1
        # Arrival.
        if math.hypot(wps[-1][0] - px, wps[-1][1] - pz) <= TARGET_ARRIVE_M:
            return "arrived", None, offcells
        # Steer toward pursuit lookahead point.
        tx, tz = pursuit_target(wps, wp_index, px, pz, PURSUIT_LOOKAHEAD_M)
        desired = math.atan2(tz - pz, tx - px)
        # Turn toward desired at limited rate.
        dyaw = (desired - facing + math.pi) % (2 * math.pi) - math.pi
        max_step = math.radians(MAX_TURN_DEG_PER_S) * STEP_DT
        facing += max(-max_step, min(max_step, dyaw))
        # Alignment speed gate: move = clamp01(cos(turn)).
        align = max(0.0, math.cos(dyaw))
        speed = MOVE_SPEED_MPS * align
        px += math.cos(facing) * speed * STEP_DT
        pz += math.sin(facing) * speed * STEP_DT
        # Check the player position against the navigable bitmap.
        ix, iz = floor.world_to_cell(px, pz)
        if not (floor.in_bounds(ix, iz) and floor.navigable(ix, iz)):
            # If we're at a door the route passes through, the executor is
            # opening it — not a wall stall. Reset the counter and proceed.
            near_door = any(math.hypot(px - dx, pz - dz) <= DOOR_OPEN_SPAN_M
                            for dx, dz in door_xz)
            if near_door:
                offcells = []
                continue
            offcells.append((round(px, 2), round(pz, 2)))
            # If we pile up off-navigable for a stretch, call it a stall.
            if len(offcells) >= 15:
                return "stall", (round(px, 2), round(pz, 2)), offcells
    return "timeout", (round(px, 2), round(pz, 2)), offcells


def upper_or_floor_wps(route, planner):
    """Extract per-floor waypoint runs from a route (each contiguous same-floor run)."""
    runs = []
    cur_floor = None
    cur = []
    for w in route["waypoints"]:
        fl = w["floor"]
        if fl != cur_floor:
            if len(cur) >= 2:
                runs.append((cur_floor, cur))
            cur_floor = fl
            cur = []
        cur.append((w["world_xz"][0], w["world_xz"][1]))
    if len(cur) >= 2:
        runs.append((cur_floor, cur))
    return runs


def _door_positions_by_floor(route, planner):
    """Collect the world XZ of every door this route is tagged to pass through,
    grouped by floor label. Door tags live on route segments (tag_doors); their
    XZ comes from the planner's door_positions() table keyed by id/name."""
    by_floor = {}
    pos_by_id = {d["id"]: d["xz"] for d in planner_mod.door_positions()}
    pos_by_name = {d["name"]: d["xz"] for d in planner_mod.door_positions()}
    for seg in route.get("segments", []):
        fl = seg.get("from", {}).get("floor")
        if fl is None:
            continue
        for d in seg.get("doors", []):
            xz = pos_by_id.get(d.get("id")) or pos_by_name.get(d.get("name"))
            if xz is not None:
                by_floor.setdefault(fl, []).append((xz[0], xz[1]))
    return by_floor


def run_one(planner, route):
    runs = upper_or_floor_wps(route, planner)
    doors_by_floor = _door_positions_by_floor(route, planner)
    results = []
    for fl_label, wps in runs:
        floor = planner.floors[fl_label]
        status, stall, off = simulate(floor, wps, door_xz=doors_by_floor.get(fl_label, []))
        results.append((fl_label, status, stall, len(off)))
    return results


def _sweep_planner_config(sweep_dir):
    """Read the door / state-wall posture from a sweep manifest so the follower's
    Planner matches the one the routes were planned under. Manifests written before
    this field existed default to the dispersed-sweep posture (every door open,
    every state-wall released), which is how those route files were planned."""
    manifest_path = sweep_dir / "sweep_manifest.json"
    if not manifest_path.exists():
        return "all", "all"
    params = json.loads(manifest_path.read_text()).get("params", {})
    doors_open = params.get("doors_open", "all")
    state_walls_open = params.get("state_walls_open", "all")
    return doors_open, state_walls_open


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--target")
    ap.add_argument("--start-xz", nargs=2, type=float, metavar=("X", "Z"))
    ap.add_argument("--start-floor", default="ground", choices=("ground", "upper"))
    ap.add_argument("--sweep", help="Directory of route.*.json to batch-simulate")
    args = ap.parse_args()

    bake = planner_mod.load_bake()

    if args.sweep:
        sweep_dir = Path(args.sweep)
        # Mirror the door / state-wall posture the routes were planned under, read
        # from the sweep manifest. A bare Planner(bake) closes every door, so a
        # doors-open sweep would false-stall at every doorway it routes through.
        doors_open, state_walls_open = _sweep_planner_config(sweep_dir)
        planner = planner_mod.Planner(
            bake, doors_open=doors_open, state_walls_open=state_walls_open
        )
        files = sorted(sweep_dir.glob("route.*.json"))
        stalls = 0
        for f in files:
            route = json.loads(f.read_text())
            if route.get("status") != "ok":
                continue
            for fl, status, stall, noff in run_one(planner, route):
                if status in ("stall", "timeout"):
                    stalls += 1
                    print(f"{f.name}: {fl} {status} at {stall} (off-cells={noff})")
        print(f"\n{len(files)} routes simulated, {stalls} predicted stalls.")
        return

    planner = planner_mod.Planner(bake)
    if not args.target:
        ap.error("provide --target or --sweep")
    route = planner_mod.plan(args.target, start_xz=args.start_xz, start_floor=args.start_floor)
    if route.get("status") != "ok":
        raise SystemExit(f"plan failed: {route.get('status')}")
    results = run_one(planner, route)
    if not results:
        print("route too short to simulate (single-waypoint / start==goal)")
        return
    for fl, status, stall, noff in results:
        flag = "" if status == "arrived" else "  <-- PREDICTED PROBLEM"
        print(f"floor={fl} status={status} stall={stall} off_navigable_cells={noff}{flag}")


if __name__ == "__main__":
    main()
