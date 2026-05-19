#define VALIDATION_TEST_ENABLED

using System;
using System.Collections.Generic;
using UnityEngine;

namespace DateEverythingAccess
{
#if VALIDATION_TEST_ENABLED
    internal sealed partial class AccessibilityWatcher
    {
        /// <summary>
        /// Validation test hook for the bathroom1->hallway door case.
        /// Returns true if this is the test case, and sets shouldUseTarget accordingly.
        /// Returns false if this is not the test case (caller ignores shouldUseTarget).
        /// </summary>
        private bool TryValidateTargetForTestCase(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 targetPosition,
            string targetKind,
            out bool shouldUseTarget)
        {
            shouldUseTarget = true;

            if (step == null ||
                !string.Equals(step.FromZone, "bathroom1", StringComparison.Ordinal) ||
                !string.Equals(step.ToZone, "hallway", StringComparison.Ordinal) ||
                !string.Equals(step.ConnectorName, "Doors_Bathroom1", StringComparison.Ordinal))
            {
                return false;
            }

            BetterPlayerControl player = BetterPlayerControl.Instance;
            if (player == null)
                return false;

            Vector3 playerPos = player.transform.position;

            DebugLogger.Log(
                LogCategory.State,
                $"TARGET_VALIDATION_TEST: bathroom1->hallway candidate" +
                $" target={FormatVector3(targetPosition)}" +
                $" player={FormatVector3(playerPos)}" +
                $" targetKind={targetKind}" +
                $" distance={Vector3.Distance(playerPos, targetPosition):F2}m");

            if (!LocalNavigationMaps.IsAvailable ||
                string.IsNullOrEmpty(currentZone) ||
                !string.Equals(currentZone, "bathroom1", StringComparison.Ordinal))
            {
                // Can't validate — let it through unchanged
                return false;
            }

            bool pathExists = LocalNavigationMaps.TryFindPath(
                currentZone,
                playerPos,
                targetPosition,
                out List<Vector3> pathPoints,
                out string failureReason);

            if (!pathExists)
            {
                DebugLogger.Log(
                    LogCategory.State,
                    $"TARGET_VALIDATION_TEST: FAILED no local path" +
                    $" from={FormatVector3(playerPos)}" +
                    $" to={FormatVector3(targetPosition)}" +
                    $" reason={failureReason}");
                shouldUseTarget = false;
                return true;
            }

            // Path exists — check if the first segment is physically clear
            Vector3 firstWaypoint = pathPoints != null && pathPoints.Count > 0
                ? pathPoints[0]
                : targetPosition;

            Collider playerCollider = player.GetComponent<Collider>();
            if (playerCollider != null)
            {
                string blockDetail = ProbeMovementBlocker(
                    playerCollider,
                    playerPos,
                    firstWaypoint,
                    requireTrigger: false,
                    minimumHitDistance: 0.05f);

                bool isBlocked = !string.Equals(blockDetail, "none", StringComparison.Ordinal) &&
                                 !string.Equals(blockDetail, "target-too-close", StringComparison.Ordinal) &&
                                 !string.Equals(blockDetail, "player-collider-null", StringComparison.Ordinal);

                if (isBlocked)
                {
                    DebugLogger.Log(
                        LogCategory.State,
                        $"TARGET_VALIDATION_TEST: FAILED first segment blocked" +
                        $" blocker={blockDetail}" +
                        $" firstWaypoint={FormatVector3(firstWaypoint)}");
                    shouldUseTarget = false;
                    return true;
                }
            }

            DebugLogger.Log(
                LogCategory.State,
                $"TARGET_VALIDATION_TEST: PASSED target is reachable" +
                $" pathPoints={pathPoints?.Count ?? 0}");
            shouldUseTarget = true;
            return true;
        }
    }
#endif
}
