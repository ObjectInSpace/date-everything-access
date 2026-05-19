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
    /// Static, world-space metadata for one Door (real <c>DoorComponent</c> + optional matched
    /// OcclusionPortal). All values come from <c>thirdpersongreybox-navigation-data.json</c>;
    /// runtime <c>Door</c> instances are still discovered live in <see cref="SimpleNav"/>.
    /// </summary>
    internal sealed class DoorMetadata
    {
        public string Name;
        public Vector3 Position;
        public Quaternion Rotation;
        public IReadOnlyList<string> NearestPrimaryZones;
        public SimpleNavDoorPortal Portal; // may be null (only ~16 of ~30 real doors have one)
    }

    /// <summary>
    /// Static metadata for an authored teleporter such as the crawlspace ladder.
    /// </summary>
    internal sealed class TeleporterMetadata
    {
        public string Name;
        public bool IsCrawlspace;
        public Vector3 Position;
        public Vector3 LocationDown;
        public Vector3 LocationUp;
        public Quaternion TeleportInRotation;  // Euler in the JSON; stored as quaternion
        public Quaternion TeleportOutRotation;
    }

    /// <summary>
    /// Static AABB blocker exported from <c>thirdpersongreybox-blockers.json</c>. The export
    /// currently covers primitive colliders only; the MeshCollider footprint pass (task A) will
    /// extend the file without changing this schema.
    /// </summary>
    internal sealed class StaticBlocker
    {
        public string Name;
        public Bounds Bounds;
        // XZ footprint and Y span, precomputed at load time so the visibility predicate can
        // reject blockers on other floors (Y) before running the XZ slab test.
        public float MinX;
        public float MaxX;
        public float MinZ;
        public float MaxZ;
        public float MinY;
        public float MaxY;
    }

    /// <summary>
    /// One graph anchor — a candidate node for the in-step visibility A*. Sourced from
    /// <c>navigation_graph.generated.json</c>'s <c>Nodes</c> list.
    /// </summary>
    internal sealed class NavGraphAnchor
    {
        public string Id;
        public string Zone;          // primary-zone name (e.g. "hallway")
        public string SceneZoneName; // sub-zone (e.g. "hallway2")
        public string Kind;          // "ZoneCenter" or "RoomSubZone"
        public Vector3 Position;
    }

    /// <summary>
    /// One-shot loader for the rich scene exports consumed by <see cref="SimpleNav"/>. Loaded
    /// lazily on first access from <see cref="Paths.PluginPath"/>; all files are optional —
    /// missing data degrades gracefully.
    /// </summary>
    internal static class SimpleNavSceneData
    {
        private const string NavigationDataFileName = "thirdpersongreybox-navigation-data.json";
        private const string BlockersFileName = "thirdpersongreybox-blockers.json";
        private const string NavigationGraphFileName = "navigation_graph.generated.json";

        // Suffixes that mark a sub-state of a primary zone (the suffix is stripped to find the
        // base primary). The crawlspace/dorian families are themselves primaries, so they are
        // NOT in this list — they get their own InNavigationGraph=true entries.
        private static readonly string[] AliasSuffixes =
        {
            "_tutorial",
            "_1love",
            "_2friend",
            "_3hate",
            "_shelley",
            "_cake",
            "_ronald",
        };

        private static bool _loaded;
        private static readonly HashSet<string> _primaryZones =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<KeyValuePair<string, string>> _graphLinks =
            new List<KeyValuePair<string, string>>();
        private static readonly Dictionary<string, DoorMetadata> _doors =
            new Dictionary<string, DoorMetadata>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TeleporterMetadata> _teleporters =
            new Dictionary<string, TeleporterMetadata>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<StaticBlocker> _blockers = new List<StaticBlocker>();
        private static readonly Dictionary<string, string> _aliasToPrimary =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Graph nodes indexed by their primary zone name. Populated from
        // navigation_graph.generated.json so SimpleNav can enumerate per-zone anchors for A*.
        private static readonly Dictionary<string, List<NavGraphAnchor>> _anchorsByZone =
            new Dictionary<string, List<NavGraphAnchor>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<NavGraphAnchor> _emptyAnchors = new List<NavGraphAnchor>(0);

        /// <summary>
        /// Pairs of (from, to) primary-zone names with at least one authored connection. Order
        /// matches the export; both directions are typically present for two-way connections.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> GraphLinks
        {
            get { EnsureLoaded(); return _graphLinks; }
        }

        /// <summary>
        /// True when <paramref name="zoneName"/> is one of the 57 <c>InNavigationGraph</c>
        /// camera spaces — i.e. a real room the player can be said to "arrive" in. Sub-zones
        /// like <c>hallway2</c> and virtual states like <c>office_tutorial</c> return false.
        /// </summary>
        public static bool IsPrimaryZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return false;
            EnsureLoaded();
            return _primaryZones.Contains(zoneName);
        }

        /// <summary>
        /// Maps a sub-zone or aliased zone name to its owning primary zone, e.g.
        /// <c>hallway2 -&gt; hallway</c>, <c>office_tutorial -&gt; office</c>,
        /// <c>upper_hallway3_shelley -&gt; upper_hallway</c>. Returns the input unchanged if it
        /// is already a primary zone or no alias rule matches.
        /// </summary>
        public static string ResolvePrimaryZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return zoneName;
            EnsureLoaded();
            if (_primaryZones.Contains(zoneName)) return zoneName;
            if (_aliasToPrimary.TryGetValue(zoneName, out string aliased)) return aliased;

            // Trailing-digit sub-zone (hallway2 -> hallway, gym_closet2 -> gym_closet) — only
            // accept when the stripped base is itself a known primary, so we don't promote
            // "bathroom1" (primary) into the non-existent "bathroom" primary.
            int end = zoneName.Length;
            while (end > 0 && char.IsDigit(zoneName[end - 1])) end--;
            if (end > 0 && end < zoneName.Length)
            {
                string stripped = zoneName.Substring(0, end);
                if (_primaryZones.Contains(stripped)) return stripped;
            }
            return zoneName;
        }

        /// <summary>
        /// Returns the static metadata for a Door GameObject by name, or null when the export
        /// does not cover that door.
        /// </summary>
        public static DoorMetadata GetDoorMetadata(string doorName)
        {
            if (string.IsNullOrEmpty(doorName)) return null;
            EnsureLoaded();
            return _doors.TryGetValue(doorName, out DoorMetadata md) ? md : null;
        }

        /// <summary>
        /// Returns the authored teleporter (e.g. <c>CrawlspaceLadder</c>) by name, or null when
        /// not present.
        /// </summary>
        public static TeleporterMetadata GetTeleporter(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            EnsureLoaded();
            return _teleporters.TryGetValue(name, out TeleporterMetadata t) ? t : null;
        }

        /// <summary>
        /// Yields every static blocker whose AABB intersects <paramref name="bounds"/>.
        /// </summary>
        public static IEnumerable<StaticBlocker> BlockersInBounds(Bounds bounds)
        {
            EnsureLoaded();
            for (int i = 0; i < _blockers.Count; i++)
            {
                StaticBlocker b = _blockers[i];
                if (b != null && b.Bounds.Intersects(bounds))
                    yield return b;
            }
        }

        // Player capsule extent below/above the foot anchor. Multi-floor scenes mean blockers
        // on a different floor share XZ footprint with the player but never collide — so we
        // pre-reject blockers whose Y span lies entirely outside this slab around the segment.
        private const float PlayerSlabBelow = 0.5f;
        private const float PlayerSlabAbove = 2.0f;

        /// <summary>
        /// Visibility test in the XZ plane with a Y-slab precheck: returns true when the
        /// segment from <paramref name="from"/> to <paramref name="to"/> does not cross any
        /// static blocker whose Y span overlaps the player's vertical extent along the segment.
        /// <para>
        /// <paramref name="skin"/> is a small inward shrink applied to each blocker's footprint
        /// (metres) so endpoints flush with a door frame or wall don't read as blocked.
        /// </para>
        /// </summary>
        public static bool IsSegmentClear(Vector3 from, Vector3 to, float skin = 0.05f)
        {
            return FindFirstBlocker(from, to, skin) == null;
        }

        /// <summary>
        /// Returns the first static blocker whose AABB intersects the segment, or null when
        /// the segment is clear. Useful for diagnostics ("no clear LoS — blocked by X").
        /// </summary>
        public static StaticBlocker FindFirstBlocker(Vector3 from, Vector3 to, float skin = 0.05f)
        {
            EnsureLoaded();

            float ax = from.x, az = from.z;
            float bx = to.x, bz = to.z;
            float dx = bx - ax, dz = bz - az;

            // Segment XZ bounding box for broad-phase rejection.
            float segMinX = dx >= 0f ? ax : bx;
            float segMaxX = dx >= 0f ? bx : ax;
            float segMinZ = dz >= 0f ? az : bz;
            float segMaxZ = dz >= 0f ? bz : az;

            // Y slab around the segment. A blocker whose AABB sits entirely above or below
            // this slab is on a different floor and can never collide with the player.
            float segMinY = (from.y < to.y ? from.y : to.y) - PlayerSlabBelow;
            float segMaxY = (from.y > to.y ? from.y : to.y) + PlayerSlabAbove;

            for (int i = 0; i < _blockers.Count; i++)
            {
                StaticBlocker b = _blockers[i];
                if (b == null) continue;

                if (b.MaxY < segMinY || b.MinY > segMaxY) continue;

                float bMinX = b.MinX + skin;
                float bMaxX = b.MaxX - skin;
                float bMinZ = b.MinZ + skin;
                float bMaxZ = b.MaxZ - skin;
                if (bMinX >= bMaxX || bMinZ >= bMaxZ) continue; // skin collapsed the footprint

                if (segMaxX < bMinX || segMinX > bMaxX) continue;
                if (segMaxZ < bMinZ || segMinZ > bMaxZ) continue;

                if (SegmentIntersectsRect(ax, az, dx, dz, bMinX, bMaxX, bMinZ, bMaxZ))
                    return b;
            }
            return null;
        }

        // 2D Liang-Barsky slab clip on the unit-parameter segment ax+t*dx, az+t*dz, t in [0,1].
        private static bool SegmentIntersectsRect(
            float ax, float az, float dx, float dz,
            float minX, float maxX, float minZ, float maxZ)
        {
            float tEnter = 0f;
            float tExit = 1f;

            // X slab.
            if (Mathf.Abs(dx) < 1e-6f)
            {
                if (ax < minX || ax > maxX) return false;
            }
            else
            {
                float t1 = (minX - ax) / dx;
                float t2 = (maxX - ax) / dx;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tEnter) tEnter = t1;
                if (t2 < tExit) tExit = t2;
                if (tEnter > tExit) return false;
            }

            // Z slab.
            if (Mathf.Abs(dz) < 1e-6f)
            {
                if (az < minZ || az > maxZ) return false;
            }
            else
            {
                float t1 = (minZ - az) / dz;
                float t2 = (maxZ - az) / dz;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tEnter) tEnter = t1;
                if (t2 < tExit) tExit = t2;
                if (tEnter > tExit) return false;
            }

            return true;
        }

        /// <summary>
        /// OcclusionPortal lookup keyed by Door GameObject name. Kept as a separate accessor so
        /// <see cref="SimpleNav.TryResolveDoorApproach"/> can stay on its fast path without going
        /// through the heavier <see cref="GetDoorMetadata"/> object.
        /// </summary>
        public static SimpleNavDoorPortal GetDoorPortal(string doorName)
        {
            DoorMetadata md = GetDoorMetadata(doorName);
            return md?.Portal;
        }

        public static void Reload()
        {
            _loaded = false;
            _primaryZones.Clear();
            _graphLinks.Clear();
            _doors.Clear();
            _teleporters.Clear();
            _blockers.Clear();
            _aliasToPrimary.Clear();
            _anchorsByZone.Clear();
            EnsureLoaded();
        }

        /// <summary>
        /// Returns every graph anchor whose primary zone name matches <paramref name="zoneName"/>.
        /// Aliases (e.g. <c>hallway2</c> → <c>hallway</c>) are resolved automatically. Returns
        /// an empty list when the zone is unknown.
        /// </summary>
        public static IReadOnlyList<NavGraphAnchor> GetAnchorsForZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return _emptyAnchors;
            EnsureLoaded();
            string primary = ResolvePrimaryZone(zoneName);
            return _anchorsByZone.TryGetValue(primary, out List<NavGraphAnchor> list) ? list : _emptyAnchors;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            LoadNavigationData();
            BuildAliasMap();
            LoadBlockers();
            LoadGraphAnchors();
        }

        private static void LoadNavigationData()
        {
            string path = Path.Combine(Paths.PluginPath, NavigationDataFileName);
            if (!File.Exists(path))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavSceneData: " + NavigationDataFileName + " not found at " + path);
                return;
            }

            // Unity's JsonUtility silently returns all-null arrays for this file (likely due to
            // the nested OcclusionPortal/Door schema), so we use DataContractJsonSerializer —
            // the same fallback NavigationGraph already employs for the same family of issue.
            SceneNavigationDataDoc doc = null;
            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SceneNavigationDataDoc));
                    doc = serializer.ReadObject(stream) as SceneNavigationDataDoc;
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavSceneData: failed to parse " + NavigationDataFileName + ": " + ex.Message);
                return;
            }
            if (doc == null)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavSceneData: parser returned null doc");
                return;
            }
            if (Main.Log != null)
            {
                Main.Log.LogInfo("SimpleNavSceneData: parsed doc"
                    + " CameraSpaces=" + (doc.CameraSpaces == null ? "null" : doc.CameraSpaces.Length.ToString())
                    + " GraphLinks=" + (doc.GraphLinks == null ? "null" : doc.GraphLinks.Length.ToString())
                    + " DoorObjects=" + (doc.DoorObjects == null ? "null" : doc.DoorObjects.Length.ToString())
                    + " Teleporters=" + (doc.Teleporters == null ? "null" : doc.Teleporters.Length.ToString())
                    + " OcclusionPortals=" + (doc.OcclusionPortals == null ? "null" : doc.OcclusionPortals.Length.ToString()));
            }

            if (doc.CameraSpaces != null)
            {
                for (int i = 0; i < doc.CameraSpaces.Length; i++)
                {
                    SceneNavigationCameraSpace cs = doc.CameraSpaces[i];
                    if (cs == null || string.IsNullOrEmpty(cs.Name)) continue;
                    if (cs.InNavigationGraph) _primaryZones.Add(cs.Name);
                }
            }

            if (doc.GraphLinks != null)
            {
                for (int i = 0; i < doc.GraphLinks.Length; i++)
                {
                    SceneNavigationGraphLink l = doc.GraphLinks[i];
                    if (l == null || string.IsNullOrEmpty(l.FromZone) || string.IsNullOrEmpty(l.ToZone)) continue;
                    _graphLinks.Add(new KeyValuePair<string, string>(l.FromZone, l.ToZone));
                }
            }

            Dictionary<int, SimpleNavDoorPortal> portalsByDoorId = null;
            if (doc.OcclusionPortals != null)
            {
                portalsByDoorId = new Dictionary<int, SimpleNavDoorPortal>(doc.OcclusionPortals.Length);
                for (int i = 0; i < doc.OcclusionPortals.Length; i++)
                {
                    SceneNavigationOcclusionPortal p = doc.OcclusionPortals[i];
                    if (p == null || string.IsNullOrEmpty(p.ParentDoorName) || p.Center == null || p.Size == null) continue;
                    SimpleNavDoorPortal portal = new SimpleNavDoorPortal
                    {
                        DoorName = p.ParentDoorName,
                        Center = ToVector3(p.Center),
                        Size = ToVector3(p.Size),
                    };
                    if (p.ParentDoorGameObjectId != 0)
                        portalsByDoorId[p.ParentDoorGameObjectId] = portal;
                }
            }

            if (doc.DoorObjects != null)
            {
                for (int i = 0; i < doc.DoorObjects.Length; i++)
                {
                    SceneNavigationDoor d = doc.DoorObjects[i];
                    if (d == null || string.IsNullOrEmpty(d.Name) || d.DoorComponent == null) continue;
                    // Same door name can appear multiple times in the export (different prefab
                    // variants); the first entry with a real DoorComponent wins. Subsequent
                    // duplicates are silently skipped — they share the same name key.
                    if (_doors.ContainsKey(d.Name)) continue;

                    DoorMetadata md = new DoorMetadata
                    {
                        Name = d.Name,
                        Position = ToVector3(d.Position),
                        Rotation = ToQuaternion(d.Rotation),
                        NearestPrimaryZones = ExtractPrimaryZoneNames(d.NearestGraphZones),
                    };
                    if (portalsByDoorId != null && portalsByDoorId.TryGetValue(d.Id, out SimpleNavDoorPortal portal))
                        md.Portal = portal;
                    _doors[d.Name] = md;
                }
            }

            if (doc.Teleporters != null)
            {
                for (int i = 0; i < doc.Teleporters.Length; i++)
                {
                    SceneNavigationTeleporter t = doc.Teleporters[i];
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    _teleporters[t.Name] = new TeleporterMetadata
                    {
                        Name = t.Name,
                        IsCrawlspace = t.IsCrawlspace,
                        Position = ToVector3(t.Position),
                        LocationDown = t.LocationDown != null ? ToVector3(t.LocationDown.Position) : Vector3.zero,
                        LocationUp = t.LocationUp != null ? ToVector3(t.LocationUp.Position) : Vector3.zero,
                        TeleportInRotation = t.TeleportInRotation != null ? Quaternion.Euler(ToVector3(t.TeleportInRotation)) : Quaternion.identity,
                        TeleportOutRotation = t.TeleportOutRotation != null ? Quaternion.Euler(ToVector3(t.TeleportOutRotation)) : Quaternion.identity,
                    };
                }
            }

            if (Main.Log != null)
            {
                Main.Log.LogInfo("SimpleNavSceneData: loaded " + _primaryZones.Count + " primary zones, "
                    + _graphLinks.Count + " graph links, " + _doors.Count + " doors, "
                    + _teleporters.Count + " teleporters.");
            }
        }

        private static void BuildAliasMap()
        {
            // Explicit alias entries cover non-digit suffixes (e.g. office_tutorial -> office).
            // Trailing-digit sub-zones are handled at query time inside ResolvePrimaryZone, so
            // they don't need entries here.
            foreach (string primary in _primaryZones)
            {
                for (int s = 0; s < AliasSuffixes.Length; s++)
                {
                    string aliased = primary + AliasSuffixes[s];
                    if (!_primaryZones.Contains(aliased))
                        _aliasToPrimary[aliased] = primary;
                }
            }
        }

        private static void LoadBlockers()
        {
            string path = Path.Combine(Paths.PluginPath, BlockersFileName);
            if (!File.Exists(path))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavSceneData: " + BlockersFileName + " not found at " + path);
                return;
            }

            SceneBlockersDoc doc = null;
            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SceneBlockersDoc));
                    doc = serializer.ReadObject(stream) as SceneBlockersDoc;
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavSceneData: failed to parse " + BlockersFileName + ": " + ex.Message);
                return;
            }
            if (doc?.NavigationBlockers == null) return;

            for (int i = 0; i < doc.NavigationBlockers.Length; i++)
            {
                SceneBlockerEntry e = doc.NavigationBlockers[i];
                if (e?.Bounds3D?.Center == null || e.Bounds3D.Size == null) continue;
                Vector3 c = ToVector3(e.Bounds3D.Center);
                Vector3 s = ToVector3(e.Bounds3D.Size);
                Vector3 half = s * 0.5f;
                _blockers.Add(new StaticBlocker
                {
                    Name = e.Name,
                    Bounds = new Bounds(c, s),
                    MinX = c.x - half.x,
                    MaxX = c.x + half.x,
                    MinZ = c.z - half.z,
                    MaxZ = c.z + half.z,
                    MinY = c.y - half.y,
                    MaxY = c.y + half.y,
                });
            }

            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavSceneData: loaded " + _blockers.Count + " static blockers.");
        }

        private static void LoadGraphAnchors()
        {
            string path = Path.Combine(Paths.PluginPath, NavigationGraphFileName);
            if (!File.Exists(path))
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavSceneData: " + NavigationGraphFileName + " not found at " + path);
                return;
            }

            SceneGraphDoc doc = null;
            try
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SceneGraphDoc));
                    doc = serializer.ReadObject(stream) as SceneGraphDoc;
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavSceneData: failed to parse " + NavigationGraphFileName + ": " + ex.Message);
                return;
            }
            if (doc?.Nodes == null) return;

            for (int i = 0; i < doc.Nodes.Length; i++)
            {
                SceneGraphNode n = doc.Nodes[i];
                if (n == null || string.IsNullOrEmpty(n.Zone) || n.Position == null) continue;
                if (!_anchorsByZone.TryGetValue(n.Zone, out List<NavGraphAnchor> list))
                {
                    list = new List<NavGraphAnchor>(4);
                    _anchorsByZone[n.Zone] = list;
                }
                list.Add(new NavGraphAnchor
                {
                    Id = n.Id,
                    Zone = n.Zone,
                    SceneZoneName = n.SceneZoneName,
                    Kind = n.Kind,
                    Position = ToVector3(n.Position),
                });
            }

            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavSceneData: loaded graph anchors across " + _anchorsByZone.Count + " zones.");
        }

        // Restrict the per-door nearest-zone list to primaries so callers don't have to filter.
        private static IReadOnlyList<string> ExtractPrimaryZoneNames(SceneNavigationZoneDistance[] zones)
        {
            if (zones == null || zones.Length == 0) return Array.Empty<string>();
            List<string> names = new List<string>(zones.Length);
            for (int i = 0; i < zones.Length; i++)
            {
                SceneNavigationZoneDistance z = zones[i];
                if (z != null && !string.IsNullOrEmpty(z.Name) && _primaryZones.Contains(z.Name))
                    names.Add(z.Name);
            }
            return names;
        }

        private static Vector3 ToVector3(SceneNavigationVector3 v)
        {
            return v == null ? Vector3.zero : new Vector3(v.x, v.y, v.z);
        }

        private static Quaternion ToQuaternion(SceneNavigationQuaternion q)
        {
            return q == null ? Quaternion.identity : new Quaternion(q.x, q.y, q.z, q.w);
        }

        // DataContractJsonSerializer DTOs. We use this instead of Unity's JsonUtility because
        // JsonUtility silently returns all-null arrays on this file (likely confused by nested
        // schema). NavigationGraph uses the same fallback for the same family of issue.
        //
        // [DataContract] classes must list every JSON field we care about as [DataMember];
        // unknown JSON fields are ignored by default.
