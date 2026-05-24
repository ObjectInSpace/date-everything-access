# Date Everything Access v0.1.3

Tutorial navigation and object picker update for the BepInEx accessibility mod for Date Everything!.

Included in this release:

- Screen reader startup announcement
- Focused UI item announcements
- Dialogue and dialogue-choice speech
- `Ctrl+F1` repeat-last-spoken support
- Spoken accessibility settings menu
- Phone app and menu text announcements
- Date A Dex biography speech
- Chat transcript and chat reply speech
- Object examination text announcements
- Room change and nearby interactable announcements
- Dateviators equip and charge announcements
- `F6` room report with facing-relative object grouping
- Improved tracker pitch behavior based on vertical camera position
- Quieter tracker volume while preserving the volume ramp
- Navigation-time ambient announcements are no longer suppressed
- Deduplicated room scans and room object picker entries
- More stable noun-style object names for room scans and picker entries
- Bundled room navigation graph and transition overrides for release installs
- Object auto-walk routing over the generated simple navigation bake
- Generated scene navigation metadata and navigable-region bake bundled with the portable package
- Improved route execution diagnostics
- Simplified door route traversal to follow baked waypoints while retaining door-open preconditions
- More tolerant door-tagged intermediate waypoint advancement for open-door collider near misses
- Stale-route prevention when route planning fails
- Improved final camera aiming and target hierarchy matching for small objects such as the Magnifying Glass
- Tutorial objective routing now uses the tutorial controller's serialized computer anchor and prefers the monitor interactable for the opening office computer step
- The `Leave the office and reflect on your life choices` tutorial step now routes to the active drone/gift delivery trigger, which is what spawns the gift box cutscene
- The `Check the delivery at the front door` tutorial step prefers the active gift box and falls back to the front door if the box cannot resolve yet
- The object picker now includes dated objects and real traversal doors only; cabinet-style objects with `door` only in the name are no longer included as doors

Release asset contents:

- `BepInEx/plugins/DateEverythingAccess.dll`
- `BepInEx/plugins/navigable_region.bake.json`
- `BepInEx/plugins/thirdpersongreybox-navigation-data.json`
- BepInEx loader files and core dependencies in the portable package
- `Tolk.dll`
- `nvdaControllerClient64.dll`
- `README.txt`
- `Install-DateEverythingAccess.ps1` in the portable package

Known follow-up work remains for broader gameplay coverage and runtime verification. Door-panel edge cases such as some closet routes and stair/attic target poses still need more runtime proof.
