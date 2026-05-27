"""Offline validator for SimpleNav route artifacts.

This checks planner output without launching the game:

* route endpoints must be on baked navigable cells;
* every same-floor segment must remain on baked navigable cells;
* every same-floor segment is also checked against exported blocker footprints.

The blocker check is intentionally independent from the bake and can be noisier than
the bitmap check. It is still useful because it names the collider path a segment
appears to cross.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SWEEP_DIR = ROOT / "artifacts" / "navigation" / "sweep" / "default"
BLOCKERS = ROOT / "artifacts" / "navigation" / "thirdpersongreybox-blockers.json"
NAVDATA = ROOT / "artifacts" / "navigation" / "thirdpersongreybox-navigation-data.json"

sys.path.insert(0, str(Path(__file__).resolve().parent))
import plan_object_route as planner_mod  # noqa: E402


PLAYER_RADIUS_M = 0.50
PLAYER_HEIGHT_M = 2.50
SAMPLE_STEP_FACTOR = 0.35
MAX_FAILURES_TO_PRINT = 200


def dist_point_segment(px, pz, ax, az, bx, bz):
    vx = bx - ax
    vz = bz - az
    l2 = vx * vx + vz * vz
    if l2 <= 1e-9:
        return math.hypot(px - ax, pz - az)
    t = max(0.0, min(1.0, ((px - ax) * vx + (pz - az) * vz) / l2))
    cx = ax + t * vx
    cz = az + t * vz
    return math.hypot(px - cx, pz - cz)


def point_in_poly(px, pz, verts):
    inside = False
    j = len(verts) - 1
    for i in range(len(verts)):
        xi, zi = verts[i]
        xj, zj = verts[j]
        crosses = (zi > pz) != (zj > pz)
        if crosses:
            x_at_z = (xj - xi) * (pz - zi) / ((zj - zi) or 1e-9) + xi
            if px < x_at_z:
                inside = not inside
        j = i
    return inside


def segments_intersect(a, b, c, d):
    def orient(p, q, r):
        return (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0])

    def on_segment(p, q, r):
        return (
            min(p[0], r[0]) - 1e-9 <= q[0] <= max(p[0], r[0]) + 1e-9
            and min(p[1], r[1]) - 1e-9 <= q[1] <= max(p[1], r[1]) + 1e-9
        )

    o1 = orient(a, b, c)
    o2 = orient(a, b, d)
    o3 = orient(c, d, a)
    o4 = orient(c, d, b)
    if o1 * o2 < 0 and o3 * o4 < 0:
        return True
    if abs(o1) <= 1e-9 and on_segment(a, c, b):
        return True
    if abs(o2) <= 1e-9 and on_segment(a, d, b):
        return True
    if abs(o3) <= 1e-9 and on_segment(c, a, d):
        return True
    if abs(o4) <= 1e-9 and on_segment(c, b, d):
        return True
    return False


def segment_polygon_distance(ax, az, bx, bz, verts):
    if not verts:
        return math.inf
    if point_in_poly(ax, az, verts) or point_in_poly(bx, bz, verts):
        return 0.0
    best = math.inf
    seg_a = (ax, az)
    seg_b = (bx, bz)
    for i, va in enumerate(verts):
        vb = verts[(i + 1) % len(verts)]
        if segments_intersect(seg_a, seg_b, va, vb):
            return 0.0
        best = min(best, dist_point_segment(va[0], va[1], ax, az, bx, bz))
        best = min(best, dist_point_segment(ax, az, va[0], va[1], vb[0], vb[1]))
        best = min(best, dist_point_segment(bx, bz, va[0], va[1], vb[0], vb[1]))
    return best


def rect_vertices(minx, maxx, minz, maxz):
    return [(minx, minz), (maxx, minz), (maxx, maxz), (minx, maxz)]


def blocker_vertices(record):
    fp = record.get("Footprint") or {}
    verts = fp.get("Vertices")
    if verts:
        return [(float(v["x"]), float(v["z"])) for v in verts if "x" in v and "z" in v]

    b = record.get("Bounds2D") or {}
    if {"MinX", "MaxX", "MinZ", "MaxZ"} <= set(b.keys()):
        return rect_vertices(float(b["MinX"]), float(b["MaxX"]), float(b["MinZ"]), float(b["MaxZ"]))

    return []


def is_solid_blocker(record):
    if not record.get("IsActive", True):
        return False
    if record.get("Enabled") is False:
        return False
    if record.get("IsTrigger"):
        return False
    if record.get("IsDoorConnector") or record.get("IsTeleporterConnector"):
        return False
    if record.get("HasRigidbody"):
        return False
    return True


def load_blocker_records():
    raw = json.loads(BLOCKERS.read_text(encoding="utf-8-sig"))
    out = []
    for key in ("NavigationBlockers", "MeshColliders"):
        for record in raw.get(key, []):
            if not is_solid_blocker(record):
                continue
            verts = blocker_vertices(record)
            if len(verts) < 3:
                continue
            out.append(
                {
                    "name": record.get("Name") or "<unnamed>",
                    "path": record.get("Path") or "",
                    "bottom": float(record.get("BottomY", -9999.0)),
                    "top": float(record.get("TopY", 9999.0)),
                    "bounds": record.get("Bounds2D") or {},
                    "vertices": verts,
                }
            )
    return out


def load_camera_zones():
    raw = json.loads(NAVDATA.read_text(encoding="utf-8-sig"))
    zones = []
    for zone in raw.get("CameraSpaces", []):
        pos = zone.get("Position") or {}
        scale = zone.get("Scale") or {}
        name = zone.get("Name") or "<unnamed>"
        if not {"x", "y", "z"} <= set(pos.keys()) or not {"x", "y", "z"} <= set(scale.keys()):
            continue
        sx = abs(float(scale["x"]))
        sy = abs(float(scale["y"]))
        sz = abs(float(scale["z"]))
        # Point zones are useful as named anchors, but they should not dominate
        # route-segment diagnostics.
        if sx <= 0.01 and sy <= 0.01 and sz <= 0.01:
            continue
        zones.append(
            {
                "name": name,
                "x": float(pos["x"]),
                "y": float(pos["y"]),
                "z": float(pos["z"]),
                "hx": sx / 2.0,
                "hy": sy / 2.0,
                "hz": sz / 2.0,
            }
        )
    return zones


def zones_containing(zones, wx, floor_y, wz):
    hits = []
    # CameraSpaces.CameraLoc uses transform position and collider closest point.
    # Offline we approximate with the standing point plus a mid-body Y probe.
    y = floor_y + PLAYER_HEIGHT_M * 0.5
    for zone in zones:
        if (
            zone["x"] - zone["hx"] <= wx <= zone["x"] + zone["hx"]
            and zone["y"] - zone["hy"] <= y <= zone["y"] + zone["hy"]
            and zone["z"] - zone["hz"] <= wz <= zone["z"] + zone["hz"]
        ):
            hits.append(zone["name"])
    return hits


def segment_zone_summary(zones, floor_y, ax, az, bx, bz):
    start = zones_containing(zones, ax, floor_y, az)
    end = zones_containing(zones, bx, floor_y, bz)
    crossed = set(start) | set(end)
    distance = math.hypot(bx - ax, bz - az)
    sample_count = max(1, int(math.ceil(distance / 1.0)))
    for i in range(1, sample_count):
        t = i / sample_count
        wx = ax + (bx - ax) * t
        wz = az + (bz - az) * t
        crossed.update(zones_containing(zones, wx, floor_y, wz))
    return start, end, sorted(crossed)


def y_overlaps_floor(record, floor_y):
    return record["top"] >= floor_y + 0.35 and record["bottom"] <= floor_y + PLAYER_HEIGHT_M


def aabb_may_hit(record, ax, az, bx, bz, radius):
    b = record.get("bounds") or {}
    if not {"MinX", "MaxX", "MinZ", "MaxZ"} <= set(b.keys()):
        return True
    minx = min(ax, bx) - radius
    maxx = max(ax, bx) + radius
    minz = min(az, bz) - radius
    maxz = max(az, bz) + radius
    return not (
        maxx < float(b["MinX"])
        or minx > float(b["MaxX"])
        or maxz < float(b["MinZ"])
        or minz > float(b["MaxZ"])
    )


def blocker_hit(blockers, floor_y, ax, az, bx, bz, radius):
    for record in blockers:
        if not y_overlaps_floor(record, floor_y):
            continue
        if not aabb_may_hit(record, ax, az, bx, bz, radius):
            continue
        if segment_polygon_distance(ax, az, bx, bz, record["vertices"]) <= radius:
            return record
    return None


def waypoint_world(waypoint):
    if "wx" in waypoint and "wz" in waypoint:
        return float(waypoint["wx"]), float(waypoint["wz"])
    if waypoint.get("world_xz"):
        return float(waypoint["world_xz"][0]), float(waypoint["world_xz"][1])
    return None


def sampled_cells_on_segment(floor, ax, az, bx, bz):
    distance = math.hypot(bx - ax, bz - az)
    step = max(floor.cell_size * SAMPLE_STEP_FACTOR, 0.02)
    samples = max(1, int(math.ceil(distance / step)))
    prev = None
    for i in range(samples + 1):
        t = i / samples
        wx = ax + (bx - ax) * t
        wz = az + (bz - az) * t
        cell = floor.world_to_cell(wx, wz)
        if cell != prev:
            prev = cell
            yield cell


def planner_bresenham_clear(floor, ix0, iz0, ix1, iz1):
    """Mirror of plan_object_route._segment_is_clear's Bresenham walk.

    Returns True iff every cell visited by the planner's line-of-sight check is
    navigable. We deliberately replicate the exact stepping order so divergence
    from supercover sampling indicates a real planner bug, not a different
    discretization."""
    dx = abs(ix1 - ix0)
    dz = abs(iz1 - iz0)
    sx = 1 if ix0 < ix1 else -1
    sz = 1 if iz0 < iz1 else -1
    err = dx - dz
    ix, iz = ix0, iz0
    while True:
        if not floor.navigable(ix, iz):
            return False
        if ix == ix1 and iz == iz1:
            return True
        e2 = 2 * err
        if e2 > -dz:
            err -= dz
            ix += sx
        if e2 < dx:
            err += dx
            iz += sz


def format_zone_detail(zones, floor_y, aw, bw):
    start, end, crossed = segment_zone_summary(zones, floor_y, aw[0], aw[1], bw[0], bw[1])
    return " zones=start:{0} end:{1} crossed:{2}".format(
        ",".join(start) if start else "<none>",
        ",".join(end) if end else "<none>",
        ",".join(crossed) if crossed else "<none>",
    )


def validate_route(route, planner, blockers, zones, source):
    failures = []
    if route.get("status") != "ok":
        return failures

    waypoints = route.get("waypoints") or []
    if len(waypoints) < 1:
        failures.append((source, "empty_route", "route has no waypoints"))
        return failures

    start = route.get("start")
    if isinstance(start, dict):
        floor_label = start.get("floor")
        cell = start.get("cell")
        if floor_label in planner.floors and cell and len(cell) == 2:
            floor = planner.floors[floor_label]
            if not floor.navigable(int(cell[0]), int(cell[1])):
                failures.append(
                    (
                        source,
                        "start_not_navigable",
                        f"start floor={floor_label} cell={cell}",
                    )
                )

    for idx, waypoint in enumerate(waypoints):
        floor_label = waypoint.get("floor")
        if floor_label not in planner.floors:
            if str(floor_label).startswith("@"):
                continue
            failures.append((source, "unknown_floor", f"wp[{idx}] floor={floor_label}"))
            continue
        floor = planner.floors[floor_label]
        cell = waypoint.get("cell")
        if not cell or len(cell) != 2:
            failures.append((source, "missing_cell", f"wp[{idx}] floor={floor_label}"))
            continue
        if not floor.navigable(int(cell[0]), int(cell[1])):
            failures.append((source, "waypoint_not_navigable", f"wp[{idx}] {floor_label} cell={cell}"))

    for idx in range(len(waypoints) - 1):
        a = waypoints[idx]
        b = waypoints[idx + 1]
        fa = a.get("floor")
        fb = b.get("floor")
        if fa != fb or fa not in planner.floors:
            continue

        aw = waypoint_world(a)
        bw = waypoint_world(b)
        if aw is None or bw is None:
            failures.append((source, "missing_world_point", f"segment[{idx}]"))
            continue

        floor = planner.floors[fa]
        ca = floor.world_to_cell(aw[0], aw[1])
        cb = floor.world_to_cell(bw[0], bw[1])
        bresenham_clear = planner_bresenham_clear(floor, ca[0], ca[1], cb[0], cb[1])
        supercover_blocked_cell = None
        for cell in sampled_cells_on_segment(floor, aw[0], aw[1], bw[0], bw[1]):
            if not floor.navigable(cell[0], cell[1]):
                supercover_blocked_cell = cell
                break

        if supercover_blocked_cell is not None:
            failures.append(
                (
                    source,
                    "segment_crosses_blocked_cell",
                    f"segment[{idx}] floor={fa} cell={list(supercover_blocked_cell)} "
                    f"from={aw} to={bw}"
                    + format_zone_detail(zones, floor.floor_y, aw, bw),
                )
            )
            if bresenham_clear:
                failures.append(
                    (
                        source,
                        "planner_bresenham_disagrees_with_supercover",
                        f"segment[{idx}] floor={fa} blocked_cell={list(supercover_blocked_cell)} "
                        f"bres_endpoints={list(ca)}->{list(cb)} "
                        f"from={aw} to={bw}"
                        + format_zone_detail(zones, floor.floor_y, aw, bw),
                    )
                )

        hit = blocker_hit(blockers, floor.floor_y, aw[0], aw[1], bw[0], bw[1], PLAYER_RADIUS_M)
        if hit is not None:
            failures.append(
                (
                    source,
                    "segment_crosses_blocker",
                    f"segment[{idx}] blocker={hit['name']} path={hit['path']}"
                    + format_zone_detail(zones, floor.floor_y, aw, bw),
                )
            )

    return failures


def iter_route_files(route_dir, manifest_path=None, max_routes=None):
    if manifest_path and manifest_path.exists():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        count = 0
        for entry in manifest.get("entries", []):
            route_name = entry.get("route")
            if not route_name:
                continue
            yield route_dir / route_name
            count += 1
            if max_routes and count >= max_routes:
                return
        return

    count = 0
    for route_path in sorted(route_dir.glob("route.*.json")):
        yield route_path
        count += 1
        if max_routes and count >= max_routes:
            return


def validate_existing_routes(args, planner, blockers, zones):
    route_dir = args.route_dir
    manifest = route_dir / "sweep_manifest.json"
    counts = {}
    failures = []
    total = 0
    for route_path in iter_route_files(route_dir, manifest, args.max_routes):
        total += 1
        try:
            route = json.loads(route_path.read_text(encoding="utf-8"))
        except Exception as ex:
            failures.append((str(route_path), "route_read_failed", str(ex)))
            continue

        route_failures = validate_route(route, planner, blockers, zones, str(route_path.relative_to(ROOT)))
        for failure in route_failures:
            counts[failure[1]] = counts.get(failure[1], 0) + 1
        failures.extend(route_failures)

    return total, counts, failures


def validate_object_plans(args, planner, blockers, zones):
    items = planner_mod.load_interactables()
    selected = []
    for item in items:
        if args.active_only and not item.get("IsActive", True):
            continue
        if args.datable_only and not item.get("IsDatable"):
            continue
        if args.name_contains:
            hay = (item.get("GameObjectName") or "") + " " + (item.get("Path") or "")
            if args.name_contains.lower() not in hay.lower():
                continue
        selected.append(item)
        if args.max_objects and len(selected) >= args.max_objects:
            break

    counts = {}
    failures = []
    for item in selected:
        target = str(item.get("GameObjectId"))
        try:
            route = planner_mod.plan(
                target,
                tuple(args.start_xz) if args.start_xz else None,
                args.start_floor,
                args.interaction_radius,
            )
        except SystemExit as ex:
            failures.append((target, "plan_failed", str(ex)))
            counts["plan_failed"] = counts.get("plan_failed", 0) + 1
            continue

        if route.get("status") != "ok":
            status = route.get("status", "unknown_status")
            failures.append((target, status, item.get("Path") or item.get("GameObjectName") or ""))
            counts[status] = counts.get(status, 0) + 1
            continue

        route_failures = validate_route(route, planner, blockers, zones, target)
        for failure in route_failures:
            counts[failure[1]] = counts.get(failure[1], 0) + 1
        failures.extend(route_failures)

    return len(selected), counts, failures


def print_report(title, total, counts, failures):
    print(title)
    print(f"checked={total}")
    if counts:
        for key in sorted(counts):
            print(f"{key}={counts[key]}")
    else:
        print("failures=0")

    for source, kind, detail in failures[:MAX_FAILURES_TO_PRINT]:
        print(f"{kind}: {source}: {detail}")
    if len(failures) > MAX_FAILURES_TO_PRINT:
        print(f"... {len(failures) - MAX_FAILURES_TO_PRINT} more failures omitted")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="mode", required=True)

    def add_doors_open(p):
        p.add_argument("--doors-open", nargs="*", metavar="NAME",
                       help="Treat the named doors as open during validation.")
        p.add_argument("--all-doors-open", action="store_true",
                       help="Open every door with exported freed-cells data.")
        p.add_argument("--state-walls-open", nargs="*", metavar="NAME",
                       help="Release named state-gated walls during validation.")
        p.add_argument("--all-state-walls-open", action="store_true",
                       help="Release every state-gated wall with exported freed-cells data.")

    route_ap = sub.add_parser("routes", help="Validate existing route JSON artifacts.")
    route_ap.add_argument("--route-dir", type=Path, default=DEFAULT_SWEEP_DIR)
    route_ap.add_argument("--max-routes", type=int)
    route_ap.add_argument("--warn-only", action="store_true")
    add_doors_open(route_ap)

    object_ap = sub.add_parser("objects", help="Plan and validate routes to exported interactables.")
    object_ap.add_argument("--start-xz", nargs=2, type=float, metavar=("X", "Z"))
    object_ap.add_argument("--start-floor", choices=("ground", "upper"), default="ground")
    object_ap.add_argument("--interaction-radius", type=float)
    object_ap.add_argument("--max-objects", type=int, default=200)
    object_ap.add_argument("--active-only", action="store_true", default=True)
    object_ap.add_argument("--datable-only", action="store_true")
    object_ap.add_argument("--name-contains")
    object_ap.add_argument("--warn-only", action="store_true")
    add_doors_open(object_ap)

    args = ap.parse_args()
    bake = planner_mod.load_bake()
    doors_open = "all" if args.all_doors_open else (args.doors_open or None)
    state_walls_open = "all" if args.all_state_walls_open else (args.state_walls_open or None)
    planner = planner_mod.Planner(bake, doors_open=doors_open, state_walls_open=state_walls_open)
    blockers = load_blocker_records()
    zones = load_camera_zones()

    if args.mode == "routes":
        total, counts, failures = validate_existing_routes(args, planner, blockers, zones)
        print_report("offline route artifact validation", total, counts, failures)
    elif args.mode == "objects":
        total, counts, failures = validate_object_plans(args, planner, blockers, zones)
        print_report("offline object route validation", total, counts, failures)
    else:
        raise SystemExit(f"unknown mode {args.mode}")

    if failures and not args.warn_only:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
