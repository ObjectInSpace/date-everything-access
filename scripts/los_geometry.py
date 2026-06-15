"""Shared offline line-of-sight geometry: collider model, ray-vs-shape tests,
ClosestPointOnBounds, target-collider resolution, and the per-cell interaction
LOS test.

This is the parity-proven raycaster (validate_los.py reached 71/71 against the
in-game LOS_PROBE rays — see [[project_navigation_offline_los_validator_2026_06_13]]).
Both validate_los.py (the in-game-ray parity gate) and plan_object_route.py (the
planner's synthetic-eye goal filter) import from here so there is ONE collider set
and ONE raycaster, never two that can drift.

Two consumers, two DIFFERENT rays — intentional, don't conflate:
  - validate_los.py replays the GAME's live-camera interaction ray (origin = camera
    pulled back 0.25m, dir = camera forward) and casts to +inf, because the game's
    Physics.Raycast is uncapped and blocks if the first hit isn't the target even
    when that occluder sits beyond the InteractionRadius.
  - the planner's goal filter (cell_has_los_to_target, below) is the SYNTHETIC eye:
    a hypothetical player standing on a candidate goal cell, eye at FloorY+1.6m,
    looking at the target collider's nearest bounds point, casting only to that
    surface (dist + EYE_SURFACE_EPSILON). It answers "is anything between this
    standpoint and the target", which is the right question for goal selection.
    EXACT mirror of SimpleNavPlanner.CellHasLineOfSightToTarget (eye 1.6, aim
    ClosestPointOnBounds, rim 0.9, cast dist+0.05).

Fidelity (Stage 1, same as validate_los): Box/Sphere/Capsule colliders exact
(OBB/sphere/capsule ray tests); Mesh colliders as AABB (conservative — over-blocks,
never invents LOS). The parity gate confirmed this is sufficient for the rays we cast.
"""
from __future__ import annotations

import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BLOCKERS = ROOT / "artifacts/navigation/thirdpersongreybox-blockers.json"

# Unity built-in layer that Physics.Raycast ignores by default. Used as the fallback
# exclusion when no per-run mask is supplied.
IGNORE_RAYCAST_LAYER = 2

# Synthetic-eye LOS constants — MIRROR SimpleNavPlanner.cs exactly:
#   InteractionEyeHeightM (1.6): standing eye height where the camera sits.
#   InteractionRimFractionM (0.9): a standpoint's sightline must be within this
#     fraction of the interaction radius (not edge-grazing out at the rim).
# Keep these in lockstep with the C# constants of the same name.
INTERACTION_EYE_HEIGHT_M = 1.6
INTERACTION_RIM_FRACTION = 0.9
# Cast slightly past the aim point so a hit exactly on the target surface still
# registers (C# uses dist + 0.05f).
EYE_SURFACE_EPSILON = 0.05

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


def build_collider(b):
    """Build one Collider from a blocker-export record, or None if it can't occlude
    (trigger / disabled / inactive / no bounds). Shared by load_colliders and target
    resolution so a target's own collider is modeled identically to an occluder."""
    if b.get("IsTrigger"):
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
        # shape). See docs/los-mesh-triangle-export-scope.md.
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


# ---------- per-cell synthetic-eye interaction LOS ----------

def _is_operable_container_door(c, container_door_paths):
    """True if collider c belongs to an operable CONTAINER door (cupboard/fridge/breaker)
    the LOS rescue may see PAST. Identified by exact transform PATH membership in
    `container_door_paths` (the bake's container_operable_only door colliders), NOT by
    loose AABB geometry — so we can never false-clear past a door the real mesh wouldn't.
    Mirror of SimpleNavPlanner.OperableContainerDoorName (which uses the live Door/SlidingDoor
    component instead). See [[project_navigation_container_doors_baked_2026_06_14]]."""
    if not container_door_paths:
        return False
    p = getattr(c, "path", None)
    if not p:
        return False
    if p in container_door_paths:
        return True
    # The door's collider may sit on a child of the named door transform (panel nested
    # under the door root), so accept a descendant path too — mirror of C#
    # GetComponentInParent<Door>() walking up from the hit collider.
    for dp in container_door_paths:
        if p == dp or p.startswith(dp + "/") or dp.startswith(p + "/"):
            return True
    return False


