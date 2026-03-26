# Project Status: DateEverythingAccess

## Project Info

- **Game:** Date Everything!
- **Engine:** Unity 2022.3.28.7056988
- **Architecture:** 64-bit
- **Mod Loader:** BepInEx 5.4.23.5
- **Runtime:** CLR 4.0.30319.42000 / target framework net472
- **Game directory:** D:\SteamLibrary\steamapps\Common\Date Everything
- **User experience level:** A Lot
- **User game familiarity:** Very well

## Setup Progress

- [x] Experience level determined
- [x] Game name and path confirmed
- [x] Game familiarity assessed
- [x] Game directory auto-check completed
- [x] Mod loader selected and installed (BepInEx x64 installed)
- [x] Tolk DLLs in place
- [x] .NET SDK available
- [x] Decompiler tool ready
- [x] Game code decompiled to `decompiled/`
- [ ] Tutorial texts extracted (if applicable)
- [x] Multilingual support decided
- [x] Project directory set up (csproj, Main.cs, etc.)
- [x] AGENTS.md updated with project-specific values
- [x] First build successful
- [x] "Mod loaded" announcement working in game

## Current Phase

**Phase:** Early Feature Work
**Currently working on:** Most recent watcher and UI speech fixes have passed in game, and the remaining work is concentrated on broader gameplay/system coverage, spatial room cues, and unresolved edge-case validation.
**Blocked by:** Broader gameplay/system coverage, spatial navigation cues for rooms, explicit runtime testing for unseen or unspoken interactables, and optional later verification for music or art screens if they become available in the current save.

## Codebase Analysis Progress

### GATE: Tier 1 MUST be complete before Phase 2 (Framework)!

- [x] 1.1 Structure overview (namespaces, singletons) → documented in game-api.md
- [x] 1.2 Input system — ALL game key bindings documented in game-api.md "Game Key Bindings"
- [x] 1.2 Input system — Safe mod keys identified and listed in game-api.md "Safe Mod Keys"
- [x] 1.3 UI system (base classes, text access patterns, Reflection needed?)
- [x] 1.4 State management decision → documented in "Architecture Decisions" below
- [x] 1.5 Localization: game's language system analyzed

### GATE: Relevant Tier 2 items MUST be done before implementing each feature!

- [x] 1.6 Game mechanics (analyzed as needed per feature)
- [x] 1.7 Status/feedback systems
- [x] 1.8 Event system / Harmony patch points
- [x] 1.9 Results documented in `docs/game-api.md`
- [ ] 1.10 Tutorial analysis (when relevant)

## Game Key Bindings (Original)

- Documented in `docs/game-api.md`
- Game input is primarily action-based through Rewired rather than hardcoded `KeyCode` checks
- Hardcoded keys found so far and reserved: `F2`, `F3`, `F8`

## Implemented Features

- Startup announcement through Tolk/NVDA
- `F1` help hotkey
- `Ctrl+F1` repeat-last-spoken hotkey for tips and dialogue lines
- `F9` debug toggle hotkey
- `Ctrl+F9` accessibility settings hotkey
- Spoken accessibility settings menu with persistent toggles for focused items, dialogue text, dialogue choices, screen text, phone app text, room changes, nearby objects, and status changes
- Automatic speech for focused UI controls
- Automatic speech for active dialogue lines
- Automatic speech for dialogue choice focus
- Arrow-key dialogue choice navigation
- `Enter` / `Space` choice activation
- Automatic screen summaries for phone/menu contexts
- Automatic phone app content announcements with visible-text fallback
- Automatic object examination text announcements
- Automatic room announcements in house exploration
- Automatic nearby interactable announcements
- Automatic Dateviators equip/charge state announcements

## Pending Tests

- Test how nearby interactable announcements behave for objects the player has not scanned or spoken to yet, especially that unmet datables use object names and met datables switch to character names.
- Identify more gameplay states and visible text that still need to be surfaced to speech.
- If music or art screens become available later in normal play, verify their speech coverage in runtime because they were not discoverable during the current test pass.

