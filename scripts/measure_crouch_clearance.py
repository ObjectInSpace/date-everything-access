"""Read-only measurement: how many cells become navigable if the player CROUCHES.

The bake models a single standing capsule height (CAPSULE_H = 3.2 m). The player can
crouch (collider height 2.5 m -> 1.0 m, BetterPlayerControl), which clears any passage
whose overhead clearance is between ~1.0 m and the standing height. This script re-runs
the bake's own per-floor rasterization at the standing height and at a crouched height
and reports, per floor, how many cells flip blocked->navigable. No files are written and
the shipped bake is untouched.

Run: python scripts/measure_crouch_clearance.py
"""
import json
import sys
from pathlib import Path

import bake_navigable_region as bake

# Standing vs crouched capsule heights. Standing uses the bake's own CAPSULE_H so the
# baseline matches the shipped bake exactly. Crouched uses the game's crouched collider
# height (colliderHeightCrouched = 1.0 in BetterPlayerControl); a small margin keeps it
# honest against the same edge-margin philosophy the standing band uses.
STANDING_H = bake.CAPSULE_H
CROUCHED_H = 1.0


def load_inputs():
    """Mirror bake_navigable_region.main()'s input loading (no output side effects)."""
    walk = json.load(open(bake.WALK, encoding="utf-8"))
    blok = json.load(open(bake.BLOCK, encoding="utf-8-sig"))
    walkables = walk["WalkableSurfaces"]

    for f in bake.FLOORS:
        if f["y"] >= bake.FLOORS_GROUND_Y:
            continue
        band = [w for w in walkables if abs(w["TopY"] - f["y"]) <= f.get("y_tol", 1.25)
                and w.get("Footprint")]
        if not band:
            continue
        bake.SUBGROUND_FLOOR_FOOTPRINTS[f["label"]] = (
            min(w["Footprint"]["MinX"] for w in band),
            max(w["Footprint"]["MaxX"] for w in band),
            min(w["Footprint"]["MinZ"] for w in band),
            max(w["Footprint"]["MaxZ"] for w in band))

    blockers = blok["NavigationBlockers"]
    mesh_colliders = blok.get("MeshColliders", [])
    door_records = blok.get("Doors", [])
    state_walls = blok.get("StateWalls", [])

    doors = []
    door_keys = set()
    if bake.NAVDATA.exists():
        nav = json.load(open(bake.NAVDATA, encoding="utf-8-sig"))
        for door in nav.get("DoorObjects", []):
            component = door.get("DoorComponent")
            if not component:
                continue
            name = door.get("Name") or ""
            if not name.startswith("Doors_"):
                continue
            pos = door.get("Position") or {}
            key = (name, round(pos.get("x", 0.0), 3), round(pos.get("y", 0.0), 3), round(pos.get("z", 0.0), 3))
            door_keys.add(key)
            doors.append({"name": name, "x": pos.get("x", 0.0), "y": pos.get("y", 0.0),
                          "z": pos.get("z", 0.0), "radius": bake.DOOR_CARVE_RADIUS})

    # storey_ceiling_y, as main() sets it.
    floors_sorted_y = sorted(f["y"] for f in bake.FLOORS)
    for floor in bake.FLOORS:
        higher = [y for y in floors_sorted_y if y > floor["y"]]
        floor["storey_ceiling_y"] = min(higher) if higher else float("inf")

    return walkables, blockers, mesh_colliders, doors, door_records, state_walls


def bake_all_floors(args):
    out = {}
    for floor in bake.FLOORS:
        res = bake.bake_floor(floor, *args)
        if "error" in res:
            out[floor["label"]] = {"error": res["error"]}
            continue
        out[floor["label"]] = dict(res["cells"])
    return out


def main():
    args = load_inputs()

    bake.CAPSULE_H = STANDING_H
    standing = bake_all_floors(args)

    bake.CAPSULE_H = CROUCHED_H
    crouched = bake_all_floors(args)

    bake.CAPSULE_H = STANDING_H  # restore

    print(f"Crouch-clearance measurement (standing {STANDING_H} m vs crouched {CROUCHED_H} m)\n")
    total_gain = 0
    for label in [f["label"] for f in bake.FLOORS]:
        s = standing.get(label, {})
        c = crouched.get(label, {})
        if "error" in s or "error" in c:
            print(f"{label}: error ({s.get('error') or c.get('error')})")
            continue
        s_nav = s.get("navigable", 0)
        c_nav = c.get("navigable", 0)
        gain = c_nav - s_nav
        total_gain += gain
        pct = (gain / s_nav * 100.0) if s_nav else 0.0
        print(f"{label:>12}: standing navigable={s_nav:6d}  crouched navigable={c_nav:6d}  "
              f"crouch-only gain={gain:5d} cells ({pct:.2f}%)  "
              f"[{gain * bake.CELL * bake.CELL:.2f} m2]")

    print(f"\nTOTAL crouch-only cells across floors: {total_gain} "
          f"({total_gain * bake.CELL * bake.CELL:.2f} m2 of floor)")
    print("(A cell is 'crouch-only' if it is non-navigable standing but navigable crouched.)")


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    main()
