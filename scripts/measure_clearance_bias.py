#!/usr/bin/env python3
"""Measure what the planner's bounded clearance-cost penalty actually does to routes.

For a sample of baseline sweep routes, re-plan the recorded start->target at several
CLEARANCE_PENALTY_PER_CELL_M values and compare:
  - how often the path CHANGES vs penalty=0 (the bias actually fires),
  - among changed routes, the interior min-clearance gain and the length cost,
  - any route whose length blows up (the "gym-loop": bought openness with a big detour).

This answers the user's question WITHOUT a code change: is the current 0.15m/0.8m-cap
penalty picking the roomy side around furniture (good), and does it ever loop the long
way around a multi-entrance room (bad)? Data, not assumption. See
[[project-navigation-clearance-cost-rejected-2026-05-29]].

Usage:
    python scripts/measure_clearance_bias.py [--limit N] [--penalties 0,0.15,0.3]
"""
import argparse
import glob
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import plan_object_route as P  # noqa: E402

BASELINE_DIR = os.path.join(
    HERE, "..", "artifacts", "navigation", "sweep", "baseline_pre")


def interior_min_clearance(planner, path):
    """Min wall-clearance (cells, capped) over the path's interior cells, same metric A* uses.
    Interior = exclude the two endpoints (they sit at start/goal, often near furniture)."""
    if not path or len(path) <= 2:
        return None
    mn = P.CLEARANCE_TARGET_CELLS
    for floor, ix, iz in path[1:-1]:
        if floor.startswith("@"):
            continue  # virtual inter-floor node, no grid clearance
        mn = min(mn, planner.floors[floor].clearance(ix, iz))
    return mn


