using System;
using UnityEngine;

namespace DateEverythingAccess
{
    /// <summary>
    /// Plays simple tone beeps that guide the player toward a world-space target.
    /// </summary>
    public static class ObjectTracker
    {
        private const int SampleRate = 44100;
        private const float BaseFrequency = 1000f;
        private const float MinPitch = 0.4f;
        private const float MaxPitch = 2f;
        private const float MinBeepRate = 0.5f;
        private const float MaxBeepRate = 8f;
        private const float MaxTrackingDistance = 100f;
        private const float ClipDurationSeconds = 0.05f;

        private static GameObject _trackerObject;
        private static AudioSource _audioSource;
        private static AudioClip _beepClip;
        private static float _nextBeepTime;
        private static float _currentBeepRate;
        private static Vector3 _targetPosition;
        private static bool _isTracking;

        /// <summary>
        /// Initializes the tracker audio source on demand.
        /// </summary>
        public static void Initialize()
        {
            if (_trackerObject != null)
                return;

            _trackerObject = new GameObject("DateEverythingObjectTracker");
            _trackerObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_trackerObject);

            _audioSource = _trackerObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.volume = 0.3f;

            _beepClip = CreateBeepClip();
            Main.Log.LogInfo("ObjectTracker initialized");
        }

        /// <summary>
        /// Starts or refreshes tracking for the supplied target position.
        /// </summary>
        public static void StartTracking(Vector3 targetPosition)
        {
            if (_audioSource == null)
                Initialize();

            _targetPosition = targetPosition;
            _isTracking = true;
            _nextBeepTime = 0f;
        }

        /// <summary>
        /// Stops the active tracking tone.
        /// </summary>
        public static void StopTracking()
        {
            _isTracking = false;
            if (_audioSource != null)
                _audioSource.Stop();
        }

        /// <summary>
        /// Advances the audio guidance state when tracking is active.
        /// </summary>
        public static void UpdateTracking()
        {
            if (!_isTracking || _audioSource == null || _beepClip == null)
                return;

            Camera mainCamera = Camera.main;
            if (mainCamera == null)
                return;

            Vector3 screenPosition = mainCamera.WorldToScreenPoint(_targetPosition);
            float normalizedY = Screen.height > 0 ? Mathf.Clamp01(screenPosition.y / Screen.height) : 0.5f;
            float distance = Vector3.Distance(mainCamera.transform.position, _targetPosition);
            float normalizedDistance = Mathf.Clamp01(1f - (distance / MaxTrackingDistance));

            _audioSource.pitch = Mathf.Lerp(MinPitch, MaxPitch, normalizedY);
            _currentBeepRate = Mathf.Lerp(MinBeepRate, MaxBeepRate, normalizedDistance);

            if (Time.unscaledTime < _nextBeepTime)
                return;

            _audioSource.PlayOneShot(_beepClip, 1f);
            _nextBeepTime = Time.unscaledTime + (1f / _currentBeepRate);
        }

        /// <summary>
        /// Gets whether tracking is currently active.
        /// </summary>
        public static bool IsTracking => _isTracking;

        /// <summary>
        /// Gets the current beep rate in Hz.
        /// </summary>
        public static float BeepRate => _currentBeepRate;

        private static AudioClip CreateBeepClip()
        {
            int sampleCount = Mathf.RoundToInt(SampleRate * ClipDurationSeconds);
            float[] samples = new float[sampleCount];
            float angularFrequency = BaseFrequency * 2f * Mathf.PI;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)SampleRate;
                samples[i] = Mathf.Sin(angularFrequency * t);
            }

            int fadeLength = Mathf.Max(1, SampleRate / 100);
            for (int i = 0; i < fadeLength && i < sampleCount; i++)
            {
                float envelope = i / (float)fadeLength;
                samples[i] *= envelope;
                int tailIndex = sampleCount - 1 - i;
                if (tailIndex >= 0 && tailIndex < sampleCount)
                    samples[tailIndex] *= envelope;
            }

            AudioClip clip = AudioClip.Create("DateEverythingNavigationBeep", sampleCount, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
