# Date Everything! - Game API Documentation

## Overview

- Game: `Date Everything!`
- Engine: `Unity 2022.3.28.7056988`
- Runtime: `CLR 4.0.30319.42000`, mod target `net472`
- Architecture: `64-bit`
- Mod loader: `BepInEx 5.4.23.5`
- Primary input stack: `Rewired` with some `Unity InputSystem` layout detection

## Singleton Access Points

### Core game state

- `Singleton<GameController>.Instance`
  - Tracks `viewState`
  - Owns `talkingUI`, `HUD`, `DialogueEnter`, `DialogueExit`
  - Provides active datable and state transitions between house and dialogue

- `BetterPlayerControl.Instance`
  - Main gameplay input loop while roaming the house
  - Consumes interaction, Dateviators, crouch, phone toggle, and examine actions

### UI and menu state

- `Singleton<CanvasUIManager>.Instance`
  - Tracks `_activeMenu`
  - Opens and closes major menus through `SwitchMenu(...)` and `BackWithReturn()`
  - Uses menu history to support back navigation

- `Singleton<ControllerMenuUI>.Instance`
  - Tracks the currently selected control
  - Central API for programmatic focus changes through `SetCurrentlySelected(...)`
  - Important for accessibility because it enforces navigation rules and focus priority

- `TalkingUI.Instance`
  - Main dialogue UI controller
  - Holds the private `dialogBox` and `choicesButtons` fields
  - Builds dialogue choices and handles confirm/cancel logic

- `Singleton<PhoneManager>.Instance`
  - Owns phone open/app-open state
  - Exposes `IsPhoneMenuOpened()`, `IsPhoneAppOpened()`, `GetCurrentApp()`
  - Tracks `openedSubMenu`, `phoneOpened`, and phone return methods

### Feature-specific systems

- `Roomers.Instance`
  - Tracks `CurrentScreen`
  - Exposes `roomersScreenInfo` for title and room text

- `DateADex.Instance`
  - Main Date A Dex app controller
  - Public UI fields such as `Item` are easier to consume than deeper internal entry state

- `Singleton<Dateviators>.Instance`
  - Exposes `IsEquipped`, `IsEquippingOrEquipped`, `GetCurrentCharges()`
  - Central source for charge/equip announcements

- `Singleton<InteractableManager>.Instance`
  - Tracks `activeObject`
  - Central source for the currently targeted interactable

- `Singleton<CameraSpaces>.Instance`
  - `PlayerZone()` returns the current room zone
  - Zone `Name` is the cleanest room identifier found so far

- `Singleton<Save>.Instance`
  - Useful for resolving internal names to user-facing names through `TryGetNameByInternalName(...)`
  - Also stores settings/tutorial flags and progression state

## Input System

## Game Key Bindings

The game does not hardcode most player-facing keyboard keys in gameplay classes. It routes almost everything through `Rewired` actions and UI services. That matters because safe mod keys must be chosen against action usage, not just `KeyCode` searches.

### Rewired actions actively consumed by gameplay/UI

From `decompiled/RewiredConsts/Action.cs` and the main input classes:

- `Pause` (`5`)
  - Used by `CanvasUIManager.Update()`, `BetterPlayerControl.Update()`, `PauseScreen.Update()`
  - Opens/closes pause or phone flows depending on state

- `Interact` (`12`)
  - Used by `BetterPlayerControl.Update()`
  - Activates the current interactable

- `Awaken Dateable` (`13`)
  - Used by `BetterPlayerControl.Update()`
  - Drives Dateviators charging / awakening behavior

- `Examen` (`18`)
  - Used by `BetterPlayerControl.Update()`
  - Opens object examination

- `Crouch` (`33`)
  - Used by `BetterPlayerControl.Update()`

- `Toggle Dateviators` (`52`)
  - Used by `BetterPlayerControl.Update()`
  - Equip / dequip Dateviators and some phone-side toggles

- `Message Log` (`11`)
  - Used in dialogue state by `BetterPlayerControl.Update()`

- `Confirm` (`28`)
  - Used by `TalkingUI.ProcessInput()`
  - Used widely in `UIInputService`

- `Cancel` (`29`)
  - Used by `TalkingUI.ProcessInput()`
  - Used widely in `UIInputService`

- `UI Up` (`24`), `UI Down` (`25`), `UI Right` (`26`), `UI Left` (`27`)
  - Central UI navigation actions via `UIInputService`

