"""CDM Collider — merge GEO_Components into Geometry LOD."""
import bmesh
import bpy

from .builder import _GeoLODBuilder
from .helpers import _apply_geo_display, move_to_collection

# Methoden, bei denen jede Component ein Convex Hull wird (Felsen, VHACD, CoACD)
HULL_MERGE_METHODS = frozenset({'HULL', 'VHACD', 'COACD'})


def method_uses_hull_merge(method):
    return method in HULL_MERGE_METHODS


def create_geometry_merge_for_method(operator, method):
    """Exact merge für Gebäude/Boxen; Hull merge für HULL/VHACD/CoACD."""
    if method_uses_hull_merge(method):
        return create_geometry_merge_components(operator)
    return create_geometry_merge_exact(operator)


def create_geometry_merge_components(operator):
    """
    Merge via convex hull per component (HULL / V-HACD / CoACD).
    """
    if 'GEO_Components' not in bpy.data.collections:
        operator.report({'ERROR'},
                        "No 'GEO_Components' collection. Run 'Decompose' first.")
        return None

    col = bpy.data.collections['GEO_Components']
    comp_objects = sorted(
        [o for o in col.objects if o.type == 'MESH'],
        key=lambda o: o.name
    )

    if not comp_objects:
        operator.report({'ERROR'}, "No objects in 'GEO_Components'.")
        return None

    builder = _GeoLODBuilder()
    for comp_obj in comp_objects:
        mw = comp_obj.matrix_world
        world_verts = [mw @ v.co.copy() for v in comp_obj.data.vertices]
        builder.add_hull(world_verts)

    if not builder.hulls:
        operator.report({'ERROR'}, "No valid hulls from Component objects.")
        return None

    # Replace old Geometry object cleanly
    for old_name in ("Geometry", "Geometry_old"):
        old = bpy.data.objects.get(old_name)
        if old:
            bpy.data.objects.remove(old, do_unlink=True)

    geo_obj = builder.finalize(name="Geometry")
    operator.report({'INFO'},
                    "Hull merge: {} components → 'Geometry' LOD.".format(
                        len(builder.hulls)))
    return geo_obj

def create_geometry_merge_exact(operator):
    """
    Merges all objects from 'GEO_Components' into ONE 'Geometry' LOD object.
    Copies exact mesh geometry (NO convex hull).
    Each component object = one ComponentXX vertex group.
    """
    if 'GEO_Components' not in bpy.data.collections:
        operator.report({'ERROR'},
                        "'GEO_Components' not found. "
                        "Run 'Only Closed -> Components' first.")
        return None

    col = bpy.data.collections['GEO_Components']
    comp_objects = sorted(
        [o for o in col.objects if o.type == 'MESH'],
        key=lambda o: o.name
    )
    if not comp_objects:
        operator.report({'ERROR'}, "No objects in 'GEO_Components'.")
        return None

    # Remove existing Geometry
    for old_name in ("Geometry", "Geometry_old"):
        old = bpy.data.objects.get(old_name)
        if old:
            bpy.data.objects.remove(old, do_unlink=True)

    master_bm         = bmesh.new()
    island_vert_lists = []

    for comp_idx, comp_obj in enumerate(comp_objects, start=1):
        comp_name = "Component{:02d}".format(comp_idx)
        mw     = comp_obj.matrix_world
        src_bm = bmesh.new()
        src_bm.from_mesh(comp_obj.data)
        src_bm.verts.ensure_lookup_table()

        old_to_new = {}
        new_verts  = []
        for v in src_bm.verts:
            nv = master_bm.verts.new(mw @ v.co)
            old_to_new[v.index] = nv
            new_verts.append(nv)

        master_bm.verts.ensure_lookup_table()
        for face in src_bm.faces:
            fi = [v.index for v in face.verts]
            try:
                master_bm.faces.new([old_to_new[vi] for vi in fi])
            except ValueError:
                pass

        island_vert_lists.append((comp_name, new_verts))
        src_bm.free()

    master_bm.verts.ensure_lookup_table()
    island_vert_indices = [
        (cn, [v.index for v in vl if v.is_valid])
        for cn, vl in island_vert_lists
    ]
    island_vert_indices = [(cn, vi) for cn, vi in island_vert_indices if vi]

    result_mesh = bpy.data.meshes.new("Geometry")
    master_bm.to_mesh(result_mesh)
    master_bm.free()
    result_mesh.validate()
    result_mesh.update()

    obj = bpy.data.objects.new("Geometry", result_mesh)
    for comp_name, vert_indices in island_vert_indices:
        vg = obj.vertex_groups.new(name=comp_name)
        vg.add(vert_indices, 1.0, 'REPLACE')

    from .helpers import apply_geometry_lod_metadata
    apply_geometry_lod_metadata(obj)

    mat = bpy.data.materials.get("cdm_geo") or bpy.data.materials.new("cdm_geo")
    result_mesh.materials.append(mat)

    _apply_geo_display(obj)
    move_to_collection(obj, "Geometry")
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)

    operator.report({'INFO'},
                    "Exact merge: {} components → 'Geometry' LOD.".format(
                        len(island_vert_indices)))
    try:
        from .helpers import apply_scene_geometry_mass
        apply_scene_geometry_mass(obj)
    except Exception:
        pass
    return obj