using System;
using System.Collections.Generic;
using T17.Services;
using T17.UI;
using Team17.Scripts.Services.Input;
using UnityEngine;

namespace DateEverythingAccess
{
    // AIM COACH. Small things sitting on big things (the magnifying glass on the shelf) are easy to
    // walk up to and hard to land the crosshair on: a few degrees off and the game picks the shelf.
    // When the player is already close enough to use the target, walking is pointless, so Alt+O
    // talks them onto it instead: "up", "a little left", "far right and down", until the game's own
    // selection is the target, then "On target." The player turns and gazes with their usual keys;
    // the coach only speaks. It also takes over when autowalk arrives but can't land the aim itself.
    internal sealed partial class AccessibilityWatcher
    {
        private bool _aimCoachActive;
        private InteractableObj _aimCoachTarget;
        private string _aimCoachLastCue;
        private float _aimCoachLastSpokeAt;
        private float _aimCoachStartedAt;
        private float _aimCoachNoAimSince;
        // This coaching session uses the original aim tone instead of spoken cues.
        private bool _aimCoachTone;
        // When the coach last announced the target selected (manual navigation then stops quietly).
        private float _aimCoachOnTargetAt = -10f;

        private const float AimCoachMinGapSeconds = 0.35f;   // don't talk over the player's turning
        private const float AimCoachRepeatSeconds = 4f;      // remind if nothing has changed
        private const float AimCoachTimeoutSeconds = 90f;
        private const float AimCoachLostAimSeconds = 3f;     // no usable aim point for this long -> stop
        private const float AimCoachFarDeg = 45f;
        private const float AimCoachLittleDeg = 12f;
        // Alt+O coaches instead of walking only when the player is already this close ("by the
        // shelf"); from further away it walks as normal even if the game's reach is larger.
        private const float AimCoachStartWithinM = 4f;

        // True when the player is within the target's reach and the game's ray could select it from
        // where the camera is now (some aim point on it is visible, not hidden behind what it sits on).
        private static bool IsTargetAimableFromHere(InteractableObj target, out Vector3 aimPoint)
        {
            return IsTargetAimableFromHere(target, float.PositiveInfinity, out aimPoint);
        }

        private static bool IsTargetAimableFromHere(InteractableObj target, float maxDistance, out Vector3 aimPoint)
        {
            aimPoint = default;
            if (target == null || target.gameObject == null || !target.gameObject.activeInHierarchy)
                return false;
            BetterPlayerControl bpc = BetterPlayerControl.Instance;
            Camera cam = Camera.main;
            if (bpc == null || cam == null)
                return false;
            if (!TryFindSelectableAimPoint(target, cam.transform.position, out aimPoint))
                return false;
            float reach = Mathf.Min(GetInteractableApproachRadius(target), maxDistance);
            return GetFlatDistance(bpc.transform.position, aimPoint) <= reach;
        }

        // A point on the target that the game's own camera ray would select from camPos. Of the
        // candidate points, the one with the most margin wins: one whose neighbouring rays (a
        // fraction of a degree either way) also select the target. The first point that merely
        // worked was often the very edge of the object, so the automatic final nudge landed a
        // hair off it and the game picked the furniture behind; and as the camera moved, the pick
        // jumped between points, so the cues flipped left, right, left.
        private const float AimRobustOffsetDeg = 0.75f;
        private const float AimNoiseDeg = 0.6f;
        // While coaching, the point being aimed at; kept as long as it still works well.
        private static Vector3? _aimCoachKeepPoint;

        private static bool TryFindSelectableAimPoint(InteractableObj target, Vector3 camPos, out Vector3 aimPoint)
        {
            aimPoint = default;
            if (_aimCoachKeepPoint.HasValue)
            {
                Vector3 kd = _aimCoachKeepPoint.Value - camPos;
                if (kd.sqrMagnitude > 1e-6f &&
                    SimpleNavPlanner.GameRayWouldSelect(camPos, kd.normalized, target) &&
                    RobustSelectCount(target, camPos, kd.normalized) >= 3)
                {
                    aimPoint = _aimCoachKeepPoint.Value;
                    return true;
                }
            }
            int bestScore = -1;
            Collider[] cols = target.GetComponentsInChildren<Collider>();
            for (int c = 0; c < cols.Length && bestScore < 5; c++)
            {
                if (cols[c] == null || !cols[c].enabled) continue;
                List<Vector3> aims = SimpleNavPlanner.AimCandidates(cols[c], camPos);
                // The middle of the object first: it has the most room all round.
                if (aims.Count > 1) { Vector3 t0 = aims[0]; aims[0] = aims[1]; aims[1] = t0; }
                for (int a = 0; a < aims.Count && bestScore < 5; a++)
                {
                    Vector3 d = aims[a] - camPos;
                    if (d.sqrMagnitude < 1e-6f) continue;
                    Vector3 dir = d.normalized;
                    if (!SimpleNavPlanner.GameRayWouldSelect(camPos, dir, target)) continue;
                    int score = 1 + RobustSelectCount(target, camPos, dir);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        aimPoint = aims[a];
                    }
                }
            }
            return bestScore > 0;
        }

