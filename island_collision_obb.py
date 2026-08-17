"""
island_collision_obb.py
=======================
Blender 5.0  –  mathutils + numpy, no bpy.ops

Calculates OBBs (Oriented Bounding Boxes) for planar mesh islands.

Core problem:
    Standard OBB on 4 coplanar points → thickness in normal direction ≈ 0.

Solution:
    1.  PCA via SVD:  Vt[-1] = face normal axis (smallest singular value)
                     Vt[0]  = longest in-plane axis
                     Vt[1]  = shorter in-plane axis
    2.  In-plane extents: exact projection → correct width/height
    3.  Thickness: bidirectional obj.ray_cast() from island centroid
        → hits back face → actual wall/floor thickness
        → no hit → fallback (default 1 cm)
    4.  Box centre: shifted by half_z along inward normal,
        so that the panel surface lies on the front face of the box.
"""

from __future__ import annotations

import numpy as np
from mathutils import Matrix, Vector

# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def compute_island_obb(
    island_verts: list[Vector],
    mesh_obj=None,
    fallback_thickness: float = 0.01,
    min_thickness: float = 0.01,
    max_raycast_distance: float = 5.0,
) -> tuple[Vector, Matrix, Vector]:
    """
    Calculates an oriented bounding box for a (nearly) planar island.

    Parameters
    ----------
    island_verts
        World coordinates of the island vertices as list[mathutils.Vector].
        For a 4-sided wall panel these are 4 coplanar points.
    mesh_obj
        Blender mesh object (bpy.types.Object) for the back-face raycast.
        None → fallback thickness is always used.
    fallback_thickness
        Thickness in metres when no raycast hit occurs. Default: 0.01 m (1 cm).
    min_thickness
        Minimum thickness (clamp lower bound). Default: 0.01 m (1 cm).
    max_raycast_distance
        Maximum raycast range in metres. Hits beyond this are discarded.
        Default: 5.0 m.

    Returns
    -------
    center : mathutils.Vector
        World-coordinate centre of the box.
        The panel surface (island verts) lies on the front face of the box.
    rotation : mathutils.Matrix (3×3)
        Rotation matrix – columns are the local axes:
          col[0] = axis0  – longest in-plane axis
          col[1] = axis1  – shorter in-plane axis
          col[2] = axis2  – outward-pointing face normal axis
    half_extents : mathutils.Vector
        Half-extents (hx, hy, hz) along the local axes.

    Example – 8 box corners in world coordinates
    ----------------------------------------------
    center, rot, he = compute_island_obb(island_verts, obj)
    corners = [
        center + rot @ Vector((sx * he.x, sy * he.y, sz * he.z))
        for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)
    ]
    """
    verts_np = np.array([v.to_tuple() for v in island_verts], dtype=np.float64)
    count = len(verts_np)

    # --- Degenerate cases ------------------------------------------------
    if count == 0:
        t = max(fallback_thickness, min_thickness)
        return Vector(), Matrix.Identity(3), Vector((t, t, t))

    centroid_np = verts_np.mean(axis=0)
    centroid = Vector(centroid_np.tolist())

    if count < 3:
        t = max(fallback_thickness, min_thickness)
        return centroid, Matrix.Identity(3), Vector((t, t, t))

    # --- PCA via SVD -------------------------------------------------------
    #   np.linalg.svd(centered) → U, S, Vt
    #   S[i] descending: S[0] ≥ S[1] ≥ S[2]
    #   Vt[i] = i-th principal axis vector
    #   Vt[2] = axis of MINIMAL variance → face normal (S[2] ≈ 0 for planar panel)
    centered = verts_np - centroid_np
    _, S, Vt = np.linalg.svd(centered, full_matrices=False)

    axis0 = Vector(Vt[0].tolist())   # Longest in-plane axis
    axis1 = Vector(Vt[1].tolist())   # Shorter in-plane axis
    axis2 = Vector(Vt[2].tolist())   # Face normal axis

    # Ensure right-handed coordinate system
    if axis0.cross(axis1).dot(axis2) < 0.0:
        axis2 = -axis2

    # --- In-Plane-Extents (korrekte Projektion) ----------------------------
    proj0 = centered @ Vt[0]    # Projektion aller Verts auf axis0
    proj1 = centered @ Vt[1]    # Projektion aller Verts auf axis1

    half_x = max(float((proj0.max() - proj0.min()) * 0.5), 1e-6)
    half_y = max(float((proj1.max() - proj1.min()) * 0.5), 1e-6)

    # --- Dicke in Normalenrichtung -----------------------------------------
    thickness, inward_dir = _measure_thickness_with_direction(
        centroid=centroid,
        normal=axis2,
        mesh_obj=mesh_obj,
        fallback=fallback_thickness,
        min_t=min_thickness,
        max_dist=max_raycast_distance,
    )
    half_z = thickness * 0.5

    # --- Box centre: panel surface should lie on the front face of the box -
    #   inward_dir points from the surface into the wall.
    #   centre = centroid + inward_dir * half_z
    center = centroid + inward_dir * half_z

    # axis2 should point outward (opposite to inward_dir)
    # → flip if axis2 points in the same direction as inward_dir
    if axis2.dot(inward_dir) > 0.0:
        axis2 = -axis2

    # --- Rotation matrix (columns = local axes) -------------------------
    rotation = Matrix((
        (axis0.x, axis1.x, axis2.x),
        (axis0.y, axis1.y, axis2.y),
        (axis0.z, axis1.z, axis2.z),
    ))

    half_extents = Vector((half_x, half_y, half_z))
    return center, rotation, half_extents


