"""CDM Collider — auto building and decompose."""
import bmesh
import bpy
import json
import mathutils

from .constants import BOX_FACES, AABB_BOX_FACES
from .convex_hull import convex_hull_mesh
from .engine import engine_generate, engine_ok
from .component_splitter import split_to_minimal_components
from .building_decompose import (
    decompose_building_phase1,
    decompose_building_phase1_boxes,
    decompose_building_phase2,
    decompose_building_hybrid,
)
from .building_obb_pipeline import (
    analyze_building_patches,
    emit_patch_preview,
    emit_boxes_from_sub_islands,
    emit_obb_components_from_patches,
    generate_obb_boxes,
    get_cached_patches,
    finalize_geometry_lod,
)
from .components import _emit_closed_island
from .helpers import (
    _get_meshes_for_geo, _clear_geo_components, _centre_component_origin,
    _apply_geo_display, get_or_create_collection, _next_component_index,
    move_to_collection, ensure_outward_normals,
)
from .clustering import (
    _cluster_by_wall_axis, _cluster_by_face_angle, _merge_antiparallel_clusters,
    _object_aabb, _thicken_verts,
)

try:
    from ..geo_engine.cdm_engine import GeoEngineError
except Exception:
    class GeoEngineError(RuntimeError):
        pass


def _corners_to_component(corners, comp_name, faces=BOX_FACES):
    """8 Ecken → Quader-Mesh. OBB: BOX_FACES, Welt-AABB: AABB_BOX_FACES."""
    tmp_bm = bmesh.new()
    verts = [tmp_bm.verts.new(mathutils.Vector(c)) for c in corners]
    for fi in faces:
        try:
            tmp_bm.faces.new([verts[i] for i in fi])
        except ValueError:
            pass
    ensure_outward_normals(tmp_bm)
    mesh = bpy.data.meshes.new(comp_name)
    tmp_bm.to_mesh(mesh)
    tmp_bm.free()
    mesh.validate()
    mesh.update()
    comp_obj = bpy.data.objects.new(comp_name, mesh)
    move_to_collection(comp_obj, 'GEO_Components')
    _centre_component_origin(comp_obj)
    _apply_geo_display(comp_obj, is_component=True)
    return comp_obj


def _emit_island_meshes(meshes, comp_idx_start=1):
    """Exakte Island-Meshes → Component-Objekte."""
    idx = comp_idx_start
    for world_verts, face_idx in meshes:
        _emit_closed_island(world_verts, face_idx,
                            "Component{:02d}".format(idx))
        idx += 1
    return idx - comp_idx_start, idx


def _emit_hybrid(closed_boxes, open_boxes, comp_idx_start=1):
    """Phase 1: OBB/AABB-Mix. Phase 2: achsparallele Wand-AABB."""
    count1, idx = _emit_boxes(closed_boxes, comp_idx_start)
    count2, idx = _emit_boxes(open_boxes, comp_idx_start=idx, aabb_faces=True)
    return count1, count2, idx


def _emit_boxes(all_boxes, comp_idx_start=1, aabb_faces=False):
    """Create component objects from corner lists. Returns (count, last_idx)."""
    idx = comp_idx_start
    for item in all_boxes:
        if (isinstance(item, (list, tuple)) and len(item) == 2
                and isinstance(item[1], bool)
                and len(item[0]) == 8):
            corners, use_aabb = item
        else:
            corners, use_aabb = item, aabb_faces
        if len(corners) != 8:
            continue
        faces = AABB_BOX_FACES if use_aabb else BOX_FACES
        _corners_to_component(corners, "Component{:02d}".format(idx), faces=faces)
        idx += 1
    return idx - comp_idx_start, idx


def _mesh_to_engine_input(obj):
    mw = obj.matrix_world
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    verts_py = [(mw @ v.co)[:] for v in bm.verts]
    tris_py = [[f.verts[0].index, f.verts[1].index, f.verts[2].index]
               for f in bm.faces]
    bm.free()
    return verts_py, tris_py


