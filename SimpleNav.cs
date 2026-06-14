using System;
using System.Collections.Generic;
using UnityEngine;

namespace DateEverythingAccess
{
    // Minimal navigation foundation. Under the object-first route model the planner
    // (SimpleNavPlanner) produces a polyline and the bridge follows it; this module is left
    // with the live observation surface the bridge still needs: per-zone floor-Y sampling,
    // Door-by-name lookup, and a logging helper.
    //
    // Truth sources (all live, no files):
    //   - Zones:  Singleton<CameraSpaces>.Instance.zones  (AABB + Name per triggerzone)
    //   - Player: BetterPlayerControl.Instance.transform.position
    //   - Doors:  Object.FindObjectsOfType<Door>()
    internal static class SimpleNav
    {
        private static readonly Dictionary<string, float> _zoneFloorY =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // Observation tick. Caller (the bridge) should call this once per frame while the
        // SimpleNav system is active, so per-zone floor Y gets sampled from real player motion.
        public static void Observe()
        {
            if (BetterPlayerControl.Instance == null)
                return;

            triggerzone playerZone = SafePlayerZone();
            if (playerZone == null || string.IsNullOrEmpty(playerZone.Name))
                return;

            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
            // Track the minimum Y ever observed in this zone. A first-observation cache poisons
            // itself when the player enters a zone mid-fall (teleport, jump, knockback): the
            // first sample is the door-pivot or apex height, not the floor. Minimum-Y converges
            // on the true floor as soon as the player lands.
            if (!_zoneFloorY.TryGetValue(playerZone.Name, out float currentMin) || playerPos.y < currentMin)
            {
                _zoneFloorY[playerZone.Name] = playerPos.y;
            }
        }

        // Resolve a Door by its GameObject name. Used by the bridge when a route segment
        // carries an authored door tag.
        public static Door FindDoorByName(string connectorName)
        {
            if (string.IsNullOrEmpty(connectorName)) return null;
            Door[] doors = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < doors.Length; i++)
            {
                Door door = doors[i];
                if (door == null) continue;
                string doorName;
                try { doorName = door.gameObject != null ? door.gameObject.name : null; }
                catch { continue; }
                if (string.Equals(doorName, connectorName, StringComparison.OrdinalIgnoreCase))
                    return door;
            }
            return null;
        }

        // Resolve an operable PORTAL by GameObject name across BOTH barrier component types:
        // swinging Door AND translating SlidingDoor (closet/cabinet/cupboard "container" doors).
        // The bake's door list is uniform over both kinds, but the runtime splits them into two
        // component types — enumerating only Door[] silently dropped every SlidingDoor, so
        // container doors were never tagged/opened and the player wedged against them. This is
        // the single resolver the bridge uses; it returns a DoorPortal that abstracts the
        // open/locked/Interact difference. See [[project_navigation_container_open_on_interact]].
        public static DoorPortal FindPortalByName(string connectorName)
        {
            if (string.IsNullOrEmpty(connectorName)) return null;
            Door swing = FindDoorByName(connectorName);
            if (swing != null) return DoorPortal.For(swing);

            SlidingDoor[] sliders = UnityEngine.Object.FindObjectsOfType<SlidingDoor>();
            for (int i = 0; i < sliders.Length; i++)
            {
                SlidingDoor sd = sliders[i];
                if (sd == null) continue;
                string n;
                try { n = sd.gameObject != null ? sd.gameObject.name : null; }
                catch { continue; }
                if (string.Equals(n, connectorName, StringComparison.OrdinalIgnoreCase))
                    return DoorPortal.For(sd);
            }
            return null;
        }

        private static triggerzone SafePlayerZone()
        {
            try
            {
                if (Singleton<CameraSpaces>.Instance == null) return null;
                return Singleton<CameraSpaces>.Instance.PlayerZone();
            }
            catch
            {
                return null;
            }
        }
    }

    // Uniform handle over the two barrier component types — swinging Door and translating
    // SlidingDoor — so the follower's tag/open path treats a closet "container" door exactly
    // like a passage door. Both derive from Interactable and expose Interact() (closed -> opens),
    // bool open, and bool locked as parallel members (NOT on the Interactable base), so we
    // dispatch on the concrete type. The only Door-specific concept a SlidingDoor lacks is the
    // SWING-rotation progress check (range / visual-rotation-delta); a slider translates, so its
    // truth is just open + moving. See [[project_navigation_container_open_on_interact]].
    internal sealed class DoorPortal
    {
        private readonly Door _swing;
        private readonly SlidingDoor _slide;

        private DoorPortal(Door swing, SlidingDoor slide) { _swing = swing; _slide = slide; }

        public static DoorPortal For(Door d) => d != null ? new DoorPortal(d, null) : null;
        public static DoorPortal For(SlidingDoor s) => s != null ? new DoorPortal(null, s) : null;

        // True when this portal is a swinging Door (has range + rotation swing). False = slider.
        public bool IsSwing => _swing != null;
        // The underlying swinging Door, or null when this portal is a SlidingDoor. Callers that
        // need swing-only specifics (visual rotation delta) gate on IsSwing first.
        public Door SwingDoor => _swing;
        // The underlying SlidingDoor, or null when this portal is a swinging Door. Used for the
        // reflected `moving` read, which is the slider's only animation-in-flight signal.
        public SlidingDoor SlideComponent => _slide;

        public GameObject gameObject =>
            _swing != null ? _swing.gameObject : (_slide != null ? _slide.gameObject : null);

        public Transform transform =>
            _swing != null ? _swing.transform : (_slide != null ? _slide.transform : null);

        public bool open => _swing != null ? _swing.open : (_slide != null && _slide.open);
        public bool locked => _swing != null ? _swing.locked : (_slide != null && _slide.locked);

        // Swing arc only; sliders have no rotation range. Used for the fully-open rotation check,
        // which is skipped for sliders anyway.
        public float range => _swing != null ? _swing.range : 0f;

        public InteractableObj interactableObj
        {
            get
            {
                if (_swing != null) return _swing.interactableObj;
                if (_slide != null) return _slide.GetComponent<InteractableObj>();
                return null;
            }
        }

        public Collider GetComponent()
        {
            Component c = _swing != null ? (Component)_swing : _slide;
            return c != null ? c.GetComponent<Collider>() : null;
        }

        // Toggle the barrier. Both Door and SlidingDoor override Interact() to open when closed
        // (Door swings, SlidingDoor translates) — one polymorphic call, identical contract.
        public void Interact()
        {
            if (_swing != null) _swing.Interact();
            else if (_slide != null) _slide.Interact();
        }

        public bool SameAs(DoorPortal other) =>
            other != null && (ReferenceEquals(_swing, other._swing) && ReferenceEquals(_slide, other._slide));
    }
}
