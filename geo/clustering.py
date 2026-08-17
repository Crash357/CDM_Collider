"""CDM Collider — face clustering and OBB/bbox helpers."""
import math
from collections import deque

import bmesh
import mathutils

def _get_loose_parts_with_normals(obj):
    """
    Find every disconnected mesh island in obj.
    Returns list of (world_verts, avg_world_normal) per island.
    avg_world_normal is the area-weighted average face normal (world space).
    For walls this points perpendicular to the wall surface.
    """
    mw = obj.matrix_world
    nm = mw.to_3x3().inverted_safe().transposed()

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()

    # Pre-compute world-space face normals and areas
    face_wn    = [(nm @ f.normal).normalized() for f in bm.faces]
    face_area  = [f.calc_area() for f in bm.faces]

    # Map vert index → list of adjacent face indices
    vert_faces = {v.index: [] for v in bm.verts}
    for f in bm.faces:
        for v in f.verts:
            vert_faces[v.index].append(f.index)

    visited_v = set()
    islands   = []

    for start in bm.verts:
        if start.index in visited_v:
            continue

        island_verts = []
        island_faces = set()
        stack = [start]

        while stack:
            v = stack.pop()
            if v.index in visited_v:
                continue
            visited_v.add(v.index)
            island_verts.append(mw @ v.co.copy())
            for fi in vert_faces[v.index]:
                island_faces.add(fi)
            for edge in v.link_edges:
                for ov in edge.verts:
                    if ov.index not in visited_v:
                        stack.append(ov)

        if len(island_verts) < 4:
            continue

        # Area-weighted average normal
        avg_n = mathutils.Vector((0.0, 0.0, 0.0))
        for fi in island_faces:
            avg_n += face_wn[fi] * face_area[fi]
        if avg_n.length < 1e-6:
            avg_n = mathutils.Vector((0.0, 0.0, 1.0))
        else:
            avg_n.normalize()

        islands.append((island_verts, avg_n))

    bm.free()
    return islands


_WALL_AXES = [
    mathutils.Vector(( 1.0,  0.0,  0.0)),
    mathutils.Vector((-1.0,  0.0,  0.0)),
    mathutils.Vector(( 0.0,  1.0,  0.0)),
    mathutils.Vector(( 0.0, -1.0,  0.0)),
    mathutils.Vector(( 0.0,  0.0,  1.0)),
    mathutils.Vector(( 0.0,  0.0, -1.0)),
]


def _cluster_by_wall_axis_faces(bm, matrix_world, face_indices,
                                min_area_m2=0.5, axis_spacing=0.3):
    """WALL clustering limited to face_indices on an existing bmesh."""
    if not face_indices:
        return []

    mw = matrix_world
    nm = mw.to_3x3().inverted_safe().transposed()
    det = abs(mw.to_3x3().determinant())
    area_scale = det ** (2.0 / 3.0) if det > 1e-10 else 1.0
    allowed = set(face_indices)

    bins = [[] for _ in range(6)]
    for face in bm.faces:
        if face.index not in allowed:
            continue
        wn = (nm @ face.normal).normalized()
        best, best_dot = 0, -2.0
        for i, ax in enumerate(_WALL_AXES):
            d = wn.dot(ax)
            if d > best_dot:
                best_dot, best = d, i
        if best_dot > 0.5:
            bins[best].append(face)

    result = []
    for axis_idx, faces in enumerate(bins):
        if not faces:
            continue
        ax = _WALL_AXES[axis_idx]
        sub_bins = {}
        for face in faces:
            c_world = mw @ face.calc_center_median()
            bucket = int(math.floor(c_world.dot(ax) / axis_spacing))
            sub_bins.setdefault(bucket, []).append(face)

        for bucket_faces in sub_bins.values():
            wn_sum = mathutils.Vector((0.0, 0.0, 0.0))
            total_area = 0.0
            vert_set = set()
            for face in bucket_faces:
                area = face.calc_area()
                wn = (nm @ face.normal).normalized()
                wn_sum += wn * area
                total_area += area
                for v in face.verts:
                    vert_set.add(v.index)

            if len(vert_set) < 4 or total_area * area_scale < min_area_m2:
                continue

            world_verts = [mw @ bm.verts[vi].co.copy() for vi in vert_set]
            avg_n = (wn_sum.normalized()
                     if wn_sum.length > 1e-6 else ax.copy())
            result.append((world_verts, avg_n))

    return result


