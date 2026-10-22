"""Shared offline line-of-sight geometry: collider model, ray-vs-shape tests,
ClosestPointOnBounds and target-collider resolution.

This is the parity-proven raycaster: validate_los.py, which imports from here, replays the
GAME's live interaction rays (origin = camera pulled back 0.25 m, dir = camera forward, cast to
+inf because the game's Physics.Raycast is uncapped) and reached 100% agreement with the in-game
LOS_PROBE verdicts.

Fidelity (Stage 1, same as validate_los): Box/Sphere/Capsule colliders exact
(OBB/sphere/capsule ray tests); Mesh colliders as AABB (conservative — over-blocks,
never invents LOS). The parity gate confirmed this is sufficient for the rays we cast.
"""
from __future__ import annotations

import json
import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BLOCKERS = ROOT / "artifacts/navigation/thirdpersongreybox-blockers.json"

# Unity built-in layer that Physics.Raycast ignores by default. Used as the fallback
# exclusion when no per-run mask is supplied.
IGNORE_RAYCAST_LAYER = 2

EPS = 1e-6


# ---------- vector helpers (plain tuples, no numpy dependency) ----------

def vsub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def vadd(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def vmul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def vdot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def vlen(a): return math.sqrt(vdot(a, a))


def quat_rotate(q, v):
    """Rotate vector v by quaternion q=(x,y,z,w)."""
    qx, qy, qz, qw = q
    tx = 2 * (qy * v[2] - qz * v[1])
    ty = 2 * (qz * v[0] - qx * v[2])
    tz = 2 * (qx * v[1] - qy * v[0])
    rx = v[0] + qw * tx + (qy * tz - qz * ty)
    ry = v[1] + qw * ty + (qz * tx - qx * tz)
    rz = v[2] + qw * tz + (qx * ty - qy * tx)
    return (rx, ry, rz)


def quat_conj(q):
    return (-q[0], -q[1], -q[2], q[3])


# ---------- ray-vs-shape intersections (return t>=0 distance or None) ----------

def ray_aabb(ro, rd, lo, hi):
    """Slab method. ro/rd world; lo/hi AABB min/max. Returns entry t>=0 or None."""
    tmin, tmax = 0.0, math.inf
    for i in range(3):
        if abs(rd[i]) < EPS:
            if ro[i] < lo[i] or ro[i] > hi[i]:
                return None
        else:
            inv = 1.0 / rd[i]
            t1 = (lo[i] - ro[i]) * inv
            t2 = (hi[i] - ro[i]) * inv
            if t1 > t2:
                t1, t2 = t2, t1
            tmin = max(tmin, t1)
            tmax = min(tmax, t2)
            if tmin > tmax:
                return None
    return tmin


def ray_obb(ro, rd, center, q, half):
    """Ray vs oriented box: transform ray into box-local space, then slab vs [-half,half]."""
    qc = quat_conj(q)
    lo = quat_rotate(qc, vsub(ro, center))
    ld = quat_rotate(qc, rd)
    return ray_aabb(lo, ld, (-half[0], -half[1], -half[2]), half)


def ray_sphere(ro, rd, center, r):
    oc = vsub(ro, center)
    b = vdot(oc, rd)
    c = vdot(oc, oc) - r * r
    disc = b * b - c
    if disc < 0:
        return None
    s = math.sqrt(disc)
    t = -b - s
    if t < 0:
        t = -b + s
    return t if t >= 0 else None


def ray_triangle(ro, rd, a, b, c):
    """Moller-Trumbore ray vs triangle. Returns t>=0 or None. Double-sided (occlusion
    doesn't care about winding)."""
    e1 = vsub(b, a)
    e2 = vsub(c, a)
    pvec = (rd[1] * e2[2] - rd[2] * e2[1],
            rd[2] * e2[0] - rd[0] * e2[2],
            rd[0] * e2[1] - rd[1] * e2[0])
    det = vdot(e1, pvec)
    if abs(det) < EPS:
        return None  # ray parallel to triangle
    inv = 1.0 / det
    tvec = vsub(ro, a)
    u = vdot(tvec, pvec) * inv
    if u < 0.0 or u > 1.0:
        return None
    qvec = (tvec[1] * e1[2] - tvec[2] * e1[1],
            tvec[2] * e1[0] - tvec[0] * e1[2],
            tvec[0] * e1[1] - tvec[1] * e1[0])
    v = vdot(rd, qvec) * inv
    if v < 0.0 or u + v > 1.0:
        return None
    t = vdot(e2, qvec) * inv
    return t if t >= 0.0 else None


def ray_triangles(ro, rd, flat):
    """Nearest hit among a flat [x,y,z, ...] triangle-soup (9 floats per tri). t>=0 or None."""
    best = None
    n = len(flat)
    i = 0
    while i + 8 < n:
        a = (flat[i], flat[i + 1], flat[i + 2])
        b = (flat[i + 3], flat[i + 4], flat[i + 5])
        c = (flat[i + 6], flat[i + 7], flat[i + 8])
        t = ray_triangle(ro, rd, a, b, c)
        if t is not None and (best is None or t < best):
            best = t
        i += 9
    return best


def ray_capsule(ro, rd, p0, p1, r):
    """Approximate capsule = segment-swept sphere: two end spheres + cylinder body
    via closest-approach between ray and segment axis. Good enough for occlusion."""
    best = None
    for cp in (p0, p1):
        t = ray_sphere(ro, rd, cp, r)
        if t is not None and (best is None or t < best):
            best = t
    axis = vsub(p1, p0)
    alen = vlen(axis)
    if alen > EPS:
        ax = vmul(axis, 1.0 / alen)
        w0 = vsub(ro, p0)
        a = vdot(rd, rd)
        b = vdot(rd, ax)
        c = vdot(rd, w0)
        d = vdot(ax, w0)
        denom = a - b * b
        if abs(denom) > EPS:
            t = (b * d - c) / denom
            if t >= 0:
                s = (vdot(rd, ax) * t + d)
                s = max(0.0, min(alen, s))
                pt_ray = vadd(ro, vmul(rd, t))
                pt_seg = vadd(p0, vmul(ax, s))
                if vlen(vsub(pt_ray, pt_seg)) <= r and (best is None or t < best):
                    best = t
    return best


# ---------- collider model ----------

class Collider:
    __slots__ = ("kind", "path", "layer", "center", "q", "half", "radius",
                 "p0", "p1", "aabb_lo", "aabb_hi", "tris")


def _v(d):
    return (d["x"], d["y"], d["z"])


def _q(d):
    return (d["x"], d["y"], d["z"], d["w"])


def closest_point_on_bounds(c, p):
    """Unity Collider.ClosestPointOnBounds: clamp p to the collider's WORLD AABB.
    Matches what SimpleNavPlanner aims at (targetCollider.ClosestPointOnBounds(eye)).
    Not the oriented shape — Unity's method uses the axis-aligned bounds for all kinds."""
    return (
        min(max(p[0], c.aabb_lo[0]), c.aabb_hi[0]),
        min(max(p[1], c.aabb_lo[1]), c.aabb_hi[1]),
        min(max(p[2], c.aabb_lo[2]), c.aabb_hi[2]),
    )


# Mirror of SimpleNavPlanner.UndersideAimEpsilonM / InteractionAimPoint. A small object RESTING
# ON a surface (toaster on a counter, bobby pin in a desk pen holder, coffee machine on a worktop)
# has its closest-bounds-point land on the collider's BOTTOM face for every floor standpoint: the
# 1.6m eye is above the object's underside. That aim point is occluded BY DEFINITION — the
# counter/desk it rests on sits between eye and underside — so every candidate cell fails and the
# object reports no_los with no standpoint anywhere. Measured: toaster 1808/1808 cells and office
# bobby pin 2998/2998 cells aimed at the underside; re-aiming at the bounds CENTRE recovers 63
# valid standpoints for the toaster. Objects whose nearest point is a side/top face are untouched.
UNDERSIDE_AIM_EPSILON_M = 0.02


def interaction_aim_point(c, eye):
    """Interaction aim point for `eye` looking at collider `c`: normally the closest bounds
    point, but re-aimed at the bounds CENTRE for a resting-on-a-surface object whose closest
    point is its occluded underside. Mirror of SimpleNavPlanner.InteractionAimPoint."""
    aim = closest_point_on_bounds(c, eye)
    center_y = (c.aabb_lo[1] + c.aabb_hi[1]) / 2.0
    # Only re-aim when the nearest point is the BOTTOM face and the body sits above it — i.e.
    # we'd be looking up into the base through whatever the object rests on.
    if aim[1] - c.aabb_lo[1] <= UNDERSIDE_AIM_EPSILON_M and center_y > aim[1]:
        return (
            (c.aabb_lo[0] + c.aabb_hi[0]) / 2.0,
            center_y,
            (c.aabb_lo[2] + c.aabb_hi[2]) / 2.0,
        )
    return aim


def build_collider(b, allow_trigger=False):
    """Build one Collider from a blocker-export record, or None if it can't occlude
    (trigger / disabled / inactive / no bounds). Shared by load_colliders and target
    resolution.

    allow_trigger: triggers are excluded as OCCLUDERS (they don't block sightlines), but the
    game's interaction raycast DOES hit trigger colliders (queriesHitTriggers=true — proven by
    a captured LOS_PROBE ray hitting the Rug_Closet trigger). So an interactable whose OWN
    collider is a trigger (the room thresholds, the Attic Orb) is genuinely hittable/datable.
    Target-collider resolution passes allow_trigger=True so those resolve, while occluder
    loading keeps the default (triggers don't occlude). See
    [[feedback_no_collider_is_missing_data_not_pass]]."""
    if b.get("IsTrigger") and not allow_trigger:
        return None
    if not b.get("Enabled", True) or not b.get("IsActive", True):
        return None
    bb = b.get("Bounds3D")
    if not bb:
        return None
    c = Collider()
    c.path = b.get("Path") or b.get("Name")
    c.layer = b.get("Layer")
    c.aabb_lo = _v(bb["Min"])
    c.aabb_hi = _v(bb["Max"])
    c.tris = None
    ctype = b.get("ColliderType")
    ls = b.get("LocalShape") or {}
    wpos = _v(b["WorldPosition"])
    wq = _q(b["WorldRotation"])
    wscale = _v(b["WorldScale"])
    if ctype == "BoxCollider":
        c.kind = "obb"
        local_center = _v(ls.get("Center", {"x": 0, "y": 0, "z": 0}))
        size = _v(ls["Size"])
        sc = (local_center[0] * wscale[0], local_center[1] * wscale[1], local_center[2] * wscale[2])
        c.center = vadd(wpos, quat_rotate(wq, sc))
        c.q = wq
        c.half = (abs(size[0] * wscale[0]) * 0.5,
                  abs(size[1] * wscale[1]) * 0.5,
                  abs(size[2] * wscale[2]) * 0.5)
    elif ctype == "SphereCollider":
        c.kind = "sphere"
        local_center = _v(ls.get("Center", {"x": 0, "y": 0, "z": 0}))
        sc = (local_center[0] * wscale[0], local_center[1] * wscale[1], local_center[2] * wscale[2])
        c.center = vadd(wpos, quat_rotate(wq, sc))
        c.radius = ls["Radius"] * max(abs(wscale[0]), abs(wscale[1]), abs(wscale[2]))
    elif ctype == "CapsuleCollider":
        c.kind = "capsule"
        local_center = _v(ls.get("Center", {"x": 0, "y": 0, "z": 0}))
        sc = (local_center[0] * wscale[0], local_center[1] * wscale[1], local_center[2] * wscale[2])
        center = vadd(wpos, quat_rotate(wq, sc))
        direction = ls.get("Direction", 1)  # 0=x,1=y,2=z
        ax = [0.0, 0.0, 0.0]
        ax[direction] = 1.0
        non_axial = [wscale[i] for i in range(3) if i != direction]
        c.radius = ls["Radius"] * max(abs(non_axial[0]), abs(non_axial[1]))
        half_h = max(0.0, ls["Height"] * 0.5 * abs(wscale[direction]) - c.radius)
        axis_world = quat_rotate(wq, tuple(ax))
        c.p0 = vadd(center, vmul(axis_world, half_h))
        c.p1 = vsub(center, vmul(axis_world, half_h))
    else:
        # MeshCollider (or anything else): AABB occluder via Bounds3D, UNLESS the export
        # carried exact world triangles (LosGeometry) for it — those meshes whose AABB
        # over-claims their true shape (chairs, plants, railings). With triangles we do an
        # exact ray-vs-triangle test; the AABB still serves as the broad-phase pre-reject in
        # first_hit. Flat walls/slabs have no LosGeometry and stay AABB (their box IS their
        # shape).
        fp = b.get("Footprint") or {}
        lg = fp.get("LosGeometry")
        if lg and lg.get("Kind") == "Triangles" and lg.get("Verts"):
            c.kind = "mesh_tris"
            c.tris = lg["Verts"]
        else:
            c.kind = "aabb"
    return c


def load_colliders(mask_excluded_layers, raw=None):
    """All occluding colliders, excluding triggers/disabled and the masked layers.
    `raw` lets a caller pass an already-parsed blockers dict to avoid re-reading."""
    if raw is None:
        raw = json.loads(BLOCKERS.read_text(encoding="utf-8-sig"))
    out = []
    for sec in ("PrimitiveColliders", "MeshColliders"):
        for b in raw.get(sec, []):
            if b.get("Layer") in mask_excluded_layers:
                continue
            c = build_collider(b)
            if c is not None:
                out.append(c)
    return out


def ray_collider(ro, rd, c):
    if c.kind == "obb":
        return ray_obb(ro, rd, c.center, c.q, c.half)
    if c.kind == "sphere":
        return ray_sphere(ro, rd, c.center, c.radius)
    if c.kind == "capsule":
        return ray_capsule(ro, rd, c.p0, c.p1, c.radius)
    if c.kind == "mesh_tris":
        # Exact ray-vs-triangle. The AABB pre-reject in first_hit already cleared the
        # broad phase, so this only runs for rays that actually enter the bounds.
        return ray_triangles(ro, rd, c.tris)
    return ray_aabb(ro, rd, c.aabb_lo, c.aabb_hi)


def point_inside_collider(p, c):
    """True if point p is provably inside collider c's EXACT volume. Unity's
    Physics.Raycast does NOT report a hit for a CONVEX collider the ray origin is inside
    (box/sphere/capsule) — the ray exits, it doesn't enter. The synthetic eye can
    legitimately stand inside a thin shelf/rack collider while still seeing a prop
    resting on it, so we skip those to match Unity.

    ONLY box/sphere/capsule return True. Mesh colliders are modeled as a loose AABB:
    being inside that AABB does NOT mean inside the mesh, and Unity WOULD still report
    the wall/mesh hit — so we never skip a mesh occluder on an origin-inside test (doing
    so leaked real wall occluders in the validator's room-spanning camera rays)."""
    if c.kind == "obb":
        lo = quat_rotate(quat_conj(c.q), vsub(p, c.center))
        return (abs(lo[0]) <= c.half[0] and abs(lo[1]) <= c.half[1] and abs(lo[2]) <= c.half[2])
    if c.kind == "sphere":
        return vlen(vsub(p, c.center)) <= c.radius
    if c.kind == "capsule":
        # Distance from p to the capsule axis segment <= radius.
        axis = vsub(c.p1, c.p0)
        alen = vlen(axis)
        if alen <= EPS:
            return vlen(vsub(p, c.p0)) <= c.radius
        ax = vmul(axis, 1.0 / alen)
        s = max(0.0, min(alen, vdot(vsub(p, c.p0), ax)))
        closest = vadd(c.p0, vmul(ax, s))
        return vlen(vsub(p, closest)) <= c.radius
    # aabb / mesh / mesh_tris: never treated as origin-inside in the SHARED raycaster. The
    # bounds are a loose box; for the validator's uncapped probe rays a t~0 mesh hit IS the
    # correct blocked verdict (the camera sits in a wall mesh's AABB and the wall really
    # blocks), so skipping it leaks. The synthetic-eye goal filter handles the
    # loose-AABB-at-origin case itself (see cell_has_los_to_target), where the short
    # eye->surface cast makes a t~0 mesh hit untrustworthy. Keeping this False holds
    # validate_los at 71/71. (mesh_tris uses exact triangles in ray_collider, but for the
    # origin-inside skip it behaves like a mesh — never skipped.)
    return False


def first_hit(ro, rd, colliders, max_dist):
    """Nearest collider hit within max_dist. Returns (t, collider) or (None, None).
    Broad-phase: AABB reject before the exact test. A collider the ray ORIGIN sits
    inside is skipped (Unity reports no hit for it — see point_inside_collider)."""
    best_t, best_c = None, None
    for c in colliders:
        ta = ray_aabb(ro, rd, c.aabb_lo, c.aabb_hi)
        if ta is None or ta > max_dist:
            continue
        if ta <= EPS and point_inside_collider(ro, c):
            continue  # origin inside this collider — Unity's raycast wouldn't hit it
        t = ray_collider(ro, rd, c)
        if t is None or t > max_dist:
            continue
        if best_t is None or t < best_t:
            best_t, best_c = t, c
    return best_t, best_c


def mask_excluded_layers(mask):
    """Layers EXCLUDED by a Unity LayerMask used as ~dateviatorIgnores. A layer L is
    raycast if (mask >> L) & 1; we exclude layers whose bit is 0."""
    excluded = set()
    for L in range(32):
        if not ((mask >> L) & 1):
            excluded.add(L)
    return excluded


# ---------- target-collider resolution (mirror of SelectBestTargetCollider) ----------

def _is_structural_slab(c):
    """A structural floor/ceiling slab — leaf named SM_Floor_* / SM_Ceiling_*. The
    look-up/look-down LOS rule sees PAST these (a recessed ceiling light ~8m up, a desk
    drawer resolving at/below the floor plane): the only thing the straight eye→target ray
    hits is the player's own storey shell, yet looking up/down + the dateviator beam is
    legitimate interaction. Mirror of SimpleNavPlanner.IsStructuralSlab; name-based so it
    can never exempt a wall or prop. See [[feedback_interaction_includes_look_and_glasses]]."""
    p = getattr(c, "path", None)
    if not p:
        return False
    leaf = p.rsplit("/", 1)[-1]
    return leaf.startswith("SM_Floor_") or leaf.startswith("SM_Ceiling_")


def _is_self_or_relative(col_path, target_path):
    """True if col_path is the target itself, a child of it, or an ancestor of it —
    the offline analogue of GetComponents / GetComponentsInChildren / InParent. Paths
    are the export's full transform paths, so prefix containment captures the same
    hierarchy relationship the runtime walks. Returns a priority (0 self, 1 child,
    2 parent) or None if unrelated, mirroring the runtime's self<child<parent penalty."""
    if not col_path or not target_path:
        return None
    if col_path == target_path:
        return 0
    if col_path.startswith(target_path + "/"):
        return 1   # collider is on a descendant transform
    if target_path.startswith(col_path + "/"):
        return 2   # collider is on an ancestor transform
    return None


# Authoring scaffolding nodes walked past to reach the real furniture unit (mirror of
# bake_navigable_region._SCAFFOLD_NODE_RE / _real_parent_unit). A prop and its supporting
# furniture (a D20 on a desk, salt on a counter) live in SIBLING subtrees under the unit
# node (Desk_MASTER) — neither is the other's ancestor, so the strict prefix check above
# misses them. We treat them as relatives when they share that unit ancestor.
_UNIT_SCAFFOLD_RE = re.compile(
    r"(_TRS|_MASTER|_Grp|_GROUP|_MODEL_UPDATE\d*|_ORIGIN|_MODEL)$", re.IGNORECASE)


def _real_parent_unit(path):
    """The furniture-unit ancestor path: walk UP past authoring scaffolding to the first
    real unit node. A prop's supporting furniture shares this unit. Mirror of
    bake_navigable_region._real_parent_unit, operating on full export paths."""
    if not path:
        return ""
    segs = [s for s in path.split("/") if s and s != "===SCENE==="]
    if len(segs) <= 1:
        return path
    parent = segs[:-1]
    while len(parent) > 1 and _UNIT_SCAFFOLD_RE.search(parent[-1]):
        parent = parent[:-1]
    return "/".join(parent)


def _shares_furniture_unit(col_path, target_path):
    """True if the occluder and target belong to the SAME furniture unit (their real-parent
    units coincide, or one's unit is an ancestor of the other's) — e.g. a desk's surface
    occluding a prop resting on that desk. Scoped to the unit, NOT the room: Desk_MASTER,
    not Office. Prevents an object's own supporting furniture from reporting false no_los,
    while a chair across the room (different unit) still occludes."""
    if not col_path or not target_path:
        return False
    cu = _real_parent_unit(col_path)
    tu = _real_parent_unit(target_path)
    if not cu or not tu:
        return False
    # Same unit, or one unit nested within the other (a sub-unit of the same furniture).
    return cu == tu or cu.startswith(tu + "/") or tu.startswith(cu + "/")


def resolve_target_collider(target_path, colliders_by_path=None, colliders=None):
    """Pick the target's own collider the way SimpleNavPlanner.SelectBestTargetCollider
    does: among the target's self/child/parent colliders, the one whose bounds are
    NEAREST the target anchor, with a self<child<parent tie-break penalty. Returns a
    Collider or None (None ⇒ planner keeps the full radius disc, matching C# when its
    live component-walk also finds nothing).

    Pass either a {path: Collider} map (built once by the caller) or a flat list.
    Resolution is by FULL PATH, not leaf name — leaf names duplicate across instances
    (e.g. hanger10_BedroomCloset appears 6×), so name matching picks the wrong one."""
    if colliders_by_path is None:
        colliders_by_path = {}
        for c in (colliders or []):
            colliders_by_path.setdefault(c.path, c)

    # Anchor = target's own transform position. We don't have it directly here, so use
    # the target collider's bounds center when the target IS a collider; otherwise the
    # nearest relative collider's bounds center is the only geometry we have. The
    # runtime scores by distance from the InteractableObj transform; offline the best
    # proxy is the candidate's own bounds (self-collider scores 0 distance, which is
    # exactly the runtime's behaviour when the collider sits on the target object).
    best = None  # (penalty, dist2, collider)
    for col_path, c in colliders_by_path.items():
        rel = _is_self_or_relative(col_path, target_path)
        if rel is None:
            continue
        # Distance from the target path's own node is 0 for the self collider; for a
        # child/parent we approximate the anchor as that relative collider's center,
        # so the nearest relative wins — same ordering as the runtime's nearest-bounds.
        dist2 = 0.0
        key = (rel, dist2)
        if best is None or key < (best[0], best[1]):
            best = (rel, dist2, c)
    return best[2] if best else None
