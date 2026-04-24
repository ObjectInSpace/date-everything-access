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
- `Ctrl+Alt+Shift+F1`: start or stop the dev-only live route audit
- `F6`: report the current room and room objects relative to the direction the player is facing
- `Ctrl+F6`: track the current tutorial objective
- `Ctrl+Shift+F6`: open and cycle the whole-house room list
- `Ctrl+Alt+F6`: toggle auto-walk to the tracked object
- `Ctrl+Alt+Shift+F6`: start or stop the dev-only door transition sweep
- `F9`: toggle debug
- `Ctrl+F9`: accessibility settings menu
- `Ctrl+Shift+F9`: export live navmesh triangulation and transition checks
- `Ctrl+Alt+Shift+F9`: start or stop the dev-only transition sweep

### Why these are safe

- No gameplay consumer for `F1` was found in the decompiled game code.
- `Ctrl+F1` reuses the same safe `F1` function key with an added modifier, so it stays outside the game's observed Rewired gameplay bindings.
- `Ctrl+Alt+Shift+F1` stays on the same safe `F1` key family, adds three modifiers, and is explicitly filtered so it does not also trigger help or repeat-last-speech.
- No gameplay consumer for plain `F6` was found in the decompiled game code.
- The plain plus `Ctrl`-modified `F6` bindings stay outside the game's observed Rewired gameplay bindings and do not conflict with the shipped `F2`, `F3`, or `F8` debug keys.
- No gameplay consumer for `F9` was found in the decompiled game code.
- `Ctrl+F9` reuses the same safe `F9` function key with an added modifier, so it stays outside the game's observed Rewired gameplay bindings.
- `Ctrl+Shift+F9` reuses the same safe `F9` function key with one more modifier and is explicitly filtered in the hotkey handler so it does not also trigger plain debug toggle or the settings menu.
- `Ctrl+Alt+Shift+F9` stays on the same safe `F9` key family, adds a third modifier, and is explicitly filtered so it does not also trigger debug, settings, or navmesh export.
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
- Keyboard-driven accessibility navigation should keep using pointer-style focus writes for both dialogue choices and chat quick responses; plain manual selection has regressed in runtime.

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
    - If accessibility keeps a virtual reply index as a fallback, it must reset or reseed that index from the real selected reply when the active `ParallelChat` instance changes; carrying a stale index into a new chat makes the first arrow move land on the wrong option

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
  - movement is applied in `FixedUpdate()` through the player's `Rigidbody` and `Collider`
  - the controller uses a `CapsuleCollider` for player body sizing, rotates the rigidbody from `look`, writes horizontal motion into `_rigidbody.velocity`, and uses a downward physics raycast for ground clamping
  - `STATE == BetterPlayerControl.PlayerState.CanControl` is the safe gate before applying movement
- `BepInEx\plugins\navigation_graph.json`
  - the current game-directory copy now uses schema version `2` with top-level `Zones`, `Nodes`, and directed `Transitions`
  - each transition now carries explicit `SourceApproachPoint`, `SourceClearPoint`, `DestinationClearPoint`, `DestinationApproachPoint`, ordered `NavigationPoints`, and nested runtime `Validation` metadata in addition to the older coarse waypoint and crossing-anchor fields
  - the current file was generated from ripped scene data plus explicit connector overrides through `.\scripts\Build-NavigationGraph.ps1`
  - closet-style transitions can now also carry `ConnectorNames` arrays so one physical door can match multiple runtime interactable names such as `Inner` and `Outer`
  - as of the deployed live audit generated at `2026-04-17T02:28:46Z`, all `11` current door pairs in the game-side graph align cleanly to exported scene door objects, including the previously bad `bedroom <-> bedroom_closet` and `gym <-> gym_closet` pairs
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
- Asset-side navmesh inspection
  - `.\scripts\Inspect-SceneNavMeshAssets.ps1`
  - writes `artifacts\navigation\thirdpersongreybox-navmesh-assets-report.json`
  - confirms that the exported `ThirdPersonGreybox.unity` scene contains `NavMeshSettings` but its `m_NavMeshData` reference is `fileID: 0`
  - the raw Addressables scene bundle still contains generic navmesh type metadata and shared-assets references, but no scene-linked baked `NavMeshData` asset was found through the local export path
  - practical consequence:
    - there is currently no evidence that `ThirdPersonGreybox` ships a baked navmesh asset we can extract and use as authoritative transition geometry
    - transition validation should continue to rely on scene geometry, authored connector assets, and runtime behavior instead of waiting on a hidden baked navmesh dump
- Asset-mining helper in this repo
  - `.\scripts\Export-SceneNavigationData.ps1`
  - writes `artifacts\navigation\thirdpersongreybox-navigation-data.json`
  - emits world-space door objects, camera objects, teleporter endpoints, and `CameraSpaces`
  - `artifacts\navigation\README.md` lists the currently identified door, stair, closet, and crawlspace connector assets for the main graph links
- Blocker-export helper in this repo
  - `.\scripts\Export-SceneBlockerData.ps1`
  - writes `artifacts\navigation\thirdpersongreybox-blockers.json`
  - currently exports primitive colliders only, filters out triggers, inactive objects, door and teleporter connectors, rigidbody-driven objects, tiny footprints, and blockers outside the player-height band
  - retained blocker records include `Bounds3D`, `Bounds2D`, `BottomY`, `TopY`, and a flattened footprint so later tooling can reject blockers that only overlap a zone in `x/z` while sitting on a different floor
  - current `ThirdPersonGreybox` export finds `623` primitive colliders, keeps `67` filtered navigation blockers, and also records `2317` mesh colliders plus `1` terrain collider that are not yet converted into blocker footprints
  - practical consequence:
    - this is enough data to begin blocker-aware local planning and debugging
    - mesh and terrain support are still a future refinement, not part of the first occupancy pass
- Local-map builder in this repo
  - `.\scripts\Build-LocalNavigationMaps.ps1`
  - writes `artifacts\navigation\local_navigation_maps.generated.json`
  - uses `CameraSpaces` family bounds as the per-zone walk envelope and subtracts filtered primitive blocker footprints on a fixed grid
  - blocker rasterization is now height-aware per zone: a blocker is only applied when its `Bounds3D` overlaps the zone's camera-space volume, which prevents downstairs furniture from blocking upstairs bathroom, gym, or hallway zones that only overlap in `x/z`
  - point-sized `CameraSpaces` are expanded to a minimum half-cell envelope during rasterization so microzones such as `dorian_*` connector nodes and `office_1love` still get a usable occupancy cell instead of an empty `1x1` map
  - current output covers all `57` graph zones with no missing `CameraSpaces` matches
  - practical consequence:
    - the repo now has an offline per-zone occupancy artifact for blocker-aware runtime steering
    - the current runtime pass loads this file from `BepInEx\plugins\local_navigation_maps.generated.json` and uses it for short in-room waypoint planning, but only for zones covered by the generated graph and only with primitive-blocker occupancy so far
- Graph-construction helper in this repo
  - `.\scripts\Build-NavigationGraph.ps1`
  - writes `artifacts\navigation\navigation_graph.generated.json`
  - uses explicit door, stairs, and teleporter connector overrides where available
  - emits `57` coarse zones, `139` in-room nodes sourced from `CameraSpaces`, and `112` explicit directed transitions
  - each transition now resolves source and destination room-node ids, explicit connector approach and clear points, ordered traversal points, connector object position when available, and builder-side validation metadata such as accepted runtime subzones, timeout recommendations, and static suspicion issues
  - all `OpenPassage` links now derive `FromWaypoint`, `ToWaypoint`, `FromCrossingAnchor`, and `ToCrossingAnchor` from the scene's own `CameraSpaces` geometry by comparing the exact graph zone plus same-family numbered variants and choosing the smallest scene-side boundary gap
  - scene-export follow-up:
    - `office -> office_closet` should use door object `Doors_Office_Closet`, not only the paired `Camera_DorianOfficeClosetDoor1/2` waypoints
    - `laundry_room -> laundry_room_closet` should use door object `Doors_Laundry_Closet`, not only `Camera_DorianLaundryCloset1` plus `Camera_Laundry Room_Closet`
    - `dorian_bathroom2_1` and `dorian_bathroom2_2` are the exact `Camera_DorianBathroom2Door1/2` subzones for `Doors_Bedroom_Bathroom`; `bathroom2 -> dorian_bathroom2_2` should be treated as a door-camera classification problem, not as a standalone open passage
  - practical consequence:
    - the builder no longer needs hand-picked open-passage room-pair overrides for the current graph
    - the generated graph now carries scene-derived threshold anchors even for subzone-to-room open passages such as `dorian_office2 -> office`
- Runtime consumption
  - `NavigationGraph.FindPathSteps(...)` now parses and returns directed transition records from the schema-v2 `Transitions` array instead of mirroring a legacy undirected link list at runtime
  - runtime follow-up: Unity `JsonUtility` did not reliably materialize the generated schema-v2 `Transitions` array in-game once the richer nested metadata was added, even though the file itself was present and current; `NavigationGraph` now falls back to `DataContractJsonSerializer` for that graph file and also seeds its known-zone set from the top-level `Zones` list before parsing links
  - each `PathStep` now carries room-node ids, explicit source and destination connector approach and clear points, ordered navigation points, accepted source and destination runtime subzones, validation timeout recommendations, static suspicion score, and asset-derivation metadata
