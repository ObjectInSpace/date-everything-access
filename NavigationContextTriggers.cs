using HarmonyLib;

namespace DateEverythingAccess
{
    /// <summary>
    /// Latches for the context-sensitive Ctrl+F6 defaults that are triggered by an interaction
    /// rather than by where the player currently is. Location-based defaults (crawlspace, attic)
    /// are evaluated live from the player position and need no latch.
    ///
    /// Each interaction latch stays the Ctrl+F6 default until EITHER:
    ///   - the associated datable is met (applied in <see cref="AccessibilityWatcher"/>, which
    ///     owns save state); or
    ///   - the player leaves the room where the trigger happened. The resolver evaluates this
    ///     live on every Ctrl+F6 press by comparing the player's current room to the room
    ///     captured here, so "context changed / left the room" needs no separate clearing hook.
    ///
    /// The room where each trigger fired is captured by <see cref="AccessibilityWatcher"/> at
    /// arm time (patches have no easy access to the room-bounds index). See
    /// <c>AccessibilityWatcher.TryResolveContextDefaultTarget</c>.
    /// </summary>
    internal static class NavigationContextTriggers
    {
        /// <summary>
        /// Armed when the couch reveals the dust bunny (dolly). "On reveal only": the sofa does
        /// not move the bunny out if dolly is already realized, so this is set from the reveal
        /// itself, not from merely touching the couch.
        /// </summary>
        internal static bool CouchRevealedDustBunny;

        /// <summary>Room the player was in when the couch revealed the dust bunny.</summary>
        internal static string CouchRoom;

        /// <summary>Armed when the player switches the thermostat (airyn).</summary>
        internal static bool ThermostatInteracted;

        /// <summary>Room the player was in when the thermostat was switched.</summary>
        internal static string ThermostatRoom;
    }

    /// <summary>
    /// Arms <see cref="NavigationContextTriggers.CouchRevealedDustBunny"/> when the sofa reveals
    /// the dust bunny. <see cref="Sofa.MoveSofa(bool)"/> only activates the bunny under its own
    /// reveal conditions, so we confirm the bunny is active before arming.
    /// </summary>
    [HarmonyPatch(typeof(Sofa), nameof(Sofa.MoveSofa), new System.Type[] { typeof(bool) })]
    internal static class SofaMoveSofaPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Sofa __instance)
        {
            if (__instance != null &&
                __instance.dustBunny != null &&
                __instance.dustBunny.activeSelf)
            {
                NavigationContextTriggers.CouchRevealedDustBunny = true;
                NavigationContextTriggers.CouchRoom = AccessibilityWatcher.GetPlayerRoomForContext();
            }
        }
    }
}