def _cluster_by_wall_axis(obj, min_area_m2=0.5, axis_spacing=0.3):
    """
    WALL mode — groups faces by the 6 axis-aligned normal directions:
      +X / -X / +Y / -Y / +Z / -Z

    Each direction group is further split by spatial proximity (k-means-like
    binning along the normal axis) so parallel opposite walls become separate
    components.

    Result: 1 box per wall/floor/ceiling → matches hand-built DayZ geo style
    (thick connected boxes, not hundreds of tiny face clusters).

    Returns list of (world_verts, avg_world_normal).
    """
    mw  = obj.matrix_world
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.verts.ensure_lookup_table()
    all_faces = {f.index for f in bm.faces}
    result = _cluster_by_wall_axis_faces(
        bm, mw, all_faces, min_area_m2=min_area_m2, axis_spacing=axis_spacing)
    bm.free()
    return result


def _cluster_by_face_angle(obj, angle_threshold_deg=30.0, min_area_m2=0.5):
    """
    Split a mesh into face clusters where every pair of adjacent (edge-sharing)
    faces has normals within angle_threshold of each other.

    min_area_m2 : skip clusters whose total face area (world space) is below
                  this value.  Filters out door frames, trim, bolts etc. that
                  would otherwise each become their own Component.
                  Default 0.5 m² keeps main walls/floors but drops tiny details.

    Uses a LOCAL comparison (current-face vs neighbour) so the cluster can
    follow gently-curved surfaces while stopping at sharp corners:
      - Wall meets floor at 90° → new cluster   (45° threshold → stop)
      - Two walls at a 90° building corner  → new cluster
      - Stair treads (all ~horizontal)      → one cluster
      - Stair risers (all ~same direction)  → one cluster

    Works for both disconnected loose parts and one connected mesh.
    Returns list of (world_verts, avg_world_normal) per cluster.
    """
    cos_thresh = math.cos(math.radians(angle_threshold_deg))
    mw = obj.matrix_world
    nm = mw.to_3x3().inverted_safe().transposed()

    # World-space area scale factor: |det(M3x3)|^(2/3) = s² for uniform scale
    det = abs(mw.to_3x3().determinant())
    area_scale = det ** (2.0 / 3.0) if det > 1e-10 else 1.0

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.verts.ensure_lookup_table()

    face_normals = [(nm @ f.normal).normalized() for f in bm.faces]
    face_areas   = [f.calc_area() for f in bm.faces]
    visited      = [False] * len(bm.faces)
    result       = []

    for seed_face in bm.faces:
        if visited[seed_face.index]:
            continue

        cluster_faces = []
        queue = deque([seed_face])          # deque: O(1) popleft vs O(n) pop(0)
        visited[seed_face.index] = True

        while queue:
            face = queue.popleft()
            cluster_faces.append(face)
            fn = face_normals[face.index]

            for edge in face.edges:
                for nb in edge.link_faces:
                    if visited[nb.index]:
                        continue
                    # LOCAL comparison: neighbour vs CURRENT face (not seed)
                    if fn.dot(face_normals[nb.index]) >= cos_thresh:
                        visited[nb.index] = True
                        queue.append(nb)

        # Collect unique vertices for this cluster
        vert_set   = set()
        total_n    = mathutils.Vector((0.0, 0.0, 0.0))   # fix: requires args in Blender 4.2
        total_area = 0.0
        for f in cluster_faces:
            area = face_areas[f.index]
            total_n    += face_normals[f.index] * area
            total_area += area
            for v in f.verts:
                vert_set.add(v.index)

        # Skip tiny clusters (detail geometry: door frames, trim, bolts ...)
        if len(vert_set) < 4 or total_area * area_scale < min_area_m2:
            continue

        world_verts = [mw @ bm.verts[vi].co.copy() for vi in vert_set]
        avg_n = (total_n.normalized()
                 if total_n.length > 1e-6
                 else mathutils.Vector((0.0, 0.0, 1.0)))
        result.append((world_verts, avg_n))

    bm.free()
    return result


