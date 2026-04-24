using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Loads generated local occupancy maps and computes short in-room routes over walkable cells.
    /// </summary>
    internal static class LocalNavigationMaps
    {
        private const int DefaultNearestCellSearchRadius = 8;
        private static readonly Regex ScalarIndexArrayPattern = new Regex(
            "(\"(?:EnvelopeIndices|BlockedIndices)\"\\s*:\\s*)(-?\\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, ZoneMap> ZonesByName =
            new Dictionary<string, ZoneMap>(StringComparer.OrdinalIgnoreCase);

        private static bool _isInitialized;
        private static bool _isAvailable;
        private static DateTime _lastFailedLoadAttemptUtc = DateTime.MinValue;
        private static readonly TimeSpan FailedLoadRetryInterval = TimeSpan.FromSeconds(5);

        [DataContract]
        private sealed class Document
        {
            [DataMember(Name = "SchemaVersion")]
            public int SchemaVersion = 0;

            [DataMember(Name = "Zones")]
            public ZoneRecord[] Zones = null;
        }

        [DataContract]
        private sealed class ZoneRecord
        {
            [DataMember(Name = "Zone")]
            public string Zone = null;

            [DataMember(Name = "CellSize")]
            public float CellSize = 0f;

            [DataMember(Name = "Bounds2D")]
            public Bounds2DRecord Bounds2D = null;

            [DataMember(Name = "GridWidth")]
            public int GridWidth = 0;

            [DataMember(Name = "GridHeight")]
            public int GridHeight = 0;

            [DataMember(Name = "EnvelopeIndices")]
            public int[] EnvelopeIndices = null;

            [DataMember(Name = "BlockedIndices")]
            public int[] BlockedIndices = null;
        }

        [DataContract]
        private sealed class Bounds2DRecord
        {
            [DataMember(Name = "MinX")]
            public float MinX = 0f;

            [DataMember(Name = "MaxX")]
            public float MaxX = 0f;

            [DataMember(Name = "MinZ")]
            public float MinZ = 0f;

            [DataMember(Name = "MaxZ")]
            public float MaxZ = 0f;
        }

        private sealed class ZoneMap
        {
            public string Zone;
            public float CellSize;
            public float MinX;
            public float MaxX;
            public float MinZ;
            public float MaxZ;
            public int GridWidth;
            public int GridHeight;
            public bool[] Walkable;
            public int[] ComponentIds;
            public WalkableComponentSummary[] ComponentSummaries;
            public bool ComponentsComputed;
        }

        [Serializable]
        internal sealed class WalkableComponentSummary
        {
            public int ComponentId;
            public int CellCount;
            public Vector3 RepresentativeWorldPosition;
        }

        /// <summary>
        /// Gets whether the generated local map file was loaded successfully.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                Initialize();
                return _isAvailable;
            }
        }

        internal static bool TrySnapPositionToNearestWalkableCell(
            string zoneName,
            Vector3 position,
            out Vector3 snappedPosition,
            out string detail)
        {
            snappedPosition = Vector3.zero;
            detail = null;

            Initialize();
            if (!_isAvailable)
            {
                detail = "LocalNavigationMapsUnavailable";
                return false;
            }

            if (string.IsNullOrWhiteSpace(zoneName))
            {
                detail = "MissingZoneName";
                return false;
            }

            if (!ZonesByName.TryGetValue(zoneName, out ZoneMap zone))
            {
                detail = "ZoneNotFound";
                return false;
            }

            if (zone.Walkable == null || zone.Walkable.Length == 0)
            {
                detail = "ZoneHasNoWalkableCells";
                return false;
            }

            return TryFindNearestWalkableCellIndex(
                zone,
                position,
                out _,
                out snappedPosition,
                out detail);
        }

        /// <summary>
        /// Attempts to compute a walkable cell path inside the given navigation zone.
        /// </summary>
        public static bool TryFindPath(
            string zoneName,
            Vector3 startPosition,
            Vector3 targetPosition,
            out List<Vector3> pathPoints,
            out string failureReason)
        {
            pathPoints = null;
            failureReason = null;

            Initialize();
            if (!_isAvailable)
            {
                failureReason = "LocalNavigationMapsUnavailable";
                return false;
            }

            if (string.IsNullOrWhiteSpace(zoneName))
            {
                failureReason = "MissingZoneName";
                return false;
            }

            if (!ZonesByName.TryGetValue(zoneName, out ZoneMap zone))
            {
                failureReason = "ZoneNotFound";
                return false;
            }

            if (zone.Walkable == null || zone.Walkable.Length == 0)
            {
                failureReason = "ZoneHasNoWalkableCells";
                return false;
            }

            if (!TryFindNearestWalkableCellIndex(
                    zone,
                    startPosition,
                    out int startIndex,
                    out Vector3 snappedStartPosition,
                    out string startSnapDetail))
            {
                failureReason =
                    "NoWalkableStartCell zone=" + zone.Zone +
                    " detail=" + (startSnapDetail ?? "<null>");
                return false;
            }

            if (!TryFindNearestWalkableCellIndex(
                    zone,
                    targetPosition,
                    out int targetIndex,
                    out Vector3 snappedTargetPosition,
                    out string targetSnapDetail))
            {
                failureReason =
                    "NoWalkableTargetCell zone=" + zone.Zone +
                    " detail=" + (targetSnapDetail ?? "<null>");
                return false;
            }

            if (startIndex == targetIndex)
            {
                pathPoints = new List<Vector3> { snappedTargetPosition };
                return true;
            }

            int cellCount = zone.Walkable.Length;
            var gScores = new float[cellCount];
            var fScores = new float[cellCount];
            var cameFrom = new int[cellCount];
            var states = new byte[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                gScores[i] = float.PositiveInfinity;
                fScores[i] = float.PositiveInfinity;
                cameFrom[i] = -1;
            }

            var openSet = new List<int>();
            gScores[startIndex] = 0f;
            fScores[startIndex] = ComputeHeuristic(zone, startIndex, targetIndex);
            openSet.Add(startIndex);
            states[startIndex] = 1;

            bool pathFound = false;
            while (openSet.Count > 0)
            {
                int currentIndex = GetLowestScoreIndex(openSet, fScores);
                if (currentIndex == targetIndex)
                {
                    pathFound = true;
                    break;
                }

                openSet.Remove(currentIndex);
                states[currentIndex] = 2;

                GetCellCoordinates(zone, currentIndex, out int currentColumn, out int currentRow);
                for (int rowOffset = -1; rowOffset <= 1; rowOffset++)
                {
                    for (int columnOffset = -1; columnOffset <= 1; columnOffset++)
                    {
                        if (rowOffset == 0 && columnOffset == 0)
                            continue;

                        int neighborColumn = currentColumn + columnOffset;
                        int neighborRow = currentRow + rowOffset;
                        if (!TryGetIndex(zone, neighborColumn, neighborRow, out int neighborIndex))
                            continue;

                        if (!zone.Walkable[neighborIndex] || states[neighborIndex] == 2)
                            continue;

                        float traversalCost = (columnOffset != 0 && rowOffset != 0) ? 1.4142135f : 1f;
                        float tentativeGScore = gScores[currentIndex] + traversalCost;
                        if (tentativeGScore >= gScores[neighborIndex])
                            continue;

                        cameFrom[neighborIndex] = currentIndex;
                        gScores[neighborIndex] = tentativeGScore;
                        fScores[neighborIndex] = tentativeGScore + ComputeHeuristic(zone, neighborIndex, targetIndex);
                        if (states[neighborIndex] != 1)
                        {
                            openSet.Add(neighborIndex);
                            states[neighborIndex] = 1;
                        }
                    }
                }
            }

            if (!pathFound)
            {
                failureReason =
                    "NoPath zone=" + zone.Zone +
                    " start={" + (startSnapDetail ?? "<null>") + "}" +
                    " target={" + (targetSnapDetail ?? "<null>") + "}";
                return false;
            }

            pathPoints = ReconstructPath(zone, cameFrom, startIndex, targetIndex, snappedStartPosition, snappedTargetPosition);
            if (pathPoints == null || pathPoints.Count == 0)
            {
                failureReason =
                    "EmptyPath zone=" + zone.Zone +
                    " start={" + (startSnapDetail ?? "<null>") + "}" +
                    " target={" + (targetSnapDetail ?? "<null>") + "}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns the cached connected walkable components for the supplied zone.
        /// </summary>
        internal static List<WalkableComponentSummary> GetWalkableComponents(string zoneName)
        {
            Initialize();
            if (!_isAvailable || string.IsNullOrWhiteSpace(zoneName))
                return new List<WalkableComponentSummary>();

            if (!ZonesByName.TryGetValue(zoneName, out ZoneMap zone))
                return new List<WalkableComponentSummary>();

            EnsureComponentCache(zone);
            if (zone.ComponentSummaries == null || zone.ComponentSummaries.Length == 0)
                return new List<WalkableComponentSummary>();

            return new List<WalkableComponentSummary>(zone.ComponentSummaries);
        }

        /// <summary>
        /// Resolves the walkable component at a world position after snapping the position to the nearest walkable cell.
        /// </summary>
        internal static bool TryGetWalkableComponentId(
            string zoneName,
            Vector3 position,
            out int componentId,
            out Vector3 snappedPosition,
            out string detail)
        {
            componentId = -1;
            snappedPosition = Vector3.zero;
            detail = null;

            Initialize();
            if (!_isAvailable)
            {
                detail = "LocalNavigationMapsUnavailable";
                return false;
            }

            if (string.IsNullOrWhiteSpace(zoneName))
            {
                detail = "MissingZoneName";
                return false;
            }

            if (!ZonesByName.TryGetValue(zoneName, out ZoneMap zone))
            {
                detail = "ZoneNotFound";
                return false;
            }

            if (!TryFindNearestWalkableCellIndex(
                    zone,
                    position,
                    out int cellIndex,
                    out snappedPosition,
                    out string snapDetail))
            {
                detail = "SnapFailed " + (snapDetail ?? "<null>");
                return false;
            }

            EnsureComponentCache(zone);
            if (zone.ComponentIds == null ||
                cellIndex < 0 ||
                cellIndex >= zone.ComponentIds.Length)
            {
                detail = "ComponentCacheUnavailable";
                return false;
            }

            componentId = zone.ComponentIds[cellIndex];
            if (componentId < 0)
            {
                detail = "ComponentNotAssigned";
                return false;
            }

            detail = "snap=" + snapDetail + " componentId=" + componentId;
            return true;
        }

        /// <summary>
        /// Returns whether the snapped world position belongs to the specified connected walkable component.
        /// </summary>
        internal static bool IsWorldPositionInComponent(
            string zoneName,
            Vector3 position,
            int componentId,
            out Vector3 snappedPosition,
            out string detail)
        {
            snappedPosition = Vector3.zero;
            detail = null;

            if (componentId < 0)
            {
                detail = "InvalidComponentId";
                return false;
            }

            if (!TryGetWalkableComponentId(
                    zoneName,
                    position,
                    out int resolvedComponentId,
                    out snappedPosition,
                    out string resolvedDetail))
            {
                detail = resolvedDetail;
                return false;
            }

            detail = resolvedDetail + " requestedComponentId=" + componentId + " resolvedComponentId=" + resolvedComponentId;
            return resolvedComponentId == componentId;
        }

        /// <summary>
        /// Resolves a walkable approach target while constraining candidates to a specific connected component.
        /// </summary>
        internal static bool TryResolveApproachTargetForComponent(
            string zoneName,
            Vector3 playerPosition,
            Vector3 referencePosition,
            List<Vector3> candidateTargets,
            int preferredComponentId,
            out Vector3 targetPosition,
            out string detail)
        {
            targetPosition = Vector3.zero;
            detail = null;

            Initialize();
            if (!_isAvailable)
            {
                detail = "LocalNavigationMapsUnavailable";
                return false;
            }

            if (string.IsNullOrWhiteSpace(zoneName))
            {
                detail = "MissingZoneName";
                return false;
            }

            if (candidateTargets == null || candidateTargets.Count == 0)
            {
                detail = "NoCandidateTargets";
                return false;
            }

            if (!ZonesByName.TryGetValue(zoneName, out ZoneMap zone))
            {
                detail = "ZoneNotFound";
                return false;
            }

            if (zone.Walkable == null || zone.Walkable.Length == 0)
            {
                detail = "ZoneHasNoWalkableCells";
                return false;
            }

            bool foundCandidate = false;
            float bestPathDistance = float.PositiveInfinity;
            float bestReferenceDistance = float.PositiveInfinity;
            int bestComponentId = -1;
            string bestComponentDetail = null;

            for (int i = 0; i < candidateTargets.Count; i++)
            {
                Vector3 candidateTarget = candidateTargets[i];
                if (!TryGetWalkableComponentId(
                        zoneName,
                        candidateTarget,
                        out int candidateComponentId,
                        out Vector3 snappedCandidateTarget,
                        out string componentDetail))
                {
                    continue;
                }

                if (preferredComponentId >= 0 && candidateComponentId != preferredComponentId)
                    continue;

                if (!TryFindPath(
                        zoneName,
                        playerPosition,
                        snappedCandidateTarget,
                        out List<Vector3> pathPoints,
                        out _ ) ||
                    pathPoints == null ||
                    pathPoints.Count == 0)
                {
                    continue;
                }

                float pathDistance = ComputePathDistance(playerPosition, pathPoints);
                float referenceDistance = ComputeFlatDistance(snappedCandidateTarget, referencePosition);
                if (foundCandidate &&
                    pathDistance > bestPathDistance + 0.01f)
                {
                    continue;
                }

                if (foundCandidate &&
                    Mathf.Abs(pathDistance - bestPathDistance) <= 0.01f &&
                    referenceDistance >= bestReferenceDistance)
                {
                    continue;
                }

                foundCandidate = true;
                bestPathDistance = pathDistance;
                bestReferenceDistance = referenceDistance;
                bestComponentId = candidateComponentId;
                bestComponentDetail = componentDetail;
                targetPosition = snappedCandidateTarget;
            }

            if (!foundCandidate)
            {
                detail = "mode=component-path-selected preferredComponentId=" + preferredComponentId + " resolution=failed";
                return false;
            }

            targetPosition.y = playerPosition.y;
            detail =
                "mode=component-path-selected" +
                " componentId=" + bestComponentId +
                " componentDetail=" + (bestComponentDetail ?? "<null>") +
                " pathDistance=" + bestPathDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " referenceDistance=" + bestReferenceDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " preferredComponentId=" + preferredComponentId;
            return true;
        }

        private static void Initialize()
        {
            if (_isInitialized)
            {
                if (_isAvailable)
                    return;

                if (_lastFailedLoadAttemptUtc > DateTime.MinValue &&
                    DateTime.UtcNow - _lastFailedLoadAttemptUtc < FailedLoadRetryInterval)
                {
                    return;
                }
            }

            lock (SyncRoot)
            {
                if (_isInitialized)
                {
                    if (_isAvailable)
                        return;

                    if (_lastFailedLoadAttemptUtc > DateTime.MinValue &&
                        DateTime.UtcNow - _lastFailedLoadAttemptUtc < FailedLoadRetryInterval)
                    {
                        return;
                    }
                }

                _isInitialized = true;
                _lastFailedLoadAttemptUtc = DateTime.UtcNow;
                _isAvailable = false;
                ZonesByName.Clear();

                try
                {
                    string jsonPath = Path.Combine(Paths.PluginPath, "local_navigation_maps.generated.json");
                    if (!File.Exists(jsonPath))
                    {
                        Main.Log?.LogWarning("Local navigation maps file not found: " + jsonPath);
                        return;
                    }

                    int normalizedScalarArrayCount = 0;
                    string json = NormalizeScalarIndexArrays(File.ReadAllText(jsonPath), out normalizedScalarArrayCount);
                    if (normalizedScalarArrayCount > 0)
                    {
                        Main.Log?.LogWarning(
                            "Normalized malformed local navigation map index arrays count=" +
                            normalizedScalarArrayCount +
                            " path=" + jsonPath);
                    }

                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(Document));
                        Document document = serializer.ReadObject(stream) as Document;
                        if (document == null || document.Zones == null || document.Zones.Length == 0)
                        {
                            Main.Log?.LogWarning("Local navigation maps file did not contain any zones: " + jsonPath);
                            return;
                        }

                        for (int i = 0; i < document.Zones.Length; i++)
                        {
                            ZoneMap zone = BuildZoneMap(document.Zones[i]);
                            if (zone == null || string.IsNullOrWhiteSpace(zone.Zone))
                                continue;

                            ZonesByName[zone.Zone] = zone;
                        }
                    }

                    foreach (ZoneMap zone in ZonesByName.Values)
                        EnsureComponentCache(zone);

                    _isAvailable = ZonesByName.Count > 0;
                    Main.Log?.LogInfo(
                        "Loaded local navigation maps zones=" + ZonesByName.Count +
                        " path=" + Path.Combine(Paths.PluginPath, "local_navigation_maps.generated.json"));
                    if (_isAvailable)
                        _lastFailedLoadAttemptUtc = DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    Main.Log?.LogError("Failed to load local navigation maps: " + ex);
                    ZonesByName.Clear();
                    _isAvailable = false;
                }
            }
        }

        private static string NormalizeScalarIndexArrays(string json, out int replacementCount)
        {
            int normalizedCount = 0;
            if (string.IsNullOrEmpty(json))
            {
                replacementCount = 0;
                return json;
            }

            string normalizedJson = ScalarIndexArrayPattern.Replace(
                json,
                match =>
                {
                    normalizedCount++;
                    return match.Groups[1].Value + "[" + match.Groups[2].Value + "]";
                });
            replacementCount = normalizedCount;
            return normalizedJson;
        }

        private static ZoneMap BuildZoneMap(ZoneRecord record)
        {
            if (record == null ||
                string.IsNullOrWhiteSpace(record.Zone) ||
                record.Bounds2D == null ||
                record.GridWidth <= 0 ||
                record.GridHeight <= 0 ||
                record.CellSize <= 0f)
            {
                return null;
            }

            int cellCount = record.GridWidth * record.GridHeight;
            if (cellCount <= 0)
                return null;

            var walkable = new bool[cellCount];
            if (record.EnvelopeIndices != null)
            {
                for (int i = 0; i < record.EnvelopeIndices.Length; i++)
                {
                    int index = record.EnvelopeIndices[i];
                    if (index >= 0 && index < cellCount)
                        walkable[index] = true;
                }
            }

            if (record.BlockedIndices != null)
            {
                for (int i = 0; i < record.BlockedIndices.Length; i++)
                {
                    int index = record.BlockedIndices[i];
                    if (index >= 0 && index < cellCount)
                        walkable[index] = false;
                }
            }

            return new ZoneMap
            {
                Zone = record.Zone,
                CellSize = record.CellSize,
                MinX = record.Bounds2D.MinX,
                MaxX = record.Bounds2D.MaxX,
                MinZ = record.Bounds2D.MinZ,
                MaxZ = record.Bounds2D.MaxZ,
                GridWidth = record.GridWidth,
                GridHeight = record.GridHeight,
                Walkable = walkable,
                ComponentIds = null,
                ComponentSummaries = null,
                ComponentsComputed = false
            };
        }

        private static void EnsureComponentCache(ZoneMap zone)
        {
            if (zone == null || zone.ComponentsComputed)
                return;

            lock (SyncRoot)
            {
                if (zone == null || zone.ComponentsComputed)
                    return;

                BuildComponentCache(zone);
                zone.ComponentsComputed = true;
            }
        }

        private static void BuildComponentCache(ZoneMap zone)
        {
            if (zone == null)
                return;

            if (zone.Walkable == null)
            {
                zone.ComponentIds = Array.Empty<int>();
                zone.ComponentSummaries = Array.Empty<WalkableComponentSummary>();
                return;
            }

            int cellCount = zone.Walkable.Length;
            zone.ComponentIds = new int[cellCount];
            for (int i = 0; i < cellCount; i++)
                zone.ComponentIds[i] = -1;

            var summaries = new List<WalkableComponentSummary>();
            var queue = new Queue<int>();

            for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                if (!zone.Walkable[cellIndex] || zone.ComponentIds[cellIndex] >= 0)
                    continue;

                int componentId = summaries.Count;
                int componentCellCount = 0;
                Vector3 representativePosition = Vector3.zero;
                bool hasRepresentative = false;

                zone.ComponentIds[cellIndex] = componentId;
                queue.Enqueue(cellIndex);

                while (queue.Count > 0)
                {
                    int currentIndex = queue.Dequeue();
                    componentCellCount++;
                    if (!hasRepresentative)
                    {
                        representativePosition = GetCellCenter(zone, currentIndex, 0f);
                        hasRepresentative = true;
                    }

                    GetCellCoordinates(zone, currentIndex, out int currentColumn, out int currentRow);
                    for (int rowOffset = -1; rowOffset <= 1; rowOffset++)
                    {
                        for (int columnOffset = -1; columnOffset <= 1; columnOffset++)
                        {
                            if (rowOffset == 0 && columnOffset == 0)
                                continue;

                            int neighborColumn = currentColumn + columnOffset;
                            int neighborRow = currentRow + rowOffset;
                            if (!TryGetIndex(zone, neighborColumn, neighborRow, out int neighborIndex))
                                continue;

                            if (!zone.Walkable[neighborIndex] || zone.ComponentIds[neighborIndex] >= 0)
                                continue;

                            zone.ComponentIds[neighborIndex] = componentId;
                            queue.Enqueue(neighborIndex);
                        }
                    }
                }

                summaries.Add(new WalkableComponentSummary
                {
                    ComponentId = componentId,
                    CellCount = componentCellCount,
                    RepresentativeWorldPosition = representativePosition
                });
            }

            zone.ComponentSummaries = summaries.ToArray();
        }

        private static bool TryFindNearestWalkableCellIndex(
            ZoneMap zone,
            Vector3 position,
            out int index,
            out Vector3 snappedPosition,
            out string detail)
        {
            index = -1;
            snappedPosition = Vector3.zero;
            detail = null;
            if (zone == null)
                return false;

            int approximateColumn = Mathf.Clamp(
                Mathf.FloorToInt((position.x - zone.MinX) / zone.CellSize),
                0,
                zone.GridWidth - 1);
            int approximateRow = Mathf.Clamp(
                Mathf.FloorToInt((position.z - zone.MinZ) / zone.CellSize),
                0,
                zone.GridHeight - 1);

            if (TryGetIndex(zone, approximateColumn, approximateRow, out int approximateIndex) &&
                zone.Walkable[approximateIndex])
            {
                index = approximateIndex;
                snappedPosition = GetCellCenter(zone, approximateIndex, position.y);
                detail = BuildCellSnapDetail(
                    zone,
                    approximateIndex,
                    position,
                    snappedPosition,
                    approximateColumn,
                    approximateRow,
                    "exact",
                    0);
                return true;
            }

            if (TryFindNearestWalkableCellIndexInRadius(
                    zone,
                    position,
                    approximateColumn,
                    approximateRow,
                    DefaultNearestCellSearchRadius,
                    out index,
                    out snappedPosition,
                    out int radiusUsed))
            {
                detail = BuildCellSnapDetail(
                    zone,
                    index,
                    position,
                    snappedPosition,
                    approximateColumn,
                    approximateRow,
                    "radius",
                    radiusUsed);
                return true;
            }

            if (TryFindNearestWalkableCellIndexInWholeZone(zone, position, out index, out snappedPosition))
            {
                detail = BuildCellSnapDetail(
                    zone,
                    index,
                    position,
                    snappedPosition,
                    approximateColumn,
                    approximateRow,
                    "full-zone",
                    -1);
                return true;
            }

            detail =
                "position=" + FormatPosition(position) +
                " approxCell=(" + approximateColumn + "," + approximateRow + ")" +
                " searchRadius=" + DefaultNearestCellSearchRadius;
            return false;
        }

        private static bool TryFindNearestWalkableCellIndexInRadius(
            ZoneMap zone,
            Vector3 position,
            int approximateColumn,
            int approximateRow,
            int maximumRadius,
            out int index,
            out Vector3 snappedPosition,
            out int radiusUsed)
        {
            index = -1;
            snappedPosition = Vector3.zero;
            radiusUsed = -1;
            if (zone == null)
                return false;

            float bestDistanceSquared = float.PositiveInfinity;
            for (int radius = 1; radius <= maximumRadius; radius++)
            {
                bool foundAtRadius = false;
                int minimumColumn = Mathf.Max(0, approximateColumn - radius);
                int maximumColumn = Mathf.Min(zone.GridWidth - 1, approximateColumn + radius);
                int minimumRow = Mathf.Max(0, approximateRow - radius);
                int maximumRow = Mathf.Min(zone.GridHeight - 1, approximateRow + radius);

                for (int row = minimumRow; row <= maximumRow; row++)
                {
                    for (int column = minimumColumn; column <= maximumColumn; column++)
                    {
                        if (column > minimumColumn &&
                            column < maximumColumn &&
                            row > minimumRow &&
                            row < maximumRow)
                        {
                            continue;
                        }

                        if (!TryGetIndex(zone, column, row, out int candidateIndex) || !zone.Walkable[candidateIndex])
                            continue;

                        Vector3 candidatePosition = GetCellCenter(zone, candidateIndex, position.y);
                        Vector3 offset = candidatePosition - position;
                        offset.y = 0f;
                        float candidateDistanceSquared = offset.sqrMagnitude;
                        if (candidateDistanceSquared >= bestDistanceSquared)
                            continue;

                        bestDistanceSquared = candidateDistanceSquared;
                        index = candidateIndex;
                        snappedPosition = candidatePosition;
                        radiusUsed = radius;
                        foundAtRadius = true;
                    }
                }

                if (foundAtRadius)
                    return true;
            }

            return false;
        }

        private static bool TryFindNearestWalkableCellIndexInWholeZone(
            ZoneMap zone,
            Vector3 position,
            out int index,
            out Vector3 snappedPosition)
        {
            index = -1;
            snappedPosition = Vector3.zero;
            if (zone == null || zone.Walkable == null)
                return false;

            float bestDistanceSquared = float.PositiveInfinity;
            for (int candidateIndex = 0; candidateIndex < zone.Walkable.Length; candidateIndex++)
            {
                if (!zone.Walkable[candidateIndex])
                    continue;

                Vector3 candidatePosition = GetCellCenter(zone, candidateIndex, position.y);
                Vector3 offset = candidatePosition - position;
                offset.y = 0f;
                float candidateDistanceSquared = offset.sqrMagnitude;
                if (candidateDistanceSquared >= bestDistanceSquared)
                    continue;

                bestDistanceSquared = candidateDistanceSquared;
                index = candidateIndex;
                snappedPosition = candidatePosition;
            }

            return index >= 0;
        }

        private static string BuildCellSnapDetail(
            ZoneMap zone,
            int cellIndex,
            Vector3 originalPosition,
            Vector3 snappedPosition,
            int approximateColumn,
            int approximateRow,
            string mode,
            int radius)
        {
            if (zone == null || cellIndex < 0)
                return "mode=" + (mode ?? "unknown");

            GetCellCoordinates(zone, cellIndex, out int column, out int row);
            Vector3 flatOffset = snappedPosition - originalPosition;
            flatOffset.y = 0f;
            string detail =
                "mode=" + (mode ?? "unknown") +
                " snappedCell=(" + column + "," + row + ")" +
                " approxCell=(" + approximateColumn + "," + approximateRow + ")" +
                " distance=" + Mathf.Sqrt(flatOffset.sqrMagnitude).ToString("0.00", CultureInfo.InvariantCulture) +
                " original=" + FormatPosition(originalPosition) +
                " snapped=" + FormatPosition(snappedPosition);
            if (radius >= 0)
                detail += " radius=" + radius;

            return detail;
        }

        private static string FormatPosition(Vector3 position)
        {
            return "(" +
                position.x.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                position.y.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                position.z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        private static List<Vector3> ReconstructPath(
            ZoneMap zone,
            int[] cameFrom,
            int startIndex,
            int targetIndex,
            Vector3 snappedStartPosition,
            Vector3 snappedTargetPosition)
        {
            var reversed = new List<Vector3>();
            int currentIndex = targetIndex;
            while (currentIndex >= 0)
            {
                Vector3 currentPosition = currentIndex == targetIndex
                    ? snappedTargetPosition
                    : currentIndex == startIndex
                        ? snappedStartPosition
                        : GetCellCenter(zone, currentIndex, snappedTargetPosition.y);
                reversed.Add(currentPosition);

                if (currentIndex == startIndex)
                    break;

                currentIndex = cameFrom[currentIndex];
            }

            if (reversed.Count == 0 || currentIndex != startIndex)
                return null;

            reversed.Reverse();
            return CompressCollinearPoints(reversed);
        }

        private static List<Vector3> CompressCollinearPoints(List<Vector3> points)
        {
            if (points == null || points.Count < 3)
                return points;

            var compressed = new List<Vector3>(points.Count);
            compressed.Add(points[0]);

            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector3 previous = compressed[compressed.Count - 1];
                Vector3 current = points[i];
                Vector3 next = points[i + 1];

                Vector3 previousDirection = current - previous;
                previousDirection.y = 0f;
                Vector3 nextDirection = next - current;
                nextDirection.y = 0f;

                if (previousDirection.sqrMagnitude <= 0.0001f || nextDirection.sqrMagnitude <= 0.0001f)
                {
                    compressed.Add(current);
                    continue;
                }

                previousDirection.Normalize();
                nextDirection.Normalize();
                if (Vector3.Dot(previousDirection, nextDirection) < 0.999f)
                    compressed.Add(current);
            }

            compressed.Add(points[points.Count - 1]);
            return compressed;
        }

        private static int GetLowestScoreIndex(List<int> openSet, float[] fScores)
        {
            int bestIndex = openSet[0];
            float bestScore = fScores[bestIndex];
            for (int i = 1; i < openSet.Count; i++)
            {
                int candidateIndex = openSet[i];
                float candidateScore = fScores[candidateIndex];
                if (candidateScore < bestScore)
                {
                    bestIndex = candidateIndex;
                    bestScore = candidateScore;
                }
            }

            return bestIndex;
        }

        private static float ComputeHeuristic(ZoneMap zone, int fromIndex, int toIndex)
        {
            GetCellCoordinates(zone, fromIndex, out int fromColumn, out int fromRow);
            GetCellCoordinates(zone, toIndex, out int toColumn, out int toRow);
            int columnDistance = Math.Abs(toColumn - fromColumn);
            int rowDistance = Math.Abs(toRow - fromRow);
            return Mathf.Sqrt(columnDistance * columnDistance + rowDistance * rowDistance);
        }

        private static bool TryGetIndex(ZoneMap zone, int column, int row, out int index)
        {
            index = -1;
            if (zone == null ||
                column < 0 ||
                row < 0 ||
                column >= zone.GridWidth ||
                row >= zone.GridHeight)
            {
                return false;
            }

            index = row * zone.GridWidth + column;
            return true;
        }

        private static void GetCellCoordinates(ZoneMap zone, int index, out int column, out int row)
        {
            row = index / zone.GridWidth;
            column = index % zone.GridWidth;
        }

        private static Vector3 GetCellCenter(ZoneMap zone, int index, float y)
        {
            GetCellCoordinates(zone, index, out int column, out int row);
            return new Vector3(
                zone.MinX + column * zone.CellSize + zone.CellSize * 0.5f,
                y,
                zone.MinZ + row * zone.CellSize + zone.CellSize * 0.5f);
        }

        private static float ComputePathDistance(Vector3 startPosition, List<Vector3> pathPoints)
        {
            if (pathPoints == null || pathPoints.Count == 0)
                return 0f;

            float distance = 0f;
            Vector3 previousPoint = startPosition;
            previousPoint.y = 0f;
            for (int i = 0; i < pathPoints.Count; i++)
            {
                Vector3 currentPoint = pathPoints[i];
                currentPoint.y = 0f;
                distance += Vector3.Distance(previousPoint, currentPoint);
                previousPoint = currentPoint;
            }

            return distance;
        }

        private static float ComputeFlatDistance(Vector3 left, Vector3 right)
        {
            left.y = 0f;
            right.y = 0f;
            return Vector3.Distance(left, right);
        }
    }
}