- `LocalNavigationMaps`
  - loads `BepInEx\plugins\local_navigation_maps.generated.json` through `DataContractJsonSerializer`
  - runtime follow-up from the `2026-04-08` rerun: the live build exposed that older generated files could serialize single-entry `EnvelopeIndices` or `BlockedIndices` values as scalars instead of arrays, which breaks `DataContractJsonSerializer` at `EnvelopeIndices`
  - implementation follow-up from the same session: `LocalNavigationMaps.Initialize()` now normalizes those malformed scalar index properties before deserialization, and `.\scripts\Build-LocalNavigationMaps.ps1` now emits proper JSON arrays again for fresh generated files
  - resolves the nearest walkable start and goal cells inside one graph zone, runs an 8-neighbor A* search over the exported occupancy grid, and returns compressed cell-center waypoints
  - start and goal snapping now first search the normal nearby cell radius, then fall back to the nearest walkable cell anywhere in the same zone before failing; the returned failure reason now includes the snap detail used for both ends when a local path cannot be built
  - practical consequence:
    - the runtime mod can now replace a raw in-room target with a short locally planned waypoint when the player is navigating inside one source or destination zone
    - crossing a doorway threshold still depends on the existing connector and open-passage stage logic; the local planner is currently an in-zone steering layer, not a full multi-zone navmesh replacement
- `NavigationGraph.FindPathSteps(...)` now also accepts optional live start and end positions so the first step is biased from the player's current location and the final step is biased toward the tracked object's live location
- When navigation is targeting a plain room zone instead of a tracked interactable, `AccessibilityWatcher` now treats normalized graph-zone equivalence as arrival. Practical consequence: room-target auto-walk can stop successfully on sibling runtime subzones such as `office2` for graph room `office` instead of failing on an exact runtime-zone-name mismatch.
- `AccessibilityWatcher` now tracks a concrete interactable target, falls back to the current step waypoint while that target is still in another zone, and switches back to a stable object-specific approach position after entering the target room
- same-zone tracked-object guidance now builds candidate approach points from the interactable's live collider or renderer bounds, caches the chosen target while that object stays stable, and prefers the shortest reachable candidate when local occupancy data is available
- tracked-target navigation mode is now resolved in one shared helper, so direct-object exact-zone tracking and same-family subzone anchoring use the same rules in path refresh, tracker targeting, and auto-walk instead of maintaining separate fallback checks
- `AccessibilityWatcher` now refreshes navigation when the live position-aware search picks a different first step, and same-family runtime subzones such as `office2` and `office6` can now keep direct-object guidance instead of falling back to a stale room anchor
- `AccessibilityWatcher` now uses those explicit connector points for open-passage handoff and door sweep stance or push-through selection, and falls back to the coarse waypoint fields only when the richer points are absent
- door post-interaction target selection is now shared between normal traversal and door sweeps, with live door traversal as the canonical path and the sweep harness reusing that same threshold-advance, threshold-handoff, push-through, and entry-advance sequence instead of carrying a peer implementation
- door traversal geometry helpers and constants are now named for core traversal rather than the sweep harness, and live traversal now reuses the same richer post-door destination chooser instead of stopping at the coarse `ToWaypoint` fallback when better destination-side points are available
- `AccessibilityWatcher` now also turns directed override waypoints plus graph-supplied `NavigationPoints`, `SourceClearPoint`, `DestinationClearPoint`, and final approach data into one ordered guided open-passage path, then walks that path in short progressive chunks instead of jumping straight from one source-side override point to a far destination waypoint
- `AccessibilityWatcher` now runs local path selection ahead of movement and tracker beeps:
  - same-zone direct-object tracking now routes toward the chosen object-approach target instead of the raw object pivot, and can still use the local occupancy grid to reach that target around blockers
  - source-side open-passage movement can route around static blockers while approaching the source guidance or source handoff point
  - destination-side open-passage movement can route around blockers while settling toward the destination approach point
  - door, stairs, and other non-open-passage steps can route to their current in-zone waypoint through the local occupancy grid before falling back to raw straight-line movement
- `AccessibilityWatcher` treats teleporter links as interaction-driven transitions, waits through the crawlspace animation while player control is disabled, can retry door interactions before declaring navigation blocked, and now falls back to per-transition validation timeout and accepted-subzone defaults from the graph when no directed override entry exists
  - when navigation still blocks, the watcher now keeps the most recent blocked-detail string and writes it into debug output plus transition-sweep failure detail so doorway and threshold stalls can be triaged from the live report instead of appearing only as a generic block
  - practical consequence:
    - the current auto-walk layer is now hybrid: graph-driven for room-to-room routing and occupancy-grid-driven for short in-zone steering
    - same-zone tracked-object arrival is now bounds-aware, but the stack is still not a full local avoidance system because mesh and terrain colliders are not rasterized yet and final blocked-recovery tuning is still follow-up work
- Repository readiness review on 2026-04-09:
  - the current stack is close on room-to-room routing, but it is not yet ready to claim reliable blind-player guidance to any arbitrary house object
  - target acquisition is still narrow: navigation currently starts from an existing tracked interactable, the current tutorial objective, or the whole-house room list, and there is not yet a searchable whole-house object registry or coverage report for every interactable
  - interactable zone assignment and final object approach remain heuristic: zone lookup scores transforms, colliders, and renderers against `CameraSpaces` bounds with an `8`-unit fallback, and final approach selection still falls back to the raw object position when candidate generation or local-path resolution fails
  - automated validation still leaves one open passage (`dining_room -> piano_room`) and one door (`office -> office_closet`) unresolved in the latest merged reruns, and stairs plus teleporter links still depend on targeted runtime testing because the automated sweeps only classify `OpenPassage` and `Door` steps
