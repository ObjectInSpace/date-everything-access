Date Everything Access Portable Package
Version: 1.0
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
- Ctrl+F1 repeat-last-spoken support
- Spoken accessibility settings menu
- Spoken game settings menu (from the main menu and from the phone), including
  the Controls tab
- Phone app and menu text announcements
- Date A Dex biography announcements, full profile via PageUp/PageDown
- Date A Dex collectable icon names and descriptions
- Chat transcript and chat reply announcements
- Object examination text
- Room change announcements
- Nearby interactable announcements
- Dateviators equip and charge announcements
- Time-of-day and day-of-week announcements
- Spoken results for interactions that only change the scene visually, such as
  the thermostat temperature and light switches
- Object auto-walk routing using the current simple navigation bake
- Improved door handling for route traversal
- Tutorial objective routing for the opening computer, gift delivery trigger, and delivery box
- Known-objects picker organized by room, then datable, then object
- Spoken descriptions of the fullscreen datable pose cards (first meeting and
  Love / Hate / Friend / Realized endings), read after the card stinger plays
- English and Japanese, following the game's own text-language setting

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
- Ctrl+F6 now falls back to helpful nearby targets when there is no active
  tutorial objective: it steers toward the key (or the rat trap once the key is
  taken) while you are in the crawlspace, Zoey while her ghost is present, Ayrin
  while the thermostat is set to cold, and the dust bunny once the couch has
  revealed it. A real tutorial objective still takes priority. "Find more
  datables" now points only at datables you have not discovered yet.
- The day of the week from the calendar is now spoken together with the time of
  day, so time changes read as, for example, "Monday, morning".
- Known objects are remembered better: objects you have examined or interacted
  with (opening a door, moving a box, a light switch) now stay in the known
  objects list across a save and reload, saved per save slot.
- Navigation: fixed the laundry closet door, which could not be routed to
  because its target was being resolved onto the wrong floor.

WHAT'S NEW IN 0.95
==================
- Speech: while walking around, looking quickly from one object or room to the
  next now cuts off the previous announcement instead of queueing them up, so
  you hear the thing you are currently looking at rather than a backlog. This
  matches how menus already behaved and completes the NVDA sleep-mode interrupt
  fix for the in-world announcements (nearby objects, room changes, status).
- Navigation: walk-in closets are now routable. They are narrow spaces the
  navigation grid previously eroded away entirely, so auto-walk could not enter
  them; the bake now recovers the interior and bridges the doorway so the
  follower can walk inside (e.g. the office and bedroom closets).
- Navigation: corrected the player height used by the navigation bake to match
  the real standing collider, which removed widespread false blocking under
  head-height furniture, windows, and counters and clears up several doorways
  and passages that were narrower than they should have been.

WHAT'S NEW IN 0.9
=================
- Icon-only buttons are now read by name everywhere instead of staying silent.
  This covers the phone app-launcher menu (Date-a-Dex, Roomers, SPECS, Save,
  Home, etc.) and any other labelless icon, so navigating those screens speaks.
- Date A Dex collectable icons: navigating the collectable grid now reads each
  item's name and description (or "Locked collectable" plus its hint for ones
  you have not unlocked yet).
- Date A Dex profile: PageUp/PageDown now reads the entire unlocked profile
  description, not just the lines currently scrolled into view.
- Settings: option titles are now read when the settings menu is opened from the
  phone, matching how it already worked from the main menu.
- Settings: the Controls tab now reads each row as its action and bound key.
- PageUp/PageDown section stepping (SPECS / Rumors / Date A Dex) now reliably
  catches key presses that were previously dropped, so it no longer goes silent.
- Speech now interrupts correctly when NVDA is in sleep mode: a new focus
  announcement cuts off the current one instead of being queued behind it.
- The mod's own menus (object list and accessibility settings) now back out and
  close with Backspace instead of Escape, which conflicts less with the game.
- Pose cards: several first-meeting cards that previously read silent now speak
  their description again. Their cards use a non-neutral pose or a variant name
  (Diana, Penelope, Connie, Mateo, Tina, Tydus, Wallace, the Dorian forms, Volt,
  Timmy, Jon Wick) that no longer prevents the description from being found, and
  Clarence now has his own description split out from Dirk's.

WHAT'S NEW IN 0.85
==================
- Pose card descriptions: every datable's first-meeting pose card now has a
  written physical description (102 in total), so the cards are no longer
  silent. Descriptions are sourced from the Date Everything Wiki.
- Ctrl+F1 now repeats the current pose card description while the card is on
  screen, even if other speech happened after the description first played.

WHAT'S NEW IN 0.8.0
===================
- Navigation: closet and sliding-door openings now keep a full walking-width path
  through the doorway, so the auto-walk no longer wedges entering a closet (the gym
  and bedroom closets in particular).
- Navigation reliability: the bake now refuses to ship a doorway that has been
  pinched too narrow for the player to pass, catching that class of routing bug
  before it reaches the game.

WHAT'S NEW IN 0.7.0
===================
- Known-objects picker reorganized around the game's own room layout: pick a
  room, then a met datable (announced by character name) or an object, then the
  objects for that datable in that room. Rooms come from the house data hierarchy
  rather than the unreliable camera zones.
- Duplicate entries collapsed: an object made of several pieces (for example a
  sofa with its cushions and pillows) now appears once and routes to the nearest
  piece.
- Navigation: door routing targets the doorway opening rather than the door
  pivot, so doors that previously could not be reached now route correctly.
- Roster cleanup: a leftover, non-interactable duplicate of the opening
  Dateviators box was removed from the object list.

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
Ctrl+F1 - Repeat the last spoken line or current visible context
F6 - Report the current room and nearby objects relative to your facing
Ctrl+F6 - Track the current objective
Ctrl+Shift+F6 - Open the known objects list
Ctrl+Alt+F6 - Toggle auto-walk to the selected target
F9 - Toggle debug mode
Ctrl+F9 - Open accessibility settings
PageUp / PageDown - Step through sections on the SPECS, Rumors, and Date A Dex
  detail screens (Date A Dex reads the full profile description this way)
Backspace - Back out of / close the mod's own menus (the known objects list and
  the accessibility settings menu)

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
silent. While a card is on screen you can press Ctrl+F1 to repeat its description.

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
