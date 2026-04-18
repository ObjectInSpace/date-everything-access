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

        private enum TrackedNavigationMode
        {
            None,
            DirectObject,
            EquivalentZoneAnchor
        }

        private enum TransitionSweepPhase
        {
            None,
            AwaitingNextStep,
            AwaitingTeleportSettle,
            AwaitingDoorInteractionSettle,
            Running
        }

        private enum TransitionSweepKind
        {
            OpenPassage,
            Door
        }

        private enum LiveRouteAuditPhase
        {
            None,
            AwaitingNextRoute,
            AwaitingTeleportSettle,
            Running
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

        private sealed class OpenPassageTransitionOverride
        {
            public string[] AcceptedSourceZones;
            public string[] AcceptedDestinationZones;
            public float DestinationApproachBias;
            public Vector3[] IntermediateWaypoints;
            public float StepTimeoutSeconds;
            public bool UseExplicitCrossingSegments;
        }

        private sealed class GuidedNavigationPoint
        {
            public Vector3 Position;
            public float Progress;
            public int Sequence;
        }

        [DataContract]
        private sealed class OpenPassageTransitionOverrideDocument
        {
            [DataMember(Name = "Entries")]
            public OpenPassageTransitionOverrideEntry[] Entries = null;
        }

        [DataContract]
        private sealed class OpenPassageTransitionOverrideEntry
        {
            [DataMember(Name = "FromZone")]
            public string FromZone = null;

            [DataMember(Name = "ToZone")]
            public string ToZone = null;

            [DataMember(Name = "AcceptedSourceZones")]
            public string[] AcceptedSourceZones = null;

            [DataMember(Name = "AcceptedDestinationZones")]
            public string[] AcceptedDestinationZones = null;

            [DataMember(Name = "DestinationApproachBias")]
            public float DestinationApproachBias = 0f;

            [DataMember(Name = "IntermediateWaypoints")]
            public SerializableVector3[] IntermediateWaypoints = null;

            [DataMember(Name = "StepTimeoutSeconds")]
            public float StepTimeoutSeconds = 0f;

            [DataMember(Name = "UseExplicitCrossingSegments")]
            public bool UseExplicitCrossingSegments = false;
        }

        [DataContract]
        private sealed class SerializableVector3
        {
            [DataMember(Name = "x")]
            public float X = 0f;

            [DataMember(Name = "y")]
            public float Y = 0f;

            [DataMember(Name = "z")]
            public float Z = 0f;

            public Vector3 ToVector3()
            {
                return new Vector3(X, Y, Z);
            }
        }

        private enum TutorialObjectiveKind
        {
            None,
            Computer,
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

        private sealed class RoomObjectTarget
        {
            public InteractableObj Interactable;
            public string Label;
            public string ZoneName;
        }

        private sealed class TransitionSweepSession
        {
            public TransitionSweepKind Kind;
            public List<NavigationGraph.PathStep> Steps;
            public List<TransitionSweepReporter.MutableEntry> Entries;
            public string OutputPath;
            public int CurrentIndex = -1;
            public NavigationGraph.PathStep CurrentStep;
            public TransitionSweepPhase Phase;
            public float NextActionTime;
            public float StepStartedAt;
            public float LastHeartbeatAt;
            public bool UsedZoneFallbackSpawn;
            public bool DoorInteractionTriggered;
            public Vector3 DoorPushThroughPosition;
            public bool DoorPostThresholdCommitted;
            public bool DoorTimeoutRecoveryUsed;
            public bool DoorTimeoutFinalGraceUsed;
        }

        private sealed class LiveRouteAuditRoute
        {
            public string Key;
            public string RouteSignature;
            public string StartZone;
            public string TargetZone;
            public string TargetLabel;
            public string[] AliasPairs;
            public List<NavigationGraph.PathStep> PathSteps;
            public Vector3 SpawnPosition;
            public string SpawnSource;
            public float TimeoutSeconds;
            public int EntryIndex;
        }

        private sealed class LiveRouteAuditSession
        {
            public List<LiveRouteAuditRoute> Routes;
            public List<LiveRouteAuditReporter.MutableEntry> Entries;
            public string OutputPath;
            public int OrderedPairCount;
            public int UniqueRouteCount;
            public int CurrentIndex = -1;
            public LiveRouteAuditRoute CurrentRoute;
            public LiveRouteAuditPhase Phase;
            public float NextActionTime;
            public float RouteStartedAt;
            public float LastHeartbeatAt;
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
        private const float AutoWalkOpenPassageDestinationApproachDistance = 2.5f;
        private const float AutoWalkLookScaleDegrees = 45f;
        private const float AutoWalkProgressDistance = 0.35f;
        private const float AutoWalkBlockedTimeoutSeconds = 2f;
        private const float AutoWalkInteractionRetrySeconds = 0.75f;
        private const float AutoWalkZoneBoundaryFallbackDistance = 5f;
        private const float AutoWalkOpenPassageCommitDistance = 2.5f;
        private const float AutoWalkOpenPassageHandoffDistance = 2.5f;
        private const float OpenPassageGuidedWaypointAdvanceDistance = 1.25f;
        private const float OpenPassageGuidedWaypointDedupDistance = 0.25f;
        private const float LocalNavigationPathAdvanceDistance = 0.75f;
        private const float LocalNavigationLookaheadDistance = 3f;
        private const float LocalNavigationGoalReachedDistance = 2f;
        private const float OpenPassageOverrideLocalNavigationGoalReachedDistance = 0.75f;
        private const float DoorThresholdHandoffLocalNavigationGoalReachedDistance = 0.35f;
        private const float DoorPushThroughLocalNavigationGoalReachedDistance = 0.35f;
        private const float LocalNavigationGoalRetargetDistance = 0.5f;
        private const float TrackedInteractableApproachClearanceDistance = 0.9f;
        private const float TrackedInteractableApproachRetargetDistance = 0.75f;
        private const float TrackedInteractableApproachMinimumExtent = 0.35f;
        private const float AutoWalkConnectorSearchDistance = 4f;
        private const float InteractableZoneFallbackDistance = 8f;
        private const float TransitionSweepTeleportSettleSeconds = 0.25f;
        private const float TransitionSweepStepSpacingSeconds = 0.1f;
        private const float TransitionSweepCrossingFallbackOffset = 1.5f;
        private const float TransitionSweepSourceAcceptanceDistance = 5f;
        private const float TransitionSweepStepTimeoutSeconds = 5f;
        private const float TransitionSweepHeartbeatSeconds = 1f;
        private const float DoorTraversalClearanceDistance = 1.4f;
        private const float DoorTraversalLateralOffsetDistance = 0.6f;
        private const float DoorTransitionSweepInteractionSettleSeconds = 0.75f;
        private const float DoorTraversalPushThroughDistance = 1.35f;
        private const float DoorTraversalMaximumPushThroughDistance = 3.25f;
        private const float DoorTransitionSweepPostRecoveryGraceSeconds = 1.5f;
        private const float DoorPushThroughSourceAdvanceDistance = 1f;
        private const float DoorPushThroughArrivalDistance = 2.15f;
        private const float DoorPushThroughBlockedTimeoutSeconds = 3f;
        private const float LiveRouteAuditTeleportSettleSeconds = 0.25f;
        private const float LiveRouteAuditStepSpacingSeconds = 0.1f;
        private const float LiveRouteAuditHeartbeatSeconds = 1f;
        private const float LiveRouteAuditMinimumTimeoutSeconds = 10f;
        private const float LiveRouteAuditRouteOverheadSeconds = 2f;
        private const int AutoWalkMaxRecoveryAttempts = 2;
        private const int VkUp = 0x26;
        private const int VkDown = 0x28;
        private const int VkLeft = 0x25;
        private const int VkRight = 0x27;
        private const int VkReturn = 0x0D;
        private const int VkSpace = 0x20;
        private const int VkEscape = 0x1B;

        private static readonly Regex RichTextRegex = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Dictionary<string, OpenPassageTransitionOverride> OpenPassageTransitionOverrides =
            new Dictionary<string, OpenPassageTransitionOverride>(StringComparer.OrdinalIgnoreCase);
        private static bool _openPassageTransitionOverridesLoaded;
        private static string _openPassageTransitionOverrideLoadStatus = "uninitialized";
        private static string _openPassageTransitionOverrideLoadDetail = "Overrides not checked yet.";
        private static int _openPassageTransitionOverrideEntryCount;
        private static bool _openPassageTransitionOverrideNormalizedScalarArrays;
        private static string _openPassageTransitionOverridePath;
        private static string _openPassageTransitionOverrideFileSha256;
        private static string _openPassageTransitionOverrideFileLastWriteUtc;

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
        private static FieldInfo _tutorialFrontDoorField;
        private static FieldInfo _tutorialComputerField;
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
        private static int _exportNavMeshRequested;
        private static int _transitionSweepRequested;
        private static int _doorTransitionSweepRequested;
        private static int _liveRouteAuditRequested;
        private static int _pendingDateADexEntryAnnouncementRequested;
        private static float _pendingDateADexEntryAnnouncementNotBefore;
        private static float _pendingDateADexEntryAnnouncementExpiresAt;
        private static float _suppressDateADexOpenEntrySelectionUntil;
        private static float _suppressInitialSpecsAnnouncementsUntil;
        private static bool _awaitingSpecsTutorialDialogs;
        private static bool _choiceUpWasDown;
        private static bool _choiceDownWasDown;
        private static bool _choiceLeftWasDown;
        private static bool _choiceRightWasDown;
        private static bool _choiceReturnWasDown;
        private static bool _choiceSpaceWasDown;
        private static bool _roomPickerUpWasDown;
        private static bool _roomPickerDownWasDown;
        private static bool _roomPickerReturnWasDown;
        private static bool _roomPickerSpaceWasDown;
        private static bool _roomPickerEscapeWasDown;
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
        private string _lastNavigationTrackerDebugSnapshot;
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
        private int _navigationSelectionIndex = -1;
        private float _nextPollTime;
        private float _suppressDateADexSelectionUntil;
        private float _suppressPopupSelectionUntil;
        private float _suppressUIDialogSelectionUntil;
        private float _suppressSpecsSelectionUntil;
        private float _suppressCreditsSelectionUntil;
        private float _suppressPendingSpecsTutorialUntil;
        private float _lastNavigationInteractionAttemptTime;
        private float _lastAutoWalkProgressTime;
        private float _autoWalkTransitionUntil;
        private SpecsAnnouncementMode _lastSpecsAnnouncementMode;
        private InputModeHandle _roomObjectPickerInputHandle;
        private InteractableObj _trackedInteractable;
        private string _trackedInteractableId;
        private string _trackedInteractableLabel;
        private string _trackedInteractableZone;
        private string _trackedInteractableApproachId;
        private string _trackedInteractableApproachZone;
        private int _trackedInteractableApproachPreferredComponentId = -1;
        private string _lastRoomObjectListZone;
        private string _navigationTargetZone;
        private string _navigationTargetLabel;
        private string _lastNavigationNextZone;
        private string _lastNavigationAnnouncementLabel;
        private string _openPassageTraversalStepKey;
        private string _navigationZoneOverride;
        private string _navigationZoneOverrideStepKey;
        private string _localNavigationPathZone;
        private string _localNavigationPathContext;
        private string _localNavigationPathStepKey;
        private string _rawNavigationTargetContext;
        private string _lastTrackerTargetKind;
        private string _lastTrackerTargetStepKey;
        private Vector3 _lastAutoWalkPosition;
        private Vector3 _lastTrackerTargetPosition;
        private Vector3 _trackedInteractableApproachReferencePosition;
        private Vector3 _trackedInteractableApproachTarget;
        private Vector3 _localNavigationPathGoal;
        private List<NavigationGraph.PathStep> _navigationPath;
        private List<Vector3> _localNavigationPathPoints;
        private List<RoomObjectTarget> _roomObjectTargets;
        private int _autoWalkRecoveryAttempts;
        private int _localNavigationPathIndex;
        private int _openPassageOverrideWaypointIndex;
        private bool _hasLastTrackerTarget;
        private int _openPassageSourceHandoffRecoveryFloor;
        private int _openPassageDestinationHandoffRecoveryFloor;
        private float _openPassageSourceHandoffProgressFloor;
        private float _openPassageDestinationHandoffProgressFloor;
        private OpenPassageTraversalStage _openPassageTraversalStage;
        private string _doorTraversalStepKey;
        private bool _doorTraversalInteractionTriggered;
        private Vector3 _doorTraversalPushThroughPosition;
        private bool _doorTraversalPostThresholdCommitted;
        private TransitionSweepSession _transitionSweepSession;
        private LiveRouteAuditSession _liveRouteAuditSession;
        private bool _isRoomObjectPickerOpen;
        private bool _isNavigationActive;
        private bool _isAutoWalking;

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

        internal static void RequestExportNavMesh()
        {
            Interlocked.Exchange(ref _exportNavMeshRequested, 1);
        }

        internal static void RequestToggleTransitionSweep()
        {
            Interlocked.Exchange(ref _transitionSweepRequested, 1);
        }

        internal static void RequestToggleDoorTransitionSweep()
        {
            Interlocked.Exchange(ref _doorTransitionSweepRequested, 1);
        }

        internal static void RequestToggleLiveRouteAudit()
        {
            Interlocked.Exchange(ref _liveRouteAuditRequested, 1);
        }

        internal static void RequestDateADexEntryAnnouncement()
        {
            Interlocked.Exchange(ref _pendingDateADexEntryAnnouncementRequested, 1);
            _pendingDateADexEntryAnnouncementNotBefore = Time.unscaledTime + 0.05f;
            _pendingDateADexEntryAnnouncementExpiresAt = Time.unscaledTime + 1.5f;
            _suppressDateADexOpenEntrySelectionUntil = Time.unscaledTime + DateADexOpenEntryInitialSuppressionSeconds;
        }

        private void Update()
        {
            if (Main.IsShuttingDown)
                return;

            HandleRepeatLastSpeechRequest();
            HandleNavigationRequests();
            HandleNavMeshExportRequest();
            HandleTransitionSweepRequest();
            HandleDoorTransitionSweepRequest();
            HandleLiveRouteAuditRequest();
            HandleTransitionSweep();
            HandleLiveRouteAudit();

            bool isSettingsMenuOpen = ModConfig.IsMenuOpen;
            if (isSettingsMenuOpen)
            {
                ModConfig.Update();
            }
            else if (_isRoomObjectPickerOpen)
            {
                UpdateRoomObjectPicker();
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
            if (!isSettingsMenuOpen && !_isRoomObjectPickerOpen)
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
                return;
            }

            if (!TryBuildDateADexDetailAnnouncement(out string announcement) || string.IsNullOrEmpty(announcement))
                return;

            Interlocked.Exchange(ref _pendingDateADexEntryAnnouncementRequested, 0);
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
                if (_liveRouteAuditSession != null)
                    StopLiveRouteAudit(announceStopped: false);
                DescribeCurrentRoom();
            }

            if (Interlocked.Exchange(ref _selectNavigationTargetRequested, 0) != 0)
            {
                if (_liveRouteAuditSession != null)
                    StopLiveRouteAudit(announceStopped: false);
                CycleNavigationTarget();
            }

            if (Interlocked.Exchange(ref _navigateToObjectiveRequested, 0) != 0)
            {
                if (_liveRouteAuditSession != null)
                    StopLiveRouteAudit(announceStopped: false);
                StartNavigationToCurrentTarget();
            }

            if (Interlocked.Exchange(ref _autoWalkRequested, 0) != 0)
            {
                if (_liveRouteAuditSession != null)
                    StopLiveRouteAudit(announceStopped: false);
                ToggleAutoWalk();
            }
        }

        private void HandleNavMeshExportRequest()
        {
            if (Interlocked.Exchange(ref _exportNavMeshRequested, 0) == 0)
                return;

            Loc.RefreshLanguage();

            if (NavMeshExporter.TryExport(
                out string outputPath,
                out int triangleCount,
                out int transitionCount,
                out bool hasActiveNavMesh,
                out NavMeshExporter.ExportFailure failure))
            {
                Main.Log.LogInfo("Navmesh export completed: " + outputPath + " hasActiveNavMesh=" + hasActiveNavMesh);
                if (TryExportSelectedObjectCoverageReport(
                    out string selectedCoverageOutputPath,
                    out string selectedCoverageStatus,
                    out string selectedCoverageFailureReason))
                {
                    Main.Log.LogInfo(
                        "Selected object coverage export completed: " + selectedCoverageOutputPath +
                        " status=" + (selectedCoverageStatus ?? "<null>"));
                }
                else
                {
                    Main.Log.LogWarning(
                        "Selected object coverage export failed: " +
                        (selectedCoverageFailureReason ?? "<null>"));
                }

                ScreenReader.Say(
                    hasActiveNavMesh
                        ? Loc.Get("navmesh_export_success", triangleCount, transitionCount)
                        : Loc.Get("navmesh_export_diagnostic", transitionCount),
                    remember: false);
                return;
            }

            string failureKey = "navmesh_export_failed";
            switch (failure)
            {
                case NavMeshExporter.ExportFailure.NoNavMesh:
                    failureKey = "navmesh_export_no_navmesh";
                    break;
                case NavMeshExporter.ExportFailure.WriteFailed:
                    failureKey = "navmesh_export_write_failed";
                    break;
            }

            Main.Log.LogWarning("Navmesh export failed: " + failure);
            ScreenReader.Say(Loc.Get(failureKey), remember: false);
        }

        private bool TryExportSelectedObjectCoverageReport(
            out string outputPath,
            out string overallStatus,
            out string failureReason)
        {
            SelectedObjectCoverageReporter.ReportData report = BuildSelectedObjectCoverageReport();
            overallStatus = report != null ? report.OverallStatus : null;
            Main.Log.LogInfo(
                "Selected object coverage export runtime build=" + Main.GetRuntimeBuildStamp() +
                " overrides=" + GetOpenPassageTransitionOverrideDiagnosticSummary());
            return SelectedObjectCoverageReporter.TryWriteReport(report, out outputPath, out failureReason);
        }

        private SelectedObjectCoverageReporter.ReportData BuildSelectedObjectCoverageReport()
        {
            GetOpenPassageTransitionOverrideDiagnostics(
                out string overrideStatus,
                out string overrideDetail,
                out int overrideEntryCount,
                out bool overrideNormalizedScalarArrays,
                out string overridePath,
                out string overrideFileSha256,
                out string overrideFileLastWriteUtc);

            var report = new SelectedObjectCoverageReporter.ReportData
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
                ActiveScene = SceneManager.GetActiveScene().name,
                PluginVersion = Main.GetPluginVersion(),
                RuntimeBuildStamp = Main.GetRuntimeBuildStamp(),
                RuntimeAssemblyPath = Main.RuntimeAssemblyPath,
                RuntimeAssemblySha256 = Main.RuntimeAssemblySha256,
                OpenPassageOverrideStatus = overrideStatus,
                OpenPassageOverrideDetail = overrideDetail,
                OpenPassageOverrideEntryCount = overrideEntryCount,
                OpenPassageOverrideNormalizedScalarArrays = overrideNormalizedScalarArrays,
                OpenPassageOverridePath = overridePath,
                OpenPassageOverrideFileSha256 = overrideFileSha256,
                OpenPassageOverrideFileLastWriteUtc = overrideFileLastWriteUtc,
                Limitations = BuildSelectedObjectCoverageLimitations(),
                Summary = new SelectedObjectCoverageReporter.SummaryData(),
                TrackerAlignment = BuildSelectedObjectCoverageTrackerAlignment(),
                Entries = Array.Empty<SelectedObjectCoverageReporter.EntryData>()
            };

            if (!TryResolveSelectedObjectCoverageTarget(
                    out InteractableObj selectedInteractable,
                    out string runtimeZone,
                    out string navigationZone,
                    out string selectedLabel,
                    out string resolutionDetail))
            {
                report.OverallStatus = "failed";
                report.FailureReason = "No selected interactable target was available.";
                report.SelectedObject = new SelectedObjectCoverageReporter.SelectedObjectData
                {
                    ResolutionStatus = "failed",
                    ResolutionDetail = resolutionDetail
                };
                return report;
            }

            Vector3 evaluationStartPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : selectedInteractable.transform.position;
            bool resolvedCurrentApproach = TryResolveSelectedObjectCoverageApproachTarget(
                selectedInteractable,
                navigationZone,
                evaluationStartPosition,
                out Vector3 currentApproachTarget,
                out string currentApproachMode,
                out string currentApproachReferenceSource,
                out string currentApproachDetail,
                out bool usesRawApproachFallback);

            string currentApproachSnapStatus = "failed";
            string currentApproachSnapDetail = "Approach target used raw object fallback.";
            if (!usesRawApproachFallback && !string.IsNullOrWhiteSpace(navigationZone))
            {
                if (LocalNavigationMaps.TrySnapPositionToNearestWalkableCell(
                        navigationZone,
                        currentApproachTarget,
                        out _,
                        out string snapDetail))
                {
                    currentApproachSnapStatus = string.Equals(currentApproachMode, "distance-selected", StringComparison.OrdinalIgnoreCase)
                        ? "unverified"
                        : "passed";
                    currentApproachSnapDetail = snapDetail ?? "SnappedToWalkableCell";
                }
                else
                {
                    currentApproachSnapStatus = ClassifyCoverageLocalFailureStatus(snapDetail);
                    currentApproachSnapDetail = snapDetail;
                }
            }

            report.SelectedObject = new SelectedObjectCoverageReporter.SelectedObjectData
            {
                InteractableId = selectedInteractable.Id,
                InternalName = selectedInteractable.gameObject != null ? selectedInteractable.gameObject.name : selectedInteractable.name,
                DisplayLabel = selectedLabel,
                RuntimeZone = runtimeZone,
                NavigationZone = navigationZone,
                ResolutionStatus = string.IsNullOrWhiteSpace(navigationZone) ? "failed" : "passed",
                ResolutionDetail = resolutionDetail,
                ApproachMode = resolvedCurrentApproach ? currentApproachMode : "raw-object",
                ApproachReferenceSource = currentApproachReferenceSource,
                ApproachTarget = currentApproachTarget,
                ApproachSnapStatus = currentApproachSnapStatus,
                ApproachSnapDetail = currentApproachSnapDetail
            };

            if (string.IsNullOrWhiteSpace(navigationZone))
            {
                report.OverallStatus = "failed";
                report.FailureReason = "Selected interactable did not resolve to a navigation graph zone.";
                return report;
            }

            List<string> graphZones = NavigationGraph.GetAllZones();
            if (graphZones == null || graphZones.Count == 0)
            {
                report.OverallStatus = "failed";
                report.FailureReason = "Navigation graph did not expose any zones.";
                return report;
            }

            Dictionary<string, TransitionSweepReporter.EntryStatus> transitionStatuses = LoadSelectedObjectCoverageTransitionStatuses();
            var entries = new List<SelectedObjectCoverageReporter.EntryData>();
            var zoneStatuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int passedComponentCount = 0;
            int failedComponentCount = 0;
            int unverifiedComponentCount = 0;

            for (int i = 0; i < graphZones.Count; i++)
            {
                string startZone = graphZones[i];
                List<LocalNavigationMaps.WalkableComponentSummary> components = LocalNavigationMaps.GetWalkableComponents(startZone);
                if (components == null || components.Count == 0)
                {
                    var entry = new SelectedObjectCoverageReporter.EntryData
                    {
                        StartZone = startZone,
                        StartComponentId = -1,
                        RepresentativeStart = Vector3.zero,
                        Status = "unverified",
                        FailureReason = "No cached walkable-component evidence was available for the start zone.",
                        StartLocalLegStatus = "unverified",
                        StartLocalLegDetail = "Walkable components unavailable.",
                        DestinationLocalLegStatus = "unverified",
                        DestinationLocalLegDetail = "Walkable components unavailable.",
                        PathSteps = Array.Empty<SelectedObjectCoverageReporter.PathStepData>()
                    };
                    entries.Add(entry);
                    zoneStatuses[startZone] = MergeCoverageStatus(
                        zoneStatuses.TryGetValue(startZone, out string existingStatus) ? existingStatus : null,
                        entry.Status);
                    unverifiedComponentCount++;
                    continue;
                }

                for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
                {
                    SelectedObjectCoverageReporter.EntryData entry = BuildSelectedObjectCoverageEntry(
                        startZone,
                        components[componentIndex],
                        selectedInteractable,
                        navigationZone,
                        transitionStatuses);
                    entries.Add(entry);
                    zoneStatuses[startZone] = MergeCoverageStatus(
                        zoneStatuses.TryGetValue(startZone, out string existingStatus) ? existingStatus : null,
                        entry.Status);

                    if (string.Equals(entry.Status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        failedComponentCount++;
                    }
                    else if (string.Equals(entry.Status, "unverified", StringComparison.OrdinalIgnoreCase))
                    {
                        unverifiedComponentCount++;
                    }
                    else
                    {
                        passedComponentCount++;
                    }
                }
            }

            int passedStartZoneCount = 0;
            int failedStartZoneCount = 0;
            int unverifiedStartZoneCount = 0;
            foreach (KeyValuePair<string, string> pair in zoneStatuses)
            {
                if (string.Equals(pair.Value, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    failedStartZoneCount++;
                }
                else if (string.Equals(pair.Value, "unverified", StringComparison.OrdinalIgnoreCase))
                {
                    unverifiedStartZoneCount++;
                }
                else
                {
                    passedStartZoneCount++;
                }
            }

            report.Summary = new SelectedObjectCoverageReporter.SummaryData
            {
                StartZoneCount = graphZones.Count,
                PassedStartZoneCount = passedStartZoneCount,
                FailedStartZoneCount = failedStartZoneCount,
                UnverifiedStartZoneCount = unverifiedStartZoneCount,
                PassedComponentCount = passedComponentCount,
                FailedComponentCount = failedComponentCount,
                UnverifiedComponentCount = unverifiedComponentCount
            };
            report.Entries = entries.ToArray();
            report.OverallStatus = failedComponentCount > 0
                ? "failed"
                : unverifiedComponentCount > 0
                    ? "unverified"
                    : "passed";
            report.FailureReason = string.Equals(report.OverallStatus, "passed", StringComparison.OrdinalIgnoreCase)
                ? null
                : string.Equals(report.OverallStatus, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "One or more start-zone components failed route, transition, or arrival proof."
                    : "At least one start-zone component remains unverified due to missing sweep or local-map evidence.";
            return report;
        }

        private bool TryResolveSelectedObjectCoverageTarget(
            out InteractableObj interactable,
            out string runtimeZone,
            out string navigationZone,
            out string selectedLabel,
            out string resolutionDetail)
        {
            interactable = null;
            runtimeZone = null;
            navigationZone = null;
            selectedLabel = null;
            resolutionDetail = "No selected interactable target was available.";

            if (TryGetTrackedInteractable(out interactable))
            {
                selectedLabel = GetTrackedInteractableLabel(interactable);
                if (TryGetTrackedInteractableZone(interactable, out runtimeZone))
                {
                    navigationZone = GetNavigationZoneName(runtimeZone);
                    resolutionDetail = "tracked interactable";
                    return true;
                }

                runtimeZone = _trackedInteractableZone;
                navigationZone = GetNavigationZoneName(runtimeZone);
                resolutionDetail = "tracked interactable zone unresolved";
                return interactable != null;
            }

            if (TryResolveCurrentObjectiveInteractable(out interactable, out runtimeZone, out selectedLabel))
            {
                navigationZone = GetNavigationZoneName(runtimeZone);
                resolutionDetail = "objective interactable";
                return true;
            }

            return false;
        }

        private SelectedObjectCoverageReporter.EntryData BuildSelectedObjectCoverageEntry(
            string startZone,
            LocalNavigationMaps.WalkableComponentSummary component,
            InteractableObj selectedInteractable,
            string targetNavigationZone,
            Dictionary<string, TransitionSweepReporter.EntryStatus> transitionStatuses)
        {
            Vector3 representativeStart = component != null
                ? component.RepresentativeWorldPosition
                : Vector3.zero;
            var entry = new SelectedObjectCoverageReporter.EntryData
            {
                StartZone = startZone,
                StartComponentId = component != null ? component.ComponentId : -1,
                RepresentativeStart = representativeStart,
                PathSteps = Array.Empty<SelectedObjectCoverageReporter.PathStepData>()
            };

            if (component == null)
            {
                entry.Status = "unverified";
                entry.FailureReason = "Walkable component summary was missing.";
                entry.StartLocalLegStatus = "unverified";
                entry.StartLocalLegDetail = "Walkable component summary was missing.";
                entry.DestinationLocalLegStatus = "unverified";
                entry.DestinationLocalLegDetail = "Walkable component summary was missing.";
                return entry;
            }

            bool approachResolved = TryResolveSelectedObjectCoverageApproachTargetForStartComponent(
                startZone,
                component,
                selectedInteractable,
                targetNavigationZone,
                representativeStart,
                out Vector3 approachTarget,
                out string approachMode,
                out string approachReferenceSource,
                out string approachDetail,
                out bool usesRawApproachFallback);
            entry.ResolvedApproachMode = approachMode;
            entry.ResolvedApproachTarget = approachTarget;
            entry.ResolvedApproachDetail =
                (approachDetail ?? "<null>") +
                " referenceSource=" + (approachReferenceSource ?? "<null>");

            if (!approachResolved || usesRawApproachFallback)
            {
                entry.Status = "failed";
                entry.FailureReason = "Selected object approach target fell back to the raw object position.";
                entry.StartLocalLegStatus = "failed";
                entry.StartLocalLegDetail = "Approach target resolution failed.";
                entry.DestinationLocalLegStatus = "failed";
                entry.DestinationLocalLegDetail = "Approach target resolution failed.";
                return entry;
            }

            bool hasHeuristicApproach = string.Equals(approachMode, "distance-selected", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(startZone, targetNavigationZone, StringComparison.OrdinalIgnoreCase))
            {
                EvaluateSelectedObjectCoverageLocalLeg(
                    startZone,
                    representativeStart,
                    approachTarget,
                    out string localStatus,
                    out string localDetail);
                entry.StartLocalLegStatus = localStatus;
                entry.StartLocalLegDetail = localDetail;
                entry.DestinationLocalLegStatus = localStatus;
                entry.DestinationLocalLegDetail = "Same-zone navigation proof.";

                entry.Status = DetermineSelectedObjectCoverageStatus(
                    hasFailedTransition: false,
                    hasUnverifiedTransition: false,
                    startLocalLegStatus: localStatus,
                    destinationLocalLegStatus: localStatus,
                    hasHeuristicApproach: hasHeuristicApproach);
                entry.FailureReason = GetSelectedObjectCoverageFailureReason(entry);
                return entry;
            }

            List<NavigationGraph.PathStep> pathSteps = NavigationGraph.FindPathSteps(
                startZone,
                targetNavigationZone,
                representativeStart,
                approachTarget);
            if (pathSteps == null)
            {
                entry.Status = "failed";
                entry.FailureReason = "No graph route connected the start zone to the target zone.";
                entry.StartLocalLegStatus = "failed";
                entry.StartLocalLegDetail = "Graph path resolution failed.";
                entry.DestinationLocalLegStatus = "failed";
                entry.DestinationLocalLegDetail = "Graph path resolution failed.";
                return entry;
            }

            entry.PathSteps = BuildSelectedObjectCoveragePathSteps(
                pathSteps,
                transitionStatuses,
                out bool hasFailedTransition,
                out bool hasUnverifiedTransition);

            Vector3 sourceAnchor = GetSelectedObjectCoverageSourceAnchor(pathSteps.Count > 0 ? pathSteps[0] : null);
            Vector3 destinationAnchor = GetSelectedObjectCoverageDestinationAnchor(pathSteps.Count > 0 ? pathSteps[pathSteps.Count - 1] : null);
            EvaluateSelectedObjectCoverageLocalLeg(
                startZone,
                representativeStart,
                sourceAnchor,
                out string startLocalLegStatus,
                out string startLocalLegDetail);
            EvaluateSelectedObjectCoverageLocalLeg(
                targetNavigationZone,
                destinationAnchor,
                approachTarget,
                out string destinationLocalLegStatus,
                out string destinationLocalLegDetail);

            entry.StartLocalLegStatus = startLocalLegStatus;
            entry.StartLocalLegDetail = startLocalLegDetail;
            entry.DestinationLocalLegStatus = destinationLocalLegStatus;
            entry.DestinationLocalLegDetail = destinationLocalLegDetail;
            entry.Status = DetermineSelectedObjectCoverageStatus(
                hasFailedTransition,
                hasUnverifiedTransition,
                startLocalLegStatus,
                destinationLocalLegStatus,
                hasHeuristicApproach);
            entry.FailureReason = GetSelectedObjectCoverageFailureReason(entry);
            return entry;
        }

        private bool TryResolveSelectedObjectCoverageApproachTarget(
            InteractableObj interactable,
            string navigationZone,
            Vector3 evaluationStartPosition,
            out Vector3 targetPosition,
            out string approachMode,
            out string referenceSource,
            out string detail,
            out bool usesRawApproachFallback)
        {
            targetPosition = interactable != null ? interactable.transform.position : Vector3.zero;
            targetPosition.y = evaluationStartPosition.y;
            approachMode = "raw-object";
            referenceSource = null;
            detail = "mode=raw-object";
            usesRawApproachFallback = true;

            if (interactable == null)
            {
                detail = "InteractableMissing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(navigationZone))
            {
                detail = "mode=raw-object zone=<null>";
                return false;
            }

            if (!TryBuildTrackedInteractableApproachCandidates(
                    interactable,
                    evaluationStartPosition,
                    out List<Vector3> candidateTargets,
                    out Vector3 referencePosition,
                    out string candidateDetail) ||
                candidateTargets == null ||
                candidateTargets.Count < 1)
            {
                referenceSource = candidateDetail;
                detail = "mode=raw-object candidates=0";
                return false;
            }

            referenceSource = candidateDetail;
            if (!TryResolveTrackedInteractableApproachTarget(
                    navigationZone,
                    evaluationStartPosition,
                    referencePosition,
                    candidateTargets,
                    out targetPosition,
                    out string resolutionDetail))
            {
                targetPosition = interactable.transform.position;
                targetPosition.y = evaluationStartPosition.y;
                detail = "mode=raw-object resolution=failed";
                return false;
            }

            targetPosition.y = evaluationStartPosition.y;
            detail = resolutionDetail;
            approachMode = ExtractSelectedObjectCoverageMode(resolutionDetail);
            usesRawApproachFallback = false;
            return true;
        }

        private SelectedObjectCoverageReporter.PathStepData[] BuildSelectedObjectCoveragePathSteps(
            List<NavigationGraph.PathStep> pathSteps,
            Dictionary<string, TransitionSweepReporter.EntryStatus> transitionStatuses,
            out bool hasFailedTransition,
            out bool hasUnverifiedTransition)
        {
            hasFailedTransition = false;
            hasUnverifiedTransition = false;
            if (pathSteps == null || pathSteps.Count == 0)
                return Array.Empty<SelectedObjectCoverageReporter.PathStepData>();

            var stepData = new SelectedObjectCoverageReporter.PathStepData[pathSteps.Count];
            for (int i = 0; i < pathSteps.Count; i++)
            {
                NavigationGraph.PathStep step = pathSteps[i];
                string stepKey = BuildNavigationStepKey(step);
                string validationStatus = "unverified";
                string validationDetail = "No transition sweep evidence matched this step.";
                if (transitionStatuses != null &&
                    !string.IsNullOrWhiteSpace(stepKey) &&
                    transitionStatuses.TryGetValue(stepKey, out TransitionSweepReporter.EntryStatus status))
                {
                    validationStatus = NormalizeSelectedObjectCoverageStatus(status.Status);
                    validationDetail = !string.IsNullOrWhiteSpace(status.StatusDetail)
                        ? status.StatusDetail
                        : status.FailureReason;
                }

                if (string.Equals(validationStatus, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    hasFailedTransition = true;
                }
                else if (!string.Equals(validationStatus, "passed", StringComparison.OrdinalIgnoreCase))
                {
                    hasUnverifiedTransition = true;
                }

                stepData[i] = new SelectedObjectCoverageReporter.PathStepData
                {
                    Key = stepKey,
                    Id = step != null ? step.Id : null,
                    FromZone = step != null ? step.FromZone : null,
                    ToZone = step != null ? step.ToZone : null,
                    StepKind = step != null ? step.Kind.ToString() : null,
                    ConnectorName = step != null ? step.ConnectorName : null,
                    ValidationStatus = validationStatus,
                    ValidationDetail = validationDetail,
                    RequiresInteraction = step != null && step.RequiresInteraction
                };
            }

            return stepData;
        }

        private static Vector3 GetSelectedObjectCoverageSourceAnchor(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (step.SourceApproachPoint.sqrMagnitude > 0.0001f)
                return step.SourceApproachPoint;

            if (step.FromCrossingAnchor.sqrMagnitude > 0.0001f)
                return step.FromCrossingAnchor;

            if (step.FromWaypoint.sqrMagnitude > 0.0001f)
                return step.FromWaypoint;

            return step.ConnectorObjectPosition;
        }

        private static Vector3 GetSelectedObjectCoverageDestinationAnchor(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (step.DestinationApproachPoint.sqrMagnitude > 0.0001f)
                return step.DestinationApproachPoint;

            if (step.DestinationClearPoint.sqrMagnitude > 0.0001f)
                return step.DestinationClearPoint;

            if (step.ToCrossingAnchor.sqrMagnitude > 0.0001f)
                return step.ToCrossingAnchor;

            if (step.ToWaypoint.sqrMagnitude > 0.0001f)
                return step.ToWaypoint;

            return step.ConnectorObjectPosition;
        }

        private void EvaluateSelectedObjectCoverageLocalLeg(
            string zoneName,
            Vector3 startPosition,
            Vector3 targetPosition,
            out string status,
            out string detail)
        {
            if (LocalNavigationMaps.TryFindPath(
                    zoneName,
                    startPosition,
                    targetPosition,
                    out List<Vector3> pathPoints,
                    out string failureReason))
            {
                status = "passed";
                detail =
                    "pathPoints=" + (pathPoints != null ? pathPoints.Count : 0) +
                    " pathDistance=" + ComputeFlatPathDistance(startPosition, pathPoints).ToString("0.00", CultureInfo.InvariantCulture);
                return;
            }

            status = ClassifyCoverageLocalFailureStatus(failureReason);
            detail = failureReason;
        }

        private Dictionary<string, TransitionSweepReporter.EntryStatus> LoadSelectedObjectCoverageTransitionStatuses()
        {
            var mergedStatuses = new Dictionary<string, TransitionSweepReporter.EntryStatus>(StringComparer.OrdinalIgnoreCase);
            MergeSelectedObjectCoverageTransitionStatuses(
                mergedStatuses,
                TransitionSweepReporter.LoadEntryStatuses(TransitionSweepReporter.GetDefaultOutputPath()));
            MergeSelectedObjectCoverageTransitionStatuses(
                mergedStatuses,
                TransitionSweepReporter.LoadEntryStatuses(TransitionSweepReporter.GetDefaultDoorOutputPath()));
            MergeSelectedObjectCoveragePassedKeys(
                mergedStatuses,
                TransitionSweepReporter.LoadPassedKeys(TransitionSweepReporter.GetDefaultOutputPath()),
                NavigationGraph.StepKind.OpenPassage.ToString(),
                "Passed on the current runtime build according to the open-passage sweep passed-key cache.");
            MergeSelectedObjectCoveragePassedKeys(
                mergedStatuses,
                TransitionSweepReporter.LoadPassedKeys(TransitionSweepReporter.GetDefaultDoorOutputPath()),
                NavigationGraph.StepKind.Door.ToString(),
                "Passed on the current runtime build according to the door sweep passed-key cache.");
            ApplyExcludedTransitionSweepStatuses(mergedStatuses);
            return mergedStatuses;
        }

        private static void MergeSelectedObjectCoverageTransitionStatuses(
            Dictionary<string, TransitionSweepReporter.EntryStatus> destination,
            Dictionary<string, TransitionSweepReporter.EntryStatus> source)
        {
            if (destination == null || source == null || source.Count == 0)
                return;

            foreach (KeyValuePair<string, TransitionSweepReporter.EntryStatus> pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                    continue;

                destination[pair.Key] = pair.Value;
            }
        }

        private static void MergeSelectedObjectCoveragePassedKeys(
            Dictionary<string, TransitionSweepReporter.EntryStatus> destination,
            HashSet<string> passedKeys,
            string defaultStepKind,
            string statusDetail)
        {
            if (destination == null || passedKeys == null || passedKeys.Count == 0)
                return;

            foreach (string key in passedKeys)
            {
                if (string.IsNullOrWhiteSpace(key) || destination.ContainsKey(key))
                    continue;

                destination[key] = new TransitionSweepReporter.EntryStatus
                {
                    Key = key,
                    StepKind = defaultStepKind,
                    Status = "passed",
                    StatusDetail = statusDetail,
                    FailureReason = null
                };
            }
        }

        private static void ApplyExcludedTransitionSweepStatuses(
            Dictionary<string, TransitionSweepReporter.EntryStatus> statuses)
        {
            if (statuses == null)
                return;

            string[] excludedStepKeys =
            {
                "transition:crawlspace->office",
                "transition:office->crawlspace"
            };

            for (int i = 0; i < excludedStepKeys.Length; i++)
            {
                string stepKey = excludedStepKeys[i];
                statuses[stepKey] = new TransitionSweepReporter.EntryStatus
                {
                    Key = stepKey,
                    StepKind = NavigationGraph.StepKind.Teleporter.ToString(),
                    Status = "passed",
                    StatusDetail = "Excluded from the automated transition sweep after manual ladder-cutscene verification on 2026-04-10.",
                    FailureReason = null
                };
            }
        }

        private SelectedObjectCoverageReporter.TrackerAlignmentData BuildSelectedObjectCoverageTrackerAlignment()
        {
            Vector3 trackerTarget = Vector3.zero;
            bool trackerActive = ObjectTracker.TryGetCurrentTargetState(out trackerTarget, out _);
            Vector3 movementTarget = _hasLastTrackerTarget ? _lastTrackerTargetPosition : Vector3.zero;
            return new SelectedObjectCoverageReporter.TrackerAlignmentData
            {
                NavigationActive = _isNavigationActive,
                TrackerActive = trackerActive,
                CurrentStepKey = _lastTrackerTargetStepKey,
                TargetKind = _lastTrackerTargetKind,
                MovementTarget = movementTarget,
                TrackerTarget = trackerTarget,
                TargetDelta = trackerActive && _hasLastTrackerTarget
                    ? Vector3.Distance(movementTarget, trackerTarget)
                    : 0f,
                LocalNavigationContext = _localNavigationPathContext
            };
        }

        private string[] BuildSelectedObjectCoverageLimitations()
        {
            var limitations = new List<string>
            {
                "Transition validation uses the saved live sweep reports; any step without matching evidence stays unverified."
            };

            if (LocalNavigationMaps.IsAvailable)
            {
                limitations.Add(
                    "Local navigation maps currently rasterize CameraSpaces envelopes with primitive blockers only; mesh and terrain blockers are not yet included.");
            }
            else
            {
                limitations.Add(
                    "Local navigation maps were unavailable at export time, so from-anywhere proof remains incomplete.");
            }

            return limitations.ToArray();
        }

        private static string DetermineSelectedObjectCoverageStatus(
            bool hasFailedTransition,
            bool hasUnverifiedTransition,
            string startLocalLegStatus,
            string destinationLocalLegStatus,
            bool hasHeuristicApproach)
        {
            if (hasFailedTransition ||
                string.Equals(startLocalLegStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(destinationLocalLegStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return "failed";
            }

            if (hasUnverifiedTransition ||
                string.Equals(startLocalLegStatus, "unverified", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(destinationLocalLegStatus, "unverified", StringComparison.OrdinalIgnoreCase) ||
                hasHeuristicApproach)
            {
                return "unverified";
            }

            return "passed";
        }

        private static string GetSelectedObjectCoverageFailureReason(SelectedObjectCoverageReporter.EntryData entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Status) ||
                string.Equals(entry.Status, "passed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (entry.PathSteps != null)
            {
                for (int i = 0; i < entry.PathSteps.Length; i++)
                {
                    SelectedObjectCoverageReporter.PathStepData step = entry.PathSteps[i];
                    if (step == null || !string.Equals(step.ValidationStatus, "failed", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return "Transition validation failed for step " + (step.Key ?? "<null>") + ".";
                }
            }

            if (string.Equals(entry.StartLocalLegStatus, "failed", StringComparison.OrdinalIgnoreCase))
                return entry.StartLocalLegDetail;

            if (string.Equals(entry.DestinationLocalLegStatus, "failed", StringComparison.OrdinalIgnoreCase))
                return entry.DestinationLocalLegDetail;

            if (string.Equals(entry.ResolvedApproachMode, "distance-selected", StringComparison.OrdinalIgnoreCase))
                return "Approach target is still heuristic and needs authoritative arrival data.";

            return entry.FailureReason;
        }

        private static string MergeCoverageStatus(string leftStatus, string rightStatus)
        {
            if (string.Equals(leftStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rightStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return "failed";
            }

            if (string.Equals(leftStatus, "unverified", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rightStatus, "unverified", StringComparison.OrdinalIgnoreCase))
            {
                return "unverified";
            }

            if (string.Equals(leftStatus, "passed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rightStatus, "passed", StringComparison.OrdinalIgnoreCase))
            {
                return "passed";
            }

            return rightStatus ?? leftStatus ?? "unverified";
        }

        private static string ClassifyCoverageLocalFailureStatus(string failureReason)
        {
            if (string.IsNullOrWhiteSpace(failureReason))
                return "unverified";

            if (failureReason.IndexOf("LocalNavigationMapsUnavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                failureReason.IndexOf("ZoneNotFound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                failureReason.IndexOf("ZoneHasNoWalkableCells", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "unverified";
            }

            return "failed";
        }

        private static string NormalizeSelectedObjectCoverageStatus(string status)
        {
            if (string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase))
                return "passed";

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                return "failed";

            return "unverified";
        }

        private static string ExtractSelectedObjectCoverageMode(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return "resolved";

            const string token = "mode=";
            int modeIndex = detail.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (modeIndex < 0)
                return "resolved";

            int valueStartIndex = modeIndex + token.Length;
            int valueEndIndex = detail.IndexOf(' ', valueStartIndex);
            if (valueEndIndex < 0)
                valueEndIndex = detail.Length;

            return detail.Substring(valueStartIndex, valueEndIndex - valueStartIndex);
        }

        private void HandleTransitionSweepRequest()
        {
            if (Interlocked.Exchange(ref _transitionSweepRequested, 0) == 0)
                return;

            Loc.RefreshLanguage();

            if (_liveRouteAuditSession != null)
                StopLiveRouteAudit(announceStopped: false);

            if (_transitionSweepSession != null)
            {
                StopTransitionSweep(announceStopped: true);
                return;
            }

            if (!TryStartTransitionSweep(TransitionSweepKind.OpenPassage))
                ScreenReader.Say(Loc.Get("transition_sweep_unavailable"), remember: false);
        }

        private void HandleDoorTransitionSweepRequest()
        {
            if (Interlocked.Exchange(ref _doorTransitionSweepRequested, 0) == 0)
                return;

            Loc.RefreshLanguage();

            if (_liveRouteAuditSession != null)
                StopLiveRouteAudit(announceStopped: false);

            if (_transitionSweepSession != null)
            {
                StopTransitionSweep(announceStopped: true);
                return;
            }

            if (!TryStartTransitionSweep(TransitionSweepKind.Door))
                ScreenReader.Say(Loc.Get("door_transition_sweep_unavailable"), remember: false);
        }

        private bool TryStartTransitionSweep(TransitionSweepKind sweepKind)
        {
            if (!CanUseNavigationNow() || BetterPlayerControl.Instance == null)
            {
                Main.Log.LogWarning(
                    GetTransitionSweepLogLabel(sweepKind) + " start rejected: " + GetNavigationUnavailableReason());
                return false;
            }

            string outputPath = GetTransitionSweepOutputPath(sweepKind);
            HashSet<string> previouslyPassedKeys = TransitionSweepReporter.LoadPassedKeys(outputPath);
            List<NavigationGraph.PathStep> steps = BuildTransitionSweepSteps(previouslyPassedKeys, sweepKind);
            if (steps == null || steps.Count == 0)
            {
                Main.Log.LogWarning(
                    GetTransitionSweepLogLabel(sweepKind) + " start rejected: no navigation steps available.");
                return false;
            }

            var entries = new List<TransitionSweepReporter.MutableEntry>(steps.Count);
            for (int i = 0; i < steps.Count; i++)
            {
                entries.Add(TransitionSweepReporter.CreateEntry(i, steps[i], BuildNavigationStepKey(steps[i])));
            }

            StopNavigationRuntime();
            SetTrackedInteractable(null, null, null);

            _transitionSweepSession = new TransitionSweepSession
            {
                Kind = sweepKind,
                Steps = steps,
                Entries = entries,
                OutputPath = outputPath,
                Phase = TransitionSweepPhase.AwaitingNextStep,
                NextActionTime = Time.unscaledTime + TransitionSweepStepSpacingSeconds
            };

            WriteTransitionSweepReport(isComplete: false);
            Main.Log.LogInfo(
                GetTransitionSweepLogLabel(sweepKind) + " runtime build=" + Main.GetRuntimeBuildStamp() +
                " overrides=" + GetOpenPassageTransitionOverrideDiagnosticSummary());
            Main.Log.LogInfo(
                GetTransitionSweepLogLabel(sweepKind) + " started. steps=" + steps.Count +
                " skippedPreviouslyPassed=" + previouslyPassedKeys.Count);
            ScreenReader.Say(Loc.Get(GetTransitionSweepStartedMessageKey(sweepKind), steps.Count), remember: false);
            return true;
        }

        private void StopTransitionSweep(bool announceStopped)
        {
            if (_transitionSweepSession == null)
                return;

            TransitionSweepKind sweepKind = _transitionSweepSession.Kind;
            Main.Log.LogInfo(GetTransitionSweepLogLabel(sweepKind) + " stopped early at index " + _transitionSweepSession.CurrentIndex);
            StopNavigationRuntime();
            WriteTransitionSweepReport(isComplete: false);
            _transitionSweepSession = null;

            if (announceStopped)
                ScreenReader.Say(Loc.Get(GetTransitionSweepStoppedMessageKey(sweepKind)), remember: false);
        }

        private void HandleLiveRouteAuditRequest()
        {
            if (Interlocked.Exchange(ref _liveRouteAuditRequested, 0) == 0)
                return;

            Loc.RefreshLanguage();

            if (_liveRouteAuditSession != null)
            {
                StopLiveRouteAudit(announceStopped: true);
                return;
            }

            if (_transitionSweepSession != null)
                StopTransitionSweep(announceStopped: false);

            if (!TryStartLiveRouteAudit())
                ScreenReader.Say(Loc.Get("live_route_audit_unavailable"), remember: false);
        }

        private bool TryStartLiveRouteAudit()
        {
            if (!CanUseNavigationNow() || BetterPlayerControl.Instance == null)
            {
                Main.Log.LogWarning("Live route audit start rejected: " + GetNavigationUnavailableReason());
                return false;
            }

            string outputPath = LiveRouteAuditReporter.GetDefaultOutputPath();
            Dictionary<string, LiveRouteAuditReporter.EntryStatus> previousStatuses =
                LiveRouteAuditReporter.LoadEntryStatuses(outputPath);
            List<LiveRouteAuditReporter.MutableEntry> entries = BuildLiveRouteAuditEntries(
                previousStatuses,
                out List<LiveRouteAuditRoute> routes,
                out int orderedPairCount,
                out int uniqueRouteCount);
            if (entries == null || entries.Count == 0)
            {
                Main.Log.LogWarning("Live route audit start rejected: no route entries available.");
                return false;
            }

            StopNavigationRuntime();
            SetTrackedInteractable(null, null, null);

            if (routes == null || routes.Count == 0)
            {
                LiveRouteAuditReporter.WriteReport(
                    outputPath,
                    isComplete: true,
                    orderedPairCount,
                    uniqueRouteCount,
                    entries);
                Main.Log.LogInfo(
                    "Live route audit complete without queued routes orderedPairs=" + orderedPairCount +
                    " uniqueRoutes=" + uniqueRouteCount +
                    " passed=" + CountLiveRouteAuditEntriesByStatus(entries, "passed") +
                    " failed=" + CountLiveRouteAuditEntriesByStatus(entries, "failed"));
                ScreenReader.Say(
                    Loc.Get(
                        "live_route_audit_complete",
                        orderedPairCount,
                        CountLiveRouteAuditEntriesByStatus(entries, "passed"),
                        CountLiveRouteAuditEntriesByStatus(entries, "failed")),
                    remember: false);
                return true;
            }

            _liveRouteAuditSession = new LiveRouteAuditSession
            {
                Routes = routes,
                Entries = entries,
                OutputPath = outputPath,
                OrderedPairCount = orderedPairCount,
                UniqueRouteCount = uniqueRouteCount,
                Phase = LiveRouteAuditPhase.AwaitingNextRoute,
                NextActionTime = Time.unscaledTime + LiveRouteAuditStepSpacingSeconds
            };

            WriteLiveRouteAuditReport(isComplete: false);
            Main.Log.LogInfo(
                "Live route audit runtime build=" + Main.GetRuntimeBuildStamp() +
                " overrides=" + GetOpenPassageTransitionOverrideDiagnosticSummary());
            Main.Log.LogInfo(
                "Live route audit started orderedPairs=" + orderedPairCount +
                " uniqueRoutes=" + uniqueRouteCount +
                " queuedRoutes=" + routes.Count +
                " skippedPreviouslyPassed=" + CountLiveRouteAuditEntriesByStatus(entries, "passed"));
            ScreenReader.Say(Loc.Get("live_route_audit_started", orderedPairCount, routes.Count), remember: false);
            return true;
        }

        private void StopLiveRouteAudit(bool announceStopped)
        {
            if (_liveRouteAuditSession == null)
                return;

            Main.Log.LogInfo("Live route audit stopped early at index " + _liveRouteAuditSession.CurrentIndex);
            StopNavigationRuntime();
            WriteLiveRouteAuditReport(isComplete: false);
            _liveRouteAuditSession = null;

            if (announceStopped)
                ScreenReader.Say(Loc.Get("live_route_audit_stopped"), remember: false);
        }

        private void HandleLiveRouteAudit()
        {
            if (_liveRouteAuditSession == null)
                return;

            if (Time.unscaledTime < _liveRouteAuditSession.NextActionTime)
                return;

            switch (_liveRouteAuditSession.Phase)
            {
                case LiveRouteAuditPhase.AwaitingNextRoute:
                    StartNextLiveRouteAuditRoute();
                    break;
                case LiveRouteAuditPhase.AwaitingTeleportSettle:
                    ContinueLiveRouteAuditAfterTeleport();
                    break;
                case LiveRouteAuditPhase.Running:
                    MonitorRunningLiveRouteAudit();
                    break;
            }
        }

        private void StartNextLiveRouteAuditRoute()
        {
            if (_liveRouteAuditSession == null)
                return;

            if (Main.IsShuttingDown)
            {
                StopLiveRouteAudit(announceStopped: false);
                return;
            }

            int nextIndex = _liveRouteAuditSession.CurrentIndex + 1;
            if (nextIndex >= _liveRouteAuditSession.Routes.Count)
            {
                FinishLiveRouteAudit();
                return;
            }

            LiveRouteAuditRoute route = _liveRouteAuditSession.Routes[nextIndex];
            _liveRouteAuditSession.CurrentIndex = nextIndex;
            _liveRouteAuditSession.CurrentRoute = route;
            _liveRouteAuditSession.Phase = LiveRouteAuditPhase.AwaitingTeleportSettle;
            _liveRouteAuditSession.NextActionTime = Time.unscaledTime + LiveRouteAuditTeleportSettleSeconds;

            StopNavigationRuntime();
            SetTrackedInteractable(null, null, null);
            if (!TryTeleportLiveRouteAuditPlayer(route, out Vector3 spawnPosition, out string spawnSource))
            {
                RecordLiveRouteAuditFailure(
                    "spawn unavailable startZone=" + (route != null ? route.StartZone : "<null>") +
                    " targetZone=" + (route != null ? route.TargetZone : "<null>"));
                return;
            }

            LiveRouteAuditReporter.MutableEntry entry = GetCurrentLiveRouteAuditEntry();
            if (entry != null)
            {
                entry.SpawnPosition = spawnPosition;
                entry.SpawnSource = spawnSource;
                entry.Status = "pending";
                entry.StatusDetail = "spawned";
            }

            Main.Log.LogInfo(
                "Live route audit route staged index=" + nextIndex +
                " key=" + (route != null ? route.Key : "<null>") +
                " startZone=" + (route != null ? route.StartZone : "<null>") +
                " targetZone=" + (route != null ? route.TargetZone : "<null>") +
                " spawnSource=" + (spawnSource ?? "<null>") +
                " spawnPosition=" + FormatVector3(spawnPosition));
            WriteLiveRouteAuditReport(isComplete: false);
        }

        private void ContinueLiveRouteAuditAfterTeleport()
        {
            if (_liveRouteAuditSession == null || _liveRouteAuditSession.CurrentRoute == null)
                return;

            LiveRouteAuditRoute route = _liveRouteAuditSession.CurrentRoute;
            string currentZone = GetCurrentZoneNameInternal();
            if (!IsZoneEquivalentToNavigationZone(currentZone, route.StartZone))
            {
                RecordLiveRouteAuditFailure(
                    "spawn zone mismatch currentZone=" + (currentZone ?? "<null>") +
                    " expectedZone=" + (route.StartZone ?? "<null>"));
                return;
            }

            SetTrackedInteractable(null, route.TargetZone, route.TargetLabel);
            if (!BeginNavigation(route.TargetZone, route.TargetLabel, announceFailure: false))
            {
                RecordLiveRouteAuditFailure(
                    "begin navigation failed currentZone=" + (currentZone ?? "<null>") +
                    " targetZone=" + (route.TargetZone ?? "<null>") +
                    " detail=" + (_lastNavigationBlockedDetail ?? "<null>"));
                return;
            }

            if (!CanUseNavigationNow() || !ApplyNavigationInput(Vector3.zero, Vector3.zero))
            {
                StopNavigationRuntime();
                RecordLiveRouteAuditFailure("auto-walk unavailable reason=" + GetNavigationUnavailableReason());
                return;
            }

            _isAutoWalking = true;
            _lastAutoWalkPosition = BetterPlayerControl.Instance != null ? BetterPlayerControl.Instance.transform.position : Vector3.zero;
            _lastAutoWalkProgressTime = Time.unscaledTime;
            _liveRouteAuditSession.RouteStartedAt = Time.unscaledTime;
            _liveRouteAuditSession.LastHeartbeatAt = 0f;
            _liveRouteAuditSession.Phase = LiveRouteAuditPhase.Running;
            _liveRouteAuditSession.NextActionTime = Time.unscaledTime + 0.1f;
            Main.Log.LogInfo(
                "Live route audit route running key=" + (route.Key ?? "<null>") +
                " signature=" + (route.RouteSignature ?? "<null>") +
                " timeoutSeconds=" + route.TimeoutSeconds.ToString("0.00", CultureInfo.InvariantCulture));
        }

        private void MonitorRunningLiveRouteAudit()
        {
            if (_liveRouteAuditSession == null || _liveRouteAuditSession.CurrentRoute == null)
                return;

            float now = Time.unscaledTime;
            LiveRouteAuditRoute route = _liveRouteAuditSession.CurrentRoute;
            if (!_isNavigationActive)
            {
                RecordLiveRouteAuditFailure(
                    "navigation stopped unexpectedly currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                    " detail=" + (_lastNavigationBlockedDetail ?? "<null>"));
                return;
            }

            if (_liveRouteAuditSession.RouteStartedAt > 0f &&
                now - _liveRouteAuditSession.RouteStartedAt >= route.TimeoutSeconds)
            {
                StopNavigationRuntime();
                RecordLiveRouteAuditFailure(
                    "route timeout elapsed=" + (now - _liveRouteAuditSession.RouteStartedAt).ToString("0.00", CultureInfo.InvariantCulture) +
                    " currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                    " detail=" + (_lastNavigationBlockedDetail ?? "<null>"));
                return;
            }

            if (_liveRouteAuditSession.LastHeartbeatAt <= 0f ||
                now - _liveRouteAuditSession.LastHeartbeatAt >= LiveRouteAuditHeartbeatSeconds)
            {
                _liveRouteAuditSession.LastHeartbeatAt = now;
                Main.Log.LogInfo(
                    "Live route audit heartbeat elapsed=" +
                    (_liveRouteAuditSession.RouteStartedAt > 0f
                        ? (now - _liveRouteAuditSession.RouteStartedAt).ToString("0.00", CultureInfo.InvariantCulture)
                        : "0.00") +
                    " key=" + (route.Key ?? "<null>") +
                    " currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                    " targetZone=" + (_navigationTargetZone ?? "<null>"));
                WriteLiveRouteAuditReport(isComplete: false);
            }

            _liveRouteAuditSession.NextActionTime = now + 0.1f;
        }

        private void FinishLiveRouteAudit()
        {
            if (_liveRouteAuditSession == null)
                return;

            int passedCount = CountLiveRouteAuditEntriesByStatus(_liveRouteAuditSession.Entries, "passed");
            int failedCount = CountLiveRouteAuditEntriesByStatus(_liveRouteAuditSession.Entries, "failed");
            int orderedPairCount = _liveRouteAuditSession.OrderedPairCount;
            WriteLiveRouteAuditReport(isComplete: true);
            Main.Log.LogInfo(
                "Live route audit complete orderedPairs=" + orderedPairCount +
                " uniqueRoutes=" + _liveRouteAuditSession.UniqueRouteCount +
                " passed=" + passedCount +
                " failed=" + failedCount);
            _liveRouteAuditSession = null;
            ScreenReader.Say(Loc.Get("live_route_audit_complete", orderedPairCount, passedCount, failedCount), remember: false);
        }

        private void HandleTransitionSweep()
        {
            if (_transitionSweepSession == null)
                return;

            if (Time.unscaledTime < _transitionSweepSession.NextActionTime)
                return;

            switch (_transitionSweepSession.Phase)
            {
                case TransitionSweepPhase.AwaitingNextStep:
                    StartNextTransitionSweepStep();
                    break;

                case TransitionSweepPhase.AwaitingTeleportSettle:
                    ContinueTransitionSweepAfterTeleport();
                    break;

                case TransitionSweepPhase.AwaitingDoorInteractionSettle:
                    ContinueDoorTransitionSweepAfterInteraction();
                    break;

                case TransitionSweepPhase.Running:
                    MonitorRunningTransitionSweepStep();
                    break;
            }
        }

        private void StartNextTransitionSweepStep()
        {
            if (_transitionSweepSession == null)
                return;

            if (!CanUseNavigationNow() || BetterPlayerControl.Instance == null)
            {
                StopTransitionSweep(announceStopped: true);
                return;
            }

            int nextIndex = _transitionSweepSession.CurrentIndex + 1;
            if (nextIndex >= _transitionSweepSession.Steps.Count)
            {
                FinishTransitionSweep();
                return;
            }

            NavigationGraph.PathStep step = _transitionSweepSession.Steps[nextIndex];
            _transitionSweepSession.CurrentIndex = nextIndex;
            _transitionSweepSession.CurrentStep = step;
            _transitionSweepSession.UsedZoneFallbackSpawn = false;
            _transitionSweepSession.DoorInteractionTriggered = false;
            _transitionSweepSession.DoorPushThroughPosition = Vector3.zero;
            _transitionSweepSession.DoorPostThresholdCommitted = false;
            _transitionSweepSession.DoorTimeoutRecoveryUsed = false;
            _transitionSweepSession.DoorTimeoutFinalGraceUsed = false;
            _transitionSweepSession.Phase = TransitionSweepPhase.AwaitingTeleportSettle;
            _transitionSweepSession.NextActionTime = Time.unscaledTime + TransitionSweepTeleportSettleSeconds;

            StopNavigationRuntime();
            SetTrackedInteractable(null, null, null);

            if (!TryTeleportTransitionSweepPlayer(step, useZoneFallback: false, out Vector3 spawnPosition, out string spawnSource))
            {
                RecordTransitionSweepFailure("spawn unavailable");
                return;
            }

            TransitionSweepReporter.MutableEntry entry = GetCurrentTransitionSweepEntry();
            if (entry != null)
            {
                entry.SpawnPosition = spawnPosition;
                entry.SpawnSource = spawnSource;
            }

            Main.Log.LogInfo(
                "Transition sweep step started index=" + nextIndex +
                " spawnSource=" + spawnSource +
                " spawnPosition=" + FormatVector3(spawnPosition) +
                " step=" + DescribeNavigationStep(step));
            WriteTransitionSweepReport(isComplete: false);
        }

        private void ContinueTransitionSweepAfterTeleport()
        {
            if (_transitionSweepSession == null || _transitionSweepSession.CurrentStep == null)
                return;

            NavigationGraph.PathStep step = _transitionSweepSession.CurrentStep;
            string currentZone = GetCurrentZoneNameForNavigation();
            if (!string.IsNullOrEmpty(currentZone) &&
                IsAcceptableTransitionSweepStartZone(step, currentZone))
            {
                bool started = _transitionSweepSession.Kind == TransitionSweepKind.Door
                    ? TryPrepareDoorTransitionSweepNavigation(step)
                    : TryBeginForcedTransitionSweepNavigation(step);
                if (!started)
                    RecordTransitionSweepFailure("forced navigation start failed");

                return;
            }

            if (!_transitionSweepSession.UsedZoneFallbackSpawn &&
                TryTeleportTransitionSweepPlayer(step, useZoneFallback: true, out Vector3 fallbackPosition, out string fallbackSource))
            {
                _transitionSweepSession.UsedZoneFallbackSpawn = true;
                _transitionSweepSession.NextActionTime = Time.unscaledTime + TransitionSweepTeleportSettleSeconds;

                TransitionSweepReporter.MutableEntry entry = GetCurrentTransitionSweepEntry();
                if (entry != null)
                {
                    entry.SpawnPosition = fallbackPosition;
                    entry.SpawnSource = fallbackSource;
                }

                Main.Log.LogWarning(
                    "Transition sweep source-zone mismatch after first teleport. Retrying with zone fallback currentZone=" +
                    (currentZone ?? "<null>") +
                    " fallbackPosition=" + FormatVector3(fallbackPosition) +
                    " step=" + DescribeNavigationStep(step));
                WriteTransitionSweepReport(isComplete: false);
                return;
            }

            RecordTransitionSweepFailure("spawn zone mismatch currentZone=" + (currentZone ?? "<null>"));
        }

        private bool IsAcceptableTransitionSweepStartZone(NavigationGraph.PathStep step, string currentZone)
        {
            if (step == null || string.IsNullOrEmpty(currentZone))
                return false;

            if (IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                IsAcceptedOverrideSourceZone(step, currentZone))
                return true;

            if (BetterPlayerControl.Instance == null)
                return false;

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            Vector3 referencePosition = GetTransitionSweepSourceReferencePosition(step);
            if (referencePosition == Vector3.zero)
                return false;

            referencePosition.y = playerPosition.y;
            Vector3 flattenedPlayerPosition = playerPosition;
            flattenedPlayerPosition.y = playerPosition.y;
            float sourceDistance = Vector3.Distance(flattenedPlayerPosition, referencePosition);
            if (sourceDistance > TransitionSweepSourceAcceptanceDistance)
                return false;

            Main.Log.LogWarning(
                "Transition sweep accepting sibling source zone currentZone=" + currentZone +
                " expectedZone=" + (step.FromZone ?? "<null>") +
                " sourceDistance=" + sourceDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private Vector3 GetTransitionSweepSourceReferencePosition(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (step.FromWaypoint != Vector3.zero)
                return step.FromWaypoint;

            if (step.FromCrossingAnchor != Vector3.zero)
                return step.FromCrossingAnchor;

            if (TryGetZonePosition(step.FromZone, out Vector3 zonePosition))
                return zonePosition;

            return Vector3.zero;
        }

        private void MonitorRunningTransitionSweepStep()
        {
            if (_transitionSweepSession == null || _transitionSweepSession.CurrentStep == null)
                return;

            NavigationGraph.PathStep currentStep = _transitionSweepSession.CurrentStep;
            float now = Time.unscaledTime;

            if (!_isNavigationActive || !_isAutoWalking)
            {
                RecordTransitionSweepFailure(
                    "forced navigation stopped isNavigationActive=" + _isNavigationActive +
                    " isAutoWalking=" + _isAutoWalking);
                return;
            }

            if (!CanUseNavigationNow())
            {
                RecordTransitionSweepFailure("navigation unavailable " + GetNavigationUnavailableReason());
                return;
            }

            NavigationGraph.PathStep activeStep = GetCurrentNavigationStep();
            if (!string.Equals(BuildNavigationStepKey(activeStep), BuildNavigationStepKey(currentStep), StringComparison.Ordinal))
            {
                RecordTransitionSweepFailure(
                    "active step changed current=" + DescribeNavigationStep(activeStep) +
                    " expected=" + DescribeNavigationStep(currentStep));
                return;
            }

            if (HasForcedTransitionSweepStepSucceeded(currentStep))
            {
                RecordTransitionSweepResult("passed", null);
                return;
            }

            if (_transitionSweepSession.StepStartedAt > 0f &&
                now - _transitionSweepSession.StepStartedAt >= GetOpenPassageTransitionOverrideTimeoutSeconds(currentStep))
            {
                if (TryRecoverTimedOutDoorTransitionSweepStep(currentStep, now))
                    return;

                string currentZone = GetCurrentZoneNameInternal();
                RecordTransitionSweepFailure(
                    "step timeout currentZone=" + (currentZone ?? "<null>") +
                    " player=" + (BetterPlayerControl.Instance != null ? FormatVector3(BetterPlayerControl.Instance.transform.position) : "<null>"));
                return;
            }

            string navigationUnavailableReason = GetNavigationUnavailableReason();
            if (_transitionSweepSession.StepStartedAt > 0f &&
                !CanUseNavigationNow() &&
                IsExpectedTeleporterSweepControlLock(currentStep, navigationUnavailableReason))
            {
                if (_transitionSweepSession.LastHeartbeatAt <= 0f ||
                    now - _transitionSweepSession.LastHeartbeatAt >= TransitionSweepHeartbeatSeconds)
                {
                    _transitionSweepSession.LastHeartbeatAt = now;
                    Main.Log.LogInfo(
                        "Transition sweep waiting for teleporter control restore elapsed=" +
                        (now - _transitionSweepSession.StepStartedAt).ToString("0.00", CultureInfo.InvariantCulture) +
                        " currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                        " player=" + (BetterPlayerControl.Instance != null ? FormatVector3(BetterPlayerControl.Instance.transform.position) : "<null>") +
                        " step=" + DescribeNavigationStep(currentStep));
                    WriteTransitionSweepReport(isComplete: false);
                }

                _transitionSweepSession.NextActionTime = now + 0.1f;
                return;
            }

            if (!CanUseNavigationNow())
            {
                RecordTransitionSweepFailure("navigation unavailable " + navigationUnavailableReason);
                return;
            }

            if (currentStep.Kind == NavigationGraph.StepKind.Door &&
                now - _transitionSweepSession.StepStartedAt >= 0.5f)
            {
                TryAttemptTransitionInteraction(currentStep, allowOptionalDoorInteraction: true);
            }

            if (_transitionSweepSession.LastHeartbeatAt <= 0f ||
                now - _transitionSweepSession.LastHeartbeatAt >= TransitionSweepHeartbeatSeconds)
            {
                _transitionSweepSession.LastHeartbeatAt = now;
                Main.Log.LogInfo(
                    "Transition sweep heartbeat elapsed=" +
                    (_transitionSweepSession.StepStartedAt > 0f
                        ? (now - _transitionSweepSession.StepStartedAt).ToString("0.00", CultureInfo.InvariantCulture)
                        : "0.00") +
                    " currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                    " player=" + (BetterPlayerControl.Instance != null ? FormatVector3(BetterPlayerControl.Instance.transform.position) : "<null>") +
                    " step=" + DescribeNavigationStep(currentStep));
                WriteTransitionSweepReport(isComplete: false);
            }

            _transitionSweepSession.NextActionTime = now + 0.1f;
        }

        private void ContinueDoorTransitionSweepAfterInteraction()
        {
            if (_transitionSweepSession == null || _transitionSweepSession.CurrentStep == null)
                return;

            if (!TryBeginForcedTransitionSweepNavigation(_transitionSweepSession.CurrentStep))
                RecordTransitionSweepFailure("forced navigation start failed");
        }

        private bool TryRecoverTimedOutDoorTransitionSweepStep(NavigationGraph.PathStep step, float now)
        {
            if (_transitionSweepSession == null ||
                step == null ||
                step.Kind != NavigationGraph.StepKind.Door)
            {
                return false;
            }

            if (!_transitionSweepSession.DoorTimeoutRecoveryUsed)
            {
                if (!TryRecoverAutoWalk(step, NavigationTargetKind.ZoneFallback))
                    return false;

                _transitionSweepSession.DoorTimeoutRecoveryUsed = true;
                _transitionSweepSession.StepStartedAt = now;
                _transitionSweepSession.LastHeartbeatAt = 0f;
                _transitionSweepSession.NextActionTime = now + 0.1f;
                Main.Log.LogInfo(
                    "Transition sweep timeout recovery granted step=" +
                    DescribeNavigationStep(step));
                return true;
            }

            if (_transitionSweepSession.DoorTimeoutFinalGraceUsed ||
                !TryRecoverAutoWalk(step, NavigationTargetKind.ZoneFallback))
            {
                return false;
            }

            _transitionSweepSession.DoorTimeoutFinalGraceUsed = true;
            float timeoutSeconds = GetOpenPassageTransitionOverrideTimeoutSeconds(step);
            _transitionSweepSession.StepStartedAt = now - Mathf.Max(0f, timeoutSeconds - DoorTransitionSweepPostRecoveryGraceSeconds);
            _transitionSweepSession.LastHeartbeatAt = 0f;
            _transitionSweepSession.NextActionTime = now + 0.1f;
            Main.Log.LogInfo(
                "Transition sweep timeout final recovery grace granted step=" +
                DescribeNavigationStep(step));
            return true;
        }

        private bool HasForcedTransitionSweepStepSucceeded(NavigationGraph.PathStep step)
        {
            if (_transitionSweepSession == null ||
                _transitionSweepSession.Phase != TransitionSweepPhase.Running ||
                step == null ||
                BetterPlayerControl.Instance == null)
            {
                return false;
            }

            string currentZone = GetCurrentZoneNameInternal();
            if (!string.IsNullOrEmpty(step.ToZone) &&
                (IsCurrentZoneEquivalentTo(step.ToZone) || IsAcceptedOverrideDestinationZone(step, currentZone)))
            {
                return true;
            }

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;

            if (step.Kind == NavigationGraph.StepKind.OpenPassage)
            {
                if (HasReachedOpenPassageTransitionSweepOverrideCheckpoint(step, playerPosition))
                    return true;

                if (HasReachedOpenPassageOverrideCompletion(step))
                    return true;

                if (ShouldAdvanceOpenPassageStepByGeometry(step))
                    return true;

                if (step.ToWaypoint != Vector3.zero)
                {
                    Vector3 targetPosition = step.ToWaypoint;
                    targetPosition.y = playerPosition.y;
                    if (Vector3.Distance(playerPosition, targetPosition) <= AutoWalkArrivalDistance)
                        return true;
                }

                if (step.ToCrossingAnchor != Vector3.zero)
                {
                    Vector3 crossingTarget = step.ToCrossingAnchor;
                    crossingTarget.y = playerPosition.y;
                    if (Vector3.Distance(playerPosition, crossingTarget) <= AutoWalkArrivalDistance)
                        return true;
                }

                return false;
            }

            if (step.Kind == NavigationGraph.StepKind.Door &&
                HasReachedDoorTransitionSweepPushThroughTarget(step, playerPosition))
            {
                return true;
            }

            if (step.ToWaypoint != Vector3.zero)
            {
                Vector3 targetPosition = step.ToWaypoint;
                targetPosition.y = playerPosition.y;
                if (Vector3.Distance(playerPosition, targetPosition) <= AutoWalkArrivalDistance)
                    return true;
            }

            return false;
        }

        private bool HasReachedOpenPassageTransitionSweepOverrideCheckpoint(NavigationGraph.PathStep step, Vector3 playerPosition)
        {
            if (_transitionSweepSession == null ||
                _transitionSweepSession.Kind != TransitionSweepKind.OpenPassage ||
                _transitionSweepSession.Phase != TransitionSweepPhase.Running ||
                step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage)
            {
                return false;
            }

            string stepKey = BuildNavigationStepKey(step);
            string currentStepKey = BuildNavigationStepKey(_transitionSweepSession.CurrentStep);
            if (string.IsNullOrEmpty(stepKey) ||
                !string.Equals(stepKey, currentStepKey, StringComparison.Ordinal) ||
                !TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) ||
                transitionOverride.IntermediateWaypoints == null ||
                transitionOverride.IntermediateWaypoints.Length == 0)
            {
                return false;
            }

            Vector3 checkpoint = transitionOverride.IntermediateWaypoints[transitionOverride.IntermediateWaypoints.Length - 1];
            if (checkpoint == Vector3.zero)
                return false;

            Vector3 flattenedCheckpoint = checkpoint;
            flattenedCheckpoint.y = playerPosition.y;
            if (Vector3.Distance(playerPosition, flattenedCheckpoint) > AutoWalkArrivalDistance)
                return false;

            Vector3 sourceOrigin = GetOpenPassageSourceGuidanceOrigin(step);
            if (sourceOrigin != Vector3.zero)
            {
                sourceOrigin.y = playerPosition.y;
                if (Vector3.Distance(sourceOrigin, flattenedCheckpoint) <= AutoWalkArrivalDistance)
                    return false;
            }

            return true;
        }

        private bool HasReachedDoorTransitionSweepPushThroughTarget(NavigationGraph.PathStep step, Vector3 playerPosition)
        {
            if (_transitionSweepSession == null ||
                _transitionSweepSession.Kind != TransitionSweepKind.Door ||
                _transitionSweepSession.Phase != TransitionSweepPhase.Running ||
                step == null ||
                step.Kind != NavigationGraph.StepKind.Door)
            {
                return false;
            }

            if (!string.Equals(
                    BuildNavigationStepKey(step),
                    BuildNavigationStepKey(_transitionSweepSession.CurrentStep),
                    StringComparison.Ordinal))
            {
                return false;
            }

            return IsWithinDoorPushThroughArrivalDistance(playerPosition, _transitionSweepSession.DoorPushThroughPosition) ||
                IsWithinDoorPushThroughArrivalDistance(playerPosition, step.DestinationClearPoint) ||
                IsWithinDoorPushThroughArrivalDistance(playerPosition, GetDoorTraversalDestinationTarget(step));
        }

        private static bool IsWithinTransitionSweepArrivalDistance(Vector3 playerPosition, Vector3 targetPosition)
        {
            if (targetPosition == Vector3.zero)
                return false;

            targetPosition.y = playerPosition.y;
            return Vector3.Distance(playerPosition, targetPosition) <= AutoWalkArrivalDistance;
        }

        private static bool IsWithinDoorPushThroughArrivalDistance(Vector3 playerPosition, Vector3 targetPosition)
        {
            if (targetPosition == Vector3.zero)
                return false;

            targetPosition.y = playerPosition.y;
            return Vector3.Distance(playerPosition, targetPosition) <= DoorPushThroughArrivalDistance;
        }

        private static float GetPlanarDistanceToTarget(Vector3 playerPosition, Vector3 targetPosition)
        {
            if (targetPosition == Vector3.zero)
                return float.PositiveInfinity;

            targetPosition.y = playerPosition.y;
            return Vector3.Distance(playerPosition, targetPosition);
        }

        private static bool TryGetDoorPushThroughSourceTarget(NavigationGraph.PathStep step, out Vector3 sourceTarget)
        {
            sourceTarget = Vector3.zero;
            if (step == null)
                return false;

            if (step.SourceClearPoint != Vector3.zero)
            {
                sourceTarget = step.SourceClearPoint;
                return true;
            }

            if (step.SourceApproachPoint != Vector3.zero)
            {
                sourceTarget = step.SourceApproachPoint;
                return true;
            }

            if (step.FromCrossingAnchor != Vector3.zero)
            {
                sourceTarget = step.FromCrossingAnchor;
                return true;
            }

            if (step.FromWaypoint != Vector3.zero)
            {
                sourceTarget = step.FromWaypoint;
                return true;
            }

            return false;
        }

        private bool TryGetDoorThresholdAdvanceTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            out Vector3 sourceTarget)
        {
            sourceTarget = Vector3.zero;
            if (!TryGetDoorPushThroughSourceTarget(step, out sourceTarget) ||
                sourceTarget == Vector3.zero)
            {
                return false;
            }

            if (step == null ||
                string.IsNullOrEmpty(step.FromZone) ||
                string.IsNullOrEmpty(currentZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
            {
                return true;
            }

            if (!LocalNavigationMaps.TrySnapPositionToNearestWalkableCell(
                    step.FromZone,
                    sourceTarget,
                    out Vector3 snappedSourceTarget,
                    out string snapDetail))
            {
                return true;
            }

            Vector3 originalSourceTarget = sourceTarget;
            if (GetFlatDistance(originalSourceTarget, snappedSourceTarget) > DoorTraversalClearanceDistance)
                return true;

            if (GetFlatDistance(originalSourceTarget, snappedSourceTarget) > 0.05f)
            {
                sourceTarget = snappedSourceTarget;
                LogNavigationTrackerDebug(
                    "Snapped door threshold source target original=" + FormatVector3(originalSourceTarget) +
                    " snapped=" + FormatVector3(sourceTarget) +
                    " detail=" + (snapDetail ?? "<null>") +
                    " step=" + DescribeNavigationStep(step));
            }

            return true;
        }

        private bool TrySnapDoorSourceNavigationTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 targetPosition,
            float maxSnapDistance,
            string context,
            out Vector3 snappedTargetPosition)
        {
            snappedTargetPosition = targetPosition;
            if (step == null ||
                targetPosition == Vector3.zero ||
                maxSnapDistance <= 0f ||
                string.IsNullOrWhiteSpace(currentZone) ||
                string.IsNullOrWhiteSpace(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
            {
                return false;
            }

            if (!LocalNavigationMaps.TrySnapPositionToNearestWalkableCell(
                    step.FromZone,
                    targetPosition,
                    out Vector3 candidatePosition,
                    out string snapDetail))
            {
                return false;
            }

            float snapDistance = GetFlatDistance(targetPosition, candidatePosition);
            if (snapDistance <= 0.05f || snapDistance > maxSnapDistance)
                return false;

            snappedTargetPosition = candidatePosition;
            LogNavigationTrackerDebug(
                "Snapped door navigation target original=" + FormatVector3(targetPosition) +
                " snapped=" + FormatVector3(snappedTargetPosition) +
                " context=" + (context ?? "<null>") +
                " detail=" + (snapDetail ?? "<null>") +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private static bool ShouldKeepDoorThresholdAdvance(
            Vector3 playerPosition,
            Vector3 sourceTarget,
            Vector3 pushThroughPosition)
        {
            if (sourceTarget == Vector3.zero || pushThroughPosition == Vector3.zero)
                return false;

            Vector3 handoffVector = pushThroughPosition - sourceTarget;
            handoffVector.y = 0f;
            float handoffDistance = handoffVector.magnitude;
            if (handoffDistance <= 0.0001f)
                return false;

            Vector3 handoffDirection = handoffVector / handoffDistance;
            Vector3 playerOffset = playerPosition - sourceTarget;
            playerOffset.y = 0f;

            float forwardProgress = Vector3.Dot(playerOffset, handoffDirection);
            if (forwardProgress < handoffDistance * 0.75f)
                return true;

            Vector3 lateralOffset = playerOffset - handoffDirection * forwardProgress;
            float allowedLateralOffset = Mathf.Max(0.65f, handoffDistance * 0.25f);
            return lateralOffset.magnitude > allowedLateralOffset;
        }

        private static bool HasMeaningfulDoorThresholdClearance(
            Vector3 sourceTarget,
            Vector3 pushThroughPosition,
            Vector3 candidateTarget)
        {
            if (sourceTarget == Vector3.zero ||
                pushThroughPosition == Vector3.zero ||
                candidateTarget == Vector3.zero)
            {
                return false;
            }

            float forwardProgress = GetDoorThresholdForwardProgress(
                sourceTarget,
                pushThroughPosition,
                candidateTarget);
            if (forwardProgress <= DoorTraversalClearanceDistance * 0.5f)
                return false;

            // Threshold handoff targets deliberately bias sideways to stay on a reachable
            // source-side cell, so their total source distance is shorter than the full
            // door-clearance span. Require real forward progress plus about a source-advance
            // worth of separation instead of the larger doorway-clearance distance.
            return GetFlatDistance(sourceTarget, candidateTarget) > DoorPushThroughSourceAdvanceDistance;
        }

        private static Vector3 BuildDoorThresholdHandoffPosition(
            Vector3 sourceTarget,
            Vector3 pushThroughPosition)
        {
            if (sourceTarget == Vector3.zero)
                return pushThroughPosition;

            if (pushThroughPosition == Vector3.zero)
                return sourceTarget;

            Vector3 handoffVector = pushThroughPosition - sourceTarget;
            handoffVector.y = 0f;
            float handoffDistance = handoffVector.magnitude;
            if (handoffDistance <= 0.0001f)
                return pushThroughPosition;

            float handoffAdvanceDistance = Mathf.Min(
                DoorPushThroughSourceAdvanceDistance,
                handoffDistance);
            Vector3 handoffDirection = handoffVector / handoffDistance;
            Vector3 handoffTarget = sourceTarget + handoffDirection * handoffAdvanceDistance;

            // Bias the bridge off the source-threshold line so navmesh snapping does not
            // collapse it back into the same source-side cell.
            Vector3 lateralDirection = Vector3.Cross(Vector3.up, handoffDirection);
            lateralDirection.y = 0f;
            if (lateralDirection.sqrMagnitude > 0.0001f)
            {
                lateralDirection.Normalize();
                float lateralOffsetDistance = Mathf.Min(
                    DoorTraversalLateralOffsetDistance,
                    handoffDistance * 0.5f);
                handoffTarget += lateralDirection * lateralOffsetDistance;
            }

            handoffTarget.y = pushThroughPosition.y != 0f
                ? pushThroughPosition.y
                : sourceTarget.y;
            return handoffTarget;
        }

        private bool TryGetDoorThresholdHandoffTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 sourceTarget,
            Vector3 pushThroughPosition,
            out Vector3 handoffTarget)
        {
            handoffTarget = BuildDoorThresholdHandoffPosition(sourceTarget, pushThroughPosition);
            if (handoffTarget == Vector3.zero)
                return false;

            if (TrySnapDoorSourceNavigationTarget(
                    step,
                    currentZone,
                    handoffTarget,
                    DoorTraversalClearanceDistance,
                    "door-threshold-handoff",
                    out Vector3 snappedHandoffTarget))
            {
                handoffTarget = snappedHandoffTarget;
            }

            float forwardProgress = GetDoorThresholdForwardProgress(
                sourceTarget,
                pushThroughPosition,
                handoffTarget);
            if (forwardProgress <= 0.05f ||
                !HasMeaningfulDoorThresholdClearance(sourceTarget, pushThroughPosition, handoffTarget))
            {
                LogNavigationTrackerDebug(
                    "Discarded door threshold handoff target position=" + FormatVector3(handoffTarget) +
                    " forwardProgress=" + forwardProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
                handoffTarget = Vector3.zero;
                return false;
            }

            return true;
        }

        private static float GetDoorThresholdForwardProgress(
            Vector3 sourceTarget,
            Vector3 pushThroughPosition,
            Vector3 candidateTarget)
        {
            if (sourceTarget == Vector3.zero ||
                pushThroughPosition == Vector3.zero ||
                candidateTarget == Vector3.zero)
            {
                return 0f;
            }

            Vector3 pushThroughVector = pushThroughPosition - sourceTarget;
            pushThroughVector.y = 0f;
            float pushThroughDistance = pushThroughVector.magnitude;
            if (pushThroughDistance <= 0.0001f)
                return 0f;

            Vector3 pushThroughDirection = pushThroughVector / pushThroughDistance;
            Vector3 candidateOffset = candidateTarget - sourceTarget;
            candidateOffset.y = 0f;
            return Vector3.Dot(candidateOffset, pushThroughDirection);
        }

        private bool TryGetActiveDoorPushThroughPosition(
            NavigationGraph.PathStep step,
            string currentZone,
            out Vector3 pushThroughPosition)
        {
            pushThroughPosition = Vector3.zero;
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                string.IsNullOrEmpty(currentZone) ||
                string.IsNullOrEmpty(step.FromZone) ||
                !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
            {
                return false;
            }

            string stepKey = BuildNavigationStepKey(step);
            if (string.IsNullOrEmpty(stepKey))
                return false;

            if (_transitionSweepSession != null &&
                _transitionSweepSession.Kind == TransitionSweepKind.Door &&
                _transitionSweepSession.Phase == TransitionSweepPhase.Running &&
                _transitionSweepSession.DoorInteractionTriggered &&
                _transitionSweepSession.DoorPushThroughPosition != Vector3.zero &&
                string.Equals(stepKey, BuildNavigationStepKey(_transitionSweepSession.CurrentStep), StringComparison.Ordinal))
            {
                pushThroughPosition = _transitionSweepSession.DoorPushThroughPosition;
                return true;
            }

            if (_doorTraversalInteractionTriggered &&
                _doorTraversalPushThroughPosition != Vector3.zero &&
                string.Equals(stepKey, _doorTraversalStepKey, StringComparison.Ordinal))
            {
                pushThroughPosition = _doorTraversalPushThroughPosition;
                return true;
            }

            return false;
        }

        private bool IsDoorPushThroughNavigationTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            NavigationTargetKind targetKind,
            Vector3 targetPosition)
        {
            if (targetKind != NavigationTargetKind.ZoneFallback)
                return false;

            if (!TryGetActiveDoorPushThroughPosition(step, currentZone, out Vector3 pushThroughPosition))
                return false;

            return targetPosition == Vector3.zero ||
                GetFlatDistance(pushThroughPosition, targetPosition) <= 0.35f;
        }

        private float GetAutoWalkBlockedTimeoutSeconds(
            NavigationGraph.PathStep step,
            string currentZone,
            NavigationTargetKind targetKind,
            Vector3 targetPosition)
        {
            return IsDoorPushThroughNavigationTarget(step, currentZone, targetKind, targetPosition)
                ? DoorPushThroughBlockedTimeoutSeconds
                : AutoWalkBlockedTimeoutSeconds;
        }

        private float GetLocalNavigationGoalReachedDistance(string planningContext)
        {
            return string.Equals(planningContext, "door-push-through-local", StringComparison.Ordinal) ||
                string.Equals(planningContext, "door-threshold-handoff-local", StringComparison.Ordinal)
                ? DoorTraversalClearanceDistance
                : string.Equals(planningContext, "door-push-through", StringComparison.Ordinal) ||
                string.Equals(planningContext, "door-threshold-handoff", StringComparison.Ordinal) ||
                string.Equals(planningContext, "open-passage-handoff", StringComparison.Ordinal) ||
                string.Equals(planningContext, "open-passage-override-source", StringComparison.Ordinal) ||
                string.Equals(planningContext, "open-passage-override-destination", StringComparison.Ordinal)
                ? (string.Equals(planningContext, "door-push-through", StringComparison.Ordinal)
                    ? DoorPushThroughLocalNavigationGoalReachedDistance
                    : string.Equals(planningContext, "door-threshold-handoff", StringComparison.Ordinal)
                        ? DoorThresholdHandoffLocalNavigationGoalReachedDistance
                    : OpenPassageOverrideLocalNavigationGoalReachedDistance)
                : LocalNavigationGoalReachedDistance;
        }

        private float GetRawNavigationGoalReachedDistance(string targetContext)
        {
            return string.Equals(targetContext, "open-passage-guided", StringComparison.Ordinal)
                ? GetOpenPassageGuidedWaypointAdvanceDistance()
                : AutoWalkArrivalDistance;
        }

        private float GetOpenPassageGuidedWaypointAdvanceDistance()
        {
            return _openPassageTraversalStage == OpenPassageTraversalStage.SourceHandoff
                ? OpenPassageOverrideLocalNavigationGoalReachedDistance
                : OpenPassageGuidedWaypointAdvanceDistance;
        }

        private float GetAutoWalkArrivalDistance(NavigationTargetKind targetKind)
        {
            if (targetKind == NavigationTargetKind.LocalWaypoint &&
                !string.IsNullOrWhiteSpace(_localNavigationPathContext))
            {
                return GetLocalNavigationGoalReachedDistance(_localNavigationPathContext);
            }

            if (!string.IsNullOrWhiteSpace(_rawNavigationTargetContext))
                return GetRawNavigationGoalReachedDistance(_rawNavigationTargetContext);

            return AutoWalkArrivalDistance;
        }

        private bool IsRunningOpenPassageTransitionSweepStep(NavigationGraph.PathStep step)
        {
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                _transitionSweepSession == null ||
                _transitionSweepSession.Kind != TransitionSweepKind.OpenPassage ||
                _transitionSweepSession.Phase != TransitionSweepPhase.Running ||
                _transitionSweepSession.CurrentStep == null)
            {
                return false;
            }

            return string.Equals(
                BuildNavigationStepKey(step),
                BuildNavigationStepKey(_transitionSweepSession.CurrentStep),
                StringComparison.Ordinal);
        }

        private void CycleNavigationTarget()
        {
            Loc.RefreshLanguage();

            string currentZone = GetCurrentZoneNameForNavigation();
            if (!TryGetRoomNavigationTargets(currentZone, out List<RoomObjectTarget> targets) || targets.Count == 0)
            {
                ScreenReader.Say(Loc.Get("navigation_no_rooms"));
                return;
            }

            if (!string.Equals(_lastRoomObjectListZone, currentZone, StringComparison.OrdinalIgnoreCase))
                _navigationSelectionIndex = -1;

            int currentIndex = FindCurrentRoomTargetIndex(targets, currentZone);

            _navigationSelectionIndex = currentIndex >= 0
                ? Mathf.Clamp(currentIndex, 0, targets.Count - 1)
                : 0;

            _roomObjectTargets = targets;
            _lastRoomObjectListZone = currentZone;
            OpenRoomObjectPicker();
        }

        private void DescribeCurrentRoom()
        {
            Loc.RefreshLanguage();

            if (!CanUseNavigationNow())
            {
                ScreenReader.Say(Loc.Get("room_scan_unavailable"), remember: false);
                return;
            }

            string currentZone = GetCurrentZoneNameForNavigation();
            string roomName = GetCurrentRoomScanName(currentZone);
            if (!TryGetRoomObjectTargets(currentZone, out List<RoomObjectTarget> targets) || targets.Count == 0)
            {
                ScreenReader.Say(Loc.Get("room_scan_empty", roomName), remember: false);
                return;
            }

            string report = BuildFacingRelativeRoomReport(roomName, targets);
            ScreenReader.Say(report, remember: false);
        }

        private string BuildFacingRelativeRoomReport(string roomName, List<RoomObjectTarget> targets)
        {
            var groupedLabels = new Dictionary<FacingRelativeDirection, List<string>>();
            for (int i = 0; i < targets.Count; i++)
            {
                RoomObjectTarget target = targets[i];
                if (target == null || target.Interactable == null)
                    continue;

                FacingRelativeDirection direction = GetFacingRelativeDirection(target.Interactable.transform.position);
                if (!groupedLabels.TryGetValue(direction, out List<string> labels))
                {
                    labels = new List<string>();
                    groupedLabels[direction] = labels;
                }

                if (!labels.Contains(target.Label))
                    labels.Add(target.Label);
            }

            var parts = new List<string>();
            parts.Add(Loc.Get("room_scan_title", roomName));

            FacingRelativeDirection[] orderedDirections =
            {
                FacingRelativeDirection.Here,
                FacingRelativeDirection.Ahead,
                FacingRelativeDirection.AheadRight,
                FacingRelativeDirection.Right,
                FacingRelativeDirection.BehindRight,
                FacingRelativeDirection.Behind,
                FacingRelativeDirection.BehindLeft,
                FacingRelativeDirection.Left,
                FacingRelativeDirection.AheadLeft
            };

            for (int i = 0; i < orderedDirections.Length; i++)
            {
                FacingRelativeDirection direction = orderedDirections[i];
                if (!groupedLabels.TryGetValue(direction, out List<string> labels) || labels.Count == 0)
                    continue;

                parts.Add(Loc.Get("room_scan_group", GetFacingRelativeDirectionLabel(direction), string.Join(", ", labels.ToArray())));
            }

            return string.Join(". ", parts.ToArray());
        }

        private void OpenRoomObjectPicker()
        {
            if (_roomObjectTargets == null || _roomObjectTargets.Count == 0)
            {
                ScreenReader.Say(Loc.Get("navigation_no_rooms"));
                return;
            }

            _isRoomObjectPickerOpen = true;
            AcquireRoomObjectPickerInputBlock();
            SyncRoomObjectPickerKeyStates();
            AnnounceCurrentRoomObjectPickerItem();
        }

        private void CloseRoomObjectPicker(bool announceClosed)
        {
            if (!_isRoomObjectPickerOpen)
                return;

            _isRoomObjectPickerOpen = false;
            ReleaseRoomObjectPickerInputBlock();
            SyncRoomObjectPickerKeyStates();
            if (announceClosed)
                ScreenReader.Say(Loc.Get("navigation_room_picker_closed"));
        }

        private void UpdateRoomObjectPicker()
        {
            if (_roomObjectTargets == null || _roomObjectTargets.Count == 0)
            {
                CloseRoomObjectPicker(announceClosed: false);
                return;
            }

            string currentZone = GetCurrentZoneNameForNavigation();
            if (string.IsNullOrEmpty(currentZone) ||
                !string.Equals(currentZone, _lastRoomObjectListZone, StringComparison.OrdinalIgnoreCase))
            {
                CloseRoomObjectPicker(announceClosed: true);
                return;
            }

            if (WasRoomPickerKeyPressed(KeyCode.UpArrow, VkUp, ref _roomPickerUpWasDown))
            {
                _navigationSelectionIndex = (_navigationSelectionIndex + _roomObjectTargets.Count - 1) % _roomObjectTargets.Count;
                AnnounceCurrentRoomObjectPickerItem();
                return;
            }

            if (WasRoomPickerKeyPressed(KeyCode.DownArrow, VkDown, ref _roomPickerDownWasDown))
            {
                _navigationSelectionIndex = (_navigationSelectionIndex + 1) % _roomObjectTargets.Count;
                AnnounceCurrentRoomObjectPickerItem();
                return;
            }

            if (WasRoomPickerKeyPressed(KeyCode.Return, VkReturn, ref _roomPickerReturnWasDown) ||
                WasRoomPickerKeyPressed(KeyCode.KeypadEnter, VkReturn, ref _roomPickerReturnWasDown) ||
                WasRoomPickerKeyPressed(KeyCode.Space, VkSpace, ref _roomPickerSpaceWasDown))
            {
                SelectCurrentRoomObjectPickerItem();
                return;
            }

            if (WasRoomPickerKeyPressed(KeyCode.Escape, VkEscape, ref _roomPickerEscapeWasDown))
                CloseRoomObjectPicker(announceClosed: true);
        }

        private void AnnounceCurrentRoomObjectPickerItem()
        {
            if (_roomObjectTargets == null || _roomObjectTargets.Count == 0)
                return;

            _navigationSelectionIndex = Mathf.Clamp(_navigationSelectionIndex, 0, _roomObjectTargets.Count - 1);
            RoomObjectTarget target = _roomObjectTargets[_navigationSelectionIndex];
            string currentZone = GetCurrentZoneNameForNavigation();
            string targetLabel = BuildRoomNavigationSelectionLabel(target.ZoneName, target.Label, currentZone);
            string announcement = Loc.Get("navigation_room_option", _navigationSelectionIndex + 1, _roomObjectTargets.Count, targetLabel);
            ScreenReader.Say(Loc.Get("navigation_room_list_title") + ". " + announcement);
        }

        private void SelectCurrentRoomObjectPickerItem()
        {
            if (_roomObjectTargets == null || _roomObjectTargets.Count == 0)
            {
                CloseRoomObjectPicker(announceClosed: false);
                return;
            }

            _navigationSelectionIndex = Mathf.Clamp(_navigationSelectionIndex, 0, _roomObjectTargets.Count - 1);
            RoomObjectTarget target = _roomObjectTargets[_navigationSelectionIndex];
            SetTrackedInteractable(null, target.ZoneName, target.Label);
            CloseRoomObjectPicker(announceClosed: false);
            BeginNavigation(target.ZoneName, target.Label);
        }

        private bool TryGetRoomNavigationTargets(string currentZone, out List<RoomObjectTarget> targets)
        {
            targets = new List<RoomObjectTarget>();
            List<string> zones = NavigationGraph.GetAllZones();
            if (zones == null || zones.Count == 0)
                return false;

            for (int i = 0; i < zones.Count; i++)
            {
                string zoneName = zones[i];
                if (string.IsNullOrWhiteSpace(zoneName))
                    continue;

                string label = NormalizeIdentifierName(zoneName);
                if (string.IsNullOrWhiteSpace(label))
                    label = zoneName;

                targets.Add(new RoomObjectTarget
                {
                    Interactable = null,
                    Label = label,
                    ZoneName = zoneName
                });
            }

            if (targets.Count == 0)
                return false;

            targets.Sort((left, right) =>
            {
                bool leftIsCurrent = !string.IsNullOrWhiteSpace(currentZone) && AreZonesEquivalent(left.ZoneName, currentZone);
                bool rightIsCurrent = !string.IsNullOrWhiteSpace(currentZone) && AreZonesEquivalent(right.ZoneName, currentZone);
                if (leftIsCurrent != rightIsCurrent)
                    return leftIsCurrent ? -1 : 1;

                return string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase);
            });

            return true;
        }

        private static int FindCurrentRoomTargetIndex(List<RoomObjectTarget> targets, string currentZone)
        {
            if (targets == null || targets.Count == 0 || string.IsNullOrWhiteSpace(currentZone))
                return -1;

            for (int i = 0; i < targets.Count; i++)
            {
                RoomObjectTarget target = targets[i];
                if (target == null || string.IsNullOrWhiteSpace(target.ZoneName))
                    continue;

                if (AreZonesEquivalent(target.ZoneName, currentZone))
                    return i;
            }

            return -1;
        }

        private static string BuildRoomNavigationSelectionLabel(string targetZone, string targetLabel, string currentZone)
        {
            string normalizedLabel = !string.IsNullOrWhiteSpace(targetLabel)
                ? targetLabel
                : NormalizeIdentifierName(targetZone);
            if (string.IsNullOrWhiteSpace(normalizedLabel))
                normalizedLabel = targetZone;

            if (!string.IsNullOrWhiteSpace(currentZone) &&
                !string.IsNullOrWhiteSpace(targetZone) &&
                AreZonesEquivalent(targetZone, currentZone))
            {
                return Loc.Get("navigation_target_in_current_room") + ". " + normalizedLabel;
            }

            return normalizedLabel;
        }

        private void AcquireRoomObjectPickerInputBlock()
        {
            ReleaseRoomObjectPickerInputBlock();

            if (Services.InputService == null)
                return;

            _roomObjectPickerInputHandle = Services.InputService.PushMode(IMirandaInputService.EInputMode.None, "DateEverythingAccess.RoomObjectPicker");
        }

        private void ReleaseRoomObjectPickerInputBlock()
        {
            if (_roomObjectPickerInputHandle == null)
                return;

            _roomObjectPickerInputHandle.SafeDispose();
            _roomObjectPickerInputHandle = null;
        }

        private static bool WasRoomPickerKeyPressed(KeyCode keyCode, int virtualKey, ref bool wasDown)
        {
            bool isDown = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            bool pressed = Input.GetKeyDown(keyCode) || (isDown && !wasDown);
            wasDown = isDown;
            return pressed;
        }

        private static void SyncRoomObjectPickerKeyStates()
        {
            _roomPickerUpWasDown = IsVirtualKeyDown(VkUp);
            _roomPickerDownWasDown = IsVirtualKeyDown(VkDown);
            _roomPickerReturnWasDown = IsVirtualKeyDown(VkReturn);
            _roomPickerSpaceWasDown = IsVirtualKeyDown(VkSpace);
            _roomPickerEscapeWasDown = IsVirtualKeyDown(VkEscape);
        }

        private static bool IsVirtualKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private void StartNavigationToCurrentTarget()
        {
            Loc.RefreshLanguage();

            if (!TryResolveCurrentObjectiveInteractable(out InteractableObj interactable, out string targetZone, out string targetLabel))
            {
                ScreenReader.Say(Loc.Get("navigation_no_objective"));
                return;
            }

            SetTrackedInteractable(interactable, targetZone, targetLabel);
            BeginNavigation(targetZone, targetLabel);
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

            _isAutoWalking = true;
            _lastAutoWalkPosition = BetterPlayerControl.Instance != null ? BetterPlayerControl.Instance.transform.position : Vector3.zero;
            _lastAutoWalkProgressTime = Time.unscaledTime;
            ScreenReader.Say(Loc.Get("navigation_autowalk_started"));
        }

        private bool TryBeginForcedTransitionSweepNavigation(NavigationGraph.PathStep step)
        {
            if (step == null || !CanUseNavigationNow() || BetterPlayerControl.Instance == null)
                return false;

            SetTrackedInteractable(null, null, null);
            _navigationTargetZone = step.ToZone;
            _navigationTargetLabel = BuildTransitionSweepLabel(step);
            _navigationPath = new List<NavigationGraph.PathStep> { step };
            _isNavigationActive = true;
            _isAutoWalking = true;
            _lastNavigationNextZone = null;
            _lastNavigationAnnouncementLabel = null;
            _lastNavigationInteractionAttemptTime = 0f;
            _autoWalkTransitionUntil = 0f;
            _autoWalkRecoveryAttempts = 0;
            ResetOpenPassageTraversalState();
            SyncOpenPassageTraversalState(step);
            ClearNavigationZoneOverride();
            UpdateNavigationTracker();

            if (!ApplyNavigationInput(Vector3.zero, Vector3.zero))
                return false;

            ResetAutoWalkProgress();
            _transitionSweepSession.StepStartedAt = Time.unscaledTime;
            _transitionSweepSession.LastHeartbeatAt = 0f;
            _transitionSweepSession.Phase = TransitionSweepPhase.Running;
            _transitionSweepSession.NextActionTime = Time.unscaledTime + 0.1f;
            Main.Log.LogInfo("Transition sweep forced navigation active step=" + DescribeNavigationStep(step));
            return true;
        }

        private static bool IsExpectedTeleporterSweepControlLock(
            NavigationGraph.PathStep step,
            string navigationUnavailableReason)
        {
            return step != null &&
                step.Kind == NavigationGraph.StepKind.Teleporter &&
                string.Equals(navigationUnavailableReason, "playerState=CantControl", StringComparison.Ordinal);
        }

        private bool TryPrepareDoorTransitionSweepNavigation(NavigationGraph.PathStep step)
        {
            if (_transitionSweepSession == null || step == null)
                return false;

            bool interactionTriggered = TryAttemptDoorTransitionSweepInteraction(step, out Vector3 pushThroughPosition);
            _transitionSweepSession.DoorInteractionTriggered = interactionTriggered;
            _transitionSweepSession.DoorPushThroughPosition = pushThroughPosition;
            _transitionSweepSession.Phase = TransitionSweepPhase.AwaitingDoorInteractionSettle;
            _transitionSweepSession.NextActionTime = Time.unscaledTime +
                (interactionTriggered ? DoorTransitionSweepInteractionSettleSeconds : 0.1f);
            Main.Log.LogInfo(
                "Door transition sweep prepared interactionTriggered=" + interactionTriggered +
                " pushThroughPosition=" + FormatVector3(pushThroughPosition) +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private void FinishTransitionSweep()
        {
            if (_transitionSweepSession == null)
                return;

            TransitionSweepKind sweepKind = _transitionSweepSession.Kind;
            int totalCount = _transitionSweepSession.Entries.Count;
            int passedCount = 0;
            int failedCount = 0;
            for (int i = 0; i < _transitionSweepSession.Entries.Count; i++)
            {
                string status = _transitionSweepSession.Entries[i].Status;
                if (string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase))
                    passedCount++;
                else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    failedCount++;
            }

            WriteTransitionSweepReport(isComplete: true);
            Main.Log.LogInfo(
                GetTransitionSweepLogLabel(sweepKind) + " complete total=" + totalCount +
                " passed=" + passedCount +
                " failed=" + failedCount);
            _transitionSweepSession = null;
            ScreenReader.Say(
                Loc.Get(GetTransitionSweepCompleteMessageKey(sweepKind), totalCount, passedCount, failedCount),
                remember: false);
        }

        private List<NavigationGraph.PathStep> BuildTransitionSweepSteps(HashSet<string> previouslyPassedKeys, TransitionSweepKind sweepKind)
        {
            List<NavigationGraph.PathStep> allSteps = NavigationGraph.GetAllPathSteps();
            if (allSteps == null || allSteps.Count == 0)
                return null;

            var steps = new List<NavigationGraph.PathStep>(allSteps.Count);
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < allSteps.Count; i++)
            {
                NavigationGraph.PathStep step = allSteps[i];
                if (ShouldSkipTransitionSweepStep(step, sweepKind))
                    continue;

                string stepKey = BuildNavigationStepKey(step);
                if (string.IsNullOrEmpty(stepKey) || !seenKeys.Add(stepKey))
                    continue;

                if (previouslyPassedKeys != null && previouslyPassedKeys.Contains(stepKey))
                    continue;

                steps.Add(step);
            }

            steps.Sort(CompareTransitionSweepSteps);
            return steps;
        }

        private static bool ShouldSkipTransitionSweepStep(NavigationGraph.PathStep step, TransitionSweepKind sweepKind)
        {
            if (step == null)
                return true;

            if (sweepKind == TransitionSweepKind.OpenPassage)
            {
                if (step.Kind != NavigationGraph.StepKind.OpenPassage &&
                    step.Kind != NavigationGraph.StepKind.Stairs &&
                    step.Kind != NavigationGraph.StepKind.Teleporter)
                {
                    return true;
                }

                return IsAtticSweepZone(step.FromZone) ||
                    IsAtticSweepZone(step.ToZone) ||
                    IsExcludedOpenTransitionSweepStep(step);
            }

            return step.Kind != NavigationGraph.StepKind.Door;
        }

        private static bool IsExcludedOpenTransitionSweepStep(NavigationGraph.PathStep step)
        {
            if (step == null || step.Kind != NavigationGraph.StepKind.Teleporter)
                return false;

            if (!string.Equals(step.ConnectorName, "CrawlspaceLadder", StringComparison.OrdinalIgnoreCase))
                return false;

            return
                (string.Equals(step.FromZone, "crawlspace", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(step.ToZone, "office", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(step.FromZone, "office", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(step.ToZone, "crawlspace", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsAtticSweepZone(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return false;

            return zoneName.IndexOf("attic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CompareTransitionSweepSteps(NavigationGraph.PathStep left, NavigationGraph.PathStep right)
        {
            int fromComparison = string.Compare(left != null ? left.FromZone : null, right != null ? right.FromZone : null, StringComparison.OrdinalIgnoreCase);
            if (fromComparison != 0)
                return fromComparison;

            int toComparison = string.Compare(left != null ? left.ToZone : null, right != null ? right.ToZone : null, StringComparison.OrdinalIgnoreCase);
            if (toComparison != 0)
                return toComparison;

            int kindComparison = string.Compare(left != null ? left.Kind.ToString() : null, right != null ? right.Kind.ToString() : null, StringComparison.OrdinalIgnoreCase);
            if (kindComparison != 0)
                return kindComparison;

            return string.Compare(
                left != null ? BuildNavigationStepKey(left) : null,
                right != null ? BuildNavigationStepKey(right) : null,
                StringComparison.Ordinal);
        }

        private bool TryTeleportTransitionSweepPlayer(
            NavigationGraph.PathStep step,
            bool useZoneFallback,
            out Vector3 spawnPosition,
            out string spawnSource)
        {
            spawnPosition = Vector3.zero;
            spawnSource = null;

            if (BetterPlayerControl.Instance == null)
                return false;

            if (!TryGetTransitionSweepSpawnPosition(step, useZoneFallback, out spawnPosition, out spawnSource))
                return false;

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            playerTransform.position = spawnPosition;

            Vector3 lookTarget = GetTransitionSweepLookTarget(step, spawnPosition);
            Vector3 lookDirection = lookTarget - spawnPosition;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.0001f)
                playerTransform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);

            ApplyNavigationInput(Vector3.zero, Vector3.zero);
            ResetAutoWalkProgress();
            return true;
        }

        private bool TryGetTransitionSweepSpawnPosition(
            NavigationGraph.PathStep step,
            bool useZoneFallback,
            out Vector3 spawnPosition,
            out string spawnSource)
        {
            spawnPosition = Vector3.zero;
            spawnSource = null;
            if (step == null)
                return false;

            if (_transitionSweepSession != null &&
                _transitionSweepSession.Kind == TransitionSweepKind.Door &&
                TryGetDoorTransitionSweepSpawnPosition(step, useZoneFallback, out spawnPosition, out spawnSource))
            {
                return true;
            }

            if (useZoneFallback && TryGetZonePosition(step.FromZone, out spawnPosition))
            {
                spawnSource = "zone";
                return true;
            }

            if (step.FromWaypoint != Vector3.zero)
            {
                spawnPosition = step.FromWaypoint;
                spawnSource = "from_waypoint";
                return true;
            }

            if (step.FromCrossingAnchor != Vector3.zero)
            {
                spawnPosition = step.FromCrossingAnchor;
                if (step.ToCrossingAnchor != Vector3.zero)
                {
                    Vector3 inwardDirection = step.FromCrossingAnchor - step.ToCrossingAnchor;
                    inwardDirection.y = 0f;
                    if (inwardDirection.sqrMagnitude > 0.0001f)
                        spawnPosition += inwardDirection.normalized * TransitionSweepCrossingFallbackOffset;
                }

                spawnSource = "from_crossing_anchor";
                return true;
            }

            if (TryGetZonePosition(step.FromZone, out spawnPosition))
            {
                spawnSource = "zone";
                return true;
            }

            return false;
        }

        private bool TryGetDoorTransitionSweepSpawnPosition(
            NavigationGraph.PathStep step,
            bool useZoneFallback,
            out Vector3 spawnPosition,
            out string spawnSource)
        {
            spawnPosition = Vector3.zero;
            spawnSource = null;

            if (step == null)
                return false;

            if (!useZoneFallback &&
                TryFindTransitionInteractableCandidate(step, out InteractableObj interactable))
            {
                spawnPosition = BuildDoorTransitionSweepStandClearPosition(step, interactable);
                spawnSource = "door_clearance";
                return true;
            }

            if (useZoneFallback && TryGetZonePosition(step.FromZone, out spawnPosition))
            {
                spawnSource = "zone";
                return true;
            }

            if (step.FromWaypoint != Vector3.zero)
            {
                spawnPosition = step.FromWaypoint;
                spawnSource = "from_waypoint";
                return true;
            }

            if (step.FromCrossingAnchor != Vector3.zero)
            {
                spawnPosition = step.FromCrossingAnchor;
                spawnSource = "from_crossing_anchor";
                return true;
            }

            if (TryGetZonePosition(step.FromZone, out spawnPosition))
            {
                spawnSource = "zone";
                return true;
            }

            return false;
        }

        private bool TryAttemptDoorTransitionSweepInteraction(NavigationGraph.PathStep step, out Vector3 pushThroughPosition)
        {
            pushThroughPosition = Vector3.zero;
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.Door ||
                BetterPlayerControl.Instance == null)
            {
                return false;
            }

            if (!TryFindTransitionInteractableCandidate(step, out InteractableObj interactable))
            {
                LogNavigationTransitionDebug(
                    "Door sweep interaction failed: no interactable candidate step=" + DescribeNavigationStep(step));
                return false;
            }

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool useOppositeLateralSide = attempt == 1;
                Vector3 interactionPosition = BuildDoorTransitionSweepStandClearPosition(step, interactable, useOppositeLateralSide);
                playerTransform.position = interactionPosition;

                Vector3 lookDirection = interactable.transform.position - interactionPosition;
                lookDirection.y = 0f;
                if (lookDirection.sqrMagnitude > 0.0001f)
                    playerTransform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);

                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                ResetAutoWalkProgress();

                if (!CanAutoInteractWithStep(step, interactable, out string interactionReason))
                {
                    if (IsAlreadyOpenInteractionReason(interactionReason))
                    {
                        pushThroughPosition = BuildDoorTraversalPushThroughPosition(step, interactable);
                        LogNavigationTransitionDebug(
                            "Door sweep interaction treating open door as ready alternateSide=" + useOppositeLateralSide +
                            " interactable=" + DescribeInteractable(interactable) +
                            " pushThroughPosition=" + FormatVector3(pushThroughPosition) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    LogNavigationTransitionDebug(
                        "Door sweep interaction skipped reason=" + interactionReason +
                        " alternateSide=" + useOppositeLateralSide +
                        " interactable=" + DescribeInteractable(interactable) +
                        " step=" + DescribeNavigationStep(step));
                    continue;
                }

                if (!TryTriggerNavigationTransitionInteraction(interactable))
                {
                    LogNavigationTransitionDebug(
                        "Door sweep interaction failed: trigger rejected alternateSide=" + useOppositeLateralSide +
                        " interactable=" + DescribeInteractable(interactable) +
                        " step=" + DescribeNavigationStep(step));
                    continue;
                }

                _lastNavigationInteractionAttemptTime = Time.unscaledTime;
                _autoWalkRecoveryAttempts = 0;
                ResetAutoWalkProgress();
                pushThroughPosition = BuildDoorTraversalPushThroughPosition(step, interactable);
                LogNavigationTransitionDebug(
                    "Door sweep interaction fired interactable=" + DescribeInteractable(interactable) +
                    " alternateSide=" + useOppositeLateralSide +
                    " player=" + FormatVector3(playerTransform.position) +
                    " pushThroughPosition=" + FormatVector3(pushThroughPosition) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            return false;
        }

        private static Vector3 BuildDoorTransitionSweepStandClearPosition(
            NavigationGraph.PathStep step,
            InteractableObj interactable,
            bool useOppositeLateralSide = false)
        {
            if (interactable == null)
                return Vector3.zero;

            Vector3 doorPosition = interactable.transform.position;
            Vector3 standClearDirection = Vector3.zero;
            if (step != null && step.SourceApproachPoint != Vector3.zero)
                standClearDirection = step.SourceApproachPoint - doorPosition;

            if (standClearDirection.sqrMagnitude <= 0.0001f && step != null && step.SourceClearPoint != Vector3.zero)
                standClearDirection = step.SourceClearPoint - doorPosition;

            if (standClearDirection.sqrMagnitude <= 0.0001f && step != null && step.FromWaypoint != Vector3.zero)
                standClearDirection = step.FromWaypoint - doorPosition;

            if (standClearDirection.sqrMagnitude <= 0.0001f && step != null && step.FromCrossingAnchor != Vector3.zero)
                standClearDirection = step.FromCrossingAnchor - doorPosition;

            if (standClearDirection.sqrMagnitude <= 0.0001f && step != null && step.DestinationClearPoint != Vector3.zero)
                standClearDirection = doorPosition - step.DestinationClearPoint;

            if (standClearDirection.sqrMagnitude <= 0.0001f && step != null && step.ToCrossingAnchor != Vector3.zero)
                standClearDirection = doorPosition - step.ToCrossingAnchor;

            if (standClearDirection.sqrMagnitude <= 0.0001f && step != null && step.DestinationApproachPoint != Vector3.zero)
                standClearDirection = doorPosition - step.DestinationApproachPoint;

            if (standClearDirection.sqrMagnitude <= 0.0001f && step != null && step.ToWaypoint != Vector3.zero)
                standClearDirection = doorPosition - step.ToWaypoint;

            standClearDirection.y = 0f;
            if (standClearDirection.sqrMagnitude <= 0.0001f)
                standClearDirection = -interactable.transform.forward;

            standClearDirection.y = 0f;
            if (standClearDirection.sqrMagnitude <= 0.0001f)
                return doorPosition;

            Vector3 normalizedClearDirection = standClearDirection.normalized;
            Vector3 lateralDirection = interactable.transform.right;
            lateralDirection.y = 0f;
            if (lateralDirection.sqrMagnitude <= 0.0001f)
                lateralDirection = Vector3.Cross(Vector3.up, normalizedClearDirection);
            lateralDirection.y = 0f;
            if (lateralDirection.sqrMagnitude > 0.0001f)
                lateralDirection.Normalize();

            if (step != null && step.SourceApproachPoint != Vector3.zero && lateralDirection.sqrMagnitude > 0.0001f)
            {
                Vector3 positiveCandidate = doorPosition +
                    normalizedClearDirection * DoorTraversalClearanceDistance +
                    lateralDirection * DoorTraversalLateralOffsetDistance;
                Vector3 negativeCandidate = doorPosition +
                    normalizedClearDirection * DoorTraversalClearanceDistance -
                    lateralDirection * DoorTraversalLateralOffsetDistance;
                lateralDirection *= Vector3.Distance(positiveCandidate, step.SourceApproachPoint) <= Vector3.Distance(negativeCandidate, step.SourceApproachPoint)
                    ? 1f
                    : -1f;
            }

            if (useOppositeLateralSide)
                lateralDirection *= -1f;

            Vector3 spawnPosition = doorPosition +
                normalizedClearDirection * DoorTraversalClearanceDistance +
                lateralDirection * DoorTraversalLateralOffsetDistance;
            spawnPosition.y = step != null && step.SourceApproachPoint != Vector3.zero
                ? step.SourceApproachPoint.y
                : doorPosition.y;
            return spawnPosition;
        }

        private static Vector3 BuildDoorTraversalPushThroughPosition(NavigationGraph.PathStep step, InteractableObj interactable)
        {
            if (TryBuildDoorTraversalThresholdPushThroughPosition(step, out Vector3 thresholdPushThroughPosition))
                return thresholdPushThroughPosition;

            if (interactable == null)
                return step != null
                    ? GetDoorTraversalDestinationTarget(step)
                    : Vector3.zero;

            Vector3 doorPosition = interactable.transform.position;
            Vector3 destinationDirection = Vector3.zero;
            if (step != null && step.DestinationClearPoint != Vector3.zero)
                destinationDirection = step.DestinationClearPoint - doorPosition;

            if (destinationDirection.sqrMagnitude <= 0.0001f && step != null && step.DestinationApproachPoint != Vector3.zero)
                destinationDirection = step.DestinationApproachPoint - doorPosition;

            if (destinationDirection.sqrMagnitude <= 0.0001f && step != null && step.ToWaypoint != Vector3.zero)
                destinationDirection = step.ToWaypoint - doorPosition;

            if (destinationDirection.sqrMagnitude <= 0.0001f && step != null && step.SourceApproachPoint != Vector3.zero)
                destinationDirection = doorPosition - step.SourceApproachPoint;

            if (destinationDirection.sqrMagnitude <= 0.0001f && step != null && step.FromWaypoint != Vector3.zero)
                destinationDirection = doorPosition - step.FromWaypoint;

            if (destinationDirection.sqrMagnitude <= 0.0001f)
                destinationDirection = interactable.transform.forward;

            destinationDirection.y = 0f;
            if (destinationDirection.sqrMagnitude <= 0.0001f)
                return step != null
                    ? GetDoorTraversalDestinationTarget(step)
                    : doorPosition;

            Vector3 flattenedDirection = destinationDirection.normalized;
            float desiredForwardDistance = DoorTraversalPushThroughDistance;
            float bestCandidateDistance = float.MaxValue;
            float bestCandidateForwardDistance = 0f;
            float pushThroughY = step != null && step.DestinationClearPoint != Vector3.zero
                ? step.DestinationClearPoint.y
                : doorPosition.y;

            if (step != null)
            {
                Vector3[] candidates =
                {
                    step.DestinationClearPoint,
                    step.DestinationApproachPoint,
                    step.ToWaypoint
                };

                for (int i = 0; i < candidates.Length; i++)
                {
                    Vector3 candidate = candidates[i];
                    if (candidate == Vector3.zero)
                        continue;

                    Vector3 candidateOffset = candidate - doorPosition;
                    candidateOffset.y = 0f;
                    float candidateDistance = candidateOffset.magnitude;
                    if (candidateDistance <= 0.0001f)
                        continue;

                    float candidateForwardDistance = Vector3.Dot(candidateOffset, flattenedDirection);
                    if (candidateForwardDistance <= 0.05f || candidateDistance >= bestCandidateDistance)
                        continue;

                    bestCandidateDistance = candidateDistance;
                    bestCandidateForwardDistance = candidateForwardDistance;
                    pushThroughY = candidate.y;
                }
            }

            if (bestCandidateDistance < float.MaxValue)
            {
                desiredForwardDistance = Mathf.Clamp(
                    bestCandidateForwardDistance,
                    DoorTraversalPushThroughDistance,
                    DoorTraversalMaximumPushThroughDistance);
            }

            Vector3 pushThroughPosition = doorPosition + flattenedDirection * desiredForwardDistance;
            pushThroughPosition.y = pushThroughY;
            return pushThroughPosition;
        }

        private static bool TryBuildDoorTraversalThresholdPushThroughPosition(
            NavigationGraph.PathStep step,
            out Vector3 pushThroughPosition)
        {
            pushThroughPosition = Vector3.zero;
            if (step == null)
                return false;

            Vector3 sourceReferencePosition = GetDoorTraversalSourceReferencePosition(step);
            if (sourceReferencePosition == Vector3.zero ||
                !TryGetDoorTraversalNearestDestinationTarget(step, sourceReferencePosition, out Vector3 destinationTarget))
            {
                return false;
            }

            Vector3 flattenedDirection = destinationTarget - sourceReferencePosition;
            flattenedDirection.y = 0f;
            float destinationDistance = flattenedDirection.magnitude;
            if (destinationDistance <= 0.0001f)
            {
                pushThroughPosition = destinationTarget;
                return true;
            }

            float pushThroughDistance = Mathf.Min(destinationDistance, DoorTraversalMaximumPushThroughDistance);
            pushThroughPosition = sourceReferencePosition + flattenedDirection / destinationDistance * pushThroughDistance;
            pushThroughPosition.y = destinationTarget.y != 0f
                ? destinationTarget.y
                : sourceReferencePosition.y;
            return true;
        }

        private static Vector3 GetDoorTraversalSourceReferencePosition(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (step.SourceClearPoint != Vector3.zero)
                return step.SourceClearPoint;

            if (step.SourceApproachPoint != Vector3.zero)
                return step.SourceApproachPoint;

            if (step.FromWaypoint != Vector3.zero)
                return step.FromWaypoint;

            if (step.FromCrossingAnchor != Vector3.zero)
                return step.FromCrossingAnchor;

            return Vector3.zero;
        }

        private static bool TryGetDoorTraversalNearestDestinationTarget(
            NavigationGraph.PathStep step,
            Vector3 sourceReferencePosition,
            out Vector3 destinationTarget)
        {
            destinationTarget = Vector3.zero;
            if (step == null || sourceReferencePosition == Vector3.zero)
                return false;

            float bestDistance = float.MaxValue;
            Vector3[] candidates =
            {
                step.DestinationClearPoint,
                step.DestinationApproachPoint,
                step.ToWaypoint
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 candidate = candidates[i];
                if (candidate == Vector3.zero)
                    continue;

                Vector3 flattenedOffset = candidate - sourceReferencePosition;
                flattenedOffset.y = 0f;
                float candidateDistance = flattenedOffset.magnitude;
                if (candidateDistance <= 0.05f || candidateDistance >= bestDistance)
                    continue;

                bestDistance = candidateDistance;
                destinationTarget = candidate;
            }

            return destinationTarget != Vector3.zero;
        }

        private static Vector3 GetDoorTraversalDestinationTarget(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            Vector3 sourceReferencePosition = GetDoorTraversalSourceReferencePosition(step);
            Vector3 bestTarget = Vector3.zero;
            float bestScore = float.NegativeInfinity;
            Vector3[] candidates =
            {
                step.DestinationClearPoint,
                step.DestinationApproachPoint,
                step.ToWaypoint
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 candidate = candidates[i];
                if (candidate == Vector3.zero)
                    continue;

                float score;
                if (sourceReferencePosition != Vector3.zero)
                {
                    Vector3 flattenedSource = sourceReferencePosition;
                    flattenedSource.y = candidate.y;
                    score = Vector3.Distance(flattenedSource, candidate);
                }
                else
                {
                    score = candidate.sqrMagnitude;
                }

                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestTarget = candidate;
            }

            return bestTarget;
        }

        private static bool TryGetDoorTraversalDestinationTarget(
            NavigationGraph.PathStep step,
            out Vector3 destinationTarget,
            out NavigationTargetKind targetKind)
        {
            destinationTarget = GetDoorTraversalDestinationTarget(step);
            if (destinationTarget == Vector3.zero)
            {
                targetKind = NavigationTargetKind.ZoneFallback;
                return false;
            }

            targetKind = step != null &&
                step.ToWaypoint != Vector3.zero &&
                destinationTarget == step.ToWaypoint
                ? NavigationTargetKind.EntryWaypoint
                : NavigationTargetKind.ZoneFallback;
            return true;
        }

        private Vector3 GetTransitionSweepLookTarget(NavigationGraph.PathStep step, Vector3 spawnPosition)
        {
            if (step == null)
                return spawnPosition + Vector3.forward;

            if (_transitionSweepSession != null &&
                _transitionSweepSession.Kind == TransitionSweepKind.Door &&
                TryFindTransitionInteractableCandidate(step, out InteractableObj interactable))
            {
                return interactable.transform.position;
            }

            if (step.ToCrossingAnchor != Vector3.zero)
                return step.ToCrossingAnchor;

            if (step.ToWaypoint != Vector3.zero)
                return step.ToWaypoint;

            if (TryGetZonePosition(step.ToZone, out Vector3 zonePosition))
                return zonePosition;

            return spawnPosition + Vector3.forward;
        }

        private bool TryFindTransitionInteractableCandidate(NavigationGraph.PathStep step, out InteractableObj interactable)
        {
            interactable = null;
            if (step == null)
                return false;

            InteractableObj[] candidates = FindObjectsOfType<InteractableObj>();
            if (candidates == null || candidates.Length == 0)
                return false;

            Vector3 referencePosition = GetTransitionSweepSourceReferencePosition(step);
            if (referencePosition == Vector3.zero && step.FromWaypoint != Vector3.zero)
                referencePosition = step.FromWaypoint;

            float bestScore = float.MaxValue;
            Vector3 routeStart = step.FromWaypoint;
            Vector3 routeEnd = step.ToWaypoint;
            for (int i = 0; i < candidates.Length; i++)
            {
                InteractableObj candidate = candidates[i];
                if (!IsMatchingTransitionInteractable(step, candidate))
                    continue;

                float score = referencePosition != Vector3.zero
                    ? Vector3.Distance(candidate.transform.position, referencePosition)
                    : 0f;
                if (routeStart != Vector3.zero && routeEnd != Vector3.zero)
                {
                    score += DistanceToSegment(candidate.transform.position, routeStart, routeEnd) * 4f;
                }

                if (IsMatchingNamedTransitionInteractable(step, candidate))
                {
                    score -= 100f;
                }

                if (score >= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 segmentStart, Vector3 segmentEnd)
        {
            Vector3 segment = segmentEnd - segmentStart;
            float segmentLengthSquared = segment.sqrMagnitude;
            if (segmentLengthSquared <= 0.0001f)
                return Vector3.Distance(point, segmentStart);

            float t = Mathf.Clamp01(Vector3.Dot(point - segmentStart, segment) / segmentLengthSquared);
            Vector3 projection = segmentStart + segment * t;
            return Vector3.Distance(point, projection);
        }

        private string BuildTransitionSweepLabel(NavigationGraph.PathStep step)
        {
            if (step == null)
                return "transition sweep";

            return (step.FromZone ?? "<null>") + " to " + (step.ToZone ?? "<null>");
        }

        private static string GetTransitionSweepOutputPath(TransitionSweepKind sweepKind)
        {
            return sweepKind == TransitionSweepKind.Door
                ? TransitionSweepReporter.GetDefaultDoorOutputPath()
                : TransitionSweepReporter.GetDefaultOutputPath();
        }

        private static string GetTransitionSweepLogLabel(TransitionSweepKind sweepKind)
        {
            return sweepKind == TransitionSweepKind.Door ? "Door transition sweep" : "Transition sweep";
        }

        private static string GetTransitionSweepStartedMessageKey(TransitionSweepKind sweepKind)
        {
            return sweepKind == TransitionSweepKind.Door ? "door_transition_sweep_started" : "transition_sweep_started";
        }

        private static string GetTransitionSweepCompleteMessageKey(TransitionSweepKind sweepKind)
        {
            return sweepKind == TransitionSweepKind.Door ? "door_transition_sweep_complete" : "transition_sweep_complete";
        }

        private static string GetTransitionSweepStoppedMessageKey(TransitionSweepKind sweepKind)
        {
            return sweepKind == TransitionSweepKind.Door ? "door_transition_sweep_stopped" : "transition_sweep_stopped";
        }

        private TransitionSweepReporter.MutableEntry GetCurrentTransitionSweepEntry()
        {
            if (_transitionSweepSession == null)
                return null;

            int index = _transitionSweepSession.CurrentIndex;
            if (index < 0 || index >= _transitionSweepSession.Entries.Count)
                return null;

            return _transitionSweepSession.Entries[index];
        }

        private void RecordTransitionSweepFailure(string failureReason)
        {
            if (_transitionSweepSession == null || _transitionSweepSession.CurrentStep == null)
                return;

            RecordTransitionSweepResult("failed", failureReason);
        }

        private void RecordTransitionSweepResult(string status, string failureReason)
        {
            if (_transitionSweepSession == null || _transitionSweepSession.CurrentStep == null)
                return;

            NavigationGraph.PathStep step = _transitionSweepSession.CurrentStep;
            TransitionSweepReporter.MutableEntry entry = GetCurrentTransitionSweepEntry();
            string currentZoneAtResult = GetCurrentZoneNameInternal();
            Vector3 playerPositionAtResult = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            string lastLocalNavigationContext = _localNavigationPathContext;
            string lastTargetKind = _lastTrackerTargetKind;
            Vector3 lastTargetPosition = _hasLastTrackerTarget ? _lastTrackerTargetPosition : Vector3.zero;
            if (entry != null)
            {
                string statusDetail = failureReason ?? status;
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    statusDetail +=
                        " localContext=" + (lastLocalNavigationContext ?? "<null>") +
                        " lastTargetKind=" + (lastTargetKind ?? "<null>");
                    if (_hasLastTrackerTarget)
                        statusDetail += " lastTarget=" + FormatVector3(lastTargetPosition);
                }

                entry.Status = status;
                entry.StatusDetail = statusDetail;
                entry.FailureReason = failureReason;
                entry.DurationSeconds = _transitionSweepSession.StepStartedAt > 0f
                    ? Mathf.Max(0f, Time.unscaledTime - _transitionSweepSession.StepStartedAt)
                    : 0f;
                entry.CurrentZoneAtResult = currentZoneAtResult;
                entry.PlayerPositionAtResult = playerPositionAtResult;
                entry.LastTargetKind = lastTargetKind;
                entry.LastTargetPosition = lastTargetPosition;
                entry.LastLocalNavigationContext = lastLocalNavigationContext;
            }

            Main.Log.LogInfo(
                "Transition sweep result status=" + status +
                " failureReason=" + (failureReason ?? "<null>") +
                " step=" + DescribeNavigationStep(step));

            _transitionSweepSession.CurrentStep = null;
            _transitionSweepSession.StepStartedAt = 0f;
            _transitionSweepSession.Phase = TransitionSweepPhase.AwaitingNextStep;
            _transitionSweepSession.NextActionTime = Time.unscaledTime + TransitionSweepStepSpacingSeconds;
            WriteTransitionSweepReport(isComplete: false);
        }

        private void WriteTransitionSweepReport(bool isComplete)
        {
            if (_transitionSweepSession == null)
                return;

            try
            {
                TransitionSweepReporter.WriteReport(
                    _transitionSweepSession.OutputPath,
                    _transitionSweepSession.Kind.ToString(),
                    isComplete,
                    _transitionSweepSession.Entries);
            }
            catch (Exception ex)
            {
                Main.Log.LogError("Failed to write transition sweep report: " + ex);
            }
        }

        private List<LiveRouteAuditReporter.MutableEntry> BuildLiveRouteAuditEntries(
            Dictionary<string, LiveRouteAuditReporter.EntryStatus> previousStatuses,
            out List<LiveRouteAuditRoute> routes,
            out int orderedPairCount,
            out int uniqueRouteCount)
        {
            routes = new List<LiveRouteAuditRoute>();
            orderedPairCount = 0;
            uniqueRouteCount = 0;

            List<string> zones = NavigationGraph.GetAllZones();
            if (zones == null || zones.Count < 2)
                return null;

            var entries = new List<LiveRouteAuditReporter.MutableEntry>();
            var groupedRoutes = new Dictionary<string, LiveRouteAuditRoute>(StringComparer.Ordinal);
            for (int sourceIndex = 0; sourceIndex < zones.Count; sourceIndex++)
            {
                string startZone = zones[sourceIndex];
                if (string.IsNullOrWhiteSpace(startZone))
                    continue;

                bool hasSpawnPosition = TryResolveLiveRouteAuditSpawnPosition(
                    startZone,
                    out Vector3 spawnPosition,
                    out string spawnSource,
                    out string spawnFailureReason);
                for (int targetIndex = 0; targetIndex < zones.Count; targetIndex++)
                {
                    string targetZone = zones[targetIndex];
                    if (string.IsNullOrWhiteSpace(targetZone) ||
                        string.Equals(startZone, targetZone, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    orderedPairCount++;
                    string entryKey = BuildLiveRouteAuditEntryKey(startZone, targetZone);
                    string aliasPair = BuildLiveRouteAuditPairLabel(startZone, targetZone);
                    if (!hasSpawnPosition)
                    {
                        entries.Add(CreateImmediateLiveRouteAuditFailureEntry(
                            entries.Count,
                            entryKey,
                            startZone,
                            targetZone,
                            aliasPair,
                            "No canonical live start position was available for the source zone. " +
                            (spawnFailureReason ?? "Start position resolution failed.")));
                        continue;
                    }

                    List<NavigationGraph.PathStep> pathSteps = NavigationGraph.FindPathSteps(
                        startZone,
                        targetZone,
                        spawnPosition,
                        null);
                    if (pathSteps == null)
                    {
                        entries.Add(CreateImmediateLiveRouteAuditFailureEntry(
                            entries.Count,
                            entryKey,
                            startZone,
                            targetZone,
                            aliasPair,
                            "No graph route connected the start zone to the target zone."));
                        continue;
                    }

                    string routeSignature = BuildLiveRouteAuditRouteSignature(pathSteps);
                    if (string.IsNullOrWhiteSpace(routeSignature))
                    {
                        entries.Add(CreateImmediateLiveRouteAuditFailureEntry(
                            entries.Count,
                            entryKey,
                            startZone,
                            targetZone,
                            aliasPair,
                            "The planned live route did not produce a stable signature."));
                        continue;
                    }

                    if (!groupedRoutes.TryGetValue(routeSignature, out LiveRouteAuditRoute route))
                    {
                        route = new LiveRouteAuditRoute
                        {
                            Key = entryKey,
                            RouteSignature = routeSignature,
                            StartZone = startZone,
                            TargetZone = targetZone,
                            TargetLabel = NormalizeIdentifierName(targetZone) ?? targetZone,
                            AliasPairs = null,
                            PathSteps = pathSteps,
                            SpawnPosition = spawnPosition,
                            SpawnSource = spawnSource,
                            TimeoutSeconds = BuildLiveRouteAuditTimeoutSeconds(pathSteps),
                            EntryIndex = -1
                        };
                        groupedRoutes[routeSignature] = route;
                        uniqueRouteCount++;
                    }

                    if (route.AliasPairs == null)
                        route.AliasPairs = new[] { aliasPair };
                    else if (Array.IndexOf(route.AliasPairs, aliasPair) < 0)
                    {
                        var aliasPairs = new List<string>(route.AliasPairs) { aliasPair };
                        route.AliasPairs = aliasPairs.ToArray();
                    }
                }
            }

            var orderedRoutes = new List<LiveRouteAuditRoute>(groupedRoutes.Values);
            orderedRoutes.Sort((left, right) =>
            {
                int startComparison = string.Compare(left.StartZone, right.StartZone, StringComparison.OrdinalIgnoreCase);
                if (startComparison != 0)
                    return startComparison;

                return string.Compare(left.TargetZone, right.TargetZone, StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < orderedRoutes.Count; i++)
            {
                LiveRouteAuditRoute route = orderedRoutes[i];
                var entry = new LiveRouteAuditReporter.MutableEntry
                {
                    Index = entries.Count,
                    Key = route.Key,
                    RouteSignature = route.RouteSignature,
                    StartZone = route.StartZone,
                    TargetZone = route.TargetZone,
                    AliasPairs = route.AliasPairs ?? Array.Empty<string>(),
                    StepIds = BuildLiveRouteAuditStepIds(route.PathSteps),
                    StepCount = route.PathSteps != null ? route.PathSteps.Count : 0,
                    Status = "pending",
                    StatusDetail = "pending",
                    FailureReason = null,
                    DurationSeconds = 0f,
                    SpawnSource = route.SpawnSource,
                    SpawnPosition = route.SpawnPosition,
                    ExpectedTimeoutSeconds = route.TimeoutSeconds,
                    CurrentZoneAtResult = null,
                    PlayerPositionAtResult = Vector3.zero,
                    LastTargetKind = null,
                    LastTargetPosition = Vector3.zero,
                    LastLocalNavigationContext = null
                };

                if (previousStatuses != null &&
                    previousStatuses.TryGetValue(entry.Key, out LiveRouteAuditReporter.EntryStatus priorStatus) &&
                    priorStatus != null &&
                    string.Equals(priorStatus.Status, "passed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(priorStatus.RouteSignature, entry.RouteSignature, StringComparison.Ordinal))
                {
                    entry.Status = "passed";
                    entry.StatusDetail = !string.IsNullOrWhiteSpace(priorStatus.StatusDetail)
                        ? priorStatus.StatusDetail
                        : "Passed on the current runtime build according to the previous live route audit.";
                    entry.FailureReason = priorStatus.FailureReason;
                }
                else
                {
                    route.EntryIndex = entry.Index;
                    routes.Add(route);
                }

                entries.Add(entry);
            }

            return entries;
        }

        private static LiveRouteAuditReporter.MutableEntry CreateImmediateLiveRouteAuditFailureEntry(
            int index,
            string key,
            string startZone,
            string targetZone,
            string aliasPair,
            string failureReason)
        {
            return new LiveRouteAuditReporter.MutableEntry
            {
                Index = index,
                Key = key,
                RouteSignature = null,
                StartZone = startZone,
                TargetZone = targetZone,
                AliasPairs = new[] { aliasPair },
                StepIds = Array.Empty<string>(),
                StepCount = 0,
                Status = "failed",
                StatusDetail = failureReason,
                FailureReason = failureReason,
                DurationSeconds = 0f,
                SpawnSource = null,
                SpawnPosition = Vector3.zero,
                ExpectedTimeoutSeconds = 0f,
                CurrentZoneAtResult = null,
                PlayerPositionAtResult = Vector3.zero,
                LastTargetKind = null,
                LastTargetPosition = Vector3.zero,
                LastLocalNavigationContext = null
            };
        }

        private static string BuildLiveRouteAuditEntryKey(string startZone, string targetZone)
        {
            return "route:" + (startZone ?? "<null>") + "->" + (targetZone ?? "<null>");
        }

        private static string BuildLiveRouteAuditPairLabel(string startZone, string targetZone)
        {
            return (startZone ?? "<null>") + " -> " + (targetZone ?? "<null>");
        }

        private string BuildLiveRouteAuditRouteSignature(List<NavigationGraph.PathStep> pathSteps)
        {
            if (pathSteps == null || pathSteps.Count == 0)
                return null;

            var signatureParts = new List<string>(pathSteps.Count);
            for (int i = 0; i < pathSteps.Count; i++)
            {
                NavigationGraph.PathStep step = pathSteps[i];
                string stepId = !string.IsNullOrWhiteSpace(step != null ? step.Id : null)
                    ? step.Id
                    : BuildNavigationStepKey(step);
                if (string.IsNullOrWhiteSpace(stepId))
                    return null;

                signatureParts.Add(stepId);
            }

            return string.Join("=>", signatureParts.ToArray());
        }

        private string[] BuildLiveRouteAuditStepIds(List<NavigationGraph.PathStep> pathSteps)
        {
            if (pathSteps == null || pathSteps.Count == 0)
                return Array.Empty<string>();

            var stepIds = new string[pathSteps.Count];
            for (int i = 0; i < pathSteps.Count; i++)
            {
                NavigationGraph.PathStep step = pathSteps[i];
                stepIds[i] = !string.IsNullOrWhiteSpace(step != null ? step.Id : null)
                    ? step.Id
                    : BuildNavigationStepKey(step);
            }

            return stepIds;
        }

        private bool TryResolveLiveRouteAuditSpawnPosition(
            string zoneName,
            out Vector3 spawnPosition,
            out string spawnSource,
            out string failureReason)
        {
            spawnPosition = Vector3.zero;
            spawnSource = null;
            failureReason = null;
            if (string.IsNullOrWhiteSpace(zoneName))
            {
                failureReason = "Source zone missing.";
                return false;
            }

            Vector3 zonePosition = Vector3.zero;
            bool hasZonePosition = TryGetZonePosition(zoneName, out zonePosition);
            if (hasZonePosition &&
                LocalNavigationMaps.TrySnapPositionToNearestWalkableCell(
                    zoneName,
                    zonePosition,
                    out Vector3 snappedZonePosition,
                    out _))
            {
                spawnPosition = snappedZonePosition;
                spawnSource = GetFlatDistance(zonePosition, snappedZonePosition) <= 0.05f ? "zone" : "snapped-zone";
                return true;
            }

            List<LocalNavigationMaps.WalkableComponentSummary> components = LocalNavigationMaps.GetWalkableComponents(zoneName);
            if (components != null && components.Count > 0)
            {
                int bestIndex = 0;
                if (hasZonePosition)
                {
                    float bestDistance = float.PositiveInfinity;
                    for (int i = 0; i < components.Count; i++)
                    {
                        float candidateDistance = GetFlatDistance(zonePosition, components[i].RepresentativeWorldPosition);
                        if (candidateDistance >= bestDistance)
                            continue;

                        bestDistance = candidateDistance;
                        bestIndex = i;
                    }
                }

                spawnPosition = components[bestIndex].RepresentativeWorldPosition;
                spawnSource = "component";
                return true;
            }

            if (hasZonePosition)
            {
                spawnPosition = zonePosition;
                spawnSource = "zone";
                return true;
            }

            failureReason = "No zone anchor or walkable component summary was available.";
            return false;
        }

        private float BuildLiveRouteAuditTimeoutSeconds(List<NavigationGraph.PathStep> pathSteps)
        {
            if (pathSteps == null || pathSteps.Count == 0)
                return LiveRouteAuditMinimumTimeoutSeconds;

            float timeoutSeconds = LiveRouteAuditRouteOverheadSeconds;
            for (int i = 0; i < pathSteps.Count; i++)
            {
                NavigationGraph.PathStep step = pathSteps[i];
                float stepTimeoutSeconds = step != null && step.Kind == NavigationGraph.StepKind.OpenPassage
                    ? GetOpenPassageTransitionOverrideTimeoutSeconds(step)
                    : step != null && step.ValidationTimeoutSeconds > 0f
                        ? Mathf.Max(step.ValidationTimeoutSeconds, TransitionSweepStepTimeoutSeconds)
                        : TransitionSweepStepTimeoutSeconds;
                timeoutSeconds += stepTimeoutSeconds;
                if (step != null)
                    timeoutSeconds += Mathf.Max(0f, step.TransitionWaitSeconds);
            }

            return Mathf.Max(timeoutSeconds, LiveRouteAuditMinimumTimeoutSeconds);
        }

        private bool TryTeleportLiveRouteAuditPlayer(
            LiveRouteAuditRoute route,
            out Vector3 spawnPosition,
            out string spawnSource)
        {
            spawnPosition = Vector3.zero;
            spawnSource = null;
            if (route == null || BetterPlayerControl.Instance == null)
                return false;

            spawnPosition = route.SpawnPosition;
            spawnSource = route.SpawnSource;
            if (spawnPosition == Vector3.zero &&
                !TryResolveLiveRouteAuditSpawnPosition(route.StartZone, out spawnPosition, out spawnSource, out _))
            {
                return false;
            }

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            playerTransform.position = spawnPosition;

            Vector3 lookTarget = GetLiveRouteAuditLookTarget(route, spawnPosition);
            Vector3 lookDirection = lookTarget - spawnPosition;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.0001f)
                playerTransform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);

            ApplyNavigationInput(Vector3.zero, Vector3.zero);
            ResetAutoWalkProgress();
            return true;
        }

        private Vector3 GetLiveRouteAuditLookTarget(LiveRouteAuditRoute route, Vector3 spawnPosition)
        {
            if (route == null || route.PathSteps == null || route.PathSteps.Count == 0)
                return spawnPosition + Vector3.forward;

            NavigationGraph.PathStep firstStep = route.PathSteps[0];
            if (firstStep.FromWaypoint != Vector3.zero)
                return firstStep.FromWaypoint;
            if (firstStep.ToWaypoint != Vector3.zero)
                return firstStep.ToWaypoint;
            if (TryGetZonePosition(route.TargetZone, out Vector3 zonePosition))
                return zonePosition;

            return spawnPosition + Vector3.forward;
        }

        private LiveRouteAuditReporter.MutableEntry GetCurrentLiveRouteAuditEntry()
        {
            if (_liveRouteAuditSession == null || _liveRouteAuditSession.CurrentRoute == null)
                return null;

            int entryIndex = _liveRouteAuditSession.CurrentRoute.EntryIndex;
            if (entryIndex < 0 || entryIndex >= _liveRouteAuditSession.Entries.Count)
                return null;

            return _liveRouteAuditSession.Entries[entryIndex];
        }

        private void RecordLiveRouteAuditFailure(string failureReason)
        {
            if (_liveRouteAuditSession == null || _liveRouteAuditSession.CurrentRoute == null)
                return;

            RecordLiveRouteAuditResult(
                "failed",
                failureReason,
                "Live room-to-room route failed while exercising normal navigation.");
        }

        private void RecordLiveRouteAuditResult(string status, string failureReason, string statusDetail)
        {
            if (_liveRouteAuditSession == null || _liveRouteAuditSession.CurrentRoute == null)
                return;

            LiveRouteAuditRoute route = _liveRouteAuditSession.CurrentRoute;
            LiveRouteAuditReporter.MutableEntry entry = GetCurrentLiveRouteAuditEntry();
            string currentZoneAtResult = GetCurrentZoneNameInternal();
            Vector3 playerPositionAtResult = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            string lastLocalNavigationContext = _localNavigationPathContext;
            string lastTargetKind = _lastTrackerTargetKind;
            Vector3 lastTargetPosition = _hasLastTrackerTarget ? _lastTrackerTargetPosition : Vector3.zero;
            if (entry != null)
            {
                entry.Status = status;
                entry.StatusDetail = statusDetail;
                entry.FailureReason = failureReason;
                entry.DurationSeconds = _liveRouteAuditSession.RouteStartedAt > 0f
                    ? Mathf.Max(0f, Time.unscaledTime - _liveRouteAuditSession.RouteStartedAt)
                    : 0f;
                entry.CurrentZoneAtResult = currentZoneAtResult;
                entry.PlayerPositionAtResult = playerPositionAtResult;
                entry.LastTargetKind = lastTargetKind;
                entry.LastTargetPosition = lastTargetPosition;
                entry.LastLocalNavigationContext = lastLocalNavigationContext;
            }

            Main.Log.LogInfo(
                "Live route audit result status=" + status +
                " failureReason=" + (failureReason ?? "<null>") +
                " key=" + (route.Key ?? "<null>") +
                " startZone=" + (route.StartZone ?? "<null>") +
                " targetZone=" + (route.TargetZone ?? "<null>"));

            _liveRouteAuditSession.CurrentRoute = null;
            _liveRouteAuditSession.RouteStartedAt = 0f;
            _liveRouteAuditSession.Phase = LiveRouteAuditPhase.AwaitingNextRoute;
            _liveRouteAuditSession.NextActionTime = Time.unscaledTime + LiveRouteAuditStepSpacingSeconds;
            WriteLiveRouteAuditReport(isComplete: false);
        }

        private void WriteLiveRouteAuditReport(bool isComplete)
        {
            if (_liveRouteAuditSession == null)
                return;

            try
            {
                LiveRouteAuditReporter.WriteReport(
                    _liveRouteAuditSession.OutputPath,
                    isComplete,
                    _liveRouteAuditSession.OrderedPairCount,
                    _liveRouteAuditSession.UniqueRouteCount,
                    _liveRouteAuditSession.Entries);
            }
            catch (Exception ex)
            {
                Main.Log.LogError("Failed to write live route audit report: " + ex);
            }
        }

        private static int CountLiveRouteAuditEntriesByStatus(List<LiveRouteAuditReporter.MutableEntry> entries, string status)
        {
            if (entries == null || string.IsNullOrWhiteSpace(status))
                return 0;

            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null &&
                    string.Equals(entries[i].Status, status, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        private bool ShouldPreserveForcedTransitionSweepStep(NavigationGraph.PathStep step)
        {
            if (_transitionSweepSession == null ||
                _transitionSweepSession.Phase != TransitionSweepPhase.Running ||
                _transitionSweepSession.CurrentStep == null ||
                step == null)
            {
                return false;
            }

            return string.Equals(
                BuildNavigationStepKey(step),
                BuildNavigationStepKey(_transitionSweepSession.CurrentStep),
                StringComparison.Ordinal);
        }

        private void UpdateNavigationState()
        {
            if (!_isNavigationActive)
            {
                if (ObjectTracker.IsTracking)
                    ObjectTracker.StopTracking();
                return;
            }

            if (HandlePendingNavigationTransition())
                return;

            string navigationUnavailableReason = GetNavigationUnavailableReason();
            if ((!CanUseNavigationNow() || string.IsNullOrEmpty(_navigationTargetZone)) &&
                !IsExpectedTeleporterSweepControlLock(GetCurrentNavigationStep(), navigationUnavailableReason))
            {
                if (_isAutoWalking)
                {
                    StopNavigationBlocked(
                        "navigation unavailable reason=" + navigationUnavailableReason +
                        " targetZone=" + (_navigationTargetZone ?? "<null>"));
                }
                else
                    StopNavigationRuntime();
                return;
            }

            if (NeedsNavigationPathRefresh(out string refreshReason))
            {
                LogNavigationAutoWalkDebug(
                    "Navigation path refresh requested reason=" + refreshReason +
                    " currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                    " targetZone=" + (_navigationTargetZone ?? "<null>") +
                    " targetLabel=" + (_navigationTargetLabel ?? "<null>"));
                if (!TryRefreshNavigationPath(forceAnnounce: false))
                {
                    if (IsZoneNavigationTargetReached(GetCurrentZoneNameInternal()))
                        return;

                    if (_isAutoWalking)
                        StopNavigationBlocked("path refresh failed after refresh request reason=" + refreshReason);
                    else
                        StopNavigationRuntime();
                    return;
                }
            }
            else
            {
                UpdateNavigationTracker();
            }

            if (IsTrackedObjectReached())
            {
                StopNavigationWithAnnouncement("navigation_arrived");
            }
        }

        private void ApplyAutoWalk()
        {
            if (!_isAutoWalking)
                return;

            if (HandlePendingNavigationTransition())
                return;

            string navigationUnavailableReason = GetNavigationUnavailableReason();
            if ((BetterPlayerControl.Instance == null || !CanUseNavigationNow()) &&
                !IsExpectedTeleporterSweepControlLock(GetCurrentNavigationStep(), navigationUnavailableReason))
            {
                LogNavigationAutoWalkDebug("Auto-walk blocked: navigation unavailable reason=" + navigationUnavailableReason);
                StopNavigationBlocked("auto-walk unavailable reason=" + navigationUnavailableReason);
                return;
            }

            NavigationGraph.PathStep currentStep = GetCurrentNavigationStep();
            if (!TryResolveAutoWalkMovementTarget("initial", out Vector3 nextPosition, out NavigationTargetKind targetKind))
            {
                LogNavigationAutoWalkDebug(
                    "Auto-walk missing next position step=" + DescribeNavigationStep(currentStep) +
                    " recoveryAttempts=" + _autoWalkRecoveryAttempts);
                if (!TryRecoverAutoWalk(currentStep, NavigationTargetKind.ZoneFallback))
                    StopNavigationBlocked("auto-walk could not resolve next movement target step=" + DescribeNavigationStep(currentStep));
                return;
            }

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            Vector3 toTarget = nextPosition - playerTransform.position;
            toTarget.y = 0f;

            float autoWalkArrivalDistance = GetAutoWalkArrivalDistance(targetKind);
            if (toTarget.sqrMagnitude <= autoWalkArrivalDistance * autoWalkArrivalDistance)
            {
                if (targetKind == NavigationTargetKind.LocalWaypoint)
                {
                    LogNavigationAutoWalkDebug(
                        "Auto-walk reached local waypoint target=" + FormatVector3(nextPosition) +
                        " step=" + DescribeNavigationStep(currentStep));
                    if (!TryResolveAutoWalkMovementTarget("post-local-waypoint", out nextPosition, out targetKind))
                    {
                        if (!TryRecoverAutoWalk(currentStep, targetKind))
                            StopNavigationBlocked("auto-walk could not resolve follow-up local waypoint target step=" + DescribeNavigationStep(currentStep));
                        return;
                    }

                    currentStep = GetCurrentNavigationStep();
                    toTarget = nextPosition - playerTransform.position;
                    toTarget.y = 0f;
                }

                if (targetKind == NavigationTargetKind.DirectObject)
                {
                    LogNavigationAutoWalkDebug(
                        "Auto-walk reached tracked object target=" + FormatVector3(nextPosition) +
                        " step=" + DescribeNavigationStep(currentStep));
                    StopNavigationWithAnnouncement("navigation_arrived");
                    return;
                }

                if (targetKind == NavigationTargetKind.TransitionInteractable)
                {
                    LogNavigationTransitionDebug(
                        "Auto-walk reached transition interactable target=" + FormatVector3(nextPosition) +
                        " step=" + DescribeNavigationStep(currentStep));
                    if (!TryAttemptTransitionInteraction(currentStep, allowOptionalDoorInteraction: false) &&
                        !TryRecoverAutoWalk(currentStep, targetKind))
                    {
                        StopNavigationBlocked("transition interaction failed at target step=" + DescribeNavigationStep(currentStep));
                    }

                    return;
                }

                if (targetKind == NavigationTargetKind.ExitWaypoint)
                {
                    LogNavigationAutoWalkDebug(
                        "Auto-walk reached exit waypoint target=" + FormatVector3(nextPosition) +
                        " step=" + DescribeNavigationStep(currentStep));
                    if (!TryResolveAutoWalkMovementTarget("post-exit-waypoint", out nextPosition, out targetKind))
                    {
                        if (!TryRecoverAutoWalk(currentStep, targetKind))
                            StopNavigationBlocked("auto-walk could not resolve follow-up exit waypoint target step=" + DescribeNavigationStep(currentStep));
                        return;
                    }
                }
                else if (targetKind == NavigationTargetKind.EntryWaypoint &&
                    currentStep != null &&
                    !IsCurrentZoneEquivalentTo(currentStep.ToZone))
                {
                    LogNavigationAutoWalkDebug(
                        "Auto-walk reached entry waypoint before zone advance target=" + FormatVector3(nextPosition) +
                        " step=" + DescribeNavigationStep(currentStep));
                    if (!TryResolveAutoWalkMovementTarget("post-entry-waypoint", out nextPosition, out targetKind))
                    {
                        if (!TryRecoverAutoWalk(currentStep, targetKind))
                            StopNavigationBlocked("auto-walk could not resolve entry waypoint continuation step=" + DescribeNavigationStep(currentStep));
                        return;
                    }
                }
                else if (!TryRefreshNavigationPath(forceAnnounce: true))
                {
                    if (IsZoneNavigationTargetReached(GetCurrentZoneNameInternal()))
                        return;

                    StopNavigationBlocked("auto-walk path refresh failed after reaching current target step=" + DescribeNavigationStep(currentStep));
                    return;
                }

                if (!TryResolveAutoWalkMovementTarget("post-refresh", out nextPosition, out targetKind))
                {
                    if (!TryRecoverAutoWalk(currentStep, targetKind))
                        StopNavigationBlocked("auto-walk could not resolve movement target after refresh step=" + DescribeNavigationStep(currentStep));
                    return;
                }

                currentStep = GetCurrentNavigationStep();
                toTarget = nextPosition - playerTransform.position;
                toTarget.y = 0f;
            }

            if (targetKind == NavigationTargetKind.TransitionInteractable)
            {
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                LogNavigationTransitionDebug(
                    "Auto-walk waiting at transition interactable target=" + FormatVector3(nextPosition) +
                    " step=" + DescribeNavigationStep(currentStep));
                if (!TryAttemptTransitionInteraction(currentStep, allowOptionalDoorInteraction: false) &&
                    Time.unscaledTime - _lastAutoWalkProgressTime >= AutoWalkBlockedTimeoutSeconds &&
                    !TryRecoverAutoWalk(currentStep, targetKind))
                {
                    StopNavigationBlocked("transition interaction timed out step=" + DescribeNavigationStep(currentStep));
                }

                return;
            }

            if (toTarget.sqrMagnitude <= 0.01f)
            {
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                return;
            }

            Vector3 direction = toTarget.normalized;
            Vector3 localDirection = playerTransform.InverseTransformDirection(direction);
            localDirection.y = 0f;

            float turnDegrees = Vector3.SignedAngle(playerTransform.forward, direction, Vector3.up);
            string currentZone = GetCurrentZoneNameForNavigation();
            Vector3 moveInput = new Vector3(
                Mathf.Clamp(localDirection.x, -1f, 1f),
                0f,
                Mathf.Clamp(localDirection.z, -1f, 1f));

            Vector3 lookInput = new Vector3(Mathf.Clamp(turnDegrees / AutoWalkLookScaleDegrees, -1f, 1f), 0f, 0f);
            if (!ApplyNavigationInput(moveInput, lookInput))
            {
                LogNavigationAutoWalkDebug(
                    "Auto-walk input application failed targetKind=" + targetKind +
                    " move=" + FormatVector3(moveInput) +
                    " look=" + FormatVector3(lookInput) +
                    " step=" + DescribeNavigationStep(currentStep));
                if (!TryRecoverAutoWalk(currentStep, targetKind))
                    StopNavigationBlocked(
                        "auto-walk input application failed targetKind=" + targetKind +
                        " step=" + DescribeNavigationStep(currentStep));
                return;
            }

            if (Vector3.Distance(playerTransform.position, _lastAutoWalkPosition) >= AutoWalkProgressDistance)
            {
                _lastAutoWalkPosition = playerTransform.position;
                _lastAutoWalkProgressTime = Time.unscaledTime;
                _autoWalkRecoveryAttempts = 0;
                ClearNavigationBlockedDetail();
            }
            else if (Time.unscaledTime - _lastAutoWalkProgressTime >=
                GetAutoWalkBlockedTimeoutSeconds(currentStep, currentZone, targetKind, nextPosition))
            {
                LogNavigationAutoWalkDebug(
                    "Auto-walk progress timeout targetKind=" + targetKind +
                    " nextPosition=" + FormatVector3(nextPosition) +
                    " player=" + FormatVector3(playerTransform.position) +
                    " step=" + DescribeNavigationStep(currentStep));
                if (!TryRecoverAutoWalk(currentStep, targetKind))
                    StopNavigationBlocked(
                        "auto-walk progress timeout targetKind=" + targetKind +
                        " step=" + DescribeNavigationStep(currentStep));
            }
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

            if (!TryRefreshNavigationPath(forceAnnounce: true))
            {
                string currentZone = GetCurrentZoneNameInternal();
                if (IsZoneNavigationTargetReached(currentZone))
                {
                    return false;
                }

                SetNavigationBlockedDetail(
                    "begin navigation failed after path refresh currentZone=" + (currentZone ?? "<null>") +
                    " targetZone=" + (_navigationTargetZone ?? "<null>"));
                if (announceFailure)
                    StopNavigationBlocked();
                else
                    StopNavigationRuntime();
                return false;
            }

            ResetAutoWalkProgress();
            return true;
        }

        private bool TryRefreshNavigationPath(bool forceAnnounce)
        {
            string currentZone = GetCurrentZoneNameForNavigation();
            if (string.IsNullOrEmpty(currentZone))
            {
                SetNavigationBlockedDetail("path refresh failed: current zone unavailable");
                LogNavigationAutoWalkDebug("TryRefreshNavigationPath failed: current zone unavailable.");
                return false;
            }

            NavigationGraph.PathStep forcedTransitionSweepStep = GetCurrentNavigationStep();
            if (ShouldPreserveForcedTransitionSweepStep(forcedTransitionSweepStep))
            {
                if (IsExactZoneMatch(currentZone, _navigationTargetZone))
                {
                    StopNavigationWithAnnouncement("navigation_arrived");
                    return false;
                }

                _navigationPath = new List<NavigationGraph.PathStep> { forcedTransitionSweepStep };
                _isNavigationActive = true;
                _autoWalkTransitionUntil = 0f;
                ClearNavigationBlockedDetail();
                UpdateNavigationTracker();
                LogNavigationAutoWalkDebug(
                    "TryRefreshNavigationPath preserved forced transition sweep step currentZone=" + currentZone +
                    " step=" + DescribeNavigationStep(forcedTransitionSweepStep));
                return true;
            }

            if (TryGetTrackedInteractable(out InteractableObj trackedInteractableTarget) &&
                TryGetTrackedInteractableZone(trackedInteractableTarget, out string trackedTargetZone))
            {
                _navigationTargetZone = trackedTargetZone;
                string trackedTargetLabel = GetTrackedInteractableLabel(trackedInteractableTarget);
                if (!string.IsNullOrEmpty(trackedTargetLabel))
                    _navigationTargetLabel = trackedTargetLabel;
            }

            if (string.IsNullOrEmpty(_navigationTargetZone))
            {
                SetNavigationBlockedDetail("path refresh failed: navigation target zone unavailable");
                LogNavigationAutoWalkDebug("TryRefreshNavigationPath failed: navigation target zone unavailable.");
                return false;
            }

            NavigationGraph.PathStep currentStepBeforeRefresh = GetCurrentNavigationStep();
            if (currentStepBeforeRefresh != null &&
                currentStepBeforeRefresh.Kind == NavigationGraph.StepKind.OpenPassage &&
                IsCommittedOpenPassageTraversal(currentStepBeforeRefresh) &&
                ShouldAdvanceOpenPassageStepByGeometry(currentStepBeforeRefresh) &&
                !string.IsNullOrEmpty(currentStepBeforeRefresh.ToZone))
            {
                currentZone = currentStepBeforeRefresh.ToZone;
                LogNavigationAutoWalkDebug(
                    "TryRefreshNavigationPath using committed open-passage destination zone currentZone=" + currentZone +
                    " step=" + DescribeNavigationStep(currentStepBeforeRefresh));
            }

            bool hasTrackedNavigationMode = TryResolveTrackedNavigationMode(
                currentZone,
                out InteractableObj trackedInteractable,
                out _,
                out _,
                out TrackedNavigationMode trackedNavigationMode,
                out Vector3 trackedAnchorPosition,
                out string trackedAnchorReason);

            if (hasTrackedNavigationMode &&
                trackedNavigationMode == TrackedNavigationMode.DirectObject)
            {
                ActivatePathlessTrackedNavigationState();

                if (!AreZonesEquivalent(_lastNavigationNextZone, currentZone) ||
                    !string.Equals(_lastNavigationAnnouncementLabel, _navigationTargetLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _lastNavigationNextZone = currentZone;
                    _lastNavigationAnnouncementLabel = _navigationTargetLabel;
                    ScreenReader.Say(Loc.Get("navigation_tracking", _navigationTargetLabel), interrupt: false);
                }

                LogNavigationAutoWalkDebug(
                    "TryRefreshNavigationPath direct-object tracking currentZone=" + currentZone +
                    " targetZone=" + _navigationTargetZone +
                    " targetLabel=" + (_navigationTargetLabel ?? "<null>"));
                UpdateNavigationTracker();
                return true;
            }

            if (hasTrackedNavigationMode &&
                trackedNavigationMode == TrackedNavigationMode.EquivalentZoneAnchor)
            {
                ActivatePathlessTrackedNavigationState();

                if (!string.Equals(_lastNavigationNextZone, _navigationTargetZone, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_lastNavigationAnnouncementLabel, _navigationTargetLabel, StringComparison.OrdinalIgnoreCase))
                {
                    _lastNavigationNextZone = _navigationTargetZone;
                    _lastNavigationAnnouncementLabel = _navigationTargetLabel;
                    ScreenReader.Say(Loc.Get("navigation_navigating", _navigationTargetLabel, BuildNavigationTargetLabel(_navigationTargetZone, currentZone)), interrupt: false);
                }

                LogNavigationAutoWalkDebug(
                    "TryRefreshNavigationPath same-navigation-zone fallback currentZone=" + currentZone +
                    " targetZone=" + _navigationTargetZone +
                    " targetZonePosition=" + FormatVector3(trackedAnchorPosition) +
                    " reason=" + (trackedAnchorReason ?? "<null>") +
                    " tracked=" + DescribeInteractable(trackedInteractable));
                UpdateNavigationTracker();
                return true;
            }

            if (IsExactZoneMatch(currentZone, _navigationTargetZone))
            {
                StopNavigationWithAnnouncement("navigation_arrived");
                return false;
            }

            string currentNavigationZone = GetNavigationZoneName(currentZone);
            string targetNavigationZone = GetNavigationZoneName(_navigationTargetZone);
            if (string.IsNullOrEmpty(currentNavigationZone) || string.IsNullOrEmpty(targetNavigationZone))
            {
                SetNavigationBlockedDetail(
                    "path refresh failed: navigation-zone normalization currentZone=" + (currentZone ?? "<null>") +
                    " targetZone=" + (_navigationTargetZone ?? "<null>"));
                LogNavigationAutoWalkDebug(
                    "TryRefreshNavigationPath failed: navigation-zone normalization failed currentZone=" + currentZone +
                    " currentNavigationZone=" + (currentNavigationZone ?? "<null>") +
                    " targetZone=" + (_navigationTargetZone ?? "<null>") +
                    " targetNavigationZone=" + (targetNavigationZone ?? "<null>"));
                return false;
            }

            if (!TryGetTrackedInteractable(out _) &&
                string.Equals(currentNavigationZone, targetNavigationZone, StringComparison.OrdinalIgnoreCase))
            {
                StopNavigationWithAnnouncement("navigation_arrived");
                return false;
            }

            if (!TryFindPreferredNavigationPath(currentNavigationZone, targetNavigationZone, out List<NavigationGraph.PathStep> path))
                path = null;
            if (path == null || path.Count < 1)
            {
                SetNavigationBlockedDetail(
                    "path refresh failed: no graph path currentNavigationZone=" + currentNavigationZone +
                    " targetNavigationZone=" + targetNavigationZone);
                LogNavigationAutoWalkDebug(
                    "TryRefreshNavigationPath failed: no path currentNavigationZone=" + currentNavigationZone +
                    " targetNavigationZone=" + targetNavigationZone);
                return false;
            }

            string previousOpenPassageStepKey = _openPassageTraversalStepKey;
            OpenPassageTraversalStage previousOpenPassageTraversalStage = _openPassageTraversalStage;
            string previousDoorTraversalStepKey = _doorTraversalStepKey;
            bool previousDoorTraversalInteractionTriggered = _doorTraversalInteractionTriggered;
            Vector3 previousDoorTraversalPushThroughPosition = _doorTraversalPushThroughPosition;
            bool previousDoorTraversalPostThresholdCommitted = _doorTraversalPostThresholdCommitted;
            _navigationPath = path;
            NavigationGraph.PathStep refreshedFirstStep = path[0];
            if (refreshedFirstStep != null &&
                refreshedFirstStep.Kind == NavigationGraph.StepKind.OpenPassage)
            {
                string refreshedFirstStepKey = BuildNavigationStepKey(refreshedFirstStep);
                if (!string.IsNullOrEmpty(previousOpenPassageStepKey) &&
                    string.Equals(previousOpenPassageStepKey, refreshedFirstStepKey, StringComparison.Ordinal))
                {
                    _openPassageTraversalStepKey = previousOpenPassageStepKey;
                    _openPassageTraversalStage = previousOpenPassageTraversalStage;
                }
                else
                {
                    _openPassageTraversalStepKey = refreshedFirstStepKey;
                    _openPassageTraversalStage = OpenPassageTraversalStage.SourceWaypoint;
                }
            }
            else if (refreshedFirstStep != null &&
                refreshedFirstStep.Kind == NavigationGraph.StepKind.Door)
            {
                string refreshedFirstStepKey = BuildNavigationStepKey(refreshedFirstStep);
                if (!string.IsNullOrEmpty(previousDoorTraversalStepKey) &&
                    string.Equals(previousDoorTraversalStepKey, refreshedFirstStepKey, StringComparison.Ordinal))
                {
                    _doorTraversalStepKey = previousDoorTraversalStepKey;
                    _doorTraversalInteractionTriggered = previousDoorTraversalInteractionTriggered;
                    _doorTraversalPushThroughPosition = previousDoorTraversalPushThroughPosition;
                    _doorTraversalPostThresholdCommitted = previousDoorTraversalPostThresholdCommitted;
                }
                else
                {
                    _doorTraversalStepKey = refreshedFirstStepKey;
                    _doorTraversalInteractionTriggered = false;
                    _doorTraversalPushThroughPosition = Vector3.zero;
                    _doorTraversalPostThresholdCommitted = false;
                }
            }
            else
            {
                ResetOpenPassageTraversalState();
                ResetDoorTraversalState();
            }
            SyncNavigationZoneOverride(currentZone, refreshedFirstStep);
            _isNavigationActive = true;
            _autoWalkTransitionUntil = 0f;
            _autoWalkRecoveryAttempts = 0;
            ClearNavigationBlockedDetail();

            string nextZone = path[0].ToZone;
            if (!string.Equals(nextZone, _lastNavigationNextZone, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_lastNavigationAnnouncementLabel, _navigationTargetLabel, StringComparison.OrdinalIgnoreCase))
            {
                _lastNavigationNextZone = nextZone;
                _lastNavigationAnnouncementLabel = _navigationTargetLabel;
                    ScreenReader.Say(Loc.Get("navigation_navigating", _navigationTargetLabel, BuildNavigationTargetLabel(nextZone, currentZone)), interrupt: false);
            }

            LogNavigationAutoWalkDebug(
                "TryRefreshNavigationPath success currentZone=" + currentZone +
                " targetZone=" + _navigationTargetZone +
                " targetLabel=" + (_navigationTargetLabel ?? "<null>") +
                " path=" + DescribeNavigationPath(path));
            UpdateNavigationTracker();

            return true;
        }

        private bool TryFindPreferredNavigationPath(
            string currentNavigationZone,
            string targetNavigationZone,
            out List<NavigationGraph.PathStep> path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(currentNavigationZone) || string.IsNullOrWhiteSpace(targetNavigationZone))
                return false;

            Vector3? playerPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : (Vector3?)null;
            Vector3? targetPosition = null;
            if (TryGetTrackedInteractable(out InteractableObj trackedInteractable))
                targetPosition = trackedInteractable.transform.position;

            path = NavigationGraph.FindPathSteps(currentNavigationZone, targetNavigationZone, playerPosition, targetPosition);
            return path != null;
        }

        private void StopNavigationWithAnnouncement(string messageKey)
        {
            if (string.Equals(messageKey, "navigation_blocked", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_lastNavigationBlockedDetail))
            {
                LogNavigationAutoWalkDebug("StopNavigationWithAnnouncement blocked detail=" + _lastNavigationBlockedDetail);
            }

            bool suppressAnnouncement =
                TryHandleTransitionSweepNavigationOutcome(messageKey) ||
                TryHandleLiveRouteAuditNavigationOutcome(messageKey);
            StopNavigationRuntime();
            if (!suppressAnnouncement)
                ScreenReader.Say(Loc.Get(messageKey));
        }

        private bool TryHandleTransitionSweepNavigationOutcome(string messageKey)
        {
            if (_transitionSweepSession == null ||
                _transitionSweepSession.Phase != TransitionSweepPhase.Running ||
                _transitionSweepSession.CurrentStep == null)
            {
                return false;
            }

            if (string.Equals(messageKey, "navigation_arrived", StringComparison.Ordinal))
            {
                RecordTransitionSweepResult("passed", null);
                return true;
            }

            if (string.Equals(messageKey, "navigation_blocked", StringComparison.Ordinal))
            {
                string blockedDetail = _lastNavigationBlockedDetail;
                string currentZone = GetCurrentZoneNameInternal();
                RecordTransitionSweepResult(
                    "failed",
                    "navigation blocked currentZone=" + (currentZone ?? "<null>") +
                    " detail=" + (blockedDetail ?? "<null>") +
                    " step=" + DescribeNavigationStep(_transitionSweepSession.CurrentStep));
                return true;
            }

            return false;
        }

        private bool TryHandleLiveRouteAuditNavigationOutcome(string messageKey)
        {
            if (_liveRouteAuditSession == null ||
                _liveRouteAuditSession.Phase != LiveRouteAuditPhase.Running ||
                _liveRouteAuditSession.CurrentRoute == null)
            {
                return false;
            }

            if (string.Equals(messageKey, "navigation_arrived", StringComparison.Ordinal))
            {
                RecordLiveRouteAuditResult(
                    "passed",
                    null,
                    "Arrived at the target room via normal live navigation.");
                return true;
            }

            if (string.Equals(messageKey, "navigation_blocked", StringComparison.Ordinal))
            {
                string blockedDetail = _lastNavigationBlockedDetail;
                string currentZone = GetCurrentZoneNameInternal();
                RecordLiveRouteAuditResult(
                    "failed",
                    "navigation blocked currentZone=" + (currentZone ?? "<null>") +
                    " detail=" + (blockedDetail ?? "<null>") +
                    " routeKey=" + (_liveRouteAuditSession.CurrentRoute.Key ?? "<null>"),
                    "Live room-to-room route blocked while exercising normal navigation.");
                return true;
            }

            return false;
        }

        private void StopNavigationRuntime()
        {
            LogNavigationAutoWalkDebug(
                "StopNavigationRuntime targetZone=" + (_navigationTargetZone ?? "<null>") +
                " targetLabel=" + (_navigationTargetLabel ?? "<null>") +
                " autoWalk=" + _isAutoWalking +
                " path=" + DescribeNavigationPath(_navigationPath));
            _isNavigationActive = false;
            _isAutoWalking = false;
            _navigationPath = null;
            ResetOpenPassageTraversalState();
            ResetDoorTraversalState();
            ClearNavigationZoneOverride();
            ClearLocalNavigationPathState();
            _lastNavigationNextZone = null;
            _lastNavigationAnnouncementLabel = null;
            _lastAutoWalkProgressTime = 0f;
            _lastNavigationInteractionAttemptTime = 0f;
            _autoWalkTransitionUntil = 0f;
            _autoWalkRecoveryAttempts = 0;
            _lastNavigationTargetDebugSnapshot = null;
            _lastNavigationTrackerDebugSnapshot = null;
            _lastNavigationAutoWalkDebugSnapshot = null;
            _lastNavigationTransitionDebugSnapshot = null;
            _lastNavigationBlockedDetail = null;
            ClearTrackerTargetDiagnostics();
            ObjectTracker.StopTracking();
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

            if (!TryResolveTutorialObjectiveKind(out TutorialObjectiveKind objectiveKind) ||
                objectiveKind == TutorialObjectiveKind.None)
            {
                DebugLogger.Log(LogCategory.State, "AccessibilityWatcher", "Objective resolve failed: no tutorial objective kind. signpostText=" + (objectiveText ?? "<null>"));
                return false;
            }

            if (!TryFindTutorialObjectiveInteractable(objectiveKind, out interactable) ||
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

            targetLabel = GetTrackedInteractableLabel(interactable);
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

        private static bool TryFindNearestDateableInteractable(bool requireUnmet, bool requireUnrealized, out InteractableObj interactable)
        {
            interactable = null;

            Save save = Singleton<Save>.Instance;
            if (save == null || BetterPlayerControl.Instance == null)
                return false;

            string currentZone = GetCurrentZoneNameInternal();
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
                if (TryGetZoneNameForInteractable(candidate, out string candidateZone) &&
                    AreZonesEquivalent(candidateZone, currentZone))
                {
                    score += 50f;
                }

                score -= Vector3.Distance(playerPosition, candidate.transform.position);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            return interactable != null;
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
                case TutorialObjectiveKind.HouseExit:
                    EnsureReflectionCache();
                    anchorObject = _tutorialFrontDoorField != null && TutorialController.Instance != null
                        ? _tutorialFrontDoorField.GetValue(TutorialController.Instance) as GameObject
                        : null;
                    break;
            }

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

        private static float ScoreTutorialObjectiveInteractable(TutorialObjectiveKind objectiveKind, InteractableObj interactable)
        {
            if (interactable == null)
                return 0f;

            string internalName = NormalizeText(interactable.InternalName());
            string objectLabel = GetObjectFacingDisplayName(interactable);
            string sceneName = NormalizeIdentifierName(interactable.name);
            string knownName = GetKnownDateableDisplayName(interactable);
            string currentZone = GetCurrentZoneNameInternal();
            bool isCurrentZone = TryGetZoneNameForInteractable(interactable, out string objectZone) &&
                AreZonesEquivalent(objectZone, currentZone);

            float score = isCurrentZone ? 5f : 0f;

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

        private bool TryGetNextNavigationPosition(out Vector3 position, out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;
            _rawNavigationTargetContext = null;
            string currentZone = GetCurrentZoneNameForNavigation();

            if (TryResolveTrackedNavigationMode(
                    currentZone,
                    out InteractableObj trackedInteractable,
                    out string trackedZone,
                    out _,
                    out TrackedNavigationMode trackedNavigationMode,
                    out Vector3 trackedAnchorPosition,
                    out string trackedAnchorReason))
            {
                if (trackedNavigationMode == TrackedNavigationMode.DirectObject)
                {
                    if (BetterPlayerControl.Instance != null &&
                        TryGetTrackedInteractableNavigationTarget(
                            trackedInteractable,
                            currentZone,
                            BetterPlayerControl.Instance.transform.position,
                            out Vector3 trackedTargetPosition,
                            out string trackedTargetDetail))
                    {
                        position = trackedTargetPosition;
                        targetKind = NavigationTargetKind.DirectObject;
                        LogNavigationTrackerDebug(
                            "Next navigation target kind=DirectObject position=" + FormatVector3(position) +
                            " trackedZone=" + trackedZone +
                            " reason=exact-zone-match" +
                            " detail=" + (trackedTargetDetail ?? "<null>") +
                            " interactable=" + DescribeInteractable(trackedInteractable));
                        return true;
                    }

                    position = trackedInteractable.transform.position;
                    targetKind = NavigationTargetKind.DirectObject;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=DirectObject position=" + FormatVector3(position) +
                        " trackedZone=" + trackedZone +
                        " reason=exact-zone-match" +
                        " interactable=" + DescribeInteractable(trackedInteractable));
                    return true;
                }

                if (trackedNavigationMode == TrackedNavigationMode.EquivalentZoneAnchor)
                {
                    position = trackedAnchorPosition;
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " trackedZone=" + trackedZone +
                        " reason=" + (trackedAnchorReason ?? "<null>") +
                        " interactable=" + DescribeInteractable(trackedInteractable));
                    return true;
                }
            }

            if (_navigationPath == null || _navigationPath.Count < 1)
            {
                SetNavigationBlockedDetail("next navigation target unavailable: navigation path empty");
                LogNavigationTrackerDebug("Next navigation target unavailable: navigation path empty.");
                return false;
            }

            NavigationGraph.PathStep step = _navigationPath[0];
            if (step == null)
            {
                SetNavigationBlockedDetail("next navigation target unavailable: current step was null");
                LogNavigationTrackerDebug("Next navigation target unavailable: current step was null.");
                return false;
            }

            if (BetterPlayerControl.Instance != null)
            {
                Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;

                if (TryGetDoorTransitionSweepNavigationTarget(step, currentZone, playerPosition, out position, out targetKind))
                    return true;

                if (step.Kind == NavigationGraph.StepKind.Door &&
                    TryGetDoorTraversalNavigationTarget(step, currentZone, playerPosition, out position, out targetKind))
                {
                    return true;
                }

                if (TryGetOpenPassageNavigationTarget(step, currentZone, playerPosition, out position, out targetKind))
                    return true;

                if (!string.IsNullOrEmpty(step.FromZone) &&
                    IsZoneEquivalentToNavigationZone(currentZone, step.FromZone))
                {
                    Vector3 fromWaypoint = step.FromWaypoint;
                    fromWaypoint.y = playerPosition.y;
                    float fromDistance = Vector3.Distance(playerPosition, fromWaypoint);
                    float toDistance = float.MaxValue;
                    if (step.ToWaypoint != Vector3.zero)
                    {
                        Vector3 toWaypoint = step.ToWaypoint;
                        toWaypoint.y = playerPosition.y;
                        toDistance = Vector3.Distance(playerPosition, toWaypoint);
                    }

                    bool shouldPreferEntryWaypoint = step.ToWaypoint != Vector3.zero &&
                        !step.RequiresInteraction &&
                        (fromDistance <= AutoWalkArrivalDistance ||
                         toDistance + 0.5f < fromDistance);

                    if (shouldPreferEntryWaypoint)
                    {
                        position = step.ToWaypoint;
                        targetKind = NavigationTargetKind.EntryWaypoint;
                        LogNavigationTrackerDebug(
                            "Next navigation target kind=EntryWaypoint position=" + FormatVector3(position) +
                            " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " toDistance=" + toDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    if (!shouldPreferEntryWaypoint && fromDistance > AutoWalkArrivalDistance)
                    {
                        position = step.FromWaypoint;
                        targetKind = NavigationTargetKind.ExitWaypoint;
                        LogNavigationTrackerDebug(
                            "Next navigation target kind=ExitWaypoint position=" + FormatVector3(position) +
                            " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " toDistance=" + toDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }

                    if (step.RequiresInteraction)
                    {
                        position = step.FromWaypoint;
                        targetKind = NavigationTargetKind.TransitionInteractable;
                        LogNavigationTrackerDebug(
                            "Next navigation target kind=TransitionInteractable position=" + FormatVector3(position) +
                            " step=" + DescribeNavigationStep(step));
                        return true;
                    }
                }
            }

            if (step.ToWaypoint != Vector3.zero)
            {
                if (!string.IsNullOrEmpty(step.ToZone) &&
                    !string.IsNullOrEmpty(step.FromZone) &&
                    !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) &&
                    !IsZoneEquivalentToNavigationZone(currentZone, step.ToZone) &&
                    TryGetZonePosition(step.ToZone, out position))
                {
                    targetKind = NavigationTargetKind.ZoneFallback;
                    LogNavigationTrackerDebug(
                        "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                position = step.ToWaypoint;
                targetKind = NavigationTargetKind.EntryWaypoint;
                LogNavigationTrackerDebug(
                    "Next navigation target kind=EntryWaypoint position=" + FormatVector3(position) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (TryGetZonePosition(step.ToZone, out position))
            {
                targetKind = NavigationTargetKind.ZoneFallback;
                LogNavigationTrackerDebug(
                    "Next navigation target kind=ZoneFallback position=" + FormatVector3(position) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            SetNavigationBlockedDetail("next navigation target unavailable: no waypoint or fallback zone for step=" + DescribeNavigationStep(step));
            LogNavigationTrackerDebug("Next navigation target unavailable: no waypoint or fallback zone for step=" + DescribeNavigationStep(step));
            return false;
        }

        private NavigationGraph.PathStep GetCurrentNavigationStep()
        {
            if (_navigationPath == null || _navigationPath.Count < 1)
                return null;

            return _navigationPath[0];
        }

        private bool TryGetDoorTransitionSweepNavigationTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            return TryGetDoorTransitionSweepNavigationTargetCore(
                step,
                currentZone,
                playerPosition,
                out position,
                out targetKind);
        }

        private bool TryGetDoorTraversalNavigationTarget(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            out Vector3 position,
            out NavigationTargetKind targetKind)
        {
            if (step == null || step.Kind != NavigationGraph.StepKind.Door)
            {
                position = Vector3.zero;
                targetKind = NavigationTargetKind.ZoneFallback;
                return false;
            }

            SyncDoorTraversalState(step);
            return TryGetDoorTraversalNavigationTargetCore(
                step,
                currentZone,
                playerPosition,
                out position,
                out targetKind);
        }

        private bool IsZoneNavigationTargetReached(string currentZone)
        {
            if (string.IsNullOrWhiteSpace(currentZone) || string.IsNullOrWhiteSpace(_navigationTargetZone))
                return false;

            if (IsExactZoneMatch(currentZone, _navigationTargetZone))
                return true;

            if (TryGetTrackedInteractable(out _))
                return false;

            string currentNavigationZone = GetNavigationZoneName(currentZone);
            string targetNavigationZone = GetNavigationZoneName(_navigationTargetZone);
            return !string.IsNullOrWhiteSpace(currentNavigationZone) &&
                !string.IsNullOrWhiteSpace(targetNavigationZone) &&
                string.Equals(currentNavigationZone, targetNavigationZone, StringComparison.OrdinalIgnoreCase);
        }

        private bool NeedsNavigationPathRefresh(out string reason)
        {
            reason = null;
            string currentZone = GetCurrentZoneNameForNavigation();
            if (string.IsNullOrEmpty(currentZone) || string.IsNullOrEmpty(_navigationTargetZone))
            {
                reason = "current zone or target zone missing";
                return true;
            }

            if (_navigationPath == null)
            {
                reason = "path missing";
                return true;
            }

            if (_navigationPath.Count < 1)
            {
                if (TryResolveTrackedNavigationMode(
                        currentZone,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _,
                        out _))
                {
                    return false;
                }

                reason = "path missing";
                return true;
            }

            if (IsZoneNavigationTargetReached(currentZone))
            {
                reason = "player entered target zone";
                return true;
            }

            string currentNavigationZone = GetNavigationZoneName(currentZone);
            if (string.IsNullOrEmpty(currentNavigationZone))
            {
                reason = "current navigation zone unavailable";
                return false;
            }

            string targetNavigationZone = GetNavigationZoneName(_navigationTargetZone);
            if (string.IsNullOrEmpty(targetNavigationZone))
            {
                reason = "target navigation zone unavailable";
                return true;
            }

            if (TryGetTrackedInteractable(out _) &&
                string.Equals(currentNavigationZone, targetNavigationZone, StringComparison.OrdinalIgnoreCase))
            {
                reason = "player entered target navigation zone";
                return true;
            }

            NavigationGraph.PathStep currentStep = GetCurrentNavigationStep();
            if (currentStep == null)
            {
                reason = "current step missing";
                return true;
            }

            if (ShouldPreserveForcedTransitionSweepStep(currentStep))
            {
                if (IsZoneNavigationTargetReached(currentZone))
                {
                    reason = "player entered target zone";
                    return true;
                }

                return false;
            }

            NavigationGraph.PathStep destinationStep = _navigationPath[_navigationPath.Count - 1];
            if (destinationStep == null ||
                !string.Equals(destinationStep.ToZone, targetNavigationZone, StringComparison.OrdinalIgnoreCase))
            {
                reason = "destination step no longer matches target";
                return true;
            }

            if (currentStep.Kind == NavigationGraph.StepKind.OpenPassage &&
                IsCommittedOpenPassageTraversal(currentStep))
            {
                if (ShouldAdvanceOpenPassageStepByGeometry(currentStep))
                {
                    reason = "open-passage geometric advance";
                    return true;
                }

                return false;
            }

            if (!IsZoneEquivalentToNavigationZone(currentNavigationZone, currentStep.FromZone))
            {
                reason = "player left current step source zone";
                return true;
            }

            if (TryFindPreferredNavigationPath(
                currentNavigationZone,
                targetNavigationZone,
                out List<NavigationGraph.PathStep> preferredPath) &&
                preferredPath != null &&
                preferredPath.Count > 0)
            {
                string preferredStepKey = BuildNavigationStepKey(preferredPath[0]);
                string currentStepKey = BuildNavigationStepKey(currentStep);
                if (!string.Equals(preferredStepKey, currentStepKey, StringComparison.Ordinal))
                {
                    reason = "position-aware first step changed";
                    return true;
                }
            }

            return false;
        }

        private bool HandlePendingNavigationTransition()
        {
            if (_autoWalkTransitionUntil <= 0f)
                return false;

            NavigationGraph.PathStep currentStep = GetCurrentNavigationStep();
            if (HasAdvancedBeyondStep(currentStep))
            {
                LogNavigationTransitionDebug(
                    "Transition completed currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                    " step=" + DescribeNavigationStep(currentStep));
                _autoWalkTransitionUntil = 0f;
                TryRefreshNavigationPath(forceAnnounce: false);
                return true;
            }

            if (Time.unscaledTime < _autoWalkTransitionUntil)
            {
                LogNavigationTransitionDebug(
                    "Transition wait active until=" + _autoWalkTransitionUntil.ToString("0.00", CultureInfo.InvariantCulture) +
                    " currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                    " step=" + DescribeNavigationStep(currentStep));
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                ObjectTracker.StopTracking();
                return true;
            }

            LogNavigationTransitionDebug(
                "Transition wait expired without zone advance currentZone=" + (GetCurrentZoneNameInternal() ?? "<null>") +
                " step=" + DescribeNavigationStep(currentStep));
            _autoWalkTransitionUntil = 0f;
            return false;
        }

        private bool HasAdvancedBeyondStep(NavigationGraph.PathStep step)
        {
            if (step == null || string.IsNullOrEmpty(step.FromZone))
                return false;

            string currentZone = GetCurrentZoneNameInternal();
            if (string.IsNullOrEmpty(currentZone))
                return false;

            return !IsZoneEquivalentToNavigationZone(currentZone, step.FromZone);
        }

        private bool IsCommittedOpenPassageTraversal(NavigationGraph.PathStep step)
        {
            if (step == null || step.Kind != NavigationGraph.StepKind.OpenPassage)
                return false;

            string stepKey = BuildNavigationStepKey(step);
            if (!string.Equals(_openPassageTraversalStepKey, stepKey, StringComparison.Ordinal))
                return false;

            return _openPassageTraversalStage == OpenPassageTraversalStage.SourceHandoff ||
                _openPassageTraversalStage == OpenPassageTraversalStage.DestinationWaypoint ||
                _openPassageTraversalStage == OpenPassageTraversalStage.DestinationHandoff;
        }

        private bool TryGetOpenPassageProgressMetrics(
            NavigationGraph.PathStep step,
            out float projectedProgress,
            out float segmentLength,
            out float destinationDistance)
        {
            projectedProgress = 0f;
            segmentLength = 0f;
            destinationDistance = float.MaxValue;

            if (step == null || step.Kind != NavigationGraph.StepKind.OpenPassage || BetterPlayerControl.Instance == null)
                return false;

            Vector3 handoffStart = GetOpenPassageSourceGuidanceOrigin(step);
            Vector3 destinationApproach = GetOpenPassageDestinationApproachPosition(step);
            if (handoffStart == Vector3.zero || destinationApproach == Vector3.zero)
                return false;

            Vector3 handoffDirection = destinationApproach - handoffStart;
            handoffDirection.y = 0f;
            if (handoffDirection.sqrMagnitude <= 0.0001f)
                return false;

            segmentLength = handoffDirection.magnitude;
            handoffDirection.Normalize();

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            Vector3 playerOffset = playerPosition - handoffStart;
            playerOffset.y = 0f;
            projectedProgress = Vector3.Dot(playerOffset, handoffDirection);

            Vector3 destinationFlat = destinationApproach;
            destinationFlat.y = playerPosition.y;
            destinationDistance = Vector3.Distance(playerPosition, destinationFlat);
            return true;
        }

        private bool ShouldSuppressOpenPassageBackwardZoneReport(
            NavigationGraph.PathStep step,
            string currentZone,
            string currentNavigationZone,
            out string fallbackZone,
            out string reason)
        {
            fallbackZone = null;
            reason = null;

            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                string.IsNullOrEmpty(step.FromZone) ||
                !IsCommittedOpenPassageTraversal(step))
            {
                return false;
            }

            if (string.IsNullOrEmpty(currentZone))
                return false;

            if (IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                (!string.IsNullOrEmpty(step.ToZone) && IsZoneEquivalentToNavigationZone(currentZone, step.ToZone)))
            {
                return false;
            }

            if (!TryGetOpenPassageProgressMetrics(step, out float projectedProgress, out float segmentLength, out float destinationDistance))
                return false;

            if (projectedProgress < AutoWalkOpenPassageCommitDistance)
                return false;

            if (string.IsNullOrEmpty(_navigationTargetZone) ||
                IsExactZoneMatch(step.ToZone, _navigationTargetZone) ||
                !IsExactZoneMatch(currentZone, _navigationTargetZone))
            {
                return false;
            }

            fallbackZone = step.FromZone;
            reason =
                "rawCurrentZone=" + currentZone +
                " currentNavigationZone=" + (currentNavigationZone ?? "<null>") +
                " fallbackZone=" + step.FromZone +
                " projectedProgress=" + projectedProgress.ToString("0.00", CultureInfo.InvariantCulture) +
                " segmentLength=" + segmentLength.ToString("0.00", CultureInfo.InvariantCulture) +
                " destinationDistance=" + destinationDistance.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        private bool ShouldAdvanceOpenPassageStepByGeometry(NavigationGraph.PathStep step)
        {
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                !IsCommittedOpenPassageTraversal(step) ||
                _openPassageTraversalStage != OpenPassageTraversalStage.DestinationHandoff)
            {
                return false;
            }

            if (!TryGetOpenPassageProgressMetrics(step, out float projectedProgress, out float segmentLength, out float destinationDistance))
                return false;

            return projectedProgress >= segmentLength ||
                destinationDistance <= AutoWalkArrivalDistance;
        }

        private void RecordTrackerTarget(
            Vector3 targetPosition,
            NavigationTargetKind targetKind,
            NavigationGraph.PathStep step)
        {
            _hasLastTrackerTarget = true;
            _lastTrackerTargetPosition = targetPosition;
            _lastTrackerTargetKind = targetKind.ToString();
            _lastTrackerTargetStepKey = BuildNavigationStepKey(step);
            LogNavigationTrackerDebug(
                "Tone target set kind=" + targetKind +
                " position=" + FormatVector3(targetPosition) +
                " stepKey=" + (_lastTrackerTargetStepKey ?? "<null>") +
                " localContext=" + (_localNavigationPathContext ?? "<null>"));
        }

        private void ClearTrackerTargetDiagnostics()
        {
            _hasLastTrackerTarget = false;
            _lastTrackerTargetPosition = Vector3.zero;
            _lastTrackerTargetKind = null;
            _lastTrackerTargetStepKey = null;
        }

        private bool TryResolveAutoWalkMovementTarget(
            string resolutionSource,
            out Vector3 nextPosition,
            out NavigationTargetKind targetKind)
        {
            if (!TryGetNavigationMovementTarget(out nextPosition, out targetKind))
                return false;

            CompareTrackerAndMovementTarget(nextPosition, targetKind, resolutionSource);
            return true;
        }

        private void CompareTrackerAndMovementTarget(
            Vector3 movementTarget,
            NavigationTargetKind targetKind,
            string resolutionSource)
        {
            float delta = _hasLastTrackerTarget
                ? Vector3.Distance(_lastTrackerTargetPosition, movementTarget)
                : -1f;
            string stepKey = _lastTrackerTargetStepKey ?? "<null>";
            LogNavigationAutoWalkDebug(
                "Movement target resolved source=" + (resolutionSource ?? "<null>") +
                " kind=" + targetKind +
                " position=" + FormatVector3(movementTarget) +
                " trackerKind=" + (_lastTrackerTargetKind ?? "<null>") +
                " trackerPosition=" + (_hasLastTrackerTarget ? FormatVector3(_lastTrackerTargetPosition) : "<null>") +
                " delta=" + delta.ToString("0.00", CultureInfo.InvariantCulture) +
                " stepKey=" + stepKey);

            if (!_hasLastTrackerTarget)
                return;

            if (delta > 0.05f ||
                !string.Equals(_lastTrackerTargetKind, targetKind.ToString(), StringComparison.Ordinal))
            {
                LogNavigationTrackerDebug(
                    "Tone alignment mismatch source=" + (resolutionSource ?? "<null>") +
                    " trackerKind=" + (_lastTrackerTargetKind ?? "<null>") +
                    " movementKind=" + targetKind +
                    " trackerPosition=" + FormatVector3(_lastTrackerTargetPosition) +
                    " movementPosition=" + FormatVector3(movementTarget) +
                    " delta=" + delta.ToString("0.00", CultureInfo.InvariantCulture) +
                    " stepKey=" + stepKey);
            }
        }

        private void UpdateNavigationTracker()
        {
            if (TryGetNavigationMovementTarget(out Vector3 nextPosition, out NavigationTargetKind targetKind))
            {
                NavigationGraph.PathStep step = GetCurrentNavigationStep();
                NavigationGraph.StepKind stepKind = step != null ? step.Kind : NavigationGraph.StepKind.Unknown;
                bool requiresInteraction = targetKind == NavigationTargetKind.TransitionInteractable ||
                    (step != null && step.RequiresInteraction);
                ObjectTracker.StartTracking(nextPosition, stepKind, requiresInteraction);
                RecordTrackerTarget(nextPosition, targetKind, step);
                return;
            }

            ClearTrackerTargetDiagnostics();
            ObjectTracker.StopTracking();
        }

        private bool TryGetNavigationMovementTarget(out Vector3 position, out NavigationTargetKind targetKind)
        {
            position = Vector3.zero;
            targetKind = NavigationTargetKind.ZoneFallback;

            if (!TryGetNextNavigationPosition(out position, out targetKind))
                return false;

            if (BetterPlayerControl.Instance == null)
                return true;

            string currentZone = GetCurrentZoneNameForNavigation();
            NavigationGraph.PathStep step = GetCurrentNavigationStep();
            if (TryAdjustNavigationTargetWithLocalPathing(
                currentZone,
                step,
                BetterPlayerControl.Instance.transform.position,
                ref position,
                ref targetKind))
            {
                return true;
            }

            return true;
        }

        private bool TryAdjustNavigationTargetWithLocalPathing(
            string currentZone,
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            ref Vector3 position,
            ref NavigationTargetKind targetKind)
        {
            if (!LocalNavigationMaps.IsAvailable || position == Vector3.zero)
            {
                ClearLocalNavigationPathState();
                return false;
            }

            if (!TryResolveLocalNavigationGoal(
                    currentZone,
                    step,
                    playerPosition,
                    position,
                    targetKind,
                    out string planningZone,
                    out Vector3 planningGoal,
                    out string planningContext))
            {
                LogNavigationTrackerDebug(
                    "Local navigation skipped currentZone=" + (currentZone ?? "<null>") +
                    " rawTargetKind=" + targetKind +
                    " rawTargetPosition=" + FormatVector3(position) +
                    " step=" + DescribeNavigationStep(step));
                ClearLocalNavigationPathState();
                return false;
            }

            if (!TryGetLocalNavigationLookaheadTarget(
                    planningZone,
                    planningGoal,
                    planningContext,
                    BuildNavigationStepKey(step),
                    playerPosition,
                    out Vector3 adjustedPosition,
                    out string debugDetail))
            {
                return false;
            }

            position = adjustedPosition;
            targetKind = NavigationTargetKind.LocalWaypoint;
            LogNavigationTrackerDebug(
                "Local navigation target kind=LocalWaypoint position=" + FormatVector3(position) +
                " rawCurrentZone=" + (currentZone ?? "<null>") +
                " detail=" + debugDetail +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private bool TryResolveLocalNavigationGoal(
            string currentZone,
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            Vector3 desiredPosition,
            NavigationTargetKind targetKind,
            out string planningZone,
            out Vector3 planningGoal,
            out string planningContext)
        {
            planningZone = null;
            planningGoal = Vector3.zero;
            planningContext = null;

            if (desiredPosition == Vector3.zero)
                return false;

            string currentNavigationZone = GetNavigationZoneName(currentZone);
            if (targetKind == NavigationTargetKind.DirectObject && !string.IsNullOrEmpty(currentNavigationZone))
            {
                planningZone = currentNavigationZone;
                planningGoal = desiredPosition;
                planningContext = "direct-object";
                return ShouldUseLocalNavigationGoal(
                    playerPosition,
                    planningGoal,
                    GetLocalNavigationGoalReachedDistance(planningContext));
            }

            if (TryResolveOpenPassageLocalNavigationGoal(
                    currentZone,
                    step,
                    playerPosition,
                    desiredPosition,
                    out planningZone,
                    out planningGoal,
                    out planningContext))
            {
                return true;
            }

            if (step != null)
            {
                if (TryResolveDoorLocalNavigationGoal(
                        currentZone,
                        step,
                        playerPosition,
                        desiredPosition,
                        out planningZone,
                        out planningGoal,
                        out planningContext))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(step.FromZone) &&
                    IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) &&
                    ShouldUseLocalNavigationGoal(
                        playerPosition,
                        desiredPosition,
                        LocalNavigationGoalReachedDistance))
                {
                    planningZone = ResolveLocalPlanningZone(currentZone, step.FromZone, playerPosition, desiredPosition);
                    planningGoal = desiredPosition;
                    planningContext = "step-source";
                    return true;
                }

                if (!string.IsNullOrEmpty(step.ToZone) &&
                    IsZoneEquivalentToNavigationZone(currentZone, step.ToZone) &&
                    ShouldUseLocalNavigationGoal(
                        playerPosition,
                        desiredPosition,
                        LocalNavigationGoalReachedDistance))
                {
                    planningZone = ResolveLocalPlanningZone(currentZone, step.ToZone, playerPosition, desiredPosition);
                    planningGoal = desiredPosition;
                    planningContext = "step-destination";
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(currentNavigationZone) &&
                ShouldUseLocalNavigationGoal(
                    playerPosition,
                    desiredPosition,
                    LocalNavigationGoalReachedDistance))
            {
                planningZone = currentNavigationZone;
                planningGoal = desiredPosition;
                planningContext = "current-zone";
                return true;
            }

            return false;
        }

        private static string ResolveLocalPlanningZone(
            string currentZone,
            string canonicalZone,
            Vector3 startPosition,
            Vector3 goalPosition)
        {
            if (string.IsNullOrWhiteSpace(canonicalZone))
                return null;

            string currentNavigationZone = GetNavigationZoneName(currentZone);
            if (!string.IsNullOrWhiteSpace(currentNavigationZone) &&
                IsZoneEquivalentToNavigationZone(currentNavigationZone, canonicalZone) &&
                CanUseLocalPlanningZone(currentNavigationZone, startPosition, goalPosition))
            {
                return currentNavigationZone;
            }

            if (CanUseLocalPlanningZone(canonicalZone, startPosition, goalPosition))
                return canonicalZone;

            return !string.IsNullOrWhiteSpace(currentNavigationZone) &&
                IsZoneEquivalentToNavigationZone(currentNavigationZone, canonicalZone)
                ? currentNavigationZone
                : canonicalZone;
        }

        private static bool CanUseLocalPlanningZone(
            string zoneName,
            Vector3 startPosition,
            Vector3 goalPosition)
        {
            if (!LocalNavigationMaps.IsAvailable || string.IsNullOrWhiteSpace(zoneName))
                return false;

            if (!LocalNavigationMaps.TrySnapPositionToNearestWalkableCell(zoneName, startPosition, out _, out _))
                return false;

            return goalPosition == Vector3.zero ||
                LocalNavigationMaps.TrySnapPositionToNearestWalkableCell(zoneName, goalPosition, out _, out _);
        }

        private static bool ShouldUseLocalNavigationGoal(
            Vector3 playerPosition,
            Vector3 goalPosition,
            float goalReachedDistance)
        {
            if (goalPosition == Vector3.zero)
                return false;

            Vector3 flattenedPlayerPosition = playerPosition;
            Vector3 flattenedGoalPosition = goalPosition;
            flattenedPlayerPosition.y = 0f;
            flattenedGoalPosition.y = 0f;
            return Vector3.Distance(flattenedPlayerPosition, flattenedGoalPosition) > goalReachedDistance;
        }

        private bool TryGetLocalNavigationLookaheadTarget(
            string planningZone,
            Vector3 planningGoal,
            string planningContext,
            string stepKey,
            Vector3 playerPosition,
            out Vector3 adjustedPosition,
            out string debugDetail)
        {
            adjustedPosition = Vector3.zero;
            debugDetail = null;

            if (string.IsNullOrWhiteSpace(planningZone) || planningGoal == Vector3.zero)
            {
                ClearLocalNavigationPathState();
                return false;
            }

            bool needsRepath = _localNavigationPathPoints == null ||
                _localNavigationPathPoints.Count < 1 ||
                !string.Equals(_localNavigationPathZone, planningZone, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_localNavigationPathContext, planningContext, StringComparison.Ordinal) ||
                !string.Equals(_localNavigationPathStepKey, stepKey ?? string.Empty, StringComparison.Ordinal) ||
                GetFlatDistance(_localNavigationPathGoal, planningGoal) > LocalNavigationGoalRetargetDistance;

            if (needsRepath)
            {
                if (!LocalNavigationMaps.TryFindPath(
                        planningZone,
                        playerPosition,
                        planningGoal,
                        out List<Vector3> pathPoints,
                        out string failureReason) ||
                    pathPoints == null ||
                    pathPoints.Count < 1)
                {
                    ClearLocalNavigationPathState();
                    LogNavigationTrackerDebug(
                        "Local navigation repath failed stepKey=" + (stepKey ?? "<null>") +
                        " zone=" + planningZone +
                        " context=" + planningContext +
                        " goal=" + FormatVector3(planningGoal) +
                        " reason=" + (failureReason ?? "<null>"));
                    return false;
                }

                _localNavigationPathZone = planningZone;
                _localNavigationPathContext = planningContext;
                _localNavigationPathStepKey = stepKey ?? string.Empty;
                _localNavigationPathGoal = planningGoal;
                _localNavigationPathPoints = pathPoints;
                _localNavigationPathIndex = 0;
                LogNavigationTrackerDebug(
                    "Local navigation repath succeeded stepKey=" + (_localNavigationPathStepKey ?? "<null>") +
                    " zone=" + planningZone +
                    " context=" + planningContext +
                    " goal=" + FormatVector3(planningGoal) +
                    " pathPoints=" + _localNavigationPathPoints.Count);
            }

            AdvanceLocalNavigationPathIndex(playerPosition);
            float remainingDistance = ComputeLocalNavigationRemainingDistance(playerPosition);
            if (remainingDistance <= GetLocalNavigationGoalReachedDistance(planningContext))
            {
                LogNavigationTrackerDebug(
                    "Local navigation goal reached stepKey=" + (_localNavigationPathStepKey ?? "<null>") +
                    " zone=" + planningZone +
                    " context=" + planningContext +
                    " goal=" + FormatVector3(planningGoal) +
                    " remainingDistance=" + remainingDistance.ToString("0.00", CultureInfo.InvariantCulture));
                ClearLocalNavigationPathState();
                return false;
            }

            int lookaheadIndex = GetLocalNavigationLookaheadIndex(playerPosition);
            if (_localNavigationPathPoints == null ||
                _localNavigationPathPoints.Count < 1 ||
                lookaheadIndex < 0 ||
                lookaheadIndex >= _localNavigationPathPoints.Count)
            {
                LogNavigationTrackerDebug(
                    "Local navigation lookahead unavailable stepKey=" + (_localNavigationPathStepKey ?? "<null>") +
                    " zone=" + planningZone +
                    " context=" + planningContext +
                    " pathCount=" + (_localNavigationPathPoints != null ? _localNavigationPathPoints.Count : 0) +
                    " lookaheadIndex=" + lookaheadIndex);
                ClearLocalNavigationPathState();
                return false;
            }

            adjustedPosition = _localNavigationPathPoints[lookaheadIndex];
            debugDetail =
                "zone=" + planningZone +
                " context=" + planningContext +
                " pathIndex=" + (_localNavigationPathIndex + 1) +
                " lookaheadIndex=" + (lookaheadIndex + 1) +
                " pathCount=" + _localNavigationPathPoints.Count +
                " remainingDistance=" + remainingDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " goal=" + FormatVector3(planningGoal);
            return true;
        }

        private void AdvanceLocalNavigationPathIndex(Vector3 playerPosition)
        {
            if (_localNavigationPathPoints == null)
                return;

            while (_localNavigationPathIndex < _localNavigationPathPoints.Count)
            {
                Vector3 pathPoint = _localNavigationPathPoints[_localNavigationPathIndex];
                pathPoint.y = playerPosition.y;
                if (Vector3.Distance(playerPosition, pathPoint) > LocalNavigationPathAdvanceDistance)
                    break;

                _localNavigationPathIndex++;
            }
        }

        private float ComputeLocalNavigationRemainingDistance(Vector3 playerPosition)
        {
            if (_localNavigationPathPoints == null || _localNavigationPathIndex >= _localNavigationPathPoints.Count)
                return 0f;

            float distance = 0f;
            Vector3 previousPoint = playerPosition;
            previousPoint.y = 0f;
            for (int i = _localNavigationPathIndex; i < _localNavigationPathPoints.Count; i++)
            {
                Vector3 currentPoint = _localNavigationPathPoints[i];
                currentPoint.y = 0f;
                distance += Vector3.Distance(previousPoint, currentPoint);
                previousPoint = currentPoint;
            }

            return distance;
        }

        private int GetLocalNavigationLookaheadIndex(Vector3 playerPosition)
        {
            if (_localNavigationPathPoints == null || _localNavigationPathIndex >= _localNavigationPathPoints.Count)
                return -1;

            float accumulatedDistance = 0f;
            Vector3 previousPoint = playerPosition;
            previousPoint.y = 0f;
            int lookaheadIndex = _localNavigationPathIndex;
            for (int i = _localNavigationPathIndex; i < _localNavigationPathPoints.Count; i++)
            {
                Vector3 currentPoint = _localNavigationPathPoints[i];
                currentPoint.y = 0f;
                accumulatedDistance += Vector3.Distance(previousPoint, currentPoint);
                lookaheadIndex = i;
                if (accumulatedDistance >= LocalNavigationLookaheadDistance)
                    break;

                previousPoint = currentPoint;
            }

            return lookaheadIndex;
        }

        private void ClearLocalNavigationPathState()
        {
            _localNavigationPathZone = null;
            _localNavigationPathContext = null;
            _localNavigationPathStepKey = null;
            _localNavigationPathGoal = Vector3.zero;
            _localNavigationPathPoints = null;
            _localNavigationPathIndex = 0;
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

        private void ActivatePathlessTrackedNavigationState()
        {
            _navigationPath = new List<NavigationGraph.PathStep>();
            _isNavigationActive = true;
            _autoWalkTransitionUntil = 0f;
            _autoWalkRecoveryAttempts = 0;
            ResetOpenPassageTraversalState();
            ResetDoorTraversalState();
            ClearNavigationZoneOverride();
            ClearNavigationBlockedDetail();
        }

        private static string BuildNavigationStepKey(NavigationGraph.PathStep step)
        {
            if (step == null)
                return null;

            if (!string.IsNullOrWhiteSpace(step.Id))
                return step.Id;

            return step.Kind + "|" +
                (step.FromZone ?? string.Empty) + "->" + (step.ToZone ?? string.Empty) +
                "|" + step.FromWaypoint + "|" + step.ToWaypoint;
        }

        private void ResetOpenPassageTraversalState()
        {
            _openPassageTraversalStepKey = null;
            _openPassageOverrideWaypointIndex = 0;
            _openPassageTraversalStage = OpenPassageTraversalStage.None;
            _openPassageSourceHandoffRecoveryFloor = 0;
            _openPassageDestinationHandoffRecoveryFloor = 0;
            _openPassageSourceHandoffProgressFloor = 0f;
            _openPassageDestinationHandoffProgressFloor = 0f;
        }

        private void ResetDoorTraversalState()
        {
            _doorTraversalStepKey = null;
            _doorTraversalInteractionTriggered = false;
            _doorTraversalPushThroughPosition = Vector3.zero;
            _doorTraversalPostThresholdCommitted = false;
        }

        private void SyncDoorTraversalState(NavigationGraph.PathStep step)
        {
            if (step == null || step.Kind != NavigationGraph.StepKind.Door)
            {
                ResetDoorTraversalState();
                return;
            }

            string stepKey = BuildNavigationStepKey(step);
            if (!string.Equals(_doorTraversalStepKey, stepKey, StringComparison.Ordinal))
            {
                _doorTraversalStepKey = stepKey;
                _doorTraversalInteractionTriggered = false;
                _doorTraversalPushThroughPosition = Vector3.zero;
                _doorTraversalPostThresholdCommitted = false;
            }
        }

        private void ClearNavigationZoneOverride()
        {
            _navigationZoneOverride = null;
            _navigationZoneOverrideStepKey = null;
        }

        private void SyncNavigationZoneOverride(string effectiveCurrentZone, NavigationGraph.PathStep step)
        {
            if (string.IsNullOrWhiteSpace(effectiveCurrentZone) ||
                step == null ||
                string.IsNullOrWhiteSpace(step.FromZone))
            {
                ClearNavigationZoneOverride();
                return;
            }

            string rawCurrentZone = GetCurrentZoneNameInternal();
            if (!string.IsNullOrEmpty(rawCurrentZone) &&
                IsZoneEquivalentToNavigationZone(rawCurrentZone, effectiveCurrentZone))
            {
                ClearNavigationZoneOverride();
                return;
            }

            if (!IsZoneEquivalentToNavigationZone(effectiveCurrentZone, step.FromZone))
            {
                ClearNavigationZoneOverride();
                return;
            }

            _navigationZoneOverride = effectiveCurrentZone;
            _navigationZoneOverrideStepKey = BuildNavigationStepKey(step);
        }

        private void SyncOpenPassageTraversalState(NavigationGraph.PathStep step)
        {
            if (step == null || step.Kind != NavigationGraph.StepKind.OpenPassage)
            {
                ResetOpenPassageTraversalState();
                return;
            }

            string stepKey = BuildNavigationStepKey(step);
            if (!string.Equals(_openPassageTraversalStepKey, stepKey, StringComparison.Ordinal))
            {
                _openPassageTraversalStepKey = stepKey;
                _openPassageOverrideWaypointIndex = 0;
                _openPassageTraversalStage = OpenPassageTraversalStage.SourceWaypoint;
                _openPassageSourceHandoffRecoveryFloor = 0;
                _openPassageDestinationHandoffRecoveryFloor = 0;
                _openPassageSourceHandoffProgressFloor = 0f;
                _openPassageDestinationHandoffProgressFloor = 0f;
            }
            else if (_openPassageTraversalStage == OpenPassageTraversalStage.None)
            {
                _openPassageTraversalStage = OpenPassageTraversalStage.SourceWaypoint;
            }
        }

        private int GetOpenPassageRecoveryAttemptsForStage(OpenPassageTraversalStage traversalStage)
        {
            switch (traversalStage)
            {
                case OpenPassageTraversalStage.SourceHandoff:
                    return Mathf.Max(_autoWalkRecoveryAttempts, _openPassageSourceHandoffRecoveryFloor);

                case OpenPassageTraversalStage.DestinationHandoff:
                    return Mathf.Max(_autoWalkRecoveryAttempts, _openPassageDestinationHandoffRecoveryFloor);

                default:
                    return _autoWalkRecoveryAttempts;
            }
        }

        private void RememberOpenPassageRecoveryAttempt(NavigationGraph.PathStep step, int recoveryAttempt)
        {
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                recoveryAttempt <= 0)
            {
                return;
            }

            string stepKey = BuildNavigationStepKey(step);
            if (!string.Equals(_openPassageTraversalStepKey, stepKey, StringComparison.Ordinal))
                return;

            switch (_openPassageTraversalStage)
            {
                case OpenPassageTraversalStage.SourceHandoff:
                    _openPassageSourceHandoffRecoveryFloor = Mathf.Max(_openPassageSourceHandoffRecoveryFloor, recoveryAttempt);
                    break;

                case OpenPassageTraversalStage.DestinationWaypoint:
                    _openPassageDestinationHandoffRecoveryFloor = Mathf.Max(_openPassageDestinationHandoffRecoveryFloor, recoveryAttempt);
                    break;

                case OpenPassageTraversalStage.DestinationHandoff:
                    _openPassageDestinationHandoffRecoveryFloor = Mathf.Max(_openPassageDestinationHandoffRecoveryFloor, recoveryAttempt);
                    break;
            }
        }

        private void AdvanceCommittedOpenPassageTarget(NavigationGraph.PathStep step)
        {
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                BetterPlayerControl.Instance == null)
            {
                return;
            }

            if (TryAdvanceOpenPassageGuidedWaypoint(step))
                return;

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            switch (_openPassageTraversalStage)
            {
                case OpenPassageTraversalStage.SourceHandoff:
                    if (TryGetOpenPassageSourceHandoffMetrics(step, playerPosition, out float sourceProgress, out float sourceSegmentLength))
                    {
                        _openPassageSourceHandoffProgressFloor = ComputeCommittedOpenPassageTargetProgress(
                            sourceProgress,
                            sourceSegmentLength,
                            AutoWalkOpenPassageHandoffDistance,
                            _openPassageSourceHandoffProgressFloor,
                            forceAdvance: true);
                    }
                    break;

                case OpenPassageTraversalStage.DestinationHandoff:
                    if (TryGetOpenPassageDestinationHandoffMetrics(step, playerPosition, out float destinationProgress, out float destinationSegmentLength))
                    {
                        _openPassageDestinationHandoffProgressFloor = ComputeCommittedOpenPassageTargetProgress(
                            destinationProgress,
                            destinationSegmentLength,
                            AutoWalkOpenPassageHandoffDistance,
                            _openPassageDestinationHandoffProgressFloor,
                            forceAdvance: true,
                            overshootDistance: AutoWalkOpenPassageHandoffDistance);
                    }
                    break;
            }
        }

        private bool TryAdvanceOpenPassageGuidedWaypoint(NavigationGraph.PathStep step)
        {
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                BetterPlayerControl.Instance == null)
            return false;

            List<Vector3> navigationPoints = BuildOpenPassageGuidedNavigationPoints(step);
            if (navigationPoints == null || navigationPoints.Count < 2)
                return false;

            int currentIndex = Mathf.Clamp(_openPassageOverrideWaypointIndex, 0, navigationPoints.Count - 1);
            if (currentIndex >= navigationPoints.Count - 1)
                return false;

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            Vector3 currentTarget = navigationPoints[currentIndex];
            currentTarget.y = playerPosition.y;
            float waypointAdvanceDistance = GetOpenPassageGuidedWaypointAdvanceDistance();
            float currentDistance = Vector3.Distance(playerPosition, currentTarget);
            if (currentDistance > waypointAdvanceDistance)
            {
                LogNavigationAutoWalkDebug(
                    "Preserved guided open-passage waypoint index=" + (currentIndex + 1) + " of " + navigationPoints.Count +
                    " distance=" + currentDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            _openPassageOverrideWaypointIndex = currentIndex + 1;
            LogNavigationAutoWalkDebug(
                "Advanced guided open-passage waypoint index=" + (_openPassageOverrideWaypointIndex + 1) + " of " + navigationPoints.Count +
                " step=" + DescribeNavigationStep(step));
            return true;
        }

        private OpenPassageTraversalStage GetOpenPassageTraversalStage(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            float fromDistance,
            float sourceSegmentDistance,
            float destinationWaypointDistance)
        {
            SyncOpenPassageTraversalState(step);
            if (step == null || step.Kind != NavigationGraph.StepKind.OpenPassage)
                return OpenPassageTraversalStage.None;

            if (!string.IsNullOrEmpty(step.ToZone) &&
                IsZoneEquivalentToNavigationZone(currentZone, step.ToZone))
            {
                if (_openPassageTraversalStage != OpenPassageTraversalStage.DestinationHandoff)
                    _openPassageDestinationHandoffProgressFloor = 0f;
                _openPassageTraversalStage = OpenPassageTraversalStage.DestinationHandoff;
                return _openPassageTraversalStage;
            }

            switch (_openPassageTraversalStage)
            {
                case OpenPassageTraversalStage.SourceWaypoint:
                    if (fromDistance <= AutoWalkOpenPassageCommitDistance)
                    {
                        _openPassageSourceHandoffProgressFloor = 0f;
                        _openPassageTraversalStage = OpenPassageTraversalStage.SourceHandoff;
                    }
                    break;

                case OpenPassageTraversalStage.SourceHandoff:
                    if (ShouldAdvanceOpenPassageSourceHandoff(
                            step,
                            currentZone,
                            playerPosition,
                            sourceSegmentDistance))
                    {
                        _openPassageDestinationHandoffProgressFloor = 0f;
                        _openPassageTraversalStage = OpenPassageTraversalStage.DestinationWaypoint;
                    }
                    break;

                case OpenPassageTraversalStage.DestinationWaypoint:
                    if (destinationWaypointDistance <= AutoWalkArrivalDistance ||
                        (_autoWalkRecoveryAttempts > 0 && destinationWaypointDistance <= AutoWalkZoneBoundaryFallbackDistance))
                    {
                        _openPassageDestinationHandoffProgressFloor = 0f;
                        _openPassageTraversalStage = OpenPassageTraversalStage.DestinationHandoff;
                    }
                    break;
            }

            return _openPassageTraversalStage;
        }

        private bool ShouldAttemptAutoWalkInteractionRecovery(
            NavigationGraph.PathStep step,
            bool allowOptionalDoorInteraction,
            out InteractableObj optionalOpenPassageInteractable,
            out string optionalOpenPassageInteractionDetail)
        {
            optionalOpenPassageInteractable = null;
            optionalOpenPassageInteractionDetail = null;
            if (step == null)
                return false;

            if (step.RequiresInteraction ||
                step.Kind == NavigationGraph.StepKind.Door ||
                step.Kind == NavigationGraph.StepKind.Teleporter)
            {
                return true;
            }

            return allowOptionalDoorInteraction &&
                step.Kind == NavigationGraph.StepKind.OpenPassage &&
                TryResolveOptionalOpenPassageInteractionCandidate(
                    step,
                    out optionalOpenPassageInteractable,
                    out optionalOpenPassageInteractionDetail);
        }

        private bool ShouldAdvanceOpenPassageSourceHandoff(
            NavigationGraph.PathStep step,
            string currentZone,
            Vector3 playerPosition,
            float sourceSegmentDistance)
        {
            if (step == null)
                return false;

            if (!UsesOverrideOnlyOpenPassageGuidance(step))
                return sourceSegmentDistance <= OpenPassageOverrideLocalNavigationGoalReachedDistance;

            if (!TryGetOpenPassageGuidedNavigationTarget(
                    step,
                    playerPosition,
                    out Vector3 guidedTarget,
                    out _,
                    out _,
                    out bool isFinalWaypoint) ||
                !isFinalWaypoint ||
                guidedTarget == Vector3.zero)
            {
                return false;
            }

            guidedTarget.y = playerPosition.y;
            if (Vector3.Distance(playerPosition, guidedTarget) >
                GetOpenPassageGuidedWaypointAdvanceDistance())
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(step.ToZone) &&
                (IsZoneEquivalentToNavigationZone(currentZone, step.ToZone) ||
                 IsAcceptedOverrideDestinationZone(step, currentZone) ||
                 sourceSegmentDistance <= OpenPassageOverrideLocalNavigationGoalReachedDistance);
        }

        private bool TryRecoverAutoWalk(NavigationGraph.PathStep step, NavigationTargetKind targetKind)
        {
            bool allowOptionalDoorInteraction = targetKind != NavigationTargetKind.TransitionInteractable;
            bool shouldAttemptInteractionRecovery = ShouldAttemptAutoWalkInteractionRecovery(
                step,
                allowOptionalDoorInteraction,
                out InteractableObj optionalOpenPassageInteractable,
                out string optionalOpenPassageInteractionDetail);
            if (shouldAttemptInteractionRecovery &&
                TryAttemptTransitionInteraction(
                    step,
                    allowOptionalDoorInteraction,
                    optionalOpenPassageInteractable,
                    optionalOpenPassageInteractionDetail))
            {
                LogNavigationAutoWalkDebug(
                    "Auto-walk recovery succeeded via interaction targetKind=" + targetKind +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (!shouldAttemptInteractionRecovery)
            {
                LogNavigationAutoWalkDebug(
                    "Auto-walk recovery skipped interaction targetKind=" + targetKind +
                    " step=" + DescribeNavigationStep(step));
            }

            if (_autoWalkRecoveryAttempts >= AutoWalkMaxRecoveryAttempts)
            {
                SetNavigationBlockedDetail(
                    "auto-walk recovery exhausted targetKind=" + targetKind +
                    " attempts=" + _autoWalkRecoveryAttempts +
                    " step=" + DescribeNavigationStep(step));
                LogNavigationAutoWalkDebug(
                    "Auto-walk recovery exhausted targetKind=" + targetKind +
                    " attempts=" + _autoWalkRecoveryAttempts +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            int nextRecoveryAttempt = _autoWalkRecoveryAttempts + 1;
            _autoWalkRecoveryAttempts = nextRecoveryAttempt;
            RememberOpenPassageRecoveryAttempt(step, nextRecoveryAttempt);
            AdvanceCommittedOpenPassageTarget(step);
            bool refreshed = TryRefreshNavigationPath(forceAnnounce: false);
            if (refreshed)
                _autoWalkRecoveryAttempts = nextRecoveryAttempt;
            LogNavigationAutoWalkDebug(
                "Auto-walk recovery refresh targetKind=" + targetKind +
                " attempt=" + nextRecoveryAttempt +
                " refreshed=" + refreshed +
                " step=" + DescribeNavigationStep(step));
            if (!refreshed)
            {
                SetNavigationBlockedDetail(
                    "auto-walk recovery refresh failed targetKind=" + targetKind +
                    " attempt=" + nextRecoveryAttempt +
                    " step=" + DescribeNavigationStep(step));
            }

            if (!refreshed)
                return false;

            ResetAutoWalkProgress();
            return true;
        }

        private bool TryResolveOptionalOpenPassageInteractionCandidate(
            NavigationGraph.PathStep step,
            out InteractableObj interactable,
            out string resolutionDetail)
        {
            interactable = null;
            resolutionDetail = null;
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage)
            {
                return false;
            }

            if (!TryFindOptionalOpenPassageDoorInteractable(step, out interactable) ||
                interactable == null)
            {
                return false;
            }

            string currentZone = GetCurrentZoneNameInternal();
            if (!string.IsNullOrEmpty(currentZone) &&
                (IsZoneEquivalentToNavigationZone(currentZone, step.FromZone) ||
                 IsAcceptedOverrideSourceZone(step, currentZone)))
            {
                resolutionDetail = "source-zone";
            }
            else if (!string.IsNullOrEmpty(currentZone))
            {
                resolutionDetail = "runtime-zone-drift currentZone=" + currentZone;
            }
            else
            {
                resolutionDetail = "runtime-zone-drift currentZone=<null>";
            }

            return true;
        }

        private bool TryAttemptTransitionInteraction(
            NavigationGraph.PathStep step,
            bool allowOptionalDoorInteraction,
            InteractableObj preResolvedOptionalInteractable = null,
            string optionalOpenPassageInteractionDetail = null)
        {
            if (step == null)
                return false;

            bool shouldAttempt = step.RequiresInteraction;
            if (!shouldAttempt &&
                allowOptionalDoorInteraction &&
                step.Kind == NavigationGraph.StepKind.Door &&
                IsCurrentZoneEquivalentTo(step.FromZone))
            {
                shouldAttempt = true;
            }

            if (!shouldAttempt &&
                allowOptionalDoorInteraction &&
                step.Kind == NavigationGraph.StepKind.OpenPassage &&
                preResolvedOptionalInteractable != null)
            {
                shouldAttempt = true;
            }

            if (!shouldAttempt)
            {
                LogNavigationTransitionDebug(
                    "Transition interaction skipped shouldAttempt=false allowOptionalDoorInteraction=" + allowOptionalDoorInteraction +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            if (Time.unscaledTime - _lastNavigationInteractionAttemptTime < AutoWalkInteractionRetrySeconds)
            {
                LogNavigationTransitionDebug(
                    "Transition interaction skipped due to retry cooldown step=" + DescribeNavigationStep(step));
                return false;
            }

            if (!string.IsNullOrEmpty(optionalOpenPassageInteractionDetail) &&
                preResolvedOptionalInteractable != null)
            {
                LogNavigationTransitionDebug(
                    "Transition interaction enabling optional open-passage door recovery detail=" +
                    optionalOpenPassageInteractionDetail +
                    " interactable=" + DescribeInteractable(preResolvedOptionalInteractable) +
                    " step=" + DescribeNavigationStep(step));
            }

            InteractableObj interactable = preResolvedOptionalInteractable;
            if (interactable == null &&
                !TryFindTransitionInteractable(step, out interactable))
            {
                SetNavigationBlockedDetail("transition interaction failed: no interactable found step=" + DescribeNavigationStep(step));
                LogNavigationTransitionDebug(
                    "Transition interaction failed: no interactable found step=" + DescribeNavigationStep(step));
                return false;
            }

            if (!CanAutoInteractWithStep(step, interactable, out string interactionReason))
            {
                if (IsAlreadyOpenInteractionReason(interactionReason))
                {
                    Vector3 pushThroughPosition = BuildDoorTraversalPushThroughPosition(step, interactable);
                    if (step.Kind == NavigationGraph.StepKind.Door)
                    {
                        _doorTraversalStepKey = BuildNavigationStepKey(step);
                        _doorTraversalInteractionTriggered = true;
                        _doorTraversalPostThresholdCommitted = false;
                        if (pushThroughPosition != Vector3.zero)
                            _doorTraversalPushThroughPosition = pushThroughPosition;
                    }
                    if (_transitionSweepSession != null &&
                        _transitionSweepSession.Kind == TransitionSweepKind.Door &&
                        string.Equals(
                            BuildNavigationStepKey(step),
                            BuildNavigationStepKey(_transitionSweepSession.CurrentStep),
                            StringComparison.Ordinal))
                    {
                        _transitionSweepSession.DoorInteractionTriggered = true;
                        _transitionSweepSession.DoorPostThresholdCommitted = false;
                        if (pushThroughPosition != Vector3.zero)
                            _transitionSweepSession.DoorPushThroughPosition = pushThroughPosition;
                    }

                    _lastNavigationInteractionAttemptTime = Time.unscaledTime;
                    _autoWalkRecoveryAttempts = 0;
                    ResetAutoWalkProgress();
                    LogNavigationTransitionDebug(
                        "Transition interaction treating open door as ready interactable=" + DescribeInteractable(interactable) +
                        " pushThroughPosition=" + FormatVector3(pushThroughPosition) +
                        " step=" + DescribeNavigationStep(step));
                    return true;
                }

                LogNavigationTransitionDebug(
                    "Transition interaction skipped reason=" + interactionReason +
                    " interactable=" + DescribeInteractable(interactable) +
                    " step=" + DescribeNavigationStep(step));
                SetNavigationBlockedDetail(
                    "transition interaction skipped reason=" + interactionReason +
                    " interactable=" + DescribeInteractable(interactable) +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            if (!TryTriggerNavigationTransitionInteraction(interactable))
            {
                SetNavigationBlockedDetail(
                    "transition interaction failed: safe trigger path rejected interactable=" + DescribeInteractable(interactable) +
                    " step=" + DescribeNavigationStep(step));
                LogNavigationTransitionDebug(
                    "Transition interaction failed: safe trigger path rejected interactable=" + DescribeInteractable(interactable) +
                    " step=" + DescribeNavigationStep(step));
                return false;
            }

            _lastNavigationInteractionAttemptTime = Time.unscaledTime;
            _autoWalkRecoveryAttempts = 0;
            ResetAutoWalkProgress();
            if (step.Kind == NavigationGraph.StepKind.Door)
            {
                _doorTraversalStepKey = BuildNavigationStepKey(step);
                _doorTraversalInteractionTriggered = true;
                _doorTraversalPostThresholdCommitted = false;
                Vector3 pushThroughPosition = BuildDoorTraversalPushThroughPosition(step, interactable);
                if (pushThroughPosition != Vector3.zero)
                    _doorTraversalPushThroughPosition = pushThroughPosition;
            }
            LogNavigationTransitionDebug(
                "Transition interaction fired interactable=" + DescribeInteractable(interactable) +
                " step=" + DescribeNavigationStep(step));

            if (step.TransitionWaitSeconds > 0f)
            {
                _autoWalkTransitionUntil = Time.unscaledTime + step.TransitionWaitSeconds;
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                ObjectTracker.StopTracking();
                LogNavigationTransitionDebug(
                    "Transition interaction started wait seconds=" + step.TransitionWaitSeconds.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
            }

            return true;
        }

        private static bool TryTriggerNavigationTransitionInteraction(InteractableObj interactable)
        {
            if (interactable == null || Singleton<GameController>.Instance == null)
                return false;

            GameController.SelectObjResult result = Singleton<GameController>.Instance.SelectObj(
                interactable,
                forceWithoutGlasses: false,
                delegateMethod: null,
                stopTime: false,
                ignoreGlassesAwakening: true,
                isFrontDoorInteraction: false);

            return result != GameController.SelectObjResult.FAILED;
        }

        private bool TryFindTransitionInteractable(NavigationGraph.PathStep step, out InteractableObj interactable)
        {
            interactable = null;

            if (BetterPlayerControl.Instance == null || Singleton<InteractableManager>.Instance == null)
                return false;

            if (TryFindOptionalOpenPassageDoorInteractable(step, out interactable))
                return true;

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            InteractableObj activeObject = Singleton<InteractableManager>.Instance.activeObject;
            bool prefersNamedConnector = HasNamedTransitionConnectors(step);
            if (prefersNamedConnector &&
                IsMatchingNamedTransitionInteractable(step, activeObject) &&
                IsInteractableWithinRange(activeObject, playerTransform.position))
            {
                interactable = activeObject;
                _instance?.LogNavigationTransitionDebug(
                    "Transition interactable resolved from active named connector interactable=" + DescribeInteractable(interactable) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            if (!prefersNamedConnector &&
                IsMatchingTransitionInteractable(step, activeObject) &&
                IsInteractableWithinRange(activeObject, playerTransform.position))
            {
                interactable = activeObject;
                _instance?.LogNavigationTransitionDebug(
                    "Transition interactable resolved from active object interactable=" + DescribeInteractable(interactable) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            InteractableObj[] candidates = FindObjectsOfType<InteractableObj>();
            float bestScore = float.MaxValue;
            bool foundNamedConnectorCandidate = false;
            for (int i = 0; i < candidates.Length; i++)
            {
                InteractableObj candidate = candidates[i];
                bool matchesNamedConnector = IsMatchingNamedTransitionInteractable(step, candidate);
                if (prefersNamedConnector && !matchesNamedConnector)
                    continue;

                if (!IsMatchingTransitionInteractable(step, candidate) || !IsInteractableWithinRange(candidate, playerTransform.position))
                    continue;

                float score = Vector3.Distance(candidate.transform.position, step.FromWaypoint) +
                    Vector3.Distance(candidate.transform.position, playerTransform.position);
                if (score >= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
                foundNamedConnectorCandidate = matchesNamedConnector;
            }

            if (interactable == null && prefersNamedConnector)
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    InteractableObj candidate = candidates[i];
                    if (!IsMatchingTransitionInteractable(step, candidate) || !IsInteractableWithinRange(candidate, playerTransform.position))
                        continue;

                    float score = Vector3.Distance(candidate.transform.position, step.FromWaypoint) +
                        Vector3.Distance(candidate.transform.position, playerTransform.position);
                    if (score >= bestScore)
                        continue;

                    bestScore = score;
                    interactable = candidate;
                }
            }

            if (interactable != null)
            {
                _instance?.LogNavigationTransitionDebug(
                    "Transition interactable resolved from search interactable=" + DescribeInteractable(interactable) +
                    " namedConnectorPreferred=" + prefersNamedConnector +
                    " namedConnectorMatched=" + foundNamedConnectorCandidate +
                    " score=" + bestScore.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
            }
            else
            {
                _instance?.LogNavigationTransitionDebug(
                    "Transition interactable search found nothing player=" + FormatVector3(playerTransform.position) +
                    " activeObject=" + DescribeInteractable(activeObject) +
                    " step=" + DescribeNavigationStep(step));
            }

            return interactable != null;
        }

        private static bool IsMatchingNamedTransitionInteractable(NavigationGraph.PathStep step, InteractableObj interactable)
        {
            return step != null &&
                interactable != null &&
                IsMatchingNamedTransitionConnectorName(step, interactable.name);
        }

        private static bool HasNamedTransitionConnectors(NavigationGraph.PathStep step)
        {
            return step != null &&
                step.ConnectorNames != null &&
                step.ConnectorNames.Length > 0;
        }

        private static bool IsMatchingNamedTransitionConnectorName(NavigationGraph.PathStep step, string connectorName)
        {
            if (step == null || string.IsNullOrEmpty(connectorName) || step.ConnectorNames == null)
                return false;

            for (int i = 0; i < step.ConnectorNames.Length; i++)
            {
                if (string.Equals(connectorName, step.ConnectorNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool TryFindOptionalOpenPassageDoorInteractable(NavigationGraph.PathStep step, out InteractableObj interactable)
        {
            interactable = null;
            if (!IsRunningOpenPassageTransitionSweepStep(step) ||
                BetterPlayerControl.Instance == null ||
                Singleton<InteractableManager>.Instance == null)
            {
                return false;
            }

            Transform playerTransform = BetterPlayerControl.Instance.transform;
            InteractableObj activeObject = Singleton<InteractableManager>.Instance.activeObject;
            if (IsMatchingOptionalOpenPassageDoorInteractable(step, activeObject, playerTransform.position))
            {
                interactable = activeObject;
                _instance?.LogNavigationTransitionDebug(
                    "Transition interactable resolved from active open-passage door interactable=" + DescribeInteractable(interactable) +
                    " step=" + DescribeNavigationStep(step));
                return true;
            }

            InteractableObj[] candidates = FindObjectsOfType<InteractableObj>();
            float bestScore = float.MaxValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                InteractableObj candidate = candidates[i];
                if (!IsMatchingOptionalOpenPassageDoorInteractable(step, candidate, playerTransform.position))
                    continue;

                float score = Vector3.Distance(candidate.transform.position, playerTransform.position);
                Vector3 sourceReference = GetOpenPassageSourceGuidanceOrigin(step);
                if (sourceReference == Vector3.zero)
                    sourceReference = GetOpenPassageSourceHandoffOrigin(step);

                if (sourceReference != Vector3.zero)
                    score += Vector3.Distance(candidate.transform.position, sourceReference);

                Vector3 routeStart = sourceReference;
                Vector3 routeEnd = GetOpenPassageDestinationApproachPosition(step);
                if (routeEnd == Vector3.zero)
                    routeEnd = GetOpenPassageDestinationClearPosition(step);

                if (routeStart != Vector3.zero && routeEnd != Vector3.zero)
                    score += DistanceToSegment(candidate.transform.position, routeStart, routeEnd) * 4f;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                interactable = candidate;
            }

            if (interactable != null)
            {
                _instance?.LogNavigationTransitionDebug(
                    "Transition interactable resolved from open-passage door search interactable=" + DescribeInteractable(interactable) +
                    " score=" + bestScore.ToString("0.00", CultureInfo.InvariantCulture) +
                    " step=" + DescribeNavigationStep(step));
            }

            return interactable != null;
        }

        private bool IsMatchingOptionalOpenPassageDoorInteractable(
            NavigationGraph.PathStep step,
            InteractableObj interactable,
            Vector3 playerPosition)
        {
            if (step == null ||
                interactable == null ||
                !interactable.gameObject.activeInHierarchy ||
                !IsRunningOpenPassageTransitionSweepStep(step) ||
                !IsInteractableWithinRange(interactable, playerPosition))
            {
                return false;
            }

            Door door = interactable.GetComponent<Door>();
            SlidingDoor slidingDoor = interactable.GetComponent<SlidingDoor>();
            if (door == null && slidingDoor == null)
                return false;

            Vector3 routeStart = GetOpenPassageSourceGuidanceOrigin(step);
            if (routeStart == Vector3.zero)
                routeStart = GetOpenPassageSourceHandoffOrigin(step);

            Vector3 routeEnd = GetOpenPassageDestinationApproachPosition(step);
            if (routeEnd == Vector3.zero)
                routeEnd = GetOpenPassageDestinationClearPosition(step);

            bool hasRouteSegment = false;
            float routeDistance = float.MaxValue;
            Vector3 flattenedCandidatePosition = interactable.transform.position;
            flattenedCandidatePosition.y = playerPosition.y;
            if (routeStart != Vector3.zero && routeEnd != Vector3.zero)
            {
                hasRouteSegment = true;
                routeStart.y = playerPosition.y;
                routeEnd.y = playerPosition.y;
                routeDistance = DistanceToSegment(flattenedCandidatePosition, routeStart, routeEnd);
                if (routeDistance > 3f)
                    return false;
            }

            if (TryGetZoneNameForInteractable(interactable, out string interactableZone) &&
                !string.IsNullOrEmpty(interactableZone) &&
                !IsZoneEquivalentToNavigationZone(interactableZone, step.FromZone) &&
                !IsZoneEquivalentToNavigationZone(interactableZone, step.ToZone) &&
                !IsAcceptedOverrideSourceZone(step, interactableZone) &&
                !IsAcceptedOverrideDestinationZone(step, interactableZone))
            {
                float relaxedPlayerDistance = Mathf.Max(2.5f, interactable.InteractionRadius + 0.5f);
                if (!hasRouteSegment ||
                    routeDistance > 2.5f ||
                    Vector3.Distance(playerPosition, interactable.transform.position) > relaxedPlayerDistance)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsMatchingTransitionInteractable(NavigationGraph.PathStep step, InteractableObj interactable)
        {
            if (step == null || interactable == null || !interactable.gameObject.activeInHierarchy)
                return false;

            if (IsMatchingNamedTransitionInteractable(step, interactable))
            {
                return true;
            }

            switch (step.Kind)
            {
                case NavigationGraph.StepKind.Teleporter:
                    return interactable.GetComponent<Teleporter>() != null;
                case NavigationGraph.StepKind.Door:
                    return interactable.GetComponent<Door>() != null || interactable.GetComponent<SlidingDoor>() != null;
                default:
                    return false;
            }
        }

        private static bool IsInteractableWithinRange(InteractableObj interactable, Vector3 playerPosition)
        {
            if (interactable == null)
                return false;

            float allowedDistance = Mathf.Max(AutoWalkConnectorSearchDistance, interactable.InteractionRadius + 1f);
            return Vector3.Distance(playerPosition, interactable.transform.position) <= allowedDistance;
        }

        private static bool CanAutoInteractWithStep(NavigationGraph.PathStep step, InteractableObj interactable, out string reason)
        {
            reason = null;
            if (step == null || interactable == null)
            {
                reason = "step or interactable missing";
                return false;
            }

            if (step.Kind == NavigationGraph.StepKind.Teleporter)
            {
                reason = interactable.GetComponent<Teleporter>() != null
                    ? "teleporter"
                    : "teleporter component missing";
                return interactable.GetComponent<Teleporter>() != null;
            }

            Door door = interactable.GetComponent<Door>();
            if (door != null)
            {
                if (door.locked)
                {
                    reason = "door locked";
                    return false;
                }

                if (door.open)
                {
                    reason = "door already open";
                    return false;
                }

                reason = "door closed";
                return true;
            }

            SlidingDoor slidingDoor = interactable.GetComponent<SlidingDoor>();
            if (slidingDoor != null)
            {
                if (slidingDoor.locked)
                {
                    reason = "sliding door locked";
                    return false;
                }

                if (slidingDoor.open)
                {
                    reason = "sliding door already open";
                    return false;
                }

                reason = "sliding door closed";
                return true;
            }

            reason = step.RequiresInteraction ? "generic required interaction" : "no supported interaction component";
            return step.RequiresInteraction;
        }

        private static bool IsAlreadyOpenInteractionReason(string reason)
        {
            return string.Equals(reason, "door already open", StringComparison.Ordinal) ||
                string.Equals(reason, "sliding door already open", StringComparison.Ordinal);
        }

        private static bool TryGetZonePosition(string zoneName, out Vector3 position)
        {
            position = Vector3.zero;

            if (Singleton<CameraSpaces>.Instance == null || Singleton<CameraSpaces>.Instance.zones == null || string.IsNullOrWhiteSpace(zoneName))
                return false;

            for (int i = 0; i < Singleton<CameraSpaces>.Instance.zones.Count; i++)
            {
                triggerzone zone = Singleton<CameraSpaces>.Instance.zones[i];
                if (zone == null || !string.Equals(zone.Name, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (zone.Position != Vector3.zero)
                {
                    position = zone.Position;
                    return true;
                }

                return false;
            }

            return false;
        }

        private string GetCurrentZoneNameForNavigation()
        {
            string rawCurrentZone = GetCurrentZoneNameInternal();
            if (string.IsNullOrEmpty(rawCurrentZone) || BetterPlayerControl.Instance == null)
                return rawCurrentZone;

            NavigationGraph.PathStep step = GetCurrentNavigationStep();
            string currentZone = rawCurrentZone;
            if (!string.IsNullOrEmpty(_navigationZoneOverride))
            {
                string currentStepKey = BuildNavigationStepKey(step);
                bool stepMatchesOverride = !string.IsNullOrEmpty(currentStepKey) &&
                    string.Equals(_navigationZoneOverrideStepKey, currentStepKey, StringComparison.Ordinal);
                bool overrideMatchesStepSource = step != null &&
                    !string.IsNullOrEmpty(step.FromZone) &&
                    IsZoneEquivalentToNavigationZone(_navigationZoneOverride, step.FromZone);
                bool rawMatchesOverride = IsZoneEquivalentToNavigationZone(rawCurrentZone, _navigationZoneOverride);
                bool rawShowsForwardProgress = (step != null &&
                        !string.IsNullOrEmpty(step.ToZone) &&
                        IsZoneEquivalentToNavigationZone(rawCurrentZone, step.ToZone)) ||
                    (!string.IsNullOrEmpty(_navigationTargetZone) && IsExactZoneMatch(rawCurrentZone, _navigationTargetZone));

                if (!stepMatchesOverride || !overrideMatchesStepSource || rawMatchesOverride || rawShowsForwardProgress)
                {
                    ClearNavigationZoneOverride();
                }
                else
                {
                    currentZone = _navigationZoneOverride;
                    LogNavigationAutoWalkDebug(
                        "Using sticky navigation zone override rawCurrentZone=" + rawCurrentZone +
                        " overrideZone=" + _navigationZoneOverride +
                        " step=" + DescribeNavigationStep(step));
                }
            }

            Vector3 destinationApproachWaypoint = GetOpenPassageDestinationApproachPosition(step);
            if (step == null ||
                step.Kind != NavigationGraph.StepKind.OpenPassage ||
                string.IsNullOrEmpty(step.FromZone) ||
                string.IsNullOrEmpty(step.ToZone) ||
                step.FromWaypoint == Vector3.zero ||
                destinationApproachWaypoint == Vector3.zero)
            {
                return currentZone;
            }

            string currentNavigationZone = GetNavigationZoneName(currentZone);
            if (ShouldSuppressOpenPassageBackwardZoneReport(step, currentZone, currentNavigationZone, out string fallbackZone, out string rewindReason))
            {
                LogNavigationAutoWalkDebug(
                    "Suppressing transient backward zone report " + rewindReason +
                    " step=" + DescribeNavigationStep(step));
                return fallbackZone;
            }

            if (string.IsNullOrEmpty(_navigationTargetZone) ||
                !IsExactZoneMatch(currentZone, _navigationTargetZone) ||
                IsExactZoneMatch(step.ToZone, _navigationTargetZone))
            {
                return currentZone;
            }

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            Vector3 fromWaypoint = step.FromWaypoint;
            fromWaypoint.y = playerPosition.y;
            Vector3 toWaypoint = destinationApproachWaypoint;
            toWaypoint.y = playerPosition.y;

            float fromDistance = Vector3.Distance(playerPosition, fromWaypoint);
            float toDistance = Vector3.Distance(playerPosition, toWaypoint);
            if (toDistance <= AutoWalkZoneBoundaryFallbackDistance)
            {
                return currentZone;
            }

            LogNavigationAutoWalkDebug(
                "Suppressing transient target-zone report rawCurrentZone=" + currentZone +
                " fallbackZone=" + step.FromZone +
                " fromDistance=" + fromDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " toDistance=" + toDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                " step=" + DescribeNavigationStep(step));
            return step.FromZone;
        }

        private static bool TryGetZoneNameForPosition(Vector3 position, out string zoneName)
        {
            zoneName = null;

            if (Singleton<CameraSpaces>.Instance == null || Singleton<CameraSpaces>.Instance.zones == null)
                return false;

            for (int i = 0; i < Singleton<CameraSpaces>.Instance.zones.Count; i++)
            {
                triggerzone zone = Singleton<CameraSpaces>.Instance.zones[i];
                if (zone == null)
                    continue;

                Bounds bounds = new Bounds(zone.Position, zone.Scale);
                if (!bounds.Contains(position))
                    continue;

                zoneName = zone.Name;
                return !string.IsNullOrEmpty(zoneName);
            }

            return false;
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

                float score = ScoreNavigableInteractableCandidate(interactable, candidate, candidateZone);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                resolvedInteractable = candidate;
                zoneName = candidateZone;
            }

            return resolvedInteractable != null && !string.IsNullOrEmpty(zoneName);
        }

        private static float ScoreNavigableInteractableCandidate(InteractableObj preferredInteractable, InteractableObj candidate, string candidateZone)
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

            string currentZone = GetCurrentZoneNameInternal();
            if (!string.IsNullOrEmpty(currentZone) &&
                AreZonesEquivalent(candidateZone, currentZone))
            {
                score += 50f;
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

        private bool TryResolveTrackedNavigationMode(
            string currentZone,
            out InteractableObj trackedInteractable,
            out string trackedZone,
            out string trackedLabel,
            out TrackedNavigationMode trackedNavigationMode,
            out Vector3 anchorPosition,
            out string anchorReason)
        {
            trackedInteractable = null;
            trackedZone = null;
            trackedLabel = null;
            trackedNavigationMode = TrackedNavigationMode.None;
            anchorPosition = Vector3.zero;
            anchorReason = null;

            if (!TryGetTrackedInteractable(out trackedInteractable) ||
                !TryGetTrackedInteractableZone(trackedInteractable, out trackedZone))
            {
                return false;
            }

            trackedLabel = GetTrackedInteractableLabel(trackedInteractable);
            if (IsExactZoneMatch(currentZone, trackedZone))
            {
                trackedNavigationMode = TrackedNavigationMode.DirectObject;
                return true;
            }

            if (!AreZonesEquivalent(currentZone, trackedZone))
                return false;

            if (TryGetZonePosition(trackedZone, out anchorPosition))
            {
                trackedNavigationMode = TrackedNavigationMode.EquivalentZoneAnchor;
                anchorReason = "subzone anchor before direct object";
                return true;
            }

            string trackedNavigationZone = GetNavigationZoneName(trackedZone);
            if (!string.IsNullOrEmpty(trackedNavigationZone) &&
                TryGetZonePosition(trackedNavigationZone, out anchorPosition))
            {
                trackedNavigationMode = TrackedNavigationMode.EquivalentZoneAnchor;
                anchorReason = "navigation-zone anchor before direct object";
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

            string currentZone = GetCurrentZoneNameInternal();
            if (!TryGetTrackedInteractableZone(trackedInteractable, out string trackedZone) ||
                !IsExactZoneMatch(trackedZone, currentZone))
            {
                return false;
            }

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            Vector3 targetPosition = trackedInteractable.transform.position;
            if (!TryGetTrackedInteractableNavigationTarget(
                    trackedInteractable,
                    currentZone,
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
            string currentZone,
            Vector3 playerPosition,
            out Vector3 targetPosition,
            out string debugDetail)
        {
            targetPosition = Vector3.zero;
            debugDetail = null;
            if (interactable == null)
                return false;

            string navigationZone = GetNavigationZoneName(currentZone);
            if (string.IsNullOrEmpty(navigationZone))
            {
                targetPosition = interactable.transform.position;
                targetPosition.y = playerPosition.y;
                debugDetail = "mode=raw-object zone=<null>";
                return true;
            }

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

            bool hasPreferredComponentId = TryGetTrackedInteractablePreferredComponentId(
                navigationZone,
                playerPosition,
                out int preferredComponentId,
                out string preferredComponentDetail);
            if (CanReuseTrackedInteractableApproachTarget(
                    interactable,
                    navigationZone,
                    referencePosition,
                    hasPreferredComponentId ? preferredComponentId : -1))
            {
                targetPosition = _trackedInteractableApproachTarget;
                targetPosition.y = playerPosition.y;
                debugDetail =
                    "mode=cached" +
                    " zone=" + navigationZone +
                    (hasPreferredComponentId
                        ? " preferredComponentId=" + preferredComponentId +
                          " preferredComponentDetail=" + (preferredComponentDetail ?? "<null>")
                        : string.Empty) +
                    " candidateSource=" + candidateDetail;
                return true;
            }

            string preferredComponentFailureDetail = null;
            int cachedPreferredComponentId = -1;
            if (hasPreferredComponentId &&
                LocalNavigationMaps.TryResolveApproachTargetForComponent(
                    navigationZone,
                    playerPosition,
                    referencePosition,
                    candidateTargets,
                    preferredComponentId,
                    out targetPosition,
                    out string componentResolutionDetail))
            {
                cachedPreferredComponentId = preferredComponentId;
                _trackedInteractableApproachId = interactable.Id;
                _trackedInteractableApproachZone = navigationZone;
                _trackedInteractableApproachPreferredComponentId = cachedPreferredComponentId;
                _trackedInteractableApproachReferencePosition = referencePosition;
                _trackedInteractableApproachTarget = targetPosition;
                targetPosition.y = playerPosition.y;
                debugDetail =
                    componentResolutionDetail +
                    " zone=" + navigationZone +
                    " candidateSource=" + candidateDetail +
                    " preferredComponentSource=player" +
                    " preferredComponentDetail=" + (preferredComponentDetail ?? "<null>");
                return true;
            }
            else if (hasPreferredComponentId)
            {
                preferredComponentFailureDetail =
                    " preferredComponentResolution=failed" +
                    " preferredComponentId=" + preferredComponentId +
                    " preferredComponentDetail=" + (preferredComponentDetail ?? "<null>");
            }

            if (!TryResolveTrackedInteractableApproachTarget(
                    navigationZone,
                    playerPosition,
                    referencePosition,
                    candidateTargets,
                    out targetPosition,
                    out string resolutionDetail))
            {
                targetPosition = interactable.transform.position;
                targetPosition.y = playerPosition.y;
                debugDetail = "mode=raw-object resolution=failed";
                return true;
            }

            _trackedInteractableApproachId = interactable.Id;
            _trackedInteractableApproachZone = navigationZone;
            _trackedInteractableApproachPreferredComponentId = cachedPreferredComponentId;
            _trackedInteractableApproachReferencePosition = referencePosition;
            _trackedInteractableApproachTarget = targetPosition;
            targetPosition.y = playerPosition.y;
            debugDetail =
                resolutionDetail +
                " zone=" + navigationZone +
                preferredComponentFailureDetail +
                " candidateSource=" + candidateDetail;
            return true;
        }

        private void ResetTrackedInteractableApproachTarget()
        {
            _trackedInteractableApproachId = null;
            _trackedInteractableApproachZone = null;
            _trackedInteractableApproachPreferredComponentId = -1;
            _trackedInteractableApproachReferencePosition = Vector3.zero;
            _trackedInteractableApproachTarget = Vector3.zero;
        }

        private bool CanReuseTrackedInteractableApproachTarget(
            InteractableObj interactable,
            string navigationZone,
            Vector3 referencePosition,
            int preferredComponentId)
        {
            return interactable != null &&
                !string.IsNullOrEmpty(interactable.Id) &&
                !string.IsNullOrEmpty(navigationZone) &&
                !string.IsNullOrEmpty(_trackedInteractableApproachId) &&
                !string.IsNullOrEmpty(_trackedInteractableApproachZone) &&
                string.Equals(_trackedInteractableApproachId, interactable.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_trackedInteractableApproachZone, navigationZone, StringComparison.OrdinalIgnoreCase) &&
                _trackedInteractableApproachPreferredComponentId == preferredComponentId &&
                GetFlatDistance(_trackedInteractableApproachReferencePosition, referencePosition) <= TrackedInteractableApproachRetargetDistance;
        }

        private bool TryGetTrackedInteractablePreferredComponentId(
            string navigationZone,
            Vector3 playerPosition,
            out int preferredComponentId,
            out string detail)
        {
            preferredComponentId = -1;
            detail = null;
            if (string.IsNullOrWhiteSpace(navigationZone) || !LocalNavigationMaps.IsAvailable)
                return false;

            List<LocalNavigationMaps.WalkableComponentSummary> components =
                LocalNavigationMaps.GetWalkableComponents(navigationZone);
            if (components == null || components.Count <= 1)
                return false;

            if (!LocalNavigationMaps.TryGetWalkableComponentId(
                    navigationZone,
                    playerPosition,
                    out preferredComponentId,
                    out Vector3 snappedPlayerPosition,
                    out string componentDetail))
            {
                return false;
            }

            detail =
                (componentDetail ?? "<null>") +
                " snappedPlayer=" + FormatVector3(snappedPlayerPosition);
            return preferredComponentId >= 0;
        }

        private bool TryResolveTrackedInteractableApproachTarget(
            string navigationZone,
            Vector3 playerPosition,
            Vector3 referencePosition,
            List<Vector3> candidateTargets,
            out Vector3 targetPosition,
            out string detail)
        {
            targetPosition = Vector3.zero;
            detail = null;
            if (candidateTargets == null || candidateTargets.Count < 1)
                return false;

            bool foundPathCandidate = false;
            float bestPathDistance = float.PositiveInfinity;
            float bestReferenceDistance = float.PositiveInfinity;
            if (!string.IsNullOrEmpty(navigationZone) && LocalNavigationMaps.IsAvailable)
            {
                for (int i = 0; i < candidateTargets.Count; i++)
                {
                    Vector3 candidateTarget = candidateTargets[i];
                    if (!LocalNavigationMaps.TryFindPath(
                            navigationZone,
                            playerPosition,
                            candidateTarget,
                            out List<Vector3> pathPoints,
                            out _) ||
                        pathPoints == null ||
                        pathPoints.Count < 1)
                    {
                        continue;
                    }

                    float pathDistance = ComputeFlatPathDistance(playerPosition, pathPoints);
                    Vector3 snappedTarget = pathPoints[pathPoints.Count - 1];
                    float referenceDistance = GetFlatDistance(snappedTarget, referencePosition);
                    if (foundPathCandidate &&
                        pathDistance > bestPathDistance + 0.01f)
                    {
                        continue;
                    }

                    if (foundPathCandidate &&
                        Mathf.Abs(pathDistance - bestPathDistance) <= 0.01f &&
                        referenceDistance >= bestReferenceDistance)
                    {
                        continue;
                    }

                    foundPathCandidate = true;
                    bestPathDistance = pathDistance;
                    bestReferenceDistance = referenceDistance;
                    targetPosition = snappedTarget;
                }
            }

            if (foundPathCandidate)
            {
                detail =
                    "mode=path-selected" +
                    " pathDistance=" + bestPathDistance.ToString("0.00", CultureInfo.InvariantCulture) +
                    " referenceDistance=" + bestReferenceDistance.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }

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

        private float ComputeFlatPathDistance(Vector3 startPosition, List<Vector3> pathPoints)
        {
            if (pathPoints == null || pathPoints.Count < 1)
                return 0f;

            float distance = 0f;
            Vector3 previousPoint = startPosition;
            previousPoint.y = 0f;
            for (int i = 0; i < pathPoints.Count; i++)
            {
                Vector3 currentPoint = pathPoints[i];
                currentPoint.y = 0f;
                distance += Vector3.Distance(previousPoint, currentPoint);
                previousPoint = currentPoint;
            }

            return distance;
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

        private bool TryGetRoomObjectTargets(string zoneName, out List<RoomObjectTarget> targets)
        {
            targets = new List<RoomObjectTarget>();
            if (string.IsNullOrEmpty(zoneName))
                return false;

            if (TryCollectRoomObjectTargets(zoneName, exactZoneOnly: true, targets))
                return true;

            string zoneFamilyKey = GetZoneFamilyKey(zoneName);
            if (string.IsNullOrEmpty(zoneFamilyKey))
                return false;

            return TryCollectRoomObjectTargets(zoneName, exactZoneOnly: false, targets);
        }

        private bool TryCollectRoomObjectTargets(string zoneName, bool exactZoneOnly, List<RoomObjectTarget> targets)
        {
            if (targets == null || string.IsNullOrEmpty(zoneName))
                return false;

            targets.Clear();
            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            Transform playerTransform = BetterPlayerControl.Instance != null ? BetterPlayerControl.Instance.transform : null;
            string currentZoneFamilyKey = exactZoneOnly ? null : GetZoneFamilyKey(zoneName);
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                if (!TryGetZoneNameForInteractable(candidate, out string candidateZone))
                    continue;

                bool zoneMatches = string.Equals(candidateZone, zoneName, StringComparison.OrdinalIgnoreCase);
                if (!zoneMatches && !exactZoneOnly)
                {
                    zoneMatches = string.Equals(GetZoneFamilyKey(candidateZone), currentZoneFamilyKey, StringComparison.OrdinalIgnoreCase);
                }

                if (!zoneMatches)
                {
                    continue;
                }

                string label = GetObjectFacingDisplayName(candidate);
                if (!IsUsableRoomObjectLabel(label))
                    continue;

                if (TryFindEquivalentRoomObjectTarget(targets, candidate, label, out RoomObjectTarget existingTarget))
                {
                    if (playerTransform == null)
                        continue;

                    float existingDistance = Vector3.Distance(playerTransform.position, existingTarget.Interactable.transform.position);
                    float candidateDistance = Vector3.Distance(playerTransform.position, candidate.transform.position);
                    if (candidateDistance < existingDistance)
                    {
                        existingTarget.Interactable = candidate;
                        existingTarget.Label = label;
                        existingTarget.ZoneName = candidateZone;
                    }

                    continue;
                }

                targets.Add(new RoomObjectTarget
                {
                    Interactable = candidate,
                    Label = label,
                    ZoneName = candidateZone
                });
            }

            if (targets.Count == 0)
                return false;

            if (playerTransform != null)
            {
                targets.Sort((left, right) =>
                {
                    float leftDistance = Vector3.Distance(playerTransform.position, left.Interactable.transform.position);
                    float rightDistance = Vector3.Distance(playerTransform.position, right.Interactable.transform.position);
                    int distanceComparison = leftDistance.CompareTo(rightDistance);
                    return distanceComparison != 0
                        ? distanceComparison
                        : string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase);
                });
            }
            else
            {
                targets.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase));
            }

            return true;
        }

        private static bool TryFindEquivalentRoomObjectTarget(List<RoomObjectTarget> targets, InteractableObj candidate, string label, out RoomObjectTarget equivalentTarget)
        {
            equivalentTarget = null;
            if (targets == null || candidate == null)
                return false;

            string candidateIdentityKey = GetRoomObjectIdentityKey(candidate);
            for (int i = 0; i < targets.Count; i++)
            {
                RoomObjectTarget existingTarget = targets[i];
                if (existingTarget == null || existingTarget.Interactable == null)
                    continue;

                if (!string.Equals(existingTarget.Label, label, StringComparison.OrdinalIgnoreCase))
                    continue;

                string existingIdentityKey = GetRoomObjectIdentityKey(existingTarget.Interactable);
                if (!string.Equals(existingIdentityKey, candidateIdentityKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                equivalentTarget = existingTarget;
                return true;
            }

            return false;
        }

        private static string GetRoomObjectIdentityKey(InteractableObj interactable)
        {
            if (interactable == null)
                return null;

            string internalName = NormalizeText(interactable.InternalName());
            if (!string.IsNullOrEmpty(internalName))
                return internalName;

            string sceneName = NormalizeIdentifierName(interactable.name);
            if (!string.IsNullOrEmpty(sceneName))
                return sceneName;

            return NormalizeText(interactable.Id);
        }

        private static bool IsUsableRoomObjectLabel(string label)
        {
            label = NormalizeText(label);
            if (string.IsNullOrEmpty(label))
                return false;

            if (label.StartsWith("Default hover text for", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(label, "Main Camera", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
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

        private static string BuildOpenPassageTransitionOverrideKey(NavigationGraph.PathStep step)
        {
            if (step == null)
                return null;

            return (step.FromZone ?? "<null>") + "->" + (step.ToZone ?? "<null>");
        }

        internal static void GetOpenPassageTransitionOverrideDiagnostics(
            out string status,
            out string detail,
            out int entryCount,
            out bool normalizedScalarArrays,
            out string path,
            out string fileSha256,
            out string fileLastWriteUtc)
        {
            EnsureOpenPassageTransitionOverridesLoaded();
            status = _openPassageTransitionOverrideLoadStatus;
            detail = _openPassageTransitionOverrideLoadDetail;
            entryCount = _openPassageTransitionOverrideEntryCount;
            normalizedScalarArrays = _openPassageTransitionOverrideNormalizedScalarArrays;
            path = _openPassageTransitionOverridePath;
            fileSha256 = _openPassageTransitionOverrideFileSha256;
            fileLastWriteUtc = _openPassageTransitionOverrideFileLastWriteUtc;
        }

        internal static string GetOpenPassageTransitionOverrideDiagnosticSummary()
        {
            GetOpenPassageTransitionOverrideDiagnostics(
                out string status,
                out string detail,
                out int entryCount,
                out bool normalizedScalarArrays,
                out _,
                out _,
                out _);

            return
                "status=" + (status ?? "<null>") +
                " entries=" + entryCount +
                " normalizedScalarArrays=" + normalizedScalarArrays +
                " detail=" + (detail ?? "<null>");
        }

        private static void EnsureOpenPassageTransitionOverridesLoaded()
        {
            if (_openPassageTransitionOverridesLoaded)
                return;

            _openPassageTransitionOverridesLoaded = true;
            OpenPassageTransitionOverrides.Clear();

            try
            {
                string path = Path.Combine(Paths.PluginPath, "navigation_transition_overrides.json");
                _openPassageTransitionOverridePath = path;
                _openPassageTransitionOverrideEntryCount = 0;
                _openPassageTransitionOverrideNormalizedScalarArrays = false;
                _openPassageTransitionOverrideFileSha256 = Main.TryComputeFileSha256(path);
                _openPassageTransitionOverrideFileLastWriteUtc = File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path).ToString("o")
                    : null;
                if (!File.Exists(path))
                {
                    _openPassageTransitionOverrideLoadStatus = "missing";
                    _openPassageTransitionOverrideLoadDetail = "Navigation transition overrides not found at: " + path;
                    Main.Log.LogWarning("Navigation transition overrides not found at: " + path);
                    return;
                }

                string rawJson = File.ReadAllText(path);
                string normalizedJson = NormalizeOpenPassageTransitionOverrideJson(rawJson);
                if (!string.Equals(rawJson, normalizedJson, StringComparison.Ordinal))
                {
                    _openPassageTransitionOverrideNormalizedScalarArrays = true;
                    Main.Log.LogWarning("Normalized scalar transition override arrays at: " + path);
                }

                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(normalizedJson)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(OpenPassageTransitionOverrideDocument));
                    OpenPassageTransitionOverrideDocument document = serializer.ReadObject(stream) as OpenPassageTransitionOverrideDocument;
                    if (document == null || document.Entries == null || document.Entries.Length == 0)
                    {
                        _openPassageTransitionOverrideLoadStatus = "empty";
                        _openPassageTransitionOverrideLoadDetail = "Navigation transition overrides file did not contain any usable entries.";
                        Main.Log.LogWarning("Navigation transition overrides file did not contain any usable entries.");
                        return;
                    }

                    for (int i = 0; i < document.Entries.Length; i++)
                    {
                        OpenPassageTransitionOverrideEntry entry = document.Entries[i];
                        if (entry == null ||
                            string.IsNullOrWhiteSpace(entry.FromZone) ||
                            string.IsNullOrWhiteSpace(entry.ToZone))
                        {
                            continue;
                        }

                        string key = (entry.FromZone ?? "<null>") + "->" + (entry.ToZone ?? "<null>");
                        OpenPassageTransitionOverrides[key] = new OpenPassageTransitionOverride
                        {
                            AcceptedSourceZones = entry.AcceptedSourceZones,
                            AcceptedDestinationZones = entry.AcceptedDestinationZones,
                            DestinationApproachBias = entry.DestinationApproachBias,
                            IntermediateWaypoints = ConvertOverrideWaypoints(entry.IntermediateWaypoints),
                            StepTimeoutSeconds = entry.StepTimeoutSeconds,
                            UseExplicitCrossingSegments = entry.UseExplicitCrossingSegments
                        };
                    }

                    _openPassageTransitionOverrideEntryCount = OpenPassageTransitionOverrides.Count;
                    _openPassageTransitionOverrideLoadStatus = "loaded";
                    _openPassageTransitionOverrideLoadDetail =
                        "Entries=" + OpenPassageTransitionOverrides.Count +
                        " normalizedScalarArrays=" + _openPassageTransitionOverrideNormalizedScalarArrays;
                    Main.Log.LogInfo("Navigation transition overrides loaded. Entries: " + OpenPassageTransitionOverrides.Count);
                }
            }
            catch (Exception ex)
            {
                _openPassageTransitionOverrideLoadStatus = "failed";
                _openPassageTransitionOverrideLoadDetail = ex.GetType().Name + ": " + ex.Message;
                Main.Log.LogError("Failed to load navigation transition overrides: " + ex);
            }
        }

        private static string NormalizeOpenPassageTransitionOverrideJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;

            string normalized = NormalizeTransitionOverrideScalarArrayProperty(json, "AcceptedSourceZones");
            normalized = NormalizeTransitionOverrideScalarArrayProperty(normalized, "AcceptedDestinationZones");
            return normalized;
        }

        private static string NormalizeTransitionOverrideScalarArrayProperty(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
                return json;

            string pattern = "\"" +
                System.Text.RegularExpressions.Regex.Escape(propertyName) +
                "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"";
            return System.Text.RegularExpressions.Regex.Replace(
                json,
                pattern,
                "\"" + propertyName + "\":[\"${value}\"]",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        private static Vector3[] ConvertOverrideWaypoints(SerializableVector3[] serializedWaypoints)
        {
            if (serializedWaypoints == null || serializedWaypoints.Length == 0)
                return null;

            var waypoints = new List<Vector3>(serializedWaypoints.Length);
            for (int i = 0; i < serializedWaypoints.Length; i++)
            {
                SerializableVector3 serializedWaypoint = serializedWaypoints[i];
                if (serializedWaypoint == null)
                    continue;

                waypoints.Add(serializedWaypoint.ToVector3());
            }

            return waypoints.Count > 0 ? waypoints.ToArray() : null;
        }

        private static bool TryGetOpenPassageTransitionOverride(NavigationGraph.PathStep step, out OpenPassageTransitionOverride transitionOverride)
        {
            EnsureOpenPassageTransitionOverridesLoaded();
            transitionOverride = null;
            string overrideKey = BuildOpenPassageTransitionOverrideKey(step);
            if (string.IsNullOrEmpty(overrideKey))
                return false;

            return OpenPassageTransitionOverrides.TryGetValue(overrideKey, out transitionOverride) &&
                transitionOverride != null;
        }

        private static bool IsAcceptedOverrideZone(string currentZone, string[] acceptedZones)
        {
            if (string.IsNullOrWhiteSpace(currentZone) || acceptedZones == null || acceptedZones.Length == 0)
                return false;

            for (int i = 0; i < acceptedZones.Length; i++)
            {
                string acceptedZone = acceptedZones[i];
                if (string.IsNullOrWhiteSpace(acceptedZone))
                    continue;

                if (IsZoneEquivalentToNavigationZone(currentZone, acceptedZone) ||
                    AreZonesEquivalent(currentZone, acceptedZone))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAcceptedOverrideSourceZone(NavigationGraph.PathStep step, string currentZone)
        {
            if (TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                IsAcceptedOverrideZone(currentZone, transitionOverride.AcceptedSourceZones))
            {
                return true;
            }

            return step != null && IsAcceptedOverrideZone(currentZone, step.AcceptedSourceZones);
        }

        private static bool IsAcceptedOverrideDestinationZone(NavigationGraph.PathStep step, string currentZone)
        {
            if (TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                IsAcceptedOverrideZone(currentZone, transitionOverride.AcceptedDestinationZones))
            {
                return true;
            }

            return step != null && IsAcceptedOverrideZone(currentZone, step.AcceptedDestinationZones);
        }

        private static float GetOpenPassageTransitionOverrideTimeoutSeconds(NavigationGraph.PathStep step)
        {
            float minimumTimeoutSeconds = GetMinimumOpenPassageTransitionSweepTimeoutSeconds(step);
            if (TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                transitionOverride.StepTimeoutSeconds > 0f)
            {
                return Mathf.Max(transitionOverride.StepTimeoutSeconds, minimumTimeoutSeconds);
            }

            if (step != null && step.ValidationTimeoutSeconds > 0f)
                return Mathf.Max(step.ValidationTimeoutSeconds, minimumTimeoutSeconds);

            return minimumTimeoutSeconds;
        }

        private static float GetMinimumOpenPassageTransitionSweepTimeoutSeconds(NavigationGraph.PathStep step)
        {
            float minimumTimeoutSeconds = TransitionSweepStepTimeoutSeconds;
            if (step == null)
                return minimumTimeoutSeconds;

            float pathLength = 0f;
            List<Vector3> navigationPoints = BuildOpenPassageGuidedNavigationPoints(step);
            if (navigationPoints != null && navigationPoints.Count > 1)
            {
                for (int i = 1; i < navigationPoints.Count; i++)
                {
                    Vector3 previousPoint = navigationPoints[i - 1];
                    Vector3 currentPoint = navigationPoints[i];
                    previousPoint.y = currentPoint.y;
                    pathLength += Vector3.Distance(previousPoint, currentPoint);
                }

                minimumTimeoutSeconds = Mathf.Max(
                    minimumTimeoutSeconds,
                    4f + (navigationPoints.Count - 1) * 1.1f);
            }
            else
            {
                Vector3 sourcePosition = GetOpenPassageSourceGuidanceOrigin(step);
                if (sourcePosition == Vector3.zero)
                    sourcePosition = GetOpenPassageSourceHandoffOrigin(step);

                Vector3 destinationPosition = GetOpenPassageDestinationApproachPosition(step);
                if (destinationPosition == Vector3.zero)
                    destinationPosition = GetOpenPassageDestinationClearPosition(step);

                if (sourcePosition != Vector3.zero && destinationPosition != Vector3.zero)
                {
                    sourcePosition.y = destinationPosition.y;
                    pathLength = Vector3.Distance(sourcePosition, destinationPosition);
                }
            }

            if (pathLength > 0f)
            {
                minimumTimeoutSeconds = Mathf.Max(
                    minimumTimeoutSeconds,
                    4.5f + pathLength / 2.5f);
            }

            return Mathf.Min(minimumTimeoutSeconds, 10f);
        }

        private bool TryGetOpenPassageGuidedNavigationTarget(
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            out Vector3 targetPosition,
            out int waypointIndex,
            out int waypointCount,
            out bool isFinalWaypoint)
        {
            targetPosition = Vector3.zero;
            waypointIndex = -1;
            waypointCount = 0;
            isFinalWaypoint = false;

            List<Vector3> navigationPoints = BuildOpenPassageGuidedNavigationPoints(step);
            if (navigationPoints == null || navigationPoints.Count == 0)
                return false;

            waypointCount = navigationPoints.Count;
            int currentIndex = Mathf.Clamp(_openPassageOverrideWaypointIndex, 0, waypointCount - 1);
            float waypointAdvanceDistance = GetOpenPassageGuidedWaypointAdvanceDistance();
            while (currentIndex < waypointCount - 1)
            {
                Vector3 currentTarget = navigationPoints[currentIndex];
                currentTarget.y = playerPosition.y;
                if (Vector3.Distance(playerPosition, currentTarget) > waypointAdvanceDistance)
                    break;

                currentIndex++;
            }

            _openPassageOverrideWaypointIndex = currentIndex;
            targetPosition = navigationPoints[currentIndex];
            waypointIndex = currentIndex;
            isFinalWaypoint = currentIndex >= waypointCount - 1;
            return true;
        }

        private static bool UsesOverrideOnlyOpenPassageGuidance(NavigationGraph.PathStep step)
        {
            return step != null &&
                TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                !transitionOverride.UseExplicitCrossingSegments &&
                transitionOverride.IntermediateWaypoints != null &&
                transitionOverride.IntermediateWaypoints.Length > 0;
        }

        private static List<Vector3> BuildOpenPassageGuidedNavigationPoints(NavigationGraph.PathStep step)
        {
            if (step == null)
                return null;

            Vector3 sourceStart = GetOpenPassageSourceGuidanceOrigin(step);
            if (sourceStart == Vector3.zero)
                sourceStart = GetOpenPassageSourceHandoffOrigin(step);

            Vector3 finalApproachPoint = GetOpenPassageDestinationApproachPosition(step);
            if (finalApproachPoint == Vector3.zero)
                finalApproachPoint = GetOpenPassageDestinationClearPosition(step);

            var candidates = new List<GuidedNavigationPoint>();
            int sequence = 0;
            bool useOverrideOnlyNavigationPoints = false;
            if (TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                transitionOverride.IntermediateWaypoints != null)
            {
                useOverrideOnlyNavigationPoints =
                    transitionOverride.IntermediateWaypoints.Length > 0 &&
                    !transitionOverride.UseExplicitCrossingSegments;

                for (int i = 0; i < transitionOverride.IntermediateWaypoints.Length; i++)
                {
                    AddGuidedNavigationPoint(candidates, transitionOverride.IntermediateWaypoints[i], sourceStart, finalApproachPoint, ref sequence);
                }
            }

            if (!useOverrideOnlyNavigationPoints &&
                step.NavigationPoints != null)
            {
                for (int i = 0; i < step.NavigationPoints.Length; i++)
                {
                    Vector3 navigationPoint = step.NavigationPoints[i];
                    if (navigationPoint == Vector3.zero)
                        continue;

                    if (sourceStart != Vector3.zero &&
                        Vector3.Distance(navigationPoint, sourceStart) <= OpenPassageGuidedWaypointDedupDistance)
                    {
                        continue;
                    }

                    if (finalApproachPoint != Vector3.zero &&
                        Vector3.Distance(navigationPoint, finalApproachPoint) <= OpenPassageGuidedWaypointDedupDistance)
                    {
                        continue;
                    }

                    AddGuidedNavigationPoint(candidates, navigationPoint, sourceStart, finalApproachPoint, ref sequence);
                }
            }

            Vector3 sourceClearPoint = GetOpenPassageSourceHandoffOrigin(step);
            if (!useOverrideOnlyNavigationPoints &&
                sourceClearPoint != Vector3.zero &&
                (sourceStart == Vector3.zero ||
                 Vector3.Distance(sourceClearPoint, sourceStart) > OpenPassageGuidedWaypointDedupDistance))
            {
                AddGuidedNavigationPoint(candidates, sourceClearPoint, sourceStart, finalApproachPoint, ref sequence);
            }

            if (!useOverrideOnlyNavigationPoints)
            {
                Vector3 destinationClearPoint = GetOpenPassageDestinationClearPosition(step);
                AddGuidedNavigationPoint(candidates, destinationClearPoint, sourceStart, finalApproachPoint, ref sequence);
            }

            AddGuidedNavigationPoint(candidates, finalApproachPoint, sourceStart, finalApproachPoint, ref sequence);

            if (candidates.Count == 0)
                return null;

            candidates.Sort((left, right) =>
            {
                int progressComparison = left.Progress.CompareTo(right.Progress);
                if (progressComparison != 0)
                    return progressComparison;

                return left.Sequence.CompareTo(right.Sequence);
            });

            var navigationPoints = new List<Vector3>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                navigationPoints.Add(candidates[i].Position);
            }

            return navigationPoints;
        }

        private static void AddGuidedNavigationPoint(
            List<GuidedNavigationPoint> candidates,
            Vector3 point,
            Vector3 sourceStart,
            Vector3 destinationEnd,
            ref int sequence)
        {
            if (candidates == null || point == Vector3.zero)
                return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (Vector3.Distance(candidates[i].Position, point) <= OpenPassageGuidedWaypointDedupDistance)
                    return;
            }

            candidates.Add(new GuidedNavigationPoint
            {
                Position = point,
                Progress = ComputeGuidedNavigationProgress(sourceStart, destinationEnd, point, sequence),
                Sequence = sequence
            });
            sequence++;
        }

        private static float ComputeGuidedNavigationProgress(
            Vector3 sourceStart,
            Vector3 destinationEnd,
            Vector3 point,
            int fallbackSequence)
        {
            Vector3 direction = destinationEnd - sourceStart;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
                return fallbackSequence;

            direction.Normalize();
            Vector3 offset = point - sourceStart;
            offset.y = 0f;
            return Vector3.Dot(offset, direction);
        }

        private static Vector3 BuildOpenPassageGuidedMovementTarget(Vector3 playerPosition, Vector3 actualWaypoint)
        {
            if (actualWaypoint == Vector3.zero)
                return Vector3.zero;

            Vector3 flattenedWaypoint = actualWaypoint;
            flattenedWaypoint.y = playerPosition.y;
            Vector3 direction = flattenedWaypoint - playerPosition;
            direction.y = 0f;
            float distance = direction.magnitude;
            if (distance <= AutoWalkOpenPassageHandoffDistance || distance <= 0.0001f)
                return actualWaypoint;

            Vector3 progressiveTarget = playerPosition + direction / distance * AutoWalkOpenPassageHandoffDistance;
            progressiveTarget.y = actualWaypoint.y != 0f ? actualWaypoint.y : playerPosition.y;
            return progressiveTarget;
        }

        private bool HasReachedOpenPassageOverrideCompletion(NavigationGraph.PathStep step)
        {
            if (step == null || BetterPlayerControl.Instance == null)
                return false;

            if (!TryGetOpenPassageGuidedNavigationTarget(
                    step,
                    BetterPlayerControl.Instance.transform.position,
                    out Vector3 targetPosition,
                    out _,
                    out _,
                    out bool isFinalWaypoint) ||
                !isFinalWaypoint)
            {
                return false;
            }

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            targetPosition.y = playerPosition.y;
            return Vector3.Distance(playerPosition, targetPosition) <= AutoWalkArrivalDistance;
        }

        private static Vector3 GetOpenPassageSourceSegmentTarget(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                transitionOverride.UseExplicitCrossingSegments &&
                GetOpenPassageSourceHandoffOrigin(step) != Vector3.zero)
            {
                return GetOpenPassageSourceHandoffOrigin(step);
            }

            return GetOpenPassageDestinationApproachPosition(step);
        }

        private static Vector3 GetOpenPassageDestinationWaypointPosition(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                transitionOverride.UseExplicitCrossingSegments &&
                GetOpenPassageDestinationClearPosition(step) != Vector3.zero)
            {
                return GetOpenPassageDestinationClearPosition(step);
            }

            return GetOpenPassageDestinationApproachPosition(step);
        }

        private static Vector3 BlendOpenPassageDestinationApproach(
            Vector3 crossingAnchor,
            Vector3 waypoint,
            float bias)
        {
            Vector3 blended = Vector3.Lerp(crossingAnchor, waypoint, Mathf.Clamp01(bias));
            blended.y = waypoint.y != 0f ? waypoint.y : crossingAnchor.y;
            return blended;
        }

        private static Vector3 GetOpenPassageDestinationApproachPosition(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            Vector3 destinationApproachPoint = step.DestinationApproachPoint != Vector3.zero
                ? step.DestinationApproachPoint
                : step.ToWaypoint;
            Vector3 destinationClearPoint = GetOpenPassageDestinationClearPosition(step);

            if (TryGetOpenPassageTransitionOverride(step, out OpenPassageTransitionOverride transitionOverride) &&
                destinationClearPoint != Vector3.zero &&
                destinationApproachPoint != Vector3.zero &&
                transitionOverride.DestinationApproachBias > 0f)
            {
                return BlendOpenPassageDestinationApproach(
                    destinationClearPoint,
                    destinationApproachPoint,
                    transitionOverride.DestinationApproachBias);
            }

            if (destinationApproachPoint != Vector3.zero)
                return destinationApproachPoint;

            return destinationClearPoint;
        }

        private static Vector3 GetOpenPassageDestinationClearPosition(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (step.DestinationClearPoint != Vector3.zero)
                return step.DestinationClearPoint;

            if (step.ToCrossingAnchor != Vector3.zero)
                return step.ToCrossingAnchor;

            return step.ToWaypoint;
        }

        private static Vector3 GetOpenPassageSourceHandoffOrigin(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (step.SourceClearPoint != Vector3.zero)
                return step.SourceClearPoint;

            if (step.FromCrossingAnchor != Vector3.zero)
                return step.FromCrossingAnchor;

            return step.FromWaypoint;
        }

        private static Vector3 GetOpenPassageSourceGuidanceOrigin(NavigationGraph.PathStep step)
        {
            if (step == null)
                return Vector3.zero;

            if (step.SourceApproachPoint != Vector3.zero)
                return step.SourceApproachPoint;

            if (step.FromWaypoint != Vector3.zero)
                return step.FromWaypoint;

            return GetOpenPassageSourceHandoffOrigin(step);
        }

        private static bool TryGetSegmentMetrics(
            Vector3 segmentStart,
            Vector3 segmentEnd,
            Vector3 playerPosition,
            out Vector3 segmentDirection,
            out float playerProgress,
            out float segmentLength)
        {
            segmentDirection = Vector3.zero;
            playerProgress = 0f;
            segmentLength = 0f;

            Vector3 direction = segmentEnd - segmentStart;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
                return false;

            segmentLength = direction.magnitude;
            segmentDirection = direction / segmentLength;

            Vector3 playerOffset = playerPosition - segmentStart;
            playerOffset.y = 0f;
            playerProgress = Vector3.Dot(playerOffset, segmentDirection);
            return true;
        }

        private static float ComputeCommittedOpenPassageTargetProgress(
            float playerProgress,
            float segmentLength,
            float handoffDistance,
            float committedProgress,
            bool forceAdvance,
            float overshootDistance = 0f)
        {
            float stepDistance = Mathf.Max(handoffDistance, 0.1f);
            float maxProgress = Mathf.Max(segmentLength, 0f) + Mathf.Max(overshootDistance, 0f);
            float clampedPlayerProgress = Mathf.Clamp(playerProgress, 0f, maxProgress);
            float clampedCommittedProgress = Mathf.Clamp(committedProgress, 0f, maxProgress);
            float reachThreshold = Mathf.Min(stepDistance * 0.5f, 1.25f);

            if (forceAdvance ||
                clampedCommittedProgress <= 0f ||
                clampedPlayerProgress >= Mathf.Max(0f, clampedCommittedProgress - reachThreshold))
            {
                clampedCommittedProgress = Mathf.Min(
                    Mathf.Max(clampedCommittedProgress, clampedPlayerProgress) + stepDistance,
                    maxProgress);
            }

            return clampedCommittedProgress;
        }

        private static bool TryGetOpenPassageSourceHandoffMetrics(
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            out float playerProgress,
            out float segmentLength)
        {
            playerProgress = 0f;
            segmentLength = 0f;

            if (step == null)
                return false;

            Vector3 handoffStart = GetOpenPassageSourceGuidanceOrigin(step);
            Vector3 handoffDestination = GetOpenPassageSourceSegmentTarget(step);
            if (handoffStart == Vector3.zero || handoffDestination == Vector3.zero)
                return false;

            return TryGetSegmentMetrics(
                handoffStart,
                handoffDestination,
                playerPosition,
                out _,
                out playerProgress,
                out segmentLength);
        }

        private static bool TryGetOpenPassageDestinationHandoffMetrics(
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            out float playerProgress,
            out float segmentLength)
        {
            playerProgress = 0f;
            segmentLength = 0f;

            if (step == null || step.ToCrossingAnchor == Vector3.zero || step.ToWaypoint == Vector3.zero)
                return false;

            return TryGetSegmentMetrics(
                step.ToCrossingAnchor,
                step.ToWaypoint,
                playerPosition,
                out _,
                out playerProgress,
                out segmentLength);
        }

        private static Vector3 BuildOpenPassageDestinationHandoffPosition(
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            float handoffDistance,
            float progressFloor,
            out float nextProgress)
        {
            nextProgress = 0f;

            if (step == null)
                return Vector3.zero;

            if (step.ToCrossingAnchor != Vector3.zero && step.ToWaypoint != Vector3.zero)
            {
                Vector3 handoffStart = step.ToCrossingAnchor;
                if (!TryGetSegmentMetrics(
                    handoffStart,
                    step.ToWaypoint,
                    playerPosition,
                    out Vector3 approachDirection,
                    out float progress,
                    out float segmentLength))
                {
                    return step.ToWaypoint;
                }

                nextProgress = ComputeCommittedOpenPassageTargetProgress(
                    progress,
                    segmentLength,
                    handoffDistance,
                    progressFloor,
                    forceAdvance: false,
                    overshootDistance: handoffDistance);

                Vector3 progressiveHandoffPosition = handoffStart + approachDirection * nextProgress;
                progressiveHandoffPosition.y = step.ToWaypoint.y != 0f ? step.ToWaypoint.y : handoffStart.y;
                return progressiveHandoffPosition;
            }

            if (step.ToWaypoint == Vector3.zero)
                return step.FromWaypoint;

            Vector3 handoffPosition = step.ToWaypoint;
            Vector3 handoffDirection = step.ToWaypoint - step.FromWaypoint;
            handoffDirection.y = 0f;

            if (handoffDirection.sqrMagnitude <= 0.0001f)
                return handoffPosition;

            handoffDirection.Normalize();
            nextProgress = Mathf.Max(handoffDistance, 0.1f);
            if (progressFloor > 0f)
                nextProgress = Mathf.Max(nextProgress, progressFloor);

            handoffPosition += handoffDirection * nextProgress;
            handoffPosition.y = step.ToWaypoint.y != 0f ? step.ToWaypoint.y : step.FromWaypoint.y;
            return handoffPosition;
        }

        private static Vector3 BuildOpenPassageSourceHandoffPosition(
            NavigationGraph.PathStep step,
            Vector3 playerPosition,
            float handoffDistance,
            float progressFloor,
            out float nextProgress)
        {
            nextProgress = 0f;

            if (step == null)
                return Vector3.zero;

            Vector3 handoffStart = GetOpenPassageSourceGuidanceOrigin(step);
            Vector3 handoffDestination = GetOpenPassageSourceSegmentTarget(step);
            if (handoffStart == Vector3.zero || handoffDestination == Vector3.zero)
                return handoffDestination != Vector3.zero ? handoffDestination : step.ToWaypoint;

            if (!TryGetSegmentMetrics(
                handoffStart,
                handoffDestination,
                playerPosition,
                out Vector3 handoffDirection,
                out float progress,
                out float segmentLength))
            {
                return handoffStart;
            }

            float targetProgress = ComputeCommittedOpenPassageTargetProgress(
                progress,
                segmentLength,
                handoffDistance,
                progressFloor,
                forceAdvance: false);

            nextProgress = targetProgress;

            Vector3 handoffPosition = handoffStart + handoffDirection * targetProgress;
            handoffPosition.y = handoffDestination.y != 0f ? handoffDestination.y : handoffStart.y;
            return handoffPosition;
        }

        private void LogNavigationTargetDebug(string snapshot)
        {
            if (!ShouldLogNavigationDebugSnapshot(snapshot, _lastNavigationTargetDebugSnapshot))
                return;

            _lastNavigationTargetDebugSnapshot = snapshot;
            DebugLogger.Log(LogCategory.State, "AccessibilityWatcher", snapshot);
        }

        private void LogNavigationTrackerDebug(string snapshot)
        {
            if (!ShouldLogNavigationDebugSnapshot(snapshot, _lastNavigationTrackerDebugSnapshot))
                return;

            _lastNavigationTrackerDebugSnapshot = snapshot;
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
                 snapshot.IndexOf("office->office_closet", StringComparison.Ordinal) >= 0);
        }

        private static string FormatVector3(Vector3 value)
        {
            return "(" +
                value.x.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                value.y.ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                value.z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        private static string DescribeNavigationStep(NavigationGraph.PathStep step)
        {
            if (step == null)
                return "<null>";

            return (step.FromZone ?? "<null>") +
                "->" + (step.ToZone ?? "<null>") +
                " kind=" + step.Kind +
                " interaction=" + step.RequiresInteraction +
                " connector=" + (step.ConnectorName ?? "<null>") +
                " from=" + FormatVector3(step.FromWaypoint) +
                " to=" + FormatVector3(step.ToWaypoint) +
                " fromCross=" + FormatVector3(step.FromCrossingAnchor) +
                " toCross=" + FormatVector3(step.ToCrossingAnchor);
        }

        private static string DescribeNavigationPath(List<NavigationGraph.PathStep> path)
        {
            if (path == null)
                return "<null>";

            if (path.Count == 0)
                return "<same-room>";

            var parts = new List<string>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                NavigationGraph.PathStep step = path[i];
                if (step == null)
                {
                    parts.Add("<null>");
                    continue;
                }

                parts.Add((step.FromZone ?? "<null>") + "->" + (step.ToZone ?? "<null>") + ":" + step.Kind);
            }

            return string.Join(" | ", parts.ToArray());
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
            _tutorialFrontDoorField = typeof(TutorialController).GetField("frontDoor", flags);
            _tutorialComputerField = typeof(TutorialController).GetField("computer", flags);
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

        private static string GetZoneFamilyKey(string zoneName)
        {
            string cleaned = NormalizeText(zoneName);
            if (string.IsNullOrEmpty(cleaned))
                return null;

            int endIndex = cleaned.Length;
            while (endIndex > 0 && char.IsDigit(cleaned[endIndex - 1]))
            {
                endIndex--;
            }

            cleaned = cleaned.Substring(0, endIndex).TrimEnd('_', '-', ' ');
            return string.IsNullOrEmpty(cleaned)
                ? null
                : cleaned.ToLowerInvariant();
        }

        private static bool IsExactZoneMatch(string firstZone, string secondZone)
        {
            return !string.IsNullOrWhiteSpace(firstZone) &&
                !string.IsNullOrWhiteSpace(secondZone) &&
                string.Equals(firstZone, secondZone, StringComparison.OrdinalIgnoreCase);
        }

        private static bool AreZonesEquivalent(string firstZone, string secondZone)
        {
            if (string.IsNullOrWhiteSpace(firstZone) || string.IsNullOrWhiteSpace(secondZone))
                return false;

            if (IsExactZoneMatch(firstZone, secondZone))
                return true;

            return string.Equals(GetZoneFamilyKey(firstZone), GetZoneFamilyKey(secondZone), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetNavigationZoneName(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return null;

            if (NavigationGraph.ContainsZone(zoneName))
                return zoneName;

            string zoneFamilyKey = GetZoneFamilyKey(zoneName);
            if (!string.IsNullOrEmpty(zoneFamilyKey) && NavigationGraph.ContainsZone(zoneFamilyKey))
                return zoneFamilyKey;

            return null;
        }

        private static bool IsZoneEquivalentToNavigationZone(string runtimeZone, string navigationZone)
        {
            if (string.IsNullOrWhiteSpace(runtimeZone) || string.IsNullOrWhiteSpace(navigationZone))
                return false;

            string normalizedRuntimeZone = GetNavigationZoneName(runtimeZone);
            if (!string.IsNullOrEmpty(normalizedRuntimeZone) &&
                string.Equals(normalizedRuntimeZone, navigationZone, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return AreZonesEquivalent(runtimeZone, navigationZone);
        }

        private static bool IsCurrentZoneEquivalentTo(string navigationZone)
        {
            return IsZoneEquivalentToNavigationZone(GetCurrentZoneNameInternal(), navigationZone);
        }
    }
}
