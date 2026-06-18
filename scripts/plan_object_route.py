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

sys.path.insert(0, str(Path(__file__).resolve().parent))
# Synthetic-eye interaction LOS — shared, parity-proven raycaster (see los_geometry +
# [[project_navigation_offline_los_validator_2026_06_13]]). Used to filter goal cells
# to those with a clear interaction line, mirroring SimpleNavPlanner exactly.
import los_geometry as _los

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
# NOTE (2026-06-13): a steep flat surcharge for clearance<=2 was TRIED to reroute the
# tight-gap follower stalls and REVERTED — only 29→24 stalls, +1.8% length, and the
# dominant upper-bedroom cluster was UNCHANGED because that furniture-dense area has
# NO wider alternative (every approach is a 1-2 cell gap), so the surcharge applies
# uniformly and can't reroute. Confirms the planner can't fix these; it's a follower
# threading requirement. See [[project-navigation-offline-validation-2026-06-13]].
CORNER_WAYPOINT_DEG = 30.0       # smoothing: keep vertices with turn > this
# Fallback InteractionRadius for the DESTINATION door-tag rule when a target's own radius is
# 0/unknown. Mirror of C# DoorInteractRadiusFallbackM. The old DOOR_TAG_RADIUS_M=2.5 magic
# constant is GONE: on-path tagging uses each door's geometric opening radius (+1 cell), and the
# destination rule uses the game's InteractionRadius. See tag_doors.
DOOR_INTERACT_RADIUS_FALLBACK_M = 7.5
# (No interaction-radius constant: the planner uses each object's own InteractionRadius verbatim —
# parity with C# SimpleNavPlanner. Interaction is gated on radius + LOS, not on any bound we impose.)
# Player-capsule + safety margin from the target's collider face: goal cells whose XZ distance to
# the collider's nearest bounds point is below this are DROPPED (you can't stand inside the prop).
# Mirrors SimpleNavPlanner.TargetColliderClearanceM (0.5m).
TARGET_COLLIDER_CLEARANCE_M = 0.5
# Performance safety valve (not a correctness limit): max A* node expansions while gathering goal
# candidates for the fewest-legs pick. Parity with SimpleNavPlanner.GoalSearchMaxExpansions (3000).
GOAL_SEARCH_MAX_EXPANSIONS = 3000
# Radius to snap an explicit start/goal world position to the nearest navigable
# cell. Kept in sync with SimpleNavPlanner.NearestNavigableSearchM (6.0m): real
# runtime starts often land >4m off-mesh beside furniture (fireplace, closet),
# so the old 4.0m caused no_path + repeated replans. See SimpleNavPlanner.cs.
NEAREST_NAVIGABLE_SEARCH_M = 6.0

# Classifying an unreachable object as exterior decor vs gate-blocked interior. Probe out to
# EXTERIOR_CLASSIFY_RADIUS_M for ANY walkable cell; if the nearest one is farther than
# GATE_BLOCKED_MAX_DISTANCE_M the object is across-the-street decor (fence/tree/drone), not a
# gate we failed to open. The threshold is comfortably past the widest real interior pocket
# (a closet/gate puts floor within a couple metres on the far side) and well short of the
# tens-of-metres gap to exterior props.
EXTERIOR_CLASSIFY_RADIUS_M = 60.0
GATE_BLOCKED_MAX_DISTANCE_M = 8.0


# ---------- bake loading + cell/world conversions ----------

def load_bake():
    return json.loads(BAKE.read_text())


