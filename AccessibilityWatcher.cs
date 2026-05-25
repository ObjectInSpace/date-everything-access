using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using TMPro;
using T17.Services;
using T17.UI;
using Team17.Scripts.Services.Input;
using BepInEx;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DateEverythingAccess
{
    internal sealed partial class AccessibilityWatcher : MonoBehaviour
    {
        private enum SpecsAnnouncementMode
        {
            None,
            Stats,
            Tooltip,
            Glossary
        }

        private enum NavigationTargetKind
        {
            DirectObject,
            ExitWaypoint,
            TransitionInteractable,
            EntryWaypoint,
            ZoneFallback,
            LocalWaypoint
        }

        private enum OpenPassageTraversalStage
        {
            None,
            SourceWaypoint,
            SourceHandoff,
            DestinationWaypoint,
            DestinationHandoff
        }

        private enum OpenPassageTraversalTrigger
        {
            ActivateStep,
            SourceWaypointCommitted,
            SourceHandoffCompleted,
            DestinationWaypointReached,
            DestinationZoneReached
        }

        private enum DoorCommittedSourceRecoveryStage
        {
            None,
            SourceThreshold,
            PushThrough
        }

        private enum DoorCommittedSourceRecoveryTrigger
        {
            ActivateFromLoop,
            EscalateFromRepeatedLoop,
            SourceThresholdSatisfied
        }

        private enum TrackedNavigationMode
        {
            None,
            DirectObject,
            EquivalentZoneAnchor
        }

        private enum FacingRelativeDirection
        {
            Here,
            Ahead,
            AheadRight,
            Right,
            BehindRight,
            Behind,
            BehindLeft,
            Left,
            AheadLeft
        }

        private enum TutorialObjectiveKind
        {
            None,
            Computer,
            OfficeExit,
            FrontDoor,
            HouseExit,
            Dorian,
            Phone,
            Maggie,
            Bed,
            Skylar,
            AnyUnmetDatable,
            AnyUnrealizedDatable
        }

        private const float PopupSelectionSuppressionSeconds = 0.75f;
        private const float UIDialogSelectionSuppressionSeconds = 0.75f;
        private const float SpecsSelectionSuppressionSeconds = 0.75f;
        private const float CreditsSelectionSuppressionSeconds = 0.75f;
        private const float SpecsInitialAnnouncementGraceSeconds = 1f;
        private const float SpecsTutorialDialogStartTimeoutSeconds = 3f;
        private const float SpecsTutorialDialogTransitionGraceSeconds = 0.5f;
        private const float DateADexOpenEntryInitialSuppressionSeconds = 3f;
        private const float DateADexOpenEntryMinimumSuppressionSeconds = 2.5f;
        private const float DateADexOpenEntryMaximumSuppressionSeconds = 8f;
        private const float EstimatedSpeechWordsPerMinute = 185f;
        private const float EstimatedSpeechLeadInSeconds = 0.75f;
        private const float AutoWalkArrivalDistance = 2f;
        private const float AutoWalkTransitionInteractableArrivalDistance = 0.75f;
        private const float AutoWalkOpenPassageDestinationApproachDistance = 2.5f;
        private const float AutoWalkLookScaleDegrees = 45f;
        private const float AutoWalkProgressDistance = 0.35f;
        private const float AutoWalkBlockedTimeoutSeconds = 2f;
        private const float AutoWalkInteractionRetrySeconds = 0.75f;
        private const float AutoWalkMovementProbeMinimumCommand = 0.2f;
        private const float AutoWalkMovementProbeCancelledVelocity = 0.1f;
        private const float AutoWalkMovementProbeCancelledDisplacement = 0.08f;
        private const float AutoWalkOpenPassageHandoffDistance = 2.5f;
        private const float OpenPassageGuidedWaypointAdvanceDistance = 1.25f;
        private const float OpenPassageGuidedWaypointDedupDistance = 0.25f;
        private const float LocalNavigationPathAdvanceDistance = 0.75f;
        private const float LocalNavigationLookaheadDistance = 3f;
        private const float LocalNavigationGoalReachedDistance = 2f;
        private const float LocalNavigationStallBypassSeconds = 2.5f;
        private const float LocalNavigationStallMovementThreshold = 0.45f;
        private const float LocalNavigationStallTargetTolerance = 0.2f;
        private const float LocalNavigationStallBypassDurationSeconds = 1f;
        private const float OpenPassageOverrideLocalNavigationGoalReachedDistance = 0.75f;
        private const float DoorThresholdHandoffLocalNavigationGoalReachedDistance = 0.35f;
        private const float DoorThresholdAdvanceRawGoalReachedDistance = 0.5f;
        private const float DoorPushThroughLocalNavigationGoalReachedDistance = 0.35f;
        private const float DoorEntryAdvanceLocalNavigationGoalReachedDistance = 0.35f;
        private const float LocalNavigationGoalRetargetDistance = 0.5f;
        private const float TrackedInteractableApproachClearanceDistance = 0.9f;
        private const float TrackedInteractableApproachRetargetDistance = 0.75f;
        private const float TrackedInteractableApproachMinimumExtent = 0.35f;
        private const float AutoWalkConnectorSearchDistance = 4f;
        private const float InteractableZoneFallbackDistance = 8f;
        private const float DoorTraversalClearanceDistance = 1.4f;
        private const float DoorTraversalLateralOffsetDistance = 0.6f;
        private const float DoorThresholdAdvanceProxyCompletionDistance = DoorTraversalClearanceDistance + DoorTraversalLateralOffsetDistance;
        private const float DoorTraversalPushThroughDistance = 1.35f;
        private const float DoorTraversalMaximumPushThroughDistance = 3.25f;
        private const float DoorPushThroughSourceAdvanceDistance = 1f;
        private const float DoorPushThroughArrivalDistance = 2.15f;
        private const float DoorPushThroughNoHandoffCommitTolerance = 0.1f;
        private const float DoorPushThroughRecoveryNoHandoffCommitExtraTolerance = 0.2f;
        private const float DoorPushThroughBlockedTimeoutSeconds = 3f;
        private const float DoorThresholdAdvanceBypassDistance = 1f;
        private const float DoorPostInteractionFallbackExhaustionSeconds = 0.2f;
        private const float DoorPostInteractionFallbackExhaustionMovementThreshold = 0.35f;
        private const float TransitionFacingAlignmentCooldownSeconds = 0.2f;
        private const float TransitionFacingAlignmentMinimumAngleDegrees = 8f;
        private const float UnityNavMeshFallbackSampleDistance = 3.5f;
        private const float UnityNavMeshFallbackAdvanceDistance = 0.5f;
        private const float TutorialGiftApproachRadius = 1.25f;
        private const float SimpleNavDoorTargetRadius = 3f;
        private const int AutoWalkMaxRecoveryAttempts = 2;
        private const int DoorCommittedSourceWatchdogLoopTripsBeforeRetry = 2;
        private const int DoorCommittedSourceWatchdogMaxInteractionRetries = 1;
        private const int DoorPostInteractionFallbackExhaustionAttempts = 8;
        private const int DoorPostInteractionLoopExhaustionDetections = 4;
        private const int VkUp = 0x26;
        private const int VkDown = 0x28;
        private const int VkLeft = 0x25;
        private const int VkRight = 0x27;
        private const int VkReturn = 0x0D;
        private const int VkSpace = 0x20;
        private const int VkEscape = 0x1B;

        private static readonly Regex RichTextRegex = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static FieldInfo _talkingUiDialogBoxField;
        private static FieldInfo _dialogBoxNameTextField;
        private static FieldInfo _dialogBoxDialogTextField;
        private static FieldInfo _talkingUiChoicesButtonsField;
        private static FieldInfo _resultSplashTitleBannerField;
        private static FieldInfo _collectablesScreenNameField;
        private static FieldInfo _collectablesScreenDescField;
        private static FieldInfo _tutorialSignpostField;
        private static FieldInfo _tutorialSignpostTextField;
        private static FieldInfo _tutorialSubtitleTextField;
        private static FieldInfo _tutorialGiftBoxField;
        private static FieldInfo _tutorialFrontDoorField;
        private static FieldInfo _tutorialComputerField;
        private static FieldInfo _tutorialTriggerZonesField;
        private static FieldInfo _roomersCurrentEntryField;
        private static FieldInfo _roomersEntriesField;
        private static FieldInfo _engagementTitleField;
        private static FieldInfo _engagementStateField;
        private static FieldInfo _specStatTooltipsField;
        private static FieldInfo _specStatMainKeyButtonField;
        private static FieldInfo _specStatMainAutoSelectFallbackField;
        private static FieldInfo _specStatMainCurrentPageField;
        private static FieldInfo _specStatBlockNameFirstLetterField;
        private static FieldInfo _specStatBlockNameRestField;
        private static FieldInfo _specStatBlockAdjectiveLabelField;
        private static FieldInfo _specStatBlockLevelDescriptionTextField;
        private static FieldInfo _specGlossaryBlockNameFirstLetterField;
        private static FieldInfo _specGlossaryBlockNameRestField;
        private static FieldInfo _specGlossaryBlockDescriptionTextField;
        private static FieldInfo _creditsScreenTextField;
        private static FieldInfo _uiDialogManagerActiveDialogsField;
        private static FieldInfo _uiDialogGameObjectField;
        private static FieldInfo _uiDialogTitleField;
        private static FieldInfo _uiDialogBodyTextField;
        private static FieldInfo _saveScreenManagerNewSaveSlotField;
        private static FieldInfo _saveSlotPlayTimeField;
        private static FieldInfo _saveSlotDaysPlayedField;
        private static FieldInfo _betterPlayerControlMoveField;
        private static FieldInfo _betterPlayerControlLookField;
        private static Type _engagementType;
        private static Type _loadingFactsType;
        private static int _repeatLastSpeechRequested;
        private static int _describeCurrentRoomRequested;
        private static int _navigateToObjectiveRequested;
        private static int _selectNavigationTargetRequested;
        private static int _autoWalkRequested;
        private static int _pendingDateADexEntryAnnouncementRequested;
        private static float _pendingDateADexEntryAnnouncementNotBefore;
        private static float _pendingDateADexEntryAnnouncementExpiresAt;
        private static float _suppressDateADexOpenEntrySelectionUntil;
        private static DateADexEntry _pendingDateADexDetailEntry;
        private static float _suppressInitialSpecsAnnouncementsUntil;
        private static bool _awaitingSpecsTutorialDialogs;
        private static bool _choiceUpWasDown;
        private static bool _choiceDownWasDown;
        private static bool _choiceLeftWasDown;
        private static bool _choiceRightWasDown;
        private static bool _choiceReturnWasDown;
        private static bool _choiceSpaceWasDown;
        private static bool _pickerUpWasDown;
        private static bool _pickerDownWasDown;
        private static bool _pickerReturnWasDown;
        private static bool _pickerEscapeWasDown;
        private static int _virtualChatChoiceIndex = -1;
        private static string _virtualChatChoiceContextKey;
        private static AccessibilityWatcher _instance;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private string _lastAnnouncedSelection;
        private int _lastSelectedObjectId;
        private string _lastAnnouncedDialogue;
        private string _lastScreenSummary;
        private string _lastRoomName;
        private string _lastInteractableId;
        private string _lastDateADexDetail;
        private string _lastResultDetail;
        private string _lastPopupAnnouncement;
        private string _lastUIDialogAnnouncement;
        private string _lastSpecsAnnouncement;
        private string _lastCreditsAnnouncement;
        private string _lastPhoneAppContentAnnouncement;
        private string _lastPhoneAppContentKey;
        private string _lastTutorialAnnouncement;
        private string _lastSubtitleAnnouncement;
        private string _lastEngagementAnnouncement;
        private string _lastLoadingAnnouncement;
        private string _lastExamineAnnouncement;
        private string _lastSelectionDebugSnapshot;
        private string _lastNavigationTargetDebugSnapshot;
        private string _lastNavigationAutoWalkDebugSnapshot;
        private string _lastNavigationTransitionDebugSnapshot;
        private string _lastNavigationBlockedDetail;
        private bool? _lastDateviatorsEquipped;
        private bool _wasSpecsVisible;
        private int _lastDateviatorsCharges = -1;
        private DayPhase? _lastDayPhase;
        private int _lastUnlockedCollectables = -1;
        private int _lastMetCount = -1;
        private int _lastFriendCount = -1;
        private int _lastLoveCount = -1;
        private int _lastHateCount = -1;
        private int _lastRealizedCount = -1;
        private float _nextPollTime;
        private float _nextSimpleRouteDiagnosticTime;
        private float _suppressDateADexSelectionUntil;
        private float _suppressPopupSelectionUntil;
        private float _suppressUIDialogSelectionUntil;
        private float _suppressSpecsSelectionUntil;
        private float _suppressCreditsSelectionUntil;
        private float _suppressPendingSpecsTutorialUntil;
        private float _lastAutoWalkProgressTime;
        private SpecsAnnouncementMode _lastSpecsAnnouncementMode;
        private InteractableObj _trackedInteractable;
        private string _trackedInteractableId;
        private string _trackedInteractableLabel;
        private string _trackedInteractableZone;
        private string _trackedInteractableApproachId;
        private string _navigationTargetZone;
        private string _navigationTargetLabel;
        private Vector3 _lastAutoWalkPosition;
        private Vector3 _trackedInteractableApproachReferencePosition;
        private Vector3 _trackedInteractableApproachTarget;
        private Vector3 _navigationWorldTarget;
        private float _navigationWorldTargetRadius;
        private bool _hasNavigationWorldTarget;
        private bool _isNavigationActive;
        private bool _isAutoWalking;

        private sealed class KnownObjectTarget
        {
            public InteractableObj Interactable;
            public string Label;
            public float Distance;
        }

        private static readonly HashSet<string> _examinedObjectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<KnownObjectTarget> _knownObjectTargets;
        private int _knownObjectSelectionIndex = -1;
        private bool _isKnownObjectPickerOpen;

        internal static void EnsureCreated()
        {
            if (_instance != null)
                return;

            AccessibilityWatcher existingWatcher = FindObjectOfType<AccessibilityWatcher>();
            if (existingWatcher != null)
            {
                _instance = existingWatcher;
                return;
            }

            var watcherObject = new GameObject("DateEverythingAccessWatcher");
            watcherObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(watcherObject);
            watcherObject.AddComponent<AccessibilityWatcher>();
            Main.Log.LogInfo("Accessibility watcher created");
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Main.Log.LogWarning("Destroying duplicate accessibility watcher instance.");
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        internal static void RequestRepeatLastSpeech()
        {
            Interlocked.Exchange(ref _repeatLastSpeechRequested, 1);
        }

        internal static void RequestDescribeCurrentRoom()
        {
            Interlocked.Exchange(ref _describeCurrentRoomRequested, 1);
        }

        internal static void RequestNavigateToObjective()
        {
            Interlocked.Exchange(ref _navigateToObjectiveRequested, 1);
        }

        internal static void RequestSelectNavigationTarget()
        {
            Interlocked.Exchange(ref _selectNavigationTargetRequested, 1);
        }

        internal static void RequestAutoWalk()
        {
            Interlocked.Exchange(ref _autoWalkRequested, 1);
        }

        internal static void RequestToggleCoverageSweep()
        {
            Interlocked.Exchange(ref _coverageSweepRequested, 1);
        }

        private static int _coverageSweepRequested;

        internal static void RequestDateADexEntryAnnouncement(DateADexEntry entry)
        {
            _pendingDateADexDetailEntry = entry;
            Interlocked.Exchange(ref _pendingDateADexEntryAnnouncementRequested, 1);
            _pendingDateADexEntryAnnouncementNotBefore = Time.unscaledTime + 0.05f;
            _pendingDateADexEntryAnnouncementExpiresAt = Time.unscaledTime + 1.5f;
            _suppressDateADexOpenEntrySelectionUntil = Time.unscaledTime + DateADexOpenEntryInitialSuppressionSeconds;
        }

        internal static void RememberExaminedObject(ObjectExamine examine)
        {
            if (examine == null)
                return;

            AddExaminedObjectKey(examine.InkNode);
            if (examine.gameObject != null)
                AddExaminedObjectKey(examine.gameObject.name);

            InteractableObj interactable = examine.GetComponentInParent<InteractableObj>();
            if (interactable == null)
                interactable = examine.GetComponentInChildren<InteractableObj>();

            if (interactable == null)
                return;

            AddExaminedObjectKey(interactable.Id);
            AddExaminedObjectKey(interactable.name);
            AddExaminedObjectKey(interactable.InternalName());
            AddExaminedObjectKey(interactable.inkFileName);
        }

        private static void AddExaminedObjectKey(string value)
        {
            string key = BuildComparisonKey(value);
            if (!string.IsNullOrEmpty(key))
                _examinedObjectKeys.Add(key);
        }

        private void Update()
        {
            if (Main.IsShuttingDown)
                return;

            HandleRepeatLastSpeechRequest();
            SimpleNavBridge.Tick();
            HandleNavigationRequests();
            HandleCoverageSweepRequest();
            SimpleNavCoverageSweep.Tick();

            bool isSettingsMenuOpen = ModConfig.IsMenuOpen;
            if (isSettingsMenuOpen)
            {
                ModConfig.Update();
            }
            else if (_isKnownObjectPickerOpen)
            {
                UpdateKnownObjectPicker();
            }
            else
            {
                HandleChoiceKeyboardInput();
            }

            if (Time.unscaledTime < _nextPollTime)
                return;

            _nextPollTime = Time.unscaledTime + 0.1f;
            UpdateSpecsVisibilityState();
            AnnounceScreenSummaryIfNeeded();
            AnnounceRoomIfNeeded();
            AnnounceInteractableIfNeeded();
            AnnounceDateviatorsStateIfNeeded();
            AnnounceDialogueIfNeeded();
            AnnouncePopupIfNeeded();
            AnnounceTutorialIfNeeded();
            AnnounceSubtitleIfNeeded();
            AnnounceEngagementIfNeeded();
            AnnounceLoadingIfNeeded();
            AnnounceExamineIfNeeded();
            AnnounceUIDialogIfNeeded();
            AnnounceSpecsDetailIfNeeded();
            AnnounceCreditsIfNeeded();
            HandlePendingDateADexEntryAnnouncement();
            if (!isSettingsMenuOpen)
            {
                AnnounceSelectionIfNeeded();
            }
            AnnouncePhoneAppContentIfNeeded();
            AnnounceResultScreenIfNeeded();
            AnnounceTimeChangeIfNeeded();
            AnnounceProgressionChangesIfNeeded();
        }

        private void LateUpdate()
        {
            if (Main.IsShuttingDown)
                return;

            UpdateNavigationState();
            ApplyAutoWalk();
            ObjectTracker.UpdateTracking();
        }

        private void HandlePendingDateADexEntryAnnouncement()
        {
            if (Interlocked.CompareExchange(ref _pendingDateADexEntryAnnouncementRequested, 0, 0) == 0)
                return;

            if (Time.unscaledTime < _pendingDateADexEntryAnnouncementNotBefore)
                return;

            if (Time.unscaledTime > _pendingDateADexEntryAnnouncementExpiresAt)
            {
                Interlocked.Exchange(ref _pendingDateADexEntryAnnouncementRequested, 0);
                _pendingDateADexDetailEntry = null;
                return;
            }

            if (!TryBuildDateADexDetailAnnouncement(out string announcement, _pendingDateADexDetailEntry) || string.IsNullOrEmpty(announcement))
                return;

            Interlocked.Exchange(ref _pendingDateADexEntryAnnouncementRequested, 0);
            _pendingDateADexDetailEntry = null;
            _lastDateADexDetail = announcement;
            float openEntrySuppressionSeconds = EstimateSpeechSuppressionSeconds(
                announcement,
                DateADexOpenEntryMinimumSuppressionSeconds,
                DateADexOpenEntryMaximumSuppressionSeconds);
            _suppressDateADexSelectionUntil = Time.unscaledTime + Mathf.Min(1.5f, openEntrySuppressionSeconds);
            _suppressDateADexOpenEntrySelectionUntil = Time.unscaledTime + openEntrySuppressionSeconds;

            if (TryGetCurrentPhoneAppKey(out string contentKey))
            {
                _lastPhoneAppContentKey = contentKey;
                _lastPhoneAppContentAnnouncement = announcement;
            }

            ScreenReader.Say(announcement);
        }

        private static bool ShouldSuppressDateADexOpenEntrySelection(GameObject selectedObject)
        {
            if (selectedObject == null || Time.unscaledTime >= _suppressDateADexOpenEntrySelectionUntil)
                return false;

            if (DateADex.Instance == null || DateADex.Instance.DateADexWindow == null || !DateADex.Instance.DateADexWindow.activeInHierarchy)
                return false;

            bool isEntryVisible = DateADex.Instance.MainEntryScreen != null && DateADex.Instance.MainEntryScreen.activeInHierarchy;
            bool isRecipeVisible = DateADex.Instance.RecipeScreen != null && DateADex.Instance.RecipeScreen.activeInHierarchy;
            if (!isEntryVisible && !isRecipeVisible)
                return false;

            return selectedObject == DateADex.Instance.DateADexWindow ||
                selectedObject.transform.IsChildOf(DateADex.Instance.DateADexWindow.transform);
        }

        private static float EstimateSpeechSuppressionSeconds(string announcement, float minimumSeconds, float maximumSeconds)
        {
            if (string.IsNullOrWhiteSpace(announcement))
                return minimumSeconds;

            string[] words = announcement.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            float estimatedSeconds = (words.Length / EstimatedSpeechWordsPerMinute) * 60f + EstimatedSpeechLeadInSeconds;
            return Mathf.Clamp(estimatedSeconds, minimumSeconds, maximumSeconds);
        }

        private void HandleRepeatLastSpeechRequest()
        {
            if (Interlocked.Exchange(ref _repeatLastSpeechRequested, 0) == 0)
                return;

            Loc.RefreshLanguage();

            if (TrySpeakCurrentRepeatableText())
                return;

            if (ScreenReader.RepeatLastSpoken())
                return;

            ScreenReader.Say(Loc.Get("repeat_last_unavailable"), remember: false);
        }

        private void HandleNavigationRequests()
        {
            if (Interlocked.Exchange(ref _describeCurrentRoomRequested, 0) != 0)
            {
                ScreenReader.Say(Loc.Get("room_scan_unavailable"));
            }

            if (Interlocked.Exchange(ref _selectNavigationTargetRequested, 0) != 0)
            {
                OpenKnownObjectPicker();
            }

            if (Interlocked.Exchange(ref _navigateToObjectiveRequested, 0) != 0)
            {
                StartNavigationToCurrentTarget();
            }

            if (Interlocked.Exchange(ref _autoWalkRequested, 0) != 0)
            {
                ToggleAutoWalk();
            }
        }



        private void HandleCoverageSweepRequest()
        {
            if (Interlocked.Exchange(ref _coverageSweepRequested, 0) == 0)
                return;
            SimpleNavCoverageSweep.RequestToggle();
        }


        private void UpdateNavigationState()
        {
            if (!_isNavigationActive)
            {
                if (ObjectTracker.IsTracking)
                    ObjectTracker.StopTracking();
                return;
            }

            if (!CanUseNavigationNow())
            {
                if (_isAutoWalking)
                {
                    StopNavigationBlocked(
                        "navigation unavailable reason=" + GetNavigationUnavailableReason());
                }
                else
                    StopNavigationRuntime();
                return;
            }

            if (_isAutoWalking && !SimpleNavBridge.HasActiveRoute && IsTrackedObjectReached())
            {
                StopNavigationWithAnnouncement("navigation_arrived");
                return;
            }

            if (!_isAutoWalking &&
                SimpleNavBridge.HasActiveRoute &&
                BetterPlayerControl.Instance != null)
            {
                Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
                Vector3 waypoint = SimpleNavBridge.LastResolvedTarget;
                if (SimpleNavBridge.TryAdvanceWaypoint(playerPos))
                    waypoint = SimpleNavBridge.LastResolvedTarget;
                ObjectTracker.StartTracking(waypoint, requiresInteraction: false);
            }
        }

        // Auto-walk driver. Drives toward the active SimpleNavRoute installed by
        // SimpleNavBridge.BeginRoute, following the polyline and opening doors as path
        // preconditions when a segment has a door tag.
        private void ApplyAutoWalk()
        {
            if (!_isAutoWalking)
                return;

            if (SimpleNavBridge.HasActiveRoute)
            {
                ApplyAutoWalkSimpleRoute();
                return;
            }
        }

        // Route-driven autowalk. Follows SimpleNavBridge.ActiveRoute's polyline.
        // Arrival is "within target's interaction radius" (XZ), not zone-family membership.
        // The connecting door is whichever door is tagged on the current segment, refreshed
        // by SimpleNavBridge.TryAdvanceWaypoint when crossing into a new segment.
        // On arrival, the player turns toward the target object's world position before stop.
        private void ApplyAutoWalkSimpleRoute()
        {
            SimpleNavRoute route = SimpleNavBridge.ActiveRoute;
            if (route == null)
            {
                StopNavigationBlocked("simple-nav route: no active route");
                return;
            }

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            Vector3 playerPos = playerTransform.position;

            // Roll forward through the polyline as the player reaches each leg's end. This also
            // updates the active door (per-segment door tags from the route).
            Vector3 target = SimpleNavBridge.LastResolvedTarget;
            if (SimpleNavBridge.TryAdvanceWaypoint(playerPos))
                target = SimpleNavBridge.LastResolvedTarget;
            if (IsWorldRouteTarget(route) &&
                SimpleNavBridge.ActiveWaypoint != null &&
                SimpleNavBridge.ActiveWaypoint.Kind == SimpleNavWaypointKind.Target)
            {
                target = route.TargetPosition;
            }

            // Drive the audible tone source from the current next-waypoint so the player hears
            // where they're being walked. The legacy executor does this via UpdateNavigationTracker;
            // the route branch has no PathStep, so we call ObjectTracker.StartTracking directly
            // with the route waypoint position. Cheap to call every frame — it's idempotent.
            ObjectTracker.StartTracking(target, requiresInteraction: false);

            // Arrival: within target interaction radius (XZ). The planner already routes to a
            // goal cell inside this disc, so this matches the goal-cell expansion in O4.
            if (SimpleNavBridge.HasArrivedAtRouteTarget(playerPos))
            {
                if (IsWorldRouteTarget(route))
                {
                    ApplyNavigationInput(Vector3.zero, Vector3.zero);
                    StopNavigationWithAnnouncement("navigation_arrived");
                    return;
                }

                if (IsSimpleRouteDoorTargetComplete(route))
                {
                    ApplyNavigationInput(Vector3.zero, Vector3.zero);
                    StopNavigationWithAnnouncement("navigation_arrived");
                    return;
                }

                if (!IsSimpleRouteTargetSelected(route))
                {
                    Vector3 lookPoint = ResolveSimpleRouteTargetLookPoint(route);
                    Vector3 lookInput = GetLookInputTowardRouteTarget(playerTransform, lookPoint);
                    if (lookInput.sqrMagnitude <= 0.0001f)
                    {
                        // If the camera is nominally pointed at the target but the game's
                        // raycast still has not selected it, keep searching instead of
                        // reporting proximity-only arrival.
                        lookInput = new Vector3(0.2f, 0f, 0f);
                    }

                    ApplyNavigationInput(Vector3.zero, lookInput);
                    LogSimpleRouteFrameDiagnostic(route, playerTransform, playerPos, lookPoint, Vector3.zero, lookInput, false);
                    return;
                }

                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                StopNavigationWithAnnouncement("navigation_arrived");
                return;
            }

            // Open the segment's tagged door if needed.
            bool segmentHasDoor = SimpleNavBridge.ActiveDoor != null;
            if (segmentHasDoor)
                SimpleNavBridge.TryOpenActiveDoorIfNeeded(playerPos);

            Vector3 toWaypoint = target - playerPos;
            toWaypoint.y = 0f;
            if (toWaypoint.sqrMagnitude <= 0.0001f)
            {
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                return;
            }

            Vector3 walkDir = toWaypoint.normalized;
            Vector3 localDirection = playerTransform.InverseTransformDirection(walkDir);
            localDirection.y = 0f;
            float turnDeg = Vector3.SignedAngle(playerTransform.forward, walkDir, Vector3.up);

            Vector3 move = new Vector3(
                Mathf.Clamp(localDirection.x, -1f, 1f),
                0f,
                Mathf.Clamp(localDirection.z, -1f, 1f));
            Vector3 look = new Vector3(Mathf.Clamp(turnDeg / AutoWalkLookScaleDegrees, -1f, 1f), 0f, 0f);

            // Hold position while the segment's door is mid-swing. Same reasoning as the step path:
            // walking into a moving door trips Door.OnCollisionEnter and pins the swing.
            bool waitingForDoorSwing = segmentHasDoor && SimpleNavBridge.IsActiveDoorMoving();
            if (waitingForDoorSwing)
                move = Vector3.zero;

            if (!ApplyNavigationInput(move, look))
            {
                StopNavigationBlocked("simple-nav route input application failed target=" + (route.TargetName ?? "<null>"));
                return;
            }

            LogSimpleRouteFrameDiagnostic(route, playerTransform, playerPos, target, move, look, waitingForDoorSwing);

            SimpleNavBridge.RecordFrameProgress(playerPos);

            if (waitingForDoorSwing)
            {
                _lastAutoWalkProgressTime = Time.unscaledTime;
                return;
            }

            if (Vector3.Distance(playerPos, _lastAutoWalkPosition) >= AutoWalkProgressDistance)
            {
                _lastAutoWalkPosition = playerPos;
                _lastAutoWalkProgressTime = Time.unscaledTime;
                ClearNavigationBlockedDetail();
            }
            else if (Time.unscaledTime - _lastAutoWalkProgressTime >= AutoWalkBlockedTimeoutSeconds)
            {
                string runtimeBlocker = ProbeRuntimeBlocker(playerPos, target);
                LogNavigationAutoWalkDebug(
                    "Simple-nav route progress timeout target=" + (route.TargetName ?? "<null>") +
                    " waypoint=" + FormatVector3(target) +
                    " player=" + FormatVector3(playerPos) +
                    " runtimeBlocker=" + (runtimeBlocker ?? "<none>"));
                StopNavigationBlocked("simple-nav route progress timeout target=" + (route.TargetName ?? "<null>") +
                    " runtimeBlocker=" + (runtimeBlocker ?? "<none>"));
            }
        }

        private static bool IsSimpleRouteTargetSelected(SimpleNavRoute route)
        {
            if (route == null)
                return false;

            InteractableManager manager = Singleton<InteractableManager>.Instance;
            if (manager == null || manager.activeObject == null || !manager.IsPlayerInRange)
                return false;

            return IsSameOrRelatedSimpleRouteTarget(route, manager.activeObject.gameObject);
        }

        private static bool IsWorldRouteTarget(SimpleNavRoute route)
        {
            return route != null && route.TargetGameObjectId == 0;
        }

        private static bool IsSameOrRelatedSimpleRouteTarget(SimpleNavRoute route, GameObject activeObject)
        {
            if (route == null || activeObject == null)
                return false;

            if (activeObject.GetInstanceID() == route.TargetGameObjectId)
                return true;

            GameObject routeTarget = FindSimpleRouteTargetObject(route);
            if (routeTarget == null)
                return false;

            Transform activeTransform = activeObject.transform;
            Transform targetTransform = routeTarget.transform;
            return IsSameOrRelatedInteractableTarget(targetTransform.gameObject, activeTransform.gameObject);
        }

        private static bool IsSameOrRelatedInteractableTarget(GameObject targetObject, GameObject activeObject)
        {
            if (targetObject == null || activeObject == null)
                return false;

            if (activeObject.GetInstanceID() == targetObject.GetInstanceID())
                return true;

            Transform activeTransform = activeObject.transform;
            Transform targetTransform = targetObject.transform;
            return activeTransform.IsChildOf(targetTransform) || targetTransform.IsChildOf(activeTransform);
        }

        private static Vector3 ResolveSimpleRouteTargetLookPoint(SimpleNavRoute route)
        {
            if (route == null)
                return Vector3.zero;

            GameObject targetObject = FindSimpleRouteTargetObject(route);
            if (targetObject == null)
                return route.TargetPosition;

            Collider collider = targetObject.GetComponent<Collider>();
            if (collider == null)
                collider = targetObject.GetComponentInChildren<Collider>();

            if (collider == null)
                return targetObject.transform.position;

            Camera cam = Camera.main;
            if (cam != null)
                return collider.ClosestPointOnBounds(cam.transform.position);

            return collider.bounds.center;
        }

        private static GameObject FindSimpleRouteTargetObject(SimpleNavRoute route)
        {
            if (route == null || route.TargetGameObjectId == 0)
                return null;

            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj interactable = interactables[i];
                if (interactable == null || interactable.gameObject == null)
                    continue;

                if (interactable.gameObject.GetInstanceID() == route.TargetGameObjectId)
                    return interactable.gameObject;
            }

            return null;
        }

        private static bool IsSimpleRouteDoorTargetComplete(SimpleNavRoute route)
        {
            Door door = FindSimpleRouteTargetDoor(route);
            if (door == null)
                return false;

            if (door.open && !SimpleNavBridge.IsDoorMoving(door))
                return true;

            return IsSimpleRouteTargetSelected(route);
        }

        private static Door FindSimpleRouteTargetDoor(SimpleNavRoute route)
        {
            if (route == null || route.TargetGameObjectId == 0)
                return null;

            Door[] doors = FindObjectsOfType<Door>();
            for (int i = 0; i < doors.Length; i++)
            {
                Door door = doors[i];
                if (door == null || door.gameObject == null)
                    continue;

                if (door.gameObject.GetInstanceID() == route.TargetGameObjectId)
                    return door;
            }

            return null;
        }

        private static Vector3 GetLookInputTowardRouteTarget(Transform playerTransform, Vector3 targetPosition)
        {
            Vector3 toTarget = targetPosition - playerTransform.position;
            Vector3 flatTarget = toTarget;
            flatTarget.y = 0f;

            float yaw = 0f;
            if (flatTarget.sqrMagnitude > 0.0001f)
            {
                yaw = Mathf.Clamp(
                    Vector3.SignedAngle(playerTransform.forward, flatTarget.normalized, Vector3.up) /
                    AutoWalkLookScaleDegrees,
                    -1f,
                    1f);
            }

            float pitch = 0f;
            Camera cam = Camera.main;
            if (cam != null && toTarget.sqrMagnitude > 0.0001f)
            {
                Vector3 cameraLocalTarget = cam.transform.InverseTransformDirection(targetPosition - cam.transform.position);
                if (cameraLocalTarget.sqrMagnitude > 0.0001f)
                {
                    float forward = Mathf.Max(0.001f, new Vector2(cameraLocalTarget.x, cameraLocalTarget.z).magnitude);
                    float pitchDegrees = Mathf.Atan2(cameraLocalTarget.y, forward) * Mathf.Rad2Deg;
                    pitch = Mathf.Clamp(pitchDegrees / AutoWalkLookScaleDegrees, -1f, 1f);
                }
            }

            if (Mathf.Abs(yaw) < 0.05f) yaw = 0f;
            if (Mathf.Abs(pitch) < 0.05f) pitch = 0f;
            return new Vector3(yaw, 0f, pitch);
        }

        private void LogSimpleRouteFrameDiagnostic(
            SimpleNavRoute route,
            Transform playerTransform,
            Vector3 playerPos,
            Vector3 waypoint,
            Vector3 move,
            Vector3 look,
            bool waitingForDoorSwing)
        {
            if (Time.unscaledTime < _nextSimpleRouteDiagnosticTime)
                return;

            _nextSimpleRouteDiagnosticTime = Time.unscaledTime + 1.0f;

            Rigidbody rb = playerTransform != null ? playerTransform.GetComponent<Rigidbody>() : null;
            Vector3 velocity = rb != null ? rb.velocity : Vector3.zero;
            Vector3 reflectedMove = Vector3.zero;
            Vector3 reflectedLook = Vector3.zero;
            bool reflectedOk = false;

            try
            {
                EnsureReflectionCache();
                if (_betterPlayerControlMoveField != null && _betterPlayerControlLookField != null && BetterPlayerControl.Instance != null)
                {
                    object rawMove = _betterPlayerControlMoveField.GetValue(BetterPlayerControl.Instance);
                    object rawLook = _betterPlayerControlLookField.GetValue(BetterPlayerControl.Instance);
                    if (rawMove is Vector3 rm && rawLook is Vector3 rl)
                    {
                        reflectedMove = rm;
                        reflectedLook = rl;
                        reflectedOk = true;
                    }
                }
            }
            catch
            {
                reflectedOk = false;
            }

            InteractableManager manager = Singleton<InteractableManager>.Instance;
            string activeName = "<none>";
            bool inRange = false;
            if (manager != null)
            {
                inRange = manager.IsPlayerInRange;
                if (manager.activeObject != null && manager.activeObject.gameObject != null)
                    activeName = manager.activeObject.gameObject.name + "#" + manager.activeObject.gameObject.GetInstanceID();
            }

            string state = BetterPlayerControl.Instance != null ? BetterPlayerControl.Instance.STATE.ToString() : "<no player>";
            string view = Singleton<GameController>.Instance != null ? Singleton<GameController>.Instance.viewState.ToString() : "<no game controller>";
            float waypointDistance = GetFlatDistance(playerPos, waypoint);
            float targetDistance = route != null ? GetFlatDistance(playerPos, route.TargetPosition) : 0f;
            SimpleNavWaypoint activeWaypoint = SimpleNavBridge.ActiveWaypoint;

            if (Main.Log != null)
            {
                Main.Log.LogInfo("SimpleNav route frame target=" + (route != null ? route.TargetName : "<null>") +
                    " player=" + FormatVector3(playerPos) +
                    " waypoint=" + FormatVector3(waypoint) +
                    " waypointKind=" + (activeWaypoint != null ? activeWaypoint.Kind.ToString() : "<none>") +
                    " waypointDoor=" + (activeWaypoint != null && !string.IsNullOrEmpty(activeWaypoint.DoorName) ? activeWaypoint.DoorName : "<none>") +
                    " waypointDist=" + waypointDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " targetDist=" + targetDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " moveCmd=" + FormatVector3(move) +
                    " lookCmd=" + FormatVector3(look) +
                    " reflected=" + (reflectedOk ? FormatVector3(reflectedMove) + "/" + FormatVector3(reflectedLook) : "<unavailable>") +
                    " velocity=" + FormatVector3(velocity) +
                    " state=" + state +
                    " view=" + view +
                    " active=" + activeName +
                    " inRange=" + inRange +
                    " waitingDoor=" + waitingForDoorSwing);
            }
        }

        // Probe what's physically between the player and the active waypoint at runtime. Casts
        // at two heights because doorframe sills and door-bottom colliders sit below the chest
        // height the player capsule normally encounters. Returns "chest=<a> ankle=<b>" with
        // either side filled with "<clear>" when that probe is unblocked, or null if both clear.
        private static string ProbeRuntimeBlocker(Vector3 playerPos, Vector3 target)
        {
            Vector3 toward = target - playerPos;
            toward.y = 0f;
            float dist = toward.magnitude;
            if (dist < 0.1f) return null;
            Vector3 dir = toward / dist;
            float castDist = dist + 0.5f;

            string chestLabel = ProbeOne(playerPos + new Vector3(0f, 1.0f, 0f), dir, castDist);
            string ankleLabel = ProbeOne(playerPos + new Vector3(0f, 0.2f, 0f), dir, castDist);
            if (chestLabel == null && ankleLabel == null) return null;
            return "chest=" + (chestLabel ?? "<clear>") + " ankle=" + (ankleLabel ?? "<clear>");
        }

        private static string ProbeOne(Vector3 origin, Vector3 dir, float maxDist)
        {
            if (!Physics.SphereCast(origin, 0.35f, dir, out RaycastHit hit, maxDist,
                    Physics.AllLayers, QueryTriggerInteraction.Ignore))
                return null;
            string name = hit.collider != null && hit.collider.gameObject != null
                ? hit.collider.gameObject.name
                : "<unknown>";
            int layer = hit.collider != null ? hit.collider.gameObject.layer : -1;
            return name + " layer=" + layer + " dist=" + hit.distance.ToString("0.00");
        }


        private bool BeginNavigation(string targetZone, string targetLabel, bool announceFailure = true)
        {
            _navigationTargetZone = targetZone;
            _navigationTargetLabel = targetLabel;
            LogNavigationTargetDebug(
                "BeginNavigation targetZone=" + (_navigationTargetZone ?? "<null>") +
                " targetLabel=" + (_navigationTargetLabel ?? "<null>") +
                " autoWalk=" + _isAutoWalking);

            if (!CanUseNavigationNow())
            {
                LogNavigationAutoWalkDebug("BeginNavigation blocked reason=" + GetNavigationUnavailableReason());
                SetNavigationBlockedDetail("begin navigation unavailable reason=" + GetNavigationUnavailableReason());
                if (announceFailure)
                    StopNavigationBlocked();
                else
                    StopNavigationRuntime();
                return false;
            }

            ResetAutoWalkProgress();
            _nextSimpleRouteDiagnosticTime = 0f;
            _isNavigationActive = true;
            return true;
        }

        private void StopNavigationWithAnnouncement(string messageKey)
        {
            if (string.Equals(messageKey, "navigation_blocked", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_lastNavigationBlockedDetail))
            {
                LogNavigationAutoWalkDebug("StopNavigationWithAnnouncement blocked detail=" + _lastNavigationBlockedDetail);
            }

            StopNavigationRuntime();
            ScreenReader.Say(Loc.Get(messageKey));
        }

        private void StopNavigationRuntime()
        {
            LogNavigationAutoWalkDebug(
                "StopNavigationRuntime targetZone=" + (_navigationTargetZone ?? "<null>") +
                " targetLabel=" + (_navigationTargetLabel ?? "<null>") +
                " autoWalk=" + _isAutoWalking);
            _isNavigationActive = false;
            _isAutoWalking = false;
            _lastAutoWalkProgressTime = 0f;
            _nextSimpleRouteDiagnosticTime = 0f;
            _lastNavigationTargetDebugSnapshot = null;
            _lastNavigationAutoWalkDebugSnapshot = null;
            _lastNavigationTransitionDebugSnapshot = null;
            _lastNavigationBlockedDetail = null;
            ClearNavigationWorldTarget();
            ObjectTracker.StopTracking();
            SimpleNavBridge.EndStep();
            ApplyNavigationInput(Vector3.zero, Vector3.zero);
        }

        private void SetNavigationBlockedDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail) ||
                string.Equals(_lastNavigationBlockedDetail, detail, StringComparison.Ordinal))
            {
                return;
            }

            _lastNavigationBlockedDetail = detail;
            LogNavigationAutoWalkDebug("Navigation blocked detail=" + detail);
        }

        private void ClearNavigationBlockedDetail()
        {
            _lastNavigationBlockedDetail = null;
        }

        private void StopNavigationBlocked(string detail = null)
        {
            if (!string.IsNullOrWhiteSpace(detail))
                SetNavigationBlockedDetail(detail);

            StopNavigationWithAnnouncement("navigation_blocked");
        }

        private bool TryEnsureNavigationTarget(out string targetZone, out string targetLabel)
        {
            if (_hasNavigationWorldTarget)
            {
                targetZone = _navigationTargetZone;
                targetLabel = _navigationTargetLabel;
                LogNavigationTargetDebug(
                    "Navigation target source=stored world target=" + _navigationWorldTarget +
                    " label=" + (targetLabel ?? "<null>"));
                return true;
            }

            if (TryGetTrackedInteractable(out InteractableObj trackedInteractable) &&
                TryGetTrackedInteractableZone(trackedInteractable, out targetZone))
            {
                _trackedInteractableZone = targetZone;
                targetLabel = GetTrackedInteractableLabel(trackedInteractable);
                _navigationTargetZone = targetZone;
                _navigationTargetLabel = targetLabel;
                LogNavigationTargetDebug(
                    "Navigation target source=tracked interactable=" + DescribeInteractable(trackedInteractable) +
                    " zone=" + targetZone +
                    " label=" + (targetLabel ?? "<null>"));
                return true;
            }

            targetZone = _navigationTargetZone;
            targetLabel = _navigationTargetLabel;
            if (!string.IsNullOrEmpty(targetZone))
            {
                if (string.IsNullOrEmpty(targetLabel))
                    targetLabel = BuildNavigationTargetLabel(targetZone, GetCurrentZoneNameInternal());
                LogNavigationTargetDebug(
                    "Navigation target source=stored zone=" + targetZone +
                    " label=" + (targetLabel ?? "<null>"));
                return true;
            }

            if (TryResolveCurrentObjectiveWorldTarget(out targetZone, out targetLabel))
            {
                LogNavigationTargetDebug(
                    "Navigation target source=objective world target=" + _navigationWorldTarget +
                    " label=" + (targetLabel ?? "<null>"));
                return true;
            }

            if (TryResolveCurrentObjectiveInteractable(out InteractableObj objectiveInteractable, out targetZone, out targetLabel))
            {
                SetTrackedInteractable(objectiveInteractable, targetZone, targetLabel);
                LogNavigationTargetDebug(
                    "Navigation target source=objective interactable=" + DescribeInteractable(objectiveInteractable) +
                    " zone=" + targetZone +
                    " label=" + (targetLabel ?? "<null>"));
                return true;
            }

            LogNavigationTargetDebug("Navigation target source=none");
            return false;
        }

        private bool TryResolveCurrentObjectiveInteractable(out InteractableObj interactable, out string targetZone, out string targetLabel)
        {
            interactable = null;
            targetZone = null;
            targetLabel = null;
            string objectiveText = null;
            TryGetCurrentTutorialObjectiveText(out objectiveText);
            string hallwayFallbackLabel = null;

            if (!TryResolveTutorialObjectiveKind(out TutorialObjectiveKind objectiveKind) ||
                objectiveKind == TutorialObjectiveKind.None)
            {
                if (TryResolveCurrentRoomersEntryInteractable(out interactable, out targetZone, out targetLabel))
                {
                    DebugLogger.Log(
                        LogCategory.State,
                        "AccessibilityWatcher",
                        "Objective resolve success: source=Roomers fallback" +
                        " signpostText=" + (objectiveText ?? "<null>") +
                        " label=" + (targetLabel ?? "<null>") +
                        " zone=" + (targetZone ?? "<null>") +
                        " interactable=" + DescribeInteractable(interactable));
                    return !string.IsNullOrEmpty(targetLabel);
                }

                DebugLogger.Log(LogCategory.State, "AccessibilityWatcher", "Objective resolve failed: no tutorial objective kind. signpostText=" + (objectiveText ?? "<null>"));
                return false;
            }

            if (objectiveKind == TutorialObjectiveKind.FrontDoor)
            {
                if (TryResolveTutorialGiftBoxInteractable(out InteractableObj giftBoxInteractable) &&
                    TryResolveNavigableInteractable(giftBoxInteractable, out InteractableObj resolvedGiftBox, out targetZone))
                {
                    interactable = resolvedGiftBox;
                }
                else
                {
                    DebugLogger.Log(
                        LogCategory.State,
                        "AccessibilityWatcher",
                        "Objective resolve failed: objectiveKind=" + objectiveKind +
                        " signpostText=" + (objectiveText ?? "<null>") +
                        " reason=gift box is not active or not navigable");
                    return false;
                }
            }
            else if (!TryFindTutorialObjectiveInteractable(objectiveKind, out interactable) ||
                interactable == null)
            {
                DebugLogger.Log(
                    LogCategory.State,
                    "AccessibilityWatcher",
                    "Objective resolve failed: objectiveKind=" + objectiveKind +
                    " signpostText=" + (objectiveText ?? "<null>") +
                    " interactable=" + (interactable != null ? interactable.name : "<null>"));
                return false;
            }

            if (!TryResolveNavigableInteractable(interactable, out InteractableObj resolvedInteractable, out targetZone))
            {
                DebugLogger.Log(
                    LogCategory.State,
                    "AccessibilityWatcher",
                    "Objective resolve failed: objectiveKind=" + objectiveKind +
                    " signpostText=" + (objectiveText ?? "<null>") +
                    " interactable=" + interactable.name +
                    " reason=no navigable zone");
                return false;
            }

            interactable = resolvedInteractable;

            targetLabel = !string.IsNullOrEmpty(hallwayFallbackLabel)
                ? hallwayFallbackLabel
                : GetTrackedInteractableLabel(interactable);
            DebugLogger.Log(
                LogCategory.State,
                "AccessibilityWatcher",
                "Objective resolve success: objectiveKind=" + objectiveKind +
                " signpostText=" + (objectiveText ?? "<null>") +
                " label=" + (targetLabel ?? "<null>") +
                " zone=" + targetZone +
                " interactable=" + DescribeInteractable(interactable));
            return !string.IsNullOrEmpty(targetLabel);
        }

        private bool TryResolveCurrentObjectiveWorldTarget(out string targetZone, out string targetLabel)
        {
            targetZone = null;
            targetLabel = null;
            string objectiveText = null;
            TryGetCurrentTutorialObjectiveText(out objectiveText);

            if (!TryResolveTutorialObjectiveKind(out TutorialObjectiveKind objectiveKind))
            {
                return false;
            }

            if (objectiveKind == TutorialObjectiveKind.FrontDoor)
            {
                DebugLogger.Log(
                    LogCategory.State,
                    "AccessibilityWatcher",
                    "Objective world-target skipped: objectiveKind=" + objectiveKind +
                    " signpostText=" + (objectiveText ?? "<null>") +
                    " reason=front-door delivery uses package or hallway object fallback, not coordinate fallback");
                return false;
            }

            if (objectiveKind != TutorialObjectiveKind.OfficeExit)
                return false;

            if (!TryResolveActiveDroneTriggerWorldTarget(out Vector3 target, out float radius))
            {
                DebugLogger.Log(
                    LogCategory.State,
                    "AccessibilityWatcher",
                    "Objective resolve failed: objectiveKind=" + objectiveKind +
                    " signpostText=" + (objectiveText ?? "<null>") +
                    " reason=no active TutorialTriggerZone.EventType.TriggerDrone");
                return false;
            }

            SetNavigationWorldTarget(target, radius, Loc.Get("navigation_tutorial_gift_delivery_trigger"));
            targetZone = _navigationTargetZone;
            targetLabel = _navigationTargetLabel;
            DebugLogger.Log(
                LogCategory.State,
                "AccessibilityWatcher",
                "Objective resolve success: objectiveKind=" + objectiveKind +
                " signpostText=" + (objectiveText ?? "<null>") +
                " label=" + (targetLabel ?? "<null>") +
                " worldTarget=" + _navigationWorldTarget);
            return true;
        }

        private static bool TryResolveTutorialObjectiveKind(out TutorialObjectiveKind objectiveKind)
        {
            objectiveKind = TutorialObjectiveKind.None;

            if (TryResolveTutorialObjectiveKindFromSignpostText(out objectiveKind) &&
                objectiveKind != TutorialObjectiveKind.None)
            {
                return true;
            }

            Save save = Singleton<Save>.Instance;
            if (save == null)
                return false;

            bool sawIntroAnimations = save.GetTutorialThresholdState(TutorialController.TUTORIAL_STATE_0_ANIMATIONS);
            bool wentToWork = save.GetTutorialThresholdState(TutorialController.TUTORIAL_STATE_1_WENT_TO_WORK);
            bool sawThiscord = save.GetTutorialThresholdState(TutorialController.TUTORIAL_STATE_2_SAW_THISCORD);
            bool wokeUpDayTwo = save.GetTutorialThresholdState(TutorialController.TUTORIAL_STATE_3_WOKE_UP_DAY_TWO);
            bool isDeluxe = save.AvailableTotalDatables() > 100;
            int realizedTargetCount = isDeluxe ? 101 : 99;
            int endingTargetCount = isDeluxe ? 101 : 99;
            int finalExitEndingCount = isDeluxe ? 102 : 100;

            if (sawIntroAnimations && !wentToWork)
            {
                objectiveKind = TutorialObjectiveKind.Computer;
                return true;
            }

            if (wentToWork && !sawThiscord)
            {
                if (Singleton<PhoneManager>.Instance != null && Singleton<PhoneManager>.Instance.HasNewMessageAlert())
                {
                    objectiveKind = TutorialObjectiveKind.Phone;
                    return true;
                }

                return false;
            }

            if (sawThiscord && save.GetDateStatus("skylar_specs") == RelationshipStatus.Unmet)
            {
                objectiveKind = TutorialObjectiveKind.FrontDoor;
                return true;
            }

            if (sawThiscord && save.GetDateStatus("dorian_door") == RelationshipStatus.Unmet)
            {
                if (Singleton<Dateviators>.Instance == null || !Singleton<Dateviators>.Instance.Equipped)
                    return false;

                objectiveKind = TutorialObjectiveKind.Dorian;
                return true;
            }

            if (sawThiscord && save.GetDateStatus("phoenicia_phone") == RelationshipStatus.Unmet)
            {
                objectiveKind = TutorialObjectiveKind.Phone;
                return true;
            }

            if (sawThiscord && save.GetDateStatus("maggie_mglass") == RelationshipStatus.Unmet)
            {
                objectiveKind = TutorialObjectiveKind.Maggie;
                return true;
            }

            if (sawThiscord && save.GetDateStatus("betty_bed") == RelationshipStatus.Unmet)
            {
                objectiveKind = TutorialObjectiveKind.Bed;
                return true;
            }

            if (sawThiscord && !wokeUpDayTwo)
            {
                objectiveKind = TutorialObjectiveKind.Bed;
                return true;
            }

            if (!wokeUpDayTwo)
                return false;

            if (!GetInkVariableBool("skylar_where"))
            {
                objectiveKind = TutorialObjectiveKind.AnyUnmetDatable;
                return true;
            }

            if (save.AvailableTotalMetDatables() < 10)
            {
                objectiveKind = TutorialObjectiveKind.Maggie;
                return true;
            }

            if (save.GetRoomersFound().Count > 5)
            {
                objectiveKind = TutorialObjectiveKind.AnyUnmetDatable;
                return true;
            }

            string realizeSkylarState = GetInkVariableString("realize_skylar_asap");
            if (save.AvailableTotalMetDatables() >= 48 &&
                save.AvailableTotalRealizedDatables() == 0 &&
                string.Equals(realizeSkylarState, "on", StringComparison.OrdinalIgnoreCase))
            {
                objectiveKind = TutorialObjectiveKind.Skylar;
                return true;
            }

            if (save.AvailableTotalMetDatables() >= 48 &&
                save.AvailableTotalRealizedDatables() == realizedTargetCount)
            {
                objectiveKind = TutorialObjectiveKind.Skylar;
                return true;
            }

            if (save.GetDateStatus("reggie") == RelationshipStatus.Unmet &&
                save.AvailableTotalLoveEndings() == endingTargetCount)
            {
                objectiveKind = TutorialObjectiveKind.Skylar;
                return true;
            }

            if (save.GetDateStatus("reggie") == RelationshipStatus.Unmet &&
                save.GetDateStatusRealized("dorian") != RelationshipStatus.Realized &&
                save.AvailableTotalFriendEndings() == endingTargetCount)
            {
                objectiveKind = TutorialObjectiveKind.Dorian;
                return true;
            }

            if (save.AvailableTotalHateEndings() == finalExitEndingCount ||
                save.AvailableTotalRealizedDatables() == finalExitEndingCount)
            {
                objectiveKind = TutorialObjectiveKind.HouseExit;
                return true;
            }

            if (save.AvailableTotalMetDatables() >= 48 &&
                string.Equals(realizeSkylarState, "complete", StringComparison.OrdinalIgnoreCase))
            {
                objectiveKind = TutorialObjectiveKind.AnyUnrealizedDatable;
                return true;
            }

            return false;
        }

        private static bool TryResolveTutorialObjectiveKindFromSignpostText(out TutorialObjectiveKind objectiveKind)
        {
            objectiveKind = TutorialObjectiveKind.None;

            if (!TryGetCurrentTutorialObjectiveText(out string objectiveText))
                return false;

            if (ContainsToken(objectiveText, "start your new job at your computer"))
            {
                objectiveKind = TutorialObjectiveKind.Computer;
                return true;
            }

            if (ContainsToken(objectiveText, "leave the office and reflect on your life choices") ||
                ContainsToken(objectiveText, "leave the office and contemplate your life choices"))
            {
                objectiveKind = TutorialObjectiveKind.OfficeExit;
                return true;
            }

            if (ContainsToken(objectiveText, "check the message on your phone") ||
                ContainsToken(objectiveText, "awaken your phone"))
            {
                objectiveKind = TutorialObjectiveKind.Phone;
                return true;
            }

            if (ContainsToken(objectiveText, "check the delivery at the front door"))
            {
                objectiveKind = TutorialObjectiveKind.FrontDoor;
                return true;
            }

            if (ContainsToken(objectiveText, "awaken a door") ||
                ContainsToken(objectiveText, "talk to dorian"))
            {
                objectiveKind = TutorialObjectiveKind.Dorian;
                return true;
            }

            if (ContainsToken(objectiveText, "locate the magnifying glass") ||
                ContainsToken(objectiveText, "speak with maggie"))
            {
                objectiveKind = TutorialObjectiveKind.Maggie;
                return true;
            }

            if (ContainsToken(objectiveText, "follow the clue in roomers") ||
                ContainsToken(objectiveText, "charge the dateviators by going to sleep"))
            {
                objectiveKind = TutorialObjectiveKind.Bed;
                return true;
            }

            if (ContainsToken(objectiveText, "talk to skylar specs") ||
                ContainsToken(objectiveText, "realize skylar specs"))
            {
                objectiveKind = TutorialObjectiveKind.Skylar;
                return true;
            }

            if (ContainsToken(objectiveText, "continue to awaken dateable objects"))
            {
                objectiveKind = TutorialObjectiveKind.AnyUnmetDatable;
                return true;
            }

            if (ContainsToken(objectiveText, "realize dateable objects"))
            {
                objectiveKind = TutorialObjectiveKind.AnyUnrealizedDatable;
                return true;
            }

            if (ContainsToken(objectiveText, "leave your home to return the dateviators") ||
                ContainsToken(objectiveText, "leave your home to see your effects on the world"))
            {
                objectiveKind = TutorialObjectiveKind.HouseExit;
                return true;
            }

            return false;
        }

        private static bool TryFindTutorialObjectiveInteractable(TutorialObjectiveKind objectiveKind, out InteractableObj interactable)
        {
            interactable = null;

            if (objectiveKind == TutorialObjectiveKind.AnyUnmetDatable)
                return TryFindNearestDateableInteractable(requireUnmet: true, requireUnrealized: false, out interactable);

            if (objectiveKind == TutorialObjectiveKind.AnyUnrealizedDatable)
                return TryFindNearestDateableInteractable(requireUnmet: false, requireUnrealized: true, out interactable);

            if (TryResolveTutorialObjectiveAnchorInteractable(objectiveKind, out interactable))
                return true;

            if (objectiveKind == TutorialObjectiveKind.Computer &&
                TryFindNearestComputerInteractable(out interactable))
            {
                return true;
            }

            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            float bestScore = float.MinValue;
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                float score = ScoreTutorialObjectiveInteractable(objectiveKind, candidate);
                if (score <= 0f || score <= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static bool TryResolveCurrentRoomersEntryInteractable(out InteractableObj interactable, out string targetZone, out string targetLabel)
        {
            interactable = null;
            targetZone = null;
            targetLabel = null;

            if (!TryGetCurrentRoomersEntry(out Save.RoomersStruct entry) ||
                !TryFindRoomersEntryInteractable(entry, out InteractableObj candidate))
            {
                return false;
            }

            if (!TryResolveNavigableInteractable(candidate, out InteractableObj resolvedInteractable, out targetZone))
                return false;

            interactable = resolvedInteractable;
            targetLabel = BuildRoomersEntryNavigationLabel(entry, interactable);
            return !string.IsNullOrEmpty(targetLabel);
        }

        private static bool TryGetCurrentRoomersEntry(out Save.RoomersStruct entry)
        {
            entry = null;

            if (Roomers.Instance == null ||
                Roomers.Instance.RoomersWindow == null ||
                !Roomers.Instance.RoomersWindow.activeInHierarchy)
            {
                return false;
            }

            GameObject selectedObject = GetCurrentSelectedObject();
            RoomersEntryButton selectedEntryButton = selectedObject != null
                ? selectedObject.GetComponentInParent<RoomersEntryButton>()
                : null;
            if (selectedEntryButton != null && selectedEntryButton.roomersEntry != null)
            {
                entry = selectedEntryButton.roomersEntry;
                return true;
            }

            EnsureReflectionCache();
            if (_roomersCurrentEntryField == null || _roomersEntriesField == null)
                return false;

            int currentEntry = (int)_roomersCurrentEntryField.GetValue(Roomers.Instance);
            List<GameObject> entries = _roomersEntriesField.GetValue(Roomers.Instance) as List<GameObject>;
            if (entries == null || currentEntry < 0 || currentEntry >= entries.Count || entries[currentEntry] == null)
                return false;

            RoomersEntryButton currentEntryButton = entries[currentEntry].GetComponent<RoomersEntryButton>();
            entry = currentEntryButton != null ? currentEntryButton.roomersEntry : null;
            return entry != null;
        }

        private static bool TryFindRoomersEntryInteractable(Save.RoomersStruct entry, out InteractableObj interactable)
        {
            interactable = null;
            if (entry == null)
                return false;

            string entryInternalKey = BuildComparisonKey(entry.character);
            string entryNameKey = BuildComparisonKey(GetRoomersCharacterDisplayName(entry.character));
            string entryObjectKey = BuildComparisonKey(GetRoomersCharacterObjectName(entry.character));
            if (string.IsNullOrEmpty(entryInternalKey) &&
                string.IsNullOrEmpty(entryNameKey) &&
                string.IsNullOrEmpty(entryObjectKey))
            {
                return false;
            }

            Vector3 playerPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            float bestScore = float.MinValue;
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                float score = ScoreRoomersEntryInteractable(entryInternalKey, entryNameKey, entryObjectKey, candidate);
                if (score <= 0f)
                    continue;

                score -= GetFlatDistance(playerPosition, GetInteractablePlanningPosition(candidate));
                if (score <= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static float ScoreRoomersEntryInteractable(string entryInternalKey, string entryNameKey, string entryObjectKey, InteractableObj interactable)
        {
            string candidateInternalKey = BuildComparisonKey(interactable.InternalName());
            string candidateInkKey = BuildComparisonKey(interactable.inkFileName);
            string candidateLabelKey = BuildComparisonKey(GetObjectFacingDisplayName(interactable));
            string candidateObjectKey = BuildComparisonKey(interactable.name);
            float score = 0f;

            score += ScoreRoomersKeyMatch(entryInternalKey, candidateInternalKey, 5000f);
            score += ScoreRoomersKeyMatch(entryInternalKey, candidateInkKey, 4000f);
            score += ScoreRoomersKeyMatch(entryNameKey, candidateLabelKey, 1500f);
            score += ScoreRoomersKeyMatch(entryNameKey, candidateObjectKey, 800f);
            score += ScoreRoomersKeyMatch(entryObjectKey, candidateLabelKey, 1200f);
            score += ScoreRoomersKeyMatch(entryObjectKey, candidateObjectKey, 700f);
            if (!string.IsNullOrEmpty(interactable.inkFileName))
                score += 100f;

            return score;
        }

        private static float ScoreRoomersKeyMatch(string entryKey, string candidateKey, float exactScore)
        {
            if (string.IsNullOrEmpty(entryKey) || string.IsNullOrEmpty(candidateKey))
                return 0f;

            if (string.Equals(candidateKey, entryKey, StringComparison.OrdinalIgnoreCase))
                return exactScore;

            if (candidateKey.StartsWith(entryKey, StringComparison.OrdinalIgnoreCase) ||
                entryKey.StartsWith(candidateKey, StringComparison.OrdinalIgnoreCase))
            {
                return exactScore * 0.75f;
            }

            if (candidateKey.IndexOf(entryKey, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entryKey.IndexOf(candidateKey, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return exactScore * 0.5f;
            }

            return 0f;
        }

        private static string BuildRoomersEntryNavigationLabel(Save.RoomersStruct entry, InteractableObj interactable)
        {
            string entryName = NormalizeIdentifierName(entry != null ? GetRoomersCharacterDisplayName(entry.character) : null);
            string entryObject = NormalizeIdentifierName(entry != null ? GetRoomersCharacterObjectName(entry.character) : null);
            if (!string.IsNullOrEmpty(entryName) && !string.IsNullOrEmpty(entryObject))
                return entryName + ", " + entryObject;

            if (!string.IsNullOrEmpty(entryName))
                return entryName;

            string label = GetObjectFacingDisplayName(interactable);
            if (!string.IsNullOrEmpty(label))
                return label;

            return NormalizeIdentifierName(entry != null ? entry.character : null);
        }

        private static string GetRoomersCharacterDisplayName(string internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName))
                return null;

            if (Singleton<Save>.Instance != null &&
                Singleton<Save>.Instance.TryGetNameByInternalName(internalName, out string displayName) &&
                !string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            DateADexEntry entry = DateADex.Instance != null ? DateADex.Instance.GetEntry(internalName) : null;
            return entry != null ? entry.CharName : internalName;
        }

        private static string GetRoomersCharacterObjectName(string internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName) || DateADex.Instance == null)
                return null;

            DateADexEntry entry = DateADex.Instance.GetEntry(internalName);
            if (entry == null && string.Equals(internalName, "curt", StringComparison.OrdinalIgnoreCase))
                entry = DateADex.Instance.GetEntry("curtrod");

            return entry != null ? entry.CharObj : null;
        }

        private static string BuildComparisonKey(string value)
        {
            value = NormalizeIdentifierName(value);
            if (string.IsNullOrEmpty(value))
                return null;

            return Regex.Replace(value, "[^A-Za-z0-9]", "").ToLowerInvariant();
        }

        private static bool TryFindNearestComputerInteractable(out InteractableObj interactable)
        {
            interactable = null;
            Vector3 playerPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            float bestScore = float.MinValue;

            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                if (!IsComputerInteractable(candidate))
                    continue;

                string label = GetObjectFacingDisplayName(candidate);
                string sceneName = NormalizeIdentifierName(candidate.name);
                Vector3 candidatePosition = GetInteractablePlanningPosition(candidate);
                float flatDistance = GetFlatDistance(playerPosition, candidatePosition);
                float verticalDistance = Mathf.Abs(playerPosition.y - candidatePosition.y);

                float score = 1000f;
                if (ContainsToken(label, "monitor") || ContainsToken(sceneName, "monitor"))
                    score += 1000f;
                if (ContainsToken(label, "computer") || ContainsToken(sceneName, "computer"))
                    score += 200f;
                if (ContainsToken(sceneName, "pc"))
                    score -= 100f;
                score -= flatDistance * 10f;
                score -= verticalDistance * 100f;

                if (score <= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static bool TryResolveComputerAnchorInteractable(GameObject anchorObject, out InteractableObj interactable)
        {
            interactable = null;
            if (anchorObject == null)
                return false;

            InteractableObj[] candidates = anchorObject.GetComponentsInChildren<InteractableObj>(includeInactive: true);
            float bestScore = float.MinValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                InteractableObj candidate = candidates[i];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                if (!IsComputerInteractable(candidate))
                    continue;

                string label = GetObjectFacingDisplayName(candidate);
                string sceneName = NormalizeIdentifierName(candidate.name);
                float score = 1000f;
                if (ContainsToken(label, "monitor") || ContainsToken(sceneName, "monitor"))
                    score += 2000f;
                if (ContainsToken(label, "computer") || ContainsToken(sceneName, "computer"))
                    score += 200f;
                score -= Vector3.Distance(GetInteractablePlanningPosition(candidate), anchorObject.transform.position);

                if (score <= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static bool IsComputerInteractable(InteractableObj interactable)
        {
            if (interactable == null)
                return false;

            string inkFileName = NormalizeIdentifierName(interactable.inkFileName);
            string internalName = NormalizeIdentifierName(interactable.InternalName());
            return ContainsToken(inkFileName, "mac computer") ||
                ContainsToken(inkFileName, "mac_computer") ||
                ContainsToken(internalName, "mac");
        }

        private static Vector3 GetInteractablePlanningPosition(InteractableObj interactable)
        {
            if (interactable == null)
                return Vector3.zero;

            if (TryGetInteractableNavigationBounds(interactable, out Bounds bounds))
                return bounds.center;

            return interactable.transform.position;
        }

        private static bool TryFindNearestDateableInteractable(bool requireUnmet, bool requireUnrealized, out InteractableObj interactable)
        {
            interactable = null;

            Save save = Singleton<Save>.Instance;
            if (save == null || BetterPlayerControl.Instance == null)
                return false;

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            float bestScore = float.MinValue;
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                string internalName = candidate.InternalName();
                if (string.IsNullOrWhiteSpace(internalName))
                    continue;

                if (!save.TryGetNameByInternalName(internalName, out string displayName) ||
                    string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                RelationshipStatus dateStatus = save.GetDateStatus(internalName);
                RelationshipStatus realizedStatus = save.GetDateStatusRealized(internalName);
                if (requireUnmet && dateStatus != RelationshipStatus.Unmet)
                    continue;

                if (requireUnrealized &&
                    (dateStatus == RelationshipStatus.Unmet || realizedStatus == RelationshipStatus.Realized))
                {
                    continue;
                }

                float score = 0f;
                score -= Vector3.Distance(playerPosition, candidate.transform.position);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static bool TryResolveTutorialFrontDoorAnchorInteractable(out InteractableObj interactable)
        {
            return TryResolveTutorialFrontDoorInteractable(out interactable);
        }

        private static bool TryResolveTutorialGiftBoxInteractable(out InteractableObj interactable)
        {
            interactable = null;
            EnsureReflectionCache();
            GameObject anchorObject = _tutorialGiftBoxField != null && TutorialController.Instance != null
                ? _tutorialGiftBoxField.GetValue(TutorialController.Instance) as GameObject
                : null;

            if (anchorObject == null || !anchorObject.activeInHierarchy)
                return false;

            return TryResolveInteractableFromTutorialAnchor(anchorObject, out interactable);
        }

        private static bool TryResolveTutorialFrontDoorInteractable(out InteractableObj interactable)
        {
            interactable = null;
            EnsureReflectionCache();
            GameObject anchorObject = _tutorialFrontDoorField != null && TutorialController.Instance != null
                ? _tutorialFrontDoorField.GetValue(TutorialController.Instance) as GameObject
                : null;

            if (TryFindBestFrontDoorInteractable(anchorObject, out interactable))
                return true;

            return TryFindBestFrontDoorInteractable(null, out interactable);
        }

        private static bool TryResolveTutorialObjectiveAnchorInteractable(TutorialObjectiveKind objectiveKind, out InteractableObj interactable)
        {
            interactable = null;

            GameObject anchorObject = null;
            switch (objectiveKind)
            {
                case TutorialObjectiveKind.Computer:
                    EnsureReflectionCache();
                    anchorObject = _tutorialComputerField != null && TutorialController.Instance != null
                        ? _tutorialComputerField.GetValue(TutorialController.Instance) as GameObject
                        : null;
                    break;

                case TutorialObjectiveKind.FrontDoor:
                    EnsureReflectionCache();
                    return TryResolveTutorialGiftBoxInteractable(out interactable);

                case TutorialObjectiveKind.HouseExit:
                    EnsureReflectionCache();
                    anchorObject = _tutorialFrontDoorField != null && TutorialController.Instance != null
                        ? _tutorialFrontDoorField.GetValue(TutorialController.Instance) as GameObject
                        : null;
                    break;
            }

            if (anchorObject == null)
                return false;

            if (objectiveKind == TutorialObjectiveKind.Computer &&
                TryResolveComputerAnchorInteractable(anchorObject, out interactable))
            {
                return true;
            }

            return TryResolveInteractableFromTutorialAnchor(anchorObject, out interactable);
        }

        private static bool TryResolveInteractableFromTutorialAnchor(GameObject anchorObject, out InteractableObj interactable)
        {
            interactable = null;
            if (anchorObject == null)
                return false;

            interactable = anchorObject.GetComponent<InteractableObj>();
            if (interactable != null && interactable.gameObject.activeInHierarchy)
                return true;

            interactable = anchorObject.GetComponentInChildren<InteractableObj>(includeInactive: true);
            if (interactable != null && interactable.gameObject.activeInHierarchy)
                return true;

            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            float bestDistance = float.MaxValue;
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                float distance = Vector3.Distance(candidate.transform.position, anchorObject.transform.position);
                if (distance >= bestDistance || distance > 8f)
                    continue;

                bestDistance = distance;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static bool TryFindBestFrontDoorInteractable(GameObject anchorObject, out InteractableObj interactable)
        {
            interactable = null;
            InteractableObj[] candidates = anchorObject != null
                ? anchorObject.GetComponentsInChildren<InteractableObj>(includeInactive: true)
                : FindObjectsOfType<InteractableObj>();

            if (candidates == null || candidates.Length == 0)
                return false;

            float bestScore = float.MinValue;
            Vector3 anchorPosition = anchorObject != null ? anchorObject.transform.position : Vector3.zero;
            for (int i = 0; i < candidates.Length; i++)
            {
                InteractableObj candidate = candidates[i];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                float score = ScoreFrontDoorCandidate(candidate);
                if (score <= 0f)
                    continue;

                if (anchorObject != null)
                    score -= Vector3.Distance(candidate.transform.position, anchorPosition);

                if (score <= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static float ScoreFrontDoorCandidate(InteractableObj interactable)
        {
            if (interactable == null)
                return 0f;

            string objectName = NormalizeIdentifierName(interactable.name);
            string label = GetObjectFacingDisplayName(interactable);
            string sceneText = GetInteractableHierarchyText(interactable);
            string sceneKey = BuildComparisonKey(sceneText);

            if (ContainsTutorialExcludedObjectName(objectName) ||
                ContainsTutorialExcludedObjectName(label) ||
                ContainsToken(sceneText, "particle") ||
                ContainsToken(sceneText, "frame") ||
                ContainsToken(sceneText, "knob") ||
                ContainsToken(sceneText, "cam"))
            {
                return 0f;
            }

            if (!ContainsToken(sceneText, "frontdoor") && !ContainsToken(sceneText, "front door"))
                return 0f;

            if (string.Equals(sceneKey, "doorfrontmodelupdate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sceneKey, "doorfrontbrokenglassmodelupdate", StringComparison.OrdinalIgnoreCase))
            {
                return 500f;
            }

            if (string.Equals(BuildComparisonKey(objectName), "doorfrontmodelupdate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BuildComparisonKey(objectName), "doorfrontbrokenglassmodelupdate", StringComparison.OrdinalIgnoreCase))
            {
                return 450f;
            }

            if (ContainsToken(label, "front door"))
                return 300f;

            return ContainsToken(objectName, "door front") ? 200f : 0f;
        }

        private static bool TryResolveTutorialHallwayFallbackInteractable(out InteractableObj interactable)
        {
            interactable = null;
            InteractableObj[] candidates = FindObjectsOfType<InteractableObj>();
            if (candidates == null || candidates.Length == 0)
                return false;

            InteractableObj stairsFallback = null;
            InteractableObj bathroomDoorFallback = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                InteractableObj candidate = candidates[i];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                string sceneText = GetInteractableHierarchyText(candidate);
                string sceneKey = BuildComparisonKey(sceneText);
                string objectKey = BuildComparisonKey(candidate.name);

                if (string.Equals(objectKey, "doorsbathroom1", StringComparison.OrdinalIgnoreCase) ||
                    sceneKey.IndexOf("doorsbathroom1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bathroomDoorFallback = candidate;
                    continue;
                }

                if (stairsFallback == null &&
                    (string.Equals(objectKey, "stairs", StringComparison.OrdinalIgnoreCase) ||
                    sceneKey.IndexOf("househallwaystairs", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    stairsFallback = candidate;
                }
            }

            interactable = stairsFallback ?? bathroomDoorFallback;
            return interactable != null;
        }

        private static string GetTutorialHallwayFallbackLabel(InteractableObj interactable)
        {
            if (IsTutorialHallwayStairsFallback(interactable))
                return Loc.Get("navigation_tutorial_hallway_stairs");

            return Loc.Get("navigation_tutorial_hallway_bathroom_door");
        }

        private static bool IsTutorialHallwayStairsFallback(InteractableObj interactable)
        {
            if (interactable == null || interactable.gameObject == null)
                return false;

            string sceneKey = BuildComparisonKey(GetInteractableHierarchyText(interactable));
            string objectKey = BuildComparisonKey(interactable.name);
            return string.Equals(objectKey, "stairs", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(sceneKey) &&
                sceneKey.IndexOf("househallwaystairs", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool TryResolveActiveDroneTriggerWorldTarget(out Vector3 target, out float radius)
        {
            target = Vector3.zero;
            radius = 1.25f;
            EnsureReflectionCache();

            TutorialTriggerZone[] triggerZones = _tutorialTriggerZonesField != null && TutorialController.Instance != null
                ? _tutorialTriggerZonesField.GetValue(TutorialController.Instance) as TutorialTriggerZone[]
                : null;

            if (triggerZones == null || triggerZones.Length == 0)
                triggerZones = FindObjectsOfType<TutorialTriggerZone>(includeInactive: true);

            TutorialTriggerZone bestTriggerZone = null;
            float bestDistance = float.MaxValue;
            Vector3 playerPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;

            for (int i = 0; i < triggerZones.Length; i++)
            {
                TutorialTriggerZone triggerZone = triggerZones[i];
                if (triggerZone == null ||
                    triggerZone.eventType != TutorialTriggerZone.EventType.TriggerDrone ||
                    triggerZone.gameObject == null ||
                    !triggerZone.gameObject.activeInHierarchy)
                {
                    continue;
                }

                float distance = Vector3.Distance(playerPosition, triggerZone.transform.position);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestTriggerZone = triggerZone;
            }

            if (bestTriggerZone == null)
                return false;

            Collider triggerCollider = bestTriggerZone.GetComponent<Collider>();
            if (triggerCollider != null)
            {
                Bounds bounds = triggerCollider.bounds;
                target = bounds.center;
                float horizontalExtent = Mathf.Max(bounds.extents.x, bounds.extents.z);
                if (horizontalExtent > 0.01f)
                    radius = Mathf.Clamp(horizontalExtent, 1f, 3.5f);
                return true;
            }

            target = bestTriggerZone.transform.position;
            return true;
        }

        private static bool TryResolveActiveDroneThresholdWorldTarget(out Vector3 target, out float radius)
        {
            target = Vector3.zero;
            radius = 1.25f;

            TutorialThreshold[] thresholds = FindObjectsOfType<TutorialThreshold>(includeInactive: true);
            if (thresholds == null || thresholds.Length == 0)
                return false;

            TutorialThreshold bestThreshold = null;
            float bestDistance = float.MaxValue;
            Vector3 playerPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;

            for (int i = 0; i < thresholds.Length; i++)
            {
                TutorialThreshold threshold = thresholds[i];
                if (threshold == null ||
                    !threshold.isDroneThreshold ||
                    threshold.gameObject == null ||
                    !threshold.gameObject.activeInHierarchy)
                {
                    continue;
                }

                float distance = Vector3.Distance(playerPosition, threshold.transform.position);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestThreshold = threshold;
            }

            if (bestThreshold == null)
                return false;

            Collider thresholdCollider = bestThreshold.GetComponent<Collider>();
            if (thresholdCollider != null)
            {
                Bounds bounds = thresholdCollider.bounds;
                target = bounds.center;
                float horizontalExtent = Mathf.Max(bounds.extents.x, bounds.extents.z);
                if (horizontalExtent > 0.01f)
                    radius = Mathf.Clamp(horizontalExtent, 1f, 3.5f);
                return true;
            }

            target = bestThreshold.transform.position;
            return true;
        }

        private static bool CanPlanToInteractable(InteractableObj interactable)
        {
            if (interactable == null ||
                interactable.gameObject == null ||
                BetterPlayerControl.Instance == null ||
                !SimpleNavPlanner.IsReady)
            {
                return false;
            }

            Vector3 start = BetterPlayerControl.Instance.transform.position;
            Vector3 target = GetInteractablePlanningPosition(interactable);
            float radius = GetInteractableRouteRadius(interactable);
            SimpleNavRoute route = SimpleNavPlanner.Plan(
                start,
                target,
                radius,
                interactable.gameObject.name,
                interactable.gameObject.GetInstanceID(),
                targetIsDatable: !string.IsNullOrEmpty(interactable.inkFileName),
                targetInkFileName: interactable.inkFileName);
            return route != null;
        }

        private static bool CanPlanToWorldTarget(Vector3 target, float radius, string label)
        {
            if (BetterPlayerControl.Instance == null || !SimpleNavPlanner.IsReady)
                return false;

            SimpleNavRoute route = SimpleNavPlanner.Plan(
                BetterPlayerControl.Instance.transform.position,
                target,
                radius > 0f ? radius : 1.25f,
                label ?? "World target",
                0,
                targetIsDatable: false,
                targetInkFileName: null);
            return route != null;
        }

        private static float ScoreTutorialObjectiveInteractable(TutorialObjectiveKind objectiveKind, InteractableObj interactable)
        {
            if (interactable == null)
                return 0f;

            string internalName = NormalizeText(interactable.InternalName());
            string objectLabel = GetObjectFacingDisplayName(interactable);
            string sceneName = NormalizeIdentifierName(interactable.name);
            string knownName = GetKnownDateableDisplayName(interactable);

            float score = 0f;

            switch (objectiveKind)
            {
                case TutorialObjectiveKind.Computer:
                    if (ContainsToken(objectLabel, "computer"))
                        score += 100f;
                    if (ContainsToken(sceneName, "computer"))
                        score += 80f;
                    break;

                case TutorialObjectiveKind.FrontDoor:
                case TutorialObjectiveKind.HouseExit:
                    if (ContainsTutorialExcludedObjectName(sceneName) ||
                        ContainsTutorialExcludedObjectName(objectLabel))
                    {
                        return 0f;
                    }

                    if (ContainsToken(objectLabel, "front door"))
                        score += 140f;
                    if (ContainsToken(sceneName, "front door") || ContainsToken(sceneName, "frontdoor"))
                        score += 120f;
                    if (ContainsToken(objectLabel, "door"))
                        score += 40f;
                    break;

                case TutorialObjectiveKind.Dorian:
                    if (string.Equals(internalName, "dorian", StringComparison.OrdinalIgnoreCase))
                        score += 140f;
                    if (ContainsToken(objectLabel, "door"))
                        score += 40f;
                    break;

                case TutorialObjectiveKind.Phone:
                    if (string.Equals(internalName, "phoenicia", StringComparison.OrdinalIgnoreCase))
                        score += 140f;
                    if (ContainsToken(objectLabel, "phone"))
                        score += 110f;
                    break;

                case TutorialObjectiveKind.Maggie:
                    if (string.Equals(internalName, "maggie", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(internalName, "maggie_mglass", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 140f;
                    }
                    if (ContainsToken(objectLabel, "maggie") ||
                        ContainsToken(sceneName, "maggie") ||
                        ContainsToken(knownName, "maggie"))
                    {
                        score += 120f;
                    }
                    if (ContainsToken(objectLabel, "magnifying"))
                        score += 110f;
                    if (ContainsToken(sceneName, "magnifying") || ContainsToken(sceneName, "mglass"))
                        score += 90f;
                    break;

                case TutorialObjectiveKind.Bed:
                    if (string.Equals(internalName, "betty", StringComparison.OrdinalIgnoreCase))
                        score += 140f;
                    if (ContainsToken(objectLabel, "bed"))
                        score += 110f;
                    break;

                case TutorialObjectiveKind.Skylar:
                    if (string.Equals(internalName, "skylar", StringComparison.OrdinalIgnoreCase))
                        score += 140f;
                    if (ContainsToken(objectLabel, "specs"))
                        score += 110f;
                    break;
            }

            return score;
        }

        private static string GetKnownDateableDisplayName(InteractableObj interactable)
        {
            if (interactable == null || Singleton<Save>.Instance == null)
                return null;

            if (!Singleton<Save>.Instance.TryGetNameByInternalName(interactable.InternalName(), out string displayName))
                return null;

            return NormalizeIdentifierName(displayName);
        }


        private static bool TryGetCurrentZoneFacingForward(out Vector3 forward, out string source)
        {
            forward = Vector3.zero;
            source = null;

            CameraSpaces cameraSpaces = Singleton<CameraSpaces>.Instance;
            if (cameraSpaces == null)
                return false;

            triggerzone zone = null;
            try
            {
                zone = cameraSpaces.PlayerZone();
            }
            catch (Exception ex)
            {
                _instance?.LogNavigationTransitionDebug("PlayerZone failed: " + ex.Message);
            }
            if (zone == null)
                return false;

            if (zone.direction != null)
            {
                forward = zone.direction.forward;
                source = "PlayerZone.direction";
                return true;
            }

            try
            {
                Transform directionTransform = cameraSpaces.CurrentDirTransform();
                if (directionTransform != null)
                {
                    forward = directionTransform.forward;
                    source = "CurrentDirTransform";
                    return true;
                }
            }
            catch (Exception ex)
            {
                _instance?.LogNavigationTransitionDebug("CurrentDirTransform failed: " + ex.Message);
            }

            return false;
        }

        private void StartNavigationToCurrentTarget()
        {
            Loc.RefreshLanguage();

            if (TryResolveCurrentObjectiveWorldTarget(out string worldTargetZone, out string worldTargetLabel))
            {
                BeginNavigationAndStartTrackerTone(worldTargetZone, worldTargetLabel);
                return;
            }

            if (!TryResolveCurrentObjectiveInteractable(out InteractableObj interactable, out string targetZone, out string targetLabel))
            {
                ScreenReader.Say(Loc.Get("navigation_no_objective"));
                return;
            }

            SetTrackedInteractable(interactable, targetZone, targetLabel);
            BeginNavigationAndStartTrackerTone(targetZone, targetLabel);
        }

        // Shared selection tail: BeginNavigation, plan the route, and start the tracker tone at
        // the first waypoint so the player gets safe spatial feedback immediately. Used by both
        // Ctrl+F6 (objective selection) and the Ctrl+Shift+F6 known-objects picker.
        private void BeginNavigationAndStartTrackerTone(string targetZone, string targetLabel)
        {
            if (!BeginNavigation(targetZone, targetLabel))
                return;

            if (TryPlanAndInstallSimpleNavRoute())
            {
                ObjectTracker.StartTracking(ResolveInitialNavigationTrackerTarget(), requiresInteraction: false);
            }
            else
            {
                StopNavigationRuntime();
            }
        }

        private Vector3 ResolveInitialNavigationTrackerTarget()
        {
            if (SimpleNavBridge.HasActiveRoute)
                return SimpleNavBridge.LastResolvedTarget;

            if (_hasNavigationWorldTarget)
                return _navigationWorldTarget;

            if (TryGetTrackedInteractable(out InteractableObj interactable) && interactable != null)
                return GetInteractablePlanningPosition(interactable);

            return SimpleNavBridge.LastResolvedTarget;
        }

        // Ctrl+Shift+F6 known-objects picker. The office door is seeded at game start;
        // every other entry comes from save/runtime evidence that the player has met,
        // interacted with, or examined the object.
        // Up/Down move selection; Enter selects (drives the same nav-tone flow as Ctrl+F6);
        // Escape closes.
        private void OpenKnownObjectPicker()
        {
            Loc.RefreshLanguage();

            if (!TryBuildKnownObjectTargets(out List<KnownObjectTarget> targets) || targets.Count == 0)
            {
                ScreenReader.Say(Loc.Get("navigation_object_picker_empty"));
                return;
            }

            _knownObjectTargets = targets;
            _knownObjectSelectionIndex = 0;
            _isKnownObjectPickerOpen = true;
            SyncKnownObjectPickerKeyStates();
            AnnounceCurrentKnownObjectPickerItem();
        }

        private void CloseKnownObjectPicker(bool announceClosed)
        {
            if (!_isKnownObjectPickerOpen)
                return;

            _isKnownObjectPickerOpen = false;
            _knownObjectTargets = null;
            _knownObjectSelectionIndex = -1;
            SyncKnownObjectPickerKeyStates();
            if (announceClosed)
                ScreenReader.Say(Loc.Get("navigation_object_picker_closed"));
        }

        private void UpdateKnownObjectPicker()
        {
            if (_knownObjectTargets == null || _knownObjectTargets.Count == 0)
            {
                CloseKnownObjectPicker(announceClosed: false);
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.UpArrow, VkUp, ref _pickerUpWasDown))
            {
                _knownObjectSelectionIndex = (_knownObjectSelectionIndex + _knownObjectTargets.Count - 1) % _knownObjectTargets.Count;
                AnnounceCurrentKnownObjectPickerItem();
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.DownArrow, VkDown, ref _pickerDownWasDown))
            {
                _knownObjectSelectionIndex = (_knownObjectSelectionIndex + 1) % _knownObjectTargets.Count;
                AnnounceCurrentKnownObjectPickerItem();
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.Return, VkReturn, ref _pickerReturnWasDown) ||
                WasChoiceKeyPressed(KeyCode.KeypadEnter, VkReturn, ref _pickerReturnWasDown))
            {
                SelectCurrentKnownObjectPickerItem();
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.Escape, VkEscape, ref _pickerEscapeWasDown))
            {
                CloseKnownObjectPicker(announceClosed: true);
            }
        }

        private void AnnounceCurrentKnownObjectPickerItem()
        {
            if (_knownObjectTargets == null || _knownObjectTargets.Count == 0)
                return;

            _knownObjectSelectionIndex = Mathf.Clamp(_knownObjectSelectionIndex, 0, _knownObjectTargets.Count - 1);
            KnownObjectTarget target = _knownObjectTargets[_knownObjectSelectionIndex];
            string option = Loc.Get(
                "navigation_object_picker_option",
                _knownObjectSelectionIndex + 1,
                _knownObjectTargets.Count,
                target.Label);
            ScreenReader.Say(Loc.Get("navigation_object_picker_title") + ". " + option);
        }

        private void SelectCurrentKnownObjectPickerItem()
        {
            if (_knownObjectTargets == null || _knownObjectTargets.Count == 0)
            {
                CloseKnownObjectPicker(announceClosed: false);
                return;
            }

            _knownObjectSelectionIndex = Mathf.Clamp(_knownObjectSelectionIndex, 0, _knownObjectTargets.Count - 1);
            KnownObjectTarget target = _knownObjectTargets[_knownObjectSelectionIndex];
            InteractableObj interactable = target.Interactable;

            CloseKnownObjectPicker(announceClosed: false);

            if (interactable == null || !interactable.gameObject.activeInHierarchy)
            {
                ScreenReader.Say(Loc.Get("navigation_object_picker_empty"));
                return;
            }

            TryGetZoneNameForInteractable(interactable, out string targetZone);
            string targetLabel = target.Label;

            SetTrackedInteractable(interactable, targetZone, targetLabel);
            BeginNavigationAndStartTrackerTone(targetZone, targetLabel);
        }

        private static void SyncKnownObjectPickerKeyStates()
        {
            _pickerUpWasDown = (GetAsyncKeyState(VkUp) & 0x8000) != 0;
            _pickerDownWasDown = (GetAsyncKeyState(VkDown) & 0x8000) != 0;
            _pickerReturnWasDown = (GetAsyncKeyState(VkReturn) & 0x8000) != 0;
            _pickerEscapeWasDown = (GetAsyncKeyState(VkEscape) & 0x8000) != 0;
        }

        private bool TryBuildKnownObjectTargets(out List<KnownObjectTarget> targets)
        {
            targets = new List<KnownObjectTarget>();
            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            if (interactables == null || interactables.Length == 0)
                return false;

            Transform playerTransform = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform
                : null;
            Vector3 playerPosition = playerTransform != null ? playerTransform.position : Vector3.zero;

            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                string label = GetObjectFacingDisplayName(candidate);
                if (string.IsNullOrWhiteSpace(label) ||
                    string.Equals(label, Loc.Get("unknown_object"), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsStartupOfficeDoorObject(candidate, label) &&
                    !IsEncounteredKnownObject(candidate))
                {
                    continue;
                }

                Vector3 candidatePos = candidate.transform.position;
                float distance = playerTransform != null
                    ? GetFlatDistance(playerPosition, candidatePos)
                    : 0f;

                if (TryFindEquivalentKnownObjectTarget(targets, candidate, label, out KnownObjectTarget existing))
                {
                    if (playerTransform != null && distance < existing.Distance)
                    {
                        existing.Interactable = candidate;
                        existing.Label = label;
                        existing.Distance = distance;
                    }
                    continue;
                }

                targets.Add(new KnownObjectTarget
                {
                    Interactable = candidate,
                    Label = label,
                    Distance = distance,
                });
            }

            targets.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return targets.Count > 0;
        }

        private static bool IsStartupOfficeDoorObject(InteractableObj interactable, string label)
        {
            if (interactable == null)
                return false;

            string sceneText = GetInteractableHierarchyText(interactable);
            string objectName = NormalizeIdentifierName(interactable.name);

            if (ContainsTutorialExcludedObjectName(objectName) ||
                ContainsTutorialExcludedObjectName(label) ||
                ContainsTutorialExcludedObjectName(sceneText))
            {
                return false;
            }

            if (!IsDoorInteractable(interactable))
                return false;

            string sceneKey = BuildComparisonKey(sceneText);
            return string.Equals(BuildComparisonKey(objectName), "doorsoffice", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BuildComparisonKey(objectName), "doorsofficeunlocked", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BuildComparisonKey(objectName), "doorsofficelocked", StringComparison.OrdinalIgnoreCase) ||
                sceneKey.IndexOf("doorsoffice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sceneKey.IndexOf("officedoorsdoorsoffice", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsTutorialExcludedObjectName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return ContainsToken(value, "particle") ||
                ContainsToken(value, "closet") ||
                ContainsToken(value, "bathroom") ||
                ContainsToken(value, "rug") ||
                ContainsToken(value, "hidden");
        }

        private static bool TryFindEquivalentKnownObjectTarget(
            List<KnownObjectTarget> targets,
            InteractableObj candidate,
            string label,
            out KnownObjectTarget equivalent)
        {
            equivalent = null;
            if (targets == null || candidate == null)
                return false;

            string candidateId = candidate.Id;
            string candidateInternal = candidate.InternalName();
            for (int i = 0; i < targets.Count; i++)
            {
                KnownObjectTarget existing = targets[i];
                if (existing == null || existing.Interactable == null)
                    continue;

                if (!string.IsNullOrEmpty(candidateId) &&
                    string.Equals(existing.Interactable.Id, candidateId, StringComparison.OrdinalIgnoreCase))
                {
                    equivalent = existing;
                    return true;
                }

                if (!string.IsNullOrEmpty(candidateInternal) &&
                    string.Equals(existing.Interactable.InternalName(), candidateInternal, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Label, label, StringComparison.OrdinalIgnoreCase))
                {
                    equivalent = existing;
                    return true;
                }
            }

            return false;
        }

        private static bool IsInteractableKnownToPlayer(InteractableObj interactable)
        {
            return IsEncounteredKnownObject(interactable);
        }

        private static bool IsEncounteredKnownObject(InteractableObj interactable)
        {
            if (interactable == null)
                return false;

            if (IsDatedInteractable(interactable))
                return true;

            ObjectSaveData saveData = interactable.objSaveData;
            if (saveData != null && saveData.hasNormalInteracted)
                return true;

            return IsExaminedInteractable(interactable);
        }

        private static bool IsExaminedInteractable(InteractableObj interactable)
        {
            if (interactable == null)
                return false;

            if (HasRememberedExaminedObjectKey(interactable.Id) ||
                HasRememberedExaminedObjectKey(interactable.name) ||
                HasRememberedExaminedObjectKey(interactable.InternalName()) ||
                HasRememberedExaminedObjectKey(interactable.inkFileName))
            {
                return true;
            }

            ObjectExamine[] childExamines = interactable.GetComponentsInChildren<ObjectExamine>(includeInactive: true);
            if (childExamines != null)
            {
                for (int i = 0; i < childExamines.Length; i++)
                {
                    if (IsRememberedOrSavedExamine(childExamines[i]))
                        return true;
                }
            }

            return IsRememberedOrSavedExamine(interactable.GetComponentInParent<ObjectExamine>());
        }

        private static bool IsRememberedOrSavedExamine(ObjectExamine examine)
        {
            if (examine == null)
                return false;

            if (HasRememberedExaminedObjectKey(examine.InkNode) ||
                (examine.gameObject != null && HasRememberedExaminedObjectKey(examine.gameObject.name)))
            {
                return true;
            }

            Save save = null;
            try { save = Singleton<Save>.Instance; }
            catch { save = null; }
            if (save == null || string.IsNullOrWhiteSpace(examine.InkNode))
                return false;

            try
            {
                Dictionary<string, int> examinedBoxes = save.GetBoxExamenData();
                return examinedBoxes != null && examinedBoxes.ContainsKey(examine.InkNode);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasRememberedExaminedObjectKey(string value)
        {
            string key = BuildComparisonKey(value);
            return !string.IsNullOrEmpty(key) && _examinedObjectKeys.Contains(key);
        }

        private static bool IsDatedInteractable(InteractableObj interactable)
        {
            if (interactable == null || string.IsNullOrEmpty(interactable.inkFileName))
                return false;

            Save save = null;
            try { save = Singleton<Save>.Instance; }
            catch { save = null; }
            if (save == null)
                return false;

            string internalName = interactable.InternalName();
            if (string.IsNullOrEmpty(internalName))
                return false;

            try
            {
                return save.GetDateStatus(internalName) != RelationshipStatus.Unmet;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDoorInteractable(InteractableObj interactable)
        {
            if (interactable == null || interactable.gameObject == null)
                return false;

            if (interactable.gameObject.GetComponent<Door>() != null ||
                interactable.gameObject.GetComponentInParent<Door>() != null ||
                interactable.gameObject.GetComponentInChildren<Door>() != null ||
                interactable.gameObject.GetComponent<SlidingDoor>() != null ||
                interactable.gameObject.GetComponentInParent<SlidingDoor>() != null ||
                interactable.gameObject.GetComponentInChildren<SlidingDoor>() != null)
            {
                return true;
            }

            return false;
        }

        private void ToggleAutoWalk()
        {
            Loc.RefreshLanguage();

            if (_isAutoWalking)
            {
                StopNavigationRuntime();
                ScreenReader.Say(Loc.Get("navigation_autowalk_stopped"));
                return;
            }

            if (!TryEnsureNavigationTarget(out string targetZone, out string targetLabel))
            {
                ScreenReader.Say(Loc.Get("navigation_no_objective"));
                return;
            }

            if (!BeginNavigation(targetZone, targetLabel))
                return;

            if (!CanUseNavigationNow() || !ApplyNavigationInput(Vector3.zero, Vector3.zero))
            {
                StopNavigationRuntime();
                ScreenReader.Say(Loc.Get("navigation_blocked"));
                return;
            }

            if (!TryPlanAndInstallSimpleNavRoute())
            {
                StopNavigationRuntime();
                return;
            }

            _isAutoWalking = true;
            _lastAutoWalkPosition = BetterPlayerControl.Instance != null ? BetterPlayerControl.Instance.transform.position : Vector3.zero;
            _lastAutoWalkProgressTime = Time.unscaledTime;
            ScreenReader.Say(Loc.Get("navigation_autowalk_started"));
        }

        // Plan a SimpleNavRoute from the player's current position to the tracked interactable,
        // and install it on SimpleNavBridge. Returns false after announcing the user-visible
        // failure reason when the planner is unavailable, the target is missing, or no path exists.
        private bool TryPlanAndInstallSimpleNavRoute()
        {
            SimpleNavBridge.EndStep();

            if (!SimpleNavPlanner.IsReady)
            {
                if (Main.Log != null) Main.Log.LogDebug("ToggleAutoWalk: SimpleNavPlanner not ready, skipping route install");
                ScreenReader.Say(Loc.Get("navigation_planner_not_ready"));
                return false;
            }
            if (_hasNavigationWorldTarget)
            {
                if (BetterPlayerControl.Instance == null)
                {
                    if (Main.Log != null) Main.Log.LogDebug("ToggleAutoWalk: no BetterPlayerControl for world-target route planning");
                    return false;
                }

                Vector3 worldStartPos = BetterPlayerControl.Instance.transform.position;
                string worldLabel = _navigationTargetLabel ?? Loc.Get("navigation_tutorial_gift_delivery_trigger");
                SimpleNavRoute worldRoute = SimpleNavPlanner.Plan(
                    worldStartPos,
                    _navigationWorldTarget,
                    _navigationWorldTargetRadius > 0f ? _navigationWorldTargetRadius : 1.25f,
                    worldLabel,
                    0,
                    targetIsDatable: false,
                    targetInkFileName: null);
                if (worldRoute == null)
                {
                    SimpleNavPlanner.PlanFailure why = SimpleNavPlanner.LastFailure;
                    if (Main.Log != null) Main.Log.LogInfo("ToggleAutoWalk: planner returned no route for world target=" + _navigationWorldTarget + " reason=" + why);
                    ScreenReader.Say(Loc.Get("navigation_no_path", worldLabel) + " (" + why + ")");
                    return false;
                }

                SimpleNavBridge.BeginRoute(worldRoute);
                return true;
            }

            if (!TryGetTrackedInteractable(out InteractableObj target) || target == null || target.gameObject == null)
            {
                if (Main.Log != null) Main.Log.LogDebug("ToggleAutoWalk: no tracked interactable for route planning");
                ScreenReader.Say(Loc.Get("navigation_no_objective"));
                return false;
            }
            if (BetterPlayerControl.Instance == null)
            {
                if (Main.Log != null) Main.Log.LogDebug("ToggleAutoWalk: no BetterPlayerControl for route planning");
                return false;
            }

            Vector3 startPos = BetterPlayerControl.Instance.transform.position;
            Vector3 targetPos = IsComputerInteractable(target)
                ? GetInteractablePlanningPosition(target)
                : target.transform.position;
            int goId = target.gameObject.GetInstanceID();
            string goName = target.gameObject.name;
            float radius = GetInteractableRouteRadius(target);
            string label = _navigationTargetLabel ?? goName;

            bool isDatable = !string.IsNullOrWhiteSpace(target.inkFileName);
            SimpleNavRoute route = SimpleNavPlanner.Plan(startPos, targetPos, radius, goName, goId, isDatable, target.inkFileName);
            if (route == null)
            {
                SimpleNavPlanner.PlanFailure why = SimpleNavPlanner.LastFailure;
                if (Main.Log != null) Main.Log.LogInfo("ToggleAutoWalk: planner returned no route for target=" + goName + " reason=" + why);
                ScreenReader.Say(Loc.Get("navigation_no_path", label) + " (" + why + ")");
                return false;
            }
            SimpleNavBridge.BeginRoute(route);
            return true;
        }

        private static bool IsTutorialSkylarGiftTarget(InteractableObj interactable)
        {
            if (interactable == null)
                return false;

            return string.Equals(interactable.inkFileName, "skylar_specs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(interactable.InternalName(), "skylar", StringComparison.OrdinalIgnoreCase);
        }

        private static float GetInteractableRouteRadius(InteractableObj interactable)
        {
            if (interactable == null)
                return 7.5f;

            if (IsTutorialSkylarGiftTarget(interactable))
                return TutorialGiftApproachRadius;

            float radius = interactable.InteractionRadius > 0f
                ? interactable.InteractionRadius
                : 7.5f;

            if (IsDoorInteractable(interactable) && radius > SimpleNavDoorTargetRadius)
                return SimpleNavDoorTargetRadius;

            return radius;
        }


        private static float GetFlatDistance(Vector3 first, Vector3 second)
        {
            first.y = 0f;
            second.y = 0f;
            return Vector3.Distance(first, second);
        }

        private void ResetAutoWalkProgress()
        {
            _lastAutoWalkPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            _lastAutoWalkProgressTime = Time.unscaledTime;
            ClearNavigationBlockedDetail();
        }


        private static bool TryGetZoneNameForInteractable(InteractableObj interactable, out string zoneName)
        {
            zoneName = null;
            if (interactable == null)
                return false;

            if (TryGetZoneNameForGameObject(interactable.gameObject, out zoneName))
                return true;

            return TryGetFallbackZoneNameForGameObject(interactable.gameObject, out zoneName);
        }

        private static bool TryGetZoneNameForGameObject(GameObject gameObject, out string zoneName)
        {
            zoneName = null;
            if (gameObject == null || Singleton<CameraSpaces>.Instance == null || Singleton<CameraSpaces>.Instance.zones == null)
                return false;

            List<Vector3> candidatePoints = new List<Vector3>();
            Transform currentTransform = gameObject.transform;
            while (currentTransform != null)
            {
                AddCandidatePoint(candidatePoints, currentTransform.position);
                currentTransform = currentTransform.parent;
            }

            List<Collider> colliders = new List<Collider>();
            AddUniqueComponents(colliders, gameObject.GetComponentsInChildren<Collider>(includeInactive: true));
            AddUniqueComponents(colliders, gameObject.GetComponentsInParent<Collider>(includeInactive: true));

            List<Renderer> renderers = new List<Renderer>();
            AddUniqueComponents(renderers, gameObject.GetComponentsInChildren<Renderer>(includeInactive: true));
            AddUniqueComponents(renderers, gameObject.GetComponentsInParent<Renderer>(includeInactive: true));

            int bestScore = int.MinValue;
            string bestZone = null;
            for (int i = 0; i < Singleton<CameraSpaces>.Instance.zones.Count; i++)
            {
                triggerzone zone = Singleton<CameraSpaces>.Instance.zones[i];
                if (zone == null)
                    continue;

                Bounds bounds = new Bounds(zone.Position, zone.Scale);
                int score = ScoreZoneMatch(bounds, zone.Position, candidatePoints, colliders, renderers);
                if (score <= 0 || score <= bestScore)
                    continue;

                bestScore = score;
                bestZone = zone.Name;
            }

            zoneName = bestZone;
            return !string.IsNullOrEmpty(zoneName);
        }

        private static int ScoreZoneMatch(Bounds zoneBounds, Vector3 zonePosition, List<Vector3> candidatePoints, List<Collider> colliders, List<Renderer> renderers)
        {
            int score = 0;

            if (candidatePoints != null)
            {
                for (int i = 0; i < candidatePoints.Count; i++)
                {
                    if (zoneBounds.Contains(candidatePoints[i]))
                        score += 50;
                }
            }

            if (colliders != null)
            {
                for (int i = 0; i < colliders.Count; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null)
                        continue;

                    if (zoneBounds.Contains(collider.ClosestPointOnBounds(zonePosition)))
                        score += 100;
                    else if (zoneBounds.Intersects(collider.bounds))
                        score += 10;
                }
            }

            if (renderers != null)
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null)
                        continue;

                    if (zoneBounds.Contains(renderer.bounds.center))
                        score += 25;
                    else if (zoneBounds.Intersects(renderer.bounds))
                        score += 5;
                }
            }

            return score;
        }

        private static bool TryResolveNavigableInteractable(InteractableObj interactable, out InteractableObj resolvedInteractable, out string zoneName)
        {
            resolvedInteractable = null;
            zoneName = null;
            if (interactable == null)
                return false;

            var candidates = new List<InteractableObj>();
            AddUniqueComponents(candidates, new[] { interactable });
            AddUniqueComponents(candidates, interactable.GetComponentsInParent<InteractableObj>(includeInactive: true));
            AddUniqueComponents(candidates, interactable.GetComponentsInChildren<InteractableObj>(includeInactive: true));
            if (interactable.transform.root != null)
                AddUniqueComponents(candidates, interactable.transform.root.GetComponentsInChildren<InteractableObj>(includeInactive: true));

            float bestScore = float.MinValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                InteractableObj candidate = candidates[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                if (!TryGetZoneNameForInteractable(candidate, out string candidateZone))
                    continue;

                float score = ScoreNavigableInteractableCandidate(interactable, candidate);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                resolvedInteractable = candidate;
                zoneName = candidateZone;
            }

            return resolvedInteractable != null && !string.IsNullOrEmpty(zoneName);
        }

        private static float ScoreNavigableInteractableCandidate(InteractableObj preferredInteractable, InteractableObj candidate)
        {
            if (preferredInteractable == null || candidate == null)
                return float.MinValue;

            float score = 0f;
            if (candidate == preferredInteractable)
                score += 1000f;

            if (!string.IsNullOrEmpty(preferredInteractable.Id) &&
                string.Equals(preferredInteractable.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
            {
                score += 800f;
            }

            if (string.Equals(preferredInteractable.InternalName(), candidate.InternalName(), StringComparison.OrdinalIgnoreCase))
                score += 400f;

            string label = GetObjectFacingDisplayName(candidate);
            if (!string.IsNullOrEmpty(label) &&
                !string.Equals(label, Loc.Get("unknown_object"), StringComparison.OrdinalIgnoreCase))
            {
                score += 100f;
            }

            string mainText = NormalizeText(candidate.mainText);
            if (!string.IsNullOrEmpty(mainText) &&
                !mainText.StartsWith("Default hover text for ", StringComparison.OrdinalIgnoreCase))
            {
                score += 100f;
            }

            score -= Vector3.Distance(preferredInteractable.transform.position, candidate.transform.position) * 5f;
            return score;
        }

        private static bool TryGetFallbackZoneNameForGameObject(GameObject gameObject, out string zoneName)
        {
            zoneName = null;
            if (gameObject == null || Singleton<CameraSpaces>.Instance == null || Singleton<CameraSpaces>.Instance.zones == null)
                return false;

            var candidatePoints = new List<Vector3>();
            Transform currentTransform = gameObject.transform;
            while (currentTransform != null)
            {
                AddCandidatePoint(candidatePoints, currentTransform.position);
                currentTransform = currentTransform.parent;
            }

            for (int i = 0; i < Singleton<CameraSpaces>.Instance.zones.Count; i++)
            {
                triggerzone zone = Singleton<CameraSpaces>.Instance.zones[i];
                if (zone == null)
                    continue;

                Bounds bounds = new Bounds(zone.Position, zone.Scale);
                for (int pointIndex = 0; pointIndex < candidatePoints.Count; pointIndex++)
                {
                    if (!bounds.Contains(candidatePoints[pointIndex]))
                        continue;

                    zoneName = zone.Name;
                    return !string.IsNullOrEmpty(zoneName);
                }
            }

            float bestDistanceSquared = float.MaxValue;
            string bestZone = null;
            for (int i = 0; i < Singleton<CameraSpaces>.Instance.zones.Count; i++)
            {
                triggerzone zone = Singleton<CameraSpaces>.Instance.zones[i];
                if (zone == null)
                    continue;

                Bounds bounds = new Bounds(zone.Position, zone.Scale);
                for (int pointIndex = 0; pointIndex < candidatePoints.Count; pointIndex++)
                {
                    float distanceSquared = bounds.SqrDistance(candidatePoints[pointIndex]);
                    if (distanceSquared >= bestDistanceSquared)
                        continue;

                    bestDistanceSquared = distanceSquared;
                    bestZone = zone.Name;
                }
            }

            if (bestDistanceSquared <= InteractableZoneFallbackDistance * InteractableZoneFallbackDistance)
            {
                zoneName = bestZone;
                return !string.IsNullOrEmpty(zoneName);
            }

            return false;
        }

        private static void AddCandidatePoint(List<Vector3> candidatePoints, Vector3 point)
        {
            if (candidatePoints == null)
                return;

            for (int i = 0; i < candidatePoints.Count; i++)
            {
                if (Vector3.SqrMagnitude(candidatePoints[i] - point) <= 0.0001f)
                    return;
            }

            candidatePoints.Add(point);
        }

        private static void AddUniqueComponents<T>(List<T> destination, T[] components) where T : Component
        {
            if (destination == null || components == null)
                return;

            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component == null || destination.Contains(component))
                    continue;

                destination.Add(component);
            }
        }

        private void SetTrackedInteractable(InteractableObj interactable, string targetZone, string targetLabel)
        {
            ClearNavigationWorldTarget();
            _trackedInteractable = interactable;
            _trackedInteractableId = interactable != null ? interactable.Id : null;
            _trackedInteractableZone = targetZone;
            _trackedInteractableLabel = targetLabel;
            _navigationTargetZone = targetZone;
            _navigationTargetLabel = targetLabel;
            ResetTrackedInteractableApproachTarget();
            LogNavigationTargetDebug(
                "SetTrackedInteractable interactable=" + DescribeInteractable(interactable) +
                " zone=" + (targetZone ?? "<null>") +
                " label=" + (targetLabel ?? "<null>"));
        }

        private void SetNavigationWorldTarget(Vector3 target, float radius, string targetLabel)
        {
            _trackedInteractable = null;
            _trackedInteractableId = null;
            _trackedInteractableZone = null;
            _trackedInteractableLabel = null;
            ResetTrackedInteractableApproachTarget();
            _hasNavigationWorldTarget = true;
            _navigationWorldTarget = target;
            _navigationWorldTargetRadius = radius;
            _navigationTargetZone = null;
            _navigationTargetLabel = targetLabel;
        }

        private void ClearNavigationWorldTarget()
        {
            _hasNavigationWorldTarget = false;
            _navigationWorldTarget = Vector3.zero;
            _navigationWorldTargetRadius = 0f;
        }

        private bool TryGetTrackedInteractable(out InteractableObj interactable)
        {
            interactable = _trackedInteractable;
            if (interactable != null &&
                interactable.gameObject != null &&
                interactable.gameObject.activeInHierarchy &&
                (string.IsNullOrEmpty(_trackedInteractableId) || string.Equals(interactable.Id, _trackedInteractableId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.IsNullOrEmpty(_trackedInteractableId))
            {
                interactable = null;
                _trackedInteractable = null;
                ResetTrackedInteractableApproachTarget();
                return false;
            }

            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                if (!string.Equals(candidate.Id, _trackedInteractableId, StringComparison.OrdinalIgnoreCase))
                    continue;

                _trackedInteractable = candidate;
                interactable = candidate;
                return true;
            }

            _trackedInteractable = null;
            _trackedInteractableId = null;
            _trackedInteractableZone = null;
            _trackedInteractableLabel = null;
            ResetTrackedInteractableApproachTarget();
            interactable = null;
            return false;
        }

        private bool TryGetTrackedInteractableZone(InteractableObj interactable, out string zoneName)
        {
            zoneName = null;
            if (interactable == null)
                return false;

            if (TryGetZoneNameForInteractable(interactable, out zoneName))
            {
                _trackedInteractableZone = zoneName;
                return true;
            }

            if (TryResolveNavigableInteractable(interactable, out InteractableObj resolvedInteractable, out zoneName))
            {
                if (resolvedInteractable != null && resolvedInteractable != interactable)
                {
                    _trackedInteractable = resolvedInteractable;
                    _trackedInteractableId = resolvedInteractable.Id;
                    _trackedInteractableLabel = GetObjectFacingDisplayName(resolvedInteractable);
                    ResetTrackedInteractableApproachTarget();
                }

                _trackedInteractableZone = zoneName;
                return true;
            }

            if (!string.IsNullOrEmpty(_trackedInteractableZone))
            {
                zoneName = _trackedInteractableZone;
                return true;
            }

            return false;
        }

        private string GetTrackedInteractableLabel(InteractableObj interactable)
        {
            if (interactable == null)
                return _trackedInteractableLabel;

            string label = GetObjectFacingDisplayName(interactable);
            if (!string.IsNullOrEmpty(label))
            {
                _trackedInteractableLabel = label;
                return label;
            }

            return _trackedInteractableLabel;
        }

        private bool IsTrackedObjectReached()
        {
            if (!TryGetTrackedInteractable(out InteractableObj trackedInteractable) ||
                BetterPlayerControl.Instance == null)
            {
                return false;
            }

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            Vector3 targetPosition = trackedInteractable.transform.position;
            if (!TryGetTrackedInteractableNavigationTarget(
                    trackedInteractable,
                    playerPosition,
                    out targetPosition,
                    out _))
            {
                targetPosition = trackedInteractable.transform.position;
            }

            playerPosition.y = 0f;
            targetPosition.y = 0f;
            return Vector3.Distance(playerPosition, targetPosition) <= AutoWalkArrivalDistance;
        }

        private bool TryGetTrackedInteractableNavigationTarget(
            InteractableObj interactable,
            Vector3 playerPosition,
            out Vector3 targetPosition,
            out string debugDetail)
        {
            targetPosition = Vector3.zero;
            debugDetail = null;
            if (interactable == null)
                return false;

            if (!TryBuildTrackedInteractableApproachCandidates(
                    interactable,
                    playerPosition,
                    out List<Vector3> candidateTargets,
                    out Vector3 referencePosition,
                    out string candidateDetail) ||
                candidateTargets == null ||
                candidateTargets.Count < 1)
            {
                targetPosition = interactable.transform.position;
                targetPosition.y = playerPosition.y;
                debugDetail = "mode=raw-object candidates=0";
                return true;
            }

            if (CanReuseTrackedInteractableApproachTarget(
                    interactable,
                    referencePosition))
            {
                targetPosition = _trackedInteractableApproachTarget;
                targetPosition.y = playerPosition.y;
                debugDetail =
                    "mode=cached candidateSource=" + candidateDetail;
                return true;
            }

            if (!TryResolveTrackedInteractableApproachTarget(
                    referencePosition,
                    candidateTargets,
                    playerPosition,
                    out targetPosition,
                    out string resolutionDetail))
            {
                targetPosition = interactable.transform.position;
                targetPosition.y = playerPosition.y;
                debugDetail = "mode=raw-object resolution=failed";
                return true;
            }

            _trackedInteractableApproachId = interactable.Id;
            _trackedInteractableApproachReferencePosition = referencePosition;
            _trackedInteractableApproachTarget = targetPosition;
            targetPosition.y = playerPosition.y;
            debugDetail = resolutionDetail + " candidateSource=" + candidateDetail;
            return true;
        }

        private void ResetTrackedInteractableApproachTarget()
        {
            _trackedInteractableApproachId = null;
            _trackedInteractableApproachReferencePosition = Vector3.zero;
            _trackedInteractableApproachTarget = Vector3.zero;
        }

        private bool CanReuseTrackedInteractableApproachTarget(
            InteractableObj interactable,
            Vector3 referencePosition)
        {
            return interactable != null &&
                !string.IsNullOrEmpty(interactable.Id) &&
                !string.IsNullOrEmpty(_trackedInteractableApproachId) &&
                string.Equals(_trackedInteractableApproachId, interactable.Id, StringComparison.OrdinalIgnoreCase) &&
                GetFlatDistance(_trackedInteractableApproachReferencePosition, referencePosition) <= TrackedInteractableApproachRetargetDistance;
        }

        private bool TryResolveTrackedInteractableApproachTarget(
            Vector3 referencePosition,
            List<Vector3> candidateTargets,
            Vector3 playerPosition,
            out Vector3 targetPosition,
            out string detail)
        {
            targetPosition = Vector3.zero;
            detail = null;
            if (candidateTargets == null || candidateTargets.Count < 1)
                return false;

            bool foundFallbackCandidate = false;
            float bestPlayerDistance = float.PositiveInfinity;
            float bestFallbackReferenceDistance = float.PositiveInfinity;
            bool foundCenterFallbackCandidate = false;
            float bestCenterFallbackDistance = float.PositiveInfinity;
            Vector3 centerFallbackTarget = Vector3.zero;
            for (int i = 0; i < candidateTargets.Count; i++)
            {
                Vector3 candidateTarget = candidateTargets[i];
                float playerDistance = GetFlatDistance(playerPosition, candidateTarget);
                float referenceDistance = GetFlatDistance(candidateTarget, referencePosition);
                if (candidateTargets.Count > 1 &&
                    referenceDistance <= 0.1f)
                {
                    if (!foundCenterFallbackCandidate || playerDistance < bestCenterFallbackDistance)
                    {
                        foundCenterFallbackCandidate = true;
                        bestCenterFallbackDistance = playerDistance;
                        centerFallbackTarget = candidateTarget;
                    }

                    continue;
                }

                if (foundFallbackCandidate &&
                    playerDistance > bestPlayerDistance + 0.01f)
                {
                    continue;
                }

                if (foundFallbackCandidate &&
                    Mathf.Abs(playerDistance - bestPlayerDistance) <= 0.01f &&
                    referenceDistance >= bestFallbackReferenceDistance)
                {
                    continue;
                }

                foundFallbackCandidate = true;
                bestPlayerDistance = playerDistance;
                bestFallbackReferenceDistance = referenceDistance;
                targetPosition = candidateTarget;
            }

            if (!foundFallbackCandidate && foundCenterFallbackCandidate)
            {
                foundFallbackCandidate = true;
                bestPlayerDistance = bestCenterFallbackDistance;
                bestFallbackReferenceDistance = 0f;
                targetPosition = centerFallbackTarget;
            }

            if (!foundFallbackCandidate)
                return false;

            detail =
                "mode=distance-selected" +
                " playerDistance=" + bestPlayerDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " referenceDistance=" + bestFallbackReferenceDistance.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        private bool TryBuildTrackedInteractableApproachCandidates(
            InteractableObj interactable,
            Vector3 playerPosition,
            out List<Vector3> candidateTargets,
            out Vector3 referencePosition,
            out string detail)
        {
            candidateTargets = new List<Vector3>();
            referencePosition = Vector3.zero;
            detail = "transform";
            if (interactable == null)
                return false;

            bool hasBounds = TryGetInteractableNavigationBounds(interactable, out Bounds bounds);
            referencePosition = hasBounds ? bounds.center : interactable.transform.position;
            detail = hasBounds ? "bounds" : "transform";

            Vector3 targetYPosition = referencePosition;
            targetYPosition.y = playerPosition.y;

            Vector3 preferredDirection = referencePosition - playerPosition;
            preferredDirection.y = 0f;
            if (preferredDirection.sqrMagnitude > 0.0001f)
            {
                AddCandidatePoint(
                    candidateTargets,
                    BuildTrackedInteractableApproachCandidatePosition(bounds, preferredDirection, playerPosition.y));
            }

            Vector3[] directions =
            {
                Vector3.right,
                Vector3.left,
                Vector3.forward,
                Vector3.back,
                (Vector3.right + Vector3.forward).normalized,
                (Vector3.left + Vector3.forward).normalized,
                (Vector3.right + Vector3.back).normalized,
                (Vector3.left + Vector3.back).normalized
            };

            for (int i = 0; i < directions.Length; i++)
            {
                AddCandidatePoint(
                    candidateTargets,
                    BuildTrackedInteractableApproachCandidatePosition(bounds, directions[i], playerPosition.y));
            }

            AddCandidatePoint(candidateTargets, targetYPosition);
            return candidateTargets.Count > 0;
        }

        private static bool TryGetInteractableNavigationBounds(InteractableObj interactable, out Bounds bounds)
        {
            bounds = new Bounds(interactable != null ? interactable.transform.position : Vector3.zero, Vector3.zero);
            if (interactable == null || interactable.gameObject == null)
                return false;

            bool hasBounds = false;
            var colliders = new List<Collider>();
            if (interactable.collider != null)
                colliders.Add(interactable.collider);

            AddUniqueComponents(colliders, interactable.gameObject.GetComponentsInChildren<Collider>(includeInactive: true));
            for (int i = 0; i < colliders.Count; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            var renderers = new List<Renderer>();
            AddUniqueComponents(renderers, interactable.gameObject.GetComponentsInChildren<Renderer>(includeInactive: true));
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                bounds = new Bounds(interactable.transform.position, Vector3.zero);
            }

            return hasBounds;
        }

        private static Vector3 BuildTrackedInteractableApproachCandidatePosition(Bounds bounds, Vector3 direction, float targetY)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                Vector3 fallback = bounds.center;
                fallback.y = targetY;
                return fallback;
            }

            direction.Normalize();
            float extentX = Mathf.Max(bounds.extents.x, TrackedInteractableApproachMinimumExtent);
            float extentZ = Mathf.Max(bounds.extents.z, TrackedInteractableApproachMinimumExtent);
            float offsetX = Mathf.Abs(direction.x) > 0.0001f
                ? Mathf.Sign(direction.x) * (extentX + TrackedInteractableApproachClearanceDistance)
                : 0f;
            float offsetZ = Mathf.Abs(direction.z) > 0.0001f
                ? Mathf.Sign(direction.z) * (extentZ + TrackedInteractableApproachClearanceDistance)
                : 0f;

            Vector3 candidate = bounds.center + new Vector3(offsetX, 0f, offsetZ);
            candidate.y = targetY;
            return candidate;
        }

        private static FacingRelativeDirection GetFacingRelativeDirection(Vector3 targetPosition)
        {
            if (BetterPlayerControl.Instance == null)
                return FacingRelativeDirection.Ahead;

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            Vector3 toTarget = targetPosition - playerTransform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude <= 1f)
                return FacingRelativeDirection.Here;

            Vector3 forward = playerTransform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f)
                forward = Vector3.forward;
            else
                forward.Normalize();

            toTarget.Normalize();
            float angle = Vector3.SignedAngle(forward, toTarget, Vector3.up);

            if (angle >= -22.5f && angle < 22.5f)
                return FacingRelativeDirection.Ahead;
            if (angle >= 22.5f && angle < 67.5f)
                return FacingRelativeDirection.AheadRight;
            if (angle >= 67.5f && angle < 112.5f)
                return FacingRelativeDirection.Right;
            if (angle >= 112.5f && angle < 157.5f)
                return FacingRelativeDirection.BehindRight;
            if (angle >= 157.5f || angle < -157.5f)
                return FacingRelativeDirection.Behind;
            if (angle >= -157.5f && angle < -112.5f)
                return FacingRelativeDirection.BehindLeft;
            if (angle >= -112.5f && angle < -67.5f)
                return FacingRelativeDirection.Left;
            return FacingRelativeDirection.AheadLeft;
        }

        private static string GetFacingRelativeDirectionLabel(FacingRelativeDirection direction)
        {
            switch (direction)
            {
                case FacingRelativeDirection.Here:
                    return Loc.Get("room_scan_direction_here");
                case FacingRelativeDirection.Ahead:
                    return Loc.Get("room_scan_direction_ahead");
                case FacingRelativeDirection.AheadRight:
                    return Loc.Get("room_scan_direction_ahead_right");
                case FacingRelativeDirection.Right:
                    return Loc.Get("room_scan_direction_right");
                case FacingRelativeDirection.BehindRight:
                    return Loc.Get("room_scan_direction_behind_right");
                case FacingRelativeDirection.Behind:
                    return Loc.Get("room_scan_direction_behind");
                case FacingRelativeDirection.BehindLeft:
                    return Loc.Get("room_scan_direction_behind_left");
                case FacingRelativeDirection.Left:
                    return Loc.Get("room_scan_direction_left");
                case FacingRelativeDirection.AheadLeft:
                default:
                    return Loc.Get("room_scan_direction_ahead_left");
            }
        }

        private static bool CanUseNavigationNow()
        {
            return string.IsNullOrEmpty(GetNavigationUnavailableReason());
        }

        private static bool ApplyNavigationInput(Vector3 moveInput, Vector3 lookInput)
        {
            if (BetterPlayerControl.Instance == null)
                return false;

            EnsureReflectionCache();
            if (_betterPlayerControlMoveField == null || _betterPlayerControlLookField == null)
                return false;

            _betterPlayerControlMoveField.SetValue(BetterPlayerControl.Instance, moveInput);
            _betterPlayerControlLookField.SetValue(BetterPlayerControl.Instance, lookInput);
            return true;
        }


        private void LogNavigationTargetDebug(string snapshot)
        {
            if (!ShouldLogNavigationDebugSnapshot(snapshot, _lastNavigationTargetDebugSnapshot))
                return;

            _lastNavigationTargetDebugSnapshot = snapshot;
            DebugLogger.Log(LogCategory.State, "AccessibilityWatcher", snapshot);
        }

        private void LogNavigationAutoWalkDebug(string snapshot)
        {
            if (!ShouldLogNavigationDebugSnapshot(snapshot, _lastNavigationAutoWalkDebugSnapshot))
                return;

            _lastNavigationAutoWalkDebugSnapshot = snapshot;
            DebugLogger.Log(LogCategory.State, "AccessibilityWatcher", snapshot);
        }

        private void LogNavigationTransitionDebug(string snapshot)
        {
            if (!ShouldLogNavigationDebugSnapshot(snapshot, _lastNavigationTransitionDebugSnapshot))
                return;

            _lastNavigationTransitionDebugSnapshot = snapshot;
            DebugLogger.Log(LogCategory.State, "AccessibilityWatcher", snapshot);
        }

        private static bool ShouldLogNavigationDebugSnapshot(string snapshot, string lastSnapshot)
        {
            if (string.IsNullOrEmpty(snapshot) ||
                string.Equals(snapshot, lastSnapshot, StringComparison.Ordinal))
            {
                return false;
            }

            return Main.DebugMode || IsForcedNavigationDiagnosticSnapshot(snapshot);
        }

        private static bool IsForcedNavigationDiagnosticSnapshot(string snapshot)
        {
            return !string.IsNullOrEmpty(snapshot) &&
                (snapshot.IndexOf("dining_room->piano_room", StringComparison.Ordinal) >= 0 ||
                 snapshot.IndexOf("office->office_closet", StringComparison.Ordinal) >= 0 ||
                 snapshot.IndexOf("auto-walk loop detected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 snapshot.IndexOf("Auto-walk loop detector triggered", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FormatVector3(Vector3 value)
        {
            return "(" +
                value.x.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                value.y.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                value.z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        private static string DescribeInteractable(InteractableObj interactable)
        {
            if (interactable == null)
                return "<null>";

            string label = GetObjectFacingDisplayName(interactable);
            string internalName = NormalizeText(interactable.InternalName());
            return "name=" + interactable.name +
                " id=" + (interactable.Id ?? "<null>") +
                " internal=" + (internalName ?? "<null>") +
                " label=" + (label ?? "<null>") +
                " position=" + FormatVector3(interactable.transform.position);
        }

        private static string GetNavigationUnavailableReason()
        {
            if (BetterPlayerControl.Instance == null)
                return "BetterPlayerControl missing";

            if (Singleton<GameController>.Instance == null)
                return "GameController missing";

            if (Singleton<GameController>.Instance.viewState != VIEW_STATE.HOUSE)
                return "viewState=" + Singleton<GameController>.Instance.viewState;

            if (BetterPlayerControl.Instance.STATE != BetterPlayerControl.PlayerState.CanControl)
                return "playerState=" + BetterPlayerControl.Instance.STATE;

            if (Singleton<PhoneManager>.Instance != null)
            {
                if (Singleton<PhoneManager>.Instance.IsPhoneMenuOpened())
                    return "phone menu open";

                if (Singleton<PhoneManager>.Instance.IsPhoneAnimating())
                    return "phone animating";
            }

            if (TalkingUI.Instance != null && TalkingUI.Instance.open)
                return "dialogue open";

            if (Popup.Instance != null && Popup.Instance.IsPopupOpen())
                return "popup open";

            if (UIDialogManager.Instance != null && UIDialogManager.Instance.HasActiveDialogs)
                return "ui dialog open";

            if (ModConfig.IsMenuOpen)
                return "accessibility menu open";

            return null;
        }

        private static string BuildNavigationTargetLabel(string zoneName, string currentZone)
        {
            string normalizedZone = NormalizeIdentifierName(zoneName);
            if (string.IsNullOrEmpty(normalizedZone))
                normalizedZone = zoneName;

            if (!string.IsNullOrEmpty(currentZone) && string.Equals(zoneName, currentZone, StringComparison.OrdinalIgnoreCase))
                return Loc.Get("navigation_target_in_current_room") + ". " + normalizedZone;

            return normalizedZone;
        }

        private static string GetCurrentZoneNameInternal()
        {
            if (Singleton<CameraSpaces>.Instance == null)
                return null;

            triggerzone zone = Singleton<CameraSpaces>.Instance.PlayerZone();
            return zone != null ? zone.Name : null;
        }

        private void HandleChoiceKeyboardInput()
        {
            IList<Button> chatChoices = GetActiveChatChoices();
            if (ShouldHandleChatChoiceKeyboardInput(chatChoices) && HandleChatChoiceKeyboardInput(chatChoices))
                return;

            ClearVirtualChatChoiceState();

            IList<Button> dialogueChoices = GetActiveDialogueChoices();
            if (ShouldHandleDialogueChoiceKeyboardInput(dialogueChoices))
                HandleChoiceKeyboardInput(dialogueChoices);
        }

        private bool HandleChoiceKeyboardInput(IList<Button> choices)
        {
            if (choices == null || choices.Count == 0)
                return false;

            int currentIndex = GetCurrentChoiceIndex(choices);
            bool hasMultipleChoices = choices.Count > 1;
            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.UpArrow, VkUp, ref _choiceUpWasDown))
            {
                int targetIndex = currentIndex >= 0 ? (currentIndex + choices.Count - 1) % choices.Count : choices.Count - 1;
                FocusChoice(choices[targetIndex], ControllerMenuUI.Direction.Up);
                return true;
            }

            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.LeftArrow, VkLeft, ref _choiceLeftWasDown))
            {
                int targetIndex = currentIndex >= 0 ? (currentIndex + choices.Count - 1) % choices.Count : choices.Count - 1;
                FocusChoice(choices[targetIndex], ControllerMenuUI.Direction.Left);
                return true;
            }

            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.DownArrow, VkDown, ref _choiceDownWasDown))
            {
                int targetIndex = currentIndex >= 0 ? (currentIndex + 1) % choices.Count : 0;
                FocusChoice(choices[targetIndex], ControllerMenuUI.Direction.Down);
                return true;
            }

            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.RightArrow, VkRight, ref _choiceRightWasDown))
            {
                int targetIndex = currentIndex >= 0 ? (currentIndex + 1) % choices.Count : 0;
                FocusChoice(choices[targetIndex], ControllerMenuUI.Direction.Right);
                return true;
            }

            if (WasChoiceKeyPressed(KeyCode.Return, VkReturn, ref _choiceReturnWasDown) ||
                WasChoiceKeyPressed(KeyCode.KeypadEnter, VkReturn, ref _choiceReturnWasDown) ||
                WasChoiceKeyPressed(KeyCode.Space, VkSpace, ref _choiceSpaceWasDown))
            {
                if (currentIndex >= 0)
                {
                    ActivateChoice(choices[currentIndex]);
                    return true;
                }
            }

            return false;
        }

        private bool HandleChatChoiceKeyboardInput(IList<Button> choices)
        {
            if (choices == null || choices.Count == 0)
                return false;

            string contextKey = GetActiveChatChoiceContextKey();
            if (string.IsNullOrEmpty(contextKey) || !string.Equals(_virtualChatChoiceContextKey, contextKey, StringComparison.Ordinal))
            {
                _virtualChatChoiceContextKey = contextKey;
                _virtualChatChoiceIndex = GetCurrentChoiceIndex(choices, allowVirtualChatFallback: false);
            }

            int currentIndex = GetCurrentChoiceIndex(choices);
            bool hasCurrentSelection = currentIndex >= 0 && currentIndex < choices.Count;
            bool hasMultipleChoices = choices.Count > 1;
            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.UpArrow, VkUp, ref _choiceUpWasDown))
            {
                int targetIndex = hasCurrentSelection ? (currentIndex + choices.Count - 1) % choices.Count : choices.Count - 1;
                FocusChatChoice(targetIndex, choices, ControllerMenuUI.Direction.Up);
                return true;
            }

            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.LeftArrow, VkLeft, ref _choiceLeftWasDown))
            {
                int targetIndex = hasCurrentSelection ? (currentIndex + choices.Count - 1) % choices.Count : choices.Count - 1;
                FocusChatChoice(targetIndex, choices, ControllerMenuUI.Direction.Left);
                return true;
            }

            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.DownArrow, VkDown, ref _choiceDownWasDown))
            {
                int targetIndex = hasCurrentSelection ? (currentIndex + 1) % choices.Count : 0;
                FocusChatChoice(targetIndex, choices, ControllerMenuUI.Direction.Down);
                return true;
            }

            if (hasMultipleChoices && WasChoiceKeyPressed(KeyCode.RightArrow, VkRight, ref _choiceRightWasDown))
            {
                int targetIndex = hasCurrentSelection ? (currentIndex + 1) % choices.Count : 0;
                FocusChatChoice(targetIndex, choices, ControllerMenuUI.Direction.Right);
                return true;
            }

            if (WasChoiceKeyPressed(KeyCode.Return, VkReturn, ref _choiceReturnWasDown) ||
                WasChoiceKeyPressed(KeyCode.KeypadEnter, VkReturn, ref _choiceReturnWasDown) ||
                WasChoiceKeyPressed(KeyCode.Space, VkSpace, ref _choiceSpaceWasDown))
            {
                if (hasCurrentSelection)
                {
                    ActivateChoice(choices[currentIndex]);
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldHandleDialogueChoiceKeyboardInput(IList<Button> choices)
        {
            if (choices == null || choices.Count == 0 || TalkingUI.Instance == null || !TalkingUI.Instance.open)
                return false;

            GameObject selectedObject = GetCurrentSelectedObject();
            if (selectedObject == null)
                return true;

            if (selectedObject == TalkingUI.Instance.gameObject || selectedObject.transform.IsChildOf(TalkingUI.Instance.transform))
                return true;

            return GetCurrentChoiceIndex(choices) >= 0;
        }

        private static bool ShouldHandleChatChoiceKeyboardInput(IList<Button> choices)
        {
            if (choices == null || choices.Count == 0 || ChatMaster.Instance == null)
                return false;

            if (!TryGetActiveChatContext(out ChatType activeChatType, out _, out _, out _, out GameObject activePanelNameObject, out GameObject secondaryPanelObject))
                return false;

            GameObject selectedObject = GetCurrentSelectedObject();
            if (selectedObject == null)
                return true;

            if (GetCurrentChoiceIndex(choices) >= 0)
                return true;

            return IsWithinChatPanel(selectedObject, activeChatType, activePanelNameObject, secondaryPanelObject);
        }

        private void AnnounceScreenSummaryIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastScreenSummary = null;
                return;
            }

            string summary = BuildScreenSummary();
            if (summary == _lastScreenSummary)
                return;

            _lastScreenSummary = summary;
            if (!string.IsNullOrEmpty(summary))
                ScreenReader.Say(summary, interrupt: false);
        }

        private void AnnounceRoomIfNeeded()
        {
            if (!ModConfig.ReadRoomChanges)
            {
                _lastRoomName = null;
                return;
            }

            if (Singleton<GameController>.Instance == null || Singleton<GameController>.Instance.viewState != VIEW_STATE.HOUSE)
            {
                _lastRoomName = null;
                return;
            }

            if (Singleton<PhoneManager>.Instance != null && Singleton<PhoneManager>.Instance.IsPhoneMenuOpened())
                return;

            string roomName = GetCurrentRoomName();
            if (string.IsNullOrEmpty(roomName) || roomName == _lastRoomName)
                return;

            _lastRoomName = roomName;
            ScreenReader.Say(Loc.Get("room_announcement", roomName), interrupt: false);
        }

        private void AnnounceInteractableIfNeeded()
        {
            if (!ModConfig.ReadNearbyObjects)
            {
                _lastInteractableId = null;
                return;
            }

            if (Singleton<GameController>.Instance == null || Singleton<GameController>.Instance.viewState != VIEW_STATE.HOUSE)
            {
                _lastInteractableId = null;
                return;
            }

            if (Singleton<InteractableManager>.Instance == null)
                return;

            InteractableObj interactable = Singleton<InteractableManager>.Instance.activeObject;
            if (interactable == null)
            {
                _lastInteractableId = null;
                return;
            }

            string identifier = interactable.Id;
            if (identifier == _lastInteractableId)
                return;

            _lastInteractableId = identifier;
            string name = GetInteractableDisplayName(interactable);
            string prompt = NormalizeText(interactable.InteractionPrompt);
            string announcement = string.IsNullOrEmpty(prompt)
                ? Loc.Get("nearby_announcement_without_prompt", name)
                : Loc.Get("nearby_announcement_with_prompt", name, prompt);
            ScreenReader.Say(announcement, interrupt: false);
        }

        private void AnnounceDateviatorsStateIfNeeded()
        {
            if (!ModConfig.ReadStatusChanges)
                return;

            if (Singleton<Dateviators>.Instance == null)
                return;

            bool equipped = Singleton<Dateviators>.Instance.IsEquipped;
            int charges = Singleton<Dateviators>.Instance.GetCurrentCharges();
            if (_lastDateviatorsEquipped == equipped && _lastDateviatorsCharges == charges)
                return;

            bool hadPreviousState = _lastDateviatorsEquipped.HasValue;
            _lastDateviatorsEquipped = equipped;
            _lastDateviatorsCharges = charges;

            if (!hadPreviousState)
                return;

            string status = Loc.Get(equipped ? "dateviators_equipped" : "dateviators_unequipped");
            ScreenReader.Say(Loc.Get("dateviators_state", status, charges), interrupt: false);
        }

        private void AnnounceDialogueIfNeeded()
        {
            if (!ModConfig.ReadDialogueText)
            {
                _lastAnnouncedDialogue = null;
                return;
            }

            if (TalkingUI.Instance == null || !TalkingUI.Instance.open)
            {
                _lastAnnouncedDialogue = null;
                return;
            }

            string speakerName;
            string dialogText;
            if (!TryGetCurrentDialogue(out speakerName, out dialogText))
                return;

            dialogText = NormalizeText(dialogText);
            speakerName = NormalizeText(speakerName);
            if (string.IsNullOrEmpty(dialogText))
                return;

            string combined = string.IsNullOrEmpty(speakerName) ? dialogText : speakerName + ". " + dialogText;
            if (combined == _lastAnnouncedDialogue)
                return;

            _lastAnnouncedDialogue = combined;
            ScreenReader.Say(combined, rememberAsRepeatable: true);
        }

        private void AnnounceSelectionIfNeeded()
        {
            if (!ModConfig.ReadFocusedItems && !ModConfig.ReadDialogueChoices)
            {
                _lastSelectedObjectId = 0;
                _lastAnnouncedSelection = null;
                return;
            }

            GameObject rawSelectedObject;
            GameObject selectedObject;
            string selectionSource;
            if (!TryGetCurrentSelectedObjectInfo(out rawSelectedObject, out selectedObject, out selectionSource))
            {
                _lastSelectedObjectId = 0;
                _lastAnnouncedSelection = null;
                TraceSelectionDebug(null, null, null, null, null, "no_selection");
                return;
            }

            if (TryPreemptSingleButtonUIDialogSelection(rawSelectedObject, selectedObject, selectionSource))
                return;

            if (ShouldSuppressDateADexOpenEntrySelection(selectedObject))
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, "dateadex_open_entry_focus", null, "suppressed_dateadex_open");
                return;
            }

            if (ShouldSuppressDateADexSelection(selectedObject))
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, "dateadex_pending_detail", null, "suppressed_dateadex");
                return;
            }

            if (ShouldSuppressChatSelection(selectedObject))
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, "chat_pending_detail", null, "suppressed_chat");
                return;
            }

            string branch;
            string announcement = BuildSelectionAnnouncement(selectedObject, out branch);

            if (string.IsNullOrEmpty(announcement))
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, branch, null, "no_announcement");
                return;
            }

            int objectId = selectedObject.GetInstanceID();
            if (ShouldSuppressPopupSelection(selectedObject) ||
                ShouldSuppressUIDialogSelection(selectedObject) ||
                ShouldSuppressSpecsSelection(selectedObject) ||
                ShouldSuppressCreditsSelection(selectedObject))
            {
                // UI overlays often auto-focus a default button, so consume that focus and keep the main content audible.
                _lastSelectedObjectId = objectId;
                _lastAnnouncedSelection = announcement;
                string suppressionReason = ShouldSuppressPopupSelection(selectedObject)
                    ? "suppressed_popup"
                    : ShouldSuppressUIDialogSelection(selectedObject)
                        ? "suppressed_uidialog"
                        : ShouldSuppressSpecsSelection(selectedObject)
                            ? "suppressed_specs"
                            : "suppressed_credits";
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, branch, announcement, suppressionReason);
                return;
            }

            if (branch == "new_game_input" && objectId == _lastSelectedObjectId)
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, branch, announcement, "suppressed_live_input_echo");
                return;
            }

            if (objectId == _lastSelectedObjectId && announcement == _lastAnnouncedSelection)
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, branch, announcement, "duplicate");
                return;
            }

            _lastSelectedObjectId = objectId;
            _lastAnnouncedSelection = announcement;
            TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, branch, announcement, "spoken");
            bool isRepeatableChatSelection = branch == "chat" || branch == "chat_choice";
            ScreenReader.Say(announcement, rememberAsRepeatable: isRepeatableChatSelection);
        }

        private bool ShouldSuppressDateADexSelection(GameObject selectedObject)
        {
            if (!ModConfig.ReadPhoneAppText)
                return false;

            if (selectedObject == null || Time.unscaledTime >= _suppressDateADexSelectionUntil)
            {
                if (!TryBuildDateADexDetailAnnouncement(out string pendingAnnouncement) || string.IsNullOrEmpty(pendingAnnouncement))
                    return false;

                if (pendingAnnouncement == _lastDateADexDetail)
                    return false;
            }

            if (DateADex.Instance == null || DateADex.Instance.DateADexWindow == null || !DateADex.Instance.DateADexWindow.activeInHierarchy)
                return false;

            bool isRecipeVisible = DateADex.Instance.RecipeScreen != null && DateADex.Instance.RecipeScreen.activeInHierarchy;
            if (!DateADex.Instance.IsInEntryScreen && !isRecipeVisible)
                return false;

            return selectedObject.transform.IsChildOf(DateADex.Instance.DateADexWindow.transform);
        }

        private bool ShouldSuppressChatSelection(GameObject selectedObject)
        {
            if (!ModConfig.ReadPhoneAppText)
                return false;

            if (selectedObject == null)
                return false;

            if (IsChatChoiceObject(selectedObject))
                return false;

            if (selectedObject.GetComponentInParent<ChatButton>() != null)
                return false;

            if (!TryBuildChatAppAnnouncement(out string pendingAnnouncement, out string activeChatKey) ||
                string.IsNullOrEmpty(pendingAnnouncement) ||
                string.IsNullOrEmpty(activeChatKey))
            {
                return false;
            }

            return IsChatSelectionObject(selectedObject);
        }

        private static bool IsChatSelectionObject(GameObject selectedObject)
        {
            if (selectedObject == null || ChatMaster.Instance == null)
                return false;

            if (IsChatChoiceObject(selectedObject))
                return true;

            if (selectedObject.GetComponentInParent<ChatButton>() != null)
                return true;

            ChatType activeChatType;
            List<ParallelChat> chats;
            ParallelChat activeChat;
            string appName;
            GameObject activePanelNameObject;
            GameObject secondaryPanelObject;
            if (!TryGetActiveChatContext(out activeChatType, out chats, out activeChat, out appName, out activePanelNameObject, out secondaryPanelObject))
                return false;

            return IsWithinChatPanel(selectedObject, activeChatType, activePanelNameObject, secondaryPanelObject);
        }

        private void AnnounceResultScreenIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastResultDetail = null;
                return;
            }

            string announcement;
            if (!TryBuildResultAnnouncement(out announcement))
            {
                _lastResultDetail = null;
                return;
            }

            if (announcement == _lastResultDetail)
                return;

            _lastResultDetail = announcement;
            ScreenReader.Say(announcement);
        }

        private void AnnouncePhoneAppContentIfNeeded()
        {
            if (!ModConfig.ReadPhoneAppText)
            {
                _lastPhoneAppContentAnnouncement = null;
                _lastPhoneAppContentKey = null;
                return;
            }

            string announcement;
            string contentKey;
            if (!TryBuildPhoneAppContentAnnouncement(out announcement, out contentKey))
            {
                _lastPhoneAppContentAnnouncement = null;
                _lastPhoneAppContentKey = null;
                return;
            }

            bool appChanged = !string.Equals(contentKey, _lastPhoneAppContentKey, StringComparison.Ordinal);
            if (appChanged)
            {
                _lastPhoneAppContentKey = contentKey;
                _lastPhoneAppContentAnnouncement = null;
            }

            if (announcement == _lastPhoneAppContentAnnouncement)
                return;

            _lastPhoneAppContentKey = contentKey;
            _lastPhoneAppContentAnnouncement = announcement;
            if (TryBuildDateADexDetailAnnouncement(out string currentDateADexDetail) &&
                string.Equals(currentDateADexDetail, announcement, StringComparison.Ordinal))
            {
                _suppressDateADexSelectionUntil = Time.unscaledTime + 0.75f;
            }

            bool isChatAnnouncement = contentKey.IndexOf("|chat|", StringComparison.Ordinal) >= 0;
            ScreenReader.Say(announcement, rememberAsRepeatable: isChatAnnouncement);
        }

        private static bool TryGetCurrentPhoneAppKey(out string contentKey)
        {
            contentKey = null;

            if (Singleton<PhoneManager>.Instance == null ||
                !Singleton<PhoneManager>.Instance.IsPhoneMenuOpened() ||
                !Singleton<PhoneManager>.Instance.IsPhoneAppOpened())
            {
                return false;
            }

            GameObject currentApp = Singleton<PhoneManager>.Instance.GetCurrentApp();
            if (currentApp == null || !currentApp.activeInHierarchy)
                return false;

            contentKey = currentApp.GetInstanceID().ToString();
            return true;
        }

        private void AnnouncePopupIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastPopupAnnouncement = null;
                return;
            }

            string announcement;
            if (!TryBuildPopupAnnouncement(out announcement))
            {
                _lastPopupAnnouncement = null;
                return;
            }

            if (announcement == _lastPopupAnnouncement)
                return;

            _lastPopupAnnouncement = announcement;
            _suppressPopupSelectionUntil = Time.unscaledTime + PopupSelectionSuppressionSeconds;
            ScreenReader.Say(announcement);
        }

        private void AnnounceTutorialIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastTutorialAnnouncement = null;
                return;
            }

            string announcement;
            if (!TryBuildTutorialAnnouncement(out announcement))
            {
                _lastTutorialAnnouncement = null;
                return;
            }

            if (announcement == _lastTutorialAnnouncement)
                return;

            _lastTutorialAnnouncement = announcement;
            ScreenReader.Say(announcement);
        }

        private void AnnounceUIDialogIfNeeded()
        {
            string announcement;
            if (!TryBuildUIDialogAnnouncement(out announcement))
            {
                _lastUIDialogAnnouncement = null;
                return;
            }

            if (announcement == _lastUIDialogAnnouncement)
                return;

            _lastUIDialogAnnouncement = announcement;
            _suppressUIDialogSelectionUntil = Time.unscaledTime + UIDialogSelectionSuppressionSeconds;
            ScreenReader.Say(announcement, interrupt: true);
        }

        private void AnnounceSpecsDetailIfNeeded()
        {
            string announcement;
            SpecsAnnouncementMode mode;
            if (!TryBuildSpecsAnnouncement(out announcement, out mode))
            {
                _lastSpecsAnnouncement = null;
                _lastSpecsAnnouncementMode = SpecsAnnouncementMode.None;
                return;
            }

            if (mode == SpecsAnnouncementMode.Stats && _lastSpecsAnnouncementMode == SpecsAnnouncementMode.Tooltip)
            {
                _lastSpecsAnnouncement = announcement;
                _lastSpecsAnnouncementMode = mode;
                return;
            }

            if (announcement == _lastSpecsAnnouncement && mode == _lastSpecsAnnouncementMode)
                return;

            _lastSpecsAnnouncement = announcement;
            _lastSpecsAnnouncementMode = mode;
            _suppressSpecsSelectionUntil = Time.unscaledTime + SpecsSelectionSuppressionSeconds;
            ScreenReader.Say(announcement);
        }

        private void AnnounceCreditsIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastCreditsAnnouncement = null;
                return;
            }

            string announcement;
            if (!TryBuildCreditsAnnouncement(out announcement))
            {
                _lastCreditsAnnouncement = null;
                return;
            }

            if (announcement == _lastCreditsAnnouncement)
                return;

            _lastCreditsAnnouncement = announcement;
            _suppressCreditsSelectionUntil = Time.unscaledTime + CreditsSelectionSuppressionSeconds;
            ScreenReader.Say(announcement);
        }

        private void AnnounceSubtitleIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastSubtitleAnnouncement = null;
                return;
            }

            string announcement;
            if (!TryBuildSubtitleAnnouncement(out announcement))
            {
                _lastSubtitleAnnouncement = null;
                return;
            }

            if (announcement == _lastSubtitleAnnouncement)
                return;

            _lastSubtitleAnnouncement = announcement;
            ScreenReader.Say(announcement, interrupt: false);
        }

        private void AnnounceEngagementIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastEngagementAnnouncement = null;
                return;
            }

            string announcement;
            if (!TryBuildEngagementAnnouncement(out announcement))
            {
                _lastEngagementAnnouncement = null;
                return;
            }

            if (announcement == _lastEngagementAnnouncement)
                return;

            _lastEngagementAnnouncement = announcement;
            ScreenReader.Say(announcement);
        }

        private void AnnounceLoadingIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastLoadingAnnouncement = null;
                return;
            }

            string announcement;
            if (!TryBuildLoadingAnnouncement(out announcement))
            {
                _lastLoadingAnnouncement = null;
                return;
            }

            if (announcement == _lastLoadingAnnouncement)
                return;

            _lastLoadingAnnouncement = announcement;
            ScreenReader.Say(announcement, interrupt: false, rememberAsRepeatable: true);
        }

        private void AnnounceExamineIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastExamineAnnouncement = null;
                return;
            }

            string announcement;
            if (!TryBuildExamineAnnouncement(out announcement))
            {
                _lastExamineAnnouncement = null;
                return;
            }

            if (announcement == _lastExamineAnnouncement)
                return;

            _lastExamineAnnouncement = announcement;
            ScreenReader.Say(announcement, rememberAsRepeatable: true);
        }

        private void AnnounceTimeChangeIfNeeded()
        {
            if (!ModConfig.ReadStatusChanges)
                return;

            if (Singleton<DayNightCycle>.Instance == null)
                return;

            DayPhase currentPhase = Singleton<DayNightCycle>.Instance.GetTime();
            if (_lastDayPhase.HasValue && _lastDayPhase.Value == currentPhase)
                return;

            bool hadPreviousPhase = _lastDayPhase.HasValue;
            _lastDayPhase = currentPhase;
            if (!hadPreviousPhase)
                return;

            ScreenReader.Say(Loc.Get("time_announcement", NormalizeIdentifierName(currentPhase.ToString())), interrupt: false);
        }

        private void AnnounceProgressionChangesIfNeeded()
        {
            if (!ModConfig.ReadStatusChanges)
                return;

            if (Singleton<Save>.Instance == null)
                return;

            int unlockedCollectables = Singleton<Save>.Instance.GetTotalUnlockedCollectables(addDeluxeEdition: true);
            int metCount = Singleton<Save>.Instance.AvailableTotalMetDatables();
            int friendCount = Singleton<Save>.Instance.AvailableTotalFriendEndings();
            int loveCount = Singleton<Save>.Instance.AvailableTotalLoveEndings();
            int hateCount = Singleton<Save>.Instance.AvailableTotalHateEndings();
            int realizedCount = Singleton<Save>.Instance.AvailableTotalRealizedDatables();

            bool firstSample = _lastUnlockedCollectables < 0;

            if (!firstSample && unlockedCollectables > _lastUnlockedCollectables)
            {
                ScreenReader.Say(Loc.Get("collectable_unlocked", unlockedCollectables), interrupt: false);
            }

            if (!firstSample && metCount > _lastMetCount)
            {
                ScreenReader.Say(Loc.Get("dateable_added", metCount), interrupt: false);
            }

            if (!firstSample && friendCount > _lastFriendCount)
            {
                ScreenReader.Say(Loc.Get("friend_ending_recorded", friendCount), interrupt: false);
            }

            if (!firstSample && loveCount > _lastLoveCount)
            {
                ScreenReader.Say(Loc.Get("love_ending_recorded", loveCount), interrupt: false);
            }

            if (!firstSample && hateCount > _lastHateCount)
            {
                ScreenReader.Say(Loc.Get("hate_ending_recorded", hateCount), interrupt: false);
            }

            if (!firstSample && realizedCount > _lastRealizedCount)
            {
                ScreenReader.Say(Loc.Get("realized_ending_recorded", realizedCount), interrupt: false);
            }

            _lastUnlockedCollectables = unlockedCollectables;
            _lastMetCount = metCount;
            _lastFriendCount = friendCount;
            _lastLoveCount = loveCount;
            _lastHateCount = hateCount;
            _lastRealizedCount = realizedCount;
        }

        private bool TrySpeakCurrentRepeatableText()
        {
            if (TryBuildCurrentRepeatableAnnouncement(out string announcement))
            {
                ScreenReader.Say(announcement, rememberAsRepeatable: true);
                return true;
            }

            return false;
        }

        private bool TryBuildCurrentRepeatableAnnouncement(out string announcement)
        {
            announcement = null;

            GameObject selectedObject = GetCurrentSelectedObject();
            int choiceIndex;
            int choiceCount;
            string choiceText;
            if (selectedObject != null &&
                TryGetChatChoiceSpeechInfo(selectedObject, out choiceIndex, out choiceCount, out choiceText))
            {
                if (!string.IsNullOrEmpty(choiceText))
                {
                    announcement = Loc.Get("choice_announcement", choiceIndex, choiceCount, choiceText);
                    return true;
                }
            }

            if (selectedObject != null &&
                TryBuildChatSelectionAnnouncement(selectedObject, out announcement) &&
                !string.IsNullOrEmpty(announcement))
            {
                return true;
            }

            if (TryBuildCurrentDialogueAnnouncement(out announcement) ||
                TryBuildPopupAnnouncement(out announcement) ||
                TryBuildTutorialAnnouncement(out announcement) ||
                TryBuildSubtitleAnnouncement(out announcement) ||
                TryBuildEngagementAnnouncement(out announcement) ||
                TryBuildLoadingAnnouncement(out announcement) ||
                TryBuildExamineAnnouncement(out announcement) ||
                TryBuildUIDialogAnnouncement(out announcement) ||
                TryBuildSpecsAnnouncement(out announcement, out SpecsAnnouncementMode _) ||
                TryBuildPhoneAppContentAnnouncement(out announcement, out string _) ||
                TryBuildCreditsAnnouncement(out announcement) ||
                TryBuildResultAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            announcement = BuildScreenSummary();
            return !string.IsNullOrEmpty(announcement);
        }

        private bool ShouldSuppressPopupSelection(GameObject selectedObject)
        {
            if (Time.unscaledTime >= _suppressPopupSelectionUntil || Popup.Instance == null || !Popup.Instance.IsPopupOpen())
                return false;

            ChatButton popupButton = selectedObject.GetComponentInParent<ChatButton>();
            return popupButton != null && Popup.Instance.IsPopupButton(popupButton.gameObject);
        }

        private bool ShouldSuppressUIDialogSelection(GameObject selectedObject)
        {
            if (selectedObject == null)
                return false;

            if (!TryGetTopUIDialog(out UIDialog dialog))
                return false;

            GameObject dialogObject = _uiDialogGameObjectField != null ? _uiDialogGameObjectField.GetValue(dialog) as GameObject : null;
            bool isWithinDialog = dialogObject != null &&
                (selectedObject == dialogObject || selectedObject.transform.IsChildOf(dialogObject.transform));

            ChatButton dialogButton = selectedObject.GetComponentInParent<ChatButton>();
            bool isDialogButton = dialogButton != null && dialog.IsDialogButton(dialogButton.gameObject);

            if (!isWithinDialog && !isDialogButton)
                return false;

            int activeButtonCount = 0;
            UIDialogButton[] buttons = dialog.Buttons;
            if (buttons != null)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] != null && buttons[i].Button != null && buttons[i].Button.gameObject.activeInHierarchy)
                        activeButtonCount++;
                }
            }

            if (activeButtonCount <= 1)
                return true;

            return Time.unscaledTime < _suppressUIDialogSelectionUntil;
        }

        private bool TryPreemptSingleButtonUIDialogSelection(GameObject rawSelectedObject, GameObject selectedObject, string selectionSource)
        {
            if (selectedObject == null)
                return false;

            if (!TryGetTopUIDialog(out UIDialog dialog))
                return false;

            GameObject dialogObject = _uiDialogGameObjectField != null ? _uiDialogGameObjectField.GetValue(dialog) as GameObject : null;
            if (dialogObject == null || !dialogObject.activeInHierarchy)
                return false;

            bool isWithinDialog = selectedObject == dialogObject || selectedObject.transform.IsChildOf(dialogObject.transform);
            ChatButton dialogButton = selectedObject.GetComponentInParent<ChatButton>();
            bool isDialogButton = dialogButton != null && dialog.IsDialogButton(dialogButton.gameObject);
            if (!isWithinDialog && !isDialogButton)
                return false;

            if (GetActiveUIDialogButtonCount(dialog) > 1)
                return false;

            if (!TryBuildUIDialogAnnouncement(out string announcement) || string.IsNullOrEmpty(announcement))
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, "uidialog_single_button", null, "preempted_without_dialog_text");
                return true;
            }

            if (announcement != _lastUIDialogAnnouncement)
            {
                _lastUIDialogAnnouncement = announcement;
                _suppressUIDialogSelectionUntil = Time.unscaledTime + UIDialogSelectionSuppressionSeconds;
                _lastSelectedObjectId = selectedObject.GetInstanceID();
                _lastAnnouncedSelection = announcement;
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, "uidialog_single_button", announcement, "preempted_and_spoken");
                ScreenReader.Say(announcement, interrupt: true);
            }
            else
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, "uidialog_single_button", announcement, "preempted_duplicate_dialog");
            }

            return true;
        }

        private bool ShouldSuppressSpecsSelection(GameObject selectedObject)
        {
            if (ShouldSuppressSpecsAnnouncements())
            {
                return selectedObject != null &&
                    SpecStatMain.Instance != null &&
                    SpecStatMain.Instance.visible &&
                    selectedObject.transform.IsChildOf(SpecStatMain.Instance.transform);
            }

            return selectedObject != null &&
                Time.unscaledTime < _suppressSpecsSelectionUntil &&
                SpecStatMain.Instance != null &&
                SpecStatMain.Instance.visible &&
                selectedObject.transform.IsChildOf(SpecStatMain.Instance.transform);
        }

        private bool ShouldSuppressCreditsSelection(GameObject selectedObject)
        {
            if (selectedObject == null || Time.unscaledTime >= _suppressCreditsSelectionUntil)
                return false;

            if (!TryGetActiveCreditsScreen(out CreditsScreen creditsScreen))
                return false;

            return selectedObject.transform.IsChildOf(creditsScreen.transform);
        }

        private static bool TryGetCurrentSelectedObjectInfo(out GameObject rawSelectedObject, out GameObject resolvedSelectedObject, out string selectionSource)
        {
            rawSelectedObject = null;
            resolvedSelectedObject = null;
            selectionSource = null;

            if (Singleton<ControllerMenuUI>.Instance != null)
            {
                rawSelectedObject = ControllerMenuUI.GetCurrentSelectedControl();
                if (rawSelectedObject != null)
                    selectionSource = "ControllerMenuUI";
            }

            if (rawSelectedObject == null && EventSystem.current != null)
            {
                rawSelectedObject = EventSystem.current.currentSelectedGameObject;
                if (rawSelectedObject != null)
                    selectionSource = "EventSystem";
            }

            if (rawSelectedObject == null || !rawSelectedObject.activeInHierarchy)
                return false;

            resolvedSelectedObject = ResolveSelectableTarget(rawSelectedObject);
            if (resolvedSelectedObject == null || !resolvedSelectedObject.activeInHierarchy)
                return false;

            if (!ReferenceEquals(rawSelectedObject, resolvedSelectedObject))
                selectionSource = string.IsNullOrEmpty(selectionSource) ? "Resolved" : selectionSource + " -> Resolved";

            return true;
        }

        private static GameObject GetCurrentSelectedObject()
        {
            GameObject rawSelectedObject;
            GameObject resolvedSelectedObject;
            string selectionSource;
            return TryGetCurrentSelectedObjectInfo(out rawSelectedObject, out resolvedSelectedObject, out selectionSource)
                ? resolvedSelectedObject
                : null;
        }

        private static string BuildSelectionAnnouncement(GameObject selectedObject, out string branch)
        {
            branch = null;
            string specialAnnouncement;
            if (TryBuildSettingsSelectionAnnouncement(selectedObject, out specialAnnouncement))
            {
                branch = "settings";
                return specialAnnouncement;
            }

            if (TryBuildValidateQuestionsSelectionAnnouncement(selectedObject, out specialAnnouncement, out branch))
                return specialAnnouncement;

            if (TryBuildUIDialogSelectionAnnouncement(selectedObject, out specialAnnouncement, out branch))
                return specialAnnouncement;

            if (TryBuildSpecsSelectionAnnouncement(selectedObject, out specialAnnouncement, out branch))
                return specialAnnouncement;

            if (TryBuildRoomersSelectionAnnouncement(selectedObject, out specialAnnouncement))
            {
                branch = "roomers";
                return specialAnnouncement;
            }

            if (TryBuildDateADexSelectionAnnouncement(selectedObject, out specialAnnouncement))
            {
                branch = "dateadex";
                return specialAnnouncement;
            }

            int choiceIndex;
            int choiceCount;
            string choiceText;
            if (TryGetChatChoiceSpeechInfo(selectedObject, out choiceIndex, out choiceCount, out choiceText))
            {
                branch = "chat_choice";
                if (!ModConfig.ReadFocusedItems && !ModConfig.ReadDialogueChoices)
                    return null;

                if (!string.IsNullOrEmpty(choiceText))
                    return Loc.Get("choice_announcement", choiceIndex, choiceCount, choiceText);
            }

            if (TryBuildChatSelectionAnnouncement(selectedObject, out specialAnnouncement))
            {
                branch = "chat";
                return specialAnnouncement;
            }

            if (TryBuildSaveSelectionAnnouncement(selectedObject, out specialAnnouncement, out branch))
                return specialAnnouncement;

            if (TryGetDialogueChoiceAnnouncement(selectedObject, out choiceIndex, out choiceCount))
            {
                branch = "dialogue_choice";
                if (!ModConfig.ReadDialogueChoices)
                    return null;

                string dialogueChoiceText = ExtractTextFromObject(selectedObject);
                if (!string.IsNullOrEmpty(dialogueChoiceText))
                    return Loc.Get("choice_announcement", choiceIndex, choiceCount, dialogueChoiceText);
            }

            if (TalkingUI.Instance != null && TalkingUI.Instance.open)
            {
                branch = "talking_ui_open";
                return null;
            }

            if (!ModConfig.ReadFocusedItems)
            {
                branch = "focused_items_disabled";
                return null;
            }

            string text = ExtractTextFromObject(selectedObject);
            if (!string.IsNullOrEmpty(text))
            {
                branch = "generic_text";
                return text;
            }

            branch = "generic_name";
            return NormalizeText(selectedObject.name.Replace("_", " "));
        }

        private void TraceSelectionDebug(GameObject rawSelectedObject, GameObject selectedObject, string selectionSource, string branch, string announcement, string outcome)
        {
            if (!Main.DebugMode || !IsSelectionDebugContextActive())
                return;

            string snapshot = "context=" + BuildSelectionDebugContext() +
                "; source=" + SafeDebugValue(selectionSource) +
                "; outcome=" + SafeDebugValue(outcome) +
                "; branch=" + SafeDebugValue(branch) +
                "; announcement=" + SafeDebugValue(announcement) +
                "; raw=" + DescribeObjectChain(rawSelectedObject) +
                "; resolved=" + DescribeObjectChain(selectedObject) +
                "; markers=" + DescribeSelectionMarkers(selectedObject);

            if (snapshot == _lastSelectionDebugSnapshot)
                return;

            _lastSelectionDebugSnapshot = snapshot;
            DebugLogger.Log(LogCategory.Handler, "AccessibilityWatcher", snapshot);
        }

        private static bool IsSelectionDebugContextActive()
        {
            return (SpecStatMain.Instance != null && SpecStatMain.Instance.visible) ||
                TryGetTopUIDialog(out UIDialog dialog) && dialog != null ||
                TryGetActiveSaveScreenManager(out SaveScreenManager saveScreenManager) && saveScreenManager != null;
        }

        private static string BuildSelectionDebugContext()
        {
            var contexts = new List<string>();

            if (SpecStatMain.Instance != null && SpecStatMain.Instance.visible)
                contexts.Add("SPECS");

            if (TryGetTopUIDialog(out UIDialog dialog) && dialog != null)
                contexts.Add("UIDialog");

            if (TryGetActiveSaveScreenManager(out SaveScreenManager saveScreenManager) && saveScreenManager != null)
                contexts.Add("SaveScreen");

            return contexts.Count > 0 ? string.Join(", ", contexts.ToArray()) : "None";
        }

        private static string DescribeObjectChain(GameObject gameObject)
        {
            if (gameObject == null)
                return "<null>";

            var parts = new List<string>();
            Transform current = gameObject.transform;
            int safety = 0;
            while (current != null && safety < 12)
            {
                parts.Add(current.name);
                current = current.parent;
                safety++;
            }

            return string.Join(" > ", parts.ToArray());
        }

        private static string DescribeSelectionMarkers(GameObject selectedObject)
        {
            if (selectedObject == null)
                return "<none>";

            var markers = new List<string>();
            AddSelectionMarker<SpecStatBlock>(markers, selectedObject, "SpecStatBlock");
            AddSelectionMarker<SpecGlossaryBlock>(markers, selectedObject, "SpecGlossaryBlock");
            AddSelectionMarker<SaveSlot>(markers, selectedObject, "SaveSlot");
            AddSelectionMarker<ChatButton>(markers, selectedObject, "ChatButton");
            AddSelectionMarker<Button>(markers, selectedObject, "Button");
            AddSelectionMarker<IsSelectableRegistered>(markers, selectedObject, "IsSelectableRegistered");
            AddSelectionMarker<TMP_Text>(markers, selectedObject, "TMP_Text");
            return markers.Count > 0 ? string.Join(", ", markers.ToArray()) : "<none>";
        }

        private static void AddSelectionMarker<T>(List<string> markers, GameObject selectedObject, string label)
            where T : Component
        {
            if (selectedObject.GetComponentInParent<T>() != null && !markers.Contains(label))
                markers.Add(label);
        }

        private static string SafeDebugValue(string value)
        {
            string normalized = NormalizeText(value);
            if (string.IsNullOrEmpty(normalized))
                return "<null>";

            if (normalized.Length > 160)
                return normalized.Substring(0, 160) + "...";

            return normalized;
        }

        private static bool TryBuildSettingsSelectionAnnouncement(GameObject selectedObject, out string announcement)
        {
            announcement = null;

            if (Singleton<CanvasUIManager>.Instance == null || Singleton<CanvasUIManager>.Instance._activeMenu == null)
                return false;

            SettingsMenu settingsMenu = Singleton<CanvasUIManager>.Instance._activeMenu.GetComponent<SettingsMenu>();
            if (settingsMenu == null || !settingsMenu.gameObject.activeInHierarchy)
                return false;

            SettingsMenuSelector selector = selectedObject.GetComponentInParent<SettingsMenuSelector>();
            if (selector != null)
            {
                string label = GetSettingsSelectorLabel(selector);
                string value = NormalizeText(selector.SelectedOption != null ? selector.SelectedOption.text : null);
                if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(value))
                    return false;

                announcement = string.IsNullOrEmpty(value) ? label : label + ". " + value;
                return !string.IsNullOrEmpty(announcement);
            }

            string sliderAnnouncement = BuildSettingsSliderAnnouncement(settingsMenu, selectedObject);
            if (!string.IsNullOrEmpty(sliderAnnouncement))
            {
                announcement = sliderAnnouncement;
                return true;
            }

            if (settingsMenu.ApplyDisplaySettingsButton != null &&
                (selectedObject == settingsMenu.ApplyDisplaySettingsButton.gameObject || selectedObject.transform.IsChildOf(settingsMenu.ApplyDisplaySettingsButton.transform)))
            {
                announcement = Loc.Get("apply_display_settings");
                return true;
            }

            return false;
        }

        private static bool TryBuildValidateQuestionsSelectionAnnouncement(GameObject selectedObject, out string announcement, out string branch)
        {
            announcement = null;
            branch = null;

            if (selectedObject == null || !IsValidateQuestionsActive() || !IsValidateQuestionsSelectionObject(selectedObject))
                return false;

            TMP_InputField inputField = selectedObject.GetComponentInParent<TMP_InputField>();
            if (IsValidateQuestionsField(inputField))
            {
                announcement = BuildValidateQuestionsInputAnnouncement(inputField);
                branch = "new_game_input";
                return !string.IsNullOrEmpty(announcement);
            }

            Toggle toggle = selectedObject.GetComponentInParent<Toggle>();
            if (toggle != null)
            {
                announcement = BuildValidateQuestionsToggleAnnouncement(toggle);
                branch = "new_game_toggle";
                return !string.IsNullOrEmpty(announcement);
            }

            return false;
        }

        private static bool IsValidateQuestionsActive()
        {
            return ValidateQuestions.Instance != null &&
                ValidateQuestions.Instance.gameObject != null &&
                ValidateQuestions.Instance.gameObject.activeInHierarchy;
        }

        private static bool IsValidateQuestionsSelectionObject(GameObject selectedObject)
        {
            return selectedObject != null &&
                ValidateQuestions.Instance != null &&
                selectedObject.transform.IsChildOf(ValidateQuestions.Instance.transform);
        }

        private static bool IsValidateQuestionsField(TMP_InputField inputField)
        {
            if (inputField == null || ValidateQuestions.Instance == null)
                return false;

            return inputField == ValidateQuestions.Instance.nameTextField ||
                inputField == ValidateQuestions.Instance.townTextField ||
                inputField == ValidateQuestions.Instance.favThingTextField;
        }

        private static string BuildValidateQuestionsInputAnnouncement(TMP_InputField inputField)
        {
            string label = GetValidateQuestionsFieldLabel(inputField);
            string value = NormalizeText(inputField != null ? inputField.text : null);
            if (string.IsNullOrEmpty(value))
                value = Loc.Get("new_game_field_empty");

            if (string.IsNullOrEmpty(label))
                return value;

            return label + ". " + value;
        }

        private static string GetValidateQuestionsFieldLabel(TMP_InputField inputField)
        {
            if (inputField == null || ValidateQuestions.Instance == null)
                return null;

            if (inputField == ValidateQuestions.Instance.nameTextField)
                return Loc.Get("new_game_field_name");

            if (inputField == ValidateQuestions.Instance.townTextField)
                return Loc.Get("new_game_field_town");

            if (inputField == ValidateQuestions.Instance.favThingTextField)
                return Loc.Get("new_game_field_favorite_thing");

            return NormalizeIdentifierName(inputField.gameObject.name);
        }

        private static string BuildValidateQuestionsToggleAnnouncement(Toggle toggle)
        {
            if (toggle == null || ValidateQuestions.Instance == null)
                return null;

            if (IsValidateQuestionsPronounToggle(toggle))
            {
                string optionLabel = GetValidateQuestionsPronounOptionLabel(toggle);
                string state = Loc.Get(toggle.isOn ? "new_game_toggle_selected" : "new_game_toggle_not_selected");
                if (string.IsNullOrEmpty(optionLabel))
                    return Loc.Get("new_game_field_pronouns") + ". " + state;

                return Loc.Get("new_game_field_pronouns") + ". " + optionLabel + ". " + state;
            }

            if (toggle == ValidateQuestions.Instance.mandatoryToggle)
            {
                string state = Loc.Get(toggle.isOn ? "settings_value_on" : "settings_value_off");
                return Loc.Get("new_game_field_confirmation") + ". " + state;
            }

            return null;
        }

        private static bool IsValidateQuestionsPronounToggle(Toggle toggle)
        {
            if (toggle == null || ValidateQuestions.Instance == null || ValidateQuestions.Instance.defaultPronoun == null)
                return false;

            ToggleGroup group = ValidateQuestions.Instance.defaultPronoun.group;
            return group != null && toggle.group == group;
        }

        private static string GetValidateQuestionsPronounOptionLabel(Toggle toggle)
        {
            string toggleName = NormalizeIdentifierName(toggle != null ? toggle.gameObject.name : null);
            if (string.IsNullOrEmpty(toggleName))
                return null;

            if (string.Equals(toggleName, "He/Him", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("new_game_pronoun_he_him");

            if (string.Equals(toggleName, "She/Her", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("new_game_pronoun_she_her");

            if (string.Equals(toggleName, "They/Them", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("new_game_pronoun_they_them");

            return toggleName;
        }

        private static bool TryBuildUIDialogSelectionAnnouncement(GameObject selectedObject, out string announcement, out string branch)
        {
            announcement = null;
            branch = null;

            if (selectedObject == null || !TryGetTopUIDialog(out UIDialog dialog))
                return false;

            EnsureReflectionCache();

            GameObject dialogObject = _uiDialogGameObjectField != null ? _uiDialogGameObjectField.GetValue(dialog) as GameObject : null;
            if (dialogObject == null || !dialogObject.activeInHierarchy)
                return false;

            if (!(selectedObject == dialogObject) && !selectedObject.transform.IsChildOf(dialogObject.transform))
                return false;

            int activeButtonCount = 0;
            UIDialogButton[] buttons = dialog.Buttons;
            if (buttons != null)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] != null && buttons[i].Button != null && buttons[i].Button.gameObject.activeInHierarchy)
                        activeButtonCount++;
                }
            }

            if (activeButtonCount <= 1 && TryBuildUIDialogAnnouncement(out announcement))
            {
                branch = "uidialog_single_button_dialog";
                return !string.IsNullOrEmpty(announcement);
            }

            ChatButton dialogButton = selectedObject.GetComponentInParent<ChatButton>();
            if (dialogButton != null)
            {
                announcement = NormalizeText(ExtractTextFromObject(dialogButton.gameObject));
                branch = "uidialog_button";
                return !string.IsNullOrEmpty(announcement);
            }

            bool matched = TryBuildUIDialogAnnouncement(out announcement);
            if (matched)
                branch = "uidialog_dialog_text";
            return matched;
        }

        private static bool TryBuildSpecsSelectionAnnouncement(GameObject selectedObject, out string announcement, out string branch)
        {
            announcement = null;
            branch = null;

            if (selectedObject == null || SpecStatMain.Instance == null || !SpecStatMain.Instance.visible)
                return false;

            if (!selectedObject.transform.IsChildOf(SpecStatMain.Instance.transform))
                return false;

            EnsureReflectionCache();

            SpecStatBlock statBlock = selectedObject.GetComponentInParent<SpecStatBlock>();
            if (statBlock != null)
            {
                announcement = BuildSpecsStatBlockAnnouncement(statBlock, includeDescription: true);
                branch = "specs_stat_block";
                return !string.IsNullOrEmpty(announcement);
            }

            SpecGlossaryBlock glossaryBlock = selectedObject.GetComponentInParent<SpecGlossaryBlock>();
            if (glossaryBlock != null)
            {
                announcement = BuildSpecsGlossaryBlockAnnouncement(glossaryBlock, includeDescription: true);
                branch = "specs_glossary_block";
                return !string.IsNullOrEmpty(announcement);
            }

            IsSelectableRegistered keyButton = _specStatMainKeyButtonField != null
                ? _specStatMainKeyButtonField.GetValue(SpecStatMain.Instance) as IsSelectableRegistered
                : null;
            IsSelectableRegistered autoSelectFallback = _specStatMainAutoSelectFallbackField != null
                ? _specStatMainAutoSelectFallbackField.GetValue(SpecStatMain.Instance) as IsSelectableRegistered
                : null;

            GameObject keyButtonObject = keyButton != null ? keyButton.gameObject : null;
            GameObject autoSelectFallbackObject = autoSelectFallback != null ? autoSelectFallback.gameObject : null;
            if (selectedObject == keyButtonObject)
            {
                announcement = IsSpecsGlossaryPage()
                    ? Loc.Get("specs_button_stats")
                    : Loc.Get("specs_button_glossary");
                branch = "specs_page_toggle_button";
                return !string.IsNullOrEmpty(announcement);
            }

            if (selectedObject == autoSelectFallbackObject)
            {
                announcement = BuildSpecsAuxiliaryButtonAnnouncement(selectedObject);
                branch = "specs_auto_fallback_button";
                return !string.IsNullOrEmpty(announcement);
            }

            string selectedText = NormalizeText(ExtractTextFromObject(selectedObject));
            if (!string.IsNullOrEmpty(selectedText) &&
                !string.Equals(selectedText, Loc.Get("specs_button_stats"), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(selectedText, Loc.Get("specs_button_glossary"), StringComparison.OrdinalIgnoreCase))
            {
                announcement = selectedText;
                branch = "specs_selected_text";
                return true;
            }

            return false;
        }

        private static string BuildSpecsAuxiliaryButtonAnnouncement(GameObject selectedObject)
        {
            if (selectedObject == null)
                return null;

            if (IsSpecsGlossaryPage())
                return Loc.Get("specs_button_stats");

            return Loc.Get("specs_button_profile");
        }

        private static bool TryBuildDateADexSelectionAnnouncement(GameObject selectedObject, out string announcement)
        {
            announcement = null;

            if (DateADex.Instance == null || DateADex.Instance.DateADexWindow == null || !DateADex.Instance.DateADexWindow.activeInHierarchy)
                return false;

            string value;
            if (IsWithin(selectedObject, DateADex.Instance.CollectableButton, null, null, out value))
            {
                if (TryBuildDateADexDetailAnnouncement(out string entryAnnouncement, _pendingDateADexDetailEntry) &&
                    !string.IsNullOrEmpty(entryAnnouncement))
                {
                    Interlocked.Exchange(ref _pendingDateADexEntryAnnouncementRequested, 0);
                    _pendingDateADexDetailEntry = null;
                    announcement = entryAnnouncement;
                    return true;
                }

                announcement = string.IsNullOrEmpty(value)
                    ? Loc.Get("dateadex_button_collectables")
                    : Loc.Get("dateadex_button_collectables_value", value);
                return true;
            }

            if (IsWithin(selectedObject, DateADex.Instance.SortButton, null, null, out value))
            {
                announcement = string.IsNullOrEmpty(value)
                    ? Loc.Get("dateadex_button_sort")
                    : Loc.Get("dateadex_button_sort_value", value);
                return true;
            }

            bool isRecipeVisible = DateADex.Instance.RecipeScreen != null && DateADex.Instance.RecipeScreen.activeInHierarchy;
            if (DateADex.Instance.RecipeTab != null &&
                (selectedObject == DateADex.Instance.RecipeTab.gameObject || selectedObject.transform.IsChildOf(DateADex.Instance.RecipeTab.transform)))
            {
                announcement = Loc.Get(isRecipeVisible ? "dateadex_button_show_bio" : "dateadex_button_recipe");
                return true;
            }

            DexEntryButton entryButton = selectedObject.GetComponentInParent<DexEntryButton>();
            if (entryButton != null)
            {
                announcement = ExtractTextFromObject(entryButton.gameObject);
                return true;
            }

            Button selectedButton = selectedObject.GetComponentInParent<Button>();
            if (selectedButton != null && selectedObject.transform.IsChildOf(DateADex.Instance.DateADexWindow.transform))
            {
                announcement = Loc.Get("button_back");
                return true;
            }

            return false;
        }

        private static bool TryBuildRoomersSelectionAnnouncement(GameObject selectedObject, out string announcement)
        {
            announcement = null;

            if (selectedObject == null ||
                Roomers.Instance == null ||
                Roomers.Instance.RoomersWindow == null ||
                !Roomers.Instance.RoomersWindow.activeInHierarchy)
            {
                return false;
            }

            RoomersEntryButton entryButton = selectedObject.GetComponentInParent<RoomersEntryButton>();
            if (entryButton == null || entryButton.roomersEntry == null)
                return false;

            announcement = BuildRoomersEntryAnnouncement(entryButton.roomersEntry);
            return !string.IsNullOrEmpty(announcement);
        }

        private static string BuildRoomersEntryAnnouncement(Save.RoomersStruct entry)
        {
            if (entry == null)
                return null;

            var parts = new List<string>();
            AddAnnouncementPart(parts, NormalizeText(entry.questName));
            AddAnnouncementPart(parts, BuildLabeledValue("roomers_character", GetRoomersCharacterDisplayName(entry.character)));
            AddAnnouncementPart(parts, BuildLabeledValue("roomers_location", GetRoomersCharacterObjectName(entry.character)));
            AddAnnouncementPart(parts, NormalizeText(entry.description));

            if (entry.skylarTipIsFound && !string.IsNullOrWhiteSpace(entry.skylar))
            {
                AddAnnouncementPart(parts, Loc.Get("roomers_character", "Skylar"));
                AddAnnouncementPart(parts, NormalizeText(entry.skylar));
            }
            else if (entry.tips != null)
            {
                for (int i = 0; i < entry.tips.Count; i++)
                {
                    Save.RoomersTipStruct tip = entry.tips[i];
                    if (tip == null || !tip.isFound)
                        continue;

                    AddAnnouncementPart(parts, NormalizeText(tip.tipNameAfterValidation));
                    AddAnnouncementPart(parts, NormalizeText(tip.tipInfoAfterValidation));
                }
            }

            return JoinAnnouncementParts(parts);
        }

        private static bool TryBuildChatSelectionAnnouncement(GameObject selectedObject, out string announcement)
        {
            announcement = null;

            if (ChatMaster.Instance == null)
                return false;

            ChatType activeChatType;
            List<ParallelChat> chats;
            ParallelChat activeChat;
            string appName;
            GameObject activePanelNameObject;
            GameObject secondaryPanelObject;
            if (!TryGetActiveChatContext(out activeChatType, out chats, out activeChat, out appName, out activePanelNameObject, out secondaryPanelObject))
            {
                return false;
            }

            ChatButton selectedChatButton = selectedObject.GetComponentInParent<ChatButton>();
            if (selectedChatButton != null)
            {
                ParallelChat selectedChat = FindChatForButton(chats, selectedChatButton.gameObject);
                if (selectedChat == null && !string.IsNullOrEmpty(selectedChatButton.NodePrefix))
                    selectedChat = FindChatForNodePrefix(chats, selectedChatButton.NodePrefix);

                string selectedName = NormalizeText(selectedChatButton.CharacterName != null ? selectedChatButton.CharacterName.text : null);
                if (string.IsNullOrEmpty(selectedName) && selectedChat != null && selectedChat.appMessage != null)
                    selectedName = NormalizeText(selectedChat.appMessage.Name);

                announcement = BuildChatAnnouncement(appName, selectedName, null);
                return !string.IsNullOrEmpty(announcement);
            }

            if (!IsWithinChatPanel(selectedObject, activeChatType, activePanelNameObject, secondaryPanelObject))
                return false;

            string name = NormalizeText(ExtractTextFromObject(activePanelNameObject));
            announcement = BuildChatAnnouncement(appName, name, null);
            return !string.IsNullOrEmpty(announcement);
        }

        private static bool TryGetActiveChatContext(out ChatType activeChatType, out List<ParallelChat> chats, out ParallelChat activeChat, out string appName, out GameObject activePanelNameObject, out GameObject secondaryPanelObject)
        {
            activeChatType = default(ChatType);
            chats = null;
            activeChat = null;
            appName = null;
            activePanelNameObject = null;
            secondaryPanelObject = null;

            if (ChatMaster.Instance.Workspace != null && ChatMaster.Instance.Workspace.activeInHierarchy)
            {
                activeChatType = ChatType.Wrkspce;
                chats = ChatMaster.Instance.WorkspaceChats;
                activeChat = ChatMaster.Instance.ActiveChatWorkspace;
                appName = "Workspace";
                activePanelNameObject = ChatMaster.Instance.CharacterNameText;
                secondaryPanelObject = ChatMaster.Instance.RatingText;
                return true;
            }

            if (ChatMaster.Instance.Thiscord != null && ChatMaster.Instance.Thiscord.activeInHierarchy)
            {
                activeChatType = ChatType.Thiscord;
                chats = ChatMaster.Instance.ThiscordChats;
                activeChat = ChatMaster.Instance.ActiveChatThiscord;
                appName = "Thiscord";
                activePanelNameObject = ChatMaster.Instance.FriendName;
                return true;
            }

            if (ChatMaster.Instance.Canopy != null && ChatMaster.Instance.Canopy.activeInHierarchy)
            {
                activeChatType = ChatType.Canopy;
                chats = ChatMaster.Instance.CanopyChats;
                activeChat = ChatMaster.Instance.ActiveChatCanopy;
                appName = "Canopy";
                return true;
            }

            return false;
        }

        private static bool TryBuildSaveSelectionAnnouncement(GameObject selectedObject, out string announcement, out string branch)
        {
            announcement = null;
            branch = null;

            if (selectedObject == null || !TryGetActiveSaveScreenManager(out SaveScreenManager saveScreenManager))
                return false;

            if (!selectedObject.transform.IsChildOf(saveScreenManager.transform))
                return false;

            EnsureReflectionCache();

            GameObject newSaveSlot = _saveScreenManagerNewSaveSlotField != null
                ? _saveScreenManagerNewSaveSlotField.GetValue(saveScreenManager) as GameObject
                : null;
            if (newSaveSlot != null &&
                newSaveSlot.activeInHierarchy &&
                (selectedObject == newSaveSlot || selectedObject.transform.IsChildOf(newSaveSlot.transform)))
            {
                announcement = ExtractTextFromObject(newSaveSlot);
                if (string.IsNullOrEmpty(announcement))
                    announcement = Loc.Get("save_new_slot");

                branch = "save_new_slot";
                return !string.IsNullOrEmpty(announcement);
            }

            SaveSlot saveSlot = selectedObject.GetComponentInParent<SaveSlot>();
            if (saveSlot == null || !saveSlot.gameObject.activeInHierarchy)
                return false;

            announcement = BuildSaveSlotSelectionAnnouncement(saveSlot, selectedObject, out branch);
            return !string.IsNullOrEmpty(announcement);
        }

        private static bool IsWithinChatPanel(GameObject selectedObject, ChatType activeChatType, GameObject activePanelNameObject, GameObject secondaryPanelObject)
        {
            if (selectedObject == null)
                return false;

            GameObject activePanelRoot = null;
            switch (activeChatType)
            {
                case ChatType.Wrkspce:
                    activePanelRoot = ChatMaster.Instance.Workspace;
                    break;
                case ChatType.Thiscord:
                    activePanelRoot = ChatMaster.Instance.Thiscord;
                    break;
                case ChatType.Canopy:
                    activePanelRoot = ChatMaster.Instance.Canopy;
                    break;
            }

            if (activePanelRoot != null && selectedObject.transform.IsChildOf(activePanelRoot.transform))
                return true;

            if (activePanelNameObject != null && (selectedObject == activePanelNameObject || selectedObject.transform.IsChildOf(activePanelNameObject.transform)))
                return true;

            if (secondaryPanelObject != null && (selectedObject == secondaryPanelObject || selectedObject.transform.IsChildOf(secondaryPanelObject.transform)))
                return true;

            return false;
        }

        private static ParallelChat FindChatForButton(List<ParallelChat> chats, GameObject buttonObject)
        {
            if (chats == null || buttonObject == null)
                return null;

            for (int i = 0; i < chats.Count; i++)
            {
                ParallelChat chat = chats[i];
                if (chat != null && chat.button == buttonObject)
                    return chat;
            }

            return null;
        }

        private static ParallelChat FindChatForNodePrefix(List<ParallelChat> chats, string nodePrefix)
        {
            if (chats == null || string.IsNullOrEmpty(nodePrefix))
                return null;

            for (int i = 0; i < chats.Count; i++)
            {
                ParallelChat chat = chats[i];
                if (chat != null && chat.appMessage != null && string.Equals(chat.appMessage.NodePrefix, nodePrefix, StringComparison.Ordinal))
                    return chat;
            }

            return null;
        }

        private static string BuildChatAnnouncement(string appName, string contactName, string latestMessage)
        {
            if (string.IsNullOrEmpty(contactName) && string.IsNullOrEmpty(latestMessage))
                return null;

            if (string.IsNullOrEmpty(latestMessage))
                return string.IsNullOrEmpty(contactName)
                    ? Loc.Get("chat_app_only", appName)
                    : Loc.Get("chat_contact_only", appName, contactName);

            return string.IsNullOrEmpty(contactName)
                ? Loc.Get("chat_latest_message_without_contact", appName, latestMessage)
                : Loc.Get("chat_latest_message_with_contact", appName, contactName, latestMessage);
        }

        private static string BuildScreenSummary()
        {
            if (UIDialogManager.Instance != null && UIDialogManager.Instance.HasActiveDialogs)
                return null;

            if (TryBuildSpecsSummary(out string specsSummary))
                return specsSummary;

            if (TryBuildCreditsSummary(out string creditsSummary))
                return creditsSummary;

            if (Singleton<PhoneManager>.Instance != null && Singleton<PhoneManager>.Instance.IsPhoneMenuOpened())
            {
                if (!Singleton<PhoneManager>.Instance.IsPhoneAppOpened())
                    return BuildPhoneHomeSummary();
                return null;
            }

            if (Singleton<CanvasUIManager>.Instance != null && Singleton<CanvasUIManager>.Instance._activeMenu != null)
            {
                string menuName = NormalizeIdentifierName(Singleton<CanvasUIManager>.Instance._activeMenu.MenuObjectName);
                if (!string.IsNullOrEmpty(menuName))
                {
                    if (menuName.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0)
                        return BuildSettingsSummary();
                    return Loc.Get("screen_open", menuName);
                }
            }

            return null;
        }

        private static bool TryBuildRoomersDetailAnnouncement(out string announcement)
        {
            announcement = null;

            if (Roomers.Instance == null || Roomers.Instance.RoomersWindow == null || !Roomers.Instance.RoomersWindow.activeInHierarchy)
                return false;

            RoomersInfo info = Roomers.Instance.roomersScreenInfo;
            if (info == null)
                return false;

            string screen = NormalizeText(Roomers.Instance.screenNameText != null ? Roomers.Instance.screenNameText.text : null);
            string title = NormalizeText(info.RoomersTitle != null ? info.RoomersTitle.text : null);
            string description = NormalizeText(info.RoomersDescription != null ? info.RoomersDescription.text : null);
            string character = NormalizeText(info.CharacterName != null ? info.CharacterName.text : null);
            string room = NormalizeText(info.RoomName != null ? info.RoomName.text : null);
            string tips = ExtractTextFromObject(info.TipContainer);
            string emptyState = Roomers.Instance.NoItemsToShow != null && Roomers.Instance.NoItemsToShow.activeInHierarchy
                ? ExtractTextFromObject(Roomers.Instance.NoItemsToShow)
                : null;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(title))
                AddAnnouncementPart(parts, title);
            else if (!string.IsNullOrEmpty(screen))
                AddAnnouncementPart(parts, screen);

            if (!string.IsNullOrEmpty(character))
                AddAnnouncementPart(parts, Loc.Get("roomers_character", character));
            if (!string.IsNullOrEmpty(room))
                AddAnnouncementPart(parts, Loc.Get("roomers_location", room));
            if (!string.IsNullOrEmpty(description))
                AddAnnouncementPart(parts, description);
            if (!string.IsNullOrEmpty(tips))
                AddAnnouncementPart(parts, tips);
            if (!string.IsNullOrEmpty(emptyState))
                AddAnnouncementPart(parts, emptyState);

            announcement = JoinAnnouncementParts(parts);
            if (string.IsNullOrEmpty(announcement))
                return false;
            return true;
        }

        private static bool TryBuildDateADexDetailAnnouncement(out string announcement)
        {
            return TryBuildDateADexDetailAnnouncement(out announcement, null);
        }

        private static bool TryBuildDateADexDetailAnnouncement(out string announcement, DateADexEntry entryOverride)
        {
            announcement = null;

            if (DateADex.Instance == null || DateADex.Instance.DateADexWindow == null || !DateADex.Instance.DateADexWindow.activeInHierarchy)
                return false;

            bool isEntryVisible = DateADex.Instance.MainEntryScreen != null && DateADex.Instance.MainEntryScreen.activeInHierarchy;
            bool isRecipeVisible = DateADex.Instance.RecipeScreen != null && DateADex.Instance.RecipeScreen.activeInHierarchy;
            if (!isEntryVisible && !isRecipeVisible)
                return false;

            string item = isEntryVisible
                ? GetActiveDateADexText(DateADex.Instance.Item)
                : null;
            string description = isEntryVisible
                ? GetVisibleDateADexDescription(DateADex.Instance.Desc, DateADex.Instance.DescScroll)
                : null;
            string voiceActor = isEntryVisible
                ? GetActiveDateADexText(DateADex.Instance.VoActor)
                : null;
            string likes = isEntryVisible
                ? GetActiveDateADexText(DateADex.Instance.Likes)
                : null;
            string dislikes = isEntryVisible
                ? GetActiveDateADexText(DateADex.Instance.Dislikes)
                : null;
            string pronouns = isEntryVisible
                ? GetActiveDateADexText(DateADex.Instance.Pronouns)
                : null;
            string listSummary = isEntryVisible && DateADex.Instance.ListSummaryData != null && DateADex.Instance.ListSummaryData.activeInHierarchy
                ? ExtractTextFromObject(DateADex.Instance.ListSummaryData)
                : null;
            string collectables = isEntryVisible && DateADex.Instance.CollectableButton != null && DateADex.Instance.CollectableButton.gameObject.activeInHierarchy
                ? NormalizeText(ExtractTextFromObject(DateADex.Instance.CollectableButton.gameObject))
                : null;
            string recipe = isRecipeVisible
                ? ExtractTextFromObject(DateADex.Instance.RecipeScreen)
                : null;

            if (isEntryVisible && entryOverride != null)
            {
                bool isMet = entryOverride.isAwakened;
                if (string.IsNullOrEmpty(item))
                    item = isMet ? NormalizeText(entryOverride.CharObj) : "???";
                if (string.IsNullOrEmpty(description))
                    description = isMet ? NormalizeText(entryOverride.CharDYK) : Loc.Get("dateadex_unmet_description");
                if (string.IsNullOrEmpty(voiceActor))
                    voiceActor = isMet ? NormalizeText(entryOverride.VoActor) : null;
                if (string.IsNullOrEmpty(likes))
                    likes = isMet ? NormalizeText(entryOverride.CharLikes) : null;
                if (string.IsNullOrEmpty(dislikes))
                    dislikes = isMet ? NormalizeText(entryOverride.CharDislikes) : null;
            }

            var parts = new List<string>();
            AddAnnouncementPart(parts, item);
            AddAnnouncementPart(parts, description);
            AddAnnouncementPart(parts, BuildLabeledValue("dateadex_voice_actor", voiceActor));
            AddAnnouncementPart(parts, BuildLabeledValue("dateadex_likes", likes));
            AddAnnouncementPart(parts, BuildLabeledValue("dateadex_dislikes", dislikes));
            AddAnnouncementPart(parts, BuildLabeledValue("dateadex_pronouns", pronouns));
            AddAnnouncementPart(parts, listSummary);
            AddAnnouncementPart(parts, BuildLabeledValue("dateadex_collectables", collectables));
            AddAnnouncementPart(parts, recipe);

            announcement = JoinAnnouncementParts(parts);
            return !string.IsNullOrEmpty(announcement);
        }

        private static string GetActiveDateADexText(TMP_Text textComponent)
        {
            if (textComponent == null || !textComponent.gameObject.activeInHierarchy)
                return null;

            return NormalizeText(textComponent.text);
        }

        private static string BuildSaveSlotSelectionAnnouncement(SaveSlot saveSlot, GameObject selectedObject, out string branch)
        {
            branch = null;

            if (saveSlot == null)
                return null;

            if (saveSlot.DeleteButton != null &&
                saveSlot.DeleteButton.gameObject.activeInHierarchy &&
                (selectedObject == saveSlot.DeleteButton.gameObject || selectedObject.transform.IsChildOf(saveSlot.DeleteButton.transform)))
            {
                string deleteText = ExtractTextFromObject(saveSlot.DeleteButton.gameObject);
                branch = "save_slot_delete_button";
                return string.IsNullOrEmpty(deleteText) ? Loc.Get("button_delete") : deleteText;
            }

            if (saveSlot.LoadButton != null &&
                saveSlot.LoadButton.gameObject.activeInHierarchy &&
                (selectedObject == saveSlot.LoadButton.gameObject || selectedObject.transform.IsChildOf(saveSlot.LoadButton.transform)))
            {
                string loadMetadata = BuildSaveSlotMetadataAnnouncement(saveSlot);
                branch = "save_slot_load_button";
                return string.IsNullOrEmpty(loadMetadata) ? Loc.Get("button_load") : loadMetadata;
            }

            if (saveSlot.SaveButton != null &&
                saveSlot.SaveButton.gameObject.activeInHierarchy &&
                (selectedObject == saveSlot.SaveButton.gameObject || selectedObject.transform.IsChildOf(saveSlot.SaveButton.transform)))
            {
                string saveMetadata = BuildSaveSlotMetadataAnnouncement(saveSlot);
                branch = "save_slot_save_button";
                return string.IsNullOrEmpty(saveMetadata) ? Loc.Get("button_save") : saveMetadata;
            }

            string metadata = BuildSaveSlotMetadataAnnouncement(saveSlot);
            branch = "save_slot_metadata";
            return metadata;
        }

        private static string BuildSaveSlotMetadataAnnouncement(SaveSlot saveSlot)
        {
            if (saveSlot == null)
                return null;

            var parts = new List<string>();
            AddAnnouncementPart(parts, GetActiveText(saveSlot.Name));
            AddAnnouncementPart(parts, GetActiveText(saveSlot.Date));
            AddAnnouncementPart(parts, GetActiveText(saveSlot.Time));

            EnsureReflectionCache();
            TMP_Text playTime = _saveSlotPlayTimeField != null ? _saveSlotPlayTimeField.GetValue(saveSlot) as TMP_Text : null;
            TMP_Text daysPlayed = _saveSlotDaysPlayedField != null ? _saveSlotDaysPlayedField.GetValue(saveSlot) as TMP_Text : null;
            AddAnnouncementPart(parts, GetActiveText(playTime));
            AddAnnouncementPart(parts, GetActiveText(daysPlayed));

            return JoinAnnouncementParts(parts);
        }

        private static string GetActiveText(TMP_Text textComponent)
        {
            if (textComponent == null || !textComponent.gameObject.activeInHierarchy)
                return null;

            return NormalizeText(textComponent.text);
        }

        private static string GetVisibleDateADexDescription(TMP_Text textComponent, ScrollRect scrollRect)
        {
            if (textComponent == null || !textComponent.gameObject.activeInHierarchy)
                return null;

            RectTransform viewport = GetScrollViewport(scrollRect);
            if (viewport == null)
                return NormalizeText(textComponent.text);

            textComponent.ForceMeshUpdate();
            TMP_TextInfo textInfo = textComponent.textInfo;
            if (textInfo == null || textInfo.lineCount == 0)
                return NormalizeText(textComponent.text);

            string sourceText = textComponent.text;
            var visibleLines = new List<string>();
            Rect viewportRect = viewport.rect;
            RectTransform textRect = textComponent.rectTransform;

            for (int i = 0; i < textInfo.lineCount; i++)
            {
                TMP_LineInfo line = textInfo.lineInfo[i];
                float topY = viewport.InverseTransformPoint(textRect.TransformPoint(new Vector3(0f, line.ascender, 0f))).y;
                float bottomY = viewport.InverseTransformPoint(textRect.TransformPoint(new Vector3(0f, line.descender, 0f))).y;
                if (topY < viewportRect.yMin || bottomY > viewportRect.yMax)
                    continue;

                int startIndex = line.firstCharacterIndex;
                int length = line.characterCount;
                if (startIndex < 0 || length <= 0 || startIndex >= sourceText.Length)
                    continue;

                if (startIndex + length > sourceText.Length)
                    length = sourceText.Length - startIndex;

                string lineText = NormalizeText(sourceText.Substring(startIndex, length));
                AddAnnouncementPart(visibleLines, lineText);
            }

            if (visibleLines.Count > 0)
                return JoinAnnouncementParts(visibleLines);

            return NormalizeText(textComponent.text);
        }

        private static bool TryBuildChatAppAnnouncement(out string announcement)
        {
            string activeChatKey;
            return TryBuildChatAppAnnouncement(out announcement, out activeChatKey);
        }

        private static bool TryBuildChatAppAnnouncement(out string announcement, out string activeChatKey)
        {
            announcement = null;
            activeChatKey = null;

            if (ChatMaster.Instance == null)
                return false;

            ChatType activeChatType;
            List<ParallelChat> chats;
            ParallelChat activeChat;
            string appName;
            GameObject activePanelNameObject;
            GameObject secondaryPanelObject;
            if (!TryGetActiveChatContext(out activeChatType, out chats, out activeChat, out appName, out activePanelNameObject, out secondaryPanelObject))
                return false;

            if (activeChat != null && activeChat.appMessage != null)
                activeChatKey = activeChatType + ":" + activeChat.appMessage.NodePrefix;
            else
                activeChatKey = activeChatType + ":none";

            if (activeChatType == ChatType.Canopy && ChatMaster.Instance.CanopyEmptyMessage != null && ChatMaster.Instance.CanopyEmptyMessage.activeInHierarchy)
            {
                announcement = Loc.Get("canopy_no_messages");
                return true;
            }

            string name = GetChatDisplayName(activeChat, activePanelNameObject);
            string secondary = NormalizeText(ExtractTextFromObject(secondaryPanelObject));
            string transcript = GetChatTranscript(activeChat);
            string visibleChoices = GetVisibleChatChoices(activeChat);
            string header = BuildChatAnnouncement(appName, name, null);

            if (activeChat == null &&
                string.IsNullOrEmpty(header) &&
                string.IsNullOrEmpty(transcript) &&
                string.IsNullOrEmpty(visibleChoices))
            {
                return false;
            }

            var parts = new List<string>();
            AddAnnouncementPart(parts, header);
            if (!string.Equals(secondary, name, StringComparison.Ordinal))
                AddAnnouncementPart(parts, secondary);
            AddAnnouncementPart(parts, transcript);
            AddAnnouncementPart(parts, BuildLabeledValue("chat_options", visibleChoices));

            announcement = JoinAnnouncementParts(parts);
            return !string.IsNullOrEmpty(announcement);
        }

        private static bool TryBuildMusicAnnouncement(out string announcement)
        {
            announcement = null;

            if (MusicPlayer.Instance == null || !MusicPlayer.Instance.gameObject.activeInHierarchy)
                return false;

            string title = NormalizeText(MusicPlayer.Instance.SongTitle != null ? MusicPlayer.Instance.SongTitle.text : null);
            if (string.IsNullOrEmpty(title))
                title = Loc.Get("music_no_track_selected");

            string playbackState = Loc.Get(MusicPlayer.Instance.isPlaying ? "music_playing" : "music_stopped");
            announcement = Loc.Get("music_detail", title, playbackState);
            return true;
        }

        private static bool TryBuildArtAnnouncement(out string announcement)
        {
            announcement = null;

            if (ArtPlayer.Instance == null || !ArtPlayer.Instance.gameObject.activeInHierarchy || ArtPlayer.Instance.selectedArt == null)
                return false;

            string title = NormalizeIdentifierName(ArtPlayer.Instance.selectedArt.title);
            if (string.IsNullOrEmpty(title))
                return false;

            announcement = Loc.Get("art_detail", ArtPlayer.Instance.selectedArt.number, title);
            return true;
        }

        private static bool TryBuildPopupAnnouncement(out string announcement)
        {
            announcement = null;

            if (Popup.Instance == null || Popup.Instance.PopUp == null || !Popup.Instance.PopUp.activeInHierarchy)
                return false;

            string title = NormalizeText(Popup.Instance.title != null ? Popup.Instance.title.text : null);
            string text = NormalizeText(Popup.Instance.text != null ? Popup.Instance.text.text : null);
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(text))
                return false;

            if (string.IsNullOrEmpty(title))
            {
                announcement = text;
                return true;
            }

            announcement = string.IsNullOrEmpty(text) ? title : title + ". " + text;
            return true;
        }

        private static bool TryBuildUIDialogAnnouncement(out string announcement)
        {
            announcement = null;

            if (!TryGetTopUIDialog(out UIDialog dialog))
                return false;

            GameObject dialogObject = _uiDialogGameObjectField != null ? _uiDialogGameObjectField.GetValue(dialog) as GameObject : null;
            if (dialogObject == null || !dialogObject.activeInHierarchy)
                return false;

            TMP_Text titleText = _uiDialogTitleField != null ? _uiDialogTitleField.GetValue(dialog) as TMP_Text : null;
            TMP_Text bodyText = _uiDialogBodyTextField != null ? _uiDialogBodyTextField.GetValue(dialog) as TMP_Text : null;
            string title = NormalizeText(titleText != null && titleText.gameObject.activeInHierarchy ? titleText.text : null);
            string text = NormalizeText(bodyText != null ? bodyText.text : null);
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(text))
                return false;

            announcement = string.IsNullOrEmpty(title) ? text : string.IsNullOrEmpty(text) ? title : title + ". " + text;
            return true;
        }

        private static bool TryBuildSpecsAnnouncement(out string announcement, out SpecsAnnouncementMode mode)
        {
            announcement = null;
            mode = SpecsAnnouncementMode.None;

            if (SpecStatMain.Instance == null || !SpecStatMain.Instance.visible)
                return false;

            if (ShouldSuppressSpecsAnnouncements())
                return false;

            if ((UIDialogManager.Instance != null && UIDialogManager.Instance.HasActiveDialogs) ||
                (Popup.Instance != null && Popup.Instance.IsPopupOpen()))
            {
                return false;
            }

            string tooltipAnnouncement = BuildSpecsTooltipAnnouncement();
            if (!string.IsNullOrEmpty(tooltipAnnouncement))
            {
                announcement = tooltipAnnouncement;
                mode = SpecsAnnouncementMode.Tooltip;
                return true;
            }

            if (IsSpecsGlossaryPage())
            {
                announcement = BuildSpecsGlossaryAnnouncement();
                mode = string.IsNullOrEmpty(announcement) ? SpecsAnnouncementMode.None : SpecsAnnouncementMode.Glossary;
                return !string.IsNullOrEmpty(announcement);
            }

            announcement = BuildSpecsStatsAnnouncement();
            mode = string.IsNullOrEmpty(announcement) ? SpecsAnnouncementMode.None : SpecsAnnouncementMode.Stats;
            return !string.IsNullOrEmpty(announcement);
        }

        private static string BuildSpecsStatsAnnouncement()
        {
            bool hasActiveBlock = false;
            var parts = new List<string>();
            List<SpecStatMain.StatBlockRef> statBlocks = SpecStatMain.Instance.Active_Stat_Blocks;
            if (statBlocks != null)
            {
                for (int i = 0; i < statBlocks.Count; i++)
                {
                    SpecStatBlock statBlock = statBlocks[i].StatBlock;
                    if (statBlock != null && statBlock.gameObject.activeInHierarchy)
                    {
                        AddAnnouncementPart(parts, BuildSpecsStatBlockAnnouncement(statBlock, includeDescription: true));
                        hasActiveBlock = true;
                    }
                }
            }

            return hasActiveBlock ? JoinAnnouncementParts(parts) : null;
        }

        private static string BuildSpecsGlossaryAnnouncement()
        {
            bool hasActiveBlock = false;
            var parts = new List<string>();
            List<SpecStatMain.StatBlockRef> statBlocks = SpecStatMain.Instance.Active_Stat_Blocks;
            if (statBlocks != null)
            {
                for (int i = 0; i < statBlocks.Count; i++)
                {
                    SpecGlossaryBlock glossaryBlock = statBlocks[i].GlossaryBlock;
                    if (glossaryBlock != null && glossaryBlock.gameObject.activeInHierarchy)
                    {
                        AddAnnouncementPart(parts, BuildSpecsGlossaryBlockAnnouncement(glossaryBlock, includeDescription: true));
                        hasActiveBlock = true;
                    }
                }
            }

            return hasActiveBlock ? JoinAnnouncementParts(parts) : null;
        }

        private static string BuildSpecsTooltipAnnouncement()
        {
            EnsureReflectionCache();
            GameObject[] tooltips = _specStatTooltipsField != null ? _specStatTooltipsField.GetValue(SpecStatMain.Instance) as GameObject[] : null;
            if (tooltips == null)
                return null;

            var parts = new List<string>();
            bool hasActiveTooltip = false;
            if (tooltips != null)
            {
                for (int i = 0; i < tooltips.Length; i++)
                {
                    if (tooltips[i] == null || !tooltips[i].activeInHierarchy)
                        continue;

                    AddAnnouncementPart(parts, ExtractTextFromObject(tooltips[i]));
                    hasActiveTooltip = true;
                }
            }

            return hasActiveTooltip ? JoinAnnouncementParts(parts) : null;
        }

        private static bool TryBuildCreditsAnnouncement(out string announcement)
        {
            announcement = null;

            if (!TryGetActiveCreditsScreen(out CreditsScreen creditsScreen))
                return false;

            EnsureReflectionCache();
            TMP_Text creditsText = _creditsScreenTextField != null ? _creditsScreenTextField.GetValue(creditsScreen) as TMP_Text : null;
            string visibleCredits = GetVisibleTextInMaskedParent(creditsText);
            if (string.IsNullOrEmpty(visibleCredits))
                return false;

            announcement = Loc.Get("credits_summary") + " " + visibleCredits;
            return true;
        }

        private static string BuildSpecsStatBlockAnnouncement(SpecStatBlock statBlock, bool includeDescription)
        {
            if (statBlock == null || !statBlock.gameObject.activeInHierarchy)
                return null;

            EnsureReflectionCache();

            TMP_Text firstLetter = _specStatBlockNameFirstLetterField != null ? _specStatBlockNameFirstLetterField.GetValue(statBlock) as TMP_Text : null;
            TMP_Text rest = _specStatBlockNameRestField != null ? _specStatBlockNameRestField.GetValue(statBlock) as TMP_Text : null;
            TMP_Text adjective = _specStatBlockAdjectiveLabelField != null ? _specStatBlockAdjectiveLabelField.GetValue(statBlock) as TMP_Text : null;
            TMP_Text description = _specStatBlockLevelDescriptionTextField != null ? _specStatBlockLevelDescriptionTextField.GetValue(statBlock) as TMP_Text : null;

            string name = JoinTextParts(
                NormalizeText(firstLetter != null ? firstLetter.text : null),
                NormalizeText(rest != null ? rest.text : null));
            string adjectiveText = NormalizeText(adjective != null ? adjective.text : null);
            string descriptionText = includeDescription ? NormalizeText(description != null ? description.text : null) : null;

            var parts = new List<string>();
            AddAnnouncementPart(parts, name);
            AddAnnouncementPart(parts, adjectiveText);
            AddAnnouncementPart(parts, descriptionText);
            return JoinAnnouncementParts(parts);
        }

        private static string BuildSpecsGlossaryBlockAnnouncement(SpecGlossaryBlock glossaryBlock, bool includeDescription)
        {
            if (glossaryBlock == null || !glossaryBlock.gameObject.activeInHierarchy)
                return null;

            EnsureReflectionCache();

            TMP_Text firstLetter = _specGlossaryBlockNameFirstLetterField != null ? _specGlossaryBlockNameFirstLetterField.GetValue(glossaryBlock) as TMP_Text : null;
            TMP_Text rest = _specGlossaryBlockNameRestField != null ? _specGlossaryBlockNameRestField.GetValue(glossaryBlock) as TMP_Text : null;
            TMP_Text description = _specGlossaryBlockDescriptionTextField != null ? _specGlossaryBlockDescriptionTextField.GetValue(glossaryBlock) as TMP_Text : null;

            string name = JoinTextParts(
                NormalizeText(firstLetter != null ? firstLetter.text : null),
                NormalizeText(rest != null ? rest.text : null));
            string descriptionText = includeDescription ? NormalizeText(description != null ? description.text : null) : null;

            var parts = new List<string>();
            AddAnnouncementPart(parts, name);
            AddAnnouncementPart(parts, descriptionText);
            return JoinAnnouncementParts(parts);
        }

        private static string JoinTextParts(string first, string second)
        {
            if (string.IsNullOrEmpty(first))
                return second;

            if (string.IsNullOrEmpty(second))
                return first;

            return first + second;
        }

        private static bool TryBuildTutorialAnnouncement(out string announcement)
        {
            announcement = null;

            if (!TryGetCurrentTutorialObjectiveText(out string text))
                return false;

            announcement = Loc.Get("objective_announcement", text);
            return true;
        }

        private static bool TryBuildSubtitleAnnouncement(out string announcement)
        {
            announcement = null;

            if (TutorialController.Instance == null)
                return false;

            EnsureReflectionCache();
            TMP_Text subtitleText = _tutorialSubtitleTextField != null ? _tutorialSubtitleTextField.GetValue(TutorialController.Instance) as TMP_Text : null;
            if (subtitleText == null || !subtitleText.gameObject.activeInHierarchy)
                return false;

            string text = NormalizeText(subtitleText.text);
            if (string.IsNullOrEmpty(text))
                return false;

            announcement = text;
            return true;
        }

        private static bool TryBuildEngagementAnnouncement(out string announcement)
        {
            announcement = null;

            EnsureReflectionCache();
            if (_engagementType == null)
                return false;

            Component engagement = UnityEngine.Object.FindObjectOfType(_engagementType) as Component;
            if (engagement == null || !engagement.gameObject.activeInHierarchy)
                return false;

            TMP_Text titleText = _engagementTitleField != null ? _engagementTitleField.GetValue(engagement) as TMP_Text : null;
            TMP_Text stateText = _engagementStateField != null ? _engagementStateField.GetValue(engagement) as TMP_Text : null;
            string title = NormalizeText(titleText != null && titleText.enabled ? titleText.text : null);
            string state = NormalizeText(stateText != null && stateText.enabled ? stateText.text : null);
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(state))
                return false;

            if (string.IsNullOrEmpty(title))
            {
                announcement = state;
                return true;
            }

            announcement = string.IsNullOrEmpty(state) ? title : title + ". " + state;
            return true;
        }

        private static bool TryBuildLoadingAnnouncement(out string announcement)
        {
            announcement = null;

            EnsureReflectionCache();
            if (_loadingFactsType == null)
                return false;

            Component loadingFacts = UnityEngine.Object.FindObjectOfType(_loadingFactsType) as Component;
            if (loadingFacts == null || !loadingFacts.gameObject.activeInHierarchy)
                return false;

            string fact = NormalizeText(ExtractTextFromObject(loadingFacts.gameObject));
            if (string.IsNullOrEmpty(fact))
                return false;

            announcement = Loc.Get("loading_announcement", fact);
            return true;
        }

        private static bool TryBuildExamineAnnouncement(out string announcement)
        {
            announcement = null;

            if (ExamineController.Instance == null ||
                !ExamineController.Instance.isShown ||
                ExamineController.Instance.ExamineGameObject == null ||
                !ExamineController.Instance.ExamineGameObject.activeInHierarchy ||
                ExamineController.Instance.ExamineText == null ||
                !ExamineController.Instance.ExamineText.gameObject.activeInHierarchy)
            {
                return false;
            }

            string text = NormalizeText(ExamineController.Instance.ExamineText.text);
            if (string.IsNullOrEmpty(text))
                return false;

            announcement = text;
            return true;
        }

        private static bool TryBuildCollectableAnnouncement(out string announcement)
        {
            announcement = null;

            if (DateADex.Instance == null || !DateADex.Instance.gameObject.activeInHierarchy)
                return false;

            CollectablesScreen collectables = DateADex.Instance.GetComponentInChildren<CollectablesScreen>(includeInactive: true);
            if (collectables == null || !collectables.gameObject.activeInHierarchy)
                return false;

            GameObject selectedObject = GetCurrentSelectedObject();
            if (selectedObject == null || !selectedObject.transform.IsChildOf(collectables.transform))
                return false;

            EnsureReflectionCache();
            TMP_Text nameText = _collectablesScreenNameField != null ? _collectablesScreenNameField.GetValue(collectables) as TMP_Text : null;
            TMP_Text descText = _collectablesScreenDescField != null ? _collectablesScreenDescField.GetValue(collectables) as TMP_Text : null;
            string name = NormalizeText(nameText != null ? nameText.text : null);
            string description = NormalizeText(descText != null ? descText.text : null);
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(description))
                return false;

            if (string.IsNullOrEmpty(name))
            {
                announcement = description;
                return true;
            }

            if (string.IsNullOrEmpty(description))
            {
                announcement = name;
                return true;
            }

            announcement = name + ". " + description;
            return true;
        }

        private static bool TryBuildPhoneAppContentAnnouncement(out string announcement, out string contentKey)
        {
            announcement = null;
            contentKey = null;

            if (Singleton<PhoneManager>.Instance == null ||
                !Singleton<PhoneManager>.Instance.IsPhoneMenuOpened() ||
                !Singleton<PhoneManager>.Instance.IsPhoneAppOpened())
            {
                return false;
            }

            GameObject currentApp = Singleton<PhoneManager>.Instance.GetCurrentApp();
            if (currentApp == null || !currentApp.activeInHierarchy)
                return false;

            string appName = NormalizeIdentifierName(currentApp.name);
            contentKey = currentApp.GetInstanceID().ToString();
            bool isDateADexApp = !string.IsNullOrEmpty(appName) &&
                (appName.IndexOf("date a dex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 appName.IndexOf("dateadex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 appName.IndexOf("dexscreens", StringComparison.OrdinalIgnoreCase) >= 0);

            bool isSpecsApp = !string.IsNullOrEmpty(appName) && appName.IndexOf("spec", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isSpecsApp && (ShouldSuppressSpecsAnnouncements() ||
                (UIDialogManager.Instance != null && UIDialogManager.Instance.HasActiveDialogs)))
            {
                return false;
            }

            if (TryBuildChatAppAnnouncement(out announcement, out string activeChatKey))
            {
                contentKey = contentKey + "|chat|" + activeChatKey;
                return !string.IsNullOrEmpty(announcement);
            }

            if (TryBuildCollectableAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            if (TryBuildRoomersDetailAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            GameObject selectedObject = GetCurrentSelectedObject();
            if (isDateADexApp &&
                selectedObject != null &&
                selectedObject.GetComponentInParent<DexEntryButton>() != null)
            {
                return false;
            }

            if (TryBuildDateADexDetailAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            if (TryBuildMusicAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            if (TryBuildArtAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            if (TryBuildSpecsAnnouncement(out announcement, out SpecsAnnouncementMode _))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            if (TryBuildCreditsAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
            }

            if (isDateADexApp)
            {
                return false;
            }

            announcement = BuildPhoneAppVisibleTextFallback(currentApp, appName);
            if (!string.IsNullOrEmpty(announcement))
                return true;

            announcement = string.IsNullOrEmpty(appName)
                ? Loc.Get("phone_app_open_generic")
                : Loc.Get("screen_open", appName);
            return !string.IsNullOrEmpty(announcement);
        }

        private static bool TryBuildResultAnnouncement(out string announcement)
        {
            announcement = null;

            if (ResultSplashScreen.Instance == null || !ResultSplashScreen.Instance.isOpen)
                return false;

            EnsureReflectionCache();
            if (_resultSplashTitleBannerField == null)
                return false;

            object titleBanner = _resultSplashTitleBannerField.GetValue(ResultSplashScreen.Instance);
            DexEntryButton banner = titleBanner as DexEntryButton;
            if (banner == null)
                return false;

            string detail = NormalizeText(ExtractTextFromObject(banner.gameObject));
            if (string.IsNullOrEmpty(detail))
                return false;

            announcement = Loc.Get("outcome_announcement", detail);
            return true;
        }

        private static bool TryGetDialogueChoiceAnnouncement(GameObject selectedObject, out int choiceIndex, out int choiceCount)
        {
            return TryGetChoiceAnnouncement(selectedObject, GetActiveDialogueChoices(), out choiceIndex, out choiceCount);
        }

        private static bool TryGetChatChoiceSpeechInfo(GameObject selectedObject, out int choiceIndex, out int choiceCount, out string choiceText)
        {
            choiceText = null;
            if (TryGetChatChoiceAnnouncement(selectedObject, out choiceIndex, out choiceCount))
            {
                choiceText = ExtractTextFromObject(selectedObject);
                return !string.IsNullOrEmpty(choiceText);
            }

            IList<Button> choices = GetActiveChatChoices();
            string activeChatContextKey = GetActiveChatChoiceContextKey();
            if (selectedObject == null ||
                choices == null ||
                choices.Count == 0 ||
                string.IsNullOrEmpty(activeChatContextKey) ||
                !string.Equals(activeChatContextKey, _virtualChatChoiceContextKey, StringComparison.Ordinal) ||
                _virtualChatChoiceIndex < 0 ||
                _virtualChatChoiceIndex >= choices.Count)
            {
                choiceIndex = 0;
                choiceCount = 0;
                return false;
            }

            if (!TryGetActiveChatContext(out ChatType activeChatType, out _, out _, out _, out GameObject activePanelNameObject, out GameObject secondaryPanelObject) ||
                !IsWithinChatPanel(selectedObject, activeChatType, activePanelNameObject, secondaryPanelObject))
            {
                choiceIndex = 0;
                choiceCount = 0;
                return false;
            }

            Button choiceButton = choices[_virtualChatChoiceIndex];
            choiceText = choiceButton != null ? NormalizeText(ExtractTextFromObject(choiceButton.gameObject)) : null;
            choiceIndex = _virtualChatChoiceIndex + 1;
            choiceCount = choices.Count;
            return !string.IsNullOrEmpty(choiceText);
        }

        private static bool TryGetChatChoiceAnnouncement(GameObject selectedObject, out int choiceIndex, out int choiceCount)
        {
            return TryGetChoiceAnnouncement(selectedObject, GetActiveChatChoices(), out choiceIndex, out choiceCount);
        }

        private static bool TryGetChoiceAnnouncement(GameObject selectedObject, IList<Button> choices, out int choiceIndex, out int choiceCount)
        {
            choiceIndex = 0;
            choiceCount = 0;

            if (selectedObject == null || choices == null || choices.Count == 0)
                return false;

            for (int i = 0; i < choices.Count; i++)
            {
                Button button = choices[i];
                if (button == null)
                    continue;

                if (selectedObject == button.gameObject || selectedObject.transform.IsChildOf(button.transform))
                {
                    choiceIndex = i + 1;
                    choiceCount = choices.Count;
                    return true;
                }
            }

            return false;
        }

        private static IList<Button> GetActiveDialogueChoices()
        {
            if (TalkingUI.Instance == null || !TalkingUI.Instance.open)
                return null;

            EnsureReflectionCache();
            if (_talkingUiChoicesButtonsField == null)
                return null;

            var allChoices = _talkingUiChoicesButtonsField.GetValue(TalkingUI.Instance) as IList<Button>;
            if (allChoices == null || allChoices.Count == 0)
                return null;

            var activeChoices = new List<Button>();
            for (int i = 0; i < allChoices.Count; i++)
            {
                Button button = allChoices[i];
                if (button != null && button.gameObject.activeInHierarchy && button.interactable)
                    activeChoices.Add(button);
            }

            return activeChoices;
        }

        private static IList<Button> GetActiveChatChoices()
        {
            if (ChatMaster.Instance == null)
                return null;

            ChatType activeChatType;
            List<ParallelChat> chats;
            ParallelChat activeChat;
            string appName;
            GameObject activePanelNameObject;
            GameObject secondaryPanelObject;
            if (!TryGetActiveChatContext(out activeChatType, out chats, out activeChat, out appName, out activePanelNameObject, out secondaryPanelObject) ||
                activeChat == null ||
                activeChat.Options == null ||
                activeChat.Options.Length == 0)
            {
                return null;
            }

            var activeChoices = new List<Button>();
            for (int i = 0; i < activeChat.Options.Length; i++)
            {
                Button option = activeChat.Options[i];
                if (option != null && option.gameObject.activeInHierarchy && option.interactable)
                    activeChoices.Add(option);
            }

            return activeChoices.Count > 0 ? activeChoices : null;
        }

        private static bool IsChatChoiceObject(GameObject selectedObject)
        {
            if (selectedObject == null)
                return false;

            IList<Button> choices = GetActiveChatChoices();
            if (choices == null || choices.Count == 0)
                return false;

            for (int i = 0; i < choices.Count; i++)
            {
                Button button = choices[i];
                if (button == null)
                    continue;

                if (selectedObject == button.gameObject || selectedObject.transform.IsChildOf(button.transform))
                    return true;
            }

            return false;
        }

        private static string GetActiveChatChoiceContextKey()
        {
            if (!TryGetActiveChatContext(out _, out _, out ParallelChat activeChat, out _, out _, out _))
                return null;

            return activeChat != null ? activeChat.GetInstanceID().ToString() : null;
        }

        private static void ClearVirtualChatChoiceState()
        {
            _virtualChatChoiceIndex = -1;
            _virtualChatChoiceContextKey = null;
        }

        private static int GetCurrentChoiceIndex(IList<Button> choices)
        {
            return GetCurrentChoiceIndex(choices, allowVirtualChatFallback: true);
        }

        private static int GetCurrentChoiceIndex(IList<Button> choices, bool allowVirtualChatFallback)
        {
            GameObject selectedObject = GetCurrentSelectedObject();
            if (selectedObject != null)
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    Button button = choices[i];
                    if (button == null)
                        continue;

                    if (selectedObject == button.gameObject || selectedObject.transform.IsChildOf(button.transform))
                        return i;
                }
            }

            if (allowVirtualChatFallback)
            {
                string activeChatContextKey = GetActiveChatChoiceContextKey();
                if (!string.IsNullOrEmpty(activeChatContextKey) &&
                    string.Equals(activeChatContextKey, _virtualChatChoiceContextKey, StringComparison.Ordinal) &&
                    _virtualChatChoiceIndex >= 0 &&
                    _virtualChatChoiceIndex < choices.Count)
                {
                    return _virtualChatChoiceIndex;
                }
            }

            return -1;
        }

        private static void FocusChatChoice(int choiceIndex, IList<Button> choices, ControllerMenuUI.Direction direction)
        {
            if (choices == null || choiceIndex < 0 || choiceIndex >= choices.Count)
                return;

            _virtualChatChoiceContextKey = GetActiveChatChoiceContextKey();
            _virtualChatChoiceIndex = choiceIndex;
            FocusChoice(choices[choiceIndex], direction);
        }

        private static void FocusChoice(Button choice, ControllerMenuUI.Direction direction)
        {
            if (choice == null)
                return;

            ControllerMenuUI.SetCurrentlySelected(choice.gameObject, direction, manualSelected: true, isViaPointer: true);
        }

        private static void ActivateChoice(Button choice)
        {
            if (choice == null || !choice.interactable)
                return;

            choice.onClick.Invoke();
        }

        private static bool WasChoiceKeyPressed(KeyCode keyCode, int virtualKey, ref bool wasDown)
        {
            bool isDown = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            bool pressed = Input.GetKeyDown(keyCode) || (isDown && !wasDown);
            wasDown = isDown;
            return pressed;
        }

        private static bool TryGetCurrentDialogue(out string speakerName, out string dialogText)
        {
            speakerName = null;
            dialogText = null;

            EnsureReflectionCache();
            if (_talkingUiDialogBoxField == null)
                return false;

            object dialogBox = _talkingUiDialogBoxField.GetValue(TalkingUI.Instance);
            if (dialogBox == null)
                return false;

            TMP_Text nameText = _dialogBoxNameTextField != null ? _dialogBoxNameTextField.GetValue(dialogBox) as TMP_Text : null;
            TMP_Text dialogueText = _dialogBoxDialogTextField != null ? _dialogBoxDialogTextField.GetValue(dialogBox) as TMP_Text : null;
            if (dialogueText == null)
                return false;

            speakerName = nameText != null ? nameText.text : string.Empty;
            dialogText = dialogueText.text;
            return true;
        }

        private static bool TryBuildCurrentDialogueAnnouncement(out string announcement)
        {
            announcement = null;

            if (TalkingUI.Instance == null || !TalkingUI.Instance.open)
                return false;

            if (!TryGetCurrentDialogue(out string speakerName, out string dialogText))
                return false;

            dialogText = NormalizeText(dialogText);
            speakerName = NormalizeText(speakerName);
            if (string.IsNullOrEmpty(dialogText))
                return false;

            announcement = string.IsNullOrEmpty(speakerName) ? dialogText : speakerName + ". " + dialogText;
            return true;
        }

        private static string BuildPhoneHomeSummary()
        {
            int charges = Singleton<Dateviators>.Instance != null ? Singleton<Dateviators>.Instance.GetCurrentCharges() : 0;
            bool equipped = Singleton<Dateviators>.Instance != null && Singleton<Dateviators>.Instance.IsEquippingOrEquipped;
            return Loc.Get("phone_menu_summary", charges, Loc.Get(equipped ? "dateviators_equipped" : "dateviators_unequipped"));
        }

        private static string BuildRoomersSummary()
        {
            if (Roomers.Instance == null)
                return Loc.Get("roomers_summary_empty");

            string screen = NormalizeIdentifierName(Roomers.Instance.CurrentScreen.ToString());
            string title = Roomers.Instance.roomersScreenInfo != null ? NormalizeText(Roomers.Instance.roomersScreenInfo.RoomersTitle.text) : null;
            string room = Roomers.Instance.roomersScreenInfo != null ? NormalizeText(Roomers.Instance.roomersScreenInfo.RoomName.text) : null;

            string summary = Loc.Get("roomers_summary_screen", screen);
            if (!string.IsNullOrEmpty(title))
                summary += " " + title + ".";
            if (!string.IsNullOrEmpty(room))
                summary += " " + room + ".";
            return summary;
        }

        private static string BuildDateADexSummary()
        {
            if (DateADex.Instance == null)
                return Loc.Get("dateadex_summary_empty");

            string item = NormalizeText(DateADex.Instance.Item != null ? DateADex.Instance.Item.text : null);
            if (string.IsNullOrEmpty(item))
                return Loc.Get("dateadex_summary_empty");

            return Loc.Get("dateadex_summary_item", item);
        }

        private static string BuildThiscordSummary()
        {
            if (ChatMaster.Instance == null)
                return Loc.Get("thiscord_summary_empty");

            string friend = NormalizeText(ExtractTextFromObject(ChatMaster.Instance.FriendName));
            if (string.IsNullOrEmpty(friend))
                return Loc.Get("thiscord_summary_empty");

            return Loc.Get("thiscord_summary_friend", friend);
        }

        private static string BuildWorkspaceSummary()
        {
            if (ChatMaster.Instance == null)
                return Loc.Get("workspace_summary_empty");

            string name = NormalizeText(ExtractTextFromObject(ChatMaster.Instance.CharacterNameText));
            if (string.IsNullOrEmpty(name))
                return Loc.Get("workspace_summary_empty");

            return Loc.Get("workspace_summary_name", name);
        }

        private static string BuildMusicSummary()
        {
            if (MusicPlayer.Instance == null)
                return Loc.Get("music_summary_empty");

            string title = NormalizeText(MusicPlayer.Instance.SongTitle != null ? MusicPlayer.Instance.SongTitle.text : null);
            if (string.IsNullOrEmpty(title))
                return Loc.Get("music_summary_empty");

            return Loc.Get("music_summary_title", title);
        }

        private static string BuildArtSummary()
        {
            if (ArtPlayer.Instance == null || ArtPlayer.Instance.selectedArt == null)
                return Loc.Get("art_summary_empty");

            return Loc.Get("art_summary_title", NormalizeIdentifierName(ArtPlayer.Instance.selectedArt.title));
        }

        private static string BuildSpecsSummary()
        {
            return IsSpecsGlossaryPage()
                ? Loc.Get("specs_summary_glossary")
                : Loc.Get("specs_summary_stats");
        }

        private static bool TryBuildSpecsSummary(out string summary)
        {
            summary = null;

            if (SpecStatMain.Instance == null || !SpecStatMain.Instance.visible)
                return false;

            if (ShouldSuppressSpecsAnnouncements())
                return false;

            summary = BuildSpecsSummary();
            return true;
        }

        private static string BuildCreditsSummary()
        {
            return Loc.Get("credits_summary");
        }

        private static bool TryBuildCreditsSummary(out string summary)
        {
            summary = null;

            if (!TryGetActiveCreditsScreen(out CreditsScreen _))
                return false;

            summary = BuildCreditsSummary();
            return true;
        }

        private void UpdateSpecsVisibilityState()
        {
            bool isSpecsVisible = SpecStatMain.Instance != null && SpecStatMain.Instance.visible;
            if (isSpecsVisible && !_wasSpecsVisible)
            {
                _suppressInitialSpecsAnnouncementsUntil = Time.unscaledTime + SpecsInitialAnnouncementGraceSeconds;
                _awaitingSpecsTutorialDialogs = Singleton<Save>.Instance != null && !Singleton<Save>.Instance.HasSeenSpecsTutorialMessages();
                _suppressPendingSpecsTutorialUntil = _awaitingSpecsTutorialDialogs
                    ? Time.unscaledTime + SpecsTutorialDialogStartTimeoutSeconds
                    : 0f;
            }

            if (_awaitingSpecsTutorialDialogs)
            {
                bool hasActiveUIDialog = UIDialogManager.Instance != null && UIDialogManager.Instance.HasActiveDialogs;
                if (hasActiveUIDialog)
                {
                    _suppressPendingSpecsTutorialUntil = Time.unscaledTime + SpecsTutorialDialogTransitionGraceSeconds;
                }
                else if (Time.unscaledTime >= _suppressPendingSpecsTutorialUntil)
                {
                    _awaitingSpecsTutorialDialogs = false;
                    _suppressPendingSpecsTutorialUntil = 0f;
                }
            }

            if (!isSpecsVisible)
            {
                _suppressInitialSpecsAnnouncementsUntil = 0f;
                _awaitingSpecsTutorialDialogs = false;
                _suppressPendingSpecsTutorialUntil = 0f;
            }

            _wasSpecsVisible = isSpecsVisible;
        }

        private static bool ShouldSuppressSpecsAnnouncements()
        {
            if (SpecStatMain.Instance == null || !SpecStatMain.Instance.visible)
                return false;

            return Time.unscaledTime < _suppressInitialSpecsAnnouncementsUntil ||
                _awaitingSpecsTutorialDialogs;
        }

        private static string BuildSettingsSummary()
        {
            int textLanguage = 0;
            float masterVolume = 1f;
            float musicVolume = 1f;

            if (T17.Services.Services.GameSettings != null)
            {
                textLanguage = T17.Services.Services.GameSettings.GetInt("textLanguage", 0);
                masterVolume = T17.Services.Services.GameSettings.GetFloat("masterVolume", 1f);
                musicVolume = T17.Services.Services.GameSettings.GetFloat("musicVolume", 1f);
            }

            string language = Loc.Get(textLanguage == 0 ? "language_english" : "language_japanese");
            return Loc.Get("settings_summary", language, Mathf.RoundToInt(masterVolume * 100f), Mathf.RoundToInt(musicVolume * 100f));
        }

        private static string GetCurrentRoomName()
        {
            if (Singleton<CameraSpaces>.Instance == null)
                return null;

            triggerzone zone = Singleton<CameraSpaces>.Instance.PlayerZone();
            if (zone == null)
                return null;

            return NormalizeIdentifierName(zone.Name);
        }

        private static string GetCurrentRoomScanName(string currentZone)
        {
            string roomName = GetCurrentRoomName();
            if (!string.IsNullOrEmpty(roomName))
                return roomName;

            string normalizedZoneName = NormalizeIdentifierName(currentZone);
            return !string.IsNullOrEmpty(normalizedZoneName)
                ? normalizedZoneName
                : Loc.Get("room_scan_unknown_room");
        }

        private static bool TryGetCurrentTutorialObjectiveText(out string objectiveText)
        {
            objectiveText = null;

            if (TutorialController.Instance == null)
                return false;

            EnsureReflectionCache();
            TMP_Text signpostText = _tutorialSignpostTextField != null ? _tutorialSignpostTextField.GetValue(TutorialController.Instance) as TMP_Text : null;
            if (signpostText == null)
                return false;

            objectiveText = NormalizeText(signpostText.text);
            return !string.IsNullOrEmpty(objectiveText);
        }

        private static string GetObjectFacingDisplayName(InteractableObj interactable)
        {
            if (interactable == null)
                return Loc.Get("unknown_object");

            string mainTextName = NormalizeObjectLabelCandidate(interactable.mainText);
            if (!IsActionStyleObjectLabel(mainTextName))
                return mainTextName;

            string alternateInteractionName = NormalizeObjectLabelCandidate(GetAlternateInteractionDisplayName(interactable));
            if (!IsActionStyleObjectLabel(alternateInteractionName))
                return alternateInteractionName;

            string displayName = NormalizeObjectIdentifierName(interactable.name);
            if (!string.IsNullOrEmpty(displayName))
                return displayName;

            displayName = NormalizeObjectIdentifierName(interactable.InternalName());
            return string.IsNullOrEmpty(displayName) ? Loc.Get("unknown_object") : displayName;
        }

        private static string GetInteractableHierarchyText(InteractableObj interactable)
        {
            if (interactable == null || interactable.transform == null)
                return string.Empty;

            List<string> names = new List<string>();
            Transform current = interactable.transform;
            int depth = 0;
            while (current != null && depth < 12)
            {
                names.Add(current.name);
                current = current.parent;
                depth++;
            }

            return string.Join(" ", names.ToArray());
        }

        private static string GetInteractableDisplayName(InteractableObj interactable)
        {
            if (interactable == null)
                return Loc.Get("unknown_object");

            string objectName = GetUnmetInteractableDisplayName(interactable);
            Save save = Singleton<Save>.Instance;
            if (save == null)
                return objectName;

            string internalName = interactable.InternalName();
            if (!HasMetInteractable(save, internalName))
                return objectName;

            if (save.TryGetNameByInternalName(internalName, out string displayName) && !string.IsNullOrEmpty(displayName))
            {
                string normalizedDisplayName = NormalizeIdentifierName(displayName);
                if (!string.IsNullOrEmpty(normalizedDisplayName) &&
                    !string.Equals(normalizedDisplayName, NormalizeIdentifierName(internalName), StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedDisplayName;
                }
            }

            return objectName;
        }

        private static bool HasMetInteractable(Save save, string internalName)
        {
            if (save == null || string.IsNullOrWhiteSpace(internalName))
                return false;

            string statusName = internalName.Equals("cf", StringComparison.OrdinalIgnoreCase)
                ? "celia"
                : internalName;

            return save.GetDateStatus(statusName) != RelationshipStatus.Unmet;
        }

        private static string GetUnmetInteractableDisplayName(InteractableObj interactable)
        {
            return GetObjectFacingDisplayName(interactable);
        }

        private static string GetAlternateInteractionDisplayName(InteractableObj interactable)
        {
            if (interactable == null || interactable.AlternateInteractions == null || interactable.AlternateInteractions.Count < 1)
                return null;

            Interactable alternateInteraction = interactable.AlternateInteractions[0];
            return NormalizeText(alternateInteraction != null ? alternateInteraction.Name : null);
        }

        private static string NormalizeObjectLabelCandidate(string value)
        {
            string normalized = NormalizeText(value);
            if (string.IsNullOrEmpty(normalized))
                return null;

            if (normalized.StartsWith("Default hover text for ", StringComparison.OrdinalIgnoreCase))
                return null;

            if (string.Equals(normalized, "Main Camera", StringComparison.OrdinalIgnoreCase))
                return null;

            return normalized;
        }

        private static string NormalizeObjectIdentifierName(string value)
        {
            string normalized = NormalizeIdentifierName(value);
            if (string.IsNullOrEmpty(normalized))
                return null;

            normalized = Regex.Replace(normalized, "(?<=[a-z])(?=[A-Z])", " ");
            normalized = Regex.Replace(normalized, "(?<=[A-Za-z])(?=[0-9])", " ");
            normalized = Regex.Replace(normalized, "(?<=[0-9])(?=[A-Za-z])", " ");
            while (normalized.Contains("  "))
            {
                normalized = normalized.Replace("  ", " ");
            }

            return normalized.Trim();
        }

        private static bool IsActionStyleObjectLabel(string label)
        {
            label = NormalizeObjectLabelCandidate(label);
            if (string.IsNullOrEmpty(label))
                return true;

            string lowered = label.ToLowerInvariant();
            string[] actionPrefixes =
            {
                "turn ",
                "turn on",
                "turn off",
                "switch ",
                "open ",
                "close ",
                "check ",
                "look ",
                "talk ",
                "use ",
                "pick up",
                "grab ",
                "awaken ",
                "start ",
                "stop ",
                "inspect ",
                "examine ",
                "enter ",
                "leave ",
                "read ",
                "press ",
                "activate "
            };

            for (int i = 0; i < actionPrefixes.Length; i++)
            {
                if (lowered.StartsWith(actionPrefixes[i], StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool ContainsToken(string value, string token)
        {
            return !string.IsNullOrEmpty(value) &&
                !string.IsNullOrEmpty(token) &&
                value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetInkVariableString(string variableName)
        {
            if (string.IsNullOrEmpty(variableName) || Singleton<InkController>.Instance == null)
                return null;

            return Singleton<InkController>.Instance.GetVariable(variableName);
        }

        private static bool GetInkVariableBool(string variableName)
        {
            string value = GetInkVariableString(variableName);
            return bool.TryParse(value, out bool parsedValue) && parsedValue;
        }

        private static void EnsureReflectionCache()
        {
            if (_talkingUiDialogBoxField != null)
                return;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _talkingUiDialogBoxField = typeof(TalkingUI).GetField("dialogBox", flags);
            _talkingUiChoicesButtonsField = typeof(TalkingUI).GetField("choicesButtons", flags);
            _dialogBoxNameTextField = typeof(DialogBoxBehavior).GetField("nameText", flags);
            _dialogBoxDialogTextField = typeof(DialogBoxBehavior).GetField("dialogText", flags);
            _resultSplashTitleBannerField = typeof(ResultSplashScreen).GetField("_titleBanner", flags);
            _collectablesScreenNameField = typeof(CollectablesScreen).GetField("collectableName", flags);
            _collectablesScreenDescField = typeof(CollectablesScreen).GetField("collectableDesc", flags);
            _tutorialSignpostField = typeof(TutorialController).GetField("tutorialSignpost", flags);
            _tutorialSignpostTextField = typeof(TutorialController).GetField("tutorialSignpostTMP", flags);
            _tutorialSubtitleTextField = typeof(TutorialController).GetField("SubtitleText", flags);
            _tutorialGiftBoxField = typeof(TutorialController).GetField("giftBox", flags);
            _tutorialFrontDoorField = typeof(TutorialController).GetField("frontDoor", flags);
            _tutorialComputerField = typeof(TutorialController).GetField("computer", flags);
            _tutorialTriggerZonesField = typeof(TutorialController).GetField("triggerZones", flags);
            _roomersCurrentEntryField = typeof(Roomers).GetField("currentEntry", flags);
            _roomersEntriesField = typeof(Roomers).GetField("RoomersEntries", flags);
            _specStatTooltipsField = typeof(SpecStatMain).GetField("statTooltips", flags);
            _specStatMainKeyButtonField = typeof(SpecStatMain).GetField("keyButton", flags);
            _specStatMainAutoSelectFallbackField = typeof(SpecStatMain).GetField("autoSelectFallback", flags);
            _specStatMainCurrentPageField = typeof(SpecStatMain).GetField("currentPage", flags);
            _specStatBlockNameFirstLetterField = typeof(SpecStatBlock).GetField("NameFirstLetter", flags);
            _specStatBlockNameRestField = typeof(SpecStatBlock).GetField("NameRest", flags);
            _specStatBlockAdjectiveLabelField = typeof(SpecStatBlock).GetField("AdjectiveLabel", flags);
            _specStatBlockLevelDescriptionTextField = typeof(SpecStatBlock).GetField("levelDescriptionText", flags);
            _specGlossaryBlockNameFirstLetterField = typeof(SpecGlossaryBlock).GetField("NameFirstLetter", flags);
            _specGlossaryBlockNameRestField = typeof(SpecGlossaryBlock).GetField("NameRest", flags);
            _specGlossaryBlockDescriptionTextField = typeof(SpecGlossaryBlock).GetField("descriptionText", flags);
            _creditsScreenTextField = typeof(CreditsScreen).GetField("tmp_credits", flags);
            _uiDialogManagerActiveDialogsField = typeof(UIDialogManager).GetField("_activeDialogs", flags);
            _uiDialogGameObjectField = typeof(UIDialog).GetField("_theDialog", flags);
            _uiDialogTitleField = typeof(UIDialog).GetField("_title", flags);
            _uiDialogBodyTextField = typeof(UIDialog).GetField("_bodyText", flags);
            _saveScreenManagerNewSaveSlotField = typeof(SaveScreenManager).GetField("newSaveSlot", flags);
            _saveSlotPlayTimeField = typeof(SaveSlot).GetField("playTime", flags);
            _saveSlotDaysPlayedField = typeof(SaveSlot).GetField("daysPlayed", flags);
            _betterPlayerControlMoveField = typeof(BetterPlayerControl).GetField("move", flags);
            _betterPlayerControlLookField = typeof(BetterPlayerControl).GetField("look", flags);
            _engagementType = FindLoadedType("T17.Flow.Engagement");
            if (_engagementType != null)
            {
                _engagementTitleField = _engagementType.GetField("m_Text_EngagementTitle", flags);
                _engagementStateField = _engagementType.GetField("m_Text_EngagementState", flags);
            }
            _loadingFactsType = FindLoadedType("Assets.Date_Everything.Scripts.UI.Loading.LoadingFacts");
        }

        private static Type FindLoadedType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type resolvedType = assemblies[i].GetType(fullName, throwOnError: false);
                if (resolvedType != null)
                    return resolvedType;
            }

            return null;
        }

        private static string ExtractTextFromObject(GameObject target)
        {
            if (target == null)
                return null;

            var segments = new List<string>();
            var textComponents = target.GetComponentsInChildren<TMP_Text>(includeInactive: true);
            for (int i = 0; i < textComponents.Length; i++)
            {
                if (textComponents[i] == null || !textComponents[i].gameObject.activeInHierarchy)
                    continue;

                string value = NormalizeText(textComponents[i].text);
                if (string.IsNullOrEmpty(value))
                    continue;

                AddAnnouncementPart(segments, value);
            }

            var slider = target.GetComponent<Slider>();
            if (slider != null)
            {
                segments.Add(Loc.Get("value_number", Mathf.RoundToInt(slider.value)));
            }

            var toggle = target.GetComponent<Toggle>();
            if (toggle != null)
            {
                segments.Add(Loc.Get(toggle.isOn ? "settings_value_on" : "settings_value_off"));
            }

            if (segments.Count == 0)
                return null;

            return string.Join(". ", segments.ToArray());
        }

        private static string ExtractVisibleTextFromObject(GameObject target)
        {
            if (target == null)
                return null;

            var segments = new List<string>();
            TMP_Text[] textComponents = target.GetComponentsInChildren<TMP_Text>(includeInactive: true);
            for (int i = 0; i < textComponents.Length; i++)
            {
                TMP_Text textComponent = textComponents[i];
                if (textComponent == null || !textComponent.gameObject.activeInHierarchy || !textComponent.enabled)
                    continue;

                string value = GetVisibleTextInMaskedParent(textComponent);
                AddAnnouncementPart(segments, value);
            }

            return JoinAnnouncementParts(segments);
        }

        private static string BuildPhoneAppVisibleTextFallback(GameObject currentApp, string appName)
        {
            var parts = new List<string>();

            AddAnnouncementPart(parts, ExtractVisibleTextFromObject(currentApp));

            if (!string.IsNullOrEmpty(appName) && appName.IndexOf("roomers", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAnnouncementPart(parts, Roomers.Instance != null ? ExtractVisibleTextFromObject(Roomers.Instance.RoomersWindow) : null);
            }
            else if (!string.IsNullOrEmpty(appName) && (appName.IndexOf("date a dex", StringComparison.OrdinalIgnoreCase) >= 0 || appName.IndexOf("dateadex", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                AddAnnouncementPart(parts, DateADex.Instance != null ? ExtractVisibleTextFromObject(DateADex.Instance.DateADexWindow) : null);
                AddAnnouncementPart(parts, DateADex.Instance != null ? ExtractVisibleTextFromObject(DateADex.Instance.RecipeScreen) : null);
            }
            else if (!string.IsNullOrEmpty(appName) && appName.IndexOf("thiscord", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAnnouncementPart(parts, ChatMaster.Instance != null ? ExtractVisibleTextFromObject(ChatMaster.Instance.Thiscord) : null);
            }
            else if (!string.IsNullOrEmpty(appName) && (appName.IndexOf("wrkspace", StringComparison.OrdinalIgnoreCase) >= 0 || appName.IndexOf("workspace", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                AddAnnouncementPart(parts, ChatMaster.Instance != null ? ExtractVisibleTextFromObject(ChatMaster.Instance.Workspace) : null);
            }
            else if (!string.IsNullOrEmpty(appName) && appName.IndexOf("canopy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAnnouncementPart(parts, ChatMaster.Instance != null ? ExtractVisibleTextFromObject(ChatMaster.Instance.Canopy) : null);
            }
            else if (!string.IsNullOrEmpty(appName) && appName.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAnnouncementPart(parts, MusicPlayer.Instance != null ? ExtractVisibleTextFromObject(MusicPlayer.Instance.gameObject) : null);
            }
            else if (!string.IsNullOrEmpty(appName) && appName.IndexOf("art", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddAnnouncementPart(parts, ArtPlayer.Instance != null ? ExtractVisibleTextFromObject(ArtPlayer.Instance.gameObject) : null);
            }

            return JoinAnnouncementParts(parts);
        }

        private static string GetSettingsSelectorLabel(SettingsMenuSelector selector)
        {
            if (selector == null)
                return null;

            string selectedValue = NormalizeText(selector.SelectedOption != null ? selector.SelectedOption.text : null);
            TMP_Text[] texts = selector.GetComponentsInChildren<TMP_Text>(includeInactive: true);
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null || text == selector.SelectedOption)
                    continue;

                if (selector.NextOption != null && text.transform.IsChildOf(selector.NextOption.transform))
                    continue;

                if (selector.PreviousOption != null && text.transform.IsChildOf(selector.PreviousOption.transform))
                    continue;

                string value = NormalizeText(text.text);
                if (string.IsNullOrEmpty(value) || value == selectedValue)
                    continue;

                return value;
            }

            return NormalizeIdentifierName(selector.SettingKey);
        }

        private static string BuildSettingsSliderAnnouncement(SettingsMenu settingsMenu, GameObject selectedObject)
        {
            string value;

            if (IsWithin(selectedObject, settingsMenu.CameraSensitivitySlider, settingsMenu.CameraSensitivitySliderValue, settingsMenu.CameraSensitivitySliderValue != null ? settingsMenu.CameraSensitivitySliderValue.gameObject : null, out value))
                return Loc.Get("settings_slider_camera_sensitivity", value);

            if (IsWithin(selectedObject, settingsMenu.MasterVolumeSlider, settingsMenu.MasterVolumeSliderValue, settingsMenu.MasterVolumeSliderValue != null ? settingsMenu.MasterVolumeSliderValue.gameObject : null, out value))
                return Loc.Get("settings_slider_master_volume", value);

            if (IsWithin(selectedObject, settingsMenu.SFXVolumeSlider, settingsMenu.SFXVolumeSliderValue, settingsMenu.SFXVolumeSliderValue != null ? settingsMenu.SFXVolumeSliderValue.gameObject : null, out value))
                return Loc.Get("settings_slider_sfx_volume", value);

            if (IsWithin(selectedObject, settingsMenu.MusicVolumeSlider, settingsMenu.MusicVolumeSliderValue, settingsMenu.MusicVolumeSliderValue != null ? settingsMenu.MusicVolumeSliderValue.gameObject : null, out value))
                return Loc.Get("settings_slider_music_volume", value);

            if (IsWithin(selectedObject, settingsMenu.VoiceVolumeSlider, settingsMenu.VoiceVolumeSliderValue, settingsMenu.VoiceVolumeSliderValue != null ? settingsMenu.VoiceVolumeSliderValue.gameObject : null, out value))
                return Loc.Get("settings_slider_voice_volume", value);

            if (IsWithin(selectedObject, settingsMenu.CameraFieldOfViewSlider, settingsMenu.CameraFieldOfViewSliderValue, settingsMenu.CameraFieldOfViewSliderValue != null ? settingsMenu.CameraFieldOfViewSliderValue.gameObject : null, out value))
                return Loc.Get("settings_slider_field_of_view", value);

            if (IsWithin(selectedObject, settingsMenu.MovementSpeedSlider, settingsMenu.MovementSpeedSliderValue, settingsMenu.MovementSpeedSliderValue != null ? settingsMenu.MovementSpeedSliderValue.gameObject : null, out value))
                return Loc.Get("settings_slider_movement_speed", value);

            return null;
        }

        private static bool IsWithin(GameObject selectedObject, Component primary, Component secondary, GameObject secondaryObject, out string value)
        {
            value = null;

            if (selectedObject == null)
                return false;

            if (primary != null && (selectedObject == primary.gameObject || selectedObject.transform.IsChildOf(primary.transform)))
            {
                value = NormalizeText(ExtractTextFromObject(primary.gameObject));
                return true;
            }

            if (secondary != null && (selectedObject == secondary.gameObject || selectedObject.transform.IsChildOf(secondary.transform)))
            {
                value = NormalizeText(ExtractTextFromObject(secondary.gameObject));
                return true;
            }

            if (secondaryObject != null && (selectedObject == secondaryObject || selectedObject.transform.IsChildOf(secondaryObject.transform)))
            {
                value = NormalizeText(ExtractTextFromObject(secondaryObject));
                return true;
            }

            return false;
        }

        private static string GetChatDisplayName(ParallelChat activeChat, GameObject activePanelNameObject)
        {
            string name = NormalizeText(ExtractTextFromObject(activePanelNameObject));
            if (!string.IsNullOrEmpty(name))
                return name;

            if (activeChat != null)
            {
                name = NormalizeText(ExtractTextFromObject(activeChat.button));
                if (!string.IsNullOrEmpty(name))
                    return name;

                if (activeChat.appMessage != null)
                    return NormalizeText(activeChat.appMessage.Name);
            }

            return null;
        }

        private static string GetChatTranscript(ParallelChat chat)
        {
            if (chat == null || chat.Chatbox == null)
                return null;

            var transcript = new List<string>();
            for (int i = 0; i < chat.Chatbox.childCount; i++)
            {
                Transform chatTransform = chat.Chatbox.GetChild(i);
                if (!IsChatMessageVisible(chat, chatTransform))
                    continue;

                ChatTextBox textBox = chatTransform.GetComponent<ChatTextBox>();
                if (textBox == null)
                    continue;

                string text = NormalizeText(textBox.Dialogue != null ? textBox.Dialogue.text : null);
                AddAnnouncementPart(transcript, text);
            }

            return JoinAnnouncementParts(transcript);
        }

        private static bool IsChatMessageVisible(ParallelChat chat, Transform chatTransform)
        {
            if (chat == null || chatTransform == null || !chatTransform.gameObject.activeInHierarchy)
                return false;

            RectTransform messageRect = chatTransform as RectTransform;
            if (messageRect == null)
                return true;

            return IsRectVisibleInViewport(messageRect, chat.screct);
        }

        private static bool IsRectVisibleInViewport(RectTransform rectTransform, ScrollRect scrollRect)
        {
            if (rectTransform == null || !rectTransform.gameObject.activeInHierarchy)
                return false;

            RectTransform viewport = GetScrollViewport(scrollRect);

            if (viewport == null)
                return true;

            Vector3[] worldCorners = new Vector3[4];
            rectTransform.GetWorldCorners(worldCorners);

            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < worldCorners.Length; i++)
            {
                Vector3 localCorner = viewport.InverseTransformPoint(worldCorners[i]);
                minX = Mathf.Min(minX, localCorner.x);
                maxX = Mathf.Max(maxX, localCorner.x);
                minY = Mathf.Min(minY, localCorner.y);
                maxY = Mathf.Max(maxY, localCorner.y);
            }

            Rect viewportRect = viewport.rect;
            bool overlapsHorizontally = maxX >= viewportRect.xMin && minX <= viewportRect.xMax;
            bool overlapsVertically = maxY >= viewportRect.yMin && minY <= viewportRect.yMax;
            return overlapsHorizontally && overlapsVertically;
        }

        private static RectTransform GetScrollViewport(ScrollRect scrollRect)
        {
            if (scrollRect == null)
                return null;

            return scrollRect.viewport != null
                ? scrollRect.viewport
                : scrollRect.GetComponent<RectTransform>();
        }

        private static bool TryGetTopUIDialog(out UIDialog dialog)
        {
            dialog = null;

            if (!TryGetActiveUIDialogs(out List<UIDialog> dialogs) || dialogs.Count == 0)
                return false;

            dialog = dialogs[dialogs.Count - 1];
            return dialog != null;
        }

        private static int GetActiveUIDialogButtonCount(UIDialog dialog)
        {
            if (dialog == null)
                return 0;

            int activeButtonCount = 0;
            UIDialogButton[] buttons = dialog.Buttons;
            if (buttons == null)
                return 0;

            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] != null && buttons[i].Button != null && buttons[i].Button.gameObject.activeInHierarchy)
                    activeButtonCount++;
            }

            return activeButtonCount;
        }

        private static bool TryGetActiveUIDialogs(out List<UIDialog> dialogs)
        {
            dialogs = null;

            if (UIDialogManager.Instance == null || !UIDialogManager.Instance.HasActiveDialogs)
                return false;

            EnsureReflectionCache();
            dialogs = _uiDialogManagerActiveDialogsField != null
                ? _uiDialogManagerActiveDialogsField.GetValue(UIDialogManager.Instance) as List<UIDialog>
                : null;
            return dialogs != null && dialogs.Count > 0;
        }

        private static bool TryGetActiveCreditsScreen(out CreditsScreen creditsScreen)
        {
            creditsScreen = null;

            CreditsScreen[] screens = FindObjectsOfType<CreditsScreen>();
            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i] != null && screens[i].gameObject.activeInHierarchy)
                {
                    creditsScreen = screens[i];
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetActiveSaveScreenManager(out SaveScreenManager saveScreenManager)
        {
            saveScreenManager = null;

            SaveScreenManager[] screens = FindObjectsOfType<SaveScreenManager>();
            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i] != null && screens[i].gameObject.activeInHierarchy)
                {
                    saveScreenManager = screens[i];
                    return true;
                }
            }

            return false;
        }

        private static bool AreAnySpecsGlossaryBlocksVisible()
        {
            if (SpecStatMain.Instance == null || SpecStatMain.Instance.Active_Stat_Blocks == null)
                return false;

            List<SpecStatMain.StatBlockRef> statBlocks = SpecStatMain.Instance.Active_Stat_Blocks;
            for (int i = 0; i < statBlocks.Count; i++)
            {
                SpecGlossaryBlock glossaryBlock = statBlocks[i].GlossaryBlock;
                if (glossaryBlock != null && glossaryBlock.gameObject.activeInHierarchy)
                    return true;
            }

            return false;
        }

        private static bool IsSpecsGlossaryPage()
        {
            if (SpecStatMain.Instance == null || !SpecStatMain.Instance.visible)
                return false;

            EnsureReflectionCache();
            object currentPage = _specStatMainCurrentPageField != null
                ? _specStatMainCurrentPageField.GetValue(SpecStatMain.Instance)
                : null;
            if (currentPage != null)
                return string.Equals(currentPage.ToString(), "Glossary", StringComparison.OrdinalIgnoreCase);

            return AreAnySpecsGlossaryBlocksVisible();
        }

        private static string GetVisibleTextInMaskedParent(TMP_Text textComponent)
        {
            if (textComponent == null || !textComponent.gameObject.activeInHierarchy)
                return null;

            RectTransform viewport = FindMaskedViewport(textComponent.transform);
            if (viewport == null)
                return NormalizeText(textComponent.text);

            string visibleText = GetVisibleTextInViewport(textComponent, viewport);
            return string.IsNullOrEmpty(visibleText)
                ? NormalizeText(textComponent.text)
                : visibleText;
        }

        private static RectTransform FindMaskedViewport(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                if (current.GetComponent<RectMask2D>() != null || current.GetComponent<Mask>() != null)
                    return current as RectTransform;

                current = current.parent;
            }

            return null;
        }

        private static string GetVisibleTextInViewport(TMP_Text textComponent, RectTransform viewport)
        {
            if (textComponent == null || viewport == null)
                return null;

            textComponent.ForceMeshUpdate();
            TMP_TextInfo textInfo = textComponent.textInfo;
            if (textInfo == null || textInfo.lineCount == 0)
                return NormalizeText(textComponent.text);

            string sourceText = textComponent.text;
            RectTransform textRect = textComponent.rectTransform;
            Rect viewportRect = viewport.rect;
            var visibleLines = new List<string>();

            for (int i = 0; i < textInfo.lineCount; i++)
            {
                TMP_LineInfo line = textInfo.lineInfo[i];
                float topY = viewport.InverseTransformPoint(textRect.TransformPoint(new Vector3(0f, line.ascender, 0f))).y;
                float bottomY = viewport.InverseTransformPoint(textRect.TransformPoint(new Vector3(0f, line.descender, 0f))).y;
                if (topY < viewportRect.yMin || bottomY > viewportRect.yMax)
                    continue;

                int startIndex = line.firstCharacterIndex;
                int length = line.characterCount;
                if (startIndex < 0 || length <= 0 || startIndex >= sourceText.Length)
                    continue;

                if (startIndex + length > sourceText.Length)
                    length = sourceText.Length - startIndex;

                AddAnnouncementPart(visibleLines, NormalizeText(sourceText.Substring(startIndex, length)));
            }

            return JoinAnnouncementParts(visibleLines);
        }

        private static string GetVisibleChatChoices(ParallelChat chat)
        {
            if (chat == null || chat.Options == null || chat.Options.Length == 0)
                return null;

            var choices = new List<string>();
            for (int i = 0; i < chat.Options.Length; i++)
            {
                Button option = chat.Options[i];
                if (option == null || !option.gameObject.activeInHierarchy)
                    continue;

                TMP_Text optionText = option.GetComponentInChildren<TMP_Text>(includeInactive: true);
                string text = NormalizeText(optionText != null ? optionText.text : null);
                if (string.IsNullOrEmpty(text) || text == "...")
                    continue;

                AddAnnouncementPart(choices, text);
            }

            return JoinAnnouncementParts(choices);
        }

        private static void AddAnnouncementPart(List<string> parts, string value)
        {
            string cleaned = NormalizeText(value);
            if (string.IsNullOrEmpty(cleaned))
                return;

            if (!parts.Contains(cleaned))
                parts.Add(cleaned);
        }

        private static string JoinAnnouncementParts(List<string> parts)
        {
            if (parts == null || parts.Count == 0)
                return null;

            return string.Join(". ", parts.ToArray());
        }

        private static string BuildLabeledValue(string key, string value)
        {
            string cleaned = NormalizeText(value);
            if (string.IsNullOrEmpty(cleaned))
                return null;

            return Loc.Get(key, cleaned);
        }

        private static GameObject ResolveSelectableTarget(GameObject selectedObject)
        {
            if (selectedObject.GetComponent<Selectable>() != null)
                return selectedObject;

            var selectable = selectedObject.GetComponentInParent<Selectable>();
            if (selectable != null && selectable.gameObject.activeInHierarchy)
                return selectable.gameObject;

            return selectedObject;
        }

        private static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string cleaned = RichTextRegex.Replace(value, " ");
            cleaned = cleaned.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();

            while (cleaned.Contains("  "))
            {
                cleaned = cleaned.Replace("  ", " ");
            }

            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            return cleaned;
        }

        private static string NormalizeIdentifierName(string value)
        {
            string cleaned = NormalizeText(value);
            if (string.IsNullOrEmpty(cleaned))
                return null;

            cleaned = cleaned.Replace("DateADex", "Date A Dex");
            cleaned = cleaned.Replace("Wrkspace", "Workspace");
            cleaned = cleaned.Replace("MainMenu", "Main menu");
            cleaned = cleaned.Replace("PauseScreen", "Pause screen");
            cleaned = cleaned.Replace("PhoneMenu", "Phone menu");
            cleaned = cleaned.Replace("SaveLoad", "Save Load");
            cleaned = cleaned.Replace("_", " ");

            while (cleaned.Contains("  "))
            {
                cleaned = cleaned.Replace("  ", " ");
            }

            return cleaned.Trim();
        }

    }
}