- `Cycle List Up` (`37`), `Cycle List Down` (`38`)
  - Used by save/load and other list screens

- `UIMenuExtraAction` (`50`), `UIMenuExtraSecondAction` (`51`)
  - Used by `Roomers`, `SaveScreenManager`, `SpecStatMain`

- `PrimaryResponse` (`39`), `SecondaryResponse` (`40`), `TetriaryResponse` (`41`), `QuaternaryResponse` (`42`)
  - Reserved for direct dialogue response selection

### Hardcoded keys found in decompiled code

These are not normal gameplay bindings and should be treated as reserved anyway:

- `F2`
  - Used by `EditorTools`

- `F3`
  - Used by `ArtGalleryTool`

- `F8`
  - Used by `InkVariableTool`

### Keyboard layout handling

`BetterPlayerControl.Update()` checks `UnityEngine.InputSystem.Keyboard.current.keyboardLayout` and reloads Rewired maps for:

- `Default`
- `Azerty`
- `Dvorak`
- `Colemak`

That means hardcoding letter keys for core controls is riskier than function keys or explicit Rewired actions.

### Runtime mapping dump for exact physical bindings

- Exact physical keyboard keys and controller buttons are chosen from the live Rewired maps, so they can vary by active layout and connected controller.
- The mod now exposes a safe runtime inspection path: press `F9` to enable debug mode, and it will dump the current Rewired keyboard, mouse, and joystick maps to the BepInEx log.
- The dump reports controller type, controller name, map category, layout id, enabled state, action name and id, and the exact `elementIdentifierName` currently bound to that action.
- This is the authoritative way to answer questions like "what does Confirm map to on this controller right now?" without guessing from decompiled action ids alone.

## Safe Mod Keys

### Current mod bindings

- `F1`: help
- `Ctrl+F1`: repeat the most recently spoken mod line
- `Ctrl+F6`: track the current tutorial objective
- `Ctrl+Shift+F6`: open and cycle the current-room object list
- `Ctrl+Alt+F6`: toggle auto-walk to the tracked object
- `F9`: toggle debug
- `Ctrl+F9`: accessibility settings menu

### Why these are safe

- No gameplay consumer for `F1` was found in the decompiled game code.
- `Ctrl+F1` reuses the same safe `F1` function key with an added modifier, so it stays outside the game's observed Rewired gameplay bindings.
- No gameplay consumer for `F6` was found in the decompiled game code.
- The three `Ctrl`-modified `F6` bindings stay outside the game's observed Rewired gameplay bindings and do not conflict with the shipped `F2`, `F3`, or `F8` debug keys.
- No gameplay consumer for `F9` was found in the decompiled game code.
- `Ctrl+F9` reuses the same safe `F9` function key with an added modifier, so it stays outside the game's observed Rewired gameplay bindings.
- They sit outside the Rewired action flow the game relies on for normal keyboard/controller input.

### Keys to avoid

- `F2`, `F3`, `F8`: explicitly used by shipped debug/editor-style code
- `Confirm`, `Cancel`, arrows, number keys, and face-button-equivalent actions: already part of live UI/dialogue flows
- `F12`: runtime observation on this machine showed Steam screenshot interception even though the game code did not claim it

## UI System

### Primary menu control

- `CanvasUIManager`
  - `_activeMenu` is the easiest global indicator of which major menu is open
  - `SwitchMenu(...)` activates a menu and pushes the previous menu into history
  - `BackWithReturn()` handles back navigation and special phone-app return behavior

### Focus management

- `ControllerMenuUI`
  - `GetCurrentSelectedControl()` is the best read path for current focus
  - `SetCurrentlySelected(...)` is the best write path for focus
  - `IsAllowedToNavigateTo(...)` blocks some targets, especially quick-response buttons, unless pointer-style navigation is allowed

Important consequence:

- Dialogue quick-response buttons do not reliably accept normal manual focus moves.
- The successful accessibility fix used `isViaPointer: true` when focusing choice buttons.

### Dialogue UI

- `TalkingUI`
  - `open` indicates whether dialogue is active
  - private `dialogBox` stores the speaker and line text
  - private `choicesButtons` stores the active choice buttons
  - `ContinuePressed()` advances or reveals choices

