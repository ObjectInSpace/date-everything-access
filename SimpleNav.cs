using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DateEverythingAccess
{
    // Simple data record for a single Unity OcclusionPortal that sits on a Door GameObject.
    // Center is world-space; Size is axis extents in the portal's local axes (we ignore the
    // rotation for the first integration cut — most doors in this scene are axis-aligned, and
    // for door-approach alignment the AABB approximation is sufficient).
    internal sealed class SimpleNavDoorPortal
    {
        public string DoorName;
        public Vector3 Center;
        public Vector3 Size;
    }

    // Minimal navigation foundation, intentionally kept tiny.
    //
    // Premise: the game already publishes everything needed for navigation. There is no NavMesh
    // and there is no path-node system, so any data file we ship is necessarily a stale snapshot
    // of state we could read live. This module reads it live.
    //
    // Truth sources (all live, no files):
    //   - Zones:  Singleton<CameraSpaces>.Instance.zones  (AABB + Name per triggerzone)
    //   - Player: BetterPlayerControl.Instance.transform.position
    //   - Doors:  Object.FindObjectsOfType<Door>(), each with its own transform + open state
    //
    // Floor Y per zone is sampled the first time the player is observed inside that zone.
    // Door geometry (swing pivot, open type, range) is read from the live Door component via
    // reflection where the field is private.
    //
    // This module owns autowalk target selection.
    internal static class SimpleNav
    {
        public const float DoorClearanceMargin = 0.6f;   // distance to stand back from a door before triggering it
        public const float PlayerCapsuleRadius = 0.4f;   // approximate; used to keep targets clear of obstacles
        private const float ZoneArrivalAabbInflation = 0.0f;

        private static readonly Dictionary<string, float> _zoneFloorY =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);


        // Observation tick. Caller (the bridge) should call this once per frame while the
        // SimpleNav system is active, so per-zone floor Y gets sampled from real player motion.
        public static void Observe()
        {
            if (BetterPlayerControl.Instance == null)
                return;

            triggerzone playerZone = SafePlayerZone();
            if (playerZone == null || string.IsNullOrEmpty(playerZone.Name))
                return;

            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
            // Track the minimum Y ever observed in this zone. A first-observation cache poisons
            // itself when the player enters a zone mid-fall (teleport, jump, knockback): the
            // first sample is the door-pivot or apex height, not the floor. Minimum-Y converges
            // on the true floor as soon as the player lands. ResetFloorYCache() still wipes
            // everything if scene geometry changes.
            if (!_zoneFloorY.TryGetValue(playerZone.Name, out float currentMin) || playerPos.y < currentMin)
            {
                _zoneFloorY[playerZone.Name] = playerPos.y;
            }
        }

        public static void ResetFloorYCache()
        {
            _zoneFloorY.Clear();
        }

        // Current player zone name (e.g. "hallway6"), or null if unavailable.
        public static string CurrentZoneName()
        {
            triggerzone z = SafePlayerZone();
            return z?.Name;
        }

        // Returns the base zone family name for a sub-zone name. "hallway6" -> "hallway",
        // "bathroom1" -> "bathroom1" (kept as-is because the family is bathroom1 itself).
        // Strategy: strip trailing digits. Matches the existing nav-graph SceneZoneNames pattern.
        public static string ZoneFamily(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName))
                return zoneName;
            int end = zoneName.Length;
            while (end > 0 && char.IsDigit(zoneName[end - 1]))
                end--;
            return end == 0 ? zoneName : zoneName.Substring(0, end);
        }

        public static bool IsInZoneFamily(string currentZone, string family)
        {
            if (string.IsNullOrEmpty(currentZone) || string.IsNullOrEmpty(family))
                return false;
            if (string.Equals(currentZone, family, StringComparison.OrdinalIgnoreCase))
                return true;
            // Authoritative path: both names resolve to the same primary (InNavigationGraph) zone.
            string currentPrimary = SimpleNavSceneData.ResolvePrimaryZone(currentZone);
            string familyPrimary = SimpleNavSceneData.ResolvePrimaryZone(family);
            if (string.Equals(currentPrimary, familyPrimary, StringComparison.OrdinalIgnoreCase))
                return true;
            // Fallback for any zone the export does not cover: trailing-digit family match.
            return string.Equals(ZoneFamily(currentZone), family, StringComparison.OrdinalIgnoreCase);
        }

        // Polymorphic arrival check.
        //   - dorian_X      => virtual destination; player just needs to be near the named interactable.
        //   - X_tutorial / X_1love => virtual sub-state of a real room; the room is the arrival target.
        //   - everything else => the player's live zone family equals the target family.
        public static bool HasArrived(string targetZoneName)
        {
            if (string.IsNullOrEmpty(targetZoneName))
                return false;

            if (IsVirtualDorianTarget(targetZoneName))
                return HasArrivedAtVirtualInteractable(targetZoneName);

            string targetFamily = StripVirtualSuffix(targetZoneName);
            return IsInZoneFamily(CurrentZoneName(), targetFamily);
        }

        public static bool IsVirtualDorianTarget(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                name.StartsWith("dorian_", StringComparison.OrdinalIgnoreCase);
        }

        public static string StripVirtualSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            const string tut = "_tutorial";
            const string love = "_1love";
            if (name.EndsWith(tut, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - tut.Length);
            if (name.EndsWith(love, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - love.Length);
            return name;
        }

        // Resolve a navigation target the player can actually walk to.
        //
        //   toZoneName: family-level zone name from the caller (e.g. "hallway", "bathroom1",
        //               "dorian_bathroom2", "office_tutorial").
        //
        // Returns false with reason set if nothing reachable could be derived. The caller
        // (the bridge) should NOT loop on a false return: a false return means "give up this
        // step" rather than "retry next frame". This is the architectural fix from the
        // NAVIGATION_REVIEW doc — no per-frame reselection of the same unreachable point.
        public static bool TryResolveTarget(string toZoneName, out Vector3 target, out string reason)
        {
            return TryResolveTarget(toZoneName, out target, out reason, out _);
        }

        // Resolve a navigation target for one step. When the resolved path goes through a
        // door, the door is returned via <paramref name="connectingDoor"/> so the caller can
        // open it on approach. For door steps the target is on the DESTINATION side of the
        // doorway, not the source-side approach point — there is no separate "push through"
        // phase; the player walks to a point past the door, opening it on the way if needed.
        public static bool TryResolveTarget(string toZoneName, out Vector3 target, out string reason, out Door connectingDoor)
        {
            target = Vector3.zero;
            reason = null;
            connectingDoor = null;

            if (string.IsNullOrEmpty(toZoneName))
            {
                reason = "no-target-zone";
                return false;
            }

            if (BetterPlayerControl.Instance == null)
            {
                reason = "no-player";
                return false;
            }

            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
            string currentZone = CurrentZoneName();

            if (IsVirtualDorianTarget(toZoneName))
            {
                return TryResolveVirtualInteractableTarget(toZoneName, playerPos, out target, out reason);
            }

            string targetFamily = StripVirtualSuffix(toZoneName);

            // Same-family already-arrived: zero-distance target so the bridge can short-circuit.
            if (IsInZoneFamily(currentZone, targetFamily))
            {
                target = playerPos;
                return true;
            }

            triggerzone destZone = FindZoneInFamily(targetFamily, playerPos);
            if (destZone == null)
            {
                reason = "destination-zone-not-found:" + targetFamily;
                return false;
            }

            Door door = FindDoorBetween(currentZone, destZone.Name);
            if (door != null)
            {
                connectingDoor = door;
                return TryResolveDoorExitTarget(door, destZone.Name, out target, out reason);
            }

            target = ClosestPointOnZoneAabb(destZone, playerPos);
            target.y = ResolveFloorY(destZone.Name, target.y);
            return true;
        }

        // Build a target on the DESTINATION side of the door, well inside the destination zone.
        // The player walks toward this point from wherever they are; if the door is closed, the
        // bridge opens it on approach. There is no source-side "approach" phase.
        //
        // Direction is door->destZone (not player->door) so it works regardless of where the
        // player is — including being teleported on top of the door for sweep tests.
        // Lateral alignment uses the OcclusionPortal center when available so the player walks
        // through the doorway opening rather than into a doorpost.
        public static bool TryResolveDoorExitTarget(Door door, string destZoneName, out Vector3 target, out string reason)
        {
            target = Vector3.zero;
            reason = null;

            if (door == null)
            {
                reason = "no-door";
                return false;
            }

            triggerzone destZone = FindZoneInFamily(StripVirtualSuffix(destZoneName), door.transform.position);
            if (destZone == null)
            {
                reason = "destination-zone-not-found:" + destZoneName;
                return false;
            }

            Vector3 doorPos = door.transform.position;
            Vector3 playerPos = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : doorPos;

            // Direction "through the door, away from the source side". We derive this from the
            // player, not from the destination zone centre: a destination zone often has many
            // sub-zone AABBs and the one nearest the door is not reliably on the *destination*
            // side of the doorway — sometimes it overlaps the door itself or sits on the source
            // side. The player is reliably in the source zone (real gameplay) or on the source
            // side of the door (sweep teleport), so player->door points correctly.
            Vector3 throughDir = doorPos - playerPos;
            throughDir.y = 0f;
            if (throughDir.sqrMagnitude < 0.0001f)
            {
                // Fall back to door->destZone if the player is sitting on top of the door.
                throughDir = destZone.Position - doorPos;
                throughDir.y = 0f;
                if (throughDir.sqrMagnitude < 0.0001f)
                {
                    reason = "door-direction-undetermined";
                    return false;
                }
            }
            Vector3 toDest = throughDir.normalized;

            // Step well past the door. We aim for a point ~1.5m inside the destination zone
            // along the through axis. Clamped to the AABB so we always land inside.
            const float stepPastDoor = 1.5f;
            Vector3 raw = doorPos + toDest * (DoorClearanceMargin + PlayerCapsuleRadius + stepPastDoor);

            // Snap lateral coordinate to the OcclusionPortal opening when available.
            SimpleNavDoorPortal portal = TryGetDoorPortal(door);
            if (portal != null)
            {
                Vector3 lateralAxis = new Vector3(-toDest.z, 0f, toDest.x);
                Vector3 portalOffset = portal.Center - doorPos;
                portalOffset.y = 0f;
                float lateralDist = Vector3.Dot(portalOffset, lateralAxis);
                raw += lateralAxis * lateralDist;
            }

            // Clamp into the destination zone's AABB so we never aim through a wall.
            Bounds bounds = new Bounds(destZone.Position, destZone.Scale);
            raw = bounds.ClosestPoint(raw);
            raw.y = ResolveFloorY(destZone.Name, raw.y);
            target = raw;
            return true;
        }

        private static SimpleNavDoorPortal TryGetDoorPortal(Door door)
        {
            if (door == null) return null;
            string doorName = null;
            try { doorName = door.gameObject != null ? door.gameObject.name : null; }
            catch { return null; }
            return SimpleNavSceneData.GetDoorPortal(doorName);
        }

        // Resolve a Door by its GameObject name. Used by TryResolveRoute when the step's
        // authored ConnectorName is available — preferred over AABB matching because it's
        // exact, survives collapsed dorian zones, and avoids picking a nearby unrelated door.
        public static Door FindDoorByName(string connectorName)
        {
            if (string.IsNullOrEmpty(connectorName)) return null;
            Door[] doors = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < doors.Length; i++)
            {
                Door door = doors[i];
                if (door == null) continue;
                string doorName;
                try { doorName = door.gameObject != null ? door.gameObject.name : null; }
                catch { continue; }
                if (string.Equals(doorName, connectorName, StringComparison.OrdinalIgnoreCase))
                    return door;
            }
            return null;
        }

        public static Door FindDoorBetween(string fromZone, string toZone)
        {
            if (string.IsNullOrEmpty(fromZone) || string.IsNullOrEmpty(toZone))
                return null;

            // Resolve family bounds (union of all live sub-zones matching the family) so that
            // graph zone names like "hallway" or "office" find their door even when no live
            // triggerzone is named exactly that — the game splits rooms into "hallway",
            // "hallway2", ..., "hallway7" and the primary may not exist as an exact zone.
            if (!TryGetFamilyBounds(fromZone, out Bounds fromBounds) ||
                !TryGetFamilyBounds(toZone, out Bounds toBounds))
                return null;

            Door[] doors = UnityEngine.Object.FindObjectsOfType<Door>();

            // Primary path: when both endpoints map to primaries linked in GraphLinks, prefer a
            // door whose authored NearestPrimaryZones lists both — i.e. the asset data agrees
            // that this door joins those two rooms. Falls through to AABB distance scoring when
            // metadata is missing or the lookup turns up nothing.
            string fromPrimary = SimpleNavSceneData.ResolvePrimaryZone(fromZone);
            string toPrimary = SimpleNavSceneData.ResolvePrimaryZone(toZone);
            if (GraphLinkExists(fromPrimary, toPrimary))
            {
                Door byMetadata = FindDoorByMetadata(doors, fromPrimary, toPrimary);
                if (byMetadata != null) return byMetadata;
            }

            fromBounds.Expand(ZoneArrivalAabbInflation);
            toBounds.Expand(ZoneArrivalAabbInflation);

            Door bestDoor = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < doors.Length; i++)
            {
                Door door = doors[i];
                if (door == null)
                    continue;
                Vector3 doorPos = door.transform.position;

                // A door connects A and B if its position is close to both AABBs.
                float dFrom = AabbDistance(fromBounds, doorPos);
                float dTo = AabbDistance(toBounds, doorPos);
                if (dFrom > 5f || dTo > 5f) continue;
                float score = dFrom + dTo;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestDoor = door;
                }
            }

            return bestDoor;
        }

        private static bool GraphLinkExists(string fromPrimary, string toPrimary)
        {
            if (string.IsNullOrEmpty(fromPrimary) || string.IsNullOrEmpty(toPrimary)) return false;
            var links = SimpleNavSceneData.GraphLinks;
            for (int i = 0; i < links.Count; i++)
            {
                var l = links[i];
                if (string.Equals(l.Key, fromPrimary, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(l.Value, toPrimary, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Walk the live Door list and return the one whose static metadata names both endpoint
        // primaries in its NearestPrimaryZones. Live identity is matched by GameObject name.
        private static Door FindDoorByMetadata(Door[] doors, string fromPrimary, string toPrimary)
        {
            if (doors == null || doors.Length == 0) return null;
            for (int i = 0; i < doors.Length; i++)
            {
                Door door = doors[i];
                if (door == null) continue;
                string doorName;
                try { doorName = door.gameObject != null ? door.gameObject.name : null; }
                catch { continue; }
                if (string.IsNullOrEmpty(doorName)) continue;

                DoorMetadata md = SimpleNavSceneData.GetDoorMetadata(doorName);
                if (md?.NearestPrimaryZones == null) continue;

                bool hasFrom = false, hasTo = false;
                for (int j = 0; j < md.NearestPrimaryZones.Count; j++)
                {
                    string z = md.NearestPrimaryZones[j];
                    if (!hasFrom && string.Equals(z, fromPrimary, StringComparison.OrdinalIgnoreCase)) hasFrom = true;
                    if (!hasTo && string.Equals(z, toPrimary, StringComparison.OrdinalIgnoreCase)) hasTo = true;
                    if (hasFrom && hasTo) return door;
                }
            }
            return null;
        }

        private static bool TryResolveVirtualInteractableTarget(
            string virtualName,
            Vector3 playerPos,
            out Vector3 target,
            out string reason)
        {
            target = Vector3.zero;
            reason = null;

            InteractableObj match = FindInteractableByInternalName(virtualName);
            if (match == null)
            {
                reason = "virtual-interactable-not-found:" + virtualName;
                return false;
            }

            target = match.transform.position;
            target.y = playerPos.y;
            return true;
        }

        private static bool HasArrivedAtVirtualInteractable(string virtualName)
        {
            InteractableObj match = FindInteractableByInternalName(virtualName);
            if (match == null || BetterPlayerControl.Instance == null)
                return false;

            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
            Vector3 objPos = match.transform.position;
            playerPos.y = objPos.y;
            float dist = Vector3.Distance(playerPos, objPos);

            float arrivalRadius = 2.0f;
            try
            {
                float interactionRadius = match.InteractionRadius;
                if (interactionRadius > 0.1f) arrivalRadius = interactionRadius;
            }
            catch
            {
                // Fall back to default arrival radius.
            }

            return dist <= arrivalRadius;
        }

        private static InteractableObj FindInteractableByInternalName(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return null;
            InteractableObj[] all = UnityEngine.Object.FindObjectsOfType<InteractableObj>();
            for (int i = 0; i < all.Length; i++)
            {
                InteractableObj obj = all[i];
                if (obj == null) continue;
                string n;
                try { n = obj.InternalName(); }
                catch { continue; }
                if (string.Equals(n, internalName, StringComparison.OrdinalIgnoreCase))
                    return obj;
            }
            return null;
        }

        private static triggerzone FindZoneByName(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return null;
            if (Singleton<CameraSpaces>.Instance == null) return null;
            var zones = Singleton<CameraSpaces>.Instance.zones;
            if (zones == null) return null;
            for (int i = 0; i < zones.Count; i++)
            {
                triggerzone z = zones[i];
                if (z != null && string.Equals(z.Name, zoneName, StringComparison.OrdinalIgnoreCase))
                    return z;
            }
            return null;
        }

        // Encompasses every live triggerzone whose family matches zoneName. The graph uses
        // primary zone names ("hallway", "office") that often have no exact live triggerzone —
        // the game splits a room into "hallway", "hallway2", … "hallway7". A union AABB lets
        // door lookups work even when the primary itself isn't a live zone.
        private static bool TryGetFamilyBounds(string zoneName, out Bounds bounds)
        {
            bounds = default(Bounds);
            if (string.IsNullOrEmpty(zoneName)) return false;
            if (Singleton<CameraSpaces>.Instance == null) return false;
            var zones = Singleton<CameraSpaces>.Instance.zones;
            if (zones == null) return false;

            bool any = false;
            for (int i = 0; i < zones.Count; i++)
            {
                triggerzone z = zones[i];
                if (z == null) continue;
                if (!string.Equals(z.Name, zoneName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ZoneFamily(z.Name), zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;
                Bounds b = new Bounds(z.Position, z.Scale);
                if (!any) { bounds = b; any = true; }
                else bounds.Encapsulate(b);
            }
            return any;
        }

        // Find the sub-zone of `family` closest to `near`. The game splits a room into many
        // sub-zones (hallway, hallway2, ... hallway7) each with its own AABB. Picking the one
        // nearest the player avoids producing a target on the far side of a multi-room family.
        private static triggerzone FindZoneInFamily(string family, Vector3 near)
        {
            if (string.IsNullOrEmpty(family)) return null;
            if (Singleton<CameraSpaces>.Instance == null) return null;
            var zones = Singleton<CameraSpaces>.Instance.zones;
            if (zones == null) return null;

            triggerzone best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < zones.Count; i++)
            {
                triggerzone z = zones[i];
                if (z == null) continue;
                if (!string.Equals(ZoneFamily(z.Name), family, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(z.Name, family, StringComparison.OrdinalIgnoreCase))
                    continue;
                Bounds b = new Bounds(z.Position, z.Scale);
                float d = AabbDistance(b, near);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = z;
                }
            }
            return best;
        }

        private static string NearestZoneName(Vector3 position)
        {
            if (Singleton<CameraSpaces>.Instance == null) return null;
            var zones = Singleton<CameraSpaces>.Instance.zones;
            if (zones == null) return null;
            string bestName = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < zones.Count; i++)
            {
                triggerzone z = zones[i];
                if (z == null) continue;
                Bounds b = new Bounds(z.Position, z.Scale);
                float d = AabbDistance(b, position);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestName = z.Name;
                }
            }
            return bestName;
        }

        private static triggerzone SafePlayerZone()
        {
            try
            {
                if (Singleton<CameraSpaces>.Instance == null) return null;
                return Singleton<CameraSpaces>.Instance.PlayerZone();
            }
            catch
            {
                return null;
            }
        }

        private static Vector3 ClosestPointOnZoneAabb(triggerzone zone, Vector3 from)
        {
            Bounds b = new Bounds(zone.Position, zone.Scale);
            return b.ClosestPoint(from);
        }

        private static float AabbDistance(Bounds bounds, Vector3 p)
        {
            Vector3 c = bounds.ClosestPoint(p);
            return Vector3.Distance(c, p);
        }

        /// <summary>
        /// Public wrapper around the per-zone floor-Y sampling cache. The sweep harness uses
        /// this when spawning the player at an authored waypoint that may carry a y-coordinate
        /// from a door pivot well above the floor.
        /// </summary>
        public static Vector3 SnapToFloorPublic(Vector3 p, string zoneName) => SnapToFloor(p, zoneName);

        private static float ResolveFloorY(string zoneName, float fallback)
        {
            if (!string.IsNullOrEmpty(zoneName) &&
                _zoneFloorY.TryGetValue(zoneName, out float cached))
                return cached;

            // Family fallback: any sampled sub-zone of the same family.
            if (!string.IsNullOrEmpty(zoneName))
            {
                string family = ZoneFamily(zoneName);
                foreach (var kv in _zoneFloorY)
                {
                    if (string.Equals(ZoneFamily(kv.Key), family, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            return fallback;
        }

        // Distance at which two anchor positions are treated as the same node when building the
        // step's visibility graph. The graph export occasionally places a sub-zone anchor and a
        // transition crossing anchor a few centimetres apart; collapsing them avoids a redundant
        // hop in the A* output.
        private const float WaypointMergeRadius = 0.25f;

        /// <summary>
        /// Resolve a multi-waypoint route for a single <see cref="NavigationGraph.PathStep"/>.
        /// Runs A* across the union of fromZone + toZone anchors (plus the player and step's
        /// pre-baked clear points) using the static-blocker visibility predicate for edge
        /// existence. Returns false with <paramref name="reason"/> set when no path exists.
        /// <para>
        /// The route always starts at the player's current position and ends ~1.5m past the
        /// connector on the destination side — the same exit point produced by
        /// <see cref="TryResolveDoorExitTarget"/>. For door steps, <paramref name="connectingDoor"/>
        /// is set so the bridge can fire <c>Interact()</c> on approach.
        /// </para>
        /// </summary>
        public static bool TryResolveRoute(
            NavigationGraph.PathStep step,
            out List<Vector3> waypoints,
            out Door connectingDoor,
            out string reason)
        {
            waypoints = null;
            connectingDoor = null;
            reason = null;

            if (step == null)
            {
                reason = "no-step";
                return false;
            }
            if (BetterPlayerControl.Instance == null)
            {
                reason = "no-player";
                return false;
            }

            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
            bool isDoorStep = step.Kind == NavigationGraph.StepKind.Door;

            // Find the door (if any) for door steps. We need this both as a routing fact (the
            // SourceClear→DestClear segment crosses a closed collider that the visibility test
            // would reject) and as a returned handle so the bridge can open it.
            if (isDoorStep)
            {
                // Prefer the authored ConnectorName(s) from $stepMetadata when present — that's
                // the routing decision the graph builder already made. AABB matching can fail
                // for collapsed dorian zones (TryGetFamilyBounds returns false) or when the
                // dorian zone center sits >5m from the door.
                if (!string.IsNullOrEmpty(step.ConnectorName))
                    connectingDoor = FindDoorByName(step.ConnectorName);
                if (connectingDoor == null && step.ConnectorNames != null)
                {
                    for (int i = 0; i < step.ConnectorNames.Length && connectingDoor == null; i++)
                        connectingDoor = FindDoorByName(step.ConnectorNames[i]);
                }
                if (connectingDoor == null)
                    connectingDoor = FindDoorBetween(step.FromZone, step.ToZone);
            }

            // Resolve the goal: ~1.5m past the connector on the destination side. For door
            // steps this matches TryResolveDoorExitTarget; for OpenPassage we still want a goal
            // a short way into the destination zone so the player doesn't stop on the threshold.
            Vector3 goal;
            if (isDoorStep && connectingDoor != null)
            {
                if (!TryResolveDoorExitTarget(connectingDoor, step.ToZone, out goal, out reason))
                    return false;
            }
            else
            {
                // Use the step's DestinationClearPoint when populated; otherwise the destination
                // approach point. Both are pre-baked by the graph builder.
                goal = step.DestinationClearPoint;
                if (goal == Vector3.zero) goal = step.DestinationApproachPoint;
                if (goal == Vector3.zero) goal = step.ToWaypoint;
                goal.y = ResolveFloorY(step.ToZone, goal.y);
            }

            // Build the anchor set. Every non-player node Y-normalizes to the floor of its
            // owning zone before insertion so A* never picks a waypoint at door-pivot height
            // (the legacy nav-graph's SourceClear/Dest*/Crossing* points all sit at door pivot
            // Y, which is several metres above the floor for upper-storey doors).
            List<Vector3> nodes = new List<Vector3>(16);
            nodes.Add(playerPos); // index 0 — start
            int startIdx = 0;

            int goalIdx = AddOrMerge(nodes, goal);

            // Transition clear/crossing anchors (forced-edge pair for the connector).
            int sourceClearIdx = -1, destClearIdx = -1;
            if (step.SourceClearPoint != Vector3.zero)
                sourceClearIdx = AddOrMerge(nodes, SnapToFloor(step.SourceClearPoint, step.FromZone));
            if (step.DestinationClearPoint != Vector3.zero)
                destClearIdx = AddOrMerge(nodes, SnapToFloor(step.DestinationClearPoint, step.ToZone));

            // Fall back to crossing anchors if clear points were absent.
            if (sourceClearIdx < 0 && step.FromCrossingAnchor != Vector3.zero)
                sourceClearIdx = AddOrMerge(nodes, SnapToFloor(step.FromCrossingAnchor, step.FromZone));
            if (destClearIdx < 0 && step.ToCrossingAnchor != Vector3.zero)
                destClearIdx = AddOrMerge(nodes, SnapToFloor(step.ToCrossingAnchor, step.ToZone));

            // OcclusionPortal centre for door steps: gives A* a node *inside the doorway opening*
            // so it can route playerPos → portalCenter → goal as a real visibility-clear path
            // instead of relying on the forced-clear-seam bypass that lets doorframes through.
            int portalIdx = -1;
            if (isDoorStep && connectingDoor != null)
            {
                SimpleNavDoorPortal portal = TryGetDoorPortal(connectingDoor);
                if (portal != null)
                {
                    Vector3 portalCentre = portal.Center;
                    portalCentre.y = ResolveFloorY(step.ToZone, portalCentre.y);
                    portalIdx = AddOrMerge(nodes, portalCentre);
                }
            }

            // Per-zone graph anchors so A* can route around in-room furniture.
            IReadOnlyList<NavGraphAnchor> fromAnchors = SimpleNavSceneData.GetAnchorsForZone(step.FromZone);
            for (int i = 0; i < fromAnchors.Count; i++) AddOrMerge(nodes, SnapToFloor(fromAnchors[i].Position, step.FromZone));
            IReadOnlyList<NavGraphAnchor> toAnchors = SimpleNavSceneData.GetAnchorsForZone(step.ToZone);
            for (int i = 0; i < toAnchors.Count; i++) AddOrMerge(nodes, SnapToFloor(toAnchors[i].Position, step.ToZone));

            int n = nodes.Count;

            // First pass: visibility-strict A* with only the source↔dest forced-clear seam.
            // The seam exists because the segment crossing the door's own collider always
            // fails the IsSegmentClear test even when the connector is traversable in practice.
            int[] cameFromStrict;
            float[] gScoreStrict;
            RunAStar(nodes, startIdx, goalIdx, sourceClearIdx, destClearIdx,
                /*goalForcedFromClear*/ false, out gScoreStrict, out cameFromStrict);

            int[] cameFrom = cameFromStrict;
            float[] gScore = gScoreStrict;
            bool usedFallback = false;

            // Fallback: only for portal-less door steps (and non-door steps, which never had a
            // portal anyway). When portal data IS available the strict pass should have routed
            // playerPos → portalIdx → goal; a strict-pass failure with portal present indicates
            // a different structural problem and we shouldn't paper over it with seam-to-goal
            // edges that would bypass legitimate doorframe blockers.
            if (float.IsPositiveInfinity(gScore[goalIdx]) && portalIdx < 0)
            {
                int[] cameFromLoose;
                float[] gScoreLoose;
                RunAStar(nodes, startIdx, goalIdx, sourceClearIdx, destClearIdx,
                    /*goalForcedFromClear*/ true, out gScoreLoose, out cameFromLoose);
                if (!float.IsPositiveInfinity(gScoreLoose[goalIdx]))
                {
                    cameFrom = cameFromLoose;
                    gScore = gScoreLoose;
                    usedFallback = true;
                }
            }

            if (float.IsPositiveInfinity(gScore[goalIdx]))
            {
                reason = "no-visibility-path";
                return false;
            }

            // Reconstruct.
            List<int> reversed = new List<int>(8);
            for (int cur = goalIdx; cur != -1; cur = cameFrom[cur]) reversed.Add(cur);
            reversed.Reverse();
            waypoints = new List<Vector3>(reversed.Count);
            for (int i = 0; i < reversed.Count; i++) waypoints.Add(nodes[reversed[i]]);
            if (usedFallback) reason = "forced-seam-fallback";
            return true;
        }

        // O(n²) A* with optional forced-clear edges. The forced seam (sourceClearIdx↔destClearIdx)
        // is always allowed when both indices are set. When goalForcedFromClear is true,
        // sourceClearIdx↔goalIdx and destClearIdx↔goalIdx are also forced — that's the
        // pre-portal seam-to-goal behaviour, kept as a fallback for portal-less connectors.
        private static void RunAStar(
            List<Vector3> nodes,
            int startIdx,
            int goalIdx,
            int sourceClearIdx,
            int destClearIdx,
            bool goalForcedFromClear,
            out float[] gScore,
            out int[] cameFrom)
        {
            int n = nodes.Count;
            gScore = new float[n];
            cameFrom = new int[n];
            bool[] closed = new bool[n];
            for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; cameFrom[i] = -1; }
            gScore[startIdx] = 0f;

            while (true)
            {
                int current = -1;
                float bestF = float.PositiveInfinity;
                for (int i = 0; i < n; i++)
                {
                    if (closed[i] || float.IsPositiveInfinity(gScore[i])) continue;
                    float f = gScore[i] + XZDistance(nodes[i], nodes[goalIdx]);
                    if (f < bestF) { bestF = f; current = i; }
                }
                if (current < 0) break;
                if (current == goalIdx) break;
                closed[current] = true;

                for (int j = 0; j < n; j++)
                {
                    if (j == current || closed[j]) continue;
                    bool forcedClearSeam =
                        (current == sourceClearIdx && j == destClearIdx) ||
                        (current == destClearIdx && j == sourceClearIdx);
                    bool forcedGoalSeam = goalForcedFromClear && (
                        (current == sourceClearIdx && j == goalIdx) ||
                        (current == goalIdx && j == sourceClearIdx) ||
                        (current == destClearIdx && j == goalIdx) ||
                        (current == goalIdx && j == destClearIdx));
                    if (!forcedClearSeam && !forcedGoalSeam &&
                        !SimpleNavSceneData.IsSegmentClear(nodes[current], nodes[j]))
                        continue;
                    float tentative = gScore[current] + XZDistance(nodes[current], nodes[j]);
                    if (tentative < gScore[j])
                    {
                        gScore[j] = tentative;
                        cameFrom[j] = current;
                    }
                }
            }
        }

        private static Vector3 SnapToFloor(Vector3 p, string zoneName)
        {
            p.y = ResolveFloorY(zoneName, p.y);
            return p;
        }

        private static int AddOrMerge(List<Vector3> nodes, Vector3 p)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (XZDistance(nodes[i], p) <= WaypointMergeRadius) return i;
            }
            nodes.Add(p);
            return nodes.Count - 1;
        }

        private static float XZDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Logging helper consistent with the rest of the mod.
        public static string DescribeTarget(string toZone, Vector3 target, string reason)
        {
            var ci = CultureInfo.InvariantCulture;
            string posStr = "(" + target.x.ToString("0.00", ci) + ", " + target.y.ToString("0.00", ci) + ", " + target.z.ToString("0.00", ci) + ")";
            return "SimpleNav toZone=" + (toZone ?? "<null>") + " target=" + posStr + " reason=" + (reason ?? "<ok>");
        }
    }
}
