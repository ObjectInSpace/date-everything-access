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


def plan_to_cell(planner, start_node, target_floor_label, target_ix, target_iz,
                 target_stanza=None, goal_cells=None):
    """Plan a route to a navigable cell (or cell set). Returns the same dict shape as
    Plan-ObjectRoute.plan() but with a synthetic target stanza, or a no_path entry.

    target_stanza lets the caller supply a richer target description (e.g. an object
    node's name + merged members); when omitted a generic sweep_cell stanza is used.

    goal_cells (optional) is the FULL set of acceptable arrival cells — for object
    targets this is every stand-cell around the object's interaction radius. A* accepts
    whichever is reachable, so an object isn't reported no_path just because its single
    nearest cell is marooned inside furniture footprint-rasterization while another of
    its stand-cells is on the reachable floor. (target_ix, target_iz) is the
    representative used for arrival-distance heuristic + coverage stamping. When
    goal_cells is None, falls back to the single (target_ix, target_iz) goal."""
    target_floor = planner.floors[target_floor_label]
    wx, wz = target_floor.cell_to_world(target_ix, target_iz)
    if goal_cells:
        goal_nodes = [(target_floor_label, ix, iz) for (ix, iz) in goal_cells
                      if target_floor.navigable(ix, iz)]
    else:
        if not target_floor.navigable(target_ix, target_iz):
            return {"status": "target_not_navigable"}
        goal_nodes = [(target_floor_label, target_ix, target_iz)]
    if not goal_nodes:
        return {"status": "target_not_navigable"}
    if start_node in goal_nodes:
        return {"status": "trivially_at_target"}

    path, total_cost, edges = planner.astar(
        start_node, goal_nodes, target_floor_label, wx, wz
    )
    if path is None:
        return {"status": "no_path"}

    waypoints = _mod.smooth_path(path, planner)
    segments = _mod.tag_doors(waypoints, planner, _mod.door_positions())
    if target_stanza is None:
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
        "goal_cell_count": len(goal_nodes),
        "path_length_cells": len(path),
        "waypoint_count": len(waypoints),
        "total_cost_m": round(total_cost, 3),
        "waypoints": _mod._waypoints_to_dicts(waypoints, planner),
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
                    help="Override the doors-open set with specific names.")
    ap.add_argument("--doors-closed", action="store_true",
                    help="Sweep with every door closed (raw scene-load cells; "
                         "routes needing any door open will report no_path).")
    ap.add_argument("--doors-all", action="store_true",
                    help="Open every door including locked ones (absolute max-"
                         "coverage probe; ignores the locked gate).")
    ap.add_argument("--state-walls-open", nargs="*", metavar="NAME",
                    help="Override the state-walls-open set; default releases every wall.")
    ap.add_argument("--state-walls-active", action="store_true",
                    help="Sweep with state-gated walls active (matches scene-load state).")
    ap.add_argument("--mode", choices=("walk", "dispersed", "objects"), default="walk",
                    help="walk: emit one reachable-cell bitmap, runtime walks a single continuous "
                         "route hitting every unvisited cell. dispersed: legacy per-cell route "
                         "catalogue. objects: one route per deduped object stand-cell (the picker's "
                         "static object set) — the object-reachability sweep.")
    args = ap.parse_args()

    bake = _mod.load_bake()
    # Door model: default to "unlocked" — open every door the player CAN open
    # during normal play, but hard-block the locked door(s). This matches the
    # in-game autowalk executor, which opens any door on the route it reaches
    # (see [[project-navigation-door-handling-rules]]) yet cannot pass a locked
    # door. It is the honest coverage question: "could the player reach this,
    # opening doors they're allowed to?" Per-route door requirements are still
    # captured via tag_doors() so consumers see which doors a route depends on.
    # Overrides: --doors-all (include locked, absolute max coverage),
    # --doors-closed (raw scene-load, no door opening), --doors-open NAME...
    # (explicit set). The locked/unlocked split comes from the exporter's
    # per-door Door.Locked carried through the bake — not guessed.
    if args.doors_closed:
        doors_open = None
    elif args.doors_all:
        doors_open = "all"
    elif args.doors_open is not None:
        doors_open = args.doors_open
    else:
        doors_open = "unlocked"
    if args.state_walls_active:
        state_walls_open = None
    elif args.state_walls_open is not None:
        state_walls_open = args.state_walls_open
    elif args.mode in ("walk", "objects"):
        # Walk- and object-sweep default: every state-wall (DaemonWall, DresserWall,
        # ...) closed. Those are the quest gates the user wants to EXCLUDE — objects
        # only reachable past a closed gate report no_path rather than being driven.
        # Override with --state-walls-open or --state-walls-active=False semantics
        # if a specific quest state is being tested.
        state_walls_open = None
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
            # Persist the door / state-wall posture the routes were planned under so
            # downstream consumers (simulate_route_follower.py) build a matching
            # Planner. "all" = every door open / every state-wall released; None =
            # all closed / all active; a list = those specific names freed.
            "doors_open": doors_open,
            "state_walls_open": state_walls_open,
        },
        "entries": [],
    }

    if args.mode == "walk":
        _emit_walk_manifest(planner, start_node, manifest, out_dir, floors_to_sweep)
        return

    if args.mode == "objects":
        _emit_object_manifest(planner, start_node, start_world, manifest, out_dir)
        return

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


