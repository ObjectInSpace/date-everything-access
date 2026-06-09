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
import argparse, heapq, json, math, re, sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
BAKE = REPO / "artifacts/navigation/navigable_region.bake.json"
INTERACTABLES = REPO / "artifacts/navigation/thirdpersongreybox-interactables.json"
NAVDATA = REPO / "artifacts/navigation/thirdpersongreybox-navigation-data.json"

NAVIGABLE_CHAR = "N"
# Clearance-cost A*: bias routes away from walls so the follower isn't sent through
# sub-passable pinches when a wider alternative exists. The dominant in-game autowalk
# stalls cluster at a SMALL set of shared chokepoints (Hall1 ground ~(14,-6.3), Hall2
# upper) where the planned path threads a 0.0-0.2m pinch even though a 1.2-1.6m-wide
# route sits within ~2m — A* takes the tight path only because it's shorter. A bounded
# per-step clearance penalty makes A* prefer the wide route there, WITHOUT detouring
# real unavoidable ~1.0m doorways (those have no wider alternative, so the penalty
# applies equally to all their cells and doesn't change the choice). See
# [[project-navigation-clearance-cost-rejected-2026-05-29]] (re-opened/reconciled) and
# [[project-navigation-corner-dilation-severance-2026-05-29]].
CLEARANCE_TARGET_CELLS = 4       # >= this many cells to nearest wall (0.8m) ⇒ no penalty
CLEARANCE_PENALTY_PER_CELL_M = 0.15  # added per missing clearance-cell below target, in metres
CORNER_WAYPOINT_DEG = 30.0       # smoothing: keep vertices with turn > this
DOOR_TAG_RADIUS_M = 0.8          # segment tagged with door if door XZ is within this distance of the segment
MIN_INTERACTION_RADIUS_M = 0.5
MAX_INTERACTION_RADIUS_M = 7.5
# Radius to snap an explicit start/goal world position to the nearest navigable
# cell. Kept in sync with SimpleNavPlanner.NearestNavigableSearchM (6.0m): real
# runtime starts often land >4m off-mesh beside furniture (fireplace, closet),
# so the old 4.0m caused no_path + repeated replans. See SimpleNavPlanner.cs.
NEAREST_NAVIGABLE_SEARCH_M = 6.0


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
        # Per-door freed-cells (populated by Planner when a doors-open set is set).
        # Cells in this set are treated as navigable regardless of the bitmap.
        self.extra_navigable = set()
        # Per-door blocked cells (e.g. for forcing a door closed). Unused today
        # but reserved so consumers can express both directions symmetrically.
        self.extra_blocked = set()
        # Raw per-door freed-cell map keyed by door name: {name: set((ix,iz))}.
        # Planner builds this lazily for re-applying different doors-open sets.
        self.doors_freed_by_name = {}
        # Same shape, for state-gated walls (DresserWall and similar).
        self.state_walls_freed_by_name = {}
        # Lazily-built clearance map: per cell, distance (in cells, capped at
        # CLEARANCE_TARGET_CELLS) to the nearest non-navigable cell. Drives the
        # clearance-cost penalty in Planner.neighbors. Invalidated whenever overlays
        # change navigability (doors/state-walls opening), rebuilt on next access.
        self._clearance = None

    def in_bounds(self, ix, iz):
        return 0 <= ix < self.nx and 0 <= iz < self.nz

    def navigable(self, ix, iz):
        if not self.in_bounds(ix, iz):
            return False
        if (ix, iz) in self.extra_blocked:
            return False
        if (ix, iz) in self.extra_navigable:
            return True
        return self.rows[ix][iz] == NAVIGABLE_CHAR

    def _build_clearance(self):
        """Multi-source BFS from every non-navigable cell: each navigable cell gets
        its 4-connected distance (in cells, capped at CLEARANCE_TARGET_CELLS) to the
        nearest wall. O(nx*nz), built once per overlay state. Capping at the target
        means roomy cells all share the max and incur no penalty — only the gradient
        inside the tight band matters. L1 distance slightly under-counts diagonal
        clearance, which is the safe (conservative) direction for wall avoidance."""
        from collections import deque
        cap = CLEARANCE_TARGET_CELLS
        nx, nz = self.nx, self.nz
        dist = [[cap] * nz for _ in range(nx)]
        dq = deque()
        for ix in range(nx):
            row = dist[ix]
            for iz in range(nz):
                if not self.navigable(ix, iz):
                    row[iz] = 0
                    dq.append((ix, iz))
        while dq:
            x, z = dq.popleft()
            d = dist[x][z]
            if d >= cap:
                continue
            for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                a, b = x + dx, z + dz
                if 0 <= a < nx and 0 <= b < nz and dist[a][b] > d + 1:
                    dist[a][b] = d + 1
                    dq.append((a, b))
        self._clearance = dist

    def clearance(self, ix, iz):
        """Cells-to-nearest-wall (capped at CLEARANCE_TARGET_CELLS) for a cell.
        Lazily builds the map; out-of-bounds returns 0."""
        if not self.in_bounds(ix, iz):
            return 0
        if self._clearance is None:
            self._build_clearance()
        return self._clearance[ix][iz]

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
    def __init__(self, bake, doors_open=None, state_walls_open=None):
        self.bake = bake
        self.floors = {f["label"]: Floor(f) for f in bake["floors"]}
        self.cell_size = next(iter(self.floors.values())).cell_size
        # Active overlay names. Sources stored separately so applying one set
        # doesn't wipe another's effect.
        self._open_doors = set()
        self._open_state_walls = set()
        # Names of doors whose authoritative scene-load state is open. Lets a
        # consumer request doors_open="default" — the real configuration the
        # player faces at load — instead of guessing "all". Populated from the
        # bake's per-door `default_open` (carried from the exporter's Door.Open).
        self._default_open_doors = set()
        # Names of doors the player can open during normal play: every door that
        # is NOT locked. Lets a consumer request doors_open="unlocked" — the
        # coverage model that matches the in-game executor (it opens any door on
        # the path it reaches) while still hard-blocking the genuinely locked
        # door. From the bake's per-door `locked` (exporter's Door.Locked).
        self._unlocked_doors = set()
        self._locked_doors = set()
        # Index per-door freed-cells from the bake into each floor.
        for f_raw in bake["floors"]:
            floor = self.floors[f_raw["label"]]
            for door in f_raw.get("doors", []):
                name = door.get("name")
                if not name:
                    continue
                if door.get("default_open"):
                    self._default_open_doors.add(name)
                if door.get("locked"):
                    self._locked_doors.add(name)
                else:
                    self._unlocked_doors.add(name)
                cells = {tuple(c) for c in door.get("freed_cells", [])}
                if not cells:
                    continue
                # Multiple door records may share a name (different cupboards
                # with identical GameObjectName) — union their freed cells.
                if name in floor.doors_freed_by_name:
                    floor.doors_freed_by_name[name] |= cells
                else:
                    floor.doors_freed_by_name[name] = cells
            for wall in f_raw.get("state_walls", []):
                name = wall.get("name")
                if not name:
                    continue
                cells = {tuple(c) for c in wall.get("freed_cells", [])}
                if not cells:
                    continue
                if name in floor.state_walls_freed_by_name:
                    floor.state_walls_freed_by_name[name] |= cells
                else:
                    floor.state_walls_freed_by_name[name] = cells
        # Locked is authoritative regardless of record ordering: if any record
        # for a name is locked, that door is not player-openable.
        self._unlocked_doors -= self._locked_doors
        if doors_open:
            self.apply_doors_open(doors_open)
        if state_walls_open:
            self.apply_state_walls_open(state_walls_open)
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
    def apply_doors_open(self, doors_open):
        """Set the doors-open set. `doors_open` is an iterable of door names; or
        the literal string "all" to open every known door; "unlocked" to open
        every door the player can open during normal play (all but the locked
        ones) — the coverage model matching the in-game executor, which opens any
        door on the path it reaches; or "default" to open exactly the doors whose
        authoritative scene-load state is open (from the exporter's Door.Open,
        carried in the bake). Pass `None` to clear (all closed). All of
        "unlocked"/"default"/`locked` derive from the exporter's per-door state
        carried through the bake — no guessing. Replaces any prior doors-open
        state but preserves state_walls_open. Unknown names are silently ignored
        so callers can pass a static list across scenes."""
        if doors_open is None:
            self._open_doors = set()
        elif isinstance(doors_open, str) and doors_open == "all":
            self._open_doors = None  # sentinel: "all known"
        elif isinstance(doors_open, str) and doors_open == "unlocked":
            self._open_doors = set(self._unlocked_doors)
        elif isinstance(doors_open, str) and doors_open == "default":
            self._open_doors = set(self._default_open_doors)
        else:
            self._open_doors = set(doors_open)
        self._rebuild_extra_navigable()

    def apply_state_walls_open(self, state_walls_open):
        """Set the state-walls-open set. Same convention as apply_doors_open.
        Walls in this set are treated as released (e.g. DresserWall after the
        `leave_house` Ink command). Replaces any prior state-walls state but
        preserves doors_open."""
        if state_walls_open is None:
            self._open_state_walls = set()
        elif isinstance(state_walls_open, str) and state_walls_open == "all":
            self._open_state_walls = None
        else:
            self._open_state_walls = set(state_walls_open)
        self._rebuild_extra_navigable()

    def _rebuild_extra_navigable(self):
        # _open_doors / _open_state_walls semantics:
        #   None      = "all known" (include every entry)
        #   set()     = "none open" (skip every entry)
        #   set(...)  = include only entries matching a name in the set
        for floor in self.floors.values():
            extra = set()
            for name, cells in floor.doors_freed_by_name.items():
                if self._open_doors is None or name in self._open_doors:
                    extra |= cells
            for name, cells in floor.state_walls_freed_by_name.items():
                if self._open_state_walls is None or name in self._open_state_walls:
                    extra |= cells
            floor.extra_navigable = extra
            floor._clearance = None  # navigability changed → clearance map stale

    def door_names(self):
        """Every door that has freed-cells on at least one floor. Useful for
        diagnostics and constructing CLI defaults."""
        names = set()
        for floor in self.floors.values():
            names.update(floor.doors_freed_by_name.keys())
        return names

    def state_wall_names(self):
        """Every state-wall with freed-cells data on at least one floor."""
        names = set()
        for floor in self.floors.values():
            names.update(floor.state_walls_freed_by_name.keys())
        return names

    def _floor_for_y(self, y):
        best, bestd = None, math.inf
        for label, f in self.floors.items():
            d = abs(f.floor_y - y)
            if d < bestd:
                bestd, best = d, label
        return best if bestd < 2.0 else None

    def _floor_for_target_y(self, y):
        """Pick the floor a player stands on to interact with a target at world Y.

        Unlike `_floor_for_y` (which picks the closest baked floor), this picks
        the floor *below* the target. A magnifying glass at Y=4 on a library
        shelf, a clock at Y=7 on a kitchen wall, and food at Y=8 inside an
        upper cupboard are all accessed from the ground floor — the player
        looks up at them, hits them with the dateviator beam, etc. See
        [[feedback-interaction-includes-look-and-glasses]].

        Rule: target_floor = highest floor whose floor_y - APPROACH_TOL is
        <= y. APPROACH_TOL=0.3m is tight on purpose — items mounted just
        below the upper floor (e.g. recessed lights at Y=12.2 in the
        ground-floor ceiling) need to route to ground, not upper. The 0.3m
        slack absorbs floor-surface-Y model quirks (ground floor mesh is at
        TopY=-0.57 vs floor_y=-0.5) without admitting near-ceiling props.
        Falls back to the lowest floor when y is below all of them.
        """
        APPROACH_TOL_M = 0.1
        ordered = sorted(self.floors.items(), key=lambda kv: -kv[1].floor_y)
        for label, f in ordered:
            if y >= f.floor_y - APPROACH_TOL_M:
                return label
        return ordered[-1][0]

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
                if not f.navigable(nx, nz):
                    continue
                # Corner-cut prevention: a diagonal step is only valid if BOTH
                # orthogonally-adjacent cells are navigable too. Without this, A*
                # slips through a corner where two blockers touch — a sub-capsule
                # pinhole the player can't physically thread (e.g. the 0.2m
                # diagonal leak through SM_Walls_Bedroom that routed autowalk into
                # a dead pocket instead of the real door). Forbidding the cut
                # closes such pinholes at the pathfinding level without changing
                # the bake's navigable cells. See [[project-navigation-executor-corner-stall]].
                if dx != 0 and dz != 0:
                    if not (f.navigable(a + dx, b) and f.navigable(a, b + dz)):
                        continue
                # Bounded clearance penalty on the destination cell: cells nearer than
                # CLEARANCE_TARGET_CELLS to a wall cost extra metres, so A* prefers a
                # wider route when one is only modestly longer (reroutes avoidable
                # pinches) but still threads a genuinely unavoidable doorway (all its
                # cells are penalized equally, so the choice is unchanged). The penalty
                # only raises g-costs, so the Euclidean heuristic stays admissible.
                deficit = CLEARANCE_TARGET_CELLS - f.clearance(nx, nz)
                penalty = deficit * CLEARANCE_PENALTY_PER_CELL_M if deficit > 0 else 0.0
                out.append(((floor, nx, nz), cost * self.cell_size + penalty, {"kind": "walk"}))
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