def load_fixture_roster():
    """The bake's canonical static target set (report["fixtures"]): already filtered
    (active + named + non-exterior), identity-deduped (lighting presets), routing-unit
    merged (48 books -> 1, cutlery -> 1, etc.), and best-available-located (bounds centre,
    not rig-origin). Consumers read THIS instead of re-deriving the set from the raw export,
    so set construction lives in the bake and the planner owns only navigation. Returns the
    list (possibly empty if an older bake predates the roster). See
    project_navigation_fixture_roster_design."""
    return load_bake().get("fixtures") or []


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
        # Per-door open-leaf footprint {name: set((ix,iz))} — open-state navigable REMOVALS,
        # the mirror of doors_freed_by_name. Mirror of C# Floor.DoorBlockedByName.
        self.doors_blocked_by_name = {}
        # Reverse map cell -> owning door name, for cell-ownership tagging (a route crosses
        # door D iff it steps on one of D's freed/threshold cells; cells uniquely owned).
        # Mirror of C# Floor.DoorByFreedCell.
        self.door_by_freed_cell = {}
        # Per-door world crossing centroid {name: (wx, wz)} — centroid of freed+threshold cells,
        # the point the DoorOpening waypoint aims through. Mirror of C# Floor.DoorCrossingByName.
        self.door_crossing_by_name = {}
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
        # Container doors (operable-only, no walk-through) — for the destination tag rule, since
        # they have no crossing cells in door_crossing_by_name. Mirror of C# container handling.
        self._container_door_names = set()
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
                if door.get("container_operable_only"):
                    self._container_door_names.add(name)
                # Per-door indexing (mirror of C# IndexDoorFreedCells). The crossing cells are
                # freed + threshold; from them derive:
                #   door_by_freed_cell    — reverse map (cell -> door) for cell-ownership tagging
                #   door_crossing_by_name — centroid, the DoorOpening waypoint aim point
                #   doors_freed_by_name   — open-state navigable additions
                #   doors_blocked_by_name — open-leaf footprint (open-state navigable removals)
                # Uniform over passage/container/threshold-less doors — no opening center/radius/
                # cells special cases. See [[project-navigation-doorway-capsule-clearance-2026-06-18]].
                freed = {tuple(c) for c in door.get("freed_cells", [])}
                thr = {tuple(c) for c in door.get("threshold_cells_list", [])}
                crossing = freed | thr
                if crossing:
                    for cell in crossing:
                        floor.door_by_freed_cell[cell] = name
                    if name not in floor.door_crossing_by_name:
                        sx = sum(c[0] for c in crossing)
                        sz = sum(c[1] for c in crossing)
                        n = len(crossing)
                        floor.door_crossing_by_name[name] = floor.cell_to_world(sx / n, sz / n)
                blocked = {tuple(c) for c in door.get("open_blocked_cells", [])}
                if blocked:
                    if name in floor.doors_blocked_by_name:
                        floor.doors_blocked_by_name[name] |= blocked
                    else:
                        floor.doors_blocked_by_name[name] = blocked
                if freed:
                    # Multiple door records may share a name — union their freed cells.
                    if name in floor.doors_freed_by_name:
                        floor.doors_freed_by_name[name] |= freed
                    else:
                        floor.doors_freed_by_name[name] = freed
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
            # Ramp interior points (world [x,y,z], excluding the two landing endpoints):
            # emitted between the landings when a route crosses this seam so the offline
            # route matches the C# planner's stair expansion (parity). Directed: the bake
            # path is bottom->top (ground->upper); reverse for the descending edge.
            full = e.get("path") or []
            interior = full[1:-1] if len(full) > 2 else []
            self.edges_from.setdefault(a, []).append(
                (b, cost, {"kind": "stairs", "path": e["source_path"], "ramp_xyz": list(interior)}))
            self.edges_from.setdefault(b, []).append(
                (a, cost, {"kind": "stairs", "path": e["source_path"], "ramp_xyz": list(reversed(interior))}))
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
            # Open doors REMOVE their swung-leaf footprint (mirror of extra_navigable, opposite
            # sign): navigable() checks extra_blocked first, so the leaf arc is blocked when open.
            blocked = set()
            for name, cells in floor.doors_blocked_by_name.items():
                if self._open_doors is None or name in self._open_doors:
                    blocked |= cells
            floor.extra_blocked = blocked
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

    def astar(self, start_node, goal_nodes, goal_floor, goal_wx, goal_wz, goal_quality=None):
        goal_set = set(goal_nodes)
        if start_node in goal_set:
            return [start_node], 0.0, []
        open_heap = [(self.heuristic(start_node, goal_floor, goal_wx, goal_wz), 0.0, start_node)]
        came_from = {start_node: (None, None)}  # node → (prev_node, edge_meta)
        gscore = {start_node: 0.0}
        closed = set()
        # Goal selection objective (parity with C# SimpleNavPlanner.AStar): FEWEST LEGS, then
        # BEST VIEW QUALITY (most unobstructed sightline — biases high-mounts to a stand-back
        # diagonal), then CLOSEST to the object, then cheapest walk. View quality replaces raw
        # proximity as the tiebreak among equally-simple routes; `goal_quality` is the dict the
        # LOS filter built (None ⇒ all-zero ⇒ falls back to closest, the old behaviour). We
        # consider ALL reachable goal cells (a far cell can be the SIMPLEST to reach, and legs is
        # primary), bounded only by a generous expansion CAP as a perf safety valve — not a
        # correctness limit. A* records the shortest cell-path to each reached goal, then we
        # smooth + count legs per candidate.
        reached_goals = []
        expansions = 0
        hit_cap = False
        while open_heap:
            _, g, node = heapq.heappop(open_heap)
            if g > gscore.get(node, math.inf):
                continue
            if node in closed:
                continue
            closed.add(node)
            expansions += 1
            if expansions > GOAL_SEARCH_MAX_EXPANSIONS:
                hit_cap = True
                break
            if node in goal_set:
                reached_goals.append(node)
                if len(reached_goals) == len(goal_set):
                    break
                # Goal cells are terminal — don't expand past them.
                continue
            for nbr, cost, meta in self.neighbors(node):
                ng = g + cost
                if ng < gscore.get(nbr, math.inf):
                    gscore[nbr] = ng
                    came_from[nbr] = (node, meta)
                    h = self.heuristic(nbr, goal_floor, goal_wx, goal_wz)
                    heapq.heappush(open_heap, (ng + h, ng, nbr))

        if not reached_goals:
            # The expansion cap is a PERF bound, NOT a reachability verdict. If we hit it
            # without reaching any goal, the object may still be perfectly reachable — just
            # FAR from this start (the heuristic-guided search ran out of budget before
            # arriving). Returning no_path here is the bug that surfaced as ~127 false
            # no_path in the offline sweep (all BFS-confirmed reachable). Escalate: a single
            # uncapped reachability + shortest-path pass to the NEAREST reachable goal. This
            # only runs on the rare capped leg (the common leg reaches a goal well under the
            # cap), so the perf valve still protects the typical case. Mirror of the C#
            # GoalSearchMaxExpansions escalation. See sweep no_path diagnosis 2026-06-15.
            if hit_cap:
                escalated = self._reach_nearest_goal(start_node, goal_set, goal_floor, goal_wx, goal_wz)
                if escalated is not None:
                    return escalated
            return None, math.inf, []

        def reconstruct(goal):
            path, edges = [], []
            cur = goal
            while cur is not None:
                path.append(cur)
                prev, meta = came_from[cur]
                if prev is not None:
                    edges.append(meta)
                cur = prev
            path.reverse(); edges.reverse()
            return path, edges

        best = None  # (key, path, edges, gcost)
        for goal in reached_goals:
            path, edges = reconstruct(goal)
            legs = len(smooth_path(path, self)) - 1
            f = self.floors[goal[0]]
            wx, wz = f.cell_to_world(goal[1], goal[2])
            dist = math.hypot(wx - goal_wx, wz - goal_wz)
            gcost = gscore[goal]
            quality = goal_quality.get(goal, 0.0) if goal_quality else 0.0
            # Higher quality is better, so negate it for the ascending tuple comparison.
            key = (legs, -quality, dist, gcost)
            if best is None or key < best[0]:
                best = (key, path, edges, gcost)
        _, best_path, best_edges, best_g = best
        return best_path, best_g, best_edges

    def _reach_nearest_goal(self, start_node, goal_set, goal_floor, goal_wx, goal_wz):
        """Uncapped A* to the SINGLE nearest reachable goal. Used only as the escalation when
        the capped goal-gathering search ran out of budget without reaching any goal (a far
        object). Drops the fewest-legs/leg-optimal niceties — we just need a valid route, since
        this leg only exists in the offline harness's cross-object chain (the in-game sweep plans
        from the player's real position and never makes legs this long). Returns
        (path, gcost, edges) or None if genuinely unreachable."""
        open_heap = [(self.heuristic(start_node, goal_floor, goal_wx, goal_wz), 0.0, start_node)]
        came_from = {start_node: (None, None)}
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
                path, edges = [], []
                cur = node
                while cur is not None:
                    path.append(cur)
                    prev, meta = came_from[cur]
                    if prev is not None:
                        edges.append(meta)
                    cur = prev
                path.reverse(); edges.reverse()
                return path, gscore[node], edges
            for nbr, cost, meta in self.neighbors(node):
                ng = g + cost
                if ng < gscore.get(nbr, math.inf):
                    gscore[nbr] = ng
                    came_from[nbr] = (node, meta)
                    h = self.heuristic(nbr, goal_floor, goal_wx, goal_wz)
                    heapq.heappush(open_heap, (ng + h, ng, nbr))
        return None


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


