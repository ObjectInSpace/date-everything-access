Date Everything Access
Version: 0.1.0
Repository: https://github.com/ObjectInSpace/date-everything-access

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

REQUIREMENTS
============
- Date Everything! on Windows
- BepInEx 5.4.23.5 x64 installed for the game
- A screen reader
  NVDA is supported directly through the included NVDA controller DLL.
  Other Tolk-compatible screen readers may also work, but NVDA is the primary tested path.

INSTALLATION
============
1. Install BepInEx 5.4.23.5 x64 for Date Everything! if it is not already installed.
2. Close the game before copying files.
3. Copy everything from this ZIP into your main Date Everything! game folder.
4. Allow Windows to merge the included BepInEx folder with your existing one.
5. Start the game. You should hear a startup announcement from the screen reader.

The ZIP should leave these files in place:
- BepInEx\plugins\DateEverythingAccess.dll
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
- If the mod does not load, check BepInEx\LogOutput.log for errors.
- If the screen reader stays silent, confirm that Tolk.dll and nvdaControllerClient64.dll are in the game folder next to the game executable.
- If an updated DLL does not copy over, make sure the game is fully closed first.

KNOWN LIMITATIONS
=================
- Some broader gameplay states still need more runtime coverage.
- Nearby interactables for unseen objects need additional verification.
- Music and art phone screens were not available for full runtime verification in the current save.

SUPPORT
=======
Report issues at:
https://github.com/ObjectInSpace/date-everything-access/issues
