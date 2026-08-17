"""CDM Collider — vertex-group based component creation."""
import bmesh
import bpy
import mathutils

from .constants import BOX_FACES
from .convex_hull import convex_hull_mesh
from .engine import engine_generate, engine_ok
from .helpers import (
    _get_mesh_for_vertex_groups, _clear_geo_components, _centre_component_origin,
    _apply_geo_display, ensure_object_mode, move_to_collection,
)


def create_geometry_from_vertex_groups(operator):
    """One OBB component per vertex group on active or target mesh."""
    target, usable_groups = _get_mesh_for_vertex_groups(operator)
    if not target:
        return None

    _clear_geo_components()

    mw = target.matrix_world
    created = 0
    skipped = []
    comp_idx = 1
    use_engine = engine_ok()

    for vg in usable_groups:
        world_verts = []
        for v in target.data.vertices:
            for g in v.groups:
                if g.group == vg.index and g.weight > 0.0:
                    world_verts.append((mw @ v.co)[:])
                    break

        if len(world_verts) < 2:
            skipped.append(vg.name)
            continue

        corners = None
        if use_engine:
            try:
                n = len(world_verts)
                cx = sum(v[0] for v in world_verts) / n
                cy = sum(v[1] for v in world_verts) / n
                cz = sum(v[2] for v in world_verts) / n
                verts_with_cent = list(world_verts) + [(cx, cy, cz)]
                cent_idx = len(verts_with_cent) - 1
                tris = [[i, (i + 1) % n, cent_idx] for i in range(n)]
                boxes = engine_generate(
                    verts_with_cent, tris,
                    angle_thresh=90.0,
                    min_area=0.0,
                    min_thickness=0.5,
                )
                if boxes:
                    corners = boxes[0]
            except Exception:
                pass

        if corners is None:
            xs = [v[0] for v in world_verts]
            ys = [v[1] for v in world_verts]
            zs = [v[2] for v in world_verts]
            x0, x1 = min(xs), max(xs)
            y0, y1 = min(ys), max(ys)
            z0, z1 = min(zs), max(zs)
            PAD = 0.25
            x0 -= PAD; y0 -= PAD; z0 -= PAD
            x1 += PAD; y1 += PAD; z1 += PAD
            corners = [
                (x0, y0, z0), (x1, y0, z0), (x0, y1, z0), (x1, y1, z0),
                (x0, y0, z1), (x1, y0, z1), (x0, y1, z1), (x1, y1, z1),
            ]

        comp_name = "Component{:02d}".format(comp_idx)
        comp_idx += 1
        tmp_bm = bmesh.new()
        bverts = [tmp_bm.verts.new(mathutils.Vector(c)) for c in corners]
        for fi in BOX_FACES:
            try:
                tmp_bm.faces.new([bverts[i] for i in fi])
            except ValueError:
                pass
        mesh = bpy.data.meshes.new(comp_name)
        tmp_bm.to_mesh(mesh)
        tmp_bm.free()
        mesh.validate()
        mesh.update()
        comp_obj = bpy.data.objects.new(comp_name, mesh)
        move_to_collection(comp_obj, 'GEO_Components')
        _centre_component_origin(comp_obj)
        _apply_geo_display(comp_obj, is_component=True)
        created += 1

    if created == 0:
        operator.report({'ERROR'},
                        "Keine Components — alle Groups < 2 Vertices.")
        return None

    if skipped:
        operator.report({'WARNING'},
                        "Übersprungen ({}): {}".format(
                            len(skipped), ', '.join(skipped[:5])))

    operator.report({'INFO'},
                    "'{}': {} Component(s) aus {} Vertex Groups. "
                    "Dann Merge.".format(target.name, created, len(usable_groups)))
    return bpy.data.collections.get('GEO_Components')


def create_geometry_hull_from_vertex_groups(operator):
    """One convex hull per vertex group on active or target mesh."""
    target, usable_groups = _get_mesh_for_vertex_groups(operator)
    if not target:
        return None

    ensure_object_mode()
    _clear_geo_components()

    mw = target.matrix_world
    created = 0
    skipped = []

    for vg in usable_groups:
        world_verts = []
        for v in target.data.vertices:
            for g in v.groups:
                if g.group == vg.index and g.weight > 0.0:
                    world_verts.append(mw @ v.co.copy())
                    break

        if len(world_verts) < 4:
            skipped.append(vg.name)
            continue

        comp_name = vg.name
        mesh = convex_hull_mesh(world_verts, comp_name)
        if mesh is None:
            skipped.append(vg.name)
            continue

        comp_obj = bpy.data.objects.new(comp_name, mesh)
        move_to_collection(comp_obj, 'GEO_Components')
        _apply_geo_display(comp_obj, is_component=True)
        created += 1

    if created == 0:
        operator.report({'ERROR'},
                        "Keine Hulls — alle Groups < 4 Vertices.")
        return None

    if skipped:
        operator.report({'WARNING'},
                        "Übersprungen ({}): {}".format(
                            len(skipped), ', '.join(skipped[:5])))

    operator.report({'INFO'},
                    "'{}': {} Hull-Component(s). Dann Merge (Hull).".format(
                        target.name, created))
    return bpy.data.collections.get('GEO_Components')
