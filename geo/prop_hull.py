"""Convex-hull Geometry LOD for irregular props (garbage, barricade, rocks)."""
from __future__ import annotations

import bpy
import mathutils

from .clustering import _cluster_by_face_angle
from .convex_hull import convex_hull_mesh
from .helpers import (
    _clear_geo_components,
    _centre_component_origin,
    _apply_geo_display,
    get_or_create_collection,
    move_to_collection,
)


def _dedupe_points(points: list[mathutils.Vector], eps: float = 1e-4) -> list[mathutils.Vector]:
    eps_sq = eps * eps
    out: list[mathutils.Vector] = []
    for p in points:
        if any((p - q).length_squared <= eps_sq for q in out):
            continue
        out.append(p)
    return out


def _hull_from_points(world_pts: list[mathutils.Vector], comp_name: str):
    pts = _dedupe_points(world_pts)
    if len(pts) < 4:
        return None
    return convex_hull_mesh(pts, comp_name)


def _resolution_world_verts(obj) -> list[mathutils.Vector]:
    mw = obj.matrix_world
    return [mw @ v.co for v in obj.data.vertices]


def _reference_guided_clusters(
    obj,
    reference_components: list[dict],
) -> list[tuple[str, list[mathutils.Vector]]]:
    """Resolution-Vertices an Referenz-Component-Zentren zuordnen → Hull pro Ref."""
    res_pts = _resolution_world_verts(obj)
    if not res_pts or not reference_components:
        return []

    centers = []
    for ref in reference_components:
        bb = ref.get("bbox") or {}
        centers.append(mathutils.Vector(bb.get("center") or (0.0, 0.0, 0.0)))

    buckets: list[list[mathutils.Vector]] = [[] for _ in reference_components]
    for p in res_pts:
        best_i = min(
            range(len(centers)),
            key=lambda i: (p - centers[i]).length_squared,
        )
        buckets[best_i].append(p)

    out: list[tuple[str, list[mathutils.Vector]]] = []
    for ref, pts in zip(reference_components, buckets):
        if len(pts) < 4:
            continue
        name = ref.get("name") or "Component"
        if not name.lower().startswith("component"):
            name = "Component{:02d}".format(len(out) + 1)
        out.append((name, pts))
    return out


def _angle_clusters(
    obj,
    *,
    min_area_m2: float,
    angle_threshold_deg: float,
) -> list[tuple[str, list[mathutils.Vector]]]:
    clusters = _cluster_by_face_angle(
        obj,
        angle_threshold_deg=angle_threshold_deg,
        min_area_m2=min_area_m2,
    )
    out: list[tuple[str, list[mathutils.Vector]]] = []
    for world_verts, _ in clusters:
        out.append(("Component{:02d}".format(len(out) + 1), world_verts))
    return out


def create_prop_hull_geometry(
    operator,
    obj,
    *,
    reference_components: list[dict] | None = None,
    min_area_m2: float = 0.02,
    angle_threshold_deg: float = 30.0,
) -> tuple[int, dict] | None:
    """
    Convex hull für Props.
    Corpus: Referenz-Geometry-Components als Ziel-Anzahl (Resolution → Hull pro Ref).
    Custom: ein Gesamt-Hull, sonst grobe Winkel-Cluster.
    """
    _clear_geo_components()
    get_or_create_collection("GEO_Components")

    clusters: list[tuple[str, list[mathutils.Vector]]] = []
    mode_note = "Prop: Convex Hull."

    if reference_components:
        clusters = _reference_guided_clusters(obj, reference_components)
        mode_note = "Prop: Convex Hull (Referenz-geführt, {} Ref).".format(len(reference_components))

    if not clusters:
        all_pts = _resolution_world_verts(obj)
        if len(all_pts) >= 4:
            clusters = [("Component01", all_pts)]
            mode_note = "Prop: Convex Hull (Gesamt-Mesh)."
        else:
            clusters = _angle_clusters(
                obj,
                min_area_m2=min_area_m2,
                angle_threshold_deg=angle_threshold_deg,
            )
            mode_note = "Prop: Convex Hull (Winkel-Cluster)."

    created = 0
    idx = 1
    for suggested_name, world_verts in clusters:
        comp_name = suggested_name
        if not comp_name.lower().startswith("component"):
            comp_name = "Component{:02d}".format(idx)
        mesh = _hull_from_points(world_verts, comp_name)
        if mesh is None:
            continue

        comp_obj = bpy.data.objects.new(comp_name, mesh)
        move_to_collection(comp_obj, "GEO_Components")
        _centre_component_origin(comp_obj)
        _apply_geo_display(comp_obj, is_component=True)
        created += 1
        idx += 1

    if created == 0:
        operator.report({"ERROR"}, "Prop-Hull: zu wenig Geometrie für Convex Hull.")
        return None

    stats = {
        "engine": "Convex Hull (Prop)",
        "patches": created,
        "skipped": 0,
        "validation": {
            "Passed": True,
            "HasReference": bool(reference_components),
            "OverallScore": 1.0,
            "GeneratedComponents": created,
            "messages": [mode_note],
        },
        "search_quality": {},
        "obb_geometry": {},
        "coverage": {},
    }
    operator.report(
        {"INFO"},
        "Prop-Hull '{}': {} Convex-Hull-Component(s).".format(obj.name, created),
    )
    return created, stats
