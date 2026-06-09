Date Everything Access Portable Package
Version: 0.6.0
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
- Focused UI item announcements
- Dialogue text and dialogue choice announcements
- Ctrl+F1 repeat-last-spoken support
- Spoken accessibility settings menu
- Phone app and menu text announcements
- Date A Dex biography announcements
- Chat transcript and chat reply announcements
- Object examination text
- Room change announcements
- Nearby interactable announcements
- Dateviators equip and charge announcements
- Object auto-walk routing using the current simple navigation bake
- Improved door handling for route traversal
- Tutorial objective routing for the opening computer, gift delivery trigger, and delivery box
- Known-objects picker with floor, section, and door filters
- Spoken descriptions of the fullscreen datable pose cards (first meeting and
  Love / Hate / Friend / Realized endings), read after the card stinger plays

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

POSE CARD DESCRIPTIONS
======================
When a datable's fullscreen pose card appears (first meeting, or a Love / Hate /
Friend / Realized ending), the mod can speak a written description of the pose.
Descriptions live in card_pose_descriptions.en.json in the BepInEx\plugins folder
and can be edited or expanded in any text editor without reinstalling the mod.
This release ships only a small set of starter descriptions; cards without a
description stay silent.

TROUBLESHOOTING
===============
- If the mod does not load, confirm that winhttp.dll and the BepInEx\core folder are present in the game folder.
- If the screen reader stays silent, confirm that Tolk.dll and nvdaControllerClient64.dll are next to the game executable.
- If copying fails, make sure the game is fully closed before installing.

KNOWN LIMITATIONS
=================
- Some broader gameplay states still need more runtime coverage.
- Nearby interactables for unseen objects need additional verification.
- Most pose cards do not yet have a written description and stay silent.

SUPPORT
=======
Report issues at:
https://github.com/ObjectInSpace/date-everything-access/issues
