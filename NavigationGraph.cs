using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Loads and queries the generated navigation graph from the plugin directory.
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
        /// Represents one directed navigation transition between authored graph zones.
        /// </summary>
        public sealed class PathStep
        {
            internal PathStep(
                string id,
                string fromZone,
                string toZone,
                string fromNodeId,
                string toNodeId,
                Vector3 fromWaypoint,
                Vector3 toWaypoint,
                Vector3 fromCrossingAnchor,
                Vector3 toCrossingAnchor,
                Vector3 sourceApproachPoint,
                Vector3 sourceClearPoint,
                Vector3 destinationClearPoint,
                Vector3 destinationApproachPoint,
                Vector3[] navigationPoints,
                float cost,
                StepKind kind,
                string connectorName,
                string[] connectorNames,
                Vector3 connectorObjectPosition,
                bool requiresInteraction,
                float transitionWaitSeconds,
                string[] acceptedSourceZones,
                string[] acceptedDestinationZones,
                float validationTimeoutSeconds,
                int staticSuspicionScore,
                string[] staticIssues,
                string sourceSceneZoneName,
                string destinationSceneZoneName,
                string assetDerivationSource)
            {
                Id = id;
                FromZone = fromZone;
                ToZone = toZone;
                FromNodeId = fromNodeId;
                ToNodeId = toNodeId;
                FromWaypoint = fromWaypoint;
                ToWaypoint = toWaypoint;
                FromCrossingAnchor = fromCrossingAnchor;
                ToCrossingAnchor = toCrossingAnchor;
                SourceApproachPoint = sourceApproachPoint;
                SourceClearPoint = sourceClearPoint;
                DestinationClearPoint = destinationClearPoint;
                DestinationApproachPoint = destinationApproachPoint;
                NavigationPoints = navigationPoints ?? Array.Empty<Vector3>();
                Cost = cost;
                Kind = kind;
                ConnectorName = connectorName;
                ConnectorNames = connectorNames != null && connectorNames.Length > 0
                    ? connectorNames
                    : !string.IsNullOrWhiteSpace(connectorName)
                        ? new[] { connectorName }
                        : Array.Empty<string>();
                ConnectorObjectPosition = connectorObjectPosition;
                RequiresInteraction = requiresInteraction;
                TransitionWaitSeconds = transitionWaitSeconds;
                AcceptedSourceZones = acceptedSourceZones ?? Array.Empty<string>();
                AcceptedDestinationZones = acceptedDestinationZones ?? Array.Empty<string>();
                ValidationTimeoutSeconds = validationTimeoutSeconds;
                StaticSuspicionScore = staticSuspicionScore;
                StaticIssues = staticIssues ?? Array.Empty<string>();
                SourceSceneZoneName = sourceSceneZoneName;
                DestinationSceneZoneName = destinationSceneZoneName;
                AssetDerivationSource = assetDerivationSource;
            }

            /// <summary>
            /// Gets the stable directed transition identifier.
            /// </summary>
            public string Id { get; }

            /// <summary>
            /// Gets the source graph zone for the step.
            /// </summary>
            public string FromZone { get; }

            /// <summary>
            /// Gets the destination graph zone for the step.
            /// </summary>
            public string ToZone { get; }

            /// <summary>
            /// Gets the source in-room node identifier when available.
            /// </summary>
            public string FromNodeId { get; }

            /// <summary>
            /// Gets the destination in-room node identifier when available.
            /// </summary>
            public string ToNodeId { get; }

            /// <summary>
            /// Gets the source-side waypoint used for coarse routing.
            /// </summary>
            public Vector3 FromWaypoint { get; }

            /// <summary>
            /// Gets the destination-side waypoint used for coarse routing.
            /// </summary>
            public Vector3 ToWaypoint { get; }

            /// <summary>
            /// Gets the source-side threshold anchor when available.
            /// </summary>
            public Vector3 FromCrossingAnchor { get; }

            /// <summary>
            /// Gets the destination-side threshold anchor when available.
            /// </summary>
            public Vector3 ToCrossingAnchor { get; }

            /// <summary>
            /// Gets the source-side pre-connector approach point.
            /// </summary>
            public Vector3 SourceApproachPoint { get; }

            /// <summary>
            /// Gets the source-side post-approach clear point.
            /// </summary>
            public Vector3 SourceClearPoint { get; }

            /// <summary>
            /// Gets the destination-side first clear point after the connector.
            /// </summary>
            public Vector3 DestinationClearPoint { get; }

            /// <summary>
            /// Gets the destination-side settling approach point.
            /// </summary>
            public Vector3 DestinationApproachPoint { get; }

            /// <summary>
            /// Gets the preferred ordered traversal points for this transition.
            /// </summary>
            public Vector3[] NavigationPoints { get; }

            /// <summary>
            /// Gets the traversal cost for this step.
            /// </summary>
            public float Cost { get; }

            /// <summary>
            /// Gets the transition type for this step.
            /// </summary>
            public StepKind Kind { get; }

            /// <summary>
            /// Gets the authored connector name when available.
            /// </summary>
            public string ConnectorName { get; }

            /// <summary>
            /// Gets the authored connector names accepted for this transition.
            /// </summary>
            public string[] ConnectorNames { get; }

            /// <summary>
            /// Gets the authored connector object position when available.
            /// </summary>
            public Vector3 ConnectorObjectPosition { get; }

            /// <summary>
            /// Gets whether the transition requires an explicit interaction.
            /// </summary>
            public bool RequiresInteraction { get; }

            /// <summary>
            /// Gets the wait time after starting an interaction-driven transition.
            /// </summary>
            public float TransitionWaitSeconds { get; }

            /// <summary>
            /// Gets the runtime-accepted source scene zones for validation and sweep recovery.
            /// </summary>
            public string[] AcceptedSourceZones { get; }

            /// <summary>
            /// Gets the runtime-accepted destination scene zones for validation and sweep recovery.
            /// </summary>
            public string[] AcceptedDestinationZones { get; }

            /// <summary>
            /// Gets the recommended transition validation timeout in seconds.
            /// </summary>
            public float ValidationTimeoutSeconds { get; }

            /// <summary>
            /// Gets the static suspicion score emitted by the graph builder.
            /// </summary>
            public int StaticSuspicionScore { get; }

            /// <summary>
            /// Gets the static validation issues emitted by the graph builder.
            /// </summary>
            public string[] StaticIssues { get; }

            /// <summary>
            /// Gets the specific source scene-zone node chosen for this directed transition.
            /// </summary>
            public string SourceSceneZoneName { get; }

            /// <summary>
            /// Gets the specific destination scene-zone node chosen for this directed transition.
            /// </summary>
            public string DestinationSceneZoneName { get; }

            /// <summary>
            /// Gets the asset-side derivation mode used by the builder.
            /// </summary>
            public string AssetDerivationSource { get; }
        }

        #pragma warning disable CS0649

        [Serializable]
        private sealed class GraphDocument
        {
            public int SchemaVersion;
            public string SceneName;
            public ZoneRecord[] Zones;
            public NodeRecord[] Nodes;
            public TransitionRecord[] Transitions;
            public TransitionRecord[] Links;
        }

        [Serializable]
        private sealed class ZoneRecord
        {
            public string Id;
            public string Name;
            public string[] NodeIds;
            public string CenterNodeId;
            public string[] SceneZoneNames;
        }

        [Serializable]
        private sealed class NodeRecord
        {
            public string Id;
            public string Zone;
            public string SceneZoneName;
            public string Kind;
            public Vector3 Position;
            public Vector3 Scale;
            public string Source;
        }

        [Serializable]
        private sealed class TransitionRecord
        {
            public string Id;
            public string FromZone;
            public string ToZone;
            public string FromNodeId;
            public string ToNodeId;
            public Vector3 FromWaypoint;
            public Vector3 ToWaypoint;
            public Vector3 FromCrossingAnchor;
            public Vector3 ToCrossingAnchor;
            public Vector3 SourceApproachPoint;
            public Vector3 SourceClearPoint;
            public Vector3 DestinationClearPoint;
            public Vector3 DestinationApproachPoint;
            public Vector3[] NavigationPoints;
            public float Cost;
            public string StepKind;
            public string ConnectorName;
            public string[] ConnectorNames;
            public bool RequiresInteraction;
            public float TransitionWaitSeconds;
            public ConnectorRecord Connector;
            public ValidationRecord Validation;
            public string SourceSceneZoneName;
            public string DestinationSceneZoneName;
            public string AssetDerivationSource;
        }

        [Serializable]
        private sealed class ConnectorRecord
        {
            public string Name;
            public string[] Names;
            public Vector3 ObjectPosition;
            public Vector3 SourceApproachPoint;
            public Vector3 SourceClearPoint;
            public Vector3 DestinationClearPoint;
            public Vector3 DestinationApproachPoint;
            public Vector3[] NavigationPoints;
            public string AssetDerivationSource;
        }

        [Serializable]
        private sealed class ValidationRecord
        {
            public string[] AcceptedSourceZones;
            public string[] AcceptedDestinationZones;
            public float StepTimeoutSeconds;
            public int StaticSuspicionScore;
            public string[] StaticIssues;
            public string AssetDerivationSource;
            public string SourceSceneZoneName;
            public string DestinationSceneZoneName;
        }

        [DataContract]
        private sealed class SerializedGraphDocument
        {
            [DataMember(Name = "SchemaVersion")]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "Zones")]
            public SerializedZoneRecord[] Zones { get; set; }

            [DataMember(Name = "Nodes")]
            public SerializedNodeRecord[] Nodes { get; set; }

            [DataMember(Name = "Transitions")]
            public SerializedTransitionRecord[] Transitions { get; set; }

            [DataMember(Name = "Links")]
            public SerializedTransitionRecord[] Links { get; set; }
        }

        [DataContract]
        private sealed class SerializedZoneRecord
        {
            [DataMember(Name = "Name")]
            public string Name { get; set; }
        }

        [DataContract]
        private sealed class SerializedNodeRecord
        {
        }

        [DataContract]
        private sealed class SerializedTransitionRecord
        {
            [DataMember(Name = "Id")]
            public string Id { get; set; }

            [DataMember(Name = "FromZone")]
            public string FromZone { get; set; }

            [DataMember(Name = "ToZone")]
            public string ToZone { get; set; }

            [DataMember(Name = "FromNodeId")]
            public string FromNodeId { get; set; }

            [DataMember(Name = "ToNodeId")]
            public string ToNodeId { get; set; }

            [DataMember(Name = "FromWaypoint")]
            public SerializedVector3 FromWaypoint { get; set; }

            [DataMember(Name = "ToWaypoint")]
            public SerializedVector3 ToWaypoint { get; set; }

            [DataMember(Name = "FromCrossingAnchor")]
            public SerializedVector3 FromCrossingAnchor { get; set; }

            [DataMember(Name = "ToCrossingAnchor")]
            public SerializedVector3 ToCrossingAnchor { get; set; }

            [DataMember(Name = "SourceApproachPoint")]
            public SerializedVector3 SourceApproachPoint { get; set; }

            [DataMember(Name = "SourceClearPoint")]
            public SerializedVector3 SourceClearPoint { get; set; }

            [DataMember(Name = "DestinationClearPoint")]
            public SerializedVector3 DestinationClearPoint { get; set; }

            [DataMember(Name = "DestinationApproachPoint")]
            public SerializedVector3 DestinationApproachPoint { get; set; }

            [DataMember(Name = "NavigationPoints")]
            public SerializedVector3[] NavigationPoints { get; set; }

            [DataMember(Name = "Cost")]
            public float Cost { get; set; }

            [DataMember(Name = "StepKind")]
            public string StepKind { get; set; }

            [DataMember(Name = "ConnectorName")]
            public string ConnectorName { get; set; }

            [DataMember(Name = "ConnectorNames")]
            public string[] ConnectorNames { get; set; }

            [DataMember(Name = "RequiresInteraction")]
            public bool RequiresInteraction { get; set; }

            [DataMember(Name = "TransitionWaitSeconds")]
            public float TransitionWaitSeconds { get; set; }

            [DataMember(Name = "Connector")]
            public SerializedConnectorRecord Connector { get; set; }

            [DataMember(Name = "Validation")]
            public SerializedValidationRecord Validation { get; set; }

            [DataMember(Name = "SourceSceneZoneName")]
            public string SourceSceneZoneName { get; set; }

            [DataMember(Name = "DestinationSceneZoneName")]
            public string DestinationSceneZoneName { get; set; }

            [DataMember(Name = "AssetDerivationSource")]
            public string AssetDerivationSource { get; set; }
        }

        [DataContract]
        private sealed class SerializedConnectorRecord
        {
            [DataMember(Name = "Name")]
            public string Name { get; set; }

            [DataMember(Name = "Names")]
            public string[] Names { get; set; }

            [DataMember(Name = "ObjectPosition")]
            public SerializedVector3 ObjectPosition { get; set; }

            [DataMember(Name = "SourceApproachPoint")]
            public SerializedVector3 SourceApproachPoint { get; set; }

            [DataMember(Name = "SourceClearPoint")]
            public SerializedVector3 SourceClearPoint { get; set; }

            [DataMember(Name = "DestinationClearPoint")]
            public SerializedVector3 DestinationClearPoint { get; set; }

            [DataMember(Name = "DestinationApproachPoint")]
            public SerializedVector3 DestinationApproachPoint { get; set; }

            [DataMember(Name = "NavigationPoints")]
            public SerializedVector3[] NavigationPoints { get; set; }

            [DataMember(Name = "AssetDerivationSource")]
            public string AssetDerivationSource { get; set; }
        }

        [DataContract]
        private sealed class SerializedValidationRecord
        {
            [DataMember(Name = "AcceptedSourceZones")]
            public string[] AcceptedSourceZones { get; set; }

            [DataMember(Name = "AcceptedDestinationZones")]
            public string[] AcceptedDestinationZones { get; set; }

            [DataMember(Name = "StepTimeoutSeconds")]
            public float StepTimeoutSeconds { get; set; }

            [DataMember(Name = "StaticSuspicionScore")]
            public int StaticSuspicionScore { get; set; }

            [DataMember(Name = "StaticIssues")]
            public string[] StaticIssues { get; set; }

            [DataMember(Name = "AssetDerivationSource")]
            public string AssetDerivationSource { get; set; }

            [DataMember(Name = "SourceSceneZoneName")]
            public string SourceSceneZoneName { get; set; }

            [DataMember(Name = "DestinationSceneZoneName")]
            public string DestinationSceneZoneName { get; set; }
        }

        [DataContract]
        private sealed class SerializedVector3
        {
            [DataMember(Name = "x")]
            public float X { get; set; }

            [DataMember(Name = "y")]
            public float Y { get; set; }

            [DataMember(Name = "z")]
            public float Z { get; set; }

            public Vector3 ToVector3()
            {
                return new Vector3(X, Y, Z);
            }
        }

        #pragma warning restore CS0649

        private sealed class Link
        {
            public string Id;
            public string FromZone;
            public string ToZone;
            public string FromNodeId;
            public string ToNodeId;
            public Vector3 FromWaypoint;
            public Vector3 ToWaypoint;
            public Vector3 FromCrossingAnchor;
            public Vector3 ToCrossingAnchor;
            public Vector3 SourceApproachPoint;
            public Vector3 SourceClearPoint;
            public Vector3 DestinationClearPoint;
            public Vector3 DestinationApproachPoint;
            public Vector3[] NavigationPoints;
            public float Cost;
            public StepKind Kind;
            public string ConnectorName;
            public string[] ConnectorNames;
            public Vector3 ConnectorObjectPosition;
            public bool RequiresInteraction;
            public float TransitionWaitSeconds;
            public string[] AcceptedSourceZones;
            public string[] AcceptedDestinationZones;
            public float ValidationTimeoutSeconds;
            public int StaticSuspicionScore;
            public string[] StaticIssues;
            public string SourceSceneZoneName;
            public string DestinationSceneZoneName;
            public string AssetDerivationSource;
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
        private static int _zoneCount;
        private static int _nodeCount;
        private static long _queueSequence;
        private static DateTime _lastFailedLoadAttemptUtc = DateTime.MinValue;
        private static readonly TimeSpan FailedLoadRetryInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Loads the generated navigation graph from the BepInEx plugins folder when present.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                if (_links.Count > 0)
                    return;

                if (_lastFailedLoadAttemptUtc > DateTime.MinValue &&
                    DateTime.UtcNow - _lastFailedLoadAttemptUtc < FailedLoadRetryInterval)
                {
                    return;
                }
            }

            _initialized = true;
            _lastFailedLoadAttemptUtc = DateTime.UtcNow;
            _links.Clear();
            _connections.Clear();
            _knownZones.Clear();
            _zoneCount = 0;
            _nodeCount = 0;

            try
            {
                string jsonPath = Path.Combine(Paths.PluginPath, "navigation_graph.json");
                if (!File.Exists(jsonPath))
                {
                    Main.Log.LogWarning("Navigation graph JSON not found at: " + jsonPath);
                    return;
                }

                string json = File.ReadAllText(jsonPath);
                TransitionRecord[] transitions;
                int schemaVersion;
                string parserLabel;
                if (!TryLoadGraphDocument(json, out transitions, out schemaVersion, out parserLabel))
                {
                    Main.Log.LogWarning("Navigation graph JSON did not contain any usable transitions.");
                    return;
                }

                for (int i = 0; i < transitions.Length; i++)
                {
                    Link link = ParseTransition(transitions[i]);
                    if (link == null)
                        continue;

                    AddLink(link);
                }

                if (_links.Count == 0)
                {
                    Main.Log.LogWarning("Navigation graph JSON did not yield any usable parsed links.");
                    return;
                }

                Main.Log.LogInfo(
                    "Navigation graph loaded. Parser=" + parserLabel +
                    " SchemaVersion=" + schemaVersion +
                    " Zones=" + (_zoneCount > 0 ? _zoneCount : _knownZones.Count) +
                    " Nodes=" + _nodeCount +
                    " Transitions=" + _links.Count);
                _lastFailedLoadAttemptUtc = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                Main.Log.LogError("Failed to initialize navigation graph: " + ex);
            }
        }

        private static bool TryLoadGraphDocument(
            string json,
            out TransitionRecord[] transitions,
            out int schemaVersion,
            out string parserLabel)
        {
            transitions = null;
            schemaVersion = 0;
            parserLabel = null;

            GraphDocument document = null;
            try
            {
                document = JsonUtility.FromJson<GraphDocument>(json);
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("Navigation graph JsonUtility parse failed: " + ex.Message);
            }

            if (document != null)
            {
                schemaVersion = document.SchemaVersion;
                _zoneCount = document.Zones != null ? document.Zones.Length : 0;
                _nodeCount = document.Nodes != null ? document.Nodes.Length : 0;
                AddKnownZones(document.Zones);
                transitions = GetTransitionRecords(document);
                if (transitions != null && transitions.Length > 0)
                {
                    parserLabel = "JsonUtility";
                    return true;
                }
            }

            SerializedGraphDocument serializedDocument;
            if (!TryLoadSerializedGraphDocument(json, out serializedDocument) || serializedDocument == null)
                return false;

            schemaVersion = serializedDocument.SchemaVersion;
            _zoneCount = serializedDocument.Zones != null ? serializedDocument.Zones.Length : 0;
            _nodeCount = serializedDocument.Nodes != null ? serializedDocument.Nodes.Length : 0;
            AddKnownZones(serializedDocument.Zones);
            transitions = ConvertTransitions(GetTransitionRecords(serializedDocument));
            if (transitions == null || transitions.Length == 0)
                return false;

            parserLabel = "DataContractJsonSerializer";
            return true;
        }

        private static bool TryLoadSerializedGraphDocument(string json, out SerializedGraphDocument document)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SerializedGraphDocument));
                    document = serializer.ReadObject(stream) as SerializedGraphDocument;
                    return document != null;
                }
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("Navigation graph DataContractJsonSerializer parse failed: " + ex.Message);
                return false;
            }
        }

        private static SerializedTransitionRecord[] GetTransitionRecords(SerializedGraphDocument document)
        {
            if (document == null)
                return null;

            if (document.Transitions != null && document.Transitions.Length > 0)
                return document.Transitions;

            return document.Links;
        }

        private static TransitionRecord[] ConvertTransitions(SerializedTransitionRecord[] records)
        {
            if (records == null || records.Length == 0)
                return null;

            var transitions = new TransitionRecord[records.Length];
            for (int i = 0; i < records.Length; i++)
            {
                transitions[i] = ConvertTransition(records[i]);
            }

            return transitions;
        }

        private static TransitionRecord ConvertTransition(SerializedTransitionRecord record)
        {
            if (record == null)
                return null;

            return new TransitionRecord
            {
                Id = record.Id,
                FromZone = record.FromZone,
                ToZone = record.ToZone,
                FromNodeId = record.FromNodeId,
                ToNodeId = record.ToNodeId,
                FromWaypoint = ToVector3(record.FromWaypoint),
                ToWaypoint = ToVector3(record.ToWaypoint),
                FromCrossingAnchor = ToVector3(record.FromCrossingAnchor),
                ToCrossingAnchor = ToVector3(record.ToCrossingAnchor),
                SourceApproachPoint = ToVector3(record.SourceApproachPoint),
                SourceClearPoint = ToVector3(record.SourceClearPoint),
                DestinationClearPoint = ToVector3(record.DestinationClearPoint),
                DestinationApproachPoint = ToVector3(record.DestinationApproachPoint),
                NavigationPoints = ToVector3Array(record.NavigationPoints),
                Cost = record.Cost,
                StepKind = record.StepKind,
                ConnectorName = record.ConnectorName,
                ConnectorNames = record.ConnectorNames,
                RequiresInteraction = record.RequiresInteraction,
                TransitionWaitSeconds = record.TransitionWaitSeconds,
                Connector = ConvertConnector(record.Connector),
                Validation = ConvertValidation(record.Validation),
                SourceSceneZoneName = record.SourceSceneZoneName,
                DestinationSceneZoneName = record.DestinationSceneZoneName,
                AssetDerivationSource = record.AssetDerivationSource
            };
        }

        private static ConnectorRecord ConvertConnector(SerializedConnectorRecord record)
        {
            if (record == null)
                return null;

            return new ConnectorRecord
            {
                Name = record.Name,
                Names = record.Names,
                ObjectPosition = ToVector3(record.ObjectPosition),
                SourceApproachPoint = ToVector3(record.SourceApproachPoint),
                SourceClearPoint = ToVector3(record.SourceClearPoint),
                DestinationClearPoint = ToVector3(record.DestinationClearPoint),
                DestinationApproachPoint = ToVector3(record.DestinationApproachPoint),
                NavigationPoints = ToVector3Array(record.NavigationPoints),
                AssetDerivationSource = record.AssetDerivationSource
            };
        }

        private static ValidationRecord ConvertValidation(SerializedValidationRecord record)
        {
            if (record == null)
                return null;

            return new ValidationRecord
            {
                AcceptedSourceZones = record.AcceptedSourceZones,
                AcceptedDestinationZones = record.AcceptedDestinationZones,
                StepTimeoutSeconds = record.StepTimeoutSeconds,
                StaticSuspicionScore = record.StaticSuspicionScore,
                StaticIssues = record.StaticIssues,
                AssetDerivationSource = record.AssetDerivationSource,
                SourceSceneZoneName = record.SourceSceneZoneName,
                DestinationSceneZoneName = record.DestinationSceneZoneName
            };
        }

        private static Vector3 ToVector3(SerializedVector3 value)
        {
            return value != null ? value.ToVector3() : Vector3.zero;
        }

        private static Vector3[] ToVector3Array(SerializedVector3[] values)
        {
            if (values == null || values.Length == 0)
                return Array.Empty<Vector3>();

            var converted = new Vector3[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                converted[i] = ToVector3(values[i]);
            }

            return converted;
        }

        private static void AddKnownZones(ZoneRecord[] zones)
        {
            if (zones == null || zones.Length == 0)
                return;

            for (int i = 0; i < zones.Length; i++)
            {
                string zoneName = zones[i] != null ? zones[i].Name : null;
                if (!string.IsNullOrWhiteSpace(zoneName))
                    _knownZones.Add(zoneName);
            }
        }

        private static void AddKnownZones(SerializedZoneRecord[] zones)
        {
            if (zones == null || zones.Length == 0)
                return;

            for (int i = 0; i < zones.Length; i++)
            {
                string zoneName = zones[i] != null ? zones[i].Name : null;
                if (!string.IsNullOrWhiteSpace(zoneName))
                    _knownZones.Add(zoneName);
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
        /// Returns every known graph zone in stable sorted order.
        /// </summary>
        public static List<string> GetAllZones()
        {
            Initialize();

            var zones = new List<string>(_knownZones.Count);
            foreach (string zone in _knownZones)
            {
                if (!string.IsNullOrWhiteSpace(zone))
                    zones.Add(zone);
            }

            zones.Sort(StringComparer.OrdinalIgnoreCase);
            return zones;
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
                steps.Add(CreatePathStep(_links[i]));
            }

            return steps;
        }

        /// <summary>
        /// Finds the shortest path of graph zones between two zones.
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
        /// Finds the shortest transition path between two zones.
        /// </summary>
        public static List<PathStep> FindPathSteps(string startZone, string endZone)
        {
            return FindPathSteps(startZone, endZone, null, null);
        }

        /// <summary>
        /// Finds the shortest transition path between two zones, biased by live start and end positions when available.
        /// </summary>
        public static List<PathStep> FindPathSteps(string startZone, string endZone, Vector3? startPosition, Vector3? endPosition)
        {
            if (string.IsNullOrWhiteSpace(startZone) || string.IsNullOrWhiteSpace(endZone))
                return null;

            Initialize();

            if (!_knownZones.Contains(startZone) || !_knownZones.Contains(endZone))
                return null;

            if (string.Equals(startZone, endZone, StringComparison.OrdinalIgnoreCase))
                return new List<PathStep>();

            var frontier = new SortedSet<QueueItem>();
            var cameFrom = new Dictionary<string, Link>(StringComparer.OrdinalIgnoreCase);
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
                    float newCost = costs[currentZone] + GetTraversalCost(link, startZone, endZone, startPosition, endPosition);
                    if (costs.TryGetValue(link.ToZone, out float existingCost) && existingCost <= newCost)
                        continue;

                    costs[link.ToZone] = newCost;
                    cameFrom[link.ToZone] = link;
                    frontier.Add(CreateQueueItem(link.ToZone, newCost));
                }
            }

            return null;
        }

        private static TransitionRecord[] GetTransitionRecords(GraphDocument document)
        {
            if (document == null)
                return null;

            if (document.Transitions != null && document.Transitions.Length > 0)
                return document.Transitions;

            return document.Links;
        }

        private static Link ParseTransition(TransitionRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.FromZone) || string.IsNullOrWhiteSpace(record.ToZone))
                return null;

            ConnectorRecord connector = record.Connector;
            ValidationRecord validation = record.Validation;

            Vector3 connectorObjectPosition = connector != null ? connector.ObjectPosition : Vector3.zero;
            string connectorName = !string.IsNullOrWhiteSpace(record.ConnectorName)
                ? record.ConnectorName
                : connector != null ? connector.Name : null;
            string[] connectorNames = MergeUniqueStringArrays(
                connectorName,
                record.ConnectorNames,
                connector != null ? connector.Names : null);
            string assetDerivationSource = !string.IsNullOrWhiteSpace(record.AssetDerivationSource)
                ? record.AssetDerivationSource
                : connector != null ? connector.AssetDerivationSource : null;

            return new Link
            {
                Id = record.Id,
                FromZone = record.FromZone,
                ToZone = record.ToZone,
                FromNodeId = record.FromNodeId,
                ToNodeId = record.ToNodeId,
                FromWaypoint = record.FromWaypoint,
                ToWaypoint = record.ToWaypoint,
                FromCrossingAnchor = record.FromCrossingAnchor,
                ToCrossingAnchor = record.ToCrossingAnchor,
                SourceApproachPoint = record.SourceApproachPoint != Vector3.zero
                    ? record.SourceApproachPoint
                    : connector != null ? connector.SourceApproachPoint : Vector3.zero,
                SourceClearPoint = record.SourceClearPoint != Vector3.zero
                    ? record.SourceClearPoint
                    : connector != null ? connector.SourceClearPoint : Vector3.zero,
                DestinationClearPoint = record.DestinationClearPoint != Vector3.zero
                    ? record.DestinationClearPoint
                    : connector != null ? connector.DestinationClearPoint : Vector3.zero,
                DestinationApproachPoint = record.DestinationApproachPoint != Vector3.zero
                    ? record.DestinationApproachPoint
                    : connector != null ? connector.DestinationApproachPoint : Vector3.zero,
                NavigationPoints = record.NavigationPoints != null && record.NavigationPoints.Length > 0
                    ? record.NavigationPoints
                    : connector != null ? connector.NavigationPoints : Array.Empty<Vector3>(),
                Cost = record.Cost > 0f ? record.Cost : 1f,
                Kind = ParseStepKind(record.StepKind),
                ConnectorName = connectorName,
                ConnectorNames = connectorNames,
                ConnectorObjectPosition = connectorObjectPosition,
                RequiresInteraction = record.RequiresInteraction,
                TransitionWaitSeconds = record.TransitionWaitSeconds < 0f ? 0f : record.TransitionWaitSeconds,
                AcceptedSourceZones = SanitizeStringArray(validation != null ? validation.AcceptedSourceZones : null),
                AcceptedDestinationZones = SanitizeStringArray(validation != null ? validation.AcceptedDestinationZones : null),
                ValidationTimeoutSeconds = validation != null && validation.StepTimeoutSeconds > 0f ? validation.StepTimeoutSeconds : 0f,
                StaticSuspicionScore = validation != null ? validation.StaticSuspicionScore : 0,
                StaticIssues = SanitizeStringArray(validation != null ? validation.StaticIssues : null),
                SourceSceneZoneName = !string.IsNullOrWhiteSpace(record.SourceSceneZoneName)
                    ? record.SourceSceneZoneName
                    : validation != null ? validation.SourceSceneZoneName : null,
                DestinationSceneZoneName = !string.IsNullOrWhiteSpace(record.DestinationSceneZoneName)
                    ? record.DestinationSceneZoneName
                    : validation != null ? validation.DestinationSceneZoneName : null,
                AssetDerivationSource = assetDerivationSource
            };
        }

        private static string[] MergeUniqueStringArrays(string primaryValue, params string[][] groups)
        {
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(primaryValue) && seen.Add(primaryValue))
                unique.Add(primaryValue);

            if (groups != null)
            {
                for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                {
                    string[] group = groups[groupIndex];
                    if (group == null || group.Length == 0)
                        continue;

                    for (int valueIndex = 0; valueIndex < group.Length; valueIndex++)
                    {
                        string value = group[valueIndex];
                        if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                            continue;

                        unique.Add(value);
                    }
                }
            }

            return unique.Count > 0 ? unique.ToArray() : Array.Empty<string>();
        }

        private static string[] SanitizeStringArray(string[] values)
        {
            if (values == null || values.Length == 0)
                return Array.Empty<string>();

            var unique = new List<string>(values.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];
                if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                    continue;

                unique.Add(value);
            }

            return unique.ToArray();
        }

        private static StepKind ParseStepKind(string kindText)
        {
            if (string.IsNullOrWhiteSpace(kindText))
                return StepKind.Unknown;

            if (Enum.TryParse(kindText, true, out StepKind parsedKind))
                return parsedKind;

            return StepKind.Unknown;
        }

        private static void AddLink(Link link)
        {
            if (link == null)
                return;

            _links.Add(link);
            if (!_connections.TryGetValue(link.FromZone, out List<Link> outgoingLinks))
            {
                outgoingLinks = new List<Link>();
                _connections[link.FromZone] = outgoingLinks;
            }

            outgoingLinks.Add(link);
            _knownZones.Add(link.FromZone);
            _knownZones.Add(link.ToZone);
        }

        private static PathStep CreatePathStep(Link link)
        {
            if (link == null)
                return null;

            return new PathStep(
                link.Id,
                link.FromZone,
                link.ToZone,
                link.FromNodeId,
                link.ToNodeId,
                link.FromWaypoint,
                link.ToWaypoint,
                link.FromCrossingAnchor,
                link.ToCrossingAnchor,
                link.SourceApproachPoint,
                link.SourceClearPoint,
                link.DestinationClearPoint,
                link.DestinationApproachPoint,
                link.NavigationPoints ?? Array.Empty<Vector3>(),
                link.Cost,
                link.Kind,
                link.ConnectorName,
                link.ConnectorNames,
                link.ConnectorObjectPosition,
                link.RequiresInteraction,
                link.TransitionWaitSeconds,
                link.AcceptedSourceZones,
                link.AcceptedDestinationZones,
                link.ValidationTimeoutSeconds,
                link.StaticSuspicionScore,
                link.StaticIssues,
                link.SourceSceneZoneName,
                link.DestinationSceneZoneName,
                link.AssetDerivationSource);
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

        private static float GetTraversalCost(Link link, string startZone, string endZone, Vector3? startPosition, Vector3? endPosition)
        {
            float cost = link.Cost;

            Vector3 sourceApproachPoint = GetSourceApproachPoint(link);
            if (startPosition.HasValue &&
                string.Equals(link.FromZone, startZone, StringComparison.OrdinalIgnoreCase) &&
                sourceApproachPoint != Vector3.zero)
            {
                cost += GetFlatDistance(startPosition.Value, sourceApproachPoint);
            }

            Vector3 destinationApproachPoint = GetDestinationApproachPoint(link);
            if (endPosition.HasValue &&
                string.Equals(link.ToZone, endZone, StringComparison.OrdinalIgnoreCase) &&
                destinationApproachPoint != Vector3.zero)
            {
                cost += GetFlatDistance(destinationApproachPoint, endPosition.Value);
            }

            return cost;
        }

        private static Vector3 GetSourceApproachPoint(Link link)
        {
            if (link == null)
                return Vector3.zero;

            if (link.SourceApproachPoint != Vector3.zero)
                return link.SourceApproachPoint;

            return link.FromWaypoint;
        }

        private static Vector3 GetDestinationApproachPoint(Link link)
        {
            if (link == null)
                return Vector3.zero;

            if (link.DestinationApproachPoint != Vector3.zero)
                return link.DestinationApproachPoint;

            return link.ToWaypoint;
        }

        private static float GetFlatDistance(Vector3 from, Vector3 to)
        {
            from.y = 0f;
            to.y = 0f;
            return Vector3.Distance(from, to);
        }

        private static List<PathStep> ReconstructPathSteps(Dictionary<string, Link> cameFrom, string currentZone)
        {
            var reverseSteps = new List<PathStep>();
            while (cameFrom.TryGetValue(currentZone, out Link matchingLink))
            {
                if (matchingLink == null)
                    return null;

                reverseSteps.Add(CreatePathStep(matchingLink));
                currentZone = matchingLink.FromZone;
            }

            reverseSteps.Reverse();
            return reverseSteps;
        }
    }
}