_SEGMENT_SAMPLE_STEP_FACTOR = 0.35


def _segment_is_clear(planner, a, b, min_clearance=0):
    """True iff every cell along the straight world-space segment from a to b is
    navigable on a's floor. Same-floor only; cross-floor and virtual-node segments
    are conservatively treated as non-clear so they always carry an explicit
    waypoint.

    When `min_clearance` > 0, every sampled cell must additionally have clearance
    (cells-to-nearest-wall) >= min_clearance. The smoother passes the minimum
    clearance of the raw A* sub-path it is collapsing, so a line-of-sight shortcut
    cannot straighten a margin-keeping path back to HUG an obstacle: A* (with the
    clearance-cost) curves around furniture/doorframe jambs with ~0.4m margin, but a
    plain navigability-only shortcut flattens it flush against the obstacle (0.2m),
    so the follower grazes. A shortcut through a genuinely tight spot the raw path
    already threaded (min_clearance already low there) is still allowed.
    See [[project-navigation-stair-arrival-stop-2026-05-29]] (doorframe/furniture class).

    Uses supercover-style sampling at ~0.35 cells per step. Earlier versions used
    Bresenham, which steps in (sx, sz) independently and could slip diagonally
    between two blocked cells that share only a corner, producing routes that
    visibly cross walls. The offline validator's segment check uses the same
    sampling, so planner and validator now agree by construction."""
    if a[0] != b[0] or a[0].startswith("@"):
        return False
    floor = planner.floors[a[0]]
    ax, az = floor.cell_to_world(a[1], a[2])
    bx, bz = floor.cell_to_world(b[1], b[2])
    distance = math.hypot(bx - ax, bz - az)
    step = max(floor.cell_size * _SEGMENT_SAMPLE_STEP_FACTOR, 0.02)
    samples = max(1, int(math.ceil(distance / step)))
    last_cell = None
    for i in range(samples + 1):
        t = i / samples
        wx = ax + (bx - ax) * t
        wz = az + (bz - az) * t
        cell = floor.world_to_cell(wx, wz)
        if cell == last_cell:
            continue
        if not floor.navigable(cell[0], cell[1]):
            return False
        if min_clearance > 0 and floor.clearance(cell[0], cell[1]) < min_clearance:
            return False
        # Reject corner-cuts: if the sampled cell moved diagonally from the
        # previous one, both orthogonal in-between cells must be navigable too.
        # Point-sampling alone can hop across a 1-cell corner pinhole the player
        # can't fit through, re-introducing the impassable-gap routes that the
        # A* corner-cut prevention closes. Mirrors Planner.neighbors. See
        # [[project-navigation-executor-corner-stall]].
        if last_cell is not None:
            ddx = cell[0] - last_cell[0]
            ddz = cell[1] - last_cell[1]
            if ddx != 0 and ddz != 0:
                if not (floor.navigable(last_cell[0] + ddx, last_cell[1]) and
                        floor.navigable(last_cell[0], last_cell[1] + ddz)):
                    return False
        last_cell = cell
    return True


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
    # Min clearance (cells, capped) of the raw A* cells over path[lo..hi]. A
    # line-of-sight shortcut spanning those cells must hold at least this much
    # clearance, so it can't straighten a margin-keeping curve back against an
    # obstacle — but a shortcut through a spot the raw path already threaded tight
    # stays allowed. Virtual nodes are skipped (they have no grid clearance).
    def _subpath_min_clearance(lo, hi):
        mn = CLEARANCE_TARGET_CELLS
        for k in range(lo, hi + 1):
            n = path[k]
            if n[0].startswith("@"):
                continue
            c = planner.floors[n[0]].clearance(n[1], n[2])
            if c < mn:
                mn = c
        return mn

    los = [path[0]]
    last_anchor_idx = 0
    anchor_path_idx = 0
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
            anchor_path_idx = i
            i += 1
            continue
        # Keep skipping while the straight segment stays clear AND holds the
        # clearance of the raw sub-path it replaces (so it rounds obstacles with
        # the margin A* found instead of hugging them).
        if _segment_is_clear(planner, los[-1], node,
                             min_clearance=_subpath_min_clearance(anchor_path_idx, i)):
            i += 1
            continue
        # Otherwise the previous cell was the last clear endpoint; anchor it.
        # Guard: if we'd re-anchor at the same index we already anchored from
        # (segment-clear sampling flickered on a freed-cells overlay), force
        # advance so the loop cannot spin.
        if i - 1 <= last_anchor_idx:
            los.append(node)
            last_anchor_idx = i
            anchor_path_idx = i
            i += 1
        else:
            los.append(prev)
            last_anchor_idx = i - 1
            anchor_path_idx = i - 1
    # Always end at the final node.
    if los[-1] != path[-1]:
        los.append(path[-1])

    # Pass 2: drop interior waypoints whose corner angle is shallow AND removing
    # them still leaves a clear segment between their neighbours.
    out = [los[0]]
    for j in range(1, len(los) - 1):
        a, b, c = out[-1], los[j], los[j + 1]
        # Never drop a floor-transition endpoint. b is a stair/teleporter landing
        # when its floor differs from a neighbour; the player MUST pass through both
        # landings to change floors. Pass-2 angles are XZ-only, so the two landings
        # of a staircase (same XZ, different Y) look collinear and the GROUND-side
        # landing was being pruned — leaving the follower to steer from the stair-top
        # XZ straight at the next ground corridor point, cutting across the stairs
        # into the side wall mid-descent (the SM_Walls_Hall1 stair-exit stall).
        # See [[project-navigation-hall1-runtime-truth-2026-05-29]].
        if b[0] != a[0] or b[0] != c[0] or b[0].startswith("@") or \
           a[0].startswith("@") or c[0].startswith("@"):
            out.append(b); continue
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
        # Only drop b if the a→c shortcut also holds b's clearance — else pruning a
        # corner of a margin-keeping curve would route a→c flush against the obstacle
        # b was rounding.
        b_clear = planner.floors[b[0]].clearance(b[1], b[2]) if not b[0].startswith("@") else 0
        if angle_deg <= CORNER_WAYPOINT_DEG and _segment_is_clear(planner, a, c, min_clearance=b_clear):
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


