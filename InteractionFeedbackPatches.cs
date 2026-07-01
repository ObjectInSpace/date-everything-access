using HarmonyLib;

namespace DateEverythingAccess
{
    // Announces interaction results that the base game only communicates visually: an
    // interactable changes the environment but plays no sound conveying the RESULTING state, so a
    // screen reader user gets nothing. We speak the new state. Deliberately excluded: results that
    // are already audible (faucet water, fireplace crackle, music).
    //
    // IMPORTANT: each patch class carries a CLASS-LEVEL [HarmonyPatch] attribute. Harmony's
    // PatchAll() discovers patch classes by that class attribute; a class with only method-level
    // [HarmonyPatch] attributes is silently skipped (this whole feature was dead for exactly that
    // reason until split into per-type classes). One class per target type since the attribute
    // names the type.

    /// <summary>
    /// Thermostat. SwitchTemperature is the actual toggle (room temperature ↔ cold). We patch it
    /// rather than the Interact() override so we don't depend on that override being the dispatched
    /// method, and gate on a live player interaction (GameController.SelectObj active) so the
    /// save-load path (LoadState → SwitchTemperature) and the magical branch stay silent. The vents
    /// make noise, but the resulting temperature is shown only as a colour + icon.
    /// </summary>
    [HarmonyPatch(typeof(HvacControls), "SwitchTemperature")]
    internal static class HvacSwitchTemperaturePatch
    {
        [HarmonyPostfix]
        private static void Postfix(HvacControls __instance)
        {
            if (!AccessibilityWatcher.IsPlayerDrivenInteractionActive)
                return;

            if (!ModConfig.ReadStatusChanges)
                return;

            string key = __instance.temperature == HvacControls.Temperature.COLD
                ? "thermostat_cold"
                : "thermostat_room_temp";
            ScreenReader.Say(Loc.Get(key), interrupt: false);
        }
    }

    /// <summary>
    /// Light switch. Interact(bool) performs the visible toggle (the parameterless Interact()
    /// override delegates to it for a player flip). LightingScenarios also calls it programmatically
    /// for profile swaps, so we gate on a live player interaction. The switch click sounds identical
    /// on and off, so the resulting on/off state is visual-only. Named by the switch's type.
    /// </summary>
    [HarmonyPatch(typeof(Lights_Inter), nameof(Lights_Inter.Interact), new System.Type[] { typeof(bool) })]
    internal static class LightsInterInteractPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Lights_Inter __instance)
        {
            if (!ModConfig.ReadStatusChanges)
                return;

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