def cell_has_los_to_target(eye_xz, floor_y, target_collider, radius_m, occluders,
                           container_door_paths=None, passed_container_doors=None):
    """Mirror of SimpleNavPlanner.CellHasLineOfSightToTarget. True if a player standing
    on this cell has a SOLID interaction line to the target:
      (1) eye at (cell XZ, floor_y + 1.6m) aimed at the target's nearest bounds point;
      (2) that sightline is within radius * 0.9 (not edge-grazing the rim);
      (3) the first occluder the ray hits IS the target collider (or its hierarchy) —
          i.e. nothing blocks the line to the surface.
    `occluders` is the full collider set (the target's own collider may be among them;
    a self/child/parent hit counts as clear). Conservative: returns False on degenerate
    input, never inventing LOS.

    CONTAINER-DOOR RESCUE (mirror of the C# rescue): when `passed_container_doors` is a
    set, a collider that is an operable container door (path in `container_door_paths`) is
    treated as TRANSPARENT — the ray sees past it — and its path is recorded so the caller
    can tag it to be opened on arrival. With `passed_container_doors=None` the strict
    first-hit rule applies (normal goal selection)."""
    if target_collider is None:
        return False
    eye = (eye_xz[0], floor_y + INTERACTION_EYE_HEIGHT_M, eye_xz[1])
    aim = closest_point_on_bounds(target_collider, eye)
    dir3 = vsub(aim, eye)
    dist = vlen(dir3)
    if dist <= 0.0001:
        return True  # standing essentially on the target — nothing to occlude
    # Rim gate, measured HORIZONTALLY (XZ only): "not standing too far ACROSS the floor from
    # the object." A ceiling light / high-mounted prop has a large UNAVOIDABLE vertical gap;
    # counting it in the rim distance rejected every cell (look + dateviator beam still count
    # as interaction). The full-3D sightline below is still used for the occlusion raycast.
    # Mirror of SimpleNavPlanner.CellHasLineOfSightToTarget.
    horiz_dist = math.hypot(aim[0] - eye[0], aim[2] - eye[2])
    if radius_m > 0.0 and horiz_dist > radius_m * INTERACTION_RIM_FRACTION:
        return False
    rd = vmul(dir3, 1.0 / dist)
    max_dist = dist + EYE_SURFACE_EPSILON
    # Nearest occluder on the SHORT eye->surface span, with the Stage-1 loose-AABB rule:
    # a mesh-AABB whose hit is at t~0 (eye inside its loose bounds) is UNTRUSTWORTHY here —
    # the eye standing this close to the target is typically skimming a thin wall whose
    # real triangles the ray passes through (the in-game synthetic eye uses exact meshes
    # and clears it). We skip such hits rather than over-block. Convex box/sphere/capsule
    # origin-inside hits are still honored via point_inside_collider in the broad-phase.
    # See [[project_navigation_offline_los_validator_2026_06_13]] (Stage-2 boundary).
    best_t, best_c = None, None
    for c in occluders:
        if _is_structural_slab(c):
            continue  # floor/ceiling shell — look up/down past it (mirror of C# IsStructuralSlab)
        ta = ray_aabb(eye, rd, c.aabb_lo, c.aabb_hi)
        if ta is None or ta > max_dist:
            continue
        if ta <= EPS and point_inside_collider(eye, c):
            continue  # convex collider the eye is inside — Unity wouldn't hit it
        t = ray_collider(eye, rd, c)
        if t is None or t > max_dist:
            continue
        if t <= EPS and c.kind == "aabb":
            continue  # loose mesh-AABB enveloping the eye — untrustworthy at Stage 1
        if passed_container_doors is not None and _is_operable_container_door(c, container_door_paths):
            # See past an operable container door; record it so the caller opens it on
            # arrival. Skip only if it isn't the target itself (a container that IS the
            # target must still be hit). Mirror of the C# rescue's pass-through.
            if _is_self_or_relative(c.path, target_collider.path) is None \
                    and _is_self_or_relative(target_collider.path, c.path) is None:
                passed_container_doors.add(c.path)
                continue
        if best_t is None or t < best_t:
            best_t, best_c = t, c
    if best_c is None:
        return True  # nothing between eye and target surface — clear line
    # Clear iff the first thing hit is the target collider or its hierarchy.
    return _is_self_or_relative(best_c.path, target_collider.path) is not None \
        or _is_self_or_relative(target_collider.path, best_c.path) is not None
