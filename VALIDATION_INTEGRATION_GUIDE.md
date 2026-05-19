# Navigation Target Validation Prototype: Integration Guide

## What the Prototype Does

The new `NavigationTargetValidation.cs` file adds three key functions:

1. **`IsTargetReachableFromCurrentPlayer()`**
   - Pre-validates any target position using local navigation maps
   - Detects if target is in a disconnected physics component
   - Checks if first movement segment is clear
   - Returns false = don't use this target, returns true = safe to use

2. **`TrySelectValidatedNavigationTarget()`**
   - Takes candidate targets (door, open passage, zone fallback)
   - Validates each in priority order
   - Returns the first one that passes validation
   - Returns false if no candidates are reachable

3. **`TargetRejectionTracker`** (bonus, for future use)
   - Prevents same target from being reselected in same frame
   - Clears when step changes

## Where to Integrate (Concrete Code Changes)

### Change 1: Door Handler (DoorNavigation.cs, lines 529–568)

**BEFORE:**
```csharp
if (TryGetZonePosition(step.ToZone, out position))
{
    // ...compute context...
    if (IsFocusedBathroom1NoSourceBridgeStep(step) && ... &&
        TryBuildDoorNoSourceBridgeRawExitTarget(..., position, out exitTarget))
    {
        position = exitTarget;
    }
    // Returns position even if no exit target found ← PROBLEM
    return TryUseDoorPostInteractionTargetDecision(
        ..., position, ...);
}
```

**AFTER:**
```csharp
if (TryGetZonePosition(step.ToZone, out position))
{
    Vector3 doorCandidate = Vector3.zero;
    NavigationTargetKind doorKind = NavigationTargetKind.ZoneFallback;
    
    if (IsFocusedBathroom1NoSourceBridgeStep(step) && ... &&
        TryBuildDoorNoSourceBridgeRawExitTarget(..., position, out doorCandidate))
    {
        doorKind = NavigationTargetKind.ZoneFallback;
    }
    
    // Validate before using
    if (doorCandidate != Vector3.zero &&
        IsTargetReachableFromCurrentPlayer(
            doorCandidate,
            step.ToZone,
            currentZone,
            step,
            "door-no-source-exit"))
    {
        position = doorCandidate;
    }
    else if (!IsTargetReachableFromCurrentPlayer(
        position,  // zone fallback
        step.ToZone,
        currentZone,
        step,
        "zone-fallback"))
    {
        // Neither candidate is reachable → fail fast
        LogNavigationTrackerDebug(
            "No reachable target for bathroom1->hallway door" +
            " doorCandidate=" + FormatVector3(doorCandidate) +
            " fallback=" + FormatVector3(position));
        return false;
    }
    
    return TryUseDoorPostInteractionTargetDecision(
        ..., position, ...);
}
```

### Change 2: Main Loop (AccessibilityWatcher.cs, around line 7228)

Instead of having separate handlers return unvalidated targets, consolidate into one call:

**BEFORE:**
```csharp
if (TryGetDoorTraversalNavigationTarget(step, ..., out position, out targetKind))
    return true;

if (TryGetOpenPassageNavigationTarget(step, ..., out position, out targetKind))
    return true;

// Fallback to zone
if (TryGetZonePosition(step.ToZone, out position))
{
    targetKind = NavigationTargetKind.ZoneFallback;
    return true;
}
```

**AFTER:**
```csharp
// Collect candidates
var doorCand = TryGetDoorCandidate(step, ...) ? 
    ((Vector3, NavigationTargetKind, string)?)(...) : null;

var openCand = TryGetOpenPassageCandidate(step, ...) ? 
    ((Vector3, NavigationTargetKind, string)?)(...) : null;

var fallbackCand = TryGetZonePosition(step.ToZone, out var zonePos) ? 
    ((zonePos, NavigationTargetKind.ZoneFallback))? : null;

// Validate and select
if (TrySelectValidatedNavigationTarget(
    step,
    currentZone,
    playerPosition,
    doorCand,
    openCand,
    fallbackCand,
    out position,
    out targetKind))
{
    return true;
}

// No valid candidates
return false;
```

## Test Plan

### Test 1: Bathroom1 → Hallway (Door)
```
Before: Loops on (-1.05, -0.62, 5.19) for 5 seconds
After: IsTargetReachableFromCurrentPlayer() returns false
       → fails fast with log "No reachable target for bathroom1->hallway"
       → user hears "navigation failed" instead of looping
```

### Test 2: Living Room → Hallway (Open Passage)
```
Before: Selected waypoint behind Table_LivingRoom_TV, physics blocks it
After: IsTargetReachableFromCurrentPlayer() checks first movement segment
       → detects collision on path.start → rejects
       → falls back to zone fallback or fails
```

### Test 3: Happy Path - Hallway → Bathroom1
```
Before: Picks correct target, reaches it
After: IsTargetReachableFromCurrentPlayer() validates path exists
       → returns true
       → works exactly as before (no regression)
```

### Test 4: Logging Output
```
Enable navigation debug logging:
  - "Target reachability check PASSED" = validation succeeded
  - "Target reachability check FAILED (no local path)" = disconnected zone
  - "Target reachability check FAILED (first segment blocked)" = physics blocker on path start
  - "Target selection chose X candidate" = which candidate was selected
  - "No reachable candidates" = all candidates failed validation
```

## Why This Works

### Current Problem
```
Frame N:   Pick target (-1.05, -0.62, 5.19)  [not validated]
Frame N+1: Try move → blocked
Frame N+2: Pick same target               [not invalidated]
Frame N+3: Try move → blocked
... loop detector fires after 5 sec
```

### After Validation Layer
```
Frame N:   Validate (-1.05, -0.62, 5.19) → no path exists → return false
Frame N:   (same frame) Pick fallback zone position
Frame N:   Validate fallback → has path → return true
Frame N:   Move toward validated target

OR

Frame N:   All candidates fail validation → fail fast
Frame N+1: (next step in path)
```

## Performance Impact

- **Per-frame cost**: One local map pathfinding query per target candidate
- **Typical case**: 1–3 candidates checked, 1–2 pass validation
- **Local map query cost**: O(cells visited) where cells ≈ 50–200 in typical zone
- **Expected**: Negligible (<1ms per frame)

## Rollback Plan

If the validation layer causes unexpected failures:
1. Switch validation to "warning only" mode (log but allow target)
2. Collect logs from 1–2 runs to see what's being rejected
3. Adjust tolerances (path search radius, initial overlap threshold)
4. Re-enable blocking mode

## Integration Checklist

- [ ] Add `NavigationTargetValidation.cs` to project file
- [ ] Compile and verify no errors
- [ ] Modify door handler (DoorNavigation.cs) to call validation
- [ ] Modify main loop (AccessibilityWatcher.cs) to use consolidated validation
- [ ] Run door sweep: expect 4 failures to change from "loop" to "validation failed"
- [ ] Run door sweep: expect no regressions on passing doors
- [ ] Run open passage sweep: expect 3 open failures to change shape
- [ ] Review logs for false positives/negatives
- [ ] If acceptable: remove redundant recovery/state machine logic
