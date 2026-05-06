using System;
using System.Globalization;
using UnityEngine;

namespace DateEverythingAccess
{
    internal sealed partial class AccessibilityWatcher
    {
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
                position = pushThroughPosition;
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-push-through";
                LogNavigationTrackerDebug(
                    "Holding door push-through target after no-handoff commit until local arrival" +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " arrivalDistance=" + pushThroughLocalArrivalDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorPushThrough" +
                    " step=" + DescribeNavigationStep(step));
                return true;
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
                position = pushThroughPosition;
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-push-through";
                LogNavigationTrackerDebug(
                    "Holding door push-through target while source zone is unchanged" +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorPushThrough" +
                    " step=" + DescribeNavigationStep(step));
                return true;
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
                position = sourceTarget;
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-threshold-advance";
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorThresholdAdvance" +
                    " step=" + DescribeNavigationStep(step));
                return true;
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
                position = handoffTarget;
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-threshold-handoff";
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " handoffDistance=" + handoffDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorThresholdHandoff" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (!shouldCommitPostThreshold && pushThroughPosition != Vector3.zero)
            {
                position = pushThroughPosition;
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-push-through";
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorPushThrough" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            Vector3 sourceZoneEntryTarget = Vector3.zero;
            Vector3 extendedEntryAdvanceTarget = Vector3.zero;
            bool shouldHoldSourceZoneExtendedEntryAdvance =
                shouldCommitPostThreshold &&
                isStillInSourceZone &&
                pushThroughLocalReached &&
                TryGetDoorTraversalDestinationTarget(step, out sourceZoneEntryTarget, out _) &&
                TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                    step,
                    currentZone,
                    pushThroughPosition,
                    sourceZoneEntryTarget,
                    out extendedEntryAdvanceTarget);
            float extendedEntryAdvanceDistance = shouldHoldSourceZoneExtendedEntryAdvance
                ? GetPlanarDistanceToTarget(playerPosition, extendedEntryAdvanceTarget)
                : float.PositiveInfinity;

