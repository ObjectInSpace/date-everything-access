# Design: Measurement-based navigation map (offline, UniqueId-joined)

**Status: DESIGN ONLY (2026-06-15, revised twice). Not built.** Decision on whether/when to build
is deferred.

**TWO corrections to my earlier overstatements (recorded so the scope stays honest):**
1. **Runtime collider sampling — REJECTED.** An early draft proposed measuring `collider.bounds`
   live; wrong (captures a moment not authored state; AABB < the export's decoded `LocalShape`;
   can't model door open/closed). Pipeline stays fully offline. (See "Why offline".)
2. **I inverted the cause of the artifact history.** I claimed the per-cell column rasterizer
   (`_rasterize_columns_into` / `_column_blocks_floor`) was "the root of the whole artifact history
   (top-lip/void-plug/wall-like-victim gates)" and should be retired for AABBs. That is BACKWARDS.
   The bake's own comments (`bake_navigable_region.py:657-665`) show those gates compensated for the
   PREVIOUS method (fixed-Y AABB slicing); the column rasterizer is what RETIRED them ("paints each
   collider only where its real triangles sit, so those phantoms never appear and no gate is
   needed"). **The column/triangle decode is the GOOD part — the offline measurement — and stays.**
   Swapping it for AABBs would be a regression. This whole design therefore KEEPS the rasterizer
   and is much narrower than the first draft implied (see "Honest scope" below).

## Context

The bake derives the navigable map from object occupancy. The question raised: can we use the
UniqueId to look up authoritative per-object geometry in the OFFLINE asset export, and keep the
whole pipeline offline — rather than sampling live colliders at runtime?

**Answer: yes, and offline is strictly better.** The blockers export
(`thirdpersongreybox-blockers.json`) ALREADY carries, per object, from the authored scene:
`GameObjectId` + `Path` (joinable to the interactables export's `UniqueId`), `Bounds3D`,
`WorldCenter/Position/Rotation/Scale`, `BottomY/TopY`, **`LocalShape`** and **`Footprint`** (the
decoded true collider geometry — not just an AABB), and state flags (`IsActive`, `Enabled`,
`IsTrigger`, `IsDoorConnector`). Plus separate `Doors` and `StateWalls` records that model the
OPEN/closed variants explicitly. (1004 MeshColliders, 1307 NavigationBlockers in the current
export.)

## Why offline (rejecting the runtime-sampling draft)

Runtime `collider.bounds` was a mistake to propose, for three reasons the user identified:
1. **A moment, not the canonical state.** Runtime captures whatever was true the frame you
   sampled — a door mid-swing, an object a scripted event relocated. The export reflects the
   AUTHORED scene (rest state), deterministic and reproducible across re-bakes. For a map, authored
   state is the correct ground truth.
2. **`LocalShape` > `bounds`.** Runtime bounds is an AABB (over-claims rotated/concave shapes).
   The export already decodes the TRUE collider shape (`LocalShape`/`Footprint` segments). So
   offline is HIGHER fidelity, not a compromise — the "exact for boxes, approximate for concave"
   ceiling of the runtime idea largely dissolves.
3. **Door/state geometry is handled correctly only offline.** A live door has ONE bounds (its
   current state). The export gives authored (closed) geometry, and the bake ALREADY models the
   open state via `Doors` (freed-cells, swing footprints) and `StateWalls` (freed-cells, e.g.
   DresserWall, and the planned ClosetRug). Representing BOTH states as explicit data is the right
   pattern; runtime sampling structurally can't (it sees one state per visit). This is the
   known-limitation class the project already chose to solve offline.

## The UniqueId's actual role here

NOT a runtime measurement key. It is the **stable OFFLINE join key**: it lets the pipeline assert
"this roster fixture == this blocker record == this collider geometry" across the export files and
across re-bakes, without name collisions (42 shared names) or rig-origin position ambiguity. The
interactables export now carries `UniqueId` + `GameObjectId`; the blockers export carries
`GameObjectId` + `Path`. Joining on `GameObjectId` (and `Path` as a cross-check) gives each
UniqueId its authored collider geometry. The measurement loop is the EXPORTER + BAKE, both offline.

## What "measurement-based by elimination" means offline

The bake ALREADY reads decoded authored geometry (it rasterizes the export's per-cell columns /
`LocalShape`, not live state) and ALREADY has the elimination shape
(`navigable = floor-cells AND NOT dilate(solid-cells)`). So this is an EVOLUTION, not a rewrite.
The improvement the UniqueId join enables:
- **Per-object authoritative occupancy:** every solid (walls/floors/ceilings/doors/furniture —
  all addressable, all with UniqueIds and collider records) contributes its exact authored
  `LocalShape`, joined reliably. No name/position guessing about which record is which object.
- **Open space = complement:** floor surfaces (measured offline) minus solids (measured offline)
  minus capsule clearance. We never sample-guess where walls are.
- **State variants stay explicit:** doors/drawers/rug contribute closed geometry as solid +
  open-state freed-cells via the existing `Doors`/`StateWalls` records, keyed by UniqueId so the
  variant attaches to the right object.

## Honest scope: what the UniqueId join actually improves (and what it does NOT)
The bake's geometry pipeline is ALREADY measurement-based and mostly clean. `_is_solid_blocker`
already decides solid/not-solid from export flags (`IsTrigger`/`IsDoorConnector`/`Enabled`/
`IsActive`) — no guessing. The column rasterizer already paints true shape. So the join is a
NARROW cleanup, not a re-founding. Specifically it could replace the remaining NAME-BASED
CLASSIFICATION helpers with UniqueId-anchored role tags:
- `_is_structural_mesh` (`bake_navigable_region.py:595-614`) — substring-matches `/walls/`,
  `sm_wall`, `doorframe`, `fence`, `/hallway/stairs` to pick wall-vs-furniture rasterization. A
  per-object role tag keyed by UniqueId removes the substring fragility.
- `_is_doorframe` (same idea), and the degenerate rig-origin position correction (the joined
  collider's authored `WorldCenter` is the true location — no heuristic).

NOT retired (all correct, keep): the `.asset` mesh decode / per-cell column rasterizer (it IS the
offline measurement), `_is_solid_blocker`, the floors/dilation/clearance pipeline, the
Doors/StateWalls state machinery. **Net: this is a classification-robustness improvement, not a new
map engine. Worth doing only if the name-based classification is actually causing misattribution
in practice — verify that first; it may not be worth the churn.**

## Open decisions before building
- **Join robustness:** GameObjectId is the primary join; confirm it's identical between the
  interactables and blockers exports for every object (both come from the same scene YAML, so it
  should be). UniqueId is the cross-check / the durable key carried into the bake + runtime.
- **Concave fidelity:** `LocalShape` is already decoded — decide where the bake uses full shape vs
  the AABB shortcut (it already does per-cell columns for meshes; AABB fallback for primitives).
- **Validation:** diff a UniqueId-joined bake against the current bake per floor; reconcile
  object-by-object (the join makes this exact). Pilot on the crawlspace (70x76, 4550 navigable,
  0 blockers, freshly built) before the whole house.

## Smaller UniqueId wins (same offline join key, lower effort)
- Persistent examine history (`_examinedObjectKeys` -> persisted UniqueIds; closes the
  `AccessibilityWatcher.cs:3695` reload-loses-examined gap). [runtime persistence, not geometry]
- Per-object mod state (hide/pin/flag picker entries) keyed by UniqueId.
- Exact cross-run sweep diffing by UniqueId (manifest already carries it).
- Collapse the 4-key fuzzy Roomers/objective match (`AccessibilityWatcher.cs:2414-2455`) to one
  exact id comparison.

## Recommendation
Keep the pipeline offline. Treat the UniqueId as the join key that makes the offline per-object
geometry authoritative, not as a runtime probe. Pilot the UniqueId-joined elimination bake on the
crawlspace, diff against the current sampled bake, scale only if it agrees or improves.
