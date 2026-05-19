# Validation Hypothesis Test: Integration (Minimal)

## What We're Testing

**Hypothesis**: If we validate that a target is reachable before using it, the bathroom1→hallway door will stop looping.

**Test case**: bathroom1 → hallway (currently loops for 5 seconds, same target repeated)

**Expected outcome**: 
- Target gets validated
- Logs show "TARGET_VALIDATION_TEST: PASSED" or "FAILED"
- If FAILED: door stops looping, fails clean instead
- If PASSED: door works without looping

---

## Integration Point (Single Spot, 2 Lines)

**File**: `AccessibilityWatcher.DoorNavigation.cs`  
**Location**: Line 550–555 (the "Extended fallback no-source" section)

### Before:
```csharp
if (IsFocusedBathroom1NoSourceBridgeStep(step) &&
    string.Equals(finalFallbackRawContext, "door-entry-advance-no-source-bridge", StringComparison.Ordinal) &&
    TryBuildDoorNoSourceBridgeRawExitTarget(
        step,
        currentZone,
        playerPosition,
        pushThroughPosition,
        position,
        out Vector3 noSourceRawExitTarget))
{
    LogNavigationTrackerDebug(
        "Extended fallback no-source door final entry target beyond clear point" +
        " originalTarget=" + FormatVector3(position) +
        " extendedTarget=" + FormatVector3(noSourceRawExitTarget) +
        " step=" + DescribeNavigationStep(step));
    position = noSourceRawExitTarget;
}
```

### After (Add These 5 Lines):
```csharp
if (IsFocusedBathroom1NoSourceBridgeStep(step) &&
    string.Equals(finalFallbackRawContext, "door-entry-advance-no-source-bridge", StringComparison.Ordinal) &&
    TryBuildDoorNoSourceBridgeRawExitTarget(
        step,
        currentZone,
        playerPosition,
        pushThroughPosition,
        position,
        out Vector3 noSourceRawExitTarget))
{
    LogNavigationTrackerDebug(
        "Extended fallback no-source door final entry target beyond clear point" +
        " originalTarget=" + FormatVector3(position) +
        " extendedTarget=" + FormatVector3(noSourceRawExitTarget) +
        " step=" + DescribeNavigationStep(step));
    position = noSourceRawExitTarget;
}

// *** ADD: Test validation on the position we're about to use ***
#if VALIDATION_TEST_ENABLED
if (TryValidateTargetForTestCase(step, currentZone, position, "ZoneFallback", out bool shouldUse))
{
    if (!shouldUse)
    {
        DebugLogger.Log("TARGET_VALIDATION_TEST: bathroom1->hallway rejected target, will fail");
        return false; // Don't use unreachable target
    }
}
#endif
// *** END ADD ***
```

That's it. Two lines of `#if` guard wrapping a validation call.

---

## Build & Test Steps

### Step 1: Compile
```powershell
cd c:\Users\amock\mod template
dotnet build DateEverythingAccess.csproj --no-restore
```
Should succeed with 0 errors.

### Step 2: Deploy
```powershell
.\scripts\Deploy-Mod.ps1 -Configuration Debug
```

### Step 3: Test in Game
1. Start game, load house
2. Set navigation destination to `bathroom1`
3. Enable auto-walk (try to walk to hallway)
4. Watch for 5-second loop

### Step 4: Check Logs
```powershell
# In PowerShell, search recent logs:
Get-Content -Path "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\LogOutput.log" -Tail 500 | 
  Select-String "TARGET_VALIDATION_TEST"
```

**Look for**:
- `TARGET_VALIDATION_TEST: bathroom1->hallway candidate` — test ran
- `TARGET_VALIDATION_TEST: FAILED - no local path exists` — target is unreachable
- `TARGET_VALIDATION_TEST: PASSED - target is reachable` — target is walkable
- `TARGET_VALIDATION_TEST: bathroom1->hallway rejected target` — validation rejected it

### Step 5: Compare Behavior

**Before test (baseline)**:
- Logs repeat: `Next door post-interaction target state=FinalEntryRaw ... position=(-1.05, -0.62, 5.19)`
- Same position 50+ times in 5 seconds
- Loop detector fires

**After test with validation failing**:
- Logs show: `TARGET_VALIDATION_TEST: FAILED - no local path exists`
- Then: `bathroom1->hallway rejected target, will fail`
- No loop, clean failure
- **This proves the hypothesis: validation prevents reselection loop**

**After test with validation passing** (unlikely for bathroom1, but would mean):
- Logs show: `TARGET_VALIDATION_TEST: PASSED - target is reachable`
- Door traversal proceeds without looping
- Position changes as player moves (not stuck on same position)

---

## What This Proves (or Disproves)

### If Validation FAILS (Expected)
✅ **Hypothesis confirmed**: The target was unreachable, validation caught it, loop stopped  
✅ **Next step**: Need to fix the data (graph waypoint, map blocker, or graph zone)  
⚠️ **Not a workaround**: Validation exposed the real problem

### If Validation PASSES (Unlikely)
⚠️ **Hypothesis needs refinement**: Target is reachable per maps, but still fails in reality  
→ Problem is elsewhere (state machine, input handling, etc.)  
→ Review logs for different error pattern

### If No Test Logs Appear
❌ **Integration issue**: Test hook isn't being called  
→ Check that `step.FromZone == "bathroom1"` and `step.ToZone == "hallway"`  
→ May need to adjust the test case selector

---

## Disable & Cleanup

Once you've seen the test results:

1. **To disable test** (keep code as reference):
   - Change `#define VALIDATION_TEST_ENABLED` to `#undef VALIDATION_TEST_ENABLED`
   - Recompile, redeploy
   - Behavior reverts to original

2. **To remove test entirely**:
   - Delete the 5-line `#if VALIDATION_TEST_ENABLED` block from DoorNavigation.cs
   - Delete `NavigationValidationTestHarness.cs`
   - Recompile

3. **To proceed to full integration**:
   - Keep the test validation code (don't use `#if` guard)
   - Move logic to permanent location in `NavigationTargetValidation.cs`
   - Integrate for all transitions, not just bathroom1

---

## Expected Timeline

- **Compile & deploy**: 2–3 minutes
- **In-game test**: 1–2 minutes (trigger once, watch logs)
- **Log analysis**: 2–3 minutes
- **Total**: ~10 minutes to prove/disprove hypothesis

If hypothesis is proven, you'll have solid evidence that validation architecture is correct and full integration is worth the time investment.
