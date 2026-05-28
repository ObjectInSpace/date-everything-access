using System.Globalization;
using UnityEngine;

namespace DateEverythingAccess
{
    // Structured snapshot of the most recent runtime-blocker spherecast probe done by the
    // autowalk's progress-timeout path (AccessibilityWatcher.ProbeRuntimeBlocker). The
    // coverage sweep reads RuntimeBlockerProbe.Last on stall to classify what physically
    // blocked the player, then categorizes it into one of three failure modes (footprint /
    // state / classification). See [[project-navigation-object-first-plan]].
    internal sealed class RuntimeBlockerProbe
    {
        public static RuntimeBlockerProbe Last;

        public Hit Chest;
        public Hit Ankle;
        public Vector3 PlayerPos;
        public Vector3 Waypoint;
        // Diagnostic context for "unknown" stalls (forward casts found nothing). These
        // fields are always populated when ProbeRuntimeBlocker runs, even if no collider
        // is in front of the player.
        public float DistanceToWaypointM;     // XZ distance from player to active waypoint
        public float RecentDisplacementM;     // how far the player has moved in the autowalk-progress window
        public float SecondsSinceProgress;    // time since _lastAutoWalkPosition updated
        public Hit Down;                      // downward cast: ground/floor under the player's feet
        public float DownDistanceM;           // distance to first thing under the player
        // Cardinal-direction casts to detect side/rear pins when forward is clear.
        public Hit Left;
        public Hit Right;
        public Hit Back;

        // Pick the closer of the two probes — that's the collider actually pinning the capsule.
        public Hit Nearest()
        {
            if (Chest == null) return Ankle;
            if (Ankle == null) return Chest;
            return Chest.Distance <= Ankle.Distance ? Chest : Ankle;
        }

        // True when all four horizontal directions came back empty. That's the diagnostic
        // signature of an "unknown" stall: nothing is in front of, to either side of, or
        // behind the player. Usually means either (a) the autowalk has stopped feeding
        // input, or (b) the player has already arrived but the arrival check disagrees.
        public bool AllHorizontalClear()
        {
            return Chest == null && Ankle == null && Left == null && Right == null && Back == null;
        }

        public sealed class Hit
        {
            public string Name;
            public string Path;
            public int Layer;
            public float Distance;

            public string Format()
            {
                return Name + " layer=" + Layer + " dist=" +
                       Distance.ToString("0.00", CultureInfo.InvariantCulture);
            }
        }

        // Build a "/Root/Child/Leaf" path so the offline triage tool can grep AssetRipper
        // exports for the exact GameObject without ambiguity.
        public static string PathOf(GameObject go)
        {
            if (go == null) return "<null>";
            string p = go.name;
            Transform t = go.transform.parent;
            while (t != null)
            {
                p = t.name + "/" + p;
                t = t.parent;
            }
            return "/" + p;
        }
    }
}