def _emit_object_manifest(planner, start_node, start_world, manifest, out_dir):
    """Object-reachability sweep (mode=objects). Targets are the deduped object
    stand-cells from plan_object_route.object_sweep_nodes — the picker's STATIC object
    set (active + named + navigable kind), collapsed so objects sharing a stand-cell
    are one node. Plans start->each node; the in-game harness then drives them in
    dispersed order and uses its existing skip-already-covered pruning so the reachable
    cells stamped by one drive retire every later node sitting on covered ground.

    Objects that snap to no navigable cell under the current door/state-wall params
    (exterior decor, or anything walled off behind a still-closed gate) are emitted as
    no_path entries WITHOUT a drive — they ARE the report of "unreachable".
    """
    items = _mod.load_interactables()
    nodes, unreachable = _mod.object_sweep_nodes(planner, items)
    print(f"object nodes: {len(nodes)} deduped stand-cells "
          f"({sum(len(n['names']) for n in nodes)} objects); "
          f"{len(unreachable)} objects off-floor/gate-blocked")

    counts = {"ok": 0, "no_path": 0, "trivially_at_target": 0, "target_not_navigable": 0}
    t0 = time.time()
    # Object-to-object chaining: each leg starts at the PREVIOUS object's reached
    # stand-cell, not a single fixed house start. This makes the sweep measure
    # "can you get from one object to the next" (true object reachability) instead
    # of "can you get from the front door to every object", which funnelled all
    # 1045 legs through one chokepoint and stalled the whole run. The first leg on
    # each floor uses that floor's resolved start; thereafter `cur_start` advances
    # only to cells we actually reached (no_path / trivially-at-target legs keep the
    # prior source, so a leg we couldn't plan to never becomes a bogus source).
    #
    # SAME-FLOOR CHAINING: the chain must NEVER cross floors. The in-game follower
    # cannot drive the staircase, so a cross-floor leg (ground->upper or vice versa)
    # is unexecutable in-game even though the offline A* happily routes it over the
    # stairs — it surfaces as a staircase loop / near-zero stall and drowns out every
    # real same-floor result (36% of legs, terminated run 2026-06-09). We sort the
    # nodes by floor and reset `cur_start` to that floor's own resolved start at each
    # floor boundary, so every leg is a same-floor route the follower can actually
    # validate. Cross-floor reachability is answered separately by walk mode's BFS.
    nodes = sorted(nodes, key=lambda n: n["floor"])
    floor_starts = {}  # floor_label -> resolved (floor,ix,iz) start node, cached

    def _floor_start(label):
        cached = floor_starts.get(label)
        if cached is not None:
            return cached
        if label == start_node[0]:
            node = start_node
        else:
            # Snap the house-start XZ onto this floor; fall back to nearest-navigable
            # to (0,0) on the floor if that spot isn't navigable here.
            f = planner.floors[label]
            n = f.nearest_navigable(start_world[0], start_world[1], max_radius_m=8.0)
            if n is None:
                n = f.nearest_navigable(0.0, 0.0, max_radius_m=30.0)
            node = (label, n[0], n[1]) if n is not None else start_node
        floor_starts[label] = node
        return node

    cur_floor = None
    cur_start = start_node
    cur_start_name = "<house-start>"
    for nd in nodes:
        floor_label = nd["floor"]
        # Floor boundary: reset the chain to this floor's own start so no leg ever
        # plans across the stairs.
        if floor_label != cur_floor:
            cur_floor = floor_label
            cur_start = _floor_start(floor_label)
            cur_start_name = "<%s-start>" % floor_label
        ix, iz = nd["cell"]
        floor = planner.floors[floor_label]
        wx, wz = floor.cell_to_world(ix, iz)
        rep = nd["representative"]
        # Real-object target stanza: primary name is the cleaned display name, and the
        # merged member list rides along so a covered cell credits every object it serves.
        target_stanza = {
            "GameObjectId": rep.get("GameObjectId", 0),
            "Name": _mod.object_display_name(rep),
            "Path": rep.get("Path"),
            "Position": {"x": wx, "y": floor.floor_y, "z": wz},
            "IsDatable": rep.get("IsDatable", False),
            "InkFileName": rep.get("InkFileName"),
            "InteractionRadius": planner.cell_size * 1.5,
            "MemberNames": sorted(set(nd["names"])),
            "MemberObjectIds": nd["object_ids"],
        }
        result = plan_to_cell(planner, cur_start, floor_label, ix, iz,
                               target_stanza=target_stanza, goal_cells=nd["goal_cells"])
        status = result["status"]
        counts[status] = counts.get(status, 0) + 1
        # On success the route may have arrived at a DIFFERENT stand-cell than the
        # representative (A* chose whichever goal cell was reachable). Use that reached
        # cell for the entry + route filename + coverage stamping so the runtime stamps
        # the spot the player actually ends on; fall back to the representative otherwise.
        reached_ix, reached_iz = ix, iz
        if status == "ok" and result.get("waypoints"):
            last = result["waypoints"][-1]
            rf = planner.floors[floor_label]
            reached_ix, reached_iz = rf.world_to_cell(last["wx"], last["wz"])
        rwx, rwz = planner.floors[floor_label].cell_to_world(reached_ix, reached_iz)
        sf = planner.floors[cur_start[0]]
        s_wx, s_wz = sf.cell_to_world(cur_start[1], cur_start[2])
        rep_pos = rep.get("Position") or {}
        entry = {
            "floor": floor_label,
            "cell": [reached_ix, reached_iz],
            "world_xz": [round(rwx, 4), round(rwz, 4)],
            "status": status,
            "name": target_stanza["Name"],
            "member_count": len(target_stanza["MemberNames"]),
            # The DEST object's stable scene id + true transform position + interaction
            # radius. The in-game sweep re-plans each leg LIVE with SimpleNavPlanner.Plan
            # (validating the C# planner's own routes, not these offline ones), resolving
            # the live InteractableObj by this id — object names are NOT unique (42 shared),
            # so the id+position disambiguate which instance a leg targets.
            "object_id": rep.get("GameObjectId", 0),
            "object_xyz": [round(rep_pos.get("x", rwx), 4),
                           round(rep_pos.get("y", planner.floors[floor_label].floor_y), 4),
                           round(rep_pos.get("z", rwz), 4)],
            "interaction_radius": round(min(7.5, max(0.5, rep.get("InteractionRadius", 1.0) or 1.0)), 3),
            # Source object this leg departed from, so the runtime + analysis can see
            # the object->object pairing rather than assuming the fixed house start.
            "from_floor": cur_start[0],
            "from_cell": [cur_start[1], cur_start[2]],
            "from_world_xz": [round(s_wx, 4), round(s_wz, 4)],
            "from_name": cur_start_name,
        }
        if status == "ok":
            # Filename keyed on the manifest index (NOT just the reached cell): with
            # per-source chaining, two legs that arrive at the same stand-cell from
            # DIFFERENT source objects are distinct routes and must not overwrite each
            # other. (Keying on the dest cell silently collided 74 legs and pointed
            # their manifest entry at another leg's route file / source.)
            entry_idx = len(manifest["entries"])
            route_name = f"route.{entry_idx}.{floor_label}.{reached_ix}.{reached_iz}.json"
            (out_dir / route_name).write_text(json.dumps(result, indent=2), encoding="utf-8")
            entry["route"] = route_name
            entry["cost_m"] = result["total_cost_m"]
            entry["waypoint_count"] = result["waypoint_count"]
            entry["edge_kinds"] = result["edge_kinds_used"]
            # Advance the chain: the next object departs from where this leg arrived.
            cur_start = (floor_label, reached_ix, reached_iz)
            cur_start_name = target_stanza["Name"]
        manifest["entries"].append(entry)

    # Unreachable objects, split by WHY they resolved nowhere:
    #   off_floor    -> exterior decor (Y outside every floor band). Expected-unreachable;
    #                   the player can't walk to a fence/tree/drone. These are NOT sweep
    #                   targets at all: emitting them as entries made the runtime record 264
    #                   `off_floor` results (floor="", cost=0) that never drove anything, so
    #                   they inflated the results denominator and buried the real pass rate
    #                   (arrived/attempted). Record them only in counts + an `excluded_exterior`
    #                   roster for visibility; do NOT add a drive entry.
    #   gate_blocked -> on a floor but walled off / behind a closed gate. A real candidate
    #                   navigation problem -> keep reporting as a no_path ENTRY so it stays
    #                   visible and re-validated in-game.
    excluded_exterior = []
    for u in unreachable:
        reason = u.get("reason") or "unresolved"
        is_exterior = reason == "off_floor"
        if is_exterior:
            excluded_exterior.append({
                "name": u.get("name"),
                "world_xz": [round((u.get("position") or {}).get("x", 0.0), 4),
                             round((u.get("position") or {}).get("z", 0.0), 4)],
                "unreachable_reason": reason,
            })
            counts["off_floor"] = counts.get("off_floor", 0) + 1
            continue
        manifest["entries"].append({
            "floor": None,
            "cell": None,
            "world_xz": [round((u.get("position") or {}).get("x", 0.0), 4),
                         round((u.get("position") or {}).get("z", 0.0), 4)],
            "status": "no_path",
            "name": u.get("name"),
            "member_count": 1,
            "unreachable_reason": reason,
        })
        counts["no_path"] = counts.get("no_path", 0) + 1
    manifest["excluded_exterior"] = excluded_exterior

    _disperse_entries(manifest, start_world)
    manifest["mode"] = "objects"
    manifest["counts"] = counts
    manifest["object_node_count"] = len(nodes)
    manifest["object_member_count"] = sum(len(n["names"]) for n in nodes)
    manifest["elapsed_s"] = round(time.time() - t0, 2)
    manifest_path = out_dir / "sweep_manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"wrote {manifest_path}")
    print(f"counts: {counts}  elapsed: {manifest['elapsed_s']}s")