# ---------- object-node resolution (for the object-reachability sweep) ----------

# Mirror of AccessibilityWatcher.StripModelAuthoringTokens so an object's offline
# node name matches the human-readable label the in-game picker speaks. Operates on
# the raw underscore-joined Unity name and leaves a clean stem ("Clock", "Bush").
_MODEL_INSTANCE_RE = re.compile(r"\s*\(\d+\)\s*$")
_MODEL_UPDATE_RE = re.compile(r"_MODEL_UPDATE\d*", re.IGNORECASE)
_MODEL_PREFIX_RE = re.compile(r"^(?:SM|SK)_+", re.IGNORECASE)


def strip_model_authoring_tokens(value):
    if not value or not value.strip():
        return value
    stripped = _MODEL_INSTANCE_RE.sub("", value)
    stripped = _MODEL_UPDATE_RE.sub("", stripped)
    stripped = _MODEL_PREFIX_RE.sub("", stripped)
    stripped = stripped.strip().strip("_").strip()
    return value if not stripped.strip() else stripped


def object_display_name(item):
    """Human-readable stem for an interactable, used as the node label. Returns the
    cleaned GameObjectName; objects whose name still collapses to nothing meaningful
    (pure punctuation/empty) are caller-rejected via the None return."""
    raw = item.get("GameObjectName") or ""
    cleaned = strip_model_authoring_tokens(raw)
    if not cleaned or not cleaned.strip():
        return None
    return cleaned