# Only entries whose name the runtime SimpleNav.FindDoorByName can resolve to a live
# Door component are worth tagging. The DoorObjects export is a superset: alongside the
# openable Door leaves (Doors_Bathroom1, Doors_Office, ...) it lists the STATIC DOORFRAME
# meshes (SM_Doorframe_*), particle emitters, knobs, cameras, and per-node transform
# duplicates (_TRS / _MASTER / _MODEL_UPDATE). Tagging any of those gives the route a name
# FindDoorByName returns null for, so ResolveActiveDoorForSegment leaves ActiveDoor null and
# the executor never opens the door — the player drives into the closed leaf and stalls.
# Critically the frame mesh and its door leaf are ~1–2m apart (the leaf swings clear of the
# frame), so they are NOT deduped by XZ and the frame can out-compete the door on segment
# proximity. Filtering to openable names is what makes the door actually open.
# See [[project-navigation-object-sweep-door-tag-bug]].
_DOOR_SKIP_SUBSTR = (
    "Doorframe", "Particle", "Knob", "knob", "Collider", "Camera_", "LightOcclusion",
    "Lights", "bumper", "Boxes", "SM_", "Cupboards", "BreakerBox", "Dishwasher",
    "Stairs", "PF_", "Wall",
)
_DOOR_SKIP_SUFFIX = ("_TRS", "_MASTER", "_MODEL_UPDATE", "_Cam")


def _is_openable_door_name(name):
    if not name:
        return False
    if "door" not in name.lower() and "trapdoor" not in name.lower():
        return False
    if any(s in name for s in _DOOR_SKIP_SUBSTR):
        return False
    if name.endswith(_DOOR_SKIP_SUFFIX):
        return False
    return True


def _openable_doors_raw():
    """Openable Door leaves from the export: names the runtime FindDoorByName resolves to
    a real Door component. Origin placeholders (0,0) and off-map (vehicle) doors dropped."""
    nav = json.loads(NAVDATA.read_text(encoding="utf-8-sig"))
    seen = set()
    out = []
    for d in nav.get("DoorObjects", []):
        p = d.get("Position") or {}
        if "x" not in p:
            continue
        name = d.get("Name") or ""
        if not _is_openable_door_name(name):
            continue
        if abs(p["x"]) < 0.01 and abs(p["z"]) < 0.01:
            continue
        if abs(p["x"]) > 60 or abs(p["z"]) > 60:
            continue
        key = (round(p["x"], 2), round(p["z"], 2), round(p["y"], 2))
        if key in seen:
            continue
        seen.add(key)
        out.append({"id": d.get("Id"), "name": name, "xz": (p["x"], p["z"]), "y": p["y"]})
    return out


# Max XZ gap to consider a doorframe and an openable Door the same doorway.
_FRAME_DOOR_PAIR_RADIUS_M = 2.8


def door_positions():
    """Door tag table for tag_doors: each entry's POSITION marks where a route crosses the
    doorway, and its NAME is the openable Door the runtime can resolve + open.

    Why split position from name: the export gives the openable Door leaf's transform at its
    HINGE/PIVOT, offset ~1–2.5m to the side of the opening the player walks through, while the
    static SM_Doorframe_* mesh sits on the doorway CENTERLINE — exactly where the route
    segment passes. Tagging by the door pivot misses (route never comes within DOOR_TAG_RADIUS
    of the pivot); tagging by the frame name gives a name FindDoorByName can't open. So we pair
    each frame to its nearest openable Door and emit a tag AT THE FRAME with the DOOR'S name.
    Frames with no openable Door nearby are open archways (no leaf) — no tag needed. Openable
    doors with no frame (Trapdoor, sliding) fall back to tagging at their own position.
    See [[project-navigation-object-sweep-door-tag-bug]]."""
    nav = json.loads(NAVDATA.read_text(encoding="utf-8-sig"))
    doors = _openable_doors_raw()
    frames = [d for d in nav.get("DoorObjects", [])
              if "Doorframe" in (d.get("Name") or "") and "x" in (d.get("Position") or {})]

    table = []
    paired_door_keys = set()
    for fr in frames:
        p = fr["Position"]
        best = None
        for dr in doors:
            if abs(dr["y"] - p["y"]) > 3.5:
                continue
            dist = math.hypot(p["x"] - dr["xz"][0], p["z"] - dr["xz"][1])
            if best is None or dist < best[0]:
                best = (dist, dr)
        if best is None or best[0] > _FRAME_DOOR_PAIR_RADIUS_M:
            continue  # open archway — no leaf to open
        dist, dr = best
        paired_door_keys.add((round(dr["xz"][0], 2), round(dr["xz"][1], 2), round(dr["y"], 2)))
        table.append({
            "id": dr["id"],
            "name": dr["name"],          # resolvable Door name
            "xz": (p["x"], p["z"]),       # frame centerline (where the route crosses)
            "y": p["y"],
        })

    # Openable doors with no paired frame (sliding doors, trapdoor): tag at their own pose.
    for dr in doors:
        k = (round(dr["xz"][0], 2), round(dr["xz"][1], 2), round(dr["y"], 2))
        if k in paired_door_keys:
            continue
        table.append({"id": dr["id"], "name": dr["name"], "xz": dr["xz"], "y": dr["y"]})
    return table


