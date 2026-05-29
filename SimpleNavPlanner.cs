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
        private const string SceneNavigationDataFileName = "thirdpersongreybox-navigation-data.json";
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
        // Radius to snap a start/goal world position to the nearest navigable
        // cell. 4.0m was too tight for real runtime starts: the player, driven
        // by the executor, often ends up standing beside furniture (fireplace
        // hearth, closet) more than 4m from any navigable cell, so Plan()
        // returned no_path and the user had to reposition and replan
        // repeatedly. 6.0m gives margin for those off-mesh standing spots while
        // staying under MaxInteractionRadiusM (7.5m) and small enough that it
        // won't routinely snap across a wall into the wrong room. Kept in sync
        // with the Python planner's start/goal snap radius for parity.
        private const float NearestNavigableSearchM = 6.0f;
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
        // Door portal waypoints are semantic, not just geometric corners. The executor should
        // approach the opening, cross it, then clear the far side instead of aiming a long
        // segment through the live open door panel.
        private const float DoorOpeningApproachDistanceM = 1.15f;
        private const float DoorOpeningMinimumSpacingSqM = 0.36f;

        private static BakeDoc _bake;
        private static List<Floor> _floors;
        private static float _cellSize;
        private static Dictionary<NodeKey, List<EdgeRef>> _interFloorEdges;
        // Min cost across all inter-floor edges, cached for the heuristic.
        private static float _minInterFloorCost;
        private static bool _loadAttempted;
        private static bool _loadOk;
        private static bool _doorOpeningsLoadAttempted;
        private static Dictionary<string, Vector3> _doorOpeningByName;

        // Names of doors and state-walls treated as open during the last
        // ApplyLiveDoorState call. Captured here so the route-capture path can
        // record overlay state without re-querying the live scene (which may
        // have changed by the time we serialize).
        private static List<string> _lastOpenDoorNames = new List<string>();
        private static List<string> _lastOpenStateWallNames = new List<string>();
        // Door names that are locked at scene load (exporter Door.Locked, carried
        // through the bake). A locked door is a hard block; every other door is
        // treated as openable when planning so A* can route through it and the
        // executor opens it en route. Populated by IndexDoorFreedCells.
        private static readonly HashSet<string> _lockedDoorNames = new HashSet<string>();
        // Monotonic counter for capture filenames. Restarted at process start.
        private static int _captureSeq;

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

            // Reflect live Door.open states into per-floor freed-cell overlays so A* can route
            // through doors that are already open. Closed doors fall back to the bake's
            // closed-pose blockers (which the door-tagging pass still flags so the executor
            // opens them en route).
            ApplyLiveDoorState();

            Floor startFloor = FloorForY(startPos.y, StartFloorMatchToleranceM);
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
            List<NodeKey> unfilteredGoals = new List<NodeKey>(goals);

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
                if (goals.Count != unfilteredGoals.Count &&
                    AStar(startNode, unfilteredGoals, goalFloor.Label, targetPos.x, targetPos.z, out path, out totalCost))
                {
                    goals = unfilteredGoals;
                    if (Main.Log != null)
                        Main.Log.LogInfo("SimpleNavPlanner.Plan: retried with full interaction radius for target=" +
                            (targetName ?? "<null>") + "#" + targetGameObjectId);
                }
                else
                {
                    // Closed-state plan failed. Try a relaxed plan that assumes every door is
                    // open and every state-wall released. If THAT succeeds, autowalk will open
                    // doors as it crosses them via the segment door-tags from TagDoors().
                    // Bedroom-from-bedroom is the canonical case: at scene load every door is
                    // closed, so the closed-state planner finds nothing. Relax + tag gives the
                    // player a single route that walks them to the first gating door, opens
                    // it, continues, opens the next, etc.
                    if (TryRelaxedPlan(startNode, goals, goalFloor.Label, targetPos,
                                       unfilteredGoals, out path, out totalCost, out List<NodeKey> relaxedGoals))
                    {
                        goals = relaxedGoals;
                        if (Main.Log != null)
                            Main.Log.LogInfo("SimpleNavPlanner.Plan: retried with all-doors-open for target=" +
                                (targetName ?? "<null>") + "#" + targetGameObjectId +
                                " (autowalk will open gating doors en route)");
                    }
                    else
                    {
                        if (Main.Log != null) Main.Log.LogWarning("SimpleNavPlanner.Plan: no_path target=" + (targetName ?? "<null>") + "#" + targetGameObjectId);
                        LastFailure = PlanFailure.NoPath;
                        return null;
                    }
                }
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
            List<Vector3> rawRouteWaypoints = new List<Vector3>(waypoints.Count);
            for (int i = 0; i < waypoints.Count; i++)
            {
                NodeKey w = waypoints[i];
                Floor f = FloorByLabel(w.Floor);
                if (f == null) continue;
                Vector2 xz = f.CellToWorld(w.Ix, w.Iz);
                rawRouteWaypoints.Add(new Vector3(xz.x, f.FloorY, xz.y));
            }
            AddRouteWaypoints(rawRouteWaypoints, segmentDoorNames, route);
            route.EnsureSemanticWaypoints();
            while (route.SegmentDoorNames.Count < route.Waypoints.Count - 1)
                route.SegmentDoorNames.Add(new List<string>(0));

            if (ModConfig.CaptureNavRoutes)
                TryCaptureRoute(startPos, targetPos, radius, targetName, startFloor.Label, route, totalCost);

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
                    SimpleNavWaypoint semantic = i < route.SemanticWaypoints.Count ? route.SemanticWaypoints[i] : null;
                    Main.Log.LogInfo("  wp[" + i + "]=(" + w.x.ToString("F2") + ", " + w.y.ToString("F2") + ", " + w.z.ToString("F2") + ")");
                    if (semantic != null && semantic.Kind != SimpleNavWaypointKind.Navigation)
                    {
                        Main.Log.LogInfo("    semantic kind=" + semantic.Kind +
                            " door=" + (string.IsNullOrEmpty(semantic.DoorName) ? "<none>" : semantic.DoorName));
                    }
                }
            }
            return route;
        }

        // ---- runtime capture for offline planner-parity check ----
        //
        // Writes each successful Plan() output to BepInEx/plugins/c_sharp_routes/
        // route_<unix>_<seq>.json when ModConfig.CaptureNavRoutes is true. The
        // file shape matches what scripts/check_planner_parity.py expects:
        // target name/position/radius, start position/floor, the live overlay
        // door+wall name sets, and the final waypoint sequence. Failures here
        // are swallowed — capture is diagnostic and must never break gameplay.
        private static void TryCaptureRoute(
            Vector3 startPos, Vector3 targetPos, float radius, string targetName,
            string startFloorLabel, SimpleNavRoute route, float totalCost)
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "c_sharp_routes");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int seq = System.Threading.Interlocked.Increment(ref _captureSeq);
                string path = Path.Combine(dir, "route_" + unix + "_" + seq + ".json");

                var sb = new System.Text.StringBuilder(512);
                sb.Append("{\n");
                sb.Append("  \"target_name\": ").Append(JsonString(targetName)).Append(",\n");
                sb.Append("  \"target_position\": [")
                    .Append(targetPos.x.ToString("F4")).Append(", ")
                    .Append(targetPos.y.ToString("F4")).Append(", ")
                    .Append(targetPos.z.ToString("F4")).Append("],\n");
                sb.Append("  \"target_interaction_radius\": ").Append(radius.ToString("F4")).Append(",\n");
                sb.Append("  \"start_position\": [")
                    .Append(startPos.x.ToString("F4")).Append(", ")
                    .Append(startPos.y.ToString("F4")).Append(", ")
                    .Append(startPos.z.ToString("F4")).Append("],\n");
                sb.Append("  \"start_floor\": ").Append(JsonString(startFloorLabel)).Append(",\n");
                // The planner routes through every unlocked door (the executor
                // opens path-doors en route), so the offline parity re-plan must
                // do the same — emit the "unlocked" mode string, not just the
                // live-open names. The Python Planner accepts doors_open="unlocked".
                sb.Append("  \"doors_open\": \"unlocked\",\n");
                sb.Append("  \"state_walls_open\": ").Append(JsonStringArray(_lastOpenStateWallNames)).Append(",\n");
                sb.Append("  \"cost_m\": ").Append(totalCost.ToString("F4")).Append(",\n");
                sb.Append("  \"waypoints\": [\n");
                for (int i = 0; i < route.Waypoints.Count; i++)
                {
                    Vector3 w = route.Waypoints[i];
                    sb.Append("    [")
                        .Append(w.x.ToString("F4")).Append(", ")
                        .Append(w.y.ToString("F4")).Append(", ")
                        .Append(w.z.ToString("F4")).Append("]");
                    if (i < route.Waypoints.Count - 1) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append("  ]\n");
                sb.Append("}\n");
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                if (Main.Log != null)
                    Main.Log.LogWarning("SimpleNavPlanner.TryCaptureRoute failed: " + ex.Message);
            }
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') { sb.Append('\\').Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string JsonStringArray(List<string> items)
        {
            if (items == null || items.Count == 0) return "[]";
            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(JsonString(items[i]));
            }
            sb.Append("]");
            return sb.ToString();
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
            IndexDoorFreedCells();
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
            if (ife != null && ife.stair_ramp != null)
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
            if (ife != null && ife.teleporter != null)
            {
                for (int i = 0; i < ife.teleporter.Length; i++)
                {
                    TeleporterEdge t = ife.teleporter[i];
                    if (t == null || t.up == null || t.up.world_xyz == null || t.up.world_xyz.Length < 3) continue;
                    Floor upFloor = FloorForY(t.up.world_xyz[1], FloorMatchToleranceM);
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

        // Index per-door and per-state-wall freed_cells from the bake into each floor's
        // name→cells maps. The planner does not enable any overlay at load time;
        // ApplyLiveWorldState() (called at the top of every Plan()) installs the current set
        // based on live Door.open / SlidingDoor.open / DresserWall.collider.enabled values.
        private static void IndexDoorFreedCells()
        {
            if (_bake?.floors == null) return;
            _lockedDoorNames.Clear();
            for (int fi = 0; fi < _bake.floors.Length; fi++)
            {
                BakeFloor raw = _bake.floors[fi];
                if (raw == null) continue;
                Floor floor = FloorByLabel(raw.label);
                if (floor == null) continue;
                if (raw.doors != null)
                {
                    for (int di = 0; di < raw.doors.Length; di++)
                    {
                        DoorRecord d = raw.doors[di];
                        if (d == null || string.IsNullOrEmpty(d.name) || d.freed_cells == null) continue;
                        // Locked is authoritative regardless of record ordering.
                        if (d.locked) _lockedDoorNames.Add(d.name);
                        HashSet<long> cells;
                        if (!floor.DoorFreedByName.TryGetValue(d.name, out cells))
                        {
                            cells = new HashSet<long>();
                            floor.DoorFreedByName[d.name] = cells;
                        }
                        for (int ci = 0; ci < d.freed_cells.Length; ci++)
                        {
                            int[] pair = d.freed_cells[ci];
                            if (pair == null || pair.Length < 2) continue;
                            cells.Add(Floor.PackCell(pair[0], pair[1]));
                        }
                    }
                }
                if (raw.state_walls != null)
                {
                    for (int wi = 0; wi < raw.state_walls.Length; wi++)
                    {
                        StateWallRecord w = raw.state_walls[wi];
                        if (w == null || string.IsNullOrEmpty(w.name) || w.freed_cells == null) continue;
                        HashSet<long> cells;
                        if (!floor.StateWallFreedByName.TryGetValue(w.name, out cells))
                        {
                            cells = new HashSet<long>();
                            floor.StateWallFreedByName[w.name] = cells;
                        }
                        for (int ci = 0; ci < w.freed_cells.Length; ci++)
                        {
                            int[] pair = w.freed_cells[ci];
                            if (pair == null || pair.Length < 2) continue;
                            cells.Add(Floor.PackCell(pair[0], pair[1]));
                        }
                    }
                }
            }
        }

        // Mirror live Door + SlidingDoor + DresserWall states into each floor's
        // ExtraNavigable set. Doors (hinges + sliders) expose `open`; state-gated walls
        // (DresserWall) expose `collider.enabled`. Anything currently open / released
        // contributes its bake record's freed cells. Unknown names (components without a
        // matching bake record) are silently ignored.
        private static void ApplyLiveDoorState()
        {
            for (int i = 0; i < _floors.Count; i++)
                _floors[i].ExtraNavigable.Clear();
            _lastOpenDoorNames.Clear();
            _lastOpenStateWallNames.Clear();

            int totalDoorsFound = 0;
            int totalDoorsOpen = 0;
            int totalDoorsMatched = 0;
            int totalDoorsUnmatched = 0;
            int totalFreed = 0;

            Door[] hinges = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < hinges.Length; i++)
            {
                Door door = hinges[i];
                if (door == null || door.gameObject == null) continue;
                totalDoorsFound++;
                if (!door.open) continue;
                totalDoorsOpen++;
                _lastOpenDoorNames.Add(door.gameObject.name);
                if (UnionDoorFreedForName(door.gameObject.name, ref totalFreed)) totalDoorsMatched++;
                else totalDoorsUnmatched++;
            }

            SlidingDoor[] sliders = UnityEngine.Object.FindObjectsOfType<SlidingDoor>();
            for (int i = 0; i < sliders.Length; i++)
            {
                SlidingDoor door = sliders[i];
                if (door == null || door.gameObject == null) continue;
                totalDoorsFound++;
                if (!door.open) continue;
                totalDoorsOpen++;
                _lastOpenDoorNames.Add(door.gameObject.name);
                if (UnionDoorFreedForName(door.gameObject.name, ref totalFreed)) totalDoorsMatched++;
                else totalDoorsUnmatched++;
            }

            // State-gated walls: collider disabled ⇒ released ⇒ freed cells contributed.
            // Currently this is just DresserWall (the post-leave_house bedroom gate); future
            // mechanisms (MovingDateable Locked/Unlocked variants) would slot in here.
            int totalWallsFound = 0;
            int totalWallsReleased = 0;
            int totalWallsMatched = 0;
            int totalWallsUnmatched = 0;
            DresserWall[] dressers = UnityEngine.Object.FindObjectsOfType<DresserWall>();
            for (int i = 0; i < dressers.Length; i++)
            {
                DresserWall dw = dressers[i];
                if (dw == null || dw.gameObject == null || dw.collider == null) continue;
                totalWallsFound++;
                if (dw.collider.enabled) continue;
                totalWallsReleased++;
                // Bake records the wall by its collider's GameObject name (e.g.
                // SM_Walls_Bedroom_2_Daemon), not the DresserWall component's owner.
                string colliderName = dw.collider.gameObject != null ? dw.collider.gameObject.name : null;
                if (!string.IsNullOrEmpty(colliderName))
                    _lastOpenStateWallNames.Add(colliderName);
                if (UnionStateWallFreedForName(colliderName, ref totalFreed)) totalWallsMatched++;
                else totalWallsUnmatched++;
            }

            // Openable-door pass: plan through every door the player CAN open
            // (everything except the locked ones), regardless of its current live
            // open state. The autowalk executor opens any door on the route as it
            // reaches it (see [[project-navigation-door-handling-rules]]), so a
            // route that needs a closed-but-unlocked door is valid — the planner
            // must not return no_path just because the door happens to be shut
            // right now. Without this, a target behind a closed door is
            // unreachable and ToggleAutoWalk reports NoPath before the executor
            // ever gets a chance to open it (observed in-game: MagnifyingGlass
            // no_path with open=0). Locked doors stay hard-blocked. Mirrors the
            // Python planner's doors_open="unlocked". See
            // [[project-navigation-sweep-follower-doorstate-fix]].
            int openableFreed = 0;
            for (int fi = 0; fi < _floors.Count; fi++)
            {
                Floor f = _floors[fi];
                foreach (var kvp in f.DoorFreedByName)
                {
                    if (_lockedDoorNames.Contains(kvp.Key)) continue;
                    int before = f.ExtraNavigable.Count;
                    f.ExtraNavigable.UnionWith(kvp.Value);
                    openableFreed += f.ExtraNavigable.Count - before;
                }
            }
            totalFreed += openableFreed;

            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavPlanner.ApplyLiveDoorState: doors=" + totalDoorsFound +
                    " (hinges=" + hinges.Length + " sliders=" + sliders.Length + ")" +
                    " open=" + totalDoorsOpen + " matched=" + totalDoorsMatched + " unmatched=" + totalDoorsUnmatched +
                    " | openableFreed=" + openableFreed + " lockedDoors=" + _lockedDoorNames.Count +
                    " | stateWalls=" + totalWallsFound + " released=" + totalWallsReleased +
                    " matched=" + totalWallsMatched + " unmatched=" + totalWallsUnmatched +
                    " | freedCells=" + totalFreed);
        }

        private static bool UnionDoorFreedForName(string name, ref int totalFreed)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool anyMatch = false;
            for (int fi = 0; fi < _floors.Count; fi++)
            {
                Floor f = _floors[fi];
                if (f.DoorFreedByName.TryGetValue(name, out HashSet<long> cells))
                {
                    f.ExtraNavigable.UnionWith(cells);
                    totalFreed += cells.Count;
                    anyMatch = true;
                }
            }
            return anyMatch;
        }

        private static bool UnionStateWallFreedForName(string name, ref int totalFreed)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool anyMatch = false;
            for (int fi = 0; fi < _floors.Count; fi++)
            {
                Floor f = _floors[fi];
                if (f.StateWallFreedByName.TryGetValue(name, out HashSet<long> cells))
                {
                    f.ExtraNavigable.UnionWith(cells);
                    totalFreed += cells.Count;
                    anyMatch = true;
                }
            }
            return anyMatch;
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

        // Half the inter-floor gap (~13m) plus margin. Used as the START floor
        // tolerance so a player caught MID-STAIRCASE (Y between the two floor
        // planes, e.g. Y=2 while descending) still resolves to the nearest
        // floor instead of failing with StartOffBake. The bake models only the
        // two discrete floor planes, so any standable mid-stair Y is up to ~6.5m
        // from a floor; the tight default tolerance (2m) wrongly rejected it and
        // the planner refused to plan from a spot the player was actually
        // standing on. Snapping to the nearest floor is sufficient: the stairs
        // connect both floors via the inter-floor edge, so A* finds a valid path
        // regardless of which floor the mid-stair player snaps to.
        // See [[project-navigation-executor-corner-stall]].
        private const float StartFloorMatchToleranceM = 7.0f;

        private static Floor FloorForY(float y, float toleranceM)
        {
            Floor best = null;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < _floors.Count; i++)
            {
                float d = Mathf.Abs(_floors[i].FloorY - y);
                if (d < bestD) { bestD = d; best = _floors[i]; }
            }
            return bestD <= toleranceM ? best : null;
        }

        // Pick the floor a player stands on to interact with a target at world Y.
        // Wall-mounted props (MagnifyingGlass mount at Y=4.09), table-top items
        // (Y=1-3), upper-cupboard contents (Y=6-9), and recessed ceiling lights
        // (Y=12.2 in the ground-floor ceiling) are all ground-accessed — the
        // player looks up at them and interacts via beam/glasses. The rule
        // picks the highest floor with floor_y - UpwardSlack <= y.
        //
        // UpwardSlack = 0.3m: tight enough that Y=12.2 (ground ceiling) falls
        // below the upper-floor cutoff and routes to ground. The 0.3m absorbs
        // floor-mesh-Y model quirks (ground SM_Floor mesh at -0.57 vs FloorY -0.5).
        private static Floor FloorForTargetY(float y)
        {
            const float UpwardSlackM = 0.1f;
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

        // Temporarily union every door's and state-wall's freed cells into ExtraNavigable
        // and re-run A*. On exit, ExtraNavigable is restored to its prior contents (i.e. the
        // live state from ApplyLiveDoorState). Caller treats a success here as "path exists
        // assuming the player will open the right doors along the way" — segment door-tags
        // (TagDoors) then handle the actual opening at runtime.
        private static bool TryRelaxedPlan(NodeKey start, List<NodeKey> goals, string goalFloorLabel, Vector3 targetPos,
            List<NodeKey> unfilteredGoals, out List<NodeKey> path, out float totalCost, out List<NodeKey> goalsUsed)
        {
            path = null;
            totalCost = 0f;
            goalsUsed = goals;

            // Snapshot current state and union all known freed cells.
            HashSet<long>[] saved = new HashSet<long>[_floors.Count];
            for (int i = 0; i < _floors.Count; i++)
            {
                Floor f = _floors[i];
                saved[i] = new HashSet<long>(f.ExtraNavigable);
                foreach (var kvp in f.DoorFreedByName)
                    f.ExtraNavigable.UnionWith(kvp.Value);
                foreach (var kvp in f.StateWallFreedByName)
                    f.ExtraNavigable.UnionWith(kvp.Value);
            }

            try
            {
                if (AStar(start, goals, goalFloorLabel, targetPos.x, targetPos.z, out path, out totalCost))
                    return true;
                if (goals.Count != unfilteredGoals.Count &&
                    AStar(start, unfilteredGoals, goalFloorLabel, targetPos.x, targetPos.z, out path, out totalCost))
                {
                    goalsUsed = unfilteredGoals;
                    return true;
                }
                return false;
            }
            finally
            {
                for (int i = 0; i < _floors.Count; i++)
                {
                    Floor f = _floors[i];
                    f.ExtraNavigable.Clear();
                    f.ExtraNavigable.UnionWith(saved[i]);
                }
            }
        }

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
            var closed = new HashSet<NodeKey>();
            gScore[start] = 0f;
            open.Push(new HeapItem(Heuristic(start, goalFloorLabel, goalWx, goalWz), 0f, start));

            while (open.Count > 0)
            {
                HeapItem cur = open.Pop();
                NodeKey node = cur.Node;
                if (cur.G > gScore[node] + 1e-6f) continue;
                if (!closed.Add(node))
                    continue;

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
                        int ddx = NeighborDx[i];
                        int ddz = NeighborDz[i];
                        int nx = node.Ix + ddx;
                        int nz = node.Iz + ddz;
                        if (!f.Navigable(nx, nz)) continue;
                        // Corner-cut prevention: a diagonal step is only valid if
                        // both orthogonally-adjacent cells are navigable. Without
                        // this, A* slips through a corner where two blockers touch
                        // — a sub-capsule pinhole the player can't physically fit
                        // (e.g. the 0.2m diagonal leak through SM_Walls_Bedroom that
                        // routed autowalk into a dead pocket instead of the real
                        // door). Must match the Python planner. See
                        // [[project-navigation-executor-corner-stall]].
                        if (ddx != 0 && ddz != 0 &&
                            !(f.Navigable(node.Ix + ddx, node.Iz) && f.Navigable(node.Ix, node.Iz + ddz)))
                            continue;
                        float step = (ddx != 0 && ddz != 0) ? Sqrt2 : 1f;
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
            int lastAnchorIdx = 0;
            while (i < path.Count)
            {
                NodeKey node = path[i];
                NodeKey prev = path[i - 1];
                // Any non-grid hop forces a waypoint (floor change, virtual node).
                if (node.Floor != los[los.Count - 1].Floor || IsVirtual(node) || IsVirtual(los[los.Count - 1]))
                {
                    if (!prev.Equals(los[los.Count - 1])) los.Add(prev);
                    if (!node.Equals(los[los.Count - 1])) los.Add(node);
                    lastAnchorIdx = i;
                    i++;
                    continue;
                }
                if (SegmentIsClear(los[los.Count - 1], node))
                {
                    i++;
                    continue;
                }
                // The straight segment from anchor to node isn't clear. Anchor the previous
                // cell and continue searching from there. Guard against the pathological
                // case where SegmentIsClear flickers (e.g. sampling a freed-cells overlay
                // discretizes into a non-navigable cell between two A*-adjacent grid cells);
                // if we'd be re-anchoring at the same index we already anchored from, force
                // advance so the loop cannot spin.
                if (i - 1 <= lastAnchorIdx)
                {
                    // Can't make progress by re-anchoring; force advance.
                    los.Add(node);
                    lastAnchorIdx = i;
                    i++;
                }
                else
                {
                    los.Add(prev);
                    lastAnchorIdx = i - 1;
                }
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

        // Dense line walk through cells; all touched cells must be navigable. The original
        // Bresenham walk could miss cells that a long diagonal segment grazed, which allowed
        // smoothing to create routes that skimmed wall-like blockers such as the living-room
        // fireplace. Sampling at half-cell spacing is deliberately conservative and only affects
        // waypoint smoothing, not A* graph connectivity.
        private static bool SegmentIsClear(NodeKey a, NodeKey b)
        {
            if (a.Floor != b.Floor || IsVirtual(a)) return false;
            Floor floor = FloorByLabel(a.Floor);
            if (floor == null) return false;

            Vector2 start = floor.CellToWorld(a.Ix, a.Iz);
            Vector2 end = floor.CellToWorld(b.Ix, b.Iz);
            float dist = Vector2.Distance(start, end);
            float sampleStep = Mathf.Max(0.02f, floor.CellSize * 0.35f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / sampleStep));
            int lastIx = int.MinValue;
            int lastIz = int.MinValue;

            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float wx = Mathf.Lerp(start.x, end.x, t);
                float wz = Mathf.Lerp(start.y, end.y, t);
                floor.WorldToCell(wx, wz, out int ix, out int iz);
                if (ix == lastIx && iz == lastIz)
                    continue;

                if (!floor.Navigable(ix, iz))
                    return false;

                // Reject corner-cuts: if the sampled cell moved diagonally from
                // the previous one, both orthogonal in-between cells must be
                // navigable. Point-sampling alone can hop a 1-cell corner pinhole
                // the player can't fit through, re-introducing impassable-gap
                // routes that the A* corner-cut prevention closes. Must match the
                // Python planner. See [[project-navigation-executor-corner-stall]].
                if (lastIx != int.MinValue)
                {
                    int ddx = ix - lastIx;
                    int ddz = iz - lastIz;
                    if (ddx != 0 && ddz != 0 &&
                        !(floor.Navigable(lastIx + ddx, lastIz) && floor.Navigable(lastIx, lastIz + ddz)))
                        return false;
                }

                lastIx = ix;
                lastIz = iz;
            }

            return true;
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
                return SelectBestTargetCollider(io);
            }
            return null;
        }

        private static Collider SelectBestTargetCollider(InteractableObj interactable)
        {
            if (interactable == null || interactable.gameObject == null)
                return null;

            Collider best = null;
            float bestScore = float.PositiveInfinity;
            Vector3 anchor = interactable.transform.position;

            ConsiderTargetColliders(interactable.GetComponents<Collider>(), anchor, 0f, ref best, ref bestScore);
            ConsiderTargetColliders(interactable.GetComponentsInChildren<Collider>(includeInactive: false), anchor, 1f, ref best, ref bestScore);
            ConsiderTargetColliders(interactable.GetComponentsInParent<Collider>(includeInactive: false), anchor, 2f, ref best, ref bestScore);

            return best;
        }

        private static void ConsiderTargetColliders(
            Collider[] colliders,
            Vector3 anchor,
            float penalty,
            ref Collider best,
            ref float bestScore)
        {
            if (colliders == null)
                return;

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || c.isTrigger || !c.enabled || c.gameObject == null || !c.gameObject.activeInHierarchy)
                    continue;

                Vector3 nearest = c.ClosestPointOnBounds(anchor);
                float score = (nearest - anchor).sqrMagnitude + penalty;
                if (score >= bestScore)
                    continue;

                best = c;
                bestScore = score;
            }
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

        private static void AddRouteWaypoints(
            List<Vector3> rawWaypoints,
            List<List<string>> rawSegmentDoors,
            SimpleNavRoute route)
        {
            if (rawWaypoints == null || rawWaypoints.Count == 0)
                return;

            route.AddWaypoint(rawWaypoints[0], SimpleNavWaypointKind.Navigation);
            for (int i = 0; i < rawWaypoints.Count - 1; i++)
            {
                Vector3 b = rawWaypoints[i + 1];
                List<string> doors = i < rawSegmentDoors.Count && rawSegmentDoors[i] != null
                    ? rawSegmentDoors[i]
                    : new List<string>(0);
                AddSemanticDoorWaypoint(route, doors, b, i == rawWaypoints.Count - 2 ? SimpleNavWaypointKind.Target : SimpleNavWaypointKind.Navigation, null);
            }
        }

        private static void AddSemanticDoorWaypoint(
            SimpleNavRoute route,
            List<string> doors,
            Vector3 waypoint,
            SimpleNavWaypointKind kind,
            string doorName)
        {
            if (route.Waypoints.Count > 0 &&
                FlatDistanceSq(route.Waypoints[route.Waypoints.Count - 1], waypoint) <= 0.04f)
                return;

            route.SegmentDoorNames.Add(new List<string>(doors ?? new List<string>(0)));
            route.AddWaypoint(waypoint, kind, doorName);
        }

        private static bool TryGetDoorOpeningWaypoints(
            List<string> doorNames,
            Vector3 segmentStart,
            Vector3 segmentEnd,
            out string doorName,
            out Vector3 approach,
            out Vector3 opening,
            out Vector3 exit)
        {
            doorName = null;
            approach = Vector3.zero;
            opening = Vector3.zero;
            exit = Vector3.zero;
            if (doorNames == null || doorNames.Count == 0)
                return false;

            for (int i = 0; i < doorNames.Count; i++)
            {
                if (!TryGetExportedDoorOpening(doorNames[i], out Vector3 center))
                    continue;

                center.y = (segmentStart.y + segmentEnd.y) * 0.5f;
                Vector3 segment = segmentEnd - segmentStart;
                segment.y = 0f;
                if (segment.sqrMagnitude <= 0.0001f)
                    continue;

                Vector3 direction = segment.normalized;
                Vector3 toCenter = center - segmentStart;
                toCenter.y = 0f;
                float projection = Vector3.Dot(toCenter, segment) / segment.sqrMagnitude;
                if (projection < 0.05f || projection > 0.95f)
                    continue;

                Vector3 candidateApproach = center - direction * DoorOpeningApproachDistanceM;
                Vector3 candidateExit = center + direction * DoorOpeningApproachDistanceM;
                candidateApproach.y = center.y;
                candidateExit.y = center.y;

                if (FlatDistanceSq(segmentStart, candidateApproach) <= DoorOpeningMinimumSpacingSqM ||
                    FlatDistanceSq(candidateApproach, center) <= DoorOpeningMinimumSpacingSqM ||
                    FlatDistanceSq(center, candidateExit) <= DoorOpeningMinimumSpacingSqM ||
                    FlatDistanceSq(candidateExit, segmentEnd) <= DoorOpeningMinimumSpacingSqM)
                    continue;

                doorName = doorNames[i];
                approach = candidateApproach;
                opening = center;
                exit = candidateExit;
                return true;
            }

            return false;
        }

        private static bool TryGetExportedDoorOpening(string doorName, out Vector3 center)
        {
            center = Vector3.zero;
            EnsureDoorOpeningsLoaded();
            return _doorOpeningByName != null &&
                !string.IsNullOrEmpty(doorName) &&
                _doorOpeningByName.TryGetValue(doorName, out center);
        }

        private static void EnsureDoorOpeningsLoaded()
        {
            if (_doorOpeningsLoadAttempted)
                return;

            _doorOpeningsLoadAttempted = true;
            _doorOpeningByName = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(Paths.PluginPath, SceneNavigationDataFileName);
            if (!File.Exists(path))
                return;

            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SceneNavigationDataDoc));
                    SceneNavigationDataDoc doc = serializer.ReadObject(stream) as SceneNavigationDataDoc;
                    if (doc == null || doc.OcclusionPortals == null)
                        return;

                    for (int i = 0; i < doc.OcclusionPortals.Length; i++)
                    {
                        ScenePortalRecord portal = doc.OcclusionPortals[i];
                        if (portal == null || string.IsNullOrEmpty(portal.ParentDoorName) || portal.Center == null)
                            continue;

                        _doorOpeningByName[portal.ParentDoorName] = new Vector3(portal.Center.x, portal.Center.y, portal.Center.z);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null)
                    Main.Log.LogWarning("SimpleNavPlanner: failed to load door opening portals from " + path + " ex=" + ex.Message);
            }
        }

        private static float FlatDistanceSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
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
            // Cells freed by currently-open doors (set per-Plan() from live Door.open state).
            // Packed as (long)ix << 32 | (uint)iz so we can use a single HashSet.
            public readonly HashSet<long> ExtraNavigable = new HashSet<long>();
            // Per-door freed-cells indexed from the bake's doors[*].freed_cells. Multiple
            // door records may share a GameObject name (different cupboards with identical
            // names) — the indexer unions their cells.
            public readonly Dictionary<string, HashSet<long>> DoorFreedByName =
                new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            // Per-state-wall freed-cells, parallel to DoorFreedByName. State-gated walls
            // (DresserWall and similar) contribute freed cells when their collider is
            // disabled at runtime.
            public readonly Dictionary<string, HashSet<long>> StateWallFreedByName =
                new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

            public static long PackCell(int ix, int iz) => ((long)ix << 32) | (uint)iz;

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
                if (ExtraNavigable.Count > 0 && ExtraNavigable.Contains(PackCell(ix, iz)))
                    return true;
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
            [DataMember] public DoorRecord[] doors;
            [DataMember] public StateWallRecord[] state_walls;
        }

        [DataContract]
        private class DoorRecord
        {
            [DataMember] public string name;
            [DataMember] public int[][] freed_cells;
            [DataMember] public bool locked;
            [DataMember] public bool default_open;
        }

        [DataContract]
        private class StateWallRecord
        {
            [DataMember] public string name;
            [DataMember] public string release_mechanism;
            [DataMember] public int[][] freed_cells;
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

        [DataContract]
        private class SceneNavigationDataDoc
        {
            [DataMember] public ScenePortalRecord[] OcclusionPortals;
        }

        [DataContract]
        private class ScenePortalRecord
        {
            [DataMember] public string ParentDoorName;
            [DataMember] public SceneVector3 Center;
        }

        [DataContract]
        private class SceneVector3
        {
            [DataMember] public float x;
            [DataMember] public float y;
            [DataMember] public float z;
        }
#pragma warning restore CS0649
    }
}
