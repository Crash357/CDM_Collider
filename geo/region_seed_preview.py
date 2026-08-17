"""Simplified region seed expansion for Blender mesh preview (mirrors C# priority)."""
from __future__ import annotations

from mathutils import Vector

from .geo_regions import REGION_KIND_ITEMS, seeds_for_object
from .region_face_resolve import resolve_face_on_mesh

# Same expansion order as RegionSeedExpander.cs
_EXPANSION_ORDER = (
    'GABLE',
    'SOFFIT',
    'PLINTH',
    'ROOF',
    'FLOOR',
    'WALL_INNER',
    'WALL_OUTER',
)

_NORMAL_DOT_MIN = 0.72
_NORMAL_DOT_MIN_STRICT = 0.82
_Z_BAND_M = 0.35


def _face_adjacency(mesh) -> list[list[int]]:
    edge_map: dict[tuple[int, int], list[int]] = {}
    for fi, poly in enumerate(mesh.polygons):
        verts = poly.vertices
        for i in range(len(verts)):
            v0, v1 = int(verts[i]), int(verts[(i + 1) % len(verts)])
            key = (v0, v1) if v0 < v1 else (v1, v0)
            edge_map.setdefault(key, []).append(fi)
    adj: list[list[int]] = [[] for _ in range(len(mesh.polygons))]
    for faces in edge_map.values():
        if len(faces) < 2:
            continue
        for a in faces:
            for b in faces:
                if a != b:
                    adj[a].append(b)
    return adj


def _poly_centroid_z(mesh, poly, matrix) -> float:
    z = 0.0
    for vi in poly.vertices:
        z += (matrix @ mesh.vertices[vi].co).z
    return z / len(poly.vertices)


def _normal_dot_min(kind: str) -> float:
    if kind in ('GABLE', 'SOFFIT'):
        return _NORMAL_DOT_MIN_STRICT
    return _NORMAL_DOT_MIN


def _can_expand(kind: str, nb_normal, seed_normal, nb_z: float, seed_z: float) -> bool:
    if nb_normal.dot(seed_normal) < _normal_dot_min(kind):
        return False
    if kind in ('ROOF', 'GABLE', 'SOFFIT'):
        return abs(nb_z - seed_z) <= _Z_BAND_M * 2.0
    if kind in ('FLOOR', 'PLINTH'):
        return abs(nb_z - seed_z) <= _Z_BAND_M
    return True


def _seed_normal_local(obj, seed, mesh, seed_fi: int) -> Vector:
    """Compare expansion normals in mesh-local space (seed.normal is world space)."""
    local_n = mesh.polygons[seed_fi].normal.copy()
    sn = seed.normal
    if hasattr(sn, 'length_squared') and sn.length_squared > 1e-8:
        return (obj.matrix_world.inverted_safe().to_3x3() @ Vector(sn)).normalized()
    if isinstance(sn, (tuple, list)) and sum(float(c) ** 2 for c in sn) > 1e-8:
        return (obj.matrix_world.inverted_safe().to_3x3() @ Vector(sn)).normalized()
    return local_n


def _resolve_seed_face(mesh, seed, obj) -> int:
    fi = resolve_face_on_mesh(
        obj,
        seed.position,
        seed.normal,
        hint_index=int(seed.face_index),
    )
    if 0 <= fi < len(mesh.polygons):
        return fi
    hint = int(seed.face_index)
    if 0 <= hint < len(mesh.polygons):
        return hint
    return -1


def build_region_face_preview(obj) -> dict[str, set[int]]:
    """Map region kind id -> face indices for viewport overlay."""
    if obj is None or obj.type != 'MESH':
        return {}
    mesh = obj.data
    if mesh is None or not mesh.polygons:
        return {}

    seeds = seeds_for_object(obj)
    if not seeds:
        return {}

    matrix = obj.matrix_world
    adj = _face_adjacency(mesh)
    normals = [poly.normal.copy() for poly in mesh.polygons]
    assigned: dict[int, str] = {}
    buckets: dict[str, set[int]] = {item_id: set() for item_id, _, _ in REGION_KIND_ITEMS}

    for kind in _EXPANSION_ORDER:
        for seed in seeds:
            if seed.kind != kind:
                continue
            seed_fi = _resolve_seed_face(mesh, seed, obj)
            if seed_fi < 0 or seed_fi in assigned:
                continue
            seed_normal = _seed_normal_local(obj, seed, mesh, seed_fi)
            seed_z = _poly_centroid_z(mesh, mesh.polygons[seed_fi], matrix)

            queue = [seed_fi]
            assigned[seed_fi] = kind
            buckets[kind].add(seed_fi)

            while queue:
                fi = queue.pop()
                for nb in adj[fi]:
                    if nb in assigned:
                        continue
                    nb_z = _poly_centroid_z(mesh, mesh.polygons[nb], matrix)
                    if not _can_expand(kind, normals[nb], seed_normal, nb_z, seed_z):
                        continue
                    assigned[nb] = kind
                    buckets[kind].add(nb)
                    queue.append(nb)

    return {k: v for k, v in buckets.items() if v}
