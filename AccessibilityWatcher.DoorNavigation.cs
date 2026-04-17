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

            bool isInSourceZone = !string.IsNullOrEmpty(step.FromZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.FromZone);
            if (!isInSourceZone)
                return false;

            if (_transitionSweepSession.DoorInteractionTriggered &&
                _transitionSweepSession.DoorPushThroughPosition != Vector3.zero)
            {
                float sourceThresholdDistance = float.PositiveInfinity;
                float pushThroughDistance = float.PositiveInfinity;
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
                            _transitionSweepSession.DoorPushThroughPosition,
                            out handoffTarget))
                    {
                        handoffDistance = GetPlanarDistanceToTarget(playerPosition, handoffTarget);
                    }
                }

                pushThroughDistance = GetPlanarDistanceToTarget(playerPosition, _transitionSweepSession.DoorPushThroughPosition);
                bool shouldKeepDoorThresholdAdvance = sourceTarget != Vector3.zero &&
                    ShouldKeepDoorThresholdAdvance(playerPosition, sourceTarget, _transitionSweepSession.DoorPushThroughPosition);
                bool shouldCommitPostThreshold = _transitionSweepSession.DoorPostThresholdCommitted ||
                    pushThroughDistance <= DoorPushThroughArrivalDistance ||
                    (sourceTarget != Vector3.zero &&
                     sourceThresholdDistance <= DoorPushThroughArrivalDistance &&
                     !shouldKeepDoorThresholdAdvance);
                if (shouldCommitPostThreshold)
                    _transitionSweepSession.DoorPostThresholdCommitted = true;
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
                        _transitionSweepSession.DoorPushThroughPosition,
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
                    position = _transitionSweepSession.DoorPushThroughPosition;
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " stage=DoorPushThrough" +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                Vector3 destinationTarget = GetDoorTransitionSweepDestinationTarget(step);
                if (destinationTarget != Vector3.zero)
                {
                    position = destinationTarget;
                    targetKind = step.ToWaypoint != Vector3.zero && destinationTarget == step.ToWaypoint
                        ? NavigationTargetKind.EntryWaypoint
                        : NavigationTargetKind.ZoneFallback;
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
            }

            if (!_transitionSweepSession.DoorInteractionTriggered)
            {
                position = step.FromWaypoint != Vector3.zero ? step.FromWaypoint : playerPosition;
                targetKind = NavigationTargetKind.TransitionInteractable;
                LogNavigationTrackerDebug(
                    "Next navigation target kind=TransitionInteractable position=" + FormatVector3(position) +
                    " stage=DoorInteractionRetry" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            return false;
        }

        private bool TryGetDoorTraversalNavigationTargetCore(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (string.IsNullOrEmpty(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
            {
                return false;
            }

            if (_doorTraversalInteractionTriggered && _doorTraversalPushThroughPosition != Vector3.zero)
            {
                float sourceThresholdDistance = float.PositiveInfinity;
                float pushThroughDistance = float.PositiveInfinity;
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
                            _doorTraversalPushThroughPosition,
                            out handoffTarget))
                    {
                        handoffDistance = GetPlanarDistanceToTarget(playerPosition, handoffTarget);
                    }
                }

                pushThroughDistance = GetPlanarDistanceToTarget(playerPosition, _doorTraversalPushThroughPosition);
                bool shouldKeepDoorThresholdAdvance = sourceTarget != Vector3.zero &&
                    ShouldKeepDoorThresholdAdvance(playerPosition, sourceTarget, _doorTraversalPushThroughPosition);
                bool shouldCommitPostThreshold = _doorTraversalPostThresholdCommitted ||
                    pushThroughDistance <= DoorPushThroughArrivalDistance ||
                    (sourceTarget != Vector3.zero &&
                     sourceThresholdDistance <= DoorPushThroughArrivalDistance &&
                     !shouldKeepDoorThresholdAdvance);
                if (shouldCommitPostThreshold)
                    _doorTraversalPostThresholdCommitted = true;
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
                        _doorTraversalPushThroughPosition,
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

                if (!shouldCommitPostThreshold &&
                    pushThroughDistance > DoorPushThroughArrivalDistance)
                {
                    position = _doorTraversalPushThroughPosition;
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " stage=DoorPushThrough" +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                if (step.ToWaypoint != Vector3.zero)
                {
                    _doorTraversalPostThresholdCommitted = true;
                    position = step.ToWaypoint;
                    targetKind = NavigationTargetKind.EntryWaypoint;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=EntryWaypoint position=" + FormatVector3(position) +
                        " stage=DoorEntryAdvance" +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                if (TryGetZonePosition(step.ToZone, out position))
                {
                    _doorTraversalPostThresholdCommitted = true;
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " stage=DoorEntryAdvance" +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }
            }

            if (!_doorTraversalInteractionTriggered)
            {
                position = step.FromWaypoint != Vector3.zero ? step.FromWaypoint : playerPosition;
                targetKind = NavigationTargetKind.TransitionInteractable;
                LogNavigationTrackerDebug(
                    "Next navigation target kind=TransitionInteractable position=" + FormatVector3(position) +
                    " stage=DoorInteractionRetry" +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            return false;
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
                    planningZone = step.FromZone;
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
                    planningZone = step.FromZone;
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

            float maxSnapDistance = DoorTransitionSweepDoorClearanceDistance + DoorPushThroughArrivalDistance;
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
