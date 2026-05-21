using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace DateEverythingAccess
{
    // Bridge between the SimpleNav module and the rest of the mod. Owns target selection,
    // door-open timing, and arrival checks for the autowalk loop.
    //
    // This bridge is deliberately thin. It owns:
    //   1. Per-frame Observe() so SimpleNav can sample floor Y.
    //   2. A route-driven mode (object-first navigation) that drives along a polyline
    //      from SimpleNavPlanner, opening doors as path preconditions.
    //   3. A simple "arrived?" check that maps to SimpleNav.HasArrived.
    internal static class SimpleNavBridge
    {
        private static string _activeStepKey;
        private static Vector3 _activeTarget;
        private static bool _activeTargetValid;
        private static string _lastResolveReason;
        private static Door _activeDoor;
        // Waypoint sequence for the active step. _activeTarget mirrors _waypoints[_waypointIndex]
        // so legacy single-target callers see the current leg without changing their API. Empty
        // when the active step came in through the single-target overload (no route was planned).
        private static readonly System.Collections.Generic.List<Vector3> _waypoints =
            new System.Collections.Generic.List<Vector3>(8);
        private static int _waypointIndex;

        // O5 route-driven mode (object-first navigation). When non-null, the bridge is driving
        // an object-route polyline instead of a zone-graph step. _activeDoor here is updated
        // per segment from the route's door tags, not per step. The two modes are mutually
        // exclusive within a session; BeginRoute clears step state and vice versa.
        private static SimpleNavRoute _activeRoute;
        private static int _activeRouteSegment; // index into _activeRoute.SegmentDoorNames
        // Player must come within this XZ distance of the active waypoint before we advance.
        // Tuned to be larger than the autowalk's tick-distance so we don't oscillate, but small
        // enough that mid-room steiner waypoints don't drag the player off the natural line.
        private const float WaypointArrivalRadius = 1.0f;

        // Telemetry recorded per active step. Read by the sweep reporter when a step ends so we
        // can correlate a failure with what SimpleNav actually did, instead of inferring from logs.
        private static float _minDistanceToTarget = float.PositiveInfinity;
        private static Vector3 _playerPositionAtStepBegin;
        private static bool _hasPlayerPositionAtStepBegin;
        private static int _appliedFrameCount;


        /// <summary>
        /// The currently-active waypoint after the most recent <see cref="TryGetTargetForStep"/>
        /// or <see cref="TryAdvanceWaypoint"/> call. Callers that drive movement from a cached
        /// `target` variable should re-read this after advancing.
        /// </summary>
        public static Vector3 LastResolvedTarget => _activeTarget;

        // Called from AccessibilityWatcher.Update once per frame.
        public static void Tick()
        {
            SimpleNav.Observe();
        }

        public static void BeginStep(string stepKey)
        {
            _activeStepKey = stepKey;
            _activeTargetValid = false;
            _activeTarget = Vector3.zero;
            _lastResolveReason = null;
            _activeDoor = null;
            _activeRoute = null;
            _activeRouteSegment = 0;
            _waypoints.Clear();
            _waypointIndex = 0;
            _nextDoorInteractTime = 0f;
            _minDistanceToTarget = float.PositiveInfinity;
            _appliedFrameCount = 0;
            if (BetterPlayerControl.Instance != null)
            {
                _playerPositionAtStepBegin = BetterPlayerControl.Instance.transform.position;
                _hasPlayerPositionAtStepBegin = true;
            }
            else
            {
                _playerPositionAtStepBegin = Vector3.zero;
                _hasPlayerPositionAtStepBegin = false;
            }
        }

        public static void EndStep()
        {
            _activeStepKey = null;
            _activeTargetValid = false;
            _activeTarget = Vector3.zero;
            _lastResolveReason = null;
            _activeDoor = null;
            _activeRoute = null;
            _activeRouteSegment = 0;
            _waypoints.Clear();
            _waypointIndex = 0;
            _minDistanceToTarget = float.PositiveInfinity;
            _appliedFrameCount = 0;
            _hasPlayerPositionAtStepBegin = false;
        }

        // ---- O5 route-driven mode --------------------------------------------------------
        //
        // The object-first navigation contract: given a SimpleNavRoute (loaded from O4's
        // route.<name>.json), drive the autowalk toward the route's final target by following
        // the polyline, opening doors as path preconditions when a segment has a door tag.
        //
        // The bridge owns the route's lifecycle (BeginRoute → per-frame TryAdvanceWaypoint →
        // EndStep). The autowalk loop in AccessibilityWatcher checks HasActiveRoute first and
        // takes the route-driven path; otherwise it falls back to the zone-graph step model.

        /// <summary>The active O5 object-route, or null when running in step-driven mode.</summary>
        public static SimpleNavRoute ActiveRoute => _activeRoute;
        public static bool HasActiveRoute => _activeRoute != null;

        /// <summary>
        /// Begin driving the autowalk against an object-route polyline. Replaces any active
        /// step-driven plan. Caller is responsible for actually invoking the autowalk loop —
        /// this just installs the route.
        /// </summary>
        public static void BeginRoute(SimpleNavRoute route)
        {
            if (route == null || route.Waypoints == null || route.Waypoints.Count < 2)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavBridge.BeginRoute: empty/short route");
                EndStep();
                return;
            }

            BeginStep("route:" + (route.TargetName ?? "<unnamed>") + "#" + route.TargetGameObjectId);
            _activeRoute = route;
            _activeRouteSegment = 0;
            _waypoints.Clear();
            _waypoints.AddRange(route.Waypoints);
            // Skip the start waypoint (player's own position) — drive toward index 1 first.
            _waypointIndex = 1;
            _activeTarget = _waypoints[_waypointIndex];
            _activeTargetValid = true;
            _lastResolveReason = null;
            ResolveActiveDoorForSegment(_activeRouteSegment);
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavBridge.BeginRoute target=" + (route.TargetName ?? "<null>") +
                    " waypoints=" + route.Waypoints.Count +
                    " segments=" + route.SegmentCount);
        }

        /// <summary>
        /// True when the player is within the target's interaction radius (XZ) of the route's
        /// target world position. The route's planner already routes to a goal cell inside this
        /// disc, so this check is the natural arrival predicate for O5.
        /// </summary>
        public static bool HasArrivedAtRouteTarget(Vector3 playerPos)
        {
            if (_activeRoute == null) return false;
            Vector3 t = _activeRoute.TargetPosition;
            float dx = t.x - playerPos.x;
            float dz = t.z - playerPos.z;
            float r = _activeRoute.TargetInteractionRadius;
            if (r < 0.5f) r = 0.5f;
            // Clamp absurdly large interaction radii (some props publish 7.5m). Matches the
            // planner's same clamp so arrival lines up with the goal-cell expansion.
            if (r > 2.0f) r = 2.0f;
            return (dx * dx + dz * dz) <= r * r;
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
                Door d = SimpleNav.FindDoorByName(names[i]);
                if (d != null) { _activeDoor = d; return; }
            }
        }

        /// <summary>
        /// The Door connecting the current step's zones, when SimpleNav resolved one. Null
        /// otherwise. Read by the bridge's caller to open the door on approach.
        /// </summary>
        public static Door ActiveDoor => _activeDoor;

        // Fallback radius used when the door has no InteractableObj component (or we can't read
        // it). Matches InteractableObj.InteractionRadius default in the decompiled source.
        private const float DoorInteractRadiusFallback = 7.5f;
        // Cooldown between Interact() attempts on the same door. The open animation is 0.5s.
        private const float DoorInteractCooldownSeconds = 0.75f;
        private static float _nextDoorInteractTime;

        /// <summary>
        /// If the active step has a connecting door and the player is approaching it while
        /// it's still closed, fire Interact() to open it. Safe to call every frame; cooldown
        /// and open-state checks prevent spamming. Returns true if an interact was fired.
        /// </summary>
        // Log skip-reason at most once per step so we can diagnose without log spam.
        private static string _doorSkipLoggedForStep;
        // One-shot entry-state dump per step for Class D2 diagnosis (office->hallway,
        // office->office_closet). Emitted by TryOpenActiveDoorIfNeeded on first call per
        // _activeStepKey so we can see which branch each transition takes.
        private static string _doorEntryLoggedForStep;

        // How close to door.range counts as "fully open" — anything below this on a door reporting
        // open=true is treated as a stuck-mid-swing desync (the game's changeDoorRot broke on
        // collidedWithPlayer). We no longer auto-recover; we surface the skip and let the step
        // time out so the failure is honest.
        private const float DoorFullyOpenRotationFraction = 0.95f;

        // Reflected access to Door.startRot (private Vector3 set in Initialize() to the
        // closed orientation). Used to diagnose the "logically open but visually closed"
        // case where Door.open == true at scene load but the transform was never rotated.
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

        // Reflected access to Door.moving (private bool set true while changeDoorRot is
        // animating the swing). Used by the autowalk to pause forward motion so the player
        // doesn't barge into a half-open door and trip OnCollisionEnter → stopOnCollision.
        private static FieldInfo _doorMovingField;
        private static bool _doorMovingFieldResolved;
        private static FieldInfo GetDoorMovingField()
        {
            if (!_doorMovingFieldResolved)
            {
                _doorMovingFieldResolved = true;
                try
                {
                    _doorMovingField = typeof(Door).GetField("moving", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception ex)
                {
                    if (Main.Log != null) Main.Log.LogWarning("SimpleNav reflect Door.moving failed: " + ex.Message);
                    _doorMovingField = null;
                }
            }
            return _doorMovingField;
        }

        // Reflected access to Door.collidedWithPlayer (private bool latched true when the
        // player rigidbody touches the door collider). Once set, Open()/Close() short-circuit
        // and the door becomes inert until OnCollisionExit clears the flag. Teleporting the
        // player away does NOT fire OnCollisionExit, so the sweep harness has to reset it
        // manually whenever it relocates the player around an in-progress door.
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

        // Reflected access to Door.portal (private OcclusionPortal). When we bypass CloseDoor()
        // and write Door.open directly the portal can stay stuck open, leaving stale visibility
        // state that confuses downstream code.
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

        public static bool IsDoorLocked(Door door) => door != null && door.locked;

        /// <summary>
        /// Snap a door back to its authored closed orientation and clear the collision/animation
        /// flags that would otherwise keep it inert. Used by the door sweep harness to guarantee
        /// each step starts with the door in a known-good state regardless of what state earlier
        /// sweep steps left it in.
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
        /// True when the active step's door is currently animating its swing. The autowalk
        /// should hold the player in place while this is true; pushing forward into a moving
        /// door triggers OnCollisionEnter, which stops the swing partway and pins the player.
        /// </summary>
        public static bool IsActiveDoorMoving()
        {
            Door door = _activeDoor;
            if (door == null) return false;
            FieldInfo fi = GetDoorMovingField();
            if (fi == null) return false;
            try
            {
                object v = fi.GetValue(door);
                return v is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        // Returns the angular distance (deg) between the door's live rotation and its
        // authored startRot (closed orientation). Returns -1 if reflection failed.
        private static float ReadDoorVisualRotationDelta(Door door)
        {
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
            Door door = _activeDoor;
            string skipReason = null;
            float dist = -1f;
            float visualRotDelta = -1f;

            // One-shot per-step entry dump: lets D2 diagnosis distinguish stuck-recovery
            // exhaust (office->hallway) from wrong-side spawn (office->office_closet).
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
                    Collider entryCol = door.GetComponent<Collider>();
                    Vector3 entryNearest = entryCol != null ? entryCol.ClosestPointOnBounds(entryOrigin) : door.transform.position;
                    entryDist = Vector3.Distance(entryNearest, entryOrigin);
                }
                Main.Log.LogInfo("SimpleNav door entry step=" + (_activeStepKey ?? "<null>") +
                    " door=" + entryDoorName +
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
                visualRotDelta = ReadDoorVisualRotationDelta(door);
                float openThreshold = door.range * DoorFullyOpenRotationFraction;
                bool swingStillRunning = IsActiveDoorMoving();
                if (swingStillRunning)
                {
                    skipReason = "door-swing-in-progress";
                }
                else if (visualRotDelta >= 0f && visualRotDelta < openThreshold)
                {
                    // Door.open is true but the swing finished short of the open pose —
                    // the game's changeDoorRot broke on collidedWithPlayer. A real player
                    // would step back and wait rather than have the door re-cycled, so
                    // skip honestly and let the step time out.
                    skipReason = "door-stuck-player-blocking";
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
                // Falls back to player position when the camera isn't available, and to a
                // 7.5f default radius when the door has no InteractableObj component.
                Vector3 origin = Camera.main != null ? Camera.main.transform.position : playerPos;
                Collider doorCol = door.GetComponent<Collider>();
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
        /// Snapshot of the current step's SimpleNav-side telemetry. Read by the sweep reporter at
        /// step end so failures can be diagnosed without log scraping.
        /// </summary>
        public readonly struct StepTelemetry
        {
            public readonly string StepKey;
            public readonly bool TargetValid;
            public readonly Vector3 Target;
            public readonly string Reason;
            public readonly float MinDistanceToTarget;
            public readonly int AppliedFrameCount;
            public readonly Vector3 PlayerPositionAtStepBegin;
            public readonly bool HasPlayerPositionAtStepBegin;

            public StepTelemetry(
                string stepKey,
                bool targetValid,
                Vector3 target,
                string reason,
                float minDistanceToTarget,
                int appliedFrameCount,
                Vector3 playerPositionAtStepBegin,
                bool hasPlayerPositionAtStepBegin)
            {
                StepKey = stepKey;
                TargetValid = targetValid;
                Target = target;
                Reason = reason;
                MinDistanceToTarget = minDistanceToTarget;
                AppliedFrameCount = appliedFrameCount;
                PlayerPositionAtStepBegin = playerPositionAtStepBegin;
                HasPlayerPositionAtStepBegin = hasPlayerPositionAtStepBegin;
            }
        }

        public static StepTelemetry GetTelemetry()
        {
            return new StepTelemetry(
                _activeStepKey,
                _activeTargetValid,
                _activeTarget,
                _lastResolveReason,
                _minDistanceToTarget,
                _appliedFrameCount,
                _playerPositionAtStepBegin,
                _hasPlayerPositionAtStepBegin);
        }

        /// <summary>
        /// Called once per frame from ApplyAutoWalkSimple after the player position is known, so
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

        // Resolve the active step's target exactly once per step. Subsequent frames reuse
        // the cached target. This is the key behavioural difference from the legacy stack,
        // which re-derived an identical unreachable target every frame.
        public static bool TryGetTarget(string stepKey, string toZoneName, out Vector3 target, out string reason)
        {
            if (!string.Equals(stepKey, _activeStepKey, StringComparison.Ordinal))
            {
                BeginStep(stepKey);
            }

            if (_activeTargetValid)
            {
                target = _activeTarget;
                reason = _lastResolveReason;
                return true;
            }

            bool ok = SimpleNav.TryResolveTarget(toZoneName, out _activeTarget, out _lastResolveReason, out _activeDoor);
            _activeTargetValid = ok;
            target = _activeTarget;
            reason = _lastResolveReason;
            if (Main.Log != null)
            {
                Main.Log.LogInfo(SimpleNav.DescribeTarget(toZoneName, target, reason));
            }
            return ok;
        }

        /// <summary>
        /// Step-aware target resolution. Plans a waypoint route across the step's fromZone +
        /// toZone anchors once per step, then exposes the current leg's waypoint via
        /// <paramref name="target"/>. Subsequent calls reuse the cached route. Use
        /// <see cref="TryAdvanceWaypoint"/> once per frame to roll forward when the player
        /// reaches an intermediate waypoint.
        /// </summary>
        public static bool TryGetTargetForStep(
            string stepKey,
            NavigationGraph.PathStep step,
            out Vector3 target,
            out string reason)
        {
            if (!string.Equals(stepKey, _activeStepKey, StringComparison.Ordinal))
            {
                BeginStep(stepKey);
            }

            if (_activeTargetValid)
            {
                target = _activeTarget;
                reason = _lastResolveReason;
                return true;
            }

            bool ok = SimpleNav.TryResolveRoute(step, out System.Collections.Generic.List<Vector3> route, out _activeDoor, out _lastResolveReason);
            if (ok && route != null && route.Count > 0)
            {
                _waypoints.Clear();
                _waypoints.AddRange(route);
                // First waypoint is the player's own position — skip it; the autowalk drives
                // toward the *next* one. Hold onto index 0 only if the route is degenerate.
                _waypointIndex = _waypoints.Count > 1 ? 1 : 0;
                _activeTarget = _waypoints[_waypointIndex];
                _activeTargetValid = true;
            }
            else
            {
                _activeTargetValid = false;
                _activeTarget = Vector3.zero;
            }
            target = _activeTarget;
            reason = _lastResolveReason;
            if (Main.Log != null)
            {
                string preview = ok ? (_waypoints.Count.ToString() + "-waypoint route") : "no-route";
                Main.Log.LogInfo("SimpleNavBridge step=" + (stepKey ?? "<null>") +
                    " resolve=" + preview + " reason=" + (reason ?? "<ok>") +
                    " target=" + SimpleNav.DescribeTarget(step?.ToZone, target, null));
                if (ok && _waypoints.Count > 0)
                {
                    var ci = CultureInfo.InvariantCulture;
                    var sb = new System.Text.StringBuilder("SimpleNavBridge waypoints: ");
                    for (int i = 0; i < _waypoints.Count; i++)
                    {
                        var w = _waypoints[i];
                        if (i > 0) sb.Append(" -> ");
                        sb.Append("[").Append(i).Append("](")
                          .Append(w.x.ToString("0.00", ci)).Append(", ")
                          .Append(w.y.ToString("0.00", ci)).Append(", ")
                          .Append(w.z.ToString("0.00", ci)).Append(")");
                    }
                    Main.Log.LogInfo(sb.ToString());
                }
            }
            return ok;
        }

        /// <summary>
        /// If the player is within <see cref="WaypointArrivalRadius"/> of the active waypoint
        /// (XZ), advance to the next one. Returns true when a waypoint was advanced. The active
        /// target stays at the final waypoint after the route's end is reached — the
        /// step-level arrival check ("am I in the destination zone family?") then takes over.
        /// </summary>
        public static bool TryAdvanceWaypoint(Vector3 playerPos)
        {
            if (!_activeTargetValid || _waypoints.Count == 0) return false;
            if (_waypointIndex >= _waypoints.Count - 1) return false;
            Vector3 cur = _waypoints[_waypointIndex];
            float dx = cur.x - playerPos.x;
            float dz = cur.z - playerPos.z;
            if (dx * dx + dz * dz > WaypointArrivalRadius * WaypointArrivalRadius) return false;
            _waypointIndex++;
            _activeTarget = _waypoints[_waypointIndex];
            if (_activeRoute != null)
            {
                // Segment index = (waypointIndex - 1) since segment N spans waypoints N→N+1.
                // After advancing to waypoint k, we are now traversing segment k.
                _activeRouteSegment = _waypointIndex - 1;
                if (_activeRouteSegment >= _activeRoute.SegmentCount)
                    _activeRouteSegment = _activeRoute.SegmentCount - 1;
                ResolveActiveDoorForSegment(_activeRouteSegment);
            }
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavBridge advance step=" + (_activeStepKey ?? "<null>") +
                    " waypoint=" + _waypointIndex + "/" + (_waypoints.Count - 1) +
                    (_activeRoute != null ? (" segment=" + _activeRouteSegment +
                        " door=" + (_activeDoor != null && _activeDoor.gameObject != null ? _activeDoor.gameObject.name : "<none>")) : ""));
            return true;
        }

        public static bool HasArrived(string toZoneName)
        {
            return SimpleNav.HasArrived(toZoneName);
        }

        // No-op pass-through. An earlier version did a chest-height SphereCast and steered the
        // player around blockers, but it hit door frames and lintels every frame and veered the
        // player out of doorways. SimpleNav already routes through door approach points that
        // are on an obstacle-free line to the door, so steering wasn't pulling its weight.
        // Kept as a public seam so a future, more selective avoidance layer can slot in here.
        public static Vector3 SteerAroundObstacles(Vector3 playerPos, Vector3 desiredDir)
        {
            if (desiredDir.sqrMagnitude < 0.0001f) return desiredDir;
            return new Vector3(desiredDir.x, 0f, desiredDir.z).normalized;
        }

        public static string DescribeState()
        {
            var ci = CultureInfo.InvariantCulture;
            return "SimpleNavBridge step=" + (_activeStepKey ?? "<none>") +
                " targetValid=" + _activeTargetValid +
                " target=(" + _activeTarget.x.ToString("0.00", ci) + ", " +
                _activeTarget.y.ToString("0.00", ci) + ", " +
                _activeTarget.z.ToString("0.00", ci) + ")" +
                " reason=" + (_lastResolveReason ?? "<none>");
        }
    }
}
