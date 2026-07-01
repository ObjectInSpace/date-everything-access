using HarmonyLib;

namespace DateEverythingAccess
{
    /// <summary>
    /// Announces interaction results that the base game only communicates visually. When an
    /// interactable changes something in the environment but plays no sound that conveys the
    /// resulting state, a sighted player reads the change (a colour, an icon, a light turning
    /// on) while a screen reader user gets nothing. These patches speak the new state.
    ///
    /// Deliberately excluded: interactions whose result is already audible (a running faucet,
    /// a crackling fireplace, music). Those need no spoken cue.
    ///
    /// The announcements honour the "read status changes" accessibility toggle, matching the
    /// other environmental status announcements in <see cref="AccessibilityWatcher"/>.
    /// </summary>
    internal static class InteractionFeedbackPatches
    {
        /// <summary>
        /// Thermostat. <see cref="HvacControls.Interact"/> toggles between room temperature and
        /// cold when the Dateviators are not equipped. The vents make noise, but the resulting
        /// temperature is shown only as a colour and icon, so we speak it. Patching the public
        /// Interact keeps this off the save-load path (LoadState calls SwitchTemperature directly)
        /// and off the magical branch (which returns without changing temperature).
        /// </summary>
        [HarmonyPatch(typeof(HvacControls), nameof(HvacControls.Interact))]
        [HarmonyPostfix]
        private static void OnHvacInteract(HvacControls __instance)
        {
            // Arm the Ctrl+F6 "thermostat → Ayrin" context default. Independent of the spoken
            // status toggle below (which the player can disable), so it runs first and unconditionally.
            NavigationContextTriggers.ThermostatInteracted = true;
            NavigationContextTriggers.ThermostatRoom = AccessibilityWatcher.GetPlayerRoomForContext();

            if (!ModConfig.ReadStatusChanges)
                return;

            string key = __instance.temperature == HvacControls.Temperature.COLD
                ? "thermostat_cold"
                : "thermostat_room_temp";
            ScreenReader.Say(Loc.Get(key), interrupt: false);
        }

        /// <summary>
        /// Light switch. The parameterless <see cref="Lights_Inter.Interact()"/> is the
        /// player-driven entry point; it flips the light only when the Dateviators are not
        /// equipped. Programmatic toggles (lighting scenarios, tutorial setup) go through the
        /// overloads instead, so patching the override here announces only genuine player flips.
        /// The switch click sounds the same on and off, so the on/off state is visual-only.
        /// The captured pre-state lets us stay silent on the equipped (magical-line) branch,
        /// which does not toggle.
        /// </summary>
        [HarmonyPatch(typeof(Lights_Inter), nameof(Lights_Inter.Interact), new System.Type[] { })]
        [HarmonyPrefix]
        private static void BeforeLightInteract(Lights_Inter __instance, out bool __state)
        {
            __state = __instance.lightsOn;
        }

        [HarmonyPatch(typeof(Lights_Inter), nameof(Lights_Inter.Interact), new System.Type[] { })]
        [HarmonyPostfix]
        private static void AfterLightInteract(Lights_Inter __instance, bool __state)
        {
            if (!ModConfig.ReadStatusChanges)
                return;

            // No change means the magical/equipped branch ran; nothing to announce.
            if (__instance.lightsOn == __state)
                return;

            string name = string.IsNullOrWhiteSpace(__instance.type)
                ? Loc.Get("light_default_name")
                : __instance.type;
            string key = __instance.lightsOn ? "light_on" : "light_off";
            ScreenReader.Say(Loc.Get(key, name), interrupt: false);
        }
    }
}
