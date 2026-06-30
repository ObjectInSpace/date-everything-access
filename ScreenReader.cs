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

        // Coalesced-cycle interrupt. The ambient world announcers (nearby object, room,
        // screen summary, status) run as a chain every poll tick. They must NOT cut each
        // other off WITHIN a tick (room + a newly-in-range object that appear on the same
        // tick should both speak), but the FIRST of them in a tick SHOULD interrupt
        // whatever stale announcement is still playing/queued from an earlier tick —
        // otherwise walking quickly past objects queues their names behind one another
        // (the bug: world focus changes pile up while menu focus changes cut off). Flow:
        // the watcher calls BeginCoalescedCycle() once at the top of the ambient chain;
        // the first SayCoalesced() that actually emits consumes the flag and interrupts,
        // the rest in that tick append. Menus don't use this — they call Say(interrupt:true)
        // directly. See the ambient block in AccessibilityWatcher.Update.
        private static bool _coalescedInterruptPending;

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
                Output(text, interrupt);
            }
            catch (Exception ex)
            {
                Main.Log.LogWarning("ScreenReader.Say failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Arms the next coalesced announcement of this poll tick to interrupt. Call once
        /// at the start of the ambient announcer chain.
        /// </summary>
        public static void BeginCoalescedCycle()
        {
            _coalescedInterruptPending = true;
        }

        /// <summary>
        /// Speaks an ambient announcement that coalesces within a poll tick: the FIRST one
        /// to emit after BeginCoalescedCycle() interrupts stale speech from earlier ticks;
        /// later ones in the same tick append so co-occurring announcements all play. This
        /// makes world focus changes (looking from object to object while walking) cut off
        /// the previous one instead of queueing, matching menu behaviour, without one
        /// ambient announcer clobbering another that fired on the same tick.
        /// </summary>
        public static void SayCoalesced(string text, bool remember = true, bool rememberAsRepeatable = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            bool interrupt = _coalescedInterruptPending;
            _coalescedInterruptPending = false;
            Say(text, interrupt: interrupt, remember: remember, rememberAsRepeatable: rememberAsRepeatable);
        }

        /// <summary>
        /// Sends text to Tolk, issuing an explicit silence first when interrupting.
        ///
        /// Tolk's own interrupt flag (Tolk_Output(text, true)) bundles a cancel + speak, but with NVDA in SLEEP MODE
        /// that bundled cancel does not clear NVDA's speech queue, so a new focus-change announcement gets appended
        /// BEHIND whatever is still being spoken instead of cutting it off. Calling Tolk_Silence() (→ NVDA controller
        /// cancelSpeech) as a separate, ordered call before the output reliably flushes the queue first, so the latest
        /// announcement is spoken immediately. Harmless when NVDA is awake (silence then speak is the normal interrupt).
        /// </summary>
        private static void Output(string text, bool interrupt)
        {
            if (interrupt)
            {
                try
                {
                    Tolk_Silence();
                }
                catch
                {
                    // A failed pre-silence must not block the speak that follows.
                }
            }

            Tolk_Output(text, interrupt);
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
                Output(lastSpokenText, interrupt);
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
