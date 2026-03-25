using System;
using System.Collections.Generic;
using System.Text;
using Rewired;

namespace DateEverythingAccess
{
    internal static class InputMappingReporter
    {
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
