using System;
using UnityEngine;

namespace DateEverythingAccess
{
    // Context-sensitive Ctrl+F6 defaults. When the player presses Ctrl+F6 (navigate to
    // objective) and there is NO active tutorial objective, these steer to a specific datable
    // based purely on the player's live location at press time. The tutorial objective takes
    // precedence — the caller (StartNavigationToCurrentTarget) only reaches here after both
    // objective resolvers have failed. They are Ctrl+F6's own logic and are deliberately
    // independent of the Ctrl+Shift+F6 known-objects picker, which serves a different purpose
    // (browsing/selecting a target by hand).
    //
    // Cases, checked in this order (first match wins):
    //   1. Player in the crawlspace              -> the Key (keith)
    //   2. Player in the attic with glasses off  -> Zoey (zoey)
    //   3. Player still in the room where they switched the thermostat, Ayrin unmet -> Ayrin (airyn)
    //   4. Player still in the room where the couch revealed the dust bunny, Dolly unmet -> Dust Bunny (dolly)
    //
    // Cases 3 and 4 are armed by an interaction (see NavigationContextTriggers) and remain the
    // default until the datable is met OR the player leaves the room where the trigger happened
    // — both evaluated live here, so no separate clearing hook is needed.
    internal sealed partial class AccessibilityWatcher
    {
        // Datable internal names for the context defaults. keith = the Key (keyes is the piano);
        // vaughn = the rat trap (crawlspace fallback); dolly = the Dust Bunny; airyn = Ayrin (the
        // thermostat datable); zoey = Zoey.
        private const string ContextKeyInternalName = "keith";
        private const string ContextRatTrapInternalName = "vaughn";
        private const string ContextDustBunnyInternalName = "dolly";
        private const string ContextThermostatInternalName = "airyn";
        private const string ContextAtticInternalName = "zoey";

        /// <summary>
        /// The player's current room (spoken hierarchy-room name), or null if it can't be
        /// resolved. Used both to capture where an interaction trigger fired and to check, on a
        /// later Ctrl+F6 press, whether the player is still in that room.
        /// </summary>
        internal static string GetPlayerRoomForContext()
        {
            if (_instance == null || BetterPlayerControl.Instance == null)
                return null;

            if (_instance._roomBoundsIndex == null)
                _instance._roomBoundsIndex = _instance.BuildRoomBoundsIndex();

            return _instance.ResolveRoomByBounds(BetterPlayerControl.Instance.transform.position);
        }

        /// <summary>
        /// Resolves a context-sensitive Ctrl+F6 default from the player's live location. On a
        /// match, sets it as the tracked interactable (zone + label resolved) and returns true;
        /// the caller then navigates to it exactly as it would any objective target.
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

            // Room-based checks share the player's current room.
            string playerRoom = GetPlayerRoomForContext();

            // 2. Attic + glasses off: Zoey. Glasses off = Dateviators not equipped.
            bool glassesOff = Singleton<Dateviators>.Instance == null || !Singleton<Dateviators>.Instance.Equipped;
            if (glassesOff && IsAtticRoom(playerRoom))
            {
                if (TrySetContextDatableTarget(ContextAtticInternalName, out interactable, out targetZone, out targetLabel))
                    return true;
            }

            // 3. Thermostat: Ayrin, while still in the room where the thermostat was switched and
            // Ayrin is not yet met. Ayrin is only PRESENT when the temperature is set to cold, so
            // don't steer there if it's since been switched back to room temperature.
            if (NavigationContextTriggers.ThermostatInteracted &&
                IsThermostatCold() &&
                IsContextTriggerStillValid(NavigationContextTriggers.ThermostatRoom, playerRoom, ContextThermostatInternalName))
            {
                if (TrySetContextDatableTarget(ContextThermostatInternalName, out interactable, out targetZone, out targetLabel))
                    return true;
            }

            // 4. Couch: Dust Bunny, while still in the room where the couch revealed it and Dolly
            // is not yet met.
            if (NavigationContextTriggers.CouchRevealedDustBunny &&
                IsContextTriggerStillValid(NavigationContextTriggers.CouchRoom, playerRoom, ContextDustBunnyInternalName))
            {
                if (TrySetContextDatableTarget(ContextDustBunnyInternalName, out interactable, out targetZone, out targetLabel))
                    return true;
            }

            return false;
        }

        // An interaction-armed context trigger still applies when the player has NOT met the
        // datable and is still in the room where the trigger fired. A null trigger room (room
        // couldn't be resolved at arm time) is treated as room-agnostic so the default isn't
        // silently lost.
        private static bool IsContextTriggerStillValid(string triggerRoom, string playerRoom, string internalName)
        {
            if (IsDatableMet(internalName))
                return false;

            if (string.IsNullOrEmpty(triggerRoom))
                return true;

            return string.Equals(triggerRoom, playerRoom, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDatableMet(string internalName)
        {
            Save save = Singleton<Save>.Instance;
            return save != null && save.GetDateStatus(internalName) != RelationshipStatus.Unmet;
        }

        private static bool IsAtticRoom(string roomName)
        {
            return !string.IsNullOrEmpty(roomName) &&
                roomName.IndexOf("attic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Ayrin (airyn) is only present in the house while the thermostat is set to cold. Read the
        // live HvacControls state rather than trusting the arming latch, since the temperature can
        // be toggled back to room temperature after the trigger fired.
        private static bool IsThermostatCold()
        {
            HvacControls hvac = FindObjectOfType<HvacControls>();
            return hvac != null && hvac.temperature == HvacControls.Temperature.COLD;
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