def _segment_cells(ax, az, bx, bz):
    """Integer cells a straight segment passes through (Bresenham). Mirror of C#
    Floor.SegmentCells."""
    dx, dz = abs(bx - ax), abs(bz - az)
    sx = 1 if ax < bx else -1
    sz = 1 if az < bz else -1
    err = dx - dz
    x, z = ax, az
    while True:
        yield (x, z)
        if x == bx and z == bz:
            break
        e2 = 2 * err
        if e2 > -dz:
            err -= dz
            x += sx
        if e2 < dx:
            err += dx
            z += sz


def tag_doors(waypoints, planner, target_name=None, target_radius=0.0):
    """CELL-OWNERSHIP door tagging (mirror of C# SimpleNavPlanner.TagDoors). Two rules:
      (1) ON-PATH: a door is tagged on a segment that STEPS ON one of its freed/threshold
          cells (cells are uniquely owned — 0 cross-door overlap — so this is exact). No
          proximity radius. The route is planned in the doors-open state, so "crosses this
          cell" is the same uniform portal set the planner routes against.
      (2) DESTINATION: the target IS a door (opened in range like any object) — tag it on the
          final segment. Container items gated by a door resolve via rule (1) (the approach
          crosses that door's freed cells).
    See [[project-navigation-doorway-capsule-clearance-2026-06-18]]."""
    segments = []
    for i in range(len(waypoints) - 1):
        a, b = waypoints[i], waypoints[i + 1]
        seg = {"from": _node_to_dict(a, planner), "to": _node_to_dict(b, planner), "doors": []}
        if a[0] == b[0] and not str(a[0]).startswith("@"):
            floor = planner.floors[a[0]]
            seen = set()
            for cell in _segment_cells(a[1], a[2], b[1], b[2]):
                name = floor.door_by_freed_cell.get(cell)
                if name and name not in seen:
                    seen.add(name)
                    seg["doors"].append({"name": name, "distance_m": 0.0})
        segments.append(seg)

    # Rule (2): the target itself is a door — tag it on the final segment (opened in range).
    if segments and target_name:
        goal = waypoints[-1]
        gf = planner.floors.get(goal[0])
        is_door = (gf is not None and (target_name in gf.door_crossing_by_name
                                       or target_name in planner._container_door_names))
        if is_door:
            last = segments[-1]["doors"]
            if not any(d["name"] == target_name for d in last):
                last.append({"name": target_name, "distance_m": 0.0})
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


def _ramp_interior_between(planner, prev, node):
    """Ramp interior world points for the directed stair seam prev->node, or [] if this
    pair has no baked ramp polyline. Mirrors the C# planner's stair-seam expansion so the
    offline route (and the parity check) include the same intermediate stair waypoints."""
    if prev[0] == node[0]:
        return []
    for nbr, _cost, meta in planner.edges_from.get(prev, ()):  # type: ignore[attr-defined]
        if nbr == node and meta and meta.get("kind") == "stairs":
            return meta.get("ramp_xyz") or []
    return []


def _ramp_point_to_dict(floor_label, xyz, planner):
    """A ramp interior waypoint dict in the same shape as _node_to_dict, but positioned at
    the ramp's true world XYZ (not a cell center) — the seam has no grid cell."""
    return {
        "floor": floor_label,
        "cell": None,
        "world_xz": [round(xyz[0], 4), round(xyz[2], 4)],
        "wx": round(xyz[0], 4),
        "wz": round(xyz[2], 4),
        "floor_y": round(xyz[1], 4),
    }


def _waypoints_to_dicts(waypoints, planner):
    """Serialize the smoothed cell-waypoint list, inserting ramp interior points at each
    stair seam so the offline route walks the diagonal run (parity with the C# planner)."""
    out = []
    for i, w in enumerate(waypoints):
        if i > 0:
            seam_floor = waypoints[i - 1][0]  # interiors belong to the floor we depart
            for xyz in _ramp_interior_between(planner, waypoints[i - 1], w):
                out.append(_ramp_point_to_dict(seam_floor, xyz, planner))
        out.append(_node_to_dict(w, planner))
    return out


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


