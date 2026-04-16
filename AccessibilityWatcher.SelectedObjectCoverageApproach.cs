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

            bool isSameNavigationZone =
                !string.IsNullOrWhiteSpace(startZone) &&
                string.Equals(startZone, navigationZone, StringComparison.OrdinalIgnoreCase);
            Vector3 candidateEvaluationStart = evaluationStartPosition;

            if (!TryBuildTrackedInteractableApproachCandidates(
                    interactable,
                    candidateEvaluationStart,
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

            List<LocalNavigationMaps.WalkableComponentSummary> navigationComponents =
                LocalNavigationMaps.GetWalkableComponents(navigationZone);
            bool hasMultipleNavigationComponents =
                navigationComponents != null && navigationComponents.Count > 1;

            bool hasObjectComponent = LocalNavigationMaps.TryGetWalkableComponentId(
                navigationZone,
                referencePosition,
                out int objectComponentId,
                out Vector3 snappedReferencePosition,
                out string objectComponentDetail);
            int preferredComponentId = -1;
            string preferredComponentSource = null;
            string preferredComponentDetail = null;
            string preferredResolutionFailureDetail = null;
            Vector3 preferredComponentEvaluationStart = candidateEvaluationStart;
            if (hasObjectComponent)
            {
                preferredComponentId = objectComponentId;
                preferredComponentSource = isSameNavigationZone
                    ? "object-component-exact-zone"
                    : "object-component";
                preferredComponentDetail = objectComponentDetail + " snappedReference=" + FormatVector3(snappedReferencePosition);
                preferredComponentEvaluationStart = isSameNavigationZone
                    ? candidateEvaluationStart
                    : snappedReferencePosition;
            }
            else if (isSameNavigationZone && startComponent != null)
            {
                preferredComponentId = startComponent.ComponentId;
                preferredComponentSource = "start-component";
            }

            if (preferredComponentId >= 0)
            {
                if (!LocalNavigationMaps.TryResolveApproachTargetForComponent(
                        navigationZone,
                        preferredComponentEvaluationStart,
                        referencePosition,
                        candidateTargets,
                        preferredComponentId,
                        out targetPosition,
                        out string resolutionDetail))
                {
                    targetPosition = interactable.transform.position;
                    preferredResolutionFailureDetail =
                        "mode=component-path-selected resolution=failed" +
                        " preferredComponentId=" + preferredComponentId +
                        " preferredComponentSource=" + (preferredComponentSource ?? "<null>") +
                        (string.IsNullOrWhiteSpace(preferredComponentDetail) ? string.Empty : " preferredComponentDetail=" + preferredComponentDetail);
                    if (isSameNavigationZone ||
                        string.Equals(preferredComponentSource, "start-component", StringComparison.Ordinal))
                    {
                        detail = preferredResolutionFailureDetail;
                        return false;
                    }
                }
                else
                {
                    detail =
                        resolutionDetail +
                        " preferredComponentId=" + preferredComponentId +
                        " preferredComponentSource=" + (preferredComponentSource ?? "<null>") +
                        (string.IsNullOrWhiteSpace(preferredComponentDetail) ? string.Empty : " preferredComponentDetail=" + preferredComponentDetail);
                    approachMode = ExtractSelectedObjectCoverageMode(detail);
                    usesRawApproachFallback = false;
                    return true;
                }
            }

            Vector3 autoSelectionEvaluationStart = evaluationStartPosition;
            string autoSelectionEvaluationSource = "evaluation-start";

            if (preferredComponentId < 0 &&
                hasMultipleNavigationComponents &&
                LocalNavigationMaps.TryResolveApproachTargetForComponent(
                    navigationZone,
                    autoSelectionEvaluationStart,
                    referencePosition,
                    candidateTargets,
                    -1,
                    out targetPosition,
                    out string autoResolutionDetail))
            {
                detail =
                    autoResolutionDetail +
                    " preferredComponentSource=auto-selected" +
                    " autoSelectionEvaluationSource=" + autoSelectionEvaluationSource +
                    " referenceSource=" + (referenceSource ?? "<null>");
                approachMode = ExtractSelectedObjectCoverageMode(detail);
                usesRawApproachFallback = false;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(preferredResolutionFailureDetail))
            {
                targetPosition = interactable.transform.position;
                detail = preferredResolutionFailureDetail;
                return false;
            }

            if (!TryResolveSelectedObjectCoverageApproachTarget(
                    interactable,
                    navigationZone,
                    candidateEvaluationStart,
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