## Recent Test Results

- Passed in game on 2026-03-25: Date A Dex list focus now reads only the datable name, and opening a datable now speaks the visible biography without being cut off by the initial `Collectables` focus announcement.
- Passed locally on 2026-03-25: cleaned up the temporary Date A Dex and Wrkspace diagnostic instrumentation after the working fix was confirmed, while keeping the Harmony `DateADex.OpenEntry(int)` hook and the final focus-suppression behavior in place.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after the first Date A Dex post-open suppression pass, which now suppresses `DexEntryButton` and `CollectablesButton` focus speech for a short window immediately after the `OpenEntry(int)` hook speaks the bio.
- Failed in game on 2026-03-25: the new `DateADex.OpenEntry(int)` hook does fire and announces the correct Skylar bio, but the game then immediately re-announces `SKYLAR SPECS. 1`, shifts focus to `Collectables. 1 / 3`, and later repeats the bio again from polling. The initial-open problem is therefore no longer missing bio data; it is competing post-open speech from list-entry and default-button focus after the hook fires.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after adding Harmony patching to the mod, hooking `DateADex.OpenEntry(int)` in a postfix, and routing that event into a watcher-side one-shot Date A Dex announcement path so the entry bio can be spoken when the game finishes populating the screen instead of waiting on focus polling.
- Failed in game on 2026-03-25: Date A Dex still does not read the biography when opening a datable from the list. The current behavior is that entry-open first speaks an unrelated placeholder or fallback line, but after returning to the datable entry screen later, moving focus between entry controls can speak that datable's bio. This points to an entry-transition timing issue rather than a steady-state detail-reader failure.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after the follow-up Date A Dex fix that makes bio detail detection use the actual `MainEntryScreen` visibility, suppresses post-bio `Collectables` focus briefly after a Date A Dex detail announcement, and blocks the noisy Date A Dex full-window visible-text fallback when no dedicated detail reader has won yet.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after using the runtime logs to make two evidence-based fixes: Wrkspace `ChatButton` focus now bypasses chat-detail suppression, and `CollectablesScreen` speech now only activates when the current focus is actually inside the collectables UI, so it should stop hijacking normal Date A Dex bio announcements.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after adding targeted debug-only logging for Wrkspace selection, Date A Dex detail assembly, phone-app content routing, and selection tracing in those phone-app contexts, and copied `DateEverythingAccess.dll` into the BepInEx plugins folder.
- Passed locally on 2026-03-25: `dotnet build .\DateEverythingAccess.csproj --no-restore -p:SkipCopyToPlugins=true` succeeded after adding targeted debug-only logging for Wrkspace selection, Date A Dex detail assembly, phone-app content routing, and selection tracing in those phone-app contexts.
- Reverted locally on 2026-03-25: rolled back the unsuccessful Wrkspace and Date A Dex watcher experiments after they produced no in-game change; the next step is targeted runtime logging rather than further blind focus or suppression changes.
- Passed locally on 2026-03-25: created the public GitHub repository `https://github.com/ObjectInSpace/date-everything-access`, added `origin`, and pushed branch `main`.
- Passed locally on 2026-03-25: initialized a local git repository in `c:\Users\amock\mod template`, updated `.gitignore` to exclude local `.dotnet` caches, created the initial commit, and renamed the default branch to `main`.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after the SPECS source-map refinement pass and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after extending first-time SPECS suppression across the tutorial dialog sequence and preventing phone-app fallback from reading underlying SPECS text while that sequence is pending, and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after adding an initial SPECS-open grace window so first-time `UIDialog` tutorial alerts are not raced by immediate SPECS summary or glossary announcements, and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after splitting phone app content onto its own `Phone app text` accessibility toggle and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after consolidating phone app speech into a single current-app content announcer with visible-text fallback and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after adding dedicated `ExamineController` speech for object examination text and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after the SPECS, credits, and `UIDialogManager` speech pass and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after the popup alert speech fix and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after the save/load slot speech pass and copied `DateEverythingAccess.dll` to the BepInEx plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` succeeded after moving `Ctrl+F1` repeat handling onto the watcher update loop so it can read current dialogue or visible screen text on demand even when automatic screen text is disabled.
- Passed locally on 2026-03-25: `dotnet build .\DateEverythingAccess.csproj --no-restore -p:SkipCopyToPlugins=true` compiled the new Rewired input-mapping dump path successfully.
- Passed locally on 2026-03-25: the rebuilt `DateEverythingAccess.dll` with the Rewired input-mapping dump was copied into the game's BepInEx plugins folder manually after the standard build script hit the sandboxed dotnet first-run and NuGet restore issues.
- Passed locally on 2026-03-25: after the first runtime dump reported no active controllers, `InputMappingReporter` was updated to inspect both Rewired `Player0` and `SystemPlayer`, dedupe overlapping controllers, and label which player owns each dumped map.
- Passed locally on 2026-03-25: because `dotnet build` lost access to the `net472` targeting pack, the updated DLL was rebuilt successfully with Roslyn `csc.dll` against the game's `netstandard.dll` and existing managed references, then recopied into the BepInEx plugins folder.
- Passed locally on 2026-03-25: `AccessibilityWatcher` was updated so newly activated chat entries get a short settle window and suppress button-label chatter until the visible transcript announcement is ready, then the rebuilt DLL was copied into the BepInEx plugins folder.
- Passed locally on 2026-03-25: `dotnet build --no-restore .\DateEverythingAccess.csproj` compiled the updated watcher with temporary SPECS and save/load selection logging, and failed only on the final copy step because the game still had `DateEverythingAccess.dll` locked in the plugins folder.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` rebuilt the temporary SPECS and save/load logging pass and copied `DateEverythingAccess.dll` into the BepInEx plugins folder successfully.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` rebuilt the follow-up SPECS and save/load behavior patch and copied `DateEverythingAccess.dll` into the BepInEx plugins folder successfully.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` rebuilt the SPECS detail-gate and button-label follow-up patch and copied `DateEverythingAccess.dll` into the BepInEx plugins folder successfully.
- Passed locally on 2026-03-25: `.\scripts\Build-Mod.ps1` rebuilt the SPECS current-page detection fix and copied `DateEverythingAccess.dll` into the BepInEx plugins folder successfully.
- Passed in game on 2026-03-25: spoken accessibility settings menu works reliably and toggles persist between launches.
- Passed in game on 2026-03-25: spoken settings menu responds reliably to arrow keys, and `Enter` and `Space` toggle the current item.
- Passed in game on 2026-03-25: spoken settings menu blocks the game from consuming arrows, confirm, and cancel inputs while it is open.
- Passed in game on 2026-03-25: dialogue, screen text, loading tips, and other live text updates still speak while the spoken settings menu is open.
- Passed in game on 2026-03-25: `Ctrl+F1` repeats the latest dialogue line or loading-screen tip, then falls back cleanly to the last general announcement when no repeatable line exists.
- Passed in game on 2026-03-25: SPECS tutorial dialogs, stats filtering, glossary speech, descriptive SPECS button labeling, and description reveal or hide behavior all worked as intended.
- Passed in game on 2026-03-25: credits speech announces the visible credits text and updates sensibly while the credits scroll.
- Passed in game on 2026-03-25: popup and alert speech reads the title and body before the auto-focused confirmation button, and button focus still announces correctly afterward.
- Passed in game on 2026-03-25: disabling dialogue text and dialogue choices suppresses their speech without breaking dialogue navigation.
- Passed in game on 2026-03-25: Roomers, Date A Dex, chat apps, save or load slot speech, and `Ctrl+F1` on-demand screen reading all behaved correctly in runtime verification.

