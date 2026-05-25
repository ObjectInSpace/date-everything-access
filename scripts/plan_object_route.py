"""Step O4 of [[project-navigation-object-first-plan]]: A* planner over the
combined per-floor navigable bitmaps + inter-floor edges.

Inputs:
  artifacts/navigation/navigable_region.bake.json      (O2/O3 output)
  artifacts/navigation/thirdpersongreybox-interactables.json  (O1 output)
  artifacts/navigation/thirdpersongreybox-navigation-data.json (door positions)

Output:
  A polyline route from a start XY(Z) (and explicit floor) to a target
  interactable, written to stdout as JSON and optionally to a file.

CLI:
  python scripts/plan_object_route.py --target <GameObjectId>
      [--start-xz X Z] [--start-floor ground|upper]
      [--out artifacts/navigation/route.<target>.json]

If --start-xz is omitted, defaults to the door-cell of `===SCENE===/House/Bedroom/Door`
or, failing that, the centre of the largest navigable connected component on the
ground floor — a deterministic placeholder for prototyping.

The planner is independent of the runtime; it produces a route artifact the
O5 executor will consume.
"""
from __future__ import annotations
import argparse, heapq, json, math, sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
BAKE = REPO / "artifacts/navigation/navigable_region.bake.json"
INTERACTABLES = REPO / "artifacts/navigation/thirdpersongreybox-interactables.json"
NAVDATA = REPO / "artifacts/navigation/thirdpersongreybox-navigation-data.json"

NAVIGABLE_CHAR = "N"
CORNER_WAYPOINT_DEG = 30.0       # smoothing: keep vertices with turn > this
DOOR_TAG_RADIUS_M = 0.8          # segment tagged with door if door XZ is within this distance of the segment
MIN_INTERACTION_RADIUS_M = 0.5
MAX_INTERACTION_RADIUS_M = 7.5


# ---------- bake loading + cell/world conversions ----------

def load_bake():
    return json.loads(BAKE.read_text())


class Floor:
    """One floor's navigable bitmap plus its cell↔world transform."""

    def __init__(self, raw):
        self.label = raw["label"]
        self.floor_y = raw["floor_y"]
        fr = raw["frame"]
        self.origin_x = fr["origin_x"]
        self.origin_z = fr["origin_z"]
        self.cell_size = fr["cell_size"]
        self.nx = fr["nx"]
        self.nz = fr["nz"]
        rows = raw["bitmap_rows"]
        # Bitmap is row-major over X (rows=ix, cols=iz). Cache as list of bytes for O(1) lookup.
        self.rows = rows
        assert len(rows) == self.nx, f"{self.label}: expected {self.nx} rows, got {len(rows)}"

    def in_bounds(self, ix, iz):
        return 0 <= ix < self.nx and 0 <= iz < self.nz

    def navigable(self, ix, iz):
        if not self.in_bounds(ix, iz):
            return False
        return self.rows[ix][iz] == NAVIGABLE_CHAR

    def cell_to_world(self, ix, iz):
        # Cell centre.
        wx = self.origin_x + ix * self.cell_size + self.cell_size / 2
        wz = self.origin_z + iz * self.cell_size + self.cell_size / 2
        return wx, wz

    def world_to_cell(self, wx, wz):
        ix = int(math.floor((wx - self.origin_x) / self.cell_size))
        iz = int(math.floor((wz - self.origin_z) / self.cell_size))
        return ix, iz

    def nearest_navigable(self, wx, wz, max_radius_m=3.0):
        """Find the nearest navigable cell to a world XZ point. Spiral outward."""
        cx, cz = self.world_to_cell(wx, wz)
        if self.navigable(cx, cz):
            return cx, cz
        max_r = int(math.ceil(max_radius_m / self.cell_size))
        for r in range(1, max_r + 1):
            for dx in range(-r, r + 1):
                for dz in (-r, r):
                    if self.navigable(cx + dx, cz + dz):
                        return cx + dx, cz + dz
                for dz in range(-r + 1, r):
                    for dx in (-r, r):
                        if self.navigable(cx + dx, cz + dz):
                            return cx + dx, cz + dz
        return None


# ---------- A* on combined graph ----------

# Node = (floor_label, ix, iz) for grid cells, plus virtual nodes for teleporter endpoints
# that don't land on a bakeable floor. We represent every node as a tuple.