def create_geometry_auto_building(operator, method='OBB',
                                  min_area=0.5, angle_threshold=30.0):
    """
    ONE CLICK — Generate complete Geometry LOD.
    OBB: native C-Engine when available, else Python face-angle clustering.
    WALL / SPLIT / SHELL: always Python.
    Blind building heuristics: use operator cdm.building_auto_geo instead.
    """
    selected = _get_meshes_for_geo(operator, method)
    if not selected:
        return None

    _clear_geo_components()

    comp_idx = 1
    used_engine = False
    engine_failed = False
    python_handled = False

    if method == 'SPLIT':
        all_boxes = []
        for obj in selected:
            all_boxes.extend(split_to_minimal_components(obj, min_area_m2=min_area))
        count, comp_idx = _emit_boxes(all_boxes)
        if count == 0:
            operator.report({'ERROR'}, "No components found (SPLIT).")
            return None
        operator.report({'INFO'},
                        "GeoLOD: {} components [Split / SPLIT / {:.2f} m²]".format(
                            count, min_area))
        python_handled = True

    elif method == 'SHELL':
        closed, open_boxes, stats = decompose_building_hybrid(
            selected[0], min_area_m2=min_area,
            angle_threshold_deg=angle_threshold)
        count1, count2, comp_idx = _emit_hybrid(closed, open_boxes)
        count = count1 + count2
        if count == 0:
            operator.report({'ERROR'}, "No components found (SHELL).")
            return None
        operator.report({'INFO'},
                        "GeoLOD: {} ({} closed + {} wall boxes) [SHELL]".format(
                            count, count1, count2))
        python_handled = True

    elif method == 'OBB' and engine_ok():
        all_boxes = []
        for obj in selected:
            verts_py, tris_py = _mesh_to_engine_input(obj)
            try:
                boxes = engine_generate(
                    verts_py, tris_py,
                    angle_thresh=angle_threshold,
                    min_area=min_area,
                    min_thickness=0.5,
                )
            except GeoEngineError:
                engine_failed = True
                boxes = []
            all_boxes.extend(boxes)

        if all_boxes:
            used_engine = True
            count, comp_idx = _emit_boxes(all_boxes)
        elif engine_failed:
            operator.report({'WARNING'},
                            "C-Engine error — falling back to Python.")

    if not used_engine and not python_handled:
        for obj in selected:
            if method == 'BBOX':
                clusters = [(_object_aabb(obj), None)]
            elif method == 'WALL':
                clusters = _cluster_by_wall_axis(obj, min_area_m2=min_area)
                clusters = _merge_antiparallel_clusters(clusters)
            elif method == 'HULL':
                clusters = _cluster_by_face_angle(
                    obj, angle_threshold_deg=angle_threshold, min_area_m2=min_area)
                clusters = _merge_antiparallel_clusters(clusters)
            else:  # OBB
                clusters = _cluster_by_face_angle(
                    obj, angle_threshold_deg=angle_threshold, min_area_m2=min_area)
                clusters = _merge_antiparallel_clusters(clusters)

            for world_verts, avg_normal in clusters:
                comp_name = "Component{:02d}".format(comp_idx)
                if method == 'HULL':
                    if len(world_verts) < 4:
                        continue
                    mesh = convex_hull_mesh(world_verts, comp_name)
                    if mesh is None:
                        continue
                else:
                    corners = (world_verts if method == 'BBOX'
                               else _thicken_verts(world_verts, avg_normal))
                    if len(corners) != 8:
                        continue
                    _corners_to_component(corners, comp_name)
                    comp_idx += 1
                    continue

                comp_obj = bpy.data.objects.new(comp_name, mesh)
                move_to_collection(comp_obj, 'GEO_Components')
                _centre_component_origin(comp_obj)
                _apply_geo_display(comp_obj, is_component=True)
                comp_idx += 1

    total = comp_idx - 1
    if total == 0:
        operator.report({'ERROR'}, "No components found.")
        return None

    if not python_handled:
        engine_str = "C-Engine" if used_engine else "Python"
        operator.report({'INFO'},
                        "GeoLOD: {} components [{} / {} / {:.0f}\u00b0 / {:.2f} m\u00b2]".format(
                            total, engine_str, method, angle_threshold, min_area))

    from .merge import create_geometry_merge_for_method
    return create_geometry_merge_for_method(operator, method)


