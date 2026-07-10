using System;
using System.Collections.Generic;
using System.Text;
using Rewired;

namespace DateEverythingAccess
{
    internal static class InputMappingReporter
    {
        // Rewired action id for Move_Vertical (RewiredConsts.Action.Move_Vertical = 4). The game ships
        // an UNDOCUMENTED secondary keyboard binding of the "O" key to this action (walk), NOT shown in
        // the Controls UI. Because the mod's O accessibility shortcut is POLLED via GetAsyncKeyState
        // (which doesn't consume the key), pressing O both opens the object tracker AND walks the
        // player. We strip just that one binding at startup so O is free for the tracker; W/A/S/D and
        // the arrows are untouched. See the hidden-keybinds reference memo.
        private const int MoveVerticalActionId = 4;

        // Display label of the key we strip off Move_Vertical. Rewired labels the letter keys by their
        // letter, so matching elementIdentifierName is layout-stable rather than depending on a raw
        // Rewired KeyboardKeyCode enum value.
        private const string MoveVerticalKeyToStrip = "O";

        // Removes the game's undocumented "O" -> Move_Vertical keyboard binding so the mod's O
        // accessibility shortcut no longer also walks the player. One-shot: returns true once it has
        // actually deleted the binding (or confirmed it's already gone); returns false while Rewired
        // isn't ready yet so the caller can retry next frame. Scans ALL keyboard controller maps
        // regardless of their enabled state — the input-mode state machine toggles map enabled flags,
        // but the binding itself lives in the map and DeleteElementMap removes it permanently for the
        // session. W/A/S/D and the arrows are left alone; only the O element map on action 4 is touched.
        public static bool TryStripObjectTrackerMovementBinding()
        {
            try
            {
                if (!ReInput.isReady)
                    return false;

                Player player = ReInput.players.GetPlayer(0);
                if (player == null || !player.controllers.hasKeyboard)
                    return false;

                Keyboard keyboard = player.controllers.Keyboard;
                IList<ControllerMap> maps = player.controllers.maps.GetMaps(keyboard.type, keyboard.id);
                if (maps == null || maps.Count == 0)
                    return false; // maps not populated yet — retry next frame rather than latch as done

                bool sawMoveVerticalMap = false;
                int removed = 0;
                for (int i = 0; i < maps.Count; i++)
                {
                    ControllerMap map = maps[i];
                    if (map == null)
                        continue;

                    // Collect the element-map ids to delete first, then delete — deleting while
                    // iterating AllMaps would mutate the collection under the loop.
                    List<int> toDelete = null;
                    for (int j = 0; j < map.AllMaps.Count; j++)
                    {
                        ActionElementMap m = map.AllMaps[j];
                        if (m == null || m.actionId != MoveVerticalActionId)
                            continue;
                        sawMoveVerticalMap = true;
                        if (!string.Equals(m.elementIdentifierName, MoveVerticalKeyToStrip, StringComparison.OrdinalIgnoreCase))
                            continue;

                        (toDelete ?? (toDelete = new List<int>())).Add(m.id);
                    }

                    if (toDelete != null)
                    {
                        for (int k = 0; k < toDelete.Count; k++)
                            if (map.DeleteElementMap(toDelete[k]))
                                removed++;
                    }
                }

                // Only consider the job done once the Move_Vertical bindings actually exist to inspect.
                // Before the game loads its controller maps, no action-4 map is present — keep retrying
                // so we don't latch "done" against an empty map and miss the real binding.
                if (!sawMoveVerticalMap)
                    return false;

                if (Main.Log != null)
                    Main.Log.LogInfo("[INPUTMAP] Stripped " + removed + " '" + MoveVerticalKeyToStrip +
                        "' -> Move_Vertical keyboard binding(s) so the object-tracker key no longer moves the player.");
                return true;
            }
            catch (Exception ex)
            {
                if (Main.Log != null)
                    Main.Log.LogWarning("[INPUTMAP] TryStripObjectTrackerMovementBinding failed: " + ex.Message);
                return false; // let the caller retry; a transient not-ready state shouldn't be permanent
            }
        }

        public static bool TryDumpCurrentMappings(out int dumpedControllerCount)
        {
            dumpedControllerCount = 0;

            try
            {
                if (!ReInput.isReady)
                {
                    Main.Log.LogWarning("[INPUTMAP] ReInput is not ready yet.");
                    return false;
                }

                StringBuilder builder = new StringBuilder();
                builder.AppendLine("[INPUTMAP] Rewired mapping dump start");
                HashSet<string> dumpedControllers = new HashSet<string>(StringComparer.Ordinal);
                DumpPlayerMappings(ReInput.players.GetPlayer(0), "Player0", builder, dumpedControllers, ref dumpedControllerCount);
                DumpPlayerMappings(ReInput.players.GetSystemPlayer(), "SystemPlayer", builder, dumpedControllers, ref dumpedControllerCount);

                if (dumpedControllerCount == 0)
                {
                    Main.Log.LogWarning("[INPUTMAP] No active keyboard, mouse, or joystick controllers were available to dump from Player0 or SystemPlayer.");
                    return false;
                }

                builder.AppendLine("[INPUTMAP] Rewired mapping dump end");
                Main.Log.LogInfo(builder.ToString());
                return true;
            }
            catch (Exception ex)
            {
                Main.Log.LogError("[INPUTMAP] Failed to dump current mappings: " + ex);
                return false;
            }
        }

