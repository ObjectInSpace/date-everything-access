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
BAKE_PATH = REPO / "artifacts" / "navigation" / "navigable_region.bake.json"

FLOORS = (("ground", -0.50), ("upper", 12.50))
STEP_UP_TOL = 0.25
CAPSULE_H = 2.50

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


def bake_used_walllike_records(mesh_records: list[dict]) -> list[tuple[str, dict, int]]:
    rows: list[tuple[str, dict, int]] = []
    for record in mesh_records:
        footprint = record.get("Footprint") or {}
        if not footprint.get("IsWallLikeFatVictim"):
            continue
        segments = footprint.get("Segments") or []
        if not segments:
            continue

        for label, floor_y in FLOORS:
            y_lo = floor_y - STEP_UP_TOL
            y_hi = floor_y + CAPSULE_H
            if record["TopY"] < y_lo or record["BottomY"] > y_hi:
                continue

            used_segments = [
                segment for segment in segments
                if (segment.get("PlaneY") is None or y_lo <= segment.get("PlaneY") <= y_hi)
                and (is_scene_point(segment["AX"], segment["AZ"])
                     or is_scene_point(segment["BX"], segment["BZ"]))
            ]
            if used_segments:
                rows.append((label, record, len(used_segments)))
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


def main() -> int:
    data = json.loads(BLOCKERS_PATH.read_text(encoding="utf-8"))
    mesh_records = data["MeshColliders"]
    navigation_blockers = data["NavigationBlockers"]
    bake = json.loads(BAKE_PATH.read_text(encoding="utf-8")) if BAKE_PATH.exists() else None

    reasons = Counter((record.get("Footprint") or {}).get("RejectionReason") or "NavigationBlocker"
                      for record in mesh_records)
    print("Mesh collider outcome counts:")
    for reason, count in sorted(reasons.items()):
        print(f"  {reason}: {count}")

    walllike_rows = bake_used_walllike_records(mesh_records)
    print()
    print(f"Wall-segment bake records used: {len(walllike_rows)}")
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
                f"mesh_segment_blockers={floor.get('mesh_segment_blocker_hits')} "
                f"mesh_segments={floor.get('mesh_segments_rasterized')} "
                f"mesh_bounds_fallback={floor.get('mesh_bounds_fallback_hits')} "
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
