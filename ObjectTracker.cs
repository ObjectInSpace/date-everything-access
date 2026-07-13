using System;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Plays a continuous guidance tone that updates with the tracked target.
    /// </summary>
    public static class ObjectTracker
    {
        private const string TrackerTrackName = "dea_navigation_tracker";
        // One-shot chirp played OVER the continuous tone when the player passes a route LANDMARK
        // (door / stairs / target) — a real place, never a bare grid-corner waypoint. The old
        // per-corner forward-chirp/reverse-buzz pair is gone: corners are planner artifacts the
        // player can't perceive, and the buzz double-fired on normal forward travel (the regress
        // ping-pong bug). Wrong-way travel is audible as the landmark pulse slowing instead.
        private const string LandmarkBlipTrackName = "dea_navigation_blip_fwd";
        private const string LogSource = "ObjectTracker";
        private const int SampleRate = 44100;
        // Carrier tone. The CLIP is generated at a FUNDAMENTAL frequency chosen by the user's tone
        // register (see ModConfig.TrackerToneFundamentalHz); the live pitch multiplier (below) shifts
        // it to encode distance. We used to synthesize a pure 440 Hz sine, but two accessibility facts
        // pushed us off that: (1) ISO 226 equal-loudness contours put human sensitivity on a broad
        // plateau around 1 kHz, so a mid-register fundamental is audible to the widest range of
        // hearing profiles; (2) a PURE sine puts all energy at one frequency, so a listener with a
        // notch there hears nothing — so the carrier is a HARMONIC STACK (fundamental + a few
        // harmonics at decaying amplitude), letting a loss at any single frequency be covered by the
        // others. The register setting exists because no single fundamental is optimal for everyone;
        // presbycusis spares the lows, some conductive/sensorineural losses spare the highs.
        // Default fundamental if the register can't be read (mid register ~1 kHz).
        private const float DefaultFundamentalFrequency = 1000f;
        // Relative amplitudes of the fundamental and its harmonics (index 0 = fundamental at 1x freq,
        // index 1 = 2x freq, ...). Decaying so the tone stays warm rather than buzzy, but every
        // partial carries enough energy to be a fallback if the listener has a notch at another.
        private static readonly float[] HarmonicAmplitudes = { 1f, 0.5f, 0.33f, 0.2f };
        // ---- Channel map: ONE meaning per channel, nothing shared ----
        //
        // The tracker has two modes with a deliberately different sound:
        //
        // ROUTE mode (walking, PULSING tone):
        //   PAN         = steer left/right toward the tracked point (heading error, body basis)
        //   VOLUME dip  = the tracked point is BEHIND you (turn around)
        //   PULSE RATE  = distance to the next LANDMARK (door/stairs/target) — fast = near
        //   PITCH       = constant. Two earlier pitch cues were removed after both leaked a
        //                 confusing second distance signal: (1) pitch-as-distance (redundant with
        //                 the pulse); (2) pitch-as-camera-elevation-aim — the required elevation to
        //                 a FLOOR-level point steepens as you approach (−19° at 5m → −60° at 1m),
        //                 so walking forward with a level camera made the tone rise and walking
        //                 back made it fall, with the camera untouched. And since every tracked
        //                 point is a floor cell, "vertical aim" could only ever say "look at the
        //                 floor" — a useless channel while WALKING.
        //
        // AIM mode (arrived, STEADY tone): the tracked point is now the OBJECT itself (which can
        // sit on a shelf or wall, unlike route points), the player is standing still (so the
        // distance-coupling that poisoned the elevation cue mid-route is gone), and the job is to
        // put the CAMERA on the object:
        //   PAN         = unchanged (horizontal aim)
        //   VOLUME dip  = unchanged (object behind you)
        //   PULSE       = OFF — the steady carrier is itself the "you have arrived, now aim" signal
        //   PITCH       = vertical aim, SIGNED: match the tone you heard while walking (the
        //                 register fundamental) and the camera is vertically on the object. Tone
        //                 too LOW → tilt the camera UP; too HIGH → tilt DOWN.
        //
        // PULSE (amplitude gating) RATE encodes DISTANCE to the next LANDMARK — the next door,
        // stair landing, or the target itself along the route (SimpleNavBridge.DistanceToNextLandmark,
        // path distance, so it's a true "how far to the next meaningful place"). FAST pulse = NEAR,
        // SLOW = FAR, like a parking sensor. Landmarks (not waypoints) so the rate never resets at an
        // invisible grid corner; each reset coincides with the audible landmark chirp, so it reads as
        // "reached the door — now counting down to the next place". We gate the carrier amplitude
        // with a raised-cosine at this rate rather than adding a second tone; PulseDepth < 1 keeps
        // the tone audible in the troughs so it reads as a pulsing tone, not on/off beeping.
        private const float MinPulseHz = 1.2f;          // farthest (≥ PulseDistanceFalloff away)
        private const float MaxPulseHz = 8f;            // right on the landmark
        private const float PulseDistanceFalloff = 10f; // metres over which the pulse ramps slow→fast
        private const float PulseDepth = 0.6f;          // how deep the amplitude dips each pulse (0=none, 1=to silence)
        // VOLUME base is CONSTANT. It used to swell with landmark proximity, which multiplied THREE
        // signals into one channel (landmark swell × behind dip × pulse gate) — a volume change was
        // unattributable. Landmark distance lives on the pulse rate now; volume only carries the
        // behind-you dip.
        private const float SteadyVolume = 0.22f;
        // AIM-mode vertical cue: pitch offset = clamp(signed camera aim error / full-scale) × swing.
        // Aim error = (camera's CURRENT elevation) − (elevation it MUST look at to hit the object):
        // aimed too HIGH → positive → tone rises ("tilt down"); too LOW → negative → tone drops
        // ("tilt up"); tone at the plain walking pitch (1.0) = vertically on the object.
        private const float ElevationPitchSwing = 0.35f;     // ± pitch multiplier at full aim error
        private const float ElevationAimFullScaleDeg = 35f;  // aim error (deg) mapped to full ± swing
        // Heading-error → pan. We compute the L/R balance ourselves (panStereo) from the direction to
        // the waypoint relative to the player's BODY forward. Pan = sin(headingError): this is the true
        // LEFT/RIGHT component of the direction, which reverses SMOOTHLY through zero and — critically —
        // has NO discontinuity when the target passes behind you. The old |SignedAngle|-with-full-scale
        // curve flipped hard-left↔hard-right as a behind-target crossed ±180° (the "snap" bug) and
        // pinned to full pan for any error past 90° (turning "did nothing" while off-axis). PanBoost < 1
        // is a gamma that EXPANDS the near-aligned range so a few degrees off-axis is still audible,
        // applied to the sine magnitude while preserving its sign. A pure 2D hard-pan sounds
        // "in-head"/artificial, so we can add a SMALL spatialBlend (PanSpatialBlend) for positional air.
        private const float PanBoost = 0.7f;
        // sin() is zero both AHEAD and BEHIND, so those two read identically on pan alone. To
        // disambiguate "turn around", attenuate the carrier when the target is BEHIND the player (cos of
        // the heading error < 0): a behind target sounds duller/quieter, easing back to full level as you
        // turn to face it. Bounded so behind never goes silent — it stays a guidance tone, not a dropout.
        private const float BehindVolumeScaleMin = 0.55f; // multiplier at directly-behind (180°)
        // Pure 2D for now (panStereo fully authoritative). A non-zero blend let the spatializer pan
        // from the CAMERA→target geometry, which pulls the source toward centre and DILUTES our
        // heading pan (part of why "everything sounded in the middle"). Re-add a small blend for
        // "natural"/positional feel only AFTER confirming the 2D heading pan reads clearly.
        private const float PanSpatialBlend = 0f;
        private const float ClipDurationSeconds = 1f;
        // Landmark blip: short rising chirp, fired when a door/stairs/target waypoint is passed.
        private const float BlipDurationSeconds = 0.16f;
        private const float BlipStartFrequency = 520f;
        private const float BlipEndFrequency = 880f;
        private const float BlipVolume = 0.35f;
        // A door contributes a TRIPLE of landmark waypoints (approach/opening/exit) that can be
        // crossed within a second of each other; the cooldown collapses them into ONE audible
        // "passed the door" chirp instead of a stutter of three.
        private const float LandmarkBlipCooldownSeconds = 2.5f;
        private const float TargetRefreshDistance = 0.2f;
        private const float DebugUpdateIntervalSeconds = 1f;

        private static GameObject _trackerAnchorObject;
        private static AudioClip _toneClip;
        private static AudioClip _landmarkBlipClip;
        private static float _nextLandmarkBlipTime;
        private static Vector3 _targetPosition;
        private static Vector3 _lastStartedTargetPosition;
        private static bool _requiresInteraction;
        private static bool _isTracking;
        // AIM mode: the tracked point is the object itself and the tone is steady (no pulse) with
        // pitch = vertical camera-aim error. Entered via StartAimTracking; any plain StartTracking
        // (route guidance) or StopTracking drops back to route mode.
        private static bool _isAimMode;
        private static bool _loggedMissingAudioManager;
        private static bool _loggedMissingReferenceTransform;
        private static bool _loggedMissingAudioSource;
        private static float _nextDebugUpdateTime;
        // Running phase of the distance PULSE (0..1), advanced each frame by the current pulse rate.
        // Kept as accumulated phase (not time*rate) so changing the rate doesn't jump the waveform —
        // the pulse stays continuous as the player moves and the rate ramps. Reset when tracking starts.
        private static float _pulsePhase;
        private static float _lastPulseUpdateTime;

        /// <summary>
        /// Initializes the tracker audio source on demand.
        /// </summary>
        public static void Initialize()
        {
            if (_toneClip != null)
                return;

            _trackerAnchorObject = new GameObject("DateEverythingObjectTrackerAnchor");
            _trackerAnchorObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_trackerAnchorObject);
            _toneClip = CreateToneClip();
            _landmarkBlipClip = CreateChirpClip("DateEverythingNavBlipForward", BlipStartFrequency, BlipEndFrequency);
            Main.Log.LogInfo("ObjectTracker initialized");
            DebugLogger.Log(LogCategory.State, LogSource, "Initialized tracker anchor and generated tone clip.");
        }

        /// <summary>
        /// Regenerates the carrier clip from the current tone register and, if tracking is live,
        /// restarts playback so the new fundamental takes effect immediately. Called by ModConfig
        /// when the user cycles the "Tracker tone pitch" setting.
        /// </summary>
        public static void RefreshToneClip()
        {
            if (_toneClip == null)
                return; // Not initialized yet; Initialize() will build the clip at the current register.

            _toneClip = CreateToneClip();

            if (!_isTracking)
                return;

            // Stop the looping track so it re-creates with the freshly generated clip, then restart.
            if (Singleton<AudioManager>.Instance != null)
                Singleton<AudioManager>.Instance.StopTrack(TrackerTrackName, 0f);
            StartTonePlayback();
        }

        /// <summary>
        /// Starts or refreshes tracking for the supplied target position.
        /// </summary>
        public static void StartTracking(Vector3 targetPosition)
        {
            StartTracking(targetPosition, requiresInteraction: false);
        }

        /// <summary>
        /// Starts or refreshes tracking for the supplied target position.
        /// </summary>
        public static void StartTracking(Vector3 targetPosition, bool requiresInteraction)
        {
            Initialize();

            _isAimMode = false; // plain tracking = route mode; StartAimTracking re-sets the flag
            Vector3 previousTargetPosition = _targetPosition;
            bool shouldRestartTone = !_isTracking ||
                _requiresInteraction != requiresInteraction ||
                Vector3.Distance(_lastStartedTargetPosition, targetPosition) > TargetRefreshDistance;
            bool didRetargetWithoutRestart = _isTracking &&
                !shouldRestartTone &&
                Vector3.Distance(previousTargetPosition, targetPosition) > 0.01f;

            _targetPosition = targetPosition;
            _requiresInteraction = requiresInteraction;
            _isTracking = true;
            if (_trackerAnchorObject != null)
                _trackerAnchorObject.transform.position = targetPosition;

            if (didRetargetWithoutRestart)
            {
                DebugLogger.Log(
                    LogCategory.State,
                    LogSource,
                    "RetargetTracking target=" + targetPosition +
                    " previousTarget=" + previousTargetPosition +
                    " requiresInteraction=" + requiresInteraction +
                    " restart=False");
            }

            if (!shouldRestartTone)
                return;

            _lastStartedTargetPosition = targetPosition;
            DebugLogger.Log(
                LogCategory.State,
                LogSource,
                "StartTracking target=" + targetPosition +
                " requiresInteraction=" + requiresInteraction +
                " restart=True");
            StartTonePlayback();
        }

        /// <summary>
        /// Switches the tone to AIM mode, tracking the object's own position (not a route point):
        /// steady carrier (no pulse), pitch = signed vertical camera-aim error, pan/behind as
        /// usual. Idempotent — call every frame while the aim phase is active.
        /// </summary>
        public static void StartAimTracking(Vector3 objectPosition)
        {
            StartTracking(objectPosition, requiresInteraction: false);
            _isAimMode = true;
        }

        /// <summary>
        /// Stops the active tracking tone.
        /// </summary>
        public static void StopTracking()
        {
            _isTracking = false;
            _isAimMode = false;
            _loggedMissingAudioSource = false;
            _loggedMissingReferenceTransform = false;
            _nextDebugUpdateTime = 0f;
            _pulsePhase = 0f;
            _lastPulseUpdateTime = 0f; // re-arm the pulse timer so the next track starts a clean cycle
            DebugLogger.Log(LogCategory.State, LogSource, "StopTracking");
            if (Singleton<AudioManager>.Instance != null)
                Singleton<AudioManager>.Instance.StopTrack(TrackerTrackName, 0f);
        }

        /// <summary>
        /// Plays the LANDMARK chirp: the player just passed a real route landmark (door, stair
        /// landing, target) — never a bare grid-corner waypoint. One-shot over the continuous
        /// tone; rate-limited so a door's approach/opening/exit triple chirps once, not thrice.
        /// </summary>
        public static void NotifyLandmarkReached()
        {
            if (Time.unscaledTime < _nextLandmarkBlipTime)
                return;
            _nextLandmarkBlipTime = Time.unscaledTime + LandmarkBlipCooldownSeconds;
            PlayBlip(LandmarkBlipTrackName, _landmarkBlipClip);
        }

        /// <summary>
        /// Plays the success chirp for the AIM phase: the game's raycast has selected the target —
        /// the camera is on it. Deliberately bypasses the landmark cooldown (arrival near a door
        /// may have chirped moments earlier; this one must not be swallowed).
        /// </summary>
        public static void NotifyAimLocked()
        {
            PlayBlip(LandmarkBlipTrackName, _landmarkBlipClip);
        }

        // Play a one-shot blip as its own non-looping 2D track so it layers over the guidance tone
        // without replacing it. Re-issuing the same track restarts it (a rapid double-advance just
        // re-triggers the chirp), which is the behaviour we want for discrete events.
        private static void PlayBlip(string trackName, AudioClip clip)
        {
            Initialize();
            if (clip == null)
                return;

            AudioManager audioManager = Singleton<AudioManager>.Instance;
            if (audioManager == null)
                return;

            audioManager.StopTrack(trackName, 0f);
            audioManager.PlayTrack(
                trackName,
                AUDIO_TYPE.SFX,
                pauseOthersOfType: false,
                pauseOthersNotOfType: false,
                fadeTime: 0f,
                playOverOtherSounds: true,
                lowerVolumeOfOthers: 1f,
                objectFor3dSound: null,
                loopSfx: false,
                providedTrack: clip,
                subgroup: SFX_SUBGROUP.FOLEY);

            AudioSource source = GetManagedAudioSourceByName(trackName);
            if (source != null)
            {
                source.spatialBlend = 0f;
                source.spatialize = false;
                source.panStereo = 0f;
                source.volume = BlipVolume;
                source.loop = false;
                source.ignoreListenerPause = true;
                source.bypassListenerEffects = true;
                if (!source.isPlaying)
                    source.Play();
            }

            DebugLogger.Log(LogCategory.State, LogSource, "Blip " + trackName);
        }

        /// <summary>
        /// Advances the audio guidance state when tracking is active.
        /// </summary>
        public static void UpdateTracking()
        {
            if (!_isTracking || _toneClip == null)
                return;

            Transform referenceTransform = GetReferenceTransform();
            if (referenceTransform == null)
            {
                if (!_loggedMissingReferenceTransform)
                {
                    _loggedMissingReferenceTransform = true;
                    DebugLogger.Log(LogCategory.State, LogSource, "No reference transform found for tracker audio.");
                }

                return;
            }

            _loggedMissingReferenceTransform = false;

            AudioSource audioSource = GetManagedAudioSource();
            if (audioSource == null)
            {
                StartTonePlayback();
                audioSource = GetManagedAudioSource();
                if (audioSource == null)
                {
                    if (!_loggedMissingAudioSource)
                    {
                        _loggedMissingAudioSource = true;
                        DebugLogger.Log(LogCategory.State, LogSource, "Tracker track exists but managed audio source was not found.");
                    }

                    return;
                }
            }

            _loggedMissingAudioSource = false;
            if (_trackerAnchorObject != null)
                _trackerAnchorObject.transform.position = _targetPosition;
            audioSource.transform.position = _targetPosition;
            // Mostly-2D source. We compute the pan ourselves (panStereo) for the "which way" cue; a
            // SMALL spatialBlend adds positional air so the hard pan doesn't sound "in-head"/artificial,
            // kept low (PanSpatialBlend) so the spatializer's own pan stays minor and our panStereo
            // dominates — no mushy centre. spatialize stays off (we don't want the platform spatializer
            // plugin on top); dopplerLevel is forced to 0 in StartTonePlayback so positional motion
            // never shifts pitch — pitch is deliberately constant.
            audioSource.spatialBlend = PanSpatialBlend;
            audioSource.spatialize = false;
            audioSource.spread = 0f;

            // DISTANCE measures from the player's BODY on the floor, flattened to XZ — NOT the
            // camera. The camera sits back and above the player, so a camera→point 3D distance never
            // drops below ~2-4m even standing on the point, which pinned the cue near its floor value
            // the whole time (the "nothing changed" bug). Body-XZ matches the follower's basis and
            // the pan, and actually reaches ~0 at the point.
            Vector3 bodyPos = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : referenceTransform.position;

            // HORIZONTAL "which way" — pan from the heading error between the player's BODY forward
            // and the direction to the waypoint (computed inside ComputeHeadingPan).
            audioSource.panStereo = ComputeHeadingPan();

            // PITCH: constant in ROUTE mode (see the channel map at the top of the file); in AIM
            // mode it carries the signed vertical camera-aim error — the tone returns to the plain
            // walking pitch exactly when the camera is vertically on the object.
            float elevationPitch = _isAimMode ? ComputeElevationPitchOffset() : 0f;
            audioSource.pitch = 1f + elevationPitch;

            // PULSE RATE = distance to the next LANDMARK (door / stairs / target) along the route —
            // path distance, so it slows when the player walks the wrong way. Falls back to the flat
            // distance to the tracked point when no route/landmark is available (e.g. bare tracking),
            // so the pulse never freezes. AIM mode is STEADY (no pulse): the un-pulsed carrier is
            // itself the "arrived — now aim the camera" phase marker.
            float landmarkDistance = -1f;
            if (!_isAimMode)
            {
                landmarkDistance = SimpleNavBridge.DistanceToNextLandmark(bodyPos);
                if (landmarkDistance < 0f)
                    landmarkDistance = FlatDistance(bodyPos, _targetPosition);
            }

            // VOLUME = constant base, dulled when the tracked point is BEHIND the player (sin-based
            // pan is zero both ahead and behind, so the dip disambiguates "turn around"), gated by
            // the landmark-distance pulse. Nothing else touches volume.
            float volume = SteadyVolume;
            if (_requiresInteraction)
                volume = Mathf.Min(1f, volume + 0.1f);
            float pulseGate = _isAimMode ? 1f : ComputeDistancePulse(landmarkDistance);
            audioSource.volume = volume * ComputeBehindVolumeScale() * pulseGate;

            if (!audioSource.isPlaying)
                StartTonePlayback();

            if (Main.DebugMode && Time.unscaledTime >= _nextDebugUpdateTime)
            {
                _nextDebugUpdateTime = Time.unscaledTime + DebugUpdateIntervalSeconds;
                DebugLogger.Log(
                    LogCategory.State,
                    LogSource,
                    "Audio update source=" + audioSource.name +
                    " playing=" + audioSource.isPlaying +
                    " target=" + _targetPosition +
                    " mode=" + (_isAimMode ? "aim" : "route") +
                    " landmarkDist=" + landmarkDistance.ToString("0.00") + " (drives pulse rate)" +
                    " volume=" + audioSource.volume.ToString("0.00") +
                    " pan=" + audioSource.panStereo.ToString("0.00") +
                    " headingErrDeg=" + GetHeadingErrorDegrees().ToString("0.0") +
                    " elevPitch=" + elevationPitch.ToString("0.00") +
                    " pulsePhase=" + _pulsePhase.ToString("0.00") +
                    " wp=" + SimpleNavBridge.WaypointProgressDebug +
                    " landmarks=" + SimpleNavBridge.LandmarkCountDebug);
            }
        }

        /// <summary>
        /// Gets whether tracking is currently active.
        /// </summary>
        public static bool IsTracking => _isTracking;

        private static void StartTonePlayback()
        {
            if (_toneClip == null)
                return;

            AudioManager audioManager = Singleton<AudioManager>.Instance;
            if (audioManager == null)
            {
                if (!_loggedMissingAudioManager)
                {
                    _loggedMissingAudioManager = true;
                    DebugLogger.Log(LogCategory.State, LogSource, "AudioManager instance was null while starting tracker tone.");
                }

                return;
            }

            _loggedMissingAudioManager = false;
            if (_trackerAnchorObject != null)
                _trackerAnchorObject.transform.position = _targetPosition;

            if (!audioManager.IsPlayingTrack(TrackerTrackName))
            {
                DebugLogger.Log(
                    LogCategory.State,
                    LogSource,
                    "Creating tracker track at " + _targetPosition + " requiresInteraction=" + _requiresInteraction);

                audioManager.PlayTrack(
                    TrackerTrackName,
                    AUDIO_TYPE.SFX,
                    pauseOthersOfType: false,
                    pauseOthersNotOfType: false,
                    fadeTime: 0f,
                    playOverOtherSounds: true,
                    lowerVolumeOfOthers: 1f,
                    objectFor3dSound: _trackerAnchorObject,
                    loopSfx: true,
                    providedTrack: _toneClip,
                    subgroup: SFX_SUBGROUP.FOLEY);
            }

            AudioSource audioSource = GetManagedAudioSource();
            if (audioSource == null)
                return;

            audioSource.loop = true;
            // Mostly-2D: pan (panStereo) and volume are computed manually each frame in
            // UpdateTracking. Match its spatialBlend here so there's no first-frame flicker. doppler=0
            // is critical — pitch is deliberately constant, so positional motion must never shift it.
            audioSource.spatialBlend = PanSpatialBlend;
            audioSource.spatialize = false;
            audioSource.priority = 0;
            audioSource.ignoreListenerPause = true;
            audioSource.bypassEffects = true;
            audioSource.bypassListenerEffects = true;
            audioSource.bypassReverbZones = true;
            audioSource.dopplerLevel = 0f;
            audioSource.spread = 0f;
            audioSource.panStereo = ComputeHeadingPan();
            audioSource.transform.position = _targetPosition;
            if (!audioSource.isPlaying)
                audioSource.Play();

            DebugLogger.Log(
                LogCategory.State,
                LogSource,
                "Tracker source ready playing=" + audioSource.isPlaying +
                " group=" + audioSource.outputAudioMixerGroup +
                " position=" + audioSource.transform.position);
        }

        // Synthesizes the continuous carrier as a harmonic stack over the user's chosen fundamental.
        // Summing several partials would overflow [-1, 1], so we normalize by the total amplitude
        // (worst case where all partials align) to keep the clip at unit peak — the live volume cue
        // then scales from there. See the carrier comment above for WHY the tone is a stack, not a sine.
        private static AudioClip CreateToneClip()
        {
            int sampleCount = Mathf.RoundToInt(SampleRate * ClipDurationSeconds);
            float[] samples = new float[sampleCount];
            float fundamental = ResolveFundamentalFrequency();

            float amplitudeSum = 0f;
            for (int h = 0; h < HarmonicAmplitudes.Length; h++)
                amplitudeSum += HarmonicAmplitudes[h];
            float normalization = amplitudeSum > 0f ? 1f / amplitudeSum : 1f;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)SampleRate;
                float value = 0f;
                for (int h = 0; h < HarmonicAmplitudes.Length; h++)
                {
                    float harmonicFreq = fundamental * (h + 1);
                    value += HarmonicAmplitudes[h] * Mathf.Sin(harmonicFreq * 2f * Mathf.PI * t);
                }
                samples[i] = value * normalization;
            }

            AudioClip clip = AudioClip.Create("DateEverythingNavigationTone", sampleCount, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        // The fundamental the carrier is synthesized at, from the user's tone register. Falls back to
        // the mid-register default if config isn't available (e.g. before ModConfig.Initialize ran).
        private static float ResolveFundamentalFrequency()
        {
            float configured = ModConfig.TrackerToneFundamentalHz;
            return configured > 0f ? configured : DefaultFundamentalFrequency;
        }

        // A short rising frequency sweep with a quick attack/decay envelope — the landmark chirp.
        // The integral of the linearly ramped frequency gives the instantaneous phase, so the pitch
        // glides smoothly across the blip.
        private static AudioClip CreateChirpClip(string name, float startHz, float endHz)
        {
            int sampleCount = Mathf.RoundToInt(SampleRate * BlipDurationSeconds);
            float[] samples = new float[sampleCount];
            float duration = BlipDurationSeconds;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)SampleRate;
                float u = t / duration;                          // 0..1 across the blip
                // Phase = 2π ∫f dt for a linear sweep = 2π (startHz·t + (endHz-startHz)·t²/(2·dur)).
                float phase = 2f * Mathf.PI * (startHz * t + (endHz - startHz) * t * t / (2f * duration));
                // Short raised-cosine envelope so the blip doesn't click on/off.
                float envelope = Mathf.Sin(Mathf.PI * Mathf.Clamp01(u));
                samples[i] = Mathf.Sin(phase) * envelope;
            }

            AudioClip clip = AudioClip.Create(name, sampleCount, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        // Signed heading error in degrees: the angle (flattened to XZ) between the player BODY's
        // forward and the direction to the waypoint. Positive = waypoint is to the player's RIGHT.
        // Body forward is the WASD basis (BetterPlayerControl turns the body on look.x and moves on
        // body.forward), so 0° means "press W and you close distance". Falls back to the audio
        // reference transform (camera/listener) only if the player control isn't available.
        private static float GetHeadingErrorDegrees()
        {
            Transform body = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform
                : GetReferenceTransform();
            if (body == null)
                return 0f;

            Vector3 toTarget = _targetPosition - body.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f)
                return 0f;

            Vector3 forward = body.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return 0f;

            return Vector3.SignedAngle(forward, toTarget.normalized, Vector3.up);
        }

        // XZ (horizontal) distance — the navigation basis. Vertical offsets (camera height, target on
        // a shelf) must not affect the proximity cues, or they pin near a floor value (the bug where
        // the cue "never changed" because camera→target 3D distance never reached ~0).
        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Pan = the LEFT/RIGHT component of the direction to the target: sin(headingError). This is
        // continuous everywhere — it rises to full pan at 90° off-axis and eases back toward centre as
        // the target passes BEHIND (there is no ±180° sign flip, so no hard-left↔hard-right snap). A
        // gamma (PanBoost < 1) on the magnitude expands the near-aligned range so small errors are still
        // audible, with the sine's sign preserved. Behind/ahead ambiguity (both give small |sin|) is
        // resolved by ComputeBehindVolumeScale, not here.
        private static float ComputeHeadingPan()
        {
            float errorRad = GetHeadingErrorDegrees() * Mathf.Deg2Rad;
            float lateral = Mathf.Sin(errorRad); // −1 (hard left) .. +1 (hard right), 0 ahead AND behind
            float shaped = Mathf.Pow(Mathf.Abs(lateral), PanBoost);
            return lateral >= 0f ? shaped : -shaped;
        }

        // AIM-mode SIGNED vertical camera-aim offset. 0 when the camera points straight at the
        // object's height; POSITIVE (tone rises) when aimed too HIGH — "tilt down"; NEGATIVE (tone
        // drops) when aimed too LOW — "tilt up". Centring the tone on the plain walking pitch =
        // vertical line of sight. Only meaningful while STANDING at the object (aim phase): the
        // required elevation depends on horizontal distance, which is why this cue was removed from
        // the walking tone (it read as a phantom distance signal there). Uses the CAMERA transform:
        // its position for the required angle, its forward for the current angle.
        private static float ComputeElevationPitchOffset()
        {
            Transform cam = GetReferenceTransform();
            if (cam == null)
                return 0f;

            // Elevation the camera MUST look at to hit the object (from the camera's own position).
            Vector3 toTarget = _targetPosition - cam.position;
            float horizontal = new Vector2(toTarget.x, toTarget.z).magnitude;
            if (horizontal < 0.01f && Mathf.Abs(toTarget.y) < 0.01f)
                return 0f; // sitting on the object — centred (aligned)
            float requiredElevDeg = Mathf.Atan2(toTarget.y, horizontal) * Mathf.Rad2Deg;

            // Camera's CURRENT elevation: the vertical angle of its forward vector.
            Vector3 fwd = cam.forward;
            float fwdHoriz = new Vector2(fwd.x, fwd.z).magnitude;
            float currentElevDeg = Mathf.Atan2(fwd.y, fwdHoriz) * Mathf.Rad2Deg;

            float aimErrorDeg = currentElevDeg - requiredElevDeg;
            float normalized = Mathf.Clamp(aimErrorDeg / ElevationAimFullScaleDeg, -1f, 1f);
            return normalized * ElevationPitchSwing;
        }

        // Amplitude gate (0..1) for the DISTANCE pulse. The pulse RATE encodes distance to the next
        // LANDMARK — fast near, slow far, like a parking sensor — while the gate depth (PulseDepth)
        // stays fixed so the tone dips but never fully drops out (reads as a pulsing tone, not beeping).
        // The rate is derived from distance each frame and the PHASE is accumulated (phase += rate*dt),
        // so a ramping rate never jumps the waveform. Returns 1 (steady, no pulse) if we can't time the
        // frame yet. Multiplies the base volume in UpdateTracking.
        private static float ComputeDistancePulse(float distance)
        {
            float now = Time.unscaledTime;
            float dt = _lastPulseUpdateTime > 0f ? now - _lastPulseUpdateTime : 0f;
            _lastPulseUpdateTime = now;

            // NEAR = fast, FAR = slow. Same one-leg falloff + squared perceptual ramp the pitch used.
            float proximity = Mathf.Clamp01(1f - (distance / PulseDistanceFalloff));
            proximity *= proximity;
            float rateHz = Mathf.Lerp(MinPulseHz, MaxPulseHz, proximity);

            // Advance and wrap the phase; guard against a huge dt (first frame / hitch) driving a jump.
            _pulsePhase += rateHz * Mathf.Min(dt, 0.1f);
            _pulsePhase -= Mathf.Floor(_pulsePhase);

            // Raised-cosine gate: 1 at phase 0, dipping to (1-PulseDepth) mid-cycle. Smooth so it
            // sounds like a swell, not a click.
            float gate = 0.5f * (1f + Mathf.Cos(_pulsePhase * 2f * Mathf.PI)); // 1..0..1 across the cycle
            return 1f - PulseDepth * (1f - gate);
        }

        // Volume multiplier that dulls the carrier when the target is BEHIND the player. cos(headingErr)
        // is +1 dead ahead, 0 at the sides, −1 directly behind; we scale from full level (ahead/sides)
        // down to BehindVolumeScaleMin (directly behind) so "turn around" is audible without a dropout.
        private static float ComputeBehindVolumeScale()
        {
            float cos = Mathf.Cos(GetHeadingErrorDegrees() * Mathf.Deg2Rad);
            float behindAmount = Mathf.Clamp01(-cos); // 0 when ahead/at sides, 1 when directly behind
            return Mathf.Lerp(1f, BehindVolumeScaleMin, behindAmount);
        }

        private static Transform GetReferenceTransform()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera != null)
                return mainCamera.transform;

            AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            if (listener != null)
                return listener.transform;

            return BetterPlayerControl.Instance != null ? BetterPlayerControl.Instance.transform : null;
        }

        private static AudioSource GetManagedAudioSource()
        {
            return GetManagedAudioSourceByName(TrackerTrackName);
        }

        private static AudioSource GetManagedAudioSourceByName(string trackName)
        {
            AudioManager audioManager = Singleton<AudioManager>.Instance;
            if (audioManager == null || audioManager.CurrentTracks == null)
                return null;

            for (int i = audioManager.CurrentTracks.Count - 1; i >= 0; i--)
            {
                AudioManager.MusicChild track = audioManager.CurrentTracks[i];
                if (track == null || !string.Equals(track.Name, trackName, StringComparison.Ordinal))
                    continue;

                return track.GetAudio();
            }

            return null;
        }
    }
}
