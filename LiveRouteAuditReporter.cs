using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DateEverythingAccess
{
    internal static class LiveRouteAuditReporter
    {
        [Serializable]
        internal sealed class MutableEntry
        {
            public int Index;
            public string Key;
            public string RouteSignature;
            public string StartZone;
            public string TargetZone;
            public string[] AliasPairs;
            public string[] StepIds;
            public int StepCount;
            public string Status;
            public string StatusDetail;
            public string FailureReason;
            public float DurationSeconds;
            public string SpawnSource;
            public Vector3 SpawnPosition;
            public float ExpectedTimeoutSeconds;
            public string CurrentZoneAtResult;
            public Vector3 PlayerPositionAtResult;
            public string LastTargetKind;
            public Vector3 LastTargetPosition;
            public string LastLocalNavigationContext;
        }

        [Serializable]
        internal sealed class EntryStatus
        {
            public string Key;
            public string RouteSignature;
            public string Status;
            public string StatusDetail;
            public string FailureReason;
        }

        internal static string GetDefaultOutputPath()
        {
            return GetOutputPath("live_route_audit.live.json");
        }

        internal static Dictionary<string, EntryStatus> LoadEntryStatuses(string reportPath)
        {
            string resolvedPath = string.IsNullOrWhiteSpace(reportPath)
                ? GetDefaultOutputPath()
                : reportPath;

            var statuses = new Dictionary<string, EntryStatus>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(resolvedPath))
                return statuses;

            try
            {
                string[] lines = File.ReadAllLines(resolvedPath);
                if (!ReportMatchesCurrentBuild(lines))
                    return statuses;

                bool inEntries = false;
                int entryDepth = 0;
                EntryStatus currentEntry = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    string trimmedLine = line.Trim();
                    if (!inEntries)
                    {
                        if (trimmedLine.StartsWith("\"Entries\"", StringComparison.Ordinal))
                            inEntries = true;

                        continue;
                    }

                    if (entryDepth == 0)
                    {
                        if (trimmedLine.StartsWith("{", StringComparison.Ordinal))
                        {
                            currentEntry = new EntryStatus();
                            entryDepth += CountCharacter(line, '{') - CountCharacter(line, '}');
                        }

                        continue;
                    }

                    if (TryExtractJsonStringProperty(line, "\"Key\"", out string key))
                    {
                        currentEntry.Key = key;
                    }
                    else if (TryExtractJsonNullableStringProperty(line, "\"RouteSignature\"", out string routeSignature))
                    {
                        currentEntry.RouteSignature = routeSignature;
                    }
                    else if (TryExtractJsonStringProperty(line, "\"Status\"", out string status))
                    {
                        currentEntry.Status = status;
                    }
                    else if (TryExtractJsonNullableStringProperty(line, "\"StatusDetail\"", out string statusDetail))
                    {
                        currentEntry.StatusDetail = statusDetail;
                    }
                    else if (TryExtractJsonNullableStringProperty(line, "\"FailureReason\"", out string failureReason))
                    {
                        currentEntry.FailureReason = failureReason;
                    }

                    entryDepth += CountCharacter(line, '{') - CountCharacter(line, '}');
                    if (entryDepth > 0)
                        continue;

                    if (currentEntry != null && !string.IsNullOrWhiteSpace(currentEntry.Key))
                        statuses[currentEntry.Key] = currentEntry;

                    currentEntry = null;
                    entryDepth = 0;
                }
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("Failed to load live route audit entry statuses: " + ex.Message);
            }

            return statuses;
        }

        internal static void WriteReport(
            string outputPath,
            bool isComplete,
            int orderedPairCount,
            int uniqueRouteCount,
            List<MutableEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = GetDefaultOutputPath();

            int passedCount = 0;
            int failedCount = 0;
            int skippedCount = 0;
            int pendingCount = 0;
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    MutableEntry entry = entries[i];
                    string status = entry != null ? entry.Status : null;
                    if (string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase))
                    {
                        passedCount++;
                    }
                    else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        failedCount++;
                    }
                    else if (string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedCount++;
                    }
                    else
                    {
                        pendingCount++;
                    }
                }
            }

            AccessibilityWatcher.GetOpenPassageTransitionOverrideDiagnostics(
                out string overrideStatus,
                out string overrideDetail,
                out int overrideEntryCount,
                out bool overrideNormalizedScalarArrays,
                out string overridePath,
                out string overrideFileSha256,
                out string overrideFileLastWriteUtc);

            Directory.CreateDirectory(Paths.PluginPath);
            File.WriteAllText(
                outputPath,
                BuildJson(
                    DateTime.UtcNow.ToString("o"),
                    SceneManager.GetActiveScene().name,
                    isComplete,
                    orderedPairCount,
                    uniqueRouteCount,
                    entries,
                    passedCount,
                    failedCount,
                    skippedCount,
                    pendingCount,
                    Main.GetPluginVersion(),
                    Main.GetRuntimeBuildStamp(),
                    Main.RuntimeAssemblyPath,
                    Main.RuntimeAssemblySha256,
                    overrideStatus,
                    overrideDetail,
                    overrideEntryCount,
                    overrideNormalizedScalarArrays,
                    overridePath,
                    overrideFileSha256,
                    overrideFileLastWriteUtc));
        }

        private static bool ReportMatchesCurrentBuild(string[] lines)
        {
            if (lines == null || lines.Length == 0)
                return false;

            string currentBuildStamp = Main.GetRuntimeBuildStamp();
            if (string.IsNullOrWhiteSpace(currentBuildStamp))
                return false;

            if (!TryReadReportRuntimeBuildStamp(lines, out string reportBuildStamp))
                return false;

            return string.Equals(reportBuildStamp, currentBuildStamp, StringComparison.Ordinal);
        }

        private static bool TryReadReportRuntimeBuildStamp(string[] lines, out string runtimeBuildStamp)
        {
            runtimeBuildStamp = null;
            if (lines == null || lines.Length == 0)
                return false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (TryExtractJsonStringProperty(line, "\"RuntimeBuildStamp\"", out runtimeBuildStamp))
                    return !string.IsNullOrWhiteSpace(runtimeBuildStamp);

                if (line.IndexOf("\"Entries\"", StringComparison.Ordinal) >= 0)
                    break;
            }

            runtimeBuildStamp = null;
            return false;
        }

        private static bool TryExtractJsonStringProperty(string line, string propertyName, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(propertyName))
                return false;

            int propertyIndex = line.IndexOf(propertyName, StringComparison.Ordinal);
            if (propertyIndex < 0)
                return false;

            int colonIndex = line.IndexOf(':', propertyIndex + propertyName.Length);
            if (colonIndex < 0)
                return false;

            int firstQuoteIndex = line.IndexOf('"', colonIndex + 1);
            if (firstQuoteIndex < 0)
                return false;

            int lastQuoteIndex = line.LastIndexOf('"');
            if (lastQuoteIndex <= firstQuoteIndex)
                return false;

            value = UnescapeJson(line.Substring(firstQuoteIndex + 1, lastQuoteIndex - firstQuoteIndex - 1));
            return true;
        }

        private static bool TryExtractJsonNullableStringProperty(string line, string propertyName, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(propertyName))
                return false;

            int propertyIndex = line.IndexOf(propertyName, StringComparison.Ordinal);
            if (propertyIndex < 0)
                return false;

            int colonIndex = line.IndexOf(':', propertyIndex + propertyName.Length);
            if (colonIndex < 0)
                return false;

            string rawValue = line.Substring(colonIndex + 1).Trim().TrimEnd(',');
            if (string.Equals(rawValue, "null", StringComparison.Ordinal))
                return true;

            return TryExtractJsonStringProperty(line, propertyName, out value);
        }

        private static int CountCharacter(string value, char character)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == character)
                    count++;
            }

            return count;
        }

        private static string UnescapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value
                .Replace("\\t", "\t")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        private static string BuildJson(
            string generatedAtUtc,
            string activeScene,
            bool isComplete,
            int orderedPairCount,
            int uniqueRouteCount,
            List<MutableEntry> entries,
            int passedCount,
            int failedCount,
            int skippedCount,
            int pendingCount,
            string pluginVersion,
            string runtimeBuildStamp,
            string runtimeAssemblyPath,
            string runtimeAssemblySha256,
            string openPassageOverrideStatus,
            string openPassageOverrideDetail,
            int openPassageOverrideEntryCount,
            bool openPassageOverrideNormalizedScalarArrays,
            string openPassageOverridePath,
            string openPassageOverrideFileSha256,
            string openPassageOverrideFileLastWriteUtc)
        {
            var builder = new StringBuilder(8192);
            builder.AppendLine("{");
            AppendProperty(builder, "GeneratedAtUtc", generatedAtUtc, 1, trailingComma: true);
            AppendProperty(builder, "ActiveScene", activeScene, 1, trailingComma: true);
            AppendProperty(builder, "ReportKind", "LiveRouteAudit", 1, trailingComma: true);
            AppendProperty(builder, "PluginVersion", pluginVersion, 1, trailingComma: true);
            AppendProperty(builder, "RuntimeBuildStamp", runtimeBuildStamp, 1, trailingComma: true);
            AppendProperty(builder, "RuntimeAssemblyPath", runtimeAssemblyPath, 1, trailingComma: true);
            AppendProperty(builder, "RuntimeAssemblySha256", runtimeAssemblySha256, 1, trailingComma: true);
            AppendProperty(builder, "OpenPassageOverrideStatus", openPassageOverrideStatus, 1, trailingComma: true);
            AppendProperty(builder, "OpenPassageOverrideDetail", openPassageOverrideDetail, 1, trailingComma: true);
            AppendProperty(builder, "OpenPassageOverrideEntryCount", openPassageOverrideEntryCount, 1, trailingComma: true);
            AppendProperty(builder, "OpenPassageOverrideNormalizedScalarArrays", openPassageOverrideNormalizedScalarArrays, 1, trailingComma: true);
            AppendProperty(builder, "OpenPassageOverridePath", openPassageOverridePath, 1, trailingComma: true);
            AppendProperty(builder, "OpenPassageOverrideFileSha256", openPassageOverrideFileSha256, 1, trailingComma: true);
            AppendProperty(builder, "OpenPassageOverrideFileLastWriteUtc", openPassageOverrideFileLastWriteUtc, 1, trailingComma: true);
            AppendProperty(builder, "IsComplete", isComplete, 1, trailingComma: true);
            AppendProperty(builder, "OrderedPairCount", orderedPairCount, 1, trailingComma: true);
            AppendProperty(builder, "UniqueRouteCount", uniqueRouteCount, 1, trailingComma: true);
            AppendProperty(builder, "TotalCount", entries != null ? entries.Count : 0, 1, trailingComma: true);
            AppendProperty(builder, "PassedCount", passedCount, 1, trailingComma: true);
            AppendProperty(builder, "FailedCount", failedCount, 1, trailingComma: true);
            AppendProperty(builder, "SkippedCount", skippedCount, 1, trailingComma: true);
            AppendProperty(builder, "PendingCount", pendingCount, 1, trailingComma: true);

            builder.Append(new string(' ', 4));
            builder.AppendLine("\"Entries\": [");
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    AppendEntry(builder, entries[i], 2, i < entries.Count - 1);
                }
            }

            builder.Append(new string(' ', 4));
            builder.AppendLine("]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void AppendEntry(StringBuilder builder, MutableEntry entry, int indentLevel, bool trailingComma)
        {
            string indent = new string(' ', indentLevel * 4);
            builder.Append(indent);
            builder.AppendLine("{");
            AppendProperty(builder, "Index", entry != null ? entry.Index : -1, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "Key", entry != null ? entry.Key : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "RouteSignature", entry != null ? entry.RouteSignature : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "StartZone", entry != null ? entry.StartZone : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "TargetZone", entry != null ? entry.TargetZone : null, indentLevel + 1, trailingComma: true);
            AppendStringArray(builder, "AliasPairs", entry != null ? entry.AliasPairs : null, indentLevel + 1, trailingComma: true);
            AppendStringArray(builder, "StepIds", entry != null ? entry.StepIds : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "StepCount", entry != null ? entry.StepCount : 0, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "Status", entry != null ? entry.Status : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "StatusDetail", entry != null ? entry.StatusDetail : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "FailureReason", entry != null ? entry.FailureReason : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "DurationSeconds", entry != null ? entry.DurationSeconds : 0f, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "SpawnSource", entry != null ? entry.SpawnSource : null, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "SpawnPosition", entry != null ? entry.SpawnPosition : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "ExpectedTimeoutSeconds", entry != null ? entry.ExpectedTimeoutSeconds : 0f, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "CurrentZoneAtResult", entry != null ? entry.CurrentZoneAtResult : null, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "PlayerPositionAtResult", entry != null ? entry.PlayerPositionAtResult : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "LastTargetKind", entry != null ? entry.LastTargetKind : null, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "LastTargetPosition", entry != null ? entry.LastTargetPosition : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "LastLocalNavigationContext", entry != null ? entry.LastLocalNavigationContext : null, indentLevel + 1, trailingComma: false);
            builder.Append(indent);
            builder.Append("}");
            if (trailingComma)
                builder.Append(",");
            builder.AppendLine();
        }

        private static void AppendStringArray(StringBuilder builder, string name, string[] values, int indentLevel, bool trailingComma)
        {
            string indent = new string(' ', indentLevel * 4);
            string itemIndent = new string(' ', (indentLevel + 1) * 4);
            builder.Append(indent);
            builder.Append('"');
            builder.Append(name);
            builder.AppendLine("\": [");
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    builder.Append(itemIndent);
                    if (values[i] == null)
                    {
                        builder.Append("null");
                    }
                    else
                    {
                        builder.Append('"');
                        builder.Append(EscapeJson(values[i]));
                        builder.Append('"');
                    }

                    if (i < values.Length - 1)
                        builder.Append(",");
                    builder.AppendLine();
                }
            }

            builder.Append(indent);
            builder.Append("]");
            if (trailingComma)
                builder.Append(",");
            builder.AppendLine();
        }

        private static void AppendVector(StringBuilder builder, string name, Vector3 value, int indentLevel, bool trailingComma)
        {
            string indent = new string(' ', indentLevel * 4);
            builder.Append(indent);
            builder.Append('"');
            builder.Append(name);
            builder.AppendLine("\": {");
            AppendProperty(builder, "x", value.x, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "y", value.y, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "z", value.z, indentLevel + 1, trailingComma: false);
            builder.Append(indent);
            builder.Append("}");
            if (trailingComma)
                builder.Append(",");
            builder.AppendLine();
        }

        private static void AppendProperty(StringBuilder builder, string name, string value, int indentLevel, bool trailingComma)
        {
            builder.Append(new string(' ', indentLevel * 4));
            builder.Append('"');
            builder.Append(name);
            builder.Append("\": ");
            if (value == null)
            {
                builder.Append("null");
            }
            else
            {
                builder.Append('"');
                builder.Append(EscapeJson(value));
                builder.Append('"');
            }

            if (trailingComma)
                builder.Append(",");
            builder.AppendLine();
        }

        private static void AppendProperty(StringBuilder builder, string name, bool value, int indentLevel, bool trailingComma)
        {
            builder.Append(new string(' ', indentLevel * 4));
            builder.Append('"');
            builder.Append(name);
            builder.Append("\": ");
            builder.Append(value ? "true" : "false");
            if (trailingComma)
                builder.Append(",");
            builder.AppendLine();
        }

        private static void AppendProperty(StringBuilder builder, string name, int value, int indentLevel, bool trailingComma)
        {
            builder.Append(new string(' ', indentLevel * 4));
            builder.Append('"');
            builder.Append(name);
            builder.Append("\": ");
            builder.Append(value);
            if (trailingComma)
                builder.Append(",");
            builder.AppendLine();
        }

        private static void AppendProperty(StringBuilder builder, string name, float value, int indentLevel, bool trailingComma)
        {
            builder.Append(new string(' ', indentLevel * 4));
            builder.Append('"');
            builder.Append(name);
            builder.Append("\": ");
            builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            if (trailingComma)
                builder.Append(",");
            builder.AppendLine();
        }

        private static string EscapeJson(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static string GetOutputPath(string fileName)
        {
            return Path.Combine(Paths.PluginPath, fileName);
        }
    }
}
