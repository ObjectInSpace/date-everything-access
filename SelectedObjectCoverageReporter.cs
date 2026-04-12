using System;
using System.Globalization;
using System.IO;
using System.Text;
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
            public string PluginVersion;
            public string RuntimeBuildStamp;
            public string RuntimeAssemblyPath;
            public string RuntimeAssemblySha256;
            public string OpenPassageOverrideStatus;
            public string OpenPassageOverrideDetail;
            public int OpenPassageOverrideEntryCount;
            public bool OpenPassageOverrideNormalizedScalarArrays;
            public string OpenPassageOverridePath;
            public string OpenPassageOverrideFileSha256;
            public string OpenPassageOverrideFileLastWriteUtc;
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
                string json = BuildJson(report, prettyPrint: true);
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

        private static string BuildJson(ReportData report, bool prettyPrint)
        {
            var builder = new StringBuilder(8192);
            AppendReport(builder, report, 0, prettyPrint);
            return builder.ToString();
        }

        private static void AppendReport(StringBuilder builder, ReportData report, int indentLevel, bool prettyPrint)
        {
            AppendStartObject(builder, indentLevel, prettyPrint);
            bool first = true;

            AppendIntProperty(builder, "SchemaVersion", report.SchemaVersion, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "GeneratedAtUtc", report.GeneratedAtUtc, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "ActiveScene", report.ActiveScene, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "ReportKind", report.ReportKind, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "PluginVersion", report.PluginVersion, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "RuntimeBuildStamp", report.RuntimeBuildStamp, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "RuntimeAssemblyPath", report.RuntimeAssemblyPath, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "RuntimeAssemblySha256", report.RuntimeAssemblySha256, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "OpenPassageOverrideStatus", report.OpenPassageOverrideStatus, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "OpenPassageOverrideDetail", report.OpenPassageOverrideDetail, indentLevel + 1, ref first, prettyPrint);
            AppendIntProperty(builder, "OpenPassageOverrideEntryCount", report.OpenPassageOverrideEntryCount, indentLevel + 1, ref first, prettyPrint);
            AppendBoolProperty(builder, "OpenPassageOverrideNormalizedScalarArrays", report.OpenPassageOverrideNormalizedScalarArrays, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "OpenPassageOverridePath", report.OpenPassageOverridePath, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "OpenPassageOverrideFileSha256", report.OpenPassageOverrideFileSha256, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "OpenPassageOverrideFileLastWriteUtc", report.OpenPassageOverrideFileLastWriteUtc, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "OverallStatus", report.OverallStatus, indentLevel + 1, ref first, prettyPrint);
            AppendStringProperty(builder, "FailureReason", report.FailureReason, indentLevel + 1, ref first, prettyPrint);
            AppendStringArrayProperty(builder, "Limitations", report.Limitations, indentLevel + 1, ref first, prettyPrint);
            AppendSelectedObjectProperty(builder, "SelectedObject", report.SelectedObject, indentLevel + 1, ref first, prettyPrint);
            AppendSummaryProperty(builder, "Summary", report.Summary, indentLevel + 1, ref first, prettyPrint);
            AppendTrackerAlignmentProperty(builder, "TrackerAlignment", report.TrackerAlignment, indentLevel + 1, ref first, prettyPrint);
            AppendEntryArrayProperty(builder, "Entries", report.Entries, indentLevel + 1, ref first, prettyPrint);

            AppendEndObject(builder, indentLevel, prettyPrint);
        }

        private static void AppendSelectedObjectProperty(
            StringBuilder builder,
            string propertyName,
            SelectedObjectData value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartObject(builder, indentLevel, prettyPrint);
            bool nestedFirst = true;
            AppendStringProperty(builder, "InteractableId", value.InteractableId, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "InternalName", value.InternalName, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "DisplayLabel", value.DisplayLabel, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "RuntimeZone", value.RuntimeZone, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "NavigationZone", value.NavigationZone, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ResolutionStatus", value.ResolutionStatus, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ResolutionDetail", value.ResolutionDetail, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ApproachMode", value.ApproachMode, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ApproachReferenceSource", value.ApproachReferenceSource, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendVector3Property(builder, "ApproachTarget", value.ApproachTarget, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ApproachSnapStatus", value.ApproachSnapStatus, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ApproachSnapDetail", value.ApproachSnapDetail, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendEndObject(builder, indentLevel, prettyPrint);
        }

        private static void AppendSummaryProperty(
            StringBuilder builder,
            string propertyName,
            SummaryData value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartObject(builder, indentLevel, prettyPrint);
            bool nestedFirst = true;
            AppendIntProperty(builder, "StartZoneCount", value.StartZoneCount, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendIntProperty(builder, "PassedStartZoneCount", value.PassedStartZoneCount, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendIntProperty(builder, "FailedStartZoneCount", value.FailedStartZoneCount, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendIntProperty(builder, "UnverifiedStartZoneCount", value.UnverifiedStartZoneCount, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendIntProperty(builder, "PassedComponentCount", value.PassedComponentCount, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendIntProperty(builder, "FailedComponentCount", value.FailedComponentCount, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendIntProperty(builder, "UnverifiedComponentCount", value.UnverifiedComponentCount, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendEndObject(builder, indentLevel, prettyPrint);
        }

        private static void AppendTrackerAlignmentProperty(
            StringBuilder builder,
            string propertyName,
            TrackerAlignmentData value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartObject(builder, indentLevel, prettyPrint);
            bool nestedFirst = true;
            AppendBoolProperty(builder, "NavigationActive", value.NavigationActive, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendBoolProperty(builder, "TrackerActive", value.TrackerActive, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "CurrentStepKey", value.CurrentStepKey, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "TargetKind", value.TargetKind, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendVector3Property(builder, "MovementTarget", value.MovementTarget, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendVector3Property(builder, "TrackerTarget", value.TrackerTarget, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendFloatProperty(builder, "TargetDelta", value.TargetDelta, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "LocalNavigationContext", value.LocalNavigationContext, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendEndObject(builder, indentLevel, prettyPrint);
        }

        private static void AppendEntryArrayProperty(
            StringBuilder builder,
            string propertyName,
            EntryData[] values,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            if (values == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartArray(builder, indentLevel, prettyPrint);
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');
                if (prettyPrint)
                    builder.AppendLine();

                if (prettyPrint)
                    AppendIndent(builder, indentLevel + 1);

                AppendEntry(builder, values[i], indentLevel + 1, prettyPrint);
            }

            AppendEndArray(builder, indentLevel, prettyPrint);
        }

        private static void AppendEntry(StringBuilder builder, EntryData value, int indentLevel, bool prettyPrint)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartObject(builder, indentLevel, prettyPrint);
            bool nestedFirst = true;
            AppendStringProperty(builder, "StartZone", value.StartZone, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendIntProperty(builder, "StartComponentId", value.StartComponentId, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendVector3Property(builder, "RepresentativeStart", value.RepresentativeStart, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "Status", value.Status, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "FailureReason", value.FailureReason, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "StartLocalLegStatus", value.StartLocalLegStatus, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "StartLocalLegDetail", value.StartLocalLegDetail, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "DestinationLocalLegStatus", value.DestinationLocalLegStatus, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "DestinationLocalLegDetail", value.DestinationLocalLegDetail, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ResolvedApproachMode", value.ResolvedApproachMode, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendVector3Property(builder, "ResolvedApproachTarget", value.ResolvedApproachTarget, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ResolvedApproachDetail", value.ResolvedApproachDetail, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendPathStepArrayProperty(builder, "PathSteps", value.PathSteps, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendEndObject(builder, indentLevel, prettyPrint);
        }

        private static void AppendPathStepArrayProperty(
            StringBuilder builder,
            string propertyName,
            PathStepData[] values,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            if (values == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartArray(builder, indentLevel, prettyPrint);
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');
                if (prettyPrint)
                    builder.AppendLine();

                if (prettyPrint)
                    AppendIndent(builder, indentLevel + 1);

                AppendPathStep(builder, values[i], indentLevel + 1, prettyPrint);
            }

            AppendEndArray(builder, indentLevel, prettyPrint);
        }

        private static void AppendPathStep(StringBuilder builder, PathStepData value, int indentLevel, bool prettyPrint)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartObject(builder, indentLevel, prettyPrint);
            bool nestedFirst = true;
            AppendStringProperty(builder, "Key", value.Key, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "Id", value.Id, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "FromZone", value.FromZone, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ToZone", value.ToZone, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "StepKind", value.StepKind, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ConnectorName", value.ConnectorName, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ValidationStatus", value.ValidationStatus, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendStringProperty(builder, "ValidationDetail", value.ValidationDetail, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendBoolProperty(builder, "RequiresInteraction", value.RequiresInteraction, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendEndObject(builder, indentLevel, prettyPrint);
        }

        private static void AppendStringArrayProperty(
            StringBuilder builder,
            string propertyName,
            string[] values,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            if (values == null)
            {
                builder.Append("null");
                return;
            }

            AppendStartArray(builder, indentLevel, prettyPrint);
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');
                if (prettyPrint)
                    builder.AppendLine();

                if (prettyPrint)
                    AppendIndent(builder, indentLevel + 1);

                AppendString(builder, values[i]);
            }

            AppendEndArray(builder, indentLevel, prettyPrint);
        }

        private static void AppendStringProperty(
            StringBuilder builder,
            string propertyName,
            string value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            AppendString(builder, value);
        }

        private static void AppendIntProperty(
            StringBuilder builder,
            string propertyName,
            int value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendBoolProperty(
            StringBuilder builder,
            string propertyName,
            bool value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            builder.Append(value ? "true" : "false");
        }

        private static void AppendFloatProperty(
            StringBuilder builder,
            string propertyName,
            float value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            builder.Append(value.ToString("0.######", CultureInfo.InvariantCulture));
        }

        private static void AppendVector3Property(
            StringBuilder builder,
            string propertyName,
            Vector3 value,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            AppendPropertyName(builder, propertyName, indentLevel, ref first, prettyPrint);
            AppendStartObject(builder, indentLevel, prettyPrint);
            bool nestedFirst = true;
            AppendFloatProperty(builder, "x", value.x, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendFloatProperty(builder, "y", value.y, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendFloatProperty(builder, "z", value.z, indentLevel + 1, ref nestedFirst, prettyPrint);
            AppendEndObject(builder, indentLevel, prettyPrint);
        }

        private static void AppendPropertyName(
            StringBuilder builder,
            string propertyName,
            int indentLevel,
            ref bool first,
            bool prettyPrint)
        {
            if (!first)
                builder.Append(',');

            if (prettyPrint)
                builder.AppendLine();

            if (prettyPrint)
                AppendIndent(builder, indentLevel);

            AppendString(builder, propertyName);
            builder.Append(prettyPrint ? ": " : ":");
            first = false;
        }

        private static void AppendString(StringBuilder builder, string value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }

            builder.Append('"');
        }

        private static void AppendStartObject(StringBuilder builder, int indentLevel, bool prettyPrint)
        {
            builder.Append('{');
        }

        private static void AppendEndObject(StringBuilder builder, int indentLevel, bool prettyPrint)
        {
            if (prettyPrint)
            {
                builder.AppendLine();
                AppendIndent(builder, indentLevel);
            }

            builder.Append('}');
        }

        private static void AppendStartArray(StringBuilder builder, int indentLevel, bool prettyPrint)
        {
            builder.Append('[');
        }

        private static void AppendEndArray(StringBuilder builder, int indentLevel, bool prettyPrint)
        {
            if (prettyPrint)
            {
                builder.AppendLine();
                AppendIndent(builder, indentLevel);
            }

            builder.Append(']');
        }

        private static void AppendIndent(StringBuilder builder, int indentLevel)
        {
            for (int i = 0; i < indentLevel; i++)
                builder.Append("  ");
        }
    }
}
