using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BepInEx;

namespace DateEverythingAccess
{
    /// <summary>
    /// Static metadata loader for primary-zone identity. Loads
    /// <c>thirdpersongreybox-navigation-data.json</c> once and exposes the set of
    /// <c>InNavigationGraph</c> camera spaces. Sub-zones, doors, teleporters, blockers and
    /// graph anchors are no longer consumed in the route-driven nav model and are not loaded.
    /// </summary>
    internal static class SimpleNavSceneData
    {
        private const string NavigationDataFileName = "thirdpersongreybox-navigation-data.json";

        private static bool _loaded;
        private static readonly HashSet<string> _primaryZones =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when <paramref name="zoneName"/> is one of the <c>InNavigationGraph</c> camera
        /// spaces — i.e. a real room the player can be said to "arrive" in. Sub-zones like
        /// <c>hallway2</c> and virtual states like <c>office_tutorial</c> return false.
        /// </summary>
        public static bool IsPrimaryZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return false;
            EnsureLoaded();
            return _primaryZones.Contains(zoneName);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            LoadNavigationData();
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
            // the nested schema), so we use DataContractJsonSerializer.
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
            if (doc?.CameraSpaces == null) return;

            for (int i = 0; i < doc.CameraSpaces.Length; i++)
            {
                SceneNavigationCameraSpace cs = doc.CameraSpaces[i];
                if (cs == null || string.IsNullOrEmpty(cs.Name)) continue;
                if (cs.InNavigationGraph) _primaryZones.Add(cs.Name);
            }

            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavSceneData: loaded " + _primaryZones.Count + " primary zones.");
        }

#pragma warning disable CS0649
        [DataContract]
        private class SceneNavigationDataDoc
        {
            [DataMember] public SceneNavigationCameraSpace[] CameraSpaces;
        }

        [DataContract]
        private class SceneNavigationCameraSpace
        {
            [DataMember] public string Name;
            [DataMember] public bool InNavigationGraph;
        }
#pragma warning restore CS0649
    }
}