        // How many of the four rays a little left, right, above and below `dir` also select it.
        private static int RobustSelectCount(InteractableObj target, Vector3 camPos, Vector3 dir)
        {
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            int n = 0;
            Vector3[] probes =
            {
                Quaternion.AngleAxis(AimRobustOffsetDeg, Vector3.up) * dir,
                Quaternion.AngleAxis(-AimRobustOffsetDeg, Vector3.up) * dir,
                Quaternion.AngleAxis(AimRobustOffsetDeg, right) * dir,
                Quaternion.AngleAxis(-AimRobustOffsetDeg, right) * dir,
            };
            for (int i = 0; i < probes.Length; i++)
                if (SimpleNavPlanner.GameRayWouldSelect(camPos, probes[i], target)) n++;
            return n;
        }

        private static bool IsAimCoachTargetSelected(InteractableObj target)
        {
            InteractableManager manager = Singleton<InteractableManager>.Instance;
            if (target == null || manager == null || manager.activeObject == null || !manager.IsPlayerInRange)
                return false;
            return IsSameOrRelatedInteractableTarget(target.gameObject, manager.activeObject.gameObject);
        }

        // Begin coaching. `afterWalk` is true when autowalk has just arrived but couldn't land the
        // aim itself (the player hears why the coach is talking).
        private void StartAimCoach(InteractableObj target, bool afterWalk)
        {
            if (_aimCoachActive)
                StopAimCoach();
            _aimCoachActive = true;
            _aimCoachTarget = target;
            _aimCoachLastCue = null;
            _aimCoachLastSpokeAt = 0f;
            _aimCoachStartedAt = Time.unscaledTime;
            _aimCoachNoAimSince = 0f;
            _aimCueNextAt = 0f;

            string label = GetObjectFacingDisplayName(target);
            if (string.IsNullOrWhiteSpace(label)) label = Loc.Get("unknown_object");
            if (Main.Log != null)
                Main.Log.LogInfo("AimCoach: start target=" + target.gameObject.name + " afterWalk=" + afterWalk);

            if (IsAimCoachTargetSelected(target))
            {
                StopAimCoach();
                ObjectTracker.NotifyAimLocked();
                _aimCoachOnTargetAt = Time.unscaledTime;
                ScreenReader.Say(Loc.Get("aim_coach_already_on", label));
                return;
            }

            // Settings: Aiming help. Tone = the original tracker's aim tone (UpdateAimCoach drives it).
            _aimCoachTone = ModConfig.AimingHelp == ModConfig.AimingHelpStyle.Tone;
            if (_aimCoachTone)
            {
                _aimCoachLastSpokeAt = Time.unscaledTime;
                ScreenReader.Say(Loc.Get(afterWalk ? "aim_tone_start_after_walk" : "aim_tone_start", label));
                return;
            }

            string cue = BuildAimCue(target, out _);
            _aimCoachLastCue = cue;
            _aimCoachLastSpokeAt = Time.unscaledTime;
            ScreenReader.Say(Loc.Get(afterWalk ? "aim_coach_start_after_walk" : "aim_coach_start", label) +
                (string.IsNullOrEmpty(cue) ? string.Empty : " " + cue));
        }

        private void StopAimCoach()
        {
            if (_aimCoachActive && Main.Log != null)
                Main.Log.LogInfo("AimCoach: stop");
            if (_aimCoachActive && _aimCoachTone)
                ObjectTracker.StopTracking();
            _aimCoachTone = false;
            _aimCoachActive = false;
            _aimCoachTarget = null;
            _aimCoachLastCue = null;
            if (_aimFineActive) EndAimFine();
            _aimFineSince = 0f;
            _aimFineGaveUp = false;
            _aimCoachKeepPoint = null;
        }

