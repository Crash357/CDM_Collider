"""CDM Collider — direct closed-island and bbox workflows."""
import bpy

from .builder import _GeoLODBuilder
from .components import _emit_closed_island
from .helpers import _get_target, ensure_object_mode, _clear_geo_components
from .islands import collect_closed_island_meshes


def create_geometry_bbox(operator):
    """One BBox component per selected object (simple fallback / large props)."""
    ensure_object_mode()
    selected = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    if not selected:
        t = _get_target()
        if not t or t.type != 'MESH':
            operator.report({'ERROR'}, "Select mesh object(s) or set a Target Object.")
            return None
        selected = [t]

    builder = _GeoLODBuilder()
    for obj in selected:
        mw = obj.matrix_world
        world_verts = [mw @ v.co.copy() for v in obj.data.vertices]
        builder.add_hull(world_verts)

    if not builder.hulls:
        operator.report({'ERROR'}, "No geometry found.")
        return None

    existing = bpy.data.objects.get("Geometry")
    if existing:
        existing.name = "Geometry_old"

    geo_obj = builder.finalize(name="Geometry")
    operator.report({'INFO'},
                    "BBox Geometry LOD: {} component(s).".format(len(builder.hulls)))
    return geo_obj


def create_geometry_direct(operator):
    """
    DIRECT mode — each closed loose island → Component in GEO_Components.
    Open islands skipped. Modifiers evaluated.
    """
    ensure_object_mode()
    selected = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    if not selected:
        t = _get_target()
        if not t or t.type != 'MESH':
            operator.report({'ERROR'}, "Select mesh object(s) or set a Target Object.")
            return None
        selected = [t]

    _clear_geo_components()
    comp_idx = 1
    open_count = 0
    created = []

    for src_obj in selected:
        closed_meshes, stats = collect_closed_island_meshes(src_obj, evaluated=True)
        open_count += stats.get('open_islands', 0)
        for world_verts, face_idx in closed_meshes:
            comp_name = "Component{:02d}".format(comp_idx)
            _emit_closed_island(world_verts, face_idx, comp_name)
            created.append(comp_name)
            comp_idx += 1

    if not created:
        if open_count:
            operator.report({'ERROR'},
                            "No closed islands — {} open skipped. "
                            "Use 'Fill Holes'!".format(open_count))
        else:
            operator.report({'ERROR'}, "No valid islands found.")
        return None

    n = len(created)
    skip_txt = "  |  {} open skipped".format(open_count) if open_count else ""
    operator.report({'INFO' if not open_count else 'WARNING'},
                    "{} Components in 'GEO_Components'{}.  "
                    "Now run 'Merge (Direct) -> Geometry LOD'.".format(n, skip_txt))
    return bpy.data.collections.get('GEO_Components')
