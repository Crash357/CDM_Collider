"""CDM Collider — V-HACD and CoACD decomposition."""
from .components import _append_hull_components
from .helpers import _get_selected_meshes, _clear_geo_components, _world_triangles, _report_decomp_result

def create_geometry_vhacd(operator,
                          max_hulls=32,
                          resolution=200_000,
                          max_verts_per_hull=32,
                          fill_mode='flood',
                          error_percent=2.0):
    """V-HACD Konvex-Zerlegung via TestVHACD.exe (V-HACD 4.x)."""
    from .. import vhacd_bridge

    selected = _get_selected_meshes(operator)
    if not selected:
        return None

    _clear_geo_components()
    comp_idx = 1

    for src_obj in selected:
        verts_py, tris_py = _world_triangles(src_obj)
        try:
            hulls = vhacd_bridge.run_vhacd(
                verts_py, tris_py,
                max_hulls=max_hulls,
                resolution=resolution,
                max_verts_per_hull=max_verts_per_hull,
                fill_mode=fill_mode,
                error_percent=error_percent,
            )
        except RuntimeError as exc:
            operator.report({'ERROR'}, str(exc))
            return None

        comp_idx = _append_hull_components(hulls, comp_idx)

    return _report_decomp_result(operator, "V-HACD", comp_idx - 1)


# ---------------------------------------------------------------------------
# CoACD — Collision-Aware Approximate Convex Decomposition
# ---------------------------------------------------------------------------

def create_geometry_coacd(operator,
                          threshold=0.05,
                          max_hulls=0,
                          preprocess_mode='auto',
                          prep_resolution=50,
                          mcts_iterations=100,
                          max_ch_vertex=64):
    """CoACD Konvex-Zerlegung via Python-API (CoACD, SIGGRAPH 2022, MIT)."""
    from .. import coacd_bridge

    selected = _get_selected_meshes(operator)
    if not selected:
        return None

    _clear_geo_components()
    comp_idx = 1

    for src_obj in selected:
        verts_py, tris_py = _world_triangles(src_obj)
        try:
            hulls = coacd_bridge.run_coacd(
                verts_py, tris_py,
                threshold=threshold,
                max_hulls=max_hulls,
                preprocess_mode=preprocess_mode,
                prep_resolution=prep_resolution,
                mcts_iterations=mcts_iterations,
                max_ch_vertex=max_ch_vertex,
            )
        except RuntimeError as exc:
            operator.report({'ERROR'}, str(exc))
            return None

        comp_idx = _append_hull_components(hulls, comp_idx)

    return _report_decomp_result(operator, "CoACD", comp_idx - 1)
