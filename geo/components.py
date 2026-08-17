"""CDM Collider — GEO_Components hull/box object creation."""
import bmesh
import bpy
import mathutils

from .constants import BOX_FACES
from .helpers import (
    move_to_collection,
    _apply_geo_display,
    _centre_component_origin,
    ensure_outward_normals,
)


def _emit_closed_island(world_verts, face_idx_lists, comp_name):
    """Closed island mesh → Component object in GEO_Components."""
    tmp_bm = bmesh.new()
    bm_verts = [tmp_bm.verts.new(mathutils.Vector(v)) for v in world_verts]
    tmp_bm.verts.ensure_lookup_table()
    for fi in face_idx_lists:
        try:
            tmp_bm.faces.new([bm_verts[i] for i in fi])
        except ValueError:
            pass
    ensure_outward_normals(tmp_bm)
    mesh = bpy.data.meshes.new(comp_name)
    tmp_bm.to_mesh(mesh)
    tmp_bm.free()
    mesh.validate()
    mesh.update()
    comp_obj = bpy.data.objects.new(comp_name, mesh)
    move_to_collection(comp_obj, 'GEO_Components')
    _centre_component_origin(comp_obj)
    _apply_geo_display(comp_obj, is_component=True)
    return comp_obj


def _append_hull_components(hulls, comp_idx_start=1):
    """Konvex-Hulls als Component-Objekte in GEO_Components ablegen."""
    comp_idx = comp_idx_start
    for hull_verts, hull_tris in hulls:
        comp_name = "Component{:02d}".format(comp_idx)
        tmp_bm = bmesh.new()
        bm_verts = [tmp_bm.verts.new(mathutils.Vector(v)) for v in hull_verts]
        tmp_bm.verts.ensure_lookup_table()
        for a, b, c in hull_tris:
            try:
                tmp_bm.faces.new([bm_verts[a], bm_verts[b], bm_verts[c]])
            except ValueError:
                pass
        ensure_outward_normals(tmp_bm)
        mesh = bpy.data.meshes.new(comp_name)
        tmp_bm.to_mesh(mesh)
        tmp_bm.free()
        mesh.validate()
        mesh.update()
        comp_obj = bpy.data.objects.new(comp_name, mesh)
        move_to_collection(comp_obj, 'GEO_Components')
        _apply_geo_display(comp_obj, is_component=True)
        comp_idx += 1
    return comp_idx
