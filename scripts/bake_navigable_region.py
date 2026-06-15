"""Step O2 of [[project-navigation-object-first-plan]]: rasterize per-floor navigable region.

For each named floor band (ground, upper):
  1. Pick representative floor Y from the walkable export (area-weighted flat/step-up peaks).
  2. Rasterize at 0.2m cells across XZ extent of the floor's walkable footprint.
  3. Cell is walkable iff a walkable surface (VExt <= 1m slab) within the floor band covers it.
  4. Cell is blocked iff the player capsule cannot stand there:
     primitive colliders use their 2D bounds; mesh colliders use exported
     player-height triangle-slice segments when available.
  5. Dilate blocked region by capsule radius (0.4m / 2 cells at 0.2m).
  6. Navigable = walkable AND NOT dilated-blocked.
Emits one bitmap per floor + debug PNG.

Crawlspace floor is missing from the walkable export (no slab at Y≈-9.6); skipped here, follow-up.

Run from repo root:
  python scripts/bake_navigable_region.py

This script also runs the O3 inter-floor derivation post-pass before exiting,
so future bake regenerations do not silently drop stair / teleporter edges.
"""
from __future__ import annotations
import importlib.util
import json, math, re
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
WALK = REPO / "artifacts/navigation/thirdpersongreybox-walkable.json"
# Per-cell vertical-span column export (replaces fixed-Y slice segments as the
# blocking primitive). See [[project-navigation-bake-percell-vertical-span]].
BLOCK = REPO / "artifacts/navigation/thirdpersongreybox-blockers.COLUMNS.json"
INTER = REPO / "artifacts/navigation/thirdpersongreybox-interactables.json"
NAVDATA = REPO / "artifacts/navigation/thirdpersongreybox-navigation-data.json"
OUT_JSON = REPO / "artifacts/navigation/navigable_region.bake.json"
OUT_PNG_DIR = REPO / "artifacts/navigation"

# Player CapsuleCollider radius is 0.4m (Player.prefab; the exported value of
# 0.2 is multiplied by the 2x world scale). The earlier 0.50m setting was a
# safety margin in response to a runtime graze against the fireplace at
# ~0.44m, but it over-sealed narrow corridors — notably the hallway between
# z=5.7 and z=6.9 (1.2m wide), where 0.5m dilation leaves <1 cell of clearance
# and breaks the office→front-door route entirely. 0.4m leaves 0.4m / 2 cells
# of clearance — passable by the actual player capsule. If fireplace-style
# grazes return, address them with per-mesh inflation rather than a global
# bump that closes legitimate doorways.
CAPSULE_R = 0.40
# Live-measured player capsule height (floor .. floor + 3.2m). The old 2.50
# value was a stale under-measurement: it let chest-to-head-height geometry
# (e.g. an open cupboard/fridge door panel at world Y ~2.3-2.7) sit just above
# the band and slip through unblocked. 3.2m matches the export's capsule band
# ([-0.7,2.9] ground = floorY-0.2 .. floorY+3.2) and the live capsule-probe in
# [[project-navigation-capsule-radius-groundtruth-2026-05-29]].
CAPSULE_H = 3.20
STEP_UP_TOL = 0.25
# A column whose top clears the floor by less than this is a sill/threshold/lip
# the capsule's FOOT steps over — the one honest physical rule that survives the
# slice-artifact-gate deletion. Calibrated in
# [[project-navigation-bake-percell-vertical-span]]: SM_Walls_* tops poke ~0.05m
# proud of the upper floor (step over), while SM_Ceiling_Hall pokes 0.45m and
# CeilingStairsFix spans floor-to-ceiling (both correctly keep blocking).
STEP_OVER_HEIGHT_M = 0.30
CELL = 0.20  # rasterization resolution
# XZ cell size of the exporter's per-cell column raster. Columns index cells on
# a fixed global origin (colIX = floor(worldX / COL_CELL)); the bake maps each
# column's cell centre back through world space into its own padded grid. Equal
# to CELL by design (1:1 mapping); validated against the export at load time.
COL_CELL = 0.20
DILATE_CELLS = int(math.ceil(CAPSULE_R / CELL))  # 2 cells
# Surface vertical extent above which we treat the surface as a column/prop
# (not a floor slab). Lets SM_Ceiling_* slabs through while keeping lightbulbs,
# daemons, and plant pots out. Tall props that pass blocker selection re-block
# themselves; this gate only filters the walkable side.
MAX_FLOOR_SLAB_EXTENT = 1.0
# Door-position carve radius (meters). Several wall meshes have asymmetric
# doorway cuts -- the opening is only modeled on one face of the wall, so
# dilation re-seals the opening. Doors are first-class passages in the planner
# model; carve a disc at each Doors_* interactable position to guarantee the
# bake reflects that. 0.4m matches the capsule radius -- minimum needed to let
# the capsule through. Smallest authored doorway clearance is ~1.14m, so a
# 0.8m-diameter disc fits even the narrowest door.
DOOR_CARVE_RADIUS = 0.40
# Wider carve for real Door components exported from scene navigation data. This
# repairs doorway component splits caused by wall/doorframe dilation without
# widening every name-only door-like object.
DOOR_COMPONENT_CARVE_RADIUS = 1.50

# Reach radius for the door-operability standpoint (meters). Door.cs opens via
# InteractableObj.Interact, gated by InteractionRadius (7.5m default). The
# planner already caps door-target approach to ~3.0m so the player stops near
# the door rather than across the room; we use the same cap as the radius of the
# operable-from disc. The runtime still confirms the live gates
# (blockInteraction, collidedWithPlayer, moving) before acting — this is the
# static candidate set of cells the player could stand in to operate the door.
# Single home for the door-approach distance: the C# runtime no longer has its own
# door radius (it routes to these operable_from_cells directly), so there is no
# cross-runtime value to keep in sync.
# See [[project-navigation-door-tag-radius]], [[project-navigation-door-pose-exporter]].
DOOR_OPERABLE_RADIUS_M = 3.0

# Floor bands: (label, target_Y, Y_tolerance_for_walkable_inclusion)
# Tolerance is ± around target_Y for which walkable TopY values count as "on this floor".
#
# crawlspace (Y≈-9.89): a real sub-storey reached by OPERATING THE LADDER in the OfficeCloset
# (a teleport down, NOT a walk-in — confirmed by the user; objects there are not reachable from
# the closet side). Its floor mesh SM_Floor_Crawlspace now exports natively in WalkableSurfaces
# via the exporter's floor-aware MinimumWalkableTopY clip (-12.0); no recovery hack. Band tol is
# tight (0.75) so it can't bleed into ground (band [-1.75,0.75]); the crawlspace floor at -9.89
# gives band [-10.64,-9.14], cleanly disjoint. See project_navigation_fixture_roster_design.
FLOORS = [
    {"label": "crawlspace", "y": -9.89, "y_tol": 0.75},
    {"label": "ground", "y": -0.50, "y_tol": 1.25},
    {"label": "upper",  "y": 12.50, "y_tol": 1.25},
]
# The ground floor plane; floors below this are sub-storeys gated by XZ footprint in
# _fixture_floor (see SUBGROUND_FLOOR_FOOTPRINTS).
FLOORS_GROUND_Y = -0.50

# Passage doors (room-to-room) live under this scene subtree; they are walked
# THROUGH and need freed/threshold cells. Everything else with a Door/SlidingDoor
# component is a CONTAINER door (cupboard/cabinet/fridge/breaker) — opened in
# place to reveal the item inside, never traversed. A container door needs only
# operable_from_cells (where to stand to open it), not freed/threshold passage
# cells. Mirrors classify_container_items.py's PASSAGE_DOOR_MARKER. See
# [[project_navigation_container_open_on_interact]] and
# [[project_navigation_sweep_2026_06_14_lynchpin]].
PASSAGE_DOOR_MARKER = "/MultiRoom/Doors/"


def _is_container_door(door_rec):
    """A door opened in place (cupboard/fridge/breaker), not a traversed passage
    door. True unless the door's scene path is under the passage-door subtree."""
    path = (door_rec.get("Path") or "").replace("===SCENE===/", "")
    return PASSAGE_DOOR_MARKER.strip("/") not in path


# ---------- interactable fixture roster ----------
#
# The bake emits the CANONICAL static interactable set so the planner never has to
# filter or dedupe — it just navigates to roster entries. Set construction (what
# exists as a distinct target, on which floor) is a static fact and belongs here;
# the planner owns only navigation (reachability, routing, live door state). See the
# 2026-06-14 fixture-roster work.
#
# THREE set-construction jobs the bake now owns (moved out of the planner):
#   (1) FILTER  — active, on a real interactable layer, with a human-readable name.
#   (2) DEDUPE  — collapse copies of the SAME object at the SAME place to one target.
#                 The lighting presets (P{V,F}_Lighting_<preset>/...) each carry a
#                 COMPLETE copy of every fixture; LightingScenarios.UpdateLighting
#                 SetActive(false)s all but the current profile, so at runtime only ONE
#                 copy is live. The static export can't see that SetActive, marking all
#                 ~961 light copies IsActive=true. Collapsing by (cleaned-name, position)
#                 reproduces the game's "one fixture" exactly: same object + same place =
#                 one target; different places stay distinct (books on a shelf vs books
#                 across the house). Position-keyed, NOT proximity-clustered, so genuinely
#                 distinct objects that sit near each other are never merged.
#   (3) FLOOR   — assign each fixture to the storey it belongs to (see below).
OBJECT_NODE_LAYERS = (0, 31)
_MODEL_INSTANCE_RE = re.compile(r"\s*\(\d+\)\s*$")
_MODEL_UPDATE_RE = re.compile(r"_MODEL_UPDATE\d*", re.IGNORECASE)
_MODEL_PREFIX_RE = re.compile(r"^(?:SM|SK)_+", re.IGNORECASE)
# Fixtures at the SAME world position to this resolution are the same logical object
# (preset copies sit at IDENTICAL coords; this only collapses true co-location, never
# nearby-but-distinct props). 0.05m = well below the smallest inter-object spacing.
FIXTURE_DEDUP_QUANT_M = 0.05

# EXTERIOR / TEST FILTER (set-construction rule, not navigation): the scene graph's
# top-level subtrees ARE the game's authoritative interior/exterior boundary. Everything
# under Exterior/ (Bush, Tree, Fence, UtilityPole, Drone, neighbour-house shells) is pure
# visual decor — leaving the house triggers the ending cutscene, so they're never
# reachable navigation targets, but a Y-band floor filter passes them (the ground bbox
# spans the whole street). TESTING_TEMP/ and Main Camera are dev artifacts. This is a
# DENYLIST, NOT a House-only allowlist: every dateable light lives under a top-level
# P{V,F}_Lighting_* subtree (outside House/), so a House-only filter would delete them all.
# Crawlspace items (TimeCapsule, CrawlspaceLadder, RatTrap, SkeletonKey) live UNDER House/
# and are reachable — they survive this filter. See project_navigation_fixture_roster_design.
FIXTURE_SUBTREE_DENYLIST = ("Exterior", "TESTING_TEMP", "Main Camera")

# ROUTING-UNIT MERGE scaffolding tokens: authoring wrapper nodes the path walks UP past to
# reach the first REAL unit parent. A target groups same-stem NUMBERED siblings under that
# real parent. See rule 3 in project_navigation_fixture_roster_design.
_SCAFFOLD_NODE_RE = re.compile(
    r"(_TRS|_MASTER|_Grp|_GROUP|_MODEL_UPDATE\d*|_ORIGIN|_MODEL)$", re.IGNORECASE)
