using System;
using System.Globalization;
using UnityEngine;

namespace DateEverythingAccess
{
    internal sealed partial class AccessibilityWatcher
    {
        private enum DoorPostInteractionState
        {
            None = 0,
            Threshold = 1,
            Handoff = 2,
            PushThrough = 3,
            ExtendedBridge = 4,
            FinalEntryLocal = 5,
            FinalEntryRaw = 6,
        }

        private enum DoorSourceZonePostThresholdMode
        {
            None = 0,
            HoldExtended = 1,
            BlockFinalEntryAdvance = 2,
            RetainExtendedForFinalEntryWaypoint = 3,
        }

        private struct DoorPostInteractionTargetDecision
        {
            public DoorPostInteractionState State;
            public Vector3 Position;
            public NavigationTargetKind TargetKind;
            public string RawContext;
            public string Detail;

            public static DoorPostInteractionTargetDecision Create(
                DoorPostInteractionState state,
                Vector3 position,
                NavigationTargetKind targetKind,
                string rawContext,
                string detail)
            {
                return new DoorPostInteractionTargetDecision
                {
                    State = state,
                    Position = position,
                    TargetKind = targetKind,
                    RawContext = rawContext,
                    Detail = detail,
                };
            }
        }

        private bool TryGetDoorTransitionSweepNavigationTargetCore(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (_transitionSweepSession == null ||
                _transitionSweepSession.Kind != TransitionSweepKind.Door ||
                _transitionSweepSession.Phase != TransitionSweepPhase.Running ||
                step == null ||
                step.Kind != NavigationGraph.StepKind.Door)
            {
                return false;
            }

            if (!string.Equals(
                BuildNavigationStepKey(step),
                BuildNavigationStepKey(_transitionSweepSession.CurrentStep),
                StringComparison.Ordinal))
            {
                return false;
            }

            return TryGetDoorNavigationTargetCore(
                step,
                currentZone,
                playerPosition,
                _transitionSweepSession.DoorInteractionTriggered,
                _transitionSweepSession.DoorPushThroughPosition,
                ref _transitionSweepSession.DoorPostThresholdCommitted,
                out position,
                out targetKind);
        }

        private bool TryGetDoorTraversalNavigationTargetCore(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            return TryGetDoorNavigationTargetCore(
                step,
                currentZone,
                playerPosition,
                _doorTraversalInteractionTriggered,
                _doorTraversalPushThroughPosition,
                ref _doorTraversalPostThresholdCommitted,
                out position,
                out targetKind);
        }

        private bool TryGetDoorNavigationTargetCore(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            bool interactionTriggered,
            Vector3 pushThroughPosition,
            ref bool postThresholdCommitted,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                string.IsNullOrEmpty(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
            {
                return false;
            }

            if (interactionTriggered && pushThroughPosition != Vector3.zero)
            {
                return TryGetDoorTraversalPostInteractionNavigationTarget(
                    step,
                    currentZone,
                    playerPosition,
                    pushThroughPosition,
                    ref postThresholdCommitted,
                    out position,
                    out targetKind);
            }

            if (interactionTriggered)
                return false;

            if (!TryGetDoorInteractionRetryTarget(step, currentZone, playerPosition, out position, out string retryTargetSource))
                return false;

            targetKind = NavigationTargetKind.TransitionInteractable;
            LogNavigationTrackerDebug(
                "Next navigation target kind=TransitionInteractable position=" + FormatVector3(position) +
                " retryTargetSource=" + retryTargetSource +
                " stage=DoorInteractionRetry" +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool TryGetDoorTraversalPostInteractionNavigationTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            Vector3 pushThroughPosition,
            ref bool postThresholdCommitted,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (step == null || pushThroughPosition == Vector3.zero)
                return false;

            float sourceThresholdDistance = float.PositiveInfinity;
            float handoffDistance = float.PositiveInfinity;
            Vector3 sourceTarget = Vector3.zero;
            Vector3 handoffTarget = Vector3.zero;
            bool hasValidHandoffTarget = false;
            if (TryGetDoorThresholdAdvanceTarget(step, currentZone, out sourceTarget))
            {
                sourceThresholdDistance = GetPlanarDistanceToTarget(playerPosition, sourceTarget);
                if (TryGetDoorThresholdHandoffTarget(
                        step,
                        currentZone,
                        sourceTarget,
                        pushThroughPosition,
                        out handoffTarget))
                {
                    hasValidHandoffTarget = true;
                    handoffDistance = GetPlanarDistanceToTarget(playerPosition, handoffTarget);
                }
            }

            float pushThroughDistance = GetPlanarDistanceToTarget(playerPosition, pushThroughPosition);
            float thresholdAdvanceArrivalDistance = GetRawNavigationGoalReachedDistance("door-threshold-advance");
            float pushThroughLocalArrivalDistance = DoorPushThroughLocalNavigationGoalReachedDistance;
            float handoffArrivalDistance = GetLocalNavigationGoalReachedDistance("door-threshold-handoff");
            bool thresholdAdvanceLocalReached = IsDoorSourceLocalGoalCompleted(
                step,
                "door-threshold-advance-local");
            bool thresholdHandoffLocalReached = IsDoorSourceLocalGoalCompleted(
                step,
                "door-threshold-handoff-local");
            bool pushThroughLocalReached = IsDoorSourceLocalGoalCompleted(
                step,
                "door-push-through-local");
            bool entryAdvanceExtendedLocalCompleted = IsDoorSourceLocalGoalCompleted(
                step,
                "door-entry-advance-extended-local");
            bool entryAdvanceExtendedProofSatisfied = IsDoorEntryAdvanceExtendedProofSatisfied(
                step,
                currentZone,
                playerPosition,
                pushThroughPosition,
                entryAdvanceExtendedLocalCompleted);
            bool sourceThresholdReached = sourceTarget != Vector3.zero &&
                sourceThresholdDistance <= thresholdAdvanceArrivalDistance;
            bool sourceThresholdLocalProxyReached = sourceTarget != Vector3.zero &&
                thresholdAdvanceLocalReached &&
                ShouldAcceptDoorThresholdAdvanceLocalProxyAsSatisfied(
                    step,
                    currentZone,
                    playerPosition,
                    sourceTarget,
                    pushThroughPosition,
                    sourceThresholdDistance,
                    thresholdAdvanceArrivalDistance,
                    hasValidHandoffTarget,
                    handoffDistance,
                    handoffArrivalDistance);
            bool sourceThresholdSatisfied =
                sourceThresholdReached ||
                sourceThresholdLocalProxyReached;
            bool handoffReached = hasValidHandoffTarget &&
                handoffTarget != Vector3.zero &&
                (handoffDistance <= handoffArrivalDistance ||
                 thresholdHandoffLocalReached);
            bool wouldKeepDoorThresholdAdvance = sourceTarget != Vector3.zero &&
                ShouldKeepDoorThresholdAdvance(playerPosition, sourceTarget, pushThroughPosition);
            bool shouldKeepDoorThresholdAdvance =
                wouldKeepDoorThresholdAdvance &&
                !sourceThresholdSatisfied;
            if (wouldKeepDoorThresholdAdvance && sourceThresholdSatisfied)
            {
                LogNavigationTrackerDebug(
                    "Released door threshold advance because source threshold is reached" +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " thresholdArrivalDistance=" + thresholdAdvanceArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " sourceThresholdLocalProxyReached=" + sourceThresholdLocalProxyReached +
                    " hasValidHandoffTarget=" + hasValidHandoffTarget +
                    " handoffDistance=" + handoffDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " handoffArrivalDistance=" + handoffArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
            }

            bool shouldCommitPostThreshold = postThresholdCommitted ||
                (sourceTarget != Vector3.zero &&
                 sourceThresholdSatisfied &&
                 !shouldKeepDoorThresholdAdvance &&
                 (!hasValidHandoffTarget || handoffReached));
            if (shouldCommitPostThreshold)
                postThresholdCommitted = true;

            bool shouldBypassDoorThresholdAdvance =
                !shouldCommitPostThreshold &&
                sourceTarget != Vector3.zero &&
                !hasValidHandoffTarget &&
                pushThroughPosition != Vector3.zero &&
                (sourceThresholdDistance <= DoorThresholdAdvanceBypassDistance ||
                 (sourceThresholdDistance <= DoorTraversalClearanceDistance &&
                  pushThroughDistance <= DoorPushThroughArrivalDistance + DoorTraversalClearanceDistance));
            if (shouldBypassDoorThresholdAdvance)
            {
                LogNavigationTrackerDebug(
                    "Bypassed door threshold advance due to unavailable handoff target" +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorThresholdAdvance" +
                    " step=" + DescribeNavigationStep(step));
            }

            float noHandoffPushThroughCommitThreshold =
                DoorPushThroughArrivalDistance + DoorPushThroughNoHandoffCommitTolerance;
            bool isNoHandoffPushThroughCommitState =
                !hasValidHandoffTarget &&
                IsDoorNoHandoffPushThroughCommitEligible(
                    sourceTarget,
                    sourceThresholdDistance,
                    pushThroughPosition,
                    pushThroughDistance,
                    extraTolerance: 0f,
                    out noHandoffPushThroughCommitThreshold);
            if (!isNoHandoffPushThroughCommitState &&
                !hasValidHandoffTarget &&
                pushThroughLocalReached &&
                sourceTarget != Vector3.zero &&
                pushThroughPosition != Vector3.zero &&
                sourceThresholdDistance <= DoorThresholdAdvanceBypassDistance &&
                pushThroughDistance <= noHandoffPushThroughCommitThreshold)
            {
                isNoHandoffPushThroughCommitState = true;
                LogNavigationTrackerDebug(
                    "Accepted completed door push-through local goal as no-handoff commit" +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughCommitThreshold=" + noHandoffPushThroughCommitThreshold.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
            }
            bool shouldCommitPostThresholdWithoutHandoff =
                shouldBypassDoorThresholdAdvance &&
                isNoHandoffPushThroughCommitState;
            if (shouldCommitPostThresholdWithoutHandoff)
            {
                postThresholdCommitted = true;
                shouldCommitPostThreshold = true;
                LogNavigationTrackerDebug(
                    "Committed door post-threshold state without snapped handoff target" +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughCommitThreshold=" + noHandoffPushThroughCommitThreshold.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorPushThrough" +
                    " step=" + DescribeNavigationStep(step));
            }

            bool isStillInSourceZone =
                !string.IsNullOrEmpty(step.FromZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.FromZone);
            if (shouldCommitPostThreshold &&
                TryGetDoorCommittedSourceRecoveryTarget(
                    step,
                    currentZone,
                    playerPosition,
                    isStillInSourceZone,
                    hasValidHandoffTarget,
                    sourceTarget,
                    sourceThresholdDistance,
                    pushThroughPosition,
                    pushThroughDistance,
                    pushThroughLocalReached,
                    out position,
                    out targetKind))
            {
                return true;
            }

            bool shouldHoldPushThroughAfterNoHandoffCommit =
                shouldCommitPostThreshold &&
                isStillInSourceZone &&
                isNoHandoffPushThroughCommitState &&
                pushThroughPosition != Vector3.zero &&
                pushThroughDistance > pushThroughLocalArrivalDistance &&
                !pushThroughLocalReached;
            if (shouldHoldPushThroughAfterNoHandoffCommit)
            {
                return TryUseDoorPostInteractionTargetDecision(
                    DoorPostInteractionTargetDecision.Create(
                        DoorPostInteractionState.PushThrough,
                        pushThroughPosition,
                        NavigationTargetKind.ZoneFallback,
                        "door-push-through",
                        "release=push-through-local-arrival" +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " arrivalDistance=" + pushThroughLocalArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " reason=no-handoff-commit"),
                    step,
                    out position,
                    out targetKind);
            }

            bool shouldHoldPushThroughInSourceZone =
                shouldCommitPostThreshold &&
                isStillInSourceZone &&
                pushThroughPosition != Vector3.zero &&
                pushThroughDistance > DoorPushThroughLocalNavigationGoalReachedDistance &&
                !isNoHandoffPushThroughCommitState &&
                !pushThroughLocalReached;
            if (shouldHoldPushThroughInSourceZone)
            {
                return TryUseDoorPostInteractionTargetDecision(
                    DoorPostInteractionTargetDecision.Create(
                        DoorPostInteractionState.PushThrough,
                        pushThroughPosition,
                        NavigationTargetKind.ZoneFallback,
                        "door-push-through",
                        "release=push-through-local-completed-or-zone-changed" +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture)),
                    step,
                    out position,
                    out targetKind);
            }

            if (shouldCommitPostThreshold &&
                isStillInSourceZone &&
                isNoHandoffPushThroughCommitState &&
                pushThroughDistance <= pushThroughLocalArrivalDistance)
            {
                LogNavigationTrackerDebug(
                    "Promoting door entry advance after no-handoff push-through commit" +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorEntryAdvance" +
                    " step=" + DescribeNavigationStep(step));
            }

            bool shouldContinueDoorThresholdAdvance =
                !shouldCommitPostThreshold &&
                !shouldBypassDoorThresholdAdvance &&
                sourceTarget != Vector3.zero &&
                (shouldKeepDoorThresholdAdvance ||
                 !sourceThresholdSatisfied);
            if (shouldContinueDoorThresholdAdvance)
            {
                return TryUseDoorPostInteractionTargetDecision(
                    DoorPostInteractionTargetDecision.Create(
                        DoorPostInteractionState.Threshold,
                        sourceTarget,
                        NavigationTargetKind.ZoneFallback,
                        "door-threshold-advance",
                        "proof=source-threshold-not-satisfied" +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture)),
                    step,
                    out position,
                    out targetKind);
            }

            if (!shouldCommitPostThreshold &&
                sourceTarget != Vector3.zero &&
                hasValidHandoffTarget &&
                handoffTarget != Vector3.zero &&
                handoffDistance > GetLocalNavigationGoalReachedDistance("door-threshold-handoff") &&
                !shouldKeepDoorThresholdAdvance &&
                HasMeaningfulDoorThresholdClearance(
                    sourceTarget,
                    pushThroughPosition,
                    handoffTarget) &&
                (sourceThresholdSatisfied ||
                 sourceThresholdDistance <= DoorPushThroughArrivalDistance ||
                 pushThroughDistance <= DoorPushThroughArrivalDistance))
            {
                return TryUseDoorPostInteractionTargetDecision(
                    DoorPostInteractionTargetDecision.Create(
                        DoorPostInteractionState.Handoff,
                        handoffTarget,
                        NavigationTargetKind.ZoneFallback,
                        "door-threshold-handoff",
                        "proof=source-threshold-satisfied" +
                        " release=handoff-arrival-or-local-completion" +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " handoffDistance=" + handoffDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture)),
                    step,
                    out position,
                    out targetKind);
            }

            if (!shouldCommitPostThreshold && pushThroughPosition != Vector3.zero)
            {
                return TryUseDoorPostInteractionTargetDecision(
                    DoorPostInteractionTargetDecision.Create(
                        DoorPostInteractionState.PushThrough,
                        pushThroughPosition,
                        NavigationTargetKind.ZoneFallback,
                        "door-push-through",
                        "proof=threshold-stage-released" +
                        " release=push-through-arrival-or-local-completion" +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture)),
                    step,
                    out position,
                    out targetKind);
            }

            if (TryResolveDoorSourceZonePostThresholdTarget(
                    step,
                    currentZone,
                    playerPosition,
                    pushThroughPosition,
                    pushThroughDistance,
                    shouldCommitPostThreshold,
                    isStillInSourceZone,
                    pushThroughLocalReached,
                    entryAdvanceExtendedProofSatisfied,
                    out position,
                    out targetKind))
            {
                return true;
            }

            if (TryGetDoorTraversalDestinationTarget(step, out Vector3 destinationTarget, out NavigationTargetKind destinationTargetKind))
            {
                string finalRawContext = GetDoorEntryAdvanceRawContextForFinalTarget(
                    step,
                    currentZone,
                    shouldCommitPostThreshold,
                    isStillInSourceZone,
                    entryAdvanceExtendedProofSatisfied,
                    pushThroughPosition,
                    destinationTarget);
                return TryUseDoorPostInteractionTargetDecision(
                    DoorPostInteractionTargetDecision.Create(
                        DoorPostInteractionState.FinalEntryRaw,
                        destinationTarget,
                        destinationTargetKind,
                        finalRawContext,
                        "proof=post-threshold-commit" +
                        " extendedBridgeProof=" + entryAdvanceExtendedProofSatisfied),
                    step,
                    out position,
                    out targetKind);
            }

            if (TryGetZonePosition(step.ToZone, out position))
            {
                string finalFallbackRawContext = GetDoorEntryAdvanceRawContextForFinalTarget(
                    step,
                    currentZone,
                    shouldCommitPostThreshold,
                    isStillInSourceZone,
                    entryAdvanceExtendedProofSatisfied,
                    pushThroughPosition,
                    position);
                return TryUseDoorPostInteractionTargetDecision(
                    DoorPostInteractionTargetDecision.Create(
                        DoorPostInteractionState.FinalEntryRaw,
                        position,
                        NavigationTargetKind.ZoneFallback,
                        finalFallbackRawContext,
                        "proof=post-threshold-commit" +
                        " extendedBridgeProof=" + entryAdvanceExtendedProofSatisfied),
                    step,
                    out position,
                    out targetKind);
            }

            return false;
        }

