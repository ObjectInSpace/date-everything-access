# Date Everything Access v0.1.1

Packaging and navigation update release for the BepInEx accessibility mod for Date Everything!.

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

Release asset contents:

- `BepInEx/plugins/DateEverythingAccess.dll`
- `BepInEx/plugins/navigation_graph.json`
- `BepInEx/plugins/navigation_transition_overrides.json`
- BepInEx loader files and core dependencies in the portable package
- `Tolk.dll`
- `nvdaControllerClient64.dll`
- `README.txt`
- `Install-DateEverythingAccess.ps1` in the portable package

Known follow-up work remains for broader gameplay coverage and runtime verification, but this release package now includes the files needed to install and run the current mod build with navigation support.
