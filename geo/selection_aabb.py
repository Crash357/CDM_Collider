"""CDM Collider — AABB component from selected faces in Edit Mode."""
import bmesh
import bpy
import mathutils

from .auto_build import _corners_to_component
from .constants import AABB_BOX_FACES, AABB_PADDING_M
from .helpers import (
    _get_target,
    _next_component_index,
    ensure_object_mode,
    get_or_create_collection,
)


def _edit_mesh_source():
    obj = bpy.context.active_object
    if obj and obj.type == 'MESH' and obj.mode == 'EDIT':
        return obj
    target = _get_target()
    if target and target.type == 'MESH':
        return target
    return None


def _aabb_corners_from_verts(verts, padding=AABB_PADDING_M):
    """Tight world AABB of *verts* plus *padding* on every side.

    Degenerate axes (coplanar faces) stay at 2×padding thickness — never
    a 15 cm wall clamp. That clamp made Faces→AABB overlap neighbouring geo.
    """
    if not verts:
        return []
    xs = [v.x for v in verts]
    ys = [v.y for v in verts]
    zs = [v.z for v in verts]
    cx = (min(xs) + max(xs)) / 2
    cy = (min(ys) + max(ys)) / 2
    cz = (min(zs) + max(zs)) / 2
    half_x = (max(xs) - min(xs)) / 2 + padding
    half_y = (max(ys) - min(ys)) / 2 + padding
    half_z = (max(zs) - min(zs)) / 2 + padding
    x0, x1 = cx - half_x, cx + half_x
    y0, y1 = cy - half_y, cy + half_y
    z0, z1 = cz - half_z, cz + half_z
    return [
        mathutils.Vector((x0, y0, z0)), mathutils.Vector((x1, y0, z0)),
        mathutils.Vector((x1, y1, z0)), mathutils.Vector((x0, y1, z0)),
        mathutils.Vector((x0, y0, z1)), mathutils.Vector((x1, y0, z1)),
        mathutils.Vector((x1, y1, z1)), mathutils.Vector((x0, y1, z1)),
    ]


def create_geometry_from_faces(operator):
    """One axis-aligned box from selected faces → GEO_Components."""
    obj = _edit_mesh_source()
    if not obj:
        operator.report({'ERROR'}, "Select a mesh in Edit Mode or set Target Object.")
        return None
    if obj.mode != 'EDIT':
        operator.report({'ERROR'}, "Enter Edit Mode and select faces.")
        return None

    bm_e = bmesh.from_edit_mesh(obj.data)
    wm = obj.matrix_world

    # AABB direkt aus Welt-Koordinaten: bei rotierten/skalierten Objekten
    # bleibt die Box so achsparallel und eng um die Auswahl.
    sel_verts = set()
    face_count = 0
    for face in bm_e.faces:
        if not face.select:
            continue
        face_count += 1
        sel_verts.update(face.verts)

    if face_count == 0:
        operator.report({'WARNING'}, "No faces selected.")
        return None

    world_verts = [wm @ v.co for v in sel_verts]
    corners = _aabb_corners_from_verts(world_verts)
    if len(corners) != 8:
        operator.report({'ERROR'}, "Could not build AABB from selection.")
        return None

    ensure_object_mode()
    get_or_create_collection('GEO_Components')

    comp_idx = _next_component_index()
    comp_name = "Component{:02d}".format(comp_idx)
    comp_obj = _corners_to_component(corners, comp_name, faces=AABB_BOX_FACES)

    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')

    operator.report({'INFO'},
                    "AABB {} from {} face(s), {} vert(s) → GEO_Components.".format(
                        comp_name, face_count, len(world_verts)))
    return comp_obj
