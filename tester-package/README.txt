Date Everything Access - Navigation Test Build
Based on Date Everything Access 1.2
Original mod: https://github.com/ObjectInSpace/date-everything-access

This is a TEST build. It adds a lot of work on getting around the house,
reaching small objects, and doors. Please report anything that gets you
stuck, and if you can, send the file BepInEx\LogOutput.log from your game
folder along with a short description of what you were doing.

INSTALLING
==========
1. Close Date Everything! completely.
2. Run DateEverythingAccess-Setup.exe.
   Windows may warn that the program is from an unknown publisher. Choose
   "More info", then "Run anyway".
3. The installer looks for your Steam copy of the game and asks before
   installing there. If it cannot find it, or you say No, it lets you pick
   your Date Everything! folder (the one containing Date Everything.exe).
4. Start the game. You should hear the mod's startup announcement.

The installer includes the BepInEx mod loader and the screen reader files,
so nothing else needs to be installed. Your existing mod settings are kept.

WHAT'S NEW IN THIS TEST BUILD
=============================
Getting around
- Auto-walk (Alt+O) handles doors much better: it opens doors on the way,
  steps back first if a door would swing into you, re-opens doors and
  sliding closet panels that got stuck half open, and copes with rooms that
  have two doors in a row, such as the upstairs bathroom.
- Walking to a door now stops in front of the doorway, facing it, without
  opening it. Walk straight forward to go through, or use the Dateviators on
  the door.
- Fewer snags on furniture, ladders and door frames, and fewer false "stuck"
  stops.

Reaching small things
- Alt+O when you are already near your target gives spoken aiming directions
  (left, right, up, down, a little, far, step back) until the game selects it.
  When you are within a few degrees, the mod makes the final tiny adjustment
  for you.
- Auto-walk hands over to the same aiming help when it arrives but the game
  has not selected the target yet.
- Objects with no clear line of sight from where you stand, such as the
  inanimals, can now be walked to.

Objectives (Ctrl+O)
- A situational datable close by (Dolly after moving the couch, and so on)
  comes first.
- Then something undiscovered right where you are, then the nearest room
  with something undiscovered.
- With no objective, Ctrl+O points you to the nearest room with something
  undiscovered.

Known objects list (O)
- Doors are listed in the room you reach them from, and hallways are named
  "Downstairs hall" and "Upstairs hall".
- Kitchen cabinets are grouped in their own sublist, and finding one kitchen
  cabinet reveals them all.
- The crawlspace trapdoor and ladder stay hidden until you find them.
- The game camera no longer moves while the list is open.

Mod tutorial
- When you start a new game, the mod talks you through its keys alongside
  the game's own tutorial, step by step: tracking, auto-walk, the phone,
  finding the door, going to bed and more. Press H to hear the latest
  tutorial message again.

Other
- Speech now goes through the Prism library instead of Tolk. It works with
  NVDA, JAWS and other screen readers, notices if you start or quit your
  screen reader while playing, and uses the Windows voice when none is
  running.
- The laundry closet is announced as "Laundry closet".
- Characters who speak in beeps instead of a voice line (in the Sassy Chap's
  content) have their lines read aloud, even with dialogue reading turned off.

CONTROLS
========
F1 - Help
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

TROUBLESHOOTING
===============
- If the mod does not load, make sure winhttp.dll and the BepInEx folder are
  in the game folder, next to Date Everything.exe.
- If your screen reader stays silent, make sure prism.dll is next to
  Date Everything.exe. With no screen reader running, the mod speaks with
  the Windows voice instead.
- If installing fails, make sure the game is fully closed and try again.

See THIRD-PARTY-NOTICES.txt for the included third-party components.
