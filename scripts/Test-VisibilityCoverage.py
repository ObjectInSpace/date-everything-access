#!/usr/bin/env python3
"""Visibility coverage audit.

For each primary zone in the navigation graph, collect every anchor that
matters (zone-graph Nodes + transition CrossingAnchors), test pairwise XZ
visibility against the static blocker AABBs, and emit a coverage report.

The visibility test mirrors SimpleNavSceneData.IsSegmentClear (2D Liang-Barsky
slab clip with an inward `skin` shrink). When this script reports a zone as
fully connected, the same A->B segments will succeed at runtime.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timezone
from typing import Iterable

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
DEFAULT_BLOCKERS = os.path.join(REPO_ROOT, "artifacts", "navigation", "thirdpersongreybox-blockers.json")
DEFAULT_GRAPH = os.path.join(REPO_ROOT, "artifacts", "navigation", "navigation_graph.generated.json")
DEFAULT_OUT_JSON = os.path.join(REPO_ROOT, "artifacts", "navigation", "visibility_coverage.json")
DEFAULT_OUT_SUMMARY = os.path.join(REPO_ROOT, "artifacts", "navigation", "visibility_coverage.summary.txt")


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Audit per-zone visibility coverage.")
    p.add_argument("--blockers", default=DEFAULT_BLOCKERS)
    p.add_argument("--graph", default=DEFAULT_GRAPH)
    p.add_argument("--out-json", default=DEFAULT_OUT_JSON)
    p.add_argument("--out-summary", default=DEFAULT_OUT_SUMMARY)
    p.add_argument("--skin", type=float, default=0.05,
                   help="Metres to shrink each blocker's XZ footprint inward.")
    return p.parse_args()


# Player capsule slab around the segment endpoint Y — mirrors SimpleNavSceneData.cs constants.
PLAYER_SLAB_BELOW = 0.5
PLAYER_SLAB_ABOVE = 2.0


class Blocker:
    __slots__ = ("name", "min_x", "max_x", "min_z", "max_z", "min_y", "max_y")

    def __init__(self, name, min_x, max_x, min_z, max_z, min_y, max_y):
        self.name = name
        self.min_x = min_x
        self.max_x = max_x
        self.min_z = min_z
        self.max_z = max_z
        self.min_y = min_y
        self.max_y = max_y


def load_blockers(path: str, skin: float) -> list[Blocker]:
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    out: list[Blocker] = []
    for e in doc.get("NavigationBlockers", []):
        b3 = e.get("Bounds3D") or {}
        c = b3.get("Center"); s = b3.get("Size")
        if not c or not s:
            continue
        cx, cy, cz = c["x"], c["y"], c["z"]
        hx, hy, hz = s["x"] / 2.0, s["y"] / 2.0, s["z"] / 2.0
        min_x = cx - hx + skin
        max_x = cx + hx - skin
        min_z = cz - hz + skin
        max_z = cz + hz - skin
        if min_x >= max_x or min_z >= max_z:
            continue
        out.append(Blocker(e.get("Name", "?"), min_x, max_x, min_z, max_z, cy - hy, cy + hy))
    return out


def segment_intersects_rect(ax: float, az: float, bx: float, bz: float,
                            min_x: float, max_x: float, min_z: float, max_z: float) -> bool:
    """2D Liang-Barsky slab clip in XZ. Returns True if segment crosses the rect."""
    dx = bx - ax
    dz = bz - az
    t_enter = 0.0
    t_exit = 1.0

    if abs(dx) < 1e-6:
        if ax < min_x or ax > max_x:
            return False
    else:
        t1 = (min_x - ax) / dx
        t2 = (max_x - ax) / dx
        if t1 > t2:
            t1, t2 = t2, t1
        if t1 > t_enter: t_enter = t1
        if t2 < t_exit: t_exit = t2
        if t_enter > t_exit:
            return False

    if abs(dz) < 1e-6:
        if az < min_z or az > max_z:
            return False
    else:
        t1 = (min_z - az) / dz
        t2 = (max_z - az) / dz
        if t1 > t2:
            t1, t2 = t2, t1
        if t1 > t_enter: t_enter = t1
        if t2 < t_exit: t_exit = t2
        if t_enter > t_exit:
            return False

    return True


def find_first_blocker(ax: float, ay: float, az: float, bx: float, by: float, bz: float,
                       blockers: list[Blocker]) -> Blocker | None:
    seg_min_x = ax if ax < bx else bx
    seg_max_x = bx if ax < bx else ax
    seg_min_z = az if az < bz else bz
    seg_max_z = bz if az < bz else az
    seg_min_y = (ay if ay < by else by) - PLAYER_SLAB_BELOW
    seg_max_y = (ay if ay > by else by) + PLAYER_SLAB_ABOVE

    for b in blockers:
        if b.max_y < seg_min_y or b.min_y > seg_max_y:
            continue
        if seg_max_x < b.min_x or seg_min_x > b.max_x:
            continue
        if seg_max_z < b.min_z or seg_min_z > b.max_z:
            continue
        if segment_intersects_rect(ax, az, bx, bz, b.min_x, b.max_x, b.min_z, b.max_z):
            return b
    return None


def collect_anchors(graph: dict) -> dict[str, list[dict]]:
    """Returns {zone_name: [{Label, X, Y, Z, Source}, ...]}."""
    anchors: dict[str, list[dict]] = defaultdict(list)
    for n in graph.get("Nodes", []):
        pos = n.get("Position")
        if not pos:
            continue
        anchors[n["Zone"]].append({
            "Label": f"{n.get('Kind','?')}:{n.get('SceneZoneName','?')}",
            "X": float(pos["x"]), "Y": float(pos["y"]), "Z": float(pos["z"]),
            "Source": "Node",
        })
    for t in graph.get("Transitions", []):
        fc = t.get("FromCrossingAnchor")
        if fc:
            anchors[t["FromZone"]].append({
                "Label": f"Crossing->{t['ToZone']}",
                "X": float(fc["x"]), "Y": float(fc["y"]), "Z": float(fc["z"]),
                "Source": "FromCrossingAnchor",
            })
        tc = t.get("ToCrossingAnchor")
        if tc:
            anchors[t["ToZone"]].append({
                "Label": f"Crossing<-{t['FromZone']}",
                "X": float(tc["x"]), "Y": float(tc["y"]), "Z": float(tc["z"]),
                "Source": "ToCrossingAnchor",
            })
    return anchors


def audit_zone(anchors: list[dict], blockers: list[Blocker]) -> dict:
    n = len(anchors)
    if n < 2:
        return {
            "AnchorCount": n, "PairCount": 0, "BlockedCount": 0,
            "BlockedFraction": 0.0, "ComponentCount": n, "LargestComponentSize": n,
            "IsolatedAnchors": 0, "BlockedPairsSample": [],
        }

    adjacency: list[set[int]] = [set() for _ in range(n)]
    blocked_pairs: list[dict] = []
    pairs = 0
    blocked = 0

    for i in range(n):
        for j in range(i + 1, n):
            pairs += 1
            a = anchors[i]; b = anchors[j]
            hit = find_first_blocker(a["X"], a["Y"], a["Z"], b["X"], b["Y"], b["Z"], blockers)
            if hit is not None:
                blocked += 1
                if len(blocked_pairs) < 10:
                    blocked_pairs.append({
                        "From": a["Label"], "To": b["Label"], "BlockedBy": hit.name,
                    })
            else:
                adjacency[i].add(j)
                adjacency[j].add(i)

    visited = [False] * n
    component_sizes: list[int] = []
    for s in range(n):
        if visited[s]:
            continue
        stack = [s]
        size = 0
        while stack:
            cur = stack.pop()
            if visited[cur]:
                continue
            visited[cur] = True
            size += 1
            for nb in adjacency[cur]:
                if not visited[nb]:
                    stack.append(nb)
        component_sizes.append(size)

    isolated = sum(1 for sz in component_sizes if sz == 1)
    largest = max(component_sizes) if component_sizes else 0

    return {
        "AnchorCount": n,
        "PairCount": pairs,
        "BlockedCount": blocked,
        "BlockedFraction": round(blocked / max(1, pairs), 4),
        "ComponentCount": len(component_sizes),
        "LargestComponentSize": largest,
        "IsolatedAnchors": isolated,
        "BlockedPairsSample": blocked_pairs,
    }


def main() -> int:
    args = parse_args()

    print(f"Loading blockers from {args.blockers}")
    blockers = load_blockers(args.blockers, args.skin)
    print(f"  loaded {len(blockers)} static blockers")

    print(f"Loading navigation graph from {args.graph}")
    with open(args.graph, encoding="utf-8") as f:
        graph = json.load(f)
    print(f"  zones={len(graph.get('Zones',[]))} nodes={len(graph.get('Nodes',[]))} transitions={len(graph.get('Transitions',[]))}")

    print("Collecting anchors...")
    anchors_by_zone = collect_anchors(graph)
    print(f"  {len(anchors_by_zone)} zones, {sum(len(v) for v in anchors_by_zone.values())} anchors")

    print("Running per-zone visibility audit...")
    zone_reports: list[dict] = []
    total_pairs = 0
    total_blocked = 0
    zones_fully_connected = 0
    zones_isolated = 0

    for zone_name in sorted(anchors_by_zone.keys()):
        anchors = anchors_by_zone[zone_name]
        report = audit_zone(anchors, blockers)
        report = {"Zone": zone_name, **report}
        zone_reports.append(report)
        total_pairs += report["PairCount"]
        total_blocked += report["BlockedCount"]
        if report["LargestComponentSize"] == report["AnchorCount"]:
            zones_fully_connected += 1
        if report["IsolatedAnchors"] > 0:
            zones_isolated += 1

    result = {
        "GeneratedAt": datetime.now(timezone.utc).isoformat(),
        "BlockersPath": args.blockers,
        "GraphPath": args.graph,
        "Skin": round(args.skin, 4),
        "Totals": {
            "ZoneCount": len(zone_reports),
            "ZonesFullyConnected": zones_fully_connected,
            "ZonesWithIsolatedAnchors": zones_isolated,
            "TotalPairs": total_pairs,
            "TotalBlocked": total_blocked,
            "OverallBlockedFraction": round(total_blocked / max(1, total_pairs), 4),
        },
        "Zones": zone_reports,
    }

    os.makedirs(os.path.dirname(args.out_json), exist_ok=True)
    with open(args.out_json, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
    print(f"Wrote {args.out_json}")

    lines: list[str] = []
    lines.append("Visibility Coverage Audit")
    lines.append("=========================")
    lines.append(f"Generated: {result['GeneratedAt']}")
    lines.append(f"Skin: {result['Skin']} m")
    lines.append("")
    t = result["Totals"]
    lines.append(f"Zones audited:           {t['ZoneCount']}")
    lines.append(f"Fully-connected zones:   {t['ZonesFullyConnected']}")
    lines.append(f"Zones w/ isolated anchor:{t['ZonesWithIsolatedAnchors']}")
    lines.append(f"Anchor pairs tested:     {t['TotalPairs']}")
    lines.append(f"Blocked pairs:           {t['TotalBlocked']}  ({t['OverallBlockedFraction']:.1%})")
    lines.append("")
    lines.append("Per-zone (zones with any blocked pair, sorted by blocked count desc):")
    lines.append("----------------------------------------------------------------------")
    problem = sorted([z for z in zone_reports if z["BlockedCount"] > 0],
                     key=lambda z: -z["BlockedCount"])
    if not problem:
        lines.append("(none -- every zone is fully visibility-connected)")
    else:
        for z in problem:
            lines.append(
                f"  {z['Zone']:<28} anchors={z['AnchorCount']:>3}  "
                f"blocked={z['BlockedCount']:>3}/{z['PairCount']:<3}  "
                f"components={z['ComponentCount']}  isolated={z['IsolatedAnchors']}"
            )
            for bp in z["BlockedPairsSample"]:
                lines.append(f"      {bp['From']}  <->  {bp['To']}    blocked by {bp['BlockedBy']}")

    with open(args.out_summary, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print(f"Wrote {args.out_summary}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
