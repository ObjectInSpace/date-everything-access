"""Validated roster->collider resolution check for no_collider objects.

For each object the offline sweep reported as `no_collider` (its roster Path resolves to no
collider — typically because the path points at a render mesh, not the collider node), find the
NEAREST kept (LOS-usable) collider that shares the object's furniture unit and measure how far
the object's transform position is from that collider's bounds.

Why a geometry gate: _shares_furniture_unit can relate an object to a NEIGHBOUR's collider under
the same unit ancestor (the Floors_UpperHall -> SM_Floor_Gym mis-relate trap). A unit-mate
collider is only THIS object's if the object's position sits IN it.

EXACT shape, not AABB. AABB over-claims an object's extent (a big sink/table AABB swallows a small
prop's position and yields a false 0.00m gap), which is why the bake itself moved off AABB/slice
sampling to rasterization. So acceptance uses los_geometry.point_inside_collider — the EXACT
oriented test (OBB/sphere/capsule) — never the AABB box:

    object inside collider's exact volume (obb/sphere/capsule) -> RESOLVE (safe to re-point).
    mesh/aabb-only collider, or object NOT inside any unit-mate -> REVIEW (AABB can't be trusted;
                                                                  manual / needs exact-mesh test).
    no unit-mate collider at all                               -> PRUNE (true render-only / decor).

The exact test is conservative the SAFE way: it never accepts on a loose box, so a mis-relate to a
neighbour's big mesh AABB lands in REVIEW, not RESOLVE.

Reads the canonical objects/ manifest's no_collider set + the fixture roster + the blocker
export. Writes nothing; prints the three lists. See
[[feedback_no_collider_is_missing_data_not_pass]] + [[project_navigation_sweep_2026_06_16_timeout]].
"""
from __future__ import annotations

import json
import math
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
import plan_object_route as P  # noqa: E402
import los_geometry as L  # noqa: E402

MANIFEST = ROOT.parent / "artifacts" / "navigation" / "sweep" / "objects" / "sweep_manifest.json"
# Tolerance for the exact-shape inside test: the object's transform may sit just OUTSIDE its own
# thin collider (a faucet handle pivot slightly proud of the tap collider). We expand the exact
# shape by this margin before the inside test — small enough not to swallow a neighbour.
INSIDE_MARGIN_M = 0.0


def _inside_exact(collider, px, py, pz):
    """True if (px,py,pz) is inside the collider's EXACT oriented volume (OBB/sphere/capsule),
    optionally grown by INSIDE_MARGIN_M. Returns False for mesh/aabb kinds (no exact volume —
    those go to REVIEW rather than being trusted on a loose box)."""
    p = (px, py, pz)
    if L.point_inside_collider(p, collider):
        return True
    if INSIDE_MARGIN_M <= 0:
        return False
    # Grow the exact shape by the margin for the kinds that support it.
    k = collider.kind
    if k == "sphere":
        return math.dist(p, collider.center) <= collider.radius + INSIDE_MARGIN_M
    if k == "capsule":
        # distance to axis segment <= radius+margin
        from los_geometry import vsub, vlen, vmul, vadd, vdot
        axis = vsub(collider.p1, collider.p0)
        alen = vlen(axis)
        if alen <= 1e-9:
            return vlen(vsub(p, collider.p0)) <= collider.radius + INSIDE_MARGIN_M
        ax = vmul(axis, 1.0 / alen)
        s = max(0.0, min(alen, vdot(vsub(p, collider.p0), ax)))
        closest = vadd(collider.p0, vmul(ax, s))
        return vlen(vsub(p, closest)) <= collider.radius + INSIDE_MARGIN_M
    if k == "obb":
        from los_geometry import quat_rotate, quat_conj, vsub
        lo = quat_rotate(quat_conj(collider.q), vsub(p, collider.center))
        h = collider.half
        m = INSIDE_MARGIN_M
        return abs(lo[0]) <= h[0] + m and abs(lo[1]) <= h[1] + m and abs(lo[2]) <= h[2] + m
    return False


def main():
    P.resolve_target_collider_for_path("===SCENE===/x")  # build the LOS caches
    kept = P._LOS_BY_PATH  # {path: Collider}, LOS-usable (game-mask) colliders
    roster = {e.get("name"): e for e in P.load_fixture_roster()}
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8-sig"))

    # The no_collider set = pass-verdict entries whose collider doesn't resolve.
    targets = []
    for e in manifest["entries"]:
        if e.get("offline_verdict") != "pass":
            continue
        nm = e.get("name")
        re = roster.get(nm)
        if not re:
            continue
        item = P._roster_entry_as_item(re)
        try:
            col = P.resolve_target_collider_for_path(item.get("Path"))
        except Exception:
            col = None
        if col is None or isinstance(col, str):
            targets.append((nm, item))

    resolve, review, prune = [], [], []
    for nm, item in targets:
        path = item.get("Path") or ""
        pos = item.get("Position") or {}
        px, py, pz = pos.get("x"), pos.get("y"), pos.get("z")
        # candidate kept colliders sharing the furniture unit
        mates = [(p, c) for p, c in kept.items() if L._shares_furniture_unit(p, path)]
        if not mates:
            prune.append(nm)
            continue
        if px is None:
            review.append((nm, "no-pos", len(mates)))
            continue
        # EXACT containment: object position inside a unit-mate's true oriented volume.
        hits = [(p, c) for p, c in mates if _inside_exact(c, px, py, pz)]
        if hits:
            # Prefer the smallest collider that contains it (tightest = most specific = least
            # likely to be a big neighbour that happens to enclose the point).
            def _vol(c):
                if c.kind == "sphere":
                    return c.radius ** 3
                if c.kind == "obb":
                    return c.half[0] * c.half[1] * c.half[2]
                if c.kind == "capsule":
                    return c.radius ** 2 * (math.dist(c.p0, c.p1) + c.radius)
                return float("inf")
            p, c = min(hits, key=lambda pc: _vol(pc[1]))
            resolve.append((nm, c.kind, p.rsplit("/", 1)[-1]))
        else:
            review.append((nm, "outside", len(mates)))

    print(f"no_collider targets analysed: {len(targets)}  (exact-shape inside test, "
          f"margin={INSIDE_MARGIN_M}m)")
    print(f"  RESOLVE (object INSIDE a unit-mate's exact volume): {len(resolve)}")
    print(f"  REVIEW  (only mesh/aabb mate, or outside all):      {len(review)}")
    print(f"  PRUNE   (no collider in unit; true render-only):    {len(prune)}")

    print("\n=== RESOLVE (name  kind  collider_leaf) ===")
    for nm, kind, p in sorted(resolve):
        print(f"  {nm:<34} {kind:<8} {p}")

    print("\n=== REVIEW (name  reason  n_unit_mates) ===")
    for nm, reason, n in sorted(review):
        print(f"  {nm:<34} {reason:<8} mates={n}")

    print("\n=== PRUNE (true render-only / decor) ===")
    fams = Counter()
    import re as _re
    for nm in prune:
        fams[_re.sub(r"[_0-9]+$", "", nm)] += 1
    for nm in sorted(prune):
        print(f"  {nm}")
    print("  families:", dict(fams.most_common(12)))


if __name__ == "__main__":
    main()
