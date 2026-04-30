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
                !UsesOpenPassageTraversalModel(step) ||
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
            if (step == null || !UsesOpenPassageTraversalModel(step))
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
                OpenPassageTraversalStage traversalStage = GetOpenPassageTraversalStageState();
                Vector3 sourceGoal = traversalStage == OpenPassageTraversalStage.SourceWaypoint
                    ? GetOpenPassageSourceGuidanceOrigin(step)
                    : GetOpenPassageSourceHandoffOrigin(step);
                string sourcePlanningContext = traversalStage == OpenPassageTraversalStage.SourceWaypoint
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
                    bool allowAcceptedSourceZone = IsAcceptedOverrideSourceZone(step, currentZone);
                    if (TryResolveOpenPassagePlanningZone(
                            currentZone,
                            step.FromZone,
                            allowAcceptedSourceZone,
                            playerPosition,
                            sourceGoal,
                            out string resolvedPlanningZone))
                    {
                        sourceGoal = ResolveOpenPassageReachablePlanningGoal(
                            resolvedPlanningZone,
                            step,
                            playerPosition,
                            sourceGoal,
                            sourcePlanningContext);
                        if (!ShouldUseLocalNavigationGoal(
                                playerPosition,
                                sourceGoal,
                                GetLocalNavigationGoalReachedDistance(sourcePlanningContext)))
                        {
                            return false;
                        }

                        planningZone = resolvedPlanningZone;
                        planningGoal = sourceGoal;
                        planningContext = sourcePlanningContext;
                        return true;
                    }

                    LogNavigationTrackerDebug(
                        "Skipped open-passage source local planning due to unavailable planning zone" +
                        " currentZone=" + (currentZone ?? "<null>") +
                        " sourceGoal=" + FormatVector3(sourceGoal) +
                        " context=" + sourcePlanningContext +
                        " step=" + DescribeNavigationStep(step));
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
                    bool allowAcceptedDestinationZone = IsAcceptedOverrideDestinationZone(step, currentZone);
                    if (TryResolveOpenPassagePlanningZone(
                            currentZone,
                            step.ToZone,
                            allowAcceptedDestinationZone,
                            playerPosition,
                            destinationGoal,
                            out string resolvedPlanningZone))
                    {
                        destinationGoal = ResolveOpenPassageReachablePlanningGoal(
                            resolvedPlanningZone,
                            step,
                            playerPosition,
                            destinationGoal,
                            destinationPlanningContext);
                        if (!ShouldUseLocalNavigationGoal(
                                playerPosition,
                                destinationGoal,
                                GetLocalNavigationGoalReachedDistance(destinationPlanningContext)))
                        {
                            return false;
                        }

                        planningZone = resolvedPlanningZone;
                        planningGoal = destinationGoal;
                        planningContext = destinationPlanningContext;
                        return true;
                    }

                    LogNavigationTrackerDebug(
                        "Skipped open-passage destination local planning due to unavailable planning zone" +
                        " currentZone=" + (currentZone ?? "<null>") +
                        " destinationGoal=" + FormatVector3(destinationGoal) +
                        " context=" + destinationPlanningContext +
                        " step=" + DescribeNavigationStep(step));
                }

                return false;
            }

            return false;
        }

        private Vector3 ResolveOpenPassageReachablePlanningGoal(
            string planningZone,
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            Vector3 planningGoal,
            string planningContext)
        {
            bool canUseReachableProxy =
                UsesExplicitOpenPassageCrossingSegments(step) &&
                (string.Equals(planningContext, "open-passage-handoff", System.StringComparison.Ordinal) ||
                 string.Equals(planningContext, "open-passage-destination", System.StringComparison.Ordinal));
            canUseReachableProxy =
                canUseReachableProxy ||
                (UsesOverrideOnlyOpenPassageGuidance(step) &&
                 (string.Equals(planningContext, "open-passage-override-source", System.StringComparison.Ordinal) ||
                  string.Equals(planningContext, "open-passage-override-destination", System.StringComparison.Ordinal)));

            if (string.IsNullOrWhiteSpace(planningZone) ||
                planningGoal == Vector3.zero ||
                !canUseReachableProxy)
            {
                return planningGoal;
            }

            if (!LocalNavigationMaps.TryResolveReachableProxyInStartComponent(
                    planningZone,
                    playerPosition,
                    planningGoal,
                    out Vector3 proxyGoal,
                    out string proxyDetail) ||
                proxyGoal == Vector3.zero)
            {
                return planningGoal;
            }

            LogNavigationTrackerDebug(
                "Using reachable open-passage local planning proxy" +
                " planningZone=" + planningZone +
                " context=" + (planningContext ?? "<null>") +
                " originalGoal=" + FormatVector3(planningGoal) +
                " proxyGoal=" + FormatVector3(proxyGoal) +
                " detail=" + (proxyDetail ?? "<null>") +
                " step=" + DescribeNavigationStep(step));
            return proxyGoal;
        }

        private bool HasReachedOpenPassageReachableSourceProxy(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out string detail)
        {
            detail = null;
            if (step == null ||
                !UsesExplicitOpenPassageCrossingSegments(step))
            {
                return false;
            }

            Vector3 sourceTarget = GetOpenPassageSourceSegmentTarget(step);
            if (sourceTarget == Vector3.zero)
                return false;

            bool allowAcceptedSourceZone = IsAcceptedOverrideSourceZone(step, currentZone);
            if (!TryResolveOpenPassagePlanningZone(
                    currentZone,
                    step.FromZone,
                    allowAcceptedSourceZone,
                    playerPosition,
                    sourceTarget,
                    out string planningZone))
            {
                return false;
            }

            if (!LocalNavigationMaps.TryResolveReachableProxyInStartComponent(
                    planningZone,
                    playerPosition,
                    sourceTarget,
                    out Vector3 proxyGoal,
                    out string proxyDetail) ||
                proxyGoal == Vector3.zero)
            {
                return false;
            }

            float proxyDistance = GetFlatDistance(playerPosition, proxyGoal);
            string progressDetail = null;
            if (proxyDistance > OpenPassageOverrideLocalNavigationGoalReachedDistance &&
                !HasCompletedOpenPassageSourceProxyBySegmentProgress(
                    step,
                    playerPosition,
                    proxyGoal,
                    sourceTarget,
                    out progressDetail))
            {
                return false;
            }

            detail =
                "planningZone=" + planningZone +
                " proxyGoal=" + FormatVector3(proxyGoal) +
                " proxyDistance=" + proxyDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " sourceTarget=" + FormatVector3(sourceTarget) +
                " proxyDetail=" + (proxyDetail ?? "<null>") +
                " progressDetail=" + (progressDetail ?? "<null>");
            return true;
        }

        private static bool HasCompletedOpenPassageSourceProxyBySegmentProgress(
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            Vector3 proxyGoal,
            Vector3 sourceTarget,
            out string detail)
        {
            detail = null;
            if (step == null || proxyGoal == Vector3.zero || sourceTarget == Vector3.zero)
                return false;

            Vector3 sourceStart = GetOpenPassageSourceGuidanceOrigin(step);
            if (sourceStart == Vector3.zero ||
                !TryGetSegmentMetrics(
                    sourceStart,
                    sourceTarget,
                    playerPosition,
                    out Vector3 segmentDirection,
                    out float playerProgress,
                    out float segmentLength) ||
                segmentLength <= 0f)
            {
                return false;
            }

            Vector3 proxyOffset = proxyGoal - sourceStart;
            proxyOffset.y = 0f;
            float proxyProgress = Mathf.Clamp(Vector3.Dot(proxyOffset, segmentDirection), 0f, segmentLength);
            Vector3 closestPointOnSegment = sourceStart + segmentDirection * Mathf.Clamp(playerProgress, 0f, segmentLength);
            closestPointOnSegment.y = playerPosition.y;
            float lateralDistance = GetFlatDistance(playerPosition, closestPointOnSegment);
            float requiredProgress = Mathf.Max(0f, proxyProgress - OpenPassageOverrideLocalNavigationGoalReachedDistance);
            if (playerProgress < requiredProgress ||
                lateralDistance > AutoWalkZoneBoundaryFallbackDistance)
            {
                return false;
            }

            detail =
                "playerProgress=" + playerProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                " proxyProgress=" + proxyProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                " requiredProgress=" + requiredProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                " segmentLength=" + segmentLength.ToString("0.00", CultureInfo.InvariantCulture) +
                " lateralDistance=" + lateralDistance.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryResolveOpenPassagePlanningZone(
            string currentZone,
            string canonicalZone,
            bool allowCurrentZoneFallback,
            Vector3 playerPosition,
            Vector3 planningGoal,
            out string planningZone)
        {
            planningZone = ResolveLocalPlanningZone(
                currentZone,
                canonicalZone,
                playerPosition,
                planningGoal);
            if (!string.IsNullOrWhiteSpace(planningZone))
                return true;

            if (!allowCurrentZoneFallback ||
                string.IsNullOrWhiteSpace(currentZone) ||
                !CanUseLocalPlanningZone(currentZone, playerPosition, planningGoal))
            {
                return false;
            }

            planningZone = currentZone;
            return true;
        }

        private static bool IsOpenPassageSourceZone(NavigationGraph.PathStep step, string currentZone)
        {
            return !string.IsNullOrEmpty(step?.FromZone) &&
                (IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                 IsAcceptedOverrideSourceZone(step, currentZone));
        }

        private static bool UsesOpenPassageTraversalModel(NavigationGraph.PathStep step)
        {
            if (step == null)
                return false;

            if (step.Kind == NavigationGraph.StepKind.OpenPassage)
                return true;

            return step.Kind == NavigationGraph.StepKind.Stairs &&
                TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                transitionOverride.IntermediateWaypoints != null &&
                transitionOverride.IntermediateWaypoints.Length > 0;
        }

        private static bool UsesExplicitOpenPassageCrossingSegments(NavigationGraph.PathStep step)
        {
            return step != null &&
                TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                transitionOverride.UseExplicitCrossingSegments;
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
