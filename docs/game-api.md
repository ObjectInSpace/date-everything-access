# Date Everything! - Game API Documentation

Facts about the game that the mod relies on. Every statement here was checked against the
installed game (decompiled code, ripped assets or live logs) on 2026-10-21; the source is named
next to each fact. Mod design choices are not game facts and live in the code, not here.

## Overview

- Game: `Date Everything!`
- Engine: Unity `2022.3.28.7056988` (BepInEx log header)
- Runtime: CLR `4.0.30319.42000`, mod target `net472`, 64-bit
- Mod loader: BepInEx `5.4.23.5`
- Input: `Rewired`, plus `UnityEngine.InputSystem` for keyboard-layout detection

## Sources of truth

- `decompiled/` is the installed game's `Assembly-CSharp.dll` (file date 2026-06-24), decompiled
  with `ilspycmd -p -o decompiled "<game>\Date Everything_Data\Managed\Assembly-CSharp.dll"`.
  Regenerate it after a game update: the March 2026 decompile it replaced was missing the June
  input actions (keyboard Interact/Examine, ChatHistory, the *Mouse actions).
- Serialized values (prefab fields, collider sizes) override the field initializers seen in the
  decompiled code. The Player values below were read from the installed build's own
  `base_leftovers_prefab_assets_all.bundle` (UnityPy) and match the live logs.
- The AssetRipper export (`D:\Root\AssetRipper\rip-2026-10-21\ExportedProject`) was ripped on
  2026-10-21 from the installed June build with AssetRipper 1.3.12 (free edition is enough; the
  premium copy has expired). The export scripts read it from the `DATE_EVERYTHING_RIP` environment
  variable. Rip with Script Export Mode **Hybrid**: the DLL mode gives the game's scripts new GUIDs,
  and the interactables export matches `InteractableObj` and `UniqueId` by GUID. The house scene
  needs only the 22 bundles in `scripts\rip_scene_bundles.json` (from `scripts\rip_scene_bundles.py`);
  rip a copy of the game folder whose `StreamingAssets\aa\StandaloneWindows64` holds just those,
  because the full game does not fit in memory (`HANDOFF.md` "Navigation tools").
  The older 16 May rip predated the build (`camerarotmultiplier` 620 against 50 now).
- Game bundles can be read directly with UnityPy (installed for Python) without a rip: the house
  scene is `StreamingAssets\aa\StandaloneWindows64\base_scene_thirdpersongreybox_scenes_all.bundle`.
