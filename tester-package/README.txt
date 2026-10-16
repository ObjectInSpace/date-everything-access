Narrate Everything
Based on Date Everything Access 1.2
Original mod: https://github.com/ObjectInSpace/date-everything-access

This fork smooths out navigation and small object tracking. It adds the option to use verbal cues when aiming at datables, and particular attention has been paid to making the autowalk feature more reliable. Currently, manual navigation works the same as the original mod.

How to Install
==============
1. Close the game.
2. Run DateEverythingAccess-Setup.exe.
   Windows may warn that the program is from an unknown publisher. Choose
   "More info", then "Run anyway".
3. The installer looks for your Steam copy of the game and asks before
   installing there. If it cannot find it, or you say No, it lets you pick
   your Date Everything! folder (the one containing Date Everything.exe).
4. Start the game. You should hear, "Date Everything Access loaded. Press F-1 for accessibility help."

The installer includes the BepInEx mod loader and the screen reader files,
so nothing else needs to be installed. Your existing mod settings are kept.

Changes
=======
- Improved autowalk's handling of furniture, doors, the staircase and overall navigation.
- Added a toggle for verbal cues when aiming at nearby datables. This can be toggled in accessibility settings. Switching it to Tone uses the sound cues from the original mod.
- When tracking objectives with control+O, a situational datable close by
  comes first.
- Then undiscovered datables in the room you're in, then the nearest room with an undiscovered datable.
- Doors are automatically trackable when a new game starts, examining or interacting with other items will add them to the list.
- The game camera no longer moves while the list is open.
- Added a mod tutorial that runs alongside the game's own.

Other
- Speech now goes through the Prism library instead of Tolk. It works with
  NVDA, JAWS and other screen readers, notices if you start or quit your
  screen reader while playing, and uses the Windows voice when none is
  running.
- Characters who speak in beeps instead of a voice line have their lines read aloud, even with dialogue reading turned off.
- While focused on the bulletin board in the office, you can use page-up and page-down to view its contents.
- Fixed a bug that caused the mod to carry discovered datables over from previous saves when starting a new game.

CONTROLS
========
F1 - Mod Help
` (backtick) - Repeat the last spoken line
H - Repeat the last mod tutorial message
L - Report the current room and objects relative to the direction you face
O - Open the known objects list
Ctrl+O - Track the current objective
Alt+O - Auto-walk to the selected target, or aiming help when already near it
W, A, S or D - Stop auto-walk and take over
Ctrl+F1 - Accessibility settings

In the known objects list: Up and Down move, Enter selects, Backspace closes,
Left and Right change the sort, F toggles this floor only, M cycles the
section filter, and D toggles doors only.

Situational Datables List
=========================
The following datables can be tracked with control+O, provided you're in the same room as them and have worked out their puzzles.
1. Keith, (This will switch to Vaughn once you've interacted with Keith).
2. Zoey
3. Airyn
4. Dolly

TROUBLESHOOTING
===============
- If the mod does not load, make sure winhttp.dll and the BepInEx folder are
  in the game folder, next to Date Everything.exe.
- If your screen reader stays silent, make sure prism.dll is next to
  Date Everything.exe. With no screen reader running, the mod speaks with
  the Windows voice instead.
- If installing fails, make sure the game is fully closed and try again.

See THIRD-PARTY-NOTICES.txt for the included third-party components.
