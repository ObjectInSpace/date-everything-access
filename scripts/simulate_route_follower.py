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


# Cross-track threshold (m): mirrors SimpleNavBridge.PursuitMaxCrossTrackM. Above
# this off-line distance, steer to the projection point (back onto the corridor)
# instead of a forward lookahead, so the player converges rather than tracking
# parallel into a wall. See [[project-navigation-hall1-runtime-truth-2026-05-29]].
PURSUIT_MAX_CROSSTRACK_M = 0.6


def pursuit_target(wps, wp_index, px, pz, lookahead, floor=None):
    """Mirror SimpleNavBridge.PursuitTarget: project player onto the remaining
    polyline; return the projection point if the player is farther than
    PURSUIT_MAX_CROSSTRACK_M off the line (steer back onto the corridor),
    otherwise the point `lookahead` m forward along it (normal pure pursuit)."""
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
    proj_x = wps[best_seg][0] + (wps[best_seg + 1][0] - wps[best_seg][0]) * best_t
    proj_z = wps[best_seg][1] + (wps[best_seg + 1][1] - wps[best_seg][1]) * best_t
    if best_d2 > PURSUIT_MAX_CROSSTRACK_M * PURSUIT_MAX_CROSSTRACK_M:
        return (proj_x, proj_z)
    rem = lookahead
    seg = best_seg
    cx, cz = proj_x, proj_z
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


# Stall is now defined by LACK OF PROGRESS, not by touching a blocked cell. The
# real player is a non-kinematic Rigidbody whose velocity the physics solver
# deflects ALONG walls (it slides), so brief wall contact is normal and not a
# stall — it is exactly how the 1.6m-geometric capsule passes the ~1.0m doors.
# See [[project-navigation-capsule-radius-groundtruth-2026-05-29]]. We model that
# with axis-separated sliding collision (try the full move; if blocked, try the
# X-only and Z-only sub-moves — the one(s) that stay navigable let the player
# slide along the wall). A genuine stall is when route progress (distance to the
# final waypoint) fails to improve for a sustained window despite trying to move.
PROGRESS_EPS_M = 0.03           # min net progress per window to count as "moving"
STALL_WINDOW_TICKS = 90         # ~3s at 30Hz with no progress ⇒ pinned (real stall)

# Move magnitude (= cos(turn) alignment) below which the follower is turning in
# place, not translating — mirrors AutoWalkMovingThreshold. The deployed watchdog
# does NOT accumulate the no-progress clock while the body is turning (move≈0 by
# the facing gate), so a sharp pivot toward the next heading is not a stall. The
# sim must do the same or it false-flags every big-turn corner.
MOVING_THRESHOLD = 0.2


def _try_step(floor, px, pz, fwd_x, fwd_z, speed_dt):
    """Move one tick under the current controller's model. `fwd_*` is the player's
    forward unit axis (XZ); `speed_dt` is forward distance this tick. Returns
    (new_px, new_pz, touched).

    The deployed follower has NO wall-slide escape: it commands move = the steer
    direction and the game's non-kinematic Rigidbody is deflected ALONG walls by
    the physics solver (it slides — that is how the 1.6m capsule threads ~1.0m
    doors). We model that deflection with axis-separated sliding: try the full
    move; if blocked, try X-only then Z-only and take whichever stays navigable.
    `touched` flags wall contact (the move was deflected) for diagnostics."""
    def nav(x, z):
        ix, iz = floor.world_to_cell(x, z)
        return floor.in_bounds(ix, iz) and floor.navigable(ix, iz)

    mx = fwd_x * speed_dt
    mz = fwd_z * speed_dt
    # Full move clear → take it.
    if nav(px + mx, pz + mz):
        return px + mx, pz + mz, False
    # Blocked: slide along the wall — keep whichever single axis stays navigable.
    if nav(px + mx, pz):
        return px + mx, pz, True
    if nav(px, pz + mz):
        return px, pz + mz, True
    # Pinned: both axes blocked (a genuine head-on pinch).
    return px, pz, True


