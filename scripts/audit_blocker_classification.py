"""Audit mesh blocker classification for furniture accidentally treated as walls.

Run from repo root:
  uv run python scripts/audit_blocker_classification.py
"""
from __future__ import annotations

import json
import re
from collections import Counter
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]
BLOCKERS_PATH = REPO / "artifacts" / "navigation" / "thirdpersongreybox-blockers.json"
# Per-cell vertical-span columns (the primitive the span bake consumes) live in the
# COLUMNS export, not the plain blockers export. The wall-like audit reads them from
# here so it reports on what the bake actually uses.
COLUMNS_PATH = REPO / "artifacts" / "navigation" / "thirdpersongreybox-blockers.COLUMNS.json"
BAKE_PATH = REPO / "artifacts" / "navigation" / "navigable_region.bake.json"
# Column cell size (XZ) of the exporter's per-cell raster — matches CELL in
# bake_navigable_region; used only to convert column cell indices to world XZ for the
# scene-point sanity filter.
COLUMN_CELL_M = 0.20

FLOORS = (("ground", -0.50), ("upper", 12.50))
# Match the SPAN bake's blocking rule (bake_navigable_region._column_blocks_floor),
# not the retired fixed-Y slice model: a column blocks a floor iff its vertical span
# [minY,maxY] overlaps the capsule volume [floorY, floorY+CAPSULE_H] AND its top is
# not a step-over sill (clears the floor by >= STEP_OVER_HEIGHT_M). Kept in sync with
# the bake's CAPSULE_H=3.20 / STEP_OVER_HEIGHT_M=0.30 so the audit reports on the
# primitive the bake actually consults (columns), not the dead slice segments.
CAPSULE_H = 3.20
STEP_OVER_HEIGHT_M = 0.30

FURNITURE_RE = re.compile(
    r"(fireplace|book|shelf|table|chair|sofa|couch|bed|cabinet|dresser|desk|"
    r"piano|drawer|fridge|washer|dryer|lamp|plant|tv|monitor|sink|toilet|"
    r"tub|shower|counter|island)",
    re.IGNORECASE,
)


def is_scene_point(x: float, z: float) -> bool:
    return abs(x) < 200.0 and abs(z) < 200.0


def record_text(record: dict) -> str:
    shape = record.get("LocalShape") or {}
    return " ".join(str(value or "") for value in (
        record.get("Path"),
        record.get("GameObjectName"),
        shape.get("MeshName"),
    ))


def mesh_name(record: dict) -> str:
    return ((record.get("LocalShape") or {}).get("MeshName") or "")


def category(record: dict) -> str:
    path = record.get("Path") or ""
    mesh = mesh_name(record)
    if "/Walls/" in path or mesh.startswith("SM_Walls_"):
        return "wall"
    if "/Doors/" in path or "Doorframe" in mesh or mesh.startswith("SM_Door") or "Door_" in mesh:
        return "door_or_frame"
    if "/Exterior/" in path or "Fence" in mesh:
        return "exterior_fence"
    if "/Art/" in path or mesh.startswith("Art_"):
        return "thin_art"
    if FURNITURE_RE.search(record_text(record)):
        return "furniture_name_match"
    return "other"


def is_structural_path(record: dict) -> bool:
    path = record.get("Path") or ""
    mesh = mesh_name(record)
    return (
        "/Floors/" in path
        or "/Walls/" in path
        or "/Ceilings/" in path
        or "/Windows/" in path
        or mesh.startswith(("SM_Floor_", "SM_Walls_", "SM_Ceiling_", "SM_Window_"))
    )


def _column_blocks_floor(col_min_y: float, col_max_y: float, floor_y: float) -> bool:
    """Mirror bake_navigable_region._column_blocks_floor: does this column's vertical
    span block the player capsule standing on `floor_y`?"""
    cap_lo = floor_y
    cap_hi = floor_y + CAPSULE_H
    if col_max_y < cap_lo or col_min_y > cap_hi:
        return False  # no overlap with the capsule
    if col_max_y - floor_y < STEP_OVER_HEIGHT_M:
        return False  # a low sill the capsule foot steps over
    return True


