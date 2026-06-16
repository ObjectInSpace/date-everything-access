using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace DateEverythingAccess
{
    // Bridge between the SimpleNav module and the rest of the mod. Owns the route-driven
    // autowalk lifecycle: install a planner-emitted polyline, drive along it, open doors as
    // path preconditions.
    //
    // The bridge is deliberately thin. It owns:
    //   1. Per-frame Observe() so SimpleNav can sample floor Y.
    //   2. Route-driven mode (object-first navigation) that drives along a polyline from
    //      SimpleNavPlanner, opening doors as path preconditions.
    internal static class SimpleNavBridge
    {
        private static string _activeStepKey;
        private static Vector3 _activeTarget;
        private static bool _activeTargetValid;
        // Active barrier on the current segment, abstracted over Door + SlidingDoor (container
        // doors). See DoorPortal in SimpleNav.cs and [[project_navigation_container_open_on_interact]].
        private static DoorPortal _activeDoor;
        // Waypoint sequence for the active route. _activeTarget mirrors _waypoints[_waypointIndex].
        private static readonly System.Collections.Generic.List<Vector3> _waypoints =
            new System.Collections.Generic.List<Vector3>(8);
        private static readonly System.Collections.Generic.List<SimpleNavWaypoint> _semanticWaypoints =
            new System.Collections.Generic.List<SimpleNavWaypoint>(8);
        private static int _waypointIndex;

        // O5 route-driven mode (object-first navigation). When non-null, the bridge is driving
        // an object-route polyline. _activeDoor is updated per segment from the route's door tags.
        private static SimpleNavRoute _activeRoute;
        private static int _activeRouteSegment;
        // Player must come within this XZ distance of the active waypoint before we advance.
        // INTERMEDIATE waypoints stay loose (1.35m) — the follower only needs to get NEAR a corner
        // to round it; requiring it to nail each vertex would make it fuss/stall mid-route.
        private const float WaypointArrivalRadius = 1.35f;
        // FINAL arrival is tighter: the planner chose the goal cell for its clear line to the
        // SPECIFIC target, and stopping ~1.35m short loses that sightline off-axis (the dominant
        // arrived_unconfirmed failure). So the follower drives ONTO the goal cell, then the
        // turn-to-face phase takes over — i.e. "walk to the cell, then turn for the raycast". The
        // goal cells are open (measured: 83/100 unconfirmed cells at max clearance), so landing on
        // them doesn't reintroduce the tight-cell stalls the loose radius guarded against. Tight
        // (~1.5 cells): the follower's close-range settle (AccessibilityWatcher) drops the heading
        // gate near the final waypoint so it CONVERGES onto the cell instead of orbiting, which is
        // what lets arrival be this tight without a loose-radius compromise. If it genuinely can't
        // land here, the no-progress watchdog reports a real failure — we do NOT widen the radius to
        // hide an oscillation (that would mask a controller/data defect). See
        // [[feedback-navigation-failure-definition]], [[project-navigation-verify-los-gap-2026-06-16]].
        private const float FinalArrivalRadius = 0.3f;
        private const float DoorWaypointArrivalRadius = 2.2f;
        private const float DoorOpeningArrivalRadius = 0.9f;
        private const float WorldTargetArrivalRadius = 0.45f;

        // Telemetry recorded per active route. Used by RecordFrameProgress so failures can be
        // diagnosed without log scraping.
        private static float _minDistanceToTarget = float.PositiveInfinity;
        private static int _appliedFrameCount;

        /// <summary>
        /// The currently-active waypoint after the most recent <see cref="BeginRoute"/> or
        /// <see cref="TryAdvanceWaypoint"/> call.
        /// </summary>
        public static Vector3 LastResolvedTarget => _activeTarget;

        /// <summary>The planner's final waypoint (the goal stand-cell chosen for its clear line to
        /// the target), or Vector3.zero if no route is active. The close-range settle uses it so the
        /// follower converges ONTO the goal cell rather than orbiting it.</summary>
        public static Vector3 FinalWaypoint =>
            _waypoints != null && _waypoints.Count > 0 ? _waypoints[_waypoints.Count - 1] : Vector3.zero;

        // Cross-track error (m) above which the player is "off the corridor" and pure
        // pursuit must steer it BACK onto the line rather than further along it. Pure
        // pursuit only converges when the lookahead exceeds the cross-track error; when
        // the player drifts ~lookahead off the line (e.g. emerging from the stairs ~1.5m
        // off a corridor), a fixed forward lookahead point sits ahead-and-beside and the
        // player tracks PARALLEL into the wall instead of cutting back — the confirmed
        // SM_Walls_Hall1 overshoot stall (player 1.55m N of the z=-6.34 corridor drove
        // east into the wall). Above this threshold we steer toward the projection point
        // (straight back onto the line); below it we use the normal forward lookahead.
        // See [[project-navigation-hall1-runtime-truth-2026-05-29]].
        private const float PursuitMaxCrossTrackM = 0.6f;

        /// <summary>
        /// Pure-pursuit steer point. Projects the player onto the planned polyline (the
        /// segment ending at the active waypoint, plus all following segments). When the
        /// player is within <see cref="PursuitMaxCrossTrackM"/> of the line, returns a
        /// point <paramref name="lookaheadM"/> metres FORWARD along the polyline (normal
        /// pure pursuit — follows the corridor smoothly through corners). When the player
        /// is FARTHER off the line than that, returns the projection point itself so the
        /// player steers straight back ONTO the corridor instead of tracking parallel and
        /// driving into a wall (the overshoot failure mode). Y is from the polyline; XZ is
        /// what matters for steering. Falls back to the active waypoint if there is no
        /// usable polyline. See [[project-navigation-hall1-runtime-truth-2026-05-29]],
        /// [[project-navigation-executor-corner-stall]].
        /// </summary>
        public static Vector3 PursuitTarget(Vector3 playerPos, float lookaheadM)
        {
            if (_waypoints.Count == 0) return _activeTarget;
            int startSeg = _waypointIndex - 1;
            if (startSeg < 0) startSeg = 0;
            if (startSeg >= _waypoints.Count - 1)
                return _waypoints[_waypoints.Count - 1];

            // 1. Find the closest point on the remaining polyline to the player, and the
            //    segment index it lies on.
            int bestSeg = startSeg;
            float bestT = 0f;
            float bestDistSq = float.PositiveInfinity;
            for (int i = startSeg; i < _waypoints.Count - 1; i++)
            {
                Vector3 a = _waypoints[i];
                Vector3 b = _waypoints[i + 1];
                float t = ProjectParamXZ(a, b, playerPos);
                Vector3 proj = Vector3.Lerp(a, b, t);
                float dx = proj.x - playerPos.x;
                float dz = proj.z - playerPos.z;
                float d2 = dx * dx + dz * dz;
                if (d2 < bestDistSq) { bestDistSq = d2; bestSeg = i; bestT = t; }
            }

            Vector3 projPoint = Vector3.Lerp(_waypoints[bestSeg], _waypoints[bestSeg + 1], bestT);

            // 2. If the player has drifted off the corridor by more than the cross-track
            //    threshold, steer straight back onto the line (the projection point).
            //    This guarantees convergence — the case a fixed forward lookahead can't
            //    handle when the lateral error approaches the lookahead distance. Stairs
            //    use the same threshold as flat corridors: the baked ramp polyline now
            //    gives the descent a real XZ-progressing centerline to project onto, so
            //    the old stair-only tightening (a patch for the single stacked landing
            //    jump that had no usable XZ line) is no longer needed.
            if (bestDistSq > PursuitMaxCrossTrackM * PursuitMaxCrossTrackM)
                return projPoint;

            // 3. On the corridor: walk forward lookaheadM metres along the polyline.
            float remaining = lookaheadM;
            int seg = bestSeg;
            Vector3 cur = projPoint;
            while (seg < _waypoints.Count - 1)
            {
                Vector3 segEnd = _waypoints[seg + 1];
                Vector3 toEnd = segEnd - cur;
                toEnd.y = 0f;
                float segLen = toEnd.magnitude;
                if (segLen >= remaining)
                {
                    if (segLen <= 1e-4f) return segEnd;
                    return cur + toEnd.normalized * remaining;
                }
                remaining -= segLen;
                seg++;
                cur = _waypoints[seg];
            }
            return _waypoints[_waypoints.Count - 1];
        }

        // Clamped projection parameter of p onto segment [a,b], XZ only.
        private static float ProjectParamXZ(Vector3 a, Vector3 b, Vector3 p)
        {
            float abx = b.x - a.x;
            float abz = b.z - a.z;
            float lenSq = abx * abx + abz * abz;
            if (lenSq <= 1e-6f) return 0f;
            float t = ((p.x - a.x) * abx + (p.z - a.z) * abz) / lenSq;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return t;
        }

        // Called from AccessibilityWatcher.Update once per frame.
        public static void Tick()
        {
            SimpleNav.Observe();
        }

        private static void BeginStep(string stepKey)
        {
            _activeStepKey = stepKey;
            _activeTargetValid = false;
            _activeTarget = Vector3.zero;
            _activeDoor = null;
            _activeRoute = null;
            _activeRouteSegment = 0;
            _waypoints.Clear();
            _semanticWaypoints.Clear();
            _waypointIndex = 0;
            _nextDoorInteractTime = 0f;
            _minDistanceToTarget = float.PositiveInfinity;
            _appliedFrameCount = 0;
        }

        public static void EndStep()
        {
            _activeStepKey = null;
            _activeTargetValid = false;
            _activeTarget = Vector3.zero;
            _activeDoor = null;
            _activeRoute = null;
            _activeRouteSegment = 0;
            _waypoints.Clear();
            _semanticWaypoints.Clear();
            _waypointIndex = 0;
            _minDistanceToTarget = float.PositiveInfinity;
            _appliedFrameCount = 0;
        }

        // ---- O5 route-driven mode --------------------------------------------------------

        /// <summary>The active O5 object-route, or null when no route is installed.</summary>
        public static SimpleNavRoute ActiveRoute => _activeRoute;
        public static bool HasActiveRoute => _activeRoute != null;

        /// <summary>
        /// Begin driving the autowalk against an object-route polyline. Caller is responsible
        /// for actually invoking the autowalk loop — this just installs the route.
        /// </summary>
        public static void BeginRoute(SimpleNavRoute route)
        {
            if (route == null || route.Waypoints == null || route.Waypoints.Count < 1)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavBridge.BeginRoute: empty/short route");
                EndStep();
                return;
            }

            BeginStep("route:" + (route.TargetName ?? "<unnamed>") + "#" + route.TargetGameObjectId);
            _activeRoute = route;
            route.EnsureSemanticWaypoints();
            _activeRouteSegment = 0;
            _waypoints.Clear();
            _semanticWaypoints.Clear();
            _waypoints.AddRange(route.Waypoints);
            _semanticWaypoints.AddRange(route.SemanticWaypoints);
            // Skip the start waypoint when a route has at least one segment. Single-waypoint
            // routes happen when the planner's start cell is already inside the goal disc; keep
            // them active so arrival/proximity handling can complete instead of starting a
            // navigation with no active route.
            _waypointIndex = route.Waypoints.Count > 1 ? 1 : 0;
            _activeTarget = _waypoints[_waypointIndex];
            _activeTargetValid = true;
            ResolveActiveDoorForSegment(_activeRouteSegment);
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavBridge.BeginRoute target=" + (route.TargetName ?? "<null>") +
                    " waypoints=" + route.Waypoints.Count +
                    " segments=" + route.SegmentCount);
        }

        /// <summary>
        /// True when the player is within the target's interaction radius (XZ) of the route's
        /// target world position. The planner already routes to a goal cell inside this disc.
        /// </summary>
        // Max |player.y - finalWaypoint.y| for arrival. The arrival checks are XZ-only,
        // so a target whose interaction disc overlaps the STAIRCASE (e.g. an object near
        // the stair foot) was reported "arrived" while the player was still mid-descent
        // (player y~3.7 vs ground waypoint y=-0.5) — autowalk stopped ON the stairs and
        // the player had to finish manually. This was the dominant stair stall (25/28
        // mid-stair stalls were arrival-stops, not grazes). Gate arrival on the player
        // being on the final waypoint's floor level. 1.5m tolerance absorbs step height /
        // the player-origin lift without admitting a mid-stair pose.
        // See [[project-navigation-stair-arrival-stop-2026-05-29]].
        private const float ArrivalMaxYDeltaM = 1.5f;

        public static bool HasArrivedAtRouteTarget(Vector3 playerPos)
        {
            if (_activeRoute == null) return false;
            if (!_activeTargetValid || _waypoints.Count == 0) return false;
            if (_waypointIndex < _waypoints.Count - 1) return false;

            // Not arrived while the player is on a different level than the final
            // waypoint (mid-stairs) — XZ proximity alone is not arrival.
            Vector3 finalWp = _waypoints[_waypoints.Count - 1];
            if (Mathf.Abs(playerPos.y - finalWp.y) > ArrivalMaxYDeltaM)
                return false;

            if (_activeRoute.TargetGameObjectId == 0)
            {
                Vector3 worldTarget = _activeRoute.TargetPosition;
                float tdx = worldTarget.x - playerPos.x;
                float tdz = worldTarget.z - playerPos.z;
                return (tdx * tdx + tdz * tdz) <= WorldTargetArrivalRadius * WorldTargetArrivalRadius;
            }

            // Arrival = the player reached the planner's final waypoint (the goal STAND-CELL),
            // within the normal waypoint radius. The planner already placed that cell inside the
            // target's interaction band (<=1.5m from the collider face, LOS-preferred), so reaching
            // it IS being in interaction range — there is no need to also test the raw
            // InteractionRadius disc, and doing so was actively harmful: a large-radius object
            // (charcoal/log/food, radius up to 7.5m) let the drive declare "arrived" up to ~6m short
            // of the goal cell, stopping the player far from where the planner parked them. If the
            // goal cell is NOT in the interaction radius, that's a bad route to fix in the planner,
            // not something the drive should paper over by stopping early. The final selection is
            // still finalized by the first-person look/raycast after arrival.
            Vector3 finalWaypoint = _waypoints[_waypoints.Count - 1];
            float wdx = finalWaypoint.x - playerPos.x;
            float wdz = finalWaypoint.z - playerPos.z;
            return (wdx * wdx + wdz * wdz) <= FinalArrivalRadius * FinalArrivalRadius;
        }

        // Resolve _activeDoor from the route's segment door tags. Multiple doors per segment
        // are possible at clusters; we pick the first that resolves to a live Door instance.
        private static void ResolveActiveDoorForSegment(int segmentIndex)
        {
            _activeDoor = null;
            if (_activeRoute == null) return;
            if (segmentIndex < 0 || segmentIndex >= _activeRoute.SegmentDoorNames.Count) return;
            var names = _activeRoute.SegmentDoorNames[segmentIndex];
            if (names == null || names.Count == 0) return;
            for (int i = 0; i < names.Count; i++)
            {
                DoorPortal d = SimpleNav.FindPortalByName(names[i]);
                if (d != null) { _activeDoor = d; return; }
            }
        }

        /// <summary>
        /// The barrier (swinging Door or sliding container door) for the current route segment,
        /// when the route has a door tag. Null otherwise.
        /// </summary>
        public static DoorPortal ActiveDoor => _activeDoor;

        public static SimpleNavWaypoint ActiveWaypoint
        {
            get
            {
                if (_waypointIndex < 0 || _waypointIndex >= _semanticWaypoints.Count)
                    return null;
                return _semanticWaypoints[_waypointIndex];
            }
        }

        // Fallback radius used when the door has no InteractableObj component (or we can't read
        // it). Matches InteractableObj.InteractionRadius default in the decompiled source.
        private const float DoorInteractRadiusFallback = 7.5f;
        // Cooldown between Interact() attempts on the same door. The open animation is 0.5s.
        private const float DoorInteractCooldownSeconds = 0.75f;
        private static float _nextDoorInteractTime;

        // Log skip-reason at most once per step so we can diagnose without log spam.
        private static string _doorSkipLoggedForStep;
        // One-shot entry-state dump per step.
        private static string _doorEntryLoggedForStep;

        // How close to door.range counts as "fully open" — anything below this on a door reporting
        // open=true is treated as a stuck-mid-swing desync (the game's changeDoorRot broke on
        // collidedWithPlayer).
        private const float DoorFullyOpenRotationFraction = 0.95f;

        // Reflected access to Door.startRot (private Vector3 set in Initialize() to the
        // closed orientation).
        private static FieldInfo _doorStartRotField;
        private static bool _doorStartRotFieldResolved;
        private static FieldInfo GetDoorStartRotField()
        {
            if (!_doorStartRotFieldResolved)
            {
                _doorStartRotFieldResolved = true;
                try
                {
                    _doorStartRotField = typeof(Door).GetField("startRot", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav reflect Door.startRot failed: " + ex.Message);
                    _doorStartRotField = null;
                }
            }
            return _doorStartRotField;
        }

        // Reflected access to the private bool `moving` on a barrier component. Both Door
        // (swing animation) and SlidingDoor (translation) declare it with the same name, so we
        // cache the FieldInfo per concrete type. Door's field is on Door itself; for any type
        // we walk up to find a private instance `moving`.
        private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo> _movingFieldByType =
            new System.Collections.Generic.Dictionary<Type, FieldInfo>();
        private static FieldInfo GetMovingField(Type t)
        {
            if (t == null) return null;
            if (_movingFieldByType.TryGetValue(t, out FieldInfo cached)) return cached;
            FieldInfo fi = null;
            try
            {
                for (Type cur = t; cur != null && fi == null; cur = cur.BaseType)
                    fi = cur.GetField("moving", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNav reflect " + t.Name + ".moving failed: " + ex.Message);
                fi = null;
            }
            _movingFieldByType[t] = fi;
            return fi;
        }
        private static FieldInfo GetDoorMovingField() => GetMovingField(typeof(Door));

        // Reflected access to Door.collidedWithPlayer (private bool latched true when the
        // player rigidbody touches the door collider). Cleared manually during sweep teleports.
        private static FieldInfo _doorCollidedField;
        private static bool _doorCollidedFieldResolved;
        private static FieldInfo GetDoorCollidedField()
        {
            if (!_doorCollidedFieldResolved)
            {
                _doorCollidedFieldResolved = true;
                try
                {
                    _doorCollidedField = typeof(Door).GetField("collidedWithPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav reflect Door.collidedWithPlayer failed: " + ex.Message);
                    _doorCollidedField = null;
                }
            }
            return _doorCollidedField;
        }

        // Reflected access to Door.portal (private OcclusionPortal).
        private static FieldInfo _doorPortalField;
        private static bool _doorPortalFieldResolved;
        private static FieldInfo GetDoorPortalField()
        {
            if (!_doorPortalFieldResolved)
            {
                _doorPortalFieldResolved = true;
                try
                {
                    _doorPortalField = typeof(Door).GetField("portal", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav reflect Door.portal failed: " + ex.Message);
                    _doorPortalField = null;
                }
            }
            return _doorPortalField;
        }

        /// <summary>
        /// Snap a door back to its authored closed orientation and clear the collision/animation
        /// flags. Used by the route coverage sweep to guarantee each route starts with the door
        /// in a known-good state.
        /// </summary>
        public static void ForceDoorClosed(Door door)
        {
            if (door == null) return;

            string doorName = door.gameObject != null ? door.gameObject.name : "<null>";

            try { door.StopAllCoroutines(); }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNav ForceDoorClosed StopAllCoroutines threw door=" + doorName + " ex=" + ex.Message);
            }

            FieldInfo startRotField = GetDoorStartRotField();
            if (startRotField != null)
            {
                try
                {
                    if (startRotField.GetValue(door) is Vector3 startRot)
                        door.transform.eulerAngles = startRot;
                }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav ForceDoorClosed startRot apply threw door=" + doorName + " ex=" + ex.Message);
                }
            }

            door.open = false;

            FieldInfo collidedField = GetDoorCollidedField();
            if (collidedField != null)
            {
                try { collidedField.SetValue(door, false); }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav ForceDoorClosed clear collidedWithPlayer threw door=" + doorName + " ex=" + ex.Message);
                }
            }

            FieldInfo movingField = GetDoorMovingField();
            if (movingField != null)
            {
                try { movingField.SetValue(door, false); }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav ForceDoorClosed clear moving threw door=" + doorName + " ex=" + ex.Message);
                }
            }

            FieldInfo portalField = GetDoorPortalField();
            if (portalField != null)
            {
                try
                {
                    if (portalField.GetValue(door) is OcclusionPortal portal && portal != null)
                        portal.open = false;
                }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav ForceDoorClosed portal close threw door=" + doorName + " ex=" + ex.Message);
                }
            }

            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNav ForceDoorClosed door=" + doorName);
        }

        /// <summary>
        /// Reset a sliding container door to its closed state for the sweep. A SlidingDoor
        /// translates (it has no swing rotation / OcclusionPortal field), so closing is just its
        /// own CloseDoor() — we stop any in-flight slide first so the coroutine can't fight it.
        /// </summary>
        public static void ForceSliderClosed(SlidingDoor slider)
        {
            if (slider == null) return;
            string name = slider.gameObject != null ? slider.gameObject.name : "<null>";
            try { slider.StopAllCoroutines(); }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNav ForceSliderClosed StopAllCoroutines threw door=" + name + " ex=" + ex.Message);
            }
            // Clear the private `moving` flag (StopAllCoroutines leaves it latched) so a later
            // Interact() isn't rejected by the moving guard.
            FieldInfo movingField = GetMovingField(typeof(SlidingDoor));
            if (movingField != null)
            {
                try { movingField.SetValue(slider, false); } catch { }
            }
            try { slider.CloseDoor(0.5f); }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNav ForceSliderClosed CloseDoor threw door=" + name + " ex=" + ex.Message);
            }
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNav ForceSliderClosed door=" + name);
        }

        /// <summary>
        /// True when the active route's door is currently animating its swing. The autowalk
        /// should hold the player in place while this is true.
        /// </summary>
        public static bool IsActiveDoorMoving() => IsDoorMoving(_activeDoor);

        /// <summary>
        /// True while the given barrier's animation is in flight — Door swing OR SlidingDoor
        /// translation; both expose a private bool `moving` of the same name. Reflected read;
        /// returns false if reflection failed.
        /// </summary>
        public static bool IsDoorMoving(DoorPortal door)
        {
            if (door == null) return false;
            object component = door.IsSwing ? (object)door.SwingDoor : door.SlideComponent;
            if (component == null) return false;
            FieldInfo fi = GetMovingField(component.GetType());
            if (fi == null) return false;
            try
            {
                object v = fi.GetValue(component);
                return v is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        // Returns the angular distance (deg) between a swinging door's live rotation and its
        // authored startRot (closed orientation). Returns -1 if reflection failed OR the portal
        // is a SLIDER (sliders translate, not rotate — no swing delta concept).
        private static float ReadDoorVisualRotationDelta(DoorPortal portal)
        {
            if (portal == null || !portal.IsSwing) return -1f;
            Door door = portal.SwingDoor;
            if (door == null) return -1f;
            FieldInfo fi = GetDoorStartRotField();
            if (fi == null) return -1f;
            try
            {
                object v = fi.GetValue(door);
                if (!(v is Vector3 startRot)) return -1f;
                return Vector3.Distance(door.transform.eulerAngles, startRot);
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNav read Door.startRot threw: " + ex.Message);
                return -1f;
            }
        }

        public static bool TryOpenActiveDoorIfNeeded(Vector3 playerPos)
        {
            return TryOpenDoorIfNeeded(_activeDoor, playerPos);
        }

        /// <summary>
        /// Interact with the given <paramref name="door"/> when the player is in range and the
        /// door isn't already open. Used both for segment-tagged doors (via the
        /// <see cref="TryOpenActiveDoorIfNeeded"/> wrapper) and for routes whose target is
        /// itself a door (via <see cref="GetRouteTargetDoor"/>).
        /// </summary>
        public static bool TryOpenDoorIfNeeded(DoorPortal door, Vector3 playerPos)
        {
            string skipReason = null;
            float dist = -1f;
            float visualRotDelta = -1f;

            // One-shot per-step entry dump.
            if (_doorEntryLoggedForStep != _activeStepKey && Main.Log != null)
            {
                _doorEntryLoggedForStep = _activeStepKey;
                string entryDoorName = door != null && door.gameObject != null ? door.gameObject.name : "<null>";
                float entryRange = door != null ? door.range : -1f;
                float entryVisualRot = door != null ? ReadDoorVisualRotationDelta(door) : -1f;
                bool entryOpen = door != null && door.open;
                bool entryMoving = IsActiveDoorMoving();
                float entryDist = -1f;
                if (door != null)
                {
                    Vector3 entryOrigin = Camera.main != null ? Camera.main.transform.position : playerPos;
                    Collider entryCol = door.GetComponent();
                    Vector3 entryNearest = entryCol != null ? entryCol.ClosestPointOnBounds(entryOrigin) : door.transform.position;
                    entryDist = Vector3.Distance(entryNearest, entryOrigin);
                }
                Main.Log.LogInfo("SimpleNav door entry step=" + (_activeStepKey ?? "<null>") +
                    " door=" + entryDoorName +
                    " kind=" + (door != null ? (door.IsSwing ? "swing" : "slider") : "<null>") +
                    " open=" + entryOpen +
                    " range=" + entryRange.ToString("0.00", CultureInfo.InvariantCulture) +
                    " visualRotDelta=" + entryVisualRot.ToString("0.00", CultureInfo.InvariantCulture) +
                    " moving=" + entryMoving +
                    " dist=" + entryDist.ToString("0.00", CultureInfo.InvariantCulture) +
                    " playerPos=" + playerPos.ToString("F2"));
            }

            if (door == null)
            {
                skipReason = "no-active-door";
            }
            else if (door.open)
            {
                bool animStillRunning = IsActiveDoorMoving();
                if (animStillRunning)
                {
                    skipReason = "door-swing-in-progress";
                }
                else if (door.IsSwing)
                {
                    // SWING door: open=true but the swing may have finished short of the open pose
                    // (the game's changeDoorRot broke on collidedWithPlayer). Check rotation.
                    // Sliders translate and have no such partial-pose desync, so this is skipped
                    // for them — open=true + not moving is simply open.
                    visualRotDelta = ReadDoorVisualRotationDelta(door);
                    float openThreshold = door.range * DoorFullyOpenRotationFraction;
                    if (visualRotDelta >= 0f && visualRotDelta < openThreshold)
                        skipReason = "door-stuck-player-blocking";
                    else
                        skipReason = "door-already-open";
                }
                else
                {
                    skipReason = "door-already-open";
                }
            }
            else
            {
                // Mirror the game's in-range test (BetterPlayerControl.cs:499):
                //   Distance(door.collider.ClosestPointOnBounds(camPos), camPos) < InteractionRadius
                Vector3 origin = Camera.main != null ? Camera.main.transform.position : playerPos;
                Collider doorCol = door.GetComponent();
                Vector3 nearestOnDoor = doorCol != null ? doorCol.ClosestPointOnBounds(origin) : door.transform.position;
                dist = Vector3.Distance(nearestOnDoor, origin);
                float radius = door.interactableObj != null ? door.interactableObj.InteractionRadius : DoorInteractRadiusFallback;
                if (Time.unscaledTime < _nextDoorInteractTime) skipReason = "cooldown";
                else if (dist > radius) skipReason = "too-far-" + dist.ToString("0.00") + "/r=" + radius.ToString("0.00");
            }

            if (skipReason != null)
            {
                if (_doorSkipLoggedForStep != _activeStepKey && Main.Log != null)
                {
                    _doorSkipLoggedForStep = _activeStepKey;
                    string doorName = door != null && door.gameObject != null ? door.gameObject.name : "<null>";
                    string extra = visualRotDelta >= 0f
                        ? " visualRotDelta=" + visualRotDelta.ToString("0.00", CultureInfo.InvariantCulture)
                        : string.Empty;
                    Main.Log.LogInfo("SimpleNav skip door interact step=" + (_activeStepKey ?? "<null>") + " door=" + doorName + " reason=" + skipReason + extra);
                }
                return false;
            }

            string firingDoorName = door.gameObject != null ? door.gameObject.name : "<null>";
            try
            {
                door.Interact();
                _nextDoorInteractTime = Time.unscaledTime + DoorInteractCooldownSeconds;
                if (Main.Log != null)
                    Main.Log.LogInfo("SimpleNav fired Interact on door=" + firingDoorName + " dist=" + dist.ToString("0.00") + " openAfter=" + door.open);
                return true;
            }
            catch (Exception ex)
            {
                _nextDoorInteractTime = Time.unscaledTime + DoorInteractCooldownSeconds;
                if (Main.Log != null)
                    Main.Log.LogWarning("SimpleNav door Interact threw door=" + firingDoorName + " dist=" + dist.ToString("0.00") + " ex=" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Called once per frame from the route autowalk after the player position is known, so
        /// telemetry tracks the closest the player ever got to the resolved target.
        /// </summary>
        public static void RecordFrameProgress(Vector3 playerPosition)
        {
            _appliedFrameCount++;
            if (!_activeTargetValid) return;
            Vector3 d = _activeTarget - playerPosition;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist < _minDistanceToTarget) _minDistanceToTarget = dist;
        }

        /// <summary>
        /// If the player is within <see cref="WaypointArrivalRadius"/> of the active waypoint
        /// (XZ), advance to the next one. Returns true when a waypoint was advanced.
        /// </summary>
        // A waypoint is "arrived" in XZ but on a different floor level than the player is
        // not really reached — the two stair landings share an XZ but are ~13m apart in Y.
        // Without a Y gate the XZ-only check counts the GROUND landing as reached the instant
        // the player passes the (vertically-stacked) UPPER landing, skipping straight to the
        // ground corridor waypoint while the player is still mid-descent — so the follower
        // steers across the stairs into the side wall. Gating advance on Y keeps the descent
        // landing active until the player has actually come down to it.
        // See [[project-navigation-hall1-runtime-truth-2026-05-29]].
        private const float WaypointArrivalMaxYDeltaM = 1.5f;

        public static bool TryAdvanceWaypoint(Vector3 playerPos)
        {
            if (!_activeTargetValid || _waypoints.Count == 0) return false;
            if (_waypointIndex >= _waypoints.Count - 1) return false;
            Vector3 cur = _waypoints[_waypointIndex];
            float dx = cur.x - playerPos.x;
            float dz = cur.z - playerPos.z;
            float arrivalRadius = GetActiveWaypointArrivalRadius();
            if (dx * dx + dz * dz > arrivalRadius * arrivalRadius &&
                !HasPassedActiveDoorWaypoint(playerPos, dx * dx + dz * dz))
            {
                return false;
            }
            // Y gate: don't count a waypoint as reached while the player is on a different
            // level (mid-stairs). Keeps the stair-bottom landing active during the descent
            // so the follower tracks DOWN the stairs rather than cutting toward the next
            // ground waypoint. Skip the gate for door waypoints (handled in XZ as before).
            if (Mathf.Abs(cur.y - playerPos.y) > WaypointArrivalMaxYDeltaM &&
                !HasPassedActiveDoorWaypoint(playerPos, dx * dx + dz * dz))
            {
                return false;
            }
            _waypointIndex++;
            _activeTarget = _waypoints[_waypointIndex];
            if (_activeRoute != null)
            {
                // Segment index = (waypointIndex - 1) since segment N spans waypoints N→N+1.
                _activeRouteSegment = _waypointIndex - 1;
                if (_activeRouteSegment >= _activeRoute.SegmentCount)
                    _activeRouteSegment = _activeRoute.SegmentCount - 1;
                ResolveActiveDoorForSegment(_activeRouteSegment);
            }
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavBridge advance step=" + (_activeStepKey ?? "<null>") +
                    " waypoint=" + _waypointIndex + "/" + (_waypoints.Count - 1) +
                    " kind=" + (ActiveWaypoint != null ? ActiveWaypoint.Kind.ToString() : "<none>") +
                    (_activeRoute != null ? (" segment=" + _activeRouteSegment +
                        " door=" + (_activeDoor != null && _activeDoor.gameObject != null ? _activeDoor.gameObject.name : "<none>")) : ""));
            return true;
        }

        private static bool HasPassedActiveDoorWaypoint(Vector3 playerPos, float currentDistSq)
        {
            SimpleNavWaypoint waypoint = ActiveWaypoint;
            if (waypoint == null ||
                (waypoint.Kind != SimpleNavWaypointKind.DoorOpening && waypoint.Kind != SimpleNavWaypointKind.DoorExit) ||
                _waypointIndex >= _waypoints.Count - 1)
            {
                return false;
            }

            if (_activeDoor != null && !_activeDoor.open)
                return false;

            Vector3 next = _waypoints[_waypointIndex + 1];
            float ndx = next.x - playerPos.x;
            float ndz = next.z - playerPos.z;
            float nextDistSq = ndx * ndx + ndz * ndz;
            if (nextDistSq >= currentDistSq)
                return false;

            float currentDist = Mathf.Sqrt(currentDistSq);
            float nextDist = Mathf.Sqrt(nextDistSq);
            float margin = waypoint.Kind == SimpleNavWaypointKind.DoorOpening ? 0.75f : 0.25f;
            return nextDist + margin < currentDist;
        }

        private static float GetActiveWaypointArrivalRadius()
        {
            SimpleNavWaypoint waypoint = ActiveWaypoint;
            if (waypoint == null)
                return _activeDoor != null ? DoorWaypointArrivalRadius : WaypointArrivalRadius;

            switch (waypoint.Kind)
            {
                case SimpleNavWaypointKind.DoorOpening:
                    return DoorOpeningArrivalRadius;
                case SimpleNavWaypointKind.DoorApproach:
                case SimpleNavWaypointKind.DoorExit:
                    return 0.9f;
                default:
                    if (_activeDoor != null)
                        return DoorWaypointArrivalRadius;
                    return WaypointArrivalRadius;
            }
        }
    }
}