- `BepInEx\LogOutput.log`: the mod logs `[INPUTMAP]` (live key bindings at startup),
  `[capsule-probe]` (live player collider), `LOS_PROBE` (the game's interaction ray) and
  `SimpleNav route frame ... player=(...)` (live player position).

## Player body, camera and movement

All from `BetterPlayerControl` (decompiled) and the Player prefab as stored in the installed build
(`base_leftovers_prefab_assets_all.bundle`, read with UnityPy; identical to the May rip in every value
below), confirmed by the live `[capsule-probe]` and camera measurements.

- The player root is scaled 2x (`m_LocalScale {2,2,2}`; live `lossyScale=(2,2,2)`). Every local
  size below doubles in the world.
- Collider: one `CapsuleCollider` on the root, local radius `0.4`, height `2.5`, centre y
  `1.849827`. World: radius **0.80 m**, spanning **1.2 m to 6.2 m above the feet** standing.
  The capsule does not reach the floor.
- Crouch: serialized `colliderHeightCrouched = 1.6` (the code initializer says 1.0; the prefab
  wins, and the live probe reads 1.600 when crouched). The collider centre and the camera node drop
  by `(2.5 - 1.6) / displacementCoefficient(2) = 0.45` local = **0.9 m**. Crouched capsule: 1.2 m to
  4.4 m above the feet. Speed drops by serialized `speedReduction = 10` (initializer 2).
- Crouch input: `crouchInputMode` is serialized `0` = `Toggle` and nothing sets it, so crouch is
  always a toggle. Keyboard binding: `Crouch = Left Shift` (live `[INPUTMAP]`); action 33, plus
  action 74 `CrouchMouse` on the mouse map. Any key chord containing Left Shift toggles crouch.
- Feet: `transform.position` is the feet. They are held on the ground by a downward raycast from
  `position + up * heightdist` of length `2 * heightdist` (serialized `heightdist = 2`, initializer
  0.25), with the vertical velocity `(hit.y - position.y) * shiftforce` (serialized 15). The player
  therefore rides up onto anything below the capsule: logs show the feet 1.84 m above the kitchen
  floor while standing on the open dishwasher door.
- Measured feet level: ground floor `-0.60`, upstairs `12.82` (thousands of route-frame samples).
- Camera: `Camera` sits at local y `2.878` under `CameraOffsetNode` (local 0) under the root:
  **5.76 m above the feet standing, 4.86 m crouched** (both seen in LOS_PROBE origins; the 4.86
  readings are crouched players). No horizontal offset at any pitch. Field of view is the
  `cameraFieldOfView` setting (default 70), applied to every `CinemachineVirtualCamera`.
- Camera pitch is limited to `maxrot = 70` degrees from level (serialized `maxrot 70`,
  `minrot -70`).
- Movement (`FixedUpdate`, only in `PlayerState.CanControl`): velocity is zeroed, then set to
  `Lerp(0, speed * input, 0.5)`, so horizontal speed is `speed / 2`. `SetSpeed()` sets
  `speed = movementSpeed setting (default 0.5) * 40`, so **10 m/s** at default settings; crouched
  `(speed - 10) / 2`; `SpeedBoost()` multiplies by 1.25. Inputs under 0.2 magnitude count as no input.
- Turning: `camerarotspeed = cameraSENSITIVITY setting (default 1) * camerarotmultiplier (50)`; each
  physics step the body yaws by up to `look.x * camerarotspeed` degrees and the camera pitches by
  `look.z * camerarotspeed`, both eased over `easingTime` (0.045 s).
- Player settings are saved in `%USERPROFILE%\AppData\LocalLow\SassyChap\Date Everything\<SteamID>\0_Settings.sav`
  (JSON after a short binary header; graphics settings are separate, in the registry under
  `HKCU\Software\SassyChap\Date Everything`, `MGSettings`). On this PC (2026-08-14 save):
  `cameraSENSITIVITY` 0.5 (so `camerarotspeed` 25, as logged), `movementSpeed` 0.625 (12.5 m/s, 0.25 m
  per physics step), `cameraFieldOfView` 102. Read live values, never assume the defaults.
- Setting ranges (the settings menu's slider limits, read from the game's own UI asset in all six
  copies of `SettingsMenu`): Movement Speed slider 10-40, stored as slider / 40 (0.25-1.0), and the
  slider value IS `speed`: 5-20 m/s standing (0.1-0.4 m per physics step), 0-15 m/s crouched, so a
  crouched player at the lowest setting cannot move. Camera Sensitivity slider 1-10, stored as
  slider / 10 (0.1-1.0; the default 1.0 is the maximum): 5-50 degrees per physics step. Field of View
  60-120 (default 70). Invert Y only flips the arrow keys' pitch input; it does not touch values
  written to `look` directly. The mod reads the live values (`LiveWalkSpeedMps`,
  `TryGetNextWalkSpeedMps`, `CameraRotDegPerStep` in AccessibilityWatcher) and eases its last
  approach so a fast setting can't step over a stop point.
- The player is a non-kinematic `Rigidbody` moved by velocity; walls stop it through ordinary
  Unity contacts (it slides along them). There is no capsule cast or custom collision helper.
- `BetterPlayerControl.Move(position, rotation)` teleports (kinematic toggle around the write).

## Object interaction

From `BetterPlayerControl.Update()`, `InteractableManager`, `InteractableObj`, `Interactable`.

- The game raycasts every frame from `camera.position - camera.forward * 0.25` along
  `camera.forward`, unlimited distance, layer mask `~dateviatorIgnores` (serialized bits 1028 =
  layers 2 and 10, so mask -1029).
- The hit transform must carry `InteractableObj`, and
  `Vector3.Distance(hit.collider.ClosestPointOnBounds(cameraPos), cameraPos) < interactableObj.InteractionRadius`.
  `InteractionRadius` defaults to `7.5`; objects may serialize other values (in the bake roster: 932 at 7.5, three at 5, one each at 10
  and 25).
- The result is published as `InteractableManager.activeObject` and `IsPlayerInRange`. Navigation
  treats "activeObject is the target (or its parent/child) and IsPlayerInRange" as the only
  arrival proof for ordinary objects.
- Further layers the player and game own, not navigation: `InteractableManager.Interact(...)`
  needs tutorial permission, `GameController.viewState != TALKING`, an active object and range;
  `GameController.CanSelectObj(...)` / `SelectObj(...)` check glasses, talked-today, charge and
  realization; alternate interactions run only when `Interactable.CheckCanUse()` is true
  (`noderequired()` empty or visited by Ink, and not `stateLock`).
- Dateviator awakening: `HandleChargingUp(...)` / `HandleChargingUpCompleted(...)` call
  `InteractableManager.Interact()` once the hold fills and the selected object is in range.

## Doors

From `Door` and `SlidingDoor`.

- Public state: `open`, `locked`, `blockInteraction`; `Door.range` (swing degrees, default 85) and
  `doorOpenType` (default `OnlyPositive`).
- Private state: `moving`, `collidedWithPlayer` (set by `OnCollisionEnter` / `OnCollisionExit`),
  `stopOnCollision`.
- `Door.Interact()` does nothing while `moving`; with Dateviators on and an `altInteractable` it
  runs that instead. Opening and closing are skipped while `blockInteraction` or
  `collidedWithPlayer` is true, and a moving leaf stops when `collidedWithPlayer && stopOnCollision`:
  a player standing in the swing stops the door.
- `SlidingDoor.Interact()` does nothing while `moving` or `stateLock`.
- Passage doors live under the scene path `/MultiRoom/Doors/`; every other `Door` / `SlidingDoor`
  is a container (cupboard, fridge, breaker box) opened in place.
- The office doorway also holds `Doors_Office_Locked`, a locked variant swapped with `Doors_Office`
  by `TutorialController` (`MovingDateable.MoveDateable("MovingOfficeDoor", "locked"/"unlocked")`).
  The scene file has it active, so the export blocks the doorway with it; the bake's `Doors_Office`
  freed cells reopen a lane through it when that door is open.

## Input

### Rewired actions

From `decompiled/RewiredConsts/Action.cs`. Keyboard bindings are the live defaults from
`[INPUTMAP]` on this install (US layout).

- Gameplay (`CharacterController` map): Talk 2 (Space), Move_Horizontal 3, Move_Vertical 4
  (W/S and comma/O; the mod strips O, see below), Look_Horizontal 9 / Look_Vertical 10 (arrows),
  Message_Log 11 (+), Interact 12, InteractKeyboard 69 (Z), Awaken 13 (Space), Examen 18,
  ExamenKeyboard 70 (X), Crouch 33 (Left Shift), Remove_Ui 55 (P), Chat_History 56 (H; bound but never polled by game code).
  Mouse duplicates: Toggle_DateviatorsMouse 71, PauseMouse 72, AwakenMouse 73, CrouchMouse 74,
  CancelMouse 76, ConfirmMouse 77.
- Dialogue: Continue 0 (C, I), Dialog_Up 1 / Dialog_Down 15 (W/S, comma/O, arrows),
  PrimaryResponse..QuaternaryResponse 39-42 (1-4), Dialog_Options_Menu 61 (Tab), Exit_Main_Menu
  62 (M), Dialog_Open_Settings 63 (J), Remove_Ui_UI 66 (N), Chat_History_UI 67 (H),
  Look_Vertical_Dialog 68.
- UI: UI_Up/Down/Right/Left 24-27 (W/A/S/D and arrows), Confirm 28 (Return), Cancel 29 (Esc,
  Backspace), CycleListUp 37 (Q), CycleListDown 38 (R), UIMenuExtraAction 50, UIMenuExtraSecondAction 51; the live map also binds
  ReverseSort (U), which has no constant in `Action.cs`.
- Pause 5 (Esc), Toggle_Dateviators 52 (Tab), Engagement 19 (Space).
- Debug map (Ctrl+G/N/M/D/2/3/R/4, Ctrl+Left Alt): disabled in retail.

### Keys hard-coded in game code

- `F2` (`EditorTools`) and `F3` (`ArtGalleryTool`): both objects destroy themselves unless
  `Debug.isDebugBuild`, so the keys do nothing in retail.
- `F8` (`InkVariableTool`): the constant exists but `Update()` is empty; unused.
- `Squirrel` (a minigame) reads number keys, W/A/D, Space, E and the arrows directly.

### Keyboard layouts

`BetterPlayerControl.Update()` watches `Keyboard.current.keyboardLayout` and loads the
`Azerty`, `Dvorak` or `Colemak` Rewired layouts for the Dialog and CharacterController maps, so
letter keys move with the layout. Function keys do not.

## Mod keys

From `Main.cs` (global hotkeys) and `AccessibilityWatcher.cs` (polled in game, suppressed while a
text field or the mod settings menu has focus).

- Global: `F1` help, `Ctrl+F1` mod settings, `F9` debug mode, `Ctrl+Alt+F8` coverage sweep
  (developer).
- Polled: `L` look around, `O` object list, `Ctrl+O` objective, `Alt+O` autowalk on/off,
  `` ` `` repeat last line, `H` repeat the latest mod tutorial tip, W/A/S/D stop autowalk.
- `O` is also a game movement key (Move_Vertical); the mod strips that keyboard binding at startup
  (`[INPUTMAP] Stripped 1 'O' -> Move_Vertical`).
- `H` is also bound to the game's gameplay Chat_History action, but game code never polls it;
  the dialogue chat history (Chat_History_UI, 67) only reacts while the dialogue menu is shown.
- Never put Left Shift in a mod chord: it toggles the game's crouch.

## Navigation data

How the shipped `navigable_region.bake.json` is made, and the evidence it matches the game.

- Scene: `ThirdPersonGreybox.unity` from the AssetRipper export of the installed build
  (2026-10-21). Against the earlier May export, the only scene changes that reach the bake are the
  June patch's: an upstairs hall box (`House/UpstairsHall/Collider`, 15 cells less navigable) and
  three kitchen boxes (`House/Kitchen/Collider_1/2/3`, over cells that were already blocked).
- Doorways: the 16 `OcclusionPortal`s are 3.7 m wide and 8 m tall (local size 3.7 x 0.8 x 8,
  rotated upright; `thirdpersongreybox-navigation-data.json`). The body (1.6 m wide) fits them all.
- Blockers (`scripts\Export-SceneBlockerData.ps1`): every active, enabled, non-trigger collider on
  a layer the player collides with (layer 10's collision matrix: 0-8, 10-12, 14-27). Mesh
  colliders become per-cell vertical spans on a 0.2 m grid; a cell is covered only where a
  triangle's XZ projection overlaps it (exact test; compiled C# `ColumnRaster`). Columns are kept
  for the bands the standing capsule can occupy on each floor (crawlspace, ground, upstairs).
- Bake (`scripts\bake_navigable_region.py`): a cell is blocked when a collider there rises at
  least 0.3 m above the walkable surface (`STEP_OVER_HEIGHT_M`, our rule: autowalk does not climb
  furniture) within 6.2 m of it (the standing capsule's top). The blocked area is dilated by the
  body radius, 0.8 m. Closet interiors the whole-cell dilation rounds shut are recovered (the body
  does stand in them).
- Doors: `freed_cells` (the doorway, navigable when open), `open_blocked_cells` (the raw open-leaf
  footprint; the planner grows passage-door leaves by the body radius), `operable_from_cells`
  (navigable, within 3 m of the door by our choice, clear of the closed panel and the swing: the
  `Door.cs` collision gates).
- Evidence (2026-10-21, sweep logs in `artifacts\navigation\sweep\runs`): live raycast hits on walls
  land on raster-blocked cells within 3 cm (90% of 291 hit points; the rest are doors, which move);
  on a 0.1 m raster of the geometry at the capsule's height, logged player positions stop 0.65-0.80 m
  from the nearest occupied cell edge (peak of 470 of 1,865 near-wall positions; the face lies 0-0.1 m
  inside that cell), and almost every closer position is by a door, under overhead geometry that the
  crouched sweeps passed beneath, or by a moving object;
  `scripts\validate_los.py` agrees with all 976 rays of the two standing-camera runs (884 + 92) and
  1,749 of 1,830 in `20261021-test-nav-open-items`: the 81 others are upstairs rays the game stopped
  on an upstairs ceiling (78) or a closed door (3). The export keeps no collider whose bottom is
  above 19.4 m (upstairs ceilings are at about 25.5 m) and no doors, so the offline check cannot
  judge rays that would reach those.
- Known data gaps: the export's 76 "MeshGuidUnresolved" mesh colliders use Unity's built-in meshes
  (75 Plane, resource 10209; 1 Cube, 10202), which a rip cannot export. They are decorations
  (picture glass on walls, floor and ceiling shadow planes, desk papers, a wall shadow behind the
  bedroom wall) and do not affect walking. One mesh fails to parse
  (`SM_Cabinet_4_Drawer_MODEL_UPDATE`, in this rip too) and is missing from the blockers. In the
  crawlspace, `Beams_Crawlspace` hangs 1.2-5.5 m above the floor over most of it, so the bake leaves
  only about 400 standing cells there; how the game plays down there is untested.

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
  - `DateADex.OnEntryFocused(ListBox prevFocusingBox, ListBox curFocusingBox)` updates preview art and collectable count for the focused row
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
    - Future Date A Dex fixes should be split into one behavior at a time: list focus speech, entry-open bio speech, and keyboard focus semantics should not be bundled in one change

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
  - private `giftBox` field stores the active Dateviators delivery box / Skylar Specs package `GameObject`
  - private `frontDoor` field stores the tutorial front-door anchor `GameObject`
  - private `triggerZones` array is populated from `FindObjectsOfType<TutorialTriggerZone>(includeInactive: true)` in `Start()`
- `TutorialTriggerZone.OnTriggerEnter(Collider other)`
  - for `EventType.TriggerDrone`, it calls `TutorialController.DeliverGiftBox()`
- `TutorialThreshold.OnTriggerEnter(Collider other)`
  - for `isDroneThreshold == true`, it calls `TutorialController.GotCloseToGift()`, not `DeliverGiftBox()`; this is post-delivery drone behavior and should not be used as the target for causing the gift box to spawn
  - during `Check the delivery at the front door.`, an active `isDroneThreshold` is still a valid intermediate objective before the package itself is routeable/selectable because it clears the hovering drone state
- `TutorialController.DeliverGiftBox0()`
  - enables the `TriggerDrone` tutorial trigger zones and clears tutorial text after the first Thiscord/phone step
- `TutorialController.UnpauseAndGoToGift()`
  - unlocks office doors, breaks the front door moving dateable, activates `giftBox`, unpauses the player, and sets the next tutorial text

Important consequence:

- Objective tracking should prefer the live tutorial signpost text over reconstructed save-state guesses when both are available.
- Computer tracking can anchor directly from the serialized `TutorialController.computer` object reference before falling back to generic interactable-name heuristics.
- `Check the delivery at the front door.` is the Skylar Specs package objective. The signpost becomes true when `DeliverGiftBox2()` sets `TUTORIAL_STATE_2_SAW_THISCORD`; `DeliverGiftBox3()` then pauses for the drone delivery camera, and `UnpauseAndGoToGift()` later activates `giftBox` automatically by timer. There is no second player-entered trigger that spawns the box during this signpost. The active `TutorialTriggerZone.EventType.TriggerDrone` belongs to the earlier `Leave the office and reflect on your life choices.` step; scene artifacts show that routeable office-side trigger volume as `===SCENE===/House/Office/TutorialDroneThreshhold_3` with approximate XZ bounds `x=5.90..9.70`, `z=7.70..9.80`. `TutorialThreshold.isDroneThreshold` is not the spawn trigger; the hall-side `TutorialDroneThreshold` calls `GotCloseToGift()` after delivery.
- World-coordinate routes such as post-delivery drone thresholds have no interactable target. For `TargetGameObjectId == 0`, the final leg must steer to the actual `SimpleNavRoute.TargetPosition` and completion must be close to that exact coordinate. Do not use the planner's broad goal-disc cell or ordinary waypoint radius as final completion, because the combined tolerances can announce arrival while still inside the office and short of the intended trigger area.
- The first computer objective should use `TutorialController.computer` as its source of truth. In the scene this serialized reference is `ComputerMovingDateable`; its active `mac_computer` monitor child is the expected interactable surface. If that anchor cannot resolve at runtime, only then fall back to a broader computer-like interactable search. For computer targets, plan to the interactable's collider/renderer bounds center instead of blindly using `transform.position`.
- `TutorialController.SetTutorialText(...)` also emits generic prompts such as `Continue to awaken dateable objects.` and `Realize Dateable Objects.` that do not name a single character directly.
- For those generic prompts, accessibility objective tracking needs a fallback policy such as picking the nearest valid unmet or not-yet-realized dateable interactable instead of returning `No current objective.`
- The `tutorialSignpostTMP` text can still hold the current objective even when the signpost object itself is hidden, so objective readers should key off the text field, not only the signpost object's active state.
- `Leave the office and reflect on your life choices.` does not name an interactable. The actual progression is entering the active `TutorialTriggerZone.EventType.TriggerDrone` volume enabled by `DeliverGiftBox0()`, which starts the drone cutscene and eventually activates `giftBox`; objective tracking should route to the live trigger zone world coordinate rather than to the office door.
- Maggie’s tutorial progression uses mixed identifiers: save-state checks use `maggie_mglass`, while the tutorial interaction gate checks `obj.InternalName() != "maggie"`, so tracker matching should tolerate both names plus visible labels like `Maggie` or `Magnifying`.
- When no tutorial objective can be derived, the objective tracker (`Ctrl+O`) may use the current Roomers entry as the next objective. Roomers stores the selected row in private `currentEntry` plus private `RoomersEntries`; each row has a `RoomersEntryButton.roomersEntry` with the target character internal name. The accessibility resolver should first use the selected UI `RoomersEntryButton` when focus is on a row, then fall back to those private Roomers fields, and finally map `roomersEntry.character` to a live dateable `InteractableObj`.
- Roomers focus behavior, as of 2026-05-24: `RoomersButton.OnSelect()` calls `Roomers.switchEntryIndex(index)` without `andDisplayInfo`, so selecting a row updates `currentEntry` but does not refresh `RoomersInfo.SetUpPanel(...)`. Screen reader row focus must read directly from `RoomersEntryButton.roomersEntry`, not from the visible detail panel, otherwise it can announce stale clue details.

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
    - the postfix should pass the opened `DateADexEntry` into the pending bio announcement; do not rely only on later UI text extraction, because focus can land on the Back button before the bio text is readable to the accessibility layer
    - `CollectableButton` focus is the normal opened-entry focus target; when the bio pane is visible, its accessibility selection speech should prefer the entry-detail announcement, which already includes the collectable count

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

## Not Yet Analyzed

- Remaining runtime verification for hidden phone bindings that may be populated outside the decompiled code path scan
- Full message log content extraction
- Non-dialogue gameplay notifications such as pickups, relationship changes, or time-of-day transitions
