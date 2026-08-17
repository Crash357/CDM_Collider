"""Experimental building geo: one box per coplanar face patch, 1 mm skin.

No corpus count, no 15 cm wall slab. Same padding as Faces→AABB.
"""
from __future__ import annotations

import math

import bmesh
import bpy
import mathutils

from .building_obb_pipeline import (
    _build_box_bmesh,
    _dominant_face_normal,
    _obb_basis,
    split_islands_by_angle,
)
from .constants import AABB_PADDING_M
from .helpers import (
    _clear_geo_components,
    ensure_object_mode,
    get_or_create_collection,
)
from .islands import get_evaluated_bmesh

SKIN_M = AABB_PADDING_M
MAX_DAYZ_HULLS = 32


def _skin_box_bmesh(world_verts, normal, skin=SKIN_M):
    """Tight OBB around patch verts: mesh span + *skin* on every axis."""
    if not world_verts:
        return None
    n, u, v = _obb_basis(normal)
    n_p = [p.dot(n) for p in world_verts]
    u_p = [p.dot(u) for p in world_verts]
    v_p = [p.dot(v) for p in world_verts]

    def _span(vals):
        lo, hi = min(vals) - skin, max(vals) + skin
        if hi - lo < skin * 2:
            mid = (lo + hi) * 0.5
            lo, hi = mid - skin, mid + skin
        return lo, hi

    n_lo, n_hi = _span(n_p)
    u_lo, u_hi = _span(u_p)
    v_lo, v_hi = _span(v_p)
    return _build_box_bmesh(n, u, v, n_lo, n_hi, u_lo, u_hi, v_lo, v_hi)


def _max_vert_to_box_m(world_verts, box_obj):
    """Max distance of source verts outside the box (0 = all inside)."""
    inv = box_obj.matrix_world.inverted()
    mesh = box_obj.data
    xs = [v.co.x for v in mesh.vertices]
    ys = [v.co.y for v in mesh.vertices]
    zs = [v.co.z for v in mesh.vertices]
    mn = mathutils.Vector((min(xs), min(ys), min(zs)))
    mx = mathutils.Vector((max(xs), max(ys), max(zs)))
    worst = 0.0
    for w in world_verts:
        p = inv @ w
        dx = max(mn.x - p.x, 0.0, p.x - mx.x)
        dy = max(mn.y - p.y, 0.0, p.y - mx.y)
        dz = max(mn.z - p.z, 0.0, p.z - mx.z)
        worst = max(worst, math.sqrt(dx * dx + dy * dy + dz * dz))
    return worst


def _min_box_clearance_m(world_verts, box_obj):
    """Smallest gap from a source vert to the nearest box face (inside)."""
    inv = box_obj.matrix_world.inverted()
    mesh = box_obj.data
    xs = [v.co.x for v in mesh.vertices]
    ys = [v.co.y for v in mesh.vertices]
    zs = [v.co.z for v in mesh.vertices]
    mn = mathutils.Vector((min(xs), min(ys), min(zs)))
    mx = mathutils.Vector((max(xs), max(ys), max(zs)))
    best = 1e9
    for w in world_verts:
        p = inv @ w
        if (p.x < mn.x or p.x > mx.x or p.y < mn.y or p.y > mx.y
                or p.z < mn.z or p.z > mx.z):
            continue
        gap = min(p.x - mn.x, mx.x - p.x, p.y - mn.y, mx.y - p.y,
                  p.z - mn.z, mx.z - p.z)
        best = min(best, gap)
    return 0.0 if best > 1e8 else best


def create_face_skin_components(operator, obj, min_area=0.05, angle_threshold=30.0):
    """Resolution mesh → GEO_Components, 1 mm over each coplanar patch."""
    if obj is None or obj.type != 'MESH':
        operator.report({'ERROR'}, "Ein Mesh wählen (Resolution / Gebäude).")
        return 0, {}

    ensure_object_mode()
    _clear_geo_components()
    get_or_create_collection('GEO_Components')

    bm, mw = get_evaluated_bmesh(obj)
    try:
        patches = split_islands_by_angle(
            bm, mw, angle_threshold_deg=angle_threshold, min_area_m2=min_area,
        )
        patch_data = []
        for patch in patches:
            normal = _dominant_face_normal(bm, mw, patch['face_indices'])
            patch_data.append((patch['world_verts'], normal, patch['area_m2']))
    finally:
        bm.free()

    if not patch_data:
        operator.report({'ERROR'}, "Keine Flächen-Patches (Min Area / Winkel).")
        return 0, {}

    from .building_obb_pipeline import _emit_bmesh_component

    emitted = []
    skipped = 0
    for world_verts, normal, _area in patch_data:
        box_bm = _skin_box_bmesh(world_verts, normal)
        if box_bm is None:
            skipped += 1
            continue
        name = "Component{:02d}".format(len(emitted) + 1)
        comp = _emit_bmesh_component(box_bm, name)
        outside = _max_vert_to_box_m(world_verts, comp)
        clearance = _min_box_clearance_m(world_verts, comp)
        emitted.append((comp, outside, clearance))

    count = len(emitted)
    max_out = max((e[1] for e in emitted), default=0.0)
    min_clr = min((e[2] for e in emitted), default=0.0)
    # Skin is 1 mm: verts should sit ~1 mm inside the box, none outside.
    passed = count > 0 and max_out <= 1e-5 and 0.0005 <= min_clr <= 0.0025

    scene = bpy.context.scene
    scene.cdm_last_geo_pipeline = 'FaceSkin'
    scene.cdm_auto_geo_model_id = obj.name
    scene.cdm_auto_geo_score = 1.0 if passed else 0.0
    scene.cdm_auto_geo_obb_score = 0.0
    scene.cdm_auto_geo_coverage_score = 1.0 if max_out <= 1e-5 else 0.0
    scene.cdm_auto_geo_passed = passed
    scene.cdm_auto_geo_report = (
        "FaceSkin 1 mm: {} Boxen, max außen {:.2f} mm, Skin innen {:.2f} mm{}".format(
            count,
            max_out * 1000.0,
            min_clr * 1000.0,
            " — über 32 Hulls" if count > MAX_DAYZ_HULLS else "",
        )
    )

    if count > MAX_DAYZ_HULLS:
        operator.report(
            {'WARNING'},
            "FaceSkin: {} Boxen (DayZ-Limit 32) — Patches mergen.".format(count),
        )
    operator.report(
        {'INFO'} if passed else {'WARNING'},
        scene.cdm_auto_geo_report,
    )
    return count, {
        'patches': len(patch_data),
        'emitted': count,
        'skipped': skipped,
        'max_outside_m': max_out,
        'min_clearance_m': min_clr,
        'passed': passed,
        'pipeline': 'FaceSkin',
    }