def _disperse_entries(manifest, start_world):
    """Farthest-point dispersion ordering (shared with dispersed mode): seed with the
    nearest planned target, then repeatedly pick the planned entry farthest from the
    running centroid, tie-breaking toward higher cost. No-path entries trail in stable
    order. Long, house-crossing routes run first so their stamped cells prune the most
    later nodes under the runtime's skip-already-covered rule."""
    entries = manifest["entries"]
    planned = [e for e in entries if "cost_m" in e]
    unplanned = [e for e in entries if "cost_m" not in e]
    if not planned:
        manifest["entries"] = unplanned
        return
    sx, sz = start_world
    planned.sort(key=lambda e: (e["world_xz"][0] - sx) ** 2 + (e["world_xz"][1] - sz) ** 2)
    picked = [planned.pop(0)]
    cx, cz = picked[0]["world_xz"]
    while planned:
        best_idx, best_key = 0, (-1.0, -1.0)
        for i, e in enumerate(planned):
            dx = e["world_xz"][0] - cx
            dz = e["world_xz"][1] - cz
            key = (dx * dx + dz * dz, e["cost_m"])
            if key > best_key:
                best_key, best_idx = key, i
        nxt = planned.pop(best_idx)
        picked.append(nxt)
        n = len(picked)
        cx = ((n - 1) * cx + nxt["world_xz"][0]) / n
        cz = ((n - 1) * cz + nxt["world_xz"][1]) / n
    manifest["entries"] = picked + unplanned


