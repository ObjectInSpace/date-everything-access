# Door Executor Contract

## Scope

This contract covers post-interaction door traversal only. It does not replace the navigation graph, local navigation maps, transition semantics overlay, open-passage logic, stairs, or pre-interaction door retry targeting.

The current runtime evidence points to an executor/state problem, not a metadata problem:
- Latest imported door sweep `299C4BAD...`: `22 total / 15 passed / 7 failed`
- Door metadata audit: `0 suspicious`, `0 missing connector`
- Six of seven failures are post-interaction `DoorEntryAdvance` source-zone loops
- One failure is `DoorThresholdAdvance` with `local-goal-unresolved`

## Stage Model

Door traversal after interaction must move through explicit stages:
- `DoorThresholdAdvance`
- `DoorThresholdHandoff`
- `DoorPushThrough`
- `DoorEntryAdvance`

`DoorEntryAdvanceExtended` is a source-zone bridge target used before final `DoorEntryAdvance` when the player has not yet left the source zone.

## Stage Completion Proof

`DoorThresholdAdvance` is complete only when one of these is true:
- Player reaches the source threshold within the raw threshold arrival radius.
- Source-side threshold local proxy completes and the same frame has valid release evidence.
- No-handoff bypass rule is valid and source threshold distance is within bypass range.

`DoorThresholdHandoff` is complete only when one of these is true:
- A valid handoff target exists and player reaches it within the handoff arrival radius.
- `door-threshold-handoff-local` completes for the same step.
- No valid handoff target exists and the no-handoff rule has explicitly allowed bypass.

`DoorPushThrough` is complete only when one of these is true:
- Player reaches the push-through target within push-through arrival distance.
- `door-push-through-local` completes with valid source-threshold and push-through evidence.
- A committed recovery path explicitly marks push-through proof for the same step.

`DoorEntryAdvance` may target the final destination only when one of these is true:
- Current zone is no longer equivalent to the source zone.
- Source-zone extended bridge proof is complete for the same step.
- No constructible source-zone extended bridge exists and the executor records that fallback explicitly.

## Forbidden Regressions

Once a stage has valid completion proof for a step:
- Do not target an earlier stage for that step unless an explicit recovery state owns that regression.
- Do not reuse a completed local proxy as a new movement goal for the same stage.
- Do not release from source-zone bridge targets to final `EntryWaypoint` while still in source zone unless bridge proof is complete or bridge is impossible.
- Do not suppress generic fallback for a stage unless the contract provides another valid target or failure detail.
- Do not make route-name-specific stage rules; use geometry/proof conditions.

## Offline-Checkable Invariants

These checks are meaningful with current imported artifacts:
- Latest door sweep must be complete and build-stamped.
- No failed door should end in source zone with `stage=DoorEntryAdvance`, `rawContext=door-entry-advance`, `localContext=<null>`, and final `EntryWaypoint` unless the failure detail says no source-zone bridge was constructible.
- No failed door should end in source zone with `stage=DoorEntryAdvance` and `localContext=door-entry-advance-local` if the local target is at or behind the already completed push-through/extended bridge proxy.
- No `DoorThresholdAdvance` `local-goal-unresolved` failure should also report a completed threshold local proxy unless release evidence or an explicit next fallback is recorded.
- Passing control doors must remain present and passed after refactor.

Run `.\scripts\Test-DoorExecutorContract.ps1` after importing door sweeps. The script writes `artifacts\navigation\door_executor_contract.validation.json` and `.summary.txt`; use `-WarnOnly` when inspecting known-bad current artifacts without failing the command.

These checks need structured runtime markers to become stronger:
- Stage entered
- Stage completion proof accepted
- Next stage selected
- Source-zone bridge constructed or rejected with reason
- Recovery state entered and exited

## Runtime Acceptance Set

Current failed doors from `299C4BAD...`:
- `bathroom2 -> bedroom`
- `bathroom2 -> dorian_bathroom2_2`
- `bedroom_closet -> bedroom`
- `gym -> gym_closet`
- `gym_closet -> gym`
- `hallway -> office`
- `office -> office_closet`

Passing controls:
- `bathroom1 -> hallway`
- `bedroom -> bathroom2`
- `dorian_bathroom2_2 -> bathroom2`
- `office -> hallway`
- `office_closet -> office`
- `upper_hallway -> gym`

Acceptance target:
- All seven current failed doors pass or fail with a new contract-specific, non-loop diagnostic.
- Passing controls remain passed.
- No new route-name-specific door logic is added.
