using System;
using System.Collections.Generic;
using UnityEngine;

namespace DateEverythingAccess
{
    internal enum SimpleNavWaypointKind
    {
        Navigation,
        DoorApproach,
        DoorOpening,
        DoorExit,
        // A staircase LANDING — the floor cell at the bottom/top of a baked stair ramp where a
        // cross-floor route changes floor (both ends of the crossing are tagged). A route landmark
        // like a door: any cross-floor route must pass through the stairs, so it punctuates the path
        // ("head to the stairs") and bounds the next-landmark audio cue. See
        // [[project-tracker-tone-distance-pitch]] and the stair-ramp polyline work.
        Stairs,
        Target
    }

    internal sealed class SimpleNavWaypoint
    {
        public Vector3 Position;
        public SimpleNavWaypointKind Kind;
        public string DoorName;

        public SimpleNavWaypoint(Vector3 position, SimpleNavWaypointKind kind = SimpleNavWaypointKind.Navigation, string doorName = null)
        {
            Position = position;
            Kind = kind;
            DoorName = doorName;
        }
    }

    // Object-first navigation route (O4 output, consumed by the O5 executor).
    //
    // One Route = one plan from a start cell to a target interactable. The Route owns:
    //   - The polyline (waypoints in world space, Y resolved from each waypoint's floor band).
    //   - The target object's world position + name + interaction radius, for arrival + facing.
    //   - Per-segment door tags (GameObject names) that the executor uses to know when a door
    //     needs opening en route. Doors are *path preconditions*, not graph topology — every
    //     segment is walkable by definition; a closed door on a segment is opened on approach.
    //
    // Built by SimpleNavPlanner.Plan.
    internal sealed class SimpleNavRoute
    {
        public string TargetName;
        public int TargetGameObjectId;
        public Vector3 TargetPosition;
        public float TargetInteractionRadius;
        public bool TargetIsDatable;
        public string TargetInkFileName;

        // Polyline. waypoints[0] is the start cell, waypoints[N-1] is the goal cell.
        // Kept as the compatibility surface for sweep/reporting code.
        public List<Vector3> Waypoints = new List<Vector3>();

        // Semantic contract consumed by the route executor. This mirrors Waypoints by index but
        // carries the reason a point exists, so door-opening points are not treated as ordinary
        // planner corners.
        public List<SimpleNavWaypoint> SemanticWaypoints = new List<SimpleNavWaypoint>();

        // Door tags, keyed by segment index (0 = waypoints[0]→waypoints[1], etc.). Multiple
        // doors per segment are possible at door-clusters (front door / vestibule); the
        // executor opens whichever is reachable + closed.
        public List<List<string>> SegmentDoorNames = new List<List<string>>();

        public int SegmentCount => SegmentDoorNames.Count;

        public void AddWaypoint(Vector3 position, SimpleNavWaypointKind kind = SimpleNavWaypointKind.Navigation, string doorName = null)
        {
            Waypoints.Add(position);
            SemanticWaypoints.Add(new SimpleNavWaypoint(position, kind, doorName));
        }

        public void EnsureSemanticWaypoints()
        {
            if (SemanticWaypoints == null)
                SemanticWaypoints = new List<SimpleNavWaypoint>();

            if (SemanticWaypoints.Count == Waypoints.Count)
            {
                if (SemanticWaypoints.Count > 0)
                    SemanticWaypoints[SemanticWaypoints.Count - 1].Kind = SimpleNavWaypointKind.Target;
                return;
            }

            SemanticWaypoints.Clear();
            for (int i = 0; i < Waypoints.Count; i++)
            {
                SimpleNavWaypointKind kind = i == Waypoints.Count - 1
                    ? SimpleNavWaypointKind.Target
                    : SimpleNavWaypointKind.Navigation;
                SemanticWaypoints.Add(new SimpleNavWaypoint(Waypoints[i], kind));
            }
        }
    }
}
