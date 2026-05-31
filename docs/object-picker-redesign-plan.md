# Object Picker Redesign — Implementation Plan

Status: PROPOSED (2026-05-30). Scope: the Ctrl+Shift+F6 known-objects picker in
`AccessibilityWatcher.cs`. No bake / planner / nav-runtime changes except one
small public accessor on `SimpleNavPlanner`.

## Goals (from the user)

1. **Tighten the eligibility filter.** Only objects the player has truly
   encountered should appear: examined (saw the examine text box), glasses-dated,
   or normal (non-glasses) interaction. Too many objects leak through today.
2. **Fix distance sorting.** Current sort is flat XZ and ignores floors, so
   cross-floor objects read as "close." Make it floor-aware.
3. **Add categories + filters** so the list is navigable even when long, mirroring
   **the organizing principle the game already uses**.

## Organizing principle — match the game

The game presents datables through two apps:

- **DateADex** — datables the player has **met**. Native grouping is relationship
  *status* (`ListSummaryDataRealized/Met/Loves/Friends/Hates`). Entries are
  per-character, by resolved name.
- **Rumors** — clues about **unmet** datables, intentionally *hints, not
  waypoints*.

**Binding constraint from Rumors:** the picker must NOT become a map for
un-encountered datables. "Met via rumor" alone is NOT eligibility. Eligibility
remains: examined OR glasses-dated OR normal-interacted. This is the same line
Rumors refuses to cross, and it's exactly the filter-leak we're closing.

### Resulting structure

- **Two sections** (user-chosen), mirroring DateADex's met-vs-rest split:
  - **MET** — datable status `!= Unmet`. Labeled by **character name**
    (`Save.TryGetNameByInternalName`), like DateADex.
  - **ENCOUNTERED** — examined/interacted but datable still `Unmet`. Labeled by
    **object name only** (no character identity revealed — consistent with
    DateADex showing `???` for unmet).
- Each entry carries **location** (zone via `TryGetZoneNameForInteractable`) and
  **floor** as a sortable label, not a top-level tree.

## Part 1 — Filter-leak fix (do first; "fix the data source first")

Current leak is in `IsExaminedInteractable` (AccessibilityWatcher.cs:2710):
- It walks **parents** (`GetComponentInParent<ObjectExamine>`) and **all
  children**, then matches on shared `ObjectExamine.InkNode`. Examine boxes are
  shared by ink node, so one examined object pulls in every sibling/child that
  shares the node, and the parent-walk attributes a parent's examine to many
  children.

Fix:
- An examine counts for an interactable ONLY when it belongs to that interactable
  directly. Keep:
  - remembered keys for the interactable's own `Id` / `name` / `InternalName()` /
    `inkFileName` (set in `RememberExaminedObject`, which already resolves the
    owning `InteractableObj` at examine time — AccessibilityWatcher.cs:432).
- Drop from the eligibility path:
  - the `GetComponentInParent<ObjectExamine>()` fallback,
  - the broad child-scan that matches any child examine's `InkNode`,
  - the save `GetBoxExamenData().ContainsKey(InkNode)` fallback IF it proves to be
    the broad-match source (verify in-game first — it may be load-bearing for
    examines remembered across a save/load with no live key). Decision gate below.
- Keep `hasNormalInteracted` and `IsDatedInteractable` unchanged — those are
  already per-object and correct.

Decision gate before deleting the save fallback: confirm with a logging pass
whether `GetBoxExamenData` keys are per-object (InkNode unique per interactable)
or shared. If shared, replace with a per-object remembered-key set persisted by
us in `RememberExaminedObject` rather than inferred from the shared box map.

Acceptance: an object the player only saw *near* an examined sibling no longer
appears; objects the player actually examined still appear within the session.

### Decision gate — RESOLVED via decompiled source (decompiled/ObjectExamine.cs,
### ObjectSaveData.cs, Save.cs)

- `ObjectExamine.InkNode` is a SHARED ink content node (`SwitchFlow("examine_text."
  + InkNode)`), so matching by InkNode alone leaks across objects → ownership-scoping
  is required (confirmed correct).
- `ObjectSaveData` persists ONLY `activeSelf / activatedAnimation / isClean /
  hasNormalInteracted`. There is NO persisted "examined" flag for ANY object.
- `boxExamenDictionary` / `GetBoxExamenData` is NOT an examine history — it is only
  the moving-box "Boxing Day" achievement tally (`CheckBoxingDay` fires at >=45),
  keyed by a running `Boxes_N` counter, covering no other objects. It cannot tell
  you which object was examined, so it is useless as an examine fallback. The
  fallback that consulted it was REMOVED; identity-key match is the sole signal.

KNOWN LIMITATION (accepted 2026-05-30, revisit later): the game persists NO examine
flag, so examine-only encounters live only in session-scoped `_examinedObjectKeys`
and DROP from the picker after a save/reload. Durable starting list = met datables +
normally-interacted objects; the player re-examines objects across a session. A
mod-side persisted examine history (per-save-slot file in the BepInEx dir) is the
eventual fix if needed. Noted in code at IsEncounteredKnownObject.

## Part 2 — Floor-aware distance sort

Today: `TryBuildKnownObjectTargets` sorts by `GetFlatDistance` (XZ only,
AccessibilityWatcher.cs:2593 + 2616).

