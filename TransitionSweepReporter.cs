using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DateEverythingAccess
{
    internal static class TransitionSweepReporter
    {
        [Serializable]
        internal sealed class MutableEntry
        {
            public int Index;
            public int StepIndex;
            public string Key;
            public string FromZone;
            public string ToZone;
            public string StepKind;
            public string TransitionKind;
            public string ConnectorName;
            public bool RequiresInteraction;
            public string Status;
            public string StatusDetail;
            public string FailureReason;
            public float DurationSeconds;
            public string SpawnSource;
            public Vector3 SpawnPosition;
            public Vector3 FromWaypoint;
            public Vector3 ToWaypoint;
            public Vector3 FromCrossingAnchor;
            public Vector3 ToCrossingAnchor;
        }

        internal static string GetDefaultOutputPath()
        {
            return GetOutputPath("transition_sweep.live.json");
        }

        internal static string GetDefaultDoorOutputPath()
        {
            return GetOutputPath("door_transition_sweep.live.json");
        }

        internal static MutableEntry CreateEntry(int index, NavigationGraph.PathStep step, string key)
        {
            return new MutableEntry
            {
                Index = index,
                StepIndex = index,
                Key = key,
                FromZone = step != null ? step.FromZone : null,
                ToZone = step != null ? step.ToZone : null,
                StepKind = step != null ? step.Kind.ToString() : null,
                TransitionKind = step != null ? step.Kind.ToString() : null,
                ConnectorName = step != null ? step.ConnectorName : null,
                RequiresInteraction = step != null && step.RequiresInteraction,
                Status = "pending",
                StatusDetail = "pending",
                FailureReason = null,
                DurationSeconds = 0f,
                SpawnSource = null,
                SpawnPosition = Vector3.zero,
                FromWaypoint = step != null ? step.FromWaypoint : Vector3.zero,
                ToWaypoint = step != null ? step.ToWaypoint : Vector3.zero,
                FromCrossingAnchor = step != null ? step.FromCrossingAnchor : Vector3.zero,
                ToCrossingAnchor = step != null ? step.ToCrossingAnchor : Vector3.zero
            };
        }

        internal static void WriteReport(string outputPath, bool isComplete, List<MutableEntry> entries)
        {
            WriteReport(outputPath, "OpenPassage", isComplete, entries);
        }

        internal static void WriteReport(string outputPath, string sweepKind, bool isComplete, List<MutableEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = GetDefaultOutputPath();

            int passedCount = 0;
            int failedCount = 0;
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
                    else
                    {
                        pendingCount++;
                    }
                }
            }

            Directory.CreateDirectory(Paths.PluginPath);
            File.WriteAllText(
                outputPath,
                BuildJson(
                    DateTime.UtcNow.ToString("o"),
                    SceneManager.GetActiveScene().name,
                    sweepKind,
                    isComplete,
                    entries,
                    passedCount,
                    failedCount,
                    pendingCount));
        }

        internal static HashSet<string> LoadPassedKeys(string reportPath)
        {
            string resolvedPath = string.IsNullOrWhiteSpace(reportPath)
                ? GetDefaultOutputPath()
                : reportPath;

            var passedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(resolvedPath))
                return passedKeys;

            try
            {
                string json = File.ReadAllText(resolvedPath);
                if (string.IsNullOrWhiteSpace(json))
                    return passedKeys;

                string[] lines = json.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                string currentKey = null;
                string currentStatus = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (TryExtractJsonStringProperty(line, "\"Key\"", out string extractedKey))
                    {
                        currentKey = extractedKey;
                        continue;
                    }

                    if (TryExtractJsonStringProperty(line, "\"Status\"", out string extractedStatus))
                    {
                        currentStatus = extractedStatus;
                    }

                    if (currentKey == null || currentStatus == null)
                    {
                        continue;
                    }

                    if (string.Equals(currentStatus, "passed", StringComparison.OrdinalIgnoreCase))
                        passedKeys.Add(currentKey);

                    currentKey = null;
                    currentStatus = null;
                }
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("Failed to load prior transition sweep report: " + ex.Message);
            }

            return passedKeys;
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
            string sweepKind,
            bool isComplete,
            List<MutableEntry> entries,
            int passedCount,
            int failedCount,
            int pendingCount)
        {
            var builder = new StringBuilder(8192);
            builder.AppendLine("{");
            AppendProperty(builder, "GeneratedAtUtc", generatedAtUtc, 1, trailingComma: true);
            AppendProperty(builder, "ActiveScene", activeScene, 1, trailingComma: true);
            AppendProperty(builder, "SweepKind", sweepKind, 1, trailingComma: true);
            AppendProperty(builder, "IsComplete", isComplete, 1, trailingComma: true);
            AppendProperty(builder, "TotalCount", entries != null ? entries.Count : 0, 1, trailingComma: true);
            AppendProperty(builder, "PassedCount", passedCount, 1, trailingComma: true);
            AppendProperty(builder, "FailedCount", failedCount, 1, trailingComma: true);
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
            string innerIndent = new string(' ', (indentLevel + 1) * 4);
            builder.Append(indent);
            builder.AppendLine("{");
            AppendProperty(builder, "Index", entry != null ? entry.Index : -1, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "StepIndex", entry != null ? entry.StepIndex : -1, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "Key", entry != null ? entry.Key : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "FromZone", entry != null ? entry.FromZone : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "ToZone", entry != null ? entry.ToZone : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "StepKind", entry != null ? entry.StepKind : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "TransitionKind", entry != null ? entry.TransitionKind : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "ConnectorName", entry != null ? entry.ConnectorName : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "RequiresInteraction", entry != null && entry.RequiresInteraction, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "Status", entry != null ? entry.Status : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "StatusDetail", entry != null ? entry.StatusDetail : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "FailureReason", entry != null ? entry.FailureReason : null, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "DurationSeconds", entry != null ? entry.DurationSeconds : 0f, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "SpawnSource", entry != null ? entry.SpawnSource : null, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "SpawnPosition", entry != null ? entry.SpawnPosition : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "FromWaypoint", entry != null ? entry.FromWaypoint : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "ToWaypoint", entry != null ? entry.ToWaypoint : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "FromCrossingAnchor", entry != null ? entry.FromCrossingAnchor : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "ToCrossingAnchor", entry != null ? entry.ToCrossingAnchor : Vector3.zero, indentLevel + 1, trailingComma: false);
            builder.Append(indent);
            builder.Append("}");
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
            builder.Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
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
