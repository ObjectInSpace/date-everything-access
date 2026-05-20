"""Step 5 prototype: offline connector derivation for a single zone pair.

Plan reference: project_navigation_unification_plan.md, Step 5.
Picks two zone pairs (one known-good, one known-bad in the current sweep) and
runs the 6-step derivation:
  1. Compute the shared boundary between two zones.
  2. Sample at capsule-width intervals.
  3. Capsule-clearance test perpendicular at step height.
  4. Cluster surviving samples into passages.
  5. Derive entry / midline / exit per cluster (symmetric).
  6. Attach door references where a door collider sits on the cluster.

Locomotion params from Step 4:
  - capsule radius 0.4m, height 2.5m
  - step-up tolerance 0.25m
  - no slope limit, no jump

Run from repo root:
  python scripts/Prototype-ConnectorDerivation.py
"""
from __future__ import annotations
import json, sys, math, os
from pathlib import Path
from collections import defaultdict

REPO = Path(__file__).resolve().parents[1]
NAV  = REPO / "artifacts/navigation/thirdpersongreybox-navigation-data.json"
WALK = REPO / "artifacts/navigation/thirdpersongreybox-walkable.json"
BLOCK= REPO / "artifacts/navigation/thirdpersongreybox-blockers.json"
OUT  = REPO / "artifacts/navigation/step5_connector_prototype.json"

CAPSULE_R   = 0.40
CAPSULE_H   = 2.50
STEP_UP_TOL = 0.25
ADJ_TOL     = 3.00   # Step-1 zone-adjacency tolerance (camera gaps up to ~2.5m)
SAMPLE_STEP = CAPSULE_R  # 0.4m sampling spacing along boundary

# (good, bad) pair selected from current sweep results
PAIRS = [
    ("bedroom",    "bathroom2",  "good"),
    ("gym",        "gym_closet", "bad"),
]


def zone_aabb(zone):
    p, s = zone["Position"], zone["Scale"]
    return {
        "name": zone["Name"],
        "xmin": p["x"] - abs(s["x"])/2, "xmax": p["x"] + abs(s["x"])/2,
        "ymin": p["y"] - abs(s["y"])/2, "ymax": p["y"] + abs(s["y"])/2,
        "zmin": p["z"] - abs(s["z"])/2, "zmax": p["z"] + abs(s["z"])/2,
        "cx":   p["x"], "cy": p["y"], "cz": p["z"],
        "sx":   abs(s["x"]), "sy": abs(s["y"]), "sz": abs(s["z"]),
    }


def boundary_face(a, b, tol=ADJ_TOL):
    """Return the shared face between two zone AABBs (within tol) or None.

    Result: dict with axis ('x' or 'z'), plane coordinate, and the perpendicular
    span (the segment along the OTHER horizontal axis where both zones overlap).
    Y span uses the intersection of the two Y ranges.
    """
    # Try X-axis boundary: a.xmax ~ b.xmin or a.xmin ~ b.xmax
    candidates = []
    for (av, bv, side) in [(a["xmax"], b["xmin"], "a-east"),
                           (a["xmin"], b["xmax"], "a-west")]:
        gap = abs(av - bv)
        if gap <= tol:
            zlo = max(a["zmin"], b["zmin"])
            zhi = min(a["zmax"], b["zmax"])
            if zhi - zlo > 0.1:
                plane = (av + bv) / 2.0
                candidates.append({
                    "axis": "x", "plane": plane, "gap": gap, "side": side,
                    "perp_lo": zlo, "perp_hi": zhi, "perp_axis": "z",
                    "ymin": max(a["ymin"], b["ymin"]),
                    "ymax": min(a["ymax"], b["ymax"]),
                })
    for (av, bv, side) in [(a["zmax"], b["zmin"], "a-north"),
                           (a["zmin"], b["zmax"], "a-south")]:
        gap = abs(av - bv)
        if gap <= tol:
            xlo = max(a["xmin"], b["xmin"])
            xhi = min(a["xmax"], b["xmax"])
            if xhi - xlo > 0.1:
                plane = (av + bv) / 2.0
                candidates.append({
                    "axis": "z", "plane": plane, "gap": gap, "side": side,
                    "perp_lo": xlo, "perp_hi": xhi, "perp_axis": "x",
                    "ymin": max(a["ymin"], b["ymin"]),
                    "ymax": min(a["ymax"], b["ymax"]),
                })
    if not candidates:
        return None
    # Pick the face with the largest perpendicular overlap (the real shared wall)
    candidates.sort(key=lambda c: -(c["perp_hi"] - c["perp_lo"]))
    return candidates[0]


