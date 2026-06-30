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
# Player STANDING collider height = 2.5m (BetterPlayerControl.colliderHeightNormal).
# This is the real height the walking capsule occupies; geometry above it does NOT
# obstruct a standing player.
#
# History: this was bumped to 3.20 to catch open cupboard/fridge door PANELS at head
# height (world Y ~2.3-2.7) that the 2.5m band let slip through. That reason is now
# OBSOLETE -- those panels are handled independently as container door_records (see
# repair_door_fixture_positions / the container-door operability bake), so they block
# regardless of capsule height. The inflated 3.2m only caused collateral OVER-BLOCKING:
# it marked ~3,300 ground floor cells blocked under furniture/prop TOPS at head height
# (shelves, hamper lid, fruit bowl, globe, desk paper, the magic-piano overhang) and the
# upper-floor slab/ceiling ~12m up -- all of which a 2.5m player clears. Measured with
# scripts/measure_crouch_clearance.py. Reverted to the true 2.5m collider; the panel
# regression cannot recur because cupboard/fridge doors are door_records, not band hits.
# See [[project_navigation_capsule_height_overblock_2026_06_30]].
CAPSULE_H = 2.50
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

# Closet interior recovery (the capsule-width walk-in channel; see the recovery block
# in bake_floor and [[project_navigation_closet_interior_eroded_2026_06_30]]). A real
# closet interior is a small enclosed pocket; an unbounded flood through the connected
# wall-dilation web leaks ~1273 cells across the floor, so a recovered eroded-floor
# component larger than the cap is treated as a leak and discarded. 120 covers every
# measured closet (largest real interior ~72 cells) with ~10x margin below any leak.
CLOSET_INTERIOR_CELL_CAP = 120
# How far (cells) beyond the door's throat bbox to search for the eroded interior
# component. The closet sits adjacent to the throat; a small pad keeps the seed search
# local to this door's opening.
CLOSET_INTERIOR_SEARCH_PAD = 12

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

# DEPRECATED-PROP PATH DENYLIST (substring match anywhere in the path). The "(Old)" suffix
# is the game's own marker for a superseded authoring copy left in the scene. The
# DateViatorsBox (Old) holds Bow/Box/Lid — active but UNDATABLE (no ink), a leftover of the
# intro DateViators device, so they add a nameless, examine-less entry to the picker with no
# datable identity. Filtered here (set-construction rule) rather than the planner. The live
# "(Old)"-free DateViatorsBox is unaffected. Substring (not subtree-root) because these sit
# deep under House/Hallway, not at a top-level subtree.
FIXTURE_PATH_DENYLIST_SUBSTRINGS = ("DateViatorsBox (Old)",)

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
        path = it.get("Path") or ""
        if any(sub in path for sub in FIXTURE_PATH_DENYLIST_SUBSTRINGS):
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


def _door_doorway_xz(door_rec, frame):
    """World (x, z) of a baked PASSAGE door's DOORWAY — the centroid of its threshold
    cells (the opening the player crosses). Returns None when the door has no threshold
    (container/closet doors: a cupboard or sliding-closet panel has no walk-through
    opening — its fixture position is fine as-is and must NOT be relocated into the
    cabinet/closet interior).

    A passage door's interactable FIXTURE carries the door PIVOT, offset ~1-2.5m from
    the doorway centreline (the hinge/jamb), often over a non-navigable cell — so the
    objects sweep mis-classifies it off_floor (no navigable cell within reach of the
    pivot). The door's real, reachable location is its doorway, which the bake already
    computes as threshold_cells. This recovers that doorway centroid so the fixture
    position lands on walkable floor."""
    cells = door_rec.get("threshold_cells_list")
    if not cells:
        return None
    cx = sum(c[0] for c in cells) / len(cells)
    cz = sum(c[1] for c in cells) / len(cells)
    ox = frame.get("origin_x")
    oz = frame.get("origin_z")
    cs = frame.get("cell_size")
    if ox is None or oz is None or not cs:
        return None
    # Cell centre -> world (mirror of FloorFrame.cell_to_world: origin + (ix+0.5)*cell).
    return (ox + (cx + 0.5) * cs, oz + (cz + 0.5) * cs)


def repair_door_fixture_positions(roster, floors):
    """Move each door FIXTURE's XZ onto its doorway centroid (computed by the bake),
    so doors stop false-dropping as off_floor in the objects sweep. The pivot-based
    position only matters for picking a navigable stand-cell; Y is left untouched
    (floor assignment already resolved correctly). Matches a roster fixture to a baked
    door record by name. Returns the number of fixtures repaired."""
    # name -> (door_record, frame) for every baked door across floors.
    door_by_name = {}
    for fl in floors:
        if "error" in fl:
            continue
        frame = fl.get("frame") or {}
        for dr in (fl.get("doors") or []):
            nm = dr.get("name")
            if nm and nm not in door_by_name:
                door_by_name[nm] = (dr, frame)

    repaired = 0
    for fx in roster:
        rec_frame = door_by_name.get(fx.get("name"))
        if rec_frame is None:
            continue
        doorway = _door_doorway_xz(rec_frame[0], rec_frame[1])
        if doorway is None:
            continue
        pos = fx.get("position")
        if not pos or len(pos) < 3:
            continue
        # Only correct when the pivot actually moved (avoid churn on doors whose
        # fixture already sits on the doorway).
        if abs(pos[0] - doorway[0]) < 1e-3 and abs(pos[2] - doorway[1]) < 1e-3:
            continue
        fx["position"] = [round(doorway[0], 4), round(pos[1], 4), round(doorway[1], 4)]
        repaired += 1
    return repaired


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


