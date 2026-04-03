using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DateEverythingAccess
{
    /// <summary>
    /// Loads and queries the room navigation graph from the plugin directory.
    /// </summary>
    public static class NavigationGraph
    {
        private sealed class Link
        {
            public string FromZone;
            public string ToZone;
            public float Cost;
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
                    AddLink(link.FromZone, link.ToZone, link.Cost);
                    AddLink(link.ToZone, link.FromZone, link.Cost);
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
        /// Finds the shortest room path between two zones.
        /// </summary>
        public static List<string> FindPath(string startZone, string endZone)
        {
            if (string.IsNullOrWhiteSpace(startZone) || string.IsNullOrWhiteSpace(endZone))
                return null;

            Initialize();

            if (!_knownZones.Contains(startZone) || !_knownZones.Contains(endZone))
                return null;

            if (string.Equals(startZone, endZone, StringComparison.OrdinalIgnoreCase))
                return new List<string> { startZone };

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
                    return ReconstructPath(cameFrom, currentZone);

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

        private static void AddLink(string fromZone, string toZone, float cost)
        {
            Link link = new Link
            {
                FromZone = fromZone,
                ToZone = toZone,
                Cost = cost <= 0f ? 1f : cost
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

            return new Link
            {
                FromZone = fromZone,
                ToZone = toZone,
                Cost = cost
            };
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
            return float.TryParse(numberText, out float parsedValue)
                ? parsedValue
                : 0f;
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

        private static List<string> ReconstructPath(Dictionary<string, string> cameFrom, string currentZone)
        {
            var path = new List<string> { currentZone };
            while (cameFrom.TryGetValue(currentZone, out string previousZone))
            {
                currentZone = previousZone;
                path.Insert(0, currentZone);
            }

            return path;
        }
    }
}