        private bool TryUseDoorPostInteractionTargetDecision(
            DoorPostInteractionTargetDecision decision,
            NavigationGraph.PathStep step,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (decision.State == DoorPostInteractionState.None ||
                decision.Position == Vector3.zero ||
                string.IsNullOrWhiteSpace(decision.RawContext))
            {
                return false;
            }

            position = decision.Position;
            targetKind = decision.TargetKind;
            _rawNavigationTargetContext = decision.RawContext;
            LogNavigationTrackerDebug(
                "Next door post-interaction target state=" + decision.State +
                " kind=" + targetKind +
                " position=" + FormatVector3(position) +
                " stage=" + GetDoorPostInteractionStageName(decision.RawContext) +
                " rawContext=" + decision.RawContext +
                " detail=" + (decision.Detail ?? "<null>") +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool TryResolveDoorSourceZonePostThresholdTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            Vector3 pushThroughPosition,
            float pushThroughDistance,
            bool shouldCommitPostThreshold,
            bool isStillInSourceZone,
            bool pushThroughLocalReached,
            bool entryAdvanceExtendedProofSatisfied,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (!shouldCommitPostThreshold ||
                !isStillInSourceZone ||
                step == null ||
                pushThroughPosition == Vector3.zero ||
                !TryGetDoorTraversalDestinationTarget(step, out Vector3 destinationTarget, out NavigationTargetKind destinationTargetKind) ||
                destinationTarget == Vector3.zero ||
                !TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                    step,
                    currentZone,
                    pushThroughPosition,
                    destinationTarget,
                    out Vector3 extendedEntryAdvanceTarget) ||
                extendedEntryAdvanceTarget == Vector3.zero)
            {
                return false;
            }

            float extendedEntryAdvanceDistance = GetPlanarDistanceToTarget(playerPosition, extendedEntryAdvanceTarget);
            DoorSourceZonePostThresholdMode mode = DoorSourceZonePostThresholdMode.None;
            if (pushThroughLocalReached &&
                extendedEntryAdvanceDistance > GetRawNavigationGoalReachedDistance("door-entry-advance-extended"))
            {
                mode = DoorSourceZonePostThresholdMode.HoldExtended;
            }
            else if (!entryAdvanceExtendedProofSatisfied)
            {
                mode = DoorSourceZonePostThresholdMode.BlockFinalEntryAdvance;
            }
            else if (destinationTargetKind == NavigationTargetKind.EntryWaypoint)
            {
                mode = DoorSourceZonePostThresholdMode.RetainExtendedForFinalEntryWaypoint;
            }

            if (mode == DoorSourceZonePostThresholdMode.None)
            {
                return false;
            }

            if (ShouldReleaseDoorSourceZoneExtendedBridgeToExplicitFinalFallback(
                    step,
                    currentZone,
                    playerPosition,
                    extendedEntryAdvanceTarget))
            {
                LogNavigationTrackerDebug(
                    "Released source-zone door extended bridge to explicit final fallback after proof" +
                    " mode=" + mode +
                    " extendedEntryAdvanceTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                    " destinationTarget=" + FormatVector3(destinationTarget) +
                    " destinationTargetKind=" + destinationTargetKind +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            return TryUseDoorPostInteractionTargetDecision(
                DoorPostInteractionTargetDecision.Create(
                    DoorPostInteractionState.ExtendedBridge,
                    extendedEntryAdvanceTarget,
                    NavigationTargetKind.ZoneFallback,
                    "door-entry-advance-extended",
                    "mode=" + mode +
                    " release=raw-extended-arrival-or-source-zone-exit" +
                    " destinationTarget=" + FormatVector3(destinationTarget) +
                    " destinationTargetKind=" + destinationTargetKind +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " extendedEntryAdvanceDistance=" + extendedEntryAdvanceDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " entryAdvanceExtendedProofSatisfied=" + entryAdvanceExtendedProofSatisfied +
                    " pushThroughLocalReached=" + pushThroughLocalReached),
                step,
                out position,
                out targetKind);
        }

        private string GetDoorEntryAdvanceRawContextForFinalTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            bool shouldCommitPostThreshold,
            bool isStillInSourceZone,
            bool entryAdvanceExtendedProofSatisfied,
            Vector3 pushThroughPosition,
            Vector3 destinationTarget)
        {
            if (!shouldCommitPostThreshold ||
                !isStillInSourceZone ||
                destinationTarget == Vector3.zero)
            {
                return "door-entry-advance";
            }

            if (TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                    step,
                    currentZone,
                    pushThroughPosition,
                    destinationTarget,
                    out _))
            {
                if (ShouldReleaseDoorSourceZoneExtendedBridgeToExplicitFinalFallback(
                        step,
                        currentZone,
                        BetterPlayerControl.Instance != null
                            ? BetterPlayerControl.Instance.transform.position
                            : Vector3.zero,
                        destinationTarget))
                {
                    LogNavigationTrackerDebug(
                        "No usable source-zone door entry bridge remains after proof; allowing explicit final fallback" +
                        " destinationTarget=" + FormatVector3(destinationTarget) +
                        " step=" + DescribeNavigationStep(step));
                    return "door-entry-advance-no-source-bridge";
                }

                if (entryAdvanceExtendedProofSatisfied)
                {
                    LogNavigationTrackerDebug(
                        "Released final door entry advance from extended context after bridge proof success" +
                        " destinationTarget=" + FormatVector3(destinationTarget) +
                        " step=" + DescribeNavigationStep(step));
                    return "door-entry-advance";
                }

                return "door-entry-advance-extended";
            }

            LogNavigationTrackerDebug(
                "No source-zone door entry bridge constructible; allowing final entry advance" +
                " destinationTarget=" + FormatVector3(destinationTarget) +
                " step=" + DescribeNavigationStep(step));
            return "door-entry-advance-no-source-bridge";
        }

        private bool IsFocusedClosetDeadlockStep(NavigationGraph.PathStep step)
        {
            if (step == null)
                return false;

            string stepKey = BuildNavigationStepKey(step);
            return string.Equals(stepKey, "transition:gym_closet->gym", StringComparison.Ordinal) ||
                string.Equals(stepKey, "transition:office->office_closet", StringComparison.Ordinal);
        }

        private bool ShouldReleaseDoorSourceZoneExtendedBridgeToExplicitFinalFallback(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            Vector3 destinationTarget)
        {
            if (step == null ||
                playerPosition == Vector3.zero ||
                destinationTarget == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                !IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") ||
                !TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 pushThroughPosition) ||
                pushThroughPosition == Vector3.zero ||
                !TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                    step,
                    currentZone,
                    pushThroughPosition,
                    destinationTarget,
                    out Vector3 extendedEntryAdvanceTarget) ||
                extendedEntryAdvanceTarget == Vector3.zero)
            {
                return false;
            }

