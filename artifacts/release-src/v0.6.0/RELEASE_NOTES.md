# Date Everything Access v0.6.0

Navigation reliability release for the BepInEx accessibility mod for Date Everything!.

This release focuses on autowalk getting you all the way to your target — across the staircase between floors and through doorways — without stopping for a manual nudge, on top of the pose-card and audio work from v0.5.0.

Highlights:

- Autowalk now walks the full staircase between the ground and upper floors as one continuous path, instead of stopping partway up or looping at the foot of the stairs.
- New wall-slide recovery for doorways. When autowalk used to wedge against a doorframe and press uselessly into the wall until it gave up, it now detects that it has stopped moving and slides sideways to thread the doorway and keep going. This clears the recurring stalls at the bedroom and office doorways.
- Object guidance now measures and routes object-to-object, so being guided between two things in the house is more reliable.
- Known-objects picker cleanup: clearer object names (the internal `SM_` mesh prefixes are gone) and duplicate entries are removed, so the list reads better.
- Hotkeys are more robust: if one key binding is already taken by another mod or the game, the rest of the mod's hotkeys keep working instead of the whole set dropping out.

Known issues still being worked on:

- A few navigation stalls remain at genuine tight pinch-points and right at the spawn point; most of the house routes cleanly.
- Most datable pose cards still do not have an authored spoken description (unchanged from v0.5.0).

Includes everything from v0.5.0.