# ---------------------------------------------------------------------------
# Internal helper: measure thickness + inward direction
# ---------------------------------------------------------------------------

def _measure_thickness_with_direction(
    centroid: Vector,
    normal: Vector,
    mesh_obj,
    fallback: float,
    min_t: float,
    max_dist: float,
    ray_eps: float = 0.001,
) -> tuple[float, Vector]:
    """
    Measures panel thickness via bidirectional raycast.

    Fires rays in ±normal direction from the centroid.
    The shortest valid hit is the panel thickness.
    The hit direction vector is the "inward direction".

    Returns
    -------
    thickness   : measured thickness in world units (always >= min_t)
    inward_dir  : unit vector pointing from the surface into the wall
    """
    default_inward = -normal.normalized()   # Fallback: -Normale ist "innen"

    if mesh_obj is None:
        return max(min_t, fallback), default_inward

    mat_world = mesh_obj.matrix_world
    mat_inv = mat_world.inverted()
    rot_inv = mat_inv.to_3x3()

    n_norm = normal.normalized()
    best_dist: float | None = None
    best_dir: Vector | None = None

    for ray_dir_world in (n_norm, -n_norm):
        # Start point: slightly above the surface to avoid self-intersection
        origin_world = centroid + ray_dir_world * ray_eps

        # Transform to local mesh space (ray_cast expects local coordinates)
        origin_local = mat_inv @ origin_world
        dir_local = (rot_inv @ ray_dir_world).normalized()

        try:
            hit, loc_local, _, _ = mesh_obj.ray_cast(
                origin_local, dir_local, distance=max_dist
            )
        except Exception:
            continue

        if not hit:
            continue

        loc_world = mat_world @ loc_local
        dist = (loc_world - centroid).length

        # Only accept valid hits
        if min_t <= dist <= max_dist:
            if best_dist is None or dist < best_dist:
                best_dist = dist
                best_dir = Vector(ray_dir_world)

    if best_dist is not None and best_dir is not None:
        return best_dist, best_dir

    # No valid hit → fallback
    return max(min_t, fallback), default_inward


# ---------------------------------------------------------------------------
# Island extraction from a Blender mesh object
# ---------------------------------------------------------------------------

def get_loose_islands(mesh_obj) -> list[list[Vector]]:
    """
    Returns all loose-part islands of a mesh object.
    Each island is a list of world-coordinate vertices.

    No bpy.ops – pure bmesh BFS.

    Parameters
    ----------
    mesh_obj : bpy.types.Object (type='MESH')

    Returns
    -------
    list[list[mathutils.Vector]]
        Each sub-list = one island (world coordinates).
    """
    import bmesh

    bm = bmesh.new()
    bm.from_mesh(mesh_obj.data)
    bm.verts.ensure_lookup_table()

    mat = mesh_obj.matrix_world
    visited: set[int] = set()
    islands: list[list[Vector]] = []

    for start_vert in bm.verts:
        if start_vert.index in visited:
            continue

        # Stack-BFS: collect all connected vertices of this island
        island_indices: list[int] = []
        stack = [start_vert]

        while stack:
            v = stack.pop()
            if v.index in visited:
                continue
            visited.add(v.index)
            island_indices.append(v.index)
            for edge in v.link_edges:
                other = edge.other_vert(v)
                if other.index not in visited:
                    stack.append(other)

        # Local → world coordinates
        island_world_verts = [mat @ bm.verts[i].co for i in island_indices]
        islands.append(island_world_verts)

    bm.free()
    return islands


