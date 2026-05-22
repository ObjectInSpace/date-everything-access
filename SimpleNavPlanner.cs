using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BepInEx;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// O6.5 runtime planner: A* over per-floor navigable bitmaps (from
    /// <c>navigable_region.bake.json</c>) plus inter-floor edges, producing a
    /// <see cref="SimpleNavRoute"/> directly. C# port of <c>scripts/plan_object_route.py</c>.
    /// </summary>
    internal static class SimpleNavPlanner
    {
        private const string BakeFileName = "navigable_region.bake.json";
        private const char NavigableChar = 'N';
        private const float CornerWaypointDeg = 30f;
        // Door is tagged on a segment if its XZ is within this radius of the segment.
        // 0.8m was too tight: a 5-waypoint route to Doors_Bedroom had the door 1.97m
        // from the final segment because the planner approaches the door's interaction
        // area, not its hinge. See [[project-navigation-door-tag-radius]].
        private const float DoorTagRadiusM = 2.5f;
        // Goal-disc radius bounds. The game's InteractableObj.InteractionRadius (default 7.5m)
        // is the range at which the player can date OR interact with an object — the check in
        // BetterPlayerControl is `Distance(camera, ClosestPointOnBounds) < InteractionRadius`
        // plus a forward raycast. Since the planner can't distinguish whether the user will
        // interact or just date, it uses the full radius so unreachable-by-2m targets still
        // succeed. A* picks the cheapest goal cell, which is typically the closest reachable
        // one, so stopping farther only happens when closer cells are blocked.
        // Floor sets a minimum so degenerate radii (0) still produce at least a few cells.
        private const float MaxInteractionRadiusM = 7.5f;
        private const float MinInteractionRadiusM = 0.5f;
        private const float NearestNavigableSearchM = 4.0f;
        private const float FloorMatchToleranceM = 2.0f;
        // Player-capsule + safety margin from the target's collider face. Goal cells whose
        // player-capsule center is closer than this become invalid (overlap), and cells further
        // than ColliderBandOuterM become invalid (too far to interact reliably). The result is
        // a narrow band around the target so A* terminates near the collider rather than
        // anywhere in the InteractionRadius disc.
        private const float TargetColliderClearanceM = 0.5f;
        // Outer edge of the collider band. Just under the game's 7.5m InteractionRadius, with
        // headroom for camera-vs-cell-XZ Y-component slop. If the target's collider has no
        // nav-eligible cells inside this band (e.g. tiny prop in a tight space), the planner
        // falls back to the standard disc.
        private const float TargetColliderBandOuterM = 1.5f;
        // Door-specific hinge-distance band. The inner edge must clear the door's swing arc
        // (panel ~0.9m wide, plus player capsule radius ~0.4m, plus margin): if the player
        // stands inside the arc the game's Door.OnCollisionEnter latches collidedWithPlayer
        // and refuses to open/close the door. Outer edge sits just inside the game's interact
        // forward-raycast reach so the player can still interact after arrival.
        private const float DoorHingeBandInnerM = 1.5f;
        private const float DoorHingeBandOuterM = 3.0f;

        private static BakeDoc _bake;
        private static List<Floor> _floors;
        private static float _cellSize;
        private static Dictionary<NodeKey, List<EdgeRef>> _interFloorEdges;
        // Min cost across all inter-floor edges, cached for the heuristic.
        private static float _minInterFloorCost;
        private static bool _loadAttempted;
        private static bool _loadOk;

        // Failure reasons surfaced by the most recent Plan() call. Read by the watcher to
        // announce *why* a route couldn't be built instead of silently dropping. Cleared at
        // the top of every Plan() invocation.
        public enum PlanFailure
        {
            None,
            NotReady,
            StartOffBake,
            TargetOffBake,
            StartUnreachable,
            TargetUnreachable,
            NoPath,
        }
        public static PlanFailure LastFailure { get; private set; } = PlanFailure.None;

        /// <summary>
        /// Plan a route from the player's current world position to the target object's world
        /// position. Returns null if the bake isn't loaded, no path is found, or inputs are
        /// invalid. The caller installs the returned route via <see cref="SimpleNavBridge.BeginRoute"/>.
        /// </summary>
        public static SimpleNavRoute Plan(
            Vector3 startPos,
            Vector3 targetPos,
            float interactionRadius,
            string targetName,
            int targetGameObjectId,
            bool targetIsDatable = false,
            string targetInkFileName = null)
        {
            LastFailure = PlanFailure.None;
            if (!EnsureLoaded())
            {
                LastFailure = PlanFailure.NotReady;
                return null;
            }

            Floor startFloor = FloorForY(startPos.y);
            Floor goalFloor = FloorForTargetY(targetPos.y);
            if (startFloor == null)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavPlanner.Plan: start Y=" + startPos.y + " not on a baked floor");
                LastFailure = PlanFailure.StartOffBake;
                return null;
            }
            if (goalFloor == null)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavPlanner.Plan: target Y=" + targetPos.y + " not on a baked floor");
                LastFailure = PlanFailure.TargetOffBake;
                return null;
            }

            int sIx, sIz;
            if (!startFloor.NearestNavigable(startPos.x, startPos.z, NearestNavigableSearchM, out sIx, out sIz))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavPlanner.Plan: no navigable cell near start (" + startPos.x + "," + startPos.z + ") on " + startFloor.Label);
                LastFailure = PlanFailure.StartUnreachable;
                return null;
            }
            NodeKey startNode = new NodeKey(startFloor.Label, sIx, sIz);

            float radius = interactionRadius;
            if (radius < MinInteractionRadiusM) radius = MinInteractionRadiusM;
            if (radius > MaxInteractionRadiusM) radius = MaxInteractionRadiusM;
            List<NodeKey> goals = GoalCellsAround(goalFloor, targetPos.x, targetPos.z, radius);
            if (goals.Count == 0)
            {
                int gIx, gIz;
                if (!goalFloor.NearestNavigable(targetPos.x, targetPos.z, NearestNavigableSearchM, out gIx, out gIz))
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNavPlanner.Plan: no navigable cell near target (" + targetPos.x + "," + targetPos.z + ") on " + goalFloor.Label);
                    LastFailure = PlanFailure.TargetUnreachable;
                    return null;
                }
                goals.Add(new NodeKey(goalFloor.Label, gIx, gIz));
            }

            // Narrow goal cells to a band around the target so A* terminates *near* the
            // interactable instead of anywhere in the 7.5m InteractionRadius disc.
            //
            // Two anchor strategies:
            //   - Door targets: distance measured from targetPos (the hinge). A door's
            //     Collider AABB sweeps along the panel rotation, so ClosestPointOnBounds
            //     reports cells 5m down a hallway as "near the collider" when the door is
            //     85deg open. Hinge-distance is invariant to rotation.
            //   - Non-door targets: distance measured from the target's Collider via
            //     ClosestPointOnBounds, which hugs prop geometry better than the hinge would.
            //
            // Cells in the [TargetColliderClearanceM, TargetColliderBandOuterM] band are
            // kept; A* picks the cheapest-from-start. Falls back to the unfiltered disc
            // if the band has no nav-eligible cells.
            // See [[project-navigation-collider-band-filter]].
            Door targetDoor = ResolveTargetDoor(targetGameObjectId);
            Collider targetCollider = targetDoor == null ? ResolveTargetCollider(targetGameObjectId) : null;
            if (targetDoor != null || targetCollider != null)
            {
                List<NodeKey> filtered = new List<NodeKey>(goals.Count);
                float innerM = targetDoor != null ? DoorHingeBandInnerM : TargetColliderClearanceM;
                float outerM = targetDoor != null ? DoorHingeBandOuterM : TargetColliderBandOuterM;
                float innerSq = innerM * innerM;
                float outerSq = outerM * outerM;
                for (int i = 0; i < goals.Count; i++)
                {
                    NodeKey g = goals[i];
                    Vector2 xz = goalFloor.CellToWorld(g.Ix, g.Iz);
                    Vector3 cellWorld = new Vector3(xz.x, goalFloor.FloorY + 1.0f, xz.y);
                    float dx, dz;
                    if (targetDoor != null)
                    {
                        dx = targetPos.x - cellWorld.x;
                        dz = targetPos.z - cellWorld.z;
                    }
                    else
                    {
                        Vector3 nearest = targetCollider.ClosestPointOnBounds(cellWorld);
                        dx = nearest.x - cellWorld.x;
                        dz = nearest.z - cellWorld.z;
                    }
                    float d2 = dx * dx + dz * dz;
                    if (d2 >= innerSq && d2 <= outerSq)
                        filtered.Add(g);
                }
                if (filtered.Count > 0) goals = filtered;
                else if (Main.Log != null)
                    Main.Log.LogInfo("SimpleNavPlanner.Plan: goal band empty for target=" +
                        (targetName ?? "<null>") + " anchor=" + (targetDoor != null ? "door-hinge" : "collider") +
                        "; keeping unfiltered goals=" + goals.Count);
            }

            List<NodeKey> path;
            float totalCost;
            if (!AStar(startNode, goals, goalFloor.Label, targetPos.x, targetPos.z, out path, out totalCost))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavPlanner.Plan: no_path target=" + (targetName ?? "<null>") + "#" + targetGameObjectId);
                LastFailure = PlanFailure.NoPath;
                return null;
            }

            List<NodeKey> waypoints = SmoothPath(path);
            List<List<string>> segmentDoorNames = TagDoors(waypoints, targetName, targetPos);

            SimpleNavRoute route = new SimpleNavRoute();
            route.TargetName = targetName;
            route.TargetGameObjectId = targetGameObjectId;
            route.TargetPosition = targetPos;
            route.TargetInteractionRadius = radius;
            route.TargetIsDatable = targetIsDatable;
            route.TargetInkFileName = targetInkFileName;
            for (int i = 0; i < waypoints.Count; i++)
            {
                NodeKey w = waypoints[i];
                Floor f = FloorByLabel(w.Floor);
                if (f == null) continue;
                Vector2 xz = f.CellToWorld(w.Ix, w.Iz);
                route.Waypoints.Add(new Vector3(xz.x, f.FloorY, xz.y));
            }
            route.SegmentDoorNames.AddRange(segmentDoorNames);
            while (route.SegmentDoorNames.Count < route.Waypoints.Count - 1)
                route.SegmentDoorNames.Add(new List<string>(0));

            if (Main.Log != null)
            {
                int doorSegs = 0;
                for (int i = 0; i < route.SegmentDoorNames.Count; i++)
                    if (route.SegmentDoorNames[i].Count > 0) doorSegs++;
                Main.Log.LogInfo("SimpleNavPlanner.Plan ok target=" + (targetName ?? "<null>") +
                    " waypoints=" + route.Waypoints.Count +
                    " segmentsWithDoor=" + doorSegs +
                    " cost_m=" + totalCost.ToString("F2"));
                // Diagnostic: log every waypoint XYZ so we can compare against the bake PPM
                // when the executor reports a wall-clipping route failure.
                for (int i = 0; i < route.Waypoints.Count; i++)
                {
                    Vector3 w = route.Waypoints[i];
                    Main.Log.LogInfo("  wp[" + i + "]=(" + w.x.ToString("F2") + ", " + w.y.ToString("F2") + ", " + w.z.ToString("F2") + ")");
                }
            }
            return route;
        }

        /// <summary>Returns true if the bake is loaded and the planner is ready.</summary>
        public static bool IsReady => EnsureLoaded();

        // ---- bake load ----

        private static bool EnsureLoaded()
        {
            if (_loadAttempted) return _loadOk;
            _loadAttempted = true;

            string path = Path.Combine(Paths.PluginPath, BakeFileName);
            if (!File.Exists(path))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavPlanner: bake file missing at " + path);
                return false;
            }

            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(BakeDoc));
                    _bake = serializer.ReadObject(stream) as BakeDoc;
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavPlanner: bake parse failed at " + path + " ex=" + ex.Message);
                return false;
            }

            if (_bake == null || _bake.floors == null || _bake.floors.Length == 0)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavPlanner: bake has no floors");
                return false;
            }

            _floors = new List<Floor>(_bake.floors.Length);
            for (int i = 0; i < _bake.floors.Length; i++)
            {
                Floor f = Floor.From(_bake.floors[i]);
                if (f == null) continue;
                _floors.Add(f);
            }
            if (_floors.Count == 0)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavPlanner: no valid floors parsed");
                return false;
            }
            _cellSize = _floors[0].CellSize;

            BuildInterFloorEdges();
            _loadOk = true;
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavPlanner: bake loaded floors=" + _floors.Count +
                    " cellSize=" + _cellSize +
                    " interFloorEdges=" + _interFloorEdges.Count);
            return true;
        }

        private static void BuildInterFloorEdges()
        {
            _interFloorEdges = new Dictionary<NodeKey, List<EdgeRef>>();
            _minInterFloorCost = float.PositiveInfinity;

            InterFloorEdges ife = _bake.inter_floor_edges;
            if (ife == null) return;

            if (ife.stair_ramp != null)
            {
                for (int i = 0; i < ife.stair_ramp.Length; i++)
                {
                    StairRampEdge e = ife.stair_ramp[i];
                    if (e == null || e.ground == null || e.upper == null) continue;
                    if (e.ground.cell == null || e.ground.cell.Length < 2) continue;
                    if (e.upper.cell == null || e.upper.cell.Length < 2) continue;
                    NodeKey a = new NodeKey("ground", e.ground.cell[0], e.ground.cell[1]);
                    NodeKey b = new NodeKey("upper", e.upper.cell[0], e.upper.cell[1]);
                    AddInterFloor(a, b, e.cost_m, "stairs");
                    AddInterFloor(b, a, e.cost_m, "stairs");
                    if (e.cost_m > 0f && e.cost_m < _minInterFloorCost) _minInterFloorCost = e.cost_m;
                }
            }

            // Teleporter: down endpoint is off-bake (no walkable in crawlspace yet); model as
            // virtual node "@teleporter:<name>" connected to the nearest navigable cell at the
            // up endpoint, with cost 0 (teleport, not walked).
            if (ife.teleporter != null)
            {
                for (int i = 0; i < ife.teleporter.Length; i++)
                {
                    TeleporterEdge t = ife.teleporter[i];
                    if (t == null || t.up == null || t.up.world_xyz == null || t.up.world_xyz.Length < 3) continue;
                    Floor upFloor = FloorForY(t.up.world_xyz[1]);
                    if (upFloor == null) continue;
                    int ix, iz;
                    if (!upFloor.NearestNavigable(t.up.world_xyz[0], t.up.world_xyz[2], 3.0f, out ix, out iz)) continue;
                    NodeKey upNode = new NodeKey(upFloor.Label, ix, iz);
                    NodeKey downNode = new NodeKey("@teleporter:" + (t.source_name ?? "?"), 0, 0);
                    AddInterFloor(upNode, downNode, t.cost_m, "teleporter");
                    AddInterFloor(downNode, upNode, t.cost_m, "teleporter");
                }
            }

            if (float.IsPositiveInfinity(_minInterFloorCost)) _minInterFloorCost = 0f;
        }

        private static void AddInterFloor(NodeKey from, NodeKey to, float cost, string kind)
        {
            List<EdgeRef> list;
            if (!_interFloorEdges.TryGetValue(from, out list))
            {
                list = new List<EdgeRef>(2);
                _interFloorEdges[from] = list;
            }
            list.Add(new EdgeRef(to, cost, kind));
        }

        // ---- floor utilities ----

        private static Floor FloorByLabel(string label)
        {
            if (label == null) return null;
            for (int i = 0; i < _floors.Count; i++)
                if (_floors[i].Label == label) return _floors[i];
            return null;
        }

        private static Floor FloorForY(float y)
        {
            Floor best = null;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < _floors.Count; i++)
            {
                float d = Mathf.Abs(_floors[i].FloorY - y);
                if (d < bestD) { bestD = d; best = _floors[i]; }
            }
            return bestD <= FloorMatchToleranceM ? best : null;
        }

        // Pick the nearest floor at-or-below the target's world Y. Wall-mounted props (e.g. the
        // MagnifyingGlass mount at Y=4.09 on the ground floor at Y=-0.5) sit *above* their floor,
        // not near it. Allow a small upward slack so a target slightly below its floor's
        // canonical Y (model-origin quirk) still matches that floor.
        private static Floor FloorForTargetY(float y)
        {
            const float UpwardSlackM = 0.5f;
            Floor best = null;
            float bestDist = float.PositiveInfinity;
            for (int i = 0; i < _floors.Count; i++)
            {
                float floorY = _floors[i].FloorY;
                if (y + UpwardSlackM < floorY) continue; // floor is above target — skip
                float dist = y - floorY; // >= -UpwardSlackM
                if (dist < bestDist) { bestDist = dist; best = _floors[i]; }
            }
            return best;
        }

        private static List<NodeKey> GoalCellsAround(Floor floor, float wx, float wz, float radiusM)
        {
            int cx, cz;
            floor.WorldToCell(wx, wz, out cx, out cz);
            int r = Mathf.Max(1, Mathf.CeilToInt(radiusM / floor.CellSize));
            float r2 = radiusM * radiusM;
            List<NodeKey> goals = new List<NodeKey>();
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    int ix = cx + dx, iz = cz + dz;
                    if (!floor.Navigable(ix, iz)) continue;
                    Vector2 w = floor.CellToWorld(ix, iz);
                    float ddx = w.x - wx, ddz = w.y - wz;
                    if (ddx * ddx + ddz * ddz <= r2)
                        goals.Add(new NodeKey(floor.Label, ix, iz));
                }
            }
            return goals;
        }

        // ---- A* ----

        // 8-connected grid neighbours.
        private static readonly int[] NeighborDx = { -1, -1, -1, 0, 0, 1, 1, 1 };
        private static readonly int[] NeighborDz = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly float Sqrt2 = Mathf.Sqrt(2f);

        private static bool AStar(NodeKey start, List<NodeKey> goals, string goalFloorLabel, float goalWx, float goalWz,
            out List<NodeKey> path, out float totalCost)
        {
            path = null;
            totalCost = float.PositiveInfinity;

            HashSet<NodeKey> goalSet = new HashSet<NodeKey>(goals);
            if (goalSet.Contains(start))
            {
                path = new List<NodeKey> { start };
                totalCost = 0f;
                return true;
            }

            // Open set as a simple binary-heap-backed priority queue (manual implementation).
            var open = new MinHeap();
            var came = new Dictionary<NodeKey, NodeKey>();
            var gScore = new Dictionary<NodeKey, float>();
            gScore[start] = 0f;
            open.Push(new HeapItem(Heuristic(start, goalFloorLabel, goalWx, goalWz), 0f, start));

            while (open.Count > 0)
            {
                HeapItem cur = open.Pop();
                NodeKey node = cur.Node;
                if (goalSet.Contains(node))
                {
                    // Reconstruct.
                    var rev = new List<NodeKey>();
                    NodeKey c = node;
                    rev.Add(c);
                    while (came.TryGetValue(c, out NodeKey prev))
                    {
                        rev.Add(prev);
                        c = prev;
                    }
                    rev.Reverse();
                    path = rev;
                    totalCost = cur.G;
                    return true;
                }
                if (cur.G > gScore[node] + 1e-6f) continue;

                foreach (var (nbr, cost) in Neighbors(node))
                {
                    float ng = cur.G + cost;
                    if (gScore.TryGetValue(nbr, out float existing) && ng >= existing) continue;
                    gScore[nbr] = ng;
                    came[nbr] = node;
                    float f = ng + Heuristic(nbr, goalFloorLabel, goalWx, goalWz);
                    open.Push(new HeapItem(f, ng, nbr));
                }
            }
            return false;
        }

        private static IEnumerable<(NodeKey, float)> Neighbors(NodeKey node)
        {
            if (!node.Floor.StartsWith("@", StringComparison.Ordinal))
            {
                Floor f = FloorByLabel(node.Floor);
                if (f != null)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = node.Ix + NeighborDx[i];
                        int nz = node.Iz + NeighborDz[i];
                        if (!f.Navigable(nx, nz)) continue;
                        float step = (NeighborDx[i] != 0 && NeighborDz[i] != 0) ? Sqrt2 : 1f;
                        yield return (new NodeKey(f.Label, nx, nz), step * f.CellSize);
                    }
                }
            }
            if (_interFloorEdges.TryGetValue(node, out List<EdgeRef> ifs))
            {
                for (int i = 0; i < ifs.Count; i++)
                    yield return (ifs[i].To, ifs[i].Cost);
            }
        }

        private static float Heuristic(NodeKey node, string goalFloor, float goalWx, float goalWz)
        {
            if (node.Floor.StartsWith("@", StringComparison.Ordinal)) return 0f;
            Floor f = FloorByLabel(node.Floor);
            if (f == null) return 0f;
            Vector2 w = f.CellToWorld(node.Ix, node.Iz);
            float dx = w.x - goalWx, dz = w.y - goalWz;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (node.Floor != goalFloor) d += _minInterFloorCost;
            return d;
        }

        // ---- smoothing ----

        private static List<NodeKey> SmoothPath(List<NodeKey> path)
        {
            if (path == null || path.Count <= 2) return new List<NodeKey>(path ?? new List<NodeKey>());

            // Pass 1: greedy line-of-sight.
            List<NodeKey> los = new List<NodeKey> { path[0] };
            int i = 1;
            while (i < path.Count)
            {
                NodeKey node = path[i];
                NodeKey prev = path[i - 1];
                // Any non-grid hop forces a waypoint (floor change, virtual node).
                if (node.Floor != los[los.Count - 1].Floor || IsVirtual(node) || IsVirtual(los[los.Count - 1]))
                {
                    if (!prev.Equals(los[los.Count - 1])) los.Add(prev);
                    if (!node.Equals(los[los.Count - 1])) los.Add(node);
                    i++;
                    continue;
                }
                if (SegmentIsClear(los[los.Count - 1], node))
                {
                    i++;
                    continue;
                }
                los.Add(prev);
            }
            if (!los[los.Count - 1].Equals(path[path.Count - 1])) los.Add(path[path.Count - 1]);

            // Pass 2: drop interior waypoints with shallow corner angle when shortcut is clear.
            List<NodeKey> outList = new List<NodeKey> { los[0] };
            for (int j = 1; j < los.Count - 1; j++)
            {
                NodeKey a = outList[outList.Count - 1], b = los[j], c = los[j + 1];
                Vector2? wa = WorldOf(a), wb = WorldOf(b), wc = WorldOf(c);
                if (!wa.HasValue || !wb.HasValue || !wc.HasValue) { outList.Add(b); continue; }
                Vector2 v1 = wb.Value - wa.Value;
                Vector2 v2 = wc.Value - wb.Value;
                float n1 = v1.magnitude, n2 = v2.magnitude;
                if (n1 == 0f || n2 == 0f) { outList.Add(b); continue; }
                float dot = Mathf.Clamp(Vector2.Dot(v1, v2) / (n1 * n2), -1f, 1f);
                float angleDeg = Mathf.Acos(dot) * Mathf.Rad2Deg;
                if (angleDeg <= CornerWaypointDeg && SegmentIsClear(a, c))
                    continue; // drop b
                outList.Add(b);
            }
            outList.Add(los[los.Count - 1]);

            // Dedup consecutive duplicates.
            List<NodeKey> dedup = new List<NodeKey> { outList[0] };
            for (int k = 1; k < outList.Count; k++)
                if (!outList[k].Equals(dedup[dedup.Count - 1])) dedup.Add(outList[k]);
            return dedup;
        }

        private static bool IsVirtual(NodeKey n) => n.Floor.StartsWith("@", StringComparison.Ordinal);

        private static Vector2? WorldOf(NodeKey n)
        {
            if (IsVirtual(n)) return null;
            Floor f = FloorByLabel(n.Floor);
            return f == null ? (Vector2?)null : f.CellToWorld(n.Ix, n.Iz);
        }

        // Bresenham line walk through cells; all must be navigable.
        private static bool SegmentIsClear(NodeKey a, NodeKey b)
        {
            if (a.Floor != b.Floor || IsVirtual(a)) return false;
            Floor floor = FloorByLabel(a.Floor);
            if (floor == null) return false;
            int ix0 = a.Ix, iz0 = a.Iz, ix1 = b.Ix, iz1 = b.Iz;
            int dx = Mathf.Abs(ix1 - ix0), dz = Mathf.Abs(iz1 - iz0);
            int sx = ix0 < ix1 ? 1 : -1;
            int sz = iz0 < iz1 ? 1 : -1;
            int err = dx - dz;
            int ix = ix0, iz = iz0;
            while (true)
            {
                if (!floor.Navigable(ix, iz)) return false;
                if (ix == ix1 && iz == iz1) return true;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; ix += sx; }
                if (e2 < dx) { err += dx; iz += sz; }
            }
        }

        // ---- door tagging ----

        // Cached live-door catalogue, rebuilt on each Plan() call since Door instances can be
        // destroyed/spawned across scene changes. Cheap: ~25 Doors in the house scene.
        //
        // When the navigation target is itself a Door, the destination's Door is force-tagged
        // on the final segment regardless of distance. The planner stops inside the target's
        // interaction radius (clamped to 0.5–7.5m), which is typically farther than the static
        // DoorTagRadiusM, so distance-based tagging would otherwise miss it.
        // See [[project-navigation-door-tag-radius]].
        // Resolve the GameObject's primary Collider for the collider-clearance goal filter.
        // Matches the GameObject by InstanceID against all live InteractableObj components.
        // Returns null if no match or the object has no collider.
        // Find the Door component for a target GameObject (by InstanceID), if any. Used to
        // exempt door targets from the collider-band goal-cell filter.
        private static Door ResolveTargetDoor(int targetGameObjectId)
        {
            if (targetGameObjectId == 0) return null;
            Door[] all = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < all.Length; i++)
            {
                Door d = all[i];
                if (d == null || d.gameObject == null) continue;
                if (d.gameObject.GetInstanceID() == targetGameObjectId) return d;
            }
            return null;
        }

        private static Collider ResolveTargetCollider(int targetGameObjectId)
        {
            if (targetGameObjectId == 0) return null;
            InteractableObj[] all = UnityEngine.Object.FindObjectsOfType<InteractableObj>();
            for (int i = 0; i < all.Length; i++)
            {
                InteractableObj io = all[i];
                if (io == null || io.gameObject == null) continue;
                if (io.gameObject.GetInstanceID() != targetGameObjectId) continue;
                return io.GetComponent<Collider>();
            }
            return null;
        }

        private static List<List<string>> TagDoors(List<NodeKey> waypoints, string targetName, Vector3 targetPos)
        {
            List<List<string>> segs = new List<List<string>>(Mathf.Max(0, waypoints.Count - 1));
            Door[] doors = UnityEngine.Object.FindObjectsOfType<Door>();

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                List<string> tagged = new List<string>(0);
                NodeKey a = waypoints[i], b = waypoints[i + 1];
                if (a.Floor == b.Floor && !IsVirtual(a))
                {
                    Floor floor = FloorByLabel(a.Floor);
                    Vector2 wa = floor.CellToWorld(a.Ix, a.Iz);
                    Vector2 wb = floor.CellToWorld(b.Ix, b.Iz);
                    float floorY = floor.FloorY;
                    for (int d = 0; d < doors.Length; d++)
                    {
                        Door door = doors[d];
                        if (door == null) continue;
                        Vector3 dp = door.transform.position;
                        if (Mathf.Abs(dp.y - floorY) > 3.0f) continue;
                        float dist = PointSegmentDistance(dp.x, dp.z, wa.x, wa.y, wb.x, wb.y);
                        if (dist <= DoorTagRadiusM)
                            tagged.Add(door.gameObject.name);
                    }
                }
                segs.Add(tagged);
            }

            // Force-tag the destination door on the final segment when the target is a Door.
            // Match by GameObject name against the target name. If found and not already
            // tagged on the final segment, append it.
            if (segs.Count > 0 && !string.IsNullOrEmpty(targetName))
            {
                for (int d = 0; d < doors.Length; d++)
                {
                    Door door = doors[d];
                    if (door == null) continue;
                    string dn = door.gameObject.name;
                    if (dn != targetName) continue;
                    List<string> last = segs[segs.Count - 1];
                    if (!last.Contains(dn))
                        last.Add(dn);
                    break;
                }
            }
            return segs;
        }

        private static float PointSegmentDistance(float px, float pz, float ax, float az, float bx, float bz)
        {
            float vx = bx - ax, vz = bz - az;
            float L2 = vx * vx + vz * vz;
            if (L2 == 0f) return Mathf.Sqrt((px - ax) * (px - ax) + (pz - az) * (pz - az));
            float t = ((px - ax) * vx + (pz - az) * vz) / L2;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float cx = ax + t * vx, cz = az + t * vz;
            return Mathf.Sqrt((px - cx) * (px - cx) + (pz - cz) * (pz - cz));
        }

        // ---- support types ----

        private readonly struct NodeKey : IEquatable<NodeKey>
        {
            public readonly string Floor;
            public readonly int Ix;
            public readonly int Iz;
            public NodeKey(string floor, int ix, int iz) { Floor = floor; Ix = ix; Iz = iz; }
            public bool Equals(NodeKey o) => Floor == o.Floor && Ix == o.Ix && Iz == o.Iz;
            public override bool Equals(object o) => o is NodeKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Floor != null ? Floor.GetHashCode() : 0;
                    h = (h * 397) ^ Ix;
                    h = (h * 397) ^ Iz;
                    return h;
                }
            }
        }

        private readonly struct EdgeRef
        {
            public readonly NodeKey To;
            public readonly float Cost;
            public readonly string Kind;
            public EdgeRef(NodeKey to, float cost, string kind) { To = to; Cost = cost; Kind = kind; }
        }

        private readonly struct HeapItem
        {
            public readonly float F;
            public readonly float G;
            public readonly NodeKey Node;
            public HeapItem(float f, float g, NodeKey node) { F = f; G = g; Node = node; }
        }

        // Binary min-heap keyed on F. Small enough that a hand-rolled heap is cheaper than
        // pulling in SortedSet (which doesn't allow duplicate keys without a tie-breaker).
        private sealed class MinHeap
        {
            private readonly List<HeapItem> _items = new List<HeapItem>(256);
            public int Count => _items.Count;

            public void Push(HeapItem item)
            {
                _items.Add(item);
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_items[p].F <= _items[i].F) break;
                    var tmp = _items[p]; _items[p] = _items[i]; _items[i] = tmp;
                    i = p;
                }
            }

            public HeapItem Pop()
            {
                HeapItem top = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                last--;
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, smallest = i;
                    if (l <= last && _items[l].F < _items[smallest].F) smallest = l;
                    if (r <= last && _items[r].F < _items[smallest].F) smallest = r;
                    if (smallest == i) break;
                    var tmp = _items[i]; _items[i] = _items[smallest]; _items[smallest] = tmp;
                    i = smallest;
                }
                return top;
            }
        }

        private sealed class Floor
        {
            public string Label;
            public float FloorY;
            public float OriginX;
            public float OriginZ;
            public float CellSize;
            public int Nx;
            public int Nz;
            public string[] Rows;

            public static Floor From(BakeFloor raw)
            {
                if (raw == null || raw.frame == null || raw.bitmap_rows == null) return null;
                Floor f = new Floor
                {
                    Label = raw.label,
                    FloorY = raw.floor_y,
                    OriginX = raw.frame.origin_x,
                    OriginZ = raw.frame.origin_z,
                    CellSize = raw.frame.cell_size,
                    Nx = raw.frame.nx,
                    Nz = raw.frame.nz,
                    Rows = raw.bitmap_rows,
                };
                if (f.Rows.Length != f.Nx)
                {
                    if (Main.Log != null) Main.Log.LogError("SimpleNavPlanner.Floor: " + f.Label + " rows=" + f.Rows.Length + " nx=" + f.Nx);
                    return null;
                }
                return f;
            }

            public bool InBounds(int ix, int iz) => ix >= 0 && ix < Nx && iz >= 0 && iz < Nz;

            public bool Navigable(int ix, int iz)
            {
                if (!InBounds(ix, iz)) return false;
                string row = Rows[ix];
                return row != null && iz < row.Length && row[iz] == NavigableChar;
            }

            public Vector2 CellToWorld(int ix, int iz)
            {
                float wx = OriginX + ix * CellSize + CellSize * 0.5f;
                float wz = OriginZ + iz * CellSize + CellSize * 0.5f;
                return new Vector2(wx, wz);
            }

            public void WorldToCell(float wx, float wz, out int ix, out int iz)
            {
                ix = Mathf.FloorToInt((wx - OriginX) / CellSize);
                iz = Mathf.FloorToInt((wz - OriginZ) / CellSize);
            }

            public bool NearestNavigable(float wx, float wz, float maxRadiusM, out int rIx, out int rIz)
            {
                int cx, cz;
                WorldToCell(wx, wz, out cx, out cz);
                if (Navigable(cx, cz)) { rIx = cx; rIz = cz; return true; }
                int maxR = Mathf.Max(1, Mathf.CeilToInt(maxRadiusM / CellSize));
                for (int r = 1; r <= maxR; r++)
                {
                    // Top and bottom rows of the ring.
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Navigable(cx + dx, cz - r)) { rIx = cx + dx; rIz = cz - r; return true; }
                        if (Navigable(cx + dx, cz + r)) { rIx = cx + dx; rIz = cz + r; return true; }
                    }
                    // Left and right columns of the ring (excluding corners already checked).
                    for (int dz = -r + 1; dz <= r - 1; dz++)
                    {
                        if (Navigable(cx - r, cz + dz)) { rIx = cx - r; rIz = cz + dz; return true; }
                        if (Navigable(cx + r, cz + dz)) { rIx = cx + r; rIz = cz + dz; return true; }
                    }
                }
                rIx = 0; rIz = 0;
                return false;
            }
        }

