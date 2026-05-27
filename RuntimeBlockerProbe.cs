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

        // Pick the closer of the two probes — that's the collider actually pinning the capsule.
        public Hit Nearest()
        {
            if (Chest == null) return Ankle;
            if (Ankle == null) return Chest;
            return Chest.Distance <= Ankle.Distance ? Chest : Ankle;
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