#pragma warning disable CS0649
        [DataContract]
        private class SceneNavigationDataDoc
        {
            [DataMember] public SceneNavigationCameraSpace[] CameraSpaces;
            [DataMember] public SceneNavigationGraphLink[] GraphLinks;
            [DataMember] public SceneNavigationDoor[] DoorObjects;
            [DataMember] public SceneNavigationTeleporter[] Teleporters;
            [DataMember] public SceneNavigationOcclusionPortal[] OcclusionPortals;
        }

        [DataContract]
        private class SceneNavigationCameraSpace
        {
            [DataMember] public string Name;
            [DataMember] public bool InNavigationGraph;
        }

        [DataContract]
        private class SceneNavigationGraphLink
        {
            [DataMember] public string FromZone;
            [DataMember] public string ToZone;
        }

        [DataContract]
        private class SceneNavigationDoor
        {
            [DataMember] public string Name;
            [DataMember] public int Id;
            [DataMember] public SceneNavigationVector3 Position;
            [DataMember] public SceneNavigationQuaternion Rotation;
            [DataMember] public SceneNavigationZoneDistance[] NearestGraphZones;
            [DataMember] public SceneNavigationDoorComponent DoorComponent;
        }

        [DataContract]
        private class SceneNavigationDoorComponent
        {
            [DataMember] public int ComponentId;
        }

        [DataContract]
        private class SceneNavigationZoneDistance
        {
            [DataMember] public string Name;
            [DataMember] public float Distance;
        }

        [DataContract]
        private class SceneNavigationTeleporter
        {
            [DataMember] public string Name;
            [DataMember] public bool IsCrawlspace;
            [DataMember] public SceneNavigationVector3 Position;
            [DataMember] public SceneNavigationVector3 TeleportInRotation;   // stored as Euler in the export
            [DataMember] public SceneNavigationVector3 TeleportOutRotation;
            [DataMember] public SceneNavigationTeleporterEnd LocationDown;
            [DataMember] public SceneNavigationTeleporterEnd LocationUp;
        }

        [DataContract]
        private class SceneNavigationTeleporterEnd
        {
            [DataMember] public string Name;
            [DataMember] public SceneNavigationVector3 Position;
            [DataMember] public SceneNavigationQuaternion Rotation;
        }

        [DataContract]
        private class SceneNavigationOcclusionPortal
        {
            [DataMember] public string ParentDoorName;
            [DataMember] public int ParentDoorGameObjectId;
            [DataMember] public SceneNavigationVector3 Center;
            [DataMember] public SceneNavigationVector3 Size;
        }

        [DataContract]
        private class SceneNavigationVector3
        {
            [DataMember] public float x;
            [DataMember] public float y;
            [DataMember] public float z;
        }

        [DataContract]
        private class SceneNavigationQuaternion
        {
            [DataMember] public float x;
            [DataMember] public float y;
            [DataMember] public float z;
            [DataMember] public float w;
        }

        [DataContract]
        private class SceneBlockersDoc
        {
            [DataMember] public SceneBlockerEntry[] NavigationBlockers;
        }

        [DataContract]
        private class SceneBlockerEntry
        {
            [DataMember] public string Name;
            [DataMember] public SceneBlockerBounds Bounds3D;
        }

        [DataContract]
        private class SceneBlockerBounds
        {
            [DataMember] public SceneNavigationVector3 Center;
            [DataMember] public SceneNavigationVector3 Size;
        }
        [DataContract]
        private class SceneGraphDoc
        {
            [DataMember] public SceneGraphNode[] Nodes;
        }

        [DataContract]
        private class SceneGraphNode
        {
            [DataMember] public string Id;
            [DataMember] public string Zone;
            [DataMember] public string SceneZoneName;
            [DataMember] public string Kind;
            [DataMember] public SceneNavigationVector3 Position;
        }
#pragma warning restore CS0649
    }
}
