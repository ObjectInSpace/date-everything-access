using HarmonyLib;

namespace DateEverythingAccess
{
    [HarmonyPatch(typeof(ObjectExamine))]
    internal static class ObjectExaminePatches
    {
        [HarmonyPatch("ShowExamine")]
        [HarmonyPostfix]
        private static void OnShowExamine(ObjectExamine __instance, bool __result)
        {
            if (__result)
                AccessibilityWatcher.RememberExaminedObject(__instance);
        }
    }
}
