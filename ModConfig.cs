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
        /// <summary>
        /// User-selectable register for the object-tracker guidance tone. No single fundamental is
        /// audible to everyone: high-frequency (age-related) loss favours a lower tone, while
        /// low/mid loss favours a higher one. The mid default sits on the ISO 226 equal-loudness
        /// sensitivity plateau (~1 kHz) that suits the broadest range of hearing profiles.
        /// </summary>
        public enum TonePitchRegister
        {
            Low,
            Mid,
            High
        }

        // Fundamental frequencies (Hz) for each register. Low favours listeners with high-frequency
        // loss; Mid (~1 kHz) is the broad-spectrum default; High sits near peak sensitivity for
        // listeners with low/mid loss. The carrier adds harmonics on top of whichever is chosen.
        private const float LowRegisterHz = 500f;
        private const float MidRegisterHz = 1000f;
        private const float HighRegisterHz = 2000f;

        private const int VkUp = 0x26;
        private const int VkDown = 0x28;
        private const int VkLeft = 0x25;
        private const int VkRight = 0x27;
        private const int VkReturn = 0x0D;
        private const int VkSpace = 0x20;
        private const int VkBackspace = 0x08;
        private const int FocusedItemsIndex = 0;
        private const int DialogueTextIndex = 1;
        private const int DialogueChoicesIndex = 2;
        private const int ScreenTextIndex = 3;
        private const int PhoneAppTextIndex = 4;
        private const int RoomChangesIndex = 5;
        private const int NearbyObjectsIndex = 6;
        private const int StatusChangesIndex = 7;
        private const int TrackerTonePitchIndex = 8;

        private static readonly string[] SettingNameKeys =
        {
            "config_focused_items",
            "config_dialogue_text",
            "config_dialogue_choices",
            "config_screen_text",
            "config_phone_app_text",
            "config_room_changes",
            "config_nearby_objects",
            "config_status_changes",
            "config_tracker_tone_pitch"
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
        private static ConfigEntry<TonePitchRegister> _trackerTonePitch;
        private static ConfigEntry<bool> _captureNavRoutes;
        private static ConfigEntry<string> _coverageSweepRunId;
        private static InputModeHandle _inputModeHandle;
        private static volatile bool _menuOpen;
        private static int _currentSettingIndex;
        private static bool _upWasDown;
        private static bool _downWasDown;
        private static bool _leftWasDown;
        private static bool _rightWasDown;
        private static bool _returnWasDown;
        private static bool _spaceWasDown;
        private static bool _backspaceWasDown;

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
        /// Gets the user-selected register for the object-tracker guidance tone.
        /// </summary>
        public static TonePitchRegister TrackerTonePitch =>
            _trackerTonePitch != null ? _trackerTonePitch.Value : TonePitchRegister.Mid;

        /// <summary>
        /// Gets the fundamental frequency (Hz) the tracker carrier should be synthesized at, derived
        /// from <see cref="TrackerTonePitch"/>. Read by <c>ObjectTracker.ResolveFundamentalFrequency</c>.
        /// </summary>
        public static float TrackerToneFundamentalHz
        {
            get
            {
                switch (TrackerTonePitch)
                {
                    case TonePitchRegister.Low:
                        return LowRegisterHz;
                    case TonePitchRegister.High:
                        return HighRegisterHz;
                    default:
                        return MidRegisterHz;
                }
            }
        }

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
            _trackerTonePitch = config.Bind("Accessibility", "TrackerTonePitch", TonePitchRegister.Mid,
                "Register for the object-tracker guidance tone. Low (~500 Hz) suits high-frequency " +
                "(age-related) hearing loss; Mid (~1 kHz) is the broad-spectrum default on the ISO 226 " +
                "sensitivity plateau; High (~2 kHz) suits low/mid hearing loss. The tone is a harmonic " +
                "stack over this fundamental so a notch at any single frequency is covered by the others.");
            _captureNavRoutes = config.Bind("Diagnostics", "CaptureNavRoutes", false,
                "When true, every successful SimpleNavPlanner.Plan output is written to " +
                "BepInEx/plugins/c_sharp_routes/route_<unix>_<idx>.json. Used by " +
                "scripts/check_planner_parity.py to compare runtime planning against the " +
                "Python planner. Off by default — turn on briefly to gather captures, then off.");
            _coverageSweepRunId = config.Bind("Diagnostics", "CoverageSweepRunId", "default",
                "Which artifacts/navigation/sweep/<run-id>/ manifest the coverage-sweep hotkey " +
                "(Ctrl+Alt+Shift+F8) drives. 'default' = the walk-mode cell sweep; 'objects' = the " +
                "object-reachability sweep (walk-chain: start where you are, walk to the nearest " +
                "object, then the next; relocate-teleport only after several failures in a row).");
        }

        /// <summary>
        /// When true, SimpleNavPlanner writes each plan output to disk for the offline parity check.
        /// </summary>
        public static bool CaptureNavRoutes => _captureNavRoutes != null && _captureNavRoutes.Value;

        /// <summary>
        /// The sweep run-id (subdirectory under artifacts/navigation/sweep/) the coverage-sweep
        /// hotkey drives. Defaults to "default" when unset. Set to "objects" for the
        /// object-reachability sweep.
        /// </summary>
        public static string CoverageSweepRunId =>
            _coverageSweepRunId != null && !string.IsNullOrWhiteSpace(_coverageSweepRunId.Value)
                ? _coverageSweepRunId.Value
                : "default";

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

            // Left/Right change the value. For boolean settings either direction toggles; for the
            // cycling tone-pitch setting, Left steps back through the registers and Right steps forward.
            if (WasPressed(KeyCode.LeftArrow, VkLeft, ref _leftWasDown))
            {
                ChangeCurrentSetting(forward: false);
                return;
            }

            if (WasPressed(KeyCode.RightArrow, VkRight, ref _rightWasDown)
                || WasPressed(KeyCode.Return, VkReturn, ref _returnWasDown)
                || WasPressed(KeyCode.KeypadEnter, VkReturn, ref _returnWasDown)
                || WasPressed(KeyCode.Space, VkSpace, ref _spaceWasDown))
            {
                ChangeCurrentSetting(forward: true);
                return;
            }

            // Backspace closes the menu (was Escape — Backspace conflicts less with the game's own pause/cancel).
            if (WasPressed(KeyCode.Backspace, VkBackspace, ref _backspaceWasDown))
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
            _backspaceWasDown = IsVirtualKeyDown(VkBackspace);
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
            if (_currentSettingIndex == TrackerTonePitchIndex)
                return GetTonePitchValueLabel();

            return IsCurrentSettingEnabled() ? Loc.Get("settings_value_on") : Loc.Get("settings_value_off");
        }

        private static string GetTonePitchValueLabel()
        {
            switch (TrackerTonePitch)
            {
                case TonePitchRegister.Low:
                    return Loc.Get("config_tracker_tone_pitch_low");
                case TonePitchRegister.High:
                    return Loc.Get("config_tracker_tone_pitch_high");
                default:
                    return Loc.Get("config_tracker_tone_pitch_mid");
            }
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

        // Applies a value change to the focused setting. Boolean settings flip regardless of
        // direction; the cycling tone-pitch setting steps forward or backward through its registers.
        private static void ChangeCurrentSetting(bool forward)
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
                case TrackerTonePitchIndex:
                    CycleTonePitch(forward);
                    break;
                default:
                    throw new InvalidOperationException("Unknown accessibility setting index: " + _currentSettingIndex);
            }

            string name = Loc.Get(SettingNameKeys[_currentSettingIndex]);
            string value = GetCurrentSettingValue();
            ScreenReader.Say(Loc.Get("settings_menu_changed", name, value));
        }

        private static void CycleTonePitch(bool forward)
        {
            const int registerCount = 3; // Low, Mid, High
            int current = (int)_trackerTonePitch.Value;
            int next = forward
                ? (current + 1) % registerCount
                : (current + registerCount - 1) % registerCount;
            _trackerTonePitch.Value = (TonePitchRegister)next;
            // Rebuild the carrier so the new register is audible immediately (and while tracking, restart it).
            ObjectTracker.RefreshToneClip();
        }
    }
}
