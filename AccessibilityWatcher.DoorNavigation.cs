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
                    handoffDistance = GetPlanarDistanceToTarget(playerPosition, handoffTarget);
                }
            }

            float pushThroughDistance = GetPlanarDistanceToTarget(playerPosition, pushThroughPosition);
            bool shouldKeepDoorThresholdAdvance = sourceTarget != Vector3.zero &&
                ShouldKeepDoorThresholdAdvance(playerPosition, sourceTarget, pushThroughPosition);
            bool shouldCommitPostThreshold = postThresholdCommitted ||
                pushThroughDistance <= DoorPushThroughArrivalDistance ||
                (sourceTarget != Vector3.zero &&
                 sourceThresholdDistance <= DoorPushThroughArrivalDistance &&
                 !shouldKeepDoorThresholdAdvance);
            if (shouldCommitPostThreshold)
                postThresholdCommitted = true;

            bool shouldContinueDoorThresholdAdvance =
                !shouldCommitPostThreshold &&
                sourceTarget != Vector3.zero &&
                pushThroughDistance > DoorPushThroughArrivalDistance &&
                sourceThresholdDistance > DoorPushThroughSourceAdvanceDistance &&
                sourceThresholdDistance > DoorPushThroughArrivalDistance;
            if (shouldContinueDoorThresholdAdvance)
            {
                position = sourceTarget;
                targetKind = NavigationTargetKind.ZoneFallback;
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
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " handoffDistance=" + handoffDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stage=DoorThresholdHandoff" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (pushThroughDistance > DoorPushThroughArrivalDistance)
            {
                position = pushThroughPosition;
                targetKind = NavigationTargetKind.ZoneFallback;
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
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " stage=DoorEntryAdvance" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            return false;
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
                        "door-threshold-handoff-local",
                        out Vector3 doorThresholdPlanningGoal) &&
                    ShouldUseLocalNavigationGoal(
                        playerPosition,
                        doorThresholdPlanningGoal,
                        GetLocalNavigationGoalReachedDistance("door-threshold-handoff-local")))
                {
                    planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, doorThresholdPlanningGoal);
                    planningGoal = doorThresholdPlanningGoal;
                    planningContext = "door-threshold-handoff-local";
                    return true;
                }
            }

            if (hasActiveDoorPushThroughPosition &&
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

            return false;
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
                TryGetDoorPushThroughSourceTarget(step, out Vector3 sourceTarget) &&
                TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 pushThroughPosition) &&
                !HasMeaningfulDoorThresholdClearance(sourceTarget, pushThroughPosition, planningGoal))
            {
                LogNavigationTrackerDebug(
                    "Discarded door source local planning goal position=" + FormatVector3(planningGoal) +
                    " context=" + planningContext +
                    " step=" + DescribeNavigationStep(step));
                planningGoal = Vector3.zero;
            }

            return planningGoal != Vector3.zero;
        }
    }
}
