# Finding: NPC-pose / dialog-only noise in the fixture roster (2026-06-15)

**Question asked:** Can we filter NPC-pose / dialog-only animation objects out of the navigation
roster at the export level to cut noise?

**Conclusion: there is no NPC-pose noise class leaking into the roster. No filter is warranted.**
Report only — no exporter/roster code was changed (per the decision to investigate and report).

## What was checked

The 939-entry fixture roster (`navigable_region.bake.json` → `fixtures`) was cross-referenced
against the AssetRipper scene YAML for the two candidate "dialog-rig" signals:

1. **No physical bounds** (`Bounds3D == null`): 1,406 active interactables, but **1,296 are real
   datables** (120 unique inks — lights, sinks, frames). These have no collider because they're
   look-only datables, not because they're pose rigs. Filtering on this deletes real targets.

2. **Has a SkinnedMeshRenderer** (`!u!137`, the animated-rig signal): 94 in the scene; **53 map
   to roster fixtures** — and all 53 are legitimate datable objects (`AirConVent`,
   `CoffeeMachine`, `BookShelf`, the `Curtain_*` family, `Blanket`, `BobbyPin`, `Dishwasher`).
   In Date Everything **every object is a datable**, and the SkinnedMeshRenderer is how the object
   itself animates during its own dating scene — it is NOT a separate NPC standing in for dialog.
   Filtering on this deletes 53 real targets.

3. **Name/path keywords** (rig/pose/anim/dialog/splash/portrait): 46 matches, all false positives
   from substring hits (`anim` inside "magical", `rig` inside "ORIGIN") on real objects
   (Cabinet_Record, Dishes, Gift, RecordPlayer, Curtain, Window).

## Why the roster is already clean

The noise the original concern targeted was already removed upstream by the roster pipeline:
- **Lighting-preset duplication** (the ~961 → 57 collapse) — handled by identity-dedup.
- **Exterior decor** (bushes, trees, neighbour houses, drones) — handled by the Exterior subtree
  denylist.
- **Animated dialog-rig colliders** — already dropped at the blocker exporter
  (`SkinnedMeshRigCollider`), so they never produce a navigation blocker.

Final roster: 935 datable + 4 non-datable (Bow, Box, Lid, Rug — physical props) + 1 off-floor
degenerate. Every entry is a real, pickable navigation target.

## Recommendation

Do not add a pose/dialog filter. If a future in-game coverage sweep surfaces specific objects
that are genuinely unreachable dialog-only artifacts, revisit with those concrete names — but the
static export shows no such class today, and every plausible blanket signal (no-bounds,
SkinnedMeshRenderer, name keyword) would delete real datables.
