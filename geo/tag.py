"""CDM Collider — in-place Geometry LOD tagging."""
import bpy

from .helpers import (
    _get_target, ensure_object_mode, move_to_collection,
    apply_geometry_lod_metadata, apply_scene_geometry_mass,
)
from .islands import iter_island_vertex_indices


def tag_as_geometry_lod(operator):
    """
    IN-PLACE tagging — does NOT change the mesh geometry at all.

    Takes the active/selected object and:
      1. Removes any existing ComponentXX vertex groups (clean slate).
      2. Finds every loose island via BFS.
      3. Assigns ComponentXX vertex groups.
      4. Sets DayZ LOD custom properties.
      5. Renames object to "Geometry" and moves to Geometry collection.

    Use this when the mesh is already correct (e.g. after Direct mode or
    manual proxy modelling) and you just need the correct DayZ tags.
    """
    ensure_object_mode()
    obj = (bpy.context.active_object
           if bpy.context.active_object and bpy.context.active_object.type == 'MESH'
           else None)
    if not obj:
        sel = [o for o in bpy.context.selected_objects if o.type == 'MESH']
        obj = sel[0] if sel else None
    if not obj:
        t = _get_target()
        obj = t if t and t.type == 'MESH' else None
    if not obj:
        operator.report({'ERROR'}, "Select the mesh object to tag.")
        return False

    to_remove = [vg for vg in obj.vertex_groups
                 if vg.name.startswith("Component")]
    for vg in to_remove:
        obj.vertex_groups.remove(vg)

    islands = list(iter_island_vertex_indices(obj, evaluated=False))
    if not islands:
        operator.report({'ERROR'}, "No vertices found.")
        return False

    mesh = obj.data
    for i, island in enumerate(islands):
        vg = obj.vertex_groups.new(name="Component{:02d}".format(i + 1))
        vg.add(island, 1.0, 'REPLACE')

    apply_geometry_lod_metadata(obj)

    mat = bpy.data.materials.get("cdm_geo") or bpy.data.materials.new("cdm_geo")
    if mat.name not in [m.name for m in mesh.materials]:
        mesh.materials.append(mat)

    obj.name = "Geometry"
    move_to_collection(obj, "Geometry")

    operator.report({'INFO'},
                    "Tagged as Geo LOD: {} component(s).".format(len(islands)))
    apply_scene_geometry_mass(obj)
    return True