def create_geometry_decompose(operator, method='OBB',
                              min_area=0.5, angle_threshold=30.0):
    """
    STEP 1 — Component01, Component02 … in 'GEO_Components'.
    Same decomposition logic as Auto Geo LOD, without immediate merge.
    """
    selected = _get_meshes_for_geo(operator, method)
    if not selected:
        return None

    _clear_geo_components()
    col = get_or_create_collection('GEO_Components')
    comp_idx = 1
    used_engine = False
    engine_failed = False

    if method == 'SPLIT':
        all_boxes = []
        for obj in selected:
            all_boxes.extend(split_to_minimal_components(obj, min_area_m2=min_area))
        count, comp_idx = _emit_boxes(all_boxes)
        if count == 0:
            operator.report({'ERROR'}, "No components created (SPLIT).")
            return None
        operator.report({'INFO'},
                        "Decompose: {} components [Split / SPLIT]. "
                        "Inspect, then Merge to Geometry LOD.".format(count))
        return col

    if method == 'SHELL':
        closed, open_boxes, stats = decompose_building_hybrid(
            selected[0], min_area_m2=min_area,
            angle_threshold_deg=angle_threshold)
        count1, count2, comp_idx = _emit_hybrid(closed, open_boxes)
        count = count1 + count2
        if count == 0:
            operator.report({'ERROR'}, "No components created (SHELL).")
            return None
        operator.report({'INFO'},
                        "Decompose: {} ({} closed + {} wall boxes) [SHELL]. "
                        "Inspect, then Merge.".format(count, count1, count2))
        return col

    if method == 'OBB' and engine_ok():
        all_boxes = []
        for obj in selected:
            verts_py, tris_py = _mesh_to_engine_input(obj)
            try:
                boxes = engine_generate(
                    verts_py, tris_py,
                    angle_thresh=angle_threshold,
                    min_area=min_area,
                    min_thickness=0.5,
                )
            except GeoEngineError:
                engine_failed = True
                boxes = []
            all_boxes.extend(boxes)

        if all_boxes:
            used_engine = True
            count, comp_idx = _emit_boxes(all_boxes)
        elif engine_failed:
            operator.report({'WARNING'},
                            "C-Engine error — falling back to Python.")

    if not used_engine:
        for src_obj in selected:
            if method == 'BBOX':
                clusters = [(_object_aabb(src_obj), None)]
            elif method == 'WALL':
                clusters = _cluster_by_wall_axis(src_obj, min_area_m2=min_area)
                clusters = _merge_antiparallel_clusters(clusters)
            elif method == 'HULL':
                clusters = _cluster_by_face_angle(
                    src_obj, angle_threshold_deg=angle_threshold, min_area_m2=min_area)
                clusters = _merge_antiparallel_clusters(clusters)
            else:
                clusters = _cluster_by_face_angle(
                    src_obj, angle_threshold_deg=angle_threshold, min_area_m2=min_area)
                clusters = _merge_antiparallel_clusters(clusters)

            for world_verts, avg_normal in clusters:
                comp_name = "Component{:02d}".format(comp_idx)

                if method in ('BBOX', 'WALL', 'OBB'):
                    corners = (world_verts if method == 'BBOX'
                               else _thicken_verts(world_verts, avg_normal))
                    if len(corners) != 8:
                        continue
                    _corners_to_component(corners, comp_name)
                    comp_idx += 1
                else:
                    if len(world_verts) < 4:
                        continue
                    mesh = convex_hull_mesh(world_verts, comp_name)
                    if mesh is None:
                        continue
                    comp_obj = bpy.data.objects.new(comp_name, mesh)
                    move_to_collection(comp_obj, 'GEO_Components')
                    _centre_component_origin(comp_obj)
                    _apply_geo_display(comp_obj, is_component=True)
                    comp_idx += 1

    count = comp_idx - 1
    if count == 0:
        operator.report({'ERROR'}, "No components created.")
        return None

    engine_str = "C-Engine" if used_engine else "Python"
    operator.report({'INFO'},
                    "Decompose: {} components [{} / {}]. "
                    "Inspect, then Merge to Geometry LOD.".format(
                        count, engine_str, method))
    return col


def create_geometry_decompose_building_phase1(operator, min_area=0.5,
                                              angle_threshold=30.0):
    """Phase 1 — Mesh auslesen: Boden + Dach/Schornstein (Anzahl aus Geometrie)."""
    meshes = _get_meshes_for_geo(operator, 'SHELL')
    if not meshes:
        return None
    obj = meshes[0]

    _clear_geo_components()
    bpy.context.scene.cdm_building_phase1_count = 0
    get_or_create_collection('GEO_Components')

    closed_boxes, stats = decompose_building_phase1_boxes(
        obj, angle_threshold_deg=angle_threshold, min_area_m2=min_area)
    count, _ = _emit_boxes(closed_boxes)

    if count == 0:
        operator.report({'ERROR'},
                        "Phase 1: keine Boxen aus Mesh-Messung.")
        return None

    bpy.context.scene.cdm_building_phase1_count = count

    operator.report({'INFO'},
                    "Phase 1 '{}': {} Boxen "
                    "(Boden:{}, Dach:{}/{} Faces, Schornstein:{}). "
                    "Dann Phase 2.".format(
                        obj.name, count,
                        int(stats.get('floor', 0)),
                        stats.get('roof_boxes', 0),
                        stats.get('roof_faces', 0),
                        stats.get('chimney_boxes', 0)))
    return bpy.data.collections.get('GEO_Components')


