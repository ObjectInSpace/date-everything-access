# Project notes

Current state of the project, how to build and test it, and recent work. Read `AGENTS.md` first
for the project rules.

## The repository

- Before committing, check that no game code or game assets go in (no files from the game
  folder, no decompiled code). The navigation data under `artifacts/navigation` is measurements
  derived from the house layout; the mod needs `navigable_region.bake.json` to autowalk.

## Building and testing

- The game folder is found automatically by `scripts\Find-GameDir.ps1`: the
  `DATE_EVERYTHING_DIR` environment variable if set, otherwise every Steam library on the PC.
  Each .bat file below also takes the game folder as an argument. For a plain build, pass it to
  dotnet: `dotnet build -c Release -p:GameDir="X:\path\to\Date Everything"` (the project falls
  back to `DATE_EVERYTHING_DIR`, then the default Steam folder on C:). Add
  `-p:SkipCopyToPlugins=true` to build without installing.
- `Build-and-Install.bat`: builds and installs into the game, and copies `prism.dll` next to
  `Date Everything.exe`. It refuses to run while the game is open. Check that the game is closed
  before launching it again, because Steam shows an error if it's already running. The first
  run backs up the installed mod DLL to `backup\`.
- `Restore-Original-Mod.bat` puts that backed-up DLL back.
- `Make-Tester-Installer.bat` builds `DateEverythingAccess-Setup.exe` for testers from the
  installed copy in the game folder (`tester-package\build-setup.ps1`). The setup finds the
  game itself, or asks for the folder.
- Game log: `BepInEx\LogOutput.log` in the game folder. F9 turns on debug mode, which also logs
  everything spoken (`[SR]` lines).
- `decompiled\` must be the installed game's code. After a game update, regenerate it (command in
  `docs\game-api.md`, "Sources of truth").

## Conventions

- Speech is tested mainly with NVDA.
- Player characters are named by each player. In readmes and change logs say "your
  character" or "the player", never a particular name or pronoun.
- The mod must not override the game's rules (for example, bulletin board photos only when the
  game shows them).

## Recent work (newest first)

- 2026-10-21, ground-truth audit (branch `audit/ground-truth`, on top of `test/camera-sightlines`):
  every fact the mod states about the game was checked against the installed build, the asset
  rip and the logs, and `docs\game-api.md` was rebuilt from those checks. Main results:
  - The player's body is a capsule of radius 0.8 m reaching 1.2 to 6.2 m above the feet when
    standing (Player.prefab: radius 0.4 local on a root scaled 2x; confirmed in the current build's
    files, live, and by logged positions stopping 0.7-0.9 m from walls). The bake now uses 0.8 m
    and the full height (it used 0.4 m and 2.5 m). The planner's compensations for the old 0.4 m bake (deep operable cells, body-room goal tiers, a
    0.4 m door-leaf margin) are gone, and door stand distances follow the live body radius.
  - The blocker exporter covered whole triangle bounding boxes, dropped every collider with a top
    below 0.35 m (all of the crawlspace) or a bottom above 15 m, and stopped its height bands at
    the crouched height; the bake let rug boxes block their whole footprint upstairs. All fixed.
    The exporter's rasterizer is compiled C# now, so an export takes about 4 minutes.
  - Left Shift is the game's crouch toggle, so the old sweep key Ctrl+Alt+Shift+F8 left every
    sweep crouched (camera 4.86 m above the feet instead of 5.76 m, shorter capsule, slower). The
    sweep key is now Ctrl+Alt+F8, and the sweep logs and says whether the player is crouched.
  - `decompiled\` was three months older than the installed game (March against June 2026); it is
    now the installed build. The asset rip (May 2026) also predated it; the scene was ripped again
    from the installed build and the navigation data rebuilt (only the June upstairs hall box
    changes the bake, 15 cells). The mod logs the live player values (`[player-probe]`).
  - The crawlspace test now asks the bake which floor the player is on (it was a hand-set
    y < -3.5); the bake puts the same 12 objects there.
  - Obsolete documents and broken analysis scripts were deleted.
- Speech goes through Prism (`lib/prism`, v0.18.2) instead of Tolk. It follows NVDA
  starting and stopping mid-game (Prism's availability watch), and the Windows voice takes over
  when no screen reader is running.
- Mod tutorial (`AccessibilityWatcher.ModTutorial.cs`, texts `modtut_*` in `Loc.cs`): spoken
  steps alongside the game's tutorial; H repeats the latest. Work screen tips come from
  `ParallelChat.chatEnded` (Sam's first chat; then five finished chats in the customer list,
  `ChatMaster.CanopyChats`). The bed tip follows meeting Betty; the morning tip a change of day.
- Aim coach (`AccessibilityWatcher.AimCoach.cs`): Alt+O next to an object, or autowalk arriving
  without landing the aim, talks the player onto it ("a little up", "far left and down").
  Settings menu (Ctrl+F1) "Aiming help": Spoken directions (default) or Tone. Spoken also takes
  over when the player walks to a target by the tone and arrives; Tone uses the original
  tracker's aim tone everywhere (pan for left/right, pitch for up/down, chirp when selected,
  no automatic final nudge).
- Doors: stand-back-and-open keeps clear of the door's whole swing (live hinge and leaf from the
  door's collider), which fixed leaving the office in the tutorial.
- At startup the log gets an `[INPUTMAP] Keyboard summary` of the key bindings, for
  remote testers whose keys misbehave.

## Navigation tools

- Ripping the scene (after a game update that changes the house), with AssetRipper 1.3.12 (free):
  1. Copy the game folder to the SSD, keeping in `Date Everything_Data\StreamingAssets\aa\StandaloneWindows64`
     only the bundles in `scripts\rip_scene_bundles.json` (regenerate the list with
     `scripts\rip_scene_bundles.py` if the scene bundle changed). Not under the Windows Temp folder:
     it gets cleaned mid-run.
  2. In `AssetRipper.Settings.json` set `ScriptExportMode` to 1 (Hybrid) and `ScriptContentLevel` to 2.
  3. `AssetRipper.GUI.Premium.exe --headless --port 47129`, then POST `path=<trimmed folder>` to
     `http://localhost:47129/LoadFolder` and `path=<output folder>` to `/Export/UnityProject`
     (urlencoded form). About 2 minutes, 4.1 GB. The kept rip is
     `D:\Root\AssetRipper\rip-2026-10-21`.
