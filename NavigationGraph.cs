using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Loads and queries the room navigation graph from the plugin directory.
    /// </summary>
    public static class NavigationGraph
    {
        /// <summary>
        /// Describes the type of transition represented by a navigation step.
        /// </summary>
        public enum StepKind
        {
            Unknown,
            OpenPassage,
            Door,
            Stairs,
            Teleporter
        }

        /// <summary>
        /// Represents a directed navigation step between two zones.
        /// </summary>
        public sealed class PathStep
        {
            internal PathStep(
                string fromZone,
                string toZone,
                Vector3 fromWaypoint,
                Vector3 toWaypoint,
                Vector3 fromCrossingAnchor,
                Vector3 toCrossingAnchor,
                float cost,
                StepKind kind,
                string connectorName,
                bool requiresInteraction,
                float transitionWaitSeconds)
            {
                FromZone = fromZone;
                ToZone = toZone;
                FromWaypoint = fromWaypoint;
                ToWaypoint = toWaypoint;
                FromCrossingAnchor = fromCrossingAnchor;
                ToCrossingAnchor = toCrossingAnchor;
                Cost = cost;
                Kind = kind;
                ConnectorName = connectorName;
                RequiresInteraction = requiresInteraction;
                TransitionWaitSeconds = transitionWaitSeconds;
            }

            /// <summary>
            /// Gets the source zone for the step.
            /// </summary>
            public string FromZone { get; }

            /// <summary>
            /// Gets the destination zone for the step.
            /// </summary>
            public string ToZone { get; }

            /// <summary>
            /// Gets the exit waypoint within the source zone.
            /// </summary>
            public Vector3 FromWaypoint { get; }

            /// <summary>
            /// Gets the entry waypoint within the destination zone.
            /// </summary>
            public Vector3 ToWaypoint { get; }

            /// <summary>
            /// Gets the authored source-side crossing anchor for open passages when available.
            /// </summary>
            public Vector3 FromCrossingAnchor { get; }

            /// <summary>
            /// Gets the authored destination-side crossing anchor for open passages when available.
            /// </summary>
            public Vector3 ToCrossingAnchor { get; }

            /// <summary>
            /// Gets the traversal cost for this step.
            /// </summary>
            public float Cost { get; }

            /// <summary>
            /// Gets the transition type for this step.
            /// </summary>
            public StepKind Kind { get; }

            /// <summary>
            /// Gets the authored connector name associated with this step when available.
            /// </summary>
            public string ConnectorName { get; }

            /// <summary>
            /// Gets whether the step requires an explicit interaction to complete.
            /// </summary>
            public bool RequiresInteraction { get; }

            /// <summary>
            /// Gets the suggested wait time after interaction-driven transitions.
            /// </summary>
            public float TransitionWaitSeconds { get; }
        }

        private sealed class Link
        {
            public string FromZone;
            public string ToZone;
            public Vector3 FromWaypoint;
            public Vector3 ToWaypoint;
            public Vector3 FromCrossingAnchor;
            public Vector3 ToCrossingAnchor;
            public float Cost;
            public StepKind Kind;
            public string ConnectorName;
            public bool RequiresInteraction;
            public float TransitionWaitSeconds;
        }

        private sealed class QueueItem : IComparable<QueueItem>
        {
            public string Zone;
            public float Priority;
            public long Sequence;

            public int CompareTo(QueueItem other)
            {
                int priorityComparison = Priority.CompareTo(other.Priority);
                if (priorityComparison != 0)
                    return priorityComparison;

                return Sequence.CompareTo(other.Sequence);
            }
        }

        private static bool _initialized;
        private static readonly List<Link> _links = new List<Link>();
        private static readonly Dictionary<string, List<Link>> _connections = new Dictionary<string, List<Link>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _knownZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static long _queueSequence;

        /// <summary>
        /// Loads the navigation graph from the BepInEx plugins folder when present.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            _links.Clear();
            _connections.Clear();
            _knownZones.Clear();

            try
            {
                string jsonPath = Path.Combine(Paths.PluginPath, "navigation_graph.json");
                if (!File.Exists(jsonPath))
                {
                    Main.Log.LogWarning("Navigation graph JSON not found at: " + jsonPath);
                    return;
                }

                string json = File.ReadAllText(jsonPath);
                List<Link> parsedLinks = ParseNavigationJson(json);
                if (parsedLinks == null || parsedLinks.Count == 0)
                {
                    Main.Log.LogWarning("Navigation graph JSON did not contain any usable links.");
                    return;
                }

                for (int i = 0; i < parsedLinks.Count; i++)
                {
                    Link link = parsedLinks[i];
                    AddLink(link.FromZone, link.ToZone, link.FromWaypoint, link.ToWaypoint, link.FromCrossingAnchor, link.ToCrossingAnchor, link.Cost, link.Kind, link.ConnectorName, link.RequiresInteraction, link.TransitionWaitSeconds);
                    AddLink(link.ToZone, link.FromZone, link.ToWaypoint, link.FromWaypoint, link.ToCrossingAnchor, link.FromCrossingAnchor, link.Cost, link.Kind, link.ConnectorName, link.RequiresInteraction, link.TransitionWaitSeconds);
                }

                Main.Log.LogInfo("Navigation graph loaded. Zones: " + _knownZones.Count + ", Links: " + _links.Count);
            }
            catch (Exception ex)
            {
                Main.Log.LogError("Failed to initialize navigation graph: " + ex);
            }
        }

        /// <summary>
        /// Returns whether the graph contains the supplied zone name.
        /// </summary>
        public static bool ContainsZone(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return false;

            Initialize();
            return _knownZones.Contains(zoneName);
        }

        /// <summary>
        /// Returns every directed step currently loaded from the live navigation graph.
        /// </summary>
        public static List<PathStep> GetAllPathSteps()
        {
            Initialize();

            var steps = new List<PathStep>(_links.Count);
            for (int i = 0; i < _links.Count; i++)
            {
                Link link = _links[i];
                steps.Add(new PathStep(
                    link.FromZone,
                    link.ToZone,
                    link.FromWaypoint,
                    link.ToWaypoint,
                    link.FromCrossingAnchor,
                    link.ToCrossingAnchor,
                    link.Cost,
                    link.Kind,
                    link.ConnectorName,
                    link.RequiresInteraction,
                    link.TransitionWaitSeconds));
            }

            return steps;
        }

        /// <summary>
        /// Finds the shortest room path between two zones.
        /// </summary>
        public static List<string> FindPath(string startZone, string endZone)
        {
            if (string.Equals(startZone, endZone, StringComparison.OrdinalIgnoreCase))
                return new List<string> { startZone };

            List<PathStep> steps = FindPathSteps(startZone, endZone);
            if (steps == null || steps.Count == 0)
                return null;

            var zones = new List<string> { steps[0].FromZone };
            for (int i = 0; i < steps.Count; i++)
                zones.Add(steps[i].ToZone);

            return zones;
        }

        /// <summary>
        /// Finds the shortest waypoint-aware path between two zones.
        /// </summary>
        public static List<PathStep> FindPathSteps(string startZone, string endZone)
        {
            if (string.IsNullOrWhiteSpace(startZone) || string.IsNullOrWhiteSpace(endZone))
                return null;

            Initialize();

            if (!_knownZones.Contains(startZone) || !_knownZones.Contains(endZone))
                return null;

            if (string.Equals(startZone, endZone, StringComparison.OrdinalIgnoreCase))
                return new List<PathStep>();

            var frontier = new SortedSet<QueueItem>();
            var cameFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var costs = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            costs[startZone] = 0f;
            frontier.Add(CreateQueueItem(startZone, 0f));

            while (frontier.Count > 0)
            {
                QueueItem currentItem = GetAndRemoveFirst(frontier);
                string currentZone = currentItem.Zone;

                if (string.Equals(currentZone, endZone, StringComparison.OrdinalIgnoreCase))
                    return ReconstructPathSteps(cameFrom, currentZone);

                if (!_connections.TryGetValue(currentZone, out List<Link> outgoingLinks))
                    continue;

                for (int i = 0; i < outgoingLinks.Count; i++)
                {
                    Link link = outgoingLinks[i];
                    float newCost = costs[currentZone] + link.Cost;
                    if (costs.TryGetValue(link.ToZone, out float existingCost) && existingCost <= newCost)
                        continue;

                    costs[link.ToZone] = newCost;
                    cameFrom[link.ToZone] = currentZone;
                    frontier.Add(CreateQueueItem(link.ToZone, newCost));
                }
            }

            return null;
        }

        private static void AddLink(
            string fromZone,
            string toZone,
            Vector3 fromWaypoint,
            Vector3 toWaypoint,
            Vector3 fromCrossingAnchor,
            Vector3 toCrossingAnchor,
            float cost,
            StepKind kind,
            string connectorName,
            bool requiresInteraction,
            float transitionWaitSeconds)
        {
            Link link = new Link
            {
                FromZone = fromZone,
                ToZone = toZone,
                FromWaypoint = fromWaypoint,
                ToWaypoint = toWaypoint,
                FromCrossingAnchor = fromCrossingAnchor,
                ToCrossingAnchor = toCrossingAnchor,
                Cost = cost <= 0f ? 1f : cost,
                Kind = kind,
                ConnectorName = connectorName,
                RequiresInteraction = requiresInteraction,
                TransitionWaitSeconds = transitionWaitSeconds < 0f ? 0f : transitionWaitSeconds
            };

            _links.Add(link);
            if (!_connections.TryGetValue(fromZone, out List<Link> outgoingLinks))
            {
                outgoingLinks = new List<Link>();
                _connections[fromZone] = outgoingLinks;
            }

            outgoingLinks.Add(link);
            _knownZones.Add(fromZone);
            _knownZones.Add(toZone);
        }

        private static List<Link> ParseNavigationJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var links = new List<Link>();
            int linksIndex = json.IndexOf("\"Links\"", StringComparison.OrdinalIgnoreCase);
            if (linksIndex < 0)
                return links;

            int arrayStart = json.IndexOf('[', linksIndex);
            if (arrayStart < 0)
                return links;

            int arrayEnd = FindMatchingBracket(json, arrayStart, '[', ']');
            if (arrayEnd < 0)
                return links;

            string linksArray = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
            string[] objects = SplitJsonObjects(linksArray);
            for (int i = 0; i < objects.Length; i++)
            {
                Link link = ParseLinkObject(objects[i]);
                if (link != null)
                    links.Add(link);
            }

            return links;
        }

        private static string[] SplitJsonObjects(string arrayContent)
        {
            var objects = new List<string>();
            int braceDepth = 0;
            StringBuilder current = new StringBuilder();

            for (int i = 0; i < arrayContent.Length; i++)
            {
                char c = arrayContent[i];
                if (c == '{')
                {
                    braceDepth++;
                    current.Append(c);
                    continue;
                }

                if (c == '}')
                {
                    current.Append(c);
                    braceDepth--;
                    if (braceDepth == 0 && current.Length > 0)
                    {
                        objects.Add(current.ToString());
                        current = new StringBuilder();
                    }
                    continue;
                }

                if (braceDepth > 0)
                    current.Append(c);
            }

            return objects.ToArray();
        }

        private static Link ParseLinkObject(string obj)
        {
            if (string.IsNullOrWhiteSpace(obj))
                return null;

            string fromZone = ExtractJsonString(obj, "FromZone");
            string toZone = ExtractJsonString(obj, "ToZone");
            if (string.IsNullOrWhiteSpace(fromZone) || string.IsNullOrWhiteSpace(toZone))
                return null;

            float cost = ExtractJsonFloat(obj, "Cost");
            if (cost <= 0f)
                cost = 1f;

            Vector3 fromWaypoint = ExtractJsonVector3(obj, "FromWaypoint");
            Vector3 toWaypoint = ExtractJsonVector3(obj, "ToWaypoint");
            Vector3 fromCrossingAnchor = ExtractJsonVector3(obj, "FromCrossingAnchor");
            Vector3 toCrossingAnchor = ExtractJsonVector3(obj, "ToCrossingAnchor");
            StepKind kind = ParseStepKind(ExtractJsonString(obj, "StepKind"));
            string connectorName = ExtractJsonString(obj, "ConnectorName");
            bool requiresInteraction = ExtractJsonBool(obj, "RequiresInteraction");
            float transitionWaitSeconds = ExtractJsonFloat(obj, "TransitionWaitSeconds");

            return new Link
            {
                FromZone = fromZone,
                ToZone = toZone,
                FromWaypoint = fromWaypoint,
                ToWaypoint = toWaypoint,
                FromCrossingAnchor = fromCrossingAnchor,
                ToCrossingAnchor = toCrossingAnchor,
                Cost = cost,
                Kind = kind,
                ConnectorName = connectorName,
                RequiresInteraction = requiresInteraction,
                TransitionWaitSeconds = transitionWaitSeconds
            };
        }

        private static StepKind ParseStepKind(string kindText)
        {
            if (string.IsNullOrWhiteSpace(kindText))
                return StepKind.Unknown;

            if (Enum.TryParse(kindText, ignoreCase: true, out StepKind parsedKind))
                return parsedKind;

            return StepKind.Unknown;
        }

        private static string ExtractJsonString(string obj, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = obj.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return null;

            int colonIndex = obj.IndexOf(':', keyIndex);
            if (colonIndex < 0)
                return null;

            int quoteStart = obj.IndexOf('"', colonIndex + 1);
            if (quoteStart < 0)
                return null;

            int quoteEnd = obj.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0)
                return null;

            return obj.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static Vector3 ExtractJsonVector3(string obj, string key)
        {
            string objectContent = ExtractJsonObject(obj, key);
            if (string.IsNullOrWhiteSpace(objectContent))
                return Vector3.zero;

            return new Vector3(
                ExtractJsonFloat(objectContent, "x"),
                ExtractJsonFloat(objectContent, "y"),
                ExtractJsonFloat(objectContent, "z"));
        }

        private static string ExtractJsonObject(string obj, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = obj.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return null;

            int colonIndex = obj.IndexOf(':', keyIndex);
            if (colonIndex < 0)
                return null;

            int braceStart = obj.IndexOf('{', colonIndex);
            if (braceStart < 0)
                return null;

            int braceEnd = FindMatchingBracket(obj, braceStart, '{', '}');
            if (braceEnd < 0)
                return null;

            return obj.Substring(braceStart, braceEnd - braceStart + 1);
        }

        private static float ExtractJsonFloat(string obj, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = obj.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return 0f;

            int colonIndex = obj.IndexOf(':', keyIndex);
            if (colonIndex < 0)
                return 0f;

            int valueStart = colonIndex + 1;
            while (valueStart < obj.Length && char.IsWhiteSpace(obj[valueStart]))
                valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < obj.Length && (char.IsDigit(obj[valueEnd]) || obj[valueEnd] == '.' || obj[valueEnd] == '-'))
                valueEnd++;

            if (valueEnd <= valueStart)
                return 0f;

            string numberText = obj.Substring(valueStart, valueEnd - valueStart);
            return float.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue)
                ? parsedValue
                : 0f;
        }

        private static bool ExtractJsonBool(string obj, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = obj.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
                return false;

            int colonIndex = obj.IndexOf(':', keyIndex);
            if (colonIndex < 0)
                return false;

            int valueStart = colonIndex + 1;
            while (valueStart < obj.Length && char.IsWhiteSpace(obj[valueStart]))
                valueStart++;

            if (valueStart + 4 <= obj.Length &&
                string.Compare(obj, valueStart, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return true;

            return false;
        }

        private static int FindMatchingBracket(string text, int startIndex, char open, char close)
        {
            int depth = 1;
            for (int i = startIndex + 1; i < text.Length; i++)
            {
                if (text[i] == open)
                    depth++;
                else if (text[i] == close)
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static QueueItem CreateQueueItem(string zone, float priority)
        {
            return new QueueItem
            {
                Zone = zone,
                Priority = priority,
                Sequence = _queueSequence++
            };
        }

        private static QueueItem GetAndRemoveFirst(SortedSet<QueueItem> frontier)
        {
            QueueItem first = null;
            foreach (QueueItem item in frontier)
            {
                first = item;
                break;
            }

            if (first != null)
                frontier.Remove(first);

            return first;
        }

        private static List<PathStep> ReconstructPathSteps(Dictionary<string, string> cameFrom, string currentZone)
        {
            var reverseSteps = new List<PathStep>();
            while (cameFrom.TryGetValue(currentZone, out string previousZone))
            {
                if (!_connections.TryGetValue(previousZone, out List<Link> outgoingLinks))
                    return null;

                Link matchingLink = null;
                for (int i = 0; i < outgoingLinks.Count; i++)
                {
                    Link candidate = outgoingLinks[i];
                    if (string.Equals(candidate.ToZone, currentZone, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingLink = candidate;
                        break;
                    }
                }

                if (matchingLink == null)
                    return null;

                reverseSteps.Add(new PathStep(
                    matchingLink.FromZone,
                    matchingLink.ToZone,
                    matchingLink.FromWaypoint,
                    matchingLink.ToWaypoint,
                    matchingLink.FromCrossingAnchor,
                    matchingLink.ToCrossingAnchor,
                    matchingLink.Cost,
                    matchingLink.Kind,
                    matchingLink.ConnectorName,
                    matchingLink.RequiresInteraction,
                    matchingLink.TransitionWaitSeconds));
                currentZone = previousZone;
            }

            reverseSteps.Reverse();
            return reverseSteps;
        }
    }
}
