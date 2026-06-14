"""Offline line-of-sight parity validator.

Replays the EXACT interaction-LOS rays the game logged (BepInEx `LOS_PROBE` lines,
emitted by AccessibilityWatcher.HasInteractionLineOfSightToTarget during a sweep)
against an offline collider set rebuilt from the scene blocker export, and diffs
the offline verdict/hit against the in-game ground truth.

Purpose: PARITY FIRST. Before trusting any offline LOS judgement, prove the offline
raycaster agrees with Unity's Physics.Raycast on the same rays. Only then is it worth
upgrading mesh fidelity (this stage uses OBB-exact primitives + mesh-as-AABB).

Fidelity (Stage 1):
  - Box / Sphere / Capsule colliders: exact (OBB / sphere / capsule ray tests).
  - Mesh colliders: AABB (Bounds3D) — CONSERVATIVE: a bounding box over-blocks, so
    offline errs toward "no LOS" (false negative), never inventing LOS that isn't
    there. The diff report quantifies how often that AABB approximation diverges from
    the real triangle hit, which tells us whether exact-mesh triangles (Stage 2) are
    needed. NOTE: ~842 mesh colliders are unresolved (no triangle records) but DO have
    Bounds3D, so AABB lets us include them as occluders now.

Collider set parity: the game rays against ~dateviatorIgnores, so we use the raw
PrimitiveColliders + MeshColliders inventory (NOT the bake's NavigationBlockers),
exclude IsTrigger and disabled colliders, and drop layers per the per-run `mask=`
field logged with each probe (falling back to excluding Unity's built-in
IgnoreRaycast layer 2 when a probe predates mask logging).

Usage:
  python scripts/validate_los.py --log "<BepInEx LogOutput.log>"
"""
from __future__ import annotations

import argparse
import math
import re
from pathlib import Path

# Collider model, ray tests, first_hit and mask_excluded_layers all live in
# los_geometry so this validator and the planner's goal filter share ONE raycaster
# (the one this script proves at parity against the in-game LOS_PROBE rays).
from los_geometry import (  # noqa: F401  (IGNORE_RAYCAST_LAYER re-exported for callers)
    IGNORE_RAYCAST_LAYER,
    first_hit,
    load_colliders,
    mask_excluded_layers,
)

ROOT = Path(__file__).resolve().parents[1]


# ---------- LEGACY vector helpers removed: now in los_geometry ----------


# ---------- LOS_PROBE log parsing ----------

def _vec(prefix):
    # Named-group vector: prefix_x/y/z, matching "(x,y,z)" with escaped literal parens.
    return (r"\(" +
            r"(?P<" + prefix + r"_x>[-\d.]+)," +
            r"(?P<" + prefix + r"_y>[-\d.]+)," +
            r"(?P<" + prefix + r"_z>[-\d.]+)\)")


PROBE_RE = re.compile(
    r"LOS_PROBE target=(?P<target>\S+)"
    r" origin=" + _vec("o") +
    r" dir=" + _vec("d") +
    r" mask=(?P<mask>-?\d+)"
    r" radius=(?P<radius>[-\d.]+)"
    r" hit=(?P<hit>\S+)"
    r" hit_path=(?P<hit_path>\S+)"
    r" hit_point=\S+"
    r" hit_dist=(?P<hit_dist>[-\d.eE+]+|Infinity)"
    r" hit_is_target=(?P<hit_is_target>True|False)"
    r" verdict=(?P<verdict>True|False)"
)