# Unity layers that carry navigable interactable props in this scene's export.
# Layer 0 (Default) and 31 (the interactable layer) hold the real objects; the
# stray layer-2 (Ignore Raycast) and layer-18 entries are scaffolding, not
# player-facing interactables. Matches the picker's "real object" intent.
OBJECT_NODE_LAYERS = (0, 31)


def is_statically_pickable(item):
    """Static (save-independent) half of the picker's eligibility rule: active, on a
    navigable interactable layer, and resolving to a real human-readable name. The
    runtime encounter filter (met/interacted/examined) is intentionally NOT applied —
    the sweep tests what the player COULD navigate to, not what this save has seen."""
    if not item.get("IsActive"):
        return False
    if item.get("Layer") not in OBJECT_NODE_LAYERS:
        return False
    return object_display_name(item) is not None


def resolve_object_node(planner, item):
    """Snap one interactable to its interaction stand-cell, mirroring plan()'s target
    resolution: floor-by-Y, then the nearest navigable cell within the (clamped)
    interaction radius, falling back to nearest-navigable search. Returns
    (floor_label, ix, iz) or None when the object sits off every navigable floor (e.g.
    walled off behind a gate the current door/state-wall params keep closed)."""
    pos = item.get("Position") or {}
    tx, ty, tz = pos.get("x"), pos.get("y"), pos.get("z")
    if tx is None or ty is None or tz is None:
        return None
    tfloor = planner._floor_for_target_y(ty)
    if tfloor is None:
        return None
    floor = planner.floors[tfloor]
    radius = item.get("InteractionRadius", 1.0) or 1.0
    radius = max(MIN_INTERACTION_RADIUS_M, min(radius, MAX_INTERACTION_RADIUS_M))
    goals = goal_cells_around(floor, tx, tz, radius)
    if goals:
        # Stand-cell = the navigable cell nearest the object centre (deterministic).
        best = min(goals, key=lambda c: (floor.cell_to_world(*c)[0] - tx) ** 2 +
                                        (floor.cell_to_world(*c)[1] - tz) ** 2)
        return (tfloor, best[0], best[1])
    n = floor.nearest_navigable(tx, tz, max_radius_m=NEAREST_NAVIGABLE_SEARCH_M)
    if n is None:
        return None
    return (tfloor, n[0], n[1])