def find_roster_fixture(roster, target):
    """Resolve a plan() target against the bake's canonical fixture ROSTER — the single
    source of truth (filtered, identity-deduped, routing-unit merged, best-located). `target`
    matches a fixture by GameObjectId (in its merged object_ids), exact name, exact path, or
    a name/path substring. Returns the roster entry or None.

    This is preferred over find_interactable (the raw asset export) so the single-target plan
    resolves the SAME logical object the sweep and the in-game planner do. The raw export
    substring-matches across lighting-preset phantoms + same-named instances (e.g.
    'light_recessed_1' hits 220 records across 11 distinct XZ), so re-deriving identity there
    re-introduces exactly the ambiguity the roster exists to remove. See load_fixture_roster."""
    # GameObjectId (any of a merged fixture's ids).
    try:
        tid = int(target)
        for fx in roster:
            if tid in (fx.get("object_ids") or []):
                return fx
    except (TypeError, ValueError):
        pass
    t = str(target).lower()
    # Exact name/path first, then substring — deterministic shortest-path tiebreak.
    exact = [fx for fx in roster
             if t == (fx.get("name") or "").lower() or t == (fx.get("path") or "").lower()]
    if exact:
        return exact[0]
    matches = [fx for fx in roster
               if t in (fx.get("name") or "").lower() or t in (fx.get("path") or "").lower()]
    if not matches:
        return None
    matches.sort(key=lambda fx: (len(fx.get("path") or ""), (fx.get("object_ids") or [0])[0]))
    return matches[0]


def find_interactable(items, target):
    """target is either a GameObjectId (int-ish string) or a substring match on path.
    RAW-EXPORT resolver — prefer find_roster_fixture (the canonical bake roster). This is a
    last-resort fallback for objects absent from the roster, and substring-matches across
    phantom/preset duplicates; the caller should warn when it falls back here."""
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


# ---------- interaction line-of-sight goal filter (mirror of SimpleNavPlanner) ----------

_LOS_OCCLUDERS = None       # lazily-built collider set (the whole scene's occluders)
_LOS_BY_PATH = None         # {collider path: Collider} OCCLUDERS only (trigger-free)
_LOS_TARGET_BY_PATH = None  # {collider path: Collider} target resolution (triggers INCLUDED)
_LOS_TARGET_PATHS = None    # {GameObjectName: [export Path, ...]} for name→path lookup
_LOS_CONTAINER_DOOR_PATHS = None  # set of collider paths for baked container doors


def _build_container_door_paths():
    """Collider paths of the bake's container_operable_only doors — the doors the LOS
    rescue may see PAST. Maps each container door NAME from the bake to its collider
    PATH(s) by leaf-name match against the occluder inventory (door names are unique
    leaf names: SM_Kitchen_Cupboard_Door_5_MODEL_UPDATE etc.). Path-based so the rescue
    never sees past a non-container collider. Mirror of C# _containerDoorNames, resolved
    to paths for the offline raycaster. See [[project_navigation_container_doors_baked_2026_06_14]]."""
    bake = load_bake()
    names = set()
    for f in bake.get("floors", []):
        for d in (f.get("doors") or []):
            if d.get("container_operable_only") and d.get("name"):
                names.add(d["name"])
    paths = set()
    if names:
        for path in _LOS_BY_PATH:
            leaf = path.rsplit("/", 1)[-1]
            if leaf in names:
                paths.add(path)
    return paths


def _ensure_los_context():
    """Build the shared occluder set + path indices once. The mask matches the game's
    interaction raycast: ~dateviatorIgnores. We don't have the live mask offline, so we
    use the parity-default (exclude only Unity's built-in IgnoreRaycast layer 2), which
    validate_los proved sufficient — the game's dateviatorIgnores is layer 2 plus a few
    effect layers that carry no interaction-blocking geometry."""
    global _LOS_OCCLUDERS, _LOS_BY_PATH, _LOS_TARGET_PATHS, _LOS_CONTAINER_DOOR_PATHS
    global _LOS_TARGET_BY_PATH
    if _LOS_OCCLUDERS is not None:
        return
    excl = {_los.IGNORE_RAYCAST_LAYER}
    _LOS_OCCLUDERS = _los.load_colliders(excl)
    _LOS_BY_PATH = {}
    for c in _LOS_OCCLUDERS:
        _LOS_BY_PATH.setdefault(c.path, c)
    # Target-resolution index: same layer mask, but triggers INCLUDED — the game's interaction
    # ray hits trigger colliders (queriesHitTriggers=true), so an interactable's own trigger
    # collider (room thresholds, Attic Orb) IS its hittable surface. Occluders above stay
    # trigger-free (a trigger doesn't block another target's sightline). Superset of _LOS_BY_PATH.
    _LOS_TARGET_BY_PATH = {}
    raw = json.loads(_los.BLOCKERS.read_text(encoding="utf-8-sig"))
    for sec in ("PrimitiveColliders", "MeshColliders"):
        for b in raw.get(sec, []):
            if b.get("Layer") in excl:
                continue
            c = _los.build_collider(b, allow_trigger=True)
            if c is not None:
                _LOS_TARGET_BY_PATH.setdefault(c.path, c)
    _LOS_TARGET_PATHS = {}
    for it in load_interactables():
        _LOS_TARGET_PATHS.setdefault(it.get("GameObjectName"), []).append(it.get("Path"))
    _LOS_CONTAINER_DOOR_PATHS = _build_container_door_paths()


