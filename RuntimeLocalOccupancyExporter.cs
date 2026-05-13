using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DateEverythingAccess
{
    internal static class RuntimeLocalOccupancyExporter
    {
        private const float GroundColliderIgnoreHeight = 0.15f;

        [DataContract]
        private sealed class RuntimeLocalOccupancyDocument
        {
            [DataMember(Name = "SchemaVersion")]
            public int SchemaVersion;
            [DataMember(Name = "GeneratedAtUtc")]
            public string GeneratedAtUtc;
            [DataMember(Name = "ActiveScene")]
            public string ActiveScene;
            [DataMember(Name = "LoadedScenes")]
            public string[] LoadedScenes;
            [DataMember(Name = "PluginVersion")]
            public string PluginVersion;
            [DataMember(Name = "RuntimeBuildStamp")]
            public string RuntimeBuildStamp;
            [DataMember(Name = "Source")]
            public string Source;
            [DataMember(Name = "PlayerShape")]
            public PlayerShapeData PlayerShape;
            [DataMember(Name = "ZoneCount")]
            public int ZoneCount;
            [DataMember(Name = "EnvelopeCellCount")]
            public int EnvelopeCellCount;
            [DataMember(Name = "BlockedCellCount")]
            public int BlockedCellCount;
            [DataMember(Name = "Zones")]
            public ZoneData[] Zones;
        }

        [DataContract]
        private sealed class PlayerShapeData
        {
            [DataMember(Name = "ColliderType")]
            public string ColliderType;
            [DataMember(Name = "PlayerPosition")]
            public Vector3Data PlayerPosition;
            [DataMember(Name = "CapsuleRadius")]
            public float CapsuleRadius;
            [DataMember(Name = "CapsuleHeight")]
            public float CapsuleHeight;
            [DataMember(Name = "CapsulePointA")]
            public Vector3Data CapsulePointA;
            [DataMember(Name = "CapsulePointB")]
            public Vector3Data CapsulePointB;
            [DataMember(Name = "Bounds")]
            public BoundsData Bounds;
        }

        [DataContract]
        private sealed class Vector3Data
        {
            [DataMember(Name = "x")]
            public float x;
            [DataMember(Name = "y")]
            public float y;
            [DataMember(Name = "z")]
            public float z;
        }

        [DataContract]
        private sealed class BoundsData
        {
            [DataMember(Name = "Min")]
            public Vector3Data Min;
            [DataMember(Name = "Max")]
            public Vector3Data Max;
            [DataMember(Name = "Center")]
            public Vector3Data Center;
            [DataMember(Name = "Size")]
            public Vector3Data Size;
        }

        [DataContract]
        private sealed class Bounds2DData
        {
            [DataMember(Name = "MinX")]
            public float MinX;
            [DataMember(Name = "MaxX")]
            public float MaxX;
            [DataMember(Name = "MinZ")]
            public float MinZ;
            [DataMember(Name = "MaxZ")]
            public float MaxZ;
            [DataMember(Name = "Width")]
            public float Width;
            [DataMember(Name = "Depth")]
            public float Depth;
        }

        [DataContract]
        private sealed class ZoneData
        {
            [DataMember(Name = "Zone")]
            public string Zone;
            [DataMember(Name = "CellSize")]
            public float CellSize;
            [DataMember(Name = "Bounds2D")]
            public Bounds2DData Bounds2D;
            [DataMember(Name = "GridWidth")]
            public int GridWidth;
            [DataMember(Name = "GridHeight")]
            public int GridHeight;
            [DataMember(Name = "EnvelopeCellCount")]
            public int EnvelopeCellCount;
            [DataMember(Name = "BlockedCellCount")]
            public int BlockedCellCount;
            [DataMember(Name = "WalkableCellCount")]
            public int WalkableCellCount;
            [DataMember(Name = "SampleY")]
            public float SampleY;
            [DataMember(Name = "EnvelopeIndices")]
            public int[] EnvelopeIndices;
            [DataMember(Name = "BlockedIndices")]
            public int[] BlockedIndices;
            [DataMember(Name = "BlockedCellDetails")]
            public BlockedCellData[] BlockedCellDetails;
            [DataMember(Name = "Notes")]
            public string[] Notes;
        }

        [DataContract]
        private sealed class BlockedCellData
        {
            [DataMember(Name = "Index")]
            public int Index;
            [DataMember(Name = "Column")]
            public int Column;
            [DataMember(Name = "Row")]
            public int Row;
            [DataMember(Name = "WorldPosition")]
            public Vector3Data WorldPosition;
            [DataMember(Name = "FirstBlocker")]
            public BlockerData FirstBlocker;
        }

        [DataContract]
        private sealed class BlockerData
        {
            [DataMember(Name = "ObjectPath")]
            public string ObjectPath;
            [DataMember(Name = "Name")]
            public string Name;
            [DataMember(Name = "ColliderType")]
            public string ColliderType;
            [DataMember(Name = "IsTrigger")]
            public bool IsTrigger;
            [DataMember(Name = "Layer")]
            public int Layer;
            [DataMember(Name = "LayerName")]
            public string LayerName;
            [DataMember(Name = "Bounds")]
            public BoundsData Bounds;
        }

        internal enum ExportFailure
        {
            None,
            LocalMapsUnavailable,
            PlayerUnavailable,
            UnsupportedPlayerCollider,
            WriteFailed,
            UnexpectedError
        }

        internal static bool TryExport(out string outputPath, out int zoneCount, out int blockedCellCount, out ExportFailure failure)
        {
            outputPath = Path.Combine(Paths.PluginPath, "local_navigation_maps.physics_sampled.live.json");
            zoneCount = 0;
            blockedCellCount = 0;
            failure = ExportFailure.None;

            try
            {
                List<LocalNavigationMaps.SamplingZoneDefinition> zones = LocalNavigationMaps.GetSamplingZoneDefinitions();
                if (zones == null || zones.Count == 0)
                {
                    failure = ExportFailure.LocalMapsUnavailable;
                    return false;
                }

                BetterPlayerControl player = BetterPlayerControl.Instance;
                if (player == null)
                {
                    failure = ExportFailure.PlayerUnavailable;
                    return false;
                }

                Collider playerCollider = player.GetComponent<Collider>();
                if (!TryGetPlayerCapsule(playerCollider, out PlayerCapsule playerCapsule))
                {
                    failure = ExportFailure.UnsupportedPlayerCollider;
                    return false;
                }

                Dictionary<string, float> zoneYByName = EstimateZoneSampleHeights(player.transform.position.y);
                var zoneOutput = new List<ZoneData>(zones.Count);
                int envelopeCells = 0;

                for (int i = 0; i < zones.Count; i++)
                {
                    ZoneData zoneData = BuildZoneData(zones[i], zoneYByName, player.transform, playerCollider, playerCapsule);
                    if (zoneData == null)
                        continue;

                    zoneOutput.Add(zoneData);
                    envelopeCells += zoneData.EnvelopeCellCount;
                    blockedCellCount += zoneData.BlockedCellCount;
                }

                zoneCount = zoneOutput.Count;
                var document = new RuntimeLocalOccupancyDocument
                {
                    SchemaVersion = 1,
                    GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
                    ActiveScene = SceneManager.GetActiveScene().name,
                    LoadedScenes = GetLoadedScenes(),
                    PluginVersion = Main.GetPluginVersion(),
                    RuntimeBuildStamp = Main.GetRuntimeBuildStamp(),
                    Source = "RuntimePhysicsPlayerCapsule",
                    PlayerShape = BuildPlayerShapeData(playerCollider, playerCapsule),
                    ZoneCount = zoneCount,
                    EnvelopeCellCount = envelopeCells,
                    BlockedCellCount = blockedCellCount,
                    Zones = zoneOutput.ToArray()
                };

                Directory.CreateDirectory(Paths.PluginPath);
                WriteJsonAtomically(outputPath, document);
                Main.Log.LogInfo(
                    "Runtime physics-sampled local occupancy export completed path=" + outputPath +
                    " zones=" + zoneCount.ToString(CultureInfo.InvariantCulture) +
                    " blockedCells=" + blockedCellCount.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (IOException ex)
            {
                failure = ExportFailure.WriteFailed;
                Main.Log.LogError("Failed to write runtime physics-sampled local occupancy export: " + ex);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                failure = ExportFailure.WriteFailed;
                Main.Log.LogError("Failed to write runtime physics-sampled local occupancy export: " + ex);
                return false;
            }
            catch (Exception ex)
            {
                failure = ExportFailure.UnexpectedError;
                Main.Log.LogError("Unexpected runtime physics-sampled local occupancy export failure: " + ex);
                return false;
            }
        }

        private static void WriteJsonAtomically(string outputPath, RuntimeLocalOccupancyDocument document)
        {
            if (!IsValidExportDocument(document, out string validationDetail))
                throw new IOException("Runtime physics-sampled local occupancy export validation failed before write: " + validationDetail);

            string temporaryPath = outputPath + ".tmp";
            WriteJson(temporaryPath, document);
            RuntimeLocalOccupancyDocument roundTrip = ReadJson(temporaryPath);
            if (!IsValidExportDocument(roundTrip, out string roundTripDetail))
                throw new IOException("Runtime physics-sampled local occupancy export validation failed after write: " + roundTripDetail);

            File.Copy(temporaryPath, outputPath, overwrite: true);
            File.Delete(temporaryPath);
        }

        private static void WriteJson(string outputPath, RuntimeLocalOccupancyDocument document)
        {
            var serializer = new DataContractJsonSerializer(typeof(RuntimeLocalOccupancyDocument));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, document);
                File.WriteAllText(outputPath, Encoding.UTF8.GetString(stream.ToArray()));
            }
        }

        private static RuntimeLocalOccupancyDocument ReadJson(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(RuntimeLocalOccupancyDocument));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                return serializer.ReadObject(stream) as RuntimeLocalOccupancyDocument;
        }

        private static bool IsValidExportDocument(RuntimeLocalOccupancyDocument document, out string detail)
        {
            detail = null;
            if (document == null)
            {
                detail = "document-null";
                return false;
            }

            if (document.Zones == null || document.Zones.Length == 0)
            {
                detail = "missing-zones";
                return false;
            }

            if (document.ZoneCount != document.Zones.Length)
            {
                detail = "zone-count-mismatch expected=" + document.ZoneCount + " actual=" + document.Zones.Length;
                return false;
            }

            bool hasEnvelope = false;
            for (int i = 0; i < document.Zones.Length; i++)
            {
                ZoneData zone = document.Zones[i];
                if (zone == null || string.IsNullOrWhiteSpace(zone.Zone))
                    continue;

                if (zone.EnvelopeIndices != null && zone.EnvelopeIndices.Length > 0)
                    hasEnvelope = true;
            }

            if (!hasEnvelope)
            {
                detail = "missing-envelope-indices";
                return false;
            }

            detail = "ok";
            return true;
        }

        private static ZoneData BuildZoneData(
            LocalNavigationMaps.SamplingZoneDefinition zone,
            Dictionary<string, float> zoneYByName,
            Transform playerTransform,
            Collider playerCollider,
            PlayerCapsule playerCapsule)
        {
            if (zone == null || string.IsNullOrWhiteSpace(zone.Zone) || zone.EnvelopeIndices == null)
                return null;

            float sampleY = playerTransform.position.y;
            if (!zoneYByName.TryGetValue(zone.Zone, out sampleY))
                sampleY = playerTransform.position.y;

            var blockedIndices = new List<int>();
            var blockedDetails = new List<BlockedCellData>();
            for (int i = 0; i < zone.EnvelopeIndices.Length; i++)
            {
                int index = zone.EnvelopeIndices[i];
                if (index < 0 || index >= zone.GridWidth * zone.GridHeight)
                    continue;

                int row = index / zone.GridWidth;
                int column = index % zone.GridWidth;
                Vector3 samplePosition = new Vector3(
                    zone.MinX + column * zone.CellSize + zone.CellSize * 0.5f,
                    sampleY,
                    zone.MinZ + row * zone.CellSize + zone.CellSize * 0.5f);

                if (!TryFindBlockingCollider(samplePosition, playerTransform, playerCollider, playerCapsule, out Collider blocker))
                    continue;

                blockedIndices.Add(index);
                blockedDetails.Add(new BlockedCellData
                {
                    Index = index,
                    Column = column,
                    Row = row,
                    WorldPosition = ConvertVector3(samplePosition),
                    FirstBlocker = BuildBlockerData(blocker)
                });
            }

            return new ZoneData
            {
                Zone = zone.Zone,
                CellSize = zone.CellSize,
                Bounds2D = new Bounds2DData
                {
                    MinX = zone.MinX,
                    MaxX = zone.MaxX,
                    MinZ = zone.MinZ,
                    MaxZ = zone.MaxZ,
                    Width = zone.MaxX - zone.MinX,
                    Depth = zone.MaxZ - zone.MinZ
                },
                GridWidth = zone.GridWidth,
                GridHeight = zone.GridHeight,
                EnvelopeCellCount = zone.EnvelopeIndices.Length,
                BlockedCellCount = blockedIndices.Count,
                WalkableCellCount = zone.EnvelopeIndices.Length - blockedIndices.Count,
                SampleY = sampleY,
                EnvelopeIndices = zone.EnvelopeIndices,
                BlockedIndices = blockedIndices.ToArray(),
                BlockedCellDetails = blockedDetails.ToArray(),
                Notes = new[]
                {
                    "RuntimePhysicsSampled",
                    "Sampled with live player capsule shape through Unity Physics.OverlapCapsule.",
                    "Ground/support colliders below the player foot band are ignored; non-trigger side blockers are retained."
                }
            };
        }

        private static bool TryFindBlockingCollider(
            Vector3 samplePosition,
            Transform playerTransform,
            Collider playerCollider,
            PlayerCapsule playerCapsule,
            out Collider blocker)
        {
            blocker = null;
            Vector3 delta = samplePosition - playerCapsule.PlayerPosition;
            Vector3 pointA = playerCapsule.PointA + delta;
            Vector3 pointB = playerCapsule.PointB + delta;
            Collider[] overlaps = Physics.OverlapCapsule(
                pointA,
                pointB,
                playerCapsule.Radius,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            if (overlaps == null || overlaps.Length == 0)
                return false;

            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider candidate = overlaps[i];
                if (!IsBlockingCollider(candidate, playerTransform, playerCollider, samplePosition))
                    continue;

                Vector3 closestPoint = candidate.ClosestPoint(samplePosition);
                float distance = GetFlatDistance(samplePosition, closestPoint);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                blocker = candidate;
            }

            return blocker != null;
        }

        private static bool IsBlockingCollider(Collider collider, Transform playerTransform, Collider playerCollider, Vector3 samplePosition)
        {
            if (collider == null || !collider.enabled || collider.isTrigger)
                return false;

            if (collider == playerCollider)
                return false;

            if (playerTransform != null &&
                (collider.transform == playerTransform || collider.transform.IsChildOf(playerTransform)))
            {
                return false;
            }

            if (collider.bounds.max.y <= samplePosition.y + GroundColliderIgnoreHeight)
                return false;

            string objectPath = GetGameObjectPath(collider.gameObject);
            if (IsCeilingCollider(collider, objectPath))
                return false;

            return true;
        }

        private static bool IsCeilingCollider(Collider collider, string objectPath)
        {
            if (collider == null)
                return false;

            string name = collider.gameObject != null ? collider.gameObject.name : string.Empty;
            if (!string.IsNullOrEmpty(name) &&
                name.IndexOf("ceiling", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return !string.IsNullOrEmpty(objectPath) &&
                objectPath.IndexOf("/Ceilings/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<string, float> EstimateZoneSampleHeights(float fallbackY)
        {
            var samples = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);
            List<NavigationGraph.PathStep> steps = NavigationGraph.GetAllPathSteps();
            for (int i = 0; i < steps.Count; i++)
            {
                NavigationGraph.PathStep step = steps[i];
                if (step == null)
                    continue;

                AddZoneHeightSamples(samples, step.FromZone, step.FromWaypoint, step.FromCrossingAnchor, step.SourceApproachPoint, step.SourceClearPoint);
                AddZoneHeightSamples(samples, step.ToZone, step.ToWaypoint, step.ToCrossingAnchor, step.DestinationClearPoint, step.DestinationApproachPoint);
            }

            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<float>> pair in samples)
            {
                List<float> values = pair.Value;
                if (values == null || values.Count == 0)
                    continue;

                values.Sort();
                result[pair.Key] = values[values.Count / 2];
            }

            if (result.Count == 0)
                result["<fallback>"] = fallbackY;

            return result;
        }

        private static void AddZoneHeightSamples(Dictionary<string, List<float>> samples, string zoneName, params Vector3[] points)
        {
            if (string.IsNullOrWhiteSpace(zoneName) || points == null)
                return;

            if (!samples.TryGetValue(zoneName, out List<float> values))
            {
                values = new List<float>();
                samples[zoneName] = values;
            }

            for (int i = 0; i < points.Length; i++)
            {
                Vector3 point = points[i];
                if (point == Vector3.zero)
                    continue;

                values.Add(point.y);
            }
        }

        private static bool TryGetPlayerCapsule(Collider collider, out PlayerCapsule capsule)
        {
            capsule = null;
            CapsuleCollider capsuleCollider = collider as CapsuleCollider;
            if (capsuleCollider == null)
                return false;

            Transform transform = capsuleCollider.transform;
            Vector3 scale = AbsVector3(transform.lossyScale);
            Vector3 localAxis;
            float heightScale;
            float radiusScale;
            switch (capsuleCollider.direction)
            {
                case 0:
                    localAxis = Vector3.right;
                    heightScale = scale.x;
                    radiusScale = Mathf.Max(scale.y, scale.z);
                    break;
                case 2:
                    localAxis = Vector3.forward;
                    heightScale = scale.z;
                    radiusScale = Mathf.Max(scale.x, scale.y);
                    break;
                case 1:
                default:
                    localAxis = Vector3.up;
                    heightScale = scale.y;
                    radiusScale = Mathf.Max(scale.x, scale.z);
                    break;
            }

            float radius = Mathf.Max(0.01f, capsuleCollider.radius * radiusScale);
            float height = Mathf.Max(radius * 2f, capsuleCollider.height * heightScale);
            Vector3 center = transform.TransformPoint(capsuleCollider.center);
            Vector3 axis = transform.TransformDirection(localAxis).normalized;
            float segmentHalfLength = Mathf.Max(0f, height * 0.5f - radius);
            capsule = new PlayerCapsule
            {
                PlayerPosition = transform.position,
                PointA = center + axis * segmentHalfLength,
                PointB = center - axis * segmentHalfLength,
                Radius = radius,
                Height = height
            };
            return true;
        }

        private static PlayerShapeData BuildPlayerShapeData(Collider playerCollider, PlayerCapsule playerCapsule)
        {
            return new PlayerShapeData
            {
                ColliderType = playerCollider != null ? playerCollider.GetType().Name : "<null>",
                PlayerPosition = ConvertVector3(playerCapsule.PlayerPosition),
                CapsuleRadius = playerCapsule.Radius,
                CapsuleHeight = playerCapsule.Height,
                CapsulePointA = ConvertVector3(playerCapsule.PointA),
                CapsulePointB = ConvertVector3(playerCapsule.PointB),
                Bounds = playerCollider != null ? BuildBoundsData(playerCollider.bounds) : null
            };
        }

        private static BlockerData BuildBlockerData(Collider collider)
        {
            if (collider == null)
                return null;

            return new BlockerData
            {
                ObjectPath = GetGameObjectPath(collider.gameObject),
                Name = collider.gameObject != null ? collider.gameObject.name : "<null>",
                ColliderType = collider.GetType().Name,
                IsTrigger = collider.isTrigger,
                Layer = collider.gameObject != null ? collider.gameObject.layer : -1,
                LayerName = collider.gameObject != null ? (LayerMask.LayerToName(collider.gameObject.layer) ?? "") : "",
                Bounds = BuildBoundsData(collider.bounds)
            };
        }

        private static BoundsData BuildBoundsData(Bounds bounds)
        {
            return new BoundsData
            {
                Min = ConvertVector3(bounds.min),
                Max = ConvertVector3(bounds.max),
                Center = ConvertVector3(bounds.center),
                Size = ConvertVector3(bounds.size)
            };
        }

        private static Vector3Data ConvertVector3(Vector3 value)
        {
            return new Vector3Data
            {
                x = value.x,
                y = value.y,
                z = value.z
            };
        }

        private static string[] GetLoadedScenes()
        {
            string[] names = new string[SceneManager.sceneCount];
            for (int i = 0; i < SceneManager.sceneCount; i++)
                names[i] = SceneManager.GetSceneAt(i).name;

            return names;
        }

        private static string GetGameObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
                return "<null>";

            const int maxSegments = 8;
            string path = gameObject.name;
            Transform current = gameObject.transform.parent;
            int segmentCount = 1;
            while (current != null && segmentCount < maxSegments)
            {
                path = current.name + "/" + path;
                current = current.parent;
                segmentCount++;
            }

            if (current != null)
                path = ".../" + path;

            return path;
        }

        private static Vector3 AbsVector3(Vector3 value)
        {
            return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        private static float GetFlatDistance(Vector3 left, Vector3 right)
        {
            left.y = 0f;
            right.y = 0f;
            return Vector3.Distance(left, right);
        }

        private sealed class PlayerCapsule
        {
            public Vector3 PlayerPosition;
            public Vector3 PointA;
            public Vector3 PointB;
            public float Radius;
            public float Height;
        }
    }
}