def point_in_aabb_xz(x, z, b, pad=0.0):
    return (b["MinX"] - pad <= x <= b["MaxX"] + pad and
            b["MinZ"] - pad <= z <= b["MaxZ"] + pad)


def find_floor_y(x, z, walkables, y_lo, y_hi):
    """Pick the highest walkable Top within [y_lo, y_hi] whose footprint contains (x,z)."""
    best = None
    for w in walkables:
        fp = w["Footprint"]
        if fp["MinX"] - 0.01 <= x <= fp["MaxX"] + 0.01 and fp["MinZ"] - 0.01 <= z <= fp["MaxZ"] + 0.01:
            top = w["TopY"]
            if y_lo - 0.5 <= top <= y_hi + 0.5:
                if best is None or top > best:
                    best = top
    return best


def capsule_blocked(x, z, floor_y, blockers):
    """Is a capsule (r=0.4, h=2.5) standing on floor_y at (x,z) intersected by any blocker?

    Capsule occupies XZ disc of radius 0.4 centered on (x,z), Y from floor_y to floor_y+2.5.
    A blocker AABB blocks if its XZ rect (expanded by capsule radius for the disc test)
    intersects (x,z) AND its Y range overlaps [floor_y - STEP_UP_TOL, floor_y + 2.5].
    """
    y0 = floor_y - STEP_UP_TOL   # capsule can step up over this much
    y1 = floor_y + CAPSULE_H
    for b in blockers:
        if b["TopY"] < y0 or b["BottomY"] > y1:
            continue
        fp = b["Footprint"]
        # Closest point on blocker XZ rect to (x,z)
        cx = min(max(x, fp["MinX"]), fp["MaxX"])
        cz = min(max(z, fp["MinZ"]), fp["MaxZ"])
        dx = x - cx; dz = z - cz
        if dx*dx + dz*dz < CAPSULE_R * CAPSULE_R:
            return True, b
    return False, None


def cluster_samples(samples, max_gap):
    """Cluster contiguous passing samples along the boundary's perp axis."""
    clusters = []
    cur = []
    for s in samples:
        if not s["passes"]:
            if cur:
                clusters.append(cur); cur = []
            continue
        if cur and (s["t"] - cur[-1]["t"]) > max_gap + 1e-6:
            clusters.append(cur); cur = []
        cur.append(s)
    if cur:
        clusters.append(cur)
    return clusters