def resolve_target_collider_for_path(target_path, target_pos=None):
    """The target's own collider (or None) by full export path — mirror of
    SimpleNavPlanner.ResolveTargetCollider + SelectBestTargetCollider. None ⇒ keep the
    full disc (matches C# when its live component-walk also finds nothing).

    CONTAINMENT FALLBACK (offline only): some interactables' export Path points at a RENDER
    mesh that carries no collider, while the real collider sits on a SIBLING node (same furniture
    unit, e.g. SM_CoffeeMachine_MODEL_UPDATE -> sibling SM_CoffeeMachine_Side_Bag) OR on a
    co-located instance in a different subtree (Rat_Dead's transform sits inside the
    Stuffed_Rat_Mesh_Grp collider, which is NOT a unit-mate). When the strict self/child/parent
    resolve finds nothing AND a target_pos is given, accept ANY target collider whose EXACT
    oriented volume STRICTLY contains the object position (point_inside_collider — never AABB,
    which over-claims; see [[feedback_no_collider_is_missing_data_not_pass]]).

    Strict containment IS the safety gate — it makes the shared-furniture-unit requirement
    redundant and over-restrictive (it excluded Rat_Dead while adding nothing, since containment
    already proves co-location). Verified safe: every distant mis-relate is rejected (a hanger 55m
    from a collider-bearing sibling, a Teddy 38m away, a pumpkin near but not inside a ball all
    resolve to None); only genuine overlaps resolve (Rat_Dead -> Stuffed_Rat). Smallest-volume
    enclosing shape wins (tightest = most specific). The C# planner reaches these via its live
    GetComponentsInParent walk, so this only restores offline parity; it does NOT invent LOS the
    game wouldn't see."""
    _ensure_los_context()
    # Resolve against the TARGET index (triggers included) — the game's ray hits an object's
    # own trigger collider, so it's a valid hittable surface for the interactable itself.
    own = _los.resolve_target_collider(target_path, colliders_by_path=_LOS_TARGET_BY_PATH)
    if own is not None or target_pos is None:
        return own
    px, py, pz = target_pos
    if px is None or py is None or pz is None:
        return None
    best = None  # (volume, collider) — prefer the tightest enclosing shape
    for col_path, c in _LOS_TARGET_BY_PATH.items():
        if not _los.point_inside_collider((px, py, pz), c):
            continue
        if c.kind == "sphere":
            vol = c.radius ** 3
        elif c.kind == "obb":
            vol = c.half[0] * c.half[1] * c.half[2]
        elif c.kind == "capsule":
            import math as _m
            vol = c.radius ** 2 * (_m.dist(c.p0, c.p1) + c.radius)
        else:
            continue  # mesh/aabb have no exact containment — never accepted here
        if best is None or vol < best[0]:
            best = (vol, c)
    return best[1] if best else None


def filter_goals_by_los(floor, goals, target_collider, target_x, target_z, radius_m):
    """Narrow goal cells to those that are valid interaction standpoints, exactly as
    SimpleNavPlanner.Plan does for a non-door collider target:
      (a) drop cells whose XZ distance to the target collider's nearest bounds point is
          below TARGET_COLLIDER_CLEARANCE_M (standing inside the prop), and
      (b) keep only cells with a clear synthetic-eye interaction line (cell_has_los).
    Returns (goals, container_doors, quality): the filtered (ix, iz) list, the set of container
    door PATHS the kept cells' sightlines saw past (to be opened on arrival), and a dict
    {(floor_label, ix, iz): view_quality} used as the goal-pick tiebreak (mirror of the C#
    goalQuality map; higher = more unobstructed view, biases high-mounts to a stand-back diagonal).
    If target_collider is None, returns (goals, set(), {}). If the collider IS resolved but NO
    cell qualifies, returns ([], set(), {}) so the caller fails fast with no_los.

    A door is a door: the sightline SEES PAST operable container doors by default (mirror
    of the C# LOS filter), the same way A* routes through a closed passage door — the
    executor opens it on arrival. Cells whose line passed a container door record it so
    tag_doors / the route can open it."""
    if target_collider is None:
        return goals, set(), {}
    _ensure_los_context()
    inner_sq = TARGET_COLLIDER_CLEARANCE_M * TARGET_COLLIDER_CLEARANCE_M
    out = []
    opened = set()
    quality = {}
    for (ix, iz) in goals:
        wx, wz = floor.cell_to_world(ix, iz)
        # (a) overlap drop: nearest bounds point to a 1m-high standpoint (matches C#'s
        # cellWorld at FloorY+1.0 for the clearance test).
        cell_world = (wx, floor.floor_y + 1.0, wz)
        nearest = _los.closest_point_on_bounds(target_collider, cell_world)
        dx = nearest[0] - cell_world[0]
        dz = nearest[2] - cell_world[2]
        if dx * dx + dz * dz < inner_sq:
            continue
        # (b) interaction LOS, seeing past operable container doors (recorded in `passed`).
        passed = set()
        if _los.cell_has_los_to_target((wx, wz), floor.floor_y, target_collider,
                                       radius_m, _LOS_OCCLUDERS,
                                       container_door_paths=_LOS_CONTAINER_DOOR_PATHS,
                                       passed_container_doors=passed):
            out.append((ix, iz))
            opened |= passed
            quality[(floor.label, ix, iz)] = _los.goal_view_quality(
                (wx, wz), floor.floor_y, target_collider)
    return out, opened, quality


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
    """Resolve one interactable to its interaction stand-cell set, mirroring plan()'s
    target resolution: floor-by-Y, then ALL navigable cells within the (clamped)
    interaction radius, falling back to a single nearest-navigable cell.

    Returns (floor_label, representative_cell, goal_cells) or None when the object sits
    off every navigable floor. representative_cell is the cell nearest the object centre
    (used for deduping + a stable node id); goal_cells is the FULL candidate set so the
    route planner can target whichever one is actually reachable from the start — a
    closer cell may be marooned inside furniture footprint-rasterization while a slightly
    farther cell on the same object is on the reachable floor. Snapping to the single
    nearest cell (the old behaviour) produced ~140 false no_path objects whose real
    stand-spot was 0.7–2.9m away and reachable. goal_cells are (ix, iz) tuples."""
    pos = item.get("Position") or {}
    tx, ty, tz = pos.get("x"), pos.get("y"), pos.get("z")
    if tx is None or ty is None or tz is None:
        return None
    tfloor = planner._floor_for_target_y(ty)
    if tfloor is None:
        # Y falls outside every baked floor band: clearly exterior/off-floor decor.
        return "off_floor"
    floor = planner.floors[tfloor]
    radius = item.get("InteractionRadius") or 0.0
    # Use the object's own InteractionRadius verbatim (no clamp, no default) — parity with C#
    # Plan(): the game gates on radius + LOS, not on any bound we impose. Every real interactable
    # carries a radius; a degenerate 0 yields no goal cells and is handled by the empty-goals path.
    goals = goal_cells_around(floor, tx, tz, radius)
    if not goals:
        n = floor.nearest_navigable(tx, tz, max_radius_m=NEAREST_NAVIGABLE_SEARCH_M)
        if n is None:
            # No navigable cell within the normal search radius. The Y-band test alone does
            # NOT separate exterior decor from gate-blocked interior objects — the ground
            # frame's bbox spans the whole street, so fences/trees 100m out still pass it.
            # The real discriminator is HORIZONTAL distance to the nearest walkable cell:
            # an interior object walled off by a closed gate has floor a couple metres away
            # on the blocked side; a fence across the road has nothing for tens of metres.
            # Probe much wider; classify by that distance.
            far = floor.nearest_navigable(tx, tz, max_radius_m=EXTERIOR_CLASSIFY_RADIUS_M)
            if far is None:
                return "off_floor"   # nothing walkable anywhere near → exterior decor
            fwx, fwz = floor.cell_to_world(*far)
            if math.hypot(fwx - tx, fwz - tz) > GATE_BLOCKED_MAX_DISTANCE_M:
                return "off_floor"   # nearest floor is far → exterior, not a gate
            # Close to walkable floor but unreachable within radius → genuinely gate-blocked
            # / marooned in footprint rasterization. A real candidate navigation problem.
            return "gate_blocked"
        goals = [(n[0], n[1])]
    else:
        # Interaction-LOS goal filter — the SAME one plan() and the in-game planner apply
        # (filter_goals_by_los): resolve the fixture's own collider by its scene Path and keep
        # only stand-cells with a clear synthetic-eye sightline to it. A resolved collider with
        # NO LOS cell anywhere ⇒ reachable-but-not-interactable ⇒ no_los.
        #
        # NO COLLIDER (None) ⇒ we do NOT know where this object's interactable surface is, so we
        # CANNOT test LOS. The old behaviour kept the full disc and let the cell pass UNVERIFIED —
        # a GUESS. ~32% of "pass" verdicts were such guesses (194/610), and the in-game raycast
        # later contradicts an unknown subset (the box7/box10/TrapDoor/Floors_UpperHall no_los).
        # Don't guess: emit no_collider, a distinct "missing data" failure to be fixed in the
        # EXPORTER (Export-SceneBlockerData dropped it, e.g. Layer-2 filtered) — NOT papered over
        # downstream. The in-game reality-check can still UPGRADE a no_collider object to verified
        # when the live component-walk resolves a collider the offline export lacks; offline simply
        # refuses to assert pass/fail on geometry it doesn't have. See
        # [[project_navigation_sweep_2026_06_16_timeout]] + [[feedback_fix_data_source_first]].
        target_collider = resolve_target_collider_for_path(item.get("Path"), target_pos=(tx, ty, tz))
        if target_collider is None:
            return "no_collider"
        los_goals, _container_doors, _quality = filter_goals_by_los(
            floor, goals, target_collider, tx, tz, radius)
        if not los_goals:
            return "no_los"
        goals = los_goals
    # Representative = the navigable cell nearest the object centre (deterministic).
    rep = min(goals, key=lambda c: (floor.cell_to_world(*c)[0] - tx) ** 2 +
                                    (floor.cell_to_world(*c)[1] - tz) ** 2)
    return (tfloor, rep, goals)