            if (GetPlanarDistanceToTarget(playerPosition, extendedEntryAdvanceTarget) <=
                GetRawNavigationGoalReachedDistance("door-entry-advance-extended"))
            {
                return false;
            }

            bool finalEntryLocalCompleted = IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-local");
            if (finalEntryLocalCompleted)
            {
                LogNavigationTrackerDebug(
                    "Completed post-proof final door entry local proxy; reevaluating source-zone extended bridge before fallback" +
                    " destinationTarget=" + FormatVector3(destinationTarget) +
                    " extendedEntryAdvanceTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                    " playerPosition=" + FormatVector3(playerPosition) +
                    " step=" + DescribeNavigationStep(step));
            }

            bool isFocusedClosetDeadlockStep = IsFocusedClosetDeadlockStep(step);
            if (isFocusedClosetDeadlockStep &&
                finalEntryLocalCompleted &&
                HasDoorRetainedExtendedNoProgressReleaseRequest(step))
            {
                LogNavigationTrackerDebug(
                    "Releasing retained source-zone extended bridge after no-progress reused local loop" +
                    " destinationTarget=" + FormatVector3(destinationTarget) +
                    " extendedEntryAdvanceTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                    " playerPosition=" + FormatVector3(playerPosition) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (TryGetDoorSourceLocalPlanningGoal(
                    step,
                    currentZone,
                    playerPosition,
                    extendedEntryAdvanceTarget,
                    "door-entry-advance-extended-local",
                    out Vector3 extendedBridgePlanningGoal) &&
                extendedBridgePlanningGoal != Vector3.zero &&
                ShouldUseLocalNavigationGoal(
                    playerPosition,
                    extendedBridgePlanningGoal,
                    GetLocalNavigationGoalReachedDistance("door-entry-advance-extended-local")))
            {
                if (isFocusedClosetDeadlockStep)
                {
                    LogNavigationTrackerDebug(
                        "Focused closet deadlock trace: retained source-zone extended bridge after proof" +
                        " currentZone=" + currentZone +
                        " playerPosition=" + FormatVector3(playerPosition) +
                        " destinationTarget=" + FormatVector3(destinationTarget) +
                        " extendedEntryAdvanceTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                        " extendedBridgePlanningGoal=" + FormatVector3(extendedBridgePlanningGoal) +
                        " finalEntryLocalCompleted=" + finalEntryLocalCompleted +
                        " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                        " step=" + DescribeNavigationStep(step));
                }
                return false;
            }

            LogNavigationTrackerDebug(
                "No usable source-zone door extended bridge plan remains after proof" +
                " destinationTarget=" + FormatVector3(destinationTarget) +
                " extendedEntryAdvanceTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                " playerPosition=" + FormatVector3(playerPosition) +
                " finalEntryLocalCompleted=" + finalEntryLocalCompleted +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool IsDoorEntryAdvanceExtendedProofSatisfied(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            Vector3 pushThroughPosition,
            bool entryAdvanceExtendedLocalCompleted)
        {
            if (!entryAdvanceExtendedLocalCompleted ||
                step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                playerPosition == Vector3.zero ||
                pushThroughPosition == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                !TryGetDoorTraversalDestinationTarget(step, out Vector3 destinationTarget, out _) ||
                destinationTarget == Vector3.zero ||
                !TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                    step,
                    currentZone,
                    pushThroughPosition,
                    destinationTarget,
                    out Vector3 extendedEntryAdvanceTarget) ||
                extendedEntryAdvanceTarget == Vector3.zero)
            {
                return false;
            }

            float extendedEntryAdvanceDistance = GetPlanarDistanceToTarget(
                playerPosition,
                extendedEntryAdvanceTarget);
            if (extendedEntryAdvanceDistance >
                GetRawNavigationGoalReachedDistance("door-entry-advance-extended"))
            {
                if (TryGetDoorSourceLocalPlanningGoal(
                        step,
                        currentZone,
                        playerPosition,
                        extendedEntryAdvanceTarget,
                        "door-entry-advance-extended-local",
                        out Vector3 localProofPlanningGoal) &&
                    localProofPlanningGoal != Vector3.zero)
                {
                    float localProofDistance = GetFlatDistance(playerPosition, localProofPlanningGoal);
                    float localProofArrivalDistance = GetLocalNavigationGoalReachedDistance("door-entry-advance-extended-local");
                    if (localProofDistance <= localProofArrivalDistance)
                    {
                        LogNavigationTrackerDebug(
                            "Accepted door entry advance extended proof from resolved local bridge goal" +
                            " playerPosition=" + FormatVector3(playerPosition) +
                            " rawExtendedTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                            " localProofGoal=" + FormatVector3(localProofPlanningGoal) +
                            " localProofDistance=" + localProofDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " localProofArrivalDistance=" + localProofArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }
                }

                if (TryGetDoorSourceLocalCompletedGoal(
                        step,
                        "door-entry-advance-extended-local",
                        out Vector3 completedLocalProofGoal) &&
                    completedLocalProofGoal != Vector3.zero)
                {
                    float completedLocalGoalOffset = GetFlatDistance(
                        completedLocalProofGoal,
                        extendedEntryAdvanceTarget);
                    float completedLocalGoalOffsetAcceptanceDistance = Mathf.Max(
                        GetLocalNavigationGoalReachedDistance("door-entry-advance-extended-local"),
                        GetRawNavigationGoalReachedDistance("door-entry-advance-extended"));
                    if (completedLocalGoalOffset <= completedLocalGoalOffsetAcceptanceDistance)
                    {
                        LogNavigationTrackerDebug(
                            "Accepted door entry advance extended proof from completed local bridge goal identity" +
                            " rawExtendedTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                            " completedLocalGoal=" + FormatVector3(completedLocalProofGoal) +
                            " completedLocalGoalOffset=" + completedLocalGoalOffset.ToString("0.00", CultureInfo.InvariantCulture) +
                            " completedLocalGoalOffsetAcceptanceDistance=" + completedLocalGoalOffsetAcceptanceDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    if (TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 completedProofSourceTarget) &&
                        completedProofSourceTarget != Vector3.zero &&
                        TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 completedProofPushThroughPosition) &&
                        completedProofPushThroughPosition != Vector3.zero)
                    {
                        float completedLocalForwardProgress = GetDoorThresholdForwardProgress(
                            completedProofSourceTarget,
                            completedProofPushThroughPosition,
                            completedLocalProofGoal);
                        float extendedTargetForwardProgress = GetDoorThresholdForwardProgress(
                            completedProofSourceTarget,
                            completedProofPushThroughPosition,
                            extendedEntryAdvanceTarget);
                        if (completedLocalForwardProgress > 0.25f &&
                            completedLocalForwardProgress + 0.25f >= extendedTargetForwardProgress)
                        {
                            LogNavigationTrackerDebug(
                                "Accepted door entry advance extended proof from completed local bridge forward progress" +
                                " rawExtendedTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                                " completedLocalGoal=" + FormatVector3(completedLocalProofGoal) +
                                " completedLocalForwardProgress=" + completedLocalForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                                " extendedTargetForwardProgress=" + extendedTargetForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                                " step=" + DescribeNavigationStep(step));
                            return true;
                        }
                    }

                    float completedLocalProofDistance = GetFlatDistance(playerPosition, completedLocalProofGoal);
                    float completedLocalProofArrivalDistance = GetLocalNavigationGoalReachedDistance("door-entry-advance-extended-local");
                    if (completedLocalProofDistance <= completedLocalProofArrivalDistance)
                    {
                        LogNavigationTrackerDebug(
                            "Accepted door entry advance extended proof from completed local bridge goal" +
                            " playerPosition=" + FormatVector3(playerPosition) +
                            " rawExtendedTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                            " completedLocalGoal=" + FormatVector3(completedLocalProofGoal) +
                            " completedLocalGoalDistance=" + completedLocalProofDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " completedLocalGoalArrivalDistance=" + completedLocalProofArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }
                }

                LogNavigationTrackerDebug(
                    "Door entry advance extended local completion did not satisfy raw bridge proof" +
                    " playerPosition=" + FormatVector3(playerPosition) +
                    " extendedTarget=" + FormatVector3(extendedEntryAdvanceTarget) +
                    " distance=" + extendedEntryAdvanceDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " arrivalDistance=" + GetRawNavigationGoalReachedDistance("door-entry-advance-extended").ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            return true;
        }

        private bool ShouldAcceptDoorThresholdAdvanceLocalProxyAsSatisfied(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            Vector3 sourceTarget,
            Vector3 pushThroughPosition,
            float sourceThresholdDistance,
            float thresholdArrivalDistance,
            bool hasValidHandoffTarget,
            float handoffDistance,
            float handoffArrivalDistance)
        {
            if (step == null)
            {
                return false;
            }

            if (sourceThresholdDistance <= thresholdArrivalDistance)
            {
                return true;
            }

            if (hasValidHandoffTarget)
            {
                Vector3 handoffLocalProofGoal = Vector3.zero;
                bool hasResolvedHandoffLocalProofGoal = false;
                if (handoffDistance <= handoffArrivalDistance)
                {
                    return true;
                }

                if (playerPosition != Vector3.zero &&
                    pushThroughPosition != Vector3.zero &&
                    !string.IsNullOrWhiteSpace(currentZone) &&
                    TryGetDoorSourceLocalPlanningGoal(
                        step,
                        currentZone,
                        playerPosition,
                        pushThroughPosition,
                        "door-threshold-handoff-local",
                        out handoffLocalProofGoal) &&
                    handoffLocalProofGoal != Vector3.zero)
                {
                    hasResolvedHandoffLocalProofGoal = true;
                    float handoffLocalProofDistance = GetFlatDistance(playerPosition, handoffLocalProofGoal);
                    float handoffLocalProofArrivalDistance = GetLocalNavigationGoalReachedDistance("door-threshold-handoff-local");
                    if (handoffLocalProofDistance <= handoffLocalProofArrivalDistance)
                    {
                        LogNavigationTrackerDebug(
                            "Accepted door threshold advance local proxy from resolved handoff-local proof goal" +
                            " playerPosition=" + FormatVector3(playerPosition) +
                            " handoffLocalProofGoal=" + FormatVector3(handoffLocalProofGoal) +
                            " handoffLocalProofDistance=" + handoffLocalProofDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " handoffLocalProofArrivalDistance=" + handoffLocalProofArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }
                }

                if (TryGetDoorSourceLocalCompletedGoal(
                        step,
                        "door-threshold-handoff-local",
                        out Vector3 completedHandoffLocalGoal) &&
                    completedHandoffLocalGoal != Vector3.zero &&
                    hasResolvedHandoffLocalProofGoal)
                {
                    float completedHandoffGoalOffset = GetFlatDistance(
                        completedHandoffLocalGoal,
                        handoffLocalProofGoal);
                    float completedHandoffGoalOffsetAcceptanceDistance = Mathf.Max(
                        GetLocalNavigationGoalReachedDistance("door-threshold-handoff-local"),
                        handoffArrivalDistance);
                    if (completedHandoffGoalOffset <= completedHandoffGoalOffsetAcceptanceDistance)
                    {
                        LogNavigationTrackerDebug(
                            "Accepted door threshold advance local proxy from completed handoff-local goal identity" +
                            " completedHandoffLocalGoal=" + FormatVector3(completedHandoffLocalGoal) +
                            " handoffLocalProofGoal=" + FormatVector3(handoffLocalProofGoal) +
                            " completedHandoffGoalOffset=" + completedHandoffGoalOffset.ToString("0.00", CultureInfo.InvariantCulture) +
                            " completedHandoffGoalOffsetAcceptanceDistance=" + completedHandoffGoalOffsetAcceptanceDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }
                }

                return false;
            }

            if (playerPosition != Vector3.zero &&
                sourceTarget != Vector3.zero &&
                pushThroughPosition != Vector3.zero &&
                !string.IsNullOrWhiteSpace(currentZone) &&
                TryGetDoorSourceLocalPlanningGoal(
                    step,
                    currentZone,
                    playerPosition,
                    sourceTarget,
                    "door-threshold-advance-local",
                    out Vector3 thresholdLocalProofGoal) &&
                thresholdLocalProofGoal != Vector3.zero)
            {
                float thresholdLocalProofGoalOffset = GetFlatDistance(thresholdLocalProofGoal, sourceTarget);
                if (thresholdLocalProofGoalOffset <= 0.05f)
                {
                    LogNavigationTrackerDebug(
                        "Rejected door threshold advance local proxy proof because resolved goal collapses to source threshold" +
                        " playerPosition=" + FormatVector3(playerPosition) +
                        " sourceTarget=" + FormatVector3(sourceTarget) +
                        " localProofGoal=" + FormatVector3(thresholdLocalProofGoal) +
                        " localProofGoalOffset=" + thresholdLocalProofGoalOffset.ToString("0.00", CultureInfo.InvariantCulture) +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " thresholdArrivalDistance=" + thresholdArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " hasValidHandoffTarget=" + hasValidHandoffTarget +
                        " handoffDistance=" + handoffDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " handoffArrivalDistance=" + handoffArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                }
                else
                {
                float thresholdLocalProofDistance = GetFlatDistance(playerPosition, thresholdLocalProofGoal);
                float thresholdLocalProofArrivalDistance = Mathf.Max(
                    GetLocalNavigationGoalReachedDistance("door-threshold-advance-local"),
                    DoorThresholdAdvanceProxyCompletionDistance);
                if (thresholdLocalProofDistance <= thresholdLocalProofArrivalDistance)
                {
                    LogNavigationTrackerDebug(
                        "Accepted door threshold advance local proxy from resolved local proof goal" +
                        " playerPosition=" + FormatVector3(playerPosition) +
                        " sourceTarget=" + FormatVector3(sourceTarget) +
                        " localProofGoal=" + FormatVector3(thresholdLocalProofGoal) +
                        " localProofDistance=" + thresholdLocalProofDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " localProofArrivalDistance=" + thresholdLocalProofArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }
                }
            }

            return sourceThresholdDistance <= DoorThresholdAdvanceBypassDistance;
        }

        private bool TryGetDoorCommittedSourceRecoveryTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            bool isStillInSourceZone,
            bool hasValidHandoffTarget,
            Vector3 sourceTarget,
            float sourceThresholdDistance,
            Vector3 pushThroughPosition,
            float pushThroughDistance,
            bool pushThroughLocalReached,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (!IsDoorCommittedSourceRecoveryActiveForStep(step, currentZone, isStillInSourceZone))
                return false;

            if (GetDoorCommittedSourceRecoveryStage() == DoorCommittedSourceRecoveryStage.SourceThreshold)
            {
                float sourceThresholdArrivalDistance = GetRawNavigationGoalReachedDistance("door-threshold-advance");
                bool thresholdAdvanceLocalReached = IsDoorSourceLocalGoalCompleted(
                    step,
                    "door-threshold-advance-local");
                if (sourceTarget != Vector3.zero &&
                    sourceThresholdDistance > sourceThresholdArrivalDistance &&
                    !thresholdAdvanceLocalReached)
                {
                    position = sourceTarget;
                    targetKind = NavigationTargetKind.ZoneFallback;
                    _rawNavigationTargetContext = "door-threshold-advance";
                    LogNavigationTrackerDebug(
                        "Door committed-source recovery target stage=SourceThreshold" +
                        " position=" + FormatVector3(position) +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                TryAdvanceDoorCommittedSourceRecoveryStage(DoorCommittedSourceRecoveryTrigger.SourceThresholdSatisfied);
                LogNavigationTrackerDebug(
                    "Door committed-source recovery advanced stage=PushThrough" +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " thresholdAdvanceLocalReached=" + thresholdAdvanceLocalReached +
                    " step=" + DescribeNavigationStep(step));
            }

            if (GetDoorCommittedSourceRecoveryStage() == DoorCommittedSourceRecoveryStage.PushThrough)
            {
                if (pushThroughLocalReached)
                {
                    ResetDoorCommittedSourceRecoveryState();
                    LogNavigationTrackerDebug(
                        "Door committed-source recovery completed after push-through local goal" +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                if (!hasValidHandoffTarget &&
                    IsDoorNoHandoffPushThroughCommitEligible(
                        sourceTarget,
                        sourceThresholdDistance,
                        pushThroughPosition,
                        pushThroughDistance,
                        sourceTarget != Vector3.zero &&
                        sourceThresholdDistance <= DoorThresholdAdvanceBypassDistance
                            ? DoorPushThroughRecoveryNoHandoffCommitExtraTolerance
                            : 0f,
                        out float noHandoffCommitThreshold))
                {
                    if (pushThroughPosition != Vector3.zero &&
                        pushThroughDistance > DoorPushThroughLocalNavigationGoalReachedDistance)
                    {
                        position = pushThroughPosition;
                        targetKind = NavigationTargetKind.ZoneFallback;
                        _rawNavigationTargetContext = "door-push-through";
                        LogNavigationTrackerDebug(
                            "Door committed-source recovery deferring no-handoff entry advance promotion" +
                            " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " arrivalDistance=" + DoorPushThroughLocalNavigationGoalReachedDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " commitThreshold=" + noHandoffCommitThreshold.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    ResetDoorCommittedSourceRecoveryState();
                    LogNavigationTrackerDebug(
                        "Door committed-source recovery promoted to entry advance after no-handoff push-through commit" +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " commitThreshold=" + noHandoffCommitThreshold.ToString("0.00", CultureInfo.InvariantCulture) +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                float pushThroughArrivalDistance = GetRawNavigationGoalReachedDistance("door-push-through");
                if (pushThroughPosition != Vector3.zero &&
                    pushThroughDistance > pushThroughArrivalDistance)
                {
                    position = pushThroughPosition;
                    targetKind = NavigationTargetKind.ZoneFallback;
                    _rawNavigationTargetContext = "door-push-through";
                    LogNavigationTrackerDebug(
                        "Door committed-source recovery target stage=PushThrough" +
                        " position=" + FormatVector3(position) +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                ResetDoorCommittedSourceRecoveryState();
                LogNavigationTrackerDebug(
                    "Door committed-source recovery completed" +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
            }

            return false;
        }

        private static bool TryBuildDoorSourceZoneEntryAdvanceTarget(
            Vector3 sourceTarget,
            Vector3 pushThroughPosition,
            Vector3 destinationTarget,
            out Vector3 entryAdvanceTarget)
        {
            entryAdvanceTarget = Vector3.zero;
            if (sourceTarget == Vector3.zero ||
                pushThroughPosition == Vector3.zero ||
                destinationTarget == Vector3.zero)
            {
                return false;
            }

            Vector3 destinationVector = destinationTarget - sourceTarget;
            destinationVector.y = 0f;
            float destinationDistance = destinationVector.magnitude;
            if (destinationDistance <= 0.0001f)
                return false;

            Vector3 destinationDirection = destinationVector / destinationDistance;
            float advanceDistance = Mathf.Min(
                DoorTraversalMaximumPushThroughDistance,
                destinationDistance);
            entryAdvanceTarget = sourceTarget + destinationDirection * advanceDistance;
            entryAdvanceTarget.y = pushThroughPosition.y != 0f
                ? pushThroughPosition.y
                : destinationTarget.y;

            return GetFlatDistance(entryAdvanceTarget, pushThroughPosition) >
                DoorPushThroughSourceAdvanceDistance;
        }

        private bool TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 pushThroughPosition,
            Vector3 destinationTarget,
            out Vector3 entryAdvanceTarget)
        {
            entryAdvanceTarget = Vector3.zero;
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                string.IsNullOrWhiteSpace(currentZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                !TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceTarget))
            {
                return false;
            }

            return TryBuildDoorSourceZoneEntryAdvanceTarget(
                sourceTarget,
                pushThroughPosition,
                destinationTarget,
                out entryAdvanceTarget);
        }

        private bool IsDoorCommittedSourceRecoveryActiveForStep(
            NavigationGraph.PathStep step,
            string currentZone,
            bool isStillInSourceZone)
        {
            if (GetDoorCommittedSourceRecoveryStage() == DoorCommittedSourceRecoveryStage.None ||
                string.IsNullOrWhiteSpace(_doorCommittedSourceRecoveryStepKey))
            {
                return false;
            }

            if (step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                string.IsNullOrWhiteSpace(currentZone) ||
                !isStillInSourceZone)
            {
                ResetDoorCommittedSourceRecoveryState();
                return false;
            }

            string stepKey = BuildNavigationStepKey(step);
            if (string.IsNullOrWhiteSpace(stepKey) ||
                !string.Equals(stepKey, _doorCommittedSourceRecoveryStepKey, StringComparison.Ordinal))
            {
                ResetDoorCommittedSourceRecoveryState();
                return false;
            }

            return true;
        }

        private bool IsDoorCommittedSourceRecoveryPushThroughStage(
            NavigationGraph.PathStep step,
            string currentZone)
        {
            if (GetDoorCommittedSourceRecoveryStage() != DoorCommittedSourceRecoveryStage.PushThrough ||
                step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
            {
                return false;
            }

            string stepKey = BuildNavigationStepKey(step);
            return !string.IsNullOrWhiteSpace(stepKey) &&
                string.Equals(stepKey, _doorCommittedSourceRecoveryStepKey, StringComparison.Ordinal);
        }

        private bool ShouldSuppressGenericDoorPostInteractionLocalFallback(
            string currentZone,
            NavigationGraph.PathStep step,
            Vector3 desiredPosition,
            NavigationTargetKind targetKind)
        {
            if ((targetKind != NavigationTargetKind.ZoneFallback &&
                 targetKind != NavigationTargetKind.EntryWaypoint) ||
                step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                desiredPosition == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                !TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 pushThroughPosition) ||
                pushThroughPosition == Vector3.zero)
            {
                return false;
            }

            if (GetFlatDistance(pushThroughPosition, desiredPosition) <=
                GetRawNavigationGoalReachedDistance("door-push-through"))
            {
                LogNavigationTrackerDebug(
                    "Suppressed generic door local fallback stage=DoorPushThrough" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (string.Equals(_rawNavigationTargetContext, "door-threshold-advance", StringComparison.Ordinal) &&
                IsDoorSourceLocalGoalCompleted(step, "door-threshold-advance-local") &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceThresholdTarget) &&
                sourceThresholdTarget != Vector3.zero &&
                GetFlatDistance(sourceThresholdTarget, desiredPosition) <= DoorPushThroughSourceAdvanceDistance)
            {
                Vector3 playerPosition = BetterPlayerControl.Instance != null
                    ? BetterPlayerControl.Instance.transform.position
                    : Vector3.zero;
                float sourceThresholdDistance = playerPosition != Vector3.zero
                    ? GetPlanarDistanceToTarget(playerPosition, sourceThresholdTarget)
                    : float.PositiveInfinity;
                bool hasValidHandoffTarget = TryGetDoorThresholdHandoffTarget(
                    step,
                    currentZone,
                    sourceThresholdTarget,
                    pushThroughPosition,
                    out Vector3 fallbackHandoffTarget);
                float handoffDistance = hasValidHandoffTarget && playerPosition != Vector3.zero
                    ? GetPlanarDistanceToTarget(playerPosition, fallbackHandoffTarget)
                    : float.PositiveInfinity;
                if (!ShouldAcceptDoorThresholdAdvanceLocalProxyAsSatisfied(
                        step,
                        currentZone,
                        playerPosition,
                        sourceThresholdTarget,
                        pushThroughPosition,
                        sourceThresholdDistance,
                        GetRawNavigationGoalReachedDistance("door-threshold-advance"),
                        hasValidHandoffTarget,
                        handoffDistance,
                        GetLocalNavigationGoalReachedDistance("door-threshold-handoff")))
                {
                    LogNavigationTrackerDebug(
                        "Allowed generic door threshold fallback after local proxy without release evidence" +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " hasValidHandoffTarget=" + hasValidHandoffTarget +
                        " handoffDistance=" + handoffDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                LogNavigationTrackerDebug(
                    "Suppressed generic door local fallback stage=DoorThresholdAdvance after local proxy" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (IsDoorTraversalPostThresholdCommitted(step) &&
                string.Equals(_rawNavigationTargetContext, "door-entry-advance-extended", StringComparison.Ordinal) &&
                IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local"))
            {
                if (IsFocusedClosetDeadlockStep(step))
                {
                    LogNavigationTrackerDebug(
                        "Focused closet deadlock trace: suppressed generic fallback for raw extended bridge" +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " targetKind=" + targetKind +
                        " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                        " step=" + DescribeNavigationStep(step));
                }
                LogNavigationTrackerDebug(
                    "Suppressed generic door local fallback stage=DoorEntryAdvanceExtended" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (IsDoorTraversalPostThresholdCommitted(step) &&
                TryGetDoorTraversalDestinationTarget(step, out Vector3 destinationTarget, out NavigationTargetKind destinationTargetKind) &&
                destinationTarget != Vector3.zero &&
                destinationTargetKind == targetKind &&
                GetFlatDistance(destinationTarget, desiredPosition) <=
                GetRawNavigationGoalReachedDistance("door-entry-advance"))
            {
                if (string.Equals(_rawNavigationTargetContext, "door-entry-advance-no-source-bridge", StringComparison.Ordinal))
                {
                    LogNavigationTrackerDebug(
                        "Allowed generic door local fallback stage=DoorEntryAdvance because source-zone bridge is unavailable" +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " destinationTarget=" + FormatVector3(destinationTarget) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                Vector3 playerPosition = BetterPlayerControl.Instance != null
                    ? BetterPlayerControl.Instance.transform.position
                    : Vector3.zero;
                LogNavigationTrackerDebug(
                    "Suppressed generic door local fallback stage=DoorEntryAdvance" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " pushThroughPosition=" + FormatVector3(pushThroughPosition) +
                    " destinationTarget=" + FormatVector3(destinationTarget) +
                    " playerToPushThrough=" + GetFlatDistance(playerPosition, pushThroughPosition).ToString("0.00", CultureInfo.InvariantCulture) +
                    " playerToDestination=" + GetFlatDistance(playerPosition, destinationTarget).ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughLocalCompleted=" + IsDoorSourceLocalGoalCompleted(step, "door-push-through-local") +
                    " entryAdvanceLocalCompleted=" + IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-local") +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (!string.Equals(_rawNavigationTargetContext, "door-threshold-handoff", StringComparison.Ordinal))
                return false;

            if (!TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceTarget) ||
                sourceTarget == Vector3.zero ||
                !TryGetDoorThresholdHandoffTarget(
                    step,
                    currentZone,
                    sourceTarget,
                    pushThroughPosition,
                    out Vector3 handoffTarget) ||
                handoffTarget == Vector3.zero ||
                GetFlatDistance(handoffTarget, desiredPosition) >
                GetRawNavigationGoalReachedDistance("door-threshold-handoff"))
            {
                return false;
            }

            LogNavigationTrackerDebug(
                "Suppressed generic door local fallback stage=DoorThresholdHandoff" +
                " desiredPosition=" + FormatVector3(desiredPosition) +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool TryGetDoorInteractionRetryTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out Vector3 position,
            out string targetSource)
        {
            position = Vector3.zero;
            targetSource = null;
            if (step == null)
                return false;

            if (TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceTarget) &&
                sourceTarget != Vector3.zero)
            {
                if (step.ConnectorObjectPosition != Vector3.zero)
                {
                    float sourceToConnectorDistance = GetFlatDistance(sourceTarget, step.ConnectorObjectPosition);
                    float playerToSourceDistance = GetFlatDistance(playerPosition, sourceTarget);
                    float playerToConnectorDistance = GetFlatDistance(playerPosition, step.ConnectorObjectPosition);
                    float maxSourceRetryDistance = AutoWalkConnectorSearchDistance + DoorTraversalClearanceDistance;
                    if (sourceToConnectorDistance > maxSourceRetryDistance &&
                        playerToConnectorDistance + 0.35f < playerToSourceDistance)
                    {
                        position = step.ConnectorObjectPosition;
                        TrySnapDoorSourceNavigationTarget(
                            step,
                            currentZone,
                            position,
                            DoorTraversalClearanceDistance + DoorPushThroughArrivalDistance,
                            "door-interaction-retry-connector-fallback",
                            out position);
                        targetSource = "connector-fallback";
                        return true;
                    }
                }

                position = sourceTarget;
                targetSource = "threshold";
                return true;
            }

            if (step.ConnectorObjectPosition != Vector3.zero)
            {
                position = step.ConnectorObjectPosition;
                TrySnapDoorSourceNavigationTarget(
                    step,
                    currentZone,
                    position,
                    DoorTraversalClearanceDistance + DoorPushThroughArrivalDistance,
                    "door-interaction-retry",
                    out position);
                targetSource = "connector";
                return true;
            }

            if (step.FromWaypoint != Vector3.zero)
            {
                position = step.FromWaypoint;
                targetSource = "from_waypoint";
                return true;
            }

            position = playerPosition;
            targetSource = "player";
            return true;
        }

        private static bool IsDoorNoHandoffPushThroughCommitEligible(
            Vector3 sourceTarget,
            float sourceThresholdDistance,
            Vector3 pushThroughPosition,
            float pushThroughDistance,
            float extraTolerance,
            out float commitThreshold)
        {
            commitThreshold =
                DoorPushThroughArrivalDistance +
                DoorPushThroughNoHandoffCommitTolerance +
                Mathf.Max(0f, extraTolerance);
            if (sourceTarget == Vector3.zero ||
                pushThroughPosition == Vector3.zero ||
                pushThroughDistance > commitThreshold ||
                sourceThresholdDistance > DoorThresholdAdvanceBypassDistance)
            {
                return false;
            }

            float pushThroughForwardProgress = GetDoorThresholdForwardProgress(
                sourceTarget,
                pushThroughPosition,
                pushThroughPosition);
            return pushThroughForwardProgress > DoorTraversalClearanceDistance * 0.5f;
        }

        private bool TryResolveDoorLocalNavigationGoal(
            string currentZone,
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            Vector3 desiredPosition,
            out string planningZone,
            out Vector3 planningGoal,
            out string planningContext)
        {
            planningZone = null;
            planningGoal = Vector3.zero;
            planningContext = null;
            if (step == null || step.Kind != NavigationGraph.StepKind.Door)
                return false;

            bool isFocusedClosetDeadlockStep = IsFocusedClosetDeadlockStep(step);

            bool hasActiveDoorPushThroughPosition = TryGetActiveDoorPushThroughPosition(
                step,
                currentZone,
                out Vector3 activeDoorPushThroughPosition);
            bool isPostThresholdCommitted = IsDoorTraversalPostThresholdCommitted(step);
            bool isRawDoorPushThrough = string.Equals(
                _rawNavigationTargetContext,
                "door-push-through",
                StringComparison.Ordinal);
            bool isRawDoorEntryAdvance = string.Equals(
                _rawNavigationTargetContext,
                "door-entry-advance",
                StringComparison.Ordinal);
            bool isRawDoorEntryAdvanceWithoutSourceBridge = string.Equals(
                _rawNavigationTargetContext,
                "door-entry-advance-no-source-bridge",
                StringComparison.Ordinal);
            bool isRawDoorEntryAdvanceExtended = string.Equals(
                _rawNavigationTargetContext,
                "door-entry-advance-extended",
                StringComparison.Ordinal);
            bool isStillInSourceZone =
                !string.IsNullOrEmpty(step.FromZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.FromZone);

            if (!string.IsNullOrEmpty(step.FromZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 doorThresholdTarget))
            {
                bool isThresholdAdvanceGoal =
                    GetFlatDistance(doorThresholdTarget, desiredPosition) <= DoorPushThroughSourceAdvanceDistance;
                bool isThresholdHandoffGoal = false;
                if (hasActiveDoorPushThroughPosition &&
                    activeDoorPushThroughPosition != Vector3.zero &&
                    TryGetDoorThresholdHandoffTarget(
                        step,
                        currentZone,
                        doorThresholdTarget,
                        activeDoorPushThroughPosition,
                        out Vector3 doorThresholdHandoffTarget))
                {
                    isThresholdHandoffGoal =
                        GetFlatDistance(doorThresholdHandoffTarget, desiredPosition) <=
                        GetLocalNavigationGoalReachedDistance("door-threshold-handoff");
                }

                if ((isThresholdAdvanceGoal || isThresholdHandoffGoal) &&
                    !isRawDoorPushThrough)
                {
                    Vector3 thresholdPlanningDesiredPosition = desiredPosition;
                    bool shouldUseThresholdAdvanceLocal =
                        isThresholdAdvanceGoal &&
                        (!isThresholdHandoffGoal ||
                         string.Equals(_rawNavigationTargetContext, "door-threshold-advance", StringComparison.Ordinal));
                    string doorThresholdPlanningContext = shouldUseThresholdAdvanceLocal
                        ? "door-threshold-advance-local"
                        : "door-threshold-handoff-local";
                    if (shouldUseThresholdAdvanceLocal &&
                        IsDoorSourceLocalGoalCompleted(step, "door-threshold-advance-local"))
                    {
                        Vector3 completedThresholdHandoffTarget = Vector3.zero;
                        bool hasValidHandoffTarget = hasActiveDoorPushThroughPosition &&
                            activeDoorPushThroughPosition != Vector3.zero &&
                            TryGetDoorThresholdHandoffTarget(
                                step,
                                currentZone,
                                doorThresholdTarget,
                                activeDoorPushThroughPosition,
                                out completedThresholdHandoffTarget);
                        float handoffDistance = hasValidHandoffTarget
                            ? GetPlanarDistanceToTarget(playerPosition, completedThresholdHandoffTarget)
                            : float.PositiveInfinity;
                        if (ShouldAcceptDoorThresholdAdvanceLocalProxyAsSatisfied(
                                step,
                                currentZone,
                                playerPosition,
                                doorThresholdTarget,
                                activeDoorPushThroughPosition,
                                GetPlanarDistanceToTarget(playerPosition, doorThresholdTarget),
                                GetRawNavigationGoalReachedDistance("door-threshold-advance"),
                                hasValidHandoffTarget,
                                handoffDistance,
                                GetLocalNavigationGoalReachedDistance("door-threshold-handoff")))
                        {
                            LogNavigationTrackerDebug(
                                "Skipped completed door threshold advance local proxy; preserving raw threshold advance" +
                                " desiredPosition=" + FormatVector3(desiredPosition) +
                                " step=" + DescribeNavigationStep(step));
                            return false;
                        }

                        LogNavigationTrackerDebug(
                            "Reused completed door threshold advance local proxy because release proof is still incomplete" +
                            " desiredPosition=" + FormatVector3(desiredPosition) +
                            " step=" + DescribeNavigationStep(step));

                        if (hasValidHandoffTarget &&
                            completedThresholdHandoffTarget != Vector3.zero)
                        {
                            shouldUseThresholdAdvanceLocal = false;
                            doorThresholdPlanningContext = "door-threshold-handoff-local";
                            thresholdPlanningDesiredPosition = completedThresholdHandoffTarget;
                            LogNavigationTrackerDebug(
                                "Promoted completed door threshold advance local proxy to handoff-local planning" +
                                " handoffTarget=" + FormatVector3(thresholdPlanningDesiredPosition) +
                                " step=" + DescribeNavigationStep(step));
                        }
                    }

                    if (!TryGetDoorSourceLocalPlanningGoal(
                        step,
                        currentZone,
                        playerPosition,
                        thresholdPlanningDesiredPosition,
                        doorThresholdPlanningContext,
                        out Vector3 doorThresholdPlanningGoal))
                    {
                        return false;
                    }

                    if (!ShouldUseLocalNavigationGoal(
                            playerPosition,
                            doorThresholdPlanningGoal,
                            GetLocalNavigationGoalReachedDistance(doorThresholdPlanningContext)))
                    {
                        MarkDoorSourceLocalGoalReached(
                            BuildNavigationStepKey(step),
                            doorThresholdPlanningContext,
                            doorThresholdPlanningGoal,
                            GetFlatDistance(playerPosition, doorThresholdPlanningGoal));
                        return false;
                    }

                    planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, doorThresholdPlanningGoal);
                    planningGoal = doorThresholdPlanningGoal;
                    planningContext = doorThresholdPlanningContext;
                    return true;
                }
            }

            if ((!isPostThresholdCommitted ||
                 IsDoorCommittedSourceRecoveryPushThroughStage(step, currentZone) ||
                 (isRawDoorPushThrough &&
                  isStillInSourceZone &&
                  !IsDoorSourceLocalGoalCompleted(step, "door-push-through-local"))) &&
                hasActiveDoorPushThroughPosition &&
                GetFlatDistance(activeDoorPushThroughPosition, desiredPosition) <= 0.35f)
            {
                if (!TryGetDoorSourceLocalPlanningGoal(
                        step,
                        currentZone,
                        playerPosition,
                        desiredPosition,
                        "door-push-through-local",
                        out Vector3 doorPushThroughPlanningGoal))
                {
                    return false;
                }

                if (ShouldUseLocalNavigationGoal(
                        playerPosition,
                        doorPushThroughPlanningGoal,
                        GetLocalNavigationGoalReachedDistance("door-push-through-local")))
                {
                    planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, doorPushThroughPlanningGoal);
                    planningGoal = doorPushThroughPlanningGoal;
                    planningContext = "door-push-through-local";
                    return true;
                }

                float remainingDistance = GetFlatDistance(playerPosition, doorPushThroughPlanningGoal);
                string stepKey = BuildNavigationStepKey(step);
                MarkDoorSourceLocalGoalReached(
                    stepKey,
                    "door-push-through-local",
                    doorPushThroughPlanningGoal,
                    remainingDistance);
                TryCommitDoorPostThresholdAfterLocalPushThroughGoalReached(
                    stepKey,
                    "door-push-through-local",
                    playerPosition,
                    doorPushThroughPlanningGoal,
                    remainingDistance);
                return false;
            }

            if (isPostThresholdCommitted &&
                isRawDoorPushThrough &&
                !IsDoorPushThroughBridgeLocalGoalCompleted(step) &&
                hasActiveDoorPushThroughPosition &&
                activeDoorPushThroughPosition != Vector3.zero &&
                TryResolveDoorPushThroughBridgeLocalNavigationGoal(
                    currentZone,
                    step,
                    playerPosition,
                    desiredPosition,
                    activeDoorPushThroughPosition,
                    out planningZone,
                    out planningGoal,
                    out planningContext))
            {
                return true;
            }

            bool shouldAllowDoorEntryAdvanceSourceLocalPlanning =
                hasActiveDoorPushThroughPosition &&
                activeDoorPushThroughPosition != Vector3.zero &&
                !string.IsNullOrEmpty(step.FromZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) &&
                (isPostThresholdCommitted ||
                 isRawDoorEntryAdvance ||
                 isRawDoorEntryAdvanceWithoutSourceBridge ||
                 isRawDoorEntryAdvanceExtended);
            if (shouldAllowDoorEntryAdvanceSourceLocalPlanning)
            {
                if (isRawDoorEntryAdvanceWithoutSourceBridge)
                {
                    if (HasDoorRetainedExtendedNoProgressReleaseRequest(step) &&
                        TryResolveReleasedDoorFinalEntryLocalNavigationGoal(
                            currentZone,
                            step,
                            playerPosition,
                            desiredPosition,
                            out planningZone,
                            out planningGoal,
                            out planningContext))
                    {
                        return true;
                    }

                    LogNavigationTrackerDebug(
                        "Skipped door entry source-local planning because no source-zone bridge is constructible" +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                if (!TrySelectDoorFinalEntryLocalPlanningTarget(
                        step,
                        currentZone,
                        activeDoorPushThroughPosition,
                        desiredPosition,
                        isRawDoorEntryAdvance,
                        isRawDoorEntryAdvanceExtended,
                        out string doorEntryAdvancePlanningContext,
                        out Vector3 doorEntryAdvanceDesiredPosition))
                {
                    if (isFocusedClosetDeadlockStep)
                    {
                        LogNavigationTrackerDebug(
                            "Focused closet deadlock trace: source-local entry-advance branch returned false" +
                            " reason=final-entry-target-selection-false" +
                            " currentZone=" + currentZone +
                            " desiredPosition=" + FormatVector3(desiredPosition) +
                            " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                            " isPostThresholdCommitted=" + isPostThresholdCommitted +
                            " isStillInSourceZone=" + isStillInSourceZone +
                            " activeDoorPushThroughPosition=" + FormatVector3(activeDoorPushThroughPosition) +
                            " step=" + DescribeNavigationStep(step));
                    }
                    return false;
                }

                if (doorEntryAdvanceDesiredPosition == Vector3.zero ||
                    !TryGetDoorSourceLocalPlanningGoal(
                        step,
                        currentZone,
                        playerPosition,
                        doorEntryAdvanceDesiredPosition,
                        doorEntryAdvancePlanningContext,
                        out Vector3 doorEntryAdvancePlanningGoal))
                {
                    if (isFocusedClosetDeadlockStep)
                    {
                        LogNavigationTrackerDebug(
                            "Focused closet deadlock trace: source-local entry-advance branch returned false" +
                            " reason=" + (doorEntryAdvanceDesiredPosition == Vector3.zero
                                ? "entry-advance-desired-position-zero"
                                : "source-local-planning-goal-false") +
                            " currentZone=" + currentZone +
                            " desiredPosition=" + FormatVector3(desiredPosition) +
                            " doorEntryAdvanceDesiredPosition=" + FormatVector3(doorEntryAdvanceDesiredPosition) +
                            " doorEntryAdvancePlanningContext=" + (doorEntryAdvancePlanningContext ?? "<null>") +
                            " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                            " step=" + DescribeNavigationStep(step));
                    }
                    return false;
                }

                if (TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceThresholdTarget) &&
                    sourceThresholdTarget != Vector3.zero)
                {
                    float forwardProgress = GetDoorThresholdForwardProgress(
                        sourceThresholdTarget,
                        activeDoorPushThroughPosition,
                        doorEntryAdvancePlanningGoal);
                    if (forwardProgress <= 0.08f)
                    {
                        if ((isRawDoorPushThrough ||
                             isRawDoorEntryAdvance ||
                             isRawDoorEntryAdvanceExtended) &&
                            !IsDoorSourceLocalGoalCompleted(step, "door-push-through-local") &&
                            TryGetDoorSourceLocalPlanningGoal(
                                step,
                                currentZone,
                                playerPosition,
                                activeDoorPushThroughPosition,
                                "door-push-through-local",
                                out Vector3 pushThroughRecoveryGoal))
                        {
                            if (ShouldUseLocalNavigationGoal(
                                    playerPosition,
                                    pushThroughRecoveryGoal,
                                    GetLocalNavigationGoalReachedDistance("door-push-through-local")))
                            {
                                planningZone = ResolveLocalPlanningZone(
                                    currentZone,
                                    step.FromZone,
                                    playerPosition,
                                    pushThroughRecoveryGoal);
                                planningGoal = pushThroughRecoveryGoal;
                                planningContext = "door-push-through-local";
                                LogNavigationTrackerDebug(
                                    "Promoted door push-through local recovery planning goal after entry-advance discard" +
                                    " planningGoal=" + FormatVector3(pushThroughRecoveryGoal) +
                                    " step=" + DescribeNavigationStep(step));
                                return true;
                            }

                            float pushThroughRemainingDistance = GetFlatDistance(playerPosition, pushThroughRecoveryGoal);
                            MarkDoorSourceLocalGoalReached(
                                BuildNavigationStepKey(step),
                                "door-push-through-local",
                                pushThroughRecoveryGoal,
                                pushThroughRemainingDistance);
                            LogNavigationTrackerDebug(
                                "Accepted close door push-through local recovery goal after entry-advance discard" +
                                " planningGoal=" + FormatVector3(pushThroughRecoveryGoal) +
                                " remainingDistance=" + pushThroughRemainingDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                                " step=" + DescribeNavigationStep(step));
                            return false;
                        }

                        if (IsDoorSourceLocalGoalCompleted(step, "door-push-through-local"))
                        {
                            LogNavigationTrackerDebug(
                                "Skipped completed door push-through local recovery after entry-advance discard" +
                                " sourceThresholdTarget=" + FormatVector3(sourceThresholdTarget) +
                                " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                                " forwardProgress=" + forwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                                " step=" + DescribeNavigationStep(step));
                            return false;
                        }

                        LogNavigationTrackerDebug(
                            "Discarded door entry advance local planning goal due to insufficient source-side progress" +
                            " sourceThresholdTarget=" + FormatVector3(sourceThresholdTarget) +
                            " planningGoal=" + FormatVector3(doorEntryAdvancePlanningGoal) +
                            " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                            " forwardProgress=" + forwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        if (IsFocusedClosetDeadlockStep(step))
                        {
                            LogNavigationTrackerDebug(
                                "Focused closet deadlock trace: source-side progress discard" +
                                " sourceThresholdTarget=" + FormatVector3(sourceThresholdTarget) +
                                " activeDoorPushThroughPosition=" + FormatVector3(activeDoorPushThroughPosition) +
                                " planningGoal=" + FormatVector3(doorEntryAdvancePlanningGoal) +
                                " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                                " planningContext=" + (doorEntryAdvancePlanningContext ?? "<null>") +
                                " forwardProgress=" + forwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                                " step=" + DescribeNavigationStep(step));
                        }
                        return false;
                    }
                }

                if (!ShouldUseLocalNavigationGoal(
                        playerPosition,
                        doorEntryAdvancePlanningGoal,
                        GetLocalNavigationGoalReachedDistance(doorEntryAdvancePlanningContext)))
                {
                    if (isFocusedClosetDeadlockStep)
                    {
                        LogNavigationTrackerDebug(
                            "Focused closet deadlock trace: source-local entry-advance branch returned false" +
                            " reason=planning-goal-already-reached" +
                            " currentZone=" + currentZone +
                            " playerPosition=" + FormatVector3(playerPosition) +
                            " doorEntryAdvancePlanningGoal=" + FormatVector3(doorEntryAdvancePlanningGoal) +
                            " doorEntryAdvancePlanningContext=" + (doorEntryAdvancePlanningContext ?? "<null>") +
                            " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                            " step=" + DescribeNavigationStep(step));
                    }
                    MarkDoorSourceLocalGoalReached(
                        BuildNavigationStepKey(step),
                        doorEntryAdvancePlanningContext,
                        doorEntryAdvancePlanningGoal,
                        GetFlatDistance(playerPosition, doorEntryAdvancePlanningGoal));
                    return false;
                }

                planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, doorEntryAdvancePlanningGoal);
                planningGoal = doorEntryAdvancePlanningGoal;
                planningContext = doorEntryAdvancePlanningContext;
                if (isFocusedClosetDeadlockStep)
                {
                    LogNavigationTrackerDebug(
                        "Focused closet deadlock trace: source-local entry-advance branch succeeded" +
                        " planningZone=" + (planningZone ?? "<null>") +
                        " planningGoal=" + FormatVector3(planningGoal) +
                        " planningContext=" + (planningContext ?? "<null>") +
                        " rawContext=" + (_rawNavigationTargetContext ?? "<null>") +
                        " step=" + DescribeNavigationStep(step));
                }
                return true;
            }

            return false;
        }

        private bool TryResolveReleasedDoorFinalEntryLocalNavigationGoal(
            string currentZone,
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            Vector3 desiredPosition,
            out string planningZone,
            out Vector3 planningGoal,
            out string planningContext)
        {
            planningZone = null;
            planningGoal = Vector3.zero;
            planningContext = null;
            if (step == null ||
                desiredPosition == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                !HasDoorRetainedExtendedNoProgressReleaseRequest(step) ||
                !IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") ||
                !IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-local"))
            {
                return false;
            }

            planningContext = "door-entry-advance-local";
            if (!TryGetDoorSourceLocalPlanningGoal(
                    step,
                    currentZone,
                    playerPosition,
                    desiredPosition,
                    planningContext,
                    out planningGoal) ||
                planningGoal == Vector3.zero)
            {
                LogNavigationTrackerDebug(
                    "Released no-source-bridge final entry local planning unavailable after retained extended no-progress release" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " planningContext=" + planningContext +
                    " step=" + DescribeNavigationStep(step));
                planningContext = null;
                return false;
            }

            planningZone = ResolveLocalPlanningZone(
                currentZone,
                step.FromZone,
                playerPosition,
                planningGoal);
            if (string.IsNullOrWhiteSpace(planningZone))
            {
                LogNavigationTrackerDebug(
                    "Released no-source-bridge final entry local planning lacked planning zone after retained extended no-progress release" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " planningGoal=" + FormatVector3(planningGoal) +
                    " step=" + DescribeNavigationStep(step));
                planningGoal = Vector3.zero;
                planningContext = null;
                return false;
            }

            LogNavigationTrackerDebug(
                "Selected released no-source-bridge final entry local planning after retained extended no-progress release" +
                " desiredPosition=" + FormatVector3(desiredPosition) +
                " planningGoal=" + FormatVector3(planningGoal) +
                " planningZone=" + planningZone +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool TrySelectDoorFinalEntryLocalPlanningTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 activeDoorPushThroughPosition,
            Vector3 desiredPosition,
            bool isRawDoorEntryAdvance,
            bool isRawDoorEntryAdvanceExtended,
            out string planningContext,
            out Vector3 planningGoal)
        {
            planningContext = null;
            planningGoal = Vector3.zero;
            bool isFocusedClosetDeadlockStep = IsFocusedClosetDeadlockStep(step);
            if (step == null ||
                activeDoorPushThroughPosition == Vector3.zero ||
                desiredPosition == Vector3.zero)
            {
                if (isFocusedClosetDeadlockStep)
                {
                    LogNavigationTrackerDebug(
                        "Focused closet deadlock trace: final-entry target selection aborted before evaluation" +
                        " stepNull=" + (step == null) +
                        " activeDoorPushThroughPosition=" + FormatVector3(activeDoorPushThroughPosition) +
                        " desiredPosition=" + FormatVector3(desiredPosition));
                }
                return false;
            }

            if (isRawDoorEntryAdvance &&
                !IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") &&
                TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                    step,
                    currentZone,
                    activeDoorPushThroughPosition,
                    desiredPosition,
                    out Vector3 promotedExtendedEntryAdvanceTarget))
            {
                planningContext = "door-entry-advance-extended-local";
                planningGoal = promotedExtendedEntryAdvanceTarget;
                LogNavigationTrackerDebug(
                    "Selected door final-entry local state=" + DoorPostInteractionState.ExtendedBridge +
                    " planningContext=" + planningContext +
                    " proof=extended-bridge-local-not-complete" +
                    " release=extended-bridge-local-completion" +
                    " planningGoal=" + FormatVector3(planningGoal) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            bool shouldAdvanceTowardDestinationAfterExtendedBridge =
                (isRawDoorEntryAdvance || isRawDoorEntryAdvanceExtended) &&
                IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local");
            bool shouldUseFinalEntryAdvanceLocalContext =
                isRawDoorEntryAdvanceExtended &&
                shouldAdvanceTowardDestinationAfterExtendedBridge;
            bool isFinalEntryAdvanceLocalCompleted =
                IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-local");
            planningContext = shouldUseFinalEntryAdvanceLocalContext
                ? "door-entry-advance-local"
                : (isRawDoorEntryAdvanceExtended
                    ? "door-entry-advance-extended-local"
                    : "door-entry-advance-local");
            planningGoal = (isRawDoorEntryAdvanceExtended ||
                    shouldAdvanceTowardDestinationAfterExtendedBridge)
                ? desiredPosition
                : activeDoorPushThroughPosition;

            if (shouldUseFinalEntryAdvanceLocalContext)
            {
                if (isFinalEntryAdvanceLocalCompleted)
                {
                    if (isFocusedClosetDeadlockStep)
                    {
                        planningContext = "door-entry-advance-extended-local";
                        planningGoal = desiredPosition;
                        LogNavigationTrackerDebug(
                            "Focused closet deadlock trace: reusing retained extended bridge local planning after completed final-entry-local" +
                            " desiredPosition=" + FormatVector3(desiredPosition) +
                            " planningContext=" + planningContext +
                            " step=" + DescribeNavigationStep(step));
                        LogNavigationTrackerDebug(
                            "Selected door final-entry local state=" + DoorPostInteractionState.ExtendedBridge +
                            " planningContext=" + planningContext +
                            " proof=extended-bridge-local-complete-final-entry-local-already-complete" +
                            " release=raw-extended-arrival-or-source-zone-exit" +
                            " planningGoal=" + FormatVector3(planningGoal) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    if (isFocusedClosetDeadlockStep)
                    {
                        LogNavigationTrackerDebug(
                            "Focused closet deadlock trace: final-entry target selection returned false" +
                            " reason=completed-post-proof-final-entry-local" +
                            " currentZone=" + currentZone +
                            " desiredPosition=" + FormatVector3(desiredPosition) +
                            " planningContext=door-entry-advance-local" +
                            " rawDoorEntryAdvance=" + isRawDoorEntryAdvance +
                            " rawDoorEntryAdvanceExtended=" + isRawDoorEntryAdvanceExtended +
                            " extendedLocalCompleted=" + IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") +
                            " finalEntryLocalCompleted=" + isFinalEntryAdvanceLocalCompleted +
                            " step=" + DescribeNavigationStep(step));
                    }
                    LogNavigationTrackerDebug(
                        "Skipped completed post-proof final door entry local proxy; preserving raw entry advance" +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                LogNavigationTrackerDebug(
                    "Selected door final-entry local state=" + DoorPostInteractionState.FinalEntryLocal +
                    " planningContext=" + planningContext +
                    " proof=extended-bridge-local-complete" +
                    " release=final-entry-local-completion-or-source-zone-exit" +
                    " planningGoal=" + FormatVector3(planningGoal) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (shouldAdvanceTowardDestinationAfterExtendedBridge)
            {
                if (isFinalEntryAdvanceLocalCompleted)
                {
                    if (isFocusedClosetDeadlockStep)
                    {
                        LogNavigationTrackerDebug(
                            "Focused closet deadlock trace: final-entry target selection returned false" +
                            " reason=completed-post-proof-door-entry-local" +
                            " currentZone=" + currentZone +
                            " desiredPosition=" + FormatVector3(desiredPosition) +
                            " planningContext=" + (planningContext ?? "<null>") +
                            " rawDoorEntryAdvance=" + isRawDoorEntryAdvance +
                            " rawDoorEntryAdvanceExtended=" + isRawDoorEntryAdvanceExtended +
                            " extendedLocalCompleted=" + IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") +
                            " finalEntryLocalCompleted=" + isFinalEntryAdvanceLocalCompleted +
                            " step=" + DescribeNavigationStep(step));
                    }
                    LogNavigationTrackerDebug(
                        "Skipped completed post-proof door entry local proxy; preserving raw entry advance" +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                LogNavigationTrackerDebug(
                    "Selected door final-entry local state=" + DoorPostInteractionState.FinalEntryLocal +
                    " planningContext=" + planningContext +
                    " proof=extended-bridge-local-complete" +
                    " release=destination-side-progress-or-source-zone-exit" +
                    " planningGoal=" + FormatVector3(planningGoal) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (isRawDoorEntryAdvance &&
                IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-local"))
            {
                if (isFocusedClosetDeadlockStep)
                {
                    LogNavigationTrackerDebug(
                        "Focused closet deadlock trace: final-entry target selection returned false" +
                        " reason=completed-raw-door-entry-local" +
                        " currentZone=" + currentZone +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " planningContext=" + (planningContext ?? "<null>") +
                        " rawDoorEntryAdvance=" + isRawDoorEntryAdvance +
                        " rawDoorEntryAdvanceExtended=" + isRawDoorEntryAdvanceExtended +
                        " extendedLocalCompleted=" + IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") +
                        " finalEntryLocalCompleted=" + IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-local") +
                        " step=" + DescribeNavigationStep(step));
                }
                LogNavigationTrackerDebug(
                    "Skipped completed door entry advance local proxy; preserving raw entry advance" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            LogNavigationTrackerDebug(
                "Selected door final-entry local state=" + DoorPostInteractionState.FinalEntryLocal +
                " planningContext=" + planningContext +
                " proof=post-threshold-commit" +
                " release=final-entry-local-completion-or-source-zone-exit" +
                " planningGoal=" + FormatVector3(planningGoal) +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool IsDoorTraversalPostThresholdCommitted(NavigationGraph.PathStep step)
        {
            if (step == null)
                return false;

            string stepKey = BuildNavigationStepKey(step);
            if (string.IsNullOrEmpty(stepKey))
                return false;

            if (_transitionSweepSession != null &&
                _transitionSweepSession.Kind == TransitionSweepKind.Door &&
                _transitionSweepSession.Phase == TransitionSweepPhase.Running &&
                _transitionSweepSession.DoorPostThresholdCommitted &&
                string.Equals(stepKey, BuildNavigationStepKey(_transitionSweepSession.CurrentStep), StringComparison.Ordinal))
            {
                return true;
            }

            return _doorTraversalInteractionTriggered &&
                _doorTraversalPostThresholdCommitted &&
                string.Equals(stepKey, _doorTraversalStepKey, StringComparison.Ordinal);
        }

        private bool TryGetDoorSourceLocalPlanningGoal(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            Vector3 desiredPosition,
            string planningContext,
            out Vector3 planningGoal)
        {
            planningGoal = desiredPosition;
            Vector3 unsnappedPlanningGoal = desiredPosition;
            bool snappedPlanningGoalApplied = false;
            if (step == null ||
                desiredPosition == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
            {
                return planningGoal != Vector3.zero;
            }

            float maxSnapDistance = DoorTraversalClearanceDistance + DoorPushThroughArrivalDistance;
            bool shouldSkipSnapForPostProofFinalDoorEntry =
                string.Equals(planningContext, "door-entry-advance-local", StringComparison.Ordinal) &&
                IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local");
            bool isFocusedClosetDeadlockStep = IsFocusedClosetDeadlockStep(step);
            if (isFocusedClosetDeadlockStep)
            {
                LogNavigationTrackerDebug(
                    "Focused closet deadlock trace: entering door source local planning goal" +
                    " currentZone=" + currentZone +
                    " planningContext=" + (planningContext ?? "<null>") +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " shouldSkipSnapForPostProofFinalDoorEntry=" + shouldSkipSnapForPostProofFinalDoorEntry +
                    " step=" + DescribeNavigationStep(step));
            }
            if (!shouldSkipSnapForPostProofFinalDoorEntry &&
                TrySnapDoorSourceNavigationTarget(
                    step,
                    currentZone,
                    desiredPosition,
                    maxSnapDistance,
                    planningContext,
                    out Vector3 snappedPlanningGoal) &&
                snappedPlanningGoal != Vector3.zero)
            {
                snappedPlanningGoalApplied = GetFlatDistance(unsnappedPlanningGoal, snappedPlanningGoal) > 0.05f;
                planningGoal = snappedPlanningGoal;
            }
            else if (shouldSkipSnapForPostProofFinalDoorEntry)
            {
                LogNavigationTrackerDebug(
                    "Skipped snapping final door entry local planning goal after extended bridge completion" +
                    " position=" + FormatVector3(planningGoal) +
                    " step=" + DescribeNavigationStep(step));
            }

            if (string.Equals(planningContext, "door-entry-advance-extended-local", StringComparison.Ordinal) &&
                snappedPlanningGoalApplied &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 extendedSourceTarget) &&
                TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 extendedPushThroughPosition))
            {
                float snappedForwardProgress = GetDoorThresholdForwardProgress(
                    extendedSourceTarget,
                    extendedPushThroughPosition,
                    planningGoal);
                float unsnappedForwardProgress = GetDoorThresholdForwardProgress(
                    extendedSourceTarget,
                    extendedPushThroughPosition,
                    unsnappedPlanningGoal);
                if (snappedForwardProgress <= 0.08f &&
                    unsnappedForwardProgress > snappedForwardProgress + 0.25f)
                {
                    planningGoal = unsnappedPlanningGoal;
                    LogNavigationTrackerDebug(
                        "Restored unsnapped door entry advance extended local planning goal" +
                        " snappedForwardProgress=" + snappedForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                        " unsnappedForwardProgress=" + unsnappedForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                        " position=" + FormatVector3(planningGoal) +
                        " step=" + DescribeNavigationStep(step));
                }
            }

            if (string.Equals(planningContext, "door-entry-advance-local", StringComparison.Ordinal) &&
                IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") &&
                snappedPlanningGoalApplied &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 finalEntrySourceTarget) &&
                TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 finalEntryPushThroughPosition))
            {
                float snappedForwardProgress = GetDoorThresholdForwardProgress(
                    finalEntrySourceTarget,
                    finalEntryPushThroughPosition,
                    planningGoal);
                float unsnappedForwardProgress = GetDoorThresholdForwardProgress(
                    finalEntrySourceTarget,
                    finalEntryPushThroughPosition,
                    unsnappedPlanningGoal);
                if (snappedForwardProgress <= 0.08f &&
                    unsnappedForwardProgress > snappedForwardProgress + 0.25f)
                {
                    planningGoal = unsnappedPlanningGoal;
                    LogNavigationTrackerDebug(
                        "Restored unsnapped final door entry local planning goal after extended bridge completion" +
                        " snappedForwardProgress=" + snappedForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                        " unsnappedForwardProgress=" + unsnappedForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                        " position=" + FormatVector3(planningGoal) +
                        " step=" + DescribeNavigationStep(step));
                }
            }

            if ((string.Equals(planningContext, "door-threshold-handoff-local", StringComparison.Ordinal) ||
                 string.Equals(planningContext, "door-push-through-local", StringComparison.Ordinal)) &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceTarget) &&
                TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 pushThroughPosition) &&
                !HasMeaningfulDoorThresholdClearance(sourceTarget, pushThroughPosition, planningGoal))
            {
                bool canRestoreUnsnappedPlanningGoal =
                    snappedPlanningGoalApplied &&
                    unsnappedPlanningGoal != Vector3.zero &&
                    HasMeaningfulDoorThresholdClearance(
                        sourceTarget,
                        pushThroughPosition,
                        unsnappedPlanningGoal);
                if (canRestoreUnsnappedPlanningGoal)
                {
                    planningGoal = unsnappedPlanningGoal;
                    LogNavigationTrackerDebug(
                        "Restored unsnapped door source local planning goal position=" + FormatVector3(planningGoal) +
                        " context=" + planningContext +
                        " step=" + DescribeNavigationStep(step));
                }
                else if (string.Equals(planningContext, "door-push-through-local", StringComparison.Ordinal) &&
                    TryResolveDoorPushThroughFallbackLocalGoal(
                        step,
                        currentZone,
                        sourceTarget,
                        pushThroughPosition,
                        out Vector3 fallbackPlanningGoal))
                {
                    planningGoal = fallbackPlanningGoal;
                    LogNavigationTrackerDebug(
                        "Fallback door source local planning goal position=" + FormatVector3(planningGoal) +
                        " context=" + planningContext +
                        " step=" + DescribeNavigationStep(step));
                }
                else
                {
                    if (isFocusedClosetDeadlockStep)
                    {
                        LogNavigationTrackerDebug(
                            "Focused closet deadlock trace: discarded source local planning goal before reachable-proxy resolution" +
                            " position=" + FormatVector3(planningGoal) +
                            " unsnappedPlanningGoal=" + FormatVector3(unsnappedPlanningGoal) +
                            " context=" + (planningContext ?? "<null>") +
                            " step=" + DescribeNavigationStep(step));
                    }
                    LogNavigationTrackerDebug(
                        "Discarded door source local planning goal position=" + FormatVector3(planningGoal) +
                        " context=" + planningContext +
                        " step=" + DescribeNavigationStep(step));
                    planningGoal = Vector3.zero;
                }
            }

            planningGoal = ResolveDoorReachableLocalPlanningGoal(
                step,
                currentZone,
                playerPosition,
                planningGoal,
                planningContext);
            if (isFocusedClosetDeadlockStep)
            {
                LogNavigationTrackerDebug(
                    "Focused closet deadlock trace: exiting door source local planning goal" +
                    " planningContext=" + (planningContext ?? "<null>") +
                    " resolvedPlanningGoal=" + FormatVector3(planningGoal) +
                    " step=" + DescribeNavigationStep(step));
            }
            return planningGoal != Vector3.zero;
        }

        private Vector3 ResolveDoorReachableLocalPlanningGoal(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            Vector3 planningGoal,
            string planningContext)
        {
            if (step == null ||
                planningGoal == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                (!string.Equals(planningContext, "door-threshold-advance-local", StringComparison.Ordinal) &&
                 !string.Equals(planningContext, "door-threshold-handoff-local", StringComparison.Ordinal) &&
                 !string.Equals(planningContext, "door-push-through-local", StringComparison.Ordinal) &&
                 !string.Equals(planningContext, "door-entry-advance-local", StringComparison.Ordinal) &&
                 !string.Equals(planningContext, "door-entry-advance-extended-local", StringComparison.Ordinal)))
            {
                return planningGoal;
            }

            string planningZone = ResolveLocalPlanningZone(
                currentZone,
                step.FromZone,
                playerPosition,
                planningGoal);
            if (string.IsNullOrWhiteSpace(planningZone) ||
                !LocalNavigationMaps.TryResolveReachableProxyInStartComponent(
                    planningZone,
                    playerPosition,
                    planningGoal,
                    out Vector3 proxyGoal,
                    out string proxyDetail) ||
                proxyGoal == Vector3.zero)
            {
                return planningGoal;
            }

            Vector3 originalPlanningGoal = planningGoal;

            if (string.Equals(planningContext, "door-entry-advance-local", StringComparison.Ordinal) &&
                IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local"))
            {
                LogNavigationTrackerDebug(
                    "Preserved original final door entry planning goal after extended bridge completion" +
                    " originalGoal=" + FormatVector3(originalPlanningGoal) +
                    " step=" + DescribeNavigationStep(step));
                return originalPlanningGoal;
            }

            if ((string.Equals(planningContext, "door-entry-advance-extended-local", StringComparison.Ordinal) ||
                 string.Equals(planningContext, "door-entry-advance-local", StringComparison.Ordinal)) &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceTarget) &&
                TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 pushThroughPosition))
            {
                float proxyForwardProgress = GetDoorThresholdForwardProgress(
                    sourceTarget,
                    pushThroughPosition,
                    proxyGoal);
                float originalForwardProgress = GetDoorThresholdForwardProgress(
                    sourceTarget,
                    pushThroughPosition,
                    originalPlanningGoal);
                if (proxyForwardProgress <= 0.08f &&
                    originalForwardProgress > proxyForwardProgress + 0.25f)
                {
                    LogNavigationTrackerDebug(
                        "Restored original door entry planning goal after reachable-proxy progress collapse" +
                        " context=" + (planningContext ?? "<null>") +
                        " originalGoal=" + FormatVector3(originalPlanningGoal) +
                        " proxyGoal=" + FormatVector3(proxyGoal) +
                        " originalForwardProgress=" + originalForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                        " proxyForwardProgress=" + proxyForwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                        " step=" + DescribeNavigationStep(step));
                    return originalPlanningGoal;
                }
            }

            LogNavigationTrackerDebug(
                "Using reachable door local planning proxy" +
                " planningZone=" + planningZone +
                " context=" + (planningContext ?? "<null>") +
                " originalGoal=" + FormatVector3(planningGoal) +
                " proxyGoal=" + FormatVector3(proxyGoal) +
                " detail=" + (proxyDetail ?? "<null>") +
                " step=" + DescribeNavigationStep(step));
            return proxyGoal;
        }

        private bool TryResolveDoorPushThroughBridgeLocalNavigationGoal(
            string currentZone,
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            Vector3 desiredPosition,
            Vector3 activeDoorPushThroughPosition,
            out string planningZone,
            out Vector3 planningGoal,
            out string planningContext)
        {
            planningZone = null;
            planningGoal = Vector3.zero;
            planningContext = null;
            if (step == null ||
                activeDoorPushThroughPosition == Vector3.zero ||
                desiredPosition == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                GetFlatDistance(activeDoorPushThroughPosition, desiredPosition) >
                GetRawNavigationGoalReachedDistance("door-push-through") ||
                !TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceTarget) ||
                sourceTarget == Vector3.zero)
            {
                return false;
            }

            Vector3 bridgeGoal = Vector3.zero;
            if (!TryGetDoorThresholdHandoffTarget(
                    step,
                    currentZone,
                    sourceTarget,
                    activeDoorPushThroughPosition,
                    out bridgeGoal) ||
                bridgeGoal == Vector3.zero)
            {
                if (!TryResolveDoorPushThroughFallbackLocalGoal(
                        step,
                        currentZone,
                        sourceTarget,
                        activeDoorPushThroughPosition,
                        out bridgeGoal) ||
                    bridgeGoal == Vector3.zero)
                {
                    return false;
                }
            }

            const string bridgeContext = "door-push-through-bridge-local";
            if (!ShouldUseLocalNavigationGoal(
                    playerPosition,
                    bridgeGoal,
                    GetLocalNavigationGoalReachedDistance(bridgeContext)))
            {
                return false;
            }

            string candidatePlanningZone = ResolveLocalPlanningZone(
                currentZone,
                step.FromZone,
                playerPosition,
                bridgeGoal);
            if (!HasUsableLocalPlanningResult(candidatePlanningZone, bridgeGoal))
                return false;

            planningZone = candidatePlanningZone;
            planningGoal = bridgeGoal;
            planningContext = bridgeContext;
            LogNavigationTrackerDebug(
                "Resolved door push-through bridge local planning goal" +
                " planningGoal=" + FormatVector3(planningGoal) +
                " rawTargetPosition=" + FormatVector3(desiredPosition) +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool TryResolveDoorPushThroughFallbackLocalGoal(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 sourceTarget,
            Vector3 pushThroughPosition,
            out Vector3 planningGoal)
        {
            planningGoal = Vector3.zero;
            if (step == null ||
                sourceTarget == Vector3.zero ||
                pushThroughPosition == Vector3.zero ||
                string.IsNullOrWhiteSpace(currentZone))
            {
                return false;
            }

            Vector3 fallbackGoal = BuildDoorThresholdHandoffPosition(sourceTarget, pushThroughPosition);
            if (fallbackGoal == Vector3.zero)
                return false;

            Vector3 unsnappedFallbackGoal = fallbackGoal;
            bool snappedFallbackGoalApplied = false;
            if (TrySnapDoorSourceNavigationTarget(
                    step,
                    currentZone,
                    fallbackGoal,
                    DoorTraversalClearanceDistance + DoorPushThroughArrivalDistance,
                    "door-push-through-local-fallback",
                    out Vector3 snappedFallbackGoal) &&
                snappedFallbackGoal != Vector3.zero)
            {
                snappedFallbackGoalApplied = GetFlatDistance(unsnappedFallbackGoal, snappedFallbackGoal) > 0.05f;
                fallbackGoal = snappedFallbackGoal;
            }

            if (!HasMeaningfulDoorThresholdClearance(
                    sourceTarget,
                    pushThroughPosition,
                    fallbackGoal))
            {
                if (!snappedFallbackGoalApplied ||
                    !HasMeaningfulDoorThresholdClearance(
                        sourceTarget,
                        pushThroughPosition,
                        unsnappedFallbackGoal))
                {
                    return false;
                }

                fallbackGoal = unsnappedFallbackGoal;
                LogNavigationTrackerDebug(
                    "Restored unsnapped door push-through fallback local planning goal position=" + FormatVector3(fallbackGoal) +
                    " step=" + DescribeNavigationStep(step));
            }

            planningGoal = fallbackGoal;
            return true;
        }
    }
}