def object_sweep_nodes(planner, items):
    """Build the deduped object-node list for the sweep. Each node is a stand-cell the
    player would occupy to interact with one or more objects. Objects that snap to the
    SAME stand-cell collapse into a single node (kills the 48-books / SM_-duplicate
    redundancy) — the merged node carries all member object names so the report can
    attribute a covered cell back to every object it serves.

    Returns a list of dicts: {floor, cell:(ix,iz), names:[...], object_ids:[...],
    representative:item}. Objects that resolve to no navigable cell (off-floor or
    gate-blocked under the current door/state-wall params) are returned separately as
    `unreachable` so the manifest can report them as no_path without a drive."""
    by_cell = {}
    unreachable = []
    for item in items:
        if not is_statically_pickable(item):
            continue
        node = resolve_object_node(planner, item)
        name = object_display_name(item)
        if node is None:
            unreachable.append({"name": name, "object_id": item.get("GameObjectId"),
                                "position": item.get("Position")})
            continue
        slot = by_cell.setdefault(node, {"floor": node[0], "cell": (node[1], node[2]),
                                         "names": [], "object_ids": [], "representative": item})
        slot["names"].append(name)
        slot["object_ids"].append(item.get("GameObjectId"))
    return list(by_cell.values()), unreachable


# ---------- top-level plan() ----------

def plan(target_spec, start_xz=None, start_floor=None, interaction_radius_override=None):
    bake = load_bake()
    planner = Planner(bake)
    items = load_interactables()
    target = find_interactable(items, target_spec)
    if target is None:
        raise SystemExit(f"no interactable matches {target_spec!r}")

    tx = target["Position"]["x"]; ty = target["Position"]["y"]; tz = target["Position"]["z"]
    tfloor = planner._floor_for_target_y(ty)
    if tfloor is None:
        raise SystemExit(f"target Y={ty} not on a baked floor")
    radius = interaction_radius_override or target.get("InteractionRadius", 1.0)
    radius = max(MIN_INTERACTION_RADIUS_M, min(radius, MAX_INTERACTION_RADIUS_M))
    goals = goal_cells_around(planner.floors[tfloor], tx, tz, radius)
    if not goals:
        # Fall back to the nearest navigable cell.
        n = planner.floors[tfloor].nearest_navigable(tx, tz, max_radius_m=NEAREST_NAVIGABLE_SEARCH_M)
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
        n = planner.floors[start_floor].nearest_navigable(start_xz[0], start_xz[1], max_radius_m=NEAREST_NAVIGABLE_SEARCH_M)
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
