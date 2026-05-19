# Navigation System Architectural Review

**Status**: Root cause identified. System has a fundamental validation-timing architecture problem, not individual door/transition failures.

---

## Executive Summary

The 4 failing transitions (1 door, 3 open passages) and the "auto-walk loops/stalls" problem are **symptoms of the same architectural issue**: targets are selected and announced *before* being validated as reachable. When a target proves unreachable (physics collision), the system retries the same unreachable target every frame instead of invalidating it and picking an alternative.

**Why previous fixes only changed shape**: Each patch tried to handle specific door states or add recovery logic, but never addressed the core problem—that an unreachable target keeps being reselected. This is like treating fever symptoms while ignoring the infection.

---

## Root Cause: Target Validation Timing

### Current Architecture (Wrong)

```
TryGetNextNavigationPosition()
  ├─ Door handler: BuildTarget() → returns (-1.05, -0.62, 5.19)
  ├─ Open passage handler: BuildTarget() → returns some waypoint
  └─ Fallback: TryGetZonePosition() → returns zone center

AutoWalkLoop:
  ├─ Frame N:     Pick target, announce to beep/UI
  ├─ Frame N+1:   Try to move → physics blocks
  ├─ Frame N+2:   Call TryGetNextNavigationPosition() AGAIN
  ├─ Frame N+3:   Get same target back (no invalidation)
  ├─ Frame N+4:   Try to move → physics blocks
  └─ ... repeat N times until loop detector fires
```

**The problem**: `TryGetNextNavigationPosition()` is called every frame, but it has no memory of "this target was just tried and failed." So it recomputes and returns the exact same unreachable position repeatedly.

### Why This Breaks Auto-Walk and Beep

1. **Beep doesn't update**: Same target every frame = same direction = static beep position
2. **Auto-walk stalls**: Forward movement is applied but physics collision cancels it (logs show `inputAppliedButCancelled=True`)
3. **4-second loop**: System hits max loop detection threshold and tries recovery (often fails)

### Where Targets Are Built (No Validation)

**Door Navigation** (`AccessibilityWatcher.DoorNavigation.cs`):
- Line 1441: `TryBuildDoorNoSourceBridgeRawExitTarget()` 
  - Returns `(-1.05, -0.62, 5.19)` for bathroom1→hallway
  - Never checks if this position has a path from player
- Line 1384: `TryBuildDoorSourceZoneEntryAdvanceTarget()`
  - Similar: picks target, doesn't validate reachability

**Open Passage Navigation** (`AccessibilityWatcher.OpenPassageNavigation.cs`):
- Builds guided waypoint targets without pre-checking reachability
- Calls local map path finding AFTER selection

**Zone Fallback** (main file, line 7397):
- `TryGetZonePosition(step.ToZone)` returns zone center
- No pre-validation

### Post-Selection Validation (Too Late)

`TryAdjustNavigationTargetWithLocalPathing()` (line 8088):
- Called AFTER a target is already selected and used
- Tries to find a path to the target
- If path fails, sometimes still uses the same target
- Next frame, `TryGetNextNavigationPosition()` runs again
- Gets the same target again (no history of "tried this, it failed")

---

## Evidence from Logs

From most recent run:

```
[Info] Tone target set kind=ZoneFallback position=(-1.05, -0.62, 5.19) stepKey=transition:bathroom1->hallway
[Info] Next door post-interaction target state=FinalEntryRaw kind=ZoneFallback position=(-1.05, -0.62, 5.19)
[Info] Allowed raw door final entry ... desiredPosition=(-1.05, -0.62, 5.19)
[Info] Tone target set kind=ZoneFallback position=(-1.05, -0.62, 5.19) stepKey=transition:bathroom1->hallway
[Info] Next door post-interaction target state=FinalEntryRaw kind=ZoneFallback position=(-1.05, -0.62, 5.19)
[Info] Allowed raw door final entry ... desiredPosition=(-1.05, -0.62, 5.19)
... [repeats 50+ times identically]
```

**Same position. Every frame. No change.** This proves the target is never invalidated.

Meanwhile:
```
[Warning] Runtime movement probe reason=loop-detector 
  input=lastMove=(0.00, 0.00, 1.00) 
  currentMove=(0.00, 0.00, 0.00) 
  inputAppliedButCancelled=True 
  firstBlockingHit=SM_Doorframe_Small_3
```

Movement input is applied but physics cancels it because the target is **behind a mesh collider**.

---

## Why This Architecture Can't Be Patch-Fixed

### Attempt 1: Add recovery logic in door state machine
**Result**: Builds more state machine complexity, but target is still picked/tried/failed/picked each frame
**Why it fails**: The root issue (same target every frame) is untouched
**Evidence**: Recent commits show recovery logic proliferation without solving the 4 failures

### Attempt 2: Tighten local map validation
**Result**: Rejects bad lookahead points, but doesn't prevent target reselection
**Why it fails**: Local validation is post-selection
**Evidence**: `bathroom1 -> hallway` still loops despite recent lookahead improvements

### Attempt 3: Add blocker rasterization
**Result**: Marks some cells blocked, but target remains unreachable and reselected
**Why it fails**: Doesn't address the frame-by-frame reselection loop
**Evidence**: Physics sampled maps helped reduce door failures from 8→3, but 4 still fail

The pattern: each fix reduces symptom scope but never reaches the architecture level.

---

## Solution: Validate Targets Before Selection

### High-Level Strategy

