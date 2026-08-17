"""CDM Collider — mesh island detection (closed vs open)."""
import bmesh
import bpy
import mathutils


def get_evaluated_bmesh(obj):
    """Modifier-applied mesh as bmesh + world matrix."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    eval_obj = obj.evaluated_get(depsgraph)
    tmp_mesh = eval_obj.to_mesh()
    bm = bmesh.new()
    bm.from_mesh(tmp_mesh)
    eval_obj.to_mesh_clear()
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    return bm, obj.matrix_world.copy()


def _island_is_closed(island_faces, island_face_ids):
    if not island_faces:
        return False
    for f in island_faces:
        for edge in f.edges:
            if len([lf for lf in edge.link_faces if id(lf) in island_face_ids]) < 2:
                return False
    return True


def iter_mesh_islands(bm):
    """Yield (vert_list, face_list, face_index_set) per connected island."""
    visited = set()
    for seed in bm.verts:
        if seed.index in visited:
            continue

        island_verts = []
        stack = [seed]
        while stack:
            v = stack.pop()
            if v.index in visited:
                continue
            visited.add(v.index)
            island_verts.append(v)
            for edge in v.link_edges:
                other = edge.other_vert(v)
                if other.index not in visited:
                    stack.append(other)

        if len(island_verts) < 4:
            continue

        island_idx = {v.index for v in island_verts}
        island_faces = [f for f in bm.faces
                        if all(v.index in island_idx for v in f.verts)]
        face_ids = {id(f) for f in island_faces}
        yield island_verts, island_faces, face_ids


def island_to_world_mesh(island_verts, island_faces, matrix_world):
    """World-space verts + face index lists for mesh build."""
    old_to_new = {}
    world_verts = []
    for v in island_verts:
        wco = matrix_world @ v.co
        old_to_new[v.index] = len(world_verts)
        world_verts.append(wco.copy())

    face_idx = []
    for f in island_faces:
        face_idx.append([old_to_new[v.index] for v in f.verts])
    return world_verts, face_idx


def classify_mesh_islands(obj, evaluated=True):
    """
    Split mesh into closed and open islands.

    Returns (closed_meshes, open_face_indices, stats)
      closed_meshes: list of (world_verts, face_idx_lists)
      open_face_indices: set of bm face indices belonging to open islands
    """
    if evaluated:
        bm, mw = get_evaluated_bmesh(obj)
    else:
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.verts.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        mw = obj.matrix_world.copy()

    closed_meshes = []
    open_face_indices = set()
    closed_count = open_count = 0

    for island_verts, island_faces, face_ids in iter_mesh_islands(bm):
        face_indices = {f.index for f in island_faces}
        if _island_is_closed(island_faces, face_ids):
            closed_meshes.append(island_to_world_mesh(island_verts, island_faces, mw))
            closed_count += 1
        else:
            open_face_indices.update(face_indices)
            open_count += 1

    bm.free()
    stats = {
        'closed_islands': closed_count,
        'open_islands': open_count,
        'open_faces': len(open_face_indices),
    }
    return closed_meshes, open_face_indices, stats


def bmesh_from_object(obj, evaluated=False):
    """bmesh + matrix_world from object mesh (optionally modifier-applied)."""
    if evaluated:
        return get_evaluated_bmesh(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    return bm, obj.matrix_world.copy()


def iter_island_vertex_indices(obj, evaluated=False):
    """Yield vertex-index lists per connected island (4+ verts)."""
    bm, mw = bmesh_from_object(obj, evaluated=evaluated)
    _ = mw
    try:
        for island_verts, _faces, _face_ids in iter_mesh_islands(bm):
            yield [v.index for v in island_verts]
    finally:
        bm.free()


def open_island_vertex_indices(obj, evaluated=False):
    """Vertex indices belonging to open (non-watertight) islands."""
    bm, mw = bmesh_from_object(obj, evaluated=evaluated)
    _ = mw
    open_verts = set()
    open_count = 0
    try:
        for island_verts, island_faces, face_ids in iter_mesh_islands(bm):
            if _island_is_closed(island_faces, face_ids):
                continue
            open_count += 1
            for v in island_verts:
                open_verts.add(v.index)
    finally:
        bm.free()
    return open_verts, open_count


def collect_closed_island_meshes(obj, evaluated=True):
    """Geschlossene Islands → [(world_verts, face_idx), ...] wie Direct."""
    bm, mw = bmesh_from_object(obj, evaluated=evaluated)
    meshes = []
    skipped_open = 0
    try:
        for island_verts, island_faces, face_ids in iter_mesh_islands(bm):
            if not _island_is_closed(island_faces, face_ids):
                skipped_open += 1
                continue
            meshes.append(island_to_world_mesh(island_verts, island_faces, mw))
    finally:
        bm.free()
    return meshes, {
        'closed_islands': len(meshes),
        'open_islands': skipped_open,
    }


def collect_open_island_meshes(obj, evaluated=True):
    """Offene Islands → exakte Mesh-Kopie pro Island (wie Closed Islands)."""
    bm, mw = bmesh_from_object(obj, evaluated=evaluated)
    meshes = []
    skipped_closed = 0
    try:
        for island_verts, island_faces, face_ids in iter_mesh_islands(bm):
            if _island_is_closed(island_faces, face_ids):
                skipped_closed += 1
                continue
            meshes.append(island_to_world_mesh(island_verts, island_faces, mw))
    finally:
        bm.free()
    return meshes, {
        'open_islands': len(meshes),
        'closed_islands': skipped_closed,
    }
