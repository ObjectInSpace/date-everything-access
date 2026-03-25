using System;
using System.Runtime.InteropServices;

namespace DateEverythingAccess
{
    /// <summary>
    /// Minimal Tolk wrapper for screen reader announcements.
    /// </summary>
    public static class ScreenReader
    {
        private static readonly object _speechLock = new object();

        [DllImport("Tolk.dll")]
        private static extern void Tolk_Load();

        [DllImport("Tolk.dll")]
        private static extern void Tolk_Unload();

        [DllImport("Tolk.dll")]
        private static extern bool Tolk_IsLoaded();

        [DllImport("Tolk.dll")]
        private static extern bool Tolk_HasSpeech();

        [DllImport("Tolk.dll", CharSet = CharSet.Unicode)]
        private static extern bool Tolk_Output(string text, bool interrupt);

        [DllImport("Tolk.dll")]
        private static extern bool Tolk_Silence();

        [DllImport("Tolk.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr Tolk_DetectScreenReader();

        private static bool _available;
        private static bool _initialized;
        private static string _lastSpokenText;
        private static string _lastRepeatableText;

        /// <summary>
        /// Gets whether Tolk and a speech-capable screen reader are available.
        /// </summary>
        public static bool IsAvailable
        {
            get { return _available; }
        }

        /// <summary>
        /// Loads Tolk and detects the active screen reader.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            try
            {
                Tolk_Load();
                _available = Tolk_IsLoaded() && Tolk_HasSpeech();

                if (_available)
                {
                    IntPtr srNamePtr = Tolk_DetectScreenReader();
                    string srName = srNamePtr != IntPtr.Zero
                        ? Marshal.PtrToStringUni(srNamePtr)
                        : "Unknown";
                    Main.Log.LogInfo("Screen reader detected: " + srName);
                }
                else
                {
                    Main.Log.LogWarning("No screen reader detected or Tolk is unavailable");
                }
            }
            catch (DllNotFoundException)
            {
                Main.Log.LogError("Tolk.dll or nvdaControllerClient64.dll is missing from the game directory.");
                _available = false;
            }
            catch (Exception ex)
            {
                Main.Log.LogError("Failed to initialize Tolk: " + ex.Message);
                _available = false;
            }

            _initialized = true;
        }

        /// <summary>
        /// Speaks text through Tolk and optionally remembers it for replay.
        /// </summary>
        public static void Say(string text, bool interrupt = true, bool remember = true, bool rememberAsRepeatable = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (remember || rememberAsRepeatable)
            {
                lock (_speechLock)
                {
                    if (remember)
                    {
                        _lastSpokenText = text;
                    }

                    if (rememberAsRepeatable)
                    {
                        _lastRepeatableText = text;
                    }
                }
            }

            DebugLogger.LogScreenReader(text);

            if (!_available)
                return;

            try
            {
                Tolk_Output(text, interrupt);
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("ScreenReader.Say failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Repeats the most recently spoken text when one is available.
        /// </summary>
        public static bool RepeatLastSpoken(bool interrupt = true)
        {
            string lastRepeatableText;
            string lastSpokenText;

            lock (_speechLock)
            {
                lastRepeatableText = _lastRepeatableText;
                lastSpokenText = _lastSpokenText;
            }

            if (!string.IsNullOrWhiteSpace(lastRepeatableText))
            {
                lastSpokenText = lastRepeatableText;
            }

            if (string.IsNullOrWhiteSpace(lastSpokenText))
                return false;

            DebugLogger.LogScreenReader(lastSpokenText);

            if (!_available)
                return true;

            try
            {
                Tolk_Output(lastSpokenText, interrupt);
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("ScreenReader.RepeatLastSpoken failed: " + ex.Message);
            }

            return true;
        }

        /// <summary>
        /// Stops any current speech output.
        /// </summary>
        public static void Stop()
        {
            if (!_available)
                return;

            try
            {
                Tolk_Silence();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Unloads Tolk and clears cached speech state.
        /// </summary>
        public static void Shutdown()
        {
            if (!_initialized)
                return;

            try
            {
                Tolk_Unload();
            }
            catch
            {
            }

            _initialized = false;
            _available = false;
            lock (_speechLock)
            {
                _lastSpokenText = null;
                _lastRepeatableText = null;
            }
        }
    }
}
