using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using UnityEngine;

namespace DateEverythingAccess
{
    // Step O6 of the object-first navigation plan: in-game coverage sweep harness.
    //
    // Consumes the sweep manifest produced by scripts/sweep_coverage_planner.py. The manifest's
    // entries are pre-sorted longest-route-first; the harness teleports the player to the start,
    // installs each route via SimpleNavBridge.BeginRoute, and watches the autowalk play out.
    // During each run it stamps the player's cell + 4-neighbour ring into a per-floor
    // verified-reachable bitmap. Future routes whose target cell is already verified are
    // skipped — long routes that cross the house pay for themselves immediately, and the sweep
    // self-prunes as coverage accumulates.
    //
    // Toggle hotkey: Ctrl+Alt+Shift+F8 (wired in Main.cs).
    //
    // Results emit to artifacts/navigation/sweep/<run-id>/sweep_results.json. Failure modes
    // are flat (the plan's contract): arrived, no_path (offline), skipped_already_covered,
    // stalled (autowalk gave up), looped (player circled a small area), door_failed, budget,
    // input_failed, exception. No transition taxonomy.
    internal static class SimpleNavCoverageSweep
    {
        // Sweep artifacts live in the project source tree, not in BepInEx/plugins, because the
        // route catalogue is many thousands of files (~100 MB) we don't want to duplicate on
        // every build. The harness reads them directly from the source path. If the project
        // moves, override this via the COVERAGE_SWEEP_DIR env var.
        private const string DefaultSweepSourceDir = @"C:\Users\amock\mod template\artifacts\navigation\sweep";
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

        private enum Phase
        {
            Idle,
            LoadingManifest,
            BetweenRoutes,
            Teleporting,
            Running,
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
        // every cell within this many cells of the player counts as walkable too. Same shape as
        // the StampCoverage 4-neighbour ring; bumped to 8-neighbour for walk mode.
        private const int WalkVerifyRadiusCells = 1;

        private static Phase _phase = Phase.Idle;
        private static SweepManifest _manifest;
        private static int _entryIndex;
        private static string _runDir;
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

        // Per-floor verified-reachable bitmap. cells[ix * nz + iz] = true once any traversal
        // has put the player's cell-ring on that cell. Allocated lazily per floor.
        private static readonly Dictionary<string, bool[]> _verified =
            new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);

        // Per-floor failed-cell bitmap. cells[ix * nz + iz] = true once a route failed with
        // the player at that cell. Used to pre-emptively skip future routes whose target sits
        // near a known failure. Symmetric to _verified.
        private static readonly Dictionary<string, bool[]> _failed =
            new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);

        // Soft cap on skipping near a failure. Key is "floor:ix:iz". When the count hits
        // FailedSkipSoftCap, the next route near that cell runs anyway (so a transient
        // false-positive doesn't permanently ban an area). The counter then resets.
        private static readonly Dictionary<string, int> _failedSkipCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private const int FailedSkipSoftCap = 5;
        // Routes whose target cell lies within this radius (in cells) of a failure cell
        // are eligible for pre-emptive skip. 5 cells × 0.2m = 1m: roughly the width of
        // a doorway, so a single wall failure poisons its immediate neighbourhood.
        private const int FailedNeighborhoodCells = 5;

        public static bool IsActive => _phase != Phase.Idle;

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
                    case Phase.Teleporting:           StepTeleporting(); break;
                    case Phase.Running:               StepRunning(); break;
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
            string runId = "default";
            string sweepBase = Environment.GetEnvironmentVariable("COVERAGE_SWEEP_DIR");
            if (string.IsNullOrEmpty(sweepBase) || !Directory.Exists(sweepBase))
                sweepBase = DefaultSweepSourceDir;
            _runDir = Path.Combine(sweepBase, runId);
            string manifestPath = Path.Combine(_runDir, "sweep_manifest.json");
            if (!File.Exists(manifestPath))
            {
                if (Main.Log != null) Main.Log.LogError("SimpleNavCoverageSweep: manifest missing at " + manifestPath);
                ScreenReader.Say("Coverage sweep manifest not found", remember: false);
                return;
            }

