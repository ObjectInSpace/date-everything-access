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
        // One-shot blip tracks played OVER the continuous tone to mark a waypoint transition. Forward
        // Forward (rising chirp) = the active leg advanced toward the goal. Reverse = a BUZZ (not a
        // chirp) so backtracking is unmistakably "wrong" rather than a mirror of the advance tone.
        // Distinct names so they don't replace the tone or each other. See
        // [[project-tracker-tone-distance-pitch]] for the channel split.
        private const string BlipForwardTrackName = "dea_navigation_blip_fwd";
        private const string BlipReverseTrackName = "dea_navigation_blip_rev";
        private const string LogSource = "ObjectTracker";
        private const int SampleRate = 44100;
        // Carrier tone. The CLIP is generated at this frequency; the live pitch multiplier (below)
        // shifts it to encode distance, so this is just the synthesis base for a comfortable A4.
        private const float BaseFrequency = 440f;
        // PITCH now encodes DISTANCE to the current waypoint (it used to encode vertical up/down, which
        // was dropped — that channel was near-silent on flat ground and the screen-projection it used
        // wandered as the player merely walked). Pitch DELTAS are far easier to perceive than volume
        // deltas, so this is the primary "am I getting closer" cue. FAR = low pitch, NEAR = high pitch,
        // ramped over one leg (PitchDistanceFalloff). The multiplier range is musical, not screechy.
        private const float MinDistancePitch = 0.7f;   // farthest (≥ PitchDistanceFalloff away)
        private const float MaxDistancePitch = 1.6f;   // right on the waypoint
        private const float PitchDistanceFalloff = 5f; // metres over which pitch sweeps min→max (one leg)
        // VOLUME = closeness to the NEXT LANDMARK (door/stairs/target), a mid-tier cue between pitch
        // (next corner) and the whole route. Bounded distance → meaningful even on a long route.
        // SteadyVolume is the fallback floor when there's no route/landmark to measure.
        private const float SteadyVolume = 0.45f;
        private const float MinLandmarkVolume = 0.3f;   // landmark is far (≥ LandmarkVolumeFalloff)
        private const float MaxLandmarkVolume = 0.75f;  // right at the landmark
        private const float LandmarkVolumeFalloff = 8f; // metres over which the swell happens
        // Heading-error → pan. We compute the L/R balance ourselves (panStereo) from the angle between
        // the player's BODY forward and the direction to the waypoint. PanSensitivityGamma < 1 EXPANDS
        // the near-aligned range so a few degrees off-axis is clearly audible; PanFullScaleDeg is the
        // error that maps to full pan. A pure 2D hard-pan sounds "in-head"/artificial, so we add a
        // SMALL spatialBlend (PanSpatialBlend) for positional air — kept low so the spatializer's own
        // pan stays a minor contributor and our computed panStereo still dominates (no mushy centre).
        private const float PanSensitivityGamma = 0.7f;
        private const float PanFullScaleDeg = 90f;
        // Pure 2D for now (panStereo fully authoritative). A non-zero blend let the spatializer pan
        // from the CAMERA→target geometry, which pulls the source toward centre and DILUTES our
        // heading pan (part of why "everything sounded in the middle"). Re-add a small blend for
        // "natural"/positional feel only AFTER confirming the 2D heading pan reads clearly.
        private const float PanSpatialBlend = 0f;
        private const float ClipDurationSeconds = 1f;
        // Forward blip: short rising chirp. Reverse buzz: a low, rougher tone (see CreateBuzzClip).
        private const float BlipDurationSeconds = 0.16f;
        private const float BlipStartFrequency = 520f;
        private const float BlipEndFrequency = 880f;
        private const float BuzzDurationSeconds = 0.22f;
        private const float BuzzFrequency = 160f;
        private const float BlipVolume = 0.7f;
        private const float TargetRefreshDistance = 0.2f;
        private const float DebugUpdateIntervalSeconds = 1f;

        private static GameObject _trackerAnchorObject;
        private static AudioClip _toneClip;
        private static AudioClip _blipForwardClip;
        private static AudioClip _blipReverseClip;
        private static Vector3 _targetPosition;
        private static Vector3 _lastStartedTargetPosition;
        private static bool _requiresInteraction;
        private static bool _isTracking;
        private static bool _loggedMissingAudioManager;
        private static bool _loggedMissingReferenceTransform;
        private static bool _loggedMissingAudioSource;
        private static float _nextDebugUpdateTime;

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
            _blipForwardClip = CreateChirpClip("DateEverythingNavBlipForward", BlipStartFrequency, BlipEndFrequency);
            _blipReverseClip = CreateBuzzClip("DateEverythingNavBlipReverse");
            Main.Log.LogInfo("ObjectTracker initialized");
            DebugLogger.Log(LogCategory.State, LogSource, "Initialized tracker anchor and generated tone clip.");
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
        /// Stops the active tracking tone.
        /// </summary>
        public static void StopTracking()
        {
            _isTracking = false;
            _loggedMissingAudioSource = false;
            _loggedMissingReferenceTransform = false;
            _nextDebugUpdateTime = 0f;
            DebugLogger.Log(LogCategory.State, LogSource, "StopTracking");
            if (Singleton<AudioManager>.Instance != null)
                Singleton<AudioManager>.Instance.StopTrack(TrackerTrackName, 0f);
        }

        /// <summary>
        /// Plays the FORWARD transition blip (rising chirp): the active leg advanced toward the goal.
        /// One-shot over the continuous tone; safe to call even if tracking just started.
        /// </summary>
        public static void NotifyWaypointAdvanced()
        {
            PlayBlip(BlipForwardTrackName, _blipForwardClip);
        }

        /// <summary>
        /// Plays the REVERSE transition blip (falling chirp): the active leg regressed because the
        /// player backtracked along the route. One-shot over the continuous tone.
        /// </summary>
        public static void NotifyWaypointRegressed()
        {
            PlayBlip(BlipReverseTrackName, _blipReverseClip);
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
            // never shifts pitch — pitch is OURS, encoding distance.
            audioSource.spatialBlend = PanSpatialBlend;
            audioSource.spatialize = false;
            audioSource.spread = 0f;

            // All DISTANCE cues (pitch, landmark volume) measure from the player's BODY on the floor,
            // flattened to XZ — NOT the camera. The camera sits back and above the player, so a
            // camera→waypoint 3D distance never drops below ~2-4m even standing on the waypoint, which
            // pinned pitch/volume near their floor values the whole time (the "nothing changed" bug).
            // Body-XZ matches the follower's basis and the pan, and actually reaches ~0 at the waypoint.
            Vector3 bodyPos = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : referenceTransform.position;

            // HORIZONTAL "which way" — pan from the heading error between the player's BODY forward
            // and the direction to the waypoint (computed inside ComputeHeadingPan).
            audioSource.panStereo = ComputeHeadingPan();

            // PITCH "how close to the current waypoint" — primary proximity cue (pitch deltas read more
            // clearly than volume deltas). FAR = MinDistancePitch, NEAR = MaxDistancePitch over one leg
            // (PitchDistanceFalloff). Flat XZ so vertical camera/target offset can't damp it.
            float distance = FlatDistance(bodyPos, _targetPosition);
            float proximityAmount = Mathf.Clamp01(1f - (distance / PitchDistanceFalloff));
            proximityAmount *= proximityAmount;
            audioSource.pitch = Mathf.Lerp(MinDistancePitch, MaxDistancePitch, proximityAmount);

            // VOLUME = closeness to the NEXT LANDMARK (next door / stairs / target along the route),
            // a mid-tier cue between pitch (next corner waypoint) and the whole route. Bounded by
            // construction so it stays meaningful on long routes. Quiet when a landmark is far,
            // swelling as the player nears it; resets when they pass one (the landmark advances). If
            // no route/landmark is available, hold the steady carrier so the tone never drops out.
            float landmarkDistance = SimpleNavBridge.DistanceToNextLandmark(bodyPos);
            float volume;
            if (landmarkDistance < 0f)
            {
                volume = SteadyVolume;
            }
            else
            {
                float nearAmount = Mathf.Clamp01(1f - (landmarkDistance / LandmarkVolumeFalloff));
                nearAmount *= nearAmount; // perceptual ramp toward the landmark
                volume = Mathf.Lerp(MinLandmarkVolume, MaxLandmarkVolume, nearAmount);
            }
            audioSource.volume = _requiresInteraction ? Mathf.Min(1f, volume + 0.1f) : volume;

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
                    " distance=" + distance.ToString("0.00") +
                    " landmarkDist=" + landmarkDistance.ToString("0.00") +
                    " volume=" + audioSource.volume.ToString("0.00") +
                    " pan=" + audioSource.panStereo.ToString("0.00") +
                    " headingErrDeg=" + GetHeadingErrorDegrees().ToString("0.0") +
                    " pitch=" + audioSource.pitch.ToString("0.00") + " (=distance cue)" +
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
            // Mostly-2D: pan (panStereo) and pitch/volume are computed manually each frame in
            // UpdateTracking. Match its spatialBlend here so there's no first-frame flicker. doppler=0
            // is critical — pitch is our distance cue, so positional motion must never shift it.
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

        private static AudioClip CreateToneClip()
        {
            int sampleCount = Mathf.RoundToInt(SampleRate * ClipDurationSeconds);
            float[] samples = new float[sampleCount];
            float angularFrequency = BaseFrequency * 2f * Mathf.PI;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)SampleRate;
                samples[i] = Mathf.Sin(angularFrequency * t);
            }

            AudioClip clip = AudioClip.Create("DateEverythingNavigationTone", sampleCount, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        // A short frequency sweep from startHz to endHz with a quick attack/decay envelope, used for
        // the waypoint-transition blips. Sweeping UP vs DOWN is what tells the player whether the leg
        // moved toward the goal (forward) or backtracked (reverse). The integral of the linearly
        // ramped frequency gives the instantaneous phase, so the pitch glides smoothly across the blip.
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

        // Map heading error → stereo pan in [-1, 1], applying PanSensitivityGamma so the near-aligned
        // range gets expanded resolution (small errors are clearly audible) and far-off errors pin to
        // the edge. We own this curve precisely because Unity's spatializer doesn't expose one.
        // XZ (horizontal) distance — the navigation basis. Vertical offsets (camera height, target on
        // a shelf) must not affect the proximity cues, or they pin near a floor value (the bug where
        // pitch/volume "never changed" because camera→target 3D distance never reached ~0).
        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float ComputeHeadingPan()
        {
            float errorDeg = GetHeadingErrorDegrees();
            float normalized = Mathf.Clamp01(Mathf.Abs(errorDeg) / PanFullScaleDeg);
            float shaped = Mathf.Pow(normalized, PanSensitivityGamma);
            return errorDeg >= 0f ? shaped : -shaped;
        }

        // The REVERSE-direction cue: a low, rough buzz (not a chirp), so backtracking along the route
        // sounds unmistakably like a "wrong way" warning rather than a mirror image of the forward
        // advance chirp. Roughness = a square-ish wave (odd harmonics) amplitude-modulated by a slow
        // tremolo, which the ear reads as a buzzy alert. Envelope ramps in/out to avoid clicks.
        private static AudioClip CreateBuzzClip(string name)
        {
            int sampleCount = Mathf.RoundToInt(SampleRate * BuzzDurationSeconds);
            float[] samples = new float[sampleCount];
            float duration = BuzzDurationSeconds;
            float angular = BuzzFrequency * 2f * Mathf.PI;
            const float tremoloHz = 55f; // amplitude flutter that gives the "buzz" character

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)SampleRate;
                float u = t / duration; // 0..1
                // Square-ish carrier (sign of a sine) softened slightly so it's harsh but not a pure
                // square click-fest; rich in odd harmonics → buzzy.
                float carrier = Mathf.Sign(Mathf.Sin(angular * t)) * 0.7f + Mathf.Sin(angular * t) * 0.3f;
                float tremolo = 0.6f + 0.4f * Mathf.Sin(tremoloHz * 2f * Mathf.PI * t);
                float envelope = Mathf.Sin(Mathf.PI * Mathf.Clamp01(u));
                samples[i] = carrier * tremolo * envelope;
            }

            AudioClip clip = AudioClip.Create(name, sampleCount, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
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