def _cluster_by_face_angle_faces(bm, matrix_world, face_indices,
                                   angle_threshold_deg=30.0,
                                   min_area_m2=0.5):
    """
    Wie _cluster_by_face_angle, aber nur für face_indices auf bestehendem bmesh.
  Generisch für jedes Gebäude — Wandflächen entlang Kanten/Türen getrennt.
    """
    if not face_indices:
        return []

    cos_thresh = math.cos(math.radians(angle_threshold_deg))
    mw = matrix_world
    nm = mw.to_3x3().inverted_safe().transposed()
    det = abs(mw.to_3x3().determinant())
    area_scale = det ** (2.0 / 3.0) if det > 1e-10 else 1.0
    allowed = set(face_indices)

    face_normals = [(nm @ f.normal).normalized() for f in bm.faces]
    face_areas = [f.calc_area() for f in bm.faces]
    visited = [False] * len(bm.faces)
    result = []

    for seed in bm.faces:
        if seed.index not in allowed or visited[seed.index]:
            continue

        cluster_faces = []
        queue = deque([seed])
        visited[seed.index] = True

        while queue:
            face = queue.popleft()
            cluster_faces.append(face)
            fn = face_normals[face.index]

            for edge in face.edges:
                for nb in edge.link_faces:
                    if nb.index not in allowed or visited[nb.index]:
                        continue
                    if fn.dot(face_normals[nb.index]) >= cos_thresh:
                        visited[nb.index] = True
                        queue.append(nb)

        vert_set = set()
        total_n = mathutils.Vector((0.0, 0.0, 0.0))
        total_area = 0.0
        for f in cluster_faces:
            area = face_areas[f.index]
            total_n += face_normals[f.index] * area
            total_area += area
            for v in f.verts:
                vert_set.add(v.index)

        if len(vert_set) < 4 or total_area * area_scale < min_area_m2:
            continue

        world_verts = [mw @ bm.verts[vi].co.copy() for vi in vert_set]
        avg_n = (total_n.normalized()
                 if total_n.length > 1e-6
                 else mathutils.Vector((0.0, 0.0, 1.0)))
        result.append((world_verts, avg_n))

    return result


