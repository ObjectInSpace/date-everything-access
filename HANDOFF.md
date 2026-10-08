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
  back to `DATE_EVERYTHING_DIR`, then the default Steam folder on C:).
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

## Conventions

- Speech is tested mainly with NVDA.
- Player characters are named by each player. In readmes and change logs say "your
  character" or "the player", never a particular name or pronoun.
- The mod must not override the game's rules (for example, bulletin board photos only when the
  game shows them).

## Recent work (newest first)

- Speech goes through Prism (`lib/prism`, v0.18.2) instead of Tolk. It follows NVDA
  starting and stopping mid-game (Prism's availability watch), and the Windows voice takes over
  when no screen reader is running.
- Mod tutorial (`AccessibilityWatcher.ModTutorial.cs`, texts `modtut_*` in `Loc.cs`): spoken
  steps alongside the game's tutorial; H repeats the latest. Work screen tips come from
  `ParallelChat.chatEnded` (Sam's first chat; then five finished chats in the customer list,
  `ChatMaster.CanopyChats`). The bed tip follows meeting Betty; the morning tip a change of day.
- Aim coach (`AccessibilityWatcher.AimCoach.cs`): Alt+O next to an object, or autowalk arriving
  without landing the aim, talks the player onto it ("a little up", "far left and down").
- Doors: stand-back-and-open keeps clear of the door's whole swing (live hinge and leaf from the
  door's collider), which fixed leaving the office in the tutorial.
- Turning: the auto-walk turn gain adapts when the game turns slower than expected (CrossOver).
- One key press no longer counts twice (Alt+O sometimes also opened the objects list).
- At startup the log gets an `[INPUTMAP] Keyboard summary` of the key bindings, for
  remote testers whose keys misbehave.

## Open items

- A CrossOver tester's Z and X did nothing. Their keyboard summary showed an older game build
  (February 2026) with no keyboard keys for Interact and Examine at all; current builds bind
  them to Z and X. Not a mod problem: the game needs updating.
- The same tester heard some game popups and tooltips repeat, and missed the morning tutorial
  tip. Likely cause: popup, tutorial and dialog announcements forget their last text as soon as
  it disappears for one poll, so a flickering popup is read again (interrupting) and cuts off a
  queued tutorial message. Unconfirmed: needs a debug-mode log (F9) from an affected setup.
