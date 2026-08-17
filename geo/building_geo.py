"""

CDM Collider — mesh-driven building Geo LOD.



Phase 1: Boden (Mesh) + Dach (geneigte Faces) + kleine Solids.

Phase 2: Wände (±X/±Y, Innen/Außen-Paarung).

Component-Anzahl = Ergebnis der Messung, kein Zielwert.

"""

import math

from collections import defaultdict



import mathutils



from .islands import (

    classify_mesh_islands,

    get_evaluated_bmesh,

    iter_mesh_islands,

    _island_is_closed,

)

from .clustering import _cluster_by_face_angle_faces, _obb_corners_tight



SKIN_M = 0.001

MIN_WALL_DEPTH_M = 0.01

MAX_WALL_DEPTH_M = 1.2

MIN_WALL_OVERLAP = 0.08

MIN_WALL_THIN_M = 0.12

MAX_WALL_THIN_M = 1.2

WALL_NORMAL_MIN = 0.70

COPLANAR_DEG = 12.0

MIN_BOX_EXTENT_M = 0.10

ROOF_MIN_FOOTPRINT_M2 = 2.0

CHIMNEY_MAX_EXTENT_M = 4.0





def _world_aabb_corners(bmin, bmax):

    x0, y0, z0 = bmin.x, bmin.y, bmin.z

    x1, y1, z1 = bmax.x, bmax.y, bmax.z

    return [

        mathutils.Vector((x0, y0, z0)), mathutils.Vector((x1, y0, z0)),

        mathutils.Vector((x1, y1, z0)), mathutils.Vector((x0, y1, z0)),

        mathutils.Vector((x0, y0, z1)), mathutils.Vector((x1, y0, z1)),

        mathutils.Vector((x1, y1, z1)), mathutils.Vector((x0, y1, z1)),

    ]





def _aabb_from_verts(verts, margin=SKIN_M):

    if not verts:

        return []

    xs = [v.x for v in verts]

    ys = [v.y for v in verts]

    zs = [v.z for v in verts]

    bmin = mathutils.Vector((min(xs) - margin, min(ys) - margin, min(zs) - margin))

    bmax = mathutils.Vector((max(xs) + margin, max(ys) + margin, max(zs) + margin))

    return _world_aabb_corners(bmin, bmax)





def _extents(verts):

    xs = [v.x for v in verts]

    ys = [v.y for v in verts]

    zs = [v.z for v in verts]

    return max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)





def _footprint_area_2d(verts, drop_axis='z'):

    if drop_axis == 'z':

        a = _extents(verts)

        return a[0] * a[1]

    if drop_axis == 'y':

        ex, _, ez = _extents(verts)

        return ex * ez

    _, ey, ez = _extents(verts)

    return ey * ez





def _is_valid_box(corners, wall=False, roof=False):

    if len(corners) != 8:

        return False

    ex, ey, ez = _extents(corners)

    thin = min(ex, ey, ez)

    if thin < MIN_BOX_EXTENT_M:

        return False

    if wall:

        if thin < MIN_WALL_THIN_M or thin > MAX_WALL_THIN_M:

            return False

        if ez < 0.6 and ex > 2.5 and ey > 2.5:

            return False

    if roof:

        if max(ex * ey, ey * ez, ex * ez) < ROOF_MIN_FOOTPRINT_M2:

            return False

    return True





def _face_world_normal(bm, mw, face):

    nm = mw.to_3x3().inverted_safe().transposed()

    return (nm @ face.normal).normalized()





def _wall_bin_index(wn):

    """Nur echte vertikale Wände ±X/±Y — kein Dach, kein Boden."""

    ax = abs(wn.x)

    ay = abs(wn.y)

    az = abs(wn.z)

    if ax >= WALL_NORMAL_MIN and ax >= ay and ax >= az:

        return 0 if wn.x > 0 else 1

    if ay >= WALL_NORMAL_MIN and ay >= ax and ay >= az:

        return 2 if wn.y > 0 else 3

    return None