# Stem = a cleaned name minus any trailing instance index / digits, so Knife1..Knife6 and
# Book_MESSY_01..48 collapse to one stem ("Knife", "Book_MESSY") while distinct NAMES
# (Monitor / Keyboard / Mouse) keep their own stems and stay distinct targets.
_STEM_TRAILING_RE = re.compile(r"[\s_]*\d+$")
# Two units' bounds belong to the SAME contiguous routing unit when their AABBs are within
# this gap (or overlap). The test is "could the player walk between these members?" — so the
# gap is ~one capsule diameter. At 1.0m a shelf of individually-bounded books (sub-metre
# adjacent gaps) stays ONE target (Book_MESSY ×48 -> 1), while a multi-wall parent whose
# members sit in different rooms splits (Shelves_Office: two walls ~16m apart -> 2). A
# tighter 0.3m over-split the books (11 phantom shelf targets); wider than ~1m starts merging
# genuinely separate units. See project_navigation_fixture_roster_design.
FIXTURE_BOUNDS_MERGE_GAP_M = 1.0


def _clean_object_name(raw):
    """Cleaned display name, mirror of plan_object_route.strip_model_authoring_tokens /
    object_display_name. Returns None when nothing human-readable remains (the planner's
    is_statically_pickable rejects those)."""
    if not raw or not raw.strip():
        return None
    s = _MODEL_INSTANCE_RE.sub("", raw)
    s = _MODEL_UPDATE_RE.sub("", s)
    s = _MODEL_PREFIX_RE.sub("", s)
    s = s.strip().strip("_").strip()
    cleaned = raw if not s.strip() else s
    return cleaned if cleaned and cleaned.strip() else None


# XZ footprints of below-ground floors (label -> (minX, maxX, minZ, maxZ)), populated from the
# recovered floor slabs before the roster is built. A sub-ground floor (the crawlspace) is gated
# by its footprint so a degenerate rig-origin fixture (SkeletonKey_0524 at y=-2.14, x=-31 — far
# outside the crawlspace) whose Y happens to fall in the inter-floor dead-zone is NOT wrongly
# pulled down into it. Floors NOT in this dict (ground, upper) are Y-only as before.
SUBGROUND_FLOOR_FOOTPRINTS = {}


def _fixture_floor(y, x=None, z=None):
    """The storey a fixture belongs to. Generally the HIGHEST floor whose plane is at or below
    the fixture: a ceiling light hangs ~12m above the ground floor — its Y lands in the upper
    band, but it belongs to the GROUND room it lights, so we attribute to the storey BELOW it.
    (Inverse of the container-door rule, which uses the band containing the anchor.)

    For a below-ground sub-storey listed in SUBGROUND_FLOOR_FOOTPRINTS (the crawlspace), the
    fixture's XZ must ALSO fall within that floor's footprint — otherwise a degenerate
    rig-origin fixture sitting in the inter-floor dead-zone (e.g. SkeletonKey_0524 at y=-2.14,
    far from the crawlspace XZ) would be Y-only-claimed by it. The ceiling-light rule is
    unaffected: a light is ABOVE its floor and within that room's XZ anyway. Returns a floor
    label or None (off every storey)."""
    floors_by_y = sorted(FLOORS, key=lambda f: f["y"])
    owner = None
    for i, f in enumerate(floors_by_y):
        nxt = floors_by_y[i + 1]["y"] if i + 1 < len(floors_by_y) else float("inf")
        # span = [floor_plane - tol, next_floor_plane). Per-floor lower tol so a fixture just
        # UNDER its own plane (a ground door at y=-0.62 under ground's -0.5) attributes to that
        # floor, not the storey below. Upper bound stays the next plane (no tol) so a floor never
        # claims a fixture at/above the next plane (keeps the ceiling-light "below" attribution).
        low = f["y"] - f.get("y_tol", 1.25)
        if not (low <= y < nxt):
            continue
        fp = SUBGROUND_FLOOR_FOOTPRINTS.get(f["label"])
        if fp is not None:
            if x is None or z is None:
                continue
            minx, maxx, minz, maxz = fp
            # Small pad so a fixture right at the wall line still counts as inside.
            if not (minx - 1.0 <= x <= maxx + 1.0 and minz - 1.0 <= z <= maxz + 1.0):
                continue
        owner = f["label"]
    return owner


def _path_segments(path):
    return [s for s in (path or "").split("/") if s and s != "===SCENE==="]


def _subtree_root(path):
    """The top-level scene subtree a node lives in (first segment after ===SCENE===).
    This is the game's authoritative interior/exterior boundary (Exterior/ vs House/ vs
    P*_Lighting_*)."""
    segs = _path_segments(path)
    return segs[0] if segs else ""


def _best_location(it):
    """The fixture's TRUE world location. Prefer the real Bounds3D center; fall back to
    Position only when bounds are empty/missing. ~106 objects report a rig-origin Position
    (0,0 or far outside their own mesh) but carry a correct collider bounds center; reading
    Position for those creates a phantom (0,0) cluster that splits real units and maps a
    bogus target at world origin. A few (e.g. Book_MESSY_45) have a valid Position but EMPTY
    bounds — those correctly fall through to Position. Returns (x, y, z) or None."""
    bnd = it.get("Bounds3D") or {}
    size = bnd.get("Size") or {}
    center = bnd.get("Center") or {}
    has_real_bounds = (
        center.get("x") is not None
        and (abs(size.get("x", 0.0)) + abs(size.get("y", 0.0)) + abs(size.get("z", 0.0))) > 1e-6
    )
    if has_real_bounds:
        return (center["x"], center["y"], center["z"])
    pos = it.get("Position") or it.get("WorldPosition") or {}
    if pos.get("x") is None or pos.get("y") is None or pos.get("z") is None:
        return None
    return (pos["x"], pos["y"], pos["z"])


def _has_real_bounds(it):
    bnd = it.get("Bounds3D") or {}
    size = bnd.get("Size") or {}
    return (abs(size.get("x", 0.0)) + abs(size.get("y", 0.0)) + abs(size.get("z", 0.0))) > 1e-6


def _real_parent_unit(path):
    """Walk UP the transform path past authoring scaffolding (_TRS / _MASTER / _Grp /
    _MODEL_UPDATE* / _ORIGIN) and self-named wrappers to the first REAL unit node — the
    parent that groups same-stem numbered siblings as one logical object (Drawers_Kitchen
    over Knife1..6). Returns the parent path string (everything above the leaf, minus
    scaffolding), used only as a grouping key."""
    segs = _path_segments(path)
    if len(segs) <= 1:
        return path or ""
    parent = segs[:-1]
    # Strip trailing scaffolding wrapper nodes so cutlery under
    # Drawers_Kitchen/_TRS/_MODEL_UPDATE group by the real Drawers_Kitchen unit.
    while len(parent) > 1 and _SCAFFOLD_NODE_RE.search(parent[-1]):
        parent = parent[:-1]
    return "/".join(parent)


def _name_stem(name):
    """A cleaned name minus its trailing instance index, so numbered siblings share a stem
    ('Knife1'..'Knife6' -> 'Knife', 'Book_MESSY_01' -> 'Book_MESSY') while distinct names
    keep their own ('Monitor', 'Keyboard'). Discriminator (user): numbers ⇒ same object,
    distinct names ⇒ distinct objects."""
    stem = _STEM_TRAILING_RE.sub("", name).strip().strip("_").strip()
    return stem or name


