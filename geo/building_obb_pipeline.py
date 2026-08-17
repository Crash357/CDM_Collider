"""
CDM Collider — 3-Phasen Building-Pipeline (Angle-Split → OBB → Finalize).

Phase 1: Mesh-Islands nach Flächenwinkel in logische Sub-Patches trennen.
Phase 2: Pro Patch eine OBB entlang der größten Flächen-Normale.
Phase 3: Join, Knife-Intersect, Innenflächen löschen, Merge by Distance.
"""
import math
from collections import deque

import bmesh
import bpy
import mathutils

from .constants import BOX_FACES
from .islands import get_evaluated_bmesh, iter_mesh_islands
from .helpers import (
    ensure_object_mode,
    get_or_create_collection,
    move_to_collection,
    _apply_geo_display,
    _centre_component_origin,
    ensure_outward_normals,
)

SKIN_M = 0.001
MIN_PATCH_AREA_M2 = 0.05
MIN_OBB_FOOTPRINT_M = 0.08
WALL_SLAB_M = 0.15
HORIZ_SLAB_M = 0.12

# Phase-1-Patches pro Gebäude-Name (für Phase 2: 1 Patch → 1 Box)
_patch_cache = {}


def _face_world_normal(bm, mw, face):
    nm = mw.to_3x3().inverted_safe().transposed()
    return (nm @ face.normal).normalized()


def _cluster_area_m2(bm, mw, face_indices):
    det = abs(mw.to_3x3().determinant())
    scale = det ** (2.0 / 3.0) if det > 1e-10 else 1.0
    return sum(bm.faces[i].calc_area() for i in face_indices) * scale


def split_islands_by_angle(bm, mw, angle_threshold_deg=30.0, min_area_m2=MIN_PATCH_AREA_M2):
    """
    Phase 1 — pro Mesh-Island Winkel-Cluster (Wand ≠ Dach an Kante).
    Returns list of dicts: {face_indices, world_verts, area_m2}
    """
    cos_thresh = math.cos(math.radians(angle_threshold_deg))
    face_normals = [_face_world_normal(bm, mw, f) for f in bm.faces]
    patches = []

    for _iv, island_faces, _face_ids in iter_mesh_islands(bm):
        if len(island_faces) < 1:
            continue

        allowed = {f.index for f in island_faces}
        visited = set()

        for seed in island_faces:
            if seed.index in visited:
                continue

            cluster_faces = []
            queue = deque([seed])
            visited.add(seed.index)

            while queue:
                face = queue.popleft()
                cluster_faces.append(face)
                fn = face_normals[face.index]

                for edge in face.edges:
                    for nb in edge.link_faces:
                        if nb.index not in allowed or nb.index in visited:
                            continue
                        if fn.dot(face_normals[nb.index]) >= cos_thresh:
                            visited.add(nb.index)
                            queue.append(nb)

            face_indices = [f.index for f in cluster_faces]
            area = _cluster_area_m2(bm, mw, face_indices)
            if area < min_area_m2:
                continue

            vert_set = set()
            for fi in face_indices:
                for v in bm.faces[fi].verts:
                    vert_set.add(v.index)
            world_verts = [mw @ bm.verts[vi].co.copy() for vi in vert_set]
            patches.append({
                'face_indices': face_indices,
                'world_verts': world_verts,
                'area_m2': area,
            })

    return patches


def _patch_kind(normal):
    n = mathutils.Vector(normal).normalized()
    ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
    if az >= 0.85 and az >= ax and az >= ay:
        return 'horizontal'
    if ax >= 0.70 and ax >= ay and ax >= az:
        return 'wall'
    if ay >= 0.70 and ay >= ax and ay >= az:
        return 'wall'
    return 'sloped'


def _dominant_face_normal(bm, mw, face_indices):
    """Hauptachse = Normale der größten Einzelfläche im Patch."""
    best_n = mathutils.Vector((0.0, 0.0, 1.0))
    best_a = 0.0
    det = abs(mw.to_3x3().determinant())
    scale = det ** (2.0 / 3.0) if det > 1e-10 else 1.0
    for fi in face_indices:
        face = bm.faces[fi]
        a = face.calc_area() * scale
        if a > best_a:
            best_a = a
            best_n = _face_world_normal(bm, mw, face)
    return best_n


def _obb_basis(normal):
    """Stabiles N/U/V-Koordinatensystem für OBB."""
    n = mathutils.Vector(normal).normalized()
    ref = mathutils.Vector((0.0, 0.0, 1.0))
    if abs(n.dot(ref)) > 0.999:
        ref = mathutils.Vector((1.0, 0.0, 0.0))
    u = (ref - ref.dot(n) * n).normalized()
    v = n.cross(u).normalized()
    return n, u, v


