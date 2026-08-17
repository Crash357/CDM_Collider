"""Resolve mesh polygon index from world-space hit (stable across Edit/Object mode)."""
from __future__ import annotations

from mathutils import Vector


def _round_co(co: Vector, digits: int = 5) -> tuple[float, float, float]:
    return (round(co.x, digits), round(co.y, digits), round(co.z, digits))


def face_local_centroid(mesh, fi: int) -> Vector:
    poly = mesh.polygons[fi]
    cx = cy = cz = 0.0
    for vi in poly.vertices:
        co = mesh.vertices[vi].co
        cx += co.x
        cy += co.y
        cz += co.z
    inv = 1.0 / len(poly.vertices)
    return Vector((cx * inv, cy * inv, cz * inv))


def face_world_centroid(obj, fi: int) -> tuple[float, float, float]:
    mesh = obj.data
    local = face_local_centroid(mesh, fi)
    world = obj.matrix_world @ local
    return (world.x, world.y, world.z)


def face_world_normal(obj, fi: int) -> tuple[float, float, float]:
    mesh = obj.data
    local_n = mesh.polygons[fi].normal.copy()
    world_n = (obj.matrix_world.to_3x3() @ local_n).normalized()
    return (world_n.x, world_n.y, world_n.z)


def map_eval_face_to_mesh(obj, eval_face_index: int, depsgraph) -> int:
    """Map depsgraph raycast face index to obj.data polygon index."""
    base = obj.data
    n_base = len(base.polygons)
    if n_base == 0 or eval_face_index < 0:
        return -1

    eval_obj = obj.evaluated_get(depsgraph)
    eval_mesh = eval_obj.to_mesh(depsgraph=depsgraph)
    try:
        n_eval = len(eval_mesh.polygons)
        if eval_face_index >= n_eval:
            return -1

        active_mods = [
            m for m in obj.modifiers
            if m.show_viewport and m.type not in {'COLLISION', 'PARTICLE_SYSTEM'}
        ]
        if not active_mods and n_eval == n_base:
            return eval_face_index

        eval_poly = eval_mesh.polygons[eval_face_index]
        matrix_inv = obj.matrix_world.inverted_safe()
        eval_coords = {
            _round_co(matrix_inv @ (obj.matrix_world @ eval_mesh.vertices[vi].co))
            for vi in eval_poly.vertices
        }

        best_fi = -1
        best_overlap = 0
        for fi, poly in enumerate(base.polygons):
            base_coords = {_round_co(base.vertices[vi].co) for vi in poly.vertices}
            overlap = len(eval_coords & base_coords)
            if overlap > best_overlap:
                best_overlap = overlap
                best_fi = fi

        if best_overlap >= min(3, len(eval_coords)):
            return best_fi
        if best_overlap >= 2:
            return best_fi
        return -1
    finally:
        eval_obj.to_mesh_clear()


def resolve_face_on_mesh(
    obj,
    world_position: tuple[float, float, float] | Vector,
    world_normal: tuple[float, float, float] | Vector,
    hint_index: int = -1,
) -> int:
    """
    Map a viewport pick to a polygon index on obj.data (not evaluated mesh).
    Uses hint_index when valid; otherwise nearest centroid with compatible normal.
    """
    if obj is None or obj.type != 'MESH':
        return -1
    mesh = obj.data
    n = len(mesh.polygons)
    if n == 0:
        return -1

    matrix_inv = obj.matrix_world.inverted_safe()
    local_pos = matrix_inv @ Vector(world_position)
    local_normal = (matrix_inv.to_3x3() @ Vector(world_normal)).normalized()
    normal_min_dot = 0.15

    if 0 <= hint_index < n:
        poly = mesh.polygons[hint_index]
        if poly.normal.dot(local_normal) > normal_min_dot:
            return hint_index

    best_fi = -1
    best_score = 1e30
    for fi, poly in enumerate(mesh.polygons):
        if poly.normal.dot(local_normal) < normal_min_dot:
            continue
        centroid = face_local_centroid(mesh, fi)
        dist = (centroid - local_pos).length_squared
        if dist < best_score:
            best_score = dist
            best_fi = fi

    return best_fi


def pick_face_from_raycast(obj, hit: dict, depsgraph) -> tuple[int, tuple[float, float, float], tuple[float, float, float]]:
    """
    Resolve base-mesh face + stable world centroid/normal from a raycast hit.
    """
    mapped_hint = map_eval_face_to_mesh(obj, int(hit.get('face_index', -1)), depsgraph)
    # Eval-Index nie ungeprüft auf Base-Mesh anwenden (Modifier ändern Topology)
    hint = mapped_hint if mapped_hint >= 0 else -1

    face_i = resolve_face_on_mesh(
        obj,
        hit['position'],
        hit['normal'],
        hint_index=hint,
    )
    if face_i < 0:
        return -1, hit['position'], hit['normal']

    return face_i, face_world_centroid(obj, face_i), face_world_normal(obj, face_i)


def revalidate_seed_face_indices(obj) -> int:
    """Re-resolve stored face indices from position/normal (after mode switches)."""
    if obj is None or not hasattr(obj, 'cdm_geo_region_seeds'):
        return 0
    fixed = 0
    for seed in obj.cdm_geo_region_seeds:
        fi = resolve_face_on_mesh(
            obj,
            seed.position,
            seed.normal,
            hint_index=int(seed.face_index),
        )
        if fi >= 0:
            if fi != seed.face_index:
                seed.face_index = fi
                fixed += 1
            seed.position = face_world_centroid(obj, fi)
            seed.normal = face_world_normal(obj, fi)
    return fixed