# ---------------------------------------------------------------------------
# High-level API: all islands of an object → OBBs
# ---------------------------------------------------------------------------

def generate_collision_boxes(
    mesh_obj,
    fallback_thickness: float = 0.01,
    min_thickness: float = 0.01,
    max_raycast_distance: float = 5.0,
) -> list[tuple[Vector, Matrix, Vector]]:
    """
    Generates one OBB per loose-part island of a mesh object.

    Suitable for building meshes that consist of hundreds of disconnected panels
    (walls, floors, stairs) — each panel gets a correctly sized collision box.

    Parameters
    ----------
    mesh_obj              : bpy.types.Object (type='MESH')
    fallback_thickness    : thickness when raycast returns no hit [m]
    min_thickness         : minimum thickness [m]
    max_raycast_distance  : maximum raycast range [m]

    Returns
    -------
    list of (center, rotation, half_extents)
        center       : mathutils.Vector  – world-coordinate centre
        rotation     : mathutils.Matrix  – 3×3 rotation matrix
        half_extents : mathutils.Vector  – half-extents along local axes
    """
    islands = get_loose_islands(mesh_obj)
    results: list[tuple[Vector, Matrix, Vector]] = []

    for verts in islands:
        if len(verts) < 3:
            continue
        obb = compute_island_obb(
            island_verts=verts,
            mesh_obj=mesh_obj,
            fallback_thickness=fallback_thickness,
            min_thickness=min_thickness,
            max_raycast_distance=max_raycast_distance,
        )
        results.append(obb)

    return results


# ---------------------------------------------------------------------------
# Optional: create box as a Blender object (debug visualisation)
# ---------------------------------------------------------------------------

def obb_to_blender_object(
    center: Vector,
    rotation: Matrix,
    half_extents: Vector,
    name: str = "collision_box",
    collection_name: str = "Collision",
) -> None:
    """
    Creates a box mesh object in Blender from an OBB.
    Useful for debug visualisation.
    No bpy.ops – only bmesh + bpy.data.

    Parameters
    ----------
    center       : world-coordinate centre
    rotation     : 3×3 rotation matrix (columns = local axes)
    half_extents : half-extents
    name         : name of the new object
    collection_name : name of the target collection (created if necessary)
    """
    import bpy
    import bmesh

    # 8 corners in local coordinates
    corners_local = [
        Vector((sx * half_extents.x, sy * half_extents.y, sz * half_extents.z))
        for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)
    ]
    # Transform to world coordinates
    corners_world = [center + rotation @ c for c in corners_local]

    # Build bmesh box
    bm = bmesh.new()
    bm_verts = [bm.verts.new(co) for co in corners_world]

    # Faces from the 8 corners (index order: sx, sy, sz = (-1/-1/-1) to (1/1/1))
    #   bit 0: sx, bit 1: sy, bit 2: sz
    face_indices = [
        (0, 1, 3, 2),   # -x face (sx = -1)
        (4, 6, 7, 5),   # +x face (sx = +1)
        (0, 4, 5, 1),   # -y face (sy = -1)
        (2, 3, 7, 6),   # +y face (sy = +1)
        (0, 2, 6, 4),   # -z face (sz = -1)
        (1, 5, 7, 3),   # +z face (sz = +1)
    ]
    for fi in face_indices:
        bm.faces.new([bm_verts[i] for i in fi])

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()

    obj = bpy.data.objects.new(name, mesh)

    # Collection
    if collection_name not in bpy.data.collections:
        coll = bpy.data.collections.new(collection_name)
        bpy.context.scene.collection.children.link(coll)
    else:
        coll = bpy.data.collections[collection_name]
    coll.objects.link(obj)


# ---------------------------------------------------------------------------
# Wall-cluster pairing: merge anti-parallel clusters into wall pairs
# ---------------------------------------------------------------------------