def simulate(floor, wps, door_xz=None):
    """Run the follower from wps[0] to wps[-1]. Returns (status, stall_pos, trace).

    Models the real movement chain: the follower commands a `move` direction
    (pure-pursuit), the controller turns it into velocity, and the physics solver
    SLIDES the capsule along walls on contact (axis-separated here). A stall is
    declared only when route progress stops for STALL_WINDOW_TICKS despite the
    player still trying to move — i.e. genuinely pinned, not merely grazing.

    `door_xz` is the list of (x, z) positions of doors this route is tagged to
    pass through; near one, wall contact is attributed to the executor opening the
    door rather than a stall."""
    if len(wps) < 2:
        return "trivial", None, []
    door_xz = door_xz or []
    px, pz = wps[0]
    facing = math.atan2(wps[1][1] - pz, wps[1][0] - px)  # start facing 1st segment
    wp_index = 1
    contacts = []                       # positions where the player touched a wall (slid)
    best_remaining = math.inf           # closest approach to the final waypoint so far
    ticks_since_progress = 0
    for _ in range(MAX_TICKS):
        # Advance discrete waypoint when within arrival radius (mirrors TryAdvanceWaypoint).
        while wp_index < len(wps) - 1 and math.hypot(wps[wp_index][0] - px, wps[wp_index][1] - pz) <= WAYPOINT_ARRIVE_M:
            wp_index += 1
        # Arrival.
        remaining = math.hypot(wps[-1][0] - px, wps[-1][1] - pz)
        if remaining <= TARGET_ARRIVE_M:
            return "arrived", None, contacts
        # Steer toward pursuit lookahead point.
        tx, tz = pursuit_target(wps, wp_index, px, pz, PURSUIT_LOOKAHEAD_M, floor)
        desired = math.atan2(tz - pz, tx - px)
        # Turn toward desired at limited rate.
        dyaw = (desired - facing + math.pi) % (2 * math.pi) - math.pi
        max_step = math.radians(MAX_TURN_DEG_PER_S) * STEP_DT
        facing += max(-max_step, min(max_step, dyaw))
        # Heading gate: move = clamp01(cos(turn)) — mirrors the deployed follower
        # (move *= Clamp01(facing)). No wall-slide escape: it was removed from the
        # game, so the sim must not model it either.
        align = max(0.0, math.cos(dyaw))
        speed = MOVE_SPEED_MPS * align
        speed_dt = speed * STEP_DT
        fwd_x, fwd_z = math.cos(facing), math.sin(facing)

        # Move under the current controller's model (axis-separated wall slide).
        npx, npz, touched = _try_step(floor, px, pz, fwd_x, fwd_z, speed_dt)
        if touched:
            near_door = any(math.hypot(npx - ddx, npz - ddz) <= DOOR_OPEN_SPAN_M
                            for ddx, ddz in door_xz)
            if not near_door:
                contacts.append((round(npx, 2), round(npz, 2)))
        px, pz = npx, npz

        # Turn-aware stall check, mirroring the deployed watchdog: only count a tick
        # toward a stall when the follower is actually TRYING to translate (align
        # above the move dead-zone). While turning in place (align ≤ MOVING_THRESHOLD)
        # no positional progress is expected, so don't accumulate — that is exactly
        # the big-turn false-stall the turn-aware watchdog fixes.
        if remaining < best_remaining - PROGRESS_EPS_M:
            best_remaining = remaining
            ticks_since_progress = 0
        elif align <= MOVING_THRESHOLD:
            ticks_since_progress = 0  # turning, not stalled
        else:
            ticks_since_progress += 1
            if ticks_since_progress >= STALL_WINDOW_TICKS:
                return "stall", (round(px, 2), round(pz, 2)), contacts
    return "timeout", (round(px, 2), round(pz, 2)), contacts


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
    XZ is the door's doorway-opening centroid from the planner's per-floor
    opening_center_by_name (uniform over swing + sliding doors)."""
    by_floor = {}
    for seg in route.get("segments", []):
        fl = seg.get("from", {}).get("floor")
        if fl is None or fl not in planner.floors:
            continue
        centers = planner.floors[fl].opening_center_by_name
        for d in seg.get("doors", []):
            xz = centers.get(d.get("name"))
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