        private bool _aimFineActive;
        private Vector3 _aimFinePoint;
        private float _aimFineSince;
        // The automatic nudge ran out of time without the game picking the target; don't restart
        // it until the player has moved the aim away and back (it was re-starting every frame and
        // fighting the player's own taps).
        private bool _aimFineGaveUp;
        private const float AimCoachFineAssistDeg = 8f;
        private const float AimCoachFineMaxSeconds = 3f;

        private void EndAimFine()
        {
            _aimFineActive = false;
            ApplyLookOnly(Vector3.zero);
        }

        // Called from FixedUpdate: one small, proportional camera step toward the aim point.
        private void ApplyAimFineStep()
        {
            if (!_aimFineActive || !_aimCoachActive || BetterPlayerControl.Instance == null)
                return;
            Vector3 look = GetLookInputTowardRouteTarget(BetterPlayerControl.Instance.transform, _aimFinePoint, 0.2f);
            ApplyLookOnly(look);
        }

        // Write only the look axis, leaving movement to the player.
        private static void ApplyLookOnly(Vector3 lookInput)
        {
            if (BetterPlayerControl.Instance == null) return;
            EnsureReflectionCache();
            if (_betterPlayerControlLookField == null) return;
            _betterPlayerControlLookField.SetValue(BetterPlayerControl.Instance, lookInput);
        }

        // Called every frame from Update.
        private void UpdateAimCoach()
        {
            if (!_aimCoachActive)
                return;

            InteractableObj target = _aimCoachTarget;
            if (target == null || target.gameObject == null || !target.gameObject.activeInHierarchy ||
                Singleton<GameController>.Instance == null ||
                Singleton<GameController>.Instance.viewState != VIEW_STATE.HOUSE)
            {
                StopAimCoach();
                return;
            }

            if (IsAimCoachTargetSelected(target))
            {
                // The tone version ends the way the original did: the chirp alone.
                bool tone = _aimCoachTone;
                StopAimCoach();
                ObjectTracker.NotifyAimLocked();
                _aimCoachOnTargetAt = Time.unscaledTime;
                if (!tone)
                    ScreenReader.Say(Loc.Get("aim_coach_on_target"));
                return;
            }

            if (Time.unscaledTime - _aimCoachStartedAt > AimCoachTimeoutSeconds)
            {
                StopAimCoach();
                ScreenReader.Say(Loc.Get("aim_coach_gave_up"));
                return;
            }

            // FINE FINISH. The game's smallest arrow tap still moves the camera several degrees,
            // which overshoots a small target. Once the player has it within a few degrees, the
            // mod makes the last tiny correction itself (see FixedUpdate) and stays quiet.
            // The aim check casts many rays; ten times a second is plenty for spoken cues and
            // keeps the game smooth (the final nudge itself still runs every physics step).
            if (Time.unscaledTime >= _aimCueNextAt)
            {
                _aimCueNextAt = Time.unscaledTime + AimCueIntervalSeconds;
                _aimCueCached = BuildAimCue(target, out _aimCueAimable, out _aimCueYaw, out _aimCuePitch,
                    out _aimCuePoint, out _aimCueTooSteep);
                _aimCueCachedUnconfirmed = _aimCueUnconfirmed;
            }
            string cue = _aimCueCached;
            bool aimable = _aimCueAimable;
            float yawErr = _aimCueYaw, pitchErr = _aimCuePitch;
            Vector3 aimPoint = _aimCuePoint;
            bool tooSteep = _aimCueTooSteep;
            _aimCueUnconfirmed = _aimCueCachedUnconfirmed;
            // The tone version leaves the aiming entirely to the player, as the original did.
            bool fine = !_aimCoachTone && aimable && !tooSteep && !_aimCueUnconfirmed &&
                Mathf.Max(Mathf.Abs(yawErr), Mathf.Abs(pitchErr)) <= AimCoachFineAssistDeg;
            _aimCoachKeepPoint = aimable ? aimPoint : (Vector3?)null;
            if (fine && !_aimFineGaveUp)
            {
                if (!_aimFineActive && Main.Log != null)
                    Main.Log.LogInfo("AimCoach: fine finish yaw=" + yawErr.ToString("F1") + " pitch=" + pitchErr.ToString("F1"));
                _aimFineActive = true;
                _aimFinePoint = aimPoint;
                if (_aimFineSince == 0f) _aimFineSince = Time.unscaledTime;
                // Give up the automatic nudge if it can't land (something in the way): hand back.
                if (Time.unscaledTime - _aimFineSince < AimCoachFineMaxSeconds)
                    return;
                EndAimFine();
                _aimFineGaveUp = true;
                if (Main.Log != null)
                {
                    InteractableManager im = Singleton<InteractableManager>.Instance;
                    string picked = im != null && im.activeObject != null ? im.activeObject.gameObject.name : "<nothing>";
                    Main.Log.LogInfo("AimCoach: fine finish gave up yaw=" + yawErr.ToString("F1") + " pitch=" +
                        pitchErr.ToString("F1") + " game selected=" + picked +
                        " inRange=" + (im != null && im.IsPlayerInRange) + " aim=" + aimPoint.ToString("F2"));
                }
            }
            else if (!fine)
            {
                if (_aimFineActive) EndAimFine();
                _aimFineSince = 0f;
                _aimFineGaveUp = false;
            }

            // Tone: steady tone on the aim point; pan says left/right, pitch says up/down
            // (ObjectTracker aim mode). It holds its last point while no spot can be aimed at.
            if (_aimCoachTone && aimable)
                ObjectTracker.StartAimTracking(aimPoint);

            if (Time.unscaledTime - _aimCoachLastSpokeAt < AimCoachMinGapSeconds)
                return;

            if (!aimable)
            {
                // Walked out of reach, or something now hides it from this spot.
                if (_aimCoachNoAimSince == 0f) _aimCoachNoAimSince = Time.unscaledTime;
                if (Time.unscaledTime - _aimCoachNoAimSince >= AimCoachLostAimSeconds)
                {
                    StopAimCoach();
                    ScreenReader.Say(Loc.Get("aim_coach_out_of_reach"));
                }
                return;
            }
            _aimCoachNoAimSince = 0f;
            if (_aimCoachTone)
                return;

            bool changed =!string.Equals(cue, _aimCoachLastCue, StringComparison.Ordinal);
            bool stale = Time.unscaledTime - _aimCoachLastSpokeAt >= AimCoachRepeatSeconds;
            if (string.IsNullOrEmpty(cue) || (!changed && !stale))
                return;

            _aimCoachLastCue = cue;
            _aimCoachLastSpokeAt = Time.unscaledTime;
            ScreenReader.Say(cue);
        }

