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
            public Vector3 SourceApproachPoint;
            public Vector3 SourceClearPoint;
            public Vector3 DestinationClearPoint;
            public Vector3 DestinationApproachPoint;
            public float ValidationTimeoutSeconds;
            public int StaticSuspicionScore;
        }

        [Serializable]
        internal sealed class EntryStatus
        {
            public string Key;
            public string StepKind;
            public string Status;
            public string StatusDetail;
            public string FailureReason;
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
                ToCrossingAnchor = step != null ? step.ToCrossingAnchor : Vector3.zero,
                SourceApproachPoint = step != null ? step.SourceApproachPoint : Vector3.zero,
                SourceClearPoint = step != null ? step.SourceClearPoint : Vector3.zero,
                DestinationClearPoint = step != null ? step.DestinationClearPoint : Vector3.zero,
                DestinationApproachPoint = step != null ? step.DestinationApproachPoint : Vector3.zero,
                ValidationTimeoutSeconds = step != null ? step.ValidationTimeoutSeconds : 0f,
                StaticSuspicionScore = step != null ? step.StaticSuspicionScore : 0
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

            PersistPassedKeys(outputPath, entries);
        }

        internal static HashSet<string> LoadPassedKeys(string reportPath)
        {
            string resolvedPath = string.IsNullOrWhiteSpace(reportPath)
                ? GetDefaultOutputPath()
                : reportPath;

            var passedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LoadPassedKeysFromCachePath(GetPassedKeyCachePath(resolvedPath), passedKeys);
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
                    else if (TryExtractJsonStringProperty(line, "\"StepKind\"", out string stepKind))
                    {
                        currentEntry.StepKind = stepKind;
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
                Main.Log.LogWarning("Failed to load transition sweep entry statuses: " + ex.Message);
            }

            return statuses;
        }

        private static void PersistPassedKeys(string reportPath, List<MutableEntry> entries)
        {
            var passedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cachePath = GetPassedKeyCachePath(reportPath);
            LoadPassedKeysFromCachePath(cachePath, passedKeys);

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    MutableEntry entry = entries[i];
                    if (entry == null ||
                        !string.Equals(entry.Status, "passed", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(entry.Key))
                    {
                        continue;
                    }

                    passedKeys.Add(entry.Key);
                }
            }

            if (passedKeys.Count == 0)
                return;

            try
            {
                string directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var orderedKeys = new List<string>(passedKeys);
                orderedKeys.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(cachePath, orderedKeys.ToArray());
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("Failed to persist passed transition sweep keys: " + ex.Message);
            }
        }

        private static void LoadPassedKeysFromCachePath(string cachePath, HashSet<string> passedKeys)
        {
            if (passedKeys == null)
                return;
            if (!File.Exists(cachePath))
                return;

            try
            {
                string[] lines = File.ReadAllLines(cachePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string key = lines[i];
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    passedKeys.Add(key.Trim());
                }
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("Failed to load cached passed transition sweep keys: " + ex.Message);
            }
        }

        private static string GetPassedKeyCachePath(string reportPath)
        {
            string resolvedPath = string.IsNullOrWhiteSpace(reportPath)
                ? GetDefaultOutputPath()
                : reportPath;

            string directory = Path.GetDirectoryName(resolvedPath);
            string fileName = Path.GetFileNameWithoutExtension(resolvedPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "transition_sweep";

            return Path.Combine(directory ?? Paths.PluginPath, fileName + ".passed.txt");
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
            AppendVector(builder, "ToCrossingAnchor", entry != null ? entry.ToCrossingAnchor : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "SourceApproachPoint", entry != null ? entry.SourceApproachPoint : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "SourceClearPoint", entry != null ? entry.SourceClearPoint : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "DestinationClearPoint", entry != null ? entry.DestinationClearPoint : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendVector(builder, "DestinationApproachPoint", entry != null ? entry.DestinationApproachPoint : Vector3.zero, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "ValidationTimeoutSeconds", entry != null ? entry.ValidationTimeoutSeconds : 0f, indentLevel + 1, trailingComma: true);
            AppendProperty(builder, "StaticSuspicionScore", entry != null ? entry.StaticSuspicionScore : 0, indentLevel + 1, trailingComma: false);
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