def _roster_entry_as_item(entry):
    """Adapt one baked fixture-roster entry to the {Position, InteractionRadius, ...} shape
    resolve_object_node expects. The roster position is ALREADY the best-available-location
    (bounds centre, not rig-origin). Path IS carried (the roster now emits it): it lets
    resolve_object_node resolve the fixture's own collider and run the SAME interaction-LOS goal
    filter the in-game planner and single-target plan() run — so the objects sweep tests
    reachability+LOS, not just routing. (It used to omit Path to skip an unreliable offline
    collider re-resolve; with UniqueId-joined exact bounds that shortcut is obsolete.)"""
    x, y, z = entry["position"]
    return {
        "GameObjectName": entry["name"],
        "Position": {"x": x, "y": y, "z": z},
        "InteractionRadius": entry.get("interaction_radius") or 0.0,
        "GameObjectId": (entry.get("object_ids") or [None])[0],
        "Layer": 0,
        "IsActive": True,
        "IsDatable": entry.get("is_datable", False),
        "InkFileName": entry.get("ink"),
        "Path": entry.get("path"),
        # Stable scene id(s) for the in-game sweep's exact roster->live bridge.
        "UniqueId": entry.get("unique_id"),
        "UniqueIds": entry.get("unique_ids") or [],
    }


def object_sweep_nodes(planner, roster):
    """Build the object-node list for the sweep from the bake's canonical fixture ROSTER.
    Each node is a stand-cell the player would occupy to interact with one or more roster
    fixtures. The roster is ALREADY filtered/identity-deduped/routing-unit-merged by the bake
    (one entry per logical interactable — the 48-books / SM_-duplicate redundancy is gone
    upstream), so this no longer re-runs is_statically_pickable or its own name/position
    derivation; it only resolves each fixture to stand-cells and collapses any that
    COINCIDENTALLY share a stand-cell.

    `roster` is load_fixture_roster() output. Returns a list of dicts:
    {floor, cell:(ix,iz), goal_cells:[(ix,iz),...], names:[...], object_ids:[...],
    representative:item}. `cell` is the representative stand-cell (dedup + node id);
    `goal_cells` is the union of every member's candidate stand-cells, so A* targets the
    whole set and finds whichever cell is reachable. Fixtures that resolve to no navigable
    cell (off-floor or gate-blocked under the current door/state-wall params) are returned
    separately as `unreachable` so the manifest reports them as no_path without a drive."""
    by_cell = {}
    unreachable = []
    for entry in roster:
        item = _roster_entry_as_item(entry)
        node = resolve_object_node(planner, item)
        name = entry["name"]
        object_ids = entry.get("object_ids") or [item.get("GameObjectId")]
        if node is None or isinstance(node, str):
            # node is a string sentinel ("off_floor" / "gate_blocked" / "no_los") describing WHY
            # it resolved nowhere; None is the legacy/no-position case. Carry the reason so the
            # manifest can separate expected-exterior from gate-blocked from not-interactable.
            reason = node if isinstance(node, str) else "unresolved"
            unreachable.append({"name": name, "object_id": object_ids[0],
                                "object_ids": object_ids,
                                "position": item.get("Position"), "reason": reason})
            continue
        floor_label, rep, goals = node
        key = (floor_label, rep)
        slot = by_cell.setdefault(key, {"floor": floor_label, "cell": rep,
                                        "goal_cells": set(), "names": [],
                                        "object_ids": [], "representative": item})
        slot["goal_cells"].update(goals)
        slot["names"].append(name)
        slot["object_ids"].extend(object_ids)
    # Freeze goal_cells to a sorted list for deterministic output.
    result = []
    for slot in by_cell.values():
        slot["goal_cells"] = sorted(slot["goal_cells"])
        result.append(slot)
    return result, unreachable


