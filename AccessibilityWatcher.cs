using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;
using System.Text;
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
    // Run our FixedUpdate BEFORE BetterPlayerControl.FixedUpdate every physics tick. The game
    // reads `move`/`look` at the START of its FixedUpdate (BetterPlayerControl.cs:912) and then
    // OVERWRITES them from live axes at the END (L968-969, gated on TUTORIAL_STATE_0_ANIMATIONS,
    // which is set in normal play). So our value survives for exactly ONE read and is then zeroed.
    // Re-asserting in our own FixedUpdate only works if we run FIRST; with the default (undefined)
    // order the game often runs first and reads the zeroed value → the follower freezes with a
    // valid command cached and velocity=0 (the partial-sweep 2026-06-16 no-blocker stalls). A
    // negative execution order guarantees we write before the game reads, every tick, closing the
    // race regardless of frame rate. See [[project-navigation-follower-speed-deadzone]].
    [DefaultExecutionOrder(-100)]
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
        private const float AutoWalkLookScaleDegrees = 45f;
        // Binary heading gate: the follower walks at full move inside this cos(turn) cone and
        // turns in place (move=0) outside it. cos(70°)≈0.342. A CONTINUOUS cos(turn) scale is
        // what we must NOT use — it lands move in (0,0.2) across the ~78-90° band, below BOTH
        // the game's 0.2 translate dead-zone (player frozen) AND the watchdog's move test (so it
        // scores the freeze as "turning" and never times out). The gate keeps move firmly on/off.
        private const float AutoWalkFacingGateCosThreshold = 0.342f;
        // Max seconds the post-arrival turn-to-face phase runs before we accept arrival
        // regardless of whether the game's raycast selected the target. Generous enough to
        // complete a full turn-and-pitch toward an overhead/odd object, short enough that a
        // never-selectable target doesn't hang. Looking counts as interaction.
        private const float AutoWalkFaceTimeoutSeconds = 3f;
        private const float AutoWalkProgressDistance = 0.35f;
        private const float AutoWalkBlockedTimeoutSeconds = 2f;
        // Within this distance of the FINAL waypoint, the follower drops the binary heading gate and
        // drives straight at the cell so it CONVERGES (settles) instead of orbiting. Larger than
        // SimpleNavBridge.FinalArrivalRadius (0.3m) so the settle engages with room to close in.
        private const float CloseRangeSettleM = 0.7f;
        // Extra grace a turn-in-place gets on top of the blocked timeout before a no-progress
        // turn is treated as stuck. A full 180° turn at AutoWalkLookScaleDegrees completes well
        // inside this, so only an oscillating/geometrically-impossible turn ever reaches it.
        private const float AutoWalkTurnGraceSeconds = 1.5f;
        // Pure-pursuit lookahead distance (metres). The route executor aims at a
        // point this far ahead along the planned polyline (projected from the
        // player), rather than at the next waypoint vertex. Small enough to track
        // corners tightly (so the player stays in narrow corridors and doorways)
        // but large enough to avoid jitter. ~1 capsule-diameter + margin.
        // See [[project-navigation-executor-corner-stall]].
        private const float AutoWalkPursuitLookahead = 1.5f;
        private const float TrackedInteractableApproachClearanceDistance = 0.9f;
        private const float TrackedInteractableApproachRetargetDistance = 0.75f;
        private const float TrackedInteractableApproachMinimumExtent = 0.35f;
        private const float InteractableZoneFallbackDistance = 8f;
        // NOTE: a large block of door/follower RECOVERY constants was retired here,
        // all declaration-only (never read) leftovers from removed systems:
        //   - DoorTraversal* / DoorPushThrough* / DoorThresholdAdvance* /
        //     DoorPostInteractionFallback* / DoorCommittedSourceRecovery* — the door
        //     push-through recovery state machine. The planner now lands the player at
        //     an authoritative operable_from_cells standpoint outside the swing arc, so
        //     doors open via the normal TryOpenActiveDoorIfNeeded path (confirmed
        //     in-game: "SimpleNav fired Interact ... openAfter=True" across multi-door
        //     routes, with no recovery path firing).
        //   - LocalNavigation* (A*-around-obstacles fallback) and UnityNavMeshFallback*
        //     (Unity NavMesh last resort) and AutoWalkMaxRecoveryAttempts — their
        //     consumer methods are already gone.
        //   - TransitionFacingAlignment* — a removed stair/floor-transition facing
        //     pre-align step.
        // See [[project-navigation-door-operability-cells]].
        private const float TutorialGiftApproachRadius = 1.25f;
        // NOTE: the C#-side door-approach cap (SimpleNavDoorTargetRadius=3m) was retired.
        // Door goals now come exclusively from the bake's operable_from_cells, which
        // override the planner's goal disc entirely, so a C# door radius bounded only a
        // discarded pre-snap. The door-approach distance now lives in ONE place — the
        // bake's DOOR_OPERABLE_RADIUS_M — removing the cross-runtime sync hazard.
        // See [[project-navigation-door-operability-cells]].
        private const int VkUp = 0x26;
        private const int VkDown = 0x28;
        private const int VkLeft = 0x25;
        private const int VkRight = 0x27;
        private const int VkReturn = 0x0D;
        private const int VkSpace = 0x20;
        private const int VkBackspace = 0x08; // VK_BACK
        private const int VkPageUp = 0x21;   // VK_PRIOR
        private const int VkPageDown = 0x22; // VK_NEXT

        private static readonly Regex RichTextRegex = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex SpriteTagRegex = new Regex(
            "<sprite(?:=\"(?<asset>[^\"]*)\"|\\s+name=\"(?<name>[^\"]*)\")?\\s*(?:index=(?<idx>\\d+))?[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static FieldInfo _iconMarkupCurrentMapField;
        private static FieldInfo _iconMarkupKeyboardMapField;
        private static FieldInfo _iconMarkupControllerMapField;
        private static FieldInfo _spriteBindingPairsField;
        private static FieldInfo _spriteBindingPairNameField;
        private static FieldInfo _spriteBindingPairIdField;
        private static bool _glyphReflectionResolved;
        private static readonly Dictionary<object, Dictionary<int, string>> _spriteReverseMaps =
            new Dictionary<object, Dictionary<int, string>>();
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
        // Deferred pose-card description (first-meet AwakenSplashScreen / ending ResultSplashScreen).
        // Both card Initialize() methods START a stinger SFX in the same call, so speaking
        // immediately would be lost under it. We hold the description until _pendingCardPoseNotBefore
        // to clear the stinger, mirroring the _pendingDateADexEntry* deferral below.
        private static int _pendingCardPoseRequested;
        private static string _pendingCardPoseDesc;
        private static float _pendingCardPoseNotBefore;
        private static float _pendingCardPoseExpiresAt;
        // The pose-card description most recently spoken, kept so Ctrl+F1 can repeat it while the
        // card is still on screen. Plain Say() leaves it in _lastSpokenText, but any later speech
        // (menu focus, a hover) overwrites that buffer; serving the pose text from the context-aware
        // repeat tier (see TryBuildCardPoseAnnouncement) keeps repeat working for the whole card.
        private static string _lastCardPoseDesc;
        // Delay before a pose-card description is spoken, letting the card stinger's loud attack
        // pass so the speech lands over the quieter tail. The stinger clips run 6.1s (awaken) to
        // 11.6s (love ending) — waiting for the whole clip would leave the player in silence and
        // could outlast the card — so we wait past the attack, not the full clip. 3s was confirmed
        // comfortable in-game.
        private const float CardPoseSpeechDelaySeconds = 3f;
        // Speak-by window after the delay. Must comfortably exceed the longest stinger so a late
        // poll never drops the description (the love-ending stinger alone is ~11.6s).
        private const float CardPoseSpeechWindowSeconds = 20f;
        private static float _suppressInitialSpecsAnnouncementsUntil;
        // One-shot guard for the live capsule-dimension diagnostic. The bake assumes
        // CAPSULE_R=0.40 (Player.prefab local radius 0.4), but the prefab root carries
        // localScale=2; we need the REAL world radius (radius * lossyScale) to confirm
        // whether tight corridors like SM_Walls_Hall1 (~1.0m gap) are passable as baked.
        // See [[project-navigation-capsule-radius-groundtruth-2026-05-29]]. Logged once.
        private static bool _loggedCapsuleDimensions;
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
        private static bool _pickerBackspaceWasDown;
        // Filter/sort toggle keys (Left/Right = sort, F = floor, M = section, D = doors).
        private static bool _pickerLeftWasDown;
        private static bool _pickerRightWasDown;
        private static bool _pickerFloorKeyWasDown;
        private static bool _pickerSectionKeyWasDown;
        private static bool _pickerDoorsKeyWasDown;
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
        private float _suppressCreditsSelectionUntil;
        private float _suppressPendingSpecsTutorialUntil;
        private float _lastAutoWalkProgressTime;
        // When the post-arrival turn-to-face phase began (0 = not facing). Bounds that phase
        // so it completes (looking counts as interaction) or ends instead of spinning forever
        // when the game's raycast never "selects" a small/odd-collider target.
        private float _facingSince;
        // Last move/look the follower commanded, re-applied each FixedUpdate so the game never
        // reads its own end-of-FixedUpdate zeroed overwrite between our LateUpdate writes.
        private Vector3 _lastAutoWalkMove;
        private Vector3 _lastAutoWalkLook;
        private bool _hasAutoWalkInput;
        // Start time of the current sweep interaction probe (turn-to-face episode). See
        // ProbeSweepInteraction.
        private float _sweepProbeStartTime;

        // True when the sweep drive ended because the follower ARRIVED (reached the target's
        // interaction radius per SimpleNavBridge.HasArrivedAtRouteTarget), as opposed to giving up
        // on a progress timeout. Both leave HasActiveRoute=false, so the sweep can't otherwise tell
        // a legitimate arrival (the follower correctly stops within InteractionRadius, often >1.35m
        // from the tight goal cell for a large-radius object) from a real short stall. The sweep
        // reads this to set the verify phase's geometricallyAtCell, so a GaveUp probe after a true
        // arrival is recorded as arrived_unconfirmed (reached, couldn't select) not stalled (nav
        // failure). Reset on each sweep route start. See [[project-navigation-stalls-are-proximity-miscount-2026-06-13]].
        internal static bool LastSweepDriveArrived { get; private set; }
        private SpecsAnnouncementMode _lastSpecsAnnouncementMode;
        // PageUp/PageDown section stepper, shared by the SPECS / Rumors / DateADex detail screens.
        // Each builds an ordered list of "meaty" sections; PageDown reads the next, PageUp the prior,
        // one per press. The full-page read on open is unchanged — this is additive re-hearing. The
        // section list + index are rebuilt when the active screen or its content changes (keyed by
        // _sectionStepperKey), so stepping always reflects what's on screen.
        private List<string> _sectionStepperSections;
        private int _sectionStepperIndex = -1;
        private string _sectionStepperKey;
        private bool _pageUpWasDown;
        private bool _pageDownWasDown;
        // Edge-detected each FRAME (not on the throttled 0.1s poll) so a quick PageUp/PageDown tap that begins and
        // ends inside one poll gap isn't missed. Sampled by PollSectionStepperKeys(); consumed by the throttled
        // HandleSectionStepperInput(), which is the only place that has the resolved section list to act on.
        private bool _pageUpPending;
        private bool _pageDownPending;
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
            // Stable floor label (e.g. "ground"/"upper") the player stands on to reach this
            // target, resolved via SimpleNavPlanner.TryGetTargetFloorLabel. Null when the bake
            // can't resolve it. Used to bucket the picker by floor before sorting on Distance.
            public string FloorLabel;
            // True when FloorLabel matches the player's current floor. Same-floor targets sort
            // ahead of cross-floor ones regardless of XZ distance (a flat XZ sort wrongly makes
            // an upstairs item at the same XZ read as "near").
            public bool IsOnPlayerFloor;
            // DateADex-style section this target belongs to.
            public PickerSection Section;
            // Resolved zone/room name for the entry label + alphabetical sort. May be null.
            public string Zone;
            // Character name for Met entries (resolved via the save). Null for Encountered
            // entries — their datable is still Unmet and must not be revealed.
            public string CharacterName;
            // True when this object is a door/passage (for the doors-only filter).
            public bool IsDoor;
            // Datable IDENTITY key (the ink/internal name) for a MET object — the grouping key for
            // the top-level collapsed list (every object of the same datable shares it). Null for
            // Encountered (unmet) objects, which stay loose at the top level. NOTE this is the
            // PICKER's datable axis, distinct from the roster's physical routing-unit axis.
            public string DatableKey;
        }

        // One row in the picker at the current drill level. A node is EITHER a drill-in GROUP
        // (a met datable, or a room within a datable — Enter descends a level) or a LEAF object
        // (Enter routes to it). The displayed list (_knownObjectView) is a list of these, rebuilt
        // from the flat _knownObjectTargets per level. Levels: Top (collapsed met datables + loose
        // unmet objects) -> Room (rooms of the chosen datable) -> Object (that datable's objects in
        // that room). Grouping NEVER expands past _knownObjectTargets, which is already per-object
        // encounter-filtered, so a datable only ever shows objects the player personally found.
        // ROOM-FIRST model: L1 Rooms -> L2 (in a room: met datables + unmet objects) -> L3 (a met
        // datable's found objects IN that room). What a drill-in group represents:
        //   Room    — a room (L1): drill -> its met datables + unmet objects.
        //   Datable — a met datable within the chosen room (L2): drill -> its found objects in it.
        // (Unmet objects at L2 are leaves, routed directly. A cross-room datable appears under each
        //  room it has found objects in.)
        private enum PickerGroupKind { None, Room, Datable }

        private sealed class PickerNode
        {
            public bool IsGroup;            // true = drill-in (room or datable); false = routable object
            public PickerGroupKind GroupKind;
            public string Label;            // spoken label (room / character / object name)
            public int ChildCount;          // members under a group (spoken as a count)
            public float Distance;          // nearest member distance (for sort + announce)
            public bool IsOnPlayerFloor;    // nearest member's floor flag (for sort)
            public string FloorLabel;       // nearest member's floor (for the cross-floor tag)
            public PickerSection Section;   // Met (datable groups + their objects) / Encountered (loose)
            // Leaf only: the object to route to.
            public KnownObjectTarget Target;
            // Group only: the members this group drills into (subset of _knownObjectTargets).
            public List<KnownObjectTarget> Members;
        }

        // The drill level the picker is currently showing: Rooms -> InRoom -> Objects.
        private enum PickerLevel { Rooms, InRoom, Objects }
        private PickerLevel _pickerLevel = PickerLevel.Rooms;
        // Breadcrumb of the descent: the room entered (Rooms->InRoom) and the datable entered
        // (InRoom->Objects). Null above the level that sets them.
        private string _pickerRoomZone;         // chosen room's zone (null at Rooms level)
        private string _pickerDatableKey;       // chosen datable's DatableKey (null above Objects)
        private string _pickerDatableLabel;     // chosen datable's spoken label

        // Which DateADex-style section a target belongs to. Met = the player has dated its
        // datable (shown by character name, like the DateADex met-list); Encountered = examined
        // or normally interacted but the datable is still Unmet (shown by object name only, never
        // revealing the character — consistent with the game hiding unmet identities).
        private enum PickerSection { Met, Encountered }

        // Picker sort axis, cycled with Left/Right.
        private enum PickerSortMode { Distance, Alphabetical }

        // Section filter, cycled with M.
        private enum PickerSectionFilter { All, MetOnly, EncounteredOnly }

        // Y below this (world units) means "in the crawlspace". The crawlspace floor sits at
        // y ~= -9.7 and its contents (ladder y=-5.24, rat trap / time capsule / key / smoke
        // alarms y ~= -9.6..-9.9) all fall below this line, while the lowest normal-house
        // interactables sit at ground level (y ~= 0). The only other below-band objects are far
        // exterior secret-room cubes (z < -50) and bushes, none of which survive the picker's
        // encountered/datable filter. So a flat Y gate cleanly separates crawlspace from house.
        private const float CrawlspaceCeilingY = -3.5f;

        private static readonly HashSet<string> _examinedObjectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Full, unfiltered candidate set built on open. The displayed list (_knownObjectView) is
        // derived from this each time a filter/sort toggle changes, so toggling never re-scans
        // the scene.
        private List<KnownObjectTarget> _knownObjectTargets;
        // World bounds of each hierarchy room container (House/<Room> and the per-room lighting
        // groups), built once per picker open. Used as the SPATIAL FALLBACK to assign a room to
        // the handful of objects whose hierarchy puts them in a catch-all container (MultiRoom
        // art/plants/doors/vents, MovableObjects) with no room of their own. NOT camera zones —
        // these bounds are derived from the data hierarchy's own room nodes.
        private Dictionary<string, Bounds> _roomBoundsIndex;
        private List<PickerNode> _knownObjectView;
        private int _knownObjectSelectionIndex = -1;
        private bool _isKnownObjectPickerOpen;

        // Filter/sort state. Persists across opens within a session so the player's last view
        // is remembered.
        private PickerSortMode _pickerSortMode = PickerSortMode.Distance;
        private PickerSectionFilter _pickerSectionFilter = PickerSectionFilter.All;
        private bool _pickerFloorCurrentOnly;
        private bool _pickerDoorsOnly;

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

        internal static bool TryStartCoverageSweepRoute(SimpleNavRoute route, out string detail)
        {
            detail = null;

            if (_instance == null)
            {
                detail = "no-watcher";
                return false;
            }

            if (route == null || route.Waypoints == null || route.Waypoints.Count == 0)
            {
                detail = "empty-route";
                return false;
            }

            if (!CanUseNavigationNow())
            {
                detail = GetNavigationUnavailableReason();
                return false;
            }

            if (!ApplyNavigationInput(Vector3.zero, Vector3.zero))
            {
                detail = "input-application-failed";
                return false;
            }

            SimpleNavBridge.EndStep();
            SimpleNavBridge.BeginRoute(route);
            LastSweepDriveArrived = false;
            _instance._isAutoWalking = true;
            LogCapsuleDimensionsOnce();
            _instance._lastAutoWalkPosition = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            _instance._lastAutoWalkProgressTime = Time.unscaledTime;
            _instance.ClearNavigationBlockedDetail();
            return true;
        }

        internal static void StopCoverageSweepRoute()
        {
            if (_instance != null)
            {
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                _instance._isAutoWalking = false;
                _instance.ClearNavigationBlockedDetail();
            }

            SimpleNavBridge.EndStep();
        }

        // ---- Sweep interaction-reachability probe -----------------------------------------
        // The geometric arrival gate (within 1.35m of the goal cell) is a PROXY for the thing
        // a player actually cares about: "can I interact with this object from here?". Those
        // aren't the same — a goal cell can be near the object yet have no line of sight to it
        // (false positive), and a follower that stalls 1.5m short may ALREADY be in range (false
        // negative). So when the follower stops — arrival OR stall — the sweep runs this probe:
        // turn to face the object (so the game's look-raycast can populate activeObject), then
        // ask the game's OWN interaction precondition, InteractableManager.IsPlayerInRange with
        // the active object matching our target. That mirrors a real player aiming at the prop.
        //
        // CanSelectObj is consulted separately: it's the dateable/glasses ELIGIBILITY gate
        // (already-realized, talked-to-today, charge cost) — orthogonal to positioning. A
        // false there means "you're positioned fine but the game won't let you date it right
        // now", which is NOT a navigation failure, so it maps to a distinct "gated" verdict.
        // See [[feedback-interaction-includes-look-and-glasses]].
        internal enum SweepProbeState { Turning, InRange, InRangeGated, GaveUp }

        // Max time to keep turning toward the object before giving up the probe. A real player
        // turning to face a target takes the better part of a second for a wide swing; 1.25s was
        // tight enough that some legs timed out mid-turn (ray never landed) and were miscounted as
        // not-interactable. 2.5s lets the camera complete the turn before we run the final
        // line-of-sight check, while still bounding a probe that can never select (wall/occluder
        // between cell and object) so it ends instead of spinning forever.
        private const float SweepProbeTurnTimeoutSeconds = 2.5f;

        /// <summary>
        /// Drive one frame of the sweep interaction probe toward <paramref name="route"/>'s
        /// target. Call every frame while in the verify phase; pass <paramref name="reset"/>=true
        /// on the first frame of a probe. Returns the current <see cref="SweepProbeState"/>:
        /// Turning (keep calling), InRange (the game would let the player interact: either
        /// InteractableManager has our target selected and in-range, or a direct camera-forward
        /// raycast hits our target within its InteractionRadius — line-of-sight confirmed),
        /// InRangeGated (in range but the eligibility gate refuses), or GaveUp (turn timed out
        /// without line-of-sight to the target — genuinely not interactable from here, as it would
        /// be for a real player). See [[feedback-interaction-includes-look-and-glasses]].
        /// </summary>
        internal static SweepProbeState ProbeSweepInteraction(SimpleNavRoute route, bool reset)
        {
            if (_instance == null || route == null || BetterPlayerControl.Instance == null)
                return SweepProbeState.GaveUp;

            if (reset)
                _instance._sweepProbeStartTime = Time.unscaledTime;

            Transform playerTransform = BetterPlayerControl.Instance.transform;

            // Aim at the object so the game's targeting raycast can pick it up. World-targets
            // (no object id) have nothing to select — treat reaching the goal cell as the only
            // available truth and report in-range immediately.
            if (IsWorldRouteTarget(route))
                return SweepProbeState.InRange;

            InteractableManager manager = Singleton<InteractableManager>.Instance;
            if (manager != null && manager.IsPlayerInRange && manager.activeObject != null &&
                IsSameOrRelatedSimpleRouteTarget(route, manager.activeObject.gameObject))
            {
                // Positioned to interact. Distinguish "would actually interact" from "eligibility
                // gate refuses" so a gated dateable doesn't read as a nav failure.
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                GameController gc = Singleton<GameController>.Instance;
                bool eligible = gc == null || gc.CanSelectObj(manager.activeObject);
                return eligible ? SweepProbeState.InRange : SweepProbeState.InRangeGated;
            }

            if (Time.unscaledTime - _instance._sweepProbeStartTime >= SweepProbeTurnTimeoutSeconds)
            {
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                // InteractableManager never reported our exact target selected within the turn
                // budget. Before declaring the spot un-interactable, run the game's OWN interaction
                // test directly: a camera-forward raycast that hits the target within its
                // InteractionRadius. This catches the case where the manager's per-frame selection
                // hadn't latched yet but the player IS aimed at and in range of the target (a real,
                // completable interaction). It does NOT accept proximity without line-of-sight — an
                // occluded/clustered target the ray can't reach stays GaveUp, exactly as it would
                // fail for a real player. See [[feedback-interaction-includes-look-and-glasses]].
                if (HasInteractionLineOfSightToTarget(route))
                    return SweepProbeState.InRange;
                return SweepProbeState.GaveUp;
            }

            // Keep turning toward the object's camera-facing point (yaw + pitch), no movement.
            Vector3 lookPoint = ResolveSimpleRouteTargetLookPoint(route);
            Vector3 lookInput = GetLookInputTowardRouteTarget(playerTransform, lookPoint);
            if (lookInput.sqrMagnitude <= 0.0001f)
                lookInput = new Vector3(0.2f, 0f, 0f); // nudge so a nominally-aligned-but-unselected target keeps searching
            ApplyNavigationInput(Vector3.zero, lookInput);
            return SweepProbeState.Turning;
        }

        // True when the player could actually interact with the route target from where it stands,
        // using the GAME'S OWN test (BetterPlayerControl.cs): a camera-forward raycast must HIT the
        // target object (or a child/parent of it) AND the hit must be within the object's
        // InteractionRadius. Line-of-sight is mandatory — proximity alone is NOT enough; the glasses
        // and bare interaction both go through this same raycast, so an object the ray can't reach
        // (occluded by furniture, or a neighbour in a cluster the ray hits first) is genuinely not
        // interactable from here, exactly as it would be for a real player. We mirror the game's ray
        // origin (camera pos pulled back 0.25m along forward), direction, and ~dateviatorIgnores
        // layer mask so the probe and the game agree.
        private static bool HasInteractionLineOfSightToTarget(SimpleNavRoute route)
        {
            if (route == null)
                return false;

            GameObject targetObject = FindSimpleRouteTargetObject(route);
            if (targetObject == null)
                return false;

            Camera cam = Camera.main;
            if (cam == null)
                return false;

            BetterPlayerControl bpc = BetterPlayerControl.Instance;
            if (bpc == null)
                return false;

            Vector3 origin = cam.transform.position - cam.transform.forward * 0.25f;
            Vector3 dir = cam.transform.forward;
            int mask = ~(int)bpc.dateviatorIgnores;
            bool didHit = Physics.Raycast(new Ray(origin, dir), out RaycastHit hitInfo, float.PositiveInfinity, mask);

            // The ray must land on our target (or its hierarchy), not a neighbour/occluder...
            bool hitIsTarget = false;
            GameObject hitGo = null;
            if (didHit)
            {
                InteractableObj hitObj = hitInfo.transform.GetComponent<InteractableObj>();
                hitGo = hitObj != null ? hitObj.gameObject : hitInfo.transform.gameObject;
                hitIsTarget = IsSameOrRelatedInteractableTarget(targetObject, hitGo);
            }

            // ...AND the hit must be within the object's InteractionRadius (game's gate at
            // BetterPlayerControl.cs:499). Use the same closest-bounds-to-camera distance.
            float radius = route.TargetInteractionRadius > 0f ? route.TargetInteractionRadius : 0f;
            float dist = didHit
                ? Vector3.Distance(hitInfo.collider.ClosestPointOnBounds(cam.transform.position), cam.transform.position)
                : float.PositiveInfinity;
            bool result = didHit && hitIsTarget && radius > 0f && dist < radius;

            // Structured LOS sample for the OFFLINE PARITY PROBE: logs the EXACT ray (origin +
            // direction) the game cast, plus the hit + verdict, so validate_los.py can replay this
            // precise ray against its offline collider BVH and diff. We log the exact ray, not a
            // re-derived one, so parity tests the offline GEOMETRY/raycaster — isolated from whether
            // a synthetic eye/aim approximation matches. Sweep-gated (diagnostic only). See
            // [[project_navigation_planner_los_goal_cells_2026_06_13]].
            if (SimpleNavCoverageSweep.IsActive && Main.Log != null)
            {
                Main.Log.LogInfo(
                    "LOS_PROBE target=" + (route.TargetName ?? "<null>") + "#" + route.TargetGameObjectId +
                    " origin=" + FormatVector3Precise(origin) +
                    " dir=" + FormatVector3Precise(dir) +
                    " mask=" + mask +
                    " radius=" + radius.ToString("0.0000", CultureInfo.InvariantCulture) +
                    " hit=" + (didHit ? (hitGo != null ? hitGo.name : "<go?>") : "<none>") +
                    " hit_path=" + (didHit && hitInfo.collider != null ? GetTransformPath(hitInfo.collider.transform) : "<none>") +
                    " hit_point=" + (didHit ? FormatVector3Precise(hitInfo.point) : "<none>") +
                    " hit_dist=" + dist.ToString("0.0000", CultureInfo.InvariantCulture) +
                    " hit_is_target=" + hitIsTarget +
                    " verdict=" + result);
            }

            return result;
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

        /// <summary>
        /// Requests a deferred spoken description of a datable pose card (first-meet or ending),
        /// keyed by the card's <c>(internalName, pose, expression)</c> identity. Looked up in
        /// <see cref="CardPoseDescriptions"/>; if found, the speech is held until the awaken/ending
        /// stinger has had time to play (see <see cref="CardPoseSpeechDelaySeconds"/>).
        /// </summary>
        internal static void RequestCardPoseAnnouncement(string internalName, E_General_Poses pose, E_Facial_Expressions expression, bool allowNeutralFallback)
        {
            bool found = CardPoseDescriptions.TryGet(internalName, pose, expression, allowNeutralFallback, out string description);
            if (Main.Log != null)
            {
                string msg = "[card-pose] key=" + CardPoseDescriptions.BuildKey(internalName, pose, expression)
                    + " fallback=" + allowNeutralFallback + " found=" + found;
                // A miss means a datable card read silent. First-meet misses are real gaps worth surfacing at
                // Warning; ending cards have no authored pose description yet and are expected to be silent, so
                // log those at Info to avoid noise.
                if (found)
                    Main.Log.LogInfo(msg);
                else if (allowNeutralFallback)
                    Main.Log.LogWarning(msg + " (NO DESCRIPTION — card will be silent)");
                else
                    Main.Log.LogInfo(msg + " (ending card, no pose description authored — silent)");
            }

            if (!found || string.IsNullOrWhiteSpace(description))
                return;

            // Clear the repeat cache for the incoming card: until this description actually fires
            // (~3s later), a Ctrl+F1 press shouldn't replay the previous datable's pose text.
            _lastCardPoseDesc = null;
            _pendingCardPoseDesc = description;
            _pendingCardPoseNotBefore = Time.unscaledTime + CardPoseSpeechDelaySeconds;
            _pendingCardPoseExpiresAt = _pendingCardPoseNotBefore + CardPoseSpeechWindowSeconds;
            Interlocked.Exchange(ref _pendingCardPoseRequested, 1);
        }

        internal static void RememberExaminedObject(ObjectExamine examine)
        {
            if (examine == null)
                return;

            // Remember ONLY the owning interactable's identity keys. The examine's own
            // InkNode / gameObject name are shared-scope (an InkNode can be reused across
            // objects, a child name can collide) and are no longer read by any consumer —
            // remembering them would re-introduce the cross-object examine leak that
            // IsExaminedInteractable was hardened against.
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

            // Sample the section-stepper keys EVERY frame so a fast tap inside a poll gap is caught (the stepper
            // itself runs only on the 0.1s poll below). Edge-detection state must advance every frame for this to work.
            PollSectionStepperKeys();

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
            // Arm the ambient announcer chain so the first one to speak this tick cuts off
            // stale speech queued from an earlier tick (walking past objects quickly used
            // to queue their names); co-occurring announcements in the same tick still
            // chain. See ScreenReader.BeginCoalescedCycle / SayCoalesced.
            ScreenReader.BeginCoalescedCycle();
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
            HandleCardPoseAnnouncement();
            if (!isSettingsMenuOpen)
            {
                AnnounceSelectionIfNeeded();
            }
            AnnouncePhoneAppContentIfNeeded();
            AnnounceResultScreenIfNeeded();
            AnnounceTimeChangeIfNeeded();
            AnnounceProgressionChangesIfNeeded();
            // After the detail builders above have captured their section lists this frame, let
            // PageUp/PageDown step through them (SPECS / Rumors / DateADex). Additive to the page read.
            HandleSectionStepperInput();
        }

        // The game reads `move`/`look` in FixedUpdate and OVERWRITES them from live input
        // axes (which are 0 when idle) at the END of that same FixedUpdate. We compute and
        // write our values in LateUpdate (once per rendered frame). When the frame rate dips
        // toward the physics rate, more than one FixedUpdate can run before our next
        // LateUpdate — and those extra FixedUpdates read the game's zeroed value, so the
        // player intermittently stops dead mid-route with a valid command still cached
        // (velocity=0, reflected=our value, CanControl). Re-assert the last command every
        // FixedUpdate so the value is always present when the game actually reads it.
        private void FixedUpdate()
        {
            if (Main.IsShuttingDown || !_isAutoWalking || !_hasAutoWalkInput)
                return;
            if (BetterPlayerControl.Instance == null)
                return;
            ApplyNavigationInput(_lastAutoWalkMove, _lastAutoWalkLook);
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

        private void HandleCardPoseAnnouncement()
        {
            if (Interlocked.CompareExchange(ref _pendingCardPoseRequested, 0, 0) == 0)
                return;

            if (Time.unscaledTime < _pendingCardPoseNotBefore)
                return;

            // Held too long (card already dismissed) — drop it rather than speak stale text.
            if (Time.unscaledTime > _pendingCardPoseExpiresAt)
            {
                Interlocked.Exchange(ref _pendingCardPoseRequested, 0);
                _pendingCardPoseDesc = null;
                return;
            }

            string description = _pendingCardPoseDesc;
            Interlocked.Exchange(ref _pendingCardPoseRequested, 0);
            _pendingCardPoseDesc = null;

            if (!string.IsNullOrWhiteSpace(description))
            {
                _lastCardPoseDesc = description;
                ScreenReader.Say(description);
            }
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
                DescribeCurrentRoom();
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



        // Radius (metres, flat XZ) within which a known object counts as "in this room" for the
        // F6 scan. Generous enough to cover a whole room without spilling the entire house into
        // one announcement. Cross-floor targets are excluded regardless of distance.
        private const float RoomScanRadiusM = 10f;

        // F6: announce the current room and the known objects near the player, grouped by their
        // facing-relative direction (Ahead, Ahead right, Right, ...). Reuses the known-object
        // enumeration that backs the Ctrl+Shift+F6 picker, so it respects the same
        // encountered/met semantics and per-object dedup.
        private void DescribeCurrentRoom()
        {
            if (Singleton<GameController>.Instance == null ||
                Singleton<GameController>.Instance.viewState != VIEW_STATE.HOUSE)
            {
                ScreenReader.Say(Loc.Get("room_scan_unavailable"));
                return;
            }

            Transform playerTransform = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform
                : null;
            if (playerTransform == null)
            {
                ScreenReader.Say(Loc.Get("room_scan_unavailable"));
                return;
            }

            string roomName = GetCurrentRoomName();
            if (string.IsNullOrEmpty(roomName))
                roomName = Loc.Get("room_scan_unknown_room");

            KnownObjectBuildResult scanBuild = BuildKnownObjectTargets(out List<KnownObjectTarget> targets);
            if (scanBuild == KnownObjectBuildResult.RosterMissing)
            {
                ScreenReader.Say(Loc.Get("navigation_object_picker_no_data"));
                return;
            }
            if (scanBuild != KnownObjectBuildResult.Ok || targets.Count == 0)
            {
                ScreenReader.Say(Loc.Get("room_scan_empty", roomName));
                return;
            }

            Vector3 playerPosition = playerTransform.position;
            Vector3 forward = playerTransform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            forward.Normalize();

            // Player's room from the SAME hierarchy/bounds model the targets' Zone uses (built by the
            // BuildKnownObjectTargets call above), so the same-room comparison is apples-to-apples —
            // GetCurrentRoomName() uses camera zones, a different system that wouldn't string-match.
            string playerRoom = ResolveRoomByBounds(playerPosition);

            // Bucket nearby same-floor targets by facing-relative direction. Gate out objects the
            // player can't perceive from here: keep a target only if it's in the SAME ROOM or the
            // player has a clear LINE OF SIGHT to it (so F6 no longer reads objects through walls,
            // while still mentioning what's visible through a doorway into the next room).
            var grouped = new Dictionary<FacingRelativeDirection, List<KnownObjectTarget>>();
            foreach (KnownObjectTarget target in targets)
            {
                if (target.Interactable == null || !target.IsOnPlayerFloor)
                    continue;
                if (target.Distance > RoomScanRadiusM)
                    continue;

                bool sameRoom = !string.IsNullOrEmpty(playerRoom)
                    && string.Equals(playerRoom, target.Zone, StringComparison.OrdinalIgnoreCase);
                if (!sameRoom && !HasRoomScanLineOfSight(playerPosition, target.Interactable))
                    continue;

                FacingRelativeDirection direction = GetFacingRelativeDirection(
                    forward, playerPosition, target.Interactable.transform.position);

                if (!grouped.TryGetValue(direction, out List<KnownObjectTarget> bucket))
                {
                    bucket = new List<KnownObjectTarget>();
                    grouped[direction] = bucket;
                }
                bucket.Add(target);
            }

            if (grouped.Count == 0)
            {
                ScreenReader.Say(Loc.Get("room_scan_empty", roomName));
                return;
            }

            var report = new StringBuilder();
            report.Append(Loc.Get("room_scan_title", roomName));

            // Fixed clockwise order from straight ahead, so the report reads consistently.
            FacingRelativeDirection[] order =
            {
                FacingRelativeDirection.Here,
                FacingRelativeDirection.Ahead,
                FacingRelativeDirection.AheadRight,
                FacingRelativeDirection.Right,
                FacingRelativeDirection.BehindRight,
                FacingRelativeDirection.Behind,
                FacingRelativeDirection.BehindLeft,
                FacingRelativeDirection.Left,
                FacingRelativeDirection.AheadLeft,
            };

            foreach (FacingRelativeDirection direction in order)
            {
                if (!grouped.TryGetValue(direction, out List<KnownObjectTarget> bucket))
                    continue;

                bucket.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                var names = new List<string>(bucket.Count);
                foreach (KnownObjectTarget target in bucket)
                {
                    string name = !string.IsNullOrEmpty(target.CharacterName) ? target.CharacterName : target.Label;
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }

                if (names.Count == 0)
                    continue;

                report.Append(" ");
                report.Append(Loc.Get("room_scan_group",
                    Loc.Get(DirectionLocKey(direction)),
                    string.Join(", ", names.ToArray())));
                report.Append(".");
            }

            ScreenReader.Say(report.ToString());
        }

        // True if the player has a clear line of sight to the object — no wall between them. Used by
        // the F6 room scan to drop objects in adjacent rooms that the old flat-distance filter read
        // straight through walls. Casts from the player's eye toward the object's closest collider
        // point; LOS is clear if the ray reaches that point (within a small slack) before hitting
        // anything. Uses the game's own ~dateviatorIgnores mask so non-occluding layers (the same
        // ones interaction raycasts skip) don't count as walls.
        private static bool HasRoomScanLineOfSight(Vector3 playerPosition, InteractableObj target)
        {
            if (target == null || target.transform == null)
                return false;

            BetterPlayerControl bpc = BetterPlayerControl.Instance;
            int mask = bpc != null ? ~(int)bpc.dateviatorIgnores : Physics.DefaultRaycastLayers;

            // Eye-height origin so the ray travels over low furniture rather than into its base.
            Vector3 origin = playerPosition + Vector3.up * 1.5f;

            // Aim at the object's closest collider point (falls back to its transform), at eye height
            // so a tall wall between the rooms blocks even when the object's pivot is on the floor.
            Collider col = target.GetComponent<Collider>();
            if (col == null)
                col = target.GetComponentInChildren<Collider>();
            Vector3 aimPoint = col != null ? col.ClosestPointOnBounds(origin) : target.transform.position;
            aimPoint.y = origin.y;

            Vector3 toAim = aimPoint - origin;
            float dist = toAim.magnitude;
            if (dist < 0.05f)
                return true;
            Vector3 dir = toAim / dist;

            if (!Physics.Raycast(origin, dir, out RaycastHit hit, dist, mask, QueryTriggerInteraction.Ignore))
                return true; // nothing in the way → clear

            // Hit something: clear only if we essentially reached the object (the hit IS the target /
            // its hierarchy, or the blocker sits at/just past the aim point — i.e. it's the object,
            // not a wall in front of it).
            if (hit.distance >= dist - 0.35f)
                return true;
            InteractableObj hitObj = hit.transform.GetComponent<InteractableObj>()
                ?? hit.transform.GetComponentInParent<InteractableObj>();
            return hitObj == target;
        }

        // Bucket a target into one of the 8 facing-relative compass directions (plus Here for
        // anything essentially on top of the player). forward is the player's flattened, normalized
        // facing; angle is measured clockwise from forward so positive = to the player's right.
        private static FacingRelativeDirection GetFacingRelativeDirection(
            Vector3 forward, Vector3 playerPosition, Vector3 targetPosition)
        {
            Vector3 toTarget = targetPosition - playerPosition;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.25f) // within ~0.5m flat → "here"
                return FacingRelativeDirection.Here;
            toTarget.Normalize();

            // Clockwise angle from forward: SignedAngle is CCW-positive about +Y, so negate to make
            // right-hand turns positive, matching how a player reads "ahead right" / "right".
            float angle = -Vector3.SignedAngle(forward, toTarget, Vector3.up);

            // Snap to the nearest of 8 sectors centred on each named direction (45-degree slices).
            int sector = Mathf.RoundToInt(angle / 45f);
            sector = ((sector % 8) + 8) % 8;
            switch (sector)
            {
                case 0: return FacingRelativeDirection.Ahead;
                case 1: return FacingRelativeDirection.AheadRight;
                case 2: return FacingRelativeDirection.Right;
                case 3: return FacingRelativeDirection.BehindRight;
                case 4: return FacingRelativeDirection.Behind;
                case 5: return FacingRelativeDirection.BehindLeft;
                case 6: return FacingRelativeDirection.Left;
                default: return FacingRelativeDirection.AheadLeft;
            }
        }

        private static string DirectionLocKey(FacingRelativeDirection direction)
        {
            switch (direction)
            {
                case FacingRelativeDirection.Here: return "room_scan_direction_here";
                case FacingRelativeDirection.Ahead: return "room_scan_direction_ahead";
                case FacingRelativeDirection.AheadRight: return "room_scan_direction_ahead_right";
                case FacingRelativeDirection.Right: return "room_scan_direction_right";
                case FacingRelativeDirection.BehindRight: return "room_scan_direction_behind_right";
                case FacingRelativeDirection.Behind: return "room_scan_direction_behind";
                case FacingRelativeDirection.BehindLeft: return "room_scan_direction_behind_left";
                case FacingRelativeDirection.Left: return "room_scan_direction_left";
                default: return "room_scan_direction_ahead_left";
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
                // Manual navigation (not autowalk): the player drives, so the active leg can move
                // FORWARD as they reach a waypoint or BACKWARD if they retreat along the route. Mark
                // each transition with its own blip — forward (rising) = closer to goal, reverse
                // (falling) = backtracked — so a moving pan/volume reads as a known leg change rather
                // than ambiguous progress. Advance is tried first; regression only when not advancing
                // (they share a boundary, so at most one fires). Autowalk keeps the monotonic advance
                // path and never calls regress.
                if (SimpleNavBridge.TryAdvanceWaypoint(playerPos))
                {
                    waypoint = SimpleNavBridge.LastResolvedTarget;
                    ObjectTracker.NotifyWaypointAdvanced();
                }
                else if (SimpleNavBridge.TryRegressWaypoint(playerPos))
                {
                    waypoint = SimpleNavBridge.LastResolvedTarget;
                    ObjectTracker.NotifyWaypointRegressed();
                }
                ObjectTracker.StartTracking(waypoint, requiresInteraction: false);
            }
        }

        // One-time diagnostic: log the player's REAL world-space capsule dimensions.
        // The bake reserves CAPSULE_R=0.40m of clearance per wall (Player.prefab local
        // radius 0.4), but the prefab root has localScale=2, so the literal world radius
        // could be 0.8m (height 5.0m — absurd, hence likely counter-scaled to net ~1x).
        // This reads the live CapsuleCollider * lossyScale to settle 0.4 vs 0.8 for real,
        // which decides whether ~1.0m gaps (SM_Walls_Hall1) are passable as baked.
        // See [[project-navigation-capsule-radius-groundtruth-2026-05-29]].
        private static void LogCapsuleDimensionsOnce()
        {
            if (_loggedCapsuleDimensions) return;
            if (Main.Log == null || BetterPlayerControl.Instance == null) return;
            Transform t = BetterPlayerControl.Instance.transform;
            CapsuleCollider cc = BetterPlayerControl.Instance.GetComponent<CapsuleCollider>();
            if (cc == null) cc = BetterPlayerControl.Instance.GetComponentInChildren<CapsuleCollider>();
            if (cc == null)
            {
                Main.Log.LogInfo("[capsule-probe] no CapsuleCollider on BetterPlayerControl");
                _loggedCapsuleDimensions = true;
                return;
            }
            // Unity scales a CapsuleCollider's radius by the larger of the two non-height
            // lossyScale axes, and its height by the height axis. Use the collider's own
            // transform lossyScale (it may differ from the controller root if nested).
            Vector3 ls = cc.transform.lossyScale;
            float radialScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
            float worldR = cc.radius * radialScale;
            float worldH = cc.height * Mathf.Abs(ls.y);
            Main.Log.LogInfo(
                "[capsule-probe] localRadius=" + cc.radius.ToString("F3") +
                " localHeight=" + cc.height.ToString("F3") +
                " colliderLossyScale=(" + ls.x.ToString("F3") + "," + ls.y.ToString("F3") + "," + ls.z.ToString("F3") + ")" +
                " rootLossyScale=(" + t.lossyScale.x.ToString("F3") + "," + t.lossyScale.y.ToString("F3") + "," + t.lossyScale.z.ToString("F3") + ")" +
                " => worldRadius=" + worldR.ToString("F3") + "m worldHeight=" + worldH.ToString("F3") + "m" +
                " (bake CAPSULE_R=0.40)");
            _loggedCapsuleDimensions = true;
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
                // Record that THIS drive reached interaction range — even when the stop point is
                // outside the sweep's tight 1.35m goal cell (true for large-radius objects). The
                // sweep uses this to treat the follow-up verify as an arrival, not a short stall.
                LastSweepDriveArrived = true;
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

                    // Turn-to-face is the final phase: we're in range, now orient toward the
                    // target so the game's first-person raycast selects it. Bound it so it
                    // can't spin forever when the raycast never lands (small/odd colliders):
                    // once we're pointed AT the target (look input ≈ 0, so we've finished
                    // turning) OR we've been facing for AutoWalkFaceTimeoutSeconds, accept
                    // arrival — looking IS the interaction for a blind player, who just needs
                    // to be placed in range and oriented. See [[feedback-interaction-includes-look-and-glasses]].
                    if (_facingSince == 0f) _facingSince = Time.unscaledTime;
                    bool aimedAtTarget = lookInput.sqrMagnitude <= 0.0001f;
                    bool facedLongEnough = Time.unscaledTime - _facingSince >= AutoWalkFaceTimeoutSeconds;
                    if (aimedAtTarget || facedLongEnough)
                    {
                        ApplyNavigationInput(Vector3.zero, Vector3.zero);
                        StopNavigationWithAnnouncement("navigation_arrived");
                        return;
                    }

                    ApplyNavigationInput(Vector3.zero, lookInput);
                    // Cache the facing command (no forward move) so FixedUpdate re-asserts THIS,
                    // not the previous forward drive, between frames.
                    _lastAutoWalkMove = Vector3.zero;
                    _lastAutoWalkLook = lookInput;
                    _hasAutoWalkInput = true;
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

            // Steer along the planned polyline with a short pure-pursuit lookahead so the
            // player tracks the corridor through corners instead of cutting toward
            // vertices. Door resolution and arrival key off the discrete waypoint index
            // (advanced above); only the steering direction uses the lookahead.
            // See [[project-navigation-executor-corner-stall]].
            Vector3 steerTarget = SimpleNavBridge.PursuitTarget(playerPos, AutoWalkPursuitLookahead);
            Vector3 toWaypoint = steerTarget - playerPos;
            toWaypoint.y = 0f;
            if (toWaypoint.sqrMagnitude <= 0.0001f)
            {
                ApplyNavigationInput(Vector3.zero, Vector3.zero);
                _lastAutoWalkMove = Vector3.zero;
                _lastAutoWalkLook = Vector3.zero;
                return;
            }

            // Drive movement and look exactly like real player input: `move` is the steer
            // direction in player-local space (forward/strafe), `look` turns the body
            // toward it.
            //
            // The heading gate is NOT a speed throttle — the game NORMALIZES move and
            // multiplies by the player's own `speed`, so any move above the 0.2 dead-zone
            // walks at full speed regardless of our magnitude. What the gate does is keep move
            // and look from ever being large at once: outside the facing cone we command a pure
            // turn (move=0), inside it a full-magnitude walk. That matters because the game
            // rebuilds world motion as forward*move.z + right*move.x using the CURRENT facing
            // every physics tick — a big move while the body is still turning under a big look
            // makes the motion direction thrash and cancel (spin-in-place). The gate is BINARY,
            // not a cos(turn) scale: a continuous scale puts move in (0,0.2) across ~78-90°,
            // below the game's 0.2 dead-zone (frozen) yet untimed-out by the watchdog. (look IS
            // analog — the game uses look.x linearly — so the turn rate eases as we align.)
            Vector3 walkDir = toWaypoint.normalized;
            Vector3 localDirection = playerTransform.InverseTransformDirection(walkDir);
            float turnDeg = Vector3.SignedAngle(playerTransform.forward, walkDir, Vector3.up);
            float facing = Vector3.Dot(playerTransform.forward, walkDir); // cos(turn)

            // CLOSE-RANGE SETTLE. The binary heading gate (turn-in-place when off-heading) is right
            // for TRAVERSING toward a far point, but it can't SETTLE onto a near one: within a few
            // tenths of a metre, a small position change swings the direction-to-cell wildly, so the
            // gate flips to "turn" and the follower orbits the cell instead of landing on it. That
            // orbit is the only reason final arrival couldn't be tight. Within CloseRangeSettleM of
            // the FINAL waypoint, drop the gate and drive STRAIGHT at it (full move, no turn-first):
            // at sub-0.5m a small move can't spin-in-place destructively (worst case a brief diagonal
            // as the body catches up), and it CONVERGES. So the follower genuinely lands on the goal
            // cell; if it still can't, the no-progress watchdog reports a real failure rather than
            // the loose radius hiding it. See [[project-navigation-verify-los-gap-2026-06-16]].
            Vector3 toFinal = SimpleNavBridge.FinalWaypoint - playerPos;
            toFinal.y = 0f;
            bool closeRangeSettle = SimpleNavBridge.FinalWaypoint != Vector3.zero &&
                                    toFinal.sqrMagnitude <= CloseRangeSettleM * CloseRangeSettleM;
            bool headingAligned = closeRangeSettle || facing >= AutoWalkFacingGateCosThreshold;
            Vector3 move = headingAligned
                ? new Vector3(
                    Mathf.Clamp(localDirection.x, -1f, 1f), 0f,
                    Mathf.Clamp(localDirection.z, -1f, 1f)).normalized
                : Vector3.zero;
            Vector3 look = new Vector3(Mathf.Clamp(turnDeg / AutoWalkLookScaleDegrees, -1f, 1f), 0f, 0f);

            // Hold position while the segment's door is mid-swing — walking into a moving
            // door trips Door.OnCollisionEnter and pins the swing.
            bool waitingForDoorSwing = segmentHasDoor && SimpleNavBridge.IsActiveDoorMoving();
            if (waitingForDoorSwing)
                move = Vector3.zero;

            if (!ApplyNavigationInput(move, look))
            {
                StopNavigationBlocked("simple-nav route input application failed target=" + (route.TargetName ?? "<null>"));
                return;
            }
            // Cache so FixedUpdate can re-assert this between LateUpdate frames (see FixedUpdate).
            _lastAutoWalkMove = move;
            _lastAutoWalkLook = look;
            _hasAutoWalkInput = true;

            LogSimpleRouteFrameDiagnostic(route, playerTransform, playerPos, target, move, look, waitingForDoorSwing);
            SimpleNavBridge.RecordFrameProgress(playerPos);

            // Watchdog. Two ways a leg can sit still: (a) committed to a waypoint and walking
            // but not advancing — a real obstacle/jamb; (b) turning in place and never aligning
            // — oscillating at the cone edge or geometrically unable to face the waypoint. Both
            // must eventually time out. The OLD gate keyed "trying" off move.magnitude, so a
            // follower frozen with a small sub-dead-zone move (the continuous-scale bug) read as
            // "turning" forever and never timed out. Now: we are TRYING whenever we hold this
            // waypoint and aren't waiting on a door swing. Genuine turning is bounded — it gets
            // its own grace (AutoWalkTurnGraceSeconds) before counting, but it is NOT exempt
            // indefinitely, so a stuck turn still trips the timeout.
            bool tryingToTranslate = !waitingForDoorSwing;
            bool turningToAlign = !headingAligned;
            if (Vector3.Distance(playerPos, _lastAutoWalkPosition) >= AutoWalkProgressDistance)
            {
                _lastAutoWalkPosition = playerPos;
                _lastAutoWalkProgressTime = Time.unscaledTime;
                ClearNavigationBlockedDetail();
            }
            else if (!tryingToTranslate)
            {
                _lastAutoWalkProgressTime = Time.unscaledTime;  // door-waiting: not a stall
            }
            else if (turningToAlign &&
                     Time.unscaledTime - _lastAutoWalkProgressTime < AutoWalkBlockedTimeoutSeconds + AutoWalkTurnGraceSeconds)
            {
                // Allow a bounded turn-in-place before it counts; an unaligned-but-progressing
                // turn keeps resetting via the positional-progress branch above, so reaching
                // this grace ceiling means the turn itself is stuck.
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
            DoorPortal door = FindSimpleRouteTargetDoor(route);
            if (door == null)
                return false;

            if (door.open && !SimpleNavBridge.IsDoorMoving(door))
                return true;

            return IsSimpleRouteTargetSelected(route);
        }

        // Resolve the route's TARGET barrier (when the target itself is a door) across both
        // component types — Door and SlidingDoor — by GameObject instance id. Mirrors the
        // tag/open path's uniform treatment so a closet/cabinet door can be a direct nav target.
        private static DoorPortal FindSimpleRouteTargetDoor(SimpleNavRoute route)
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
                    return DoorPortal.For(door);
            }

            SlidingDoor[] sliders = FindObjectsOfType<SlidingDoor>();
            for (int i = 0; i < sliders.Length; i++)
            {
                SlidingDoor sd = sliders[i];
                if (sd == null || sd.gameObject == null)
                    continue;
                if (sd.gameObject.GetInstanceID() == route.TargetGameObjectId)
                    return DoorPortal.For(sd);
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
        // Also stamps RuntimeBlockerProbe.Last so the coverage sweep can pick up structured data.
        // Public entry for the coverage sweep: fire a fresh probe on demand so impass records
        // captured outside the autowalk's own progress-timeout path still have diagnostic data.
        // Returns the diagnostic string for logging; the structured snapshot lives in
        // RuntimeBlockerProbe.Last.
        public static string ProbeRuntimeBlockerNow(Vector3 playerPos, Vector3 target)
        {
            if (_instance == null) { RuntimeBlockerProbe.Last = null; return null; }
            return _instance.ProbeRuntimeBlocker(playerPos, target);
        }

        private string ProbeRuntimeBlocker(Vector3 playerPos, Vector3 target)
        {
            Vector3 toward = target - playerPos;
            toward.y = 0f;
            float dist = toward.magnitude;
            Vector3 dir = dist > 0.01f ? toward / dist : Vector3.forward;
            // Forward cast distance includes a 0.5m overshoot so we don't miss a wall
            // sitting exactly at the waypoint.
            float castDist = Mathf.Max(0.6f, dist + 0.5f);

            RuntimeBlockerProbe.Hit chest = ProbeOne(playerPos + new Vector3(0f, 1.0f, 0f), dir, castDist);
            RuntimeBlockerProbe.Hit ankle = ProbeOne(playerPos + new Vector3(0f, 0.2f, 0f), dir, castDist);

            // Side / rear casts — fixed 1m range, used for "forward is clear but I'm still
            // stuck" diagnosis. Right-handed: right = (dir.z, 0, -dir.x), left = -right.
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            Vector3 left = -right;
            Vector3 back = -dir;
            RuntimeBlockerProbe.Hit hRight = ProbeOne(playerPos + new Vector3(0f, 1.0f, 0f), right, 1.0f);
            RuntimeBlockerProbe.Hit hLeft  = ProbeOne(playerPos + new Vector3(0f, 1.0f, 0f), left,  1.0f);
            RuntimeBlockerProbe.Hit hBack  = ProbeOne(playerPos + new Vector3(0f, 1.0f, 0f), back,  1.0f);

            // Downward cast — 2m below the player. Uses a raycast, not a spherecast, because
            // SphereCast returns no hit when the sphere is already overlapping geometry at the
            // start of the cast (which happens for any reasonable above-feet origin near the
            // floor). The raycast doesn't have that limitation. Origin sits 1.2m up to clear
            // the player capsule's lower half cleanly.
            RuntimeBlockerProbe.Hit down = ProbeDown(playerPos + new Vector3(0f, 1.2f, 0f), 3.0f);

            // Recent movement tracking — uses the autowalk's progress detector state, so we
            // see exactly the displacement that timed out.
            float recentDisp = Vector3.Distance(
                new Vector3(playerPos.x, 0, playerPos.z),
                new Vector3(_lastAutoWalkPosition.x, 0, _lastAutoWalkPosition.z));
            float sinceProgress = Time.unscaledTime - _lastAutoWalkProgressTime;

            var probe = new RuntimeBlockerProbe
            {
                Chest = chest,
                Ankle = ankle,
                Left = hLeft,
                Right = hRight,
                Back = hBack,
                Down = down,
                DownDistanceM = down != null ? down.Distance : -1f,
                PlayerPos = playerPos,
                Waypoint = target,
                DistanceToWaypointM = dist,
                RecentDisplacementM = recentDisp,
                SecondsSinceProgress = sinceProgress,
            };
            RuntimeBlockerProbe.Last = probe;
            if (chest == null && ankle == null) return null;
            // Include the directional casts (1m range) so a stall log shows which way is
            // actually open — essential for diagnosing tight-channel chokepoints like
            // SM_Walls_Hall1 where "forward is blocked but a side is clear" is the whole
            // question. left/right are perpendicular to the travel dir; back is behind.
            // See [[project-navigation-corner-dilation-severance-2026-05-29]].
            return "chest=" + (chest?.Format() ?? "<clear>") + " ankle=" + (ankle?.Format() ?? "<clear>") +
                   " left=" + (hLeft?.Format() ?? "<clear>") + " right=" + (hRight?.Format() ?? "<clear>") +
                   " back=" + (hBack?.Format() ?? "<clear>") + " downDist=" + (down != null ? down.Distance.ToString("F2") : "-1");
        }

        private static RuntimeBlockerProbe.Hit ProbeOne(Vector3 origin, Vector3 dir, float maxDist)
        {
            if (!Physics.SphereCast(origin, 0.35f, dir, out RaycastHit hit, maxDist,
                    Physics.AllLayers, QueryTriggerInteraction.Ignore))
                return null;
            return HitFrom(hit);
        }

        // Straight raycast (no sphere) — used for the downward ground probe, where a spherecast
        // would start overlapping the floor and report no hit. Distance is measured from the
        // origin, so subtract the 1.2m origin lift to recover the player's clearance to ground.
        private static RuntimeBlockerProbe.Hit ProbeDown(Vector3 origin, float maxDist)
        {
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxDist,
                    Physics.AllLayers, QueryTriggerInteraction.Ignore))
                return null;
            return HitFrom(hit);
        }

        private static RuntimeBlockerProbe.Hit HitFrom(RaycastHit hit)
        {
            GameObject go = hit.collider != null ? hit.collider.gameObject : null;
            return new RuntimeBlockerProbe.Hit
            {
                Name = go != null ? go.name : "<unknown>",
                Path = go != null ? RuntimeBlockerProbe.PathOf(go) : "<unknown>",
                Layer = go != null ? go.layer : -1,
                Distance = hit.distance,
            };
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
            _hasAutoWalkInput = false;  // stop re-asserting input in FixedUpdate
            _lastAutoWalkProgressTime = 0f;
            _nextSimpleRouteDiagnosticTime = 0f;
            _lastNavigationTargetDebugSnapshot = null;
            _lastNavigationAutoWalkDebugSnapshot = null;
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

            bool haveKind = TryResolveTutorialObjectiveKind(out TutorialObjectiveKind objectiveKind);

            // Rumor/DateADex lookups are RETIRED here: a Rumor names a datable (character), not an
            // object ID, and a clue may point at a specific location — so matching it to the
            // closest/best-named candidate can steer the player to the wrong object. Without a kind
            // at all we give up (player can use the Ctrl+Shift+F6 picker); generic "awaken any
            // datable" objectives fall through to the floor-aware nearest-datable search below;
            // specific objectives (computer, gift box, Maggie, Skylar, ...) resolve exactly.
            if (!haveKind || objectiveKind == TutorialObjectiveKind.None)
            {
                DebugLogger.Log(LogCategory.State, "AccessibilityWatcher", "Objective resolve failed: no tutorial objective kind. signpostText=" + (objectiveText ?? "<null>"));
                return false;
            }

            // "Find more datables" (the post-tutorial majority): steer to the nearest ROOM that
            // still holds an undiscovered datable, announced as the room only. Routes to a real
            // member of that room but never names the object — preserving the discovery loop.
            // AnyUnrealizedDatable (endgame) keeps the nearest-datable behaviour below: by then the
            // player has met everything, so there's no object to spoil.
            if (objectiveKind == TutorialObjectiveKind.AnyUnmetDatable &&
                TryResolveNearestUnexploredRoomTarget(out InteractableObj roomMember, out string roomLabel))
            {
                interactable = roomMember;
                hallwayFallbackLabel = roomLabel;
            }
            else if (objectiveKind == TutorialObjectiveKind.FrontDoor)
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
            SimpleNavPlanner.TryGetPlayerFloorLabel(playerPosition.y, out string playerFloorLabel);

            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            // Track the best candidate floor-aware: prefer one on the player's floor, then nearest
            // by flat XZ distance. Raw 3D distance let an object directly above/below or through a
            // wall win as "nearest" even though it isn't the easiest to actually reach.
            bool bestOnPlayerFloor = false;
            float bestDistance = float.MaxValue;
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

                Vector3 candidatePos = candidate.transform.position;
                float distance = GetFlatDistance(playerPosition, candidatePos);
                SimpleNavPlanner.TryGetTargetFloorLabel(candidatePos.y, out string candidateFloor);
                bool onPlayerFloor = playerFloorLabel == null || candidateFloor == null ||
                    string.Equals(candidateFloor, playerFloorLabel, StringComparison.OrdinalIgnoreCase);

                if (interactable == null ||
                    CompareFloorAwareDistance(onPlayerFloor, distance, bestOnPlayerFloor, bestDistance) < 0)
                {
                    interactable = candidate;
                    bestOnPlayerFloor = onPlayerFloor;
                    bestDistance = distance;
                }
            }

            return interactable != null;
        }

        // "Find more datables" default (post-tutorial AnyUnmetDatable). Rather than name the single
        // nearest unmet datable — which spoils which object IS a datable, the whole discovery loop —
        // steer to the NEAREST ROOM that still holds an undiscovered datable. The route terminates
        // at a real reachable cell (the nearest unmet-datable interactable in that room), but the
        // player only hears the room ("Kitchen"), so it nudges direction without revealing the
        // object or that it's dateable. roomLabel is the spoken room; routeTarget is what to plan to.
        private bool TryResolveNearestUnexploredRoomTarget(out InteractableObj routeTarget, out string roomLabel)
        {
            routeTarget = null;
            roomLabel = null;

            Save save = Singleton<Save>.Instance;
            if (save == null || BetterPlayerControl.Instance == null)
                return false;

            // The room-bounds spatial fallback is normally built when the picker opens; Ctrl+F6 can
            // fire without ever opening it, so build it on demand if absent.
            if (_roomBoundsIndex == null)
                _roomBoundsIndex = BuildRoomBoundsIndex();

            Vector3 playerPosition = BetterPlayerControl.Instance.transform.position;
            SimpleNavPlanner.TryGetPlayerFloorLabel(playerPosition.y, out string playerFloorLabel);

            // Group every undiscovered datable by its room, tracking the nearest member per room.
            // We pick the nearest ROOM (by its nearest member, floor-aware), then route to that
            // member — so the announcement is the room but the route still lands somewhere real.
            InteractableObj[] interactables = FindObjectsOfType<InteractableObj>();
            string bestRoom = null;
            InteractableObj bestRoomMember = null;
            bool bestRoomOnPlayerFloor = false;
            float bestRoomDistance = float.MaxValue;
            for (int i = 0; i < interactables.Length; i++)
            {
                InteractableObj candidate = interactables[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                    continue;

                string internalName = candidate.InternalName();
                if (string.IsNullOrWhiteSpace(internalName))
                    continue;

                // Game knowledge: an unmet datable (a named date target the player hasn't dated),
                // including ones the player has never noticed — that's the point of "find more".
                if (!save.TryGetNameByInternalName(internalName, out string displayName) ||
                    string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }
                if (save.GetDateStatus(internalName) != RelationshipStatus.Unmet)
                    continue;

                Vector3 candidatePos = candidate.transform.position;
                // Room name from the data hierarchy, falling back to the spatial bounds — same room
                // model the picker shows the player, so what they hear matches the room list.
                if (!TryGetHierarchyRoomForInteractable(candidate, out string room))
                    room = ResolveRoomByBounds(candidatePos);
                if (string.IsNullOrWhiteSpace(room))
                    continue;

                float distance = GetFlatDistance(playerPosition, candidatePos);
                SimpleNavPlanner.TryGetTargetFloorLabel(candidatePos.y, out string candidateFloor);
                bool onPlayerFloor = playerFloorLabel == null || candidateFloor == null ||
                    string.Equals(candidateFloor, playerFloorLabel, StringComparison.OrdinalIgnoreCase);

                if (bestRoomMember == null ||
                    CompareFloorAwareDistance(onPlayerFloor, distance, bestRoomOnPlayerFloor, bestRoomDistance) < 0)
                {
                    bestRoom = room;
                    bestRoomMember = candidate;
                    bestRoomOnPlayerFloor = onPlayerFloor;
                    bestRoomDistance = distance;
                }
            }

            if (bestRoomMember == null)
                return false;

            routeTarget = bestRoomMember;
            roomLabel = bestRoom;
            return true;
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
        // interacted with, or examined the object. Entries are grouped DateADex-style into a
        // Met section (by character name) and an Encountered section (object name only).
        // Up/Down move selection; Enter selects (drives the same nav-tone flow as Ctrl+F6);
        // Backspace backs out / closes. Left/Right cycle the sort, F toggles current-floor-only,
        // M cycles the section filter, D toggles doors-only.
        private void OpenKnownObjectPicker()
        {
            Loc.RefreshLanguage();

            KnownObjectBuildResult build = BuildKnownObjectTargets(out List<KnownObjectTarget> targets);
            if (build == KnownObjectBuildResult.RosterMissing)
            {
                // Upstream dependency failure — a missing/old bake, not an empty save. Announce it
                // distinctly so it's not mistaken for "you haven't found anything yet".
                ScreenReader.Say(Loc.Get("navigation_object_picker_no_data"));
                return;
            }
            if (build != KnownObjectBuildResult.Ok || targets.Count == 0)
            {
                ScreenReader.Say(Loc.Get("navigation_object_picker_empty"));
                return;
            }

            _knownObjectTargets = targets;
            // Always open at the TOP drill level (collapsed datables + unmet rooms).
            ResetPickerDrillLevel();
            _knownObjectView = BuildFilteredKnownObjectView();
            // If the remembered filters hide everything, open on the full set instead of an
            // empty picker — better to show the player their objects than a dead end.
            if (_knownObjectView.Count == 0)
            {
                _pickerDoorsOnly = false;
                _pickerFloorCurrentOnly = false;
                _pickerSectionFilter = PickerSectionFilter.All;
                _knownObjectView = BuildFilteredKnownObjectView();
            }

            _knownObjectSelectionIndex = 0;
            _isKnownObjectPickerOpen = true;
            SyncKnownObjectPickerKeyStates();
            AnnounceKnownObjectPickerTitleAndItem();
        }

        private void CloseKnownObjectPicker(bool announceClosed)
        {
            if (!_isKnownObjectPickerOpen)
                return;

            _isKnownObjectPickerOpen = false;
            _knownObjectTargets = null;
            _knownObjectView = null;
            _knownObjectSelectionIndex = -1;
            ResetPickerDrillLevel();
            SyncKnownObjectPickerKeyStates();
            if (announceClosed)
                ScreenReader.Say(Loc.Get("navigation_object_picker_closed"));
        }

        // Return the picker to the top (Rooms) drill level and clear the breadcrumb. Called on open
        // and close so a fresh open always starts at the room list.
        private void ResetPickerDrillLevel()
        {
            _pickerLevel = PickerLevel.Rooms;
            _pickerDatableKey = null;
            _pickerDatableLabel = null;
            _pickerRoomZone = null;
        }

        private void UpdateKnownObjectPicker()
        {
            if (_knownObjectView == null || _knownObjectView.Count == 0)
            {
                CloseKnownObjectPicker(announceClosed: false);
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.UpArrow, VkUp, ref _pickerUpWasDown))
            {
                _knownObjectSelectionIndex = (_knownObjectSelectionIndex + _knownObjectView.Count - 1) % _knownObjectView.Count;
                AnnounceCurrentKnownObjectPickerItem();
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.DownArrow, VkDown, ref _pickerDownWasDown))
            {
                _knownObjectSelectionIndex = (_knownObjectSelectionIndex + 1) % _knownObjectView.Count;
                AnnounceCurrentKnownObjectPickerItem();
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.Return, VkReturn, ref _pickerReturnWasDown) ||
                WasChoiceKeyPressed(KeyCode.KeypadEnter, VkReturn, ref _pickerReturnWasDown))
            {
                SelectCurrentKnownObjectPickerItem();
                return;
            }

            // Left/Right cycle the sort mode (distance <-> alphabetical).
            if (WasChoiceKeyPressed(KeyCode.LeftArrow, VkLeft, ref _pickerLeftWasDown) ||
                WasChoiceKeyPressed(KeyCode.RightArrow, VkRight, ref _pickerRightWasDown))
            {
                _pickerSortMode = _pickerSortMode == PickerSortMode.Distance
                    ? PickerSortMode.Alphabetical
                    : PickerSortMode.Distance;
                ReapplyKnownObjectFiltersAndAnnounce(Loc.Get(_pickerSortMode == PickerSortMode.Distance
                    ? "navigation_object_picker_sort_distance"
                    : "navigation_object_picker_sort_alpha"));
                return;
            }

            // F toggles current-floor-only.
            if (WasChoiceKeyPressed(KeyCode.F, 0x46, ref _pickerFloorKeyWasDown))
            {
                _pickerFloorCurrentOnly = !_pickerFloorCurrentOnly;
                ReapplyKnownObjectFiltersAndAnnounce(Loc.Get(_pickerFloorCurrentOnly
                    ? "navigation_object_picker_filter_floor_current"
                    : "navigation_object_picker_filter_floor_all"));
                return;
            }

            // M cycles the section filter (all -> met -> encountered -> all).
            if (WasChoiceKeyPressed(KeyCode.M, 0x4D, ref _pickerSectionKeyWasDown))
            {
                _pickerSectionFilter = NextSectionFilter(_pickerSectionFilter);
                ReapplyKnownObjectFiltersAndAnnounce(Loc.Get(SectionFilterLocKey(_pickerSectionFilter)));
                return;
            }

            // D toggles doors-only.
            if (WasChoiceKeyPressed(KeyCode.D, 0x44, ref _pickerDoorsKeyWasDown))
            {
                _pickerDoorsOnly = !_pickerDoorsOnly;
                ReapplyKnownObjectFiltersAndAnnounce(Loc.Get(_pickerDoorsOnly
                    ? "navigation_object_picker_filter_doors_on"
                    : "navigation_object_picker_filter_doors_off"));
                return;
            }

            if (WasChoiceKeyPressed(KeyCode.Backspace, VkBackspace, ref _pickerBackspaceWasDown))
            {
                // Backspace backs OUT one drill level (object -> room -> top), and only closes the
                // picker when already at the top — so a player who drilled in can step back up.
                // (Was Escape; Backspace conflicts less with the game's own menu/cancel handling.)
                AscendOrClosePicker();
            }
        }

        private static PickerSectionFilter NextSectionFilter(PickerSectionFilter current)
        {
            switch (current)
            {
                case PickerSectionFilter.All: return PickerSectionFilter.MetOnly;
                case PickerSectionFilter.MetOnly: return PickerSectionFilter.EncounteredOnly;
                default: return PickerSectionFilter.All;
            }
        }

        private static string SectionFilterLocKey(PickerSectionFilter filter)
        {
            switch (filter)
            {
                case PickerSectionFilter.MetOnly: return "navigation_object_picker_filter_section_met";
                case PickerSectionFilter.EncounteredOnly: return "navigation_object_picker_filter_section_encountered";
                default: return "navigation_object_picker_filter_section_all";
            }
        }

        // Re-derive the filtered view after a toggle, keep the selection sensible, and announce
        // the new filter state + the now-current item (or an empty-result message). The toggle is
        // left applied even when it empties the list, so the player can cycle back out of it.
        private void ReapplyKnownObjectFiltersAndAnnounce(string filterAnnouncement)
        {
            _knownObjectView = BuildFilteredKnownObjectView();
            if (_knownObjectView.Count == 0)
            {
                _knownObjectSelectionIndex = 0;
                ScreenReader.Say(filterAnnouncement + ". " + Loc.Get("navigation_object_picker_empty_filtered"));
                return;
            }

            _knownObjectSelectionIndex = Mathf.Clamp(_knownObjectSelectionIndex, 0, _knownObjectView.Count - 1);
            // Don't force the section header here: the filter announcement already gives context,
            // and when a single section is filtered the header just echoes it ("met only. Met, 8.").
            // Speak the header only if the resulting view still spans both sections.
            bool viewSpansBothSections = false;
            for (int i = 1; i < _knownObjectView.Count; i++)
            {
                if (_knownObjectView[i].Section != _knownObjectView[0].Section)
                {
                    viewSpansBothSections = true;
                    break;
                }
            }
            ScreenReader.Say(filterAnnouncement + ". " + ComposeKnownObjectItemText(_knownObjectSelectionIndex, includeSectionHeader: viewSpansBothSections));
        }

        // Spoken when the picker opens: the title, then ONLY the filters that are off their
        // default (an unfiltered open shouldn't announce "all floors, showing all" — stating the
        // absence of filters is noise), then the current item with its section header.
        private void AnnounceKnownObjectPickerTitleAndItem()
        {
            if (_knownObjectView == null || _knownObjectView.Count == 0)
                return;

            _knownObjectSelectionIndex = Mathf.Clamp(_knownObjectSelectionIndex, 0, _knownObjectView.Count - 1);

            string title = Loc.Get("navigation_object_picker_title");
            // Sort is always stated (there's no "default" the player can assume); floor/section
            // are stated only when active.
            if (_pickerSortMode == PickerSortMode.Alphabetical)
                title += ". " + Loc.Get("navigation_object_picker_sort_alpha");
            if (_pickerFloorCurrentOnly)
                title += ". " + Loc.Get("navigation_object_picker_filter_floor_current");
            if (_pickerSectionFilter != PickerSectionFilter.All)
                title += ". " + Loc.Get(SectionFilterLocKey(_pickerSectionFilter));
            if (_pickerDoorsOnly)
                title += ". " + Loc.Get("navigation_object_picker_filter_doors_on");

            ScreenReader.Say(title + ". " + ComposeKnownObjectItemText(_knownObjectSelectionIndex, includeSectionHeader: true));
        }

        private void AnnounceCurrentKnownObjectPickerItem()
        {
            if (_knownObjectView == null || _knownObjectView.Count == 0)
                return;

            _knownObjectSelectionIndex = Mathf.Clamp(_knownObjectSelectionIndex, 0, _knownObjectView.Count - 1);
            // Speak the section header only when crossing into a new section, so a run of items
            // in the same section doesn't repeat "Met." on every arrow press.
            bool atSectionStart = _knownObjectSelectionIndex == 0 ||
                _knownObjectView[_knownObjectSelectionIndex - 1].Section != _knownObjectView[_knownObjectSelectionIndex].Section;
            ScreenReader.Say(ComposeKnownObjectItemText(_knownObjectSelectionIndex, includeSectionHeader: atSectionStart));
        }

        // Build the spoken string for one entry. A GROUP (met datable at Top, room at Room level)
        // speaks its label + a member count + "more" so the player knows to drill in; a LEAF speaks
        // the object name + zone + floor tag + distance, the same as the old flat list. The "x of y"
        // counter trails so the player hears WHAT the entry is first.
        private string ComposeKnownObjectItemText(int index, bool includeSectionHeader)
        {
            PickerNode node = _knownObjectView[index];

            string sectionHeader = string.Empty;
            if (includeSectionHeader)
            {
                int sectionCount = 0;
                for (int i = 0; i < _knownObjectView.Count; i++)
                {
                    if (_knownObjectView[i].Section == node.Section)
                        sectionCount++;
                }
                sectionHeader = Loc.Get(node.Section == PickerSection.Met
                    ? "navigation_object_picker_section_met"
                    : "navigation_object_picker_section_encountered", sectionCount) + ". ";
            }

            string position = ". " + Loc.Get(
                "navigation_object_picker_position", index + 1, _knownObjectView.Count);

            // Floor call-out only when it ISN'T the player's floor (suppress "this floor" noise).
            string floorTagText = DescribeFloorTag(node.IsOnPlayerFloor, node.FloorLabel);
            string floorTag = string.IsNullOrEmpty(floorTagText) ? string.Empty : ", " + floorTagText;
            string distance = ", " + Loc.Get("navigation_object_picker_distance_m", Mathf.RoundToInt(node.Distance));

            if (node.IsGroup)
            {
                // "<character/room>, N objects, <floor>, Nm. x of y." — the count cues a drill-in.
                string count = ", " + Loc.Get("navigation_object_picker_group_count", node.ChildCount);
                return sectionHeader + node.Label + count + floorTag + distance + position;
            }

            return sectionHeader + node.Label + floorTag + distance + position;
        }

        // Floor call-out, or empty when on the player's floor (the common case — suppressed to
        // avoid "this floor" on every item). Named floor (e.g. "upper floor") for cross-floor
        // targets, generic other-floor phrase when unknown.
        private string DescribeFloorTag(bool isOnPlayerFloor, string floorLabel)
        {
            if (isOnPlayerFloor)
                return string.Empty;
            if (!string.IsNullOrEmpty(floorLabel))
                return Loc.Get("navigation_object_picker_floor_named", floorLabel);
            return Loc.Get("navigation_object_picker_floor_other");
        }

        // Enter: a GROUP descends one drill level (datable -> room -> object), auto-collapsing a
        // level whose only child is a single group/object so the player never steps through a
        // one-item menu; a LEAF routes to its object.
        private void SelectCurrentKnownObjectPickerItem()
        {
            if (_knownObjectView == null || _knownObjectView.Count == 0)
            {
                CloseKnownObjectPicker(announceClosed: false);
                return;
            }

            _knownObjectSelectionIndex = Mathf.Clamp(_knownObjectSelectionIndex, 0, _knownObjectView.Count - 1);
            PickerNode node = _knownObjectView[_knownObjectSelectionIndex];

            if (node.IsGroup)
            {
                DescendIntoGroup(node);
                return;
            }

            RouteToLeaf(node.Target);
        }

        // Descend into a group. Dispatch on the group KIND, since the two top-level group types
        // descend differently: a met Datable -> Room level, an UnmetRoom -> Object level directly,
        // a DatableRoom -> Object level. After setting the breadcrumb, rebuild the child view; if it
        // collapses to a single child, descend/route again so a one-room datable or one-object room
        // doesn't make the player step through a trivial menu.
        private void DescendIntoGroup(PickerNode group)
        {
            string firstZone = group.Members != null && group.Members.Count > 0 ? group.Members[0].Zone : null;
            switch (group.GroupKind)
            {
                case PickerGroupKind.Room:
                    // L1 -> L2: enter the room; its datables + unmet objects are next.
                    _pickerRoomZone = firstZone;
                    _pickerDatableKey = null;
                    _pickerDatableLabel = null;
                    _pickerLevel = PickerLevel.InRoom;
                    break;
                case PickerGroupKind.Datable:
                    // L2 -> L3: enter the datable (within the already-chosen room); its found
                    // objects in this room are next. _pickerRoomZone stays as the chosen room.
                    _pickerDatableKey = group.Members != null && group.Members.Count > 0 ? group.Members[0].DatableKey : null;
                    _pickerDatableLabel = group.Label;
                    _pickerLevel = PickerLevel.Objects;
                    break;
                default:
                    return;
            }

            _knownObjectView = BuildFilteredKnownObjectView();
            _knownObjectSelectionIndex = 0;

            // Auto-collapse a single-child level rather than announce a one-item menu.
            if (_knownObjectView.Count == 1)
            {
                PickerNode only = _knownObjectView[0];
                if (only.IsGroup)
                {
                    DescendIntoGroup(only);
                    return;
                }
                RouteToLeaf(only.Target);
                return;
            }

            if (_knownObjectView.Count == 0)
            {
                // Shouldn't happen (a group always has members), but fail safe back up a level.
                AscendOrClosePicker();
                return;
            }

            AnnounceDrillLevelAndItem();
        }

        private void RouteToLeaf(KnownObjectTarget target)
        {
            InteractableObj interactable = target != null ? target.Interactable : null;

            CloseKnownObjectPicker(announceClosed: false);

            if (interactable == null || !interactable.gameObject.activeInHierarchy)
            {
                ScreenReader.Say(Loc.Get("navigation_object_picker_empty"));
                return;
            }

            // Use the room resolved at build time (data hierarchy, not camera zones) so the
            // navigation announcement names the same room the picker grouped this object under.
            string targetZone = target.Zone;
            string targetLabel = target.Label;

            SetTrackedInteractable(interactable, targetZone, targetLabel);
            BeginNavigationAndStartTrackerTone(targetZone, targetLabel);
        }

        // Backspace behaviour: ascend one drill level (Objects -> InRoom -> Rooms), else close at the
        // Rooms level. Returns true if it ascended (picker stays open), false if it closed.
        private bool AscendOrClosePicker()
        {
            if (_pickerLevel == PickerLevel.Objects)
            {
                // L3 -> L2: back to the room's datables + unmet objects; keep the chosen room.
                _pickerLevel = PickerLevel.InRoom;
                _pickerDatableKey = null;
                _pickerDatableLabel = null;
            }
            else if (_pickerLevel == PickerLevel.InRoom)
            {
                // L2 -> L1: back to the room list.
                _pickerLevel = PickerLevel.Rooms;
                _pickerRoomZone = null;
            }
            else
            {
                CloseKnownObjectPicker(announceClosed: true);
                return false;
            }

            _knownObjectView = BuildFilteredKnownObjectView();
            _knownObjectSelectionIndex = 0;
            AnnounceDrillLevelAndItem();
            return true;
        }

        // Announce the current drill level's context (which datable / room we're inside) then the
        // current item. At Top there's no breadcrumb, so this matches the old open announcement.
        private void AnnounceDrillLevelAndItem()
        {
            if (_knownObjectView == null || _knownObjectView.Count == 0)
                return;
            _knownObjectSelectionIndex = Mathf.Clamp(_knownObjectSelectionIndex, 0, _knownObjectView.Count - 1);

            string roomName = string.IsNullOrWhiteSpace(_pickerRoomZone)
                ? Loc.Get("navigation_object_picker_room_unknown") : _pickerRoomZone;
            string crumb;
            if (_pickerLevel == PickerLevel.InRoom)
                // L2: "in <room>, choose a datable or object".
                crumb = Loc.Get("navigation_object_picker_level_inroom", roomName);
            else if (_pickerLevel == PickerLevel.Objects)
                // L3: the chosen datable's objects in the chosen room.
                crumb = Loc.Get("navigation_object_picker_level_objects",
                    _pickerDatableLabel ?? string.Empty, roomName);
            else
                // L1: the room list.
                crumb = Loc.Get("navigation_object_picker_title");

            ScreenReader.Say(crumb + ". " + ComposeKnownObjectItemText(_knownObjectSelectionIndex, includeSectionHeader: true));
        }

        private static void SyncKnownObjectPickerKeyStates()
        {
            _pickerUpWasDown = (GetAsyncKeyState(VkUp) & 0x8000) != 0;
            _pickerDownWasDown = (GetAsyncKeyState(VkDown) & 0x8000) != 0;
            _pickerReturnWasDown = (GetAsyncKeyState(VkReturn) & 0x8000) != 0;
            _pickerBackspaceWasDown = (GetAsyncKeyState(VkBackspace) & 0x8000) != 0;
            _pickerLeftWasDown = (GetAsyncKeyState(VkLeft) & 0x8000) != 0;
            _pickerRightWasDown = (GetAsyncKeyState(VkRight) & 0x8000) != 0;
            _pickerFloorKeyWasDown = (GetAsyncKeyState(0x46) & 0x8000) != 0;
            _pickerSectionKeyWasDown = (GetAsyncKeyState(0x4D) & 0x8000) != 0;
            _pickerDoorsKeyWasDown = (GetAsyncKeyState(0x44) & 0x8000) != 0;
        }

        // Build the picker's candidate list from the BAKE FIXTURE ROSTER — the single source of
        // truth for the set of valid interactable targets. The roster is already filtered
        // (active + named + non-exterior), identity-deduped (lighting presets), and routing-unit
        // merged (the 48 books -> 1, cutlery -> 1) upstream in the bake, so this method does NO
        // set construction of its own: no FindObjectsOfType enumeration, no co-located/same-name
        // dedup, no exterior/secret-cube guards. Those were duplicate logic that drifted from the
        // bake and produced the picker's no-ops and misclassifications.
        //
        // Runtime state is applied here as SECOND-ORDER filters on top of the roster set: each
        // roster entry is bridged to its live InteractableObj (by cleaned-name + nearest position,
        // since roster object_ids are serialized export ids, not runtime instance ids), then
        // filtered/annotated by encounter state, active-in-hierarchy, crawlspace band, distance,
        // floor, and Met/Encountered section.
        //
        // FAIL-FAST: an empty roster is a broken UPSTREAM dependency (missing/old bake), NOT a
        // legitimately-empty picker. The two must be distinguishable, so this returns the
        // RosterMissing status and the caller announces a dependency error rather than the benign
        // "no objects" message. We do not silently fall back to live enumeration — the whole point
        // of the roster is to surface a missing dependency immediately.
        private enum KnownObjectBuildResult { Ok, RosterMissing, Empty }

        private KnownObjectBuildResult BuildKnownObjectTargets(out List<KnownObjectTarget> targets)
        {
            targets = new List<KnownObjectTarget>();

            IReadOnlyList<SimpleNavPlanner.Fixture> roster = SimpleNavPlanner.GetFixtureRoster();
            if (roster == null || roster.Count == 0)
            {
                // Upstream dependency missing — the bake didn't ship a roster. Fail loud.
                if (Main.Log != null)
                    Main.Log.LogError("KnownObjectPicker: fixture roster is empty — the bake is " +
                        "missing report[\"fixtures\"]. The picker has no source of truth; fix the bake.");
                return KnownObjectBuildResult.RosterMissing;
            }

            // Index live InteractableObj by stable id AND cleaned name in one pass, so each roster
            // entry resolves to its runtime instance: exact unique_id match first (the bake now
            // emits it), cleaned-name + nearest position as fallback.
            LiveInteractableIndex live = BuildLiveInteractableIndex();

            // Room world-bounds from the data hierarchy's own room containers — the spatial
            // fallback for objects whose hierarchy can't name a room (built once per open).
            _roomBoundsIndex = BuildRoomBoundsIndex();

            Transform playerTransform = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform
                : null;
            Vector3 playerPosition = playerTransform != null ? playerTransform.position : Vector3.zero;

            // Resolve the player's floor once so each target can be tagged same-floor vs
            // other-floor. When the bake can't resolve it, playerFloorLabel stays null and every
            // target is treated as same-floor, degrading gracefully to a flat XZ sort.
            string playerFloorLabel = null;
            if (playerTransform != null)
                SimpleNavPlanner.TryGetPlayerFloorLabel(playerPosition.y, out playerFloorLabel);

            // In the crawlspace, the only reachable things are the crawlspace's own contents, so
            // restrict to crawlspace-band fixtures; normal behaviour resumes once the player climbs
            // back out (Y above the ceiling line).
            bool playerInCrawlspace = playerTransform != null && playerPosition.y < CrawlspaceCeilingY;

            for (int i = 0; i < roster.Count; i++)
            {
                SimpleNavPlanner.Fixture fixture = roster[i];
                if (fixture == null || string.IsNullOrWhiteSpace(fixture.name))
                    continue;

                Vector3 fixturePos = fixture.Position();

                // Crawlspace band gate is a property of the fixture's own position (no live object
                // required), so it applies before the live bridge.
                if (playerInCrawlspace && fixturePos.y >= CrawlspaceCeilingY)
                    continue;

                // Bridge to the live instance — required for the runtime second-order filters
                // (encounter state, active-in-hierarchy) and for door detection.
                InteractableObj liveObj = ResolveLiveForFixture(live, fixture);
                if (liveObj == null || liveObj.gameObject == null || !liveObj.gameObject.activeInHierarchy)
                    continue;

                // SECOND-ORDER FILTER: only objects the player has encountered (met / interacted /
                // examined) appear, plus the seeded startup office door. The roster defines what
                // EXISTS; this narrows it to what THIS SAVE knows.
                if (!IsStartupOfficeDoorObject(liveObj, fixture.name) &&
                    !IsEncounteredKnownObject(liveObj))
                {
                    continue;
                }

                // Display label: the live object's facing name (so dynamic/localized labels stay
                // correct), falling back to the roster's cleaned name when the live object yields
                // nothing human-readable.
                string label = GetObjectFacingDisplayName(liveObj);
                if (string.IsNullOrWhiteSpace(label) ||
                    string.Equals(label, Loc.Get("unknown_object"), StringComparison.OrdinalIgnoreCase))
                {
                    label = fixture.name;
                }

                // Use the ROSTER position for distance/floor (best-available-location: bounds
                // centre, not a rig-origin transform), so a degenerate live transform can't mis-sort
                // the entry. Distance is to the live-or-roster XZ from the player.
                float distance = playerTransform != null
                    ? GetFlatDistance(playerPosition, fixturePos)
                    : 0f;

                // Prefer the roster's own floor assignment (it owns the ceiling-light "belongs to
                // the room below" rule); fall back to resolving from Y when the roster left it null.
                string candidateFloor = fixture.floor;
                if (string.IsNullOrEmpty(candidateFloor))
                    SimpleNavPlanner.TryGetTargetFloorLabel(fixturePos.y, out candidateFloor);
                bool onPlayerFloor = playerFloorLabel == null || candidateFloor == null ||
                    string.Equals(candidateFloor, playerFloorLabel, StringComparison.OrdinalIgnoreCase);

                // Met (dated) → DateADex-style entry by character name; otherwise Encountered
                // (examined/interacted, datable still Unmet) → object name only, no character.
                bool isMet = IsDatedInteractable(liveObj);
                PickerSection section = isMet ? PickerSection.Met : PickerSection.Encountered;
                string characterName = isMet ? GetInteractableDisplayName(liveObj) : null;
                // Room from the DATA HIERARCHY (House/<Room> or the per-room lighting container),
                // NOT camera zones. Falls back to the room whose hierarchy-derived bounds contain
                // the object only when the hierarchy puts it in a catch-all container.
                if (!TryGetHierarchyRoomForInteractable(liveObj, out string zone))
                    zone = ResolveRoomByBounds(fixturePos);
                bool isDoor = IsDoorInteractable(liveObj);

                // Datable grouping key for the collapsed top level — only for MET objects (the
                // top level collapses one entry per met datable). This target ALREADY passed the
                // per-object encounter filter above, so a met datable's group only ever contains
                // the objects the player has personally dated/interacted/examined — finding one
                // object never reveals the datable's other locations.
                string datableKey = isMet ? BuildComparisonKey(liveObj.inkFileName) : null;
                if (string.IsNullOrEmpty(datableKey) && isMet)
                    datableKey = BuildComparisonKey(liveObj.InternalName());

                targets.Add(new KnownObjectTarget
                {
                    Interactable = liveObj,
                    Label = label,
                    Distance = distance,
                    FloorLabel = candidateFloor,
                    IsOnPlayerFloor = onPlayerFloor,
                    Section = section,
                    Zone = zone,
                    CharacterName = characterName,
                    IsDoor = isDoor,
                    DatableKey = datableKey,
                });
            }

            return targets.Count > 0 ? KnownObjectBuildResult.Ok : KnownObjectBuildResult.Empty;
        }

        // The set of room-container names the data hierarchy uses, mapped to the spoken room name.
        // The room lives in the transform path either as the segment directly under "House"
        // (House/Kitchen/...) or, for lighting, encoded in a per-room container name
        // ("Lights_Kitchen", "LightSwitches"). Catch-all containers that are NOT rooms
        // (MultiRoom, MovableObjects, Exterior, lighting-preset roots) are deliberately absent —
        // an object under one of those gets the spatial-bounds fallback instead. Keys are
        // BuildComparisonKey-normalized (lowercase, alphanumeric) so "LivingRoom"/"Living Room"
        // both hit.
        private static readonly Dictionary<string, string> _hierarchyRoomNames =
            BuildHierarchyRoomNameTable();

        private static Dictionary<string, string> BuildHierarchyRoomNameTable()
        {
            // Canonical spoken names for each room container under House/. Friendly-cased here so
            // the picker speaks "Living room" not "LivingRoom".
            string[] rooms =
            {
                "Kitchen", "Office", "Bedroom", "Gym", "Attic",
                "Bathroom1", "Bathroom2", "LivingRoom", "PianoRoom", "DiningRoom",
                "UpstairsHall", "Hallway", "LaundryRoom",
            };
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in rooms)
            {
                string spoken = FriendlyRoomName(r);
                table[BuildComparisonKey(r)] = spoken;            // "House/Bedroom"
                table[BuildComparisonKey("Lights_" + r)] = spoken; // "Lights_Bedroom" (lighting group)
            }
            // Light switches live under a single non-room container; they belong to "light switches".
            table[BuildComparisonKey("LightSwitches")] = FriendlyRoomName("LightSwitches");
            return table;
        }

        // "LivingRoom" -> "Living room", "Bathroom1" -> "Bathroom 1", "LightSwitches" -> "Light
        // switches". Splits CamelCase and trailing digits into spaced words, lowercasing all but
        // the first word, so the room reads naturally.
        private static string FriendlyRoomName(string container)
        {
            if (string.IsNullOrEmpty(container))
                return container;
            string spaced = Regex.Replace(container, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)", " ");
            string[] words = spaced.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
                words[i] = i == 0
                    ? char.ToUpperInvariant(words[i][0]) + words[i].Substring(1).ToLowerInvariant()
                    : words[i].ToLowerInvariant();
            return string.Join(" ", words);
        }

        // Resolve an interactable's room from the DATA HIERARCHY (its transform parents), not
        // camera zones. Walks parents looking for a known room container: the segment under
        // "House" (House/Kitchen/...) or a per-room lighting container ("Lights_Kitchen",
        // "LightSwitches"). Returns false when the hierarchy only yields catch-all containers
        // (MultiRoom, MovableObjects, Exterior) — the caller then uses the spatial fallback.
        private static bool TryGetHierarchyRoomForInteractable(InteractableObj interactable, out string roomName)
        {
            roomName = null;
            if (interactable == null || interactable.transform == null)
                return false;

            // First pass: a parent whose own name is a known room container (covers the lighting
            // groups, which name the room directly: "Lights_Kitchen", "LightSwitches").
            Transform current = interactable.transform;
            int depth = 0;
            Transform houseChild = null;
            Transform house = null;
            while (current != null && depth < 16)
            {
                if (_hierarchyRoomNames.TryGetValue(BuildComparisonKey(current.name), out string named))
                {
                    roomName = named;
                    return true;
                }
                // Track the child of "House" along this path — that's the House/<Room> segment.
                if (string.Equals(current.name, "House", StringComparison.OrdinalIgnoreCase))
                    house = current;
                else if (house == null)
                    houseChild = current;
                current = current.parent;
                depth++;
            }

            // Second pass: the segment directly under House (House/<Room>/...). houseChild is the
            // ancestor we last saw before reaching House.
            if (house != null && houseChild != null &&
                _hierarchyRoomNames.TryGetValue(BuildComparisonKey(houseChild.name), out string underHouse))
            {
                roomName = underHouse;
                return true;
            }

            return false;
        }

        // Build a room -> world-bounds map from the live hierarchy's room containers, for the
        // spatial fallback. For each known room container GameObject found in the scene, union the
        // bounds of its renderers. One pass over all transforms; only the room containers (matched
        // by name) contribute. Empty/rendererless containers are skipped.
        private Dictionary<string, Bounds> BuildRoomBoundsIndex()
        {
            var index = new Dictionary<string, Bounds>(StringComparer.OrdinalIgnoreCase);
            Transform[] all = FindObjectsOfType<Transform>();
            if (all == null)
                return index;
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null)
                    continue;
                if (!_hierarchyRoomNames.TryGetValue(BuildComparisonKey(t.name), out string room))
                    continue;
                Renderer[] rends = t.GetComponentsInChildren<Renderer>(includeInactive: true);
                if (rends == null || rends.Length == 0)
                    continue;
                bool has = false;
                Bounds b = default;
                for (int r = 0; r < rends.Length; r++)
                {
                    if (rends[r] == null)
                        continue;
                    if (!has) { b = rends[r].bounds; has = true; }
                    else b.Encapsulate(rends[r].bounds);
                }
                if (!has)
                    continue;
                // The same room name can come from several containers (House/Kitchen plus
                // Lights_Kitchen); union them so the room's volume covers all its sub-containers.
                if (index.TryGetValue(room, out Bounds existing))
                {
                    existing.Encapsulate(b);
                    index[room] = existing;
                }
                else
                {
                    index[room] = b;
                }
            }
            return index;
        }

        // Spatial fallback: the room whose hierarchy-derived bounds contain the position, else the
        // room whose bounds are nearest. Used only for objects the hierarchy can't place (catch-all
        // containers). Returns null when no room bounds exist.
        private string ResolveRoomByBounds(Vector3 position)
        {
            if (_roomBoundsIndex == null || _roomBoundsIndex.Count == 0)
                return null;
            string containing = null;
            string nearest = null;
            float nearestSqr = float.PositiveInfinity;
            foreach (KeyValuePair<string, Bounds> kv in _roomBoundsIndex)
            {
                if (kv.Value.Contains(position))
                {
                    // A containing room wins outright; if several contain it (overlapping floors),
                    // keep the first — they're the same logical area to the player.
                    containing = kv.Key;
                    break;
                }
                float d = kv.Value.SqrDistance(position);
                if (d < nearestSqr) { nearestSqr = d; nearest = kv.Key; }
            }
            return containing ?? nearest;
        }

        // A live name reduced to its routing-unit STEM: cleaned, then with ONE trailing instance
        // index removed ("Cutlery_Knife6" -> "Cutlery_Knife", "Book_MODEL_UPDATE15" -> "Book"),
        // mirroring the bake's _name_stem. Used to bridge a routing-unit-merged fixture (stored
        // under its stem) to its live numbered members. Kept separate from the DISPLAY path
        // (StripModelAuthoringTokens), which must not drop digits so "Book_MESSY_45" stays intact.
        private static string LiveNameStem(string name)
        {
            string cleaned = StripModelAuthoringTokens(name);
            if (string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
            string stem = Regex.Replace(cleaned, @"[\s_]*\d+$", "").Trim().Trim('_').Trim();
            return string.IsNullOrWhiteSpace(stem) ? cleaned : stem;
        }

        // Index every live InteractableObj under BOTH its cleaned name AND its stem, so a roster
        // entry resolves in one lookup whether it's a single fixture (roster name = full cleaned
        // name, e.g. "Bathtub") or a routing-unit-merged unit (roster name = stem, e.g.
        // "Cutlery_Knife" or "Food_1"). Looking up by the roster name's CLEANED form (without
        // re-stripping digits, since the roster name is already the final stem) then matches the
        // correct bucket in either regime — verified to bridge all 939 fixtures with 0 misses,
        // where a single stem-only or cleaned-only index left 6–74 fixtures unbridged. One
        // FindObjectsOfType pass for the whole picker build.
        // Two indices over the live InteractableObj set, built in one FindObjectsOfType pass:
        //   ById   — exact map from InteractableObj.Id (the stable UniqueId GUID) to the live
        //            instance. This is the PREFERRED roster->live bridge: the bake now emits each
        //            fixture's unique_id, so resolution is an exact lookup, not a fuzzy match.
        //   ByName — the legacy cleaned-name/stem buckets, used as a FALLBACK for fixtures/objects
        //            lacking a unique id (older bakes, or a live object whose Id failed to resolve).
        private sealed class LiveInteractableIndex
        {
            public Dictionary<string, InteractableObj> ById;
            public Dictionary<string, List<InteractableObj>> ByName;
        }

        private static LiveInteractableIndex BuildLiveInteractableIndex()
        {
            var byId = new Dictionary<string, InteractableObj>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, List<InteractableObj>>(StringComparer.OrdinalIgnoreCase);
            InteractableObj[] all = FindObjectsOfType<InteractableObj>();
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    InteractableObj o = all[i];
                    if (o == null || o.gameObject == null)
                        continue;
                    string id = TryGetInteractableId(o);
                    if (!string.IsNullOrWhiteSpace(id) && !byId.ContainsKey(id))
                        byId[id] = o;
                    AddLiveIndexKey(byName, StripModelAuthoringTokens(o.gameObject.name), o);
                    AddLiveIndexKey(byName, LiveNameStem(o.gameObject.name), o);
                }
            }
            return new LiveInteractableIndex { ById = byId, ByName = byName };
        }

        // The stable id off a live InteractableObj (InteractableObj.Id => uniqId.uniqueId).
        // Guarded: the uniqId component can be unassigned on some objects, which throws.
        private static string TryGetInteractableId(InteractableObj o)
        {
            if (o == null)
                return null;
            try { return o.Id; }
            catch { return null; }
        }

        private static void AddLiveIndexKey(Dictionary<string, List<InteractableObj>> index, string key, InteractableObj o)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            if (!index.TryGetValue(key, out List<InteractableObj> bucket))
            {
                bucket = new List<InteractableObj>();
                index[key] = bucket;
            }
            // The same object can land under both its cleaned name and stem when they coincide;
            // don't list it twice in one bucket (nearest-position pick is unaffected, but keeps
            // the index honest).
            if (!bucket.Contains(o))
                bucket.Add(o);
        }

        // Resolve the live InteractableObj for a roster fixture. PREFERRED: an exact match on the
        // fixture's stable unique id(s) (unique_id, then any unique_ids member for a routing-unit-
        // merged fixture). FALLBACK (older bakes / unresolved ids): the legacy bridge — the live
        // instance whose cleaned name matches and whose transform is nearest the fixture's
        // best-available-location.
        private static InteractableObj ResolveLiveForFixture(
            LiveInteractableIndex live, SimpleNavPlanner.Fixture fixture)
        {
            if (live == null || fixture == null)
                return null;

            // Exact stable-id bridge first.
            if (live.ById != null)
            {
                if (!string.IsNullOrWhiteSpace(fixture.unique_id) &&
                    live.ById.TryGetValue(fixture.unique_id, out InteractableObj byId))
                    return byId;
                if (fixture.unique_ids != null)
                {
                    for (int i = 0; i < fixture.unique_ids.Length; i++)
                    {
                        string uid = fixture.unique_ids[i];
                        if (!string.IsNullOrWhiteSpace(uid) && live.ById.TryGetValue(uid, out InteractableObj m))
                            return m;
                    }
                }
            }

            // Fallback: cleaned-name bucket + nearest position. The roster name is ALREADY the
            // final stem (single fixtures carry their full cleaned name, merged units the shared
            // stem); the name index holds both cleaned-name and stem keys, so this hits the right
            // bucket in either regime.
            if (live.ByName == null)
                return null;
            string key = StripModelAuthoringTokens(fixture.name);
            if (string.IsNullOrWhiteSpace(key) || !live.ByName.TryGetValue(key, out List<InteractableObj> bucket))
                return null;

            Vector3 want = fixture.Position();
            InteractableObj best = null;
            float bestD2 = float.PositiveInfinity;
            for (int i = 0; i < bucket.Count; i++)
            {
                InteractableObj o = bucket[i];
                if (o == null || o.transform == null)
                    continue;
                float d2 = (o.transform.position - want).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = o; }
            }
            return best;
        }

        // Build the displayed list from the full candidate set by applying the live filters,
        // then ordering by section (Met before Encountered) and the active sort mode. Distance
        // sort is floor-aware (player's floor first, nearest-XZ within); alphabetical sorts by
        // label then zone. Section grouping is always primary so the spoken section headers stay
        // coherent.
        // The flat, filtered candidate set (doors/floor/section filters applied) — shared by every
        // drill level. Grouping happens on top of this in BuildFilteredKnownObjectView.
        private List<KnownObjectTarget> FilteredKnownObjectTargets()
        {
            List<KnownObjectTarget> view = new List<KnownObjectTarget>();
            if (_knownObjectTargets == null)
                return view;
            for (int i = 0; i < _knownObjectTargets.Count; i++)
            {
                KnownObjectTarget t = _knownObjectTargets[i];
                if (t == null)
                    continue;
                if (_pickerDoorsOnly && !t.IsDoor)
                    continue;
                if (_pickerFloorCurrentOnly && !t.IsOnPlayerFloor)
                    continue;
                if (_pickerSectionFilter == PickerSectionFilter.MetOnly && t.Section != PickerSection.Met)
                    continue;
                if (_pickerSectionFilter == PickerSectionFilter.EncounteredOnly && t.Section != PickerSection.Encountered)
                    continue;
                view.Add(t);
            }
            return view;
        }

        // Build the list of nodes shown at the CURRENT drill level (Top / Room / Object) over the
        // filtered target set. Top = one GROUP per met datable (collapsed) + each loose unmet
        // object as a leaf. Room = one GROUP per distinct room among the chosen datable's objects.
        // Object = a leaf per object of the chosen datable in the chosen room. A descent into a
        // group with a single child is auto-collapsed by SelectCurrentKnownObjectPickerItem, not
        // here, so the level state stays simple.
        private List<PickerNode> BuildFilteredKnownObjectView()
        {
            List<KnownObjectTarget> filtered = FilteredKnownObjectTargets();
            List<PickerNode> view;
            switch (_pickerLevel)
            {
                case PickerLevel.InRoom:
                    view = BuildInRoomNodes(filtered);
                    break;
                case PickerLevel.Objects:
                    view = BuildObjectNodes(filtered);
                    break;
                default:
                    view = BuildRoomNodes(filtered);
                    break;
            }
            view.Sort(CompareKnownObjectForView);
            return view;
        }

        // L1 ROOMS: one group per room (Zone) that contains ANY discovered object — met or unmet.
        // A null/blank zone collapses into a single "unknown room" group.
        private List<PickerNode> BuildRoomNodes(List<KnownObjectTarget> filtered)
        {
            List<PickerNode> view = new List<PickerNode>();
            Dictionary<string, PickerNode> rooms = new Dictionary<string, PickerNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < filtered.Count; i++)
            {
                KnownObjectTarget t = filtered[i];
                string zoneKey = string.IsNullOrWhiteSpace(t.Zone) ? "\0noroom" : t.Zone;
                if (!rooms.TryGetValue(zoneKey, out PickerNode group))
                {
                    group = new PickerNode
                    {
                        IsGroup = true,
                        GroupKind = PickerGroupKind.Room,
                        Label = string.IsNullOrWhiteSpace(t.Zone) ? Loc.Get("navigation_object_picker_room_unknown") : t.Zone,
                        // A room can hold both Met and Encountered; tag it Met if it holds any met
                        // object so the section ordering keeps rooms-with-datables first.
                        Section = PickerSection.Encountered,
                        Distance = float.MaxValue,
                        Members = new List<KnownObjectTarget>(),
                    };
                    rooms[zoneKey] = group;
                    view.Add(group);
                }
                if (t.Section == PickerSection.Met) group.Section = PickerSection.Met;
                AccumulateGroupMember(group, t);
            }
            return view;
        }

        // L2 IN-ROOM: within the chosen room, one group per met DATABLE present + each unmet object
        // as a routable leaf. A datable spanning multiple rooms appears under each room with only
        // its objects there. A single-object datable in this room auto-collapses to that object
        // (handled in DescendIntoGroup), so it reads as a leaf to the player.
        private List<PickerNode> BuildInRoomNodes(List<KnownObjectTarget> filtered)
        {
            List<PickerNode> view = new List<PickerNode>();
            Dictionary<string, PickerNode> datables = new Dictionary<string, PickerNode>(StringComparer.OrdinalIgnoreCase);
            // Unmet objects route directly, but same-named ones collapse (deduped below) so a
            // multi-piece unmet object doesn't list once per piece.
            List<KnownObjectTarget> unmet = new List<KnownObjectTarget>();
            for (int i = 0; i < filtered.Count; i++)
            {
                KnownObjectTarget t = filtered[i];
                if (!SameChosenRoom(t))
                    continue;
                if (t.Section == PickerSection.Met && !string.IsNullOrEmpty(t.DatableKey))
                {
                    if (!datables.TryGetValue(t.DatableKey, out PickerNode group))
                    {
                        group = new PickerNode
                        {
                            IsGroup = true,
                            GroupKind = PickerGroupKind.Datable,
                            Label = !string.IsNullOrEmpty(t.CharacterName) ? t.CharacterName : t.Label,
                            Section = PickerSection.Met,
                            Distance = float.MaxValue,
                            Members = new List<KnownObjectTarget>(),
                        };
                        datables[t.DatableKey] = group;
                        view.Add(group);
                    }
                    AccumulateGroupMember(group, t);
                }
                else
                {
                    unmet.Add(t);
                }
            }
            view.AddRange(DedupeLeavesByName(unmet));
            // The group count the player hears must match what drilling in reveals (deduped by
            // name), or "Sofa, 5 objects" leads to a single-object menu. Recount on distinct labels.
            foreach (PickerNode group in datables.Values)
                group.ChildCount = DistinctLabelCount(group.Members);
            return view;
        }

        // Number of distinct spoken labels among a group's members (the count after same-name
        // dedupe), so a group announces the entry count the player will actually see on drill-in.
        private static int DistinctLabelCount(List<KnownObjectTarget> members)
        {
            if (members == null || members.Count == 0)
                return 0;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < members.Count; i++)
            {
                KnownObjectTarget t = members[i];
                names.Add(string.IsNullOrWhiteSpace(t.Label) ? "\0" : t.Label.Trim());
            }
            return names.Count;
        }

        // L3 OBJECTS: the chosen datable's found objects IN the chosen room, as routable leaves,
        // deduped so multiple same-named interactables (e.g. a sofa's couch + cushions + pillows,
        // all "Sofa") read as ONE entry routing to the nearest instance.
        private List<PickerNode> BuildObjectNodes(List<KnownObjectTarget> filtered)
        {
            List<KnownObjectTarget> matched = new List<KnownObjectTarget>();
            for (int i = 0; i < filtered.Count; i++)
            {
                KnownObjectTarget t = filtered[i];
                if (IsInChosenDatable(t) && SameChosenRoom(t))
                    matched.Add(t);
            }
            return DedupeLeavesByName(matched);
        }

        // Collapse same-named targets to one leaf each, keeping the nearest instance, preserving
        // first-seen order. The dedupe key is the spoken label (case-insensitive) — that's exactly
        // what the player hears, so two leaves the player can't tell apart by ear become one.
        private static List<PickerNode> DedupeLeavesByName(List<KnownObjectTarget> targets)
        {
            List<PickerNode> view = new List<PickerNode>();
            Dictionary<string, int> indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Count; i++)
            {
                KnownObjectTarget t = targets[i];
                string key = string.IsNullOrWhiteSpace(t.Label) ? "\0" : t.Label.Trim();
                if (indexByName.TryGetValue(key, out int existing))
                {
                    // Keep the nearest same-named instance (mirror the floor-aware preference).
                    KnownObjectTarget kept = view[existing].Target;
                    bool better = t.IsOnPlayerFloor && !kept.IsOnPlayerFloor;
                    if (better || (t.IsOnPlayerFloor == kept.IsOnPlayerFloor && t.Distance < kept.Distance))
                        view[existing] = LeafNode(t);
                    continue;
                }
                indexByName[key] = view.Count;
                view.Add(LeafNode(t));
            }
            return view;
        }

        private bool IsInChosenDatable(KnownObjectTarget t)
        {
            return t.Section == PickerSection.Met &&
                !string.IsNullOrEmpty(_pickerDatableKey) &&
                string.Equals(t.DatableKey, _pickerDatableKey, StringComparison.OrdinalIgnoreCase);
        }

        private bool SameChosenRoom(KnownObjectTarget t)
        {
            return string.IsNullOrEmpty(_pickerRoomZone)
                ? string.IsNullOrWhiteSpace(t.Zone)
                : string.Equals(t.Zone, _pickerRoomZone, StringComparison.OrdinalIgnoreCase);
        }

        private static PickerNode LeafNode(KnownObjectTarget t)
        {
            return new PickerNode
            {
                IsGroup = false,
                Label = t.Label,
                Distance = t.Distance,
                IsOnPlayerFloor = t.IsOnPlayerFloor,
                FloorLabel = t.FloorLabel,
                Section = t.Section,
                Target = t,
            };
        }

        // Add a member to a group, keeping the group's nearest-member distance/floor for sort +
        // announce, and incrementing the child count.
        private static void AccumulateGroupMember(PickerNode group, KnownObjectTarget t)
        {
            group.Members.Add(t);
            group.ChildCount = group.Members.Count;
            // Nearest member drives the group's sort key: a same-floor near member beats a
            // cross-floor far one (mirrors CompareFloorAwareDistance's preference).
            bool better = t.IsOnPlayerFloor && !group.IsOnPlayerFloor;
            if (group.ChildCount == 1 || better ||
                (t.IsOnPlayerFloor == group.IsOnPlayerFloor && t.Distance < group.Distance))
            {
                group.Distance = t.Distance;
                group.IsOnPlayerFloor = t.IsOnPlayerFloor;
                group.FloorLabel = t.FloorLabel;
            }
        }

        private int CompareKnownObjectForView(PickerNode a, PickerNode b)
        {
            // Met before Encountered, always — keeps the inline section headers contiguous.
            // (Only the top level mixes sections; room/object levels are all-Met.)
            if (a.Section != b.Section)
                return a.Section == PickerSection.Met ? -1 : 1;

            if (_pickerSortMode == PickerSortMode.Alphabetical)
                return string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase);

            return CompareFloorAwareDistance(a.IsOnPlayerFloor, a.Distance, b.IsOnPlayerFloor, b.Distance);
        }

        // Orders two targets by (same-as-player-floor first, then ascending XZ distance).
        // Returns <0 when 'a' should sort before 'b'.
        private static int CompareFloorAwareDistance(bool aOnPlayerFloor, float aDistance, bool bOnPlayerFloor, float bDistance)
        {
            if (aOnPlayerFloor != bOnPlayerFloor)
                return aOnPlayerFloor ? -1 : 1;
            return aDistance.CompareTo(bDistance);
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

        // An object is "encountered" (and thus a valid picker target) if the player has met
        // its datable, normally interacted with it, or examined it. The first two are persisted
        // by the game (GetDateStatus / ObjectSaveData.hasNormalInteracted) and survive a reload,
        // so they form the durable starting list.
        //
        // KNOWN LIMITATION: the game does NOT persist that an object was examined. ObjectSaveData
        // stores only activeSelf/activatedAnimation/isClean/hasNormalInteracted. (The save's
        // boxExamenDictionary is NOT an examine history — it is only the moving-box "Boxing Day"
        // achievement tally, keyed by a running counter, and covers no other objects.) So examine
        // evidence lives solely in our session-only _examinedObjectKeys set, and examine-only
        // entries DROP from the picker after a save/reload. Accepted for now: examine is an extra
        // encounter signal on top of the persisted set, and the player re-examines objects over a
        // play session. Revisit with a mod-side persisted examine history if this proves limiting.
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

        // True only when the player has examined THIS interactable during the session.
        // Examine evidence is session-only: the game persists no general "examined" flag
        // (see IsEncounteredKnownObject), so the only source is _examinedObjectKeys, which
        // RememberExaminedObject populates with the owning interactable's identity keys at
        // examine time (ObjectExamine.ShowExamine postfix).
        //
        // The previous implementation also (1) walked GetComponentInParent<ObjectExamine>,
        // attributing a shared parent's examine to every child interactable, and (2) matched
        // the achievement-only box counter (GetBoxExamenData) by InkNode. Both leaked the
        // examine onto neighbouring objects — InkNode is a SHARED ink content node, and the
        // box counter is just the moving-box "Boxing Day" achievement tally, not a per-object
        // examine record. Both paths removed; identity-key match is the sole, correct signal.
        private static bool IsExaminedInteractable(InteractableObj interactable)
        {
            if (interactable == null)
                return false;

            return HasRememberedExaminedObjectKey(interactable.Id) ||
                HasRememberedExaminedObjectKey(interactable.name) ||
                HasRememberedExaminedObjectKey(interactable.InternalName()) ||
                HasRememberedExaminedObjectKey(interactable.inkFileName);
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
            LogCapsuleDimensionsOnce();
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
            float radius = GetInteractableApproachRadius(target);
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

        // APPROACH radius (metres) = the disc the planner uses to gather candidate goal cells.
        // This is the object's OWN InteractionRadius, passed through unchanged — the game gates
        // interaction on `Distance(camera, ClosestPointOnBounds) < InteractionRadius` + a forward
        // raycast, and the planner now filters those candidates by mandatory line-of-sight and
        // picks the fewest-legs / closest cell, so there's no reason to cap or override the radius
        // here. (The former Skylar-gift 1.25m cap was a workaround for the OLD arrival rule that
        // stopped anywhere in the radius — removed now that arrival is the goal cell; re-add if a
        // specific target needs it.) Doors don't use this disc at all: their goal cells come from
        // the bake's operable_from_cells. See [[project-navigation-door-operability-cells]].
        private static float GetInteractableApproachRadius(InteractableObj interactable)
        {
            if (interactable == null)
                return 7.5f;

            return interactable.InteractionRadius > 0f
                ? interactable.InteractionRadius
                : 7.5f;
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
            _facingSince = 0f;
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

        private static bool ShouldLogNavigationDebugSnapshot(string snapshot, string lastSnapshot)
        {
            if (string.IsNullOrEmpty(snapshot) ||
                string.Equals(snapshot, lastSnapshot, StringComparison.Ordinal))
            {
                return false;
            }

            // A coverage sweep is a diagnostic run: capture every nav snapshot (wall-slide
            // escape fires, loop detection, blocked reasons) for its whole duration so the
            // sweep can report on its own follower logic without the manual DebugMode toggle.
            return Main.DebugMode || SimpleNavCoverageSweep.IsActive ||
                IsForcedNavigationDiagnosticSnapshot(snapshot);
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

        // High-precision vector format for the LOS parity probe: the offline validator replays the
        // EXACT logged ray, so origin/direction need enough precision that the replayed ray doesn't
        // drift off a thin occluder. Comma-free, paren-wrapped so it's one whitespace-delimited
        // token the log parser can split cleanly.
        private static string FormatVector3Precise(Vector3 value)
        {
            return "(" +
                value.x.ToString("0.000000", CultureInfo.InvariantCulture) + "," +
                value.y.ToString("0.000000", CultureInfo.InvariantCulture) + "," +
                value.z.ToString("0.000000", CultureInfo.InvariantCulture) + ")";
        }

        // Full hierarchy path of a transform (root/.../leaf), so the offline parity validator can
        // match the hit collider against the export's collider Path field.
        private static string GetTransformPath(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new System.Text.StringBuilder(t.name);
            Transform p = t.parent;
            while (p != null)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
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
                ScreenReader.SayCoalesced(summary);
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
            ScreenReader.SayCoalesced(Loc.Get("room_announcement", roomName));
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

            // Only announce objects the player can actually interact with. activeObject is set
            // from the targeting raycast even for out-of-range hits, but the game only shows the
            // interaction prompt (UIon) once the player is in range — IsPlayerInRange is that gate.
            // Announcing only in-range objects means hearing an object's name tells the player they
            // can interact with it. Reset the last-id when out of range so re-entering re-announces.
            if (!Singleton<InteractableManager>.Instance.IsPlayerInRange)
            {
                _lastInteractableId = null;
                return;
            }

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
            ScreenReader.SayCoalesced(Loc.Get("nearby_announcement_without_prompt", name));
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
            ScreenReader.SayCoalesced(Loc.Get("dateviators_state", status, charges));
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
            ScreenReader.Say(announcement);
        }

        // PageUp/PageDown section stepper for the SPECS / Rumors / DateADex detail screens. The
        // full-page read on open is unchanged; this lets the player re-hear one section at a time.
        // Sections come from the same builders that produce the spoken page (captured as a side
        // effect), so the stepper and the page never disagree. Reset when the active screen or its
        // content changes (keyed). Polled each frame; no-op unless one of the three screens is up.
        // Edge-detect PageUp/PageDown once per frame and latch the press until the throttled stepper consumes it.
        // Called from Update() every frame (outside the 0.1s poll gate) because GetAsyncKeyState edge-detection only
        // catches a press if it's sampled while the key is held — a tap that starts and ends between two polls would
        // otherwise be lost, which read as "PageUp/PageDown does nothing" on every stepper screen.
        private void PollSectionStepperKeys()
        {
            if (WasChoiceKeyPressed(KeyCode.PageDown, VkPageDown, ref _pageDownWasDown))
                _pageDownPending = true;
            if (WasChoiceKeyPressed(KeyCode.PageUp, VkPageUp, ref _pageUpWasDown))
                _pageUpPending = true;
        }

        private void HandleSectionStepperInput()
        {
            List<string> sections = ResolveActiveSectionStepperSections(out string key);

            if (sections == null || sections.Count == 0)
            {
                // No stepper-eligible screen (or no content yet): drop state so a fresh open starts
                // at the top, and discard any latched press so it can't fire on the next eligible screen.
                _sectionStepperSections = null;
                _sectionStepperIndex = -1;
                _sectionStepperKey = null;
                _pageUpPending = false;
                _pageDownPending = false;
                return;
            }

            // Rebuild on screen/content change so the index always maps to what's on screen.
            if (!string.Equals(key, _sectionStepperKey, StringComparison.Ordinal))
            {
                _sectionStepperKey = key;
                _sectionStepperSections = sections;
                _sectionStepperIndex = -1; // first PageDown reads section 0
            }
            else
            {
                _sectionStepperSections = sections; // refresh contents (values may have updated)
            }

            bool pageDown = _pageDownPending;
            bool pageUp = _pageUpPending;
            _pageDownPending = false;
            _pageUpPending = false;
            if (!pageDown && !pageUp)
                return;

            int count = _sectionStepperSections.Count;
            if (pageDown)
                _sectionStepperIndex = Mathf.Min(count - 1, _sectionStepperIndex + 1);
            else // pageUp
                _sectionStepperIndex = Mathf.Max(0, _sectionStepperIndex < 0 ? 0 : _sectionStepperIndex - 1);

            string section = _sectionStepperSections[Mathf.Clamp(_sectionStepperIndex, 0, count - 1)];
            if (!string.IsNullOrWhiteSpace(section))
            {
                // "3 of 7. <text>" so the player knows where they are in the list.
                ScreenReader.Say(Loc.Get("section_stepper_item",
                    _sectionStepperIndex + 1, count, section), interrupt: true);
            }
        }

        // The section list + a change-detection key for whichever stepper-eligible detail screen is
        // currently up (SPECS page, an open Rumor detail, or an open DateADex entry detail). Null when
        // none is active. The key changes when the screen or its content changes so the stepper resets.
        private List<string> ResolveActiveSectionStepperSections(out string key)
        {
            key = null;

            // SPECS profile / glossary page.
            if (SpecStatMain.Instance != null && SpecStatMain.Instance.visible
                && !ShouldSuppressSpecsAnnouncements()
                && _lastSpecsSections != null && _lastSpecsSections.Count > 0)
            {
                key = "specs:" + (IsSpecsGlossaryPage() ? "glossary" : "stats")
                    + ":" + string.Join("|", _lastSpecsSections.ToArray()).GetHashCode();
                return _lastSpecsSections;
            }

            // DateADex entry detail.
            if (DateADex.Instance != null && DateADex.Instance.DateADexWindow != null
                && DateADex.Instance.DateADexWindow.activeInHierarchy
                && _lastDateADexDetailSections != null && _lastDateADexDetailSections.Count > 0)
            {
                key = "dateadex:" + string.Join("|", _lastDateADexDetailSections.ToArray()).GetHashCode();
                return _lastDateADexDetailSections;
            }

            // Rumors entry detail.
            if (Roomers.Instance != null && Roomers.Instance.RoomersWindow != null
                && Roomers.Instance.RoomersWindow.activeInHierarchy
                && _lastRoomersDetailSections != null && _lastRoomersDetailSections.Count > 0)
            {
                key = "rumors:" + string.Join("|", _lastRoomersDetailSections.ToArray()).GetHashCode();
                return _lastRoomersDetailSections;
            }

            return null;
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

            if (TryBuildCardPoseAnnouncement(out announcement) ||
                TryBuildCurrentDialogueAnnouncement(out announcement) ||
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
            // Suppress the raw focus announcement WHENEVER the SPECS screen is visible and the focused
            // object is part of it — not just inside the post-announce timing window. The screen reader
            // (AnnounceSpecsDetailIfNeeded, which runs earlier each frame) reads the full profile/glossary
            // including the focused block, so the focus echo is redundant. Gating on the timing window
            // alone raced: on the frame SPECS opens, the focus is already set but the screen content
            // (Active_Stat_Blocks) may not be built yet, so the screen-read produced nothing and never
            // set the window — letting the focus read fire first ("reads the current focus instead of
            // the screen"). Tying suppression to "SPECS visible + focus is a SPECS child" is race-free.
            return selectedObject != null &&
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

            if (TryBuildControlsItemSelectionAnnouncement(selectedObject, out specialAnnouncement))
            {
                branch = "controls_item";
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
                {
                    choiceText = DecorateChoiceTextWithLockState(selectedObject, GetActiveChatChoices(), choiceText);
                    return Loc.Get("choice_announcement", choiceIndex, choiceCount, choiceText);
                }
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
                {
                    dialogueChoiceText = DecorateChoiceTextWithLockState(selectedObject, GetActiveDialogueChoices(), dialogueChoiceText);
                    return Loc.Get("choice_announcement", choiceIndex, choiceCount, dialogueChoiceText);
                }
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

            // Icon-only control (no readable TMP label) — read it by GameObject name rather than going silent. This is
            // the general path for every labelless button/icon (phone home-screen launchers, collectable icons, app
            // tabs, etc.), so individual screens don't each need a bespoke handler. Resolve the name from the nearest
            // named selectable in the parent chain (the focused object is often an unnamed "Icon"/"Image" child), and
            // clean it up: strip a trailing "Button" and normalize identifier casing/separators.
            branch = "generic_name";
            return BuildIconNameAnnouncement(selectedObject);
        }

        // Spoken label for a control that has no text: the cleaned GameObject name of the nearest meaningfully-named
        // selectable (or the object itself). Strips a trailing "Button"/"Icon" so "Thiscord Button" -> "Thiscord" and
        // normalizes casing/underscores. Returns null only if nothing usable can be derived.
        private static string BuildIconNameAnnouncement(GameObject selectedObject)
        {
            if (selectedObject == null)
                return null;

            // Climb to the owning Selectable so a focused unnamed sub-object ("Icon", "Image", "Highlight") reports
            // the button's name instead. Falls back to the object itself if it isn't inside a Selectable.
            Selectable selectable = selectedObject.GetComponentInParent<Selectable>();
            string rawName = selectable != null ? selectable.gameObject.name : selectedObject.name;

            rawName = StripTrailingWord(rawName, "Button");
            rawName = StripTrailingWord(rawName, "Icon");

            string label = NormalizeIdentifierName(rawName);
            return string.IsNullOrEmpty(label) ? null : label;
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

        // The settings Controls tab is the Rewired-style keybind list (hierarchy ...> Item > ItemArea >
        // KeybindKeyboard<Action> > ... > MenuInternal_Keybind...). Each row is a container named "Item" holding:
        //   - a TMP named "Item"               -> the action name ("Move Forward")
        //   - a TMP named "SelectedOptionText" -> the current binding ("W"; empty until a frame after focus)
        //   - "Reassign" and "Clear" buttons   -> the focused control is one of these (a ReassignButton/ClearButton
        //                                          with its own "Reassign"/"Clear" TMP child)
        // The default reader saw only the focused button's "Reassign" TMP and read "Reassign" for every row. Resolve
        // the row container, then speak "<action>, <binding>, <button>" so the player hears what they're rebinding,
        // the current key, and which button is focused. (Verified from a live focus dump of this menu.)
        private static bool TryBuildControlsItemSelectionAnnouncement(GameObject selectedObject, out string announcement)
        {
            announcement = null;

            if (selectedObject == null || selectedObject.transform == null)
                return false;

            // Find the row container named "Item" by climbing the parent chain (the focused button sits a couple of
            // levels below it). Bounded climb so this stays cheap and can't run away on unrelated selections.
            Transform row = null;
            Transform t = selectedObject.transform;
            for (int i = 0; i < 6 && t != null; i++)
            {
                if (string.Equals(t.gameObject.name, "Item", StringComparison.Ordinal))
                {
                    row = t;
                    break;
                }
                t = t.parent;
            }
            if (row == null)
                return false;

            // Pull the action name and current binding from the named TMPs in the row.
            string action = null;
            string binding = null;
            foreach (TMP_Text text in row.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text == null)
                    continue;
                string name = text.gameObject.name;
                if (action == null && string.Equals(name, "Item", StringComparison.Ordinal))
                    action = NormalizeText(text.text);
                else if (binding == null && string.Equals(name, "SelectedOptionText", StringComparison.Ordinal))
                    binding = NormalizeText(text.text);
            }

            if (string.IsNullOrEmpty(action))
                return false;

            // Which button is focused (Reassign / Clear); read its own label so the player knows the action.
            string button = NormalizeText(ExtractTextFromObject(selectedObject));

            var parts = new List<string>();
            parts.Add(action);
            parts.Add(string.IsNullOrEmpty(binding) ? Loc.Get("controls_unbound") : binding);
            if (!string.IsNullOrEmpty(button))
                parts.Add(button);

            announcement = string.Join(", ", parts.ToArray());
            return true;
        }

        private static bool TryBuildSettingsSelectionAnnouncement(GameObject selectedObject, out string announcement)
        {
            announcement = null;

            if (selectedObject == null)
                return false;

            // Resolve the SettingsMenu from the FOCUSED object's parent chain, not from CanvasUIManager._activeMenu.
            // The settings panel is the same singleton MenuComponent whether it's reached from the main menu or from
            // the in-game phone, but only the main-menu path makes it the _activeMenu: opening settings from the phone
            // shows the panel while _activeMenu stays the Phone/Pause menu, so the old _activeMenu.GetComponent
            // <SettingsMenu>() check returned null and no option titles were spoken. Every settings control (selector,
            // slider, apply button) lives UNDER the SettingsMenu transform, so climbing from the selection finds it in
            // both cases. Fall back to the active menu for any selection that isn't itself under the panel.
            SettingsMenu settingsMenu = selectedObject.GetComponentInParent<SettingsMenu>();
            if (settingsMenu == null && Singleton<CanvasUIManager>.Instance != null && Singleton<CanvasUIManager>.Instance._activeMenu != null)
                settingsMenu = Singleton<CanvasUIManager>.Instance._activeMenu.GetComponent<SettingsMenu>();

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

        // Trim a trailing whole word (case-insensitive) from a raw identifier, e.g. "Thiscord Button" -> "Thiscord",
        // "HomeButton" -> "Home". Leaves the string unchanged if removing the word would empty it.
        private static string StripTrailingWord(string value, string word)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(word))
                return value;

            string trimmed = value.TrimEnd();
            if (trimmed.Length > word.Length &&
                trimmed.EndsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - word.Length).TrimEnd();
                return string.IsNullOrEmpty(trimmed) ? value : trimmed;
            }

            return value;
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

            // A collectable grid icon (CollectableView): read its name + description/hint. The screen-level
            // collectableName/collectableDesc text only updates when a collectable is INSPECTED (clicked), so
            // navigating the grid wouldn't read anything from those; read the focused view's own data instead.
            // GetName/GetDescription already encode the locked state ("???" + the hint for locked, real name +
            // description for unlocked), so no unrevealed text leaks. (Without this, it would fall through to the
            // generic name fallback and read the raw view name instead of the collectable's actual name/description.)
            CollectableView collectableView = selectedObject.GetComponentInParent<CollectableView>();
            if (collectableView != null)
            {
                announcement = BuildCollectableViewAnnouncement(collectableView);
                return !string.IsNullOrEmpty(announcement);
            }

            DexEntryButton entryButton = selectedObject.GetComponentInParent<DexEntryButton>();
            if (entryButton != null)
            {
                announcement = ExtractTextFromObject(entryButton.gameObject);
                return true;
            }

            // Any other button on the DateADex screen falls through to the generic icon-name fallback in
            // BuildSelectionAnnouncement, which reads its actual name (so a real Back button still says "Back", but
            // other icon-only buttons read as themselves instead of being blanket-labeled "Back").
            return false;
        }

        // Spoken description of a focused collectable grid icon. Unlocked: "<name>. <description>". Locked: the
        // game returns "???"/"????" for the name and a hint for the description, so announce it as a locked
        // collectable plus that hint — never the real (unrevealed) name or description.
        private static string BuildCollectableViewAnnouncement(CollectableView view)
        {
            if (view == null)
                return null;

            string name = NormalizeText(view.GetName());
            string description = NormalizeText(view.GetDescription());
            bool locked = string.IsNullOrEmpty(name) || name == "???" || name == "????";

            if (locked)
            {
                return string.IsNullOrEmpty(description)
                    ? Loc.Get("dateadex_collectable_locked_plain")
                    : Loc.Get("dateadex_collectable_locked", description);
            }

            if (string.IsNullOrEmpty(description))
                return name;

            return Loc.Get("dateadex_collectable_unlocked", name, description);
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

            // Tip sections: group each tip's name + info into ONE section (meatier step granularity),
            // rather than separate name/info parts, so PageUp/PageDown moves tip-by-tip.
            if (entry.skylarTipIsFound && !string.IsNullOrWhiteSpace(entry.skylar))
            {
                AddAnnouncementPart(parts, JoinAnnouncementParts(new List<string>
                {
                    Loc.Get("roomers_character", "Skylar"),
                    NormalizeText(entry.skylar),
                }));
            }
            else if (entry.tips != null)
            {
                for (int i = 0; i < entry.tips.Count; i++)
                {
                    Save.RoomersTipStruct tip = entry.tips[i];
                    if (tip == null || !tip.isFound)
                        continue;

                    AddAnnouncementPart(parts, JoinAnnouncementParts(new List<string>
                    {
                        NormalizeText(tip.tipNameAfterValidation),
                        NormalizeText(tip.tipInfoAfterValidation),
                    }));
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

                // Per-focus selection: announce only the focused contact/item. The app name
                // ("Workspace"/"Thiscord") is already read once when the app opens, so repeating it
                // on every navigation step (e.g. "Workspace. David Most") is noise.
                announcement = selectedName;
                return !string.IsNullOrEmpty(announcement);
            }

            if (!IsWithinChatPanel(selectedObject, activeChatType, activePanelNameObject, secondaryPanelObject))
                return false;

            announcement = NormalizeText(ExtractTextFromObject(activePanelNameObject));
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

            // Sections for the PageUp/PageDown stepper — the live on-screen rumor detail.
            _lastRoomersDetailSections = parts.Count > 0 ? new List<string>(parts) : null;

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
            // The description is scrollable and only meant to be read with the mouse wheel/scrollbar (not keyboard-
            // navigable). The live page read mirrors the screen (the lines currently in the viewport), but the
            // PageUp/PageDown stepper reads the WHOLE scrollable description so a keyboard-only player hears all of
            // it. This grabs every UNLOCKED line: Desc.text is CharDYK, which the game builds by prepending only the
            // dex comments the player has unlocked (DateADex.cs), so locked/not-yet-revealed text is never in the
            // string and is excluded for free. Capture both forms.
            string description = isEntryVisible
                ? GetVisibleDateADexDescription(DateADex.Instance.Desc, DateADex.Instance.DescScroll)
                : null;
            string fullDescription = isEntryVisible
                ? GetActiveDateADexText(DateADex.Instance.Desc)
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

            // When the live visible-clip read came up empty, the override block above already set `description` to
            // the full CharDYK; mirror that into fullDescription so the stepper never reads LESS than the page.
            if (string.IsNullOrEmpty(fullDescription))
                fullDescription = description;

            // Build the section list twice from one helper: the live read uses the visible-clip description, the
            // PageUp/PageDown stepper uses the full unlocked description. Everything else is identical.
            List<string> BuildSections(string desc)
            {
                var p = new List<string>();
                AddAnnouncementPart(p, item);
                AddAnnouncementPart(p, desc);
                AddAnnouncementPart(p, BuildLabeledValue("dateadex_voice_actor", voiceActor));
                AddAnnouncementPart(p, BuildLabeledValue("dateadex_likes", likes));
                AddAnnouncementPart(p, BuildLabeledValue("dateadex_dislikes", dislikes));
                AddAnnouncementPart(p, BuildLabeledValue("dateadex_pronouns", pronouns));
                AddAnnouncementPart(p, listSummary);
                AddAnnouncementPart(p, BuildLabeledValue("dateadex_collectables", collectables));
                AddAnnouncementPart(p, recipe);
                return p;
            }

            var parts = BuildSections(description);

            // Each part is already a meaty, self-contained section ("Likes: ...", the description,
            // etc.) — exactly the granularity the PageUp/PageDown stepper wants, so expose it. The stepper gets the
            // FULL unlocked description so a keyboard-only player can hear all of it (the page read stays on screen).
            List<string> stepperParts = BuildSections(fullDescription);
            _lastDateADexDetailSections = stepperParts.Count > 0 ? new List<string>(stepperParts) : null;

            announcement = JoinAnnouncementParts(parts);
            return !string.IsNullOrEmpty(announcement);
        }

        // Most-recent section lists captured by the detail builders, consumed by the section stepper
        // (PageUp/PageDown). Captured as a side effect of the normal full-read build so the stepper
        // and the spoken page never disagree about what's on screen.
        private static List<string> _lastDateADexDetailSections;
        private static List<string> _lastRoomersDetailSections;

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

            // Each stat block is one section for the PageUp/PageDown stepper.
            _lastSpecsSections = parts.Count > 0 ? new List<string>(parts) : null;
            return hasActiveBlock ? JoinAnnouncementParts(parts) : null;
        }

        // Most-recent SPECS section list (one per stat/glossary block), for the section stepper.
        private static List<string> _lastSpecsSections;

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

            _lastSpecsSections = parts.Count > 0 ? new List<string>(parts) : null;
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

        // Ctrl+F1 repeat for the fullscreen datable pose card. While either splash screen is open,
        // serve the description we spoke for it (HandleCardPoseAnnouncement stores it), so repeat
        // works even after other speech has overwritten the generic last-spoken buffer. Once both
        // cards close we drop the cached text so it can't leak into a later repeat press.
        private static bool TryBuildCardPoseAnnouncement(out string announcement)
        {
            announcement = null;

            bool cardOpen =
                (AwakenSplashScreen.Instance != null && AwakenSplashScreen.Instance.isOpen) ||
                (ResultSplashScreen.Instance != null && ResultSplashScreen.Instance.isOpen);

            if (!cardOpen)
            {
                _lastCardPoseDesc = null;
                return false;
            }

            if (string.IsNullOrWhiteSpace(_lastCardPoseDesc))
                return false;

            announcement = _lastCardPoseDesc;
            return true;
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
            if (choiceButton != null && !choiceButton.interactable && !string.IsNullOrEmpty(choiceText))
                choiceText = Loc.Get("choice_locked_suffix", choiceText);
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

        // Wrap a choice's spoken text with a "Locked" marker when its button is non-interactable.
        // The stat requirement itself is already baked into the button text by the game
        // (e.g. "[Charm 3/5] ..." for a failed check), so only the locked state is added here.
        private static string DecorateChoiceTextWithLockState(GameObject selectedObject, IList<Button> choices, string choiceText)
        {
            if (string.IsNullOrEmpty(choiceText) || selectedObject == null || choices == null)
                return choiceText;

            for (int i = 0; i < choices.Count; i++)
            {
                Button button = choices[i];
                if (button == null)
                    continue;

                if (selectedObject == button.gameObject || selectedObject.transform.IsChildOf(button.transform))
                {
                    if (!button.interactable)
                        return Loc.Get("choice_locked_suffix", choiceText);
                    break;
                }
            }

            return choiceText;
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
                // Keep locked (non-interactable) options in the list: the game leaves them
                // active and visible (only failed negative StatChecks are SetActive(false)),
                // so they must still be read and navigable. Activation is gated separately.
                if (button != null && button.gameObject.activeInHierarchy)
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
                // Keep locked (non-interactable) options so they are read and navigable;
                // activation is gated separately in ActivateChoice.
                if (option != null && option.gameObject.activeInHierarchy)
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
            if (choice == null)
                return;

            // Locked options (e.g. a failed stat check) stay focusable so they can be read,
            // but the game disables them. Announce why instead of silently doing nothing.
            if (!choice.interactable)
            {
                ScreenReader.Say(Loc.Get("choice_locked_activate"));
                return;
            }

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
            // The Dateviators state/charges are announced only on equip/unequip
            // (AnnounceDateviatorsStateIfNeeded); the phone summary doesn't repeat them.
            return Loc.Get("phone_menu_summary");
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
            // Object names that reach here are raw Unity GameObject names used as a last-resort
            // label (the game gave us no human-facing mainText). Strip the model-authoring noise
            // first so the player hears "Bathtub", not "SM Bathtub" or "SM Clock MODEL UPDATE":
            //   - "SM_" / "SK_" static- and skeletal-mesh prefixes
            //   - the "_MODEL_UPDATE" re-authoring marker (and its "MODEL_UPDATE2" variant)
            //   - Unity's duplicate-instance suffix " (13)"
            // This runs on the underscore-joined raw value, BEFORE NormalizeIdentifierName turns
            // "_" into spaces, so the token boundaries are still intact.
            value = StripModelAuthoringTokens(value);

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

        // Removes mesh-authoring decoration from a raw Unity object name. Operates on the
        // underscore-joined form (e.g. "SM_Clock_MODEL_UPDATE", "SM_Bush (43)") and leaves a
        // clean stem ("Clock", "Bush"). Returns the input unchanged when there is nothing to
        // strip, and never returns an empty/whitespace stem (falls back to the original).
        private static string StripModelAuthoringTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            string stripped = value;

            // Unity duplicate-instance suffix, e.g. "SM_Bush (43)".
            stripped = Regex.Replace(stripped, @"\s*\(\d+\)\s*$", "");

            // "_MODEL_UPDATE", "_MODEL_UPDATE2" re-authoring marker anywhere in the name.
            stripped = Regex.Replace(stripped, @"_MODEL_UPDATE\d*", "", RegexOptions.IgnoreCase);

            // Leading static-/skeletal-mesh prefix.
            stripped = Regex.Replace(stripped, @"^(?:SM|SK)_+", "", RegexOptions.IgnoreCase);

            stripped = stripped.Trim().Trim('_').Trim();

            return string.IsNullOrWhiteSpace(stripped) ? value : stripped;
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
            EnsureGlyphReflectionCache(flags);
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

            value = ResolveControlGlyphs(value);

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

        // Replaces TMP control-prompt sprite tags (e.g. <sprite="Keyboard" index=12>) with the
        // readable Rewired element name (e.g. "Space"), so the screen reader speaks the actual
        // button instead of dropping the glyph. Device-aware: keyboard map vs. controller map.
        // Runs before RichTextRegex strips all tags, so it must keep the regex cheap.
        private static string ResolveControlGlyphs(string value)
        {
            if (value.IndexOf("<sprite", StringComparison.OrdinalIgnoreCase) < 0)
                return value;

            object markupMap;
            Dictionary<int, string> reverseMap;
            try
            {
                EnsureReflectionCache();
                markupMap = GetActiveSpriteMarkupMap();
                reverseMap = GetReverseSpriteMap(markupMap);
            }
            catch (Exception)
            {
                reverseMap = null;
            }

            return SpriteTagRegex.Replace(value, match =>
            {
                string name = null;
                if (reverseMap != null && match.Groups["idx"].Success
                    && int.TryParse(match.Groups["idx"].Value, out int spriteId)
                    && reverseMap.TryGetValue(spriteId, out string resolved))
                {
                    name = resolved;
                }

                // Keep the sentence coherent ("Press button to start") when unresolved.
                return string.IsNullOrEmpty(name) ? " button " : " " + name + " ";
            });
        }

        private static void EnsureGlyphReflectionCache(BindingFlags flags)
        {
            if (_glyphReflectionResolved)
                return;

            _glyphReflectionResolved = true;

            Type serviceType = typeof(Team17.Services.IconTextMarkupService);
            _iconMarkupCurrentMapField = serviceType.GetField("_CurrentSpriteMarkupMap", flags);
            _iconMarkupKeyboardMapField = serviceType.GetField("_KeyboardSpriteControllerMarkupMap", flags);
            _iconMarkupControllerMapField = serviceType.GetField("_ControllerSpriteMarkupMap", flags);

            Type markupType = typeof(InputSpriteBindingMarkupObject);
            _spriteBindingPairsField = markupType.GetField("m_DeviceBindingSprites", flags);

            Type pairType = markupType.GetNestedType("RewiredIdSpritePair", BindingFlags.NonPublic | BindingFlags.Public);
            if (pairType != null)
            {
                _spriteBindingPairNameField = pairType.GetField("RewiredElementName", flags);
                _spriteBindingPairIdField = pairType.GetField("spriteId", flags);
            }
        }

        // Returns the InputSpriteBindingMarkupObject for the device that produced the current
        // input, mirroring IconTextMarkupService.GetTMPSpriteTag's keyboard/controller branch.
        private static object GetActiveSpriteMarkupMap()
        {
            object service = T17.Services.Services.IconTextMarkupService;
            if (service == null || _spriteBindingPairsField == null)
                return null;

            bool useController = T17.Services.Services.InputService != null
                && T17.Services.Services.InputService.IsLastActiveInputController();

            FieldInfo mapField = useController ? _iconMarkupControllerMapField : _iconMarkupKeyboardMapField;
            object map = mapField != null ? mapField.GetValue(service) : null;

            // Fall back to whatever the service last resolved if the device-specific map is null.
            if (map == null && _iconMarkupCurrentMapField != null)
                map = _iconMarkupCurrentMapField.GetValue(service);

            return map;
        }

        // Builds (and caches per markup object) a spriteId -> RewiredElementName reverse map from
        // the markup object's private m_DeviceBindingSprites array.
        private static Dictionary<int, string> GetReverseSpriteMap(object markupMap)
        {
            if (markupMap == null || _spriteBindingPairsField == null
                || _spriteBindingPairNameField == null || _spriteBindingPairIdField == null)
                return null;

            if (_spriteReverseMaps.TryGetValue(markupMap, out Dictionary<int, string> cached))
                return cached;

            var reverse = new Dictionary<int, string>();
            if (_spriteBindingPairsField.GetValue(markupMap) is Array pairs)
            {
                foreach (object pair in pairs)
                {
                    if (pair == null)
                        continue;

                    int id = (int)_spriteBindingPairIdField.GetValue(pair);
                    string name = _spriteBindingPairNameField.GetValue(pair) as string;
                    if (!string.IsNullOrEmpty(name) && !reverse.ContainsKey(id))
                        reverse[id] = name;
                }
            }

            _spriteReverseMaps[markupMap] = reverse;
            return reverse;
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