## Known Issues

- No public source repository was found for Date Everything!, so this project uses the separate-mod workflow.
- The game relies heavily on Rewired action maps, so default physical keyboard keys are not all visible from code alone.
- The decompiled `TranslationManager` appears to ignore non-English text output in this build, so mod localization still needs its own fallback behavior.

## Architecture Decisions

- `AGENTS.md` is the primary control file for Codex. `CLAUDE.md` and `CLAUDE.de.md` are compatibility notices only.
- Use `BepInEx` for this project because current public Date Everything! mods target BepInEx.
- Use `ILSpy CLI` for a terminal-first, automatable, screen-reader-friendly decompilation workflow.
- Plan multilingual support from the start with `Loc.cs`.
- Prefer CLI-driven installation steps when possible, asking for approval when elevated access or network access is required.
- Base framework verified: BepInEx loads `Date Everything Access 0.1.0`, NVDA is detected, and the startup announcement path runs.
- Use `ControllerMenuUI` as the authoritative focus writer because it enforces the same navigation rules as the game UI.
- Use reflection against `TalkingUI` and `DialogBoxBehavior` for dialogue text and choice extraction until a cleaner event path is needed.

## Key Bindings (Mod)

- F1: Help
- Ctrl+F1: Repeat last spoken line
- F9: Toggle debug mode
- Ctrl+F9: Accessibility settings