        // "a little up", "far left and down", or "almost" when both errors are inside the target's
        // own size but the game hasn't picked it yet. `aimable` is false when no aim point on the
        // target can be selected from here.
        private static string BuildAimCue(InteractableObj target, out bool aimable)
        {
            return BuildAimCue(target, out aimable, out _, out _, out _, out _);
        }

        private static string BuildAimCue(InteractableObj target, out bool aimable,
            out float yawErrOut, out float pitchErrOut, out Vector3 aimOut, out bool tooSteep)
        {
            aimable = false;
            yawErrOut = 0f;
            pitchErrOut = 0f;
            aimOut = default;
            tooSteep = false;
            Camera cam = Camera.main;
            BetterPlayerControl bpc = BetterPlayerControl.Instance;
            if (cam == null || bpc == null || target == null)
                return null;

            _aimCueUnconfirmed = false;
            if (!IsTargetAimableFromHere(target, out Vector3 aim))
            {
                // No spot on it that the game would surely pick from here (something partly in the
                // way, or an odd shape). Still steer toward its middle rather than give up: "Turn
                // slowly to find it" was no help. The automatic final nudge is left out for these.
                if (!TryGetRoughAimPoint(target, out aim))
                    return null;
                _aimCueUnconfirmed = true;
            }
            aimable = true;

            Vector3 camPos = cam.transform.position;
            Vector3 toAim = aim - camPos;

            // Horizontal: camera heading vs direction to the aim point, both flattened.
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            Vector3 flat = toAim; flat.y = 0f;
            float yawErr = 0f;
            if (fwd.sqrMagnitude > 1e-6f && flat.sqrMagnitude > 1e-6f)
                yawErr = Vector3.SignedAngle(fwd.normalized, flat.normalized, Vector3.up);

            // Vertical: elevation of the aim point in the camera's own frame.
            Vector3 local = cam.transform.InverseTransformDirection(toAim);
            float horiz = Mathf.Max(0.001f, new Vector2(local.x, local.z).magnitude);
            float pitchErr = Mathf.Atan2(local.y, horiz) * Mathf.Rad2Deg;
            yawErrOut = yawErr;
            pitchErrOut = pitchErr;
            aimOut = aim;

            // The game stops tilting the camera at about 70 degrees up or down. When the target
            // needs a steeper look than that (something on the floor right at her feet), "down"
            // can never be reached however long the arrow is held: step back to flatten the angle.
            float flatDist = Mathf.Max(0.01f, new Vector2(toAim.x, toAim.z).magnitude);
            float neededElevation = Mathf.Atan2(toAim.y, flatDist) * Mathf.Rad2Deg;
            if (Mathf.Abs(neededElevation) > SimpleNavPlanner.LiveMaxCameraPitchDeg() - 2f)
            {
                tooSteep = true;
                return Loc.Get("aim_step_back");
            }

            // How close counts as "on it": half the target's apparent size, within sane limits.
            float tol = 1f;
            Collider col = target.GetComponentInChildren<Collider>();
            if (col != null)
            {
                float dist = Mathf.Max(0.1f, toAim.magnitude);
                float radius = col.bounds.extents.magnitude;
                float angular = Mathf.Atan2(radius, dist) * Mathf.Rad2Deg;
                tol = Mathf.Clamp(angular * 0.5f, 0.75f, 6f);
            }

            string h = null, v = null;
            if (Mathf.Abs(yawErr) > tol)
                h = Graded(yawErr > 0f ? Loc.Get("aim_right") : Loc.Get("aim_left"), Mathf.Abs(yawErr));
            if (Mathf.Abs(pitchErr) > tol)
                v = Graded(pitchErr > 0f ? Loc.Get("aim_up") : Loc.Get("aim_down"), Mathf.Abs(pitchErr));

            // Within the target's own size on both axes but the game hasn't picked it yet: the
            // crosshair is on its edge. Nudge toward the aim point by the larger remaining error
            // ("a little up"), which is something the player can act on.
            // A few tenths of a degree either way is noise, not a direction: saying "a little left",
            // then "a little right" as it wobbles only sends the player back and forth.
            if (h == null && v == null && Mathf.Max(Mathf.Abs(yawErr), Mathf.Abs(pitchErr)) < AimNoiseDeg)
                return null;
            if (h == null && v == null)
                return Mathf.Abs(yawErr) >= Mathf.Abs(pitchErr)
                    ? Loc.Get("aim_little", yawErr > 0f ? Loc.Get("aim_right") : Loc.Get("aim_left"))
                    : Loc.Get("aim_little", pitchErr > 0f ? Loc.Get("aim_up") : Loc.Get("aim_down"));
            if (h != null && v != null)
                return Loc.Get("aim_and", Mathf.Abs(yawErr) >= Mathf.Abs(pitchErr) ? h : v,
                    Mathf.Abs(yawErr) >= Mathf.Abs(pitchErr) ? v : h);
            return h ?? v;
        }

