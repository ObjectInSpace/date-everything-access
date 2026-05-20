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
        }

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
            _phase = Phase.BetweenRoutes;
            _nextActionTime = 0f;
            if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep: started, entries=" + _manifest.entries.Length);
            ScreenReader.Say("Coverage sweep started, " + _manifest.entries.Length + " routes", remember: false);
        }

        private static void AbortSweep(string reason)
        {
            if (Main.Log != null) Main.Log.LogInfo("SimpleNavCoverageSweep: abort reason=" + reason + " completed=" + (_results?.Count ?? 0));
            // If a route was in flight, stop the autowalk cleanly.
            try { SimpleNavBridge.EndStep(); } catch { }
            FlushResults();
            _phase = Phase.Idle;
            _manifest = null;
            _currentRoute = null;
            _results = null;
            _verified.Clear();
            _failed.Clear();
            _failedSkipCounts.Clear();
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

            // Hand the route to the bridge and start the autowalk.
            SimpleNavBridge.BeginRoute(_currentRoute);
            _routeStartUnscaledTime = Time.unscaledTime;
            _routeBudgetSeconds = ComputeBudgetSeconds(_currentRoute);
            _loopWindow.Clear();
            _nextLoopSampleTime = Time.unscaledTime + LoopSampleIntervalSeconds;
            _doorCloseObservedSince = 0f;
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
            _results.Add(new RouteResult
            {
                manifest_index = _currentManifestIndex,
                floor = entry.floor,
                cell = entry.cell,
                outcome = outcome,
                cost_m = entry.cost_m,
                elapsed_s = Time.unscaledTime - _routeStartUnscaledTime,
            });
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

            try { SimpleNavBridge.EndStep(); } catch { }
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
            [DataMember] public ManifestFloorFrames floor_frames;
            [DataMember] public ManifestEntry[] entries;
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
            public ManifestFloorFrames floor_frames;
            public ManifestEntry[] entries;
        }

        private struct RouteResult
        {
            public int manifest_index;
            public string floor;
            public int[] cell;
            public string outcome;
            public float cost_m;
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
                    floor_frames = doc.floor_frames,
                    entries = doc.entries ?? Array.Empty<ManifestEntry>(),
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