def plan_route(planner, start_node, target):
    """Replan a recorded baseline route; return (path, cost) or (None, inf)."""
    tx = target["Position"]["x"]
    ty = target["Position"]["y"]
    tz = target["Position"]["z"]
    tfloor = planner._floor_for_target_y(ty)
    if tfloor is None:
        return None, math.inf
    radius = target.get("InteractionRadius", 1.0) or 1.0
    radius = max(P.MIN_INTERACTION_RADIUS_M, min(radius, P.MAX_INTERACTION_RADIUS_M))
    goals = P.goal_cells_around(planner.floors[tfloor], tx, tz, radius)
    if not goals:
        n = planner.floors[tfloor].nearest_navigable(
            tx, tz, max_radius_m=P.NEAREST_NAVIGABLE_SEARCH_M)
        if n is None:
            return None, math.inf
        goals = [n]
    goal_nodes = [(tfloor, ix, iz) for ix, iz in goals]
    path, cost, _ = planner.astar(start_node, goal_nodes, tfloor, tx, tz)
    return path, cost


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=400,
                    help="max baseline routes to sample (default 400)")
    ap.add_argument("--penalties", default="0,0.15,0.30",
                    help="comma-separated penalty values to compare (default 0,0.15,0.30)")
    args = ap.parse_args()
    penalties = [float(x) for x in args.penalties.split(",")]

    files = sorted(glob.glob(os.path.join(BASELINE_DIR, "route.*.json")))
    if args.limit:
        # Even stride so we sample across the whole house, not just one corner.
        step = max(1, len(files) // args.limit)
        files = files[::step][:args.limit]

    bake = P.load_bake()
    planner = P.Planner(bake)
    cell_m = planner.cell_size

    # Reference plan per route at penalty=0 (the unbiased shortest path).
    base_clear = {}     # value -> aggregate stats
    rows = []           # per-route comparison rows

    n_ok = 0
    for fp in files:
        d = json.load(open(fp))
        if d.get("status") != "ok":
            continue
        start = d["start"]
        sx, sz = start["wx"], start["wz"]
        sfloor = start["floor"]
        sn = planner.floors[sfloor].nearest_navigable(
            sx, sz, max_radius_m=P.NEAREST_NAVIGABLE_SEARCH_M)
        if sn is None:
            continue
        start_node = (sfloor, sn[0], sn[1])
        target = d["target"]

        per_pen = {}
        ok = True
        for pen in penalties:
            P.CLEARANCE_PENALTY_PER_CELL_M = pen
            for f in planner.floors.values():
                f._clearance = None  # force clearance map rebuild (cheap; metric unchanged anyway)
            path, cost = plan_route(planner, start_node, target)
            if path is None:
                ok = False
                break
            length_m = _path_length_m(planner, path, cell_m)
            per_pen[pen] = {
                "cells": tuple((fl, ix, iz) for fl, ix, iz in path),
                "minclr": interior_min_clearance(planner, path),
                "length": length_m,
            }
        if not ok:
            continue
        n_ok += 1

        ref = per_pen[penalties[0]]  # penalty=0 reference
        for pen in penalties:
            st = base_clear.setdefault(pen, {
                "n": 0, "sum_minclr": 0.0, "sum_len": 0.0,
                "changed": 0, "clr_gain_cells": 0, "len_pct_sum": 0.0,
                "blowups": 0, "n_minclr": 0})
            cur = per_pen[pen]
            st["n"] += 1
            st["sum_len"] += cur["length"]
            if cur["minclr"] is not None:
                st["sum_minclr"] += cur["minclr"]
                st["n_minclr"] += 1
            if pen == penalties[0]:
                continue
            changed = cur["cells"] != ref["cells"]
            if changed:
                st["changed"] += 1
                if cur["minclr"] is not None and ref["minclr"] is not None:
                    st["clr_gain_cells"] += (cur["minclr"] - ref["minclr"])
                if ref["length"] > 0.1:
                    pct = 100.0 * (cur["length"] - ref["length"]) / ref["length"]
                    st["len_pct_sum"] += pct
                    if pct >= 50.0:  # gym-loop signature: bought clearance with a big detour
                        st["blowups"] += 1
                        rows.append((os.path.basename(fp), pen, round(pct, 1),
                                     ref["minclr"], cur["minclr"],
                                     round(ref["length"], 1), round(cur["length"], 1)))

    print(f"sampled {n_ok} routable baseline routes; cell={cell_m:.3f}m\n")
    print(f"{'penalty':>8} {'avg_minclr_m':>12} {'avg_len_m':>10} {'%changed':>9} "
          f"{'avg_clr_gain_m':>14} {'avg_len_+%':>10} {'blowups>=50%':>12}")
    for pen in penalties:
        st = base_clear[pen]
        avg_minclr = (st["sum_minclr"] / st["n_minclr"] * cell_m) if st["n_minclr"] else 0.0
        avg_len = st["sum_len"] / st["n"] if st["n"] else 0.0
        if pen == penalties[0]:
            print(f"{pen:>8.2f} {avg_minclr:>12.3f} {avg_len:>10.1f} {'(ref)':>9} "
                  f"{'-':>14} {'-':>10} {'-':>12}")
            continue
        changed = st["changed"]
        pct_changed = 100.0 * changed / st["n"] if st["n"] else 0.0
        avg_gain = (st["clr_gain_cells"] / changed * cell_m) if changed else 0.0
        avg_lenpct = (st["len_pct_sum"] / changed) if changed else 0.0
        print(f"{pen:>8.2f} {avg_minclr:>12.3f} {avg_len:>10.1f} {pct_changed:>8.1f}% "
              f"{avg_gain:>14.3f} {avg_lenpct:>9.1f}% {st['blowups']:>12}")

    if rows:
        print("\nroutes that detoured >=50% (gym-loop signature: file, pen, len+%, "
              "ref_minclr_cells, new_minclr_cells, ref_len_m, new_len_m):")
        for r in sorted(rows, key=lambda x: -x[2])[:25]:
            print("  ", r)
    else:
        print("\nno route detoured >=50% at any tested penalty (no gym-loop signature).")


def _path_length_m(planner, path, cell_m):
    total = 0.0
    prev = None
    for fl, ix, iz in path:
        if fl.startswith("@"):
            continue
        w = planner.floors[fl].cell_to_world(ix, iz)
        if prev is not None and prev[0] == fl:
            dx = w[0] - prev[1][0]
            dz = w[1] - prev[1][1]
            total += math.hypot(dx, dz)
        prev = (fl, w)
    return total


if __name__ == "__main__":
    main()
