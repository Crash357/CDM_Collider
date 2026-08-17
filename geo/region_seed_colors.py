"""Mesh face colors + viewport shading for geo region preview (always visible)."""
from __future__ import annotations

import bpy

from .region_seed_preview import build_region_face_preview

_ATTR_NAME = 'cdm_geo_region'
_UNPAINTED_FACE = (0.82, 0.82, 0.84, 1.0)

_KIND_RGB: dict[str, tuple[float, float, float]] = {
    'WALL_OUTER': (0.25, 0.55, 0.95),
    'ROOF': (0.95, 0.35, 0.25),
    'WALL_INNER': (0.55, 0.75, 0.95),
    'FLOOR': (0.45, 0.85, 0.55),
    'GABLE': (0.95, 0.75, 0.30),
    'PLINTH': (0.65, 0.65, 0.70),
    'SOFFIT': (0.85, 0.55, 0.90),
}


def _set_active_color_attribute(mesh: bpy.types.Mesh, attr) -> None:
    ca = mesh.color_attributes
    idx = -1
    for i, key in enumerate(ca.keys()):
        if key == attr.name:
            idx = i
            break
    if idx >= 0:
        if hasattr(ca, 'active_index'):
            ca.active_index = idx
        if hasattr(ca, 'render_color_index'):
            ca.render_color_index = idx
    for prop in ('active', 'active_color', 'active_render'):
        if hasattr(ca, prop):
            try:
                setattr(ca, prop, attr)
            except (TypeError, AttributeError):
                pass


def _ensure_corner_color_attr(mesh, n_loops: int) -> bpy.types.ColorAttribute | None:
    """CORNER domain = flat per-face colors without vertex interpolation artifacts."""
    if n_loops <= 0:
        return None

    attr = mesh.color_attributes.get(_ATTR_NAME)
    if attr is not None and (attr.domain != 'CORNER' or len(attr.data) != n_loops):
        mesh.color_attributes.remove(attr)
        attr = None

    if attr is None:
        attr = mesh.color_attributes.new(name=_ATTR_NAME, type='FLOAT_COLOR', domain='CORNER')

    _set_active_color_attribute(mesh, attr)
    mesh.update()

    if len(attr.data) != n_loops:
        return None
    return attr


def _object_mode_for_mesh_edit(context, obj, was_edit: bool) -> None:
    """Color attributes are unreliable while mesh is in Edit Mode — use Object Mode."""
    if was_edit and obj.mode == 'EDIT':
        bpy.ops.object.mode_set(mode='OBJECT')


def _restore_edit_mode(context, obj, was_edit: bool) -> None:
    if was_edit and obj.mode != 'EDIT':
        bpy.ops.object.mode_set(mode='EDIT')
        if context.tool_settings.mesh_select_mode != (False, False, True):
            context.tool_settings.mesh_select_mode = (False, False, True)
        bpy.ops.mesh.select_all(action='DESELECT')


def sync_region_mesh_colors(obj: bpy.types.Object | None, context=None) -> int:
    """Paint region faces via corner colors (one color per face, sharp edges)."""
    if obj is None or obj.type != 'MESH':
        return 0
    if context is None:
        context = bpy.context

    from .region_face_resolve import revalidate_seed_face_indices

    was_edit = obj.mode == 'EDIT'
    _object_mode_for_mesh_edit(context, obj, was_edit)

    mesh = obj.data
    n_faces = len(mesh.polygons)
    n_loops = len(mesh.loops)
    if n_faces == 0 or n_loops == 0:
        if was_edit:
            _restore_edit_mode(context, obj, was_edit)
        return 0

    revalidate_seed_face_indices(obj)

    attr = _ensure_corner_color_attr(mesh, n_loops)
    if attr is None:
        if was_edit:
            _restore_edit_mode(context, obj, was_edit)
        return 0

    plan = build_region_face_preview(obj)

    for li in range(n_loops):
        attr.data[li].color = _UNPAINTED_FACE

    painted = 0
    for kind, face_set in plan.items():
        rgb = _KIND_RGB.get(kind, (0.8, 0.8, 0.2))
        col = (rgb[0], rgb[1], rgb[2], 1.0)
        for fi in face_set:
            if not (0 <= fi < n_faces):
                continue
            for loop_i in mesh.polygons[fi].loop_indices:
                attr.data[loop_i].color = col
            painted += 1

    mesh.update()
    try:
        mesh.update_gpu_tag()
    except AttributeError:
        pass
    obj.update_tag(refresh={'OBJECT'})

    if was_edit:
        _restore_edit_mode(context, obj, was_edit)

    return painted


def clear_region_mesh_colors(obj: bpy.types.Object | None, context=None) -> None:
    if obj is None or obj.type != 'MESH':
        return
    if context is None:
        context = bpy.context

    was_edit = obj.mode == 'EDIT'
    _object_mode_for_mesh_edit(context, obj, was_edit)

    mesh = obj.data
    if _ATTR_NAME in mesh.color_attributes:
        mesh.color_attributes.remove(mesh.color_attributes[_ATTR_NAME])
    mesh.update()
    try:
        mesh.update_gpu_tag()
    except AttributeError:
        pass

    if was_edit:
        _restore_edit_mode(context, obj, was_edit)


def set_viewport_region_colors(context, enabled: bool = True) -> None:
    if not context or not context.screen:
        return
    for area in context.screen.areas:
        if area.type != 'VIEW_3D':
            continue
        space = area.spaces.active
        if space.type != 'VIEW_3D':
            continue
        shading = space.shading
        if enabled:
            shading.type = 'SOLID'
            shading.color_type = 'VERTEX'
            if hasattr(shading, 'color_attribute'):
                shading.color_attribute = _ATTR_NAME
            if hasattr(shading, 'color_attribute_name'):
                shading.color_attribute_name = _ATTR_NAME
            shading.show_xray = False
        else:
            if shading.color_type != 'VERTEX':
                continue
            active = getattr(shading, 'color_attribute', '') or getattr(
                shading, 'color_attribute_name', '',
            )
            if active and active != _ATTR_NAME:
                continue
            shading.color_type = 'MATERIAL'
            if hasattr(shading, 'color_attribute'):
                shading.color_attribute = ''
            if hasattr(shading, 'color_attribute_name'):
                shading.color_attribute_name = ''


def refresh_region_display(context, obj: bpy.types.Object | None = None) -> int:
    from .geo_regions import resolve_resolution_obj

    if obj is None:
        obj = resolve_resolution_obj(context)
    scene = context.scene
    if obj is None or not getattr(scene, 'cdm_geo_region_show_mesh', True):
        if obj is not None:
            clear_region_mesh_colors(obj, context)
        set_viewport_region_colors(context, enabled=False)
        return 0

    if not hasattr(obj, 'cdm_geo_region_seeds') or len(obj.cdm_geo_region_seeds) == 0:
        clear_region_mesh_colors(obj, context)
        set_viewport_region_colors(context, enabled=False)
        return 0

    painted = sync_region_mesh_colors(obj, context)
    set_viewport_region_colors(context, enabled=True)
    return painted
