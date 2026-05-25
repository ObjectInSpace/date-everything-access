using HarmonyLib;

namespace DateEverythingAccess
{
    [HarmonyPatch(typeof(DateADex))]
    internal static class DateADexPatches
    {
        [HarmonyPatch("OpenEntry", new[] { typeof(int) })]
        [HarmonyPostfix]
        private static void OnOpenEntry(int id)
        {
            DateADexEntry entry = DateADex.Instance != null ? DateADex.Instance.GetEntry(id) : null;
            AccessibilityWatcher.RequestDateADexEntryAnnouncement(entry);
        }
    }
}