def derive_for_pair(zone_a, zone_b, walkables_a, walkables_b, all_blockers, doors):
    a = zone_aabb(zone_a); b = zone_aabb(zone_b)
    face = boundary_face(a, b)
    if face is None:
        return {"error": "no shared boundary within tolerance", "zone_a": a, "zone_b": b}

    # Walk the perp axis; "plane" is the boundary line at face['axis'] = face['plane'].
    # At each step, we probe BOTH sides (one capsule-radius into A and into B) plus on-line.
    perp_lo, perp_hi = face["perp_lo"], face["perp_hi"]
    n = max(2, int(math.ceil((perp_hi - perp_lo) / SAMPLE_STEP)))
    samples = []
    pad = CAPSULE_R * 1.25   # probe slightly inside each side
    walkables_join = walkables_a + walkables_b
    for i in range(n + 1):
        t = perp_lo + (perp_hi - perp_lo) * (i / n)
        if face["axis"] == "x":
            x_line, z_line = face["plane"], t
            probe_a = (face["plane"] - pad if a["cx"] < b["cx"] else face["plane"] + pad, t)
            probe_b = (face["plane"] + pad if a["cx"] < b["cx"] else face["plane"] - pad, t)
        else:
            x_line, z_line = t, face["plane"]
            probe_a = (t, face["plane"] - pad if a["cz"] < b["cz"] else face["plane"] + pad)
            probe_b = (t, face["plane"] + pad if a["cz"] < b["cz"] else face["plane"] - pad)

        floor_a = find_floor_y(*probe_a, walkables_a, face["ymin"], face["ymax"])
        floor_b = find_floor_y(*probe_b, walkables_b, face["ymin"], face["ymax"])
        # If we have a floor on at least one side, use the lower (more permissive) for clearance.
        floors = [f for f in (floor_a, floor_b) if f is not None]
        if not floors:
            samples.append({"t": t, "x": x_line, "z": z_line, "passes": False,
                            "reason": "no-floor-either-side"})
            continue
        floor_y = min(floors)
        if abs((floor_a or floor_y) - (floor_b or floor_y)) > STEP_UP_TOL:
            samples.append({"t": t, "x": x_line, "z": z_line, "passes": False,
                            "reason": f"step-too-big ({(floor_a or 0):.2f} vs {(floor_b or 0):.2f})"})
            continue
        blocked, by = capsule_blocked(x_line, z_line, floor_y, all_blockers)
        if blocked:
            samples.append({"t": t, "x": x_line, "z": z_line, "passes": False,
                            "reason": f"blocked-by:{by['Name']}", "floor_y": floor_y})
        else:
            samples.append({"t": t, "x": x_line, "z": z_line, "passes": True,
                            "floor_y": floor_y,
                            "floor_a": floor_a, "floor_b": floor_b})

    clusters_raw = cluster_samples(samples, max_gap=SAMPLE_STEP * 1.5)

    # Filter clusters too narrow for capsule (need at least 2*r width)
    clusters = []
    for c in clusters_raw:
        width = c[-1]["t"] - c[0]["t"]
        if width + SAMPLE_STEP < 2 * CAPSULE_R:
            continue
        clusters.append(c)

    # Build passages with entry/exit/midline + door attachment
    passages = []
    for c in clusters:
        t_mid = (c[0]["t"] + c[-1]["t"]) / 2
        if face["axis"] == "x":
            mid_x = face["plane"]; mid_z = t_mid
            entry = (face["plane"] - pad if a["cx"] < b["cx"] else face["plane"] + pad, t_mid)
            exit_ = (face["plane"] + pad if a["cx"] < b["cx"] else face["plane"] - pad, t_mid)
        else:
            mid_x = t_mid; mid_z = face["plane"]
            entry = (t_mid, face["plane"] - pad if a["cz"] < b["cz"] else face["plane"] + pad)
            exit_ = (t_mid, face["plane"] + pad if a["cz"] < b["cz"] else face["plane"] - pad)
        # Attach door if any door XZ lies within cluster perp extent and within ~1.5m of boundary line
        door_ref = None
        for d in doors:
            dp = d["Position"]
            if face["axis"] == "x":
                if abs(dp["x"] - face["plane"]) > 2.0:  # door must sit on/near boundary line
                    continue
                if c[0]["t"] - CAPSULE_R <= dp["z"] <= c[-1]["t"] + CAPSULE_R:
                    door_ref = {"name": d["Name"], "position": dp,
                                "dist_to_boundary": abs(dp["x"] - face["plane"])}
                    break
            else:
                if abs(dp["z"] - face["plane"]) > 2.0:
                    continue
                if c[0]["t"] - CAPSULE_R <= dp["x"] <= c[-1]["t"] + CAPSULE_R:
                    door_ref = {"name": d["Name"], "position": dp,
                                "dist_to_boundary": abs(dp["z"] - face["plane"])}
                    break
        passages.append({
            "width_m": round(c[-1]["t"] - c[0]["t"], 3),
            "sample_count": len(c),
            "midline_xz": [round(mid_x, 3), round(mid_z, 3)],
            "entry_xz":   [round(entry[0], 3), round(entry[1], 3)],
            "exit_xz":    [round(exit_[0], 3),  round(exit_[1], 3)],
            "floor_y":    round(sum(s["floor_y"] for s in c)/len(c), 3),
            "door":       door_ref,
        })

    fail_reasons = defaultdict(int)
    for s in samples:
        if not s["passes"]:
            tag = s["reason"].split(":", 1)[0] if ":" in s["reason"] else s["reason"]
            fail_reasons[tag] += 1

    return {
        "boundary_face": face,
        "sample_count": len(samples),
        "passing_samples": sum(1 for s in samples if s["passes"]),
        "fail_histogram": dict(fail_reasons),
        "clusters_raw": len(clusters_raw),
        "passages": passages,
    }