- `DialogBoxBehavior`
  - private `nameText`
  - private `dialogText`
  - Text animation is gated by `Services.GameSettings.GetInt("AnimateText", 1)`

### Phone apps and app-specific screens

- `PhoneManager`
  - `IsPhoneMenuOpened()`
  - `IsPhoneAppOpened()`
  - `GetCurrentApp()`
  - `openedSubMenu`
  - `curPhoneMenu` is the live app container returned by `GetCurrentApp()`
  - `curPhoneMenu` is not always the only visible phone-app root
    - `OpenPhoneAppRoomers(...)` also enables `roomersMainWidget`
    - `OpenPhoneAppDateADex(...)` also enables `dateADexMainWidget`
  - `dayNumber` holds the day label on the phone home screen
  - `wrkspacePhoneAlert`, `thiscordPhoneAlert`, and `skylarPhoneAlert` each contain their unread-count `TextMeshProUGUI`

- Phone home screen buttons
  - The phone app buttons themselves are plain `GameObject` references on `PhoneManager`
  - `PhoneAppManager` only swaps sprites and selection state; it does not expose a dedicated label field
  - App names therefore need to come from the selected button's child `TMP_Text`, the interactable name, or the current app `GameObject.name`
  - Practical consequence:
    - generic phone-app text coverage should not assume `GetCurrentApp()` contains every visible text block
    - Roomers and Date A Dex need their extra widget roots inspected in addition to `curPhoneMenu`

- `Roomers`
  - `CurrentScreen`
  - `screenNameText` is the current tab label: `ALL`, `ACTIVE`, or `RESOLVED`
  - Each list row is a `RoomersEntryButton`; the visible row label is `nameText`
  - The detail pane lives in `roomersScreenInfo`
    - `RoomersTitle`
    - `RoomersDescription`
    - `CharacterName`
    - `RoomName`
  - Extra clue text is instantiated into `TipContainer` as `RoomersTip`
    - `TipTitle`
    - `TipInfo`

- `DateADex`
  - Each list row is a `DexEntryButton`
    - `numberText`
    - `nameText`
  - The main bio pane uses direct `TextMeshProUGUI` fields
    - `Item`
    - `Desc`
    - `VoActor`
    - `Likes`
    - `Dislikes`
  - `DescScroll` is the relevant visible viewport for the entry bio pane
  - `ListSummaryDataRealized`, `ListSummaryDataMet`, `ListSummaryDataLoves`, `ListSummaryDataFriends`, and `ListSummaryDataHates` hold the list summary counters
  - `collectableButtonText` holds the `x / total` collectable count for the selected entry
  - `CollectablesScreen` exposes the selected collectable text through
    - `collectableName`
    - `collectableDesc`
  - Underlying entry data comes from `DateADexEntry`
    - `CharName`
    - `CharObj`
    - `CharDYK`
    - `VoActor`
    - `CharLikes`
    - `CharDislikes`
    - `Collectable_Names_Desc_Hint`
  - `Pronouns` exists as a UI field on `DateADex`, but no assignment to `Pronouns.text` was found in the decompiled code path scanned so far
  - Important consequence:
    - Accessibility should not read the full bio payload blindly from `Item`, `Desc`, `VoActor`, `Likes`, `Dislikes`, and `Pronouns`
    - Those text blocks need to be filtered against `DescScroll.viewport` so speech matches only the portions currently visible on screen

- Chat apps: `Wrkspace`, `Canopy`, `Thiscord`
  - Chat list rows are `ChatButton`
    - `CharacterName` is the visible conversation label
  - Open-chat headers are app-specific
    - `ChatMaster.CharacterNameText` for `Wrkspace`
    - `ChatMaster.FriendName` for `Thiscord`
    - `CanopyEmptyMessage` is the explicit empty-state indicator for `Canopy`
- Chat history is not stored as a static label on the app screen
  - `ParallelChat.GetText()` pulls live text from `InkController.story.currentText`
  - `ParallelChat` renders each message into a `ChatTextBox`
  - `ChatTextBox.Dialogue` is the rendered message text actually shown on screen