def _emit_walk_manifest(planner, start_node, manifest, out_dir, floors_to_sweep):
    """Walk-sweep mode. Computes the BFS-reachable cell set from the start with all
    doors openable but all state-walls (DaemonWall, DresserWall, ...) closed — that
    naturally excludes attic CCs, the Daemon bedroom pocket, and other quest-gated
    regions without a hand-maintained list. Emits the set as a packed bitmap per
    floor; the runtime walks one continuous route hitting every unvisited cell.

    Run this with `--state-walls-active` to honor quest gates, or pass
    `--state-walls-open <names>` to manually carve specific gates in.
    """
    # Force the gate-honoring configuration: doors all openable, state-walls closed.
    # The user can override on the CLI; respect whatever the planner has now.
    reachable = _bfs_reachable(planner, start_node, floors_to_sweep)
    total_reachable = sum(len(v) for v in reachable.values())

    # Per-floor packed bitmap (one byte per cell, '1' or '0'). The runtime decodes
    # these into a bool[] alongside the existing floor_frames table.
    floor_bitmaps = {}
    for label in floors_to_sweep:
        if label not in planner.floors:
            continue
        f = planner.floors[label]
        cells = reachable.get(label, set())
        # Row-major over X (rows=ix, cols=iz), matching the bake's row layout.
        rows = []
        for ix in range(f.nx):
            row_chars = []
            for iz in range(f.nz):
                row_chars.append("1" if (ix, iz) in cells else "0")
            rows.append("".join(row_chars))
        floor_bitmaps[label] = rows

    manifest["mode"] = "walk"
    manifest["reachable_bitmap_rows"] = floor_bitmaps
    manifest["reachable_counts"] = {label: len(reachable.get(label, set())) for label in floors_to_sweep}
    manifest["reachable_total"] = total_reachable
    # No per-route files in walk mode — drop the entries list to avoid confusion.
    manifest["entries"] = []

    manifest_path = out_dir / "sweep_manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"wrote {manifest_path}")
    print(f"walk-mode reachable cells: {manifest['reachable_counts']}  total={total_reachable}")


