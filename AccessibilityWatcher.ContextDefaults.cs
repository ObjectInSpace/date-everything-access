using System;
using UnityEngine;

namespace DateEverythingAccess
{
    // Context-sensitive Ctrl+F6 defaults. When the player presses Ctrl+F6 (navigate to
    // objective) and there is NO active tutorial objective, these steer to a specific datable.
    // The tutorial objective takes precedence — the caller (StartNavigationToCurrentTarget) only
    // reaches here after both objective resolvers have failed. They are Ctrl+F6's own logic and
    // are deliberately independent of the Ctrl+Shift+F6 known-objects picker.
    //
    // Each target is gated on the datable's OWN in-game appearance condition, read live, so we
    // only steer to something the game is currently showing (and that the player hasn't met yet):
    //   1. Player in the crawlspace                 -> the Key (keith), else the rat trap (vaughn)
    //   2. Zoey's ghost is currently manifested      -> Zoey (zoey)
    //   3. Thermostat is set to cold (the air blows) -> Ayrin (airyn)
    //   4. The dust bunny is revealed                -> Dust Bunny (dolly)
    //
    // No interaction latches or room-matching: the game activates/deactivates each of these when
    // its condition holds, so we just ask the game.
    internal sealed partial class AccessibilityWatcher
    {
        // Datable internal names for the context defaults. keith = the Key (keyes is the piano);
        // vaughn = the rat trap (crawlspace fallback); dolly = the Dust Bunny; airyn = Ayrin (the
        // thermostat/air datable); zoey = Zoey (the ghost).
        private const string ContextKeyInternalName = "keith";
        private const string ContextRatTrapInternalName = "vaughn";
        private const string ContextDustBunnyInternalName = "dolly";
        private const string ContextThermostatInternalName = "airyn";
        private const string ContextGhostInternalName = "zoey";

        /// <summary>
        /// Resolves a context-sensitive Ctrl+F6 default. On a match, sets it as the tracked
        /// interactable (zone + label resolved) and returns true; the caller then navigates to it
        /// exactly as it would any objective target.
        /// </summary>
        private bool TryResolveContextDefaultTarget(out InteractableObj interactable, out string targetZone, out string targetLabel)
        {
            interactable = null;
            targetZone = null;
            targetLabel = null;

            if (BetterPlayerControl.Instance == null)
                return false;

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;

            // 1. Crawlspace: same Y gate the picker/room-scan use for the crawlspace band. Below
            // the ceiling line you can only have gotten there via the ladder, so this is a clean
            // "you are in the crawlspace" test. Target the Key, falling back to the rat trap once
            // the Key is no longer present (e.g. after the player dates it — the object goes
            // inactive, so TrySetContextDatableTarget skips it and we steer to the rat trap).
            if (playerPosition.y < CrawlspaceCeilingY)
            {
                if (TrySetContextDatableTarget(ContextKeyInternalName, out interactable, out targetZone, out targetLabel))
                    return true;
                if (TrySetContextDatableTarget(ContextRatTrapInternalName, out interactable, out targetZone, out targetLabel))
                    return true;
            }

            // 2. Zoey: only while her ghost is actually manifested (the game's own visibility rule).
            if (!IsDatableMet(ContextGhostInternalName) && IsGhostManifested() &&
                TrySetContextDatableTarget(ContextGhostInternalName, out interactable, out targetZone, out targetLabel))
            {
                return true;
            }

            // 3. Ayrin: only while the thermostat is set to cold (the air is blowing from the vents).
            if (!IsDatableMet(ContextThermostatInternalName) && IsThermostatCold() &&
                TrySetContextDatableTarget(ContextThermostatInternalName, out interactable, out targetZone, out targetLabel))
            {
                return true;
            }

            // 4. Dolly: only while the dust bunny is revealed (the couch has moved it into the open).
            if (!IsDatableMet(ContextDustBunnyInternalName) && IsDustBunnyRevealed() &&
                TrySetContextDatableTarget(ContextDustBunnyInternalName, out interactable, out targetZone, out targetLabel))
            {
                return true;
            }

            return false;
        }

        private static bool IsDatableMet(string internalName)
        {
            Save save = Singleton<Save>.Instance;
            return save != null && save.GetDateStatus(internalName) != RelationshipStatus.Unmet;
        }

        // Zoey's ghost is manifested when GhostController's orb is active. That single flag is
        // whatever the game's own visibility logic (GhostController.CheckTime) decided, so reading
        // it matches exactly what the player sees — we don't reproduce the rule.
        private static bool IsGhostManifested()
        {
            GhostController ghost = FindObjectOfType<GhostController>();
            return ghost != null && ghost.orb != null && ghost.orb.activeInHierarchy;
        }

        // Ayrin (airyn) is present in the house while the thermostat is set to cold — the air
        // blowing from the vents is Ayrin. Read the live HvacControls temperature.
        private static bool IsThermostatCold()
        {
            HvacControls hvac = FindObjectOfType<HvacControls>();
            return hvac != null && hvac.temperature == HvacControls.Temperature.COLD;
        }

        // Dolly (the dust bunny) is present once the couch has revealed her. Sofa.dustBunny is the
        // GameObject the couch activates; it's inactive until revealed and again once realized.
        private static bool IsDustBunnyRevealed()
        {
            Sofa sofa = FindObjectOfType<Sofa>();
            return sofa != null && sofa.dustBunny != null && sofa.dustBunny.activeInHierarchy;
        }

        // Finds the live datable interactable for an internal name and, on success, installs it
        // as the tracked target with its resolved zone and label — mirroring the objective path.
        private bool TrySetContextDatableTarget(string internalName, out InteractableObj interactable, out string targetZone, out string targetLabel)
        {
            interactable = null;
            targetZone = null;
            targetLabel = null;

            if (!TryFindDatableByInternalName(internalName, out InteractableObj found) || found == null)
                return false;

            if (!TryGetTrackedInteractableZone(found, out targetZone))
                return false;

            // TryGetTrackedInteractableZone may redirect _trackedInteractable to a navigable
            // stand-in; use whatever it settled on for the label and target. The caller installs
            // it via SetTrackedInteractable, mirroring the objective path.
            interactable = _trackedInteractable != null ? _trackedInteractable : found;
            targetLabel = GetTrackedInteractableLabel(interactable);
            return true;
        }

        private static bool TryFindDatableByInternalName(string internalName, out InteractableObj interactable)
        {
            interactable = null;
            if (string.IsNullOrWhiteSpace(internalName))
                return false;

            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                if (string.Equals(candidate.InternalName(), internalName, StringComparison.OrdinalIgnoreCase))
                {
                    interactable = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
