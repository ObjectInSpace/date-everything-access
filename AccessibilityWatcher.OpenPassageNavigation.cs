using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DateEverythingAccess
{
    internal sealed partial class AccessibilityWatcher
    {
        private bool TryGetOpenPassageNavigationTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                string.IsNullOrEmpty(step.FromZone) ||
                string.IsNullOrEmpty(step.ToZone) ||
                step.FromWaypoint == Vector3.zero ||
                step.ToWaypoint == Vector3.zero)
            {
                return false;
            }

            Vector3 fromWaypoint = step.FromWaypoint;
            fromWaypoint.y = playerPosition.y;
            float fromDistance = Vector3.Distance(playerPosition, fromWaypoint);
            Vector3 sourceSegmentTarget = GetOpenPassageSourceSegmentTarget(step);
            sourceSegmentTarget.y = playerPosition.y;
            float sourceSegmentDistance = Vector3.Distance(playerPosition, sourceSegmentTarget);
            Vector3 destinationWaypointPosition = GetOpenPassageDestinationWaypointPosition(step);
            destinationWaypointPosition.y = playerPosition.y;
            float destinationWaypointDistance = Vector3.Distance(playerPosition, destinationWaypointPosition);
            OpenPassageTraversalStage traversalStage = GetOpenPassageTraversalStage(
                step,
                currentZone,
                playerPosition,
                fromDistance,
                sourceSegmentDistance,
                destinationWaypointDistance);

            switch (traversalStage)
            {
                case OpenPassageTraversalStage.SourceWaypoint:
                    position = step.FromWaypoint;
                    targetKind = NavigationTargetKind.ExitWaypoint;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ExitWaypoint position=" + FormatVector3(position) +
                        " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " sourceSegmentDistance=" + sourceSegmentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " destinationWaypointDistance=" + destinationWaypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " stage=SourceWaypoint" +
                        " step=" + DescribeNavigationStep(step));
                    return true;

                case OpenPassageTraversalStage.SourceHandoff:
                    if (TryGetOpenPassageGuidedNavigationTarget(
                        step,
                        playerPosition,
                        out Vector3 sourceOverrideTarget,
                        out int sourceOverrideIndex,
                        out int sourceOverrideCount,
                        out bool sourceOverrideIsFinal))
                    {
                        position = BuildOpenPassageGuidedMovementTarget(playerPosition, sourceOverrideTarget);
                        targetKind = NavigationTargetKind.ZoneFallback;
                        _rawNavigationTargetContext = "open-passage-guided";
                        LogNavigationTrackerDebug(
                            "Next navigation target kind=" + targetKind +
                            " position=" + FormatVector3(position) +
                            " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " sourceSegmentDistance=" + sourceSegmentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " destinationWaypointDistance=" + destinationWaypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " overrideWaypoint=" + (sourceOverrideIndex + 1) + " of " + sourceOverrideCount +
                            " overrideFinal=" + sourceOverrideIsFinal +
                            " stage=SourceHandoff" +
                            " reason=override navigation_transition_overrides.json" +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    int sourceHandoffRecoveryAttempts = GetOpenPassageRecoveryAttemptsForStage(traversalStage);
                    float sourceHandoffDistance = AutoWalkOpenPassageHandoffDistance;
                    position = BuildOpenPassageSourceHandoffPosition(
                        step,
                        playerPosition,
                        sourceHandoffDistance,
                        _openPassageSourceHandoffProgressFloor,
                        out float sourceHandoffProgress);
                    _openPassageSourceHandoffProgressFloor = Mathf.Max(_openPassageSourceHandoffProgressFloor, sourceHandoffProgress);
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " sourceSegmentDistance=" + sourceSegmentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " destinationWaypointDistance=" + destinationWaypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " recoveryAttempts=" + sourceHandoffRecoveryAttempts +
                        " progressFloor=" + _openPassageSourceHandoffProgressFloor.ToString("0.00", CultureInfo.InvariantCulture) +
                        " stage=SourceHandoff" +
                        " reason=push through open-passage source threshold" +
                        " step=" + DescribeNavigationStep(step));
                    return true;

                case OpenPassageTraversalStage.DestinationWaypoint:
                    if (TryGetOpenPassageGuidedNavigationTarget(
                        step,
                        playerPosition,
                        out Vector3 destinationOverrideTarget,
                        out int destinationOverrideIndex,
                        out int destinationOverrideCount,
                        out bool destinationOverrideIsFinal))
                    {
                        position = BuildOpenPassageGuidedMovementTarget(playerPosition, destinationOverrideTarget);
                        targetKind = destinationOverrideIsFinal
                            ? NavigationTargetKind.EntryWaypoint
                            : NavigationTargetKind.ZoneFallback;
                        _rawNavigationTargetContext = "open-passage-guided";
                        LogNavigationTrackerDebug(
                            "Next navigation target kind=" + targetKind +
                            " position=" + FormatVector3(position) +
                            " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " sourceSegmentDistance=" + sourceSegmentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " destinationWaypointDistance=" + destinationWaypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " overrideWaypoint=" + (destinationOverrideIndex + 1) + " of " + destinationOverrideCount +
                            " stage=DestinationWaypoint" +
                            " reason=override navigation_transition_overrides.json" +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    position = destinationWaypointPosition;
                    targetKind = NavigationTargetKind.EntryWaypoint;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=EntryWaypoint position=" + FormatVector3(position) +
                        " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " sourceSegmentDistance=" + sourceSegmentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " destinationWaypointDistance=" + destinationWaypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " stage=DestinationWaypoint" +
                        " reason=approach open-passage destination threshold" +
                        " step=" + DescribeNavigationStep(step));
                    return true;

                case OpenPassageTraversalStage.DestinationHandoff:
                    if (TryGetOpenPassageGuidedNavigationTarget(
                        step,
                        playerPosition,
                        out Vector3 handoffOverrideTarget,
                        out int handoffOverrideIndex,
                        out int handoffOverrideCount,
                        out bool handoffOverrideIsFinal))
                    {
                        position = BuildOpenPassageGuidedMovementTarget(playerPosition, handoffOverrideTarget);
                        targetKind = handoffOverrideIsFinal
                            ? NavigationTargetKind.EntryWaypoint
                            : NavigationTargetKind.ZoneFallback;
                        _rawNavigationTargetContext = "open-passage-guided";
                        LogNavigationTrackerDebug(
                            "Next navigation target kind=" + targetKind +
                            " position=" + FormatVector3(position) +
                            " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " sourceSegmentDistance=" + sourceSegmentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " destinationWaypointDistance=" + destinationWaypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " overrideWaypoint=" + (handoffOverrideIndex + 1) + " of " + handoffOverrideCount +
                            " stage=DestinationHandoff" +
                            " reason=override navigation_transition_overrides.json" +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    int destinationHandoffRecoveryAttempts = GetOpenPassageRecoveryAttemptsForStage(traversalStage);
                    float destinationHandoffDistance = AutoWalkOpenPassageHandoffDistance;
                    position = BuildOpenPassageDestinationHandoffPosition(
                        step,
                        playerPosition,
                        destinationHandoffDistance,
                        _openPassageDestinationHandoffProgressFloor,
                        out float destinationHandoffProgress);
                    _openPassageDestinationHandoffProgressFloor = Mathf.Max(_openPassageDestinationHandoffProgressFloor, destinationHandoffProgress);
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " sourceSegmentDistance=" + sourceSegmentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " destinationWaypointDistance=" + destinationWaypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                        " recoveryAttempts=" + destinationHandoffRecoveryAttempts +
                        " progressFloor=" + _openPassageDestinationHandoffProgressFloor.ToString("0.00", CultureInfo.InvariantCulture) +
                        " stage=DestinationHandoff" +
                        " reason=push beyond open-passage destination threshold" +
                        " step=" + DescribeNavigationStep(step));
                    return true;
            }

            return false;
        }

        private bool TryResolveOpenPassageLocalNavigationGoal(
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
            if (step == null || step.Kind != NavigationGraph.StepKind.OpenPassage)
                return false;

            bool isInSourceZone = IsOpenPassageSourceZone(step, currentZone);
            bool isInDestinationZone = !string.IsNullOrEmpty(step.ToZone) &&
                (IsZoneEquivalentToNavigationZone(currentZone, step.ToZone) ||
                 IsAcceptedOverrideDestinationZone(step, currentZone));
            bool hasOverridePlanningGoal = TryGetOpenPassageOverridePlanningGoal(
                step,
                playerPosition,
                out Vector3 overridePlanningGoal);

            if (isInSourceZone)
            {
                Vector3 sourceGoal = _openPassageTraversalStage == OpenPassageTraversalStage.SourceWaypoint
                    ? GetOpenPassageSourceGuidanceOrigin(step)
                    : GetOpenPassageSourceHandoffOrigin(step);
                string sourcePlanningContext = _openPassageTraversalStage == OpenPassageTraversalStage.SourceWaypoint
                    ? "open-passage-source"
                    : "open-passage-handoff";
                if (hasOverridePlanningGoal)
                {
                    sourceGoal = overridePlanningGoal;
                    sourcePlanningContext = "open-passage-override-source";
                }

                if (sourceGoal != Vector3.zero &&
                    ShouldUseLocalNavigationGoal(
                        playerPosition,
                        sourceGoal,
                        GetLocalNavigationGoalReachedDistance(sourcePlanningContext)))
                {
                    planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, sourceGoal);
                    planningGoal = sourceGoal;
                    planningContext = sourcePlanningContext;
                    return true;
                }

                return false;
            }

            if (isInDestinationZone)
            {
                Vector3 destinationGoal = GetOpenPassageDestinationApproachPosition(step);
                string destinationPlanningContext = "open-passage-destination";
                if (hasOverridePlanningGoal)
                {
                    destinationGoal = overridePlanningGoal;
                    destinationPlanningContext = "open-passage-override-destination";
                }

                if (destinationGoal == Vector3.zero)
                    destinationGoal = desiredPosition;

                if (destinationGoal != Vector3.zero &&
                    ShouldUseLocalNavigationGoal(
                        playerPosition,
                        destinationGoal,
                        GetLocalNavigationGoalReachedDistance(destinationPlanningContext)))
                {
                    planningZone = ResolveLocalPlanningZone(currentZone, step.ToZone, playerPosition, destinationGoal);
                    planningGoal = destinationGoal;
                    planningContext = destinationPlanningContext;
                    return true;
                }

                return false;
            }

            return false;
        }

        private static bool IsOpenPassageSourceZone(NavigationGraph.PathStep step, string currentZone)
        {
            return !string.IsNullOrEmpty(step?.FromZone) &&
                (IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                 IsAcceptedOverrideSourceZone(step, currentZone));
        }

        private bool TryGetOpenPassageOverridePlanningGoal(
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            out Vector3 planningGoal)
        {
            planningGoal = Vector3.zero;
            if (step == null ||
                !TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) ||
                transitionOverride.UseExplicitCrossingSegments ||
                transitionOverride.IntermediateWaypoints == null ||
                transitionOverride.IntermediateWaypoints.Length == 0)
            {
                return false;
            }

            if (TryGetOpenPassageGuidedNavigationTarget(
                    step,
                    playerPosition,
                    out Vector3 guidedTarget,
                    out _,
                    out _,
                    out _)
                && guidedTarget != Vector3.zero)
            {
                planningGoal = guidedTarget;
                return true;
            }

            List<Vector3> navigationPoints = BuildOpenPassageGuidedNavigationPoints(step);
            if (navigationPoints == null || navigationPoints.Count == 0)
                return false;

            int currentIndex = Mathf.Clamp(_openPassageOverrideWaypointIndex, 0, navigationPoints.Count - 1);
            planningGoal = navigationPoints[currentIndex];
            return planningGoal != Vector3.zero;
        }
    }
}