# ---------- top-level plan() ----------

def plan(target_spec, start_xz=None, start_floor=None, interaction_radius_override=None):
    bake = load_bake()
    planner = Planner(bake)
    # Resolve the target through the bake's canonical fixture ROSTER (single source of truth),
    # the same set the sweep and the in-game planner use. Only fall back to the raw asset export
    # if the roster has no match (and say so loudly) — re-deriving identity from the raw export
    # re-introduces the preset/instance ambiguity the roster removes. See load_fixture_roster.
    roster = load_fixture_roster()
    fixture = find_roster_fixture(roster, target_spec) if roster else None
    if fixture is not None:
        target = _roster_entry_as_item(fixture)
    else:
        target = find_interactable(load_interactables(), target_spec)
        if target is None:
            raise SystemExit(f"no interactable matches {target_spec!r}")
        sys.stderr.write(
            f"WARNING: {target_spec!r} not in the fixture roster; resolved from the RAW asset "
            f"export (may be ambiguous across preset/instance duplicates). Path="
            f"{target.get('Path')!r}\n")

    tx = target["Position"]["x"]; ty = target["Position"]["y"]; tz = target["Position"]["z"]

    # Correct a DEGENERATE target position (mirror of SimpleNavPlanner.Plan). Some interactables
    # (animated cushions, curtains, the broken-glass door, ~30 objects) report a transform at
    # their RIG ORIGIN, tens of metres from the geometry; the COLLIDER carries the true location.
    # When the given position lies OUTSIDE the resolved collider's XZ bounds (+ pad), substitute
    # the collider's bounds centre. See [[project_navigation_target_position_degenerate]].
    target_collider = resolve_target_collider_for_path(target.get("Path"))
    if target_collider is not None:
        pad = 0.5
        lo, hi = target_collider.aabb_lo, target_collider.aabb_hi
        if tx < lo[0] - pad or tx > hi[0] + pad or tz < lo[2] - pad or tz > hi[2] + pad:
            tx = (lo[0] + hi[0]) / 2.0
            ty = (lo[1] + hi[1]) / 2.0
            tz = (lo[2] + hi[2]) / 2.0

    tfloor = planner._floor_for_target_y(ty)
    if tfloor is None:
        raise SystemExit(f"target Y={ty} not on a baked floor")
    radius = interaction_radius_override or target.get("InteractionRadius") or 0.0
    goals = goal_cells_around(planner.floors[tfloor], tx, tz, radius)
    goal_quality = None  # set by the LOS filter below; None ⇒ astar falls back to closest
    if not goals:
        # Fall back to the nearest navigable cell.
        n = planner.floors[tfloor].nearest_navigable(tx, tz, max_radius_m=NEAREST_NAVIGABLE_SEARCH_M)
        if n is None:
            raise SystemExit(f"no navigable cell near target {target['Path']}")
        goals = [n]
    else:
        # Interaction LOS goal filter (mirror of SimpleNavPlanner non-door branch): keep
        # only cells that don't overlap the target collider and have a clear interaction
        # line. None collider ⇒ keep the disc. A resolved collider with NO LOS cell ⇒
        # the object is reachable but not interactable from anywhere → fail fast.
        if target_collider is not None:
            los_goals, container_doors_opened, goal_quality = filter_goals_by_los(
                planner.floors[tfloor], goals, target_collider, tx, tz, radius)
            if not los_goals:
                return {
                    "status": "no_los",
                    "target": _summarize_target(target),
                }
            goals = los_goals
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

    path, total_cost, edges = planner.astar(start_node, goal_nodes, tfloor, tx, tz, goal_quality)
    if path is None:
        return {
            "status": "no_path",
            "target": _summarize_target(target),
            "start": _node_to_dict(start_node, planner),
        }

    waypoints = smooth_path(path, planner)
    segments = tag_doors(waypoints, planner,
                         target_name=target.get("GameObjectName"), target_radius=radius)
    return {
        "status": "ok",
        "target": _summarize_target(target),
        "start": _node_to_dict(start_node, planner),
        "goal_cell_count": len(goals),
        "path_length_cells": len(path),
        "waypoint_count": len(waypoints),
        "total_cost_m": round(total_cost, 3),
        "waypoints": _waypoints_to_dicts(waypoints, planner),
        "segments": segments,
        "edge_kinds_used": sorted({e.get("kind", "walk") for e in edges if e}),
        "params": {
            "cell_size_m": planner.cell_size,
            "interaction_radius_used_m": radius,
            "corner_waypoint_deg": CORNER_WAYPOINT_DEG,
            "door_interact_radius_fallback_m": DOOR_INTERACT_RADIUS_FALLBACK_M,
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
