using System;
using System.Collections.Generic;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Validates that navigation targets are reachable before being selected.
    /// Prevents frame-by-frame reselection of unreachable targets.
    /// </summary>
    internal sealed partial class AccessibilityWatcher
    {
        /// <summary>
        /// Checks if a target position is reachable from the player's current position.
        /// Returns false if target is physically blocked or in a disconnected component.
        /// </summary>
        private bool IsTargetReachableFromCurrentPlayer(
            Vector3 targetPosition,
            string targetZone,
            string sourceZone,
            NavigationGraph.PathStep step,
            string methodContext)
        {
            // Null checks
            if (targetPosition == Vector3.zero || string.IsNullOrEmpty(targetZone))
                return false;

            if (!LocalNavigationMaps.IsAvailable)
                return true; // Can't validate without maps, allow

            BetterPlayerControl player = BetterPlayerControl.Instance;
            if (player == null)
                return true; // Can't validate without player

            Vector3 playerPosition = player.transform.position;
            string playerZone = GetCurrentZoneNameForNavigation();

            // If source and target are same zone, use local path validation
            if (!string.IsNullOrEmpty(playerZone) &&
                !string.IsNullOrEmpty(sourceZone) &&
                IsZoneEquivalentToNavigationZone(playerZone, sourceZone) &&
                IsZoneEquivalentToNavigationZone(playerZone, targetZone))
            {
                // Try to find a local path from player to target
                if (!LocalNavigationMaps.TryFindPath(
                    playerZone,
                    playerPosition,
                    targetPosition,
                    out List<Vector3> pathPoints,
                    out string pathFailureReason))
                {
                    LogNavigationTrackerDebug(
                        "Target reachability check FAILED (no local path)" +
                        " methodContext=" + (methodContext ?? "<null>") +
                        " targetPosition=" + FormatVector3(targetPosition) +
                        " playerPosition=" + FormatVector3(playerPosition) +
                        " reason=" + (pathFailureReason ?? "<null>") +
                        " zone=" + playerZone +
                        " step=" + DescribeNavigationStep(step));
                    return false;
                }

                // Path exists. Check if the first waypoint segment is physically clear.
                Vector3 firstWaypoint = pathPoints != null && pathPoints.Count > 0
                    ? pathPoints[0]
                    : targetPosition;

                Collider playerCollider = player.GetComponent<Collider>();
                if (playerCollider != null)
                {
                    string blockDetail = ProbeMovementBlocker(
                        playerCollider,
                        playerPosition,
                        firstWaypoint,
                        requireTrigger: false,
                        minimumHitDistance: 0.05f);

                    if (!string.Equals(blockDetail, "none", StringComparison.Ordinal) &&
                        !string.Equals(blockDetail, "target-too-close", StringComparison.Ordinal))
                    {
                        LogNavigationTrackerDebug(
                            "Target reachability check FAILED (first segment blocked)" +
                            " methodContext=" + (methodContext ?? "<null>") +
                            " targetPosition=" + FormatVector3(targetPosition) +
                            " playerPosition=" + FormatVector3(playerPosition) +
                            " firstWaypoint=" + FormatVector3(firstWaypoint) +
                            " blocker=" + (blockDetail ?? "<null>") +
                            " zone=" + playerZone +
                            " step=" + DescribeNavigationStep(step));
                        return false;
                    }
                }

                LogNavigationTrackerDebug(
                    "Target reachability check PASSED" +
                    " methodContext=" + (methodContext ?? "<null>") +
                    " targetPosition=" + FormatVector3(targetPosition) +
                    " playerPosition=" + FormatVector3(playerPosition) +
                    " zone=" + playerZone +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            // Cross-zone transitions: can't validate with local maps alone
            // Trust the transition system's waypoints
            LogNavigationTrackerDebug(
                "Target reachability check SKIPPED (cross-zone)" +
                " methodContext=" + (methodContext ?? "<null>") +
                " targetPosition=" + FormatVector3(targetPosition) +
                " playerZone=" + (playerZone ?? "<null>") +
                " sourceZone=" + (sourceZone ?? "<null>") +
                " targetZone=" + targetZone +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        /// <summary>
        /// Tracks which targets have been rejected this frame to prevent reselection loops.
        /// </summary>
        private sealed class TargetRejectionTracker
        {
            private string _currentStepKey;
            private readonly System.Collections.Generic.HashSet<Vector3> _rejectedTargets
                = new System.Collections.Generic.HashSet<Vector3>();

            public void OnStepChanged(string newStepKey)
            {
                if (!string.Equals(_currentStepKey, newStepKey, StringComparison.Ordinal))
                {
                    _currentStepKey = newStepKey;
                    _rejectedTargets.Clear();
                }
            }

            public bool IsTargetRejected(Vector3 target, float tolerance = 0.1f)
            {
                foreach (var rejected in _rejectedTargets)
                {
                    if (Vector3.Distance(rejected, target) < tolerance)
                        return true;
                }
                return false;
            }

            public void RecordRejection(Vector3 target)
            {
                _rejectedTargets.Add(target);
            }

            public void Clear()
            {
                _currentStepKey = null;
                _rejectedTargets.Clear();
            }
        }

        /// <summary>
        /// Central target validation layer for navigation.
        /// Validates candidates before selection to prevent reselection loops.
        /// </summary>
        private bool TrySelectValidatedNavigationTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            in (Vector3 pos, NavigationTargetKind kind, string context)? doorCandidate,
            in (Vector3 pos, NavigationTargetKind kind, string context)? openPassageCandidate,
            in (Vector3 pos, NavigationTargetKind kind)? zoneFallbackCandidate,
            out Vector3 selectedPosition,
            out NavigationTargetKind selectedKind)
        {
            selectedPosition = Vector3.zero;
            selectedKind = NavigationTargetKind.ZoneFallback;

            string stepKey = BuildNavigationStepKey(step);
            string targetZone = step?.ToZone ?? "<unknown>";

            // Try door target first
            if (doorCandidate.HasValue)
            {
                var (pos, kind, ctx) = doorCandidate.Value;
                if (pos != Vector3.zero &&
                    IsTargetReachableFromCurrentPlayer(pos, targetZone, currentZone, step, "door-candidate"))
                {
                    selectedPosition = pos;
                    selectedKind = kind;
                    LogNavigationTrackerDebug(
                        "Target selection chose door candidate" +
                        " position=" + FormatVector3(pos) +
                        " kind=" + kind +
                        " stepKey=" + stepKey);
                    return true;
                }
            }

            // Try open passage target second
            if (openPassageCandidate.HasValue)
            {
                var (pos, kind, ctx) = openPassageCandidate.Value;
                if (pos != Vector3.zero &&
                    IsTargetReachableFromCurrentPlayer(pos, targetZone, currentZone, step, "open-passage-candidate"))
                {
                    selectedPosition = pos;
                    selectedKind = kind;
                    LogNavigationTrackerDebug(
                        "Target selection chose open-passage candidate" +
                        " position=" + FormatVector3(pos) +
                        " kind=" + kind +
                        " stepKey=" + stepKey);
                    return true;
                }
            }

            // Try zone fallback last
            if (zoneFallbackCandidate.HasValue)
            {
                var (pos, kind) = zoneFallbackCandidate.Value;
                if (pos != Vector3.zero &&
                    IsTargetReachableFromCurrentPlayer(pos, targetZone, currentZone, step, "zone-fallback-candidate"))
                {
                    selectedPosition = pos;
                    selectedKind = kind;
                    LogNavigationTrackerDebug(
                        "Target selection chose zone-fallback candidate" +
                        " position=" + FormatVector3(pos) +
                        " kind=" + kind +
                        " stepKey=" + stepKey);
                    return true;
                }
            }

            // No candidates passed validation
            LogNavigationTrackerDebug(
                "Target selection FAILED: no reachable candidates" +
                " stepKey=" + stepKey +
                " hasDoor=" + doorCandidate.HasValue +
                " hasOpenPassage=" + openPassageCandidate.HasValue +
                " hasZoneFallback=" + zoneFallbackCandidate.HasValue);
            return false;
        }
    }
}
