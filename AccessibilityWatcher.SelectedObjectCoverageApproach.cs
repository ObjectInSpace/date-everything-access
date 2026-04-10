using System;
using System.Collections.Generic;
using UnityEngine;

namespace DateEverythingAccess
{
    internal sealed partial class AccessibilityWatcher
    {
        private bool TryResolveSelectedObjectCoverageApproachTargetForStartComponent(
            string startZone,
            LocalNavigationMaps.WalkableComponentSummary startComponent,
            InteractableObj interactable,
            string navigationZone,
            Vector3 evaluationStartPosition,
            out Vector3 targetPosition,
            out string approachMode,
            out string referenceSource,
            out string detail,
            out bool usesRawApproachFallback)
        {
            targetPosition = interactable != null ? interactable.transform.position : Vector3.zero;
            targetPosition.y = evaluationStartPosition.y;
            approachMode = "raw-object";
            referenceSource = null;
            detail = "mode=raw-object";
            usesRawApproachFallback = true;

            if (interactable == null)
            {
                detail = "InteractableMissing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(navigationZone))
            {
                detail = "mode=raw-object zone=<null>";
                return false;
            }

            if (!TryBuildTrackedInteractableApproachCandidates(
                    interactable,
                    evaluationStartPosition,
                    out List<Vector3> candidateTargets,
                    out Vector3 referencePosition,
                    out string candidateDetail) ||
                candidateTargets == null ||
                candidateTargets.Count < 1)
            {
                referenceSource = candidateDetail;
                detail = "mode=raw-object candidates=0";
                return false;
            }

            referenceSource = candidateDetail;

            int preferredComponentId = -1;
            string preferredComponentSource = null;
            string preferredComponentDetail = null;
            if (!string.IsNullOrWhiteSpace(startZone) &&
                string.Equals(startZone, navigationZone, StringComparison.OrdinalIgnoreCase) &&
                startComponent != null)
            {
                preferredComponentId = startComponent.ComponentId;
                preferredComponentSource = "start-component";
            }
            else if (LocalNavigationMaps.TryGetWalkableComponentId(
                         navigationZone,
                         referencePosition,
                         out int objectComponentId,
                         out Vector3 snappedReferencePosition,
                         out string componentDetail))
            {
                preferredComponentId = objectComponentId;
                preferredComponentSource = "object-component";
                preferredComponentDetail = componentDetail + " snappedReference=" + FormatVector3(snappedReferencePosition);
            }

            if (preferredComponentId >= 0)
            {
                if (!LocalNavigationMaps.TryResolveApproachTargetForComponent(
                        navigationZone,
                        evaluationStartPosition,
                        referencePosition,
                        candidateTargets,
                        preferredComponentId,
                        out targetPosition,
                        out string resolutionDetail))
                {
                    targetPosition = interactable.transform.position;
                    targetPosition.y = evaluationStartPosition.y;
                    detail =
                        "mode=component-path-selected resolution=failed" +
                        " preferredComponentId=" + preferredComponentId +
                        " preferredComponentSource=" + (preferredComponentSource ?? "<null>") +
                        (string.IsNullOrWhiteSpace(preferredComponentDetail) ? string.Empty : " preferredComponentDetail=" + preferredComponentDetail);
                    return false;
                }

                targetPosition.y = evaluationStartPosition.y;
                detail =
                    resolutionDetail +
                    " preferredComponentId=" + preferredComponentId +
                    " preferredComponentSource=" + (preferredComponentSource ?? "<null>") +
                    (string.IsNullOrWhiteSpace(preferredComponentDetail) ? string.Empty : " preferredComponentDetail=" + preferredComponentDetail);
                approachMode = ExtractSelectedObjectCoverageMode(detail);
                usesRawApproachFallback = false;
                return true;
            }

            if (!TryResolveSelectedObjectCoverageApproachTarget(
                    interactable,
                    navigationZone,
                    evaluationStartPosition,
                    out targetPosition,
                    out approachMode,
                    out referenceSource,
                    out detail,
                    out usesRawApproachFallback))
            {
                return false;
            }

            return true;
        }
    }
}