def _build_box_bmesh(n, u, v, n_lo, n_hi, u_lo, u_hi, v_lo, v_hi):
    """8 Vertices in BOX_FACES-Reihenfolge → 6 gültige Quaderflächen."""
    specs = (
        (n_lo, u_lo, v_lo), (n_lo, u_lo, v_hi), (n_lo, u_hi, v_lo), (n_lo, u_hi, v_hi),
        (n_hi, u_lo, v_lo), (n_hi, u_lo, v_hi), (n_hi, u_hi, v_lo), (n_hi, u_hi, v_hi),
    )
    bm = bmesh.new()
    verts = [bm.verts.new(sn * n + su * u + sv * v) for sn, su, sv in specs]
    bm.verts.ensure_lookup_table()
    for fi in BOX_FACES:
        try:
            face = bm.faces.new([verts[i] for i in fi])
        except ValueError:
            bm.free()
            return None
        if face.calc_area() < 1e-10:
            bm.free()
            return None
    return bm


def _build_patch_box_bmesh(world_verts, normal):
    """
    Patch-Vertices → 8V-Box (Weltkoordinaten).
    Wand: 15 cm in Normalenrichtung. Schräge: Skin. Horizontal: min. 12 cm.
    Note: Face-Größe-Check erfolgt in _build_box_bmesh(); kein Footprint-Limit hier.
    """
    if not world_verts:
        return None

    kind = _patch_kind(normal)
    n, u, v = _obb_basis(normal)
    n_p = [p.dot(n) for p in world_verts]
    u_p = [p.dot(u) for p in world_verts]
    v_p = [p.dot(v) for p in world_verts]

    u_lo, u_hi = min(u_p) - SKIN_M, max(u_p) + SKIN_M
    v_lo, v_hi = min(v_p) - SKIN_M, max(v_p) + SKIN_M

    ns = sorted(n_p)
    if kind == 'wall':
        n_q1 = ns[max(0, len(ns) // 4)]
        n_q3 = ns[min(len(ns) - 1, 3 * len(ns) // 4)]
        n_surface = (n_q1 + n_q3) * 0.5
        n_hi = n_surface + SKIN_M
        n_lo = n_surface - max(n_q3 - n_q1, WALL_SLAB_M)
    elif kind == 'horizontal':
        n_lo, n_hi = min(n_p) - SKIN_M, max(n_p) + SKIN_M
        if n_hi - n_lo < HORIZ_SLAB_M:
            mid = (n_lo + n_hi) * 0.5
            n_lo, n_hi = mid - HORIZ_SLAB_M * 0.5, mid + HORIZ_SLAB_M * 0.5
    else:
        n_lo, n_hi = min(n_p) - SKIN_M, max(n_p) + SKIN_M
        if n_hi - n_lo < SKIN_M * 2:
            mid = (n_lo + n_hi) * 0.5
            n_lo, n_hi = mid - SKIN_M, mid + SKIN_M

    return _build_box_bmesh(n, u, v, n_lo, n_hi, u_lo, u_hi, v_lo, v_hi)


def _patch_obj_world_data(obj):
    """Phase-1-Patch-Objekt → Welt-Vertices + flächengewichtete Normale."""
    mesh = obj.data
    if not mesh.vertices or not mesh.polygons:
        return None, None

    mw = obj.matrix_world
    world_verts = [(mw @ v.co).copy() for v in mesh.vertices]
    nm = mw.to_3x3().inverted_safe().transposed()
    total = mathutils.Vector((0.0, 0.0, 0.0))
    area_sum = 0.0
    for poly in mesh.polygons:
        wn = (nm @ poly.normal).normalized()
        a = poly.area
        total += wn * a
        area_sum += a
    if area_sum < 1e-12:
        return None, None
    return world_verts, total.normalized()


def _build_obb_bmesh(world_verts, normal, margin=SKIN_M):
    """Legacy — delegiert an _build_patch_box_bmesh."""
    _ = margin
    return _build_patch_box_bmesh(world_verts, normal)


def _emit_bmesh_component(bm, comp_name):
    ensure_outward_normals(bm)
    mesh = bpy.data.meshes.new(comp_name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.validate()
    mesh.update()
    comp_obj = bpy.data.objects.new(comp_name, mesh)
    move_to_collection(comp_obj, 'GEO_Components')
    _centre_component_origin(comp_obj)
    _apply_geo_display(comp_obj, is_component=True)
    return comp_obj


def emit_boxes_from_sub_islands():
    """
    Phase 2 — 1:1 aus GEO_SubIslands (exakt das, was Schritt 1 zeigt).
    Jedes Patch-Mesh → eine 8-Vertex-Box.
    """
    col = bpy.data.collections.get('GEO_SubIslands')
    if not col:
        return 0, {'patches': 0, 'emitted': 0, 'skipped': 0, 'source': 'none'}

    patch_objs = sorted(
        [o for o in col.objects if o.type == 'MESH' and len(o.data.polygons) > 0],
        key=lambda o: o.name,
    )

    idx = 1
    skipped = 0
    for pobj in patch_objs:
        world_verts, normal = _patch_obj_world_data(pobj)
        if not world_verts or normal is None:
            skipped += 1
            continue
        box_bm = _build_patch_box_bmesh(world_verts, normal)
        if not box_bm:
            skipped += 1
            continue
        _emit_bmesh_component(box_bm, "Component{:02d}".format(idx))
        idx += 1

    count = idx - 1
    return count, {
        'patches': len(patch_objs),
        'emitted': count,
        'skipped': skipped,
        'source': 'GEO_SubIslands',
    }


def emit_obb_components_from_patches(obj, patches, add_floor=True):
    """Fallback wenn GEO_SubIslands fehlt — gleiche Box-Logik aus Cache."""
    _ = obj, add_floor
    idx = 1
    skipped = 0
    for patch in patches:
        box_bm = _build_patch_box_bmesh(patch['world_verts'], patch['normal'])
        if not box_bm:
            skipped += 1
            continue
        _emit_bmesh_component(box_bm, "Component{:02d}".format(idx))
        idx += 1
    count = idx - 1
    return count, {
        'patches': len(patches),
        'emitted': count,
        'skipped': skipped,
        'source': 'cache',
    }


def _cache_patches(obj_name, patches, bm, mw):
    cached = []
    for p in patches:
        cached.append({
            'world_verts': [v.copy() for v in p['world_verts']],
            'area_m2': p['area_m2'],
            'normal': _dominant_face_normal(bm, mw, p['face_indices']).copy(),
        })
    _patch_cache[obj_name] = cached
    return cached


def get_cached_patches(obj_name):
    return _patch_cache.get(obj_name, [])


def _patch_to_mesh(patch, bm, mw):
    """Sub-Island als Welt-Mesh (für Preview-Collection)."""
    old_to_new = {}
    world_verts = []
    for fi in patch['face_indices']:
        for v in bm.faces[fi].verts:
            if v.index not in old_to_new:
                old_to_new[v.index] = len(world_verts)
                world_verts.append((mw @ v.co.copy())[:])
    face_idx = []
    for fi in patch['face_indices']:
        face_idx.append([old_to_new[v.index] for v in bm.faces[fi].verts])
    return world_verts, face_idx


def _clear_collection(name):
    if name not in bpy.data.collections:
        return
    col = bpy.data.collections[name]
    for obj in list(col.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def analyze_building_patches(obj, angle_threshold_deg=30.0, min_area_m2=MIN_PATCH_AREA_M2):
    bm, mw = get_evaluated_bmesh(obj)
    patches = split_islands_by_angle(
        bm, mw, angle_threshold_deg=angle_threshold_deg, min_area_m2=min_area_m2)
    stats = {
        'patches': len(patches),
        'islands': sum(1 for _ in iter_mesh_islands(bm)),
        'total_area_m2': sum(p['area_m2'] for p in patches),
    }
    _cache_patches(obj.name, patches, bm, mw)
    return patches, stats, bm, mw


def _emit_patch_object(world_verts, face_idx_lists, name, collection_name):
    tmp_bm = bmesh.new()
    bm_verts = [tmp_bm.verts.new(mathutils.Vector(v)) for v in world_verts]
    tmp_bm.verts.ensure_lookup_table()
    for fi in face_idx_lists:
        try:
            tmp_bm.faces.new([bm_verts[i] for i in fi])
        except ValueError:
            pass
    mesh = bpy.data.meshes.new(name)
    tmp_bm.to_mesh(mesh)
    tmp_bm.free()
    mesh.validate()
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    move_to_collection(obj, collection_name)
    _apply_geo_display(obj, is_component=True)
    return obj


def emit_patch_preview(patches, bm, mw, collection_name='GEO_SubIslands'):
    """Phase 1 — flache Sub-Patches als Mesh-Preview."""
    _clear_collection(collection_name)
    get_or_create_collection(collection_name)
    count = 0
    for i, patch in enumerate(patches, start=1):
        wv, fi = _patch_to_mesh(patch, bm, mw)
        if len(wv) < 4 or not fi:
            continue
        _emit_patch_object(wv, fi, "Patch{:03d}".format(i), collection_name)
        count += 1
    return count


def generate_obb_boxes_from_patches(obj, patches, add_floor=True):
    """Legacy-Wrapper — delegiert an emit_obb_components_from_patches."""
    count, stats = emit_obb_components_from_patches(obj, patches, add_floor=add_floor)
    stats['obb_boxes'] = count
    stats['from_cache'] = True
    return [], stats


def generate_obb_boxes(obj, angle_threshold_deg=30.0, min_area_m2=MIN_PATCH_AREA_M2):
    """Fallback: Split + OBB wenn Phase 1 nicht gelaufen."""
    cached = get_cached_patches(obj.name)
    if cached:
        return generate_obb_boxes_from_patches(obj, cached, add_floor=True)

    bm, mw = get_evaluated_bmesh(obj)
    patches = split_islands_by_angle(
        bm, mw, angle_threshold_deg=angle_threshold_deg, min_area_m2=min_area_m2)
    cached = _cache_patches(obj.name, patches, bm, mw)
    bm.free()
    return generate_obb_boxes_from_patches(obj, cached, add_floor=True)


def _rebuild_vertex_groups_from_loose_parts(obj):
    """Nach Cleanup: lose Teile → ComponentXX Vertex Groups."""
    mesh = obj.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()

    for vg in list(obj.vertex_groups):
        obj.vertex_groups.remove(vg)

    visited = set()
    comp_idx = 1
    for seed in bm.verts:
        if seed.index in visited:
            continue
        stack = [seed]
        indices = []
        while stack:
            v = stack.pop()
            if v.index in visited:
                continue
            visited.add(v.index)
            indices.append(v.index)
            for e in v.link_edges:
                o = e.other_vert(v)
                if o.index not in visited:
                    stack.append(o)
        if len(indices) < 4:
            continue
        vg = obj.vertex_groups.new(name="Component{:02d}".format(comp_idx))
        vg.add(indices, 1.0, 'REPLACE')
        comp_idx += 1

    bm.free()
    mesh.update()
    return comp_idx - 1


def finalize_geometry_lod(operator, merge_distance=0.001):
    """
    Phase 3 — Join (Exact), Knife-Intersect, Innenflächen weg, Merge, Re-Island.
    Erwartet GEO_Components; erzeugt 'Geometry'.
    """
    from .merge import create_geometry_merge_exact

    ensure_object_mode()
    geo_obj = create_geometry_merge_exact(operator)
    if not geo_obj:
        return None

    bpy.context.view_layer.objects.active = geo_obj
    geo_obj.select_set(True)

    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')

    try:
        bpy.ops.mesh.intersect(mode='SELECT_UNSELECT', separate_mode='NONE')
    except RuntimeError:
        try:
            bpy.ops.mesh.intersect(mode='SELECT', separate_mode='NONE')
        except RuntimeError as exc:
            operator.report({'WARNING'},
                            "Intersect übersprungen: {}".format(exc))

    bpy.ops.mesh.select_all(action='DESELECT')
    try:
        bpy.ops.mesh.select_interior_faces()
        bpy.ops.mesh.delete(type='FACE')
    except RuntimeError:
        pass

    bpy.ops.object.mode_set(mode='OBJECT')

    mesh = geo_obj.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=merge_distance)
    bm.to_mesh(mesh)
    bm.free()
    mesh.validate()
    mesh.update()

    # bmesh.to_mesh invalidiert Vertex-Group-Gewichte — neu aus losen Teilen
    comp_count = _rebuild_vertex_groups_from_loose_parts(geo_obj)
    
    # Kopiere Named Properties von der Source-Geometrie
    _copy_named_props_to_geometry_lod(geo_obj)
    
    operator.report({'INFO'},
                    "Finalize: 'Geometry' — {} Components (Groups neu gesetzt).".format(
                        comp_count))
    try:
        from .helpers import apply_scene_geometry_mass
        apply_scene_geometry_mass(geo_obj)
    except Exception:
        pass
    return geo_obj


def _copy_named_props_to_geometry_lod(geo_obj):
    """Kopiere Named Properties von der Source-Geometrie (falls vorhanden)."""
    try:
        # Suche Source-Geometrie (Quellobjekt mit Named Properties)
        source_objs = [o for o in bpy.data.objects 
                       if o.type == 'MESH' and o != geo_obj 
                       and len(o.cdm_named_props) > 0]
        if not source_objs:
            return
        
        # Nimm das erste Source-Objekt mit Named Properties
        source = source_objs[0]
        
        # Kopiere alle Named Properties
        geo_obj.cdm_named_props.clear()
        for item in source.cdm_named_props:
            new_item = geo_obj.cdm_named_props.add()
            new_item.prop_name = item.prop_name
            new_item.prop_value = item.prop_value
        geo_obj.cdm_named_props_index = 0
    except Exception:
        pass