## Notes for Next Session

- Date A Dex now uses a Harmony `DateADex.OpenEntry(int)` hook plus watcher-side entry suppression so opening a datable speaks the visible biography before entry controls such as `Collectables` are allowed to interrupt.
- Wrkspace contact navigation is currently working again, and the temporary debug-only logging used to track the regressions has been removed from the live build.
- Public GitHub repository is now available at `https://github.com/ObjectInSpace/date-everything-access` and `main` tracks `origin/main`.
- Local git repository now exists with an initial commit on branch `main`; GitHub CLI is authenticated as `ObjectInSpace`, so only the target repository name and public/private choice are still needed before creating the remote and pushing.
- Spoken settings menu input is now processed from `AccessibilityWatcher.Update()` instead of `Main.Update()`.
- The spoken settings menu now uses a Win32 key-state fallback for arrows and also accepts `Enter` and `Space` to toggle the current option.
- While the spoken settings menu is open, the mod pushes the game's input stack to `None` so Rewired gameplay and UI actions do not consume those keys.
- While the spoken settings menu is open, live text updates continue, but focused-item selection speech stays suppressed to avoid fighting the menu prompts.
- `Ctrl+F1` now prefers the latest dialogue line or loading-screen tip, then falls back to the last general announcement if needed.
- Pressing `F9` now enables debug mode and also dumps the current live Rewired keyboard, mouse, and joystick maps to the BepInEx log so exact physical bindings can be inspected without guessing from action ids alone.
- The input-mapping dump now falls back to Rewired `SystemPlayer` when `Player0` has not yet taken ownership of the active controllers, which covers pre-engagement and menu states more reliably.
- Chat-entry activation now uses chat-specific pending-detail handling in `AccessibilityWatcher`, so selecting a chat thread should defer the button label briefly and then speak the visible transcript or reply options once the active chat panel has settled.
- `Ctrl+F1` repeat requests now get handed off from the background hotkey thread to `AccessibilityWatcher.Update()` so the mod can safely inspect Unity UI state before speaking.
- `Ctrl+F1` now tries the current dialogue or current visible screen-text context first, even when automatic screen text is turned off, and only then falls back to the remembered repeatable line.
- Local build succeeded after the repeat-last-spoken hotkey pass.
- Phone and app text should prefer full on-screen text coverage rather than shorter summaries.
- `docs/game-api.md` now includes an app-by-app phone text source map:
  Roomers uses `RoomersEntryButton` plus `RoomersInfo` and `RoomersTip`.
  Date A Dex uses `DexEntryButton`, direct bio fields, list summary counters, and `CollectablesScreen`.
  Wrkspace, Canopy, and Thiscord render message text from Ink into `ParallelChat` and `ChatTextBox`, with choices coming from `currentChoices`.
  Music uses `MusicEntryButton` and `MusicPlayer.SongTitle`.
  Art uses `ArtEntryButton` plus `ArtPlayer.selectedArt.title`; `ArtTitle` exists but no assignment path was found in the decompiled scan.
  Save/load, settings, specs, credits, and popups expose their own TMP fields and can be handled separately from the main phone app summaries.
  More specifically, `SaveSlot` owns the visible slot metadata in `Name`, `Date`, `Time`, `playTime`, and `daysPlayed`, while `SaveScreenManager` keeps the special `newSaveSlot` entry in a private field and focuses its child button rather than the text container.
