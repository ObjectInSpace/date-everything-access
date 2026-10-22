# Project Status: DateEverythingAccess

The central tracking document (see `AGENTS.md`). History lives in git; this file says only what is
true now. Build, test and recent-work details are in `HANDOFF.md`; facts about the game are in
`docs/game-api.md`.

## Project Info

- **Game:** Date Everything!
- **Engine:** Unity 2022.3.28.7056988
- **Architecture:** 64-bit
- **Mod Loader:** BepInEx 5.4.23.5
- **Runtime:** CLR 4.0.30319.42000 / target framework net472
- **Game directory:** found by `scripts\Find-GameDir.ps1` (this PC: D:\SteamLibrary\steamapps\Common\Date Everything)
- **Speech:** Prism v0.18.2 (`lib\prism`), NVDA built in; Windows voice when no screen reader runs
- **Mod version:** 1.2 (`Main.cs`)
- **User experience level:** A Lot
- **User game familiarity:** Very well

## Setup Progress

- [x] Experience level determined
- [x] Game name and path confirmed
- [x] Game familiarity assessed
- [x] Game directory auto-check completed
- [x] Mod loader selected and installed (BepInEx x64 installed)
- [x] Speech library in place (Prism; replaced Tolk 2026-10-21)
- [x] .NET SDK available
- [x] Decompiler tool ready (`ilspycmd`)
- [x] Game code decompiled to `decompiled/` (the installed June 2026 build, regenerated 2026-10-21)
- [ ] Tutorial texts extracted (if applicable)
- [x] Multilingual support decided (English and Japanese, `Loc.cs`)
- [x] Project directory set up (csproj, Main.cs, etc.)
- [x] AGENTS.md updated with project-specific values
- [x] First build successful
- [x] "Mod loaded" announcement working in game

## Current Phase

Navigation accuracy. On 2026-10-21 every fact the mod states about the game was checked against
the installed build, the asset rip and the logs (branch `audit/ground-truth`, on top of the test
branch `test/camera-sightlines`). The navigation data now models the player's real body (0.8 m
radius, 6.2 m tall standing) and needs an in-game sweep before it is merged. See `HANDOFF.md`
"Recent work" and "Open items".

## Codebase Analysis Progress

### GATE: Tier 1 MUST be complete before Phase 2 (Framework)!

- [x] 1.1 Structure overview (namespaces, singletons) → documented in game-api.md
- [x] 1.2 Input system — game key bindings documented in game-api.md "Input"
- [x] 1.2 Input system — mod keys and why they are safe in game-api.md "Mod keys"
- [x] 1.3 UI system (base classes, text access patterns, Reflection needed?)
- [x] 1.4 State management decision → documented in "Architecture Decisions" below
- [x] 1.5 Localization: game's language system analyzed

### GATE: Relevant Tier 2 items MUST be done before implementing each feature!

- [x] 1.6 Game mechanics (analyzed as needed per feature; player body and movement in game-api.md)
- [x] 1.7 Status/feedback systems
- [x] 1.8 Event system / Harmony patch points
- [x] 1.9 Results documented in `docs/game-api.md`
- [ ] 1.10 Tutorial analysis (when relevant)

## Implemented Features

- Speech for focused menu items, dialogue lines and choices, screen and phone-app text, popups and
  dialogs, object examination text, room changes, nearby objects, status changes (Dateviators,
  charges), Date A Dex entries, SPECS, credits, save slots and the new-game questionnaire.
- Arrow-key navigation of dialogue choices and chat replies; Enter / Space to choose.
- Spoken settings menu (Ctrl+F1), saved in the BepInEx config.
- Look around (L): the current room and the objects in it, by direction from where the player faces.
- Known-objects list (O): objects the player has met, interacted with or examined, drilled down
  rooms → a room's datables and objects → a datable's objects (encountered cupboards grouped);
  sort (distance, same floor first, or alphabetical), floor, section (met / encountered) and
  doors-only filters.