            if (shouldHoldSourceZoneExtendedEntryAdvance &&
                extendedEntryAdvanceDistance >
                GetRawNavigationGoalReachedDistance("door-entry-advance-extended"))
            {
                position = extendedEntryAdvanceTarget;
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-entry-advance-extended";
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " extendedEntryAdvanceDistance=" + extendedEntryAdvanceDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " entryAdvanceExtendedProofSatisfied=" + entryAdvanceExtendedProofSatisfied +
                    " stage=DoorEntryAdvanceExtended" +
                    " sourceZoneHold=True" +
                    " destinationTarget=" + FormatVector3(sourceZoneEntryTarget) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            Vector3 blockedSourceZoneEntryTarget = Vector3.zero;
            Vector3 blockedExtendedEntryAdvanceTarget = Vector3.zero;
            bool shouldBlockRawFinalDoorEntryAdvanceInSourceZone =
                shouldCommitPostThreshold &&
                isStillInSourceZone &&
                !entryAdvanceExtendedProofSatisfied &&
                TryGetDoorTraversalDestinationTarget(step, out blockedSourceZoneEntryTarget, out _) &&
                TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                    step,
                    currentZone,
                    pushThroughPosition,
                    blockedSourceZoneEntryTarget,
                    out blockedExtendedEntryAdvanceTarget);
            if (shouldBlockRawFinalDoorEntryAdvanceInSourceZone)
            {
                position = blockedExtendedEntryAdvanceTarget;
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-entry-advance-extended";
                LogNavigationTrackerDebug(
                    "Blocked raw final door entry advance until extended local bridge completes" +
                    " position=" + FormatVector3(position) +
                    " destinationTarget=" + FormatVector3(blockedSourceZoneEntryTarget) +
                    " entryAdvanceExtendedProofSatisfied=" + entryAdvanceExtendedProofSatisfied +
                    " stage=DoorEntryAdvanceExtended" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (TryGetDoorTraversalDestinationTarget(step, out Vector3 destinationTarget, out NavigationTargetKind destinationTargetKind))
            {
                position = destinationTarget;
                targetKind = destinationTargetKind;
                _rawNavigationTargetContext = GetDoorEntryAdvanceRawContextForFinalTarget(
                    step,
                    currentZone,
                    shouldCommitPostThreshold,
                    isStillInSourceZone,
                    entryAdvanceExtendedProofSatisfied,
                    pushThroughPosition,
                    destinationTarget);
                LogNavigationTrackerDebug(
                    "Next navigation target kind=" + targetKind +
                    " position=" + FormatVector3(position) +
                    " stage=DoorEntryAdvance" +
                    " rawContext=" + _rawNavigationTargetContext +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (TryGetZonePosition(step.ToZone, out position))
            {
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = GetDoorEntryAdvanceRawContextForFinalTarget(
                    step,
                    currentZone,
                    shouldCommitPostThreshold,
                    isStillInSourceZone,
                    entryAdvanceExtendedProofSatisfied,
                    pushThroughPosition,
                    position);
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " stage=DoorEntryAdvance" +
                    " rawContext=" + _rawNavigationTargetContext +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            return false;
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
                if (entryAdvanceExtendedProofSatisfied)
                {
                    LogNavigationTrackerDebug(
                        "Reclassified final door entry advance to extended context after bridge proof success" +
                        " destinationTarget=" + FormatVector3(destinationTarget) +
                        " step=" + DescribeNavigationStep(step));
                }

                return "door-entry-advance-extended";
            }

            LogNavigationTrackerDebug(
                "No source-zone door entry bridge constructible; allowing final entry advance" +
                " destinationTarget=" + FormatVector3(destinationTarget) +
                " step=" + DescribeNavigationStep(step));
            return "door-entry-advance-no-source-bridge";
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
                    LogNavigationTrackerDebug(
                        "Skipped door entry source-local planning because no source-zone bridge is constructible" +
                        " desiredPosition=" + FormatVector3(desiredPosition) +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                string doorEntryAdvancePlanningContext;
                Vector3 doorEntryAdvanceDesiredPosition;
                Vector3 promotedExtendedEntryAdvanceTarget = Vector3.zero;

                bool shouldPreferExtendedEntryAdvanceBridge =
                    isRawDoorEntryAdvance &&
                    !IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local") &&
                    TryBuildDoorSourceZoneExtendedEntryAdvanceTarget(
                        step,
                        currentZone,
                        activeDoorPushThroughPosition,
                        desiredPosition,
                        out promotedExtendedEntryAdvanceTarget);
                if (shouldPreferExtendedEntryAdvanceBridge)
                {
                    doorEntryAdvancePlanningContext = "door-entry-advance-extended-local";
                    doorEntryAdvanceDesiredPosition = promotedExtendedEntryAdvanceTarget;
                    LogNavigationTrackerDebug(
                        "Promoted raw door entry advance to extended local bridge planning" +
                        " planningGoal=" + FormatVector3(doorEntryAdvanceDesiredPosition) +
                        " step=" + DescribeNavigationStep(step));
                }
                else
                {
                    bool shouldAdvanceTowardDestinationAfterExtendedBridge =
                        (isRawDoorEntryAdvance || isRawDoorEntryAdvanceExtended) &&
                        IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-extended-local");
                    bool shouldUseFinalEntryAdvanceLocalContext =
                        isRawDoorEntryAdvanceExtended &&
                        shouldAdvanceTowardDestinationAfterExtendedBridge;
                    doorEntryAdvancePlanningContext = shouldUseFinalEntryAdvanceLocalContext
                        ? "door-entry-advance-local"
                        : (isRawDoorEntryAdvanceExtended
                            ? "door-entry-advance-extended-local"
                            : "door-entry-advance-local");
                    doorEntryAdvanceDesiredPosition = (isRawDoorEntryAdvanceExtended ||
                            shouldAdvanceTowardDestinationAfterExtendedBridge)
                        ? desiredPosition
                        : activeDoorPushThroughPosition;
                    if (shouldUseFinalEntryAdvanceLocalContext)
                    {
                        LogNavigationTrackerDebug(
                            "Preserved completed extended bridge proof while advancing final door entry locally" +
                            " planningGoal=" + FormatVector3(doorEntryAdvanceDesiredPosition) +
                            " planningContext=" + doorEntryAdvancePlanningContext +
                            " step=" + DescribeNavigationStep(step));
                    }
                    else if (shouldAdvanceTowardDestinationAfterExtendedBridge)
                    {
                        LogNavigationTrackerDebug(
                            "Advanced raw door entry planning toward destination after extended bridge completion" +
                            " planningGoal=" + FormatVector3(doorEntryAdvanceDesiredPosition) +
                            " step=" + DescribeNavigationStep(step));
                    }
                    else if (isRawDoorEntryAdvance &&
                        IsDoorSourceLocalGoalCompleted(step, "door-entry-advance-local"))
                    {
                        LogNavigationTrackerDebug(
                            "Skipped completed door entry advance local proxy; preserving raw entry advance" +
                            " desiredPosition=" + FormatVector3(desiredPosition) +
                            " step=" + DescribeNavigationStep(step));
                        return false;
                    }
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
                        return false;
                    }
                }

                if (!ShouldUseLocalNavigationGoal(
                        playerPosition,
                        doorEntryAdvancePlanningGoal,
                        GetLocalNavigationGoalReachedDistance(doorEntryAdvancePlanningContext)))
                {
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
                return true;
            }

            return false;
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
            if (TrySnapDoorSourceNavigationTarget(
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