        // Set by BuildAimCue when it is steering at the target's middle without a confirmed spot.
        private static bool _aimCueUnconfirmed;
        private const float AimCueIntervalSeconds = 0.1f;
        private float _aimCueNextAt;
        private string _aimCueCached;
        private bool _aimCueAimable, _aimCueTooSteep, _aimCueCachedUnconfirmed;
        private float _aimCueYaw, _aimCuePitch;
        private Vector3 _aimCuePoint;
        private const float AimRoughExtraReachM = 1.5f;

        // The middle of the target, when it is within (a little more than) reach.
        private static bool TryGetRoughAimPoint(InteractableObj target, out Vector3 aim)
        {
            aim = default;
            BetterPlayerControl bpc = BetterPlayerControl.Instance;
            if (bpc == null || target == null) return false;
            Collider best = null;
            foreach (Collider c in target.GetComponentsInChildren<Collider>())
                if (c != null && c.enabled && (best == null || c.bounds.size.sqrMagnitude > best.bounds.size.sqrMagnitude))
                    best = c;
            Vector3 centre = best != null ? best.bounds.center : target.transform.position;
            float reach = GetInteractableApproachRadius(target) + AimRoughExtraReachM;
            if (GetFlatDistance(bpc.transform.position, centre) > reach) return false;
            aim = centre;
            return true;
        }

        private static string Graded(string direction, float errDeg)
        {
            if (errDeg >= AimCoachFarDeg) return Loc.Get("aim_far", direction);
            if (errDeg <= AimCoachLittleDeg) return Loc.Get("aim_little", direction);
            return direction;
        }
    }
}
