Date Everything Access Portable Package
Version: 0.1.1
Repository: https://github.com/ObjectInSpace/date-everything-access

THIS PACKAGE ALREADY INCLUDES THE MOD LOADER
============================================
This ZIP already contains the BepInEx loader files needed to run the mod.
You do not need to install BepInEx separately for this package.

This package includes:
- BepInEx loader files
- DateEverythingAccess.dll
- navigation_graph.json
- navigation_transition_overrides.json
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
- BepInEx\plugins\navigation_graph.json
- BepInEx\plugins\navigation_transition_overrides.json
- Tolk.dll
- nvdaControllerClient64.dll

CONTROLS
========
F1 - Help
Ctrl+F1 - Repeat the last spoken line or current visible context
F9 - Toggle debug mode
Ctrl+F9 - Open accessibility settings

TROUBLESHOOTING
===============
- If the mod does not load, confirm that winhttp.dll and the BepInEx\core folder are present in the game folder.
- If the screen reader stays silent, confirm that Tolk.dll and nvdaControllerClient64.dll are next to the game executable.
- If copying fails, make sure the game is fully closed before installing.

KNOWN LIMITATIONS
=================
- Some broader gameplay states still need more runtime coverage.
- Nearby interactables for unseen objects need additional verification.
- Music and art phone screens were not available for full runtime verification in the current save.

SUPPORT
=======
Report issues at:
https://github.com/ObjectInSpace/date-everything-access/issues
