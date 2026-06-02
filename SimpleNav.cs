using System;
using System.Collections.Generic;
using UnityEngine;

namespace DateEverythingAccess
{
    // Minimal navigation foundation. Under the object-first route model the planner
    // (SimpleNavPlanner) produces a polyline and the bridge follows it; this module is left
    // with the live observation surface the bridge still needs: per-zone floor-Y sampling,
    // Door-by-name lookup, and a logging helper.
    //
    // Truth sources (all live, no files):
    //   - Zones:  Singleton<CameraSpaces>.Instance.zones  (AABB + Name per triggerzone)
    //   - Player: BetterPlayerControl.Instance.transform.position
    //   - Doors:  Object.FindObjectsOfType<Door>()
    internal static class SimpleNav
    {
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
            // on the true floor as soon as the player lands.
            if (!_zoneFloorY.TryGetValue(playerZone.Name, out float currentMin) || playerPos.y < currentMin)
            {
                _zoneFloorY[playerZone.Name] = playerPos.y;
            }
        }

        // Resolve a Door by its GameObject name. Used by the bridge when a route segment
        // carries an authored door tag.
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
    }
}