1. **Separate target GENERATION from target SELECTION**:
   - Generate candidates (door, open passage, zone fallback)
   - Validate each against local maps before use
   - Return first valid candidate or fail

2. **Invalidate targets that fail**:
   - Track which targets were tried from this step
   - Don't reuse a target that just failed on this frame
   - Pick a different candidate or fail the transition

3. **Centralize target validation**:
   - Single `IsTargetReachableFromPlayer()` function
   - Used before any target is returned to auto-walk loop
   - Same validation for doors, open passages, fallback

### Implementation Outline

**Phase 1: Create validation layer**
```csharp
private bool IsTargetReachableFromCurrentPlayer(
    Vector3 targetPosition, 
    NavigationGraph.PathStep step)
{
    // 1. Check target is in a valid zone
    if (!TryGetZoneNameForPosition(targetPosition, out string targetZone))
        return false;
    
    // 2. Check local maps have a path from player to target
    if (LocalNavigationMaps.IsAvailable)
    {
        string playerZone = GetCurrentZoneNameForNavigation();
        
        // If target is in player's zone, check local pathing
        if (playerZone == targetZone)
        {
            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
            if (!LocalNavigationMaps.TryFindPath(playerPos, targetPosition, out _, out _))
                return false;
        }
    }
    
    return true;
}
```

**Phase 2: Refactor target resolution**
```csharp
private bool TryResolveReachableNavigationTarget(
    NavigationGraph.PathStep step,
    string currentZone,
    Vector3 playerPosition,
    out Vector3 position,
    out NavigationTargetKind targetKind)
{
    // Generate candidates from all sources
    var candidates = new List<(Vector3 pos, NavigationTargetKind kind)>();
    
    // Door targets
    if (TryBuildDoorNoSourceBridgeRawExitTarget(..., out var doorPos))
        candidates.Add((doorPos, NavigationTargetKind.ZoneFallback));
    
    // Open passage targets  
    if (TryBuildOpenPassageTarget(..., out var openPos))
        candidates.Add((openPos, NavigationTargetKind.???));
    
    // Zone fallback
    if (TryGetZonePosition(step.ToZone, out var zonePos))
        candidates.Add((zonePos, NavigationTargetKind.ZoneFallback));
    
    // Validate and use first reachable
    foreach (var (pos, kind) in candidates)
    {
        if (IsTargetReachableFromCurrentPlayer(pos, step))
        {
            position = pos;
            targetKind = kind;
            return true;
        }
    }
    
    // None reachable → fail, don't retry same target
    position = Vector3.zero;
    targetKind = NavigationTargetKind.ZoneFallback;
    return false;
}
```

**Phase 3: Track rejected targets**
```csharp
private Dictionary<string, HashSet<Vector3>> _rejectedTargets = 
    new Dictionary<string, HashSet<Vector3>>();

private bool IsTargetRejectedThisFrame(
    NavigationGraph.PathStep step, 
    Vector3 target)
{
    string stepKey = BuildNavigationStepKey(step);
    if (_rejectedTargets.TryGetValue(stepKey, out var rejected))
    {
        return rejected.Any(t => Vector3.Distance(t, target) < 0.1f);
    }
    return false;
}

private void RecordTargetRejection(
    NavigationGraph.PathStep step,
    Vector3 target)
{
    string stepKey = BuildNavigationStepKey(step);
    if (!_rejectedTargets.ContainsKey(stepKey))
        _rejectedTargets[stepKey] = new HashSet<Vector3>();
    
    _rejectedTargets[stepKey].Add(target);
}

// Clear rejected list when step changes
private void ClearRejectedTargetsOnStepChange()
{
    _rejectedTargets.Clear();
}
```

---

## Migration Path

### Complexity Budget: Medium

- **Low risk**: New validation function is isolated, can be tested independently
- **Medium risk**: Refactor target resolution to use new validation (but logic is already scattered, consolidation helps)
- **Testing**: Existing door/open passage tests become validation tests; sweep/contract tools validate counts

### What Changes

1. Door/Open passage handlers keep generating same candidates
2. New layer validates before use
3. Loop detector becomes less necessary (targets are pre-validated)
4. 4 failing transitions should pass because targets are now reachable-first

### What Stays the Same

- Graph structure (waypoints, zones, transitions)
- Local navigation maps
- State machine (simplified, less recovery logic needed)
- UI/beep (works because target is validated before announcement)

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| Validation too strict, rejects valid targets | Player can't navigate anywhere | Start with minimal validation (local maps only), expand if needed |
| Candidate generation gives no reachable option | Transition fails immediately | Still acceptable (fail-fast beats 5-sec loop); can add richer fallbacks later |
| Performance of per-frame validation | Frame rate drop | Validate only during target selection (not every frame); cache local map queries |
| Merge conflicts with recent door/open passage patches | Integration headache | Consolidate into target layer; many patches become unnecessary |

---

## What to Do Next

1. **Confirm this diagnosis**: Run a sweep with logging that shows `IsTargetReachable()` returning false for the 4 failures
2. **Prototype validation function**: Implement `IsTargetReachableFromCurrentPlayer()` in isolation, test it
3. **Refactor target resolution**: Move validation to pre-selection step
4. **Remove recovery logic**: Delete increasingly complex recovery/state machine workarounds that are now unnecessary
5. **Validate with sweeps**: Confirm 4 failures now pass without new patches

The complexity reduction (fewer state machine states, no recovery loops, simpler logic) should offset the refactor cost.