def _is_roof_face(wn):
    """Geneigtes Dach — nicht Wand (±X/±Y), nicht horizontal (±Z)."""
    ax, ay, az = abs(wn.x), abs(wn.y), abs(wn.z)
    if az >= 0.92 and az >= ax and az >= ay:
        return False
    if ax >= 0.85 and ax >= ay and ax >= az:
        return False
    if ay >= 0.85 and ay >= ax and ay >= az:
        return False
    return 0.25 <= az <= 0.97





def _cluster_coplanar(bm, mw, face_indices, angle_deg=COPLANAR_DEG):

    if not face_indices:

        return []

    cos_t = math.cos(math.radians(angle_deg))

    allowed = set(face_indices)

    fn = [_face_world_normal(bm, mw, f) for f in bm.faces]

    visited = set()

    clusters = []



    for seed_i in face_indices:

        if seed_i in visited:

            continue

        stack = [bm.faces[seed_i]]

        comp = []

        while stack:

            face = stack.pop()

            if face.index in visited:

                continue

            visited.add(face.index)

            comp.append(face)

            sn = fn[face.index]

            for edge in face.edges:

                for nb in edge.link_faces:

                    if nb.index not in allowed or nb.index in visited:

                        continue

                    if sn.dot(fn[nb.index]) >= cos_t:

                        stack.append(nb)



        vert_set = set()

        wn_sum = mathutils.Vector((0.0, 0.0, 0.0))

        area_sum = 0.0

        for face in comp:

            a = face.calc_area()

            wn_sum += fn[face.index] * a

            area_sum += a

            for v in face.verts:

                vert_set.add(v.index)

        if len(vert_set) < 3 or area_sum < 1e-6:

            continue

        wv = [mw @ bm.verts[vi].co.copy() for vi in vert_set]

        avg_n = wn_sum.normalized() if wn_sum.length > 1e-6 else fn[comp[0].index]

        centroid = sum(wv, mathutils.Vector()) / len(wv)

        clusters.append((wv, avg_n, centroid))

    return clusters





def _wall_tangent_frame(axis_is_x):

    if axis_is_x:

        u = mathutils.Vector((0.0, 1.0, 0.0))

        v = mathutils.Vector((0.0, 0.0, 1.0))

    else:

        u = mathutils.Vector((1.0, 0.0, 0.0))

        v = mathutils.Vector((0.0, 0.0, 1.0))

    return u, v





def _footprint_overlap(pa, pb, origin, u, v):

    ua = [(p - origin).dot(u) for p in pa]

    va = [(p - origin).dot(v) for p in pa]

    ub = [(p - origin).dot(u) for p in pb]

    vb = [(p - origin).dot(v) for p in pb]

    au0, au1, av0, av1 = min(ua), max(ua), min(va), max(va)

    bu0, bu1, bv0, bv1 = min(ub), max(ub), min(vb), max(vb)

    ou = max(0.0, min(au1, bu1) - max(au0, bu0))

    ov = max(0.0, min(av1, bv1) - max(av0, bv0))

    overlap = ou * ov

    area_a = max(au1 - au0, 1e-6) * max(av1 - av0, 1e-6)

    area_b = max(bu1 - bu0, 1e-6) * max(bv1 - bv0, 1e-6)

    return overlap / min(area_a, area_b)





