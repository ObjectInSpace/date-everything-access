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
        // Fallback interaction radius for the DESTINATION door-tag rule when a target's own
        // InteractionRadius is 0/unknown. Doors in this scene are 7.5m; this only applies to a
        // degenerate target. (The old constant DoorTagRadiusM=2.5 was a hand-tuned compromise; it
        // is GONE — on-path tagging now uses each door's geometric opening radius, and the
        // destination rule uses the game's InteractionRadius. See TagDoors +
        // [[project-navigation-door-tag-radius]], [[project_navigation_container_open_on_interact]].)
        private const float DoorInteractRadiusFallbackM = 7.5f;
        // (No planner-side interaction-radius constant: the planner uses the object's own
        // InteractionRadius verbatim — interaction is gated on radius + line of sight, not on any
        // bound we impose. See the radius handling in Plan() and BetterPlayerControl.cs:493-499.)
        // Radius to snap a start/goal world position to the nearest navigable
        // cell. 4.0m was too tight for real runtime starts: the player, driven
        // by the executor, often ends up standing beside furniture (fireplace
        // hearth, closet) more than 4m from any navigable cell, so Plan()
        // returned no_path and the user had to reposition and replan
        // repeatedly. 6.0m gives margin for those off-mesh standing spots while
        // staying under the game's 7.5m default interaction radius and small enough that it
        // won't routinely snap across a wall into the wrong room. Kept in sync
        // with the Python planner's start/goal snap radius for parity.
        private const float NearestNavigableSearchM = 6.0f;
        private const float FloorMatchToleranceM = 2.0f;
        // Bounded clearance-cost A* + smoother guard. A cell nearer than
        // ClearanceTargetCells (=4 cells = 0.8m) to a wall costs extra metres per missing
        // clearance-cell, so A* prefers the wide lane through a doorway/between furniture
        // WITHOUT detouring where there's no wider option. The smoother then refuses to
        // straighten those margin-keeping curves back against the obstacle. Mirrors
        // plan_object_route.py CLEARANCE_TARGET_CELLS / CLEARANCE_PENALTY_PER_CELL_M. See
        // [[project-navigation-csharp-clearance-port-TODO]],
        // [[project-navigation-clearance-cost-rejected-2026-05-29]] (reconciled: bounded, not flat).
        private const int ClearanceTargetCells = 4;
        private const float ClearancePenaltyPerCellM = 0.15f;
        // Player-capsule + safety margin from the target's collider face. Goal cells whose
        // player-capsule center is closer than this become invalid (overlap), and cells further
        // than ColliderBandOuterM become invalid (too far to interact reliably). The result is
        // a narrow band around the target so A* terminates near the collider rather than
        // anywhere in the InteractionRadius disc.
        private const float TargetColliderClearanceM = 0.5f;
        // (The former TargetColliderBandOuterM 1.5m outer cap was removed: with a real per-cell
        // line-of-sight test, distance within the InteractionRadius is no longer a quality signal,
        // and the cap excluded the stand-back cells that give the cleanest line to an above-floor
        // object. Goal cells are now bounded only by the InteractionRadius disc + LOS preference.)
        // NOTE: the DoorHingeBandInner/OuterM geometric door-approach band was retired. A
        // door target's goal cells now come exclusively from the bake's
        // operable_from_cells (authoritative, swing-arc-aware). If a door lacks those
        // cells the planner fails fast (DoorMissingOperableCells) rather than guessing
        // with the band, so a missing/broken operability data source surfaces instead of
        // being silently papered over. See [[project-navigation-door-operability-cells]].
        private static BakeDoc _bake;
        private static List<Floor> _floors;
        private static float _cellSize;
        private static Dictionary<NodeKey, List<EdgeRef>> _interFloorEdges;
        // Min cost across all inter-floor edges, cached for the heuristic.
        private static float _minInterFloorCost;
        // Ramp interior points (world XYZ) keyed by the directed landing-cell pair, so a
        // route crossing the stair seam can be expanded into a walkable polyline up the
        // run. Direction matters: ascending and descending share geometry but reversed.
        private static Dictionary<(NodeKey, NodeKey), Vector3[]> _stairRampInteriors;
        private static bool _loadAttempted;
        private static bool _loadOk;

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
        // Names of every door the BAKE modelled — the real PASSAGE/closet doors the player walks
        // through or operates from a standpoint. A live GameObject can carry a Door component yet
        // NOT be in here: fridge/cupboard "doors" are CONTAINERS the player opens in place and
        // never walks through, so the exporter's door-detection rule deliberately skips them.
        // The planner uses this to tell the two apart: a target Door IN this set takes the
        // door-operability branch (and a missing operability set is a real bake bug → fail fast);
        // a target Door NOT in this set is a container, routed like any other prop via the
        // collider-band branch (front-approach; its swing is still a footprint blocker for
        // pathing). See [[project-navigation-sweep-three-buckets-2026-06-12]].
        private static readonly HashSet<string> _bakedDoorNames = new HashSet<string>();
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
            // A door target has no operable_from_cells in the bake. We FAIL FAST here
            // rather than fall back to a geometric hinge-band guess: a door with no
            // operability data is a broken/missing data source (bake didn't compute it,
            // e.g. a new scene's door geometry the operability pass didn't handle), and
            // silently routing with a worse approximation would mask that. The log names
            // the door so the producer can be fixed. See
            // [[project-navigation-door-operability-cells]], [[feedback-fix-the-data-source-first]].
            DoorMissingOperableCells,
            // No navigable cell within the target's InteractionRadius has a clear interaction
            // line-of-sight to it. LOS is MANDATORY — the game's interaction needs the camera ray
            // to hit the object (BetterPlayerControl.cs:493-499) — so a cell without it is not a
            // valid standpoint. Rather than route the player to a spot they provably can't interact
            // from, we fail here: the object is reachable but not interactable from anywhere on the
            // mesh (occluded by furniture/geometry, or only reachable from cells the ray can't
            // clear). Names the target so the cause (placement/occluder) can be investigated.
            TargetNoLineOfSight,
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

            // Correct a DEGENERATE target position. Some interactables (animated cushions,
            // curtains, the broken-glass front door, ~30 objects) report a transform.position at
            // their RIG ORIGIN — often near (0,0,0) — tens of metres from where the object's
            // geometry actually is. Planning to that bogus point centres the goal disc on empty
            // space and the object is unreachable. The object's COLLIDER carries the true world
            // location, so when the given targetPos lies OUTSIDE the resolved collider's bounds
            // (with a small pad), substitute the collider's bounds centre. Inside-bounds positions
            // (large objects whose pivot is off-centre but still on the object) are left untouched.
            // See [[project_navigation_target_position_degenerate]].
            Collider posCheckCollider = ResolveTargetCollider(targetGameObjectId);
            if (posCheckCollider != null)
            {
                Bounds b = posCheckCollider.bounds;
                const float pad = 0.5f;
                bool outsideXZ =
                    targetPos.x < b.min.x - pad || targetPos.x > b.max.x + pad ||
                    targetPos.z < b.min.z - pad || targetPos.z > b.max.z + pad;
                if (outsideXZ)
                {
                    Vector3 corrected = b.center;
                    if (Main.Log != null)
                        Main.Log.LogInfo("SimpleNavPlanner.Plan: corrected degenerate targetPos for " +
                            (targetName ?? "<null>") + "#" + targetGameObjectId +
                            " from " + targetPos.ToString("F2") + " to collider centre " + corrected.ToString("F2"));
                    targetPos = corrected;
                }
            }

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

            // Use the game's OWN interaction radius exactly as given — no planner clamp, no default.
            // Interaction is gated purely on `Distance(camera, ClosestPointOnBounds) < InteractionRadius`
            // + line of sight (BetterPlayerControl.cs:493-499). Every real interactable carries a
            // radius (verified: 0/2612 missing; values include 10m and 25m the old 7.5 cap wrongly
            // shrank). A degenerate radius (0 — only the junk "Main Camera" entry, never a real nav
            // target) simply yields no goal cells and is handled by the goals.Count==0 path below.
            float radius = interactionRadius;
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

            // Refine goal cells by target kind:
            //   - Door targets: goals come from the bake's operable_from_cells (handled
            //     just below) — authoritative swing-arc-aware standpoints; the disc above
            //     is discarded for doors.
            //   - Non-door targets: keep the whole interaction-radius disc, drop cells that
            //     overlap the collider (< TargetColliderClearanceM via ClosestPointOnBounds),
            //     then PREFER cells with clear interaction line-of-sight. The game's gate is
            //     radius + LOS, so any in-radius LOS-clear cell is valid — we no longer cap to
            //     a tight near-the-collider band (that excluded the stand-back cells that are
            //     the only clean line to an above-floor object).
            // See [[project_navigation_planner_los_goal_cells_2026_06_13]], [[project-navigation-door-operability-cells]].
            // A live Door component alone doesn't make this a passage door: fridge/cupboard
            // containers carry one too. Only treat it as a door target if the bake actually
            // modelled it (it's in the passage-door set). Container doors — present live, absent
            // from the bake — are dropped to null here so they route through the prop collider
            // branch below: front-approach, swing arc as a footprint blocker, no operable-cell
            // requirement (which would fail-fast on them). See the three-buckets memo.
            Door targetDoor = ResolveTargetDoor(targetGameObjectId);
            if (targetDoor != null && targetDoor.gameObject != null &&
                !_bakedDoorNames.Contains(targetDoor.gameObject.name))
            {
                if (Main.Log != null)
                    Main.Log.LogInfo("SimpleNavPlanner.Plan: target=" + (targetName ?? "<null>") +
                        " (" + targetDoor.gameObject.name + ") has a Door component but isn't a baked " +
                        "passage door — treating as a container/prop (collider-band approach).");
                targetDoor = null;
            }
            // Reuse the collider already resolved for the degenerate-position check above
            // (avoids a second FindObjectsOfType). Null for door targets — they use
            // operable_from_cells, not the collider-LOS goal filter.
            Collider targetCollider = targetDoor == null ? posCheckCollider : null;

            // Door target: the goal set is the bake's operable_from_cells — the
            // authoritative navigable cells the player can stand in to operate this door
            // (excludes the swing arc and the panel, computed from the real Door.cs rule).
            // This is REQUIRED, not best-effort: if a door has no operable cells we FAIL
            // FAST (loud log + DoorMissingOperableCells) instead of guessing with a
            // geometric hinge band. A door without operability data is a broken/missing
            // data source to fix at the producer, not to paper over at runtime. See
            // [[project-navigation-door-operability-cells]], [[feedback-fix-the-data-source-first]].
            if (targetDoor != null && targetDoor.gameObject != null)
            {
                HashSet<long> operable;
                List<NodeKey> opGoals = null;
                if (goalFloor.OperableFromByName.TryGetValue(targetDoor.gameObject.name, out operable)
                    && operable.Count > 0)
                {
                    opGoals = new List<NodeKey>(operable.Count);
                    foreach (long packed in operable)
                    {
                        int ix = (int)(packed >> 32);
                        int iz = (int)(uint)packed;
                        if (goalFloor.Navigable(ix, iz))
                            opGoals.Add(new NodeKey(goalFloor.Label, ix, iz));
                    }
                }
                if (opGoals == null || opGoals.Count == 0)
                {
                    if (Main.Log != null)
                        Main.Log.LogWarning("SimpleNavPlanner.Plan: door target=" +
                            (targetName ?? "<null>") + " (" + targetDoor.gameObject.name +
                            ") on " + goalFloor.Label + " has no operable_from_cells in the bake" +
                            (operable != null && operable.Count > 0 ? " (all off the navigable set)" : "") +
                            " — failing fast; fix the bake's door operability data for this door.");
                    LastFailure = PlanFailure.DoorMissingOperableCells;
                    return null;
                }
                goals = opGoals;
            }
            // Non-door collider target: the game's interaction gate is radius + LINE OF SIGHT, and
            // nothing else — interaction needs the camera ray to HIT the object within
            // InteractionRadius (BetterPlayerControl.cs:493-499). LOS is therefore MANDATORY, not a
            // preference: a cell from which the ray can't reach the object is not a valid standpoint,
            // and routing the player there would put them somewhere they provably can't interact.
            // Distance within the radius is NOT a quality signal, so we do NOT narrow to a tight
            // near-the-collider band (that crude pre-LOS proxy threw away the stand-BACK cells that
            // are the only clean line to an above-floor object). Instead: from the whole radius disc,
            // keep ONLY cells that (a) don't overlap the collider and (b) have clear interaction LOS.
            // If none qualify, the object is reachable but not interactable from anywhere on the
            // mesh → fail fast with TargetNoLineOfSight rather than route to a useless cell.
            // LOS is live physics (C#-only); it doesn't change the route graph, only goal selection.
            // See [[project_navigation_planner_los_goal_cells_2026_06_13]].
            else if (targetCollider != null)
            {
                List<NodeKey> losClear = new List<NodeKey>(goals.Count);
                float innerSq = TargetColliderClearanceM * TargetColliderClearanceM;
                float bestClearance = 0f;
                List<float> clearanceOf = new List<float>(goals.Count);
                for (int i = 0; i < goals.Count; i++)
                {
                    NodeKey g = goals[i];
                    Vector2 xz = goalFloor.CellToWorld(g.Ix, g.Iz);
                    Vector3 cellWorld = new Vector3(xz.x, goalFloor.FloorY + 1.0f, xz.y);
                    Vector3 nearest = targetCollider.ClosestPointOnBounds(cellWorld);
                    float dx = nearest.x - cellWorld.x;
                    float dz = nearest.z - cellWorld.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < innerSq)
                        continue; // cell overlaps the collider — can't stand here
                    float margin = CellLineOfSightClearanceM(goalFloor, g, targetCollider, radius);
                    if (margin >= 0f)
                    {
                        losClear.Add(g);
                        clearanceOf.Add(margin);
                        if (margin > bestClearance) bestClearance = margin;
                    }
                }
                if (losClear.Count > 0)
                {
                    // CLOSEST-CLEAR-CELL: prefer SOLIDLY-clear standpoints over edge-grazing ones.
                    // When any cell clears with real side margin, drop the cells that only barely
                    // clear (margin a full probe-step below the best). This removes the far doorframe-
                    // grazed / furniture-skimming standpoints that pass the boolean test but fail the
                    // game's real ray, while keeping the leg-optimal pick among the robust cells.
                    // See [[project_navigation_noloss_full_classification_2026_06_14]].
                    if (bestClearance > 0f)
                    {
                        float keepAbove = bestClearance - LosProbeSideOffsetsM[0] - 1e-4f;
                        List<NodeKey> solid = new List<NodeKey>(losClear.Count);
                        for (int i = 0; i < losClear.Count; i++)
                            if (clearanceOf[i] >= keepAbove)
                                solid.Add(losClear[i]);
                        goals = solid.Count > 0 ? solid : losClear;
                    }
                    else
                    {
                        goals = losClear;
                    }
                }
                else
                {
                    if (Main.Log != null)
                        Main.Log.LogWarning("SimpleNavPlanner.Plan: no goal cell with interaction line-of-sight for target=" +
                            (targetName ?? "<null>") + "#" + targetGameObjectId +
                            " — object reachable but not interactable from any navigable cell (occluded / placement)");
                    LastFailure = PlanFailure.TargetNoLineOfSight;
                    return null;
                }
            }

            List<NodeKey> path;
            float totalCost;
            if (!AStar(startNode, goals, goalFloor.Label, targetPos.x, targetPos.z, out path, out totalCost))
            {
                // The closed-state plan to the LOS goal cells failed. Retry only with DOORS
                // RELAXED (every door assumed openable, state-walls released) on the SAME goal
                // set — autowalk opens gating doors en route via the segment door-tags. We do NOT
                // widen the goal set back to the full radius disc: LOS is mandatory, so a non-LOS
                // cell is never an acceptable destination even when the LOS cells are unreachable.
                // Bedroom-from-bedroom is the canonical relax case (all doors closed at scene load).
                {
                    if (TryRelaxedPlan(startNode, goals, goalFloor.Label, targetPos,
                                       out path, out totalCost))
                    {
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
            List<List<string>> segmentDoorNames = TagDoors(waypoints, targetName, targetPos, radius);
            // segmentDoorNames is per cell-waypoint segment (length waypoints.Count-1). The
            // ramp-interior insertion below adds waypoints at the stair seam, splitting that
            // one seam segment into several; expandedSegmentDoors mirrors the insertion with
            // empty (no-door) entries so door tags stay aligned to their segments.
            List<List<string>> expandedSegmentDoors = new List<List<string>>(segmentDoorNames.Count);

            SimpleNavRoute route = new SimpleNavRoute();
            route.TargetName = targetName;
            route.TargetGameObjectId = targetGameObjectId;
            route.TargetPosition = targetPos;
            route.TargetInteractionRadius = radius;
            route.TargetIsDatable = targetIsDatable;
            route.TargetInkFileName = targetInkFileName;
            // expandedSegmentDoors[j] is the door list for the segment STARTING at
            // rawRouteWaypoints[j] (the same convention AddRouteWaypoints reads). For each
            // source cell-waypoint i (i < last) we append exactly one door entry —
            // segmentDoorNames[i] — for the segment leaving waypoint i. When we insert K
            // ramp-interior points BEFORE landing waypoint i, the K extra segments they add
            // all belong to the seam (no door), so we append K empty entries at that point.
            List<Vector3> rawRouteWaypoints = new List<Vector3>(waypoints.Count);
            for (int i = 0; i < waypoints.Count; i++)
            {
                NodeKey w = waypoints[i];
                Floor f = FloorByLabel(w.Floor);
                if (f == null) continue;
                // Stair seam: the previous waypoint is on a different floor and this directed
                // landing pair has a baked ramp polyline — insert its interior points so the
                // follower walks the diagonal run (real XZ progression + true Y) instead of a
                // single stacked landing-to-landing jump it can't steer along.
                if (i > 0 && _stairRampInteriors != null)
                {
                    NodeKey prev = waypoints[i - 1];
                    if (prev.Floor != w.Floor &&
                        _stairRampInteriors.TryGetValue((prev, w), out Vector3[] interior))
                    {
                        for (int k = 0; k < interior.Length; k++)
                        {
                            rawRouteWaypoints.Add(interior[k]);
                            expandedSegmentDoors.Add(new List<string>(0)); // seam sub-segment, no door
                        }
                    }
                }
                Vector2 xz = f.CellToWorld(w.Ix, w.Iz);
                rawRouteWaypoints.Add(new Vector3(xz.x, f.FloorY, xz.y));
                // Door list for the segment LEAVING this cell-waypoint (i -> i+1), appended
                // only for non-terminal waypoints to keep one entry per emitted segment.
                if (i < waypoints.Count - 1)
                {
                    List<string> seg = i < segmentDoorNames.Count ? segmentDoorNames[i] : null;
                    expandedSegmentDoors.Add(seg ?? new List<string>(0));
                }
            }
            AddRouteWaypoints(rawRouteWaypoints, expandedSegmentDoors, route);
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

        // Resolve the floor a player STANDING at world Y belongs to (start-floor rule), as a
        // stable string label (e.g. "ground"/"upper"). Used by the object picker to bucket
        // candidates by floor before sorting by XZ distance, so cross-floor items don't read
        // as "near" the way a flat XZ distance makes them. Returns false when the bake isn't
        // loaded or Y matches no floor within tolerance.
        public static bool TryGetPlayerFloorLabel(float worldY, out string floorLabel)
        {
            floorLabel = null;
            if (!EnsureLoaded() || _floors == null)
                return false;

            Floor floor = FloorForY(worldY, StartFloorMatchToleranceM);
            floorLabel = floor?.Label;
            return floorLabel != null;
        }

        // Resolve the floor a player would STAND ON to interact with a TARGET at world Y, using
        // the same FloorForTargetY rule the planner uses to choose where autowalk arrives (so a
        // wall-mounted/tabletop/ceiling item resolves to the floor below it, matching where the
        // route actually ends). Returns false only when the bake isn't loaded.
        public static bool TryGetTargetFloorLabel(float worldY, out string floorLabel)
        {
            floorLabel = null;
            if (!EnsureLoaded() || _floors == null)
                return false;

            Floor floor = FloorForTargetY(worldY);
            floorLabel = floor?.Label;
            return floorLabel != null;
        }

        // Pick a CLEAN stand-cell near a world point: the navigable cell within radiusM whose
        // clearance (cells-to-nearest-wall, the same metric A* uses) is highest, tie-broken by
        // closeness to the anchor. Used by the coverage sweep to teleport the player without
        // spawning hard against a collider/door (which stalls the leg instantly). Picking the
        // HIGHEST-clearance cell already lands the player as far off walls as the 0.8m clearance
        // cap allows — that is the anti-wedge guard. `minClearanceCells` additionally REQUIRES the
        // chosen cell to be at least that far off any wall (so the relocation teleport can refuse a
        // too-tight spot rather than drop the player somewhere a downstream leg would fail from for
        // a bad-teleport reason); pass 0 to accept the best available. Floor is chosen by
        // FloorForTargetY. Returns false if no qualifying navigable cell is in range.
        public static bool TryGetCleanStandCell(float anchorX, float anchorY, float anchorZ,
                                                float radiusM, out Vector3 cleanWorld,
                                                int minClearanceCells = 0)
        {
            cleanWorld = Vector3.zero;
            if (!EnsureLoaded() || _floors == null) return false;
            Floor floor = FloorForTargetY(anchorY);
            if (floor == null) return false;

            int cx, cz;
            floor.WorldToCell(anchorX, anchorZ, out cx, out cz);
            int r = Mathf.Max(1, Mathf.CeilToInt(radiusM / floor.CellSize));
            float r2 = radiusM * radiusM;

            int bestClear = -1;
            float bestDist2 = float.PositiveInfinity;
            bool found = false;
            int bIx = 0, bIz = 0;
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    int ix = cx + dx, iz = cz + dz;
                    if (!floor.Navigable(ix, iz)) continue;
                    int clear = floor.Clearance(ix, iz);
                    if (clear < minClearanceCells) continue;
                    Vector2 w = floor.CellToWorld(ix, iz);
                    float ddx = w.x - anchorX, ddz = w.y - anchorZ;
                    float d2 = ddx * ddx + ddz * ddz;
                    if (d2 > r2) continue;
                    if (clear > bestClear || (clear == bestClear && d2 < bestDist2))
                    {
                        bestClear = clear; bestDist2 = d2; bIx = ix; bIz = iz; found = true;
                    }
                }
            }
            if (!found) return false;
            Vector2 bw = floor.CellToWorld(bIx, bIz);
            cleanWorld = new Vector3(bw.x, floor.FloorY, bw.y);
            return true;
        }

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
            _stairRampInteriors = new Dictionary<(NodeKey, NodeKey), Vector3[]>();
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

                    // Interior ramp points (world XYZ, excluding the two landing endpoints):
                    // emitted between the landings when a route crosses this seam so the
                    // follower walks the diagonal run instead of one stacked jump. The bake
                    // path is ordered bottom->top (ground->upper); store both directions.
                    Vector3[] interior = ExtractRampInterior(e.path);
                    if (interior != null && interior.Length > 0)
                    {
                        _stairRampInteriors[(a, b)] = interior;            // ground->upper
                        Vector3[] rev = new Vector3[interior.Length];
                        for (int k = 0; k < interior.Length; k++)
                            rev[k] = interior[interior.Length - 1 - k];
                        _stairRampInteriors[(b, a)] = rev;                 // upper->ground
                    }
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
            _bakedDoorNames.Clear();
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
                        if (d == null || string.IsNullOrEmpty(d.name)) continue;
                        // Every modelled door name — used to distinguish passage doors (in here)
                        // from container doors (carry a Door component but were never baked).
                        _bakedDoorNames.Add(d.name);
                        // Locked is authoritative regardless of record ordering.
                        if (d.locked) _lockedDoorNames.Add(d.name);
                        if (d.freed_cells != null)
                        {
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
                        if (d.operable_from_cells != null)
                        {
                            HashSet<long> op;
                            if (!floor.OperableFromByName.TryGetValue(d.name, out op))
                            {
                                op = new HashSet<long>();
                                floor.OperableFromByName[d.name] = op;
                            }
                            for (int ci = 0; ci < d.operable_from_cells.Length; ci++)
                            {
                                int[] pair = d.operable_from_cells[ci];
                                if (pair == null || pair.Length < 2) continue;
                                op.Add(Floor.PackCell(pair[0], pair[1]));
                            }
                        }
                        if (d.threshold_cells_list != null && d.threshold_cells_list.Length > 0
                            && !floor.OpeningCenterByName.ContainsKey(d.name))
                        {
                            // World-space centroid of the doorway-opening cells.
                            double sx = 0, sz = 0; int n = 0;
                            for (int ci = 0; ci < d.threshold_cells_list.Length; ci++)
                            {
                                int[] pair = d.threshold_cells_list[ci];
                                if (pair == null || pair.Length < 2) continue;
                                Vector2 w = floor.CellToWorld(pair[0], pair[1]);
                                sx += w.x; sz += w.y; n++;
                            }
                            if (n > 0)
                            {
                                float cx = (float)(sx / n), cz = (float)(sz / n);
                                floor.OpeningCenterByName[d.name] =
                                    new Vector3(cx, floor.FloorY, cz);
                                // Opening radius = max threshold-cell distance from the centroid
                                // (half the doorway width). Second pass over the same cells.
                                float maxR = 0f;
                                for (int ci = 0; ci < d.threshold_cells_list.Length; ci++)
                                {
                                    int[] pair = d.threshold_cells_list[ci];
                                    if (pair == null || pair.Length < 2) continue;
                                    Vector2 w = floor.CellToWorld(pair[0], pair[1]);
                                    float dx = w.x - cx, dz = w.y - cz;
                                    float r = Mathf.Sqrt(dx * dx + dz * dz);
                                    if (r > maxR) maxR = r;
                                }
                                floor.OpeningRadiusByName[d.name] = maxR;
                            }
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

            // Navigability just changed (overlay rebuilt) — the clearance map is stale.
            // Mirror plan_object_route.py _rebuild_extra_navigable nulling floor._clearance.
            for (int i = 0; i < _floors.Count; i++)
                _floors[i].InvalidateClearance();

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

        // The ramp polyline minus its two landing endpoints (those are the A* landing cells,
        // already emitted as waypoints). Returns the interior world-XYZ points bottom->top,
        // or null when the bake edge has no path or only the two endpoints.
        private static Vector3[] ExtractRampInterior(float[][] path)
        {
            if (path == null || path.Length <= 2) return null;
            int n = path.Length - 2;
            Vector3[] interior = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float[] p = path[i + 1];
                if (p == null || p.Length < 3) return null;
                interior[i] = new Vector3(p[0], p[1], p[2]);
            }
            return interior;
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
            // Highest floor whose floor_y - slack <= y (the floor the player stands on to
            // interact with a target above it). If the target is BELOW every floor plane,
            // fall back to the lowest floor — door pivots sit ~0.12m under the ground plane
            // (Y=-0.62 vs floor_y=-0.5), and without this fallback they return null →
            // TargetOffBake, making all five ground doors (Laundry, Office, their closets,
            // Bathroom1) un-targetable. Mirrors plan_object_route._floor_for_target_y, which
            // has this fallback (it's why the offline plan succeeded where the C# failed).
            Floor best = null;
            float bestFloorY = float.NegativeInfinity;
            Floor lowest = null;
            float lowestFloorY = float.PositiveInfinity;
            for (int i = 0; i < _floors.Count; i++)
            {
                float floorY = _floors[i].FloorY;
                if (floorY < lowestFloorY) { lowestFloorY = floorY; lowest = _floors[i]; }
                if (y >= floorY - UpwardSlackM && floorY > bestFloorY)
                {
                    bestFloorY = floorY; best = _floors[i];
                }
            }
            return best ?? lowest;
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
            out List<NodeKey> path, out float totalCost)
        {
            path = null;
            totalCost = 0f;

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
                // Navigability changed for the relaxed A* — clearance map is stale.
                f.InvalidateClearance();
            }

            try
            {
                return AStar(start, goals, goalFloorLabel, targetPos.x, targetPos.z, out path, out totalCost);
            }
            finally
            {
                for (int i = 0; i < _floors.Count; i++)
                {
                    Floor f = _floors[i];
                    f.ExtraNavigable.Clear();
                    f.ExtraNavigable.UnionWith(saved[i]);
                    // Restored the live overlay — clearance map must reflect it again.
                    f.InvalidateClearance();
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

            // Goal selection objective: FEWEST LEGS, then CLOSEST to the object. Every goal cell is
            // an equally-valid interaction standpoint (within InteractionRadius + LOS); among them
            // we want the route that's simplest for the follower to drive — fewest direction changes
            // (legs) — and, among equally-simple routes, the stand cell nearest the object (a few
            // steps closer is more stable than stopping at the radius edge). "Legs" is a property of
            // the SMOOTHED polyline, so we can't score it in the search; instead A* records the
            // shortest cell-path to each reached goal, then we smooth + count legs per candidate.
            //
            // We consider ALL reachable goal cells (don't cut by walk distance): a far cell can be
            // the SIMPLEST to reach (a straight shot to the radius edge vs weaving around the object
            // to a near cell), and legs is the primary axis — a distance-based bound provably picked
            // more-complex routes. The only bound is a generous expansion CAP as a safety valve: an
            // un-clamped 7.5m radius yields ~2300 goal cells and reaching the farthest can take ~73k
            // expansions (~2.9s). Profiling showed a 3000-expansion cap reproduces the uncapped
            // leg-optimal pick on every sampled target at ~100ms, so the cap bounds the pathological
            // tail without changing results in practice. It is a PERF safety valve, not a
            // correctness limit. See [[project_navigation_planner_los_goal_cells_2026_06_13]].
            var reachedGoals = new List<NodeKey>();
            int expansions = 0;

            while (open.Count > 0)
            {
                HeapItem cur = open.Pop();
                NodeKey node = cur.Node;
                if (cur.G > gScore[node] + 1e-6f) continue;
                if (!closed.Add(node))
                    continue;
                if (++expansions > GoalSearchMaxExpansions)
                    break;

                if (goalSet.Contains(node))
                {
                    reachedGoals.Add(node);
                    if (reachedGoals.Count == goalSet.Count)
                        break;
                    // A goal cell is terminal, so don't expand its neighbours.
                    continue;
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

            if (reachedGoals.Count == 0)
                return false;

            // Pick the reached goal with (fewest smoothed legs, then closest to the object, then
            // cheapest walk as a final stable tiebreak).
            List<NodeKey> bestPath = null;
            int bestLegs = int.MaxValue;
            float bestDist = float.PositiveInfinity;
            float bestG = float.PositiveInfinity;
            foreach (NodeKey goal in reachedGoals)
            {
                List<NodeKey> cellPath = ReconstructPath(came, goal);
                int legs = SmoothPath(cellPath).Count - 1;
                float dist = GoalDistanceToObjectM(goal, goalFloorLabel, goalWx, goalWz);
                float gCost = gScore[goal];
                bool better =
                    legs < bestLegs ||
                    (legs == bestLegs && dist < bestDist - 1e-4f) ||
                    (legs == bestLegs && Mathf.Abs(dist - bestDist) <= 1e-4f && gCost < bestG);
                if (better)
                {
                    bestLegs = legs;
                    bestDist = dist;
                    bestG = gCost;
                    bestPath = cellPath;
                }
            }
            path = bestPath;
            totalCost = bestG;
            return true;
        }

        // Performance safety valve (NOT a correctness limit): max A* node expansions while gathering
        // goal candidates for the fewest-legs pick. Profiling: reaching every goal of an un-clamped
        // 7.5m radius can hit ~73k expansions (~2.9s); a 3000 cap reproduced the same leg-optimal
        // pick on every sampled target at ~100ms. Bounds the pathological tail without altering
        // results in practice. Kept in sync with plan_object_route.py GOAL_SEARCH_MAX_EXPANSIONS.
        private const int GoalSearchMaxExpansions = 3000;

        private static List<NodeKey> ReconstructPath(Dictionary<NodeKey, NodeKey> came, NodeKey goal)
        {
            var rev = new List<NodeKey>();
            NodeKey c = goal;
            rev.Add(c);
            while (came.TryGetValue(c, out NodeKey prev))
            {
                rev.Add(prev);
                c = prev;
            }
            rev.Reverse();
            return rev;
        }

        // Flat-XZ distance (m) from a goal cell to the target object position. Used by the goal
        // closeness tiebreak in AStar.
        private static float GoalDistanceToObjectM(NodeKey goal, string goalFloorLabel, float goalWx, float goalWz)
        {
            Floor f = FloorByLabel(goalFloorLabel);
            if (f == null) return 0f;
            Vector2 xz = f.CellToWorld(goal.Ix, goal.Iz);
            float dx = xz.x - goalWx;
            float dz = xz.y - goalWz;
            return Mathf.Sqrt(dx * dx + dz * dz);
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
                        // Bounded clearance penalty on the destination cell: cells nearer
                        // than ClearanceTargetCells to a wall cost extra metres, so A*
                        // prefers the wide lane. Penalty only raises g (heuristic stays
                        // admissible). Mirrors plan_object_route.py Planner.neighbors.
                        int deficit = ClearanceTargetCells - f.Clearance(nx, nz);
                        float penalty = deficit > 0 ? deficit * ClearancePenaltyPerCellM : 0f;
                        yield return (new NodeKey(f.Label, nx, nz), step * f.CellSize + penalty);
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

        // Min clearance (cells, capped) of the raw A* cells over path[lo..hi]. A
        // line-of-sight shortcut spanning those cells must hold at least this much
        // clearance, so it can't straighten a margin-keeping curve back against an
        // obstacle — but a shortcut through a spot the raw path already threaded tight
        // stays allowed. Virtual nodes are skipped (no grid clearance). Mirrors
        // plan_object_route.py _subpath_min_clearance.
        private static int SubpathMinClearance(List<NodeKey> path, int lo, int hi)
        {
            int mn = ClearanceTargetCells;
            for (int k = lo; k <= hi; k++)
            {
                NodeKey n = path[k];
                if (IsVirtual(n)) continue;
                Floor f = FloorByLabel(n.Floor);
                if (f == null) continue;
                int c = f.Clearance(n.Ix, n.Iz);
                if (c < mn) mn = c;
            }
            return mn;
        }

        private static List<NodeKey> SmoothPath(List<NodeKey> path)
        {
            if (path == null || path.Count <= 2) return new List<NodeKey>(path ?? new List<NodeKey>());

            // Pass 1: greedy line-of-sight.
            List<NodeKey> los = new List<NodeKey> { path[0] };
            int i = 1;
            int lastAnchorIdx = 0;
            int anchorPathIdx = 0;
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
                    anchorPathIdx = i;
                    i++;
                    continue;
                }
                // Keep skipping while the straight segment stays clear AND holds the
                // clearance of the raw A* sub-path it replaces (so it rounds obstacles
                // with the margin A* found instead of hugging them). Mirrors
                // plan_object_route.py smooth_path pass 1.
                if (SegmentIsClear(los[los.Count - 1], node,
                        SubpathMinClearance(path, anchorPathIdx, i)))
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
                    anchorPathIdx = i;
                    i++;
                }
                else
                {
                    los.Add(prev);
                    lastAnchorIdx = i - 1;
                    anchorPathIdx = i - 1;
                }
            }
            if (!los[los.Count - 1].Equals(path[path.Count - 1])) los.Add(path[path.Count - 1]);

            // Pass 2: drop interior waypoints with shallow corner angle when shortcut is clear.
            List<NodeKey> outList = new List<NodeKey> { los[0] };
            for (int j = 1; j < los.Count - 1; j++)
            {
                NodeKey a = outList[outList.Count - 1], b = los[j], c = los[j + 1];
                // Never drop a floor-transition endpoint (stair/teleporter landing): the
                // player must pass through BOTH landings to change floors. Pass-2 angles
                // are XZ-only, so a staircase's two landings (same XZ, different floor Y)
                // look collinear and the ground-side landing was pruned — making the
                // follower steer from the stair-top XZ straight at the next ground waypoint,
                // cutting across the stairs into the side wall mid-descent.
                // Mirrors plan_object_route.smooth_path. See
                // [[project-navigation-hall1-runtime-truth-2026-05-29]].
                if (b.Floor != a.Floor || b.Floor != c.Floor || IsVirtual(a) || IsVirtual(b) || IsVirtual(c))
                {
                    outList.Add(b); continue;
                }
                Vector2? wa = WorldOf(a), wb = WorldOf(b), wc = WorldOf(c);
                if (!wa.HasValue || !wb.HasValue || !wc.HasValue) { outList.Add(b); continue; }
                Vector2 v1 = wb.Value - wa.Value;
                Vector2 v2 = wc.Value - wb.Value;
                float n1 = v1.magnitude, n2 = v2.magnitude;
                if (n1 == 0f || n2 == 0f) { outList.Add(b); continue; }
                float dot = Mathf.Clamp(Vector2.Dot(v1, v2) / (n1 * n2), -1f, 1f);
                float angleDeg = Mathf.Acos(dot) * Mathf.Rad2Deg;
                // Only drop b if the a→c shortcut also holds b's own clearance — else
                // pruning a corner of a margin-keeping curve routes a→c flush against the
                // obstacle b was rounding. Mirrors plan_object_route.py smooth_path pass 2.
                Floor bf = IsVirtual(b) ? null : FloorByLabel(b.Floor);
                int bClear = bf != null ? bf.Clearance(b.Ix, b.Iz) : 0;
                if (angleDeg <= CornerWaypointDeg && SegmentIsClear(a, c, bClear))
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
        private static bool SegmentIsClear(NodeKey a, NodeKey b, int minClearance = 0)
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

                // Min-clearance guard: when collapsing a curve that the clearance-cost A*
                // pushed off a wall, every shortcut cell must keep at least the curve's own
                // clearance, so the smoother can't flatten the margin back against the
                // obstacle. Mirrors plan_object_route.py _segment_is_clear(min_clearance).
                if (minClearance > 0 && floor.Clearance(ix, iz) < minClearance)
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
        // When the navigation target is itself a Door (or an item gated by a container door),
        // the gating door is force-tagged on the final segment by the DESTINATION rule in
        // TagDoors: nearest door opening within the target's InteractionRadius of the goal cell.
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

        // Eye height (m above the floor) for the interaction line-of-sight test. The game's
        // interaction ray originates at the player camera; ~1.6m is a standing eye height and
        // matches where the camera sits on the player capsule.
        private const float InteractionEyeHeightM = 1.6f;

        // A cell is only a valid standpoint if its sightline to the target is comfortably inside the
        // interaction radius, not grazing the rim. The outer edge of the radius is where the line is
        // longest and the viewing angle most marginal — for a wide-radius object the player should
        // be ALLOWED to stand back (that's why the radius is wide), but not at the extreme edge
        // where it may be hard to see. We require the actual eye→target distance to be within this
        // fraction of the radius, so the outer (1 - fraction) shell is rejected as a standpoint
        // while everything inward stays valid. "Solid LOS, not edge-grazing." See user guidance in
        // [[project_navigation_planner_los_goal_cells_2026_06_13]].
        private const float InteractionRimFractionM = 0.9f;

        // True if, standing on goal cell `g`, the player has a SOLID interaction line to the target:
        // (1) the game's camera ray from eye height toward the collider's closest bounds point hits
        // THAT collider first (no occluder) — mirrors BetterPlayerControl.cs:493-499 — AND (2) that
        // sightline is within InteractionRimFractionM of the interaction radius (not edge-grazing).
        // LOS is MANDATORY: a cell failing this is not a valid standpoint. Conservative on error.
        private static bool CellHasLineOfSightToTarget(Floor floor, NodeKey g, Collider targetCollider, float radiusM)
        {
            return CellLineOfSightClearanceM(floor, g, targetCollider, radiusM) >= 0f;
        }

        // Sentinel returned by CellLineOfSightClearanceM when the cell has NO interaction line of
        // sight at all (occluded, embedded origin, or edge-grazing rim). Any value >= 0 means the
        // cell is a valid standpoint; the magnitude is the SIDE clearance margin (see below) used to
        // prefer solidly-clear standpoints over ones whose line skims an occluder edge.
        private const float NoLineOfSight = -1f;

        // Returns the interaction-LOS CLEARANCE MARGIN (m) for standing on cell `g`, or NoLineOfSight
        // if the cell can't interact at all. The boolean clear/blocked test (CellHasLineOfSightToTarget)
        // is "margin >= 0". The margin is how far the sightline clears the NEAREST occluder edge:
        // a center ray that passes but skims a doorframe/furniture edge has a SMALL margin, while a
        // standpoint with open space around the line has a LARGE one. Goal selection prefers the
        // larger margin so the planner parks the player where the game's real (camera-pose) ray has
        // room to spare, not at the grazing rim where it passed offline but fails in-game — the
        // doorframe-grazed light switches + furniture-occluded items in
        // [[project_navigation_noloss_full_classification_2026_06_14]].
        private static float CellLineOfSightClearanceM(Floor floor, NodeKey g, Collider targetCollider, float radiusM)
        {
            if (targetCollider == null)
                return NoLineOfSight;
            try
            {
                Vector2 xz = floor.CellToWorld(g.Ix, g.Iz);
                Vector3 eye = new Vector3(xz.x, floor.FloorY + InteractionEyeHeightM, xz.y);
                Vector3 aimPoint = targetCollider.ClosestPointOnBounds(eye);
                Vector3 dir = aimPoint - eye;
                float dist = dir.magnitude;
                if (dist <= 0.0001f)
                    return LosProbeSideOffsetsM[LosProbeSideOffsetsM.Length - 1]; // on the target — max clearance

                // Reject edge-grazing standpoints: the sightline must be comfortably inside the
                // radius, not out at the rim where the view is marginal. Measured HORIZONTALLY
                // (XZ only): the rim is about not standing too far ACROSS the floor from the
                // object. A ceiling light / high-mounted prop has a large UNAVOIDABLE vertical
                // gap (recessed lights sit ~11m up in the ground-floor ceiling); counting that
                // gap in the rim distance rejected every cell and made the object un-interactable
                // offline, even though the player legitimately stands under it and looks up
                // (look + dateviator beam count as interaction — [[feedback-interaction-includes-look-and-glasses]]).
                // The full-3D sightline is still used for the occlusion raycast below.
                float horizDist = new Vector2(aimPoint.x - eye.x, aimPoint.z - eye.z).magnitude;
                if (radiusM > 0f && horizDist > radiusM * InteractionRimFractionM)
                    return NoLineOfSight;

                dir /= dist;
                BetterPlayerControl bpc = BetterPlayerControl.Instance;
                int mask = bpc != null ? ~(int)bpc.dateviatorIgnores : ~0;

                // CAMERA-POSE MODEL (not an idealized eye point). The game does NOT cast from the
                // standpoint toward the target — it casts from the THIRD-PERSON camera, pulled back
                // 0.25m along the look direction: origin = camera.pos - camera.forward*0.25,
                // dir = camera.forward (BetterPlayerControl.cs:493). When the player stands to
                // interact they face the target, so camera.forward ≈ dir. The dominant, wall-
                // independent piece of the rig that the old eye-point ray ignored is that 0.25m
                // BACKWARD pullback: it moves the origin AWAY from the target, which near a wall
                // lands the origin INSIDE the wall and self-collides at dist≈0 — the exact failure
                // that made the planner mark wall-adjacent items (light switches, mirrors, towel
                // rails, built-in cupboards) interactable while the game's own ray rejected them.
                // We model that backward shift and reject any cell whose pulled-back origin starts
                // embedded in a non-target collider. See
                // [[project_navigation_los_camera_origin_mismatch_2026_06_14]].
                const float CameraBackPullM = 0.25f;
                Vector3 origin = eye - dir * CameraBackPullM;

                // If the pulled-back origin is itself inside an occluder, the game's ray self-
                // collides immediately (hit_dist≈0) and the object is not interactable from here.
                Collider[] embedded = Physics.OverlapSphere(origin, 0.001f, mask, QueryTriggerInteraction.Ignore);
                for (int e = 0; e < embedded.Length; e++)
                {
                    Collider c = embedded[e];
                    if (c == null) continue;
                    if (IsStructuralSlab(c)) continue; // standing on/under the floor/ceiling shell is normal
                    if (!IsTargetCollider(c, targetCollider))
                        return NoLineOfSight; // origin embedded in a wall/prop → game ray self-collides
                }

                // The CENTER ray must reach the target first (mirrors the game's single cast). If it's
                // occluded, the cell is not a standpoint at all.
                if (!CenterRayReachesTarget(origin, dir, dist + CameraBackPullM + 0.05f, mask, targetCollider))
                    return NoLineOfSight;

                // GRADED CLEARANCE: the center ray clears, but HOW MUCH room does the line have?
                // Cast parallel side-probes offset perpendicular to the sightline (in the horizontal
                // plane — doorframes/furniture edges the player skims are vertical) at increasing
                // offsets. The largest offset that still reaches the target unobstructed is the side
                // clearance margin. A standpoint whose line grazes a doorframe edge clears at 0.0 but
                // fails the first side-probe (small margin); a standpoint with open space around the
                // line passes wider probes (large margin). Selection prefers the larger margin.
                Vector3 side = Vector3.Cross(dir, Vector3.up);
                float sideLen = side.magnitude;
                if (sideLen < 1e-4f)
                    return LosProbeSideOffsetsM[LosProbeSideOffsetsM.Length - 1]; // ray vertical — no horizontal edge to graze
                side /= sideLen;

                float clearance = 0f;
                for (int s = 0; s < LosProbeSideOffsetsM.Length; s++)
                {
                    float off = LosProbeSideOffsetsM[s];
                    bool leftOk = CenterRayReachesTarget(origin + side * off, dir, dist + CameraBackPullM + 0.05f, mask, targetCollider);
                    bool rightOk = CenterRayReachesTarget(origin - side * off, dir, dist + CameraBackPullM + 0.05f, mask, targetCollider);
                    if (leftOk && rightOk)
                        clearance = off; // both sides clear at this width → at least this much margin
                    else
                        break;          // skims an edge here → margin is the previous offset
                }
                return clearance;
            }
            catch
            {
                return NoLineOfSight;
            }
        }

        // Perpendicular side-probe offsets (m) for the graded LOS clearance margin. Two steps keep
        // the worst-case extra cost at 4 raycasts/cell (vs the center test's existing ~2): a near
        // probe that a doorframe/furniture graze fails (≈ half the player's ~0.4m nav radius) and a
        // wider one that confirms comfortable room clearance. Two steps is enough to rank "grazing"
        // below "solid"; finer resolution buys nothing for selection and multiplies cost over the
        // ~2300 goal cells of a wide-radius target. Ascending.
        private static readonly float[] LosProbeSideOffsetsM = { 0.20f, 0.45f };

        private static bool IsTargetCollider(Collider c, Collider targetCollider)
        {
            return c == targetCollider
                || (c != null && c.transform.IsChildOf(targetCollider.transform))
                || targetCollider.transform.IsChildOf(c.transform);
        }

        // True if a ray from `origin` along `dir` reaches the target collider first (no occluder) or
        // hits nothing within `maxDist` (clear line to the surface). Mirrors the game's first-hit rule,
        // with ONE deliberate leniency: a structural FLOOR/CEILING slab between the eye and a
        // vertically-displaced target is NOT treated as an occluder. A recessed ceiling light sits
        // ~8m above the floor and a desk drawer / floor item resolves its closest-bounds point at or
        // below the floor plane — in both cases the only thing the straight eye→target ray hits is the
        // horizontal slab of the player's own storey, yet the player legitimately interacts by looking
        // up / down (and the dateviator beam counts as interaction). See
        // [[feedback_interaction_includes_look_and_glasses]] and the floor/ceiling buckets in
        // [[project_navigation_noloss_full_classification_2026_06_14]]. We "see past" such a slab by
        // taking the first hit that is NOT a structural slab (RaycastAll, nearest-first).
        private static bool CenterRayReachesTarget(Vector3 origin, Vector3 dir, float maxDist, int mask, Collider targetCollider)
        {
            RaycastHit[] hits = Physics.RaycastAll(new Ray(origin, dir), maxDist, mask);
            if (hits == null || hits.Length == 0)
                return true; // nothing between camera and target surface — clear line

            RaycastHit firstReal = default;
            float bestDist = float.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;
                if (IsStructuralSlab(c)) continue; // floor/ceiling of the player's storey — look up/down past it
                if (hits[i].distance < bestDist)
                {
                    bestDist = hits[i].distance;
                    firstReal = hits[i];
                    found = true;
                }
            }
            if (!found)
                return true; // only structural slabs in the way → clear by the look-up/down rule
            return IsTargetCollider(firstReal.collider, targetCollider);
        }

        // A structural floor/ceiling slab — the horizontal building shell, named SM_Floor_* /
        // SM_Ceiling_* under House/MultiRoom/{Floors,Ceilings}. These are the only colliders the
        // look-up/look-down LOS rule sees past; everything else (walls, furniture, doors) still
        // occludes normally. Name-based so it can't accidentally exempt a wall or prop.
        private static bool IsStructuralSlab(Collider c)
        {
            if (c == null) return false;
            string n = c.transform != null ? c.transform.name : null;
            if (string.IsNullOrEmpty(n)) return false;
            return n.StartsWith("SM_Floor_", StringComparison.Ordinal)
                || n.StartsWith("SM_Ceiling_", StringComparison.Ordinal);
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

        private static List<List<string>> TagDoors(List<NodeKey> waypoints, string targetName, Vector3 targetPos, float targetInteractionRadius)
        {
            List<List<string>> segs = new List<List<string>>(Mathf.Max(0, waypoints.Count - 1));

            // Tag from the BAKE's uniform door list (per-floor OpeningCenterByName: the world
            // doorway-opening centroid of each door's threshold cells), NOT live
            // FindObjectsOfType<Door>(). The bake carries BOTH swinging Doors AND translating
            // SlidingDoors (closet/cabinet/cupboard "container" doors) with identical schema,
            // but the runtime splits them into two component types — enumerating only Door[]
            // silently dropped every SlidingDoor, so container doors were never tagged and the
            // follower walked into them and wedged. The bake list is the right source: it's the
            // same uniform portal set the planner already routes against (operable_from_cells /
            // freed_cells). See [[project_navigation_container_open_on_interact]].
            //
            // TWO tag rules, no magic radius constant (replaces the old hand-tuned DoorTagRadiusM):
            //   (1) ON-PATH: a door is tagged on a segment that THREADS its doorway — segment within
            //       the door's own opening radius (half the doorway width) + one cell of clearance.
            //       This is a tight geometric "the route goes through here" test, so it does NOT
            //       over-tag doors the route merely passes near (which the game's 7.5m
            //       InteractionRadius would).
            //   (2) DESTINATION: the barrier gating the TARGET (a container door whose opening sits
            //       between the goal cell and the item) is force-tagged on the final segment when the
            //       goal cell is within the GAME'S InteractionRadius of the door opening — the real
            //       "can the player stand at the goal and reach to open it" test.
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                List<string> tagged = new List<string>(0);
                NodeKey a = waypoints[i], b = waypoints[i + 1];
                if (a.Floor == b.Floor && !IsVirtual(a))
                {
                    Floor floor = FloorByLabel(a.Floor);
                    Vector2 wa = floor.CellToWorld(a.Ix, a.Iz);
                    Vector2 wb = floor.CellToWorld(b.Ix, b.Iz);
                    foreach (KeyValuePair<string, Vector3> kv in floor.OpeningCenterByName)
                    {
                        Vector3 dp = kv.Value;
                        float dist = PointSegmentDistance(dp.x, dp.z, wa.x, wa.y, wb.x, wb.y);
                        float openR = floor.OpeningRadiusByName.TryGetValue(kv.Key, out float orr) ? orr : 0f;
                        if (dist <= openR + floor.CellSize)
                            tagged.Add(kv.Key);
                    }
                }
                segs.Add(tagged);
            }

            // Rule (2): destination barrier. Find the door whose opening is nearest the FINAL
            // stand cell and within the target's InteractionRadius, and force-tag it on the last
            // segment. Covers (a) the target IS a door, and (b) the target is an item gated by a
            // container door — both reduce to "the goal cell is in interaction range of this door."
            if (segs.Count > 0 && waypoints.Count >= 1)
            {
                NodeKey goal = waypoints[waypoints.Count - 1];
                Floor gf = FloorByLabel(goal.Floor);
                if (gf != null && !IsVirtual(goal))
                {
                    Vector2 gw = gf.CellToWorld(goal.Ix, goal.Iz);
                    float radius = targetInteractionRadius > 0f ? targetInteractionRadius : DoorInteractRadiusFallbackM;
                    string bestName = null; float bestDist = float.PositiveInfinity;
                    foreach (KeyValuePair<string, Vector3> kv in gf.OpeningCenterByName)
                    {
                        float dx = kv.Value.x - gw.x, dz = kv.Value.z - gw.y;
                        float dist = Mathf.Sqrt(dx * dx + dz * dz);
                        // A door whose name matches the target always wins (target IS a door);
                        // otherwise the nearest in-range door is the gating barrier.
                        bool isTargetDoor = !string.IsNullOrEmpty(targetName) && kv.Key == targetName;
                        if (dist <= radius && (isTargetDoor || dist < bestDist))
                        {
                            bestName = kv.Key; bestDist = isTargetDoor ? -1f : dist;
                            if (isTargetDoor) break;
                        }
                    }
                    if (bestName != null)
                    {
                        List<string> last = segs[segs.Count - 1];
                        if (!last.Contains(bestName))
                            last.Add(bestName);
                    }
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
                Vector3 a = rawWaypoints[i];
                Vector3 b = rawWaypoints[i + 1];
                List<string> doors = i < rawSegmentDoors.Count && rawSegmentDoors[i] != null
                    ? rawSegmentDoors[i]
                    : new List<string>(0);
                bool isFinalSegment = i == rawWaypoints.Count - 2;

                // On-path door crossing: if this segment is tagged with a door the route
                // passes THROUGH (not the final target), aim the follower through the
                // doorway opening center first, so it threads the gap instead of pure-
                // pursuing the cell past the jamb (the office-doorway wedge). Only insert
                // when the opening lies ahead between a and b, so we never steer backward.
                // The destination door (final segment, door==target) is excluded — its
                // approach is governed by operable_from_cells goal selection.
                if (!isFinalSegment && doors.Count > 0)
                {
                    Vector3 opening;
                    if (TryGetDoorOpeningCenter(doors, a, b, out opening))
                        AddSemanticDoorWaypoint(route, doors, opening, SimpleNavWaypointKind.DoorOpening, null);
                }

                AddSemanticDoorWaypoint(route, doors, b, isFinalSegment ? SimpleNavWaypointKind.Target : SimpleNavWaypointKind.Navigation, null);
            }
        }

        // Look up a tagged on-path door's doorway-opening center (world centroid of its
        // threshold cells) and accept it only when it sits ahead of the player along the
        // a->b segment (projection in (0,1)) and near the segment line, so inserting it
        // threads the opening without steering backward or sideways off-route.
        private static bool TryGetDoorOpeningCenter(List<string> doors, Vector3 a, Vector3 b, out Vector3 opening)
        {
            opening = default;
            Floor floor = NearestFloorByY(a.y);
            if (floor == null) return false;
            float bestT = -1f;
            bool found = false;
            for (int i = 0; i < doors.Count; i++)
            {
                if (!floor.OpeningCenterByName.TryGetValue(doors[i], out Vector3 c)) continue;
                float t = ProjectParamXZ(a, b, c);
                if (t <= 0.05f || t >= 0.95f) continue;          // must be genuinely between a and b
                Vector3 proj = Vector3.Lerp(a, b, t);
                float dx = proj.x - c.x, dz = proj.z - c.z;
                if (dx * dx + dz * dz > 1.0f) continue;          // opening must be near the route line (<=1m)
                if (t > bestT) { bestT = t; opening = new Vector3(c.x, a.y, c.z); found = true; }
            }
            return found;
        }

        private static float ProjectParamXZ(Vector3 a, Vector3 b, Vector3 p)
        {
            float abx = b.x - a.x, abz = b.z - a.z;
            float len2 = abx * abx + abz * abz;
            if (len2 <= 1e-6f) return 0f;
            float t = ((p.x - a.x) * abx + (p.z - a.z) * abz) / len2;
            return t < 0f ? 0f : (t > 1f ? 1f : t);
        }

        private static Floor NearestFloorByY(float y)
        {
            Floor best = null; float bestD = float.PositiveInfinity;
            for (int i = 0; i < _floors.Count; i++)
            {
                float d = Mathf.Abs(_floors[i].FloorY - y);
                if (d < bestD) { bestD = d; best = _floors[i]; }
            }
            return best;
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
            // Per-door operability cells (where the player can stand to open the door),
            // indexed from the bake's doors[*].operable_from_cells. Used as the door-target
            // goal set. Multiple door records may share a name → union.
            public readonly Dictionary<string, HashSet<long>> OperableFromByName =
                new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            // Per-door doorway-opening center in WORLD space (centroid of threshold_cells),
            // used to insert a "thread through the opening" waypoint on routes that cross
            // an on-path door. Indexed from the bake at load time.
            public readonly Dictionary<string, Vector3> OpeningCenterByName =
                new Dictionary<string, Vector3>(StringComparer.Ordinal);
            // Per-door OPENING RADIUS (world metres): max distance of any threshold cell from the
            // opening centroid, i.e. half the doorway width. An on-path door is tagged when the
            // route segment passes within this radius (+ one cell of clearance) — a geometric
            // "the route threads THIS doorway" test, not a magic constant. Indexed at load.
            public readonly Dictionary<string, float> OpeningRadiusByName =
                new Dictionary<string, float>(StringComparer.Ordinal);
            // Per-state-wall freed-cells, parallel to DoorFreedByName. State-gated walls
            // (DresserWall and similar) contribute freed cells when their collider is
            // disabled at runtime.
            public readonly Dictionary<string, HashSet<long>> StateWallFreedByName =
                new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

            // Lazily-built clearance map: per cell, the 4-connected distance (in cells,
            // capped at ClearanceTargetCells) to the nearest non-navigable cell. Drives
            // the bounded clearance-cost penalty in Neighbors and the min-clearance guard
            // in SmoothPath, so routes curve around doorframe jambs / furniture with margin
            // instead of grazing. Invalidated (set null) whenever ExtraNavigable changes —
            // mirrors plan_object_route.py Floor._build_clearance / _rebuild_extra_navigable.
            // See [[project-navigation-csharp-clearance-port-TODO]].
            private int[][] _clearance;

            public static long PackCell(int ix, int iz) => ((long)ix << 32) | (uint)iz;

            // Invalidate the cached clearance map. Call after any mutation of
            // ExtraNavigable (door/state-wall freed-cell overlay).
            public void InvalidateClearance() { _clearance = null; }

            private void BuildClearance()
            {
                int cap = ClearanceTargetCells;
                int[][] dist = new int[Nx][];
                // Multi-source BFS queue (packed cells). Seed every non-navigable cell at 0.
                var dq = new Queue<long>();
                for (int ix = 0; ix < Nx; ix++)
                {
                    int[] col = new int[Nz];
                    dist[ix] = col;
                    for (int iz = 0; iz < Nz; iz++)
                    {
                        if (!Navigable(ix, iz))
                        {
                            col[iz] = 0;
                            dq.Enqueue(PackCell(ix, iz));
                        }
                        else
                        {
                            col[iz] = cap;
                        }
                    }
                }
                while (dq.Count > 0)
                {
                    long packed = dq.Dequeue();
                    int x = (int)(packed >> 32);
                    int z = (int)(uint)packed;
                    int d = dist[x][z];
                    if (d >= cap) continue;
                    // 4-connected (L1) — under-counts diagonal clearance, the safe direction.
                    if (x + 1 < Nx && dist[x + 1][z] > d + 1) { dist[x + 1][z] = d + 1; dq.Enqueue(PackCell(x + 1, z)); }
                    if (x - 1 >= 0 && dist[x - 1][z] > d + 1) { dist[x - 1][z] = d + 1; dq.Enqueue(PackCell(x - 1, z)); }
                    if (z + 1 < Nz && dist[x][z + 1] > d + 1) { dist[x][z + 1] = d + 1; dq.Enqueue(PackCell(x, z + 1)); }
                    if (z - 1 >= 0 && dist[x][z - 1] > d + 1) { dist[x][z - 1] = d + 1; dq.Enqueue(PackCell(x, z - 1)); }
                }
                _clearance = dist;
            }

            // Cells-to-nearest-wall (capped at ClearanceTargetCells) for a cell. Lazily
            // builds the map; out-of-bounds returns 0 (treated as a wall).
            public int Clearance(int ix, int iz)
            {
                if (!InBounds(ix, iz)) return 0;
                if (_clearance == null) BuildClearance();
                return _clearance[ix][iz];
            }

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
            // Cells where the player can stand to open/close this door, computed
            // offline by the bake from the real Door.cs rule (navigable + within reach
            // + not touching the closed panel + not in the swing arc). Used as the
            // door-target goal set, replacing the planner's hinge-distance band
            // approximation. See [[project-navigation-door-operability-cells]].
            [DataMember] public int[][] operable_from_cells;
            // The doorway-opening gap cells (the threshold the player crosses through).
            // Their centroid is the point an on-path route aims THROUGH, so the follower
            // threads the opening instead of pure-pursuing a cell past the door jamb.
            // See [[project-navigation-office-doorway-wedge-2026-05-30]].
            [DataMember] public int[][] threshold_cells_list;
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
            // Ordered bottom->top ramp polyline of world-space [x,y,z] points (the stair
            // run the follower walks). Endpoints are the two landings; interiors give the
            // follower an XZ-progressing line up the diagonal instead of one stacked jump.
            [DataMember] public float[][] path;
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