- `ParallelChat.screct.viewport` is the relevant visible window for accessibility summaries
- Reply choices come from `InkController.story.currentChoices`
  - `ParallelChat.Options[i].GetComponentInChildren<TextMeshProUGUI>().text`
  - `ParallelChat.Options` are wired as quick-response UI and `ControllerMenuUI.IsAllowedToNavigateTo(...)` blocks manual focus on `QuickResponseButton` targets unless accessibility uses `SetCurrentlySelected(..., isViaPointer: true)`
  - Save data keeps structure, not a ready-made rendered transcript
    - `Save.AppMessage.Name`
    - `Save.AppMessage.NodePrefix`
    - `Save.AppMessage.StitchHistory`
    - `Save.AppMessageStitch.ChatHistoryOptionsSelected`
  - Important consequence:
    - For full text coverage, the best live source is the active `ParallelChat` UI plus Ink `currentText/currentChoices`, not only the saved `AppMessage` data
    - Accessibility summaries should read only the `ChatTextBox` entries that overlap the current `ScrollRect` viewport, because the full chat history remains mounted in the hierarchy even when older messages are scrolled off-screen
    - Accessibility focus fallback for chat replies should treat `ParallelChat.Options` like dialogue choices, not like generic chat-panel focus, or the spoken result collapses to the chat header instead of the selected reply text

- `MusicPlayer`
  - Each list row is a `MusicEntryButton`
    - `numberText`
    - `nameText`
  - The active track label is `SongTitle`
  - `UpdateTrackText()` fills `SongTitle` from `MusicEntry.number` and `MusicEntry.title`
  - The volume warning is a normal `Popup` created with explicit title/body strings

- `ArtPlayer`
  - Each list row is an `ArtEntryButton`
    - `numberText`
    - `nameText`
  - The currently selected artwork identity is `selectedArt`
    - `selectedArt.number`
    - `selectedArt.title`
  - `ArtTitle` exists as a field, but no assignment path to `ArtTitle.text` was found in the scanned decompiled code
  - Practical consequence:
    - Treat the selected row text and `selectedArt.title` as the authoritative title source unless runtime inspection proves a hidden binding updates `ArtTitle`

- Phone-launched utility screens
  - `SettingsMenu`
    - Slider value text lives in the explicit fields such as `MasterVolumeSliderValue`, `SFXVolumeSliderValue`, `MusicVolumeSliderValue`, `VoiceVolumeSliderValue`, `CameraSensitivitySliderValue`, `CameraFieldOfViewSliderValue`, and `MovementSpeedSliderValue`
    - The selected control label itself still follows the general focus-text path
  - `ValidateQuestions`
    - Main-menu new-game questionnaire controller
    - Public interactive fields found in decompiled source:
      - `nameTextField`
      - `townTextField`
      - `favThingTextField`
      - `defaultPronoun`
      - `mandatoryToggle`
      - `nextButton`
    - A decompiled source search did not find the printed form copy such as `APPLICATION FOR EMPLOYMENT`, `PERSONAL INFORMATION`, `CONTACT INFORMATION`, `Preferred Pronouns`, `E-mail Address`, or `Date of Birth`
    - Practical consequence:
      - the large printed questionnaire copy is probably scene art or another non-code asset rather than live text defined in C#
      - wiring that copy to accessibility will need asset or runtime-object inspection, not just source-string lookup
  - `SaveScreenManager` / `SaveSlot`
    - Each save slot exposes visible text through `Name`, `Date`, `Time`, `playTime`, `daysPlayed`, `activatedCharacters`, and `realisedCharacters`
    - `SaveScreenManager` keeps the special `newSaveSlot` entry in a private field and focuses its child button, not the container text object
    - Practical consequence:
      - accessibility should treat focus inside `SaveScreenManager` as a slot-selection context, not a generic button-label context
      - existing saves should be announced from the parent `SaveSlot` metadata fields, because the focused control is usually the action button (`Save` or `Load`) layered on top of the slot
      - the new-save entry should read from the `newSaveSlot` container text when present, with a fallback label such as `New save` if the focused child button has no readable text of its own
  - `CreditsScreen`
    - `tmp_credits` is the full credits text block
    - `StartCredits()` rewrites the placeholder player-name token in `tmp_credits`, starts the credits scroll, and focuses `backButton` when this is the phone credits screen
    - Practical consequence:
      - the credits text needs its own screen-text reader, because the focused `Back` button does not expose the rolling credits body
  - `SpecStatMain`
    - Stat blocks and glossary blocks use `SpecStatBlock` / `SpecGlossaryBlock` text fields rather than phone-specific wrappers
    - Each `SpecStatBlock` also carries a private `levelDescriptionText`, so generic TMP extraction reads more than the visible stats intent unless the mod filters it out
    - The opening tutorial messages are not `Popup`; they use `UIDialogManager.ShowOKDialog(...)`
    - `OnEnable()` / `Enabled()` start `SelectInitialButton()`, and `OnOpenedMenu()` only calls `ShowSpecsTutorialMessages()` after that focus setup completes
    - `Enabled()` / `OnEnable()` call `SelectInitialButton()`, which immediately focuses the default SPECS button
    - The initial SPECS focus target is `keyButton` or `autoSelectFallback`, both private `IsSelectableRegistered` fields on `SpecStatMain`
    - Practical consequence:
      - SPECS needs both a `UIDialogManager` reader for tutorial dialogs and a dedicated SPECS content reader, or focus speech will only see the default button
      - generic focus text is not enough for SPECS because the initial button can expose only its glyph text and the stat blocks contain hidden description text that should not be spoken on the initial stats view
      - there is a short open-time race where SPECS can become visible before its first tutorial dialog exists, so accessibility needs a brief initial settle window or the stats/glossary summary can speak before the alert
      - because SPECS is opened as a phone app, any generic phone-app visible-text fallback also needs to respect that tutorial-dialog settle window or it can leak glossary or stat text underneath the alert
      - the decompiled source map splits cleanly into:
        stats page speech from `SpecStatBlock.NameFirstLetter`, `NameRest`, and `AdjectiveLabel`,
        tooltip speech from `statTooltips`,
        and glossary speech from `SpecGlossaryBlock.NameFirstLetter`, `NameRest`, and `descriptionText`