def _pair_wall_patches(pos_clusters, neg_clusters, axis_is_x):

    u, v = _wall_tangent_frame(axis_is_x)

    used_neg = set()

    boxes = []



    for vp, np_pos, cp in pos_clusters:

        best_j = None

        best_ov = 0.0

        for j, (vn, nn, cn) in enumerate(neg_clusters):

            if j in used_neg:

                continue

            if np_pos.dot(nn) > -0.5:

                continue

            depth = abs((cp - cn).dot(np_pos))

            if depth < MIN_WALL_DEPTH_M or depth > MAX_WALL_DEPTH_M:

                continue

            ov = _footprint_overlap(vp, vn, cp, u, v)

            if ov >= MIN_WALL_OVERLAP and ov > best_ov:

                best_ov = ov

                best_j = j

        if best_j is None:

            continue

        used_neg.add(best_j)

        vn, _, _ = neg_clusters[best_j]

        corners = _aabb_from_verts(vp + vn)

        if _is_valid_box(corners, wall=True):

            boxes.append(corners)

    return boxes





def _classify_open_wall_faces(bm, mw, open_indices):

    bins = {0: [], 1: [], 2: [], 3: []}

    for face in bm.faces:

        if face.index not in open_indices:

            continue

        bi = _wall_bin_index(_face_world_normal(bm, mw, face))

        if bi is not None:

            bins[bi].append(face.index)

    return bins





def measure_wall_boxes(bm, mw, open_face_indices, min_area_m2=0.02):

    bins = _classify_open_wall_faces(bm, mw, set(open_face_indices))

    boxes = []

    for pos_bin, neg_bin, axis_is_x in ((0, 1, True), (2, 3, False)):

        pos_c = _cluster_coplanar(bm, mw, bins[pos_bin])

        neg_c = _cluster_coplanar(bm, mw, bins[neg_bin])

        if min_area_m2 > 0:

            pos_c = [c for c in pos_c if _footprint_area_2d(c[0]) >= min_area_m2]

            neg_c = [c for c in neg_c if _footprint_area_2d(c[0]) >= min_area_m2]

        boxes.extend(_pair_wall_patches(pos_c, neg_c, axis_is_x))

    return _dedupe_boxes(boxes)





def _dedupe_boxes(boxes):

    seen = []

    out = []

    for corners in boxes:

        key = tuple(round(c, 4) for v in corners for c in v)

        if key in seen:

            continue

        seen.append(key)

        out.append(corners)

    return out





def _dedupe_tagged_boxes(boxes):

    seen = []

    out = []

    for item in boxes:

        corners, tag = item

        key = tuple(round(c, 4) for v in corners for c in v)

        if key in seen:

            continue

        seen.append(key)

        out.append(item)

    return out





def _detect_floor_box(bm, mw, z_extend_m=0.0):

    """Boden: voller Grundriss, Z von Mesh-Unterkante bis Bodenoberkante."""

    world_verts = [mw @ v.co.copy() for v in bm.verts]

    if not world_verts:

        return []

    z_min = min(v.z for v in world_verts)

    z_top = z_min + 0.35

    for face in bm.faces:

        wn = _face_world_normal(bm, mw, face)

        if wn.z < 0.85:

            continue

        cz = (mw @ face.calc_center_median()).z

        if cz <= z_top + 0.05:

            z_top = max(z_top, cz)

    floor_verts = [v for v in world_verts if v.z <= z_top + SKIN_M]

    if z_extend_m > 0.0:

        xs = [v.x for v in floor_verts]

        ys = [v.y for v in floor_verts]

        z_lo = z_min - z_extend_m

        for x in (min(xs), max(xs)):

            for y in (min(ys), max(ys)):

                floor_verts.append(mathutils.Vector((x, y, z_lo)))

    corners = _aabb_from_verts(floor_verts)

    return corners if _is_valid_box(corners) else []





def _collect_roof_face_indices(bm, mw):

    """Geneigte Dachflächen — aus dem ganzen Mesh (meist offene Hülle)."""

    indices = []

    for face in bm.faces:

        if _is_roof_face(_face_world_normal(bm, mw, face)):

            indices.append(face.index)

    return indices





