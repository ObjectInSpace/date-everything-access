# Scope: exact mesh LOS via triangle/hull export

**Status: EXPORT HALF BUILT (2026-06-15). Raycaster half NOT built.** User said "we already know we
need the export, just do it" — so the Step-0 measurement gate was SKIPPED and the exporter side was
implemented directly.

## What was built (exporter side)
`Export-SceneBlockerData.ps1`: new params `EmitMeshLosGeometry` (default true) +
`MeshLosFootprintFillThreshold` (0.5). In `Get-MeshColliderRecord`, mesh blockers whose 2D footprint
fills < threshold of their AABB cross-section (the box meaningfully over-claims) get a
`Footprint.LosGeometry` field: `ConvexHull3D` (world verts) for convex meshes, `Triangles` (flat
world [x,y,z...] per tri) for concave. Flat walls/slabs (footprint ~= AABB) emit nothing and stay
AABB-tested. The AABB (`Bounds3D`) is still emitted for everyone as the cheap pre-reject + fallback.
- Result: **74 of 1004 mesh blockers** got geometry (9 ConvexHull3D, 65 Triangles) — more than the
  ~32 the 2D-only proxy estimated, because the real divergence check caught more.
- Cost: LosGeometry adds only **~2.5 MB** to the compact JSON (44.4 -> 46.9 MB). The headline 161 MB
  file is PRE-EXISTING pretty-print whitespace overhead (ConvertTo-Json indentation), NOT this
  change — out of scope; compact form is 47 MB. 90% of LOS verts come from 7 high-poly meshes
  (Yacht, 4 Plants, Treadmill, Keyboard, Safe); left in for now since total cost is negligible.
- Regression-checked: NavigationBlockers 1307 / MeshColliders 1004 unchanged; 0 malformed
  LosGeometry; sample triangles verified in-AABB world space.

## Raycaster half — BUILT + VERIFIED (2026-06-15)
`los_geometry.py`: added `ray_triangle` (Moller-Trumbore) + `ray_triangles`; new collider kind
`mesh_tris` loaded from `Footprint.LosGeometry`; `ray_collider` dispatches to the exact triangle
test for those, AABB for the rest. The AABB stays the broad-phase pre-reject in `first_hit` and the
fallback for meshes without LosGeometry. Both consumers (validate_los parity ray + planner
`cell_has_los`) use the one upgraded tester, so they can't drift. `point_inside_collider` treats
`mesh_tris` like a mesh (never origin-skipped) — preserves the parity invariant.