- `Popup`
  - `title`
  - `text`
  - `LegendNavigateText`
  - `LegendConfirmText`
  - `CreatePopup(...)` writes `title` and `text`, activates the popup, then immediately selects the default `ChatButton` (`OK` for single-button alerts, `Yes` for yes/no alerts)
  - Practical consequence:
      - popup body speech must win over the popup's initial focus speech, or the auto-focused button can interrupt the alert text before it finishes

- `ExamineController`
  - `ExamineGameObject`
  - `ExamineText`
  - `isShown`
  - `ShowExamine(string text)` activates the examine panel, writes the text into `ExamineText`, and resizes it on the next frame
  - Practical consequence:
      - object examination text does not flow through the normal focus path or phone summaries
      - accessibility needs a dedicated reader for `ExamineController.ExamineText` while `isShown` is true

### Text access pattern

The most reliable general-purpose text extraction path remains:

- current selected `GameObject`
- nearest `Selectable`
- `GetComponentsInChildren<TMP_Text>(includeInactive: true)`

That approach already works for menu focus speech and dialogue choice speech, but the phone scan shows it is not sufficient by itself for:

- hidden detail panes such as `RoomersInfo` and `DateADex`
- unread counters and status labels on the phone shell
- chat history generated from Ink into `ParallelChat`
- popups and utility screens that expose non-focused text blocks

## Gameplay State and Status Sources

### Current room

- `Singleton<CameraSpaces>.Instance.PlayerZone()?.Name`
- `CameraSpaces.CameraLoc(Transform t)`
  - checks `Bounds.Contains(t.position)`
  - also checks `Collider.ClosestPointOnBounds(zone.Position)` when the tracked transform has a collider

This is the cleanest room identifier found for house navigation speech.

### Room navigation graph

- `Singleton<CameraSpaces>.Instance.zones`
  - each `triggerzone` exposes `Name`, `Position`, and `Scale`
  - `Position` is currently the most reliable room anchor for navigation handoff and auto-walk targeting
- `Singleton<CameraSpaces>.Instance.PlayerZone()`
  - resolves the player's current room zone at runtime
- `BetterPlayerControl`
  - private `move` and `look` fields can be written by reflection for accessibility auto-walk input
  - `STATE == BetterPlayerControl.PlayerState.CanControl` is the safe gate before applying movement
- `BepInEx\plugins\navigation_graph.json`
  - the current game-directory copy contains usable room `Links`, nonzero `FromWaypoint` and `ToWaypoint` coordinates, and populated `Cost` values
  - the current file was generated from ripped scene data plus explicit connector overrides through `.\scripts\Build-NavigationGraph.ps1`