- Exporting: set `DATE_EVERYTHING_RIP` to the rip's `ExportedProject` folder, then run
  `scripts\Export-SceneBlockerData.ps1` first (about 4 minutes; the interactables export reads its
  output), `Export-SceneNavigationData.ps1`, `Export-SceneWalkableSurfaceData.ps1` and
  `Export-SceneInteractables.ps1`. Copy `artifacts\navigation\thirdpersongreybox-blockers.json` to
  `thirdpersongreybox-blockers.COLUMNS.json`.
- Baking: `python scripts\bake_navigable_region.py` (writes
  `artifacts\navigation\navigable_region.bake.json`, which ships with the mod).
- `python scripts\validate_los.py --log <LogOutput.log>` replays the game's logged interaction rays
  against the export's static colliders. It cannot judge rays that reach an upstairs ceiling (above
  the export's height cap) or a door (exported separately, state not logged) (`docs\game-api.md`
  "Navigation data").
- The offline planner and simulator were retired on 2026-10-21; navigation is tested in game.
  The coverage sweep (Ctrl+Alt+F8) visits every object in the bake's roster; results go to
  `BepInEx\plugins\coverage_sweep\<run-id>\sweep_results.json`.

## Open items

- Branch `test/camera-sightlines` (installed, being tested): sightlines from the measured camera
  position plus an aim spread over the target, to fix the kitchen cutlery drawer and cookie
  cupboard that the earlier live-camera-height attempt (`test/nav-open-items`) broke.
- Branch `audit/ground-truth` (this audit) needs an in-game sweep before merging: the new bake
  (0.8 m body, full height) changes every route. Compare it with a sweep of the current build,
  both run standing (not crouched).
- Untested in game: the eased final approach (autowalk's last 0.7 m and the door stand-back walk now
  scale their input to the live walk speed, so the top speed setting, 0.4 m per physics step, can't
  overshoot the 0.3 m stop point), and the "crouched at the lowest speed setting cannot move"
  message. Worth a run at Movement Speed 40 and at 10.
- Untested: walking in the crawlspace. Its beams hang 1.2-5.5 m above the floor over most of it,
  so the bake leaves only about 400 standing cells there.
- Merged 2026-10-21 after a sweep comparison (550 objects; main 484 vs test 488 reached): PR #1's
  35-degree corner hold, its clearance-3 standpoint tiers and deep operable-cell filter (both
  removed again on `audit/ground-truth`, where the bake itself allows for the real body), and its
  adaptive turn speed-up (for CrossOver) with two safeguards: turns
  are only measured while the player is in control, and every autowalk / aiming session starts at
  normal strength. The turn check fires on Windows too (all four logged checks on 2026-10-21 turned
  4-20% of what was asked); the cause is not known.
- Known, not caused by these: the gym closet's outer sliding panel can stop part-way across its
  doorway and walks stall against it (seen in the 2026-10-21 test sweep).
- PR #1's 4.5 m limit on stand-in parts was replaced on main by a same-room rule (the 4.5 m limit
  excluded all of Kopi's parts). The same-room rule is untested in game.
- Decided against: PR #1's steeper clearance-penalty table (replaying 64 baseline routes showed
  no change in wall clearance or route length).
- A CrossOver tester's Z and X did nothing. Their keyboard summary showed an older game build
  (February 2026) with no keyboard keys for Interact and Examine at all; current builds bind
  them to Z and X. Not a mod problem: the game needs updating.
- The same tester heard some game popups and tooltips repeat, and missed the morning tutorial
  tip. Possible cause (unconfirmed): popup, tutorial and dialog announcements forget their last
  text as soon as it disappears for one poll, so a flickering popup is read again (interrupting)
  and cuts off a queued tutorial message. Needs a debug-mode log (F9) from an affected setup.