def parse_probes(log_path):
    probes = []
    for line in Path(log_path).read_text(encoding="utf-8", errors="replace").splitlines():
        m = PROBE_RE.search(line)
        if not m:
            continue
        g = m.group
        probes.append({
            "target": g("target"),
            "origin": (float(g("o_x")), float(g("o_y")), float(g("o_z"))),
            "dir": (float(g("d_x")), float(g("d_y")), float(g("d_z"))),
            "mask": int(g("mask")),
            "radius": float(g("radius")),
            "hit_path": g("hit_path"),
            "hit_is_target": g("hit_is_target") == "True",
            "verdict": g("verdict") == "True",
        })
    return probes


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", required=True, help="BepInEx LogOutput.log containing LOS_PROBE lines")
    ap.add_argument("--limit", type=int, default=0, help="cap number of probes (0=all)")
    args = ap.parse_args()

    probes = parse_probes(args.log)
    if args.limit:
        probes = probes[:args.limit]
    if not probes:
        raise SystemExit("No LOS_PROBE lines found — run a sweep with the probe build first.")

    # Group by mask so the collider set matches each probe's raycast layers. In practice
    # one mask per run, but be safe.
    by_mask = {}
    for p in probes:
        by_mask.setdefault(p["mask"], []).append(p)

    total = agree = 0
    occluder_disagree = 0  # offline says blocked, game says clear (mesh-AABB over-block — expected)
    leak_disagree = 0      # offline says clear, game says blocked (DANGEROUS: missing occluder)
    samples = []
    for mask, plist in by_mask.items():
        excl = mask_excluded_layers(mask)
        excl.discard(None)
        colliders = load_colliders(excl)
        for p in plist:
            total += 1
            ro, rd = p["origin"], p["dir"]
            # Mirror the game EXACTLY (AccessibilityWatcher.HasInteractionLineOfSightToTarget):
            # it casts Physics.Raycast to float.PositiveInfinity, takes the FIRST collider hit,
            # and LOS is clear iff that first hit is the target AND its distance is within the
            # object's InteractionRadius. The cast is NOT capped at the radius — an occluder
            # BEYOND the radius (e.g. the bathroom toilet-paper shelf at 9.59m vs a 7.5m radius)
            # still makes first-hit != target, so the game blocks. Capping the offline cast at
            # radius+0.5 missed those occluders and leaked false "clear" verdicts.
            t, c = first_hit(ro, rd, colliders, max_dist=math.inf)
            if c is None:
                # Nothing in the way at all → the game's raycast missed → not clear (didHit=False
                # ⇒ result False), but also no occluder. Treat as clear only if the game also saw
                # no hit; otherwise this is a genuine missing-occluder leak surfaced below.
                offline_clear = True
            else:
                first_is_target = _path_is_target(c.path, p["hit_path"]) and p["hit_is_target"]
                # Game's range gate: dist < radius, distance to closest bounds point. Our t is
                # the ray entry distance, a close proxy for ClosestPointOnBounds for parity.
                offline_clear = first_is_target and t < p["radius"]
            game_clear = p["verdict"]
            if offline_clear == game_clear:
                agree += 1
            elif game_clear and not offline_clear:
                occluder_disagree += 1
                if len(samples) < 20:
                    samples.append(("OFFLINE-BLOCKS game-clears", p["target"],
                                    c.path if c else None, c.kind if c else None))
            else:
                leak_disagree += 1
                if len(samples) < 20:
                    samples.append(("OFFLINE-LEAKS game-blocks", p["target"],
                                    c.path if c else None, c.kind if c else None))

    print(f"probes: {total}")
    print(f"agree:  {agree}  ({100.0*agree/total:.1f}%)")
    print(f"offline-blocks / game-clears: {occluder_disagree}  (mesh-AABB over-block — expected, safe)")
    print(f"offline-leaks  / game-blocks: {leak_disagree}  (MISSING OCCLUDER — dangerous, investigate)")
    print()
    for s in samples:
        print("  ", s)


def _path_is_target(collider_path, hit_path):
    """The offline collider Path vs the game's logged hit transform path. They may differ in
    exact prefix formatting, so match on suffix containment in either direction."""
    if not collider_path or not hit_path:
        return False
    a = collider_path.replace("===SCENE===/", "")
    b = hit_path.replace("===SCENE===/", "")
    return a.endswith(b) or b.endswith(a) or a.split("/")[-1] == b.split("/")[-1]


if __name__ == "__main__":
    main()
