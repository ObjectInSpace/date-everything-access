using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace DateEverythingAccess
{
    [BepInPlugin("com.amock.dateeverythingaccess", "Date Everything Access", "0.1.0")]
    public class Main : BaseUnityPlugin
    {
        private const int VkF1 = 0x70;
        private const int VkF6 = 0x75;
        private const int VkF9 = 0x78;
        private const int WmHotkey = 0x0312;
        private const int WmQuit = 0x0012;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModAlt = 0x0001;
        private const uint ModNoRepeat = 0x4000;
        private const int HelpHotkeyId = 1;
        private const int DebugHotkeyId = 2;
        private const int SettingsHotkeyId = 3;
        private const int RepeatSpeechHotkeyId = 4;
        private const int NavigateToObjectiveHotkeyId = 5;
        private const int SelectNavigationTargetHotkeyId = 6;
        private const int AutoWalkHotkeyId = 7;
        private const int ExportNavMeshHotkeyId = 8;

        private Thread _hotkeyThread;
        private volatile bool _hotkeyThreadRunning;
        private uint _hotkeyThreadId;
        private bool _applicationQuitting;
        private bool _cleanupCompleted;
        private Harmony _harmony;

        public static bool DebugMode { get; private set; }
        public static ManualLogSource Log { get; private set; }
        public static Main Instance { get; private set; }
        public static bool IsShuttingDown { get; private set; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern sbyte GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            ScreenReader.Initialize();
            Loc.Initialize();
            ModConfig.Initialize(Config);
            _harmony = new Harmony("com.amock.dateeverythingaccess");
            _harmony.PatchAll();

            Application.quitting += OnApplicationQuitting;
            StartHotkeyThread();
            AccessibilityWatcher.EnsureCreated();

            Logger.LogInfo("Date Everything Access initialized");
            ScreenReader.Say(Loc.Get("mod_loaded"));
            Logger.LogInfo("Startup announcement queued");
        }

        private void OnDestroy()
        {
            Logger.LogInfo("Main.OnDestroy invoked");

            if (!_applicationQuitting)
            {
                Logger.LogWarning("Ignoring OnDestroy because the application is still running");
                return;
            }

            Cleanup();
        }

        private void OnApplicationQuitting()
        {
            _applicationQuitting = true;
            IsShuttingDown = true;
            Logger.LogInfo("Application quitting");
            Cleanup();
        }

        private void Cleanup()
        {
            if (_cleanupCompleted)
                return;

            _cleanupCompleted = true;
            Application.quitting -= OnApplicationQuitting;
            StopHotkeyThread();
            _harmony?.UnpatchSelf();
            _harmony = null;
            ScreenReader.Stop();
            ScreenReader.Shutdown();
        }

        private void StartHotkeyThread()
        {
            if (_hotkeyThread != null)
                return;

            _hotkeyThreadRunning = true;
            _hotkeyThread = new Thread(HotkeyThreadLoop)
            {
                IsBackground = true,
                Name = "DateEverythingAccessHotkeys"
            };
            _hotkeyThread.Start();
            Logger.LogInfo("Background hotkey thread started");
        }

        private void StopHotkeyThread()
        {
            if (_hotkeyThread == null)
                return;

            _hotkeyThreadRunning = false;
            if (_hotkeyThreadId != 0)
            {
                PostThreadMessage(_hotkeyThreadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
            }

            if (!_hotkeyThread.Join(500))
            {
                Logger.LogWarning("Background hotkey thread did not stop within 500ms");
            }

            _hotkeyThread = null;
            _hotkeyThreadId = 0;
        }

        private void HotkeyThreadLoop()
        {
            _hotkeyThreadId = GetCurrentThreadId();

            try
            {
                RegisterHotkeyOrThrow(HelpHotkeyId, VkF1, "F1");
                RegisterHotkeyOrThrow(DebugHotkeyId, VkF9, "F9");
                RegisterHotkeyOrThrow(SettingsHotkeyId, ModControl | ModNoRepeat, VkF9, "Ctrl+F9");
                RegisterHotkeyOrThrow(RepeatSpeechHotkeyId, ModControl | ModNoRepeat, VkF1, "Ctrl+F1");
                RegisterHotkeyOrThrow(NavigateToObjectiveHotkeyId, ModControl | ModNoRepeat, VkF6, "Ctrl+F6");
                RegisterHotkeyOrThrow(SelectNavigationTargetHotkeyId, ModControl | ModShift | ModNoRepeat, VkF6, "Ctrl+Shift+F6");
                RegisterHotkeyOrThrow(AutoWalkHotkeyId, ModControl | ModAlt | ModNoRepeat, VkF6, "Ctrl+Alt+F6");
                RegisterHotkeyOrThrow(ExportNavMeshHotkeyId, ModControl | ModShift | ModNoRepeat, VkF9, "Ctrl+Shift+F9");
                Logger.LogInfo("Background hotkey message loop active");

                NativeMessage message;
                while (_hotkeyThreadRunning)
                {
                    sbyte result = GetMessage(out message, IntPtr.Zero, 0, 0);
                    if (result <= 0)
                        break;

                    if (message.message == WmHotkey)
                    {
                        ProcessRegisteredHotkey((int)message.wParam);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Background hotkey thread failed: " + ex);
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, HelpHotkeyId);
                UnregisterHotKey(IntPtr.Zero, DebugHotkeyId);
                UnregisterHotKey(IntPtr.Zero, SettingsHotkeyId);
                UnregisterHotKey(IntPtr.Zero, RepeatSpeechHotkeyId);
                UnregisterHotKey(IntPtr.Zero, NavigateToObjectiveHotkeyId);
                UnregisterHotKey(IntPtr.Zero, SelectNavigationTargetHotkeyId);
                UnregisterHotKey(IntPtr.Zero, AutoWalkHotkeyId);
                UnregisterHotKey(IntPtr.Zero, ExportNavMeshHotkeyId);
                Logger.LogInfo("Background hotkey thread exiting");
            }
        }

        private void RegisterHotkeyOrThrow(int id, uint virtualKey, string label)
        {
            RegisterHotkeyOrThrow(id, ModNoRepeat, virtualKey, label);
        }

        private void RegisterHotkeyOrThrow(int id, uint modifiers, uint virtualKey, string label)
        {
            if (RegisterHotKey(IntPtr.Zero, id, modifiers, virtualKey))
            {
                Logger.LogInfo("Registered hotkey: " + label);
                return;
            }

            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException("RegisterHotKey failed for " + label + " with Win32 error " + error);
        }

        private void ProcessRegisteredHotkey(int hotkeyId)
        {
            if (hotkeyId == HelpHotkeyId)
            {
                Logger.LogInfo("Help hotkey detected");
                AnnounceHelp();
                return;
            }

            if (hotkeyId == DebugHotkeyId)
            {
                if (IsModifierKeyDown(0x10) || IsModifierKeyDown(0x11) || IsModifierKeyDown(0x12))
                    return;

                ToggleDebugMode();
                return;
            }

            if (hotkeyId == SettingsHotkeyId)
            {
                if (!IsModifierKeyDown(0x11) || IsModifierKeyDown(0x10) || IsModifierKeyDown(0x12))
                    return;

                ToggleSettingsMenu();
                return;
            }

            if (hotkeyId == RepeatSpeechHotkeyId)
            {
                RepeatLastSpeech();
                return;
            }

            if (hotkeyId == NavigateToObjectiveHotkeyId)
            {
                if (IsModifierKeyDown(0x10) || IsModifierKeyDown(0x12))
                    return;

                Logger.LogInfo("Navigate to objective hotkey detected");
                AccessibilityWatcher.RequestNavigateToObjective();
                return;
            }

            if (hotkeyId == SelectNavigationTargetHotkeyId)
            {
                if (!IsModifierKeyDown(0x10) || IsModifierKeyDown(0x12))
                    return;

                Logger.LogInfo("Select navigation target hotkey detected");
                AccessibilityWatcher.RequestSelectNavigationTarget();
                return;
            }

            if (hotkeyId == AutoWalkHotkeyId)
            {
                if (!IsModifierKeyDown(0x12) || IsModifierKeyDown(0x10))
                    return;

                Logger.LogInfo("Auto-walk hotkey detected");
                AccessibilityWatcher.RequestAutoWalk();
                return;
            }

            if (hotkeyId == ExportNavMeshHotkeyId)
            {
                if (!IsModifierKeyDown(0x11) || !IsModifierKeyDown(0x10) || IsModifierKeyDown(0x12))
                    return;

                Logger.LogInfo("Navmesh export hotkey detected");
                AccessibilityWatcher.RequestExportNavMesh();
            }
        }

        private static bool IsModifierKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private void AnnounceHelp()
        {
            Loc.RefreshLanguage();
            ScreenReader.Say(Loc.Get("help_text"));
        }

        private void ToggleDebugMode()
        {
            DebugMode = !DebugMode;
            string status = DebugMode ? "enabled" : "disabled";
            Logger.LogInfo("Debug mode " + status);
            Loc.RefreshLanguage();

            if (DebugMode)
            {
                bool dumpedMappings = InputMappingReporter.TryDumpCurrentMappings(out int dumpedControllerCount);
                string messageKey = dumpedMappings ? "debug_mode_enabled_with_mapping_dump" : "debug_mode_enabled";
                ScreenReader.Say(Loc.Get(messageKey, dumpedControllerCount));
                return;
            }

            ScreenReader.Say(Loc.Get("debug_mode_disabled"));
        }

        private void ToggleSettingsMenu()
        {
            Logger.LogInfo("Accessibility settings hotkey detected");
            ModConfig.ToggleMenu();
        }

        private void RepeatLastSpeech()
        {
            Logger.LogInfo("Repeat speech hotkey detected");
            AccessibilityWatcher.RequestRepeatLastSpeech();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        public IntPtr hWnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