- `Ctrl+Shift+F9` now runs a live navmesh export through `UnityEngine.AI.NavMesh.CalculateTriangulation()` and writes `BepInEx\plugins\navmesh_export.live.json`
- The same `Ctrl+Shift+F9` export path now also writes `BepInEx\plugins\selected_object_coverage.live.json`, which evaluates the currently selected tracked or objective interactable against graph zones, walkable local-map components, saved transition-sweep evidence, and final approach-target resolution
- when `CalculateTriangulation()` returns no active scene navmesh, the exporter now still writes a diagnostic `navmesh_export.live.json` with `HasActiveNavMesh = false`, zero triangles, loaded-scene metadata, and the per-transition anchor checks instead of aborting without a file
- the export samples each directed `NavigationGraph` step's waypoints and crossing anchors onto the current navmesh and records the resulting path status and any missing or suspicious anchors
- `.\scripts\Import-NavMeshExport.ps1` copies that live export into `artifacts\navigation\navmesh_export.live.json` for repo-side inspection
- `.\scripts\Import-SelectedObjectCoverageReport.ps1` copies the selected-object coverage export into `artifacts\navigation\selected_object_coverage.live.json` for repo-side inspection
- `Ctrl+Alt+Shift+F9` now runs a dev-only `OpenPassage` transition sweep that teleports the player to each directed graph step's source, forces a single-step auto-walk attempt, and writes `BepInEx\plugins\transition_sweep.live.json`
- `Ctrl+Alt+Shift+F6` now runs a separate dev-only `Door` transition sweep that teleports to a stand-clear source-side position, shifts slightly off-center so the player is less likely to block the swing, snaps to an interaction-ready stance near the door, tries the door interaction before movement, waits briefly for the door to open, then forces a single-step auto-walk attempt and writes `BepInEx\plugins\door_transition_sweep.live.json`
- `Ctrl+Alt+Shift+F1` now runs a separate dev-only live route audit that teleports to one canonical representative start position per source graph zone, launches normal room-target live navigation for each ordered room pair, and writes `BepInEx\plugins\live_route_audit.live.json`
- unlike the transition sweeps, the live route audit does not force one authored step or branch into sweep traversal logic; it reuses normal `BeginNavigation(...)`, path refresh, and auto-walk end to end so the report reflects actual room-to-room behavior
- the live route audit stores the canonical planned route signature for each ordered pair, carries same-build `passed` results forward from the previous `live_route_audit.live.json`, and only queues the remaining unresolved route groups on the next rerun so repeated audits do not restart from zero every time
- the current live route audit is still room-level rather than component-complete: it picks one representative walkable start position per normalized source graph zone, so a passing route there is evidence that the canonical room path works, not yet proof that every disconnected component inside that same normalized zone is reachable
- For door sweeps, `AccessibilityWatcher` now retries the first interaction from the opposite lateral side when the initial stance fails, scores door candidates against the full `FromWaypoint -> ToWaypoint` route instead of only source proximity, and after a successful interaction commits to a short destination-side push-through target so movement does not fall back to the source-side waypoint immediately after the door opens
- For both normal door traversal and door sweeps, the pre-interaction `DoorInteractionRetry` target now prefers the source-side threshold anchor from `TryGetDoorThresholdAdvanceTarget(...)`, then a snapped `ConnectorObjectPosition`, then `FromWaypoint`, then the player position; the debug log now records that choice as `retryTargetSource=threshold`, `connector`, `from_waypoint`, or `player`
- For door sweeps, `AccessibilityWatcher` now also treats `door already open` and `sliding door already open` as valid ready-to-traverse states and immediately uses the destination-side push-through target instead of falling back to a dead-end interaction wait on an already-open door
- If a door sweep step times out while the normal auto-walk recovery path can still salvage it, `AccessibilityWatcher` now gives that step one last standard `TryRecoverAutoWalk(...)` pass before recording the sweep failure; this is intended to stop routes like `office -> office_closet` from being marked failed one frame before their interaction recovery fires
- Each sweep entry now includes `StepIndex`, `TransitionKind`, and `StatusDetail` so repo-side analysis can distinguish transition classes without inferring from raw log lines
- `AccessibilityWatcher` now also carries a directed open-passage override table for the worst confirmed runtime failures; those overrides can bias destination approach targeting deeper into the destination side without regenerating the whole graph
- Sweep reruns now read the existing `BepInEx\plugins\transition_sweep.live.json` file with a simple manual JSON line parser and skip any transition whose last recorded status was `passed`, so repeated sweeps focus on unresolved failures without relying on `JsonUtility` deserialization
- `TransitionSweepReporter.LoadEntryStatuses(...)` now exposes saved per-step `Status`, `StatusDetail`, and `FailureReason` so other diagnostics can distinguish `passed`, `failed`, and `unverified` transitions instead of only consulting the passed-key cache
- Door-sweep reruns do the same against `BepInEx\plugins\door_transition_sweep.live.json`, so already-passed door links are skipped on the next pass
- `BuildNavigationStepKey(...)` now prefers the graph step's stable `Id`, which keeps door, open-passage, stairs, and teleporter evidence aligned across sweep reports, live navigation, and selected-object coverage analysis
- The generic transition sweep now admits `OpenPassage`, `Stairs`, and `Teleporter` steps; the dedicated door sweep remains separate because door preparation still needs its own interaction setup
- The generic sweep now intentionally excludes the `office <-> crawlspace` `CrawlspaceLadder` teleporter in both directions because the ladder cutscene interrupts the forced-step harness even though manual verification on `2026-04-10` confirmed that live traversal succeeds
- For the worst confirmed open-passage failures, the watcher can now traverse in explicit segments `FromWaypoint -> FromCrossingAnchor -> ToCrossingAnchor -> ToWaypoint` instead of one long straight-line handoff, which is intended to avoid cutting through bad doorway or stairwell geometry
- Open-passage override data is now externalized in `navigation_transition_overrides.json`, copied into `BepInEx\plugins\`, and loaded at runtime through `DataContractJsonSerializer`; each directed entry can define accepted source/destination subzones, destination approach bias, explicit intermediate waypoints, explicit-crossing mode, and an optional per-transition timeout
- When an override entry provides explicit intermediate waypoints and sets `UseExplicitCrossingSegments` to `false`, the guided open-passage path now treats those override points as the authoritative handoff route instead of always injecting the graph's source-clear, destination-clear, and intermediate anchor points back into the path
- Override-only open-passage routes now also drive local occupancy planning on both the source and destination sides instead of always snapping source-side planning back to the graph's `SourceClearPoint` or destination-side planning back to the blended destination approach; this is specifically meant to let manually tuned threshold routes such as `dining_room -> piano_room` follow walkable-cell-aligned override points instead of fighting the graph anchors
- On the `2026-04-09T01:45:57Z` rerun, the live `dining_room -> piano_room` override-only route and `LocalNavigationMaps` both load successfully, but the player still stalls around `(-18.82, -0.60, -9.23)` after the second manual waypoint; that leaves threshold waypoint placement as the remaining open-passage blocker rather than graph loading or map deserialization
- On the `2026-04-09T01:46:18Z` rerun, `office -> office_closet` still appears as the lone failed door sweep entry, but the matching log shows repeated open-door readiness and `Auto-walk recovery succeeded via interaction` immediately after the sweep writes the timeout, so the next fix there should target sweep timeout or failure recording before more connector retuning
- Door sweeps now get one bounded final post-timeout recovery grace after the normal timeout recovery has already been used; if that last recovery succeeds, the sweep keeps running briefly instead of writing a failure immediately and letting normal auto-walk succeed only after the report is already wrong
- On the `2026-04-09T03:33:28Z` rerun, the imported open-passage entry for `dining_room -> piano_room` is back on `ValidationTimeoutSeconds = 5`, and the log shows `AdvanceCommittedOpenPassageTarget(...)` advancing the guided override from waypoint `4 of 6` to `6 of 6` while the player remains stalled around `(-18.82, -0.60, -9.23)`; the next fix should stop timeout recovery from auto-advancing unreached manual waypoints before any more threshold retuning
- On the `2026-04-09T03:33:48Z` door rerun, `office -> office_closet` never leaves `DoorThresholdAdvance`: the sweep keeps targeting source clear `(4.84, -0.62, 25.98)` while the player stalls near `(5.52, -0.60, 25.38)`, and local-navigation-map sampling puts the nearest walkable office cell around `(5.66, 25.64)` while the current push-through target `(3.24, -0.62, 25.90)` is about `2.43` units off the office walkable set; the next fix should retarget that threshold handoff onto walkable cells or relax `ShouldKeepDoorThresholdAdvance(...)` once progress has flattened out
- Latest local follow-up on 2026-04-09: guided open-passage timeout recovery now preserves the current manual override waypoint unless the player is actually within `OpenPassageGuidedWaypointAdvanceDistance` of it, so recovery no longer burns through unreached manual threshold points just because the timeout path fired
- Latest local follow-up on 2026-04-09: door threshold advance now snaps its source-side threshold target onto the nearest nearby walkable source-zone cell before deciding whether to keep threshold advance or switch to push-through, and once that snapped source target is effectively reached it allows push-through instead of holding the older stricter threshold-progress gate forever
- Latest local follow-up on 2026-04-09: override-only open-passage local planning now uses the tighter `0.75f` goal-reached cutoff that was already reserved for door push-through, so off-zone manual waypoints such as `dining_room -> piano_room` keep source-side occupancy steering active until the player is actually close to the snapped in-zone threshold cell instead of falling back to the raw cross-boundary target as soon as that snap drops under `2f`
- Latest local follow-up on 2026-04-09: active door push-through targets in the source zone can now reuse local occupancy planning instead of hard-bypassing it, so source-side steering can carry routes such as `office -> office_closet` onto the last walkable office cell before the raw push-through target takes over again
- Optional open-passage door recovery is now candidate-driven instead of exact-source-zone-driven once a route-adjacent live door has already been resolved during a forced sweep; this avoids the earlier `shouldAttempt=false` skip when the player drifts into a sibling runtime zone like `hallway6` or `office5` before the retry fires
- `.\scripts\Import-TransitionSweepReport.ps1` now imports that live sweep report into `artifacts\navigation\transition_sweep.live.json`, replaces the repo-side artifact with the latest imported report instead of preserving older entries, writes `artifacts\navigation\transition_sweep.summary.txt`, and refreshes `navigation_transition_overrides.json` from failed entries that include stalled player positions
- `.\scripts\Import-DoorTransitionSweepReport.ps1` now imports the live door sweep report into `artifacts\navigation\door_transition_sweep.live.json`, replaces the repo-side artifact with the latest imported report instead of preserving older entries, and writes `artifacts\navigation\door_transition_sweep.summary.txt` with grouped remaining-failure details
- On the `2026-04-09T23:44:23` open rerun, the raw game-side `transition_sweep.live.json` is `IsComplete = false` with `84` queued steps, `6` passes, `3` failures, and `75` pending because the door sweep was started immediately afterward; the log shows `Door transition sweep started. steps=22 skippedPreviouslyPassed=21` right after the `crawlspace -> office` teleporter failure, so merged repo totals must not be treated as one complete current rerun
- At that point repo-side imports still preserved legacy coordinate-based keys alongside stable `transition:` ids, so merged sweep artifacts could show an older pass and a newer fail for the same logical directed link; this affected routes such as `bathroom1 -> hallway`, `bathroom2 -> bedroom`, `bathroom2 -> dorian_bathroom2_1`, `bedroom -> dorian_bedroom1`, `hallway -> office`, `office -> hallway`, `office -> office_closet`, `upper_hallway -> gym`, and `dining_room -> piano_room`
- The latest raw open blockers before that interruption are `bathroom2 -> dorian_bathroom2_1`, `bedroom -> dorian_bedroom1`, and `crawlspace -> office`; the first two time out while override-driven source handoff keeps reusing the same local goal, and the teleporter still fails as `navigation unavailable playerState=CantControl`
- The `2026-04-09T23:48:00` raw door rerun is complete at `22` total with `13` passed and `9` failed on stable keys: `bathroom1 -> hallway`, `bathroom2 -> bedroom`, `bathroom2 -> dorian_bathroom2_2`, `bedroom -> bathroom2`, `hallway -> bathroom1`, `hallway -> office`, `office -> hallway`, `office -> office_closet`, and `upper_hallway -> gym`; the merged repo artifact reports `10` failures only because it also preserves one older coordinate-key `office -> office_closet` failure
- The first selected-object coverage export currently writes only the top-level `ReportKind`, `OverallStatus`, `FailureReason`, and `Limitations` fields even though `BuildSelectedObjectCoverageReport()` populates `SelectedObject`, `Summary`, `TrackerAlignment`, and `Entries`; treat `selected_object_coverage.live.json` as malformed until that writer path is fixed
- Latest local follow-up on 2026-04-10: transition sweeps now treat `playerState=CantControl` as an expected temporary lock during `Teleporter` steps instead of immediately recording `navigation unavailable`, so teleporter routes such as `crawlspace -> office` should wait for control to return and only fail on a real timeout
- Latest local follow-up on 2026-04-10: selected-object coverage now force-seeds `transition:crawlspace->office` and `transition:office->crawlspace` as `passed` with a manual-verification detail because those two ladder-cutscene steps are intentionally excluded from the automated sweep until a cutscene-aware completion rule exists
- Latest local follow-up on 2026-04-10: `SelectedObjectCoverageReporter` no longer uses `JsonUtility.ToJson`; it now writes `selected_object_coverage.live.json` through a manual serializer so the full nested report (`SelectedObject`, `Summary`, `TrackerAlignment`, `Entries`, `PathSteps`) survives to disk for repo-side inspection
- Latest local follow-up on 2026-04-10: `LocalNavigationMaps.TryResolveApproachTargetForComponent(...)` now constrains selected-object approach candidates to one connected walkable component and scores them by path distance first, then reference distance; coverage proof uses the start component for same-zone cases and the interactable's snapped component for cross-zone cases before falling back to the older broad resolver
- Latest local follow-up on 2026-04-10: `ShouldKeepDoorThresholdAdvance(...)` now requires roughly `75%` forward progress through the source-threshold-to-push-through segment before the watcher abandons the snapped source-side handoff, which is intended to keep `DoorThresholdHandoff` alive longer on the remaining post-interaction door failures
- Latest local follow-up on 2026-04-10: refreshed `navigation_transition_overrides.json` entries now rely on `AcceptedSourceZones` runtime subzones such as `bathroom2_3`, `hallway4`, and `living_room2`, so override-driven routes can survive live subzone drift instead of matching only the normalized graph zone
- Latest repo-side review on 2026-04-10 after the `17:17` to `17:19` reruns: the unresolved-only open report now contains only `dining_room -> piano_room`, `hallway -> hallway_arma`, and `living_room -> hallway`, but importing that report auto-refreshed the workspace `navigation_transition_overrides.json` back to shallow `UseExplicitCrossingSegments = true` / `StepTimeoutSeconds = 5.0` entries for those routes. Restore the intended override-only entries before the next build or the fresh report will keep targeting the same threshold-anchor cells.
- Latest local follow-up on 2026-04-10: `Update-OpenPassageOverridesFromReport(...)` now preserves curated manual guided overrides whenever an entry already has multiple intermediate waypoints or already runs in override-only mode with `UseExplicitCrossingSegments = false`, so importing an unresolved-only sweep no longer downgrades routes such as `dining_room -> piano_room`, `hallway -> hallway_arma`, or `living_room -> hallway` back to shallow explicit-segment defaults.
- Latest local follow-up on 2026-04-10: the root cause of the remaining importer rewrite bug was dictionary-backed override entries. `Update-OpenPassageOverridesFromReport(...)` copies overrides into ordered dictionaries, but `Get-JsonPropertyValue(...)` originally only read PSObject properties, so it missed dictionary `IntermediateWaypoints` and `UseExplicitCrossingSegments` values and treated curated entries as empty/default during refresh. `Get-JsonPropertyValue(...)` now also resolves dictionary keys case-insensitively, which lets the preservation guard see the real override data.
- The same review narrows the remaining door cluster to post-interaction source-side timeouts only. `BuildDoorThresholdHandoffPosition(...)` can offset the handoff target by up to `DoorPushThroughSourceAdvanceDistance = 1f` forward plus `DoorTransitionSweepDoorLateralOffsetDistance = 0.6f` sideways, but `TryResolveDoorLocalNavigationGoal(...)` still only treats that desired point as `door-threshold-handoff` when it stays within the same `1f` radius from the threshold target. That gate mismatch can still drop local steering for the very handoff target the watcher just generated.
- Latest local follow-up on 2026-04-10: `TryResolveDoorLocalNavigationGoal(...)` now explicitly recognizes the computed `BuildDoorThresholdHandoffPosition(...)` target itself as a valid `door-threshold-handoff` local goal, using the tighter `door-threshold-handoff` arrival cutoff instead of only the raw threshold radius check. This should keep local steering active for the four current post-interaction handoff failures instead of repeating `Local navigation skipped` until timeout.
- The same review also exposes an independent selected-object coverage bug beyond the remaining sweep blockers. Cross-zone coverage still calls `LocalNavigationMaps.TryResolveApproachTargetForComponent(...)` with the off-zone start position as the pathfinding origin inside the target zone, which explains the new `Selected object approach target fell back to the raw object position` cluster. Even after that is fixed, coarse normalized zones such as `hallway` and `upper_hallway` still need component-aware same-zone proof or finer splitting because several disconnected components are currently treated as one room.
- Latest local follow-up on 2026-04-10: `TryResolveSelectedObjectCoverageApproachTargetForStartComponent(...)` now uses the snapped target-zone reference position as the evaluation start when cross-zone coverage resolves an `object-component` approach target, and it no longer forces the resolved target back onto the start component's `y`. This should remove the separate raw-object fallback cluster that was caused by solving target-zone local paths from remote source-component positions.
- On the fresh `2026-04-10` midday imports, the merged open sweep is now complete at `164` total, `156` passed, `8` failed, and `0` pending; the remaining logical blockers are `bathroom2 -> dorian_bathroom2_1`, `bedroom -> dorian_bedroom1`, `crawlspace -> office`, `dining_room -> piano_room`, `hallway -> hallway_arma`, `living_room -> hallway`, and `office -> crawlspace`, with one preserved legacy-coordinate duplicate still attached to `dining_room -> piano_room`
- User follow-up after those imports: the generic transition sweep had to be run three times to finish the full report, and the ladder-climb cutscene visibly played in both `crawlspace -> office` and `office -> crawlspace`. Treat those two teleporter failures in the imported open-sweep artifact as sweep false negatives caused by the cutscene interrupting the forced-step harness, not as live traversal blockers
- On the same imports, the merged door sweep is `44` total, `37` passed, `7` failed, and `0` pending; the remaining logical blockers are `bedroom -> bathroom2`, `hallway -> bathroom1`, `hallway -> office`, `office -> hallway`, `office -> office_closet`, and `upper_hallway -> gym`, with one preserved legacy-coordinate duplicate still attached to `office -> office_closet`
- The first actionable selected-object coverage export on `2026-04-10` tracked `Magnifying Glass` in runtime zone `hallway6` and shows the tracker itself is aligned: selected-object resolution passed, approach snapping passed, and `TrackerAlignment` reports `NavigationActive = true`, `TrackerActive = true`, `TargetKind = LocalWaypoint`, and `TargetDelta = 0`
- The same coverage report shows the remaining tracked-object guidance failures are mostly route-proof or local-map failures rather than tracker mismatch:
  - failed transition proof is concentrated on `transition:living_room -> hallway` (`25` failing components), `transition:office -> hallway` (`15`), and the currently imported but user-disputed `transition:crawlspace -> office` (`3`)
  - destination-leg local-map failure is concentrated on `NoPath zone=hallway` (`30` failing components), which means many routes can reach normalized zone `hallway` but still cannot prove the final leg to the tracked target's chosen arrival cell
  - same-zone or destination-leg `NoPath` failures also remain in `bedroom` (`7`), `upper_hallway` (`4`), `gym` (`2`), `gym_closet` (`2`), `attic` (`1`), and `laundry_room` (`1`), so component-aware arrival targeting is still missing in several multi-component zones
- Re-reading `D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\LogOutput.log` after those imports narrows the remaining root causes:
  - raw sweep evidence still writes teleporter-side failures for `crawlspace -> office` and `office -> crawlspace`, but user verification on `2026-04-10` confirmed that both directions actually play the ladder-climb cutscene and succeed in practice; treat the current failure rows as harness false negatives until the sweep can recognize cutscene completion instead of timeout
  - `dining_room -> piano_room` and `living_room -> hallway` both keep source-side local navigation active, but stall roughly `1.13` to `1.17` units from the snapped source-handoff goal until recovery is exhausted; at this point the blocker is the authored threshold target geometry itself, not another timeout-only problem
  - `bathroom2 -> dorian_bathroom2_1`, `bedroom -> dorian_bedroom1`, and `hallway -> hallway_arma` all succeed on the source side and then immediately drop local navigation when the step advances to an off-zone destination or `EntryWaypoint`; these look more like door-camera or vertical-connector modeling problems than ordinary open-passage tuning
  - the remaining door cluster (`bedroom -> bathroom2`, `hallway -> bathroom1`, `hallway -> office`, `office -> hallway`, `office -> office_closet`, and `upper_hallway -> gym`) all still fail after interaction succeeds because `DoorThresholdAdvance` or `DoorThresholdHandoff` emits a raw `ZoneFallback` target and source-zone local planning drops out before the last walkable threshold cell is cleared
- Fresh repo-side review on 2026-04-10 after importing the `22:15:57` open sweep, `22:17:52` door sweep, and `22:18:04` selected-object export:
  - the open sweep is still the same stable `3`-route set, and the importer no longer mutates the curated override-only entries while doing that import (`preservedManualWaypoints=1`, `refreshed=0`)
  - `dining_room -> piano_room` is still a source-side threshold-geometry blocker: the log keeps `open-passage-override-source` local planning active, advances cleanly through the first two manual points, and then stalls at the third manual source-side point around `(-17.90, 4.20, -9.88)` with `remainingDistance` holding at about `1.13`
  - `living_room -> hallway` is also still a source-side threshold-geometry blocker, but its shape differs slightly: the override goal keeps sliding forward from roughly `(6.60, 4.67, -11.07)` toward `(6.95, 4.67, -8.62)` while the resolved local waypoint stays pinned near `x=5.10`, so the source-side local path never commits into the intended threshold lane before timeout
  - `hallway -> hallway_arma` no longer looks like another ordinary open-passage override retune; the log repeatedly promotes the final override point into an elevated `EntryWaypoint` around `(6.7, 9.95, -3.6)` while the player remains in `hallway4` around `(9.32, 2.87, -3.45)`, which points back to graph or connector modeling for a vertical link rather than simple source-threshold waypoint tuning
  - the fresh door failures now split into two code-path classes instead of one. `bedroom -> bathroom2`, `hallway -> bathroom1`, `hallway -> office`, `office -> office_closet`, and `upper_hallway -> gym` still enter `DoorThresholdHandoff`, build a raw `ZoneFallback` handoff target, and then immediately repeat `Local navigation skipped` until timeout even after the snapped source-threshold cell has been reached closely enough to start the handoff
  - `office -> hallway` is the current outlier: it no longer drops local planning outright, but it still times out inside `DoorThresholdAdvance` with source-side local planning active and about `1.69` units of office-threshold progress still missing, so that route needs a separate source-threshold progress or retarget adjustment after the shared handoff-skip bug is fixed
  - the current `selected_object_coverage.live.json` should not treat `TrackerAlignment` inactivity as a regression in the export writer or tracker snapshot logic. The same runtime log shows active navigation toward `Magnifying Glass` via `upper_hallway -> hallway:Stairs`, then `Auto-walk hotkey detected` immediately followed by `StopNavigationRuntime ... autoWalk=True` just before `Selected object coverage export completed`, so the inactive tracker snapshot is expected for this particular export
  - that export still leaves one independent selected-object approach bug after the transition blockers are accounted for: `25` start components still fall back to `raw-object` because the preferred object-component snap is being chosen from a far off-walkable bounds reference in `hallway`, and `TryResolveApproachTargetForComponent(...)` then finds no candidate in that preferred component even though ordinary candidate building succeeded
  - the remaining non-transition coverage failures are now the same-zone local-map `NoPath` cluster in normalized `hallway`, `bedroom`, `upper_hallway`, `gym`, `gym_closet`, and `laundry_room`; those should be revisited only after the `living_room -> hallway` and `office -> hallway` route-proof failures are cleared so downstream noise is reduced first
- Follow-up code read on the same 2026-04-10 late-night imports:
  - `living_room -> hallway` is no longer best described as only "threshold geometry". The log stays on `overrideWaypoint=1 of 3` while `BuildOpenPassageGuidedMovementTarget(...)` keeps advancing the raw source-side goal, and `TryResolveOpenPassageLocalNavigationGoal(...)` keeps replanning against that moving progressive point instead of the actual guided waypoint; the next fix there should anchor `open-passage-override-source` local planning to the real guided waypoint before trying more manual retunes
  - `hallway -> hallway_arma` is still a vertical-connector modeling problem, not another ordinary override-only route. `artifacts\navigation\transition_validation.static.json` still flags `LargeHeightDelta` (`5.6`) and `WideCrossingGap`, and the second override point is already off-zone at `y = 9.952` while the player never leaves `hallway4`
  - the remaining `Magnifying Glass` raw-object fallback rows now point to target-zone normalization, not the earlier cross-zone evaluation-start bug. Normalizing runtime zone `hallway6` down to coarse `hallway` snaps the preferred object component about `28.58` units onto the wrong hallway component, so `TryResolveApproachTargetForComponent(...)` still fails even though the report is now using the snapped target-zone reference position as its evaluation start
- Latest local follow-up on 2026-04-11: the first actionable clue in the imported `2026-04-11` open sweep was a runtime `Failed to load navigation transition overrides` error before `dining_room -> piano_room` and `living_room -> hallway` failed. The immediate cause was single-item `AcceptedSourceZones` or `AcceptedDestinationZones` values being serialized as scalar strings instead of arrays. `AccessibilityWatcher` now normalizes those scalar accepted-zone properties before `DataContractJsonSerializer` reads `navigation_transition_overrides.json`, and `scripts\SweepReportTools.ps1` now rewrites those properties back out as proper string arrays during import.
- Latest local follow-up on 2026-04-11: `TryResolveOpenPassageLocalNavigationGoal(...)` no longer reuses the sliding progressive desired point for override-only local planning. It now asks `TryGetOpenPassageOverridePlanningGoal(...)` for the currently guided override waypoint and uses that on both the source and destination legs, which is the direct fix for the `living_room -> hallway` lane-commit bug exposed by the late-night rerun.
- Latest local follow-up on 2026-04-11 after importing the authoritative `23:42:32` / `23:46:32` full reruns from runtime build `44EEC894...`: the widened failure set (`10` open passages, `9` doors) showed a second open-passage bug beyond the earlier `living_room -> hallway` lane-commit issue. Several override-only source handoffs were still planning local movement against an off-zone final checkpoint even while the live movement target had already been reduced to a nearer progressive subtarget, which let source-side local planning collapse too early on routes such as `bathroom2 -> dorian_bathroom2_1`, `bedroom -> dorian_bedroom1`, `living_room -> dining_room`, and `living_room_tutorial -> frontdoor`. `TryGetOpenPassageOverridePlanningGoal(...)` now returns that same progressive guided subtarget so local steering and live movement stay aligned during override-only source handoff.
- Latest local follow-up on 2026-04-11 from the same full-rerun review: post-interaction door failures were not all the same old `Local navigation skipped` bug anymore. The current shared blocker was that a snapped `DoorThresholdHandoff` bridge could still collapse back to a point with effectively no forward progress away from the source threshold, which made the watcher time out on a dead proxy even after interaction succeeded. `TryGetDoorThresholdHandoffTarget(...)` now reuses source-side snapping and discards any snapped handoff target that does not meaningfully advance toward the push-through point, allowing traversal to stay on threshold advance or move on to push-through instead of looping on a useless bridge. `hallway -> hallway_arma` still remains separate graph or local-map modeling debt rather than another handoff bug.
- Latest local follow-up on 2026-04-11: `TryResolveSelectedObjectCoverageApproachTargetForStartComponent(...)` now only hard-prefers the interactable's snapped object component when that snapped reference is close enough to the target-side candidate reference or when the zone has only one walkable component. In multi-component cross-zone cases it now re-evaluates from the target-side reference position and auto-selects a reachable component instead of locking onto a far disconnected `hallway` component.
- Latest local follow-up on 2026-04-11: exported sweep and selected-object coverage reports are now self-identifying. They include the loaded plugin build stamp derived from the running DLL timestamp plus SHA-256, the active override-file load status and hash, and transition-sweep result rows now preserve the final zone, player position, last target kind or position, and last local-navigation context. The import summary script also prints the runtime build and override-load status so a single rerun can confirm whether a failure came from stale deployment, override-load failure, or a real pathing problem.
- Latest local follow-up on 2026-04-11 from the same selected-object coverage import: tracked-object auto-walk was still collapsing sibling runtime subzones such as `hallway6` into a direct-object target too early. `TryGetNextNavigationPosition(...)` now requires an exact runtime-zone match before it switches to `DirectObject`; equivalent sibling zones first steer to the tracked subzone anchor. Once the player is in the exact tracked runtime zone, `TryGetTrackedInteractableNavigationTarget(...)` now prefers the player's current connected local-navigation component when the normalized map exposes multiple disconnected components, which should stop coarse zones such as `hallway` from reusing an unreachable candidate on the wrong component.
- Latest local follow-up on 2026-04-11 after importing the later `22:39:49` / `22:42:17` / `22:42:59` runtime artifacts from build `56638BB64C46C3F2F7ADB484E09245239E068074D4CA68843764BB57C0ECF78D`: the blocker set dropped to `8` open-passage failures and `4` door failures, but the remaining generic code split changed again. Override-guided open passages were still judged against the generic raw-target `AutoWalkArrivalDistance = 2f` once local steering dropped out, which let routes such as `bathroom2 -> dorian_bathroom2_1`, `bedroom -> dorian_bedroom1`, `dining_room -> piano_room`, and `living_room -> hallway` refresh before the current guided waypoint was actually reached. The same log review also showed that post-interaction door source-zone steering was now timing out on snapped source-side proxy goals about `0.84` to `1.22` units away because `TryResolveDoorLocalNavigationGoal(...)` still held those snapped local goals to the tighter beyond-door `0.35f` cutoff. `TryGetNextNavigationPosition(...)` now tags override-guided raw targets so auto-walk keeps the stage-appropriate guided arrival radius, and `TryResolveDoorLocalNavigationGoal(...)` now uses `door-threshold-handoff-local` / `door-push-through-local` clearance-distance completion for snapped source-side proxy goals before handing back to the raw door target.
- Latest local follow-up on 2026-04-16 from the same `Magnifying Glass` coverage blocker: the export-side proof path was still broader than live navigation even after the exact runtime-zone `DirectObject` fix landed. `TryResolveSelectedObjectCoverageApproachTargetForStartComponent(...)` could still auto-select whichever snapped target-zone component was easiest to reach inside coarse multi-component zones such as `hallway`, which let same-zone coverage pass on the wrong disconnected component and made cross-zone destination proofs drift away from the actual object component. Coverage export now prefers the interactable's snapped object component whenever it can be resolved, keeps that exact component even in multi-component zones, uses the start component only as a fallback when the object component itself cannot be identified, and no longer rewrites raw fallback targets onto the remote start-zone `y`. Fresh reruns from build `469F249234CBA0C88B75F8F62D9CD63FB7DF6C65FD0705733DA0DC931081FEBB` should now show whether the remaining `Magnifying Glass` failures are real transition debt plus true target-component `NoPath` cases instead of exporter-side component drift.
- Latest local follow-up on 2026-04-16 after importing the authoritative Apr 16 live reruns into the repo: the current blocker baseline is `82 total / 75 passed / 7 failed / 0 pending` for open passages and `22 total / 20 passed / 2 failed / 0 pending` for doors from runtime build `469F249234CBA0C88B75F8F62D9CD63FB7DF6C65FD0705733DA0DC931081FEBB`. The imported `selected_object_coverage.live.json` from the same date is not a valid post-sweep proof file because it was written at `18:05:26Z`, before the open sweep (`18:07:36Z`) and door sweep (`18:09:31Z`), so most of its `PathSteps` are still stale `unverified` evidence rather than a coverage-reader bug.
- Latest local follow-up on 2026-04-16 from the `dorian_trapdoor1 -> office` live failure: `Transition sweep accepting sibling source zone currentZone=office_closet expectedZone=dorian_trapdoor1` was already proving that the sweep harness can legitimately start this step from the closet-side sibling zone, but `TryResolveOpenPassageLocalNavigationGoal(...)` still only treated strict source zones as valid for open-passage local steering. The open-passage planner now accepts override-declared sibling source zones there as well, and the deployed `navigation_transition_overrides.json` explicitly marks `office_closet` as an accepted source zone for `dorian_trapdoor1 -> office`.
- Latest local follow-up on 2026-04-16 from the remaining office-door and hallway-connector blockers: office door handoff or push-through proxies now have to show meaningful threshold clearance, not merely a tiny nonzero forward vector, before either raw traversal or source-side local steering will keep them alive. Separately, `scripts\Build-NavigationGraph.ps1` now classifies the `hallway <-> hallway_arma` pair as `Stairs` instead of `OpenPassage`, and the regenerated graph plus static validation now treat that pair as a vertical handoff with `LargeHeightDelta` only, not as another flat `WideCrossingGap` doorway.
- `.\scripts\Inspect-NavigationTransitions.ps1` writes `artifacts\navigation\transition_validation.static.json`, which scores every generated transition with static geometry heuristics so suspicious links can be prioritized before runtime testing
- `.\scripts\Inspect-DoorTransitionMetadata.ps1` writes `artifacts\navigation\door_transition_audit.json` plus `artifacts\navigation\door_transition_audit.summary.txt`, comparing door-transition connector points against exported scene `Doors_*` / `AtticDoors` objects
  - latest deployed live audit generated at `2026-04-17T02:28:46Z` against `D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\navigation_graph.json`: `11` door pairs total, `0` suspicious
  - the former closet blockers now use explicit door-object positions plus `ConnectorNames` arrays:
    - `bedroom <-> bedroom_closet` now targets `Doors_Bedroom_ClosetRight_Outer` with `Doors_Bedroom_ClosetRight_Inner` as an accepted runtime variant
    - `gym <-> gym_closet` now targets `Doors_Gym_ClosetOuter` with `Doors_Gym_ClosetInner` as an accepted runtime variant
  - practical consequence:
    - closet-door metadata is no longer the known graph blocker
    - the next question is runtime verification, not whether the generated connector points still fall back to camera midpoints
- `ObjectTracker` beeps now follow the tracked object or current waypoint chosen by the watcher, use stereo panning for left or right guidance, map pitch to the target's vertical position in the camera frame with a stable center pitch near mid-screen, map beep rate to target proximity, and raise volume as the player gets closer
  - practical consequence:
    - the navigation graph currently uses coarse authored zones such as `office`
    - live `CameraSpaces.PlayerZone()` can return finer runtime subzones such as `office2`, `office5`, and `office6`
    - tracker and auto-walk code therefore need to normalize runtime subzones onto graph zones for pathfinding and also treat same-family subzones as the same room for direct-object guidance
  - `NavigationGraph.GetAllZones()` now exposes the normalized graph-zone list directly, and `LocalNavigationMaps.GetWalkableComponents(...)` plus `TryGetWalkableComponentId(...)` expose cached connected walkable regions per zone so navigation proof can reason about "from anywhere in the house" instead of only from one sample point
  - `ObjectTracker.TryGetCurrentTargetState(...)` plus the watcher's `Tone target set` and `Movement target resolved` debug snapshots now make it possible to compare the live tone target against the actual movement target chosen after local-path retargeting
- Generic interactable placement data
  - many serialized `Interactable.interactedPosition` values in the scene export are still `{x: 0, y: 0, z: 0}`
  - practical consequence:
    - `interactedPosition` is not a reliable generic source for doorway waypoints in this scene

Important consequence:

- Inter-room navigation can now use authored or inferred connector waypoints from the generated JSON instead of only zeroed placeholders.
- The generated graph now carries enough metadata for runtime code to distinguish open passages, doors, stairs, and the crawlspace teleporter without hardcoded room-pair logic in the watcher.
- In-room guidance may still benefit from live `triggerzone.Position` or runtime refinement, but open-passage threshold selection is now coming from scene-derived `CameraSpaces` geometry rather than from coarse-room fallback guesses.
- Asset data is sufficient to build and regenerate the current waypoint graph from local tooling.
- The repo now also has a live runtime export path for the actual Unity navmesh, so transition anchors can be checked against the walkable surface instead of only against scene-geometry heuristics.
- The current asset-side evidence says `ThirdPersonGreybox` does not expose a baked `NavMeshData` link, so "extract the baked navmesh from assets" is not presently a viable path for this scene.

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
- Accessibility tracker labels and current-room object lists should prefer stable noun-style object labels.
- If `mainText` or `AlternateInteractions[0].Name` looks like an imperative action prompt such as `Turn on`, prefer the scene object name or internal name instead.
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
- `F6` room report with facing-relative object grouping
- `Ctrl+Shift+F6` whole-house room listing
- `Ctrl+Alt+F6` auto-walk to the tracked object

## Not Yet Analyzed

- Remaining runtime verification for hidden phone bindings that may be populated outside the decompiled code path scan
- Full message log content extraction
- Non-dialogue gameplay notifications such as pickups, relationship changes, or time-of-day transitions

## Latest Tracker Findings

- Tutorial objective resolution can surface a model-child `InteractableObj` instead of the usable root interactable, especially for Maggie and other multi-part dateables. Tracker resolution should canonicalize within the interactable hierarchy before trusting the candidate for zone lookup or navigation.
- `UpdateNavigationState()` should not rebuild the navigation path every poll while the player is still in the current step's `FromZone`. Rebuilding every frame causes repeated tracker restarts and unstable open-passage auto-walk behavior even when the target step has not changed.
- Runtime transit zones such as `stairsdown` can be outside the authored navigation graph. Auto-walk must keep the current graph step alive through those zones and continue toward the step's destination graph zone instead of refreshing the path immediately on entry-waypoint arrival.
- The first representative `Ctrl+Alt+Shift+F1` live route audit on 2026-04-17 showed that repeated live failures were clustering in runtime source subzones such as `upper_hallway8`, `hallway6`, and `piano_room11` while local path planning still preferred the coarser graph zones `upper_hallway`, `hallway`, and `piano_room`.
- Local path planning now prefers the current runtime-compatible zone when it is equivalent to the active graph step zone and both the live start and goal snap onto that zone's local walkable map. The planner falls back to the coarse graph zone only when the runtime-specific map is not usable for that local leg.
- Follow-up on the partial bedroom-origin live route audit from runtime build `AB19617B...`: `TryResolveLocalNavigationGoal(...)` and `ResolveLocalPlanningZone(...)` now test the raw current runtime zone before the normalized graph zone, so subzones such as `bedroom4`, `hallway4`, and `upper_hallway5` can keep their own local map when it is actually usable instead of being collapsed immediately to `bedroom`, `hallway`, or `upper_hallway`.
- Door transitions with authored `ConnectorNames` now treat those names as a hard requirement in both the live interaction search and the shared audit or sweep candidate lookup. Routes such as `bedroom -> bathroom2` and `upper_hallway -> attic` should now fail as "named connector missing" if the correct door is unavailable instead of silently selecting a nearby wrong door.
- The live route audit now treats every attic-family source zone plus `crawlspace` as temporarily excluded starts and emits those ordered room pairs as `skipped` instead of queuing them.
- The live route audit keeps its canonical route-start ordering and now preflights the first required interactable step after teleport so a locked first-step door is recorded as `skipped` for the current save instead of being misreported as a live-navigation failure.
- The live route audit no longer resolves one shared spawn per source graph zone before planning every target from that zone. It now chooses a preferred start position per ordered route from that route's first-step source anchor, then groups representatives by both the final step signature and that resolved spawn. Practical effect: one bad generic hallway start should no longer poison every hallway-origin route that would naturally begin from a different source-side anchor.
- The live route audit now has a separate `7s` no-progress timeout on top of the full route budget. It resets on meaningful movement, graph-step change, or runtime-zone change, and it is suspended during intentional transition waits, so long route budgets still exist for real traversals but obvious hallway stalls should fail much sooner.
- Follow-up on the bathroom-origin route-audit failures from runtime build `CB10D6FC...`: `bathroom1 -> hallway` already passes in the directed transition sweep when the audit starts from the door's stand-clear position near `Doors_Bathroom1`, but the older live route audit was still starting those routes from a generic snapped bathroom anchor. The live route audit now prefers that same door-clearance start for door-first routes before falling back to generic source anchors, so bathroom exits and similar first-door routes should begin from the proved viable side of the first connector.
- Follow-up on the next bathroom-origin rerun from runtime build `D0424117...`: the first door was still failing, but now the log showed the exact loop. After interaction, `bathroom1 -> hallway` was discarding the threshold handoff bridge as intended, then immediately treating the nearby push-through target as "close enough" to skip `DoorPushThrough` and fall into `DoorEntryAdvance` while the player was still in `bathroom1`. Door post-interaction targeting now stays on `DoorPushThrough` until post-threshold commitment is actually earned instead of letting mere proximity to the push-through point jump straight to entry advance.
- Follow-up on the next bathroom-origin rerun from runtime build `514BE7DE...`: the stage fix exposed the remaining source-side short-circuit more clearly. `bathroom1 -> hallway` was now staying in `DoorPushThrough`, but once the door-specific local proxy was correctly discarded for lacking real threshold clearance, auto-walk still treated the raw handoff or push-through point as a generic `2f` arrival target. Raw door `DoorThresholdHandoff` and `DoorPushThrough` targets are now tagged with explicit raw navigation contexts, and auto-walk uses the same tight completion radii for those raw door stages that the local planner already used, so bathroom-side door traversal should no longer mark those post-interaction targets "reached" while still inside `bathroom1`.
- Follow-up on the next bathroom-origin rerun from runtime build `7ABF9D0E...`: the tightened raw door arrival distance removed one short-circuit, but the route still was not leaving `bathroom1`. The log showed two remaining conflicts in the post-interaction door flow: `DoorThresholdAdvance` was still allowed to hand off too early while the player remained almost a full unit short of the snapped source-threshold cell, and once `door-push-through-local` was intentionally rejected for lacking real clearance, `TryResolveLocalNavigationGoal(...)` still fell through into generic `step-source` or `current-zone` local planning for that same raw push-through leg. Door post-interaction traversal now keeps `DoorThresholdAdvance` active until the source threshold is actually reached closely enough for a real commit, tags that raw threshold-advance target with its own tight arrival context, and suppresses generic local-planning fallback for raw `DoorThresholdHandoff` and `DoorPushThrough` targets so those stages stay on the canonical raw door target instead of being rewrapped into generic source-zone planning.
- Follow-up on the next bathroom-origin rerun from runtime build `F45E1D77...`: the threshold-advance stay-on-stage fix was active, but the route still stalled in `bathroom1` because the source-threshold leg was still using the wrong local-planning rules. `TryResolveDoorLocalNavigationGoal(...)` was treating `DoorThresholdAdvance` as if it were already a post-threshold clearance stage and was therefore routing it through `door-threshold-handoff-local`, where `TryGetDoorSourceLocalPlanningGoal(...)` correctly rejects any target that does not meaningfully clear the doorway. That discarded the only usable local path to the snapped bathroom-side threshold cell and left raw movement trying to finish the source-side threshold leg alone. Threshold advance now uses its own `door-threshold-advance-local` planning context with a tight source-threshold completion radius, and the meaningful-clearance rejection remains reserved for the actual handoff and push-through local proxy stages.
- Follow-up on the next bathroom-origin rerun from runtime build `48D863F0...`: the new `door-threshold-advance-local` planning path was active, but it revealed one more pre-interaction conflict. The player was now being steered toward the snapped bathroom-side threshold cell correctly, yet once already in range to operate the door, local threshold planning kept returning the same tight `LocalWaypoint` and prevented the raw `TransitionInteractable` target from taking over, so the interaction never fired. `TryResolveLocalNavigationGoal(...)` now skips local planning for door `TransitionInteractable` targets once a matching interactable is already within live interaction range, allowing auto-walk to hand back to the canonical raw door interaction path instead of overcommitting to the last threshold waypoint.
- Follow-up on the next bathroom-origin rerun from runtime build `36195EF2...`: the pre-interaction handoff fix was active and `Doors_Bathroom1` now fires correctly, but the watcher still stalled in `bathroom1` immediately afterward. The remaining blocker was in the local-path lookahead itself: `door-threshold-advance-local` was finding a valid source-threshold path, but its `3f` lookahead kept promoting the final snapped threshold cell as the live `LocalWaypoint` even while an intermediate point still remained on the path. That let movement cut directly at the final bathroom-side threshold cell and stall roughly `0.88` units short instead of stepping through the remaining path point. Tight threshold-advance local steering now clamps that case so it follows the current remaining path point before promoting the final threshold goal. Deployed hash for this follow-up is `78C46D235A316E24217898C0FAF336F048266FF9A52423DBC55D17FBAE32F8D3`.
- Follow-up on the next bathroom-origin rerun from runtime build `78C46D23...`: the lookahead clamp was active and the route now does take the intermediate threshold waypoint first, but the deeper contradiction remained. The same snapped bridge cell `(1.73, -0.62, 7.09)` that `door-threshold-advance-local` can use as an intermediate source-side waypoint is still rejected as a valid threshold-handoff target because snapping it back onto the source nav cell leaves only about `0.03` forward progress relative to the ideal source-threshold cell `(2.23, -0.62, 7.09)`. That left post-interaction traversal looping on `DoorThresholdAdvance` and insisting on the stricter final threshold cell even when the player was already within about `0.86` of it and not improving further. Door traversal now bypasses repeated threshold-advance insistence in that specific case and hands off to raw push-through once the source threshold is close enough but no meaningful snapped handoff target exists.
- Follow-up on the next bathroom-origin rerun from runtime build `8DA6747F...`: the threshold-advance bypass was active and the route did promote from source-threshold advance to raw `DoorPushThrough`, but that still did not leave `bathroom1`. The remaining contradiction was that the traversal had already proved there was no meaningful snapped handoff target, and the player was already inside the same `DoorPushThroughArrivalDistance` radius the sweep uses as a push-through commit signal, yet live traversal still refused to mark the door as post-threshold committed and kept retrying raw push-through from the source room. Door traversal now treats that exact case as a post-threshold commit and advances to destination-side entry logic instead of looping on raw push-through forever.
- Follow-up on the next bathroom-origin rerun from runtime build `5DC3F4EC...`: the post-threshold commit fix was active and the route no longer looped on raw `DoorPushThrough`. The new failure was the next stage: once still in `bathroom1` but already marked post-threshold committed, live traversal immediately switched to the hallway-side `EntryWaypoint` and local planning skipped that target entirely because there was no door-specific source-room local goal for destination entry. Door traversal now uses a dedicated `door-entry-advance-local` planning context while the player is still in the source room after post-threshold commit, so doorway exits continue to use a source-side local bridge until the runtime zone actually flips.
- Follow-up from the next local bathroom-origin fix on 2026-04-18: `DoorEntryAdvance` was still missing the same explicit raw arrival context used by `DoorThresholdAdvance`, `DoorThresholdHandoff`, and `DoorPushThrough`, so the hallway-side destination target could still complete under the generic `2f` auto-walk radius while the player remained in `bathroom1`. The source-room post-threshold branch could also fall back into generic `step-source` local planning once the dedicated `door-entry-advance-local` bridge said it was close enough. `DoorEntryAdvance` now tags `_rawNavigationTargetContext = "door-entry-advance"` and uses the tight `DoorEntryAdvanceLocalNavigationGoalReachedDistance`, and generic door local fallback is now suppressed for that source-room post-threshold destination target so the bathroom exit stays on the canonical doorway handoff until the runtime zone actually flips.
- Follow-up from the next runtime rerun on 2026-04-18 using deployed hash `CE4E3757...`: the bathroom-origin live route audit still stalled immediately on first-step routes with signature `transition:bathroom1->hallway=>...`, and those failures were still starting from `spawnSource=snapped-route-source-anchor` instead of an explicit door-clearance label. Live route audit route planning now tries the same door-first spawn resolver used by the passing door sweep (`TryGetDoorTransitionSweepSpawnPosition(..., useZoneFallback: false)`) before generic source anchors, so first-door routes can start from `door_door_clearance` when available. The audit monitor also now records stall and timeout failure context before calling `StopNavigationRuntime()`, preserving the live step identifier and blocker detail in the failure reason and report instead of collapsing to `currentStep=<null>` and `detail=<null>` after cleanup.
- Follow-up from the next runtime rerun on 2026-04-18 using deployed hash `C76C9C75...`: the first-door spawn and failure-context fixes are now active (`spawnSource=door_door_clearance`, `currentStep=bathroom1->hallway` preserved), but the route still stalls in `bathroom1` during post-interaction `DoorPushThrough`. The new trace shows repeated `DoorPushThrough` targeting at `(2.15, -0.62, 5.62)` while `door-push-through-local` keeps snapping back to the source-threshold cell and being discarded for insufficient threshold-clearance progress, after which generic local fallback is intentionally suppressed. A new local fallback path now allows `door-push-through-local` to keep a short source-side bridge goal from the handoff builder when strict clearance fails, and the local goal-reached distance for `door-push-through-local` is tightened to the push-through local distance so that bridge actually drives movement before handing back to raw push-through.
- Follow-up from the next local fix on 2026-04-18 after confirming the same `door-push-through-local` no-op loop persisted: when local planning reports `door-push-through-local` goal reached with `remainingDistance=0.00` and the active door step key still matches, the watcher now force-commits post-threshold state (guarded by the same push-through arrival threshold) and advances to entry-stage targeting instead of repeatedly clearing local state and re-emitting the same source-side push-through target forever. Deployed hash for this follow-up is `B84A99841C11E759B8FD010105D77E6B09595E37D8C4C46C8F9877428279EF13`.
- Follow-up from the next runtime rerun on 2026-04-19 using deployed hash `B84A9984...`: post-threshold commit is now active, but `door-entry-advance-local` can still trap the player in the source room by snapping the entry local goal back onto the source threshold cell `(2.23, -0.62, 7.09)` with stable nonzero remaining distance. `TryResolveDoorLocalNavigationGoal(...)` now discards `door-entry-advance-local` goals that do not make real forward progress from the source threshold toward the push-through point (`forwardProgress <= 0.08`) so raw `DoorEntryAdvance` can continue instead of looping source-side local waypoints. Deployed hash for this follow-up is `A163EDC16BF63095C04D2A89E6AFFC8D2131DD1D60ADAF68CE972BF16EC27C2F`.
- Follow-up from local automation work on 2026-04-19: `AccessibilityWatcher` now has an auto-walk loop watchdog that samples recent target signatures (`step`, `zone`, `targetKind`, raw or local context, rounded target position) and raises an explicit `auto-walk loop detected` blocker when a one-signature or two-signature low-movement oscillation persists across the sample window. This gives live audits and sweeps a deterministic loop failure reason instead of only timeout fallout, and it is mirrored by repo-side log tooling in `scripts\Analyze-NavigationLoops.ps1` for fast post-run loop summaries.
- Follow-up from static automation work on 2026-04-19: `scripts\Inspect-NavigationLoopRisks.ps1` now provides a fail-fast code scan for loop-risk regressions (required loop-detector hooks, local-stall bypass hook, warning emission path, and gated door `planningContext` local-return branches). `scripts\Build-Mod.ps1` now runs that scan automatically before `dotnet build`, and `scripts\Deploy-Mod.ps1` passes the same inspection and restore switches through, so loop-risk regressions are caught during build scripting instead of waiting for another full in-game rerun.
- Follow-up from runtime log review on 2026-04-19 (build `A163EDC1...`): bathroom door traversal can still loop in post-interaction `DoorThresholdAdvance` when snapped threshold-handoff targets are repeatedly rejected with very low forward progress (for example `forwardProgress=0.03` on `bathroom1 -> hallway`). The no-handoff threshold-advance bypass gate now allows earlier promotion out of threshold advance once source-threshold distance is within doorway-clearance range and push-through distance is already near-range, reducing repeated source-side threshold loops.
- Follow-up from the next runtime log review on 2026-04-19 (build `B19E2672...`): after threshold-advance bypass started triggering correctly, the bathroom route still stalled because post-interaction `DoorEntryAdvance` can be emitted as `targetKind=EntryWaypoint` while `ShouldSuppressGenericDoorPostInteractionLocalFallback(...)` was only suppressing generic source-room local fallback for `ZoneFallback`. That allowed repeated source-room `step-source` local rewraps for destination `EntryWaypoint` targets (for example `(0.52, 4.17, 0.66)`) instead of staying on the canonical door-entry flow. The suppressor now handles both `ZoneFallback` and `EntryWaypoint` target kinds in this post-interaction door path.
- Follow-up from the runtime rerun on 2026-04-18 (build `5C9600C8...`): the new runtime hash loaded and `bathroom1 -> hallway` no longer regressed to the older `DoorEntryAdvance` source-loop, but the route still stalled in a committed-stage source fallback cycle. The log repeatedly alternates `Holding door push-through target while source zone is unchanged` with `Fallback door source local planning goal ... context=door-push-through-local` and immediate `Local navigation goal reached ... committedDoorPostThreshold=False`, which re-clears local state without advancing out of `bathroom1`. `TryResolveDoorLocalNavigationGoal(...)` now only allows `door-push-through-local` planning while post-threshold is not yet committed, so committed door traversal cannot be rewrapped back into source-side push-through local fallback.
- Follow-up from the runtime rerun on 2026-04-19 (build `F409851D...`): the committed-stage `door-push-through-local` loop was gone, but `bathroom1 -> hallway` could still stall in source zone because post-threshold traversal kept re-holding raw `DoorPushThrough` while `door-entry-advance-local` was repeatedly discarded for zero forward progress (`forwardProgress=0.00`) and generic fallback was intentionally suppressed. `TryGetDoorTraversalPostInteractionNavigationTarget(...)` now promotes directly to `DoorEntryAdvance` inside the no-handoff push-through commit window (logged as `Promoting door entry advance after no-handoff push-through commit`) instead of re-holding the same raw push-through target forever while still in the source room.
- Follow-up from the runtime rerun on 2026-04-19 (build `C7345726...`): the `DoorPushThrough` re-hold loop was removed and promotion to entry stage was active, but `bathroom1 -> hallway` still stalled because no-handoff committed traversal could repeatedly target far `EntryWaypoint (0.52, 4.17, 0.66)` in `bathroom1`, with `door-entry-advance-local` correctly discarded for zero forward progress and generic fallback suppressed by design. `TryGetDoorTraversalPostInteractionNavigationTarget(...)` now computes an unsnapped doorway handoff fallback when snapped handoff is unavailable, and while still in the source zone it uses that bridge first (`Using unsnapped door handoff fallback target before entry advance`) before promoting to `DoorEntryAdvance`.
- Follow-up from the runtime rerun on 2026-04-19 (build `48728D3D...`): the unsnapped-bridge branch was active, but `bathroom1 -> hallway` still looped on the same fallback target `fallbackPosition=(1.58, -0.62, 6.13)` with repeated `Local navigation skipped ... localContext=<null>` and eventual loop-detector failures on `rawContext=door-threshold-handoff`. `TryGetDoorTraversalPostInteractionNavigationTarget(...)` now replaces that no-handoff source-zone retry with the nearest destination-side door target (`Using nearest door destination target after no-handoff push-through commit`) so committed post-threshold traversal can move forward without reselecting the collapsed unsnapped handoff fallback.
- Follow-up from the runtime rerun on 2026-04-19 (build `CDF142F3...`): the nearest-destination branch was active, but `bathroom1 -> hallway` still looped in source zone with repeated `destinationPosition=(2.15, -0.62, 5.62)`, `Discarded door entry advance local planning goal due to insufficient source-side progress ... forwardProgress=0.00`, and `Local navigation skipped ... rawTargetPosition=(2.15, -0.62, 5.62)`, then loop-detector failures on `rawContext=door-entry-advance`. `TryResolveDoorLocalNavigationGoal(...)` now falls back to `door-threshold-advance-local` toward the source-threshold cell when that entry-advance local goal is rejected and the player is still meaningfully short of threshold (`Fallback door entry advance local planning goal to source threshold`) instead of returning no local plan.
- Follow-up from local implementation on 2026-04-23: loop detection now activates a shared committed-source door recovery state machine instead of only reporting the loop and retrying generic recovery. `ApplyAutoWalk()` now calls `ActivateDoorCommittedSourceRecoveryFromLoop(...)` when a loop is detected on a committed door step still in the source zone (`targetKind != TransitionInteractable`). Door post-interaction targeting consumes that state first through `TryGetDoorCommittedSourceRecoveryTarget(...)`, forcing a staged `door-threshold-advance` then `door-push-through` retry (`SourceThreshold` -> `PushThrough`) before returning to normal flow. The push-through local planner gate in `TryResolveDoorLocalNavigationGoal(...)` now also allows committed push-through local guidance only while this explicit recovery push-through stage is active. This is designed as a cross-transition fix for committed source-zone door loops, not a `bathroom1 -> hallway` one-off.
- Follow-up from runtime log review and local patch on 2026-04-23 (build `7B638FA3...`): the new shared recovery was activating, but `bathroom1 -> hallway` still looped because repeated loop detections on the same committed source-zone door step stayed in `SourceThreshold` forever (`Door committed-source recovery target stage=SourceThreshold` repeating with `sourceThresholdDistance` around `0.83`) and never promoted to push-through. `ActivateDoorCommittedSourceRecoveryFromLoop(...)` now escalates an already-active same-step recovery from `SourceThreshold` to `PushThrough` on loop re-trigger (`Escalated door committed-source recovery stage=PushThrough due to repeated loop detection`) instead of returning no-op while recovery is active.
- Follow-up from runtime log review and local patch on 2026-04-23 (build `841CE262...`): recovery escalation now reaches `PushThrough`, but the same route still looped because push-through local fallback could be accepted against an unsnapped source anchor, then snap back near the source threshold and instantly complete (`remainingDistance=0.00`) without real forward progress. `TryGetDoorSourceLocalPlanningGoal(...)` now uses `TryGetDoorThresholdAdvanceTarget(...)` as the source baseline for push-through local clearance checks, and `TryResolveDoorPushThroughFallbackLocalGoal(...)` now requires full `HasMeaningfulDoorThresholdClearance(...)` before keeping a push-through fallback local goal. Practical effect: no-progress push-through local fallbacks should now be rejected instead of repeatedly re-emitting source-side local goals while recovery is already in `PushThrough`.
- Follow-up from runtime log review and local patch on 2026-04-23 (build `6C1022E1...`): after the push-through local fallback rejection fix, `bathroom1 -> hallway` still looped by repeatedly emitting raw committed push-through targets (`Door committed-source recovery target stage=PushThrough`) while local fallback was intentionally suppressed and loop detector movement dropped to `0.00`. `TryGetDoorCommittedSourceRecoveryTarget(...)` now promotes recovery directly to entry-advance flow when no snapped handoff is available and push-through distance is already within the no-handoff commit window, logging `Door committed-source recovery promoted to entry advance after no-handoff push-through commit`. This turns the shared committed-source `PushThrough` recovery stage into a bounded handoff rather than an unbounded raw push-through retry loop.
- Follow-up from local implementation on 2026-04-23 (deploy hash `E75F1692...`): added a bounded committed-source watchdog in `ActivateDoorCommittedSourceRecoveryFromLoop(...)` for all door transitions, not only bathroom routes. The watchdog tracks repeated loop-detector trips per committed source-zone door step and, after two trips, forces one controlled interaction retry by clearing post-interaction door traversal state (`_doorTraversalInteractionTriggered`, `_doorTraversalPostThresholdCommitted`, and `_doorTraversalPushThroughPosition`), resetting committed-source recovery state, and clearing local-path carryover so targeting returns to `DoorInteractionRetry`. This is intentionally capped at one forced retry per step (`DoorCommittedSourceWatchdogMaxInteractionRetries = 1`) to avoid introducing a new infinite interaction loop while still breaking the current push-through or entry-advance oscillation class.
- Runtime note on 2026-04-23 at 15:20:58 ET (`2026-04-23T19:20:58Z` metadata stamp): the latest captured `LogOutput.log` segment still reports loaded runtime hash `6C1022E1...`; the `E75F1692...` watchdog markers (`Door committed-source watchdog loop trip`, `Door committed-source watchdog forcing interaction retry`) require a game restart before validation because the previous session had not reloaded the new assembly.
- Follow-up from local implementation on 2026-04-23 (deploy hash `F1F6BCC0...`): added two pre-retry hardening changes for cross-transition post-interaction door loops. In `TryGetDoorCommittedSourceRecoveryTarget(...)`, no-handoff push-through commit now has a recovery-only extra tolerance (`DoorPushThroughRecoveryNoHandoffCommitExtraTolerance = 0.2f`) when the player is already within source-threshold bypass range, so committed-source recovery does not stall on near-threshold distances that are just outside the strict base commit radius. In `TryResolveDoorLocalNavigationGoal(...)`, the source-threshold fallback path for `door-entry-advance-local` is now explicitly skipped while raw traversal context is `door-push-through`, preventing the observed push-through-hold versus source-threshold-fallback oscillation class.
- Follow-up from local implementation on 2026-04-24 (code-only): transition interaction hooks now include game runtime state preflight and motion-state guards. `TryTriggerNavigationTransitionInteraction(...)` now runs `GameController.CanSelectObj(...)` before `SelectObj(...)`; `CanAutoInteractWithStep(...)` now rejects interactions when `Door.blockInteraction` is active, when private `Door.moving` or `SlidingDoor.moving` are true, and when private `Teleporter.inAnimation` is true (private flags read through safe reflection with non-fatal fallback). Teleporter transition wait timing now uses `max(step.TransitionWaitSeconds, Teleporter.WaitInSeconds)` so auto-walk transition holds can follow game-authored teleporter wait durations.
- Follow-up from local implementation on 2026-04-24 (code-only): committed-source door recovery staging now also has an explicit typed event-state-machine layer to reduce fragile direct stage mutation. Added `EventStateMachine<TState, TEvent>` and migrated recovery-stage state handling from integer constants/field mutation to typed `DoorCommittedSourceRecoveryStage` transitions (`None -> SourceThreshold -> PushThrough`) driven by `DoorCommittedSourceRecoveryTrigger` events (`ActivateFromLoop`, `EscalateFromRepeatedLoop`, `SourceThresholdSatisfied`) in `AccessibilityWatcher` and `AccessibilityWatcher.DoorNavigation`.