- `AccessibilityWatcher` now:
  treats first-time SPECS tutorial dialogs as a short pending sequence instead of only a one-frame open delay, so SPECS summary, glossary or stats detail, SPECS focus speech, and phone-app visible-text fallback all stay suppressed until that tutorial dialog chain has had time to appear and settle,
  applies a short initial SPECS-open grace window before any SPECS summary, glossary, stats, or SPECS focus speech can fire, so the first tutorial `UIDialog` alert has time to appear and speak first,
  gates automatic phone app content speech behind its own `Phone app text` setting instead of the broader `Screen text` toggle, and only suppresses Date A Dex or chat focus chatter when that phone app content setting is enabled,
  consolidates phone app text speech into a single current-app content pass that prefers app-specific readers but falls back to visible TMP text from the live phone app roots, including the extra Roomers and Date A Dex widget roots when those apps are open,
  reads `ExamineController.ExamineText` while `ExamineController.isShown` so object examination text is spoken immediately and available to `Ctrl+F1` repeat,
  suppresses the popup's auto-focused default button briefly after a new popup announcement so the alert title/body is spoken instead of being interrupted by `OK` or `Yes`,
  reads `UIDialogManager` title/body text and briefly suppresses the dialog's auto-focused button so tutorial and utility dialogs do not collapse to `OK`,
  follows the decompiled SPECS source map more closely by reading stats-page speech only from visible `SpecStatBlock` name, value, and adjective fields, reading tooltip descriptions only from active `statTooltips`, reading glossary speech only from visible `SpecGlossaryBlock` entries, giving the initial SPECS button an explicit descriptive label instead of its raw glyph text, and briefly suppressing the default SPECS focus target so the content can finish speaking,
  announces credits from `CreditsScreen.tmp_credits` using the visible masked text instead of only the focused `Back` button,
  extends Roomers detail speech with clue text and empty-state text,
  extends Date A Dex detail speech with voice actor, likes, dislikes, pronouns, list summary, collectable count, and recipe text when visible, but only treats the entry and recipe panes as detail-visible so opening an entry resets stale list-state caching and should announce the bio immediately,
  filters Date A Dex bio fields against `DescScroll` so it only reads text blocks that are actually visible on screen instead of the full off-screen entry payload,
  more specifically, Date A Dex now clips the long `Desc` text to visible lines in the scroll viewport, while reading active side fields like voice actor, likes, dislikes, and pronouns normally,
  filters inactive TMP text out of generic UI extraction and gives Date A Dex its own selection handling so list entries announce their real visible labels instead of hidden placeholder text, while entry and recipe screens suppress focus chatter and defer to the detail announcer,
  also uses Date A Dex entry or recipe focus itself as a trigger to speak the current detail immediately and sync the detail cache so the separate detail poll does not repeat it,
  and now gives Date A Dex controls explicit labels such as Collectables, Sort, Recipe, Show Bio, and Back instead of reading only raw child text like `3 / total`,
  and applies a short Date A Dex focus-suppression window after a new bio or recipe detail announcement so the focused control does not immediately interrupt the content speech,
  and now debounces new Date A Dex detail text for a short settle window before speaking it, while suppressing focus speech whenever a new unsaid detail is pending,
  reads only the visible chat transcript window from `ParallelChat.Chatbox` using the `ScrollRect` viewport, removes the old latest-message fallback, and reads visible options from `ParallelChat.Options`,
  and announces art with the selected entry number plus title,
  and treats save/load focus as its own context so the new-save entry uses the `newSaveSlot` label path while existing saves announce parent `SaveSlot` metadata instead of the focused `Save` or `Load` button text.
