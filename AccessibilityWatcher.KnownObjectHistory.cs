using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace DateEverythingAccess
{
    // Captures the active save slot from the game's load/save so the known-object history is
    // scoped to the right slot. LoadGameAsync tells us which slot the player just opened;
    // SaveGameAsync covers a fresh game that saves before it ever loads (and every save path,
    // including autosave, funnels through SaveGameAsync via SaveSlot.TrySaveGameDataAsync). Both
    // are async Task methods — the postfix runs when the method is INVOKED (not when its Task
    // completes), which is fine: we only need the slot id, and our history files are independent
    // of the game's async save/load.
    [HarmonyPatch(typeof(Save), nameof(Save.LoadGameAsync))]
    internal static class SaveLoadGameSlotPatch
    {
        [HarmonyPostfix]
        private static void Postfix(int saveSlotID)
        {
            AccessibilityWatcher.SetActiveHistorySlot(saveSlotID);
        }
    }

    [HarmonyPatch(typeof(Save), nameof(Save.SaveGameAsync))]
    internal static class SaveGameSlotPatch
    {
        [HarmonyPostfix]
        private static void Postfix(int saveSlotID)
        {
            AccessibilityWatcher.SetActiveHistorySlot(saveSlotID);
        }
    }

    // Records that the player performed an ENVIRONMENTAL interaction with an object — a door
    // opening/closing, a box being picked up/put down, a light switch, and the like. Every
    // interaction routes through GameController.SelectObj; its result distinguishes the branches:
    // CHAT_STARTED = datable dialogue (already tracked by met/unmet status), FAILED = nothing
    // happened, ALT_INTERACTION = an alternate interaction that changed the environment fired.
    // We record only ALT_INTERACTION, so this captures the plain world interactions the game's own
    // hasNormalInteracted flag misses (it only reflects the alt-interaction TOGGLE state, and only
    // for objects that have one). Keying off the result also skips blocked/failed attempts and the
    // dialogue path. See AccessibilityWatcher.RememberInteractedObject / IsEncounteredKnownObject.
    [HarmonyPatch(typeof(GameController), nameof(GameController.SelectObj))]
    internal static class SelectObjInteractHistoryPatch
    {
        // SelectObj runs the alternate interaction synchronously in its body (it calls
        // alternateInteraction.Interact() directly), so marking a player-driven interaction as
        // active for the duration of SelectObj lets the environmental-feedback patches
        // (InteractionFeedbackPatches) tell a player flip from a programmatic one. Prefix sets it,
        // finalizer clears it even if the body throws.
        [HarmonyPrefix]
        private static void Prefix()
        {
            AccessibilityWatcher.IsPlayerDrivenInteractionActive = true;
        }

        [HarmonyPostfix]
        private static void Postfix(InteractableObj iObj, GameController.SelectObjResult __result)
        {
            if (__result == GameController.SelectObjResult.ALT_INTERACTION)
                AccessibilityWatcher.RememberInteractedObject(iObj);
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            AccessibilityWatcher.IsPlayerDrivenInteractionActive = false;
        }
    }

    // Mod-side persistence for "the player knows about this object" evidence the game does NOT
    // persist itself:
    //   - examined objects — the game keeps no examine history at all;
    //   - plain interacts — the game's hasNormalInteracted only tracks alternate-interaction
    //     toggle state, missing dialogue-only interacts on still-Unmet objects.
    // Both are stored PER SAVE SLOT under BepInEx/plugins, keyed by the interactable's stable
    // identity strings (InternalName/name/inkFileName/Id), so evidence never leaks across saves
    // and never touches the game's own save file.
    internal sealed partial class AccessibilityWatcher
    {
        // A HashSet of comparison-keys backed by a per-slot text file (one key per line). The
        // active slot is shared across all sets (SetActiveHistorySlot drives them together).
        private sealed class PersistedKeySet
        {
            private readonly string _dirName;
            internal readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            internal PersistedKeySet(string dirName)
            {
                _dirName = dirName;
            }

            private string Dir => Path.Combine(Paths.PluginPath, _dirName);

            private string PathForSlot(int slot) => Path.Combine(Dir, "slot_" + slot + ".txt");

            // Adds a pre-normalized key and, if new AND a slot is known, flushes to disk so the
            // evidence survives a reload. Returns true when the key was newly added.
            internal bool AddAndPersist(string key, int slot)
            {
                if (string.IsNullOrEmpty(key) || !Keys.Add(key))
                    return false;

                Persist(slot);
                return true;
            }

            internal void Load(int slot, bool clearFirst)
            {
                if (clearFirst)
                    Keys.Clear();

                if (slot < 0)
                    return;

                try
                {
                    string path = PathForSlot(slot);
                    if (!File.Exists(path))
                        return;

                    foreach (string line in File.ReadAllLines(path))
                    {
                        string key = line?.Trim();
                        if (!string.IsNullOrEmpty(key))
                            Keys.Add(key);
                    }
                }
                catch (Exception ex)
                {
                    Main.Log?.LogWarning("Failed to load " + _dirName + " for slot " + slot + ": " + ex.Message);
                }
            }

            internal void Persist(int slot)
            {
                if (slot < 0)
                    return;

                try
                {
                    Directory.CreateDirectory(Dir);

                    var sb = new StringBuilder();
                    foreach (string key in Keys)
                        sb.AppendLine(key);

                    File.WriteAllText(PathForSlot(slot), sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Main.Log?.LogWarning("Failed to save " + _dirName + " for slot " + slot + ": " + ex.Message);
                }
            }
        }

        // True while GameController.SelectObj is running — i.e. the current interaction is
        // player-driven. Lets InteractionFeedbackPatches announce a player's light flip while
        // staying silent on LightingScenarios' programmatic toggles, which call the same
        // Lights_Inter.Interact(bool) outside any SelectObj.
        internal static bool IsPlayerDrivenInteractionActive { get; set; }

        // -1 = no slot known yet (main menu / pre-load). While unknown, evidence still accumulates
        // in memory but is not written; it gets flushed once a slot is first known.
        private static int _activeHistorySlot = -1;

        private static readonly PersistedKeySet _examinedObjects = new PersistedKeySet("examine_history");
        private static readonly PersistedKeySet _interactedObjects = new PersistedKeySet("interact_history");

        // All per-slot key sets, so slot changes drive them uniformly.
        private static PersistedKeySet[] HistorySets => new[] { _examinedObjects, _interactedObjects };

        // Called from the load/save hooks with the slot the game is operating on. A genuine slot
        // SWITCH replaces the in-memory sets with that slot's persisted keys so evidence never
        // leaks across saves; the FIRST slot of a session instead MERGES (keeps whatever already
        // accumulated in memory, e.g. a new game's pre-first-save examines) and flushes.
        internal static void SetActiveHistorySlot(int slot)
        {
            if (slot < 0)
                return;

            if (_activeHistorySlot == slot)
            {
                // Same slot re-confirmed (e.g. a save after a load). Flush current evidence so any
                // gathered before the slot was first known is written out.
                foreach (PersistedKeySet set in HistorySets)
                    set.Persist(slot);
                return;
            }

            bool firstSlotThisSession = _activeHistorySlot < 0;
            _activeHistorySlot = slot;

            foreach (PersistedKeySet set in HistorySets)
                set.Load(slot, clearFirst: !firstSlotThisSession);

            if (firstSlotThisSession)
            {
                foreach (PersistedKeySet set in HistorySets)
                    set.Persist(slot);
            }
        }

        // Records the owning interactable's identity keys when the player performs an
        // environmental interaction with it (door, box, light, ...). Mirrors the key selection
        // RememberExaminedObject uses so both histories match the same objects. iObj is the
        // InteractableObj GameController.SelectObj was handed.
        internal static void RememberInteractedObject(InteractableObj iObj)
        {
            if (iObj == null)
                return;

            AddInteractedObjectKey(iObj.Id);
            AddInteractedObjectKey(iObj.name);
            AddInteractedObjectKey(iObj.InternalName());
            AddInteractedObjectKey(iObj.inkFileName);
        }

        private static void AddInteractedObjectKey(string value)
        {
            _interactedObjects.AddAndPersist(BuildComparisonKey(value), _activeHistorySlot);
        }

        private static bool HasRememberedInteractedObjectKey(string value)
        {
            string key = BuildComparisonKey(value);
            return !string.IsNullOrEmpty(key) && _interactedObjects.Keys.Contains(key);
        }

        // True when the player has performed an environmental interaction with THIS interactable
        // (door/box/light/...), per our persisted interact history.
        private static bool IsInteractedInteractable(InteractableObj interactable)
        {
            if (interactable == null)
                return false;

            return HasRememberedInteractedObjectKey(interactable.Id) ||
                HasRememberedInteractedObjectKey(interactable.name) ||
                HasRememberedInteractedObjectKey(interactable.InternalName()) ||
                HasRememberedInteractedObjectKey(interactable.inkFileName);
        }
    }
}