- Objective tracker (Ctrl+O): the current tutorial objective, Roomers entry or context default.
- Autowalk (Alt+O) along planned routes over the baked navigation data, opening doors on the way,
  with a guidance tone; the aim coach (spoken or tone) lands the camera on the target.
- Mod tutorial tips alongside the game's tutorial (H repeats the latest); bulletin board photo
  paging; card pose descriptions; interaction feedback for the thermostat and lights.
- Coverage sweep (developer, Ctrl+Alt+F8) that walks to every object in the bake's roster.

## Pending Tests

- `test/camera-sightlines`: sightlines from the measured camera position on small objects on
  counters, desks and high shelves (kitchen cutlery drawer, cookie cupboard), against main.
- `audit/ground-truth`: a full coverage sweep, run standing, against a sweep of the current build:
  reached objects, stalls at doors and corners, and the door stand-back spots (they now keep the
  full body radius plus 0.2 m from an open leaf). Read the `[player-probe]` line to confirm the live
  body and movement values match `docs/game-api.md`.
- The crawlspace: can the player walk where the bake says (about 400 standing cells under the beams)?
- PR #1 merge (2026-10-21): speech through Prism with NVDA (start/quit NVDA mid-game, sleep-mode
  interrupts); office monitor selected on autowalk arrival; door stand-back-and-open and stuck-door
  recovery (office, laundry closet, shared upstairs bathroom, gym closet sliders); New Game in a used
  slot starts with an empty object list; Kopi (coffee machine) still routable; set
  `ReplanAfterStall=false` and confirm a stall stops at once.

## Known Issues

- No public source repository exists for Date Everything!, so this project uses the separate-mod
  workflow.
- The game relies on Rewired action maps, so physical keys come from the live maps (`[INPUTMAP]`
  in the log), not from code alone.
- `TranslationManager.GetTranslation` reads the text-language setting but always returns English,
  so the mod keeps its own localization.
- The asset rip (16 May 2026) predates the installed game build (June 2026). The current bake is
  made from it, so it lacks the June patch's four new solid boxes (kitchen and upstairs hall) and
  keeps 20 frame-glass colliders the patch disabled. A new rip is being made.
- The 76 mesh colliders whose meshes live in `patch1_leftovers_model_assets_all.bundle` are not
  linked in the rip, and one mesh fails to parse; they are missing from the navigation blockers.

## Architecture Decisions

- `AGENTS.md` is the primary control file. `CLAUDE.md` and `CLAUDE.de.md` are compatibility notices only.
- BepInEx, because the public Date Everything! mods target it.
- `ilspycmd` for a terminal-first, screen-reader-friendly decompilation workflow.
- Multilingual from the start with `Loc.cs`.
- `ControllerMenuUI` is the authoritative focus writer, because it enforces the game's own
  navigation rules; dialogue and chat choices are focused pointer-style (`isViaPointer: true`).
- Reflection on `TalkingUI` and `DialogBoxBehavior` for dialogue text and choices.
- Navigation: plan over an offline bake of the house (`navigable_region.bake.json`), check what the
  game can do live (door state, sightlines, the game's own interaction ray), and test in game.
- Values about the game are read from the game at runtime where possible; fixed numbers come from
  the installed build, the asset rip confirmed live, or logged measurements, and say which.

## Key Bindings (Mod)

- F1: Help
- Ctrl+F1: Accessibility settings
- F9: Toggle debug mode
- Ctrl+Alt+F8: Coverage sweep (developer)
- L: Look around
- O: Known-objects list
- Ctrl+O: Track the current objective
- Alt+O: Autowalk on or off (next to the target: aiming help)
- Backtick: Repeat the last spoken line
- H: Repeat the latest mod tutorial tip

## Notes for Next Session

- Run the two pending sweeps above (standing) before merging either branch.
- After a game update: regenerate `decompiled/`, re-rip the scene if the house changed, then
  re-export and re-bake (`HANDOFF.md` "Navigation tools").
