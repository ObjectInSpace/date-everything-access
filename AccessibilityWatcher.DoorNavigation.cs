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
                Vector3 sourceTarget = Vector3.zero;
                if (TryGetDoorThresholdAdvanceTarget(step, currentZone, out sourceTarget))
                {
                    sourceThresholdDistance = GetPlanarDistanceToTarget(playerPosition, sourceTarget);
                }

                pushThroughDistance = GetPlanarDistanceToTarget(playerPosition, _transitionSweepSession.DoorPushThroughPosition);
                bool shouldKeepDoorThresholdAdvance = sourceTarget != Vector3.zero &&
                    ShouldKeepDoorThresholdAdvance(playerPosition, sourceTarget, _transitionSweepSession.DoorPushThroughPosition);

                if (sourceTarget != Vector3.zero &&
                    sourceThresholdDistance > DoorPushThroughSourceAdvanceDistance)
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

                if (sourceTarget != Vector3.zero &&
                    (shouldKeepDoorThresholdAdvance ||
                     pushThroughDistance <= DoorPushThroughArrivalDistance))
                {
                    position = BuildDoorThresholdHandoffPosition(
                        sourceTarget,
                        _transitionSweepSession.DoorPushThroughPosition);
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
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
                Vector3 sourceTarget = Vector3.zero;
                if (TryGetDoorThresholdAdvanceTarget(step, currentZone, out sourceTarget))
                {
                    sourceThresholdDistance = GetPlanarDistanceToTarget(playerPosition, sourceTarget);
                }

                pushThroughDistance = GetPlanarDistanceToTarget(playerPosition, _doorTraversalPushThroughPosition);
                bool shouldKeepDoorThresholdAdvance = sourceTarget != Vector3.zero &&
                    ShouldKeepDoorThresholdAdvance(playerPosition, sourceTarget, _doorTraversalPushThroughPosition);

                if (sourceTarget != Vector3.zero &&
                    sourceThresholdDistance > DoorPushThroughSourceAdvanceDistance)
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

                if (sourceTarget != Vector3.zero &&
                    (shouldKeepDoorThresholdAdvance ||
                     pushThroughDistance <= DoorPushThroughArrivalDistance))
                {
                    position = BuildDoorThresholdHandoffPosition(
                        sourceTarget,
                        _doorTraversalPushThroughPosition);
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " sourceThresholdDistance=" + sourceThresholdDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " pushThroughDistance=" + pushThroughDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " stage=DoorThresholdHandoff" +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                if (pushThroughDistance > DoorPushThroughArrivalDistance)
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
            if (step == null)
                return false;

            if (!string.IsNullOrEmpty(step.FromZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) &&
                TryGetDoorThresholdAdvanceTarget(step, currentZone, out Vector3 doorThresholdTarget))
            {
                if (GetFlatDistance(doorThresholdTarget, desiredPosition) <= DoorPushThroughSourceAdvanceDistance &&
                    ShouldUseLocalNavigationGoal(
                        playerPosition,
                        desiredPosition,
                        GetLocalNavigationGoalReachedDistance("door-threshold-handoff")))
                {
                    planningZone = step.FromZone;
                    planningGoal = desiredPosition;
                    planningContext = "door-threshold-handoff";
                    return true;
                }
            }

            if (TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 activeDoorPushThroughPosition) &&
                GetFlatDistance(activeDoorPushThroughPosition, desiredPosition) <= 0.35f)
            {
                if (ShouldUseLocalNavigationGoal(
                        playerPosition,
                        desiredPosition,
                        GetLocalNavigationGoalReachedDistance("door-push-through")))
                {
                    planningZone = step.FromZone;
                    planningGoal = desiredPosition;
                    planningContext = "door-push-through";
                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
