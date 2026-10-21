using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DateEverythingAccess
{
    // Which dialogue lines are actually voiced. Most of the cast speaks with recorded voice lines,
    // but some characters (in the Sassy Chap's content, among others) "speak" in text blips: beeps
    // and boops while the line types out. A player who turns dialogue reading off because the game
    // is voiced still needs those lines read. The game plays the blips through a method called
    // PlayRPGBlip and voice lines through PlayVoiceOver / PlayVoiceOverNoDelay; we note when each
    // last ran. The methods are found by name at load, so a game update that renames them just
    // switches this off (and says so in the log) rather than breaking the other patches.
    internal static class DialogueAudioWatch
    {
        public static float LastBlipTime = -100f;
        public static float LastVoiceOverTime = -100f;
        public static bool BlipHooked;
        public static bool VoiceOverHooked;

        internal static List<MethodBase> FindMethods(params string[] names)
        {
            var found = new List<MethodBase>();
            Assembly game = typeof(TalkingUI).Assembly;
            Type[] types;
            try { types = game.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            foreach (Type t in types)
            {
                if (t == null) continue;
                MethodInfo[] methods;
                try
                {
                    methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { continue; }
                foreach (MethodInfo m in methods)
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    for (int i = 0; i < names.Length; i++)
                        if (string.Equals(m.Name, names[i], StringComparison.Ordinal))
                        {
                            found.Add(m);
                            break;
                        }
                }
            }
            return found;
        }
    }

    [HarmonyPatch]
    internal static class DialogueBlipPatch
    {
        private static List<MethodBase> _targets;

        private static bool Prepare()
        {
            _targets = DialogueAudioWatch.FindMethods("PlayRPGBlip");
            DialogueAudioWatch.BlipHooked = _targets.Count > 0;
            if (Main.Log != null)
                Main.Log.LogInfo("DialogueAudioWatch: text-blip methods found=" + _targets.Count);
            return _targets.Count > 0;
        }

        private static IEnumerable<MethodBase> TargetMethods() => _targets;

        [HarmonyPostfix]
        private static void Postfix()
        {
            DialogueAudioWatch.LastBlipTime = Time.unscaledTime;
        }
    }

    [HarmonyPatch]
    internal static class DialogueVoiceOverPatch
    {
        private static List<MethodBase> _targets;

        private static bool Prepare()
        {
            _targets = DialogueAudioWatch.FindMethods("PlayVoiceOver", "PlayVoiceOverNoDelay");
            DialogueAudioWatch.VoiceOverHooked = _targets.Count > 0;
            if (Main.Log != null)
                Main.Log.LogInfo("DialogueAudioWatch: voice-over methods found=" + _targets.Count);
            return _targets.Count > 0;
        }

        private static IEnumerable<MethodBase> TargetMethods() => _targets;

        [HarmonyPostfix]
        private static void Postfix()
        {
            DialogueAudioWatch.LastVoiceOverTime = Time.unscaledTime;
        }
    }
}