            _manifest = LoadManifest(manifestPath);
            if (_manifest == null || _manifest.entries == null || _manifest.entries.Length == 0)
            {
                ScreenReader.Say("Coverage sweep manifest empty or unreadable", remember: false);
                return;
            }

            // Allocate verified / failed bitmaps per floor.
            _verified.Clear();
            _failed.Clear();
            _failedSkipCounts.Clear();
            if (_manifest.floor_frames != null)
            {
                foreach (var kv in _manifest.floor_frames)
                {
                    int cells = kv.Value.nx * kv.Value.nz;
                    _verified[kv.Key] = new bool[cells];
                    _failed[kv.Key] = new bool[cells];
                }
            }

            _results = new List<RouteResult>(_manifest.entries.Length);
            _entryIndex = 0;

            bool walkMode = string.Equals(_manifest.mode, "walk", StringComparison.OrdinalIgnoreCase);
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
            if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep: started, entries=" + _manifest.entries.Length);
            ScreenReader.Say("Coverage sweep started, " + _manifest.entries.Length + " routes", remember: false);
        }

        private static void InitWalkMode()
        {
            _walkState.Clear();
            _walkReachable.Clear();
            _impassRecords = new List<ImpassRecord>(64);
            _walkLegIndex = 0;
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
            _phase = Phase.Idle;
            _manifest = null;
            _currentRoute = null;
            _results = null;
            _verified.Clear();
            _failed.Clear();
            _failedSkipCounts.Clear();
            _walkState.Clear();
            _walkReachable.Clear();
            _impassRecords = null;
            _walkStartTeleported = false;
            ScreenReader.Say("Coverage sweep stopped", remember: false);
        }

        // ---- Phase: BetweenRoutes ---------------------------------------------------------
        // Pick the next manifest entry that isn't already covered, plan it, set up the run.

        private static void StepBetweenRoutes()
        {
            // Make sure any previous run is fully torn down.
            try { SimpleNavBridge.EndStep(); } catch { }

            while (_entryIndex < _manifest.entries.Length)
            {
                var entry = _manifest.entries[_entryIndex];
                if (entry == null)
                {
                    _entryIndex++;
                    continue;
                }

                // Offline-planner outcomes recorded as-is, then skip.
                if (!string.Equals(entry.status, "ok", StringComparison.Ordinal))
                {
                    _results.Add(new RouteResult
                    {
                        manifest_index = _entryIndex,
                        floor = entry.floor,
                        cell = entry.cell,
                        outcome = entry.status, // e.g. "no_path"
                    });
                    _entryIndex++;
                    continue;
                }

                // Target cell already covered by an earlier traversal — skip and credit pass.
                if (IsCellVerified(entry.floor, entry.cell))
                {
                    _results.Add(new RouteResult
                    {
                        manifest_index = _entryIndex,
                        floor = entry.floor,
                        cell = entry.cell,
                        outcome = "skipped_already_covered",
                    });
                    _entryIndex++;
                    continue;
                }

                // Target cell sits near a known failure. Skip pre-emptively unless this
                // failure cell has already been skipped past too many times — at which
                // point we run the route to confirm the failure isn't a transient.
                if (IsTargetNearFailure(entry.floor, entry.cell, out string failedKey))
                {
                    int count = _failedSkipCounts.TryGetValue(failedKey, out int c) ? c : 0;
                    if (count < FailedSkipSoftCap)
                    {
                        _failedSkipCounts[failedKey] = count + 1;
                        _results.Add(new RouteResult
                        {
                            manifest_index = _entryIndex,
                            floor = entry.floor,
                            cell = entry.cell,
                            outcome = "skipped_known_blocker:" + failedKey,
                        });
                        _entryIndex++;
                        continue;
                    }
                    // Soft cap reached — force a retry, reset the counter. If this route also
                    // fails, the cell gets re-stamped (or a new failure cell appears) and the
                    // soft cap protects the next FailedSkipSoftCap routes again.
                    _failedSkipCounts[failedKey] = 0;
                }

                // Load the route. If load fails, record and move on.
                string routePath = Path.Combine(_runDir, entry.route ?? "");
                SimpleNavRoute route = SimpleNavRoute.Load(routePath);
                if (route == null)
                {
                    _results.Add(new RouteResult
                    {
                        manifest_index = _entryIndex,
                        floor = entry.floor,
                        cell = entry.cell,
                        outcome = "load_failed",
                    });
                    _entryIndex++;
                    continue;
                }

                _currentRoute = route;
                _currentManifestIndex = _entryIndex;
                BeginCurrentRoute();
                return;
            }

            // Drained the manifest.
            FinishSweep();
        }

        private static void BeginCurrentRoute()
        {
            // Teleport to the manifest's start. The route's first waypoint is the start cell,
            // so we use that to avoid drift from earlier route's end positions.
            if (BetterPlayerControl.Instance == null)
            {
                RecordCurrentRouteAsException("no-player");
                AdvanceToNextEntry();
                return;
            }

            Vector3 startWorld = _currentRoute.Waypoints[0];
            Transform playerTransform = BetterPlayerControl.Instance.transform;
            playerTransform.position = startWorld;

            // Face the second waypoint so the first input doesn't waste a turn.
            if (_currentRoute.Waypoints.Count > 1)
            {
                Vector3 toNext = _currentRoute.Waypoints[1] - startWorld;
                toNext.y = 0f;
                if (toNext.sqrMagnitude > 0.0001f)
                    playerTransform.rotation = Quaternion.LookRotation(toNext.normalized, Vector3.up);
            }

            // Zero rigidbody so the player doesn't slide into the start.
            Rigidbody rb = BetterPlayerControl.Instance.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // Force-close all doors so an earlier run's open door doesn't make this run easier.
            ForceCloseAllDoors();

            _phase = Phase.Teleporting;
            _nextActionTime = Time.unscaledTime + WaitAfterTeleportSeconds;
        }

        private static void StepTeleporting()
        {
            if (Time.unscaledTime < _nextActionTime) return;

            // Hand the route to the bridge and start the route-driven autowalk.
            _routeStartUnscaledTime = Time.unscaledTime;
            _routeBudgetSeconds = ComputeBudgetSeconds(_currentRoute);
            _loopWindow.Clear();
            _nextLoopSampleTime = Time.unscaledTime + LoopSampleIntervalSeconds;
            _doorCloseObservedSince = 0f;
            if (!AccessibilityWatcher.TryStartCoverageSweepRoute(_currentRoute, out string detail))
            {
                FinishRoute("input_failed:" + detail);
                return;
            }

            _phase = Phase.Running;
        }

        // ---- Phase: Running ---------------------------------------------------------------
        // Watch the autowalk. Stamp player position into the verified bitmap every frame.
        // Decide outcome whenever one of the detectors fires.

        private static void StepRunning()
        {
            if (BetterPlayerControl.Instance == null)
            {
                FinishRoute("exception:no-player");
                return;
            }

            Vector3 playerPos = BetterPlayerControl.Instance.transform.position;

            // Stamp the player's cell + 4-neighbour ring into the verified bitmap on the
            // floor whose Y-band the player is currently in.
            StampCoverage(playerPos);

            // 1. Arrival vs stall: did the route succeed, or did the autowalk give up?
            // Both end with HasActiveRoute=false; disambiguate by checking proximity to target.
            // HasArrivedAtRouteTarget uses target XZ + clamped interaction radius, matching the
            // planner's goal-cell expansion.
            bool arrived = SimpleNavBridge.HasArrivedAtRouteTarget(playerPos);
            if (arrived)
            {
                FinishRoute("arrived");
                return;
            }
            if (!SimpleNavBridge.HasActiveRoute)
            {
                // Autowalk ended the step but we're not at the target → it gave up (stall).
                FinishRoute("stalled");
                return;
            }

            // 2. Door-open failure.
            if (SimpleNavBridge.ActiveDoor != null)
            {
                Door door = SimpleNavBridge.ActiveDoor;
                bool open = door.open;
                bool moving = SimpleNavBridge.IsActiveDoorMoving();
                if (!open && !moving)
                {
                    if (_doorCloseObservedSince <= 0f) _doorCloseObservedSince = Time.unscaledTime;
                    else if (Time.unscaledTime - _doorCloseObservedSince > DoorOpenTimeoutSeconds)
                    {
                        FinishRoute("door_failed:" + (door.gameObject != null ? door.gameObject.name : "<null>"));
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
                    FinishRoute("looped");
                    return;
                }
            }

            // 4. Budget ceiling — safety net.
            if (Time.unscaledTime - _routeStartUnscaledTime > _routeBudgetSeconds)
            {
                FinishRoute("budget");
                return;
            }

            // 5. Steering stall: the autowalk's own progress detector kicks in when the player
            // hasn't moved; it will call StopNavigationBlocked, which ends the route via the
            // HasActiveRoute=false branch above. Nothing extra to do here — the autowalk's
            // _lastAutoWalkProgressTime detector is the stall signal.
        }

        private static void FinishRoute(string outcome)
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
            };
            if (outcome != "arrived")
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
            }
            _results.Add(result);
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavCoverageSweep result idx=" + _currentManifestIndex +
                    " floor=" + entry.floor + " cell=(" + entry.cell[0] + "," + entry.cell[1] + ")" +
                    " outcome=" + outcome + " elapsed=" + (Time.unscaledTime - _routeStartUnscaledTime).ToString("0.0") +
                    " start=" + startPos.ToString("F2") + " end=" + endPos.ToString("F2") +
                    " moved=" + displacement.ToString("0.00") + "m");

            // On any non-success outcome, stamp the player's current cell into _failed so
            // future routes whose target lies near here can be skipped pre-emptively. We
            // treat "skipped_already_covered" and "arrived" as the only successes; everything
            // else (looped, stalled, door_failed, budget, exception:*) marks a failure cell.
            if (outcome != "arrived" && BetterPlayerControl.Instance != null)
            {
                StampFailureCell(BetterPlayerControl.Instance.transform.position);
            }

            try { AccessibilityWatcher.StopCoverageSweepRoute(); } catch { }
            AdvanceToNextEntry();
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
            });
        }

        private static void AdvanceToNextEntry()
        {
            _currentRoute = null;
            _entryIndex++;
            _phase = Phase.BetweenRoutes;
            // Periodically flush results so a crash doesn't lose hours of progress.
            if ((_results.Count % 50) == 0) FlushResults();
        }

        private static void FinishSweep()
        {
            FlushResults();
            WriteVerifiedBitmaps();
            int passed = 0, skipped = 0, failed = 0, noPath = 0;
            for (int i = 0; i < _results.Count; i++)
            {
                string o = _results[i].outcome ?? "";
                if (o == "arrived") passed++;
                else if (o == "skipped_already_covered") skipped++;
                else if (o == "no_path") noPath++;
                else failed++;
            }
            if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep done passed=" + passed +
                " skipped=" + skipped + " no_path=" + noPath + " failed=" + failed);
            ScreenReader.Say("Coverage sweep complete: " + passed + " passed, " + skipped +
                " skipped, " + failed + " failed", remember: false);
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

        private static bool IsCellVerified(string floorLabel, int[] cell)
        {
            if (cell == null || cell.Length < 2) return false;
            if (!_manifest.floor_frames.TryGetValue(floorLabel, out FloorFrame frame)) return false;
            if (!_verified.TryGetValue(floorLabel, out bool[] bitmap)) return false;
            int ix = cell[0], iz = cell[1];
            if (ix < 0 || ix >= frame.nx || iz < 0 || iz >= frame.nz) return false;
            return bitmap[ix * frame.nz + iz];
        }

        // Mark the cell containing worldPos as a known failure spot on its floor. Future routes
        // whose target sits within FailedNeighborhoodCells of this cell will be skipped, up to
        // the soft cap.
        private static void StampFailureCell(Vector3 worldPos)
        {
            string floorLabel = FloorForY(worldPos.y);
            if (floorLabel == null) return;
            if (!_manifest.floor_frames.TryGetValue(floorLabel, out FloorFrame frame)) return;
            if (!_failed.TryGetValue(floorLabel, out bool[] bitmap)) return;
            int ix = (int)Mathf.Floor((worldPos.x - frame.origin_x) / frame.cell_size);
            int iz = (int)Mathf.Floor((worldPos.z - frame.origin_z) / frame.cell_size);
            if (ix < 0 || ix >= frame.nx || iz < 0 || iz >= frame.nz) return;
            bitmap[ix * frame.nz + iz] = true;
            if (Main.Log != null)
                Main.Log.LogInfo("SimpleNavCoverageSweep failure-cell floor=" + floorLabel +
                    " cell=(" + ix + "," + iz + ")");
        }

        // True iff the target cell sits within FailedNeighborhoodCells of any cell marked
        // failed on the same floor. Scans a (2R+1)^2 window — fine at R=5 (121 cells).
        // Returns the key of the nearest failed cell via out param so the caller can charge
        // the right skip counter.
        private static bool IsTargetNearFailure(string floorLabel, int[] cell, out string failedKey)
        {
            failedKey = null;
            if (cell == null || cell.Length < 2) return false;
            if (!_manifest.floor_frames.TryGetValue(floorLabel, out FloorFrame frame)) return false;
            if (!_failed.TryGetValue(floorLabel, out bool[] bitmap)) return false;
            int tix = cell[0], tiz = cell[1];
            int r = FailedNeighborhoodCells;
            int bestD2 = int.MaxValue;
            int bestIx = -1, bestIz = -1;
            for (int dx = -r; dx <= r; dx++)
            {
                int ix = tix + dx;
                if (ix < 0 || ix >= frame.nx) continue;
                for (int dz = -r; dz <= r; dz++)
                {
                    int iz = tiz + dz;
                    if (iz < 0 || iz >= frame.nz) continue;
                    if (!bitmap[ix * frame.nz + iz]) continue;
                    int d2 = dx * dx + dz * dz;
                    if (d2 < bestD2) { bestD2 = d2; bestIx = ix; bestIz = iz; }
                }
            }
            if (bestIx < 0) return false;
            failedKey = floorLabel + ":" + bestIx + ":" + bestIz;
            return true;
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
                MarkImpassable(floor, ix, iz, playerPos, "no_path", targetPos, null);
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
                MarkImpassable(floor, ix, iz, playerPos, "input_failed:" + detail, targetPos, null);
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
                Door door = SimpleNavBridge.ActiveDoor;
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
            try { AccessibilityWatcher.StopCoverageSweepRoute(); } catch { }
            try { SimpleNavBridge.EndStep(); } catch { }
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
            // Pull probe data while it's fresh.
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
                    " walkable=" + walked + " impassable=" + impass + " untested=" + untested);
            ScreenReader.Say("Walk-sweep complete: " + walked + " walkable, " + impass + " blocked", remember: false);
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
                        sw.Write("}");
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

        // Bucket impass records by (floor, chest.path, ankle.path). One bucket = one bake-side
        // bug to chase. Each bucket reports: cell count, the (ix,iz) bounding box, the blocker
        // mode, layers, the smallest distances seen, and the leg index of the first occurrence
        // so you can cross-ref the raw impassable[] list.
        private static void WriteWalkGroups(StreamWriter sw, CultureInfo ci)
        {
            if (_impassRecords == null || _impassRecords.Count == 0) return;
            var buckets = new Dictionary<string, WalkGroup>(StringComparer.Ordinal);
            for (int i = 0; i < _impassRecords.Count; i++)
            {
                var r = _impassRecords[i];
                string key = (r.target_floor ?? "") + "|" + (r.chest_path ?? "") + "|" + (r.ankle_path ?? "");
                if (!buckets.TryGetValue(key, out WalkGroup g))
                {
                    g = new WalkGroup
                    {
                        Floor = r.target_floor,
                        ChestPath = r.chest_path,
                        AnklePath = r.ankle_path,
                        ChestLayer = r.chest_layer,
                        AnkleLayer = r.ankle_layer,
                        Mode = r.blocker_mode,
                        FirstLeg = r.leg_index,
                        MinIx = r.target_ix, MaxIx = r.target_ix,
                        MinIz = r.target_iz, MaxIz = r.target_iz,
                        MinChestDist = r.chest_distance,
                        MinAnkleDist = r.ankle_distance,
                    };
                    buckets[key] = g;
                }
                g.Count++;
                if (r.target_ix < g.MinIx) g.MinIx = r.target_ix;
                if (r.target_ix > g.MaxIx) g.MaxIx = r.target_ix;
                if (r.target_iz < g.MinIz) g.MinIz = r.target_iz;
                if (r.target_iz > g.MaxIz) g.MaxIz = r.target_iz;
                if (r.chest_distance < g.MinChestDist) g.MinChestDist = r.chest_distance;
                if (r.ankle_distance < g.MinAnkleDist) g.MinAnkleDist = r.ankle_distance;
            }
            // Sort by count desc so the worst offender is on top.
            var list = new List<WalkGroup>(buckets.Values);
            list.Sort((a, b) => b.Count.CompareTo(a.Count));
            for (int i = 0; i < list.Count; i++)
            {
                WalkGroup g = list[i];
                if (i > 0) sw.Write(",");
                sw.Write("{\"floor\":\""); sw.Write(JsonEscape(g.Floor ?? ""));
                sw.Write("\",\"chest_path\":\""); sw.Write(JsonEscape(g.ChestPath ?? ""));
                sw.Write("\",\"ankle_path\":\""); sw.Write(JsonEscape(g.AnklePath ?? ""));
                sw.Write("\",\"chest_layer\":"); sw.Write(g.ChestLayer);
                sw.Write(",\"ankle_layer\":"); sw.Write(g.AnkleLayer);
                sw.Write(",\"mode\":\""); sw.Write(JsonEscape(g.Mode ?? "unknown"));
                sw.Write("\",\"count\":"); sw.Write(g.Count);
                sw.Write(",\"bbox\":["); sw.Write(g.MinIx); sw.Write(","); sw.Write(g.MinIz);
                sw.Write(","); sw.Write(g.MaxIx); sw.Write(","); sw.Write(g.MaxIz); sw.Write("]");
                sw.Write(",\"min_chest_distance\":"); sw.Write(g.MinChestDist.ToString("0.000", ci));
                sw.Write(",\"min_ankle_distance\":"); sw.Write(g.MinAnkleDist.ToString("0.000", ci));
                sw.Write(",\"first_leg\":"); sw.Write(g.FirstLeg);
                sw.Write("}");
            }
        }

        private sealed class WalkGroup
        {
            public string Floor;
            public string ChestPath;
            public string AnklePath;
            public int ChestLayer;
            public int AnkleLayer;
            public string Mode;
            public int Count;
            public int FirstLeg;
            public int MinIx, MaxIx, MinIz, MaxIz;
            public float MinChestDist;
            public float MinAnkleDist;
        }

        private static void FlushResults()
        {
            if (_results == null || _runDir == null) return;
            try
            {
                string path = Path.Combine(_runDir, "sweep_results.json");
                var ci = CultureInfo.InvariantCulture;
                using (var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8))
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
                        sw.Write("],\"outcome\":\""); sw.Write(JsonEscape(r.outcome ?? ""));
                        sw.Write("\",\"cost_m\":"); sw.Write(r.cost_m.ToString("0.000", ci));
                        sw.Write(",\"elapsed_s\":"); sw.Write(r.elapsed_s.ToString("0.000", ci));
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
        }

        private struct RouteResult
        {
            public int manifest_index;
            public string floor;
            public int[] cell;
            public string outcome;
            public float cost_m;
            public float elapsed_s;
            // Populated only for non-arrival outcomes when ProbeRuntimeBlocker found a collider.
            // Mode is one of "footprint" | "state" | "classification" | "unknown" — a coarse
            // triage hint, not a final verdict. See RuntimeBlockerProbe + the offline triage tool.
            public string blocker_path;
            public int blocker_layer;
            public float blocker_distance;
            public string blocker_mode;
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