def _split_group_into_units(members):
    """HYBRID geometric split within a same-(parent, stem) group. Two regimes:
      (a) any member has real Bounds3D -> split into contiguous units by BOUNDS-OVERLAP
          (gap <= FIXTURE_BOUNDS_MERGE_GAP_M). Keeps a 2-wall parent (Shelves_Office, walls
          20m apart) as two targets, merges a dense furniture row into one.
      (b) ALL members point-only (empty bounds) -> keep the whole group as ONE unit. Trust
          the hierarchy, no distance knob: the only point-only groups that span far are the
          already-collapsed phantom lights, exterior decor (filtered), and ~2 benign interior
          cases (ribbon row = shelf width; beauty supplies = counter midpoint, still on the
          counter). Merging point-only never corrupts the bake the way a bounds merge would.
    `members` is a list of dicts each with 'loc' (x,y,z) and 'bounds' (Min/Max dict or None).
    Returns a list of unit-member-lists."""
    if not any(m["bounds"] for m in members):
        return [members]

    # Union-find over members by bounds-AABB overlap (inflated by the merge gap). Members
    # with no real bounds attach to the nearest bounded member's unit so they're not lost.
    gap = FIXTURE_BOUNDS_MERGE_GAP_M
    bounded = [m for m in members if m["bounds"]]
    point_only = [m for m in members if not m["bounds"]]

    parent = list(range(len(bounded)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def union(a, b):
        parent[find(a)] = find(b)

    def aabbs_touch(b1, b2):
        for ax in ("x", "y", "z"):
            lo1, hi1 = b1["Min"][ax], b1["Max"][ax]
            lo2, hi2 = b2["Min"][ax], b2["Max"][ax]
            if hi1 + gap < lo2 or hi2 + gap < lo1:
                return False
        return True

    for i in range(len(bounded)):
        for j in range(i + 1, len(bounded)):
            if aabbs_touch(bounded[i]["bounds"], bounded[j]["bounds"]):
                union(i, j)

    units = {}
    for i, m in enumerate(bounded):
        units.setdefault(find(i), []).append(m)

    unit_list = list(units.values())
    # Attach each point-only member to the geometrically nearest unit (by unit centroid).
    for m in point_only:
        if not unit_list:
            unit_list.append([m])
            continue
        mx, my, mz = m["loc"]
        best = None
        best_d = None
        for u in unit_list:
            cx = sum(p["loc"][0] for p in u) / len(u)
            cy = sum(p["loc"][1] for p in u) / len(u)
            cz = sum(p["loc"][2] for p in u) / len(u)
            d = (cx - mx) ** 2 + (cy - my) ** 2 + (cz - mz) ** 2
            if best_d is None or d < best_d:
                best_d = d
                best = u
        best.append(m)
    return unit_list


def build_fixture_roster(interactables):
    """The canonical static interactable target set: filtered, deduped, routing-unit-merged,
    floor-assigned. One entry per distinct physical interactable the planner should consider.
    Returns a list of dicts {name, position:[x,y,z], floor, object_ids:[...],
    interaction_radius, is_datable, ink}. Objects off every storey get floor=None (the
    planner tags them off_floor without trying to route).

    Pipeline (set construction owned by the bake, NOT the planner):
      1. FILTER          — active, on an interactable layer, human-readable name, NOT in the
                           Exterior/TESTING_TEMP/Main Camera subtree denylist.
      2. IDENTITY DEDUP  — collapse copies of the SAME (name, place) — the phantom lighting
                           presets, each of which carries a full copy of every fixture.
      3. ROUTING-UNIT    — group same-(real-parent, name-stem) NUMBERED siblings, then a
         MERGE             hybrid geometric split (bounds-overlap for bounded units, keep
                           point-only groups whole).
      4. FLOOR           — assign to the storey BELOW the fixture (ceiling lights belong to
                           the room they light, not the band their Y lands in).
    Location for every stage uses _best_location (bounds center, else Position)."""
    # ---- 1. FILTER + 2. IDENTITY DEDUP -----------------------------------------------
    by_fixture = {}
    q = FIXTURE_DEDUP_QUANT_M
    for it in interactables:
        if not it.get("IsActive"):
            continue
        if it.get("Layer") not in OBJECT_NODE_LAYERS:
            continue
        if _subtree_root(it.get("Path")) in FIXTURE_SUBTREE_DENYLIST:
            continue
        name = _clean_object_name(it.get("GameObjectName") or it.get("Name"))
        if name is None:
            continue
        loc = _best_location(it)
        if loc is None:
            continue
        x, y, z = loc
        # DEDUPE key: same cleaned name at the same place = one object (the game's
        # preset-SetActive collapse). Position-keyed, so distinct nearby objects stay
        # separate.
        key = (name, round(x / q), round(y / q), round(z / q))
        slot = by_fixture.get(key)
        if slot is None:
            by_fixture[key] = {
                "name": name,
                "loc": (x, y, z),
                "object_ids": [it.get("GameObjectId")],
                "interaction_radius": it.get("InteractionRadius") or 0.0,
                "is_datable": bool(it.get("IsDatable")),
                "ink": it.get("InkFileName"),
                "unique_id": it.get("UniqueId"),
                "path": it.get("Path") or "",
                "bounds": (it.get("Bounds3D") or {}) if _has_real_bounds(it) else None,
            }
        else:
            slot["object_ids"].append(it.get("GameObjectId"))
            # Keep the richest record: prefer a real-bounds member's bounds/location so a
            # mis-located preset copy can't override the true placement.
            if slot["bounds"] is None and _has_real_bounds(it):
                slot["bounds"] = it.get("Bounds3D")
                slot["loc"] = (x, y, z)

    # De-dup ids within each deduped fixture (the export sometimes repeats one GameObjectId
    # at the same position — e.g. Ceiling_Shadow_* listed 3×).
    for slot in by_fixture.values():
        seen = set()
        slot["object_ids"] = [i for i in slot["object_ids"] if not (i in seen or seen.add(i))]

    # ---- 3. ROUTING-UNIT MERGE -------------------------------------------------------
    # Group by (real-parent-unit, name-stem): numbered siblings under one real parent are
    # one logical object; distinct names stay distinct.
    groups = {}
    for slot in by_fixture.values():
        gkey = (_real_parent_unit(slot["path"]), _name_stem(slot["name"]))
        groups.setdefault(gkey, []).append(slot)

    roster = []
    for (_, stem), members in groups.items():
        for unit in _split_group_into_units(members):
            ids = []
            for m in unit:
                ids.extend(m["object_ids"])
            seen = set()
            ids = [i for i in ids if not (i in seen or seen.add(i))]
            # Unit location: centroid of member locations (bounded members dominate via
            # their bounds-center loc). Display name = the shared stem when the unit merged
            # >1 distinct member, else the single member's own cleaned name.
            cx = sum(m["loc"][0] for m in unit) / len(unit)
            cy = sum(m["loc"][1] for m in unit) / len(unit)
            cz = sum(m["loc"][2] for m in unit) / len(unit)
            name = unit[0]["name"] if len(unit) == 1 else stem
            # Keep the most informative datable/ink/radius among members (a datable member
            # defines the unit's identity for the picker; nav only needs one).
            datable_member = next((m for m in unit if m["is_datable"]), unit[0])
            # Stable id(s) for the roster->live bridge. `unique_id` is the unit's primary id
            # (the datable member's, matching ink/identity); `unique_ids` is every member's id
            # so a routing-unit-merged unit (48 books -> 1) matches a live object that is ANY
            # of its members. Dedup, drop blanks.
            unit_uids = []
            useen = set()
            for m in unit:
                u = m.get("unique_id")
                if u and u not in useen:
                    useen.add(u)
                    unit_uids.append(u)
            roster.append({
                "name": name,
                "position": [round(cx, 4), round(cy, 4), round(cz, 4)],
                "floor": _fixture_floor(cy, cx, cz),
                "object_ids": ids,
                "interaction_radius": max(m["interaction_radius"] for m in unit),
                "is_datable": any(m["is_datable"] for m in unit),
                "ink": datable_member["ink"],
                "unique_id": datable_member.get("unique_id"),
                "unique_ids": unit_uids,
                # Scene path of the unit's identity-defining member, so the offline planner can
                # resolve this fixture's own collider and run the SAME interaction-LOS goal filter
                # the in-game planner does (resolve_target_collider_for_path). Without it the
                # objects sweep skipped LOS entirely and under-tested reachability.
                "path": datable_member.get("path"),
            })
    return sorted(roster, key=lambda e: (e["floor"] or "~", e["name"], e["position"]))


# Scene bounds clip — restrict the bake to the HOUSE + CRAWLSPACE region.
#
# The exterior is unreachable (leaving the house triggers the ending cutscene) and its
# interactables are already filtered out of the roster (Exterior subtree denylist). So
# exterior navigable cells are pure noise: the planner never routes to them, but they
# bloated the ground grid to 1175x559 (235m x 112m, 656k cells) for a house that fits in
# ~63m x 80m. The old symmetric +/-200m clip couldn't bound it tightly (the house isn't
# centred at origin or square), so the grid carried ~3x excess empty cells + a ring of
# stranded exterior-perimeter "navigable" strips. This rectangle is derived from the House
# subtree's own geometry (walkable surfaces + blockers, all floors: X[-34.0,28.3]
# Z[-35.4,44.9]) plus a margin for dilation; the crawlspace (X[-5.6,8.4] Z[17.5,32.7]) sits
# well inside it. Harmless tightening — only removes cells the planner never used.
SCENE_MIN_X = -36.0
SCENE_MAX_X = 30.0
SCENE_MIN_Z = -37.0
SCENE_MAX_Z = 47.0


def in_scene(x, z):
    return (SCENE_MIN_X <= x <= SCENE_MAX_X) and (SCENE_MIN_Z <= z <= SCENE_MAX_Z)


def _column_blocks_floor(col_min_y, col_max_y, floor_y):
    """The one physical question the bake answers per cell: does a collider whose
    vertical span over this cell is [col_min_y, col_max_y] intersect the player
    capsule's volume when the player stands on the surface at height `floor_y`?

    `floor_y` is the REAL walkable surface height at this cell (floor_y_bm), not
    the nominal band Y — otherwise the floor slab the player stands on (its top
    is ~0.34m above the round band Y) would block itself.

    True iff [col_min_y, col_max_y] overlaps the capsule extent
    [floor_y, floor_y+CAPSULE_H] AND the column top is not a step-over sill
    (clears the surface by < STEP_OVER). No slice planes, no top-lip / borrow /
    void-plug gates — those were all compensation for the fixed-Y slicing this
    column raster replaces.
    """
    cap_lo = floor_y
    cap_hi = floor_y + CAPSULE_H
    # No vertical overlap with the capsule -> can't block the player here.
    if col_max_y < cap_lo or col_min_y > cap_hi:
        return False
    # Step-over: a sill/lip/threshold whose whole top sits below knee height is
    # walked over by the capsule foot. Measured against the surface the player
    # actually stands on (so the floor mesh itself, top == floor_y, is a 0m lip
    # = walkable, never a self-block).
    if col_max_y - floor_y < STEP_OVER_HEIGHT_M:
        return False
    return True


def _rasterize_columns_into(blocked_bm, columns, floor_y_bm, band_floor_y,
                            minx, minz, nx, nz, cell, col_cell):
    """Mark every bake cell whose column interval blocks the player standing on
    that cell's real floor surface.

    `columns` is the exporter's flat per-cell list [colIX, colIZ, minY, maxY]
    where colIX = floor(worldX / col_cell) on a FIXED global origin (0,0). The
    bake grid has its own padded per-floor origin (minx, minz), so map each
    column's cell CENTER back through world space into the bake grid. col_cell
    matches CELL (0.2) so this is a 1:1 index shift, but going through world
    coordinates keeps it correct regardless.

    The blocking decision is per-cell because the capsule floor is per-cell:
    floor_y_bm[ix][iz] (the walkable surface there, default band_floor_y where no
    surface exists). Returns the number of cells newly marked blocked.
    """
    marked = 0
    for col in columns:
        cmin = col[2]
        cmax = col[3]
        wx = (col[0] + 0.5) * col_cell
        wz = (col[1] + 0.5) * col_cell
        if not in_scene(wx, wz):
            continue
        ix = int(math.floor((wx - minx) / cell))
        iz = int(math.floor((wz - minz) / cell))
        if ix < 0 or ix >= nx or iz < 0 or iz >= nz:
            continue
        floor_y = floor_y_bm[ix][iz] if floor_y_bm is not None else band_floor_y
        if not _column_blocks_floor(cmin, cmax, floor_y):
            continue
        if not blocked_bm[ix][iz]:
            marked += 1
        blocked_bm[ix][iz] = True
    return marked


def _rasterize_bounds(blocked_bm, bb, minx, minz, nx, nz, cell):
    ix0 = max(0, int(math.floor((bb["MinX"] - minx) / cell)))
    ix1 = min(nx, int(math.ceil((bb["MaxX"] - minx) / cell)))
    iz0 = max(0, int(math.floor((bb["MinZ"] - minz) / cell)))
    iz1 = min(nz, int(math.ceil((bb["MaxZ"] - minz) / cell)))
    if ix0 >= ix1 or iz0 >= iz1:
        return False
    for ix in range(ix0, ix1):
        row = blocked_bm[ix]
        for iz in range(iz0, iz1):
            row[iz] = True
    return True


def _is_structural_mesh(record):
    text = f"{record.get('Name', '')} {record.get('Path', '')}".lower()
    structural_markers = (
        "/walls/",
        "/wall/",
        "/doors/",
        "sm_walls",
        "sm_wall",
        "sm_doorframe",
        "doorframe",
        "fence",
        "exterior",
        # The /House/Hallway/Stairs mesh: its ground-band (Y=0.5) slice segments
        # are the bottom-landing side walls, which must rasterize as wall traces
        # only. Routing it through the furniture path (convex-hull perimeter +
        # closed-region fill) would over-block, since the hull spans the full
        # 21m stair run. See [[project-navigation-stairs-runtime-collision]].
        "/hallway/stairs",
    )
    return any(marker in text for marker in structural_markers)


# Open-archway carve. Doorframe meshes (SM_Doorframe_*, Door_frame_*) are thin
# walls with an opening, rasterized from their per-cell columns like any other
# mesh. But the frame's footprint is a CLOSED loop — its threshold/sill
# and lintel cross-pieces span the opening width and
# seal the doorway line at floor level, so a narrow archway (e.g.
# SM_Doorframe_Small_13, 1.23m throat) gets walled off, isolating whole rooms.
#
# Real doors are repaired by the door-position carve (Doors_* interactables) or
# the per-door freed-cells state machine (panel-based door_records). But ~18
# frames in this scene are open archways with NO associated door, so nothing
# opens them. For those, carve the frame's footprint bbox clear of dilation
# (bounded to the bbox + a small margin so it can't leak past the jambs) in the
# door-carve pass below. See [[project-navigation-upper-hall2-archway-seal]],
# [[project-navigation-bake-doorframe-gap-outcome]].
def _is_doorframe(record):
    text = f"{record.get('Name', '')} {record.get('GameObjectName', '')} {record.get('Path', '')}".lower()
    return "doorframe" in text or "door_frame" in text


def _is_solid_blocker(record):
    if record.get("IsTrigger"):
        return False
    if record.get("IsDoorConnector") or record.get("IsTeleporterConnector"):
        return False
    if record.get("Enabled") is False:
        return False
    if record.get("IsActive") is False:
        return False
    # NOTE: the former MovingDateable path-skip and the wiggle 2x bounds-
    # inflation gate are RETIRED. Those compensated for animated dialog-rig
    # colliders whose world AABB sprawled across all animation poses. That whole
    # failure mode is now handled at the SOURCE: (1) the exporter drops any
    # MeshCollider sharing a GameObject with a SkinnedMeshRenderer
    # (SkinnedMeshRigCollider) — the animated rig body, e.g. Monitor and the
    # cars — so it never reaches the blocker set; and (2) the bake no longer
    # rasterizes any mesh-collider AABB, only its real collision-surface slice
    # segments, so a pose-union bounding box can't seal a room. The wiggle props
    # that had no collider never produced a blocker in the first place. See
    # [[project-navigation-model-update-meshes-2026-05-29]],
    # [[project-navigation-capsule-radius-groundtruth-2026-05-29]].
    # The former /ceilings/ void-plug gate, the top-lip gate, MIN_BORROW_HEIGHT_M
    # and the _is_vertical_wall shape test all lived here and just above. Every
    # one of them compensated for the fixed-Y slice export: a tall ground wall
    # sliced at 12.5 looked like an upper-floor wall, and a void-plug box's slice
    # silhouette sprawled across the landing. The per-cell column raster paints
    # each collider only where its real triangles sit, so those phantoms never
    # appear and no gate is needed. The step-over rule (_column_blocks_floor)
    # carries the one genuine physical case (a wall top poking ~5cm proud of the
    # floor above). See [[project-navigation-bake-percell-vertical-span]].
    return True


def _dilate_disc(bm, nx, nz, d):
    if d <= 0:
        return [row[:] for row in bm]
    out = [[False] * nz for _ in range(nx)]
    offsets = [(dx, dz) for dx in range(-d, d+1) for dz in range(-d, d+1)
               if dx*dx + dz*dz <= d*d]
    for ix in range(nx):
        for iz in range(nz):
            if not bm[ix][iz]:
                continue
            for dx, dz in offsets:
                jx = ix + dx
                if jx < 0 or jx >= nx: continue
                jz = iz + dz
                if jz < 0 or jz >= nz: continue
                out[jx][jz] = True
    return out


def _door_operable_cells(navigable_bm, panel_closed_dil, panel_open_dil,
                         anchor_x, anchor_z, minx, minz, nx, nz):
    """Cells where the player can stand to open/close a door, derived offline
    from the Door.cs rule. A cell qualifies when it is:
      (1) NAVIGABLE — the player can actually stand on it;
      (2) within DOOR_OPERABLE_RADIUS_M of the door anchor (the
          InteractionRadius reach, capped to the planner's door-approach cap);
      (3) NOT within a capsule radius of the CLOSED panel — Door.OpenDoor is
          gated on !collidedWithPlayer, so a cell touching the panel can't
          trigger it (panel_closed_dil is the panel rasterized + dilated by R);
      (4) NOT within a capsule radius of the OPEN panel sweep — standing where
          the leaf swings would abort the open (stopOnCollision) or trap the
          player. For sliding doors the "open" set is the slid-to position,
          which the player likewise must not occupy.
    Returns a sorted list of [ix, iz]. The runtime still re-checks the live
    gates (blockInteraction, collidedWithPlayer, moving) before acting; this is
    the static candidate set. See [[project-navigation-door-handling-rules]]."""
    if anchor_x is None or anchor_z is None:
        return []
    cx = int((anchor_x - minx) / CELL)
    cz = int((anchor_z - minz) / CELL)
    cr = int(math.ceil(DOOR_OPERABLE_RADIUS_M / CELL))
    cr2 = cr * cr
    out = []
    for ix in range(max(0, cx - cr), min(nx, cx + cr + 1)):
        for iz in range(max(0, cz - cr), min(nz, cz + cr + 1)):
            if (ix - cx) ** 2 + (iz - cz) ** 2 > cr2:
                continue
            if not navigable_bm[ix][iz]:
                continue
            if panel_closed_dil[ix][iz]:
                continue
            if panel_open_dil[ix][iz]:
                continue
            out.append([ix, iz])
    return out


def _container_operable_record(door_rec, navigable_bm, minx, minz, nx, nz,
                               floor_y, storey_ceiling_y):
    """Build an OPERABILITY-ONLY door record for a container door whose panel mesh
    contributes no in-band / no freed cells (upper wall cupboard above the capsule
    band, or a render-only panel with no collider). The player opens it in place
    from the floor, so it needs only operable_from_cells (where to stand), derived
    from the door anchor XZ against this floor's navigable cells — no freed or
    threshold passage cells.

    Floor disambiguation is by anchor Y: the door belongs to the STOREY it sits in
    ([floor_y - 1.0, storey_ceiling_y)), not every floor whose XZ happens to lie
    beneath it. Without this a ground-floor kitchen cupboard (pivot ~7m up) would
    also emit on the upper floor that overlaps its XZ. A cupboard pivot is well
    above its own floor but below the next storey's floor, which this bracket
    captures.

    Returns None when the anchor is off this storey, or when no navigable cell is
    within reach on this floor. Empty panel masks make _door_operable_cells fall
    back to the whole in-reach navigable disc, exactly right for a high cupboard."""
    door_pos = door_rec.get("WorldPosition") or {}
    anchor_x = door_pos.get("x")
    anchor_z = door_pos.get("z")
    anchor_y = door_pos.get("y")
    if anchor_x is None or anchor_z is None:
        return None
    # Anchor must sit in this storey's vertical span (allow 1m below the floor
    # plane for low cabinet pivots; cap at the next storey's floor).
    if anchor_y is not None and not (floor_y - 1.0 <= anchor_y < storey_ceiling_y):
        return None
    empty_mask = [[False] * nz for _ in range(nx)]
    operable = _door_operable_cells(
        navigable_bm, empty_mask, empty_mask, anchor_x, anchor_z, minx, minz, nx, nz)
    if not operable:
        return None
    # Opening center = the door's OWN anchor XZ. A container has no walk-through
    # threshold, so its "opening" — the point the destination tag rule and the
    # executor aim at to open it — is just where the door is. With this, a container
    # door is tagged by the SAME tag_doors destination rule as a passage door (goal
    # cell within InteractionRadius of the opening), so it needs no special LOS
    # rescue: a door is a door. opening_radius is the door's own footprint, kept
    # small so the on-path rule never tags it (you never thread a container doorway).
    # See [[project_navigation_container_open_on_interact]].
    return {
        "name": door_rec.get("Name"),
        "kind": door_rec.get("Kind"),
        "component_id": door_rec.get("ComponentId"),
        "panel_count": len(door_rec.get("Panels", [])),
        "closed_cells": 0,
        "open_cells": 0,
        "threshold_cells": 0,
        "threshold_cells_list": [],
        "freed_cells": [],
        "freed_count": 0,
        "panel_dilated_cells": [],
        "operable_from_cells": operable,
        "operable_from_count": len(operable),
        "opening_center": [float(anchor_x), float(door_pos.get("y", floor_y)), float(anchor_z)],
        "opening_radius": float(CELL),
        "default_open": bool(door_rec.get("Open", False)),
        "locked": bool(door_rec.get("Locked", False)),
        "container_operable_only": True,
    }


def bake_floor(floor, walkables, blockers, mesh_colliders, doors, door_records, state_walls):
    fy = floor["y"]
    ytol = floor["y_tol"]
    # Top of this storey's vertical span = the next floor's Y, or open-ended for the
    # top floor. Used to assign container doors (whose pivot sits high above their
    # own floor) to the storey they belong to rather than every overlapping floor.
    storey_ceiling_y = floor.get("storey_ceiling_y", float("inf"))
    floor_walks = [
        w for w in walkables
        if in_scene(w["Footprint"]["CenterX"], w["Footprint"]["CenterZ"])
        and abs(w["TopY"] - fy) <= ytol
        and w["VerticalExtent"] <= MAX_FLOOR_SLAB_EXTENT
    ]
    if not floor_walks:
        return {"error": "no walkable surfaces", "floor": floor}

    # Floor XZ extents from walkable footprints
    minx = min(w["Footprint"]["MinX"] for w in floor_walks)
    maxx = max(w["Footprint"]["MaxX"] for w in floor_walks)
    minz = min(w["Footprint"]["MinZ"] for w in floor_walks)
    maxz = max(w["Footprint"]["MaxZ"] for w in floor_walks)
    # Pad by capsule radius so the grid covers dilated regions too
    pad = CAPSULE_R + CELL
    minx -= pad; maxx += pad; minz -= pad; maxz += pad

    nx = int(math.ceil((maxx - minx) / CELL))
    nz = int(math.ceil((maxz - minz) / CELL))

    def cell_center(ix, iz):
        return (minx + (ix + 0.5) * CELL, minz + (iz + 0.5) * CELL)

    # Walkable rasterization with vaulted-ceiling gate.
    #
    # Some rooms in this scene are vaulted (Kitchen, LaundryRoom) — they have
    # no real upper floor above them, just a thin `SM_Ceiling_*` slab at
    # ~12.37m as a visual ceiling cap. Other rooms have a real upper-floor
    # walkable surface in the same band (often named `SM_Ceiling_<RoomBelow>`
    # too — e.g. SM_Ceiling_Hall is the upstairs hall floor, doubling as the
    # downstairs hall ceiling). Discriminator: real walkable surfaces sit
    # near the floor-band's target Y (within VAULTED_GATE_M of fy); pure
    # visual ceilings sit noticeably lower (~0.1-0.2m below fy).
    #
    # This filter only triggers above STEP_UP_TOL below fy — anything closer
    # than that to fy is admitted as before. Ground-floor band stays
    # unaffected because no vaulted-ceiling pattern exists there.
    # Vaulted-ceiling gate.
    #
    # Some rooms in this scene are vaulted (Kitchen, LaundryRoom). They have
    # no real upper-floor walkable area, just a thin visual `SM_Ceiling_*`
    # slab at TopY≈12.37m (0.5m below the actual upper-floor surfaces at
    # ~12.84-12.95). Without filtering, that visual ceiling rasterizes as
    # walkable upper-floor area and interactables snap into a phantom region.
    #
    # Discriminator: each band has a "true" floor Y close to its highest
    # large walkable surface. Visual ceilings sit noticeably lower. We
    # measure that distance per-band rather than against the band's mid-Y
    # because fy is a round number that doesn't always equal the actual
    # mesh height (ground SM_Floor_* sits at -0.57 vs fy=-0.5).
    #
    # Algorithm: the band's "true" floor Y is the highest TopY among LARGE
    # walkable surfaces (area > LARGE_SLAB_M2). Props and small objects (a
    # 0.5×0.5m book lying on a desk) don't qualify. Then gate surfaces whose
    # TopY is more than VAULTED_DROP_M below that.
    #
    # Calibration: vaulted Kitchen ceiling (TopY=12.37, area >>4 m² → large)
    # vs band-top 12.95 (SM_Ceiling_Hall = upstairs hall floor) → 0.58m drop
    # → gates. SM_Floor_Bedroom 12.84 vs 12.95 → 0.11m drop → passes.
    # Ground floor SM_Floor_Office −0.57 vs band-top −0.48 (rugs) → 0.09 drop
    # → passes.
    LARGE_SLAB_M2 = 50.0       # actual room floors are 100-800 m²
    SLAB_MAX_VEXT_M = 0.10     # actual floor/ceiling meshes are thin (VExt 0-0.04m);
                               # treadmills, rugs, beds have VExt 0.16-0.81m
    VAULTED_DROP_M = 0.30
    def _slab_area(w):
        fp = w["Footprint"]
        return (fp["MaxX"] - fp["MinX"]) * (fp["MaxZ"] - fp["MinZ"])
    large_walks = [
        w for w in floor_walks
        if _slab_area(w) >= LARGE_SLAB_M2 and w["VerticalExtent"] <= SLAB_MAX_VEXT_M
    ]
    if large_walks:
        band_top_y = max(w["TopY"] for w in large_walks)
    else:
        band_top_y = max(w["TopY"] for w in floor_walks)

    # walkable_bm: can the player stand here. floor_y_bm: the Y of the surface
    # they stand ON at that cell — the capsule's FOOT. The nominal band Y (fy) is
    # a round number that does NOT equal the real floor mesh (upper floors sit at
    # ~12.84, not 12.5; ground at ~-0.57, not -0.5). Column blocking and the
    # step-over rule must measure the capsule from the REAL surface, or the floor
    # slab the player stands on (TopY 12.84, 0.34m above fy=12.5) blocks itself.
    # Cells with no walkable surface default to band_top_y so blocker columns off
    # the walkable area still have a sane floor reference for the overlap test.
    walkable_bm = [[False] * nz for _ in range(nx)]
    floor_y_bm = [[band_top_y] * nz for _ in range(nx)]
    for w in floor_walks:
        if band_top_y - w["TopY"] > VAULTED_DROP_M:
            continue
        fp = w["Footprint"]
        top_y = w["TopY"]
        ix0 = max(0, int(math.floor((fp["MinX"] - minx) / CELL)))
        ix1 = min(nx, int(math.ceil((fp["MaxX"] - minx) / CELL)))
        iz0 = max(0, int(math.floor((fp["MinZ"] - minz) / CELL)))
        iz1 = min(nz, int(math.ceil((fp["MaxZ"] - minz) / CELL)))
        for ix in range(ix0, ix1):
            for iz in range(iz0, iz1):
                if not walkable_bm[ix][iz] or top_y > floor_y_bm[ix][iz]:
                    walkable_bm[ix][iz] = True
                    floor_y_bm[ix][iz] = top_y

    # Coarse vertical band for the cheap per-record TopY/BottomY prefilter only
    # (the authoritative test is the per-cell column overlap below). Widened a
    # touch around the real surface span so a record sitting on the 12.84 floor
    # isn't prefiltered out before its columns are measured against floor_y_bm.
    y_lo = fy - STEP_UP_TOL
    y_hi = fy + CAPSULE_H

    blocked_bm = [[False] * nz for _ in range(nx)]
    blocker_hits = 0
    primitive_blocker_hits = 0
    mesh_column_blocker_hits = 0  # mesh colliders that blocked >=1 cell here
    mesh_column_cells = 0         # total cells blocked by mesh-collider columns
    # Mesh collider pass: this is the 2.5D capsule-clearance approximation.
    # Any active, enabled, non-trigger mesh collider that has player-height
    # triangle-slice segments contributes its actual surface traces, regardless
    # of whether it is a wall, fireplace, table, counter, bookshelf, etc.
    # Dilation below expands those traces by the player capsule radius.
    # Anchors for the "is this archway carved by a door?" test. A doorframe
    # within DOOR_COMPONENT_CARVE_RADIUS of any door anchor is a REAL door's
    # frame — its passability is governed by the door-position carve (for
    # Doors_* interactables) or the per-door freed-cells state machine (for
    # panel-based door_records like AtticDoor_11, BackDoorPivot, the front
    # door). Those frames must NOT be archway-carved: doing so would force the
    # doorway permanently open and bypass locked/closed-door state. Frames with
    # no nearby door anchor are genuine open archways and get carved.
    #
    # Both anchor sources matter: `doors` covers Doors_*-named carve anchors;
    # door_records covers panel-based doors whose names don't start with Doors_
    # (e.g. AtticDoor_11 frames SM_Doorframe_Small_12 — a LOCKED attic door
    # that must stay shut). Missing door_records here wrongly opened it.
    door_anchor_xz = [
        (d["x"], d["z"]) for d in doors
        if abs(d.get("y", fy) - fy) <= 2.0
    ]
    for dr in door_records:
        wp = dr.get("WorldPosition") or {}
        ax = wp.get("x")
        az = wp.get("z")
        ay = wp.get("y")
        if ax is None or az is None:
            continue
        if ay is not None and abs(ay - fy) > 2.0:
            continue
        door_anchor_xz.append((ax, az))

    def _frame_has_door(record):
        c = record.get("Footprint", {}).get("Center") or {}
        cx = c.get("x")
        cz = c.get("z")
        if cx is None or cz is None:
            return False
        return any(
            math.hypot(cx - ax, cz - az) <= DOOR_COMPONENT_CARVE_RADIUS
            for ax, az in door_anchor_xz
        )

    # Open-archway carve anchors. Doorframes with no associated door
    # (open archways) get a clearance disc carved at the frame center, exactly
    # like a real door — the door-carve below is proven to punch through a
    # doorway's asymmetric segment stubs + dilation. Collected here, applied in
    # the door-carve pass. See [[project-navigation-upper-hall2-archway-seal]].
    archway_carves = []

    for m in mesh_colliders:
        if not _is_solid_blocker(m):
            continue
        if m["TopY"] < y_lo or m["BottomY"] > y_hi:
            continue
        columns = (m.get("Footprint") or {}).get("Columns") or []
        if not columns:
            continue
        # Cells this mesh blocks on THIS floor, by per-cell vertical-span overlap.
        # Columns replace the old segment-trace + footprint-perimeter + closed-
        # region passes in one shot: a thin wall's columns are a thin cell line
        # (interior untouched, like the old segments), while a solid object's
        # top-surface triangles paint every interior cell, so the interior fills
        # itself with no hull/flood-fill heuristic.
        if _is_doorframe(m) and not _frame_has_door(m):
            bb = m.get("Bounds2D")
            if bb:
                # Keep the frame's own in-band columns alongside its bbox so the
                # archway carve can rebuild the jamb-post halo and open only the
                # threshold gap between the posts.
                archway_carves.append((bb, columns))
        marked = _rasterize_columns_into(
            blocked_bm, columns, floor_y_bm, band_top_y,
            minx, minz, nx, nz, CELL, COL_CELL,
        )
        if marked > 0:
            mesh_column_blocker_hits += 1
            mesh_column_cells += marked

    # Door panels in CLOSED pose: block cells via their closed-pose columns. The
    # regular mesh-collider pass excludes door-connector meshes (IsDoorConnector
    # filter) because the legacy bake treated all doors as always-open. Now that
    # we track per-door open/closed state via freed-cells, the closed-pose panel
    # must contribute to the blocked bitmap — otherwise the doorway is always
    # passable in the bake and "freed when open" is meaningless. Container doors
    # (fridge/cupboard) live entirely at chest height; their columns block the
    # floor band correctly where fixed-Y slice planes missed them.
    for door_rec in door_records:
        for panel in door_rec.get("Panels", []):
            _rasterize_columns_into(
                blocked_bm, panel.get("ColumnsClosed") or [],
                floor_y_bm, band_top_y, minx, minz, nx, nz, CELL, COL_CELL,
            )

    for b in blockers:
        if not _is_solid_blocker(b): continue
        if b["TopY"] < y_lo or b["BottomY"] > y_hi: continue
        bb = b.get("Bounds2D")
        if not bb: continue
        if not in_scene((bb["MinX"]+bb["MaxX"])/2, (bb["MinZ"]+bb["MaxZ"])/2): continue

        # Mesh colliders contribute ONLY via their actual collision-surface
        # slice traces (the segment pass above). We never rasterize a mesh
        # collider's AABB: the bounding box of a non-convex collision mesh
        # over-blocks (a wall's AABB fills the room interior; an animated/
        # skinned mesh's AABB sprawls across all poses). A mesh collider with no
        # in-band segments simply doesn't intersect the player band here, so it
        # contributes nothing. Skinned dialog-rig colliders are already dropped
        # at export (SkinnedMeshRigCollider). Only PRIMITIVE colliders rasterize
        # from bounds below — for a Box/Sphere/Capsule the "bounds" ARE the
        # exact collision dimensions, not an inflated mesh AABB.
        if b.get("ColliderType") == "MeshCollider":
            continue

        if _rasterize_bounds(blocked_bm, bb, minx, minz, nx, nz, CELL):
            blocker_hits += 1
            primitive_blocker_hits += 1

    # Dilate blocked by capsule radius. Use Euclidean disc instead of
    # Chebyshev box: a 2-cell box gives 0.57m corner reach (sqrt(2)*0.4m)
    # and overshrinks doorway gaps for wall-segment rasterizations; the
    # Euclidean disc respects the actual capsule radius (0.4m) in all
    # directions, freeing diagonal corners and recovering 0.8m doorways.
    if DILATE_CELLS > 0:
        dilated = [[False] * nz for _ in range(nx)]
        d = DILATE_CELLS
        offsets = [(dx, dz) for dx in range(-d, d+1) for dz in range(-d, d+1)
                   if dx*dx + dz*dz <= d*d]
        for ix in range(nx):
            for iz in range(nz):
                if not blocked_bm[ix][iz]: continue
                for dx, dz in offsets:
                    jx = ix + dx
                    if jx < 0 or jx >= nx: continue
                    jz = iz + dz
                    if jz < 0 or jz >= nz: continue
                    dilated[jx][jz] = True
    else:
        dilated = blocked_bm

    # Door-position carve: undo dilation in a disc around each door on this
    # floor. Several wall meshes cut doorways on only one face; dilation
    # re-seals them.
    #
    # Doors that have per-door freed-cells exported (Panels[] with
    # SegmentsClosed/OpenSegmentSets) are skipped here -- their closed-pose
    # panel mesh is now a blocker, and consumers apply freed-cells when the
    # door opens. The carve still runs for doors WITHOUT per-door data
    # (older datable Doors_* interactables that lack a DoorComponent and
    # therefore have no panel mesh association in the exporter).
    doors_with_panel_data = set()
    for door_rec in door_records:
        name = door_rec.get("Name")
        if name and door_rec.get("Panels"):
            doors_with_panel_data.add(name)

    door_carves = 0
    for d in doors:
        if d.get("name") in doors_with_panel_data:
            continue
        dy = d["y"]
        if abs(dy - fy) > 2.0: continue  # different floor
        dx_w, dz_w = d["x"], d["z"]
        ix = int((dx_w - minx) / CELL)
        iz = int((dz_w - minz) / CELL)
        radius = d.get("radius", DOOR_CARVE_RADIUS)
        cr = int(math.ceil(radius / CELL))
        carve_offsets = [(dx, dz) for dx in range(-cr, cr+1) for dz in range(-cr, cr+1)
                         if dx*dx + dz*dz <= cr*cr]
        for dx, dz in carve_offsets:
            jx = ix + dx; jz = iz + dz
            if jx < 0 or jx >= nx or jz < 0 or jz >= nz: continue
            if dilated[jx][jz]:
                dilated[jx][jz] = False
                door_carves += 1

    # Open-archway carve: for doorframes with no associated door, undo dilation
    # across the doorway throat so the passage opens. Masked to the frame's own
    # XZ bounding box (plus a margin) so the carve cannot leak far from the
    # frame. Like the door-carve, only dilated cells are cleared and the final
    # `walkable AND NOT dilated` keeps non-floor cells blocked.
    #
    # Margin is floor-aware. Ground frames use a tight 0.5m margin: ground
    # rooms are densely packed and a wide carve over-widens many doorways at
    # once (merging components that should stay doorway-gated). The upper floor
    # uses 1.2m to bridge the stair-newel dilation pinch that seals the stair
    # landing from the upstairs archway corridor — the newel post + jamb
    # dilation close a ~1m doorway about one capsule-width past the
    # SM_Doorframe_Small_13 frame, and a 0.5m box stops just short of it. The
    # upper floor is safe to carve wider because the per-cell column raster no
    # longer paints phantom ground-wall lips on the upper floor for a wide carve
    # to graze (a ground wall's top only blocks the upper floor where it genuinely
    # rises >0.30m above the 12.84 surface — the step-over rule in
    # _column_blocks_floor — so there is nothing spurious near the carve).
    # See [[project-navigation-upper-hall2-archway-seal]],
    # [[project-navigation-bake-percell-vertical-span]].
    # POST-CLEARANCE GUARD: a doorframe is not a clean hole — it has solid jamb
    # POSTS. The carve must open the threshold GAP between the posts but must NOT
    # remove the capsule-clearance dilation hugging the posts, or the planner
    # routes the player flush against a post and the runtime collider stops them
    # (e.g. SM_Doorframe_Small_7's east post: bake said navigable, player walked
    # into it and stalled). For each frame, re-rasterize its own columns and
    # dilate by the capsule radius; that post-halo is preserved (never cleared),
    # while the threshold gap — which is >1 capsule-width from either post — is
    # opened. See [[project-navigation-executor-corner-stall]].
    ARCHWAY_CARVE_MARGIN_M = 1.2 if fy > 6.0 else 0.5
    mgn = ARCHWAY_CARVE_MARGIN_M
    for bb, columns in archway_carves:
        bx0 = int((bb["MinX"] - mgn - minx) / CELL)
        bx1 = int((bb["MaxX"] + mgn - minx) / CELL)
        bz0 = int((bb["MinZ"] - mgn - minz) / CELL)
        bz1 = int((bb["MaxZ"] + mgn - minz) / CELL)

        # Build the frame's own post-halo (raw post cells dilated by capsule R)
        # from the frame's in-band columns — same cells that blocked above.
        post_raw = [[False] * nz for _ in range(nx)]
        _rasterize_columns_into(post_raw, columns, floor_y_bm, band_top_y,
                                minx, minz, nx, nz, CELL, COL_CELL)
        post_halo = _dilate_disc(post_raw, nx, nz, DILATE_CELLS)

        for jx in range(max(0, bx0), min(nx, bx1 + 1)):
            for jz in range(max(0, bz0), min(nz, bz1 + 1)):
                if dilated[jx][jz] and not post_halo[jx][jz]:
                    dilated[jx][jz] = False
                    door_carves += 1

    # Navigable = walkable AND NOT dilated
    navigable_bm = [[walkable_bm[ix][iz] and not dilated[ix][iz]
                     for iz in range(nz)] for ix in range(nx)]

    # Per-door freed-cells pass. For each door, rasterize all its panels'
    # closed-pose floor segments into a per-door bitmap, dilate, and do the
    # same for the union of all open poses. The freed-cells set is:
    #     freed = panel_closed_dil AND NOT panel_open_dil_union
    #             AND walkable AND NOT (dilated AND NOT panel_closed_dil)
    # The last factor masks out cells that would still be blocked by something
    # OTHER than this door's panels — freeing the door doesn't help if a wall
    # also sits there. Consumers OR the freed cells into navigable_bm at
    # door-open time. BothWays hinges union both signed open poses (the door
    # is passable either way, so the consumer doesn't need to pick a side).
    doors_per_floor = []
    for door_rec in door_records:
        is_container = _is_container_door(door_rec)
        panel_closed_raw = [[False] * nz for _ in range(nx)]
        panel_open_raw = [[False] * nz for _ in range(nx)]
        has_closed_in_band = False
        has_open_in_band = False
        for panel in door_rec.get("Panels", []):
            cm = _rasterize_columns_into(
                panel_closed_raw, panel.get("ColumnsClosed") or [],
                floor_y_bm, band_top_y, minx, minz, nx, nz, CELL, COL_CELL)
            if cm > 0:
                has_closed_in_band = True
            for os in panel.get("OpenSegmentSets", []):
                om = _rasterize_columns_into(
                    panel_open_raw, os.get("Columns") or [],
                    floor_y_bm, band_top_y, minx, minz, nx, nz, CELL, COL_CELL)
                if om > 0:
                    has_open_in_band = True
        if not has_closed_in_band and not has_open_in_band:
            # A CONTAINER door whose panel mesh sits above the capsule band (upper
            # wall cupboard) or has no collider (render-only panel) rasterizes to
            # zero in-band cells. It's still real and operable: the player stands
            # on the floor below and opens it to reach the item inside. Emit it as
            # an OPERABILITY-ONLY record on whatever floor has navigable cells
            # under its anchor XZ, with no freed/threshold cells (it's not a
            # passage). Passage doors with no in-band panel are still skipped —
            # they'd be a broken export to fix at the source, not papered over.
            if is_container:
                op_only = _container_operable_record(
                    door_rec, navigable_bm, minx, minz, nx, nz, fy, storey_ceiling_y)
                if op_only is not None:
                    doors_per_floor.append(op_only)
            continue

        panel_closed_dil = _dilate_disc(panel_closed_raw, nx, nz, DILATE_CELLS)
        panel_open_dil = _dilate_disc(panel_open_raw, nx, nz, DILATE_CELLS)

        # Doorway-threshold cells. The panel diff alone tells us where the
        # panel's mesh moved when opening. But the actual passable space when
        # the door swings open also includes the doorway threshold itself —
        # the gap between the surrounding wall meshes. After dilation by the
        # capsule radius, wall meshes re-seal that gap. The blanket carve was
        # solving that problem; we now apply a per-door carve gated on door
        # state. Cells within DOOR_COMPONENT_CARVE_RADIUS of the door's anchor
        # that are currently dilated-blocked AND walkable count as threshold
        # cells that opening this door unblocks.
        # Threshold cells: cells within DOOR_COMPONENT_CARVE_RADIUS of the
        # door anchor that are walkable + dilation-blocked. The doorway gap
        # in the wall mesh is often modelled on only one face (asymmetric
        # export), so dilation seals the gap even though the geometry has
        # it. Threshold cells re-open that gap.
        door_pos = door_rec.get("WorldPosition") or {}
        anchor_x = door_pos.get("x")
        anchor_z = door_pos.get("z")
        anchor_y = door_pos.get("y", fy)
        threshold_cells = []
        if (anchor_x is not None and anchor_z is not None
                and abs(anchor_y - fy) <= 2.0):
            cx = int((anchor_x - minx) / CELL)
            cz = int((anchor_z - minz) / CELL)
            cr = int(math.ceil(DOOR_COMPONENT_CARVE_RADIUS / CELL))
            # Connectivity gate against the original bug A: a threshold cell
            # must be reachable from the door's own panel through cells that
            # are EITHER navigable in the door-open world OR in the door's
            # closed-pose dilation. This prevents the carve from leaking
            # through an intervening wall into a neighbouring room's clearance
            # band (Doors_Office leaking into SM_Walls_Hall1 dilation).
            # Implemented as a BFS seeded from the panel_closed_dil cells,
            # bounded to the carve disc.
            from collections import deque
            seeds = []
            for ix in range(max(0, cx - cr), min(nx, cx + cr + 1)):
                for iz in range(max(0, cz - cr), min(nz, cz + cr + 1)):
                    if panel_closed_dil[ix][iz]:
                        seeds.append((ix, iz))
            if seeds:
                reach = set(seeds)
                queue = deque(seeds)
                while queue:
                    qx, qz = queue.popleft()
                    for dx in (-1, 0, 1):
                        for dz in (-1, 0, 1):
                            if dx == 0 and dz == 0:
                                continue
                            tx = qx + dx; tz = qz + dz
                            if tx < 0 or tx >= nx or tz < 0 or tz >= nz:
                                continue
                            # Stay inside the carve disc.
                            if (tx - cx) ** 2 + (tz - cz) ** 2 > cr * cr:
                                continue
                            if (tx, tz) in reach:
                                continue
                            # Walkable cells inside the disc that are EITHER
                            # navigable post-bake OR in the panel's closed
                            # dilation are reachable. Walls (dilated cells
                            # NOT in the panel) block the BFS.
                            if not walkable_bm[tx][tz]:
                                continue
                            if dilated[tx][tz] and not panel_closed_dil[tx][tz]:
                                continue
                            reach.add((tx, tz))
                            queue.append((tx, tz))
                # Threshold cells = reachable cells that are dilation-blocked
                # (so opening the door is what gives them passage). Cells
                # already navigable don't need to be re-added.
                for (jx, jz) in reach:
                    if dilated[jx][jz]:
                        threshold_cells.append((jx, jz))

        # Door-open dilation mask. A freed cell must be navigable in the world
        # where this door is open. The earlier "any non-door raw blocker
        # within DILATE_CELLS" check was too aggressive — it dropped the
        # entire doorway threshold (cells in the gap between the door's
        # surrounding walls) because the walls themselves are within DILATE
        # of the gap, even though the gap is wider than 2× capsule radius.
        #
        # The correct test: compute the would-be dilated bitmap if this door
        # were open (= blocked_bm minus this door's closed panel cells, plus
        # this door's open panel cells), then a cell is legitimately freed
        # iff it is NOT dilation-blocked in that alternative world. This
        # exactly captures "opening the door makes this cell reachable."
        #
        # Cost: one O(nx*nz*DILATE_CELLS^2) dilation per door. Doors are
        # sparse and only one floor at a time matters, so total cost is fine.
        door_open_raw = [
            [(blocked_bm[ix][iz] and not panel_closed_raw[ix][iz]) or panel_open_raw[ix][iz]
             for iz in range(nz)]
            for ix in range(nx)
        ]
        door_open_dil = _dilate_disc(door_open_raw, nx, nz, DILATE_CELLS)

        freed_set = set()
        for ix in range(nx):
            for iz in range(nz):
                # Candidate cells: those the closed-pose dilation covers but
                # the open-pose dilation does not (the door panel's swept
                # region) and the doorway threshold (added below).
                if not panel_closed_dil[ix][iz]:
                    continue
                if panel_open_dil[ix][iz]:
                    continue
                if not walkable_bm[ix][iz]:
                    continue
                # Final gate: in the door-open world, this cell must be
                # outside any blocker's capsule clearance.
                if door_open_dil[ix][iz]:
                    continue
                freed_set.add((ix, iz))
        # Threshold cells are exempt from the door_open_dil gate by design:
        # the doorway opening is exactly the place where the wall has a gap
        # that dilation seals over (the wall mesh is exported on one face
        # only). The adjacent-to-panel_closed_dil constraint above ensures
        # threshold cells sit in the door's own wall opening rather than in
        # a different wall's clearance band.
        for c in threshold_cells:
            freed_set.add(c)

        if not freed_set:
            # A container door (e.g. fridge door whose leaf is at counter height)
            # can have an in-band panel but sweep no walkable threshold — there's
            # nothing to "free" because you don't walk through it. Still emit its
            # operability so the planner can route the player to open it. Passage
            # doors with no freed cells are a real defect and still skipped.
            if is_container:
                op_only = _container_operable_record(
                    door_rec, navigable_bm, minx, minz, nx, nz, fy, storey_ceiling_y)
                if op_only is not None:
                    doors_per_floor.append(op_only)
            continue
        freed = sorted([list(c) for c in freed_set])
        # Emit the door's own closed-pose dilation footprint so the post-bake
        # invariant can subtract it when checking freed_cells against the
        # global dilated bitmap (otherwise every freed cell looks "blocked"
        # because the door's own panel contributes to dilation).
        own_dil_cells = sorted(
            [ix, iz]
            for ix in range(nx) for iz in range(nz)
            if panel_closed_dil[ix][iz]
        )
        # Threshold cells emitted separately so the invariant can exempt them
        # from the "freed cells must not be dilation-blocked by another wall"
        # check. Threshold cells are by design in the surrounding wall's
        # dilation band — that's the wall opening dilation seals over. The
        # adjacency-to-panel_closed_dil constraint above keeps them legitimate.
        threshold_cells_list = sorted([list(c) for c in threshold_cells])
        # Operability standpoint: navigable cells where the player can stand to
        # open/close this door, derived offline from the Door.cs rule (within
        # reach, not touching the closed panel, not in the open-pose sweep). The
        # cleaner blocker signal (no dating-rig / AABB sprawl) means panel_*_dil
        # is now a faithful silhouette of just the door leaf, so this set is
        # tight. Computed against the door-CLOSED navigable bitmap (you operate a
        # door from the world where it is currently closed).
        operable_from_cells = _door_operable_cells(
            navigable_bm, panel_closed_dil, panel_open_dil,
            anchor_x, anchor_z, minx, minz, nx, nz)
        doors_per_floor.append({
            "name": door_rec.get("Name"),
            "kind": door_rec.get("Kind"),
            "component_id": door_rec.get("ComponentId"),
            "panel_count": len(door_rec.get("Panels", [])),
            "closed_cells": sum(sum(row) for row in panel_closed_dil),
            "open_cells": sum(sum(row) for row in panel_open_dil),
            "threshold_cells": len(threshold_cells),
            "threshold_cells_list": threshold_cells_list,
            "freed_cells": freed,
            "freed_count": len(freed),
            "panel_dilated_cells": own_dil_cells,
            "operable_from_cells": operable_from_cells,
            "operable_from_count": len(operable_from_cells),
            # Authoritative scene-load state from the exporter's Door component,
            # carried through so consumers stop GUESSING the doors-open set. At
            # scene load every Door/SlidingDoor in this house exports Open=False
            # (one is Locked). A consumer wanting "what the player actually faces
            # on load" opens exactly the doors with default_open=True (none here)
            # rather than the all-open coverage probe. See
            # [[project-navigation-doors-open-defaults]],
            # [[project-navigation-sweep-follower-doorstate-fix]].
            "default_open": bool(door_rec.get("Open", False)),
            "locked": bool(door_rec.get("Locked", False)),
        })

    # Per-state-wall freed-cells pass. State-gated walls (currently just the
    # DresserWall) are active in the closed-pose bake by default. Consumers can
    # opt into the post-release state via the same overlay mechanism as doors.
    # Each state-wall's freed-cells = wall_dil ∩ walkable ∩ ¬(dilated ∩ ¬wall_dil)
    # — cells the wall covers in dilation that would be navigable absent the
    # wall (i.e. nothing else also blocks them).
    state_walls_per_floor = []
    for wall in state_walls or []:
        b2 = wall.get("Bounds2D") or {}
        if not b2:
            continue
        if wall.get("TopY") is None or wall.get("BottomY") is None:
            continue
        if wall["TopY"] < y_lo or wall["BottomY"] > y_hi:
            continue
        wall_raw = [[False] * nz for _ in range(nx)]
        if not _rasterize_bounds(wall_raw, b2, minx, minz, nx, nz, CELL):
            continue
        wall_dil = _dilate_disc(wall_raw, nx, nz, DILATE_CELLS)
        # Wall-released dilation mask, same shape as the door pass.
        # The original guard `dilated AND NOT wall_dil` was a no-op (the
        # outer loop already required wall_dil). The correct test: compute
        # the dilated bitmap as it would be if THIS wall were removed, then
        # the wall's freed cells are those navigable in that alternative
        # world. See [[project-navigation-door-carve-dilation-bug]].
        wall_released_raw = [
            [blocked_bm[ix][iz] and not wall_raw[ix][iz] for iz in range(nz)]
            for ix in range(nx)
        ]
        wall_released_dil = _dilate_disc(wall_released_raw, nx, nz, DILATE_CELLS)
        freed = []
        for ix in range(nx):
            for iz in range(nz):
                if not wall_dil[ix][iz]:
                    continue
                if not walkable_bm[ix][iz]:
                    continue
                if wall_released_dil[ix][iz]:
                    continue
                freed.append([ix, iz])
        if not freed:
            continue
        own_dil_cells_sw = sorted(
            [ix, iz]
            for ix in range(nx) for iz in range(nz)
            if wall_dil[ix][iz]
        )
        state_walls_per_floor.append({
            "name": wall.get("Name"),
            "component_id": wall.get("ComponentId"),
            "release_mechanism": wall.get("ReleaseMechanism"),
            "release_condition": wall.get("ReleaseCondition"),
            "default_active": wall.get("DefaultActive", True),
            "wall_cells": sum(sum(row) for row in wall_dil),
            "freed_cells": freed,
            "freed_count": len(freed),
            "panel_dilated_cells": own_dil_cells_sw,
        })

    walk_count = sum(sum(row) for row in walkable_bm)
    block_count = sum(sum(row) for row in blocked_bm)
    dil_count = sum(sum(row) for row in dilated)
    nav_count = sum(sum(row) for row in navigable_bm)

    return {
        "label": floor["label"],
        "floor_y": fy,
        "frame": {
            "origin_x": minx, "origin_z": minz,
            "cell_size": CELL,
            "nx": nx, "nz": nz,
            "extent_x": [minx, maxx],
            "extent_z": [minz, maxz],
        },
        "walkable_surface_count": len(floor_walks),
        "blocker_hits": blocker_hits,
        "primitive_blocker_hits": primitive_blocker_hits,
        "mesh_column_blocker_hits": mesh_column_blocker_hits,
        "mesh_column_cells": mesh_column_cells,
        # Legacy metric names retained for older diagnostics. These now mean
        # all mesh-collider column blocks, not only path/name-classified walls.
        "wall_meshes_rasterized": mesh_column_blocker_hits,
        "wall_segments_rasterized": mesh_column_cells,
        "door_carves": door_carves,
        "doors": doors_per_floor,
        "state_walls": state_walls_per_floor,
        "cells": {
            "walkable": walk_count,
            "blocked_raw": block_count,
            "blocked_dilated": dil_count,
            "navigable": nav_count,
        },
        # Pack bitmap as one row-string per ix (chars '.', 'W', 'B', 'N')
        # '.' = void, 'W' = walkable-only (blocked), 'B' = blocker-only, 'N' = navigable
        "bitmap_rows": _pack(walkable_bm, dilated, navigable_bm, nx, nz),
    }


def _pack(walk, dil, nav, nx, nz):
    rows = []
    for ix in range(nx):
        chars = []
        for iz in range(nz):
            n = nav[ix][iz]
            w = walk[ix][iz]
            b = dil[ix][iz]
            if n: c = 'N'
            elif w and b: c = 'X'  # walkable but blocked
            elif w: c = 'W'        # walkable, somehow not navigable (shouldn't happen if not blocked)
            elif b: c = 'B'        # blocker outside walkable
            else: c = '.'
            chars.append(c)
        rows.append(''.join(chars))
    return rows


def write_png(floor_result, path):
    """Write a debug PPM (no deps). Renamed .png for convenience but PPM-formatted."""
    rows = floor_result["bitmap_rows"]
    nx = floor_result["frame"]["nx"]
    nz = floor_result["frame"]["nz"]
    # Render Z increasing upward (image row 0 = max Z)
    # Each pixel = one cell
    palette = {
        '.': (24, 24, 24),
        'W': (180, 180, 60),
        'X': (110, 50, 50),
        'B': (60, 60, 60),
        'N': (80, 200, 120),
    }
    with open(path, 'wb') as f:
        f.write(f"P6\n{nx} {nz}\n255\n".encode())
        for iz in range(nz - 1, -1, -1):
            line = bytearray()
            for ix in range(nx):
                c = rows[ix][iz]
                r, g, b = palette.get(c, (255, 0, 255))
                line.append(r); line.append(g); line.append(b)
            f.write(bytes(line))


def _verify_bake_invariants(report, mesh_colliders):
    """Assert structural invariants on a freshly-baked report. Each failure is
    a recurring bug shape we want to catch at bake time, not at runtime.

    Returns (errors, warnings) — caller decides whether to raise.
    """
    errors = []
    warnings = []

    # 1. door.freed_cells ∩ (dilated_blocked ∖ panel_dilated_cells) must be empty.
    # 2. state_wall.freed_cells ∩ (dilated_blocked ∖ panel_dilated_cells) must be empty.
    # A freed cell may legitimately sit in the global dilated bitmap because
    # the door's own closed-pose panel contributes to dilation — that's the
    # whole point of freed_cells. The invariant catches freed cells that are
    # closed by a *different* blocker (a neighbouring wall). Repro this would
    # catch: see [[project-navigation-door-carve-dilation-bug]] (Doors_Office
    # freeing cells inside SM_Walls_Hall1's clearance band).
    for floor in report["floors"]:
        if "error" in floor:
            continue
        label = floor["label"]
        rows = floor["bitmap_rows"]
        def _is_dilated_blocked(ix, iz):
            # 'X' = walkable AND blocked, 'B' = blocker only. Both are dilated-blocked.
            return rows[ix][iz] in ('X', 'B')
        for door in floor.get("doors", []):
            own = {(c[0], c[1]) for c in door.get("panel_dilated_cells", [])}
            # Threshold cells are exempt: they sit in the door's surrounding
            # wall's dilation by design (asymmetric wall-mesh export), and
            # the carve adjacency constraint keeps them inside the door's
            # actual opening rather than in an unrelated wall.
            thresholds = {(c[0], c[1]) for c in door.get("threshold_cells_list", [])}
            violating = [(c[0], c[1]) for c in door.get("freed_cells", [])
                         if _is_dilated_blocked(c[0], c[1])
                         and (c[0], c[1]) not in own
                         and (c[0], c[1]) not in thresholds]
            if violating:
                errors.append(
                    f"floor={label} door={door.get('name')!r}: "
                    f"{len(violating)} freed_cells closed by a non-door blocker "
                    f"(e.g. {violating[:5]})"
                )
        for wall in floor.get("state_walls", []):
            own = {(c[0], c[1]) for c in wall.get("panel_dilated_cells", [])}
            violating = [(c[0], c[1]) for c in wall.get("freed_cells", [])
                         if _is_dilated_blocked(c[0], c[1])
                         and (c[0], c[1]) not in own]
            if violating:
                errors.append(
                    f"floor={label} state_wall={wall.get('name')!r}: "
                    f"{len(violating)} freed_cells closed by a non-wall blocker "
                    f"(e.g. {violating[:5]})"
                )

    # 3. (Retired.) This slot held a slice-plane-coverage assertion that backed
    # the borrow-from-other-band fallback in _segments_in_floor_band. Both the
    # fallback and the IsWallLikeFatVictim flag it keyed on are gone: the bake
    # rasterizes only real collision-slice segments and the 12.5 slice plane
    # already covers the ground-wall top-lip case. A wall-slice-gap regression
    # now surfaces as item isolation in scripts/reachability_matrix.py, which is
    # the authoritative coverage check. See
    # [[project-navigation-iswalllikefatvictim-followup]].

    # 4. Interactable coverage smoke check intentionally omitted here. The
    # raw interactables list contains many sub-mesh entries (book pages,
    # monitor sub-parts, lighting variants) whose Position is buried inside
    # the parent's collider footprint, so a naive nearest-navigable check
    # produces hundreds of false positives every bake. scripts/reachability_matrix.py
    # already does the per-interactable check correctly (snapping by Path
    # and interaction radius); use that as the authoritative coverage tool.

    # 5. Every inter_floor_edge endpoint must land on a navigable cell.
    # If a stair/teleporter terminus falls into a sealed cell, the planner
    # silently drops the edge and cross-floor routing breaks. Edges live in
    # a dict keyed by category (stair_ramp, teleporter); each entry has
    # per-floor endpoints with {cell: [ix, iz]} or a deferred note.
    edges_doc = report.get("inter_floor_edges") or {}
    if isinstance(edges_doc, dict):
        floors_by_label = {f["label"]: f for f in report["floors"] if "error" not in f}
        edge_lists = []
        for category, lst in edges_doc.items():
            if isinstance(lst, list):
                edge_lists.append((category, lst))
        for category, lst in edge_lists:
            if category.endswith("rejected"):
                continue  # rejected entries are diagnostic, not active edges
            for edge in lst:
                if not isinstance(edge, dict):
                    continue
                for label, f in floors_by_label.items():
                    ep = edge.get(label)
                    if not isinstance(ep, dict):
                        continue
                    cell = ep.get("cell")
                    if not isinstance(cell, list) or len(cell) < 2:
                        continue  # deferred / no-cell endpoint
                    ix, iz = cell[0], cell[1]
                    fr = f["frame"]
                    if not (0 <= ix < fr["nx"] and 0 <= iz < fr["nz"]):
                        errors.append(
                            f"inter_floor_edge {category}/{edge.get('kind','?')} endpoint "
                            f"on floor {label} cell ({ix},{iz}) out of bounds"
                        )
                        continue
                    if f["bitmap_rows"][ix][iz] != 'N':
                        errors.append(
                            f"inter_floor_edge {category}/{edge.get('kind','?')} endpoint "
                            f"on floor {label} cell ({ix},{iz}) is not navigable "
                            f"(char={f['bitmap_rows'][ix][iz]!r})"
                        )

    # 6. Every door / state-wall with panel data must have a non-empty
    # freed_cells set. The carve passes have several layers of masking
    # (other-blocker dilation, door-open-dilation, threshold adjacency) and
    # a regression in any of them can wipe a door's contribution silently —
    # the runtime then unions an empty set when the door opens, the door
    # becomes a no-op overlay, and the room behind it stays unreachable.
    #
    # Distinction: doors with `panel_count == 0` are name-only entries
    # (interactables tagged Doors_* but no exported panel mesh, e.g. the
    # Camera_DorianBathroom2Door* placeholder objects). Those legitimately
    # have no freed cells. Warning, not error.
    for floor in report["floors"]:
        if "error" in floor:
            continue
        label = floor["label"]
        for door in floor.get("doors", []):
            if door.get("freed_count", 0) > 0:
                continue
            name = door.get("name") or "?"
            panels = door.get("panel_count", 0)
            if door.get("container_operable_only"):
                # A container door (cupboard/fridge) is opened in place, not walked
                # through, so 0 freed_cells is BY DESIGN — it carries operable_from_cells
                # instead. Not a carve-mask defect. Still require it to be useful.
                if door.get("operable_from_count", 0) == 0:
                    errors.append(
                        f"floor={label} container door={name!r}: emitted operability-only "
                        f"but has 0 operable_from_cells — should not have been emitted."
                    )
            elif panels > 0:
                errors.append(
                    f"floor={label} door={name!r}: 0 freed_cells despite "
                    f"panel_count={panels}. Carve masks may be over-aggressive — "
                    f"opening this door has no effect on routing."
                )
            else:
                warnings.append(
                    f"floor={label} door={name!r}: 0 freed_cells (no panel data; "
                    f"name-only door entry — expected)."
                )
        for wall in floor.get("state_walls", []):
            if wall.get("freed_count", 0) > 0:
                continue
            name = wall.get("name") or "?"
            errors.append(
                f"floor={label} state_wall={name!r}: 0 freed_cells. Releasing "
                f"this wall has no effect on routing."
            )

    return errors, warnings


def append_inter_floor_edges():
    """Run the O3 post-pass against the freshly-written bake."""
    script_path = Path(__file__).resolve().with_name("derive_inter_floor_edges.py")
    spec = importlib.util.spec_from_file_location("derive_inter_floor_edges", script_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load {script_path}")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.main()

    refreshed = json.load(open(OUT_JSON, encoding="utf-8"))
    if len(refreshed.get("floors", [])) > 1 and "inter_floor_edges" not in refreshed:
        raise RuntimeError("Bake has multiple floors but no inter_floor_edges after O3 derivation")


def main():
    walk = json.load(open(WALK, encoding="utf-8"))
    blok = json.load(open(BLOCK, encoding="utf-8-sig"))
    walkables = walk["WalkableSurfaces"]
    # Register each BELOW-GROUND floor's XZ footprint so _fixture_floor can gate sub-ground
    # fixture assignment by location (keeps a degenerate rig-origin fixture far from the
    # crawlspace from being Y-only-claimed by it). Derived from the native walkable surfaces
    # in that floor's band — the crawlspace floor (SM_Floor_Crawlspace @ Y-9.89) now exports
    # natively via the exporter's floor-aware MinimumWalkableTopY clip, so no recovery hack.
    for f in FLOORS:
        if f["y"] >= FLOORS_GROUND_Y:
            continue
        band = [w for w in walkables if abs(w["TopY"] - f["y"]) <= f.get("y_tol", 1.25)
                and w.get("Footprint")]
        if not band:
            continue
        SUBGROUND_FLOOR_FOOTPRINTS[f["label"]] = (
            min(w["Footprint"]["MinX"] for w in band),
            max(w["Footprint"]["MaxX"] for w in band),
            min(w["Footprint"]["MinZ"] for w in band),
            max(w["Footprint"]["MaxZ"] for w in band))
    blockers = blok["NavigationBlockers"]
    mesh_colliders = blok.get("MeshColliders", [])
    door_records = blok.get("Doors", [])
    state_walls = blok.get("StateWalls", [])

    # Validate the column-raster cell size matches the bake grid (the column
    # index->world->bake-cell mapping in _rasterize_columns_into assumes
    # COL_CELL; a mismatch would silently shift every blocker). Read the first
    # mesh record carrying a ColumnCellSize and assert.
    for _m in mesh_colliders:
        _ccs = (_m.get("Footprint") or {}).get("ColumnCellSize")
        if _ccs is not None:
            if abs(_ccs - COL_CELL) > 1e-9:
                raise SystemExit(
                    f"Export ColumnCellSize={_ccs} != bake COL_CELL={COL_CELL}; "
                    f"re-export or update COL_CELL.")
            break

    # Doors from interactables. Each entry: {x, y, z, name}. Used to carve
    # navigability discs that survive wall-mesh asymmetric-cut artifacts.
    doors = []
    door_keys = set()
    if NAVDATA.exists():
        nav = json.load(open(NAVDATA, encoding="utf-8-sig"))
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
            doors.append({
                "name": name,
                "x": pos.get("x", 0.0),
                "y": pos.get("y", 0.0),
                "z": pos.get("z", 0.0),
                "radius": DOOR_COMPONENT_CARVE_RADIUS,
            })

    fixture_roster = []
    if INTER.exists():
        inter = json.load(open(INTER, encoding="utf-8"))
        recs = inter.get("Interactables") or inter.get("Records") or []
        # Canonical static target set: filtered + deduped + floor-assigned, so the planner
        # consumes a clean roster instead of re-deriving it from the raw export.
        fixture_roster = build_fixture_roster(recs)
        for it in recs:
            name = it.get("GameObjectName") or it.get("Name") or ""
            if not name.startswith("Doors_"): continue
            pos = it.get("WorldPosition") or it.get("Position") or {}
            key = (name, round(pos.get("x", 0.0), 3), round(pos.get("y", 0.0), 3), round(pos.get("z", 0.0), 3))
            if key in door_keys:
                continue
            # Doors_* datable interactables driven by dorian_door.* ink scripts
            # are functionally doors even when they lack a DoorComponent (e.g.
            # the Bedroom/Gym closet doors). The player can always open them
            # from outside and they don't latch from inside, so for routing
            # they should be treated as passable. Use the full DoorComponent
            # carve radius -- the 0.4m default isn't wide enough to punch
            # through the dilated mesh-segment trace of the door panel.
            ink = (it.get("InkFileName") or "")
            is_dorian_door = ink.startswith("dorian_door.") or it.get("IsDatable")
            radius = DOOR_COMPONENT_CARVE_RADIUS if is_dorian_door else DOOR_CARVE_RADIUS
            doors.append({
                "name": name,
                "x": pos.get("x", 0.0),
                "y": pos.get("y", 0.0),
                "z": pos.get("z", 0.0),
                "radius": radius,
            })

    report = {
        "params": {
            "capsule_radius_m": CAPSULE_R,
            "capsule_height_m": CAPSULE_H,
            "step_up_tolerance_m": STEP_UP_TOL,
            "cell_size_m": CELL,
            "dilation_cells": DILATE_CELLS,
            "door_component_carve_radius_m": DOOR_COMPONENT_CARVE_RADIUS,
        },
        "fixtures": fixture_roster,
        "floors": [],
    }
    # Each storey spans from its own floor Y up to the next floor's Y (the top
    # floor is open-ended). Container doors are assigned to the storey their pivot
    # falls in, so a kitchen cupboard mounted high doesn't also bake onto the upper
    # floor that overlaps its XZ.
    floors_sorted_y = sorted(f["y"] for f in FLOORS)
    for floor in FLOORS:
        higher = [y for y in floors_sorted_y if y > floor["y"]]
        floor["storey_ceiling_y"] = min(higher) if higher else float("inf")
        print(f"Baking floor: {floor['label']} (Y={floor['y']})...")
        result = bake_floor(floor, walkables, blockers, mesh_colliders, doors, door_records, state_walls)
        report["floors"].append(result)
        if "error" in result:
            print(f"  ERROR: {result['error']}")
            continue
        c = result["cells"]
        f = result["frame"]
        print(f"  grid: {f['nx']}x{f['nz']} cells ({f['nx']*f['nz']} total)")
        print(f"  walkable={c['walkable']}  blocked_raw={c['blocked_raw']}  "
              f"blocked_dilated={c['blocked_dilated']}  navigable={c['navigable']}")
        print(f"  primitive_blockers={result['primitive_blocker_hits']}  "
              f"mesh_column_blockers={result['mesh_column_blocker_hits']}  "
              f"mesh_column_cells={result['mesh_column_cells']}  "
              f"door_carves={result['door_carves']}")
        png_path = OUT_PNG_DIR / f"navigable_region.{floor['label']}.ppm"
        write_png(result, png_path)
        print(f"  debug image: {png_path}")

    OUT_JSON.parent.mkdir(parents=True, exist_ok=True)
    OUT_JSON.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"\nWrote {OUT_JSON}")
    append_inter_floor_edges()

    # Re-load after the inter-floor pass so invariant 5 sees the edges.
    full_report = json.loads(OUT_JSON.read_text(encoding="utf-8"))
    errors, warnings = _verify_bake_invariants(full_report, mesh_colliders)
    if warnings:
        print("\nBake invariant warnings:")
        for w in warnings:
            print(f"  WARN: {w}")
    if errors:
        print("\nBake invariant errors:")
        for e in errors:
            print(f"  ERROR: {e}")
        raise SystemExit(
            f"Bake produced {len(errors)} invariant violation(s). "
            f"See errors above; do not consume this artifact."
        )
    print("\nBake invariants: OK")


if __name__ == "__main__":
    main()