NEIGHBORS_8 = [
    (-1, -1, math.sqrt(2)), (-1, 0, 1.0), (-1, 1, math.sqrt(2)),
    ( 0, -1, 1.0),                          ( 0, 1, 1.0),
    ( 1, -1, math.sqrt(2)), ( 1, 0, 1.0), ( 1, 1, math.sqrt(2)),
]


class Planner:
    def __init__(self, bake):
        self.bake = bake
        self.floors = {f["label"]: Floor(f) for f in bake["floors"]}
        self.cell_size = next(iter(self.floors.values())).cell_size
        # Inter-floor edges: keyed by (floor, ix, iz) → list of (other_node, cost, edge_meta).
        # other_node is (floor', ix', iz') or a virtual ('@', name) tuple for off-bake endpoints.
        self.edges_from = {}
        for e in bake.get("inter_floor_edges", {}).get("stair_ramp", []):
            g = e["ground"]; u = e["upper"]
            a = ("ground", g["cell"][0], g["cell"][1])
            b = ("upper",  u["cell"][0], u["cell"][1])
            cost = e["cost_m"]
            self.edges_from.setdefault(a, []).append((b, cost, {"kind": "stairs", "path": e["source_path"]}))
            self.edges_from.setdefault(b, []).append((a, cost, {"kind": "stairs", "path": e["source_path"]}))
        for t in bake.get("inter_floor_edges", {}).get("teleporter", []):
            up_xyz = t["up"]["world_xyz"]
            up_floor = self._floor_for_y(up_xyz[1])
            if up_floor is None:
                continue
            up_cell = self.floors[up_floor].nearest_navigable(up_xyz[0], up_xyz[2])
            if up_cell is None:
                continue
            up_node = (up_floor, up_cell[0], up_cell[1])
            down_node = ("@teleporter_down", t["source_name"], 0)
            meta = {"kind": "teleporter", "name": t["source_name"]}
            self.edges_from.setdefault(up_node, []).append((down_node, t["cost_m"], meta))
            self.edges_from.setdefault(down_node, []).append((up_node, t["cost_m"], meta))
    def _floor_for_y(self, y):
        best, bestd = None, math.inf
        for label, f in self.floors.items():
            d = abs(f.floor_y - y)
            if d < bestd:
                bestd, best = d, label
        return best if bestd < 2.0 else None

    def world_of(self, node):
        floor, a, b = node
        if floor.startswith("@"):
            return None
        return self.floors[floor].cell_to_world(a, b)

    def heuristic(self, node, goal_floor, goal_wx, goal_wz):
        """Admissible: Euclidean in XZ, plus a flat per-floor-mismatch addend bounded by
        the cheapest inter-floor edge cost (so it stays admissible)."""
        if node[0].startswith("@"):
            return 0.0
        f = self.floors[node[0]]
        wx, wz = f.cell_to_world(node[1], node[2])
        d = math.hypot(wx - goal_wx, wz - goal_wz) * (self.cell_size / self.cell_size)  # metres
        # add cheapest inter-floor cost if floors differ — admissible (any cross-floor path pays at least this).
        if node[0] != goal_floor:
            min_inter = math.inf
            for adj in self.edges_from.values():
                for _, c, _ in adj:
                    if c > 0 and c < min_inter:
                        min_inter = c
            if min_inter == math.inf:
                min_inter = 0.0
            d += min_inter
        return d

    def neighbors(self, node):
        floor, a, b = node
        out = []
        if not floor.startswith("@"):
            f = self.floors[floor]
            for dx, dz, cost in NEIGHBORS_8:
                nx, nz = a + dx, b + dz
                if f.navigable(nx, nz):
                    out.append(((floor, nx, nz), cost * self.cell_size, {"kind": "walk"}))
        for nbr, cost, meta in self.edges_from.get(node, ()):
            out.append((nbr, cost, meta))
        return out

    def astar(self, start_node, goal_nodes, goal_floor, goal_wx, goal_wz):
        goal_set = set(goal_nodes)
        if start_node in goal_set:
            return [start_node], 0.0, []
        open_heap = [(self.heuristic(start_node, goal_floor, goal_wx, goal_wz), 0.0, start_node)]
        came_from = {start_node: (None, None)}  # node → (prev_node, edge_meta)
        gscore = {start_node: 0.0}
        closed = set()
        while open_heap:
            _, g, node = heapq.heappop(open_heap)
            if g > gscore.get(node, math.inf):
                continue
            if node in closed:
                continue
            closed.add(node)
            if node in goal_set:
                # Reconstruct.
                path, edges = [], []
                cur = node
                while cur is not None:
                    path.append(cur)
                    prev, meta = came_from[cur]
                    if prev is not None:
                        edges.append(meta)
                    cur = prev
                path.reverse(); edges.reverse()
                return path, g, edges
            for nbr, cost, meta in self.neighbors(node):
                ng = g + cost
                if ng < gscore.get(nbr, math.inf):
                    gscore[nbr] = ng
                    came_from[nbr] = (node, meta)
                    h = self.heuristic(nbr, goal_floor, goal_wx, goal_wz)
                    heapq.heappush(open_heap, (ng + h, ng, nbr))
        return None, math.inf, []


