"""
CDM Collider — Geometry LOD generation for DayZ / Arma.
Split from legacy geometry.py; all public symbols re-exported here.
"""

from .constants import BOX_FACES, AABB_BOX_FACES, AABB_PADDING_M
from .helpers import (
    get_or_create_collection,
    move_to_collection,
    ensure_object_mode,
    ensure_outward_normals,
    get_bbox,
    _get_target,
    _get_selected_meshes,
    _clear_geo_components,
    _centre_component_origin,
    _world_triangles,
    _report_decomp_result,
    _apply_geo_display,
    _ensure_geo_viewport_colors,
)
from .components import _append_hull_components, _emit_closed_island
from .convex_hull import convex_hull_bmesh, convex_hull_verts, convex_hull_mesh
from .builder import _GeoLODBuilder
from .clustering import (
    _get_loose_parts_with_normals,
    _cluster_by_wall_axis,
    _cluster_by_face_angle,
    _thicken_verts,
    _build_face_clusters,
    _pair_wall_clusters,
    _box_corners,
    _object_aabb,
    _merge_antiparallel_clusters,
)
from .auto_build import (
    create_geometry_auto_building,
    create_geometry_decompose,
    create_geometry_decompose_building_phase1,
    create_geometry_decompose_building_phase2,
    create_building_angle_split,
    create_building_obb_boxes,
    create_building_finalize,
)
from .building_obb_pipeline import (
    split_islands_by_angle,
    generate_obb_boxes,
    finalize_geometry_lod,
)
from .building_decompose import (
    decompose_building_phase1,
    decompose_building_phase1_boxes,
    decompose_building_phase2,
    decompose_building_hybrid,
    closed_islands_to_boxes,
)
from .building_geo import generate_phase1_boxes, generate_phase2_boxes
from .component_splitter import split_to_minimal_components, generate_components_for_houses
from .shell_engine import build_shell_boxes_from_objects
from .merge import (
    create_geometry_merge_components,
    create_geometry_merge_exact,
    create_geometry_merge_for_method,
    method_uses_hull_merge,
    HULL_MERGE_METHODS,
)
from .direct import create_geometry_bbox, create_geometry_direct
from .selection import select_open_islands
from .tag import tag_as_geometry_lod
from .vertex_groups import (
    create_geometry_from_vertex_groups,
    create_geometry_hull_from_vertex_groups,
)
from .selection_hull import create_geometry_from_selection
from .selection_aabb import create_geometry_from_faces
from .cs_engine_bridge import (
    cs_engine_available,
    corpus_summary,
    create_building_geometry_cs,
    create_building_auto_geo,
    resolve_corpus_model_id,
    export_resolution_mesh_json,
)
from .face_skin import create_face_skin_components
from .external import create_geometry_vhacd, create_geometry_coacd
from .mesh_dump import (
    build_mesh_dump_text,
    build_components_dump_text,
    build_geometry_lod_dump_text,
    export_mesh_dump_to_file,
    export_geometry_lod_dump_to_file,
    export_compare_dumps,
    resolve_geometry_lod,
    resolve_source_building,
)

__all__ = [
    "BOX_FACES",
    "AABB_BOX_FACES",
    "AABB_PADDING_M",
    "get_or_create_collection",
    "move_to_collection",
    "ensure_object_mode",
    "ensure_outward_normals",
    "get_bbox",
    "_get_target",
    "_get_selected_meshes",
    "_clear_geo_components",
    "_centre_component_origin",
    "_world_triangles",
    "_append_hull_components",
    "_emit_closed_island",
    "_report_decomp_result",
    "_apply_geo_display",
    "_ensure_geo_viewport_colors",
    "_GeoLODBuilder",
    "_get_loose_parts_with_normals",
    "_cluster_by_wall_axis",
    "_cluster_by_face_angle",
    "_thicken_verts",
    "_build_face_clusters",
    "_pair_wall_clusters",
    "_box_corners",
    "_object_aabb",
    "_merge_antiparallel_clusters",
    "create_geometry_auto_building",
    "create_geometry_decompose",
    "create_geometry_decompose_building_phase1",
    "create_geometry_decompose_building_phase2",
    "create_building_angle_split",
    "create_building_obb_boxes",
    "create_building_finalize",
    "split_islands_by_angle",
    "generate_obb_boxes",
    "finalize_geometry_lod",
    "decompose_building_phase1",
    "decompose_building_phase1_boxes",
    "decompose_building_phase2",
    "decompose_building_hybrid",
    "closed_islands_to_boxes",
    "generate_phase1_boxes",
    "generate_phase2_boxes",
    "split_to_minimal_components",
    "generate_components_for_houses",
    "build_shell_boxes_from_objects",
    "create_geometry_merge_components",
    "create_geometry_merge_exact",
    "create_geometry_merge_for_method",
    "method_uses_hull_merge",
    "HULL_MERGE_METHODS",
    "create_geometry_bbox",
    "create_geometry_direct",
    "select_open_islands",
    "convex_hull_bmesh",
    "convex_hull_verts",
    "convex_hull_mesh",
    "tag_as_geometry_lod",
    "create_geometry_from_vertex_groups",
    "create_geometry_hull_from_vertex_groups",
    "create_geometry_from_selection",
    "create_geometry_from_faces",
    "cs_engine_available",
    "corpus_summary",
    "create_building_geometry_cs",
    "create_building_auto_geo",
    "create_face_skin_components",
    "resolve_corpus_model_id",
    "export_resolution_mesh_json",
    "create_geometry_vhacd",
    "create_geometry_coacd",
    "build_mesh_dump_text",
    "build_components_dump_text",
    "build_geometry_lod_dump_text",
    "export_mesh_dump_to_file",
    "export_geometry_lod_dump_to_file",
    "export_compare_dumps",
    "resolve_geometry_lod",
    "resolve_source_building",
]
