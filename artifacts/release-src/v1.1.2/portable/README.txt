Date Everything Access Portable Package
Version: 1.1.2
Repository: https://github.com/ObjectInSpace/date-everything-access

THIS PACKAGE ALREADY INCLUDES THE MOD LOADER
============================================
This ZIP already contains the BepInEx loader files needed to run the mod.
You do not need to install BepInEx separately for this package.

This package includes:
- BepInEx loader files
- DateEverythingAccess.dll
- navigable_region.bake.json
- card_pose_descriptions.en.json
- Tolk.dll
- nvdaControllerClient64.dll
- Install-DateEverythingAccess.ps1

WHAT THIS MOD DOES
==================
Date Everything Access adds screen reader support for Date Everything!.

Current coverage includes:
- Startup announcement
- Focused UI item announcements, including icon-only buttons read by name
- Dialogue text and dialogue choice announcements
- Backtick repeat-last-spoken support
- Spoken accessibility settings menu
- Spoken game settings menu (from the main menu and from the phone), including
  the Controls tab
- Phone app and menu text announcements
- Date A Dex biography announcements, full profile via PageUp/PageDown
- Date A Dex collectable icon names and descriptions
- Chat transcript and chat reply announcements
- Object examination text
- Room change announcements
- Nearby interactable announcements, including whether an approached door is
  closed
- Dateviators equip and charge announcements
- Time-of-day and day-of-week announcements
- Spoken results for interactions that only change the scene visually, such as
  the thermostat temperature and light switches
- Audio guidance tone for walking to a selected object yourself, plus
  auto-walk routing using the simple navigation bake
- Improved door handling for route traversal
- Tutorial objective routing for the opening computer, gift delivery trigger, and delivery box
- Known-objects picker organized by room, then datable, then object
- Spoken descriptions of the fullscreen datable pose cards (first meeting and
  Love / Hate / Friend / Realized endings), read after the card stinger plays
- English and Japanese, following the game's own text-language setting

WHAT'S NEW IN 1.1.2
===================
- Fixed the pose-card description for the curtains (Curt and Rod). Their card
  was staying silent because of an internal naming mismatch; it now reads the
  description of both characters when the card appears.
- Lowered the overall volume of the object-tracking guidance tone.
- Dunk's sports equipment now announces the specific item (Baseball, Dumbbell,
  Tennis racket, Kettlebell, Yoga mat, and so on) instead of the generic
  "sports equipment" label, so the pieces the storyline asks you to pick can be
  told apart. Available in English and Japanese.

WHAT'S NEW IN 1.1.1
===================
- The mod now speaks aloud through the Windows system voice when no screen
  reader is running, instead of staying silent. A running screen reader is
  still used whenever one is present.
- Fixed the startup and help announcements: they now correctly say Backspace
  closes the known-objects list (the close key had changed but the spoken text
  still said Escape).

WHAT'S NEW IN 1.1
=================
- Simpler keyboard shortcuts. The navigation commands moved off the F6 chords
  onto single keys: L reports the current room and nearby objects, O opens the
  known objects list, Ctrl+O tracks the current objective, Alt+O toggles
  auto-walk, and backtick repeats the last spoken line. Ctrl+F1 opens the
  accessibility settings. The game's hidden secondary binding that made the O
  key walk forward is disabled so O is safe to use.
- The navigation guidance tone was redesigned around one meaning per sound:
  * The tone always leads from a couple of meters ahead of you on the planned
    path, so keeping it centered and walking forward is always correct. It
    glides smoothly around corners instead of jumping.
  * The tone pulses like a parking sensor: the pulse gets faster as you
    approach the next landmark on the route (a door, the stairs, or the target
    itself) and slows if you head the wrong way.
  * A short chirp plays when you pass a landmark. The old wrong-way buzz is
    gone; it could sound during normal forward walking and made routes feel
    circular.
  * The tone gets duller when the path point is behind you, so "turn around"
    is audible.
- Arriving by walking yourself is now announced, and the tone switches to a
  steady aiming sound aimed at the object itself: if the tone is lower than
  the walking tone, tilt the camera up; if higher, tilt down. When the camera
  lands on the object and the game selects it, a chirp confirms and the tone
  stops.
- The guidance tone is now a fuller, richer tone instead of a pure beep, and
  its pitch register (low, mid, or high) can be chosen in the accessibility
  settings to suit your hearing.
- Walking up to a closed door now announces it as closed, so you know to
  interact before trying to walk through.
- Navigation routes now prefer approaching objects from the front, so
  directional objects such as the computer monitor are faced when you arrive.

WHAT'S NEW IN 1.0
=================
- F6 room scan now only reports objects you can actually see. Objects are
  included only when they are in your line of sight and their model is actually
  drawing, so things behind walls or off screen are no longer read out.
- Items inside closed cabinets and drawers are hidden from the scan until the
  container is opened, instead of being read through the closed door.
- The scan now also includes datables you have not met yet, not just ones you
  have already encountered, and de-duplicates repeats so each thing is reported
  once.

WHAT'S NEW IN 0.99
==================
- Japanese support: the mod now follows the game's text-language setting and
  speaks in Japanese when the game is set to Japanese, and English otherwise.
  (Only the languages the game itself offers are supported.)