# ---------- polyline smoothing + door tagging ----------

def _segment_is_clear(planner, a, b):
    """True iff the straight line of cells from node a to node b is navigable on a's
    floor. Same-floor only; cross-floor and virtual-node segments are conservatively
    treated as non-clear so they always carry an explicit waypoint."""
    if a[0] != b[0] or a[0].startswith("@"):
        return False
    floor = planner.floors[a[0]]
    ix0, iz0 = a[1], a[2]
    ix1, iz1 = b[1], b[2]
    # Bresenham-style line walk on the cell grid.
    dx = abs(ix1 - ix0); dz = abs(iz1 - iz0)
    sx = 1 if ix0 < ix1 else -1
    sz = 1 if iz0 < iz1 else -1
    err = dx - dz
    ix, iz = ix0, iz0
    while True:
        if not floor.navigable(ix, iz):
            return False
        if ix == ix1 and iz == iz1:
            return True
        e2 = 2 * err
        if e2 > -dz:
            err -= dz
            ix += sx
        if e2 < dx:
            err += dx
            iz += sz


def smooth_path(path, planner):
    """Two-pass smoother.

    Pass 1 (line-of-sight): greedy waypoint reduction. From the last anchor,
    extend forward as long as the straight segment to the next candidate cell
    is navigable; when the next step would cross a blocker, the previous step
    becomes a kept waypoint and we restart from there. This is the standard
    funnel-style smoother adapted to a grid.

    Pass 2 (corner-angle pruning): along the reduced polyline, drop any
    intermediate waypoint whose incoming/outgoing angle is below
    CORNER_WAYPOINT_DEG, but only if dropping it leaves a still-clear segment.

    Always preserves: first/last, any virtual node, any node where the floor
    changes from its neighbour (so inter-floor edges keep their explicit
    transition waypoints)."""
    if not path:
        return []
    if len(path) <= 2:
        return list(path)

    # Pass 1: greedy line-of-sight.
    los = [path[0]]
    last_anchor_idx = 0
    i = 1
    while i < len(path):
        node = path[i]
        prev = path[i - 1]
        # Any non-grid hop forces a waypoint (floor change, virtual node).
        if node[0] != los[-1][0] or node[0].startswith("@") or los[-1][0].startswith("@"):
            los.append(prev if prev != los[-1] else node)
            if los[-1] != node:
                los.append(node)
            last_anchor_idx = i
            i += 1
            continue
        # If the straight segment from anchor to node is still clear, keep skipping.
        if _segment_is_clear(planner, los[-1], node):
            i += 1
            continue
        # Otherwise the previous cell was the last clear endpoint; anchor it.
        los.append(prev)
        last_anchor_idx = i - 1
    # Always end at the final node.
    if los[-1] != path[-1]:
        los.append(path[-1])

    # Pass 2: drop interior waypoints whose corner angle is shallow AND removing
    # them still leaves a clear segment between their neighbours.
    out = [los[0]]
    for j in range(1, len(los) - 1):
        a, b, c = out[-1], los[j], los[j + 1]
        wa = planner.world_of(a); wb = planner.world_of(b); wc = planner.world_of(c)
        if wa is None or wb is None or wc is None:
            out.append(b); continue
        v1 = (wb[0] - wa[0], wb[1] - wa[1])
        v2 = (wc[0] - wb[0], wc[1] - wb[1])
        n1 = math.hypot(*v1); n2 = math.hypot(*v2)
        if n1 == 0 or n2 == 0:
            out.append(b); continue
        dot = (v1[0] * v2[0] + v1[1] * v2[1]) / (n1 * n2)
        dot = max(-1.0, min(1.0, dot))
        angle_deg = math.degrees(math.acos(dot))
        if angle_deg <= CORNER_WAYPOINT_DEG and _segment_is_clear(planner, a, c):
            continue  # drop b
        out.append(b)
    out.append(los[-1])

    # Dedup consecutive duplicates.
    dedup = [out[0]]
    for w in out[1:]:
        if w != dedup[-1]:
            dedup.append(w)
    return dedup


