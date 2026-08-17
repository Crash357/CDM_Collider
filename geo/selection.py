"""CDM Collider — edit-mode selection tools."""
import bpy

from .helpers import ensure_object_mode
from .islands import open_island_vertex_indices


def select_open_islands(operator):
    """
    Select only vertices / edges / faces that belong to open (non-watertight)
    islands. All closed islands are deselected. Switches to Edit Mode
    in Face-Select mode so the user sees exactly what is open.
    """
    ensure_object_mode()
    obj = bpy.context.active_object
    if obj is None or obj.type != 'MESH':
        operator.report({'ERROR'}, "No active mesh object selected.")
        return False

    open_vert_idx, open_island_count = open_island_vertex_indices(
        obj, evaluated=False)

    for v in obj.data.vertices:
        v.select = (v.index in open_vert_idx)
    for e in obj.data.edges:
        e.select = all(vi in open_vert_idx for vi in e.vertices)
    for p in obj.data.polygons:
        p.select = all(vi in open_vert_idx for vi in p.vertices)

    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_mode(use_extend=False, use_expand=False, type='FACE')

    if not open_vert_idx:
        operator.report({'INFO'}, "All islands are watertight — nothing selected.")
    else:
        operator.report({'WARNING'},
                        "{} open island(s) selected — use 'Fill Holes' to close them."
                        .format(open_island_count))
    return True
