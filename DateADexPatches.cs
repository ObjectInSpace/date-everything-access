using HarmonyLib;

namespace DateEverythingAccess
{
    [HarmonyPatch(typeof(DateADex))]
    internal static class DateADexPatches
    {
        [HarmonyPatch("OpenEntry", new[] { typeof(int) })]
        [HarmonyPostfix]
        private static void OnOpenEntry()
        {
            AccessibilityWatcher.RequestDateADexEntryAnnouncement();
        }
    }
}