- Latest SPECS regression fix:
  active `UIDialog` overlays now suppress underlying SPECS announcements,
  dialog-focus suppression now checks the actual dialog root as well as dialog buttons,
  SPECS stat and glossary blocks are matched before the page-toggle button,
  and stats-page overview and stat-block focus both include the visible stat descriptions again.
- Latest SPECS fallback change:
  `BuildSelectionAnnouncement()` now gives active single-button `UIDialog` overlays their title/body text,
  and the SPECS page-toggle selection now appends the current stats or glossary content instead of speaking only `Return to stats` or `Open glossary`.
- Latest SPECS hardening change:
  single-button `UIDialog` selections now stay suppressed for the full life of the dialog instead of only for the short timer window,
  and the SPECS page-toggle fallback now matches only the exact toggle controls instead of broad descendant checks that could collapse unrelated SPECS focus to the toggle label.
- Save/load button-focus fix:
  slot action buttons now return explicit button labels for Delete, Load, and Save before falling back to slot metadata, so focusing Delete should no longer read the slot name.
- Latest follow-up patch from the runtime logs:
  single-button `UIDialog` alerts now announce their title and body even when automatic screen-text reading is off,
  save/load slot `Save` and `Load` button focus now falls back to the slot metadata instead of speaking only the button label,
  and SPECS `KeyButton` and auto-fallback button focus no longer reuse the full stats or glossary page overview, so that content should stay on the detail-announcement path instead of repeating on every button focus.
- Latest SPECS follow-up patch:
  SPECS stats and glossary detail announcements now speak even when general screen-text speech is disabled,
  and the SPECS auto-fallback button now uses an explicit page-aware label instead of inheriting arbitrary child text.
- Latest SPECS page-state fix:
  the watcher now reads `SpecStatMain.currentPage` directly instead of inferring the page from active glossary objects,
  so the opening SPECS announcement and the glossary or stats button labels should now follow the real current page.
- Saved app-message data stores chat structure and stitch history, but not a ready-made rendered transcript, so full chat speech should prefer the live `ParallelChat` UI and Ink state.
- Local compile verification worked earlier in this environment, but the latest deploy attempt on 2026-03-25 failed only because the game had `DateEverythingAccess.dll` locked in the plugins folder while the project itself still built before the copy step.
- Dateviators equip and charge announcements tested well in normal play.
- Room announcements are acceptable for now, but numeric cues are not very meaningful; add spatial navigation cues where possible.
- Nearby interactables seem acceptable for already scanned objects, but unseen or unspoken object handling still needs explicit testing.
- Nearby interactable naming now uses the object-facing label while `Save.GetDateStatus(...)` is `Unmet`, then switches to the resolved character name after the player has met that datable.
- Add richer coverage for more gameplay states and visible UI text.
- Music and art screens were not discoverable during the 2026-03-25 runtime pass, so their phone coverage remains unverified but is no longer blocking the confirmed watcher fixes.
- Latest Date A Dex focus fix:
  the `OpenEntry(int)` hook now starts the Date A Dex open-entry focus suppression window immediately so the first bio announcement is less likely to be interrupted by the focused entry or collectables control,
  and generic phone-app content speech now skips Date A Dex list-entry focus so selecting a datable in the list should speak only the datable name instead of stale placeholder entry text such as `Something's Wrong here`.
- Latest Date A Dex timing hardening:
  the open-entry suppression window now expands to the whole visible Date A Dex entry screen instead of only the original narrow button check,
  and its duration is now estimated from the spoken bio length so a long entry should finish before `Collectables` focus speech is allowed to interrupt it.
- Decide which gameplay events should become interrupting announcements versus passive status speech.
- Expand analysis only where the next feature needs it, especially tutorial flows and message log extraction.
