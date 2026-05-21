using BepInEx.Configuration;
using System;
using System.Runtime.InteropServices;
using T17.Services;
using Team17.Scripts.Services.Input;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Stores user-configurable accessibility speech settings and exposes an in-game spoken settings menu.
    /// </summary>
    public static class ModConfig
    {
        private const int VkUp = 0x26;
        private const int VkDown = 0x28;
        private const int VkLeft = 0x25;
        private const int VkRight = 0x27;
        private const int VkReturn = 0x0D;
        private const int VkSpace = 0x20;
        private const int VkEscape = 0x1B;
        private const int FocusedItemsIndex = 0;
        private const int DialogueTextIndex = 1;
        private const int DialogueChoicesIndex = 2;
        private const int ScreenTextIndex = 3;
        private const int PhoneAppTextIndex = 4;
        private const int RoomChangesIndex = 5;
        private const int NearbyObjectsIndex = 6;
        private const int StatusChangesIndex = 7;

        private static readonly string[] SettingNameKeys =
        {
            "config_focused_items",
            "config_dialogue_text",
            "config_dialogue_choices",
            "config_screen_text",
            "config_phone_app_text",
            "config_room_changes",
            "config_nearby_objects",
            "config_status_changes"
        };

        private static ConfigFile _config;
        private static ConfigEntry<bool> _readFocusedItems;
        private static ConfigEntry<bool> _readDialogueText;
        private static ConfigEntry<bool> _readDialogueChoices;
        private static ConfigEntry<bool> _readScreenText;
        private static ConfigEntry<bool> _readPhoneAppText;
        private static ConfigEntry<bool> _readRoomChanges;
        private static ConfigEntry<bool> _readNearbyObjects;
        private static ConfigEntry<bool> _readStatusChanges;
        private static InputModeHandle _inputModeHandle;
        private static volatile bool _menuOpen;
        private static int _currentSettingIndex;
        private static bool _upWasDown;
        private static bool _downWasDown;
        private static bool _leftWasDown;
        private static bool _rightWasDown;
        private static bool _returnWasDown;
        private static bool _spaceWasDown;
        private static bool _escapeWasDown;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// Gets a value indicating whether focused UI controls should be spoken.
        /// </summary>
        public static bool ReadFocusedItems => _readFocusedItems.Value;

        /// <summary>
        /// Gets a value indicating whether dialogue lines should be spoken automatically.
        /// </summary>
        public static bool ReadDialogueText => _readDialogueText.Value;

        /// <summary>
        /// Gets a value indicating whether dialogue choice focus should be spoken.
        /// </summary>
        public static bool ReadDialogueChoices => _readDialogueChoices.Value;

        /// <summary>
        /// Gets a value indicating whether menu, popup, tutorial, and other non-phone screen text should be spoken.
        /// </summary>
        public static bool ReadScreenText => _readScreenText.Value;

        /// <summary>
        /// Gets a value indicating whether phone app content should be spoken automatically.
        /// </summary>
        public static bool ReadPhoneAppText => _readPhoneAppText.Value;

        /// <summary>
        /// Gets a value indicating whether room changes should be spoken.
        /// </summary>
        public static bool ReadRoomChanges => _readRoomChanges.Value;

        /// <summary>
        /// Gets a value indicating whether nearby interactables should be spoken.
        /// </summary>
        public static bool ReadNearbyObjects => _readNearbyObjects.Value;

        /// <summary>
        /// Gets a value indicating whether status changes such as Dateviators and progression should be spoken.
        /// </summary>
        public static bool ReadStatusChanges => _readStatusChanges.Value;

        /// <summary>
        /// Gets a value indicating whether the spoken settings menu is currently open.
        /// </summary>
        public static bool IsMenuOpen => _menuOpen;

        /// <summary>
        /// Initializes the accessibility configuration entries.
        /// </summary>
        public static void Initialize(ConfigFile config)
        {
            if (_config != null)
                return;

            _config = config;
            _readFocusedItems = config.Bind("Accessibility", "ReadFocusedItems", true, "Speak focused UI controls.");
            _readDialogueText = config.Bind("Accessibility", "ReadDialogueText", true, "Speak active dialogue lines.");
            _readDialogueChoices = config.Bind("Accessibility", "ReadDialogueChoices", true, "Speak dialogue choice focus changes.");
            _readScreenText = config.Bind("Accessibility", "ReadScreenText", true, "Speak menu, popup, tutorial, and other non-phone screen text.");
            _readPhoneAppText = config.Bind("Accessibility", "ReadPhoneAppText", true, "Speak phone app content such as Roomers, Date A Dex, and chats.");
            _readRoomChanges = config.Bind("Accessibility", "ReadRoomChanges", true, "Speak room changes while exploring.");
            _readNearbyObjects = config.Bind("Accessibility", "ReadNearbyObjects", true, "Speak nearby interactables.");
            _readStatusChanges = config.Bind("Accessibility", "ReadStatusChanges", true, "Speak Dateviators, time, and progression changes.");
        }

        /// <summary>
        /// Opens or closes the spoken settings menu.
        /// </summary>
        public static void ToggleMenu()
        {
            if (_config == null)
                return;

            Loc.RefreshLanguage();
            _menuOpen = !_menuOpen;

            if (_menuOpen)
            {
                _currentSettingIndex = 0;
                AcquireInputBlock();
                SyncMenuKeyStates();
                ScreenReader.Say(Loc.Get("settings_menu_opened"));
                AnnounceCurrentSetting();
                return;
            }

            ReleaseInputBlock();
            SyncMenuKeyStates();
            _config.Save();
            ScreenReader.Say(Loc.Get("settings_menu_closed"));
        }

        /// <summary>
        /// Processes keyboard input for the spoken settings menu.
        /// </summary>
        public static void Update()
        {
            if (!_menuOpen)
                return;

            if (WasPressed(KeyCode.UpArrow, VkUp, ref _upWasDown))
            {
                _currentSettingIndex = (_currentSettingIndex + SettingNameKeys.Length - 1) % SettingNameKeys.Length;
                AnnounceCurrentSetting();
                return;
            }

            if (WasPressed(KeyCode.DownArrow, VkDown, ref _downWasDown))
            {
                _currentSettingIndex = (_currentSettingIndex + 1) % SettingNameKeys.Length;
                AnnounceCurrentSetting();
                return;
            }

            if (WasPressed(KeyCode.LeftArrow, VkLeft, ref _leftWasDown)
                || WasPressed(KeyCode.RightArrow, VkRight, ref _rightWasDown)
                || WasPressed(KeyCode.Return, VkReturn, ref _returnWasDown)
                || WasPressed(KeyCode.KeypadEnter, VkReturn, ref _returnWasDown)
                || WasPressed(KeyCode.Space, VkSpace, ref _spaceWasDown))
            {
                ToggleCurrentSetting();
                return;
            }

            if (WasPressed(KeyCode.Escape, VkEscape, ref _escapeWasDown))
            {
                ToggleMenu();
            }
        }

        private static bool WasPressed(KeyCode keyCode, int virtualKey, ref bool wasDown)
        {
            bool isDown = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            bool pressed = Input.GetKeyDown(keyCode) || (isDown && !wasDown);
            wasDown = isDown;
            return pressed;
        }

        private static void SyncMenuKeyStates()
        {
            _upWasDown = IsVirtualKeyDown(VkUp);
            _downWasDown = IsVirtualKeyDown(VkDown);
            _leftWasDown = IsVirtualKeyDown(VkLeft);
            _rightWasDown = IsVirtualKeyDown(VkRight);
            _returnWasDown = IsVirtualKeyDown(VkReturn);
            _spaceWasDown = IsVirtualKeyDown(VkSpace);
            _escapeWasDown = IsVirtualKeyDown(VkEscape);
        }

        private static bool IsVirtualKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static void AcquireInputBlock()
        {
            ReleaseInputBlock();

            if (Services.InputService == null)
                return;

            _inputModeHandle = Services.InputService.PushMode(IMirandaInputService.EInputMode.None, "DateEverythingAccess.SettingsMenu");
        }

        private static void ReleaseInputBlock()
        {
            if (_inputModeHandle == null)
                return;

            _inputModeHandle.SafeDispose();
            _inputModeHandle = null;
        }

        private static void AnnounceCurrentSetting()
        {
            string name = Loc.Get(SettingNameKeys[_currentSettingIndex]);
            string value = GetCurrentSettingValue();
            ScreenReader.Say(Loc.Get("settings_menu_item", _currentSettingIndex + 1, SettingNameKeys.Length, name, value));
        }

        private static string GetCurrentSettingValue()
        {
            return IsCurrentSettingEnabled() ? Loc.Get("settings_value_on") : Loc.Get("settings_value_off");
        }

        private static bool IsCurrentSettingEnabled()
        {
            switch (_currentSettingIndex)
            {
                case FocusedItemsIndex:
                    return _readFocusedItems.Value;
                case DialogueTextIndex:
                    return _readDialogueText.Value;
                case DialogueChoicesIndex:
                    return _readDialogueChoices.Value;
                case ScreenTextIndex:
                    return _readScreenText.Value;
                case PhoneAppTextIndex:
                    return _readPhoneAppText.Value;
                case RoomChangesIndex:
                    return _readRoomChanges.Value;
                case NearbyObjectsIndex:
                    return _readNearbyObjects.Value;
                case StatusChangesIndex:
                    return _readStatusChanges.Value;
                default:
                    return true;
            }
        }

        private static void ToggleCurrentSetting()
        {
            switch (_currentSettingIndex)
            {
                case FocusedItemsIndex:
                    _readFocusedItems.Value = !_readFocusedItems.Value;
                    break;
                case DialogueTextIndex:
                    _readDialogueText.Value = !_readDialogueText.Value;
                    break;
                case DialogueChoicesIndex:
                    _readDialogueChoices.Value = !_readDialogueChoices.Value;
                    break;
                case ScreenTextIndex:
                    _readScreenText.Value = !_readScreenText.Value;
                    break;
                case PhoneAppTextIndex:
                    _readPhoneAppText.Value = !_readPhoneAppText.Value;
                    break;
                case RoomChangesIndex:
                    _readRoomChanges.Value = !_readRoomChanges.Value;
                    break;
                case NearbyObjectsIndex:
                    _readNearbyObjects.Value = !_readNearbyObjects.Value;
                    break;
                case StatusChangesIndex:
                    _readStatusChanges.Value = !_readStatusChanges.Value;
                    break;
                default:
                    throw new InvalidOperationException("Unknown accessibility setting index: " + _currentSettingIndex);
            }

            string name = Loc.Get(SettingNameKeys[_currentSettingIndex]);
            string value = GetCurrentSettingValue();
            ScreenReader.Say(Loc.Get("settings_menu_changed", name, value));
        }
    }
}