def measure_roof_boxes(bm, mw, angle_threshold_deg=30.0, min_area_m2=0.5):

    """Dach-OBBs aus geneigten Faces (nicht aus geschlossenen Islands)."""

    roof_faces = _collect_roof_face_indices(bm, mw)

    if not roof_faces:

        return []



    clusters = _cluster_by_face_angle_faces(

        bm, mw, roof_faces,

        angle_threshold_deg=angle_threshold_deg,

        min_area_m2=min_area_m2)



    boxes = []

    for wv, avg_n in clusters:

        corners = _obb_corners_tight(wv, avg_n, margin=SKIN_M)

        if len(corners) == 8 and _is_valid_box(corners, roof=True):

            boxes.append((corners, False))

    return _dedupe_tagged_boxes(boxes)





def measure_chimney_boxes(bm, mw, angle_threshold_deg=30.0, min_area_m2=0.25):

    """Kleine geschlossene Volumen (Schornstein …), kein Dach-Ersatz."""

    boxes = []

    for _iv, island_faces, face_ids in iter_mesh_islands(bm):

        if not _island_is_closed(island_faces, face_ids):

            continue

        fi = [f.index for f in island_faces]

        vert_set = set()

        for fidx in fi:

            for v in bm.faces[fidx].verts:

                vert_set.add(v.index)

        wv = [mw @ bm.verts[vi].co.copy() for vi in vert_set]

        ex, ey, ez = _extents(wv)

        if max(ex, ey, ez) > CHIMNEY_MAX_EXTENT_M:

            continue



        clusters = _cluster_by_face_angle_faces(

            bm, mw, fi, angle_threshold_deg=angle_threshold_deg,

            min_area_m2=min_area_m2)

        for cv, avg_n in clusters:

            n = avg_n.normalized()

            if abs(n.z) < 0.85:

                corners = _obb_corners_tight(cv, avg_n, margin=SKIN_M)

                use_aabb = False

            else:

                if _footprint_area_2d(cv) > 9.0:

                    continue

                corners = _aabb_from_verts(cv)

                use_aabb = True

            if len(corners) == 8 and _is_valid_box(corners):

                boxes.append((corners, use_aabb))

    return _dedupe_tagged_boxes(boxes)





def generate_phase1_boxes(obj, angle_threshold_deg=30.0, min_area_m2=0.25,

                          floor_z_extend_m=0.0):

    bm, mw = get_evaluated_bmesh(obj)

    boxes = []



    floor = _detect_floor_box(bm, mw, z_extend_m=floor_z_extend_m)

    if floor:

        boxes.append((floor, True))



    roof_face_count = len(_collect_roof_face_indices(bm, mw))
    roof_min = max(min_area_m2, 0.5)
    roof_boxes = measure_roof_boxes(
        bm, mw, angle_threshold_deg=angle_threshold_deg, min_area_m2=roof_min)
    boxes.extend(roof_boxes)
    chimney_boxes = measure_chimney_boxes(
        bm, mw, angle_threshold_deg=angle_threshold_deg, min_area_m2=min_area_m2)
    boxes.extend(chimney_boxes)

    bm.free()
    _c, _o, island_stats = classify_mesh_islands(obj, evaluated=True)
    stats = {
        **island_stats,
        'phase1_total': len(boxes),
        'floor': bool(floor),
        'roof_faces': roof_face_count,
        'roof_boxes': len(roof_boxes),
        'chimney_boxes': len(chimney_boxes),
    }
    return boxes, stats





def generate_phase2_boxes(obj, min_area_m2=0.02, angle_threshold_deg=30.0):

    _ = angle_threshold_deg

    _closed, open_faces, island_stats = classify_mesh_islands(obj, evaluated=True)

    boxes = []

    if open_faces:

        bm, mw = get_evaluated_bmesh(obj)

        boxes = measure_wall_boxes(bm, mw, open_faces, min_area_m2=min_area_m2)

        bm.free()

    stats = {**island_stats, 'phase2_total': len(boxes), 'open_faces': len(open_faces)}

    return boxes, stats


