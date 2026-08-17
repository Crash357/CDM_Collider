"""CDM Collider — hull from selected vertices."""
import bmesh
import bpy

from .constants import AABB_PADDING_M
from .convex_hull import convex_hull_mesh
from .helpers import (
    _get_target,
    _next_component_index,
    ensure_object_mode,
    get_or_create_collection,
    move_to_collection,
    _centre_component_origin,
    _apply_geo_display,
)


def _edit_mesh_source():
    obj = bpy.context.active_object
    if obj and obj.type == 'MESH' and obj.mode == 'EDIT':
        return obj
    target = _get_target()
    if target and target.type == 'MESH':
        return target
    return None


def _thicken_if_coplanar(points, skin=AABB_PADDING_M):
    """Auswahl ±*skin* (1 mm) entlang der Best-Fit-Normale aufdicken.

    Ein Convex Hull aus koplanaren Punkten ist eine flache Scheibe ohne
    Volumen. Früher wurde auf 15 cm aufgeblasen — dieselbe Überlappung
    wie bei Faces→AABB. Jetzt nur 1 mm je Seite, wie Faces→AABB.
    """
    if len(points) < 3:
        return points
    # Robuste Ebenen-Normale unabhängig von der Punktreihenfolge:
    # weitester Punkt von p0 spannt die erste Achse, weitester Punkt
    # von dieser Linie die zweite — Kreuzprodukt ergibt die Normale.
    p0 = points[0]
    p1 = max(points, key=lambda p: (p - p0).length_squared)
    axis = p1 - p0
    if axis.length < 1e-6:
        return points
    axis.normalize()

    def _dist_to_line(p):
        d = p - p0
        return (d - d.dot(axis) * axis).length_squared

    p2 = max(points, key=_dist_to_line)
    normal = axis.cross(p2 - p0)
    if normal.length < 1e-6:
        return points
    normal.normalize()
    off = normal * skin
    return [p + off for p in points] + [p - off for p in points]


def create_geometry_from_selection(operator):
    """One convex hull component from selected vertices in Edit Mode."""
    obj = _edit_mesh_source()
    if not obj:
        operator.report({'ERROR'}, "Select a mesh in Edit Mode or set Target Object.")
        return None
    if obj.mode != 'EDIT':
        operator.report({'ERROR'}, "Enter Edit Mode and select vertices.")
        return None

    bm_e = bmesh.from_edit_mesh(obj.data)
    wm = obj.matrix_world
    sel = [wm @ v.co for v in bm_e.verts if v.select]

    if len(sel) < 3:
        operator.report({'WARNING'}, "Need ≥ 3 selected vertices.")
        return None
    n_sel = len(sel)
    sel = _thicken_if_coplanar(sel)

    ensure_object_mode()
    get_or_create_collection('GEO_Components')

    comp_idx = _next_component_index()
    comp_name = "Component{:02d}".format(comp_idx)
    mesh = convex_hull_mesh(sel, comp_name)
    if mesh is None:
        operator.report({'ERROR'}, "Convex hull failed — check selection.")
        return None

    comp_obj = bpy.data.objects.new(comp_name, mesh)
    move_to_collection(comp_obj, 'GEO_Components')
    _centre_component_origin(comp_obj)
    _apply_geo_display(comp_obj, is_component=True)

    vg = comp_obj.vertex_groups.new(name=comp_name)
    vg.add(list(range(len(mesh.vertices))), 1.0, 'REPLACE')

    # Zurück in den Edit Mode des Quell-Meshes (wie Faces → AABB), damit
    # der Nutzer direkt weitere Komponenten aus der Auswahl bauen kann.
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')

    operator.report({'INFO'},
                    "Hull {} from {} vert(s) → GEO_Components. "
                    "Then Merge (Exact).".format(comp_name, n_sel))
    return comp_obj
