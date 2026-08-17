"""Mesh-kind classification without Blender (corpus index + shared hints)."""
from __future__ import annotations

from typing import Any

PROP_HULL_HINTS = (
    "convex",
    "garbage",
    "barricade",
    "woodpile",
    "well_pump",
    "pile",
    "rock",
    "stone",
    "debris",
    "bin",
    "boundarystone",
    "boogieman",
    "carousel",
    "chair",
    "bench",
    "hose",
    "cylinder",
    "sponge",
    "blanket",
    "hazmatbag",
    "cbrncase",
    "boulder",
    "log",
    "stump",
    "crate",
    "canister",
    "tire",
    "tyre",
    "wheel",
    "barrel",
    "bucket",
    "sack",
    "bag",
    "trash",
    "rubble",
    "wreck",
    "scrap",
    "prop_",
)

LINEAR_BUILDING_HINTS = ("ladder",)

_SKIP_SELECTION_PREFIXES = (
    "component", "res", "lod", "view", "fire", "ce", "memory", "roadway", "hit", "path", "phys",
)


def _name_hits_prop_hull(*names: str) -> bool:
    for raw in names:
        if not raw:
            continue
        n = raw.lower()
        if any(h in n for h in LINEAR_BUILDING_HINTS):
            return False
        if any(h in n for h in PROP_HULL_HINTS):
            return True
    return False


def _iter_selection_names(record: dict[str, Any]) -> list[str]:
    names: list[str] = []
    for lod in record.get("lods") or []:
        for sel in (lod.get("selections") or {}).keys():
            names.append(sel)
        for door in lod.get("doors") or []:
            if door.get("name"):
                names.append(door["name"])
    return names


def classify_from_names(model_id: str = "", obj_name: str = "", extra_names: list[str] | None = None) -> str:
    names = [model_id, obj_name, *(extra_names or [])]
    if _name_hits_prop_hull(*names):
        return "prop_hull"
    return "building"


def classify_model_record(record: dict[str, Any]) -> str:
    """Classify corpus record: building (OBB) vs prop_hull (convex)."""
    model_id = record.get("id") or ""
    if _name_hits_prop_hull(model_id, *_iter_selection_names(record)):
        return "prop_hull"

    geo = None
    for lod in record.get("geometry_lods") or []:
        geo = lod
        break
    res = None
    for lod in record.get("resolution_lods") or []:
        if lod.get("resolution") == 1.0:
            res = lod
            break
    if res is None and record.get("resolution_lods"):
        res = record["resolution_lods"][0]

    comp_count = len((geo or {}).get("components") or [])
    geo_faces = int((geo or {}).get("faces") or 0)
    res_faces = int((res or {}).get("faces") or 0)
    geo_verts = int((geo or {}).get("vertices") or 0)

    if geo_faces == 0 and res_faces == 0:
        return "building"
    if comp_count <= 3 and geo_verts > 0 and geo_verts <= comp_count * 12 + 8:
        return "prop_hull"
    if comp_count == 1 and res_faces > 0 and geo_faces > 0 and geo_verts <= 64:
        return "prop_hull"

    return "building"
