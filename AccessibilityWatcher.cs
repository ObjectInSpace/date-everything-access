using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using TMPro;
using T17.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DateEverythingAccess
{
    internal sealed class AccessibilityWatcher : MonoBehaviour
    {
        private enum SpecsAnnouncementMode
        {
            None,
            Stats,
            Tooltip,
            Glossary
        }

        private const float PopupSelectionSuppressionSeconds = 0.75f;
        private const float UIDialogSelectionSuppressionSeconds = 0.75f;
        private const float SpecsSelectionSuppressionSeconds = 0.75f;
        private const float CreditsSelectionSuppressionSeconds = 0.75f;
        private const float SpecsInitialAnnouncementGraceSeconds = 1f;
        private const float SpecsTutorialDialogStartTimeoutSeconds = 3f;
        private const float SpecsTutorialDialogTransitionGraceSeconds = 0.5f;

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
        private static Type _engagementType;
        private static Type _loadingFactsType;
        private static int _repeatLastSpeechRequested;
        private static float _suppressInitialSpecsAnnouncementsUntil;
        private static bool _awaitingSpecsTutorialDialogs;

        private string _lastAnnouncedSelection;
        private int _lastSelectedObjectId;
        private string _lastAnnouncedDialogue;
        private string _lastScreenSummary;
        private string _lastRoomName;
        private string _lastInteractableId;
        private string _lastRoomersDetail;
        private string _lastDateADexDetail;
        private string _pendingDateADexDetail;
        private string _lastChatAppDetail;
        private string _pendingChatAppDetail;
        private string _lastActiveChatKey;
        private string _lastMusicDetail;
        private string _lastArtDetail;
        private string _lastCollectableDetail;
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
        private float _suppressDateADexSelectionUntil;
        private float _suppressPopupSelectionUntil;
        private float _suppressUIDialogSelectionUntil;
        private float _suppressSpecsSelectionUntil;
        private float _suppressCreditsSelectionUntil;
        private float _pendingDateADexDetailSince;
        private float _pendingChatAppDetailSince;
        private float _suppressChatSelectionUntil;
        private float _suppressPendingSpecsTutorialUntil;
        private SpecsAnnouncementMode _lastSpecsAnnouncementMode;

        internal static void EnsureCreated()
        {
            if (FindObjectOfType<AccessibilityWatcher>() != null)
                return;

            var watcherObject = new GameObject("DateEverythingAccessWatcher");
            watcherObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(watcherObject);
            watcherObject.AddComponent<AccessibilityWatcher>();
            Main.Log.LogInfo("Accessibility watcher created");
        }

        internal static void RequestRepeatLastSpeech()
        {
            Interlocked.Exchange(ref _repeatLastSpeechRequested, 1);
        }

        private void Update()
        {
            if (Main.IsShuttingDown)
                return;

            HandleRepeatLastSpeechRequest();

            bool isSettingsMenuOpen = ModConfig.IsMenuOpen;
            if (isSettingsMenuOpen)
            {
                ModConfig.Update();
            }
            else
            {
                HandleDialogueChoiceKeyboardInput();
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
            if (!isSettingsMenuOpen)
            {
                AnnounceSelectionIfNeeded();
            }
            AnnouncePhoneAppContentIfNeeded();
            AnnounceResultScreenIfNeeded();
            AnnounceTimeChangeIfNeeded();
            AnnounceProgressionChangesIfNeeded();
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

        private void HandleDialogueChoiceKeyboardInput()
        {
            IList<Button> choices = GetActiveDialogueChoices();
            if (choices == null || choices.Count == 0)
                return;

            int currentIndex = GetCurrentDialogueChoiceIndex(choices);
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.LeftArrow))
            {
                int targetIndex = currentIndex >= 0 ? (currentIndex + choices.Count - 1) % choices.Count : choices.Count - 1;
                FocusChoice(choices[targetIndex]);
                return;
            }

            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.RightArrow))
            {
                int targetIndex = currentIndex >= 0 ? (currentIndex + 1) % choices.Count : 0;
                FocusChoice(choices[targetIndex]);
                return;
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space))
            {
                int targetIndex = currentIndex >= 0 ? currentIndex : 0;
                ActivateChoice(choices[targetIndex]);
            }
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

            if (objectId == _lastSelectedObjectId && announcement == _lastAnnouncedSelection)
            {
                TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, branch, announcement, "duplicate");
                return;
            }

            _lastSelectedObjectId = objectId;
            _lastAnnouncedSelection = announcement;
            TraceSelectionDebug(rawSelectedObject, selectedObject, selectionSource, branch, announcement, "spoken");
            ScreenReader.Say(announcement);
        }

        private void AnnounceRoomersDetailIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastRoomersDetail = null;
                return;
            }

            string announcement;
            if (!TryBuildRoomersDetailAnnouncement(out announcement))
            {
                _lastRoomersDetail = null;
                return;
            }

            if (announcement == _lastRoomersDetail)
                return;

            _lastRoomersDetail = announcement;
            ScreenReader.Say(announcement);
        }

        private void AnnounceDateADexDetailIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastDateADexDetail = null;
                _pendingDateADexDetail = null;
                return;
            }

            string announcement;
            if (!TryBuildDateADexDetailAnnouncement(out announcement))
            {
                _lastDateADexDetail = null;
                _pendingDateADexDetail = null;
                return;
            }

            if (announcement == _lastDateADexDetail)
            {
                _pendingDateADexDetail = null;
                return;
            }

            if (!string.Equals(announcement, _pendingDateADexDetail, StringComparison.Ordinal))
            {
                _pendingDateADexDetail = announcement;
                _pendingDateADexDetailSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _pendingDateADexDetailSince < 0.25f)
                return;

            _lastDateADexDetail = announcement;
            _pendingDateADexDetail = null;
            _suppressDateADexSelectionUntil = Time.unscaledTime + 0.5f;
            ScreenReader.Say(announcement);
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

            if (Time.unscaledTime < _suppressChatSelectionUntil)
                return IsChatSelectionObject(selectedObject);

            if (!TryBuildChatAppAnnouncement(out string pendingAnnouncement, out string activeChatKey) ||
                string.IsNullOrEmpty(pendingAnnouncement) ||
                pendingAnnouncement == _lastChatAppDetail ||
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

        private void AnnounceChatAppDetailIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastChatAppDetail = null;
                _pendingChatAppDetail = null;
                _lastActiveChatKey = null;
                return;
            }

            string announcement;
            string activeChatKey;
            if (!TryBuildChatAppAnnouncement(out announcement, out activeChatKey))
            {
                _lastChatAppDetail = null;
                _pendingChatAppDetail = null;
                _lastActiveChatKey = null;
                return;
            }

            bool activeChatChanged = !string.Equals(activeChatKey, _lastActiveChatKey, StringComparison.Ordinal);
            if (activeChatChanged)
            {
                _lastActiveChatKey = activeChatKey;
                _lastChatAppDetail = null;
            }

            if (announcement == _lastChatAppDetail)
            {
                _pendingChatAppDetail = null;
                return;
            }

            if (activeChatChanged || !string.Equals(announcement, _pendingChatAppDetail, StringComparison.Ordinal))
            {
                _pendingChatAppDetail = announcement;
                _pendingChatAppDetailSince = Time.unscaledTime;
                _suppressChatSelectionUntil = Time.unscaledTime + 0.5f;
                return;
            }

            if (Time.unscaledTime - _pendingChatAppDetailSince < 0.2f)
                return;

            _lastChatAppDetail = announcement;
            _pendingChatAppDetail = null;
            _suppressChatSelectionUntil = Time.unscaledTime + 0.5f;
            ScreenReader.Say(announcement, interrupt: false);
        }

        private void AnnounceMusicDetailIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastMusicDetail = null;
                return;
            }

            string announcement;
            if (!TryBuildMusicAnnouncement(out announcement))
            {
                _lastMusicDetail = null;
                return;
            }

            if (announcement == _lastMusicDetail)
                return;

            _lastMusicDetail = announcement;
            ScreenReader.Say(announcement, interrupt: false);
        }

        private void AnnounceArtDetailIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastArtDetail = null;
                return;
            }

            string announcement;
            if (!TryBuildArtAnnouncement(out announcement))
            {
                _lastArtDetail = null;
                return;
            }

            if (announcement == _lastArtDetail)
                return;

            _lastArtDetail = announcement;
            ScreenReader.Say(announcement, interrupt: false);
        }

        private void AnnounceCollectableDetailIfNeeded()
        {
            if (!ModConfig.ReadScreenText)
            {
                _lastCollectableDetail = null;
                return;
            }

            string announcement;
            if (!TryBuildCollectableAnnouncement(out announcement))
            {
                _lastCollectableDetail = null;
                return;
            }

            if (announcement == _lastCollectableDetail)
                return;

            _lastCollectableDetail = announcement;
            ScreenReader.Say(announcement);
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
            ScreenReader.Say(announcement);
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

            if (TryBuildUIDialogSelectionAnnouncement(selectedObject, out specialAnnouncement, out branch))
                return specialAnnouncement;

            if (TryBuildSpecsSelectionAnnouncement(selectedObject, out specialAnnouncement, out branch))
                return specialAnnouncement;

            if (TryBuildDateADexSelectionAnnouncement(selectedObject, out specialAnnouncement))
            {
                branch = "dateadex";
                return specialAnnouncement;
            }

            if (TryBuildChatSelectionAnnouncement(selectedObject, out specialAnnouncement))
            {
                branch = "chat";
                return specialAnnouncement;
            }

            if (TryBuildSaveSelectionAnnouncement(selectedObject, out specialAnnouncement, out branch))
                return specialAnnouncement;

            int choiceIndex;
            int choiceCount;
            if (TryGetDialogueChoiceAnnouncement(selectedObject, out choiceIndex, out choiceCount))
            {
                branch = "dialogue_choice";
                if (!ModConfig.ReadDialogueChoices)
                    return null;

                string choiceText = ExtractTextFromObject(selectedObject);
                if (!string.IsNullOrEmpty(choiceText))
                    return Loc.Get("choice_announcement", choiceIndex, choiceCount, choiceText);
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

            bool isEntryVisible = DateADex.Instance.IsInEntryScreen;
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
            if (string.IsNullOrEmpty(announcement))
                return false;
            return true;
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

            var parts = new List<string>();
            AddAnnouncementPart(parts, header);
            if (!string.Equals(secondary, name, StringComparison.Ordinal))
                AddAnnouncementPart(parts, secondary);
            AddAnnouncementPart(parts, transcript);
            AddAnnouncementPart(parts, BuildLabeledValue("chat_options", visibleChoices));

            announcement = JoinAnnouncementParts(parts);
            if (!string.IsNullOrEmpty(announcement))
                return true;

            return false;
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

            if (TutorialController.Instance == null)
                return false;

            EnsureReflectionCache();
            GameObject signpost = _tutorialSignpostField != null ? _tutorialSignpostField.GetValue(TutorialController.Instance) as GameObject : null;
            TMP_Text signpostText = _tutorialSignpostTextField != null ? _tutorialSignpostTextField.GetValue(TutorialController.Instance) as TMP_Text : null;
            if (signpost == null || !signpost.activeInHierarchy || signpostText == null)
                return false;

            string text = NormalizeText(signpostText.text);
            if (string.IsNullOrEmpty(text))
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

            if (TryBuildCollectableAnnouncement(out announcement) ||
                TryBuildRoomersDetailAnnouncement(out announcement) ||
                TryBuildDateADexDetailAnnouncement(out announcement) ||
                TryBuildMusicAnnouncement(out announcement) ||
                TryBuildArtAnnouncement(out announcement) ||
                TryBuildSpecsAnnouncement(out announcement, out SpecsAnnouncementMode _) ||
                TryBuildCreditsAnnouncement(out announcement))
            {
                return !string.IsNullOrEmpty(announcement);
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
            choiceIndex = 0;
            choiceCount = 0;

            var choices = GetActiveDialogueChoices();
            if (choices == null || choices.Count == 0)
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

        private static int GetCurrentDialogueChoiceIndex(IList<Button> choices)
        {
            GameObject selectedObject = GetCurrentSelectedObject();
            if (selectedObject == null)
                return -1;

            for (int i = 0; i < choices.Count; i++)
            {
                Button button = choices[i];
                if (button == null)
                    continue;

                if (selectedObject == button.gameObject || selectedObject.transform.IsChildOf(button.transform))
                    return i;
            }

            return -1;
        }

        private static void FocusChoice(Button choice)
        {
            if (choice == null)
                return;

            ControllerMenuUI.SetCurrentlySelected(choice.gameObject, ControllerMenuUI.Direction.Down, manualSelected: true, isViaPointer: true);
        }

        private static void ActivateChoice(Button choice)
        {
            if (choice == null || !choice.interactable)
                return;

            choice.onClick.Invoke();
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
                return displayName;
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
            string displayName = NormalizeText(interactable.mainText);
            if (!string.IsNullOrEmpty(displayName) &&
                !displayName.StartsWith("Default hover text for ", StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }

            displayName = NormalizeIdentifierName(interactable.name);
            if (!string.IsNullOrEmpty(displayName))
                return displayName;

            displayName = NormalizeIdentifierName(interactable.InternalName());
            return string.IsNullOrEmpty(displayName) ? Loc.Get("unknown_object") : displayName;
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
