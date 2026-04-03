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
        private const string LogSource = "ObjectTracker";
        private const int SampleRate = 44100;
        private const float BaseFrequency = 880f;
        private const float MinPitch = 0.4f;
        private const float MaxPitch = 2f;
        private const float MinVolume = 0.7f;
        private const float MaxVolume = 1f;
        private const float MaxTrackingDistance = 100f;
        private const float MaxVerticalPitchOffset = 6f;
        private const float ClipDurationSeconds = 1f;
        private const float TargetRefreshDistance = 0.2f;
        private const float DebugUpdateIntervalSeconds = 1f;

        private static GameObject _trackerAnchorObject;
        private static AudioClip _toneClip;
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
            Main.Log.LogInfo("ObjectTracker initialized");
            DebugLogger.Log(LogCategory.State, LogSource, "Initialized tracker anchor and generated tone clip.");
        }

        /// <summary>
        /// Starts or refreshes tracking for the supplied target position.
        /// </summary>
        public static void StartTracking(Vector3 targetPosition)
        {
            StartTracking(targetPosition, NavigationGraph.StepKind.Unknown, requiresInteraction: false);
        }

        /// <summary>
        /// Starts or refreshes tracking for the supplied target position and transition type.
        /// </summary>
        public static void StartTracking(Vector3 targetPosition, NavigationGraph.StepKind stepKind, bool requiresInteraction)
        {
            Initialize();

            bool shouldRestartTone = !_isTracking ||
                _requiresInteraction != requiresInteraction ||
                Vector3.Distance(_lastStartedTargetPosition, targetPosition) > TargetRefreshDistance;

            _targetPosition = targetPosition;
            _requiresInteraction = requiresInteraction;
            _isTracking = true;
            if (_trackerAnchorObject != null)
                _trackerAnchorObject.transform.position = targetPosition;

            if (!shouldRestartTone)
                return;

            _lastStartedTargetPosition = targetPosition;
            DebugLogger.Log(
                LogCategory.State,
                LogSource,
                "StartTracking target=" + targetPosition +
                " step=" + stepKind +
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
            audioSource.spatialBlend = 1f;
            audioSource.spatialize = true;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 0.5f;
            audioSource.maxDistance = MaxTrackingDistance;
            audioSource.spread = 0f;
            audioSource.panStereo = 0f;

            Vector3 toTarget = _targetPosition - referenceTransform.position;
            float distance = toTarget.magnitude;
            float proximityAmount = Mathf.Clamp01(1f - (distance / MaxTrackingDistance));

            float verticalAmount = Mathf.InverseLerp(-MaxVerticalPitchOffset, MaxVerticalPitchOffset, toTarget.y);
            audioSource.pitch = Mathf.Lerp(MinPitch, MaxPitch, verticalAmount);
            float proximityVolume = Mathf.Lerp(MinVolume, MaxVolume, proximityAmount);
            audioSource.volume = _requiresInteraction
                ? Mathf.Min(1f, proximityVolume + 0.1f)
                : proximityVolume;

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
                    " volume=" + audioSource.volume.ToString("0.00") +
                    " pitch=" + audioSource.pitch.ToString("0.00") +
                    " spatialBlend=" + audioSource.spatialBlend.ToString("0.00") +
                    " spatialize=" + audioSource.spatialize);
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
            audioSource.spatialBlend = 1f;
            audioSource.spatialize = true;
            audioSource.priority = 0;
            audioSource.ignoreListenerPause = true;
            audioSource.bypassEffects = true;
            audioSource.bypassListenerEffects = true;
            audioSource.bypassReverbZones = true;
            audioSource.dopplerLevel = 0f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 0.5f;
            audioSource.maxDistance = MaxTrackingDistance;
            audioSource.spread = 0f;
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
            AudioManager audioManager = Singleton<AudioManager>.Instance;
            if (audioManager == null || audioManager.CurrentTracks == null)
                return null;

            for (int i = audioManager.CurrentTracks.Count - 1; i >= 0; i--)
            {
                AudioManager.MusicChild track = audioManager.CurrentTracks[i];
                if (track == null || !string.Equals(track.Name, TrackerTrackName, StringComparison.Ordinal))
                    continue;

                return track.GetAudio();
            }

            return null;
        }
    }
}