def _pair_wall_clusters(
    clusters: list[tuple[set, "Vector"]],
    vert_positions,
    max_thickness: float = 0.80,
    min_thickness: float = 0.05,
    antiparallel_thresh: float = -0.90,
    min_overlap_frac: float = 0.10,
    fallback_wall: float = 0.25,
    fallback_floor: float = 0.20,
    fallback_ceil: float = 0.15,
) -> tuple[list[tuple[int, int, float]], list[tuple[int, float]]]:
    """
    Pairs anti-parallel face clusters into wall/floor/ceiling pairs and
    returns the respective thickness in metres.

    Algorithm (SDF-inspired, robust against door/window cutouts):
    ─────────────────────────────────────────────────────────────────────
    1.  For each cluster pair (A, B) with dot(N_A, N_B) ≤ -0.9:
        • Project all vertices of both clusters onto N_A.
        • Thickness = median(proj_A) − median(proj_B)   [positive if B is behind A]
        • Tangential overlap (bounding box in the plane ⊥ N_A) must be
          ≥ min_overlap_frac — excludes distant parallel walls.
    2.  Greedy 1:1 assignment: best pairs (highest overlap, preferring
        symmetrically confirmed pairs) win.
    3.  Unpaired clusters receive a fallback value depending on the
        normal orientation (wall / floor / ceiling).

    Parameters
    ---------
    clusters : list of (vert_index_set, seed_normal_world)
        Result of BFS grouping. Each normal component as
        mathutils.Vector or 3-tuple.
    vert_positions : sequence or dict
        World coordinates of the vertices: vert_positions[i] → Vector-like.
    max_thickness : float
        Maximum accepted thickness [m]. Larger than any wall, smaller than
        a room width. Default: 0.80 m.
    min_thickness : float
        Minimum accepted thickness [m]. Default: 0.05 m.
    antiparallel_thresh : float
        dot(N_A, N_B) ≤ this value → considered anti-parallel.
        Default: -0.90  (~154°).
    min_overlap_frac : float
        Minimum fraction of tangential bounding-box overlap in
        both directions (geometric mean). Default: 0.10.
    fallback_wall / fallback_floor / fallback_ceil : float
        Fallback thickness for unpaired clusters [m].

    Returns
    --------
    paired : list of (idx_A, idx_B, thickness_m)
        idx_A/idx_B are indices into ``clusters``.
        N_A points from B to A (outward direction for the idx_A cluster).
    unpaired : list of (idx, fallback_thickness_m)
        Clusters without a matching opposite cluster.

    Mathematical background
    --------------------------
    The median projection is equivalent to the Shape Diameter Function (SDF)
    of Shapira et al. 2008, simplified to cluster level instead of per-face.
    It is more robust than centroid-to-centroid distance:
        • Cutouts (doors, windows) shift the centroid tangentially, but
          the median projection onto N remains stable.
        • Edge outliers are dampened by the median.
    """
    n = len(clusters)
    if n == 0:
        return [], []

    # ------------------------------------------------------------------
    # Helper functions: keep vectors usable without mathutils dependency
    # outside Blender
    # ------------------------------------------------------------------
    def _as_vec(v):
        """Returns a numpy array (3,) float64."""
        if hasattr(v, "to_tuple"):          # mathutils.Vector
            return np.array(v.to_tuple(), dtype=np.float64)
        return np.array(v, dtype=np.float64)

    def _normalize(v):
        n_ = np.linalg.norm(v)
        return v / n_ if n_ > 1e-10 else v

    def _tangent_frame(N):
        """Two unit vectors that together with N form an ONB."""
        up = np.array([0.0, 0.0, 1.0])
        if abs(np.dot(N, up)) > 0.85:
            up = np.array([1.0, 0.0, 0.0])
        t1 = _normalize(np.cross(N, up))
        t2 = np.cross(N, t1)           # bereits normiert, da N⊥t1
        return t1, t2

    # ------------------------------------------------------------------
    # 1. Pre-compute cluster properties
    # ------------------------------------------------------------------
    normals:   list[np.ndarray] = []   # (3,) unit
    verts_pos: list[np.ndarray] = []   # (k, 3) world coordinates

    for vset, raw_normal in clusters:
        N = _normalize(_as_vec(raw_normal))
        normals.append(N)
        pos = np.array([_as_vec(vert_positions[i]) for i in vset], dtype=np.float64)
        verts_pos.append(pos)

    # ------------------------------------------------------------------
    # 2. Compute candidate scores
    #    score_ab[a][b] = (thickness, overlap_frac) or None
    # ------------------------------------------------------------------
    score_ab: list[list[tuple[float, float] | None]] = [
        [None] * n for _ in range(n)
    ]

    for a in range(n):
        N_a    = normals[a]
        pos_a  = verts_pos[a]
        proj_a = pos_a @ N_a                   # Projektion aller A-Verts auf N_a
        med_a  = float(np.median(proj_a))

        t1_a, t2_a = _tangent_frame(N_a)
        tang_a_u = pos_a @ t1_a
        tang_a_v = pos_a @ t2_a
        span_u_a = max(float(tang_a_u.max() - tang_a_u.min()), 1e-6)
        span_v_a = max(float(tang_a_v.max() - tang_a_v.min()), 1e-6)

        for b in range(n):
            if b == a:
                continue
            if np.dot(N_a, normals[b]) > antiparallel_thresh:
                continue                                # not anti-parallel

            pos_b  = verts_pos[b]
            proj_b = pos_b @ N_a                       # project B onto N_a
            med_b  = float(np.median(proj_b))

            # B must lie "behind" A (in -N_a direction)
            thickness = med_a - med_b
            if not (min_thickness <= thickness <= max_thickness):
                continue

            # Tangential bounding-box overlap (in A's frame)
            tang_b_u = pos_b @ t1_a
            tang_b_v = pos_b @ t2_a

            def _ov1d(a_min, a_max, b_min, b_max, span):
                ov = min(a_max, b_max) - max(a_min, b_min)
                return max(0.0, ov) / span

            ov_u = _ov1d(
                tang_a_u.min(), tang_a_u.max(),
                tang_b_u.min(), tang_b_u.max(),
                span_u_a,
            )
            ov_v = _ov1d(
                tang_a_v.min(), tang_a_v.max(),
                tang_b_v.min(), tang_b_v.max(),
                span_v_a,
            )
            # Geometric mean → 1.0 only for complete overlap
            overlap = float(np.sqrt(ov_u * ov_v))
            if overlap < min_overlap_frac:
                continue

            score_ab[a][b] = (thickness, overlap)

    # ------------------------------------------------------------------
    # 3. Build candidate list – prefer symmetrically confirmed pairs
    #    Priority score: overlap + 1 (if symmetrically confirmed)
    # ------------------------------------------------------------------
    seen: set[tuple[int, int]] = set()
    candidates: list[tuple[float, int, int, float]] = []  # (priority, a, b, t)

    for a in range(n):
        for b in range(n):
            if score_ab[a][b] is None:
                continue
            key = (min(a, b), max(a, b))
            if key in seen:
                continue
            seen.add(key)

            t_ab, ov_ab = score_ab[a][b]
            sym = score_ab[b][a] is not None

            if sym:
                t_ba, ov_ba = score_ab[b][a]
                thickness = (t_ab + t_ba) * 0.5
                overlap   = (ov_ab + ov_ba) * 0.5
            else:
                thickness = t_ab
                overlap   = ov_ab

            priority = overlap + (1.0 if sym else 0.0)
            candidates.append((priority, a, b, thickness))

    # Best pairs first
    candidates.sort(key=lambda x: -x[0])

    # ------------------------------------------------------------------
    # 4. Greedy 1:1 assignment
    # ------------------------------------------------------------------
    used: set[int] = set()
    paired: list[tuple[int, int, float]] = []

    for priority, a, b, thickness in candidates:
        if a in used or b in used:
            continue

        # Determine which cluster is "outer":
        # Outer cluster: its centroid projects further along its own normal
        # (larger projection centroid·N).
        c_a = float(np.mean(verts_pos[a] @ normals[a]))
        c_b = float(np.mean(verts_pos[b] @ normals[a]))  # both projected onto N_a

        # c_a > c_b → a is outer (N_a points outward)
        # c_a < c_b → b is outer, but since N_b ≈ -N_a, b lies "in front of" a
        #             → swap so that idx_A is always the outer cluster
        if c_a >= c_b:
            outer, inner = a, b
        else:
            outer, inner = b, a

        paired.append((outer, inner, round(thickness, 4)))
        used.add(a)
        used.add(b)

    # ------------------------------------------------------------------
    # 5. Fallback for unpaired clusters
    # ------------------------------------------------------------------
    unpaired: list[tuple[int, float]] = []
    for idx in range(n):
        if idx in used:
            continue
        nz = float(normals[idx][2])
        if abs(nz) > 0.70:
            fb = fallback_floor if nz > 0.0 else fallback_ceil
        else:
            fb = fallback_wall
        unpaired.append((idx, fb))

    return paired, unpaired