def _bfs_reachable(planner, start_node, floors_to_sweep):
    """8-connected BFS from start across navigable cells, crossing inter-floor edges
    (stairs + teleporters) wherever both endpoints are bake-resident. Doors that the
    planner has opened contribute their freed_cells through `floor.navigable()`."""
    from collections import deque
    visited = {label: set() for label in floors_to_sweep if label in planner.floors}
    if start_node[0] not in visited:
        return visited
    queue = deque([start_node])
    visited[start_node[0]].add((start_node[1], start_node[2]))
    while queue:
        node = queue.popleft()
        floor_label, ix, iz = node
        if floor_label.startswith("@"):
            # Virtual teleporter endpoint — fan out to its declared neighbors.
            for nbr, _, _ in planner.edges_from.get(node, []):
                if nbr[0].startswith("@"):
                    continue
                if nbr[0] not in visited:
                    continue
                key = (nbr[1], nbr[2])
                if key in visited[nbr[0]]:
                    continue
                visited[nbr[0]].add(key)
                queue.append(nbr)
            continue
        f = planner.floors[floor_label]
        for dx, dz, _ in NEIGHBORS_8_LITE:
            nx_, nz_ = ix + dx, iz + dz
            if (nx_, nz_) in visited[floor_label]:
                continue
            if not f.navigable(nx_, nz_):
                continue
            visited[floor_label].add((nx_, nz_))
            queue.append((floor_label, nx_, nz_))
        # Inter-floor edges off this cell.
        for nbr, _, _ in planner.edges_from.get(node, []):
            if nbr[0].startswith("@"):
                # Virtual teleporter node — let the @-branch above fan it out.
                # Push it onto the queue with a dummy key so we don't revisit.
                queue.append(nbr)
                continue
            if nbr[0] not in visited:
                continue
            key = (nbr[1], nbr[2])
            if key in visited[nbr[0]]:
                continue
            visited[nbr[0]].add(key)
            queue.append(nbr)
    return visited


NEIGHBORS_8_LITE = [
    (-1, -1, 1), (-1, 0, 1), (-1, 1, 1),
    ( 0, -1, 1),              ( 0, 1, 1),
    ( 1, -1, 1), ( 1, 0, 1), ( 1, 1, 1),
]


if __name__ == "__main__":
    main()
