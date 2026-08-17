"""Raycast helpers for geo region picker (Blender scene API)."""
from __future__ import annotations

import bpy
from bpy_extras import view3d_utils
from mathutils import Vector


def raycast_face_on_object(context, event, obj: bpy.types.Object) -> dict | None:
    """
    Pick face on evaluated mesh using scene.ray_cast (depsgraph).
    Returns dict with face_index, position, normal in world space.
    """
    region = context.region
    rv3d = context.region_data
    if region is None or rv3d is None or obj is None:
        return None

    coord = (event.mouse_region_x, event.mouse_region_y)
    view_vector = view3d_utils.region_2d_to_vector_3d(region, rv3d, coord)
    ray_origin = view3d_utils.region_2d_to_origin_3d(region, rv3d, coord)
    if view_vector is None or ray_origin is None:
        return None

    direction = view_vector.normalized()
    depsgraph = context.evaluated_depsgraph_get()

    hit, location, normal, face_index, hit_obj, _matrix = context.scene.ray_cast(
        depsgraph, ray_origin, direction,
    )
    if not hit:
        return _raycast_object_local(obj, ray_origin, direction)

    eval_target = obj.evaluated_get(depsgraph)
    if hit_obj != eval_target and hit_obj != obj:
        return _raycast_object_local(obj, ray_origin, direction)

    if normal.length_squared < 1e-12:
        normal = Vector((0.0, 0.0, 1.0))
    else:
        normal = normal.normalized()

    return {
        'face_index': int(face_index),
        'position': tuple(location),
        'normal': tuple(normal),
    }


def _raycast_object_local(
    obj: bpy.types.Object,
    ray_origin: Vector,
    direction: Vector,
) -> dict | None:
    """Fallback: object-space ray_cast on the mesh object."""
    matrix_inv = obj.matrix_world.inverted_safe()
    origin_local = matrix_inv @ ray_origin
    direction_local = (matrix_inv.to_3x3() @ direction).normalized()

    hit, location, normal, face_index = obj.ray_cast(origin_local, direction_local)
    if not hit:
        return None

    world_loc = obj.matrix_world @ location
    world_normal = (obj.matrix_world.to_3x3() @ normal).normalized()

    return {
        'face_index': int(face_index),
        'position': tuple(world_loc),
        'normal': tuple(world_normal),
    }
