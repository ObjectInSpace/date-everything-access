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
            bool shouldKeepDoorThresholdAdvance = sourceTarget != Vector3.zero &&
                ShouldKeepDoorThresholdAdvance(playerPosition, sourceTarget, pushThroughPosition);

            bool shouldCommitPostThreshold = postThresholdCommitted ||
                (sourceTarget != Vector3.zero &&
                 sourceThresholdDistance <= thresholdAdvanceArrivalDistance &&
                 !shouldKeepDoorThresholdAdvance);
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
                    out position,
                    out targetKind))
            {
                return true;
            }

            bool shouldHoldPushThroughInSourceZone =
                shouldCommitPostThreshold &&
                isStillInSourceZone &&
                pushThroughPosition != Vector3.zero &&
                pushThroughDistance > DoorPushThroughLocalNavigationGoalReachedDistance &&
                !isNoHandoffPushThroughCommitState;
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
                isNoHandoffPushThroughCommitState)
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
                 sourceThresholdDistance > thresholdAdvanceArrivalDistance);
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
                (sourceThresholdDistance <= DoorPushThroughArrivalDistance ||
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

            if (TryGetDoorTraversalDestinationTarget(step, out Vector3 destinationTarget, out NavigationTargetKind destinationTargetKind))
            {
                position = destinationTarget;
                targetKind = destinationTargetKind;
                _rawNavigationTargetContext = "door-entry-advance";
                LogNavigationTrackerDebug(
                    "Next navigation target kind=" + targetKind +
                    " position=" + FormatVector3(position) +
                    " stage=DoorEntryAdvance" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (TryGetZonePosition(step.ToZone, out position))
            {
                targetKind = NavigationTargetKind.ZoneFallback;
                _rawNavigationTargetContext = "door-entry-advance";
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " stage=DoorEntryAdvance" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            return false;
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
                if (sourceTarget != Vector3.zero &&
                    sourceThresholdDistance > sourceThresholdArrivalDistance)
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
                    " step=" + DescribeNavigationStep(step));
            }

            if (GetDoorCommittedSourceRecoveryStage() == DoorCommittedSourceRecoveryStage.PushThrough)
            {
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

            if (IsDoorTraversalPostThresholdCommitted(step) &&
                TryGetDoorTraversalDestinationTarget(step, out Vector3 destinationTarget, out NavigationTargetKind destinationTargetKind) &&
                destinationTarget != Vector3.zero &&
                destinationTargetKind == targetKind &&
                GetFlatDistance(destinationTarget, desiredPosition) <=
                GetRawNavigationGoalReachedDistance("door-entry-advance"))
            {
                LogNavigationTrackerDebug(
                    "Suppressed generic door local fallback stage=DoorEntryAdvance" +
                    " desiredPosition=" + FormatVector3(desiredPosition) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

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
                    TryGetDoorSourceLocalPlanningGoal(
                        step,
                        currentZone,
                        desiredPosition,
                        isThresholdAdvanceGoal && !isThresholdHandoffGoal
                            ? "door-threshold-advance-local"
                            : "door-threshold-handoff-local",
                        out Vector3 doorThresholdPlanningGoal) &&
                    ShouldUseLocalNavigationGoal(
                        playerPosition,
                        doorThresholdPlanningGoal,
                        GetLocalNavigationGoalReachedDistance(
                            isThresholdAdvanceGoal && !isThresholdHandoffGoal
                                ? "door-threshold-advance-local"
                                : "door-threshold-handoff-local")))
                {
                    planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, doorThresholdPlanningGoal);
                    planningGoal = doorThresholdPlanningGoal;
                    planningContext = isThresholdAdvanceGoal && !isThresholdHandoffGoal
                        ? "door-threshold-advance-local"
                        : "door-threshold-handoff-local";
                    return true;
                }
            }

            if ((!isPostThresholdCommitted ||
                 IsDoorCommittedSourceRecoveryPushThroughStage(step, currentZone)) &&
                hasActiveDoorPushThroughPosition &&
                GetFlatDistance(activeDoorPushThroughPosition, desiredPosition) <= 0.35f)
            {
                if (!TryGetDoorSourceLocalPlanningGoal(
                        step,
                        currentZone,
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

                return false;
            }

            if (isPostThresholdCommitted &&
                hasActiveDoorPushThroughPosition &&
                activeDoorPushThroughPosition != Vector3.zero &&
                !string.IsNullOrEmpty(step.FromZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) &&
                TryGetDoorSourceLocalPlanningGoal(
                    step,
                    currentZone,
                    activeDoorPushThroughPosition,
                    "door-entry-advance-local",
                    out Vector3 doorEntryAdvancePlanningGoal))
            {
                if (TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceThresholdTarget) &&
                    sourceThresholdTarget != Vector3.zero)
                {
                    float forwardProgress = GetDoorThresholdForwardProgress(
                        sourceThresholdTarget,
                        activeDoorPushThroughPosition,
                        doorEntryAdvancePlanningGoal);
                    if (forwardProgress <= 0.08f)
                    {
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
                        GetLocalNavigationGoalReachedDistance("door-entry-advance-local")))
                {
                    return false;
                }

                planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, doorEntryAdvancePlanningGoal);
                planningGoal = doorEntryAdvancePlanningGoal;
                planningContext = "door-entry-advance-local";
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
            Vector3 desiredPosition,
            string planningContext,
            out Vector3 planningGoal)
        {
            planningGoal = desiredPosition;
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
                planningGoal = snappedPlanningGoal;
            }

            if ((string.Equals(planningContext, "door-threshold-handoff-local", StringComparison.Ordinal) ||
                 string.Equals(planningContext, "door-push-through-local", StringComparison.Ordinal)) &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 sourceTarget) &&
                TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 pushThroughPosition) &&
                !HasMeaningfulDoorThresholdClearance(sourceTarget, pushThroughPosition, planningGoal))
            {
                if (string.Equals(planningContext, "door-push-through-local", StringComparison.Ordinal) &&
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

            return planningGoal != Vector3.zero;
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

            if (TrySnapDoorSourceNavigationTarget(
                    step,
                    currentZone,
                    fallbackGoal,
                    DoorTraversalClearanceDistance + DoorPushThroughArrivalDistance,
                    "door-push-through-local-fallback",
                    out Vector3 snappedFallbackGoal) &&
                snappedFallbackGoal != Vector3.zero)
            {
                fallbackGoal = snappedFallbackGoal;
            }

            if (!HasMeaningfulDoorThresholdClearance(
                    sourceTarget,
                    pushThroughPosition,
                    fallbackGoal))
            {
                return false;
            }

            planningGoal = fallbackGoal;
            return true;
        }
    }
}