def door_positions():
    """DoorObjects in the nav-data export is a superset of real doors — it includes
    bumper colliders, camera placeholders, wall remnants, and many entries stacked at
    a canonical front-door XZ. O4 emits door tags as hints; O5's executor resolves
    them against the runtime door FSM. Here we just dedupe by (rounded XZ) and skip
    names that obviously aren't doors the player opens."""
    nav = json.loads(NAVDATA.read_text(encoding="utf-8-sig"))
    seen = set()
    doors = []
    SKIP_NAME_PATTERNS = ("Camera_", "bumper", "Wall")
    for d in nav.get("DoorObjects", []):
        p = d.get("Position") or {}
        if "x" not in p:
            continue
        name = d.get("Name") or ""
        if any(s in name for s in SKIP_NAME_PATTERNS):
            continue
        key = (round(p["x"], 2), round(p["z"], 2), round(p["y"], 2))
        if key in seen:
            continue
        seen.add(key)
        doors.append({
            "id": d.get("Id"),
            "name": name,
            "xz": (p["x"], p["z"]),
            "y": p["y"],
        })
    return doors


def tag_doors(waypoints, planner, doors):
    """For each segment between waypoints, if any door's XZ lies within DOOR_TAG_RADIUS_M
    of the segment AND the door's Y is near the segment's floor, attach the door id."""
    segments = []
    for i in range(len(waypoints) - 1):
        a, b = waypoints[i], waypoints[i + 1]
        wa, wb = planner.world_of(a), planner.world_of(b)
        seg = {"from": _node_to_dict(a, planner), "to": _node_to_dict(b, planner), "doors": []}
        if wa is not None and wb is not None and a[0] == b[0]:
            floor_y = planner.floors[a[0]].floor_y
            for d in doors:
                if abs(d["y"] - floor_y) > 3.0:
                    continue
                dist = _point_segment_distance(d["xz"], wa, wb)
                if dist <= DOOR_TAG_RADIUS_M:
                    seg["doors"].append({"id": d["id"], "name": d["name"], "distance_m": round(dist, 3)})
        segments.append(seg)
    return segments


def _point_segment_distance(p, a, b):
    px, pz = p; ax, az = a; bx, bz = b
    vx, vz = bx - ax, bz - az
    L2 = vx * vx + vz * vz
    if L2 == 0:
        return math.hypot(px - ax, pz - az)
    t = max(0.0, min(1.0, ((px - ax) * vx + (pz - az) * vz) / L2))
    cx, cz = ax + t * vx, az + t * vz
    return math.hypot(px - cx, pz - cz)


def _node_to_dict(node, planner):
    floor, a, b = node
    d = {"floor": floor, "cell": [a, b]}
    w = planner.world_of(node)
    if w is not None:
        # world_xz is the canonical array form (preserved for the Python sweep harness).
        # wx/wz/floor_y are scalar mirrors so the C# DataContractJsonSerializer in
        # SimpleNavRouteLoader can deserialize without parsing a JSON array of floats.
        d["world_xz"] = [round(w[0], 4), round(w[1], 4)]
        d["wx"] = round(w[0], 4)
        d["wz"] = round(w[1], 4)
        d["floor_y"] = planner.floors[floor].floor_y
    return d


# ---------- interactable lookup ----------

def load_interactables():
    return json.loads(INTERACTABLES.read_text(encoding="utf-8-sig"))["Interactables"]


def find_interactable(items, target):
    """target is either a GameObjectId (int-ish string) or a substring match on path."""
    try:
        tid = int(target)
        for it in items:
            if it.get("GameObjectId") == tid:
                return it
    except (TypeError, ValueError):
        pass
    matches = [it for it in items if target.lower() in (it.get("Path") or "").lower()]
    if not matches:
        return None
    if len(matches) > 1:
        # Disambiguate by shortest path (least nested) — deterministic.
        matches.sort(key=lambda it: (len(it.get("Path") or ""), it.get("GameObjectId", 0)))
    return matches[0]


def goal_cells_around(floor, wx, wz, radius_m):
    """All navigable cells within radius_m of (wx, wz) on floor."""
    cx, cz = floor.world_to_cell(wx, wz)
    r = max(1, int(math.ceil(radius_m / floor.cell_size)))
    goals = []
    r2 = radius_m * radius_m
    for dx in range(-r, r + 1):
        for dz in range(-r, r + 1):
            ix, iz = cx + dx, cz + dz
            if not floor.navigable(ix, iz):
                continue
            wcx, wcz = floor.cell_to_world(ix, iz)
            if (wcx - wx) ** 2 + (wcz - wz) ** 2 <= r2:
                goals.append((ix, iz))
    return goals