def furniture_blocking_records(columns_by_path: dict[str, tuple[dict, list]]) -> list[tuple[str, dict, int]]:
    """FURNITURE-named mesh records whose per-cell COLUMNS actually block the player
    band on a floor under the span bake's rule — the modern form of this audit's
    question ("is furniture being treated as an obstacle the player can't pass?").

    Replaces the retired wall-like check: that keyed off Footprint.IsWallLikeFatVictim,
    a flag the SPAN exporter no longer emits (the wall-like/fat-victim slice gates were
    deleted in the per-cell-span rewrite). We now look directly at furniture meshes
    whose columns intersect the capsule band. `columns_by_path` maps a collider Path to
    (record, flat column list [colIX, colIZ, minY, maxY])."""
    rows: list[tuple[str, dict, int]] = []
    for path, (record, columns) in columns_by_path.items():
        if not FURNITURE_RE.search(record_text(record)):
            continue
        if not columns:
            continue

        for label, floor_y in FLOORS:
            blocking = 0
            for col in columns:
                if len(col) < 4:
                    continue
                cix, ciz, cmin, cmax = col[0], col[1], col[2], col[3]
                wx = (cix + 0.5) * COLUMN_CELL_M
                wz = (ciz + 0.5) * COLUMN_CELL_M
                if not is_scene_point(wx, wz):
                    continue
                if _column_blocks_floor(cmin, cmax, floor_y):
                    blocking += 1
            if blocking:
                rows.append((label, record, blocking))
    return rows


def has_descendant_navigation_blocker(record: dict, navigation_blockers: list[dict]) -> bool:
    path = record.get("Path") or ""
    if not path:
        return False
    prefix = path.rstrip("/") + "/"
    return any((blocker.get("Path") or "").startswith(prefix) for blocker in navigation_blockers)


def print_record(prefix: str, record: dict, extra: str = "") -> None:
    footprint = record.get("Footprint") or {}
    bounds = record.get("Bounds2D") or {}
    print(
        f"{prefix} mesh={mesh_name(record)} "
        f"reason={footprint.get('RejectionReason')} "
        f"wallVictim={footprint.get('IsWallLikeFatVictim')} "
        f"area={footprint.get('AreaSqM', 0):.1f} "
        f"seg={footprint.get('SegmentCount')} "
        f"top={record.get('TopY'):.1f} bot={record.get('BottomY'):.1f} "
        f"w={bounds.get('Width', 0):.1f} d={bounds.get('Depth', 0):.1f} "
        f"{extra}path={record.get('Path')}"
    )


def _load_columns_by_path() -> dict[str, tuple[dict, list]]:
    """Map each MeshCollider Path -> (record, flat column list) from the COLUMNS export.
    Empty if the export is missing (the audit then just skips the wall-like check)."""
    if not COLUMNS_PATH.exists():
        return {}
    cdata = json.loads(COLUMNS_PATH.read_text(encoding="utf-8-sig"))
    out: dict[str, tuple[dict, list]] = {}
    for record in cdata.get("MeshColliders", []):
        path = record.get("Path")
        if not path:
            continue
        cols = (record.get("Footprint") or {}).get("Columns") or []
        out[path] = (record, cols)
    return out


def main() -> int:
    data = json.loads(BLOCKERS_PATH.read_text(encoding="utf-8"))
    mesh_records = data["MeshColliders"]
    navigation_blockers = data["NavigationBlockers"]
    columns_by_path = _load_columns_by_path()
    bake = json.loads(BAKE_PATH.read_text(encoding="utf-8")) if BAKE_PATH.exists() else None

    reasons = Counter((record.get("Footprint") or {}).get("RejectionReason") or "NavigationBlocker"
                      for record in mesh_records)
    print("Mesh collider outcome counts:")
    for reason, count in sorted(reasons.items()):
        print(f"  {reason}: {count}")

    walllike_rows = furniture_blocking_records(columns_by_path)
    print()
    print(f"Furniture meshes with player-band-blocking columns: {len(walllike_rows)}")
    for name, count in sorted(Counter(category(record) for _, record, _ in walllike_rows).items()):
        print(f"  {name}: {count}")

    if bake is not None:
        print()
        print("Current capsule-clearance bake mesh usage:")
        for floor in bake.get("floors", []):
            if "error" in floor:
                continue
            print(
                f"  {floor['label']}: "
                f"mesh_column_blockers={floor.get('mesh_column_blocker_hits')} "
                f"mesh_column_cells={floor.get('mesh_column_cells')} "
                f"primitive_blockers={floor.get('primitive_blocker_hits')} "
                f"navigable={floor.get('cells', {}).get('navigable')}"
            )

    print()
    print("Non-structural wall-segment bake candidates:")
    for floor_label, record, used_segment_count in walllike_rows:
        cat = category(record)
        if cat in {"wall", "door_or_frame", "exterior_fence"}:
            continue
        print_record(f"  floor={floor_label} cat={cat} usedSeg={used_segment_count}", record)

    print()
    print("Large house furniture meshes rejected by area cap:")
    for record in mesh_records:
        footprint = record.get("Footprint") or {}
        path = record.get("Path") or ""
        if footprint.get("RejectionReason") != "FootprintAreaExceedsMax":
            continue
        if "/House/" not in path:
            continue
        if is_structural_path(record):
            continue
        if not FURNITURE_RE.search(record_text(record)):
            continue
        covered = has_descendant_navigation_blocker(record, navigation_blockers)
        print_record("  ", record, extra=f"descendantBlocker={covered} ")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