- Interaction feedback: interactions whose only feedback in the base game is
  visual are now spoken. Flipping a light switch announces the light on or off
  by name, and the thermostat announces its new temperature (cold or room
  temperature). Interactions that already make a distinct sound, such as the
  faucet or fireplace, are left alone.
- Objective tracking now falls back to helpful nearby targets when there is no
  active tutorial objective: it steers toward the key (or the rat trap once the
  key is taken) while you are in the crawlspace, Zoey while her ghost is
  present, Ayrin while the thermostat is set to cold, and the dust bunny once
  the couch has revealed it. A real tutorial objective still takes priority.
  "Find more datables" now points only at datables you have not discovered yet.
- The day of the week from the calendar is now spoken together with the time of
  day, so time changes read as, for example, "Monday, morning".
- Known objects are remembered better: objects you have examined or interacted
  with (opening a door, moving a box, a light switch) now stay in the known
  objects list across a save and reload, saved per save slot.
- Navigation: fixed the laundry closet door, which could not be routed to
  because its target was being resolved onto the wrong floor.

INSTALLATION OPTIONS
====================
Option 1: Run the installer script
1. Close the game.
2. Extract this ZIP anywhere.
3. Run Install-DateEverythingAccess.ps1.
4. Enter your Date Everything! game folder when prompted.
5. Start the game.

Option 2: Copy files manually
1. Close the game.
2. Copy all files and folders from this ZIP into your main Date Everything! game folder.
3. Allow Windows to merge the included BepInEx folder.
4. Start the game.

EXPECTED RESULT
===============
The game folder should end up containing:
- winhttp.dll
- doorstop_config.ini
- .doorstop_version
- BepInEx\core\...
- BepInEx\plugins\DateEverythingAccess.dll
- BepInEx\plugins\navigable_region.bake.json
- BepInEx\plugins\card_pose_descriptions.en.json
- Tolk.dll
- nvdaControllerClient64.dll

CONTROLS
========
F1 - Help
` (backtick) - Repeat the last spoken line
L - Report the current room and objects relative to the direction you face
O - Open the known objects list
Ctrl+O - Track the current objective
Alt+O - Toggle auto-walk to the selected target
Ctrl+F1 - Open accessibility settings
F9 - Toggle debug mode
PageUp / PageDown - Step through sections on the SPECS, Rumors, and Date A Dex
  detail screens (Date A Dex reads the full profile description this way)

In the known objects list: Up and Down move the selection, Enter selects,
Escape closes, Left and Right change the sort, F toggles this floor only,
M cycles the section filter, and D toggles doors only.

NAVIGATION GUIDANCE TONE
========================
After selecting a target (Ctrl+O objective or a pick from the O list), a
guidance tone leads you along a planned path:
- Keep the tone centered between your ears and walk forward. The tone leads
  from a couple of meters ahead on the path and glides around corners.
- The tone pulses faster as you approach the next landmark (a door, the
  stairs, or the target itself) and slower if you head away from it.
- A chirp means you passed a landmark. A duller tone means the path point is
  behind you - turn around.
- On arrival the mod announces it and the tone goes steady, now aimed at the
  object itself: tone lower than the walking tone means tilt the camera up,
  higher means tilt down. A final chirp confirms the game has selected the
  object, and the tone stops.
Alt+O instead walks you there automatically with the same tone playing.
The tone's pitch register (low, mid, high) is selectable in the accessibility
settings (Ctrl+F1).

LANGUAGE
========
The mod speaks in the language selected by the game's text-language setting.
English and Japanese are supported, matching the languages the game itself
offers. Changing the game's language changes the mod's spoken language too.

POSE CARD DESCRIPTIONS
======================
When a datable's fullscreen pose card appears (first meeting, or a Love / Hate /
Friend / Realized ending), the mod speaks a written description of the pose.
Descriptions live in card_pose_descriptions.en.json in the BepInEx\plugins folder
and can be edited or expanded in any text editor without reinstalling the mod.
This release ships a description for every datable's first-meeting card; the
relationship-ending pose cards do not yet have their own descriptions and stay
silent. While a card is on screen you can press backtick to repeat its
description.

The appearance descriptions are quoted from the Date Everything Wiki
(https://dateeverything.wiki.gg/) and are licensed under Creative Commons
Attribution-ShareAlike 4.0 (CC-BY-SA 4.0). See THIRD-PARTY-NOTICES.txt.

TROUBLESHOOTING
===============
- If the mod does not load, confirm that winhttp.dll and the BepInEx\core folder are present in the game folder.
- If the screen reader stays silent, confirm that Tolk.dll and nvdaControllerClient64.dll are next to the game executable.
- If copying fails, make sure the game is fully closed before installing.

KNOWN LIMITATIONS
=================
- Some broader gameplay states still need more runtime coverage.
- Nearby interactables for unseen objects need additional verification.
- The relationship-ending pose cards (Love / Hate / Friend / Realized) do not
  yet have written descriptions and stay silent.
- Pose card descriptions are currently English only; when the game is set to
  Japanese the spoken interface is Japanese but the pose descriptions fall back
  to English.

SUPPORT
=======
Report issues at:
https://github.com/ObjectInSpace/date-everything-access/issues