# Floor / ceiling slabs (SM_Floor_*, SM_Ceiling_*). These are the storey-cap
# meshes: a thin horizontal slab spanning a whole room. You WALK ON floors and
# never collide with a ceiling as a navigation obstacle, so neither belongs in
# the navigation blocker grid — they only ever appear there as phantom blockers
# where a slab's boundary triangles rasterize at room edges (the very phantoms
# the door/archway carves were compensating for). Walkability comes from the
# separate WalkableSurfaces pipeline, where these same meshes legitimately act
# as the walkable floor (e.g. SM_Ceiling_Hall doubles as the upstairs hall
# floor). Excluding them from blocked_bm therefore loses nothing and removes the
# phantoms. See [[project-navigation-doorway-capsule-clearance-2026-06-18]].
def _is_floor_or_ceiling_slab(record):
    text = f"{record.get('Name', '')} {record.get('GameObjectName', '')} {record.get('Path', '')}".lower()
    return "sm_floor" in text or "sm_ceiling" in text


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
    # A container has no walk-through threshold or freed cells — you open it in place to reach
    # an item inside, never walk through it. It carries only operable_from_cells; the consumer
    # tags it by the destination rule (the target IS this door) and opens it in range, like any
    # object. See [[project_navigation_container_open_on_interact]].
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
        "open_blocked_cells": [],
        "open_blocked_count": 0,
        "panel_dilated_cells": [],
        "operable_from_cells": operable,
        "operable_from_count": len(operable),
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
    for m in mesh_colliders:
        if not _is_solid_blocker(m):
            continue
        # Floor / ceiling slabs are never navigation obstacles (you walk on
        # floors; ceilings are above you). Their only contribution to blocked_bm
        # was phantom boundary blockers at room edges. Skip them here; their
        # walkable role is handled by the WalkableSurfaces pipeline.
        if _is_floor_or_ceiling_slab(m):
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
        #
        # Doorframes (with or without an associated door) contribute their jamb
        # posts here like any other mesh. There is no special archway carve: an
        # open archway is the gap between two post columns, handled by ordinary
        # dilation/clearance. A real door's passability is governed by the
        # per-door freed_cells/open_blocked_cells state machine.
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
    # floor band correctly where fixed-Y slice planes missed them. These cells
    # (and their dilation halo) are governed end-to-end by the per-door state
    # machine: blocked while closed, freed via freed_cells when the door opens.
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

    # No dilation carves. Doorway passability is governed entirely by the per-door
    # freed_cells/open_blocked_cells state machine (below); open archways are just
    # the gap between two jamb-post columns. The former door-position carve (a disc
    # of un-dilation around each Doors_* interactable) and open-archway carve (the
    # door-less doorframe bbox un-dilation) were heuristic compensations for two
    # things now fixed at the source: floor/ceiling slab phantoms (no longer
    # rasterized as blockers) and closed door panels (owned by the door state
    # machine). With those gone the carves are unnecessary — removing both keeps
    # the inter-floor stair edge, holds the stranded-pocket baseline, and routes
    # all cross-floor/same-floor CLI cases. The carves also un-blocked raw wall
    # cells, which caused the bedroom wall-clip. Reported as door_carves=0 for
    # downstream compatibility. See
    # [[project-navigation-doorway-capsule-clearance-2026-06-18]].
    door_carves = 0

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
            # Also seed from the door ANCHOR's own cell (and its walkable
            # neighbours). A sliding CLOSET with two co-located panels that just
            # trade places — Gym/Bedroom closet inner doors — has its inner
            # panel's closed-dilation sitting OFF the doorway opening, so the
            # panel-only seeds above never reach the throat and the inner doorway
            # bakes pinched (96/107 sweep freezes wedged at Doors_Gym_ClosetInner).
            # The anchor sits IN the opening by construction. Seeds still have to
            # satisfy the same spread predicate (walkable, not a foreign raw
            # blocker), so adding them can only EXTEND the flood through the same
            # door's own opening — never past a wall — leaving every door the
            # panel seeds already handled unchanged. See
            # [[project-navigation-doorway-capsule-clearance-2026-06-18]].
            for sx, sz in ((cx, cz), (cx + 1, cz), (cx - 1, cz),
                           (cx, cz + 1), (cx, cz - 1)):
                if 0 <= sx < nx and 0 <= sz < nz and walkable_bm[sx][sz] \
                        and not (blocked_bm[sx][sz] and not panel_closed_raw[sx][sz]):
                    seeds.append((sx, sz))
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
                            # Spread through walkable floor, stopping only at a RAW blocker (an
                            # actual wall-mesh cell), NOT at mere dilation. The doorway THROAT is
                            # walkable floor the surrounding wall jambs DILATE shut; a dilation-stop
                            # gate collapsed the threshold to a 1-wide diagonal the 0.4m capsule
                            # can't follow (follower jams at the wall corner). Blocking on raw
                            # blockers lets the BFS fill the throat's full width while a real wall
                            # still stops it; the disc bound + raw-wall stop prevent leaking into a
                            # neighbour room (the Doors_Office→SM_Walls_Hall1 leak the old gate
                            # guarded). See [[project-navigation-doorway-capsule-clearance-2026-06-18]].
                            if not walkable_bm[tx][tz]:
                                continue
                            if blocked_bm[tx][tz] and not panel_closed_raw[tx][tz]:
                                continue
                            reach.add((tx, tz))
                            queue.append((tx, tz))
                # Threshold cells = reachable cells that are dilation-blocked
                # (so opening the door is what gives them passage). Cells
                # already navigable don't need to be re-added.
                for (jx, jz) in reach:
                    if dilated[jx][jz]:
                        threshold_cells.append((jx, jz))

        # Drop threshold cells the OPEN panel sweeps into. A swing door's leaf
        # rotates ACROSS its own doorway, so 11-13 of these threshold cells land
        # inside the open-panel footprint. The main freed-cell loop already
        # excludes panel_open_dil, but threshold cells bypass that gate (they're
        # exempt from door_open_dil to re-open the wall gap) — which re-admitted
        # exactly the cells the swung-open leaf occupies, so the follower routed
        # through them and WEDGED on the open panel (the dominant "state-door"
        # stall once the freeze was fixed). Subtract the open-panel footprint:
        # those cells are not passable when the door is open, whatever the wall
        # gap does. See [[project-navigation-door-wedge-2026-06-16]].
        threshold_cells = [c for c in threshold_cells if not panel_open_dil[c[0]][c[1]]]

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

        # CLOSET INTERIOR RECOVERY (the capsule-width walk-in channel). A closet is a
        # NARROW walk-in space (~0.6-1.0m deep) that the radius-2 capsule dilation
        # (DILATE_CELLS = CAPSULE_R/CELL = 2, i.e. 0.8m total width) erodes to ZERO
        # navigable cells — every interior cell is within 0.4m of a wall. But the player
        # physically walks INTO these closets and the objects inside (shelves, hangers,
        # boxes) occupy navigable interior space: the game's EFFECTIVE navigation radius
        # is ~0.4m (velocity-driven rigidbody wall-SLIDING lets the player squeeze gaps
        # narrower than the 0.8m geometric capsule — same reason the player uses the 1.0m
        # house doors). So a closet interior is passable at the effective radius even
        # though radius-2 dilation says otherwise. See
        # [[project_navigation_closet_interior_eroded_2026_06_30]] and
        # [[project-navigation-capsule-radius-groundtruth-2026-05-29]].
        #
        # Recover the closet's enclosed walkable pocket as door-open navigable (added to
        # freed_cells, gated on THIS door being open exactly like freed_cells/threshold,
        # so consumers OR them in unchanged and nested Inner/Outer closets compose via the
        # existing per-door overlay). Bounds that keep it safe:
        #   - Flood only WALKABLE, NON-raw-wall cells (never un-block a wall mesh -> no
        #     wall-clip, the bf32c87 bedroom regression) that are NOT already navigable
        #     (the pocket IS the dilation-eroded floor; stopping at 'N' keeps it from
        #     escaping into the open house through the doorway).
        #   - CAP at CLOSET_INTERIOR_CELL_CAP: a real closet interior is small; an
        #     unbounded flood leaks ~1273 cells along the connected wall-dilation web, so
        #     a component over the cap is a leak and is discarded.
        #   - RESTRICTED to closet doors by name ('Closet'): only closets are the narrow
        #     walk-ins; other passage doors (Office/Bathroom/Bedroom/Gym_Hall) open into
        #     full rooms that are already navigable and must keep radius-2 routing.
        interior_recovered = []
        door_name_for_closet = (door_rec.get("Name") or "")
        if (not is_container) and ("Closet" in door_name_for_closet):
            from collections import deque as _deque
            # Seed from eroded-interior cells (walkable, not raw-blocked, not navigable)
            # in a box around the door's throat, then flood each connected component
            # bounded by raw walls and the navigable region. The throat box keeps the
            # search local to this door's opening.
            seed_box = [(c[0], c[1]) for c in threshold_cells] + list(freed_set)
            recovered_set = set()
            visited = set()
            if seed_box:
                bxs = [c[0] for c in seed_box]
                bzs = [c[1] for c in seed_box]
                pad = CLOSET_INTERIOR_SEARCH_PAD
                rx0 = max(0, min(bxs) - pad); rx1 = min(nx, max(bxs) + pad + 1)
                rz0 = max(0, min(bzs) - pad); rz1 = min(nz, max(bzs) + pad + 1)
                for sx in range(rx0, rx1):
                    for sz in range(rz0, rz1):
                        if (sx, sz) in visited:
                            continue
                        if not walkable_bm[sx][sz] or blocked_bm[sx][sz] \
                                or navigable_bm[sx][sz]:
                            continue
                        # Flood this connected eroded-floor component.
                        comp = {(sx, sz)}
                        cq = _deque([(sx, sz)])
                        leaked = False
                        while cq:
                            cx2, cz2 = cq.popleft()
                            for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                                ax, az = cx2 + dx, cz2 + dz
                                if ax < 0 or ax >= nx or az < 0 or az >= nz:
                                    continue
                                if (ax, az) in comp:
                                    continue
                                if not walkable_bm[ax][az] or blocked_bm[ax][az] \
                                        or navigable_bm[ax][az]:
                                    continue
                                comp.add((ax, az))
                                cq.append((ax, az))
                                if len(comp) > CLOSET_INTERIOR_CELL_CAP:
                                    leaked = True
                                    break
                            if leaked:
                                break
                        visited |= comp
                        if not leaked:
                            recovered_set |= comp
            # BRIDGE the recovered interior to the door's throat across sealed doorways.
            # A closet doorway (and internal dividers — shelves/partitions) is a PHANTOM
            # SEAL: the mesh rasterizes over walkable floor (asymmetric export, same class
            # as the bedroom doorway), so the eroded interior splits into several pockets
            # separated by a few walkable-under-wall cells, and from the freed/threshold
            # throat (which is wired to the house). Without bridging, each pocket is a
            # NAVIGABLE ISLAND the follower can never route into.
            #
            # Connect them with straight walkable corridors. Starting from the throat
            # (already house-connected), repeatedly take the interior cell-component
            # nearest the connected set and carve the shortest straight corridor (Bresenham
            # over REAL walkable floor only) between the nearest connected/disconnected cell
            # pair, then absorb that component. Each corridor is short (a seal is ~1-6
            # cells), 1-wide, stays on real floor (so it punches a phantom seal, never
            # crosses a void/neighbour room), and runs between two fixed known-good
            # endpoints, so it cannot sprawl the way an open flood does. Iterating absorbs
            # internal dividers so the WHOLE interior ends up house-reachable, not just the
            # nearest pocket. See [[project_navigation_closet_interior_eroded_2026_06_30]].
            def _straight_corridor(a, b):
                """4-connected (orthogonal, L-shaped) corridor of cells from a to b;
                None if it leaves walkable floor. ORTHOGONAL, not Bresenham diagonal:
                the planner is 8-connected with CORNER-CUT PREVENTION (a diagonal step
                needs both flanking orthogonal cells clear), so a diagonal corridor
                through a seal whose flanks are walls is rejected at route time and the
                pocket stays unreachable. A 4-connected staircase is always traversable.
                Step the long axis first, then the short axis."""
                (cx0, cz0), (cx1, cz1) = a, b
                pts = []
                cx, cz = cx0, cz0
                # Order axes so the longer run goes first (shorter visible seam).
                if abs(cx1 - cx0) >= abs(cz1 - cz0):
                    axes = (('x', cx1), ('z', cz1))
                else:
                    axes = (('z', cz1), ('x', cx1))
                pts.append((cx, cz))
                for axis, target in axes:
                    while (cx if axis == 'x' else cz) != target:
                        step = 1 if target > (cx if axis == 'x' else cz) else -1
                        if axis == 'x':
                            cx += step
                        else:
                            cz += step
                        if not walkable_bm[cx][cz]:
                            return None
                        pts.append((cx, cz))
                return pts

            bridge_cells = set()
            # Seed the connected set with ONLY the throat cells genuinely wired to the
            # house: flood the door-open set (freed ∪ threshold) and keep the component
            # that touches static-'N' navigable floor. The throat can itself be a few
            # disconnected fragments; bridging a pocket to a stranded throat fragment
            # would keep an unreachable pocket (the Office_Closet left arm bug). Seeding
            # from the house-wired component guarantees every kept pocket is reachable.
            throat_all = set(threshold_cells) | set(freed_set)
            connected = set()
            seed_q = _deque()
            for tc in throat_all:
                for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ax, az = tc[0] + dx, tc[1] + dz
                    if 0 <= ax < nx and 0 <= az < nz and navigable_bm[ax][az] \
                            and tc not in connected:
                        connected.add(tc); seed_q.append(tc)
            while seed_q:
                cx2, cz2 = seed_q.popleft()
                for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ax, az = cx2 + dx, cz2 + dz
                    if (ax, az) in throat_all and (ax, az) not in connected:
                        connected.add((ax, az)); seed_q.append((ax, az))
            remaining = set(recovered_set)
            # Cap the bridging work; a closet has only a handful of internal pockets.
            for _ in range(16):
                if not remaining or not connected:
                    break
                # Find the closest connected/remaining cell pair (Manhattan).
                best = None; best_d = None
                for cc in connected:
                    for rc in remaining:
                        dman = abs(cc[0] - rc[0]) + abs(cc[1] - rc[1])
                        if best_d is None or dman < best_d:
                            best_d = dman; best = (cc, rc)
                if best is None:
                    break
                corridor = _straight_corridor(best[0], best[1])
                if corridor is None:
                    # No real-floor corridor to the nearest pocket; it's genuinely
                    # walled off (not a phantom seal). Stop — the island invariant
                    # will flag any interior left unconnected.
                    break
                # Absorb the corridor + the pocket it just reached (flood the reached
                # cell's interior component) into the connected set.
                bridge_cells.update(corridor)
                connected.update(corridor)
                reached = best[1]
                comp = {reached}; cq = _deque([reached])
                while cq:
                    px, pz = cq.popleft()
                    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        ax, az = px + dx, pz + dz
                        if (ax, az) in remaining and (ax, az) not in comp:
                            comp.add((ax, az)); cq.append((ax, az))
                connected.update(comp)
                remaining -= comp
            # Drop interior pockets the bridge could NOT reach: they're walled off from
            # this door by REAL geometry (a separate closet section reached by a different
            # route, or a genuinely sealed void the pad-12 search box happened to catch),
            # not a phantom doorway seal. Keeping them would re-create navigable islands.
            # Recover only the cells now wired to the house through this door.
            recovered_set = (recovered_set - remaining) | bridge_cells
            interior_recovered = sorted([list(c) for c in recovered_set])
            # Add to freed so consumers make them navigable when this door opens.
            for c in recovered_set:
                if list(c) not in freed:
                    freed.append(list(c))
            freed.sort()

        # OPEN-BLOCKED cells = the door's open-leaf footprint: navigable floor (door CLOSED) the
        # leaf sweeps into when OPEN. Mirror of freed_cells (closed:blocked->open:navigable);
        # this is closed:navigable->open:blocked. The consumer removes these from navigable when
        # the door is open so no route runs through the swung leaf (the swing-leaf wedge). Raw
        # leaf footprint; the planner's clearance handles the body margin. See
        # [[project-navigation-doorway-capsule-clearance-2026-06-18]].
        open_blocked = sorted(
            [ix, iz]
            for ix in range(nx) for iz in range(nz)
            if panel_open_raw[ix][iz] and navigable_bm[ix][iz]
        )
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
            # Passage (walked-through) vs container (opened-in-place). Carried into
            # the report so post-bake invariants don't have to re-derive it from the
            # name — the scene-path classifier (_is_container_door) is the authority,
            # and a container that happened to free a few cells on the passage path
            # must still be excluded from the doorway capsule-width check.
            "is_passage": not is_container,
            "panel_count": len(door_rec.get("Panels", [])),
            "closed_cells": sum(sum(row) for row in panel_closed_dil),
            "open_cells": sum(sum(row) for row in panel_open_dil),
            "threshold_cells": len(threshold_cells),
            "threshold_cells_list": threshold_cells_list,
            # Closet interior cells recovered at the effective (sub-radius-2) clearance
            # the game permits — a subset of freed_cells, emitted separately so the
            # post-bake invariants can exempt them from the radius-2 dilation checks
            # (these cells ARE radius-2 dilation-blocked by design; the player still
            # fits). Empty for non-closet doors. See
            # [[project_navigation_closet_interior_eroded_2026_06_30]].
            "interior_recovered_cells": interior_recovered,
            "interior_recovered_count": len(interior_recovered),
            "freed_cells": freed,
            "freed_count": len(freed),
            # Open-leaf footprint: navigable-when-closed cells the leaf fills when open. Mirror
            # of freed_cells; the consumer removes these from navigable when the door is open.
            "open_blocked_cells": open_blocked,
            "open_blocked_count": len(open_blocked),
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
            # Closet interior cells are radius-2 dilation-blocked BY DESIGN — they're the
            # narrow walk-in floor the capsule clears at the game's effective radius, not
            # the geometric one. Exempt them like threshold cells. See
            # [[project_navigation_closet_interior_eroded_2026_06_30]].
            interior_rec = {(c[0], c[1]) for c in door.get("interior_recovered_cells", [])}
            violating = [(c[0], c[1]) for c in door.get("freed_cells", [])
                         if _is_dilated_blocked(c[0], c[1])
                         and (c[0], c[1]) not in own
                         and (c[0], c[1]) not in thresholds
                         and (c[0], c[1]) not in interior_rec]
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

    # 7. CROSS-FLOOR CONNECTIVITY via STAIRS/RAMPS. The walkable floors that the player
    # WALKS between (ground↔upper) must be mutually reachable through stair_ramp edges.
    # This is the invariant that would have caught the staircase being dropped: when
    # /House/Hallway/Stairs (the lone Layer-18 collider) was skipped by the walkable
    # exporter, stair_ramp came out EMPTY, upper and ground were graph-disconnected, and
    # EVERY upstairs↔downstairs route returned no_path — yet the bake reported OK because
    # nothing asserted connectivity. See
    # [[project-navigation-stairs-missing-from-bake-2026-06-15]].
    #
    # SCOPE: only the floors joined by stair_ramp edges. TELEPORTER-only sub-storeys (the
    # crawlspace) connect through a VIRTUAL down-node, not a navigable cell-to-cell bridge,
    # so the planner reaches them by a different mechanism that this cell-flood can't model
    # — their endpoint validity is covered by invariant 5. Don't flag them here.
    real_floors = [f for f in report["floors"] if "error" not in f]
    stair_edges = (edges_doc.get("stair_ramp") or []) if isinstance(edges_doc, dict) else []
    floors_by_label = {f["label"]: f for f in real_floors}

    # Floors that should be walk-connected: any pair that a stair_ramp edge names. If the
    # bake has >1 floor but a real staircase exists in neither, that's the regression.
    stair_floor_set = set()
    for edge in stair_edges:
        if isinstance(edge, dict):
            for label in floors_by_label:
                ep = edge.get(label)
                if isinstance(ep, dict) and isinstance(ep.get("cell"), list) and len(ep["cell"]) >= 2:
                    stair_floor_set.add(label)

    # 7a. Loud structural guard: ground+upper both present but no stair edge joins them.
    walk_floors = {lab for lab in floors_by_label if lab != "crawlspace"}
    if len(walk_floors) > 1 and not (stair_floor_set & walk_floors):
        errors.append(
            f"multi-floor bake has walkable floors {sorted(walk_floors)} but ZERO "
            f"stair_ramp edges connecting them. The staircase was almost certainly "
            f"dropped upstream (e.g. the walkable exporter's SkipMeshLayers excluding "
            f"the stair's layer). Check Export-SceneWalkableSurfaceData.ps1 + "
            f"derive_inter_floor_edges."
        )

    # 7b. Flood check across the stair-joined floors: confirm they're actually mutually
    # reachable from the main component (a stair edge that lands in a stranded pocket
    # would pass 7a but still leave a floor unroutable).
    if len(stair_floor_set) > 1:
        # Doors-OPEN navigable set per floor: static 'N' plus every unlocked door's freed/threshold
        # cells (and state-wall freed cells), since the in-game planner opens any non-locked door on
        # the path. A room reachable only THROUGH a door must count as connected, or the connectivity
        # checks below would false-positive on every closed-door room. Locked doors stay shut.
        _freed_by_floor = {}
        for lab, f in floors_by_label.items():
            extra = set()
            for door in f.get("doors", []):
                if door.get("locked"):
                    continue
                for c in door.get("freed_cells", []) or []:
                    extra.add((c[0], c[1]))
                for c in door.get("threshold_cells_list", []) or []:
                    extra.add((c[0], c[1]))
            for wall in f.get("state_walls", []):
                for c in wall.get("freed_cells", []) or []:
                    extra.add((c[0], c[1]))
            _freed_by_floor[lab] = extra

        def _nav(label, ix, iz):
            f = floors_by_label.get(label)
            if f is None:
                return False
            fr = f["frame"]
            if not (0 <= ix < fr["nx"] and 0 <= iz < fr["nz"]):
                return False
            if f["bitmap_rows"][ix][iz] == 'N':
                return True
            return (ix, iz) in _freed_by_floor.get(label, ())

        bridges = {}
        for edge in stair_edges:
            if not isinstance(edge, dict):
                continue
            ends = []
            for label in floors_by_label:
                ep = edge.get(label)
                if isinstance(ep, dict) and isinstance(ep.get("cell"), list) and len(ep["cell"]) >= 2:
                    ends.append((label, int(ep["cell"][0]), int(ep["cell"][1])))
            for a in ends:
                for b in ends:
                    if a != b:
                        bridges.setdefault(a, []).append(b)

        from collections import deque

        def _flood(seed):
            seen = {seed}
            q = deque([seed])
            reached = {seed[0]}
            while q:
                label, ix, iz = q.popleft()
                for dx in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        if dx == 0 and dz == 0:
                            continue
                        if (label, ix + dx, iz + dz) not in seen and _nav(label, ix + dx, iz + dz):
                            seen.add((label, ix + dx, iz + dz))
                            q.append((label, ix + dx, iz + dz))
                for nb in bridges.get((label, ix, iz), ()):
                    if nb not in seen and _nav(*nb):
                        seen.add(nb)
                        reached.add(nb[0])
                        q.append(nb)
            return reached, seen

        # Seed from the LARGEST component of a stair-joined floor — NOT the first 'N'
        # cell, which can sit in a stranded pocket and under-report connectivity.
        seed_label = max(stair_floor_set,
                         key=lambda lab: sum(r.count('N') for r in floors_by_label[lab]["bitmap_rows"]))
        sf = floors_by_label[seed_label]
        sf_rows = sf["bitmap_rows"]
        nx, nz = sf["frame"]["nx"], sf["frame"]["nz"]
        # Largest within-floor component, found by a plain within-floor flood.
        visited = set()
        best_seed, best_size = None, -1
        for ix in range(nx):
            row = sf_rows[ix]
            for iz in range(nz):
                if row[iz] != 'N' or (ix, iz) in visited:
                    continue
                comp = {(ix, iz)}
                dq = deque([(ix, iz)])
                while dq:
                    cx, cz = dq.popleft()
                    for dx in (-1, 0, 1):
                        for dz in (-1, 0, 1):
                            if (dx or dz) and (cx + dx, cz + dz) not in comp and _nav(seed_label, cx + dx, cz + dz):
                                comp.add((cx + dx, cz + dz))
                                dq.append((cx + dx, cz + dz))
                visited |= comp
                if len(comp) > best_size:
                    best_size, best_seed = len(comp), (seed_label, ix, iz)

        if best_seed is not None:
            reached_floors, reached_cells = _flood(best_seed)
            unreached = stair_floor_set - reached_floors
            if unreached:
                errors.append(
                    f"cross-floor connectivity: stair-joined floor(s) {sorted(unreached)} "
                    f"are UNREACHABLE from the main {seed_label!r} component — the stair "
                    f"edge lands in a stranded pocket. Routes to those floors will "
                    f"return no_path. Check the stair landing cells in "
                    f"derive_inter_floor_edges."
                )

            # STRANDED-POCKET check. The floor-level check above only verifies the MAIN component
            # reaches the other floors — a navigable POCKET disconnected from the main component is
            # invisible to it. That is the regression class where a bake change (e.g. an over-broad
            # carve guard re-sealing a doorway) walls off part of a floor: routes that START in the
            # pocket get no_path even though the floors are "connected" overall. The scene HAS some
            # expected-isolated regions (exterior perimeter strips, locked-content rooms), so a hard
            # error would false-fail the baseline. Instead WARN with the largest pockets and the
            # total stranded-cell count: a regression shows up as a big jump in stranded cells / a
            # large NEW pocket against this baseline. See
            # [[project-navigation-doorway-capsule-clearance-2026-06-18]].
            STRANDED_POCKET_MIN_CELLS = 200
            pocket_report = []
            total_stranded = 0
            for lab in stair_floor_set:
                rows = floors_by_label[lab]["bitmap_rows"]
                lnx = floors_by_label[lab]["frame"]["nx"]
                lnz = floors_by_label[lab]["frame"]["nz"]
                seen_pockets = set()
                for ix in range(lnx):
                    row = rows[ix]
                    for iz in range(lnz):
                        if row[iz] != 'N' or (lab, ix, iz) in reached_cells or (ix, iz) in seen_pockets:
                            continue
                        pocket = {(ix, iz)}
                        dq = deque([(ix, iz)])
                        while dq:
                            cx, cz = dq.popleft()
                            for dx in (-1, 0, 1):
                                for dz in (-1, 0, 1):
                                    nc = (cx + dx, cz + dz)
                                    if (dx or dz) and nc not in pocket and _nav(lab, cx + dx, cz + dz):
                                        pocket.add(nc)
                                        dq.append(nc)
                        seen_pockets |= pocket
                        if len(pocket) >= STRANDED_POCKET_MIN_CELLS:
                            total_stranded += len(pocket)
                            pocket_report.append((len(pocket), lab, (ix, iz)))
            if pocket_report:
                pocket_report.sort(reverse=True)
                top = "; ".join(f"{lab} {n} cells @{cell}" for n, lab, cell in pocket_report[:5])
                warnings.append(
                    f"stranded navigable pockets (doors-open) disconnected from the main "
                    f"stairs-joined component: {len(pocket_report)} pocket(s), {total_stranded} "
                    f"cells total. Largest: {top}. Routes STARTING in a pocket return no_path. "
                    f"Baseline has a few expected-isolated regions (exterior strips, locked rooms); "
                    f"a sudden jump here means a carve/dilation change walled off a doorway."
                )

    # 8. DOORWAY CAPSULE-WIDTH CLEARANCE. Every PASSAGE door must open a throat the
    # follower's capsule can actually thread. The planner's A* threads single cells,
    # but the follower drives a 0.4m-radius capsule whose CENTRE can only occupy cells
    # that are navigable along with their whole capsule-radius neighbourhood. A door
    # whose open throat is a single-file diagonal (navigable cells but no cell with a
    # full capsule-radius clear disc) lets A* plan a route the follower then FREEZES
    # on at the doorway — the dominant real sweep failure (freeze-at-launch, wedged at
    # Doors_Gym_ClosetInner). Nothing asserted this, which is why the carve-removal
    # commits pinched closet throats undetected.
    #
    # Test: erode the doors-open navigable set (static 'N' + this door's freed +
    # threshold − open_blocked) by the capsule radius; the throat is traversable iff
    # ≥1 throat cell survives erosion (admits the capsule centre).
    #
    # WARNING, not error, and only for PASSAGE doors: a CONTAINER door is opened in
    # place, never threaded. A closet whose INTERIOR is itself un-navigable (too small
    # for the capsule — reached by interaction-LOS from the doorway, never entered) is
    # NOT a bake defect; its door correctly yields 0 traversable cells. We can't tell
    # those apart from a genuine pinch in the bake alone, so we surface the count the
    # same way as the stranded-pocket check: a JUMP against baseline flags a
    # regression. Baseline: Doors_Office_Closet + Doors_Bedroom_ClosetRight_Inner
    # (tiny closet interiors). See [[project-navigation-doorway-capsule-clearance-2026-06-18]].
    erode_r = DILATE_CELLS
    erode_disc = [(dx, dz) for dx in range(-erode_r, erode_r + 1)
                  for dz in range(-erode_r, erode_r + 1)
                  if dx * dx + dz * dz <= erode_r * erode_r]
    pinched = []
    island_closets = []
    for floor in report["floors"]:
        if "error" in floor:
            continue
        label = floor["label"]
        rows = floor["bitmap_rows"]
        nx = floor["frame"]["nx"]
        nz = floor["frame"]["nz"]
        for door in floor.get("doors", []):
            # Container doors (fridge/cupboard/iron-cupboard) are opened in place and
            # never threaded — exclude them whether they were emitted operability-only
            # or (because their panel diff happened to free a few cells) on the passage
            # path. The scene-path classifier is the authority, same as the bake's own
            # _is_container_door (passage doors live under /MultiRoom/Doors/).
            if door.get("container_operable_only"):
                continue
            if not door.get("is_passage", True):
                continue
            if door.get("panel_count", 0) == 0:
                continue
            # Closet doors that recovered an interior pocket are enterable at the game's
            # EFFECTIVE navigation radius (the closet-interior recovery in bake_floor; see
            # [[project_navigation_closet_interior_eroded_2026_06_30]]). A closet interior
            # is a narrow walk-in that the radius-2 capsule disc never fits — testing it
            # against the geometric disc would always (correctly) report "pinched", but
            # the player physically walks in (wall-slide squeeze, effective radius ~0.4m),
            # so the recovered pocket is the passing condition, not a radius-2 clear disc.
            # BUT a recovered interior is only useful if it is CONNECTED to the house: the
            # bridge across the doorway seal must actually join it to the throat (which is
            # itself connected to the navigable house), else the closet is a navigable
            # ISLAND the follower can never route into. Assert that connectivity here.
            if door.get("interior_recovered_count", 0) > 0:
                interior = {(c[0], c[1]) for c in door.get("interior_recovered_cells", [])}
                open_set = set(interior)
                open_set |= {(c[0], c[1]) for c in (door.get("freed_cells") or [])}
                open_set |= {(c[0], c[1]) for c in (door.get("threshold_cells_list") or [])}
                for c in (door.get("open_blocked_cells") or []):
                    open_set.discard((c[0], c[1]))
                # Flood the door-open set FROM the house (static-'N' cells adjacent to it),
                # 4-connected to match the planner (8-connected but with corner-cut
                # prevention, so a diagonal-only link is not a real route). EVERY recovered
                # interior cell must be reached, not just one: a closet split by internal
                # dividers can have the doorway pocket reachable while a back pocket stays
                # a stranded island (the Office_Closet left-arm bug). Require full coverage.
                from collections import deque as _dq
                seen_i = set()
                qi = _dq()
                for cell in open_set:
                    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        ax, az = cell[0] + dx, cell[1] + dz
                        if 0 <= ax < nx and 0 <= az < nz and rows[ax][az] == 'N' \
                                and cell not in seen_i:
                            seen_i.add(cell); qi.append(cell)
                while qi:
                    cx2, cz2 = qi.popleft()
                    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        ax, az = cx2 + dx, cz2 + dz
                        if (ax, az) in open_set and (ax, az) not in seen_i:
                            seen_i.add((ax, az)); qi.append((ax, az))
                unreached = interior - seen_i
                if unreached:
                    island_closets.append(
                        f"{label}/{door.get('name')!r} ({len(unreached)}/{len(interior)} "
                        f"interior cells unreachable)")
                continue
            throat = [(c[0], c[1]) for c in (door.get("freed_cells") or [])]
            throat += [(c[0], c[1]) for c in (door.get("threshold_cells_list") or [])]
            if not throat:
                # A passage door with panels but no freed/threshold throat at all is
                # already caught by invariant 6 (0 freed_cells); don't double-report.
                continue
            open_nav = set()
            xs = [c[0] for c in throat]
            zs = [c[1] for c in throat]
            pad = erode_r + 2
            x0 = max(0, min(xs) - pad); x1 = min(nx, max(xs) + pad + 1)
            z0 = max(0, min(zs) - pad); z1 = min(nz, max(zs) + pad + 1)
            for ix in range(x0, x1):
                for iz in range(z0, z1):
                    if rows[ix][iz] == 'N':
                        open_nav.add((ix, iz))
            open_nav |= set(throat)
            for c in (door.get("open_blocked_cells") or []):
                open_nav.discard((c[0], c[1]))
            # Capsule centre fits at a throat cell iff its whole erosion disc is
            # navigable in the doors-open set.
            def _fits(ix, iz):
                for dx, dz in erode_disc:
                    if (ix + dx, iz + dz) not in open_nav:
                        return False
                return True
            if not any(_fits(ix, iz) for (ix, iz) in throat):
                pinched.append(f"{label}/{door.get('name')!r}")
    # Baseline = doors whose CLOSET INTERIOR is itself too small for the capsule, so 0
    # traversable cells is correct (the item inside is reached by interaction-LOS from
    # the doorway, never by entering). A door OUTSIDE this set with a pinched throat is a
    # fresh regression and must fail the bake; a baseline door staying pinched only warns.
    PINCHED_THROAT_BASELINE = {
        "upper/'Doors_Bedroom_ClosetRight_Inner'",
    }
    new_pinched = [p for p in pinched if p not in PINCHED_THROAT_BASELINE]
    if new_pinched:
        errors.append(
            f"doorway capsule-width: {len(new_pinched)} PASSAGE door(s) open a throat "
            f"too narrow for the capsule centre (single-file — A* threads it, the follower "
            f"freezes at the doorway): {sorted(new_pinched)}. A carve/dilation/door-throat "
            f"change pinched the opening. Restore a capsule-diameter channel (the door's "
            f"threshold BFS / anchor seed) or, if the interior is genuinely un-enterable, "
            f"add it to PINCHED_THROAT_BASELINE with a justification."
        )
    if island_closets:
        errors.append(
            f"closet interior island: {len(island_closets)} closet door(s) recovered an "
            f"interior pocket that is NOT connected to the house in the door-open set "
            f"(the doorway-seal bridge failed to join the interior to the throat): "
            f"{sorted(island_closets)}. The follower could never route in. Fix the bridge "
            f"in bake_floor's closet-interior recovery (the throat<->interior corridor "
            f"across the phantom doorway seal). See "
            f"[[project_navigation_closet_interior_eroded_2026_06_30]]."
        )
    expected_still_pinched = [p for p in pinched if p in PINCHED_THROAT_BASELINE]
    if expected_still_pinched:
        warnings.append(
            f"doorway capsule-width: {len(expected_still_pinched)} baseline door(s) still "
            f"have an un-threadable throat (closet interiors too small for the capsule — "
            f"reached by interaction-LOS from the doorway, never entered): "
            f"{sorted(expected_still_pinched)}. Expected; tracked so a planner goal-cell "
            f"fix can clear them later."
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

    # Door fixtures carry the door PIVOT, offset from the doorway and often over a
    # non-navigable cell, so the objects sweep mis-classifies them off_floor. Snap each
    # door fixture's XZ onto its baked doorway centroid (all door records now exist in
    # report["floors"]). Runs before the JSON write so the roster ships repaired.
    door_fixups = repair_door_fixture_positions(report["fixtures"], report["floors"])
    if door_fixups:
        print(f"Repaired {door_fixups} door fixture position(s) onto their doorway centroid")

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
