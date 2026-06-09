"""Static-layer reachability check (gap 8 of the offline validator plan).

For each exported interactable, decide whether a planner *could in principle*
reach it from a default start, using only the bake's navigable bitmap and the
inter-floor edges. No A*, no smoothing, no door semantics — this is a pure
connected-component test.

Outputs:
    reachable=<n>         goal disc intersects the start's reachable component set
    no_floor=<n>          target Y doesn't land on a baked floor
    no_navigable_goal=<n> target has no navigable cell within its interaction radius
    isolated=<n>          target's goal cells are in a component not reachable from start

Use this to separate "bake bug" from "planner bug": isolated targets cannot be
fixed by changing the planner.

NOT the ground truth for "can the player reach this object" — it deliberately
over-reports isolation. Two reasons, both by design:
  - No door semantics: the default Planner here has doors_open=None (every door
    CLOSED). Any object reachable only THROUGH an openable door counts as
    isolated, even though the in-game autowalk opens doors on the route. Pass
    --all-doors-open / --doors-open to relax this.
  - Per-object, not per-stand-cell-deduped, and a pure connected-component test
    (no A*).
For the real "is every object reachable" answer use the object-reachability
sweep (scripts/sweep_coverage_planner.py --mode objects), which is door-aware
(doors_open='unlocked') and snaps to the full stand-cell set. As of 2026-06-09
the sweep reports 778/780 nodes reachable (2 isolated exterior bushes) while
this matrix reports isolated~90 — the gap is the closed-door + no-dedup posture
above, NOT a bake regression. See [[project-navigation-object-reachability-sweep]].
This script writes no artifact and nothing consumes its output; it's an offline
eyeball check before online route validation.
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
from diagnose_navigable_components import label_components  # noqa: E402


MIN_RADIUS_M = 0.5
MAX_RADIUS_M = 7.5
TELEPORTER_FLOOR_TOLERANCE = 2.0
# Targets up to this far above a floor are still reachable from it. The game
# treats "look at" and "use the glasses" as interaction, so ceiling fixtures,
# wall mounts, and high shelf items all attribute to the floor below them as
# long as the player can stand somewhere navigable near their XZ. The inter-
# floor gap is ~13m; this generous window admits attic and ceiling items while
# still flagging genuinely off-map debris.
TARGET_ABOVE_FLOOR_MAX_M = 20.0
# Targets slightly below a floor are still attributable to it (drooping props,
# colliders at ankle height).
TARGET_BELOW_FLOOR_MAX_M = 1.0


def teleporter_floor_for_y(planner, y):
    """Used only for stair/teleporter snap: we want the closest floor."""
    best_label, best_d = None, math.inf
    for label, f in planner.floors.items():
        d = abs(f.floor_y - y)
        if d < best_d:
            best_d, best_label = d, label
    return best_label if best_d < TELEPORTER_FLOOR_TOLERANCE else None


def target_floor_for_y(planner, y):
    """A target at world-Y is reachable from the highest floor whose Y is below
    it (plus a small below-floor tolerance). Wall-mounted and shelf items thus
    attribute to the floor below them, not the floor above."""
    best_label, best_dy = None, math.inf
    for label, f in planner.floors.items():
        dy = y - f.floor_y
        if -TARGET_BELOW_FLOOR_MAX_M <= dy <= TARGET_ABOVE_FLOOR_MAX_M:
            # Prefer the floor immediately below the target (smallest nonneg dy).
            score = dy if dy >= 0 else (TARGET_ABOVE_FLOOR_MAX_M + abs(dy))
            if score < best_dy:
                best_dy, best_label = score, label
    return best_label


def goal_cells(floor, wx, wz, radius_m):
    cx, cz = floor.world_to_cell(wx, wz)
    r = max(1, int(math.ceil(radius_m / floor.cell_size)))
    r2 = radius_m * radius_m
    cells = []
    for dx in range(-r, r + 1):
        for dz in range(-r, r + 1):
            ix = cx + dx
            iz = cz + dz
            if not floor.navigable(ix, iz):
                continue
            wcx, wcz = floor.cell_to_world(ix, iz)
            d2 = (wcx - wx) ** 2 + (wcz - wz) ** 2
            if d2 <= r2:
                cells.append((ix, iz))
    return cells


def compute_reachable_components(planner, labels_by_floor, sizes_by_floor, start_xz, start_floor_label):
    """Walk inter-floor edges to merge components across floors.

    Returns: {floor_label: set(component_id)} containing every component
    reachable from the start cell via same-floor adjacency + stair_ramp +
    teleporter edges.
    """
    reachable = {label: set() for label in planner.floors}

    start_floor = planner.floors[start_floor_label]
    snap = start_floor.nearest_navigable(start_xz[0], start_xz[1], max_radius_m=3.0)
    if snap is None:
        return reachable, None
    start_label = labels_by_floor[start_floor_label][snap[0]][snap[1]]
    if start_label < 0:
        return reachable, None

    # Frontier of (floor_label, component_id) pairs.
    frontier = [(start_floor_label, start_label)]
    reachable[start_floor_label].add(start_label)

    # Precompute floor-to-floor bridges as (src_label, src_comp) -> set((dst_label, dst_comp))
    bridges = {}

    bake = planner.bake
    for edge in bake.get("inter_floor_edges", {}).get("stair_ramp", []):
        g = edge["ground"]["cell"]
        u = edge["upper"]["cell"]
        g_label = labels_by_floor["ground"][g[0]][g[1]] if planner.floors["ground"].navigable(g[0], g[1]) else -1
        u_label = labels_by_floor["upper"][u[0]][u[1]] if planner.floors["upper"].navigable(u[0], u[1]) else -1
        if g_label < 0 or u_label < 0:
            continue
        bridges.setdefault(("ground", g_label), set()).add(("upper", u_label))
        bridges.setdefault(("upper", u_label), set()).add(("ground", g_label))

    for tele in bake.get("inter_floor_edges", {}).get("teleporter", []):
        up_xyz = tele["up"]["world_xyz"]
        up_floor_label = teleporter_floor_for_y(planner, up_xyz[1])
        if up_floor_label is None:
            continue
        up_floor = planner.floors[up_floor_label]
        cell = up_floor.nearest_navigable(up_xyz[0], up_xyz[2])
        if cell is None:
            continue
        comp = labels_by_floor[up_floor_label][cell[0]][cell[1]]
        if comp < 0:
            continue
        # Teleporter "down" endpoint is off-bake. Both endpoints of a teleporter
        # are functionally the same node from a reachability standpoint, so any
        # already-reachable component that contains the up endpoint stays where
        # it is. Modelled here as a self-bridge (no extra reachability).
        bridges.setdefault((up_floor_label, comp), set())

    while frontier:
        key = frontier.pop()
        for dst in bridges.get(key, ()):
            if dst[1] not in reachable[dst[0]]:
                reachable[dst[0]].add(dst[1])
                frontier.append(dst)

    return reachable, snap


def load_interactables():
    return planner_mod.load_interactables()


def _classify_on_floor(planner, labels_by_floor, reachable, floor_label, wx, wz, radius):
    """Returns ("reachable"|"isolated"|"no_navigable_goal", floor_label)."""
    floor = planner.floors[floor_label]
    cells = goal_cells(floor, wx, wz, radius)
    if not cells:
        snap = floor.nearest_navigable(wx, wz, max_radius_m=3.0)
        if snap is None:
            return "no_navigable_goal", floor_label
        cells = [snap]
    labels = labels_by_floor[floor_label]
    reachable_comps = reachable[floor_label]
    for (ix, iz) in cells:
        if labels[ix][iz] in reachable_comps:
            return "reachable", floor_label
    return "isolated", floor_label


def classify_target(planner, labels_by_floor, reachable, item):
    pos = item.get("Position") or {}
    if "x" not in pos or "z" not in pos:
        return "no_position", None

    floor_label = target_floor_for_y(planner, float(pos.get("y", 0.0)))
    if floor_label is None:
        return "no_floor", None

    radius = float(item.get("InteractionRadius", 1.0))
    if radius < MIN_RADIUS_M:
        radius = MIN_RADIUS_M
    if radius > MAX_RADIUS_M:
        radius = MAX_RADIUS_M

    wx = float(pos["x"])
    wz = float(pos["z"])

    verdict, _ = _classify_on_floor(planner, labels_by_floor, reachable, floor_label, wx, wz, radius)
    return verdict, floor_label


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--start-xz", nargs=2, type=float, default=[-0.613, 0.661],
                    help="Default: front hall on ground floor.")
    ap.add_argument("--start-floor", choices=("ground", "upper"), default="ground")
    ap.add_argument("--active-only", action="store_true", default=True)
    ap.add_argument("--datable-only", action="store_true")
    ap.add_argument("--show", type=int, default=0,
                    help="Print up to N isolated targets (path + position + floor).")
    ap.add_argument("--show-no-floor", type=int, default=0,
                    help="Print up to N no_floor targets.")
    ap.add_argument("--cluster-isolated", action="store_true",
                    help="Group isolated targets by scene-path prefix and print counts.")
    ap.add_argument("--doors-open", nargs="*", metavar="NAME",
                    help="Treat the named doors as open; their per-door freed cells "
                         "extend the navigable bitmap. Names match GameObjectName.")
    ap.add_argument("--all-doors-open", action="store_true",
                    help="Open every door with exported freed-cells data.")
    ap.add_argument("--state-walls-open", nargs="*", metavar="NAME",
                    help="Treat the named state-gated walls as released (e.g. "
                         "DresserWall after the leave_house Ink command).")
    ap.add_argument("--all-state-walls-open", action="store_true",
                    help="Release every state-gated wall with exported freed-cells data.")
    args = ap.parse_args()

    bake = planner_mod.load_bake()
    doors_open = "all" if args.all_doors_open else (args.doors_open or None)
    state_walls_open = "all" if args.all_state_walls_open else (args.state_walls_open or None)
    planner = planner_mod.Planner(bake, doors_open=doors_open, state_walls_open=state_walls_open)

    labels_by_floor = {}
    sizes_by_floor = {}
    for label, floor in planner.floors.items():
        labels, sizes = label_components(floor)
        labels_by_floor[label] = labels
        sizes_by_floor[label] = sizes

    reachable, snap = compute_reachable_components(
        planner, labels_by_floor, sizes_by_floor, args.start_xz, args.start_floor,
    )
    if snap is None:
        print("error: start position not on or near any navigable cell")
        raise SystemExit(2)

    items = load_interactables()
    counts = {"reachable": 0, "no_floor": 0, "no_navigable_goal": 0, "isolated": 0, "no_position": 0}
    isolated_examples = []
    isolated_items = []
    for item in items:
        if args.active_only and not item.get("IsActive", True):
            continue
        if args.datable_only and not item.get("IsDatable"):
            continue
        verdict, floor_label = classify_target(planner, labels_by_floor, reachable, item)
        counts[verdict] = counts.get(verdict, 0) + 1
        if verdict == "isolated":
            isolated_items.append((item, floor_label))
            if len(isolated_examples) < args.show:
                isolated_examples.append((item, floor_label))

    total_reachable = sum(len(s) for s in reachable.values())
    print("reachability matrix")
    print(f"start_floor={args.start_floor} start_snap_cell={list(snap)}")
    print(f"reachable_components={total_reachable} across floors={list(reachable.keys())}")
    for label in sorted(counts):
        print(f"{label}={counts[label]}")

    for item, floor_label in isolated_examples:
        pos = item.get("Position") or {}
        print(f"isolated: id={item.get('GameObjectId')} floor={floor_label} "
              f"xyz=({pos.get('x'):.2f},{pos.get('y'):.2f},{pos.get('z'):.2f}) "
              f"path={item.get('Path')}")

    if args.cluster_isolated and isolated_items:
        clusters = {}
        for item, floor_label in isolated_items:
            path = item.get("Path") or ""
            parts = path.split("/")
            if parts and parts[0] == "===SCENE===" and len(parts) >= 3:
                key = f"{parts[1]}/{parts[2]}"
            elif len(parts) >= 2:
                key = f"{parts[0]}/{parts[1]}"
            elif parts:
                key = parts[0]
            else:
                key = "<no-path>"
            slot = clusters.setdefault(key, {"ground": 0, "upper": 0, "sample": None})
            slot[floor_label] = slot.get(floor_label, 0) + 1
            if slot["sample"] is None:
                slot["sample"] = item.get("GameObjectName") or path
        # Clusters whose isolation is expected (quest-gated at scene load):
        # - Attic items: gated by AtticLocked tag (see decompiled/AtticDoorUnlocker.cs).
        # - Exterior items: gated by FrontDoor.locked = true (see decompiled/FrontDoor.cs).
        #   FrontDoor is not a Door/SlidingDoor MonoBehaviour so it has no freed-cells
        #   data; the player exits the house only after a specific narrator beat.
        # Lighting-prefab variants of these rooms are also expected-isolated since
        # they reference the same physical lights inside the gated room.
        expected_isolated_substrings = ("Attic", "Lights_Attic", "Exterior")
        print("isolated clusters (path-prefix):")
        ranked = sorted(clusters.items(), key=lambda kv: -(kv[1]["ground"] + kv[1]["upper"]))
        for key, info in ranked:
            total = info["ground"] + info["upper"]
            note = ""
            if any(s in key for s in expected_isolated_substrings):
                note = "  [EXPECTED: quest-gated]"
            print(f"  {total:4d}  {key:40s}  ground={info['ground']} upper={info['upper']}  e.g. {info['sample']}{note}")

    if args.show_no_floor:
        shown = 0
        for item in items:
            if args.active_only and not item.get("IsActive", True):
                continue
            pos = item.get("Position") or {}
            if "y" not in pos:
                continue
            if target_floor_for_y(planner, float(pos["y"])) is not None:
                continue
            print(f"no_floor: id={item.get('GameObjectId')} "
                  f"xyz=({pos.get('x'):.2f},{pos.get('y'):.2f},{pos.get('z'):.2f}) "
                  f"path={item.get('Path')}")
            shown += 1
            if shown >= args.show_no_floor:
                break


if __name__ == "__main__":
    main()
