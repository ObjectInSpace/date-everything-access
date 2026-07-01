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
        /// Thermostat. SwitchTemperature is the actual toggle (room temperature ↔ cold); we patch
        /// it rather than the Interact() override so the announcement doesn't depend on that
        /// override being the dispatched method. It's also invoked on save-load (LoadState →
        /// SwitchTemperature) and the magical branch, so we gate on a live player interaction
        /// (SelectObj active) to fire only on a genuine player flip. The vents make noise, but the
        /// resulting temperature is shown only as a colour and icon, so we speak it.
        /// </summary>
        [HarmonyPatch(typeof(HvacControls), "SwitchTemperature")]
        [HarmonyPostfix]
        private static void OnHvacSwitchTemperature(HvacControls __instance)
        {
            if (!AccessibilityWatcher.IsPlayerDrivenInteractionActive)
                return;

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
        /// Light switch. We patch <see cref="Lights_Inter.Interact(bool)"/> — the method that
        /// actually performs the visible toggle — rather than the parameterless override, because
        /// a player flip does not reliably dispatch through that override at runtime. But
        /// Interact(bool) is ALSO called programmatically by LightingScenarios (profile swaps at
        /// scene load / time change), which must NOT announce. We distinguish the two with a
        /// short-lived "player is interacting" flag set by the GameController.SelectObj hook
        /// (<see cref="AccessibilityWatcher.IsPlayerDrivenInteractionActive"/>): only a toggle that
        /// happens inside a live player interaction is announced. The switch click sounds the same
        /// on and off, so the resulting on/off state is visual-only.
        /// </summary>
        [HarmonyPatch(typeof(Lights_Inter), nameof(Lights_Inter.Interact), new System.Type[] { typeof(bool) })]
        [HarmonyPostfix]
        private static void AfterLightInteract(Lights_Inter __instance)
        {
            if (!ModConfig.ReadStatusChanges)
                return;

            // Only announce player-driven flips, not LightingScenarios' programmatic profile swaps.
            if (!AccessibilityWatcher.IsPlayerDrivenInteractionActive)
                return;

            string name = string.IsNullOrWhiteSpace(__instance.type)
                ? Loc.Get("light_default_name")
                : __instance.type;
            string key = __instance.lightsOn ? "light_on" : "light_off";
            ScreenReader.Say(Loc.Get(key, name), interrupt: false);
        }
    }
}
