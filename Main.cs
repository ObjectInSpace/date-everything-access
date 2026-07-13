using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;

namespace DateEverythingAccess
{
    internal static class PluginMetadata
    {
        internal const string Guid = "com.amock.dateeverythingaccess";
        internal const string Name = "Date Everything Access";
        internal const string Version = "1.1.2";
    }

    [BepInPlugin(PluginMetadata.Guid, PluginMetadata.Name, PluginMetadata.Version)]
    public class Main : BaseUnityPlugin
    {
        private const int VkF1 = 0x70;
        private const int VkF8 = 0x77;
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
        private const int CoverageSweepHotkeyId = 13;
        // NOTE: Repeat-last (`), look-around (L), object tracker (O), objective tracker (Ctrl+O),
        // and auto-walk (Alt+O) are NO LONGER global OS hotkeys. Bare letter/backtick keys registered
        // via RegisterHotKey would be swallowed from ALL text input (name/save entry) while the game is
        // focused, so they are polled in AccessibilityWatcher.Update instead and suppressed while a text
        // field or the settings menu has focus. Only F-key / Ctrl combos remain global below.

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
        public static string RuntimeAssemblyPath { get; private set; }
        public static string RuntimeAssemblySha256 { get; private set; }
        public static string RuntimeBuildStamp { get; private set; }

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
            InitializeRuntimeBuildMetadata();
            ScreenReader.Initialize();
            Loc.Initialize();
            ModConfig.Initialize(Config);
            _harmony = new Harmony("com.amock.dateeverythingaccess");
            _harmony.PatchAll();

            Application.quitting += OnApplicationQuitting;
            StartHotkeyThread();
            AccessibilityWatcher.EnsureCreated();

            Logger.LogInfo("Date Everything Access initialized version=" + PluginMetadata.Version);
            Logger.LogInfo(
                "Runtime build metadata stamp=" + GetRuntimeBuildStamp() +
                " assemblyPath=" + (RuntimeAssemblyPath ?? "<null>"));
            ScreenReader.Say(Loc.Get("mod_loaded"));
            Logger.LogInfo("Startup announcement queued");
        }

        internal static string GetRuntimeBuildStamp()
        {
            return string.IsNullOrWhiteSpace(RuntimeBuildStamp)
                ? PluginMetadata.Version + "|uninitialized"
                : RuntimeBuildStamp;
        }

        internal static string TryComputeFileSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", string.Empty);
                }
            }
            catch (Exception ex)
            {
                Log?.LogWarning("Failed to compute SHA256 for " + path + ": " + ex.Message);
                return null;
            }
        }

        private static void InitializeRuntimeBuildMetadata()
        {
            try
            {
                string assemblyPath = typeof(Main).Assembly.Location;
                RuntimeAssemblyPath = assemblyPath;

                string lastWriteUtc = File.Exists(assemblyPath)
                    ? File.GetLastWriteTimeUtc(assemblyPath).ToString("o")
                    : "missing";
                RuntimeAssemblySha256 = TryComputeFileSha256(assemblyPath) ?? "unavailable";
                RuntimeBuildStamp =
                    PluginMetadata.Version +
                    "|utc=" + lastWriteUtc +
                    "|sha256=" + RuntimeAssemblySha256;
            }
            catch (Exception ex)
            {
                RuntimeBuildStamp = PluginMetadata.Version + "|build-metadata-error";
                Log?.LogWarning("Failed to initialize runtime build metadata: " + ex.Message);
            }
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
                int registered = 0;
                registered += TryRegisterHotkey(HelpHotkeyId, VkF1, "F1") ? 1 : 0;
                registered += TryRegisterHotkey(DebugHotkeyId, VkF9, "F9") ? 1 : 0;
                registered += TryRegisterHotkey(SettingsHotkeyId, ModControl | ModNoRepeat, VkF1, "Ctrl+F1") ? 1 : 0;
                registered += TryRegisterHotkey(CoverageSweepHotkeyId, ModControl | ModShift | ModAlt | ModNoRepeat, VkF8, "Ctrl+Alt+Shift+F8") ? 1 : 0;
                Logger.LogInfo("Background hotkey message loop active (" + registered + " hotkey(s) registered)");

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
                UnregisterHotKey(IntPtr.Zero, CoverageSweepHotkeyId);
                Logger.LogInfo("Background hotkey thread exiting");
            }
        }

        private bool TryRegisterHotkey(int id, uint virtualKey, string label)
        {
            return TryRegisterHotkey(id, ModNoRepeat, virtualKey, label);
        }

        private bool TryRegisterHotkey(int id, uint modifiers, uint virtualKey, string label)
        {
            if (RegisterHotKey(IntPtr.Zero, id, modifiers, virtualKey))
            {
                Logger.LogInfo("Registered hotkey: " + label);
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            // Error 1409 (ERROR_HOTKEY_ALREADY_REGISTERED) means another app owns this
            // combo. Skip just this binding and keep the thread + message loop alive so
            // the remaining hotkeys still work, rather than tearing down all of them.
            Logger.LogWarning("Could not register hotkey " + label + " (Win32 error " + error +
                "); skipping it. Other hotkeys are unaffected.");
            return false;
        }

        private void ProcessRegisteredHotkey(int hotkeyId)
        {
            if (hotkeyId == HelpHotkeyId)
            {
                if (IsModifierKeyDown(0x10) || IsModifierKeyDown(0x11) || IsModifierKeyDown(0x12))
                    return;

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

            if (hotkeyId == CoverageSweepHotkeyId)
            {
                if (!IsModifierKeyDown(0x11) || !IsModifierKeyDown(0x10) || !IsModifierKeyDown(0x12))
                    return;

                Logger.LogInfo("Coverage sweep hotkey detected");
                AccessibilityWatcher.RequestToggleCoverageSweep();
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