def create_geometry_decompose_building_phase2(operator, min_area=0.25,
                                              angle_threshold=30.0):
    """Phase 2 — Wände: Innen/Außen-Paare aus Mesh messen (8V AABB)."""
    meshes = _get_meshes_for_geo(operator, 'SHELL')
    if not meshes:
        return None
    obj = meshes[0]

    col = bpy.data.collections.get('GEO_Components')
    if not col or not col.objects:
        operator.report({'ERROR'}, "Zuerst Phase 1 ausführen.")
        return None

    open_boxes, stats = decompose_building_phase2(
        obj, min_area_m2=min_area,
        angle_threshold_deg=angle_threshold)

    if not open_boxes:
        operator.report({'ERROR'},
                        "Phase 2: keine Wand-Boxen. Min Area senken (z.B. 0.02).")
        return None

    start = _next_component_index()
    count, _ = _emit_boxes(open_boxes, comp_idx_start=start, aabb_faces=True)

    operator.report({'INFO'},
                    "Phase 2 '{}': {} geschlossene Wand-Boxen ab Component{:02d}. "
                    "Merge wenn OK.".format(obj.name, count, start))
    return col


def create_building_angle_split(operator, min_area=0.05, angle_threshold=30.0):
    """OBB-Pipeline Phase 1 — Winkel-Split, Preview in GEO_SubIslands."""
    meshes = _get_meshes_for_geo(operator, 'SHELL')
    if not meshes:
        return None
    obj = meshes[0]

    patches, stats, bm, mw = analyze_building_patches(
        obj, angle_threshold_deg=angle_threshold, min_area_m2=min_area)
    if not patches:
        bm.free()
        operator.report({'ERROR'},
                        "Phase 1: keine Patches (Angle/Min Area anpassen).")
        return None

    count = emit_patch_preview(patches, bm, mw)
    bm.free()
    bpy.context.scene.cdm_building_phase1_count = len(patches)
    operator.report({'INFO'},
                    "Angle-Split '{}': {} Patches aus {} Islands "
                    "(Collection GEO_SubIslands).".format(
                        obj.name, count, stats['islands']))
    return bpy.data.collections.get('GEO_SubIslands')


def create_building_obb_boxes(operator, min_area=0.05, angle_threshold=30.0):
    """OBB-Pipeline Phase 2 — bevorzugt C# GeoEngine."""
    from .cs_engine_bridge import cs_engine_available, create_building_geometry_cs

    if cs_engine_available():
        return create_building_geometry_cs(operator, min_area, angle_threshold)

    meshes = _get_meshes_for_geo(operator, 'SHELL')
    if not meshes:
        return None
    obj = meshes[0]

    sub_col = bpy.data.collections.get('GEO_SubIslands')
    sub_count = len(sub_col.objects) if sub_col else 0
    if sub_count == 0:
        operator.report({'WARNING'},
                        "GEO_SubIslands leer — Phase 1 wird nachgeholt.")
        patches, stats, bm, mw = analyze_building_patches(
            obj, angle_threshold_deg=angle_threshold, min_area_m2=min_area)
        if not patches:
            bm.free()
            operator.report({'ERROR'}, "Keine Patches — Min Area / Angle prüfen.")
            return None
        sub_count = emit_patch_preview(patches, bm, mw)
        bm.free()
        _ = stats

    _clear_geo_components()
    get_or_create_collection('GEO_Components')

    count, stats = emit_boxes_from_sub_islands()
    if count == 0:
        cached = get_cached_patches(obj.name)
        if cached:
            count, stats = emit_obb_components_from_patches(obj, cached)
        if count == 0:
            operator.report({'ERROR'},
                            "Phase 2: keine Boxen — Phase 1 prüfen.")
            return None

    bpy.context.scene.cdm_building_phase1_count = count
    operator.report({'INFO'},
                    "OBB v5 '{}': {} Boxen aus {} Patches "
                    "({} skip).".format(
                        obj.name, count, stats.get('patches', 0),
                        stats.get('skipped', 0)))
    return bpy.data.collections.get('GEO_Components')


def create_building_finalize(operator, merge_distance=0.001):
    """OBB-Pipeline Phase 3 — Join, Intersect, Cleanup → Geometry."""
    return finalize_geometry_lod(operator, merge_distance=merge_distance)

