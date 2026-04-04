using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace DateEverythingAccess
{
    internal static class NavMeshExporter
    {
        private const float SampleRadius = 4f;
        private const float SuspiciousSnapDistance = 1.25f;

        [Serializable]
        private sealed class NavMeshExportData
        {
            public string GeneratedAtUtc;
            public string ActiveScene;
            public string[] LoadedScenes;
            public bool HasActiveNavMesh;
            public string ExportStatus;
            public int VertexCount;
            public int TriangleCount;
            public Vector3[] Vertices;
            public int[] Indices;
            public int[] Areas;
            public BoundsData Bounds;
            public int TransitionCount;
            public TransitionCheckData[] TransitionChecks;
        }

        [Serializable]
        private sealed class BoundsData
        {
            public Vector3 Center;
            public Vector3 Size;
            public Vector3 Min;
            public Vector3 Max;
        }

        [Serializable]
        private sealed class TransitionCheckData
        {
            public string FromZone;
            public string ToZone;
            public string StepKind;
            public string ConnectorName;
            public bool RequiresInteraction;
            public float TransitionWaitSeconds;
            public float ValidationTimeoutSeconds;
            public int StaticSuspicionScore;
            public AnchorCheckData ConnectorObjectPosition;
            public AnchorCheckData FromWaypoint;
            public AnchorCheckData ToWaypoint;
            public AnchorCheckData FromCrossingAnchor;
            public AnchorCheckData ToCrossingAnchor;
            public AnchorCheckData SourceApproachPoint;
            public AnchorCheckData SourceClearPoint;
            public AnchorCheckData DestinationClearPoint;
            public AnchorCheckData DestinationApproachPoint;
            public PathCheckData WaypointPath;
            public PathCheckData CrossingPath;
            public string[] Issues;
        }

        [Serializable]
        private sealed class AnchorCheckData
        {
            public Vector3 AuthoredPosition;
            public bool FoundOnNavMesh;
            public Vector3 NavMeshPosition;
            public float SnapDistance;
            public int AreaMask;
        }

        [Serializable]
        private sealed class PathCheckData
        {
            public bool Computed;
            public string Status;
            public float DirectDistance;
            public float PathLength;
            public int CornerCount;
            public Vector3[] Corners;
            public bool StraightLineClear;
            public Vector3 RaycastHitPosition;
        }

        internal enum ExportFailure
        {
            None,
            NoNavMesh,
            WriteFailed,
            UnexpectedError
        }

        internal static bool TryExport(
            out string outputPath,
            out int triangleCount,
            out int transitionCount,
            out bool hasActiveNavMesh,
            out ExportFailure failure)
        {
            outputPath = Path.Combine(Paths.PluginPath, "navmesh_export.live.json");
            triangleCount = 0;
            transitionCount = 0;
            hasActiveNavMesh = false;
            failure = ExportFailure.None;

            try
            {
                NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
                hasActiveNavMesh = triangulation.vertices != null &&
                    triangulation.vertices.Length > 0 &&
                    triangulation.indices != null &&
                    triangulation.indices.Length >= 3;
                triangleCount = hasActiveNavMesh ? triangulation.indices.Length / 3 : 0;
                List<NavigationGraph.PathStep> steps = NavigationGraph.GetAllPathSteps();
                transitionCount = steps.Count;

                NavMeshExportData exportData = new NavMeshExportData
                {
                    GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
                    ActiveScene = SceneManager.GetActiveScene().name,
                    LoadedScenes = GetLoadedScenes(),
                    HasActiveNavMesh = hasActiveNavMesh,
                    ExportStatus = hasActiveNavMesh ? "LiveTriangulation" : "NoActiveNavMesh",
                    VertexCount = hasActiveNavMesh ? triangulation.vertices.Length : 0,
                    TriangleCount = triangleCount,
                    Vertices = hasActiveNavMesh ? triangulation.vertices : Array.Empty<Vector3>(),
                    Indices = hasActiveNavMesh ? triangulation.indices : Array.Empty<int>(),
                    Areas = hasActiveNavMesh ? triangulation.areas : Array.Empty<int>(),
                    Bounds = hasActiveNavMesh ? BuildBounds(triangulation.vertices) : null,
                    TransitionCount = transitionCount,
                    TransitionChecks = BuildTransitionChecks(steps)
                };

                string json = JsonUtility.ToJson(exportData, prettyPrint: true);
                Directory.CreateDirectory(Paths.PluginPath);
                File.WriteAllText(outputPath, json);
                if (hasActiveNavMesh)
                {
                    Main.Log.LogInfo("Navmesh export written to " + outputPath + " triangles=" + triangleCount + " transitions=" + transitionCount);
                }
                else
                {
                    Main.Log.LogWarning(
                        "Navmesh export wrote diagnostic report without active navmesh. outputPath=" + outputPath +
                        " transitions=" + transitionCount);
                }
                return true;
            }
            catch (IOException ex)
            {
                failure = ExportFailure.WriteFailed;
                Main.Log.LogError("Failed to write navmesh export: " + ex);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                failure = ExportFailure.WriteFailed;
                Main.Log.LogError("Failed to write navmesh export: " + ex);
                return false;
            }
            catch (Exception ex)
            {
                failure = ExportFailure.UnexpectedError;
                Main.Log.LogError("Unexpected navmesh export failure: " + ex);
                return false;
            }
        }

        private static string[] GetLoadedScenes()
        {
            string[] names = new string[SceneManager.sceneCount];
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                names[i] = SceneManager.GetSceneAt(i).name;
            }

            return names;
        }

        private static BoundsData BuildBounds(Vector3[] vertices)
        {
            Bounds bounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Length; i++)
            {
                bounds.Encapsulate(vertices[i]);
            }

            return new BoundsData
            {
                Center = bounds.center,
                Size = bounds.size,
                Min = bounds.min,
                Max = bounds.max
            };
        }

        private static TransitionCheckData[] BuildTransitionChecks(List<NavigationGraph.PathStep> steps)
        {
            var checks = new TransitionCheckData[steps.Count];
            for (int i = 0; i < steps.Count; i++)
            {
                NavigationGraph.PathStep step = steps[i];
                AnchorCheckData fromWaypoint = BuildAnchorCheck(step.FromWaypoint);
                AnchorCheckData toWaypoint = BuildAnchorCheck(step.ToWaypoint);
                AnchorCheckData fromCrossingAnchor = BuildAnchorCheck(step.FromCrossingAnchor);
                AnchorCheckData toCrossingAnchor = BuildAnchorCheck(step.ToCrossingAnchor);
                AnchorCheckData sourceApproachPoint = BuildAnchorCheck(step.SourceApproachPoint);
                AnchorCheckData sourceClearPoint = BuildAnchorCheck(step.SourceClearPoint);
                AnchorCheckData destinationClearPoint = BuildAnchorCheck(step.DestinationClearPoint);
                AnchorCheckData destinationApproachPoint = BuildAnchorCheck(step.DestinationApproachPoint);
                AnchorCheckData connectorObjectPosition = BuildAnchorCheck(step.ConnectorObjectPosition);

                var issues = new List<string>();
                CollectAnchorIssues(issues, "FromWaypoint", fromWaypoint);
                CollectAnchorIssues(issues, "ToWaypoint", toWaypoint);
                CollectAnchorIssues(issues, "FromCrossingAnchor", fromCrossingAnchor);
                CollectAnchorIssues(issues, "ToCrossingAnchor", toCrossingAnchor);
                CollectAnchorIssues(issues, "SourceApproachPoint", sourceApproachPoint);
                CollectAnchorIssues(issues, "SourceClearPoint", sourceClearPoint);
                CollectAnchorIssues(issues, "DestinationClearPoint", destinationClearPoint);
                CollectAnchorIssues(issues, "DestinationApproachPoint", destinationApproachPoint);

                PathCheckData waypointPath = BuildPathCheck(fromWaypoint, toWaypoint);
                PathCheckData crossingPath = BuildPathCheck(fromCrossingAnchor, toCrossingAnchor);

                CollectPathIssues(issues, "WaypointPath", waypointPath);
                CollectPathIssues(issues, "CrossingPath", crossingPath);

                checks[i] = new TransitionCheckData
                {
                    FromZone = step.FromZone,
                    ToZone = step.ToZone,
                    StepKind = step.Kind.ToString(),
                    ConnectorName = step.ConnectorName,
                    RequiresInteraction = step.RequiresInteraction,
                    TransitionWaitSeconds = step.TransitionWaitSeconds,
                    ValidationTimeoutSeconds = step.ValidationTimeoutSeconds,
                    StaticSuspicionScore = step.StaticSuspicionScore,
                    ConnectorObjectPosition = connectorObjectPosition,
                    FromWaypoint = fromWaypoint,
                    ToWaypoint = toWaypoint,
                    FromCrossingAnchor = fromCrossingAnchor,
                    ToCrossingAnchor = toCrossingAnchor,
                    SourceApproachPoint = sourceApproachPoint,
                    SourceClearPoint = sourceClearPoint,
                    DestinationClearPoint = destinationClearPoint,
                    DestinationApproachPoint = destinationApproachPoint,
                    WaypointPath = waypointPath,
                    CrossingPath = crossingPath,
                    Issues = issues.ToArray()
                };
            }

            return checks;
        }

        private static AnchorCheckData BuildAnchorCheck(Vector3 authoredPosition)
        {
            var anchor = new AnchorCheckData
            {
                AuthoredPosition = authoredPosition,
                FoundOnNavMesh = false,
                NavMeshPosition = Vector3.zero,
                SnapDistance = -1f,
                AreaMask = 0
            };

            if (NavMesh.SamplePosition(authoredPosition, out NavMeshHit hit, SampleRadius, NavMesh.AllAreas))
            {
                anchor.FoundOnNavMesh = true;
                anchor.NavMeshPosition = hit.position;
                anchor.SnapDistance = Vector3.Distance(authoredPosition, hit.position);
                anchor.AreaMask = hit.mask;
            }

            return anchor;
        }

        private static PathCheckData BuildPathCheck(AnchorCheckData fromAnchor, AnchorCheckData toAnchor)
        {
            if (!fromAnchor.FoundOnNavMesh || !toAnchor.FoundOnNavMesh)
            {
                return new PathCheckData
                {
                    Computed = false,
                    Status = "MissingAnchor",
                    DirectDistance = -1f,
                    PathLength = -1f,
                    CornerCount = 0,
                    Corners = Array.Empty<Vector3>(),
                    StraightLineClear = false,
                    RaycastHitPosition = Vector3.zero
                };
            }

            var path = new NavMeshPath();
            bool calculated = NavMesh.CalculatePath(fromAnchor.NavMeshPosition, toAnchor.NavMeshPosition, NavMesh.AllAreas, path);
            Vector3[] corners = path.corners ?? Array.Empty<Vector3>();
            float pathLength = CalculatePathLength(corners);
            bool blocked = NavMesh.Raycast(fromAnchor.NavMeshPosition, toAnchor.NavMeshPosition, out NavMeshHit raycastHit, NavMesh.AllAreas);

            return new PathCheckData
            {
                Computed = calculated,
                Status = path.status.ToString(),
                DirectDistance = Vector3.Distance(fromAnchor.NavMeshPosition, toAnchor.NavMeshPosition),
                PathLength = pathLength,
                CornerCount = corners.Length,
                Corners = corners,
                StraightLineClear = !blocked,
                RaycastHitPosition = blocked ? raycastHit.position : Vector3.zero
            };
        }

        private static float CalculatePathLength(Vector3[] corners)
        {
            if (corners == null || corners.Length < 2)
                return 0f;

            float total = 0f;
            for (int i = 1; i < corners.Length; i++)
            {
                total += Vector3.Distance(corners[i - 1], corners[i]);
            }

            return total;
        }

        private static void CollectAnchorIssues(List<string> issues, string label, AnchorCheckData anchor)
        {
            if (!anchor.FoundOnNavMesh)
            {
                issues.Add(label + "Missing");
                return;
            }

            if (anchor.SnapDistance > SuspiciousSnapDistance)
            {
                issues.Add(label + "FarFromNavMesh");
            }
        }

        private static void CollectPathIssues(List<string> issues, string label, PathCheckData path)
        {
            if (!path.Computed)
            {
                issues.Add(label + "Unavailable");
                return;
            }

            if (!string.Equals(path.Status, NavMeshPathStatus.PathComplete.ToString(), StringComparison.Ordinal))
            {
                issues.Add(label + path.Status);
            }
        }
    }
}