def _thicken_verts(world_verts, avg_normal, min_thickness=0.5):
    """
    Oriented Bounding Box (OBB) for a face cluster, aligned to its surface normal.

    N   = cluster's avg face normal  →  solid-depth axis
    U,V = tangent plane axes         →  wall width and height

    The box grows ONE-SIDED: from the surface outward (+5 cm clearance) and
    inward by at least min_thickness.  This models the solid material behind
    each face correctly:
      floor / stair tread  (N ≈ +Z): box extends DOWNWARD  → proper stair step
      wall                 (N horiz): box extends INTO wall  → proper wall slab
      ceiling              (N ≈ -Z): box extends UPWARD     → proper ceiling slab

    WHY NOT centred ±n_half:
      A symmetric box centred on a flat face would be half above / half below.
      For stair treads that are coplanar (all verts at the same Z), the box
      would be only min_thickness thick and look like a paper-thin slab.
      One-sided thickening ensures every face cluster becomes a visually
      substantial box.

    Returns 8 corners.  Convex hull of 8 corners = correct rectangular prism.
    """
    N = mathutils.Vector(avg_normal).normalized()

    # Stable tangent frame — handles floors/ceilings (N ≈ ±Z)
    up = mathutils.Vector((0.0, 0.0, 1.0))
    if abs(N.dot(up)) > 0.99:
        up = mathutils.Vector((1.0, 0.0, 0.0))
    U = (up - up.dot(N) * N).normalized()
    V = N.cross(U).normalized()

    n_projs = [v.dot(N) for v in world_verts]
    u_projs = [v.dot(U) for v in world_verts]
    v_projs = [v.dot(V) for v in world_verts]

    # N-axis: one-sided from surface outward
    # n_surface = IQR midpoint (robust against shared boundary verts)
    ns        = sorted(n_projs)
    n_q1      = ns[max(0, len(ns) // 4)]
    n_q3      = ns[min(len(ns) - 1, 3 * len(ns) // 4)]
    n_surface = (n_q1 + n_q3) * 0.5
    n_actual  = n_q3 - n_q1                        # geometry depth (IQR)
    n_depth   = max(n_actual, min_thickness)        # solid-material depth
    n_hi      = n_surface + 0.05                    # 5 cm clearance outside surface
    n_lo      = n_surface - n_depth                 # inward / downward

    # Tangent axes: full 2D outline of the cluster
    u_min, u_max = min(u_projs), max(u_projs)
    v_min, v_max = min(v_projs), max(v_projs)

    # No degenerate thin edges
    min_h = min_thickness * 0.5
    if u_max - u_min < min_thickness:
        uc = (u_min + u_max) * 0.5
        u_min, u_max = uc - min_h, uc + min_h
    if v_max - v_min < min_thickness:
        vc = (v_min + v_max) * 0.5
        v_min, v_max = vc - min_h, vc + min_h

    # 8 OBB corners
    corners = []
    for sn in (n_lo, n_hi):
        for su in (u_min, u_max):
            for sv in (v_min, v_max):
                corners.append(sn * N + su * U + sv * V)
    return corners


def _obb_corners_tight(world_verts, avg_normal, margin=0.001):
    """Symmetrischer OBB um Cluster — Dach/Schräge (geschlossene Islands)."""
    if not world_verts:
        return []

    N = mathutils.Vector(avg_normal).normalized()
    up = mathutils.Vector((0.0, 0.0, 1.0))
    if abs(N.dot(up)) > 0.99:
        up = mathutils.Vector((1.0, 0.0, 0.0))
    U = (up - up.dot(N) * N).normalized()
    V = N.cross(U).normalized()

    n_projs = [v.dot(N) for v in world_verts]
    u_projs = [v.dot(U) for v in world_verts]
    v_projs = [v.dot(V) for v in world_verts]

    n_lo, n_hi = min(n_projs) - margin, max(n_projs) + margin
    u_lo, u_hi = min(u_projs) - margin, max(u_projs) + margin
    v_lo, v_hi = min(v_projs) - margin, max(v_projs) + margin

    if min(n_hi - n_lo, u_hi - u_lo, v_hi - v_lo) < 1e-4:
        return []

    corners = []
    for sn in (n_lo, n_hi):
        for su in (u_lo, u_hi):
            for sv in (v_lo, v_hi):
                corners.append(sn * N + su * U + sv * V)
    return corners


def _normal_is_axis_aligned(normal, thresh=0.92):
    n = mathutils.Vector(normal).normalized()
    return max(abs(n.x), abs(n.y), abs(n.z)) >= thresh


# ---------------------------------------------------------------------------
# (Kept for reference — not used by default operator)
# ---------------------------------------------------------------------------

def _build_face_clusters(obj, normal_threshold_deg=25.0, min_cluster_verts=3):
    """
    BFS flood-fill by face normals. Returns list of (world_verts, seed_normal).
    """
    cos_thresh = math.cos(math.radians(normal_threshold_deg))
    mw = obj.matrix_world
    nm = mw.to_3x3().inverted_safe().transposed()

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.verts.ensure_lookup_table()

    face_normals = {f.index: (nm @ f.normal).normalized() for f in bm.faces}
    visited = set()
    clusters = []

    for seed in bm.faces:
        if seed.index in visited:
            continue
        seed_n = face_normals[seed.index]
        cluster_faces = set()
        queue = [seed]
        queued = {seed.index}
        while queue:
            face = queue.pop(0)  # FIFO — BFS order
            visited.add(face.index)
            cluster_faces.add(face.index)
            for edge in face.edges:
                for nb in edge.link_faces:
                    if nb.index in visited or nb.index in queued:
                        continue
                    if seed_n.dot(face_normals[nb.index]) >= cos_thresh:
                        queue.append(nb)
                        queued.add(nb.index)

        vert_indices = set()
        for fi in cluster_faces:
            for v in bm.faces[fi].verts:
                vert_indices.add(v.index)
        if len(vert_indices) >= min_cluster_verts:
            world_verts = [mw @ bm.verts[vi].co.copy()
                           for vi in vert_indices]
            clusters.append((world_verts, seed_n))

    bm.free()
    return clusters


def _pair_wall_clusters(clusters):
    """
    Match antiparallel cluster pairs (outer + inner wall surfaces).
    Returns:
      paired   = [(outer_verts, inner_verts, normal), ...]
      unpaired = [(verts, normal), ...]
    """
    n = len(clusters)
    centroids = []
    normals   = []
    for (verts, seed_n) in clusters:
        c = sum(verts, mathutils.Vector()) / len(verts)
        centroids.append(c)
        normals.append(mathutils.Vector(seed_n).normalized())

    used   = set()
    paired = []

    for a in range(n):
        if a in used:
            continue
        N_a = normals[a]
        c_a = centroids[a]
        world_up = mathutils.Vector((0.0, 0.0, 1.0))
        if abs(N_a.dot(world_up)) > 0.85:
            world_up = mathutils.Vector((1.0, 0.0, 0.0))
        U_a = (world_up - world_up.dot(N_a) * N_a).normalized()
        V_a = N_a.cross(U_a).normalized()

        verts_a = clusters[a][0]
        u_a = [(v - c_a).dot(U_a) for v in verts_a]
        v_a_proj = [(v - c_a).dot(V_a) for v in verts_a]
        au_min, au_max = min(u_a), max(u_a)
        av_min, av_max = min(v_a_proj), max(v_a_proj)
        area_a = max((au_max - au_min) * (av_max - av_min), 1e-6)

        best_b    = None
        best_ovlp = 0.0

        for b in range(n):
            if b == a or b in used:
                continue
            N_b = normals[b]
            if N_a.dot(N_b) > -0.70:   # antiparallel threshold
                continue
            c_b = centroids[b]
            t = (c_a - c_b).dot(N_a)
            if not (0.01 <= t <= 3.0):  # 1 cm … 3 m (Wände, dicke Bodenplatten)
                continue
            verts_b = clusters[b][0]
            u_b = [(v - c_a).dot(U_a) for v in verts_b]
            v_b = [(v - c_a).dot(V_a) for v in verts_b]
            bu_min, bu_max = min(u_b), max(u_b)
            bv_min, bv_max = min(v_b), max(v_b)
            ov_u = max(0.0, min(au_max, bu_max) - max(au_min, bu_min))
            ov_v = max(0.0, min(av_max, bv_max) - max(av_min, bv_min))
            area_b = max((bu_max - bu_min) * (bv_max - bv_min), 1e-6)
            overlap = ov_u * ov_v / min(area_a, area_b)
            if overlap < 0.10:
                continue
            if overlap > best_ovlp:
                best_ovlp = overlap
                best_b    = b

        if best_b is not None:
            paired.append((clusters[a][0], clusters[best_b][0], N_a))
            used.add(a)
            used.add(best_b)

    unpaired = [(clusters[i][0], clusters[i][1]) for i in range(n) if i not in used]
    return paired, unpaired


def _box_corners_skin(all_verts, normal, margin=0.001):
    """
    Axis-aligned box in the wall's local frame that wraps all verts.
    Extends margin (default 1 mm) beyond the geometry on every axis —
    DayZ wall geo: inner + outer face, no fixed slab thickness.
    """
    if not all_verts:
        return []

    N = mathutils.Vector(normal).normalized()
    world_up = mathutils.Vector((0.0, 0.0, 1.0))
    if abs(N.dot(world_up)) > 0.85:
        world_up = mathutils.Vector((1.0, 0.0, 0.0))
    U = (world_up - world_up.dot(N) * N).normalized()
    V = N.cross(U).normalized()

    centroid = sum(all_verts, mathutils.Vector()) / len(all_verts)
    u_c = [(v - centroid).dot(U) for v in all_verts]
    v_c = [(v - centroid).dot(V) for v in all_verts]
    n_c = [(v - centroid).dot(N) for v in all_verts]

    u_min, u_max = min(u_c) - margin, max(u_c) + margin
    v_min, v_max = min(v_c) - margin, max(v_c) + margin
    n_min, n_max = min(n_c) - margin, max(n_c) + margin

    center = (centroid
              + (u_min + u_max) * 0.5 * U
              + (v_min + v_max) * 0.5 * V
              + (n_min + n_max) * 0.5 * N)
    hu = max((u_max - u_min) * 0.5, margin)
    hv = max((v_max - v_min) * 0.5, margin)
    hn = max((n_max - n_min) * 0.5, margin)

    corners = []
    for su in (-1, 1):
        for sv in (-1, 1):
            for sn in (-1, 1):
                corners.append(center + su * hu * U + sv * hv * V + sn * hn * N)
    return corners


def _box_corners(verts_list, normal, min_thickness=0.5, thickness_fallback=0.5):
    """
    8 world-space corners for a wall/panel bounding box.
    verts_list = [outer_verts] or [outer_verts, inner_verts].
    """
    N = mathutils.Vector(normal).normalized()
    world_up = mathutils.Vector((0.0, 0.0, 1.0))
    if abs(N.dot(world_up)) > 0.85:
        world_up = mathutils.Vector((1.0, 0.0, 0.0))
    U = (world_up - world_up.dot(N) * N).normalized()
    V = N.cross(U).normalized()

    all_verts = []
    for vl in verts_list:
        all_verts.extend(vl)

    centroid = sum(all_verts, mathutils.Vector()) / len(all_verts)
    u_c = [(v - centroid).dot(U) for v in all_verts]
    v_c = [(v - centroid).dot(V) for v in all_verts]
    n_c = [(v - centroid).dot(N) for v in all_verts]

    u_min, u_max = min(u_c), max(u_c)
    v_min, v_max = min(v_c), max(v_c)
    n_min, n_max = min(n_c), max(n_c)
    n_ext = n_max - n_min

    if n_ext < min_thickness:
        n_max_adj = n_max
        n_min_adj = n_max - max(thickness_fallback, min_thickness)
    else:
        n_max_adj, n_min_adj = n_max, n_min

    center = (centroid
              + (u_min + u_max) * 0.5 * U
              + (v_min + v_max) * 0.5 * V
              + (n_min_adj + n_max_adj) * 0.5 * N)
    hu = max((u_max - u_min) * 0.5, min_thickness * 0.5)
    hv = max((v_max - v_min) * 0.5, min_thickness * 0.5)
    hn = max((n_max_adj - n_min_adj) * 0.5, min_thickness * 0.5)

    corners = []
    for su in (-1, 1):
        for sv in (-1, 1):
            for sn in (-1, 1):
                corners.append(center + su * hu * U + sv * hv * V + sn * hn * N)
    return corners


# ---------------------------------------------------------------------------
# Bounding-box helper (BBOX method)
# ---------------------------------------------------------------------------

def _object_aabb(obj, padding=0.05):
    """
    Axis-Aligned Bounding Box of ALL vertices in world space, with padding.
    Returns 8 corners as mathutils.Vector.  One corner set = one solid box.
    """
    M = obj.matrix_world
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    pts = [M @ v.co for v in bm.verts]
    bm.free()
    if not pts:
        return []
    xs = [p.x for p in pts]
    ys = [p.y for p in pts]
    zs = [p.z for p in pts]
    corners = []
    for x in (min(xs) - padding, max(xs) + padding):
        for y in (min(ys) - padding, max(ys) + padding):
            for z in (min(zs) - padding, max(zs) + padding):
                corners.append(mathutils.Vector((x, y, z)))
    return corners


# ---------------------------------------------------------------------------
# Merge antiparallel clusters → 2 planes of a wall become 1 box
# ---------------------------------------------------------------------------

def _merge_antiparallel_clusters(clusters, normal_dot_thresh=-0.8,
                                 proximity_factor=2.0):
    """
    Merge cluster pairs that are antiparallel and spatially close.
    Returns a new (possibly shorter) cluster list.
    """
    if not clusters:
        return clusters

    def centroid(verts):
        c = mathutils.Vector((0.0, 0.0, 0.0))
        for v in verts:
            c += v
        return c / len(verts)

    def span(verts):
        c = centroid(verts)
        return max((v - c).length for v in verts)

    merged = [False] * len(clusters)
    result = []

    for i, (verts_i, n_i) in enumerate(clusters):
        if merged[i]:
            continue
        best_j = -1
        best_dist = float('inf')
        c_i = centroid(verts_i)
        s_i = span(verts_i)

        for j, (verts_j, n_j) in enumerate(clusters):
            if j <= i or merged[j]:
                continue
            if n_i.dot(n_j) > normal_dot_thresh:
                continue
            c_j = centroid(verts_j)
            s_j = span(verts_j)
            dist = (c_i - c_j).length
            if dist < proximity_factor * max(s_i, s_j, 0.01):
                if dist < best_dist:
                    best_dist = dist
                    best_j = j

        if best_j >= 0:
            verts_j, n_j = clusters[best_j]
            merged_verts = verts_i + verts_j
            avg_n = (n_i - n_j).normalized() if (n_i - n_j).length > 1e-6 else n_i
            result.append((merged_verts, avg_n))
            merged[i] = True
            merged[best_j] = True

    for i, cluster in enumerate(clusters):
        if not merged[i]:
            result.append(cluster)

    return result