Exporter simplification: convex AND concave divergent meshes now both emit `Kind:"Triangles"`
(exact triangle soup is correct for either; the 9 convex ones are small, so a separate hull
encoding wasn't worth it). 74 meshes get geometry.

**Verification (all green):**
- `validate_los.py` parity: **349/349 (100%)**, `offline-blocks/game-clears: 0` (no more
  over-block on the probe rays), `offline-leaks/game-blocks: 0` (no dangerous missing occluder).
- Direct AABB-vs-triangle diff: for **8 of 74** meshes a ray through the AABB center that the box
  blocks now correctly CLEARS with triangles (the over-block this fixes); the other 66 are solid
  through center and stay blocked. Confirms the exact test engages and only REMOVES false
  occlusions.
- Offline planner sanity: sweep nodes 605 / 934 fixtures / 5 off-floor — unchanged (no regression).
- LOS payload ~2.5 MB compact; non-LOS data counts unchanged (1307/1004).

NOT re-run in-game. The win (recovered standpoints on rays that thread mesh gaps) shows up in a
live coverage sweep; offline parity + the AABB/triangle diff prove the mechanism is correct and
safe.

---
## Original scope notes (retained for context)

Requested because the offline LOS raycaster treats mesh colliders as AABBs (over-blocks), and the
user is seeing many in-game `no_path` routes.

## The gap (confirmed against code + data)

`los_geometry.py` ray-tests Box/Sphere/Capsule colliders EXACTLY (303 of them) but treats all
**1004 MeshColliders as their AABB** (docstring line 24-26: "Mesh colliders as AABB — conservative,
over-blocks, never invents LOS"). So offline LOS is conservative: it reports "no sightline" on rays
that the game's real ray threads through a mesh gap (under a table, between railing posts, through
an archway). That eliminates valid goal cells.

## Why this can show up as `no_path` (the user's symptom) — but might not be the (whole) cause

In `plan_object_route.plan()` the LOS filter runs FIRST (`filter_goals_by_los`, line ~1364),
narrowing goal cells to those with line-of-sight; A* then routes to ONLY those (line ~1389). So
mesh-AABB over-blocking cascades two ways:
- eliminates ALL goal cells -> `no_los`;
- eliminates the REACHABLE goal cells, leaving only unreachable ones -> A* fails -> **`no_path`**.

So the triangle export is a PLAUSIBLE contributor to the `no_path` symptom — but `no_path` is also
produced by pure routing failures (disconnected bake regions, the recent crawlspace/exporter/floor
changes, start-cell resolution). **Do NOT assume LOS is the cause. Step 0 below measures it.**

## What the export carries today vs what's needed
Per mesh blocker the export already has: `Bounds3D` (the AABB used now), `LocalShape`
(`MeshGuid`/`VertexCount`/`TriangleCount`/`IsConvex`/local AABB), and `Footprint` (the 2D slice
intersection used by the BAKE). It does NOT carry the 3D triangle vertices, so the raycaster can't
do ray-vs-triangle. The decoder to produce them already exists: `Read-UnityMeshAsset.ps1` (decodes
`.asset` verts+tris, validated to 6 dp) — the exporter already calls it to build `Footprint`.

## Sizing (why "emit all triangles" is the wrong scope)
- 1004 mesh blockers, **565,041 triangles total** (median 172, max 6152). Emitting all as
  world-space triangles = tens of MB JSON + slow ray-vs-triangle.
- But only **~32 mesh blockers** have a 2D footprint filling <50% of their AABB — i.e. only ~32 are
  MEANINGFULLY over-claimed by the box. The other ~970 are near-exact as AABBs (flat walls, slabs,
  compact props) and gain nothing from triangles.
- Convexity: 213 convex / 791 concave. Convex meshes get an exact + cheap CONVEX HULL ray test;
  only concave-and-over-claimed meshes need the full soup.

## Proposed approach (targeted, not blanket)

**Step 0 — MEASURE FIRST (do before building anything):** the next in-game sweep emits `LOS_PROBE`
rays. Run the offline raycaster against those rays TWICE — once with mesh=AABB (today), once with a
prototype mesh=triangle test — and count how many probe rays flip verdict, and how many planner
goal cells / routes that actually changes. Also instrument `plan()` to tag each `no_path` with
whether the LOS filter removed reachable goal cells (LOS-caused) vs A* failed on the full disc
(routing-caused). This says whether triangle export is worth it AND how much of the `no_path` it
explains. The harness (`validate_los.py`, probe rays) already exists.

**Step 1 — emit geometry only where it helps:** in `Export-SceneBlockerData.ps1`, for mesh blockers
where the AABB diverges from true shape (heuristic: footprint area < ~50% of AABB cross-section, OR
unconditionally for the ~32 worst; tune from Step 0), add to the record:
  - convex meshes: `ConvexHull` (the hull vertices — small, exact for convex);
  - concave meshes: `WorldTriangles` (triangle vertices transformed to world space).
Keep emitting `Bounds3D` for everyone (the AABB pre-reject). Gate behind a param so the heavy
fields are opt-in. Near-exact-AABB meshes emit nothing new.

**Step 2 — exact ray test in `los_geometry.py`:** add ray-vs-triangle (Moller-Trumbore) and
ray-vs-convex-hull tests. Resolution order per mesh blocker: AABB pre-reject (cheap, existing) ->
if it has `ConvexHull`/`WorldTriangles`, do the exact test -> else fall back to AABB (today's
behaviour). Both consumers (`validate_los.py` parity ray + planner synthetic-eye `cell_has_los`)
use the same upgraded tester, so they can't drift. Conservative invariant preserved: exact test
only ever REMOVES false occlusions, never invents LOS.

**Step 3 — validate:** re-run the Step-0 probe diff; confirm parity holds (no regressions on the
71/71 probe rays) and measure how many `no_los`/`no_path` routes recover. The UniqueId join (now in
the export) lets us attribute each recovered route to a specific object exactly.

## Caveats / risks
- Conservative today = SAFE (never invents LOS), so this is accuracy/coverage, not a correctness
  fix. The over-block can only cost valid standpoints, never create a bad one.
- The 71/71 probe parity already passed WITH the AABB approximation, so for the rays tested so far
  AABB was sufficient — the win is on rays that thread mesh gaps, whose count is UNKNOWN until Step
  0. Build the export only if Step 0 shows a meaningful number.
- Door/state meshes: emit triangles for the authored (closed) state; open-state is still the
  Doors/StateWalls freed-cell machinery (LOS through an open door is a separate concern).

## Bottom line
Triangle/hull export is a real, correctly-aimed fix for offline LOS over-blocking, scoped to the
~32 meshes that need it rather than all 1004. But `no_path` in-game is not proven to be LOS-caused;
**Step 0 (the probe-ray diff + no_path cause-tagging) is the gate** — it both justifies the export
and rules in/out the user's symptom before any heavy work.
