using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace DateEverythingAccess
{
    // In-game coverage sweep harness. A DIAGNOSTIC TOOL whose only job is to surface upstream
    // bugs (in the bake or the route planner) fast — it is not tuned for its own sake.
    //
    // Two modes, selected by the manifest's `mode` (ModConfig.CoverageSweepRunId picks the run):
    //
    //  * WALK mode (run-id "default"): teleport ONCE to the manifest start, then walk to the
    //    nearest untested CELL, then the next, covering the whole reachable floor set. A stall is
    //    recorded as an impassable cell — clean upstream data, no recovery machinery.
    //
    //  * OBJECTS mode (run-id "objects"): a walk CHAIN over interactable objects. The player
    //    starts wherever they are when the sweep toggles on, walks to the nearest unvisited
    //    object, then from there to the next nearest, exactly as a player would. Arrived = pass.
    //    A leg that can't reach its object is recorded once WITH ITS REASON (the upstream datum);
    //    the object stays in the pool to be retried from a different angle later. There is NO
    //    teleport anywhere in objects mode (per-leg source teleport, same-leg retry/un-wedge, and
    //    the recovery relocate were all removed — the first two papered over teleport landings and
    //    dominated the 2026-06-12 log as thrash; the relocate masked broken paths and reached
    //    genuinely inaccessible outside objects). If one blocker boxes the player into a room, the
    //    objects it can't reach just record failures and drain via the per-object give-up cap. That
    //    pile of failures is the point: a path broken enough to trap the player is what most needs
    //    fixing, so the sweep surfaces it rather than driving around it.
    //
    // Both stamp the player's cell + 4-neighbour ring into a per-floor verified-reachable bitmap.
    //
    // Toggle hotkey: Ctrl+Alt+Shift+F8 (wired in Main.cs).
    //
    // Results emit to artifacts/navigation/sweep/<run-id>/sweep_results.json. Outcomes are flat:
    // arrived, no_path, skipped_already_covered, stalled (autowalk gave up), looped (circled a
    // small area), door_failed, budget, input_failed (game-state gate), exception. The summary
    // collapses to one outcome per object (arrival wins over an earlier failure).
    internal static class SimpleNavCoverageSweep
    {
        // Sweep artifacts live in the project source tree, not in BepInEx/plugins, because the
        // route catalogue is many thousands of files (~100 MB) we don't want to duplicate on
        // every build. The harness reads them directly from the source path. If the project
        // moves, override this via the COVERAGE_SWEEP_DIR env var.
        private const string DefaultSweepSourceDir = @"C:\Users\amock\mod template\artifacts\navigation\sweep";
        // Walk-mode only: settle wait after the single start teleport (objects-mode no longer
        // teleports per leg, so it doesn't use this).
        private const float WaitAfterTeleportSeconds = 0.25f;

        // Loop detector: player position sampled every ~0.5s; if N consecutive samples sit
        // inside a small radius, we call it a loop. Decided 2026-05-20 with the user.
        private const float LoopSampleIntervalSeconds = 0.5f;
        private const int LoopSampleWindow = 16;          // 16 samples × 0.5s = 8s
        private const float LoopRadiusMeters = 1.5f;
        // Budget ceiling per route: cost_m / 1.5 + 5s. A safety net, not the primary signal.
        private const float BudgetMetersPerSecond = 1.5f;
        private const float BudgetSlackSeconds = 5f;
        // Door-failed detector: if any tagged door on the current segment is still closed
        // after this much time (excluding swing-in-progress periods), mark door_failed.
        private const float DoorOpenTimeoutSeconds = 4f;
        // A leg counts as a GENUINE WALK only if the player actually moved this far (XZ) from the
        // route's first waypoint. The walk chain picks the nearest object next, so many legs start
        // ON or beside the goal cell — those finish in a fraction of a second WITHOUT testing any
        // walk path. They still run the camera-aim LOS raycast, so their LOS verdict is real, but
        // crediting them as walk-successes inflates the pass rate (77% of "verified" were such
        // no-ops in run 195613).
        //
        // Below this, the walk is UNTESTED — but for an object whose LOS-interact PASSED, that is
        // NOT a gap. An LOS pass proves the goal CELL is a valid interaction standpoint for the
        // object. The specific source->cell walk that happened is unrepeatable (the player's real
        // start varies and can't be guessed), so it's not bankable — but it doesn't need to be:
        // the only runtime requirement is that the planner can route the player TO that cell, which
        // is the planner's standing job for ANY cell, independent of this object. So a validated
        // interaction cell exists => if the planner plans to that cell for that object, it is valid.
        // The walk path is the variable; proving it is not this object's burden.
        // See the walk/LOS axis split in ReportWalkLosAxes.
        private const float GenuineWalkMeters = 3f;

        private enum Phase
        {
            Idle,
            LoadingManifest,
            BetweenRoutes,
            Running,
            // Objects-mode: the follower stopped (arrival OR stall) and we're turning to face
            // the object to confirm we can actually interact with it from here. See StepVerifying.
            Verifying,
            WritingResults,
            // Walk-mode phases: one continuous traversal hitting every reachable cell.
            WalkPickLeg,        // pick next unvisited reachable cell, plan a leg to it.
            WalkRunningLeg,     // leg's autowalk is in flight; same detectors as Running.
        }

        // Walk-mode per-cell state. 0=untested, 1=walkable (player stood on it), 2=impassable.
        // Stored row-major (ix * nz + iz) per floor, parallel to the manifest's reachable bitmap.
        private const byte CellUntested = 0;
        private const byte CellWalkable = 1;
        private const byte CellImpassable = 2;
        private static readonly Dictionary<string, byte[]> _walkState =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool[]> _walkReachable =
            new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
        private static List<ImpassRecord> _impassRecords;
        private static int _walkLegIndex;
        private static string _walkTargetFloor;
        private static int _walkTargetIx;
        private static int _walkTargetIz;
        // Cell-snap radius around the current player position. After arriving at a leg target,
        // every cell within this many cells of the player counts as walkable too. Set to match
        // the player capsule's physical footprint (~0.8m diameter at cell_size=0.2m → 4 cells)
        // so the next-leg target picker doesn't return a cell already inside the arrival disc
        // and produce zero-distance legs.
        private const int WalkVerifyRadiusCells = 4;

        private static Phase _phase = Phase.Idle;

        // True whenever a sweep is running. The sweep is a diagnostic tool, so the
        // follower instrumentation it depends on (wall-slide escape fires, blocked
        // reasons) must capture unconditionally while it runs — never gated behind the
        // manual DebugMode toggle, or a sweep can't report on its own escape logic.
        public static bool IsActive => _phase != Phase.Idle;

        // Suppress the in-game phone for the whole sweep. The phone IS the game's pause: opening it
        // freezes the player (playerState=CantMove) and plays an open/close animation, during which
        // the controller refuses navigation input. An unattended sweep has no reason to open the
        // phone, but a stray keypress (Esc/the phone button) during a run did — and every leg that
        // tried to start while it was up/animating failed as input_failed, with the chain frozen in
        // place until it cleared. Setting PhoneManager.BlockPhoneOpening makes the game's own input
        // handler refuse to open the phone, so the pause state can't occur mid-run. This is the
        // game's sanctioned mechanism (AtticDoorUnlocker / CinematicBars use it identically). We
        // restore the prior value on every sweep teardown so manual play is unaffected afterward.
        private static bool _phoneBlockSet;
        private static bool _phoneBlockPrev;

        private static void SetPhoneBlockedForSweep(bool blocked)
        {
            try
            {
                var phone = Singleton<PhoneManager>.Instance;
                if (phone == null) return;
                if (blocked)
                {
                    if (_phoneBlockSet) return;          // already engaged for this run
                    _phoneBlockPrev = phone.BlockPhoneOpening;
                    phone.BlockPhoneOpening = true;
                    _phoneBlockSet = true;
                }
                else
                {
                    if (!_phoneBlockSet) return;         // nothing to restore
                    phone.BlockPhoneOpening = _phoneBlockPrev;
                    _phoneBlockSet = false;
                }
            }
            catch { /* never let a phone-state hiccup break sweep start/teardown */ }
        }

        private static SweepManifest _manifest;
        private static int _entryIndex;
        private static string _runDir;
        // Per-run stamp set once at StartSweep. The run dir is fixed (it holds the input manifest,
        // keyed by run-id), so the canonical sweep_results.json is OVERWRITTEN every run — a game
        // relaunch right after a sweep clobbers the prior run's detail before it can be reviewed.
        // We additionally write a timestamped copy (sweep_results.<stamp>.json) that no later run
        // touches, so every run's per-result data survives. Canonical name is kept for tooling.
        private static string _runStamp;
        private static float _nextActionTime;
        private static List<RouteResult> _results;

        // Active-route state
        private static SimpleNavRoute _currentRoute;
        private static int _currentManifestIndex;
        private static float _routeStartUnscaledTime;
        private static float _routeBudgetSeconds;
        private static float _nextLoopSampleTime;
        private static readonly Queue<Vector3> _loopWindow = new Queue<Vector3>(LoopSampleWindow + 1);
        private static float _doorCloseObservedSince;  // 0 = not currently waiting on a door

        // Objects-mode is a WALK CHAIN, not a teleport-per-leg harness (reworked 2026-06-12).
        // The sweep's only job is to confirm each object can be ARRIVED AT and, when it can't,
        // record WHY for upstream (bake/planner) triage — not to be tuned itself. So: the player
        // starts wherever they are when the sweep turns on and walks to the nearest unvisited
        // object, then from there to the next nearest, and so on — exactly how a player would
        // traverse. A leg that fails is recorded once with its reason; no per-leg teleport, no
        // un-wedge, no recovery re-plan (all of which existed only to paper over teleport
        // landings, and which DOMINATED the 2026-06-12 failure log as thrash).
        //
        // NO teleport at all (removed 2026-06-17). The sweep walks the whole run from wherever the
        // player starts. If one blocker boxes the player into a room, every object it can't reach
        // simply records a failure and drains from the pool via the per-object give-up cap — that
        // pile of failures IS the signal: a path out so broken the player can get stuck is exactly
        // what most needs fixing, and a recovery teleport (which also reached genuinely
        // inaccessible outside objects) only masked it. Termination no longer depends on relocation;
        // it rests entirely on MaxObjectFailures draining the pool.
        // Objects already arrived-at (pass) — by manifest index — so the nearest-unvisited picker
        // skips them. A FAILED object is NOT added here: it stays in the pool to be retried from
        // every region until reached (its failure reason is recorded each time for triage).
        private static readonly HashSet<int> _objectVisited = new HashSet<int>();
        // Objects failed during the CURRENT failure streak — skipped by the picker so the strikes
        // sample different nearby objects. Cleared on any arrival and on relocate (fresh streak).
        private static readonly HashSet<int> _recentlyFailed = new HashSet<int>();
        // Per-object lifetime failure count. An object reachable from nowhere would otherwise keep
        // the pool from ever draining (it's never marked visited), so once it has failed from
        // MaxObjectFailures distinct attempts we give up on it: mark it visited (= leave the pool)
        // with its failures already recorded. This is what GUARANTEES the sweep terminates.
        private static readonly Dictionary<int, int> _objectFailCount = new Dictionary<int, int>();
        private const int MaxObjectFailures = 3;

        // Per-floor verified-reachable bitmap. cells[ix * nz + iz] = true once any traversal
        // has put the player's cell-ring on that cell. Allocated lazily per floor.
        private static readonly Dictionary<string, bool[]> _verified =
            new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Toggle the sweep on/off. Wired to the Ctrl+Alt+Shift+F8 hotkey.</summary>
        public static void RequestToggle()
        {
            if (_phase == Phase.Idle) StartSweep();
            else AbortSweep("user toggle");
        }

        /// <summary>Per-frame tick. Cheap when idle.</summary>
        public static void Tick()
        {
            if (_phase == Phase.Idle) return;
            try
            {
                switch (_phase)
                {
                    case Phase.LoadingManifest:       /* handled in StartSweep */ break;
                    case Phase.BetweenRoutes:         StepBetweenRoutes(); break;
                    case Phase.Running:               StepRunning(); break;
                    case Phase.Verifying:             StepVerifying(); break;
                    case Phase.WritingResults:        /* handled in finish */ break;
                    case Phase.WalkPickLeg:           WalkStepPickLeg(); break;
                    case Phase.WalkRunningLeg:        WalkStepRunningLeg(); break;
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavCoverageSweep tick threw: " + ex);
                RecordCurrentRouteAsException(ex.Message);
                AdvanceToNextEntry();
            }
        }

        private static void StartSweep()
        {
            // Run-id selects which sweep manifest to drive: "default" (walk-mode cell sweep)
            // or "objects" (object-reachability sweep). Configurable via ModConfig so the same
            // hotkey can run either without a rebuild.
            string runId = ModConfig.CoverageSweepRunId;
            string sweepBase = Environment.GetEnvironmentVariable("COVERAGE_SWEEP_DIR");
            if (string.IsNullOrEmpty(sweepBase) || !Directory.Exists(sweepBase))
                sweepBase = DefaultSweepSourceDir;
            _runDir = Path.Combine(sweepBase, runId);
            _runStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string manifestPath = Path.Combine(_runDir, "sweep_manifest.json");
            if (!File.Exists(manifestPath))
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavCoverageSweep: manifest missing at " + manifestPath);
                ScreenReader.Say("Coverage sweep manifest not found", remember: false);
                return;
            }

            _manifest = LoadManifest(manifestPath);
            if (_manifest == null)
            {
                ScreenReader.Say("Coverage sweep manifest unreadable", remember: false);
                return;
            }
            bool walkMode = string.Equals(_manifest.mode, "walk", StringComparison.OrdinalIgnoreCase);
            // Dispersed mode requires an entries list; walk mode requires a reachable bitmap.
            if (!walkMode && (_manifest.entries == null || _manifest.entries.Length == 0))
            {
                ScreenReader.Say("Coverage sweep manifest empty", remember: false);
                return;
            }
            if (walkMode && _manifest.reachable_bitmap_rows == null)
            {
                ScreenReader.Say("Coverage walk-sweep manifest has no reachable bitmap", remember: false);
                return;
            }

            // Allocate verified bitmap per floor; reset the objects-mode walk-chain state.
            _verified.Clear();
            _objectVisited.Clear();
            _recentlyFailed.Clear();
            _objectFailCount.Clear();
            if (_manifest.floor_frames != null)
            {
                foreach (var kv in _manifest.floor_frames)
                {
                    int cells = kv.Value.nx * kv.Value.nz;
                    _verified[kv.Key] = new bool[cells];
                }
            }

            _results = new List<RouteResult>(_manifest.entries?.Length ?? 0);
            _entryIndex = 0;

            // Block the phone for the whole run (both modes) so a stray keypress can't open the
            // game's pause mid-sweep and strand the chain. Restored in every teardown path.
            SetPhoneBlockedForSweep(true);

            if (walkMode)
            {
                InitWalkMode();
                _phase = Phase.WalkPickLeg;
                if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep: walk-mode started, reachable cells per floor: " + DescribeReachable());
                ScreenReader.Say("Coverage walk-sweep started", remember: false);
                _nextActionTime = 0f;
                return;
            }

            _phase = Phase.BetweenRoutes;
            _nextActionTime = 0f;
            // Close every door once at the start (as walk-mode does). The chain then begins with
            // all doors shut and opens them only as the player walks through — so every door is
            // tested: can it be opened from where the route planner parks the player on the
            // approach side, before passing through? Doors stay in whatever state the chain leaves
            // them, so this tests each from the first direction the player reaches it. (Testing
            // the reverse direction too is a later pass; one direction is the simple first cut.)
            ForceCloseAllDoors();
            bool objectMode = string.Equals(_manifest.mode, "objects", StringComparison.OrdinalIgnoreCase);
            if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep: started, mode=" +
                (_manifest.mode ?? "dispersed") + " entries=" + _manifest.entries.Length);
            ScreenReader.Say((objectMode ? "Object reachability sweep started, " : "Coverage sweep started, ")
                + _manifest.entries.Length + (objectMode ? " objects" : " routes"), remember: false);
        }

        private static void InitWalkMode()
        {
            _walkState.Clear();
            _walkReachable.Clear();
            _impassRecords = new List<ImpassRecord>(64);
            _walkLegIndex = 0;
            _walkPlannerFailureCount = 0;
            if (_manifest.reachable_bitmap_rows == null || _manifest.floor_frames == null) return;
            foreach (var kv in _manifest.floor_frames)
            {
                FloorFrame frame = kv.Value;
                int cells = frame.nx * frame.nz;
                _walkState[kv.Key] = new byte[cells];
                bool[] reachable = new bool[cells];
                string[] rows = _manifest.reachable_bitmap_rows.ForFloor(kv.Key);
                if (rows != null && rows.Length == frame.nx)
                {
                    for (int ix = 0; ix < frame.nx; ix++)
                    {
                        string row = rows[ix];
                        if (row == null) continue;
                        int rowBase = ix * frame.nz;
                        int upper = Math.Min(row.Length, frame.nz);
                        for (int iz = 0; iz < upper; iz++)
                            if (row[iz] == '1') reachable[rowBase + iz] = true;
                    }
                }
                _walkReachable[kv.Key] = reachable;
            }
        }

        private static string DescribeReachable()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _walkReachable)
            {
                int n = 0; for (int i = 0; i < kv.Value.Length; i++) if (kv.Value[i]) n++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key); sb.Append('='); sb.Append(n);
            }
            return sb.ToString();
        }

        private static void AbortSweep(string reason)
        {
            if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep: abort reason=" + reason + " completed=" + (_results?.Count ?? 0));
            // If a route was in flight, stop the autowalk cleanly.
            try { SimpleNavBridge.EndStep(); } catch { }
            FlushResults();
            FlushWalkResults();
            SetPhoneBlockedForSweep(false);
            _phase = Phase.Idle;
            _manifest = null;
            _currentRoute = null;
            _results = null;
            _verified.Clear();
            _objectVisited.Clear();
            _recentlyFailed.Clear();
            _objectFailCount.Clear();
            _walkState.Clear();
            _walkReachable.Clear();
            _impassRecords = null;
            _walkStartTeleported = false;
            ScreenReader.Say("Coverage sweep stopped", remember: false);
        }

        // ---- Phase: BetweenRoutes ---------------------------------------------------------
        // Walk-chain leg picker: from the player's CURRENT position, find the nearest unvisited
        // object and plan a live route to it. No per-leg source teleport — the leg starts wherever
        // the previous leg ended. Offline-planner failures (status != ok) are recorded once up
        // front. When every object is visited (or only unreachable ones remain after we've
        // exhausted relocation), the sweep finishes.

        private static void StepBetweenRoutes()
        {
            // Make sure any previous run is fully torn down.
            try { SimpleNavBridge.EndStep(); } catch { }

            // Record offline-planner verdicts once, up front, and treat them as visited so the
            // nearest-object picker never selects them (the offline planner already said no_path).
            for (; _entryIndex < _manifest.entries.Length; _entryIndex++)
            {
                var e = _manifest.entries[_entryIndex];
                if (e == null) continue;
                if (string.Equals(e.status, "ok", StringComparison.Ordinal)) continue;
                _results.Add(new RouteResult
                {
                    manifest_index = _entryIndex,
                    floor = e.floor,
                    cell = e.cell,
                    outcome = e.status, // e.g. "no_path"
                    name = e.name,
                });
                _objectVisited.Add(_entryIndex);
            }

            if (BetterPlayerControl.Instance == null) { AbortSweep("objects: no player"); return; }
            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;

            if (!PickNearestUnvisitedObject(playerPos, out int idx))
            {
                // Nothing left to reach.
                FinishSweep();
                return;
            }

            _currentManifestIndex = idx;
            BeginCurrentLeg(playerPos);
        }

        // Begin a leg: plan a live route from the player's CURRENT position to the picked object
        // and drive it. No source teleport — `fromPos` is wherever the player already is. Doors
        // are left in whatever state prior legs left them (a walking player doesn't re-close doors
        // between objects); the route opens any door it needs.
        private static void BeginCurrentLeg(Vector3 fromPos)
        {
            if (BetterPlayerControl.Instance == null)
            {
                RecordCurrentRouteAsException("no-player");
                AdvanceToNextEntry();
                return;
            }
            PlanAndDriveFrom(fromPos);
        }

        // Pick the nearest unvisited object (by straight-line distance from `fromPos`) whose live
        // transform we can resolve. "Unvisited" = not yet arrived-at AND not an offline no_path.
        // Failed-but-reachable-elsewhere objects stay eligible, so a later chain can reach them
        // from a different angle. Within a failure streak we also skip objects we JUST failed
        // (_recentlyFailed), so the consecutive strikes sample DIFFERENT nearby objects — that
        // makes "N failures in a row" a real "this region is boxed in" probe rather than the same
        // unreachable object re-failing in place N times. If skipping them leaves nothing, we fall
        // back to allowing them (better to re-test than to stall the chain). Returns false only
        // when no eligible object exists at all.
        private static bool PickNearestUnvisitedObject(Vector3 fromPos, out int idx)
        {
            if (PickNearestUnvisitedObject(fromPos, true, out idx)) return true;
            return PickNearestUnvisitedObject(fromPos, false, out idx);
        }

        private static bool PickNearestUnvisitedObject(Vector3 fromPos, bool excludeRecentlyFailed, out int idx)
        {
            idx = -1;
            float bestD2 = float.PositiveInfinity;
            for (int i = 0; i < _manifest.entries.Length; i++)
            {
                if (_objectVisited.Contains(i)) continue;
                if (excludeRecentlyFailed && _recentlyFailed.Contains(i)) continue;
                var e = _manifest.entries[i];
                if (e == null || !string.Equals(e.status, "ok", StringComparison.Ordinal)) continue;
                if (e.object_xyz == null || e.object_xyz.Length < 3) continue;
                float dx = e.object_xyz[0] - fromPos.x;
                float dy = e.object_xyz[1] - fromPos.y;
                float dz = e.object_xyz[2] - fromPos.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; idx = i; }
            }
            return idx >= 0;
        }

        // Resolve the live DEST object, plan to it from `startWorld` exactly as in-game WalkTo
        // does, face the first heading, and hand the route to the driver.
        private static void PlanAndDriveFrom(Vector3 startWorld)
        {
            ManifestEntry entry = _manifest.entries[_currentManifestIndex];
            Transform playerTransform = BetterPlayerControl.Instance.transform;

            SimpleNavRoute route = PlanLegToObject(entry, startWorld);
            if (route == null)
            {
                // C# planner refused from the player's current position — a planner verdict, not a
                // drive stall. Record the SPECIFIC reason so an interactability defect doesn't hide
                // inside the generic no_path bucket: TargetNoLineOfSight means the object is
                // reachable but occluded from every navigable cell (a placement/occluder problem to
                // investigate), distinct from genuinely can't-get-there. Then chain on.
                FinishLeg(SimpleNavPlanner.LastFailure == SimpleNavPlanner.PlanFailure.TargetNoLineOfSight
                    ? "no_los"
                    : "no_path");
                return;
            }

            _currentRoute = route;

            // Face the second waypoint so the first input doesn't waste a turn.
            if (route.Waypoints != null && route.Waypoints.Count > 1)
            {
                Vector3 toNext = route.Waypoints[1] - startWorld;
                toNext.y = 0f;
                if (toNext.sqrMagnitude > 0.0001f)
                    playerTransform.rotation = Quaternion.LookRotation(toNext.normalized, Vector3.up);
            }

            // No teleport to settle — start driving immediately from where the player stands.
            _routeStartUnscaledTime = Time.unscaledTime;
            _routeBudgetSeconds = ComputeBudgetSeconds(route);
            _loopWindow.Clear();
            _nextLoopSampleTime = Time.unscaledTime + LoopSampleIntervalSeconds;
            _doorCloseObservedSince = 0f;
            if (!AccessibilityWatcher.TryStartCoverageSweepRoute(route, out string detail))
            {
                // Game wasn't in a controllable state (dialogue/menu/CantMove). Record the reason
                // and chain on — it counts toward the consecutive-failure relocation budget.
                FinishLeg("input_failed:" + detail);
                return;
            }
            _phase = Phase.Running;
        }

        // Resolve the live DEST object and plan to it with the C# planner exactly as the in-game
        // WalkTo does — so the sweep validates the routes the game itself would choose. We resolve
        // by name + nearest position because the object id in the manifest is a serialized id, not
        // the runtime GetInstanceID the planner keys on; the live object's own InstanceID is used.
        private static SimpleNavRoute PlanLegToObject(ManifestEntry entry, Vector3 startWorld)
        {
            float tx = entry.object_xyz != null && entry.object_xyz.Length > 0 ? entry.object_xyz[0] : 0f;
            float ty = entry.object_xyz != null && entry.object_xyz.Length > 1 ? entry.object_xyz[1] : 0f;
            float tz = entry.object_xyz != null && entry.object_xyz.Length > 2 ? entry.object_xyz[2] : 0f;

            InteractableObj dst = ResolveLiveObject(entry.unique_id, entry.unique_ids, entry.name, tx, ty, tz);
            Vector3 targetPos = dst != null && dst.transform != null ? dst.transform.position : new Vector3(tx, ty, tz);
            int goId = dst != null && dst.gameObject != null ? dst.gameObject.GetInstanceID() : 0;
            string goName = dst != null && dst.gameObject != null ? dst.gameObject.name : entry.name;
            float radius = entry.interaction_radius > 0.5f ? entry.interaction_radius : 1.0f;
            bool isDatable = dst != null && !string.IsNullOrWhiteSpace(dst.inkFileName);
            string inkFile = dst != null ? dst.inkFileName : null;

            return SimpleNavPlanner.Plan(startWorld, targetPos, radius, goName, goId, isDatable, inkFile);
        }

        // Resolve the live InteractableObj for a manifest leg. PREFERRED: an exact match on the
        // stable scene id (uniqueId / any uniqueIds member == InteractableObj.Id). FALLBACK:
        // the legacy bridge — name match + nearest transform position (names aren't unique, so
        // position disambiguates). The id path is exact and instance-correct; the fallback covers
        // older manifests that predate the unique-id field.
        private static InteractableObj ResolveLiveObject(string uniqueId, string[] uniqueIds, string name, float x, float y, float z)
        {
            bool haveId = !string.IsNullOrWhiteSpace(uniqueId) || (uniqueIds != null && uniqueIds.Length > 0);
            if (string.IsNullOrEmpty(name) && !haveId) return null;

            InteractableObj[] all = UnityEngine.Object.FindObjectsOfType<InteractableObj>();

            // Exact stable-id bridge first.
            if (haveId)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    InteractableObj o = all[i];
                    if (o == null || o.gameObject == null) continue;
                    string id;
                    try { id = o.Id; } catch { id = null; }
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!string.IsNullOrWhiteSpace(uniqueId) && string.Equals(id, uniqueId, StringComparison.OrdinalIgnoreCase))
                        return o;
                    if (uniqueIds != null)
                    {
                        for (int j = 0; j < uniqueIds.Length; j++)
                        {
                            if (!string.IsNullOrWhiteSpace(uniqueIds[j]) &&
                                string.Equals(id, uniqueIds[j], StringComparison.OrdinalIgnoreCase))
                                return o;
                        }
                    }
                }
            }

            // Fallback: name match + nearest position.
            if (string.IsNullOrEmpty(name)) return null;
            InteractableObj best = null;
            float bestD2 = float.PositiveInfinity;
            for (int i = 0; i < all.Length; i++)
            {
                InteractableObj o = all[i];
                if (o == null || o.gameObject == null) continue;
                // Match the cleaned/base name OR the raw GameObject name — the manifest stores the
                // picker's display name, which may be a stripped form of the GameObject name.
                if (!NameMatches(o.gameObject.name, name)) continue;
                Vector3 p = o.transform.position;
                float dx = p.x - x, dy = p.y - y, dz = p.z - z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; best = o; }
            }
            return best;
        }

        private static bool NameMatches(string goName, string manifestName)
        {
            if (string.IsNullOrEmpty(goName) || string.IsNullOrEmpty(manifestName)) return false;
            if (string.Equals(goName, manifestName, StringComparison.OrdinalIgnoreCase)) return true;
            // The manifest name is the picker's stripped label; accept a contains-match either way
            // so "glass" matches "glass_MODEL_UPDATE" and stripped forms match their raw names.
            return goName.IndexOf(manifestName, StringComparison.OrdinalIgnoreCase) >= 0
                || manifestName.IndexOf(goName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---- Phase: Running ---------------------------------------------------------------
        // Watch the autowalk. Stamp player position into the verified bitmap every frame.
        // Decide outcome whenever one of the detectors fires.

        private static void StepRunning()
        {
            if (BetterPlayerControl.Instance == null)
            {
                FinishLeg("exception:no-player");
                return;
            }

            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;

            // Stamp the player's cell + 4-neighbour ring into the verified bitmap on the
            // floor whose Y-band the player is currently in.
            StampCoverage(playerPos);

            // 1. Arrival vs stall: did the route succeed, or did the autowalk give up?
            // Both end with HasActiveRoute=false; disambiguate by checking proximity to target.
            //
            // SWEEP ARRIVAL = reaching the route's final waypoint (the goal STAND-CELL), NOT
            // SimpleNavBridge.HasArrivedAtRouteTarget. That shared method's object-target branch
            // also requires being within the OBJECT TRANSFORM's interaction radius — but sweep
            // objects sit on shelves / walls / beds whose transform is up to ~12m (median ~7.4m)
            // from the nearest reachable floor cell, so 251/757 routes can NEVER satisfy it even
            // standing perfectly on the goal cell. That made every driven leg fall through to the
            // progress-timeout and log "stalled" (arrived=0/1045). The sweep's question is "can
            // the player REACH the navigable stand-cell next to this object", i.e. the final
            // waypoint the planner already placed at the closest reachable floor. Use that.
            // On EITHER outcome — reached the goal cell, or the follower gave up — hand off to
            // the interaction probe before recording. Geometric arrival is only a proxy; the
            // probe turns to face the object and asks the game whether we can actually interact
            // from here. Re-probing on STALL too is deliberate: a follower that times out 1.5m
            // short may already be in range (a false-negative stall), and the probe promotes it
            // rather than recording a phantom failure. The geometric verdict is carried as
            // context so the probe's result can be mapped to the right outcome. See StepVerifying.
            if (HasReachedGoalWaypoint(playerPos))
            {
                BeginVerify(geometricallyAtCell: true);
                return;
            }
            if (!SimpleNavBridge.HasActiveRoute)
            {
                // Autowalk ended the step but we're not within the tight 1.35m goal cell. Two very
                // different reasons land here and the sweep MUST distinguish them, or it miscounts
                // legitimate arrivals as stalls (the dominant artifact in the 2026-06-13 run):
                //   - The follower ARRIVED: it stopped because the player is within the object's
                //     InteractionRadius (HasArrivedAtRouteTarget), which for a large-radius object
                //     (charcoal/log/food, radius up to 7.5m) is routinely >1.35m from the goal cell.
                //     This is a real arrival by the game's own rule — verify selection from here.
                //   - The follower GAVE UP: a progress timeout stopped it short. That's a stall.
                // LastSweepDriveArrived carries which one it was. Either way we re-probe (a true
                // arrival still needs the interaction/LOS check; a short stall may yet be in range),
                // but the geometric verdict controls how a GaveUp probe is recorded:
                // arrived_unconfirmed vs stalled. See [[project-navigation-stalls-are-proximity-miscount-2026-06-13]].
                BeginVerify(geometricallyAtCell: AccessibilityWatcher.LastSweepDriveArrived);
                return;
            }

            // 2. Door-open failure.
            if (SimpleNavBridge.ActiveDoor != null)
            {
                DoorPortal door = SimpleNavBridge.ActiveDoor;
                bool open = door.open;
                bool moving = SimpleNavBridge.IsActiveDoorMoving();
                if (!open && !moving)
                {
                    if (_doorCloseObservedSince <= 0f) _doorCloseObservedSince = Time.unscaledTime;
                    else if (Time.unscaledTime - _doorCloseObservedSince > DoorOpenTimeoutSeconds)
                    {
                        FinishLeg("door_failed:" + (door.gameObject != null ? door.gameObject.name : "<null>"));
                        return;
                    }
                }
                else
                {
                    _doorCloseObservedSince = 0f;
                }
            }
            else
            {
                _doorCloseObservedSince = 0f;
            }

            // 3. Loop detector — sampled, not per-frame.
            if (Time.unscaledTime >= _nextLoopSampleTime)
            {
                _nextLoopSampleTime = Time.unscaledTime + LoopSampleIntervalSeconds;
                Vector3 sample = new Vector3(playerPos.x, 0f, playerPos.z);
                _loopWindow.Enqueue(sample);
                while (_loopWindow.Count > LoopSampleWindow) _loopWindow.Dequeue();
                if (_loopWindow.Count == LoopSampleWindow && AllSamplesWithinRadius(_loopWindow, LoopRadiusMeters))
                {
                    FinishLeg("looped");
                    return;
                }
            }

            // 4. Budget ceiling — safety net.
            if (Time.unscaledTime - _routeStartUnscaledTime > _routeBudgetSeconds)
            {
                FinishLeg("budget");
                return;
            }

            // 5. Steering stall: the autowalk's own progress detector kicks in when the player
            // hasn't moved; it will call StopNavigationBlocked, which ends the route via the
            // HasActiveRoute=false branch above. Nothing extra to do here — the autowalk's
            // _lastAutoWalkProgressTime detector is the stall signal.
        }

        // ---- Phase: Verifying -------------------------------------------------------------
        // The follower stopped. Confirm the object is actually INTERACTABLE from here by turning
        // to face it and asking the game's own precondition (InteractableManager.IsPlayerInRange
        // with the object selected). This re-partitions the geometric arrival/stall verdict into
        // ground truth:
        //   - in range          → arrived_verified  (the object is reachable AND usable)
        //   - in range, gated    → arrived_gated     (positioned fine; dateable eligibility gate
        //                                             refuses — not a nav failure)
        //   - turn timed out:
        //       was at goal cell → arrived_unconfirmed (reached the cell but couldn't select the
        //                                               object — a geometric FALSE POSITIVE)
        //       stopped short    → stalled             (a genuine nav failure)
        // Stamps coverage while turning so the verified bitmap still credits the spot.

        // True when the follower reached the goal cell before this probe (vs. gave up short).
        private static bool _verifyGeometricallyAtCell;

        private static void BeginVerify(bool geometricallyAtCell)
        {
            _verifyGeometricallyAtCell = geometricallyAtCell;
            // Tear the autowalk drive down but keep the route installed — the probe needs the
            // route's target to resolve the look point and the in-range match.
            try { SimpleNavBridge.EndStep(); } catch { }
            AccessibilityWatcher.ProbeSweepInteraction(_currentRoute, reset: true);
            _phase = Phase.Verifying;
        }

        private static void StepVerifying()
        {
            if (BetterPlayerControl.Instance != null)
                StampCoverage(BetterPlayerControl.Instance.transform.position);

            AccessibilityWatcher.SweepProbeState state =
                AccessibilityWatcher.ProbeSweepInteraction(_currentRoute, reset: false);

            switch (state)
            {
                case AccessibilityWatcher.SweepProbeState.Turning:
                    return; // keep turning next frame
                case AccessibilityWatcher.SweepProbeState.InRange:
                    FinishLeg("arrived_verified");
                    return;
                case AccessibilityWatcher.SweepProbeState.InRangeGated:
                    FinishLeg("arrived_gated");
                    return;
                case AccessibilityWatcher.SweepProbeState.GaveUp:
                    // Couldn't select the object from where we stopped. If we'd reached the goal
                    // cell, the cell is a geometric false-positive (reached, not interactable);
                    // if we stopped short, it's a real stall.
                    FinishLeg(_verifyGeometricallyAtCell ? "arrived_unconfirmed" : "stalled");
                    return;
            }
        }

        // Sweep arrival: the player is within one cell-and-a-bit of the route's FINAL waypoint
        // (the goal stand-cell), on the same floor level. XZ-only proximity plus a Y gate that
        // rejects mid-stair poses (player still descending reads close in XZ but is meters up in
        // Y). Deliberately independent of the object transform — see the StepRunning note.
        private const float GoalWaypointArrivalRadiusM = 1.35f;   // mirrors WaypointArrivalRadius
        private const float GoalWaypointMaxYDeltaM = 1.5f;        // mirrors ArrivalMaxYDeltaM

        private static bool HasReachedGoalWaypoint(Vector3 playerPos)
        {
            if (_currentRoute == null || _currentRoute.Waypoints == null || _currentRoute.Waypoints.Count == 0)
                return false;
            Vector3 goal = _currentRoute.Waypoints[_currentRoute.Waypoints.Count - 1];
            if (Mathf.Abs(playerPos.y - goal.y) > GoalWaypointMaxYDeltaM)
                return false;
            float dx = goal.x - playerPos.x;
            float dz = goal.z - playerPos.z;
            return (dx * dx + dz * dz) <= GoalWaypointArrivalRadiusM * GoalWaypointArrivalRadiusM;
        }

        // A drive stall is worth retrying only if THIS attempt actually got the player moving
        // before it stalled — i.e. the follower made progress down the corridor and then wedged
        // A game-state gate (not a nav result): the player controller wasn't in CanControl, a
        // menu/dialogue/popup/phone was up, or the view wasn't HOUSE when the leg tried to start.
        // Transient (the prior leg's interaction is still settling) and says nothing about whether
        // the route is walkable, so it's bucketed separately from real nav failures in the summary
        // and doesn't stamp a failure cell. See GetNavigationUnavailableReason for the full set.
        private static bool IsTransientGateOutcome(string outcome)
        {
            return !string.IsNullOrEmpty(outcome) && outcome.StartsWith("input_failed");
        }

        // A SUCCESSFUL arrival: the object was reached AND interaction was confirmed (or only the
        // dateable eligibility gate refused, which is positioning-fine). These mark the object
        // visited and end the failure streak. NOT included: arrived_unconfirmed — that reached
        // the goal cell but could not select the object (a geometric false-positive), so it's
        // treated like a failure (stays in the pool, counts toward the give-up cap) until a
        // later approach from a different angle either confirms it or exhausts the retries.
        private static bool IsArrivalOutcome(string outcome)
        {
            return outcome == "arrived" || outcome == "arrived_verified" || outcome == "arrived_gated";
        }

        // End the current leg: record its outcome once, then chain to the next nearest object from
        // wherever the player now stands. There is no recovery relocation — if a blocker boxes the
        // player in, the trapped objects record failures and drain via the per-object give-up cap.
        private static void FinishLeg(string outcome)
        {
            // Tear down the autowalk regardless of what we do next.
            try { AccessibilityWatcher.StopCoverageSweepRoute(); } catch { }

            RecordLegResult(outcome);

            if (IsArrivalOutcome(outcome))
            {
                // Reached: mark visited so the picker won't re-select it. Making progress clears the
                // recently-failed skip set so those objects become eligible again from this new
                // position.
                _objectVisited.Add(_currentManifestIndex);
                _recentlyFailed.Clear();
            }
            else
            {
                // Failed: the object stays in the pool to be retried from another angle — UNLESS it
                // has now failed MaxObjectFailures times, in which case we give up on it (mark
                // visited so it leaves the pool; its failures are recorded). The per-object cap
                // counts EVERY failure including transient gates, so an object we can never even
                // start a route to still eventually leaves the pool — this is what guarantees the
                // sweep terminates. _recentlyFailed makes the picker sample DIFFERENT nearby objects
                // after a failure rather than re-failing the same one in place.
                _recentlyFailed.Add(_currentManifestIndex);
                int fails = _objectFailCount.TryGetValue(_currentManifestIndex, out int f) ? f + 1 : 1;
                _objectFailCount[_currentManifestIndex] = fails;
                if (fails >= MaxObjectFailures) _objectVisited.Add(_currentManifestIndex);
            }

            _currentRoute = null;
            // Periodically flush so a crash doesn't lose hours of progress.
            if ((_results.Count % 50) == 0) FlushResults();

            // Always chain on from where the player stands — no relocation. A long failure streak
            // just means a region is boxed in; those objects keep recording failures until the
            // per-object give-up cap drains them from the pool (which is what terminates the sweep).
            _phase = Phase.BetweenRoutes;
        }

        private static void RecordLegResult(string outcome)
        {
            var entry = _manifest.entries[_currentManifestIndex];
            Vector3 endPos = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            Vector3 startPos = _currentRoute != null && _currentRoute.Waypoints != null && _currentRoute.Waypoints.Count > 0
                ? _currentRoute.Waypoints[0]
                : Vector3.zero;
            float displacement = Vector3.Distance(new Vector3(startPos.x, 0, startPos.z),
                                                  new Vector3(endPos.x, 0, endPos.z));
            var result = new RouteResult
            {
                manifest_index = _currentManifestIndex,
                floor = entry.floor,
                cell = entry.cell,
                outcome = outcome,
                cost_m = entry.cost_m,
                elapsed_s = Time.unscaledTime - _routeStartUnscaledTime,
                displacement_m = displacement,
                name = entry.name,
            };
            if (!IsArrivalOutcome(outcome))
            {
                RuntimeBlockerProbe probe = RuntimeBlockerProbe.Last;
                RuntimeBlockerProbe.Hit hit = probe?.Nearest();
                if (hit != null)
                {
                    result.blocker_path = hit.Path;
                    result.blocker_layer = hit.Layer;
                    result.blocker_distance = hit.Distance;
                    result.blocker_mode = ClassifyBlockerMode(hit);
                }
                // Probe is one-shot; clear so the next route doesn't see a stale value.
                RuntimeBlockerProbe.Last = null;
                // Reliable stall triage: classify the navmesh state at where the player ACTUALLY
                // got stuck (endPos), independent of the unreliable nearest-collider blocker label.
                // Resolve the floor from the player's real Y, NOT entry.floor: entry.floor is the
                // TARGET's floor, but a cross-floor leg can stall before the player gets there (still
                // downstairs, or mid-stair at an in-between Y) — classifying that stuck position
                // against the wrong floor's grid gives garbage. The player's Y picks the right grid.
                string stuckFloor = entry.floor;
                SimpleNavPlanner.TryGetPlayerFloorLabel(endPos.y, out string resolvedFloor);
                if (!string.IsNullOrEmpty(resolvedFloor)) stuckFloor = resolvedFloor;
                result.stall_class = SimpleNavPlanner.ClassifyStallCell(stuckFloor, endPos.x, endPos.z);
            }
            _results.Add(result);
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavCoverageSweep result idx=" + _currentManifestIndex +
                    " floor=" + entry.floor + " cell=(" + entry.cell[0] + "," + entry.cell[1] + ")" +
                    " outcome=" + outcome +
                    " elapsed=" + (Time.unscaledTime - _routeStartUnscaledTime).ToString("0.0") +
                    " start=" + startPos.ToString("F2") + " end=" + endPos.ToString("F2") +
                    " moved=" + displacement.ToString("0.00") + "m");
        }

        private static void RecordCurrentRouteAsException(string detail)
        {
            if (_currentManifestIndex < 0 || _currentManifestIndex >= _manifest.entries.Length) return;
            var entry = _manifest.entries[_currentManifestIndex];
            _results.Add(new RouteResult
            {
                manifest_index = _currentManifestIndex,
                floor = entry.floor,
                cell = entry.cell,
                outcome = "exception:" + detail,
                name = entry.name,
            });
            _objectVisited.Add(_currentManifestIndex);  // don't re-pick an object we can't resolve
        }

        private static void AdvanceToNextEntry()
        {
            _currentRoute = null;
            _phase = Phase.BetweenRoutes;
            if ((_results.Count % 50) == 0) FlushResults();
        }

        private static void FinishSweep()
        {
            FlushResults();
            WriteVerifiedBitmaps();

            // The walk chain can record the SAME object several times — a failure, then (after a
            // relocate) a later pass from a different angle. The summary is about OBJECTS, not
            // attempts, so collapse to one outcome per manifest_index, with "arrived" winning over
            // any earlier failure (the object is reachable; the earlier failure is kept in the raw
            // results for triage). A real nav failure beats a transient gate; gate beats nothing.
            var finalOutcome = new Dictionary<int, string>();
            for (int i = 0; i < _results.Count; i++)
            {
                int idx = _results[i].manifest_index;
                string o = _results[i].outcome ?? "";
                if (!finalOutcome.TryGetValue(idx, out string prev))
                {
                    finalOutcome[idx] = o;
                    continue;
                }
                finalOutcome[idx] = BetterOutcome(prev, o);
            }

            int passed = 0, skipped = 0, failed = 0, noPath = 0, noLos = 0, offFloor = 0, gated = 0, unconfirmed = 0;
            foreach (string o in finalOutcome.Values)
            {
                if (IsArrivalOutcome(o)) passed++;
                // Reached the goal cell but the interaction probe couldn't select the object — a
                // geometric false-positive. Surfaced separately: it's NOT a clean pass (the
                // player would arrive and be unable to interact) but also NOT a walk failure.
                else if (o == "arrived_unconfirmed") unconfirmed++;
                else if (o == "skipped_already_covered") skipped++;
                else if (o == "no_path") noPath++;
                // Reachable but occluded from every navigable cell (planner TargetNoLineOfSight):
                // an interactability/placement defect, distinct from can't-get-there.
                else if (o == "no_los") noLos++;
                // Legacy manifests still emit off_floor as a (non-drive) result entry. Newer
                // manifests exclude exterior decor from `entries` entirely (counted below from
                // excluded_exterior), so this branch is a no-op for them.
                else if (o == "off_floor") offFloor++;
                // Transient game-state gates (input_failed:playerState=CantMove, dialogue/menu/
                // phone open, etc.) are NOT nav failures: the leg never got to drive because the
                // game wasn't in a controllable state — bucket separately so they don't inflate
                // `failed`. An object only lands here if EVERY attempt was gated and it was never
                // reached; a single later arrival promotes it to passed via BetterOutcome.
                else if (IsTransientGateOutcome(o)) gated++;
                else failed++;
            }
            ReportOutcome(passed, skipped, noPath, noLos, offFloor, gated, failed, unconfirmed);
            ReportCrossFloorSplit(finalOutcome);
            ReportStallClassSplit();
            ReportPlacementSuccess();
        }

        // Cross-floor vs same-floor breakdown. A whole storey silently disconnecting (the
        // missing-staircase bug) shows up here as ~100% cross-floor failure while same-floor
        // stays healthy — a signal that's INVISIBLE in the flat outcome totals, where cross-floor
        // failures are diluted across the much larger same-floor population. Split each object's
        // FINAL outcome by whether its leg crossed floors (manifest from_floor != floor), so a
        // future floor-disconnect (or a stair/door-tag regression that only bites cross-floor) is
        // obvious from the summary. See [[project-navigation-stairs-missing-from-bake-2026-06-15]].
        private static void ReportCrossFloorSplit(Dictionary<int, string> finalOutcome)
        {
            if (Main.Log == null || _manifest?.entries == null) return;
            int xPass = 0, xFail = 0, xOther = 0, sPass = 0, sFail = 0, sOther = 0;
            foreach (var kvp in finalOutcome)
            {
                int idx = kvp.Key;
                if (idx < 0 || idx >= _manifest.entries.Length) continue;
                var e = _manifest.entries[idx];
                // Cross-floor only when both floors are known and differ. A null/empty from_floor
                // (e.g. the first leg from the player's start, or a legacy manifest) is counted as
                // same-floor so it never inflates the cross-floor failure rate spuriously.
                bool crossFloor = !string.IsNullOrEmpty(e.from_floor)
                    && !string.IsNullOrEmpty(e.floor)
                    && !string.Equals(e.from_floor, e.floor, StringComparison.OrdinalIgnoreCase);
                string o = kvp.Value ?? "";
                bool pass = IsArrivalOutcome(o) || o == "arrived_unconfirmed";
                // Real walk failures (no_path/no_los/stalled/looped/budget/door_failed/exception).
                // Transient gates and skipped don't count as failures.
                bool fail = !pass && !IsTransientGateOutcome(o)
                    && o != "skipped_already_covered" && o != "off_floor";
                if (crossFloor)
                {
                    if (pass) xPass++; else if (fail) xFail++; else xOther++;
                }
                else
                {
                    if (pass) sPass++; else if (fail) sFail++; else sOther++;
                }
            }
            int xTot = xPass + xFail + xOther, sTot = sPass + sFail + sOther;
            float xFailPct = xTot > 0 ? 100f * xFail / xTot : 0f;
            float sFailPct = sTot > 0 ? 100f * sFail / sTot : 0f;
            Main.Log.LogInfo(
                "SimpleNavCoverageSweep cross-floor: cross n=" + xTot + " pass=" + xPass +
                " fail=" + xFail + " (" + xFailPct.ToString("0.0", CultureInfo.InvariantCulture) +
                "%) other=" + xOther + " | same-floor n=" + sTot + " pass=" + sPass +
                " fail=" + sFail + " (" + sFailPct.ToString("0.0", CultureInfo.InvariantCulture) +
                "%) other=" + sOther +
                (xTot > 0 && xFailPct >= 80f
                    ? "  *** cross-floor failure >=80% — suspect a disconnected storey "
                      + "(check inter_floor_edges / stairs) ***"
                    : ""));
        }

        // Collapse two outcomes for the SAME object to the one that best describes its final
        // reachability. Priority: a verified/clean arrival (reached AND interactable) > a
        // reached-but-not-interactable cell (arrived_unconfirmed) > a concrete nav failure
        // (no_path / stalled / looped / budget / door_failed / exception) > a transient game-state
        // gate > skipped. So one confirmed approach makes the object "passed"; a cell that was
        // reached but never confirmed outranks a hard walk failure (it got further); and among
        // never-reached outcomes a real walkability verdict outranks a game-state artifact.
        private static string BetterOutcome(string a, string b)
        {
            return OutcomeRank(a) >= OutcomeRank(b) ? a : b;
        }

        private static int OutcomeRank(string o)
        {
            if (IsArrivalOutcome(o)) return 5;
            if (string.IsNullOrEmpty(o)) return 0;
            if (o == "arrived_unconfirmed") return 4;
            if (o == "skipped_already_covered") return 1;
            if (IsTransientGateOutcome(o)) return 2;
            return 3;  // no_path / stalled / looped / budget / door_failed:* / exception:* / off_floor
        }

        // Breakdown of STALLED legs by the navmesh state at the player's stuck cell. The flat
        // "stalled" total and the blocker-mode label both hide WHY the leg stalled; this splits it
        // into the actionable classes (off-mesh dead-pocket vs open-space mislabel vs real edge
        // graze) so the dominant cause is visible without a hand-decompose. Tallies the per-row
        // stall_class across stalled rows (deduped by leg). See
        // [[project-navigation-footprint-stalls-decomposed-2026-06-16]].
        private static void ReportStallClassSplit()
        {
            if (Main.Log == null || _results == null) return;
            var seen = new HashSet<int>();
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int total = 0;
            foreach (RouteResult r in _results)
            {
                if (r.outcome != "stalled") continue;
                if (!seen.Add(r.manifest_index)) continue;   // one class per leg (first stall row)
                string c = string.IsNullOrEmpty(r.stall_class) ? "unclassified" : r.stall_class;
                counts.TryGetValue(c, out int n);
                counts[c] = n + 1;
                total++;
            }
            if (total == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.Append("SimpleNavCoverageSweep stall-class: total=").Append(total);
            foreach (var kv in counts)
                sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            Main.Log.LogInfo(sb.ToString());
        }

        // The honest scorecard. WHAT THIS SWEEP TESTS: can the PLANNER get the player close enough
        // to each object to interact with it — i.e. place the player on a cell from which the
        // camera-aim raycast hits the target — covering as many objects as possible systematically
        // WITHOUT teleporting (teleporting would invalidate the start position, throwing every
        // route's viability into question). It does NOT test "is object X reachable from point Y":
        // the player's real start is unpredictable and the planner picks each final destination
        // from wherever the player happens to be. So the only verdict per object is whether the
        // planner ever landed a valid interaction standpoint.
        //
        // DISTANCE MOVED IS IRRELEVANT. A no-walk LOS pass is a FULL pass, not "untested": the
        // planner had previously walked the player to that cell (for a nearby object), the player
        // stood there, and the raycast hit — the whole loop executed; that this leg added 0 metres
        // is meaningless because walk distance was never under test. Conversely, a leg that doesn't
        // move, turns, and STILL can't hit the target is a real failure — the planner chose the
        // wrong cell for this object and should have placed the player where the ray clears. That
        // surfaces below as no_los / unconfirmed (a planner-PLACEMENT defect), exactly what we want
        // to catch. There is therefore no "walk axis" worth reporting; displacement is not used.
        //
        // The flat outcome totals mislead two ways, both corrected here: (1) the manifest carries
        // DUPLICATE object names (~2x inflation — preset/lighting copies), so per-row counts double
        // every object — we dedupe by NAME; (2) ~51% of objects flip outcome across attempts in one
        // run, so an object PASSES if ANY attempt confirmed it (one valid standpoint is success).
        //
        //   Per distinct object:
        //     pass        : the planner landed a valid interaction standpoint at least once
        //                   (arrived/_verified/_gated). THE success metric.
        //     no_los      : reached a cell but the aim raycast was occluded on every cell the
        //                   planner tried — planner PLACEMENT defect (wrong standpoint for this
        //                   object) or a genuine occlusion the planner can't route around.
        //     unconfirmed : reached a cell, probe never selected the object, never a hard no_los —
        //                   also a placement/standpoint problem, just not a clean no_los verdict.
        //     unreached   : the planner never landed the player on ANY cell (no_path / every leg
        //                   stalled) — a routing/walkability failure, the only bucket where getting
        //                   there at all is the blocker.
        //
        // DISTANCE MOVED is kept as DIAGNOSTIC context on FAILURES (not part of the verdict): for a
        // no_los/unconfirmed object, displacement~0 means the planner placed the player AT the
        // object with a bad angle (pure standpoint defect — pick a clearer cell), while a large
        // displacement means it routed somewhere but the final cell still didn't clear; for an
        // unreached object, small displacement = froze early vs large = walked then stalled. So the
        // failure line below splits each failure bucket by whether its best attempt moved.
        //
        // Also tallies per-object outcome AGREEMENT so follower nondeterminism stays visible.
        private static void ReportPlacementSuccess()
        {
            if (Main.Log == null || _results == null || _results.Count == 0) return;

            // Group every attempt by OBJECT NAME (collapses the manifest's duplicate entries, which
            // manifest_index does NOT — distinct indices can share a name). Unnamed (cell-mode) rows
            // fall back to a per-index key so they each stay their own object.
            var byObject = new Dictionary<string, List<RouteResult>>(StringComparer.Ordinal);
            foreach (RouteResult r in _results)
            {
                string key = !string.IsNullOrEmpty(r.name) ? r.name : ("#idx" + r.manifest_index);
                if (!byObject.TryGetValue(key, out var list)) { list = new List<RouteResult>(); byObject[key] = list; }
                list.Add(r);
            }

            int pass = 0, noLos = 0, unconfirmed = 0, unreached = 0;
            int nondetermined = 0, multiAttempt = 0;
            // Diagnostic: per failure bucket, how many objects had at least one attempt that MOVED
            // (>= GenuineWalkMeters) vs none. "moved" failures routed somewhere but the final cell
            // failed; "still" failures never left the start — for no_los/unconfirmed that's a pure
            // standpoint defect, for unreached an early freeze.
            int noLosMoved = 0, unconfMoved = 0, unreachedMoved = 0;

            foreach (var kv in byObject)
            {
                List<RouteResult> attempts = kv.Value;
                bool anyPass = false, anyHardNoLos = false, anyReachedCell = false, anyMoved = false;
                var distinctOutcomes = new HashSet<string>(StringComparer.Ordinal);

                foreach (RouteResult a in attempts)
                {
                    string o = a.outcome ?? "";
                    distinctOutcomes.Add(o);
                    if (IsArrivalOutcome(o)) anyPass = true;
                    if (o == "no_los") anyHardNoLos = true;
                    if (IsArrivalOutcome(o) || o == "arrived_unconfirmed") anyReachedCell = true;
                    if (a.displacement_m >= GenuineWalkMeters) anyMoved = true;
                }

                // One valid standpoint is success. Otherwise rank the failure: a cell was reached
                // but LOS failed (placement defect) > never reached a cell at all (routing failure).
                if (anyPass) pass++;
                else if (anyHardNoLos) { noLos++; if (anyMoved) noLosMoved++; }
                else if (anyReachedCell) { unconfirmed++; if (anyMoved) unconfMoved++; }
                else { unreached++; if (anyMoved) unreachedMoved++; }

                if (attempts.Count > 1)
                {
                    multiAttempt++;
                    if (distinctOutcomes.Count > 1) nondetermined++;
                }
            }

            int objects = byObject.Count;
            var ci = CultureInfo.InvariantCulture;
            float passPct = objects > 0 ? 100f * pass / objects : 0f;
            // HEADLINE: per-object planner-placement success — did the planner ever land a cell from
            // which the object is interactable. The only metric that ultimately matters.
            Main.Log.LogInfo(
                "SimpleNavCoverageSweep INTERACT-SUCCESS=" + pass + "/" + objects +
                " (" + passPct.ToString("0.0", ci) + "%) objects deduped by name from " + _results.Count + " rows" +
                " | nondeterministic=" + nondetermined + "/" + multiAttempt + " multi-attempt" +
                (multiAttempt > 0
                    ? " (" + (100f * nondetermined / multiAttempt).ToString("0.0", ci) + "% flip)"
                    : ""));
            // Failures with a distance-moved diagnostic: still=never moved (standpoint defect / early
            // freeze), moved=routed but the final cell still failed.
            Main.Log.LogInfo(
                "SimpleNavCoverageSweep failures: no_los=" + noLos + " (occluded; still=" + (noLos - noLosMoved) +
                " wrong standpoint, moved=" + noLosMoved + ")" +
                " unconfirmed=" + unconfirmed + " (couldn't select; still=" + (unconfirmed - unconfMoved) +
                " moved=" + unconfMoved + ")" +
                " unreached=" + unreached + " (no cell landed; still=" + (unreached - unreachedMoved) +
                " early-freeze, moved=" + unreachedMoved + " walked-then-stalled)");
        }

        private static void ReportOutcome(int passed, int skipped, int noPath, int noLos, int offFloor, int gated, int failed, int unconfirmed)
        {
            // Exterior decor (fence/tree/drone, Y off every floor) is expected-unreachable and is
            // no longer driven: the planner drops it from `entries` and records the count here.
            // Add it to the off-floor total so the summary still surfaces it without it inflating
            // the results denominator or the failed bucket.
            offFloor += _manifest?.excluded_exterior_count ?? 0;
            if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep done passed=" + passed +
                " unconfirmed=" + unconfirmed + " skipped=" + skipped + " no_path=" + noPath +
                " no_los=" + noLos +
                " off_floor=" + offFloor + " gated=" + gated + " failed=" + failed);
            ScreenReader.Say("Coverage sweep complete: " + passed + " passed, " + unconfirmed +
                " unconfirmed, " + failed + " failed", remember: false);
            SetPhoneBlockedForSweep(false);
            _phase = Phase.Idle;
            _manifest = null;
            _currentRoute = null;
            _results = null;
            _verified.Clear();
        }

        // ---- Coverage stamping ------------------------------------------------------------

        private static void StampCoverage(Vector3 worldPos)
        {
            string floorLabel = FloorForY(worldPos.y);
            if (floorLabel == null) return;
            if (!_manifest.floor_frames.TryGetValue(floorLabel, out FloorFrame frame)) return;
            if (!_verified.TryGetValue(floorLabel, out bool[] bitmap)) return;

            int ix = (int)Mathf.Floor((worldPos.x - frame.origin_x) / frame.cell_size);
            int iz = (int)Mathf.Floor((worldPos.z - frame.origin_z) / frame.cell_size);
            StampOne(bitmap, frame, ix, iz);
            StampOne(bitmap, frame, ix + 1, iz);
            StampOne(bitmap, frame, ix - 1, iz);
            StampOne(bitmap, frame, ix, iz + 1);
            StampOne(bitmap, frame, ix, iz - 1);
        }

        private static void StampOne(bool[] bitmap, FloorFrame frame, int ix, int iz)
        {
            if (ix < 0 || ix >= frame.nx || iz < 0 || iz >= frame.nz) return;
            bitmap[ix * frame.nz + iz] = true;
        }

        // Pick the floor whose floor_y is closest to the player's Y. The bake's floors are
        // well-separated (ground ~-0.5, upper ~12.5), so closest-by-Y is unambiguous.
        private static string FloorForY(float y)
        {
            string best = null;
            float bestDist = float.MaxValue;
            foreach (var kv in _manifest.floor_frames)
            {
                float d = Mathf.Abs(kv.Value.floor_y - y);
                if (d < bestDist) { bestDist = d; best = kv.Key; }
            }
            // Cap: if the player is more than 4m from any floor band (mid-fall, off-mesh),
            // don't credit coverage to any floor.
            if (bestDist > 4f) return null;
            return best;
        }

        // ---- Helpers ----------------------------------------------------------------------

        private static float ComputeBudgetSeconds(SimpleNavRoute route)
        {
            // Sum waypoint segment lengths as an approximation of route cost. We could also
            // pass cost_m from the manifest, but the polyline length is what the executor
            // actually has to walk, so it's the more honest budget basis.
            float m = 0f;
            for (int i = 1; i < route.Waypoints.Count; i++)
            {
                Vector3 a = route.Waypoints[i - 1];
                Vector3 b = route.Waypoints[i];
                float dx = a.x - b.x, dz = a.z - b.z;
                m += Mathf.Sqrt(dx * dx + dz * dz);
            }
            return m / BudgetMetersPerSecond + BudgetSlackSeconds;
        }

        private static bool AllSamplesWithinRadius(Queue<Vector3> samples, float radius)
        {
            // O(n^2) but n=16; cheap. Compute centroid then check max-dist.
            Vector3 c = Vector3.zero;
            foreach (var s in samples) c += s;
            c /= samples.Count;
            float r2 = radius * radius;
            foreach (var s in samples)
            {
                float dx = s.x - c.x, dz = s.z - c.z;
                if (dx * dx + dz * dz > r2) return false;
            }
            return true;
        }

        // Three failure modes for a runtime stall:
        //   "state"          — door / state-wall the bake modelled in one pose; live state diverges.
        //   "classification" — collider on a layer the bake skips (e.g. Mirror=18, layer 31).
        //                       Bake's classification rule rejected the geometry, runtime did not.
        //   "footprint"      — default: mesh footprint and collider footprint disagree. The bake
        //                       rasterized something narrower/elsewhere than the real collider.
        // The mode is a triage hint, not a verdict — offline tooling looks up the specific rule.
        private static string ClassifyBlockerMode(RuntimeBlockerProbe.Hit hit)
        {
            if (hit == null) return "unknown";

            // Classification mode: layer the bake explicitly skips.
            if (hit.Layer == 18 || hit.Layer == 31) return "classification";

            // State mode: name match against known state-bearing colliders. Cheap heuristic;
            // the offline triage tool can refine with the full GameObject path.
            string n = hit.Name ?? string.Empty;
            string p = hit.Path ?? string.Empty;
            if (n.StartsWith("Doors_", StringComparison.OrdinalIgnoreCase)) return "state";
            if (n.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (n.IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 n.IndexOf("Slide", StringComparison.OrdinalIgnoreCase) >= 0))
                return "state";
            if (n.IndexOf("DaemonWall", StringComparison.OrdinalIgnoreCase) >= 0) return "state";
            if (n.IndexOf("DresserWall", StringComparison.OrdinalIgnoreCase) >= 0) return "state";
            if (p.IndexOf("/Doors/", StringComparison.OrdinalIgnoreCase) >= 0) return "state";

            return "footprint";
        }

        private static void ForceCloseAllDoors()
        {
            Door[] doors = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < doors.Length; i++)
            {
                Door d = doors[i];
                if (d == null) continue;
                if (!d.open && !SimpleNavBridge.IsActiveDoorMoving()) continue;
                try { SimpleNavBridge.ForceDoorClosed(d); } catch { }
            }

            // Container doors are SlidingDoors, a separate component the Door[] sweep above
            // misses. Reset any the previous leg opened so each leg starts from the closed
            // state (sliders are default-closed, so we only need to undo our own opens). See
            // [[project_navigation_container_open_on_interact]].
            SlidingDoor[] sliders = UnityEngine.Object.FindObjectsOfType<SlidingDoor>();
            for (int i = 0; i < sliders.Length; i++)
            {
                SlidingDoor s = sliders[i];
                if (s == null || !s.open) continue;
                try { SimpleNavBridge.ForceSliderClosed(s); } catch { }
            }
        }

        // ---- Walk-mode loop ---------------------------------------------------------------
        // Single continuous traversal: pick the nearest untested reachable cell, plan a leg,
        // walk it. On arrival mark the target (and the 8-neighbourhood) walkable. On stall
        // mark the target impassable with a full diagnostic record. Either way, the cell is
        // never re-tested in the same sweep.

        private static bool _walkStartTeleported;

        private static void WalkStepPickLeg()
        {
            // Make sure the prior leg, if any, is fully torn down.
            try { SimpleNavBridge.EndStep(); } catch { }

            // On the very first leg of the sweep: teleport to the manifest start and close every
            // door exactly once. Subsequent legs run continuously from wherever the player ended
            // up, exercising natural door interactions along the way.
            if (!_walkStartTeleported)
            {
                if (!TeleportToWalkStart()) { AbortSweep("walk: cannot resolve start"); return; }
                ForceCloseAllDoors();
                _walkStartTeleported = true;
                _nextActionTime = Time.unscaledTime + WaitAfterTeleportSeconds;
                return;
            }
            if (Time.unscaledTime < _nextActionTime) return;

            Vector3 playerPos = BetterPlayerControl.Instance != null
                ? BetterPlayerControl.Instance.transform.position
                : Vector3.zero;
            if (BetterPlayerControl.Instance == null) { AbortSweep("walk: no player"); return; }

            // Stamp the cell the player is standing on as walkable — we're here, so it works.
            StampWalkable(playerPos);

            if (!PickNearestUntested(playerPos, out string floor, out int ix, out int iz))
            {
                FinishWalkSweep();
                return;
            }

            // Plan a route to the cell centre.
            if (!_manifest.floor_frames.TryGetValue(floor, out FloorFrame frame))
            {
                MarkImpassable(floor, ix, iz, playerPos, "no_path", Vector3.zero, null);
                return;
            }
            float wx = frame.origin_x + ix * frame.cell_size + frame.cell_size * 0.5f;
            float wz = frame.origin_z + iz * frame.cell_size + frame.cell_size * 0.5f;
            Vector3 targetPos = new Vector3(wx, frame.floor_y, wz);
            // 0.5m interaction radius: arrival means "within one cell of the target centre."
            SimpleNavRoute route = SimpleNavPlanner.Plan(
                playerPos, targetPos, 0.5f,
                targetName: "walk_cell:" + floor + ":" + ix + ":" + iz,
                targetGameObjectId: 0);
            if (route == null)
            {
                // Planner couldn't reach this cell from the player's current position. That's
                // a planner / connectivity bug, not a bake-rasterization bug — record it on a
                // separate channel so the impass list stays focused on runtime stalls.
                MarkPlannerFailure(floor, ix, iz, playerPos, "no_path");
                return;
            }

            _currentRoute = route;
            _walkTargetFloor = floor;
            _walkTargetIx = ix;
            _walkTargetIz = iz;
            _routeStartUnscaledTime = Time.unscaledTime;
            _routeBudgetSeconds = ComputeBudgetSeconds(route);
            _loopWindow.Clear();
            _nextLoopSampleTime = Time.unscaledTime + LoopSampleIntervalSeconds;
            _doorCloseObservedSince = 0f;
            if (!AccessibilityWatcher.TryStartCoverageSweepRoute(route, out string detail))
            {
                MarkPlannerFailure(floor, ix, iz, playerPos, "input_failed:" + detail);
                return;
            }
            _phase = Phase.WalkRunningLeg;
        }

        private static void WalkStepRunningLeg()
        {
            if (BetterPlayerControl.Instance == null) { AbortSweep("walk: no player"); return; }
            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;
            StampWalkable(playerPos);  // stamp every frame so back-tracks credit cells

            bool arrived = SimpleNavBridge.HasArrivedAtRouteTarget(playerPos);
            if (arrived) { FinishWalkLegArrived(); return; }
            if (!SimpleNavBridge.HasActiveRoute) { FinishWalkLegFailure("stalled", playerPos); return; }

            // Door-open failure (same logic as the dispersed path).
            if (SimpleNavBridge.ActiveDoor != null)
            {
                DoorPortal door = SimpleNavBridge.ActiveDoor;
                bool open = door.open;
                bool moving = SimpleNavBridge.IsActiveDoorMoving();
                if (!open && !moving)
                {
                    if (_doorCloseObservedSince <= 0f) _doorCloseObservedSince = Time.unscaledTime;
                    else if (Time.unscaledTime - _doorCloseObservedSince > DoorOpenTimeoutSeconds)
                    {
                        string dname = door.gameObject != null ? door.gameObject.name : "<null>";
                        FinishWalkLegFailure("door_failed:" + dname, playerPos);
                        return;
                    }
                }
                else _doorCloseObservedSince = 0f;
            }
            else _doorCloseObservedSince = 0f;

            if (Time.unscaledTime >= _nextLoopSampleTime)
            {
                _nextLoopSampleTime = Time.unscaledTime + LoopSampleIntervalSeconds;
                _loopWindow.Enqueue(new Vector3(playerPos.x, 0f, playerPos.z));
                while (_loopWindow.Count > LoopSampleWindow) _loopWindow.Dequeue();
                if (_loopWindow.Count == LoopSampleWindow && AllSamplesWithinRadius(_loopWindow, LoopRadiusMeters))
                {
                    FinishWalkLegFailure("looped", playerPos);
                    return;
                }
            }

            if (Time.unscaledTime - _routeStartUnscaledTime > _routeBudgetSeconds)
            {
                FinishWalkLegFailure("budget", playerPos);
            }
        }

        private static void FinishWalkLegArrived()
        {
            _walkLegIndex++;
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavCoverageSweep walk-leg " + _walkLegIndex +
                    " arrived target=(" + _walkTargetFloor + "," + _walkTargetIx + "," + _walkTargetIz + ")");
            try { AccessibilityWatcher.StopCoverageSweepRoute(); } catch { }
            try { SimpleNavBridge.EndStep(); } catch { }
            // Mark target + neighbourhood walkable.
            MarkWalkableNeighbourhood(_walkTargetFloor, _walkTargetIx, _walkTargetIz);
            _currentRoute = null;
            _phase = Phase.WalkPickLeg;
            _nextActionTime = 0f;
            // Flush periodically.
            if ((_walkLegIndex % 20) == 0) FlushWalkResults();
        }

        private static void FinishWalkLegFailure(string outcome, Vector3 playerPos)
        {
            _walkLegIndex++;
            // Compute target world coords for the record.
            Vector3 targetPos = Vector3.zero;
            if (_manifest.floor_frames.TryGetValue(_walkTargetFloor, out FloorFrame frame))
            {
                targetPos = new Vector3(
                    frame.origin_x + _walkTargetIx * frame.cell_size + frame.cell_size * 0.5f,
                    frame.floor_y,
                    frame.origin_z + _walkTargetIz * frame.cell_size + frame.cell_size * 0.5f);
            }
            // Stash the bridge state we'll need for the record before we tear it down.
            string activeDoorName = SimpleNavBridge.ActiveDoor != null && SimpleNavBridge.ActiveDoor.gameObject != null
                ? SimpleNavBridge.ActiveDoor.gameObject.name : null;
            string segmentDoorName = null;
            if (SimpleNavBridge.ActiveWaypoint != null)
            {
                // Best-effort tag from the current waypoint.
                segmentDoorName = SimpleNavBridge.ActiveWaypoint.DoorName;
            }
            // Fire the diagnostic probe BEFORE tearing down the autowalk — StopCoverageSweepRoute
            // calls StopNavigationRuntime which zeros _lastAutoWalkProgressTime, and emptying the
            // input may also briefly leave the player in a state where physics queries return
            // nothing. The probe stamps RuntimeBlockerProbe.Last; MarkImpassable then reads it
            // without re-probing.
            Vector3 probeTarget = (targetPos.sqrMagnitude > 0.001f) ? targetPos : playerPos + Vector3.forward;
            try { AccessibilityWatcher.ProbeRuntimeBlockerNow(playerPos, probeTarget); }
            catch (Exception ex) { if (Main.Log != null) Main.Log.LogWarning("ProbeRuntimeBlockerNow threw: " + ex.Message); }
            try { AccessibilityWatcher.StopCoverageSweepRoute(); } catch { }
            try { SimpleNavBridge.EndStep(); } catch { }
            // Drop sub-half-second failures: those are planner/bridge errors that fired before
            // the autowalk had a chance to make progress, not navigation stalls. Treating them
            // as impass records would pollute the bake-triage signal with planner-side bugs.
            float elapsed = Time.unscaledTime - _routeStartUnscaledTime;
            if (elapsed < 0.5f)
            {
                if (Main.Log != null)
                    Main.Log.LogInfo("SimpleNavCoverageSweep walk-leg " + _walkLegIndex +
                        " dropped (early failure " + elapsed.ToString("0.00") + "s outcome=" + outcome + ")");
                _currentRoute = null;
                _phase = Phase.WalkPickLeg;
                _nextActionTime = 0f;
                return;
            }
            MarkImpassable(_walkTargetFloor, _walkTargetIx, _walkTargetIz, playerPos, outcome, targetPos,
                activeDoorName, segmentDoorName);
            _currentRoute = null;
            _phase = Phase.WalkPickLeg;
            _nextActionTime = 0f;
            if ((_walkLegIndex % 20) == 0) FlushWalkResults();
        }

        private static void MarkWalkableNeighbourhood(string floor, int ix, int iz)
        {
            if (!_manifest.floor_frames.TryGetValue(floor, out FloorFrame frame)) return;
            if (!_walkState.TryGetValue(floor, out byte[] state)) return;
            int r = WalkVerifyRadiusCells;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = ix + dx; if (x < 0 || x >= frame.nx) continue;
                for (int dz = -r; dz <= r; dz++)
                {
                    int z = iz + dz; if (z < 0 || z >= frame.nz) continue;
                    int k = x * frame.nz + z;
                    // Don't overwrite an impassable verdict — that would mask a real bug.
                    if (state[k] == CellUntested) state[k] = CellWalkable;
                }
            }
        }

        private static void StampWalkable(Vector3 worldPos)
        {
            string floor = FloorForY(worldPos.y);
            if (floor == null) return;
            if (!_manifest.floor_frames.TryGetValue(floor, out FloorFrame frame)) return;
            if (!_walkState.TryGetValue(floor, out byte[] state)) return;
            int ix = (int)Mathf.Floor((worldPos.x - frame.origin_x) / frame.cell_size);
            int iz = (int)Mathf.Floor((worldPos.z - frame.origin_z) / frame.cell_size);
            MarkWalkableNeighbourhood(floor, ix, iz);
        }

        private static void MarkImpassable(
            string floor, int ix, int iz, Vector3 playerPos, string outcome, Vector3 targetPos,
            string activeDoorName, string segmentDoorName = null)
        {
            if (_manifest.floor_frames.TryGetValue(floor, out FloorFrame frame) &&
                _walkState.TryGetValue(floor, out byte[] state) &&
                ix >= 0 && ix < frame.nx && iz >= 0 && iz < frame.nz)
            {
                state[ix * frame.nz + iz] = CellImpassable;
            }
            // Caller (FinishWalkLegFailure or WalkStepPickLeg no-path branch) is responsible
            // for firing the probe before tearing down the autowalk. We just consume Last here.
            RuntimeBlockerProbe probe = RuntimeBlockerProbe.Last;
            RuntimeBlockerProbe.Hit chest = probe?.Chest;
            RuntimeBlockerProbe.Hit ankle = probe?.Ankle;
            RuntimeBlockerProbe.Hit nearest = probe?.Nearest();
            Vector3 waypoint = probe != null ? probe.Waypoint : Vector3.zero;
            RuntimeBlockerProbe.Last = null;

            string playerFloor = FloorForY(playerPos.y);
            ImpassRecord rec = new ImpassRecord
            {
                leg_index = _walkLegIndex,
                target_floor = floor,
                target_ix = ix,
                target_iz = iz,
                target_wx = targetPos.x,
                target_wz = targetPos.z,
                player_floor = playerFloor,
                player_wx = playerPos.x,
                player_wy = playerPos.y,
                player_wz = playerPos.z,
                outcome = outcome,
                waypoint_wx = waypoint.x,
                waypoint_wz = waypoint.z,
                blocker_mode = nearest != null ? ClassifyBlockerMode(nearest) : "unknown",
                chest_path = chest?.Path,
                chest_layer = chest != null ? chest.Layer : -1,
                chest_distance = chest != null ? chest.Distance : 0f,
                ankle_path = ankle?.Path,
                ankle_layer = ankle != null ? ankle.Layer : -1,
                ankle_distance = ankle != null ? ankle.Distance : 0f,
                active_door_name = activeDoorName,
                segment_door_name = segmentDoorName,
                elapsed_s = Time.unscaledTime - _routeStartUnscaledTime,
                distance_to_waypoint_m = probe != null ? probe.DistanceToWaypointM : -1f,
                recent_displacement_m = probe != null ? probe.RecentDisplacementM : -1f,
                seconds_since_progress = probe != null ? probe.SecondsSinceProgress : -1f,
                down_path = probe?.Down?.Path,
                down_layer = probe?.Down != null ? probe.Down.Layer : -1,
                down_distance = probe != null ? probe.DownDistanceM : -1f,
                left_path = probe?.Left?.Path,
                left_distance = probe?.Left != null ? probe.Left.Distance : -1f,
                right_path = probe?.Right?.Path,
                right_distance = probe?.Right != null ? probe.Right.Distance : -1f,
                back_path = probe?.Back?.Path,
                back_distance = probe?.Back != null ? probe.Back.Distance : -1f,
                all_horizontal_clear = probe != null && probe.AllHorizontalClear(),
            };
            _impassRecords.Add(rec);
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavCoverageSweep walk-leg " + _walkLegIndex +
                    " IMPASS target=(" + floor + "," + ix + "," + iz + ")" +
                    " outcome=" + outcome +
                    " mode=" + (rec.blocker_mode ?? "?") +
                    " chest=" + (rec.chest_path ?? "<clear>") +
                    " ankle=" + (rec.ankle_path ?? "<clear>"));
        }

        // Planner failures (no_path, input_failed) — the player never moved, no spherecast
        // diagnostics are meaningful. Mark the cell impassable so it isn't retried; emit a
        // skeletal record on a separate channel from runtime stalls.
        private static int _walkPlannerFailureCount;
        private static void MarkPlannerFailure(string floor, int ix, int iz, Vector3 playerPos, string reason)
        {
            _walkLegIndex++;
            _walkPlannerFailureCount++;
            if (_manifest.floor_frames.TryGetValue(floor, out FloorFrame frame) &&
                _walkState.TryGetValue(floor, out byte[] state) &&
                ix >= 0 && ix < frame.nx && iz >= 0 && iz < frame.nz)
            {
                state[ix * frame.nz + iz] = CellImpassable;
            }
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavCoverageSweep walk-leg " + _walkLegIndex +
                    " PLANNER_FAIL target=(" + floor + "," + ix + "," + iz + ") reason=" + reason);
            _phase = Phase.WalkPickLeg;
            _nextActionTime = 0f;
        }

        private static bool PickNearestUntested(Vector3 playerPos, out string floor, out int ix, out int iz)
        {
            // Spiral search on the player's current floor first; fall back to scanning every
            // floor for the absolute nearest if the current floor has nothing left.
            string preferred = FloorForY(playerPos.y) ?? "ground";
            if (TryPickNearestOnFloor(preferred, playerPos, out ix, out iz))
            {
                floor = preferred;
                return true;
            }
            // Try the other floors.
            foreach (var kv in _walkReachable)
            {
                if (string.Equals(kv.Key, preferred, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryPickNearestOnFloor(kv.Key, playerPos, out ix, out iz))
                {
                    floor = kv.Key;
                    return true;
                }
            }
            floor = null; ix = 0; iz = 0;
            return false;
        }

        private static bool TryPickNearestOnFloor(string floor, Vector3 playerPos, out int ix, out int iz)
        {
            ix = 0; iz = 0;
            if (!_manifest.floor_frames.TryGetValue(floor, out FloorFrame frame)) return false;
            if (!_walkReachable.TryGetValue(floor, out bool[] reachable)) return false;
            if (!_walkState.TryGetValue(floor, out byte[] state)) return false;
            int pIx = (int)Mathf.Floor((playerPos.x - frame.origin_x) / frame.cell_size);
            int pIz = (int)Mathf.Floor((playerPos.z - frame.origin_z) / frame.cell_size);
            // Skip cells inside the bridge's WorldTargetArrivalRadius (0.45m). The autowalk
            // would immediately report "arrived" without the player having moved, producing
            // zero-distance legs and locking the sweep into a tight oscillation.
            // 0.45m / cell_size + slack → 3 cells at cell_size=0.2m.
            int minDistSq = 3 * 3;
            // Brute-force linear scan — nx*nz is ~100k per floor and we run this once per leg.
            // A real spiral would be ~10x faster but ~50x more code; revisit if profiling demands.
            int bestD2 = int.MaxValue;
            int bestIx = -1, bestIz = -1;
            for (int x = 0; x < frame.nx; x++)
            {
                int rowBase = x * frame.nz;
                for (int z = 0; z < frame.nz; z++)
                {
                    int k = rowBase + z;
                    if (!reachable[k]) continue;
                    if (state[k] != CellUntested) continue;
                    int dx = x - pIx, dz = z - pIz;
                    int d2 = dx * dx + dz * dz;
                    if (d2 < minDistSq) continue;
                    if (d2 < bestD2) { bestD2 = d2; bestIx = x; bestIz = z; }
                }
            }
            if (bestIx < 0) return false;
            ix = bestIx; iz = bestIz;
            return true;
        }

        private static bool TeleportToWalkStart()
        {
            if (BetterPlayerControl.Instance == null) return false;
            ManifestStart s = _manifest.start;
            if (s == null) return false;
            Vector3 startWorld = new Vector3(s.wx, s.floor_y, s.wz);
            Transform pt = BetterPlayerControl.Instance.transform;
            pt.position = startWorld;
            Rigidbody rb = BetterPlayerControl.Instance.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            return true;
        }

        private static void FinishWalkSweep()
        {
            FlushWalkResults();
            int walked = 0, impass = 0, untested = 0;
            foreach (var kv in _walkState)
            {
                byte[] s = kv.Value;
                bool[] r = _walkReachable[kv.Key];
                for (int i = 0; i < s.Length; i++)
                {
                    if (!r[i]) continue;
                    if (s[i] == CellWalkable) walked++;
                    else if (s[i] == CellImpassable) impass++;
                    else untested++;
                }
            }
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavCoverageSweep walk done legs=" + _walkLegIndex +
                    " walkable=" + walked + " impassable=" + impass + " untested=" + untested +
                    " planner_failures=" + _walkPlannerFailureCount);
            ScreenReader.Say("Walk-sweep complete: " + walked + " walkable, " + impass + " blocked", remember: false);
            SetPhoneBlockedForSweep(false);
            _phase = Phase.Idle;
            _manifest = null;
            _impassRecords = null;
            _walkState.Clear();
            _walkReachable.Clear();
            _walkStartTeleported = false;
        }

        private static void FlushWalkResults()
        {
            if (_impassRecords == null || _runDir == null) return;
            try
            {
                string path = Path.Combine(_runDir, "walk_results.json");
                var ci = CultureInfo.InvariantCulture;
                using (var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    sw.Write("{\"run_id\":\"");
                    sw.Write(JsonEscape(_manifest?.run_id ?? "default"));
                    sw.Write("\",\"legs\":");
                    sw.Write(_walkLegIndex);
                    sw.Write(",\"impassable_count\":");
                    sw.Write(_impassRecords.Count);
                    sw.Write(",\"impassable\":[");
                    for (int i = 0; i < _impassRecords.Count; i++)
                    {
                        var r = _impassRecords[i];
                        if (i > 0) sw.Write(",");
                        sw.Write("{\"leg\":"); sw.Write(r.leg_index);
                        sw.Write(",\"target_floor\":\""); sw.Write(JsonEscape(r.target_floor ?? ""));
                        sw.Write("\",\"target_cell\":["); sw.Write(r.target_ix); sw.Write(","); sw.Write(r.target_iz); sw.Write("]");
                        sw.Write(",\"target_world\":["); sw.Write(r.target_wx.ToString("0.000", ci)); sw.Write(","); sw.Write(r.target_wz.ToString("0.000", ci)); sw.Write("]");
                        sw.Write(",\"player_floor\":\""); sw.Write(JsonEscape(r.player_floor ?? ""));
                        sw.Write("\",\"player_world\":["); sw.Write(r.player_wx.ToString("0.000", ci)); sw.Write(","); sw.Write(r.player_wy.ToString("0.000", ci)); sw.Write(","); sw.Write(r.player_wz.ToString("0.000", ci)); sw.Write("]");
                        sw.Write(",\"outcome\":\""); sw.Write(JsonEscape(r.outcome ?? ""));
                        sw.Write("\",\"waypoint\":["); sw.Write(r.waypoint_wx.ToString("0.000", ci)); sw.Write(","); sw.Write(r.waypoint_wz.ToString("0.000", ci)); sw.Write("]");
                        sw.Write(",\"blocker_mode\":\""); sw.Write(JsonEscape(r.blocker_mode ?? "unknown"));
                        sw.Write("\",\"chest\":{\"path\":\""); sw.Write(JsonEscape(r.chest_path ?? ""));
                        sw.Write("\",\"layer\":"); sw.Write(r.chest_layer);
                        sw.Write(",\"distance\":"); sw.Write(r.chest_distance.ToString("0.000", ci));
                        sw.Write("},\"ankle\":{\"path\":\""); sw.Write(JsonEscape(r.ankle_path ?? ""));
                        sw.Write("\",\"layer\":"); sw.Write(r.ankle_layer);
                        sw.Write(",\"distance\":"); sw.Write(r.ankle_distance.ToString("0.000", ci));
                        sw.Write("},\"active_door\":\""); sw.Write(JsonEscape(r.active_door_name ?? ""));
                        sw.Write("\",\"segment_door\":\""); sw.Write(JsonEscape(r.segment_door_name ?? ""));
                        sw.Write("\",\"elapsed_s\":"); sw.Write(r.elapsed_s.ToString("0.000", ci));
                        sw.Write(",\"distance_to_waypoint\":"); sw.Write(r.distance_to_waypoint_m.ToString("0.000", ci));
                        sw.Write(",\"recent_displacement\":"); sw.Write(r.recent_displacement_m.ToString("0.000", ci));
                        sw.Write(",\"seconds_since_progress\":"); sw.Write(r.seconds_since_progress.ToString("0.000", ci));
                        sw.Write(",\"all_horizontal_clear\":"); sw.Write(r.all_horizontal_clear ? "true" : "false");
                        sw.Write(",\"down\":{\"path\":\""); sw.Write(JsonEscape(r.down_path ?? ""));
                        sw.Write("\",\"layer\":"); sw.Write(r.down_layer);
                        sw.Write(",\"distance\":"); sw.Write(r.down_distance.ToString("0.000", ci));
                        sw.Write("},\"left\":{\"path\":\""); sw.Write(JsonEscape(r.left_path ?? ""));
                        sw.Write("\",\"distance\":"); sw.Write(r.left_distance.ToString("0.000", ci));
                        sw.Write("},\"right\":{\"path\":\""); sw.Write(JsonEscape(r.right_path ?? ""));
                        sw.Write("\",\"distance\":"); sw.Write(r.right_distance.ToString("0.000", ci));
                        sw.Write("},\"back\":{\"path\":\""); sw.Write(JsonEscape(r.back_path ?? ""));
                        sw.Write("\",\"distance\":"); sw.Write(r.back_distance.ToString("0.000", ci));
                        sw.Write("}}");
                    }
                    sw.Write("],\"groups\":[");
                    WriteWalkGroups(sw, ci);
                    sw.Write("]}");
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavCoverageSweep FlushWalkResults threw: " + ex.Message);
            }
        }

        // Bucket impass records by (floor, nearest-collider). The nearest collider is the closest
        // non-empty probe across all six directions (chest/ankle/left/right/back/down) — a player
        // wedged sideways against a wall keys to that wall, not "unknown", so one bake-side bug
        // shows up as one bucket regardless of which way the capsule was pinned. Each bucket
        // reports cell count, (ix,iz) bbox, mode, the nearest collider's layer, min distance seen,
        // and the first leg index for cross-referencing the raw impassable[] list.
        private static void WriteWalkGroups(StreamWriter sw, CultureInfo ci)
        {
            if (_impassRecords == null || _impassRecords.Count == 0) return;
            var buckets = new Dictionary<string, WalkGroup>(StringComparer.Ordinal);
            for (int i = 0; i < _impassRecords.Count; i++)
            {
                var r = _impassRecords[i];
                NearestProbe(r, out string nearPath, out int nearLayer, out float nearDist, out string nearDir);
                string key = (r.target_floor ?? "") + "|" + (nearPath ?? "");
                if (!buckets.TryGetValue(key, out WalkGroup g))
                {
                    g = new WalkGroup
                    {
                        Floor = r.target_floor,
                        BlockerPath = nearPath,
                        BlockerLayer = nearLayer,
                        BlockerDir = nearDir,
                        Mode = r.blocker_mode,
                        FirstLeg = r.leg_index,
                        MinIx = r.target_ix, MaxIx = r.target_ix,
                        MinIz = r.target_iz, MaxIz = r.target_iz,
                        MinDist = nearDist,
                    };
                    buckets[key] = g;
                }
                g.Count++;
                if (r.target_ix < g.MinIx) g.MinIx = r.target_ix;
                if (r.target_ix > g.MaxIx) g.MaxIx = r.target_ix;
                if (r.target_iz < g.MinIz) g.MinIz = r.target_iz;
                if (r.target_iz > g.MaxIz) g.MaxIz = r.target_iz;
                if (nearDist >= 0f && (g.MinDist < 0f || nearDist < g.MinDist)) g.MinDist = nearDist;
            }
            // Sort by count desc so the worst offender is on top.
            var list = new List<WalkGroup>(buckets.Values);
            list.Sort((a, b) => b.Count.CompareTo(a.Count));
            for (int i = 0; i < list.Count; i++)
            {
                WalkGroup g = list[i];
                if (i > 0) sw.Write(",");
                sw.Write("{\"floor\":\""); sw.Write(JsonEscape(g.Floor ?? ""));
                sw.Write("\",\"blocker_path\":\""); sw.Write(JsonEscape(g.BlockerPath ?? ""));
                sw.Write("\",\"blocker_layer\":"); sw.Write(g.BlockerLayer);
                sw.Write(",\"blocker_dir\":\""); sw.Write(JsonEscape(g.BlockerDir ?? ""));
                sw.Write("\",\"mode\":\""); sw.Write(JsonEscape(g.Mode ?? "unknown"));
                sw.Write("\",\"count\":"); sw.Write(g.Count);
                sw.Write(",\"bbox\":["); sw.Write(g.MinIx); sw.Write(","); sw.Write(g.MinIz);
                sw.Write(","); sw.Write(g.MaxIx); sw.Write(","); sw.Write(g.MaxIz); sw.Write("]");
                sw.Write(",\"min_distance\":"); sw.Write(g.MinDist.ToString("0.000", ci));
                sw.Write(",\"first_leg\":"); sw.Write(g.FirstLeg);
                sw.Write("}");
            }
        }

        // Pick the closest non-empty probe across all six directions. Distances of -1 (no hit)
        // are ignored. Returns empty path when every probe missed (a genuine "nothing around"
        // stall — autowalk stopped feeding input, or the player arrived but the check disagreed).
        private static void NearestProbe(in ImpassRecord r,
            out string path, out int layer, out float dist, out string dir)
        {
            // Horizontal probes only — the down probe always finds the floor and would define
            // a bogus "floor mesh" blocker group for every record. Down stays diagnostic-only
            // (kept in the raw impassable[] record for fall/wrong-floor analysis).
            path = ""; layer = -1; dist = -1f; dir = "";
            string[] paths = { r.chest_path, r.ankle_path, r.left_path, r.right_path, r.back_path };
            int[] layers = { r.chest_layer, r.ankle_layer, -1, -1, -1 };
            float[] dists = { r.chest_distance, r.ankle_distance, r.left_distance, r.right_distance, r.back_distance };
            string[] dirs = { "chest", "ankle", "left", "right", "back" };
            for (int i = 0; i < paths.Length; i++)
            {
                if (string.IsNullOrEmpty(paths[i]) || dists[i] < 0f) continue;
                if (dist < 0f || dists[i] < dist)
                {
                    path = paths[i]; layer = layers[i]; dist = dists[i]; dir = dirs[i];
                }
            }
        }

        private sealed class WalkGroup
        {
            public string Floor;
            public string BlockerPath;
            public int BlockerLayer;
            public string BlockerDir;
            public string Mode;
            public int Count;
            public int FirstLeg;
            public int MinIx, MaxIx, MinIz, MaxIz;
            public float MinDist;
        }

        private static void FlushResults()
        {
            if (_results == null || _runDir == null) return;
            try
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder(_results.Count * 96 + 64);
                using (var sw = new StringWriter(sb, ci))
                {
                    sw.Write("{\"run_id\":\"");
                    sw.Write(_manifest?.run_id ?? "default");
                    sw.Write("\",\"completed_count\":");
                    sw.Write(_results.Count);
                    sw.Write(",\"results\":[");
                    for (int i = 0; i < _results.Count; i++)
                    {
                        var r = _results[i];
                        if (i > 0) sw.Write(",");
                        sw.Write("{\"i\":"); sw.Write(r.manifest_index);
                        sw.Write(",\"floor\":\""); sw.Write(r.floor ?? "");
                        sw.Write("\",\"cell\":["); sw.Write(r.cell != null && r.cell.Length > 0 ? r.cell[0] : 0);
                        sw.Write(","); sw.Write(r.cell != null && r.cell.Length > 1 ? r.cell[1] : 0);
                        sw.Write("]");
                        if (!string.IsNullOrEmpty(r.name))
                        {
                            sw.Write(",\"name\":\""); sw.Write(JsonEscape(r.name)); sw.Write("\"");
                        }
                        sw.Write(",\"outcome\":\""); sw.Write(JsonEscape(r.outcome ?? ""));
                        sw.Write("\",\"cost_m\":"); sw.Write(r.cost_m.ToString("0.000", ci));
                        sw.Write(",\"displacement_m\":"); sw.Write(r.displacement_m.ToString("0.000", ci));
                        sw.Write(",\"elapsed_s\":"); sw.Write(r.elapsed_s.ToString("0.000", ci));
                        if (!string.IsNullOrEmpty(r.stall_class))
                        {
                            sw.Write(",\"stall_class\":\""); sw.Write(JsonEscape(r.stall_class)); sw.Write("\"");
                        }
                        if (!string.IsNullOrEmpty(r.blocker_path))
                        {
                            sw.Write(",\"blocker\":{\"path\":\""); sw.Write(JsonEscape(r.blocker_path));
                            sw.Write("\",\"layer\":"); sw.Write(r.blocker_layer);
                            sw.Write(",\"distance\":"); sw.Write(r.blocker_distance.ToString("0.000", ci));
                            sw.Write(",\"mode\":\""); sw.Write(JsonEscape(r.blocker_mode ?? "unknown"));
                            sw.Write("\"}");
                        }
                        sw.Write("}");
                    }
                    sw.Write("]}");
                }
                string json = sb.ToString();
                // Canonical name (latest run) for tooling that reads a stable path...
                File.WriteAllText(Path.Combine(_runDir, "sweep_results.json"), json, System.Text.Encoding.UTF8);
                // ...plus a per-run copy a later run/relaunch can't clobber.
                if (!string.IsNullOrEmpty(_runStamp))
                    File.WriteAllText(Path.Combine(_runDir, "sweep_results." + _runStamp + ".json"),
                        json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavCoverageSweep FlushResults threw: " + ex.Message);
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // Write a compact verified bitmap per floor: ASCII PPM-like file with one row of
        // '.' / 'V' chars per x-row. The Python coverage-map emitter overlays these onto
        // the bake's navigable PPM to show what the player physically traversed.
        private static void WriteVerifiedBitmaps()
        {
            if (_manifest?.floor_frames == null || _runDir == null) return;
            try
            {
                foreach (var kv in _manifest.floor_frames)
                {
                    string label = kv.Key;
                    FloorFrame f = kv.Value;
                    if (!_verified.TryGetValue(label, out bool[] bitmap)) continue;
                    string outPath = Path.Combine(_runDir, "verified." + label + ".txt");
                    using (var sw = new StreamWriter(outPath, false, System.Text.Encoding.ASCII))
                    {
                        sw.Write("# floor="); sw.Write(label);
                        sw.Write(" nx="); sw.Write(f.nx);
                        sw.Write(" nz="); sw.Write(f.nz);
                        sw.Write(" origin_x="); sw.Write(f.origin_x.ToString("0.0000", CultureInfo.InvariantCulture));
                        sw.Write(" origin_z="); sw.Write(f.origin_z.ToString("0.0000", CultureInfo.InvariantCulture));
                        sw.Write(" cell_size="); sw.Write(f.cell_size.ToString("0.0000", CultureInfo.InvariantCulture));
                        sw.WriteLine();
                        char[] buf = new char[f.nz];
                        for (int ix = 0; ix < f.nx; ix++)
                        {
                            int rowBase = ix * f.nz;
                            for (int iz = 0; iz < f.nz; iz++) buf[iz] = bitmap[rowBase + iz] ? 'V' : '.';
                            sw.Write(buf);
                            sw.WriteLine();
                        }
                    }
                    // Per-run copy alongside the canonical name, so a later run can't clobber it
                    // before the overlay/review step (mirrors sweep_results.<stamp>.json).
                    if (!string.IsNullOrEmpty(_runStamp))
                        File.Copy(outPath, Path.Combine(_runDir, "verified." + label + "." + _runStamp + ".txt"), true);
                }
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogWarning("SimpleNavCoverageSweep WriteVerifiedBitmaps threw: " + ex.Message);
            }
        }

        // ---- Manifest DTOs ----------------------------------------------------------------

#pragma warning disable CS0649
        [DataContract]
        private class SweepManifestDoc
        {
            [DataMember] public string run_id;
            [DataMember] public string mode;             // "walk" or null/"dispersed"
            [DataMember] public ManifestFloorFrames floor_frames;
            [DataMember] public ManifestEntry[] entries;
            [DataMember] public ManifestStart start;
            [DataMember] public WalkBitmaps reachable_bitmap_rows;
            // Exterior decor (fence/tree/drone) the planner resolved off every floor band.
            // These are NOT drive targets — the planner excludes them from `entries` — but it
            // records how many it dropped so the completion summary can still report them.
            [DataMember] public ExteriorExclusion[] excluded_exterior;
        }

        [DataContract]
        private class ExteriorExclusion
        {
            [DataMember] public string name;
            [DataMember] public float[] world_xz;
            [DataMember] public string unreachable_reason;
        }

        [DataContract]
        private class ManifestStart
        {
            [DataMember] public string floor;
            [DataMember] public int[] cell;
            [DataMember] public float wx;
            [DataMember] public float wz;
            [DataMember] public float floor_y;
        }

        [DataContract]
        private class WalkBitmaps
        {
            // Each floor's bitmap is a string[]: rows[ix][iz] == '1' iff cell is reachable.
            [DataMember] public string[] ground;
            [DataMember] public string[] upper;
            public string[] ForFloor(string label)
            {
                if (string.Equals(label, "ground", StringComparison.OrdinalIgnoreCase)) return ground;
                if (string.Equals(label, "upper", StringComparison.OrdinalIgnoreCase)) return upper;
                return null;
            }
        }

        // DataContractJsonSerializer's UseSimpleDictionaryAsObject reads a JSON object of
        // {floor: frame} via a typed inner class with explicit members. There are only two
        // floors in practice, so a closed-list shape is fine.
        [DataContract]
        private class ManifestFloorFrames
        {
            [DataMember] public FloorFrame ground;
            [DataMember] public FloorFrame upper;

            // Convenience: iteration shape the rest of the harness expects.
            public IEnumerable<KeyValuePair<string, FloorFrame>> AsPairs()
            {
                if (ground != null) yield return new KeyValuePair<string, FloorFrame>("ground", ground);
                if (upper != null) yield return new KeyValuePair<string, FloorFrame>("upper", upper);
            }

            public bool TryGetValue(string key, out FloorFrame frame)
            {
                if (string.Equals(key, "ground", StringComparison.OrdinalIgnoreCase)) { frame = ground; return frame != null; }
                if (string.Equals(key, "upper", StringComparison.OrdinalIgnoreCase)) { frame = upper; return frame != null; }
                frame = null; return false;
            }

            public IEnumerator<KeyValuePair<string, FloorFrame>> GetEnumerator() => AsPairs().GetEnumerator();
        }

        [DataContract]
        private class FloorFrame
        {
            [DataMember] public float origin_x;
            [DataMember] public float origin_z;
            [DataMember] public float cell_size;
            [DataMember] public int nx;
            [DataMember] public int nz;
            [DataMember] public float floor_y;
        }

        [DataContract]
        private class ManifestEntry
        {
            [DataMember] public string floor;
            [DataMember] public int[] cell;
            [DataMember] public string status;
            [DataMember] public string route;
            [DataMember] public float cost_m;
            // Object-mode only: the human-readable object name this stand-cell serves
            // (mode="objects"). Null/absent for cell-targeted (walk/dispersed) manifests.
            [DataMember] public string name;
            // Object-mode C#-replan inputs: the DEST object's stable scene id, true
            // transform position, and interaction radius. The sweep resolves the live
            // InteractableObj by these and re-plans with SimpleNavPlanner.Plan so it
            // validates the C# planner's routes (not the offline ones). from_* describe
            // the SOURCE object the leg teleports to before planning.
            [DataMember] public long object_id;
            // Stable scene id(s) (UniqueId == runtime InteractableObj.Id). Preferred exact bridge
            // to the live object; object_id is a SERIALIZED id that does NOT match GetInstanceID,
            // and names aren't unique, so the unique id resolves the right instance directly.
            [DataMember] public string unique_id;
            [DataMember] public string[] unique_ids;
            [DataMember] public float[] object_xyz;
            [DataMember] public float interaction_radius;
            [DataMember] public string from_floor;
            [DataMember] public int[] from_cell;
            [DataMember] public float[] from_world_xz;
            [DataMember] public string from_name;
        }
#pragma warning restore CS0649

        // ---- View shapes (internal, post-load) --------------------------------------------

        private class SweepManifest
        {
            public string run_id;
            public string mode;
            public ManifestFloorFrames floor_frames;
            public ManifestEntry[] entries;
            public ManifestStart start;
            public WalkBitmaps reachable_bitmap_rows;
            // Count of exterior decor the planner dropped from `entries` (see DTO). Reported in
            // the completion summary so the off-floor total stays visible without driving them.
            public int excluded_exterior_count;
        }

        private struct RouteResult
        {
            public int manifest_index;
            public string floor;
            public int[] cell;
            public string outcome;
            public float cost_m;
            public float elapsed_s;
            // XZ distance the player ACTUALLY moved from the route's first waypoint to where it
            // stopped (computed at FinishLeg). cost_m is the PLANNED route length; this is travel.
            // The two together split a stall: displacement_m≈0 = froze at the start (the watchdog
            // is right to fire, a real follower defect); displacement_m≈cost_m = walked the route
            // then stalled on final approach (the watchdog is too aggressive). Without this the
            // stall classes are indistinguishable from the JSON alone.
            public float displacement_m;
            // Object-mode: the object name this entry targets, copied from the manifest
            // entry so sweep_results.json names the object, not just a cell. Null otherwise.
            public string name;
            // Populated only for non-arrival outcomes when ProbeRuntimeBlocker found a collider.
            // Mode is one of "footprint" | "state" | "classification" | "unknown" — a coarse
            // triage hint, not a final verdict. See RuntimeBlockerProbe + the offline triage tool.
            public string blocker_path;
            public int blocker_layer;
            public float blocker_distance;
            public string blocker_mode;
            // Navmesh state at the player's ACTUAL stuck position (SimpleNavPlanner.ClassifyStallCell):
            // dead_pocket / off_mesh_open / open_space / edge_graze / on_mesh / unknown. The reliable
            // signal for triaging a stall, since blocker_mode/path is just the nearest collider (a
            // big-AABB mesh or floor edge mislabels the cause). See
            // [[project-navigation-footprint-stalls-decomposed-2026-06-16]].
            public string stall_class;
        }

        // One per walk-mode impassable verdict. Fields are filled to be self-contained — the
        // offline triage tool reads these JSON-side without consulting any other artifact.
        private struct ImpassRecord
        {
            public int leg_index;
            public string target_floor;
            public int target_ix;
            public int target_iz;
            public float target_wx;
            public float target_wz;
            // Where the player actually was when we declared the leg impassable.
            public string player_floor;
            public float player_wx;
            public float player_wy;
            public float player_wz;
            // Why the leg failed. One of: "no_path" (planner refused), "stalled", "looped",
            // "door_failed", "budget", "input_failed", "exception".
            public string outcome;
            // The waypoint the player was trying to reach when blocked.
            public float waypoint_wx;
            public float waypoint_wz;
            // Triage hint: footprint / state / classification / unknown.
            public string blocker_mode;
            // Chest- and ankle-height probes are reported separately because the underlying
            // collider may sit at one height only (doorframe sill, low planter, etc.).
            public string chest_path;
            public int chest_layer;
            public float chest_distance;
            public string ankle_path;
            public int ankle_layer;
            public float ankle_distance;
            // The Door object the autowalk was opening, if any, when the leg failed.
            public string active_door_name;
            // Active route's tagged door for the segment we were on.
            public string segment_door_name;
            public float elapsed_s;
            // Diagnostic context — filled by the probe even when no forward collider is found,
            // so "unknown" stalls (player stuck without an obstacle in front) become diagnosable.
            public float distance_to_waypoint_m;
            public float recent_displacement_m;
            public float seconds_since_progress;
            public string down_path;
            public int down_layer;
            public float down_distance;
            public string left_path;
            public float left_distance;
            public string right_path;
            public float right_distance;
            public string back_path;
            public float back_distance;
            public bool all_horizontal_clear;
        }

        private static SweepManifest LoadManifest(string path)
        {
            try
            {
                SweepManifestDoc doc;
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SweepManifestDoc));
                    doc = serializer.ReadObject(stream) as SweepManifestDoc;
                }
                if (doc == null) return null;
                return new SweepManifest
                {
                    run_id = doc.run_id,
                    mode = doc.mode,
                    floor_frames = doc.floor_frames,
                    entries = doc.entries ?? Array.Empty<ManifestEntry>(),
                    start = doc.start,
                    reachable_bitmap_rows = doc.reachable_bitmap_rows,
                    excluded_exterior_count = doc.excluded_exterior?.Length ?? 0,
                };
            }
            catch (Exception ex)
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavCoverageSweep manifest parse threw: " + ex);
                return null;
            }
        }
    }
}