def main():
    nav  = json.load(open(NAV,  encoding="utf-8"))
    walk = json.load(open(WALK, encoding="utf-8"))
    blok = json.load(open(BLOCK,encoding="utf-8"))

    zones_by_name = {z["Name"]: z for z in nav["CameraSpaces"]}
    # Walkable surfaces tagged by zone AABB.
    # Test: surface Footprint AABB intersects zone XZ AABB (not just center inside).
    # Reason: shared floor slabs (e.g. SM_Floor_Bedroom is 700+ sqm covering several
    # upstairs rooms) have their CENTER outside any single zone. The slab still
    # belongs to every zone whose XZ rectangle it overlaps.
    surfaces_by_zone = defaultdict(list)
    for w in walk["WalkableSurfaces"]:
        fp = w["Footprint"]
        if abs(fp["CenterX"]) > 200 or abs(fp["CenterZ"]) > 200:
            continue
        for name, z in zones_by_name.items():
            a = zone_aabb(z)
            if (a["sx"] <= 0 or a["sz"] <= 0):
                continue
            # XZ AABB overlap test
            if fp["MaxX"] < a["xmin"] - 0.5 or fp["MinX"] > a["xmax"] + 0.5:
                continue
            if fp["MaxZ"] < a["zmin"] - 0.5 or fp["MinZ"] > a["zmax"] + 0.5:
                continue
            # Y plausibility: walkable top must be at-or-below zone Y range top + ceiling pad
            if not (a["ymin"] - 2.0 <= w["TopY"] <= a["ymax"] + 1.0):
                continue
            surfaces_by_zone[name].append(w)

    door_objs = [d for d in nav["DoorObjects"]
                 if d["Name"].startswith("Doors_") and d.get("NearestGraphZones")]

    blockers = blok["NavigationBlockers"]

    report = {
        "params": {
            "capsule_radius_m": CAPSULE_R,
            "capsule_height_m": CAPSULE_H,
            "step_up_tolerance_m": STEP_UP_TOL,
            "adjacency_tolerance_m": ADJ_TOL,
            "sample_step_m": SAMPLE_STEP,
        },
        "walkable_zone_tagging": {
            "total_surfaces": len(walk["WalkableSurfaces"]),
            "tagged_to_any_target": sum(
                len(surfaces_by_zone[n]) for n in ("bedroom","bathroom2","gym","gym_closet")
            ),
            "per_zone_counts": {n: len(surfaces_by_zone[n])
                                for n in ("bedroom","bathroom2","gym","gym_closet")},
        },
        "pairs": [],
    }

    for a_name, b_name, label in PAIRS:
        za, zb = zones_by_name[a_name], zones_by_name[b_name]
        out = derive_for_pair(za, zb,
                              surfaces_by_zone[a_name],
                              surfaces_by_zone[b_name],
                              blockers, door_objs)
        out["from"] = a_name; out["to"] = b_name; out["sweep_label"] = label
        report["pairs"].append(out)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(report, indent=2), encoding="utf-8")

    # Console summary
    print(f"Wrote {OUT}")
    print()
    print(f"Walkable zone-tagging:")
    for n, c in report["walkable_zone_tagging"]["per_zone_counts"].items():
        print(f"  {n}: {c} surfaces")
    print()
    for p in report["pairs"]:
        print(f"--- {p['from']} -> {p['to']} ({p['sweep_label']}) ---")
        if "error" in p:
            print(f"  ERROR: {p['error']}"); continue
        f = p["boundary_face"]
        print(f"  boundary: axis={f['axis']} plane={f['plane']:.2f} "
              f"perp[{f['perp_lo']:.2f},{f['perp_hi']:.2f}] "
              f"y[{f['ymin']:.2f},{f['ymax']:.2f}] gap={f['gap']:.2f}m")
        print(f"  samples: {p['passing_samples']}/{p['sample_count']} passing")
        print(f"  fail_histogram: {p['fail_histogram']}")
        print(f"  clusters: {p['clusters_raw']} raw -> {len(p['passages'])} viable passages")
        for i, pa in enumerate(p["passages"]):
            d = pa["door"]["name"] if pa["door"] else "(none)"
            print(f"    passage[{i}]: width={pa['width_m']}m mid={pa['midline_xz']} "
                  f"floor_y={pa['floor_y']} door={d}")
        print()


if __name__ == "__main__":
    main()
