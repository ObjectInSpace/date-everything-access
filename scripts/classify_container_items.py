"""Resolve which interactable items live INSIDE a container (cupboard / cabinet /
fridge / closet) by shared scene-graph ancestor, and classify a sweep run's
no_los targets as in-container vs not (e.g. ceiling / high-mounted).

DATA-LAYER ONLY (no planner/follower changes). Purpose: de-risk the path-ancestor
content->container assumption and size the in-container vs other split of the
no_los bucket, so the open-container feature can be scoped on real numbers.

Container link: an item and its container's Door share a parent subtree. Container
doors sit under a per-container parent (e.g. .../Cupboards_Kitchen_Chocolate/...,
.../Cupboard_Laundry_Parent/...), and the item sits under that SAME parent. Passage
doors instead live under .../MultiRoom/Doors/... and are excluded as containers.

Usage:
  python scripts/classify_container_items.py [--sweep <results.json>]
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BLOCKERS = ROOT / "artifacts/navigation/thirdpersongreybox-blockers.json"
INTERACTABLES = ROOT / "artifacts/navigation/thirdpersongreybox-interactables.json"
DEFAULT_SWEEP = ROOT / "artifacts/navigation/sweep/objects/sweep_results.json"

# Passage doors (room-to-room / closets handled as traversal) live under this subtree
# in the scene graph; they are NOT containers you open to reveal a held item.
PASSAGE_DOOR_MARKER = "/MultiRoom/Doors/"

# Scene path prefix stripped for matching.
SCENE_PREFIX = "===SCENE===/"


def _strip(p):
    return (p or "").replace(SCENE_PREFIX, "")


def _ancestors(path):
    """Yield each ancestor directory of a path, deepest first.
    'House/Kitchen/Cupboards_Chocolate/Door' -> ['House/Kitchen/Cupboards_Chocolate',
    'House/Kitchen', 'House']."""
    parts = _strip(path).split("/")
    for i in range(len(parts) - 1, 0, -1):
        yield "/".join(parts[:i])


# A parent node is a plausible CONTAINER subtree if its leaf name looks like a
# cupboard/cabinet/closet/fridge/drawer grouping, not a generic room.
CONTAINER_NAME_RE = re.compile(
    r"(cupboard|cabinet|closet|fridge|drawer|ironcupboard|dishcase|cookie|chocolate"
    r"|shelf|case|laundry_parent)", re.IGNORECASE)


def load_container_doors():
    b = json.loads(BLOCKERS.read_text(encoding="utf-8-sig"))
    doors = []
    for d in b.get("Doors", []):
        path = _strip(d.get("Path"))
        if PASSAGE_DOOR_MARKER.strip("/") in path:
            continue  # passage door, not a container
        doors.append({
            "name": d.get("Name"),
            "id": d.get("GameObjectId"),
            "path": path,
            "locked": d.get("Locked", False),
        })
    return doors


def container_subtree_for_door(door_path):
    """The container's parent subtree for a door = the deepest ancestor whose name
    looks container-ish. Falls back to the door's immediate parent."""
    for anc in _ancestors(door_path):
        leaf = anc.split("/")[-1]
        if CONTAINER_NAME_RE.search(leaf):
            return anc
    parts = door_path.split("/")
    return "/".join(parts[:-1]) if len(parts) > 1 else door_path


def build_container_index(doors):
    """Map container-subtree path -> door. If multiple doors share a subtree
    (double-door cupboards), keep them all."""
    idx = {}
    for d in doors:
        sub = container_subtree_for_door(d["path"])
        idx.setdefault(sub, []).append(d)
    return idx


def item_container(item_path, container_index):
    """The container door(s) whose subtree is an ancestor of the item, or None.
    Picks the DEEPEST matching subtree (most specific container)."""
    ip = _strip(item_path)
    best = None
    best_depth = -1
    for sub, doors in container_index.items():
        if ip == sub or ip.startswith(sub + "/"):
            depth = sub.count("/")
            if depth > best_depth:
                best_depth = depth
                best = (sub, doors)
    return best


def load_interactables():
    raw = json.loads(INTERACTABLES.read_text(encoding="utf-8-sig"))
    return raw if isinstance(raw, list) else (raw.get("Interactables") or raw.get("interactables"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sweep", default=str(DEFAULT_SWEEP))
    args = ap.parse_args()

    doors = load_container_doors()
    cindex = build_container_index(doors)
    print(f"container doors: {len(doors)}  |  container subtrees: {len(cindex)}")
    for sub, ds in sorted(cindex.items()):
        print(f"  {sub}  <- {[d['name'] for d in ds]}")
    print()

    items = load_interactables()
    in_container = 0
    by_container = Counter()
    item_to_container = {}
    for it in items:
        match = item_container(it.get("Path"), cindex)
        if match:
            in_container += 1
            by_container[match[0]] += 1
            item_to_container[it.get("GameObjectName")] = match
    print(f"interactables total: {len(items)}  |  resolved as IN a container: {in_container}")
    print("  by container subtree:")
    for sub, n in by_container.most_common():
        print(f"    {n:4}  {sub}")
    print()

    # Classify the sweep's no_los targets.
    sweep_path = Path(args.sweep)
    if not sweep_path.exists():
        print(f"(no sweep file at {sweep_path}; skipping no_los classification)")
        return
    d = json.loads(sweep_path.read_text(encoding="utf-8-sig"))
    no_los = {r["name"] for r in d["results"] if r.get("outcome") == "no_los"}
    if not no_los:
        print("(sweep has no no_los outcomes)")
        return
    inc = [n for n in no_los if n in item_to_container]
    other = [n for n in no_los if n not in item_to_container]
    print(f"no_los unique targets: {len(no_los)}")
    print(f"  IN-CONTAINER (open-container feature would fix): {len(inc)}")
    print(f"  OTHER (ceiling/high-mounted/occluded — LOS tuning): {len(other)}")

    def fam(n):
        return re.split(r"[_\d]", n)[0] or n
    print("  in-container families:", dict(Counter(fam(n) for n in inc).most_common(10)))
    print("  other families:       ", dict(Counter(fam(n) for n in other).most_common(10)))


if __name__ == "__main__":
    main()
