"""Classify Resolution LOD → building OBB pipeline vs prop convex hull."""
from __future__ import annotations

import bpy

from .mesh_kind_core import LINEAR_BUILDING_HINTS, PROP_HULL_HINTS, classify_from_names


def classify_mesh_kind(obj=None, model_id: str | None = None) -> str:
    """
    building — C# wall-axis / corpus adaptive
    prop_hull — convex hull (garbage, barricade, convex, rocks, …)
    """
    obj = obj or bpy.context.active_object
    model_id = model_id or ""
    obj_name = obj.name if obj else ""
    extra = [vg.name for vg in obj.vertex_groups] if obj and obj.type == "MESH" else []
    kind = classify_from_names(model_id, obj_name, extra)
    if kind == "prop_hull":
        return "prop_hull"

    if obj and obj.type == "MESH":
        mesh = obj.data
        if len(mesh.vertices) >= 4:
            mw = obj.matrix_world
            xs, ys, zs = [], [], []
            for v in mesh.vertices:
                w = mw @ v.co
                xs.append(w.x)
                ys.append(w.y)
                zs.append(w.z)
            sx, sy, sz = max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)
            horiz = max(sx, sy)
            footprint = sx * sy
            if horiz > 1e-3 and sz / horiz < 1.2 and footprint < 4.0 and len(mesh.polygons) < 800:
                vg_count = sum(
                    1 for vg in obj.vertex_groups
                    if _usable_group_name(vg.name)
                )
                if vg_count <= 1:
                    return "prop_hull"

    return "building"


def _usable_group_name(name: str) -> bool:
    n = name.lower()
    skip = ("component", "res", "lod", "view", "fire", "ce", "memory", "roadway", "hit", "path")
    return not any(n.startswith(p) for p in skip)
