# Date Everything Access v0.1.4

Navigation accuracy and pipeline-simplification update for the BepInEx accessibility mod for Date Everything!.

This release reworks how the auto-walk navigation system understands the house, so routing to objects and through doors is more reliable, and lands the route-margin and door-handling improvements in the in-game planner.

Highlights:

- Blockers are now derived only from the colliders the player can physically hit (real Unity collision geometry), instead of animated/dialog mesh bounds. This removes phantom obstacles (e.g. an animated monitor that used to seal a doorway) and gives a cleaner map of the house.
- The route planner now keeps clearance from walls and furniture (bounded clearance-cost pathfinding plus a clearance-preserving smoother), so routes round doorframes and furniture with margin instead of grazing them.
- Doors you walk to now use authoritative "where can I stand to open this" data computed from the game's real door rules (outside the swing arc, not touching the panel).
- Routes that pass through a door now aim through the doorway opening, so the follower threads the gap instead of clipping the door frame.
- Fixed a long-standing bug where targeting some ground-floor doors (laundry, office, their closets, bathroom) reported no route.
- Stair descent/ascent overshoot handling improved so the follower stays centered in the stairwell.
- Stop bundling the unused scene-navigation-data file; the package is leaner.
- Large internal cleanup of superseded navigation code paths.

Known issues still being worked on:

- Occasional stalls at the foot of the stairs and at one office doorway that need a manual nudge.

Includes everything from v0.1.3.
