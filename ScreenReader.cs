using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DateEverythingAccess
{
    /// <summary>
    /// Speech output through Prism (https://github.com/ethindp/prism): the running screen reader
    /// when there is one (NVDA, JAWS and others), otherwise the system voice.
    /// </summary>
    public static class ScreenReader
    {
        private static readonly object _speechLock = new object();
        // Prism backends are single-threaded; speech is requested from the game thread and the
        // background hotkey thread (F1 help), so every Prism call goes through this lock.
        private static readonly object _prismLock = new object();

        private const string PrismDll = "prism.dll";

        // PrismConfig as of Prism 0.18 (PRISM_CONFIG_VERSION 3). Filled by prism_config_init.
        [StructLayout(LayoutKind.Sequential)]
        private struct PrismConfig
        {
            public byte version;
            public IntPtr registry;
            public IntPtr availability_callback;
            public IntPtr availability_userdata;
            public uint availability_poll_interval_ms;
            public uint availability_debounce_samples;
            public uint availability_backoff_max_ms;
            [MarshalAs(UnmanagedType.U1)] public bool availability_auto_power_manage;
        }

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern PrismConfig prism_config_init();

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_init(ref PrismConfig cfg);

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void prism_shutdown(IntPtr ctx);

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_registry_acquire_best(IntPtr ctx);

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void prism_backend_free(IntPtr backend);

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_backend_name(IntPtr backend);

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int prism_backend_output(IntPtr backend, byte[] utf8Text,
            [MarshalAs(UnmanagedType.U1)] bool interrupt);

        [DllImport(PrismDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int prism_backend_stop(IntPtr backend);

        // Called by Prism on its own background thread whenever a backend becomes available or
        // unavailable (the player starts or quits NVDA). It only raises a flag; the switch happens
        // at the next thing said.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PrismAvailabilityCallback(IntPtr userdata, ulong backend, IntPtr name,
            [MarshalAs(UnmanagedType.U1)] bool available);

        private static readonly PrismAvailabilityCallback _availabilityCallback = OnBackendAvailabilityChanged;
        private static volatile bool _backendsChanged;

        private static void OnBackendAvailabilityChanged(IntPtr userdata, ulong backend, IntPtr name, bool available)
        {
            _backendsChanged = true;
            try
            {
                if (Main.Log != null)
                    Main.Log.LogInfo("Speech: " + (name != IntPtr.Zero ? PtrToUtf8(name) : "backend") +
                        (available ? " became available" : " stopped"));
            }
            catch { }
        }

        private static IntPtr _prismContext;
        private static IntPtr _backend;
        private static string _backendName;

        private static bool _available;
        private static bool _usingSapiFallback;

        /// <summary>
        /// True when no screen reader is running and speech goes through a Windows system
        /// voice (SAPI or OneCore) instead.
        /// </summary>
        public static bool IsUsingSapiFallback => _usingSapiFallback;
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
        /// Starts Prism and picks the running screen reader, or the system voice.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            try
            {
                PrismConfig cfg = prism_config_init();
                // Ask Prism to watch for screen readers starting and stopping (it polls about once
                // a second on its own thread) so speech can move to NVDA when it starts and back
                // to the Windows voice when it quits.
                cfg.availability_callback = Marshal.GetFunctionPointerForDelegate(_availabilityCallback);
                _prismContext = prism_init(ref cfg);
                if (_prismContext == IntPtr.Zero)
                {
                    Main.Log.LogError("Prism failed to start; speech output disabled.");
                    _available = false;
                }
                else
                {
                    lock (_prismLock)
                        PickBestBackend(logChoice: true);
                    if (!_available)
                        Main.Log.LogWarning("Prism found no screen reader and no system voice; speech output disabled.");
                }
            }
            catch (DllNotFoundException)
            {
                Main.Log.LogError("prism.dll is missing from the game directory.");
                _available = false;
            }
            catch (EntryPointNotFoundException ex)
            {
                Main.Log.LogError("prism.dll is not the expected version: " + ex.Message);
                _available = false;
            }
            catch (Exception ex)
            {
                Main.Log.LogError("Failed to initialize Prism: " + ex.Message);
                _available = false;
            }

            _initialized = true;
        }

        // Prism tries its backends from highest priority down (screen readers before the system
        // voices) and returns the first that works. The one in use is let go first: Prism keeps
        // backends it has handed out, and while we held NVDA's it kept giving it back even after
        // NVDA had quit, so the Windows voice never took over. Call with _prismLock held.
        private static void PickBestBackend(bool logChoice)
        {
            _backendsChanged = false;
            if (_backend != IntPtr.Zero)
            {
                prism_backend_free(_backend);
                _backend = IntPtr.Zero;
            }
            IntPtr best = prism_registry_acquire_best(_prismContext);
            if (best == IntPtr.Zero)
            {
                if ((logChoice || _backendName != null) && Main.Log != null)
                    Main.Log.LogWarning("Speech: no screen reader or system voice available.");
                _available = false;
                _backendName = null;
                return;
            }
            _backend = best;
            _available = true;
            IntPtr namePtr = prism_backend_name(best);
            string name = namePtr != IntPtr.Zero ? PtrToUtf8(namePtr) : "?";
            bool changed = !string.Equals(name, _backendName, StringComparison.Ordinal);
            _backendName = name;
            _usingSapiFallback = name.IndexOf("SAPI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("OneCore", StringComparison.OrdinalIgnoreCase) >= 0;
            if ((logChoice || changed) && Main.Log != null)
                Main.Log.LogInfo(_usingSapiFallback
                    ? "No screen reader detected; using the system voice (" + name + ")."
                    : "Screen reader detected: " + name);
        }

        private static void RecheckBackendIfDue()
        {
            if (_prismContext == IntPtr.Zero) return;
            if (_backendsChanged || _backend == IntPtr.Zero)
                PickBestBackend(logChoice: false);
        }

        private static string PtrToUtf8(IntPtr ptr)
        {
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            byte[] buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        private static byte[] ToUtf8(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            byte[] z = new byte[raw.Length + 1];
            Buffer.BlockCopy(raw, 0, z, 0, raw.Length);
            return z;
        }

        /// <summary>
        /// Speaks text through Prism and optionally remembers it for replay.
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

            // Not gated on _available: with Prism running but nothing to speak through yet (no
            // screen reader, no voice), Output re-checks every few seconds and picks one up.
            if (_prismContext == IntPtr.Zero)
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
        /// Sends text to Prism, issuing an explicit stop first when interrupting.
        ///
        /// With NVDA in SLEEP MODE, an interrupting speak on its own did not clear NVDA's speech queue (seen with
        /// Tolk), so a new focus-change announcement got appended BEHIND whatever was still being spoken instead of
        /// cutting it off. A separate, ordered stop before the output flushes the queue first, so the latest
        /// announcement is spoken immediately. Harmless when NVDA is awake (stop then speak is the normal interrupt).
        /// </summary>
        private static void Output(string text, bool interrupt)
        {
            lock (_prismLock)
            {
                RecheckBackendIfDue();
                if (_backend == IntPtr.Zero)
                    return;
                if (interrupt)
                {
                    // An explicit stop first, then the new line (see above).
                    try { prism_backend_stop(_backend); } catch { }
                }
                int err = prism_backend_output(_backend, ToUtf8(text), interrupt);
                if (err != 0)
                {
                    // Speaking failed: most likely the screen reader just went away (NVDA quit, and
                    // Prism hasn't reported it yet). Switch now and say it there.
                    if (Main.Log != null) Main.Log.LogInfo("Speech: " + (_backendName ?? "backend") + " failed (error " + err + "); switching");
                    PickBestBackend(logChoice: false);
                    if (_backend != IntPtr.Zero)
                        prism_backend_output(_backend, ToUtf8(text), interrupt);
                }
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

            if (_prismContext == IntPtr.Zero)
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
                lock (_prismLock)
                {
                    if (_backend != IntPtr.Zero)
                        prism_backend_stop(_backend);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Shuts Prism down and clears cached speech state.
        /// </summary>
        public static void Shutdown()
        {
            if (!_initialized)
                return;

            try
            {
                lock (_prismLock)
                {
                    if (_backend != IntPtr.Zero)
                    {
                        prism_backend_free(_backend);
                        _backend = IntPtr.Zero;
                    }
                    if (_prismContext != IntPtr.Zero)
                    {
                        prism_shutdown(_prismContext);
                        _prismContext = IntPtr.Zero;
                    }
                }
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