Change:
- Add `KnownObjectTarget.FloorIndex` (and keep `Distance` = XZ).
- Resolve each candidate's floor from its world Y, and the player's floor from the
  player's Y, using the planner's floor table. `SimpleNavPlanner.FloorForY` /
  `FloorForTargetY` are private static; add a thin public accessor:
  `SimpleNavPlanner.TryGetFloorIndexForY(float y, out int floorIndex)` (and one
  for the player position) so the picker doesn't duplicate the floor list.
  - For *targets* use the `FloorForTargetY` rule (wall/ceiling/tabletop items are
    accessed from the floor below — same rule the navigator already uses, so the
    picker's notion of "which floor" matches where autowalk will actually stand).
- Sort key = (sameFloorAsPlayer ? 0 : 1, then XZ distance). Player's floor first,
  nearest-first within each floor. Cross-floor items go after, still ordered.
- This is intentionally NOT full planner routing per item (too expensive for a
  live menu) — floor-bucketed XZ is correct-enough and fixes the reported symptom.

## Part 3 — Grouping + filters UX

### Data model
Replace the flat `List<KnownObjectTarget>` with a view built from the full
candidate set each open, plus live filter state:

```
enum PickerSection { Met, Encountered }
KnownObjectTarget {
  InteractableObj Interactable;
  string Label;          // character name (Met) or object name (Encountered)
  string Zone;           // resolved zone, for label + alpha sort
  int FloorIndex;
  float Distance;        // XZ
  PickerSection Section;
  bool IsDoor;           // IsDoorInteractable(candidate)
}
```

### Filter / sort state (persist across opens in the watcher instance)
- `FloorFilter`: All | CurrentFloorOnly
- `SectionFilter`: All | MetOnly | EncounteredOnly  (the met-vs-unmet toggle)
- `SortMode`: Distance (floor-aware) | Alphabetical (by Label, then Zone)
- `DoorsOnly`: bool

### Navigation model (keep it simple & quick — screen-reader friendly)
- Build one **flat, ordered, announced** list after applying filters+sort, with
  section headers spoken inline ("Met. 12 items." / "Encountered. 5 items.") when
  crossing a boundary, so there's no nested-menu depth to get lost in.
- Up/Down/Enter/Escape stay as-is (`UpdateKnownObjectPicker`,
  AccessibilityWatcher.cs:2476).
- Add filter toggles on currently-unused keys, each announcing new state and
  re-announcing the now-current item:
  - Left/Right: cycle `SortMode` (Distance ↔ Alphabetical).
  - `F`: cycle `FloorFilter` (All ↔ Current floor).
  - `M`: cycle `SectionFilter` (All → Met → Encountered → All).
  - `D`: toggle `DoorsOnly`.
  - Announce e.g. "Current floor only. 7 items. 1 of 7, Dorian, laundry door."
- On any toggle, clamp selection index and re-announce; if the filtered list is
  empty, say so and leave the toggle applied (player can cycle back).

### Announcement format
- Met entry: `"{CharacterName}, {object}, {zone}, {floor-tag}, {dist}m"`
  e.g. "Dorian, door, laundry room, this floor, 4 metres."
- Encountered entry: `"{object}, {zone}, {floor-tag}, {dist}m"` (no character).
- `floor-tag`: omit when on player's floor or say "this floor"; "upstairs"/
  "downstairs"/"floor N" otherwise. New `Loc` keys.
- Title line includes active filters so state is discoverable:
  `"Objects. Sort: nearest. Floor: this. Showing: all."`

### Loc keys to add
- `navigation_object_picker_section_met`, `_section_encountered`
- `navigation_object_picker_floor_this`, `_floor_up`, `_floor_down`, `_floor_n`
- `navigation_object_picker_sort_distance`, `_sort_alpha`
- `navigation_object_picker_filter_floor_all`, `_filter_floor_current`
- `navigation_object_picker_filter_section_*`, `_filter_doors_on/off`
- `navigation_object_picker_empty_filtered` (filters hid everything)

## Touch list
- `AccessibilityWatcher.cs`: `KnownObjectTarget` struct; `TryBuildKnownObjectTargets`
  (sectioning + floor + zone + door flags, drop equivalence-by-distance merge or
  keep but section-aware); `OpenKnownObjectPicker` / `UpdateKnownObjectPicker` /
  `AnnounceCurrentKnownObjectPickerItem` (filter state + new keys + announcements);
  `IsExaminedInteractable` (leak fix).
- `SimpleNavPlanner.cs`: add public `TryGetFloorIndexForY` accessor(s).
- `Loc.cs`: new keys.

## Out of scope (explicitly)
- No bake/planner pathing changes; no Rumors-driven targeting; no full per-item
  route-length sorting; no nested location tree.

## Test plan (in-game, game must be CLOSED to deploy plugin copy)
1. Filter leak: examine one object next to an un-examined sibling sharing an ink
   node → only the examined one appears. Reload save → still appears.
2. Floor sort: stand downstairs, open picker → all downstairs items precede
   upstairs items; nearest downstairs first. Stand on stairs → still bucketed by
   target floor, not flat XZ.
3. Sections: met datable shows by name; examined-but-unmet shows by object name,
   never the character name.
4. Filters: F hides other floor; M cycles met/encountered/all; D shows only doors;
   Left/Right flips to alphabetical. Each announces state + current item. Empty
   result announces gracefully.