- AssetRipper export of `ThirdPersonGreybox.unity`
  - the scene's `CameraSetup` object serializes the full `CameraSpaces.zones` list directly into the asset
  - every zone currently used by `navigation_graph.json` exists in that `zones` list
  - the scene also contains many extra `CameraSpaces` entries that are not in the JSON, including room-local variants such as `hallway2` to `hallway7`, `living_room2` to `living_room11`, `office2` to `office9`, `piano_room2` to `piano_room11`, and `upper_hallway2` to `upper_hallway8`
  - those extra entries are the strongest current asset-side candidates for finer-grained waypoint nodes or doorway-adjacent anchors
  - practical consequence:
    - runtime room-object listing cannot assume a single coarse room zone
    - when an exact room-zone match is empty, a same-family fallback such as `office6` -> `office` is a reasonable recovery path for room-local object discovery
- `Teleporter` in `ThirdPersonGreybox.unity`
  - the crawlspace teleporter serializes explicit `LocationDown` and `LocationUp` destination objects
  - it also serializes `teleportInRotation` and `teleportOutRotation`
  - practical consequence:
    - the crawlspace transition already has explicit asset-defined endpoints and facing data
    - ordinary room-to-room transitions do not appear to expose the same direct paired-endpoint structure
- Asset-mining helper in this repo
  - `.\scripts\Export-SceneNavigationData.ps1`
  - writes `artifacts\navigation\thirdpersongreybox-navigation-data.json`
  - emits world-space door objects, camera objects, teleporter endpoints, and `CameraSpaces`
  - `artifacts\navigation\README.md` lists the currently identified door, stair, closet, and crawlspace connector assets for the main graph links
- Graph-construction helper in this repo
  - `.\scripts\Build-NavigationGraph.ps1`
  - writes `artifacts\navigation\navigation_graph.generated.json`
  - uses explicit door, stairs, and teleporter connector overrides where available
  - emits per-link `StepKind`, `ConnectorName`, `RequiresInteraction`, and `TransitionWaitSeconds` metadata for runtime navigation handling
  - falls back to zone anchors or nearest room-family anchors for the remaining links
- Runtime consumption
  - `NavigationGraph.FindPathSteps(...)` now parses and returns per-link `FromWaypoint`, `ToWaypoint`, `Cost`, transition type, connector name, and transition timing data from the live JSON
  - `AccessibilityWatcher` now tracks a concrete interactable target, falls back to the current step waypoint while that target is still in another zone, and switches back to the live object position after entering the target room
  - `AccessibilityWatcher` treats teleporter links as interaction-driven transitions, waits through the crawlspace animation while player control is disabled, and can retry door interactions before declaring navigation blocked
  - `ObjectTracker` beeps now follow the tracked object or current waypoint chosen by the watcher, use stereo panning for left or right guidance, map pitch to the target's height relative to the camera, map beep rate to target proximity, and raise volume as the player gets closer
  - practical consequence:
    - the navigation graph currently uses coarse authored zones such as `office`
    - live `CameraSpaces.PlayerZone()` can return finer runtime subzones such as `office2`, `office5`, and `office6`
    - tracker and auto-walk code therefore need to normalize runtime subzones onto graph zones for pathfinding and also treat same-family subzones as the same room for direct-object guidance
- Generic interactable placement data
  - many serialized `Interactable.interactedPosition` values in the scene export are still `{x: 0, y: 0, z: 0}`
  - practical consequence:
    - `interactedPosition` is not a reliable generic source for doorway waypoints in this scene

Important consequence:

- Inter-room navigation can now use authored or inferred connector waypoints from the generated JSON instead of only zeroed placeholders.
- The generated graph now carries enough metadata for runtime code to distinguish open passages, doors, stairs, and the crawlspace teleporter without hardcoded room-pair logic in the watcher.
- In-room guidance may still benefit from live `triggerzone.Position` or runtime refinement, especially for open passages and any link where the generated graph had to fall back to room-family heuristics.
- Asset data is sufficient to build and regenerate the current waypoint graph from local tooling.

### Current interactable

- `Singleton<InteractableManager>.Instance.activeObject`
- `InteractableObj.InteractionPrompt`
- `InteractableObj.InternalName()`
- `InteractableObj.mainText`
- `InteractableObj.AlternateInteractions[0].Name`
- `Save.TryGetNameByInternalName(...)` to resolve display names
- `Save.GetDateStatus(...)` to determine whether the player has met the datable yet

Important consequence:

- `InteractableManager` uses `AlternateInteractions[0].Name` as the visible in-world object label for normal house interactions.
- Accessibility tracker labels and current-room object lists should prefer that object-facing label before falling back to `mainText`, scene object names, or internal names.
- Nearby-object speech should not always use the dateable name.
- `InteractableObj.StartDialogue()` calls `Save.MeetDatableIfUnmet(InternalName())`, so `GetDateStatus(...) != Unmet` is the correct "player knows this character" boundary.
- Before that point, prefer the object's own label, starting with `InteractableObj.mainText` when it is populated, then falling back to the scene object name.
- After the player has met the datable, it is appropriate to switch to the resolved character name from `TryGetNameByInternalName(...)`.

### AudioManager 3D track behavior

- `AudioManager.PlayTrack(...)`
- `AudioManager.NewTrack(...)`
- `AudioManager.MusicChild.GetAudio()`

Important consequence:

- `AudioManager.NewTrack(...)` only applies the game's built-in 3D setup when `objectFor3dSound` is non-null.
- In that path the new source is initialized with positional audio settings and anchored to the owner object's current world position.
- For a moving accessibility tracker tone, the safest game-native approach is to keep a dedicated anchor `GameObject`, pass it as `objectFor3dSound`, and move that anchor as the tracked object or waypoint changes.

### Tutorial objectives

- `TutorialController.SetTutorialText(...)`
  - this is the authoritative source for the current tutorial signpost text
  - it drives objective text such as `Start your new job at your computer`, `Check the delivery at the front door.`, `Awaken your phone.`, `Locate the magnifying glass to Awaken it.`, and `Talk to Skylar Specs.`
- `TutorialController`
  - private `computer` field stores the tutorial computer anchor `GameObject`
  - private `frontDoor` field stores the tutorial front-door anchor `GameObject`

Important consequence:

- Objective tracking should prefer the live tutorial signpost text over reconstructed save-state guesses when both are available.
- Computer and front-door tracking can anchor directly from those serialized `TutorialController` object references before falling back to generic interactable-name heuristics.
- `TutorialController.SetTutorialText(...)` also emits generic prompts such as `Continue to awaken dateable objects.` and `Realize Dateable Objects.` that do not name a single character directly.
- For those generic prompts, accessibility objective tracking needs a fallback policy such as picking the nearest valid unmet or not-yet-realized dateable interactable instead of returning `No current objective.`
- The `tutorialSignpostTMP` text can still hold the current objective even when the signpost object itself is hidden, so objective readers should key off the text field, not only the signpost object's active state.
- Maggie’s tutorial progression uses mixed identifiers: save-state checks use `maggie_mglass`, while the tutorial interaction gate checks `obj.InternalName() != "maggie"`, so tracker matching should tolerate both names plus visible labels like `Maggie` or `Magnifying`.

### Hotkey overlap

- Windows `RegisterHotKey(...)` registrations can overlap when one modifier combination is a strict superset of another, for example `Ctrl+F6` and `Ctrl+Shift+F6`.

Important consequence:

- The mod cannot rely on the registered hotkey id alone to distinguish those combinations cleanly.
- The hotkey handler needs an explicit modifier-state check so `Ctrl+Shift+F6` and `Ctrl+Alt+F6` do not also trigger the plain `Ctrl+F6` objective action.

### Dateviators status

- `Singleton<Dateviators>.Instance.IsEquipped`
- `Singleton<Dateviators>.Instance.IsEquippingOrEquipped`
- `Singleton<Dateviators>.Instance.GetCurrentCharges()`

### Dialogue state

- `TalkingUI.Instance.open`
- `GameController.viewState == VIEW_STATE.TALKING`

## Status and Feedback Systems

Relevant systems already identified for future speech hooks:

- `MessageLogManager`
  - toggled from `BetterPlayerControl` via the `Message Log` action during dialogue

- `Popup`
  - `Singleton<Popup>.Instance.IsPopupOpen()`
  - used heavily to block or redirect UI focus
  - popup opening also writes controller focus to one of its buttons immediately via `SelectButton(...)`

- `UIDialogManager`
  - `HasActiveDialogs`
  - `ShowOKDialog(...)`, `ShowOkCancelDialog(...)`, `ShowYesNoDialog(...)`
  - internally tracks active dialogs in `_activeDialogs`
  - each `UIDialog` has private `_title`, `_bodyText`, and `_theDialog` fields and auto-selects one of its option buttons through `SelectButton(...)`
  - dialog presence affects `ControllerMenuUI` navigation rules

