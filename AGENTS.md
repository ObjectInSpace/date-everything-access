# Accessibility Mod Template

## User

- Blind, screen reader user
- Experience level: ask during setup and adjust communication
- User directs, Codex codes and explains
- For uncertainties: ask briefly, then act
- Output: NO `|` tables, use lists

## Session Start

On greeting or new project:
- If `project_status.md` does not exist: read `docs/setup-guide.md` and run the setup interview one question at a time. Prefer `winget` and CLI tools for installations where possible.
- If `project_status.md` exists: read it first, summarize the current phase, last work, pending tests, and notes. If pending tests exist, ask for results before continuing. Then suggest next steps or ask what to work on.

`project_status.md` is the central tracking document. Update it on significant progress and always before session end.

## Environment

- **OS:** Windows. ALWAYS use Windows-native commands when working inside this template. Use PowerShell or cmd commands, Windows paths, and backslashes.
- **Game directory:** `D:\SteamLibrary\steamapps\Common\Date Everything`
- **Architecture:** `64-bit`
- **Mod Loader:** `BepInEx 5.4.23.5`
- **Unity version:** `2022.3.28.7056988`
- **CLR runtime:** `4.0.30319.42000`

## Tolk DLLs - Setup Reminder

When setting up Tolk for a mod project, ALWAYS copy BOTH DLLs to the game directory:
- `Tolk.dll` - screen reader bridge library
- `nvdaControllerClient64.dll` or `nvdaControllerClient32.dll` - required for NVDA support

## Coding Rules

- Handler classes: `[Feature]Handler`
- Private fields: `_camelCase`
- Logs and comments: English
- Build: `.\scripts\Build-Mod.ps1`
- Build and deploy: `.\scripts\Deploy-Mod.ps1`
- XML docs: add `<summary>` on all public classes and methods. Private members only if non-obvious.
- Localization from day one: ALL screen reader strings go through `Loc.Get()`. No exceptions.

## Coding Principles

- **Playability** - match the sighted experience; cheats only if unavoidable
- **Modular** - separate input, UI, announcements, and game state
- **Maintainable** - consistent patterns, extensible structure
- **Efficient** - cache object references, not stale values; always read live data
- **Robust** - cover edge cases and announce state changes
- **Respect game controls** - never override game keys blindly; handle rapid input
- **Submission-quality** - clean enough for developer integration

Patterns: `docs/ACCESSIBILITY_MODDING_GUIDE.md`

## Error Handling

- Null-safety with logging: never fail silently. Log via `DebugLogger` and announce via `ScreenReader`.
- Use try/catch only for reflection and external calls such as Tolk or unstable game APIs. Use null checks for normal code.
- `DebugLogger` should always exist and only become active in debug mode.

## Before Implementation

1. Gate check: Tier 1 analysis must be complete before Phase 2. If game key bindings are not documented in `docs/game-api.md`, stop and do that first.
2. Search `decompiled/` for real class and method names. Never guess.
3. Check `docs/game-api.md` for keys, methods, and patterns.
4. Only use safe mod keys from `docs/game-api.md`.
5. For files over 500 lines, use targeted search before reading the whole file.

## Session and Context Management

- After a feature is done, suggest a new conversation to save tokens and update `project_status.md`.
- Around 30+ messages or when context is getting crowded, remind the user to start a fresh conversation.
- Before ending the session, always update `project_status.md`.
- Check `docs/game-api.md` before reading decompiled code, but verify against the actual decompiled source when unsure.
- After new code analysis, document findings in `docs/game-api.md` immediately.
- If a problem persists after 3 attempts, stop, explain, suggest alternatives, and ask the user how to proceed.

## References

- `project_status.md` - central tracking
- `docs/ACCESSIBILITY_MODDING_GUIDE.md` - code patterns
- `docs/technical-reference.md` - BepInEx, Harmony, Tolk
- `docs/unity-reflection-guide.md` - reflection patterns
- `docs/state-management-guide.md` - multiple handlers
- `docs/localization-guide.md` - localization
- `docs/menu-accessibility-checklist.md` - menu checklist
- `docs/menu-accessibility-patterns.md` - menu patterns
- `docs/known-issues.md` - compatibility warnings checked during setup
- `docs/legacy-unity-modding.md` - Unity 5.x and older
- `docs/game-api.md` - keys, methods, patterns
- `docs/distribution-guide.md` - packaging and publishing
- `docs/git-github-guide.md` - Git and GitHub intro
- `templates/bepinex/` - BepInEx-specific templates
- `templates/shared/` - mod-loader-independent templates
- `scripts/` - PowerShell helpers

## Notes

- Project file: `DateEverythingAccess.csproj`
- Plugin GUID: `com.amock.dateeverythingaccess`
- Target framework: `net472`
- Tolk and `nvdaControllerClient64.dll` are installed in the game directory
