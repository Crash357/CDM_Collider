"""Triangulated resolution mesh (same topology as C# region-generate export)."""
from __future__ import annotations

import math


def _vec3_sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _vec3_add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _vec3_scale(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def _vec3_dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _vec3_len_sq(a):
    return _vec3_dot(a, a)


def _face_centroid(vertices, face):
    cx = cy = cz = 0.0
    for vi in face:
        x, y, z = vertices[vi]
        cx += x
        cy += y
        cz += z
    inv = 1.0 / len(face)
    return (cx * inv, cy * inv, cz * inv)


def _face_normal(vertices, face):
    if len(face) < 3:
        return (0.0, 0.0, 1.0)
    ax, ay, az = vertices[face[0]]
    bx, by, bz = vertices[face[1]]
    cx, cy, cz = vertices[face[2]]
    ux, uy, uz = (bx - ax, by - ay, bz - az)
    vx, vy, vz = (cx - ax, cy - ay, cz - az)
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    ln = math.sqrt(nx * nx + ny * ny + nz * nz)
    if ln < 1e-12:
        return (0.0, 0.0, 1.0)
    inv = 1.0 / ln
    return (nx * inv, ny * inv, nz * inv)


def resolve_face_on_tri_mesh(
    vertices: list[tuple[float, float, float]],
    faces: list[list[int]],
    world_position: tuple[float, float, float],
    world_normal: tuple[float, float, float],
    *,
    hint_index: int = -1,
    normal_min_dot: float = 0.15,
) -> int:
    """Nearest compatible face on triangulated export mesh."""
    if not faces:
        return -1

    nx, ny, nz = world_normal
    n_len = math.sqrt(nx * nx + ny * ny + nz * nz)
    if n_len > 1e-12:
        nx, ny, nz = nx / n_len, ny / n_len, nz / n_len
    else:
        nx, ny, nz = 0.0, 0.0, 1.0

    if 0 <= hint_index < len(faces):
        fn = _face_normal(vertices, faces[hint_index])
        if _vec3_dot(fn, (nx, ny, nz)) >= normal_min_dot:
            return hint_index

    px, py, pz = world_position
    best_fi = -1
    best_score = 1e30
    for fi, face in enumerate(faces):
        fn = _face_normal(vertices, face)
        if _vec3_dot(fn, (nx, ny, nz)) < normal_min_dot:
            continue
        cx, cy, cz = _face_centroid(vertices, face)
        dx, dy, dz = (cx - px, cy - py, cz - pz)
        dist = dx * dx + dy * dy + dz * dz
        if dist < best_score:
            best_score = dist
            best_fi = fi
    return best_fi


def build_resolution_mesh_triangles(obj, depsgraph=None):
    """
    World-space vertices + triangulated faces (matches cs_engine_bridge export).
    Returns (vertices, faces) or (None, None) on failure.
    """
    import bmesh
    import bpy

    if obj is None or obj.type != 'MESH':
        return None, None

    if depsgraph is None:
        depsgraph = bpy.context.evaluated_depsgraph_get()

    eval_obj = obj.evaluated_get(depsgraph)
    mesh = eval_obj.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()

    mw = eval_obj.matrix_world
    verts = [tuple(mw @ v.co) for v in bm.verts]
    faces = [[v.index for v in f.verts] for f in bm.faces]

    bm.free()
    eval_obj.to_mesh_clear()
    return verts, faces


def remap_seed_to_export_mesh(obj, world_position, world_normal, depsgraph=None) -> int:
    verts, faces = build_resolution_mesh_triangles(obj, depsgraph)
    if not faces:
        return -1
    pos = tuple(float(c) for c in world_position)
    nrm = tuple(float(c) for c in world_normal)
    return resolve_face_on_tri_mesh(verts, faces, pos, nrm)
