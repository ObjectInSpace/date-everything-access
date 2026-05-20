using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace DateEverythingAccess
{
    // Object-first navigation route (O4 output, consumed by the O5 executor).
    //
    // One Route = one plan from a start cell to a target interactable. The Route owns:
    //   - The polyline (waypoints in world space, Y resolved from each waypoint's floor band).
    //   - The target object's world position + name + interaction radius, for arrival + facing.
    //   - Per-segment door tags (GameObject names) that the executor uses to know when a door
    //     needs opening en route. Doors are *path preconditions*, not graph topology — every
    //     segment is walkable by definition; a closed door on a segment is opened on approach.
    //
    // Loaded from artifacts/navigation/route.<name>.json (produced by scripts/plan_object_route.py).
    // The JSON's `world_xz` array form is ignored by this loader; the planner also emits scalar
    // `wx`/`wz`/`floor_y` mirrors specifically for DataContractJsonSerializer here.
    internal sealed class SimpleNavRoute
    {
        public string TargetName;
        public int TargetGameObjectId;
        public Vector3 TargetPosition;
        public float TargetInteractionRadius;
        public bool TargetIsDatable;
        public string TargetInkFileName;

        // Polyline. waypoints[0] is the start cell, waypoints[N-1] is the goal cell.
        public List<Vector3> Waypoints = new List<Vector3>();

        // Door tags, keyed by segment index (0 = waypoints[0]→waypoints[1], etc.). Multiple
        // doors per segment are possible at door-clusters (front door / vestibule); the
        // executor opens whichever is reachable + closed.
        public List<List<string>> SegmentDoorNames = new List<List<string>>();

        public int SegmentCount => SegmentDoorNames.Count;

        /// <summary>
        /// Load a route from a JSON file produced by plan_object_route.py. Returns null on any
        /// failure (logged) so the caller can decide whether to fall back or stop.
        /// </summary>
        public static SimpleNavRoute Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavRoute.Load: file missing path=" + (path ?? "<null>"));
                return null;
            }

            RouteDoc doc;
            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(RouteDoc));
                    doc = serializer.ReadObject(stream) as RouteDoc;
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavRoute.Load: parse failed path=" + path + " ex=" + ex.Message);
                return null;
            }

            if (doc == null || !string.Equals(doc.status, "ok", StringComparison.Ordinal))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavRoute.Load: status not ok path=" + path + " status=" + (doc?.status ?? "<null>"));
                return null;
            }
            if (doc.waypoints == null || doc.waypoints.Length == 0)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavRoute.Load: no waypoints path=" + path);
                return null;
            }

            SimpleNavRoute route = new SimpleNavRoute();
            if (doc.target != null)
            {
                route.TargetName = doc.target.Name;
                route.TargetGameObjectId = doc.target.GameObjectId;
                route.TargetIsDatable = doc.target.IsDatable;
                route.TargetInkFileName = doc.target.InkFileName;
                route.TargetInteractionRadius = doc.target.InteractionRadius > 0f ? doc.target.InteractionRadius : 2.0f;
                if (doc.target.Position != null)
                    route.TargetPosition = new Vector3(doc.target.Position.x, doc.target.Position.y, doc.target.Position.z);
            }

            for (int i = 0; i < doc.waypoints.Length; i++)
            {
                RouteNode w = doc.waypoints[i];
                if (w == null) continue;
                route.Waypoints.Add(new Vector3(w.wx, w.floor_y, w.wz));
            }

            // Segments are emitted by the planner as (waypoints.Length - 1) entries. Each carries
            // a doors[] list; we keep only the GameObject names (the executor resolves them live).
            if (doc.segments != null)
            {
                for (int i = 0; i < doc.segments.Length; i++)
                {
                    RouteSegment seg = doc.segments[i];
                    List<string> names = new List<string>();
                    if (seg != null && seg.doors != null)
                    {
                        for (int j = 0; j < seg.doors.Length; j++)
                        {
                            RouteDoor d = seg.doors[j];
                            if (d != null && !string.IsNullOrEmpty(d.name))
                                names.Add(d.name);
                        }
                    }
                    route.SegmentDoorNames.Add(names);
                }
            }
            // Defensive: align segment list length to (Waypoints.Count - 1).
            while (route.SegmentDoorNames.Count < route.Waypoints.Count - 1)
                route.SegmentDoorNames.Add(new List<string>(0));

            if (Main.Log != null)
            {
                int doorSegments = 0;
                for (int i = 0; i < route.SegmentDoorNames.Count; i++)
                    if (route.SegmentDoorNames[i].Count > 0) doorSegments++;
                Main.Log.LogInfo("SimpleNavRoute.Load ok path=" + path +
                    " target=" + (route.TargetName ?? "<null>") +
                    " waypoints=" + route.Waypoints.Count +
                    " segmentsWithDoor=" + doorSegments);
            }
            return route;
        }

#pragma warning disable CS0649
        [DataContract]
        private class RouteDoc
        {
            [DataMember] public string status;
            [DataMember] public RouteTarget target;
            [DataMember] public RouteNode[] waypoints;
            [DataMember] public RouteSegment[] segments;
        }

        [DataContract]
        private class RouteTarget
        {
            [DataMember] public int GameObjectId;
            [DataMember] public string Name;
            [DataMember] public RouteVec3 Position;
            [DataMember] public bool IsDatable;
            [DataMember] public string InkFileName;
            [DataMember] public float InteractionRadius;
        }

        [DataContract]
        private class RouteVec3
        {
            [DataMember] public float x;
            [DataMember] public float y;
            [DataMember] public float z;
        }

        // The planner emits both `world_xz` (array) and `wx`/`wz` (scalars). DataContractJsonSerializer
        // can't easily consume the array form for typed fields, so we read the scalar mirrors.
        [DataContract]
        private class RouteNode
        {
            [DataMember] public string floor;
            [DataMember] public float wx;
            [DataMember] public float wz;
            [DataMember] public float floor_y;
        }

        [DataContract]
        private class RouteSegment
        {
            [DataMember] public RouteNode from;
            [DataMember] public RouteNode to;
            [DataMember] public RouteDoor[] doors;
        }

        [DataContract]
        private class RouteDoor
        {
            [DataMember] public int id;
            [DataMember] public string name;
            [DataMember] public float distance_m;
        }
#pragma warning restore CS0649
    }
}
