# Navigation Failure Triage (2026-05-16)

## Context

Previously the sweep harness accepted **proximity-based passes** as success: a door/passage was considered "passed" if the player ended within 2–2.15m of a pushThrough target or waypoint. Investigation revealed this masked real navigation failures — the bathroom1↔hallway pair "passed" while the player was still entirely inside the source zone.

A heuristic was added to [AccessibilityWatcher.cs:2736-2787](AccessibilityWatcher.cs#L2736-L2787): proximity-based passes are now gated by `stillInSourceZone` — they only count if the player has left the source zone. Zone arrival continues to pass as before. `ShouldAdvanceOpenPassageStepByGeometry` was left ungated because it already requires `OpenPassageTraversalStage.DestinationHandoff` (a stronger check).

This exposed **13 door failures** and **17 open passage failures** that were previously passing as false positives.

The user noted these were workarounds added to make tests pass without addressing root causes. The expanded failure set is the surface area we now need to address — and most of the failures point to design-level issues, not single-line bugs.

---

## Cross-Cutting Design Requirement: Autowalk Must Handle Both Door States

Any auto-walk path that crosses a door must work in both states:

- **Door closed**: player walks up to the door, the mod triggers interaction, door opens, player walks through.
- **Door already open** (e.g., already opened by player or by a prior sweep step): player walks straight through the doorway without being blocked by the open panel.

Currently:
- The spawn calculation uses `interactable.transform.right`, which mutates when the door is rotated. Different spawn for "door currently closed" vs "door currently open" → state contamination (see Design Issue D).
- The physics-sampled local-nav map captures a *single snapshot* of door state. Cells near a door are blocked or walkable depending on whether the door happened to be closed or open at sampling time.
- Pre-computed spawn and destination points were computed without accounting for the open-door geometry intrusion (panel sweeping into the source room).

The harness/data needs to validate both directions for both door states. The sweep design currently runs each transition once; it should ideally run it twice (door-closed start and door-open start) and pass only when both succeed.

---

## Design Issues

Each issue lists the failure buckets that point to it. Buckets themselves are described later as supporting evidence.

### Design Issue A — Pass criterion is monolithic

**Affects: every bucket. Highest leverage.**

Current pass logic in `HasForcedTransitionSweepStepSucceeded`:

- Zone arrival → pass
- Proximity to pushThrough / waypoint → pass (gated now by `stillInSourceZone`)

This single model can't represent the different transition kinds:

- **Physical room transitions**: zone arrival is correct (Buckets A, B, C, D, G).
- **Virtual destinations** (`dorian_X`, `_tutorial`, `_1love`): no zone change ever happens; should pass when the named interactable is triggered or the player is within `InteractableObj.InteractionRadius` (Buckets E, F).
- **Partial / long passages**: zone arrival may be too strict when the destination zone has a wide entry corridor that overlaps the source.

**Fix direction**: polymorphic pass criterion per transition type. Add a `TransitionPassKind` (PhysicalRoom, VirtualInteractable, IntraZonePassage) on the nav-graph step; route each kind to its appropriate success check.

### Design Issue B — Spawn formula doesn't model door geometry

**Affects: Buckets A, B, and likely some C/D.**

Spawn = `doorPos + clearDir.normalized * DoorTraversalClearanceDistance (1.4f) + lateral * DoorTraversalLateralOffsetDistance (0.6f)`.

This formula doesn't account for:

- **Swing-arc radius**: bathroom1 has a 1.68m panel. Player capsule has 0.80m radius. Total exclusion zone from pivot: 2.48m. The 1.4m clearance puts the spawn *inside* the open panel's swing area, causing the panel to collide with the player while it rotates open.
- **Narrow doorway corridor width**: bathroom1's doorway has ~0.08m of slack for the capsule. Cell-grid positions don't align with this corridor (see Issue F).
- **Door's `transform.right` mutating at runtime**: when a prior sweep step opened this door, its world-space right axis is no longer `(-1, 0, 0)` — it's been rotated. The spawn formula then produces a different position.

**Fix direction**: compute spawn from door geometry. Take the door's *closed* (or initial) rotation as a stable reference. Calculate the swing arc analytically. Place spawn outside the arc + player-radius buffer. For narrow doorways, align spawn x with the corridor center (the unique walkable x between doorposts).

### Design Issue C — Autowalk doesn't strictly follow local-nav paths

**Affects: Buckets B, G mainly.**

The auto-walk drives the player toward a single next target (`TryResolveAutoWalkMovementTarget`) with lookahead skipping ahead in the path. This works fine for open spaces but fails for:

- **Narrow doorways** where the path must curve precisely (e.g., bathroom1 needs col 5 → col 6 zigzag to clear the left doorpost while staying clear of the open panel).
- **Doorways where the player needs to align before crossing** — the lookahead cuts the corner, putting the player into the doorpost on the way through.

**Fix direction**: detect "narrow corridor" path segments and switch to strict waypoint-by-waypoint following (no lookahead skip). Could be triggered by a flag on the path step, or by detecting that the lookahead vector intersects a blocker.

### Design Issue D — Sweep-step state contamination

**Affects: any door that gets opened, then the door's reverse direction is tested.**

When a sweep step opens a door, the door stays open for subsequent steps. Two consequences:

1. The spawn formula reads `interactable.transform.right` — rotated value — and produces a different spawn (observed in this session: hallway→bathroom1 had spawn `(1.14, 5.07)` when door closed, `(2.28, 4.81)` after the prior step opened it).
2. The local-nav map and waypoints were generated assuming a specific door state.

**Fix direction**: between sweep steps, reset doors to a known state (probably closed). Alternatively, capture the door's initial closed-state rotation in the nav-graph at generation time and reference *that* in the spawn formula instead of the runtime transform.

### Design Issue E — Physics map captures a single door-state snapshot

**Affects: Bucket B for bathroom1↔hallway. Probably many others across the unknown sampling state.**

The current `local_navigation_maps.physics_sampled.live.json` was captured at a single moment with whatever door each was set to at that time — some open, some closed, unknown which. So:

- Cells blocked because of a *closed* door geometry are flagged "blocked" forever — even though when the door opens those cells become walkable.
- Cells flagged "walkable" might have been sampled with the door open and could actually be blocked when the door is closed.

**Fix direction (preferred)**:

1. Extend the live-capture command to cycle door states: close all openable doors → sample → open all openable doors → sample again. Emit *two* maps, or emit one map with per-cell flags `{walkable, blocked, blocked-only-if-door-X-closed, blocked-only-if-door-X-open}`.
2. Local-nav queries then consult both maps (or use the door-state-tagged flags) based on the current door state.

Going around manually opening/closing every door is tedious; building it into the capture command is the right move.

### Design Issue F — Local-nav grid cell positions aren't tuned per zone

**Affects: B (bathroom1↔hallway) and likely G.**

The local-nav grid for each zone has a fixed `MinX`/`MinZ` origin and `CellSize` of 0.5m. Cell centers fall wherever the grid lands. For narrow corridors, this matters:

- **Bathroom1** grid happens to have a cell at x=1.225 (col 6), which fits the doorway corridor (≈1.23–1.31 valid x). Coincidence — works.
- **Hallway** grid (MinX=-11.346) has cells at x=0.904, 1.404, 1.904 — none align with the corridor. Col 25 at x=1.404 has the player capsule's right edge at 2.204, overlapping the doorpost at 2.11.

**Fix direction**: per-zone grid origin tuning so cells fall on critical walkable positions (corridor centers, doorways). Or finer grids near narrow passages. Or non-grid path representation for narrow segments.

### Design Issue G — Zone equivalence for scene subzones is incomplete

**Affects: Buckets C, G.**

Failures show players ending in subzones (e.g., `hallway4`, `upper_hallway2`, `bedroom4`) when the step's `ToZone` is the base name (`hallway`, `upper_hallway`, `bedroom`). The `IsCurrentZoneEquivalentTo` check should accept these via the `AcceptedDestinationZones` list, but didn't pass.

Possible causes: the subzone alias isn't in the destination list, or the equivalence check has a bug for some name patterns.

**Fix direction**: audit `AcceptedDestinationZones` for each transition; ensure all scene subzones of the destination zone family are included. Add a test that round-trips a sample of subzones through the equivalence check.

### Design Issue H — Long-distance non-traversals (route/waypoint placement)

**Affects: Bucket D.**

`bedroom_closet->bedroom` moved 11.25m without entering bedroom. That's not a stall — the player was actively walking somewhere, but somewhere wrong. Likely a waypoint pointing in the wrong direction or a target outside the destination zone.

**Fix direction**: case-by-case waypoint/target review. May be data fixes more than architecture changes.

---

## Failure Buckets (Supporting Evidence)

### Doors (13 failures)

| Bucket | Description | Failures | Points to |
|---|---|---|---|
| A | Stalled at spawn (≤0.5m) | `gym_closet->gym` (0.39m); `hallway->bathroom1` (0.22m); `office->office_closet` (0.44m) | A, B, possibly D, E |
| B | Minor movement (0.5–1m) | `bathroom1->hallway` (0.88m); `bathroom2->bedroom` (0.53m); `bathroom2->dorian_bathroom2_2` (0.81m); `gym->upper_hallway` (0.55m) | B, C, E, F |
| C | Moderate movement (1–3m) | `bedroom->bathroom2` (2.98m); `gym->gym_closet` (2.34m); `laundry_room_closet->laundry_room` (2.34m); `upper_hallway->gym` (2.72m) | C, F, G |
| D | Significant movement (>3m) | `bedroom_closet->bedroom` (11.25m); `upper_hallway->attic` (3.73m) | H, possibly G |

### Open Passages (17 failures)

| Bucket | Description | Failures | Points to |
|---|---|---|---|
| E | Virtual destination — `dorian_X` (8) | `bathroom2->dorian_bathroom2`; `bedroom_closet->dorian_irondoor`; `gym->dorian_gym2`; `gym->dorian_gym4`; `gym_closet->dorian_gymcloset1`; `laundry_room->dorian_backdoor`; `laundry_room->dorian_laundry1`; `upper_hallway->dorian_upperhallcloset1` | A (need polymorphic criterion) |
| F | Virtual destination — state subzones (2) | `kitchen->kitchen_tutorial`; `office->office_1love` | A |
| G | Real room transitions failing (7) | `dining_room->piano_room`; `hallway->living_room`; `kitchen->dining_room`; `laundry_room->upper_hallway`; `living_room->hallway`; `piano_room->dining_room`; `upper_hallway->laundry_room` | C, F, G — real navigation issues |

---

## Recommended Order of Attack

1. **Design Issue A** (polymorphic pass criterion). Highest leverage — resolves all of E and F at once, and makes the rest of the failures honest.
2. **Design Issue E** (resample with both door states + automate). Enables proper analysis of door-state-dependent failures and underpins fixes for B and C.
3. **Design Issue D** (sweep state contamination). Fixes spurious spawn-position changes during multi-step sweeps so we get repeatable results.
4. **Design Issue B** (spawn formula). Especially important for narrow doorways like bathroom1.
5. **Design Issue C** (autowalk path-following). Needed for narrow corridors that require precise curves.
6. **Design Issue F** (grid alignment). Targeted fix for known narrow doorways.
7. **Design Issue G** (zone equivalence). Likely a quick audit-and-fix.
8. **Design Issue H** (long-distance non-traversals). Investigate per-case.

---

## bathroom1↔hallway Deep-Dive Context

Kept here for the follow-up — derived during this session:

- Door pivot at `(2.11, -0.617, 6.42)`, panel length ≈1.68–1.86m, opens `OnlyPositive` (+85° around world Y).
- Open panel sweeps from `(0.43, 6.42)` [closed] into bathroom; tip at approximately `(1.964, 8.094)`.
- Doorway corridor effective width is ~0.08m for the 1.60m-diameter player capsule. Player center must be `x ∈ [1.23, 1.31]` to clear both posts.
- Bathroom1 side has the panel sweeping INTO it. A bathroom1 spawn at corridor center (x≈1.23) is inside the swing arc (~1.63m from pivot vs 1.68m panel length). Door panel collides with the player during opening.
- Hallway side spawn (z<6.42) is outside the swing zone (panel only sweeps north of pivot). That's why `hallway->bathroom1` previously traversed cleanly when the door was closed.
- `DoorTraversalClearanceDistance = 1.4f` is too short to put the spawn outside this door's swing arc while aligning with the narrow corridor.
- Physics-sampled map cells were unblocked along the doorway corridor path (col 5 → col 6 curve) in `local_navigation_maps.physics_sampled.live.json` — but this was guesswork; proper fix is to resample with the door open (Design Issue E).
- Current SAP for `bathroom1->hallway`: `(0.725, 4.17, 8.05)` → spawn `(0.60, 7.49)` outside swing arc, but the south-walk hits the left doorpost because the auto-walk doesn't follow the corridor curve (Design Issue C).

---

## Files Modified This Session

- `AccessibilityWatcher.cs` — pushThroughPosition.y fix; sweep pass-criterion tightening (Doors and Open Passages both gated by `stillInSourceZone`).
- `artifacts/navigation/navigation_graph.generated.json` — `bathroom1->hallway` SAP reverted to `(0.725, 4.17, 8.05)`.
- `D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\local_navigation_maps.physics_sampled.live.json` — manually unblocked cells along bathroom1 doorway corridor (indices 17, 28, 39, 50, 61, 66–72, 77–82). Proper fix is to resample with both door states (Design Issue E).
- `D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\navigation_graph.json` — kept in sync with `.generated.json`.
- Door-sweep passed-key cache (`door_transition_sweep.live.passed.txt`): `transition:bathroom1->hallway` removed so it re-runs.

Untracked working files left in place (kept across the session):
- `NAVIGATION_REVIEW.md`
- `NEXT_STEPS.md`
- `NavigationTargetValidation.cs` (prototype validation layer, not integrated)
- `NavigationValidationTestHarness.cs` (test harness, currently active)
- `TEST_VALIDATION_HYPOTHESIS.md`
- `VALIDATION_INTEGRATION_GUIDE.md`

## Reproduction

```pwsh
.\scripts\Import-DoorTransitionSweepReport.ps1
.\scripts\Import-TransitionSweepReport.ps1
.\scripts\Invoke-LiveNavigationAudit.ps1
.\scripts\Test-DoorExecutorContract.ps1
```

Latest artifacts after this session:
- `artifacts/navigation/door_transition_sweep.live.json` — 22 entries, 9 passed, 13 failed
- `artifacts/navigation/transition_sweep.live.json` — 88 entries, 71 passed, 17 failed
- `artifacts/navigation/live_navigation_audit.live.json`
