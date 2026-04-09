using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace DateEverythingAccess
{
    internal static class SelectedObjectCoverageReporter
    {
        [Serializable]
        internal sealed class ReportData
        {
            public int SchemaVersion = 1;
            public string GeneratedAtUtc;
            public string ActiveScene;
            public string ReportKind = "SelectedObjectCoverage";
            public string OverallStatus;
            public string FailureReason;
            public string[] Limitations;
            public SelectedObjectData SelectedObject;
            public SummaryData Summary;
            public TrackerAlignmentData TrackerAlignment;
            public EntryData[] Entries;
        }

        [Serializable]
        internal sealed class SelectedObjectData
        {
            public string InteractableId;
            public string InternalName;
            public string DisplayLabel;
            public string RuntimeZone;
            public string NavigationZone;
            public string ResolutionStatus;
            public string ResolutionDetail;
            public string ApproachMode;
            public string ApproachReferenceSource;
            public Vector3 ApproachTarget;
            public string ApproachSnapStatus;
            public string ApproachSnapDetail;
        }

        [Serializable]
        internal sealed class SummaryData
        {
            public int StartZoneCount;
            public int PassedStartZoneCount;
            public int FailedStartZoneCount;
            public int UnverifiedStartZoneCount;
            public int PassedComponentCount;
            public int FailedComponentCount;
            public int UnverifiedComponentCount;
        }

        [Serializable]
        internal sealed class TrackerAlignmentData
        {
            public bool NavigationActive;
            public bool TrackerActive;
            public string CurrentStepKey;
            public string TargetKind;
            public Vector3 MovementTarget;
            public Vector3 TrackerTarget;
            public float TargetDelta;
            public string LocalNavigationContext;
        }

        [Serializable]
        internal sealed class EntryData
        {
            public string StartZone;
            public int StartComponentId;
            public Vector3 RepresentativeStart;
            public string Status;
            public string FailureReason;
            public string StartLocalLegStatus;
            public string StartLocalLegDetail;
            public string DestinationLocalLegStatus;
            public string DestinationLocalLegDetail;
            public string ResolvedApproachMode;
            public Vector3 ResolvedApproachTarget;
            public string ResolvedApproachDetail;
            public PathStepData[] PathSteps;
        }

        [Serializable]
        internal sealed class PathStepData
        {
            public string Key;
            public string Id;
            public string FromZone;
            public string ToZone;
            public string StepKind;
            public string ConnectorName;
            public string ValidationStatus;
            public string ValidationDetail;
            public bool RequiresInteraction;
        }

        internal static string GetDefaultOutputPath()
        {
            return Path.Combine(Paths.PluginPath, "selected_object_coverage.live.json");
        }

        internal static bool TryWriteReport(
            ReportData report,
            out string outputPath,
            out string failureReason)
        {
            outputPath = GetDefaultOutputPath();
            failureReason = null;

            if (report == null)
            {
                failureReason = "ReportDataMissing";
                return false;
            }

            try
            {
                Directory.CreateDirectory(Paths.PluginPath);
                string json = JsonUtility.ToJson(report, prettyPrint: true);
                File.WriteAllText(outputPath, json);
                return true;
            }
            catch (IOException ex)
            {
                failureReason = ex.Message;
                Main.Log.LogError("Failed to write selected object coverage report: " + ex);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                failureReason = ex.Message;
                Main.Log.LogError("Failed to write selected object coverage report: " + ex);
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                Main.Log.LogError("Unexpected selected object coverage report failure: " + ex);
                return false;
            }
        }
    }
}
