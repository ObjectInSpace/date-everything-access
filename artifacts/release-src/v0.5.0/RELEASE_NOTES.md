# Date Everything Access v0.5.0

Pose-card descriptions and audio-guidance refinements for the BepInEx accessibility mod for Date Everything!.

This release adds spoken descriptions for the fullscreen datable pose cards and improves the navigation guidance tone, on top of the navigation work from v0.1.4.

Highlights:

- Spoken descriptions for the fullscreen datable pose cards. When a datable's card appears — both the first-meeting card and the Love / Hate / Friend / Realized ending cards — the mod speaks a written description of the pose and expression on screen.
- The description is held until the card's musical stinger has played past its loud opening, so the speech is not lost underneath it. (The stingers run from about 6 to 11.5 seconds; the delay targets the quieter tail, not the full clip.)
- Descriptions live in an external, per-language file (`card_pose_descriptions.en.json`) in the plugins folder. They can be edited, corrected, or expanded in any text editor without reinstalling the mod, and the language follows the game's text-language setting.
- This release ships only a small set of starter descriptions; cards without a description stay silent (no change from before for those cards).
- Navigation guidance tone improvements: the tracking tone was dropped a full octave (880 Hz to 440 Hz) for a more comfortable range, and its loudness now swells meaningfully as you approach the target instead of staying near-flat across a room.
- Known-objects picker text updated for the floor / section / door filters.

Known issues still being worked on:

- Most pose cards do not yet have an authored description.
- Occasional navigation stalls at the foot of the stairs and at one office doorway that need a manual nudge.

Includes everything from v0.1.4.