# ---------- top-level plan() ----------

def plan(target_spec, start_xz=None, start_floor=None, interaction_radius_override=None):
    bake = load_bake()
    planner = Planner(bake)
    items = load_interactables()
    target = find_interactable(items, target_spec)
    if target is None:
        raise SystemExit(f"no interactable matches {target_spec!r}")

    tx = target["Position"]["x"]; ty = target["Position"]["y"]; tz = target["Position"]["z"]
    tfloor = planner._floor_for_y(ty)
    if tfloor is None:
        raise SystemExit(f"target Y={ty} not on a baked floor")
    radius = interaction_radius_override or target.get("InteractionRadius", 1.0)
    radius = max(MIN_INTERACTION_RADIUS_M, min(radius, MAX_INTERACTION_RADIUS_M))
    goals = goal_cells_around(planner.floors[tfloor], tx, tz, radius)
    if not goals:
        # Fall back to the nearest navigable cell.
        n = planner.floors[tfloor].nearest_navigable(tx, tz, max_radius_m=4.0)
        if n is None:
            raise SystemExit(f"no navigable cell near target {target['Path']}")
        goals = [n]
    goal_nodes = [(tfloor, ix, iz) for ix, iz in goals]

    # Start node.
    if start_xz is None:
        # Default start: nearest navigable to (0, 0) on ground.
        start_floor = start_floor or "ground"
        n = planner.floors[start_floor].nearest_navigable(0.0, 0.0, max_radius_m=15.0)
        if n is None:
            raise SystemExit("no default start cell found")
        start_node = (start_floor, n[0], n[1])
    else:
        start_floor = start_floor or "ground"
        n = planner.floors[start_floor].nearest_navigable(start_xz[0], start_xz[1], max_radius_m=4.0)
        if n is None:
            raise SystemExit(f"no navigable cell near start {start_xz} on {start_floor}")
        start_node = (start_floor, n[0], n[1])

    path, total_cost, edges = planner.astar(start_node, goal_nodes, tfloor, tx, tz)
    if path is None:
        return {
            "status": "no_path",
            "target": _summarize_target(target),
            "start": _node_to_dict(start_node, planner),
        }

    waypoints = smooth_path(path, planner)
    segments = tag_doors(waypoints, planner, door_positions())
    return {
        "status": "ok",
        "target": _summarize_target(target),
        "start": _node_to_dict(start_node, planner),
        "goal_cell_count": len(goals),
        "path_length_cells": len(path),
        "waypoint_count": len(waypoints),
        "total_cost_m": round(total_cost, 3),
        "waypoints": [_node_to_dict(w, planner) for w in waypoints],
        "segments": segments,
        "edge_kinds_used": sorted({e.get("kind", "walk") for e in edges if e}),
        "params": {
            "cell_size_m": planner.cell_size,
            "interaction_radius_used_m": radius,
            "corner_waypoint_deg": CORNER_WAYPOINT_DEG,
            "door_tag_radius_m": DOOR_TAG_RADIUS_M,
        },
    }


def _summarize_target(target):
    return {
        "GameObjectId": target.get("GameObjectId"),
        "Name": target.get("GameObjectName"),
        "Path": target.get("Path"),
        "Position": target.get("Position"),
        "IsDatable": target.get("IsDatable"),
        "InkFileName": target.get("InkFileName"),
        "InteractionRadius": target.get("InteractionRadius"),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", required=True, help="GameObjectId or substring of interactable Path")
    ap.add_argument("--start-xz", nargs=2, type=float, metavar=("X", "Z"))
    ap.add_argument("--start-floor", choices=("ground", "upper"))
    ap.add_argument("--interaction-radius", type=float)
    ap.add_argument("--out", type=Path)
    args = ap.parse_args()
    result = plan(args.target, tuple(args.start_xz) if args.start_xz else None,
                  args.start_floor, args.interaction_radius)
    text = json.dumps(result, indent=2)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(text)
        # Console summary.
        print(f"status={result.get('status')} cost={result.get('total_cost_m')} "
              f"waypoints={result.get('waypoint_count')} edges={result.get('edge_kinds_used')}")
        print(f"wrote {args.out}")
    else:
        print(text)


if __name__ == "__main__":
    main()