#pragma warning disable CS0649
        [DataContract]
        private class BakeDoc
        {
            [DataMember] public BakeParams @params;
            [DataMember] public BakeFloor[] floors;
            [DataMember] public InterFloorEdges inter_floor_edges;
        }

        [DataContract]
        private class BakeParams
        {
            [DataMember] public float cell_size_m;
            [DataMember] public float capsule_radius_m;
        }

        [DataContract]
        private class BakeFloor
        {
            [DataMember] public string label;
            [DataMember] public float floor_y;
            [DataMember] public BakeFrame frame;
            [DataMember] public string[] bitmap_rows;
        }

        [DataContract]
        private class BakeFrame
        {
            [DataMember] public float origin_x;
            [DataMember] public float origin_z;
            [DataMember] public float cell_size;
            [DataMember] public int nx;
            [DataMember] public int nz;
        }

        [DataContract]
        private class InterFloorEdges
        {
            [DataMember] public StairRampEdge[] stair_ramp;
            [DataMember] public TeleporterEdge[] teleporter;
        }

        [DataContract]
        private class StairRampEdge
        {
            [DataMember] public string source_path;
            [DataMember] public StairCellEndpoint ground;
            [DataMember] public StairCellEndpoint upper;
            [DataMember] public float cost_m;
        }

        [DataContract]
        private class StairCellEndpoint
        {
            [DataMember] public int[] cell;
            [DataMember] public float floor_y;
        }

        [DataContract]
        private class TeleporterEdge
        {
            [DataMember] public string source_name;
            [DataMember] public TeleporterEndpoint up;
            [DataMember] public TeleporterEndpoint down;
            [DataMember] public float cost_m;
        }

        [DataContract]
        private class TeleporterEndpoint
        {
            [DataMember] public float[] world_xyz;
        }
#pragma warning restore CS0649
    }
}