        private static void DumpPlayerMappings(Player player, string playerLabel, StringBuilder builder, HashSet<string> dumpedControllers, ref int dumpedControllerCount)
        {
            if (player == null)
            {
                builder.AppendLine("[INPUTMAP] Player=" + playerLabel + " is unavailable.");
                return;
            }

            bool dumpedAnyForPlayer = false;

            if (player.controllers.hasKeyboard && TryMarkController(dumpedControllers, player.controllers.Keyboard.type, player.controllers.Keyboard.id))
            {
                dumpedAnyForPlayer = true;
                dumpedControllerCount++;
                DumpControllerMaps(
                    builder,
                    playerLabel,
                    "Keyboard",
                    player.controllers.Keyboard.type,
                    player.controllers.Keyboard.id,
                    player.controllers.Keyboard.name,
                    player.controllers.maps.GetMaps(player.controllers.Keyboard.type, player.controllers.Keyboard.id));
            }

            if (player.controllers.hasMouse && TryMarkController(dumpedControllers, player.controllers.Mouse.type, player.controllers.Mouse.id))
            {
                dumpedAnyForPlayer = true;
                dumpedControllerCount++;
                DumpControllerMaps(
                    builder,
                    playerLabel,
                    "Mouse",
                    player.controllers.Mouse.type,
                    player.controllers.Mouse.id,
                    player.controllers.Mouse.name,
                    player.controllers.maps.GetMaps(player.controllers.Mouse.type, player.controllers.Mouse.id));
            }

            for (int i = 0; i < player.controllers.joystickCount; i++)
            {
                Joystick joystick = player.controllers.Joysticks[i];
                if (joystick == null || !TryMarkController(dumpedControllers, joystick.type, joystick.id))
                {
                    continue;
                }

                dumpedAnyForPlayer = true;
                dumpedControllerCount++;
                string label = string.IsNullOrEmpty(joystick.hardwareName) ? joystick.name : joystick.hardwareName;
                DumpControllerMaps(
                    builder,
                    playerLabel,
                    "Joystick",
                    joystick.type,
                    joystick.id,
                    label,
                    player.controllers.maps.GetMaps(joystick.type, joystick.id));
            }

            if (!dumpedAnyForPlayer)
            {
                builder.AppendLine("[INPUTMAP] Player=" + playerLabel + " has no uniquely assigned active controllers.");
            }
        }

        private static void DumpControllerMaps(StringBuilder builder, string playerLabel, string label, ControllerType controllerType, int controllerId, string controllerName, IList<ControllerMap> maps)
        {
            builder.AppendLine(string.Format(
                "[INPUTMAP] Player={0} Controller={1} Type={2} Id={3} Name={4}",
                playerLabel,
                label,
                controllerType,
                controllerId,
                Safe(controllerName)));

            if (maps == null || maps.Count == 0)
            {
                builder.AppendLine("[INPUTMAP]   No maps returned.");
                return;
            }

            for (int i = 0; i < maps.Count; i++)
            {
                ControllerMap map = maps[i];
                if (map == null)
                {
                    continue;
                }

                builder.AppendLine(string.Format(
                    "[INPUTMAP]   Map Category={0} LayoutId={1} Enabled={2}",
                    GetCategoryName(map.categoryId),
                    map.layoutId,
                    map.enabled));

                for (int j = 0; j < map.AllMaps.Count; j++)
                {
                    ActionElementMap actionMap = map.AllMaps[j];
                    if (actionMap == null)
                    {
                        continue;
                    }

                    builder.AppendLine(string.Format(
                        "[INPUTMAP]     {0} ({1}) -> {2} [ElementType={3}, AxisRange={4}]",
                        Safe(GetActionName(actionMap.actionId, actionMap.actionDescriptiveName)),
                        actionMap.actionId,
                        Safe(actionMap.elementIdentifierName),
                        actionMap.elementType,
                        actionMap.axisRange));
                }
            }
        }

        private static bool TryMarkController(HashSet<string> dumpedControllers, ControllerType controllerType, int controllerId)
        {
            return dumpedControllers.Add(controllerType + ":" + controllerId);
        }

        private static string GetActionName(int actionId, string fallbackName)
        {
            InputAction action = ReInput.mapping.GetAction(actionId);
            if (action != null && !string.IsNullOrEmpty(action.name))
            {
                return action.name;
            }

            return string.IsNullOrEmpty(fallbackName) ? "Action " + actionId : fallbackName;
        }

        private static string GetCategoryName(int categoryId)
        {
            switch (categoryId)
            {
                case 0:
                    return "Default";
                case 1:
                    return "Dialog";
                case 2:
                    return "CharacterController";
                case 3:
                    return "Engagement";
                case 4:
                    return "Debug";
                case 5:
                    return "UI";
                case 6:
                    return "Toggle_Dateviators";
                default:
                    return "Category_" + categoryId;
            }
        }

        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "<unnamed>" : value;
        }
    }
}