- `SpecStatMain`, `ResultSplashScreen`, `SaveScreenManager`
  - concrete menu/screen classes with their own action handling

## Event Hooks and Patch Targets

Good Harmony targets for future feature work:

- `TalkingUI.RefreshViewStep3AfterParseTag()`
  - dialogue line and choice refresh point

- `TalkingUI.ContinuePressed()`
  - useful for intercepting advance / reveal-choice transitions

- `CanvasUIManager.SwitchMenu(...)`
  - announces major menu transitions cleanly

- `CanvasUIManager.BackWithReturn()`
  - good for back-navigation announcements

- `PhoneManager.ReturnToMainPhoneScreen()`
  - phone home transitions

- `PhoneManager.ReturnToMainPhoneScreenRotate()`
  - additional phone return path

- `Roomers.SwitchScreen(...)`
  - app-specific screen summary updates

- `DateADex` entry/screen update methods
  - likely better than polling once entry-specific features begin
  - `OpenEntry(int)` populates `MainEntryScreen`, hides `RecipeScreen`, fills `Item`, `Desc`, `VoActor`, `Likes`, and `Dislikes`, resets `DescScroll`, and refreshes collectables before focus later lands on `CollectableButton`
  - accessibility implication:
    - the initial Date A Dex bio announcement is more reliable from an `OpenEntry(int)` postfix than from focus polling alone, because the game commits the entry data before the default button focus takes over

- `BetterPlayerControl.Update()`
  - only for carefully scoped observation; avoid heavy patching here unless needed

## Localization

### Game localization facts

- `SettingsMenu` stores text language in `Services.GameSettings` under `textLanguage`
- `VOICE_LANGUAGE_KEY` is `voiceLanguage`
- `TextLanguage` currently exposes English and Japanese in the decompiled code
- `TranslationManager.GetTranslation(...)` currently reads `textLanguage` but returns `TextEnglish.GetText(id)`, so localization support looks incomplete or stripped in this build

### Mod implication

- The mod can use `Services.GameSettings.GetInt("textLanguage", 0)` as the authoritative game language setting
- Because the game-side text language options found so far are English and Japanese, the mod should keep its own fallback behavior for unsupported languages

## Architecture Decisions

- Use `ControllerMenuUI` rather than raw `EventSystem` alone whenever possible
- Use reflection for `TalkingUI.dialogBox`, `DialogBoxBehavior.nameText`, `DialogBoxBehavior.dialogText`, and `TalkingUI.choicesButtons`
- Prefer screen summaries derived from live singleton state over Harmony patches until a feature becomes too noisy or too stateful for polling
- Keep mod hotkeys outside the game’s Rewired action set unless there is a strong reason to bind into the game’s own action maps

## Current Accessibility Hooks Implemented

- Focused menu item speech
- Dialogue line speech
- Dialogue choice speech
- Arrow-key dialogue choice navigation
- `Enter` / `Space` choice activation
- Phone/menu screen summaries
- Room announcements
- Nearby interactable announcements
- Dateviators equip/charge change announcements
- `Ctrl+F6` current tutorial objective tracking
- `Ctrl+Shift+F6` current-room object listing
- `Ctrl+Alt+F6` auto-walk to the tracked object

## Not Yet Analyzed

- Remaining runtime verification for hidden phone bindings that may be populated outside the decompiled code path scan
- Full message log content extraction
- Non-dialogue gameplay notifications such as pickups, relationship changes, or time-of-day transitions

## Latest Tracker Findings

- Tutorial objective resolution can surface a model-child `InteractableObj` instead of the usable root interactable, especially for Maggie and other multi-part dateables. Tracker resolution should canonicalize within the interactable hierarchy before trusting the candidate for zone lookup or navigation.
- `UpdateNavigationState()` should not rebuild the navigation path every poll while the player is still in the current step's `FromZone`. Rebuilding every frame causes repeated tracker restarts and unstable open-passage auto-walk behavior even when the target step has not changed.
- Runtime transit zones such as `stairsdown` can be outside the authored navigation graph. Auto-walk must keep the current graph step alive through those zones and continue toward the step's destination graph zone instead of refreshing the path immediately on entry-waypoint arrival.
