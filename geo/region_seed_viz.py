"""3D viewport overlay: colored faces for geo region preview."""
from __future__ import annotations

import bpy
import gpu
from gpu_extras.batch import batch_for_shader

from .geo_regions import resolve_resolution_obj
from .region_seed_preview import build_region_face_preview

_DRAW_HANDLE = None

_KIND_COLORS: dict[str, tuple[float, float, float, float]] = {
    'WALL_OUTER': (0.25, 0.55, 0.95, 0.55),
    'ROOF': (0.95, 0.35, 0.25, 0.55),
    'WALL_INNER': (0.55, 0.75, 0.95, 0.50),
    'FLOOR': (0.45, 0.85, 0.55, 0.50),
    'GABLE': (0.95, 0.75, 0.30, 0.55),
    'PLINTH': (0.65, 0.65, 0.70, 0.50),
    'SOFFIT': (0.85, 0.55, 0.90, 0.50),
}


def _shader():
    return gpu.shader.from_builtin('UNIFORM_COLOR')


def _poly_triangulate_local(poly, mesh) -> list[tuple[float, float, float]]:
    """Blender loop_triangles for correct GPU TRIS (handles quads/ngons)."""
    verts: list[tuple[float, float, float]] = []
    # Mesh.loop_triangles (Polygon hat kein loop_triangles-Attribut)
    try:
        mesh.calc_loop_triangles()
    except Exception:
        pass
    tris = getattr(mesh, 'loop_triangles', None)
    if tris:
        for tri in tris:
            if int(getattr(tri, 'polygon_index', -1)) != int(poly.index):
                continue
            for vi in tri.vertices:
                verts.append(tuple(mesh.vertices[vi].co))
        if verts:
            return verts

    loop_verts = [mesh.vertices[mesh.loops[li].vertex_index].co for li in poly.loop_indices]
    if len(loop_verts) < 3:
        return []
    if len(loop_verts) == 3:
        return [tuple(loop_verts[0]), tuple(loop_verts[1]), tuple(loop_verts[2])]
    v0 = loop_verts[0]
    for i in range(1, len(loop_verts) - 1):
        verts.extend((tuple(v0), tuple(loop_verts[i]), tuple(loop_verts[i + 1])))
    return verts


def _should_draw(context) -> bool:
    scene = context.scene
    if scene is None or not getattr(scene, 'cdm_geo_region_show_mesh', True):
        return False
    obj = resolve_resolution_obj(context)
    if obj is None or not hasattr(obj, 'cdm_geo_region_seeds'):
        return False
    return len(obj.cdm_geo_region_seeds) > 0


def draw_region_mesh_overlay():
    context = bpy.context
    if not _should_draw(context):
        return

    obj = resolve_resolution_obj(context)
    if obj is None:
        return

    plan = build_region_face_preview(obj)
    if not plan:
        return

    mesh = obj.data
    matrix = obj.matrix_world
    shader = _shader()

    gpu.state.blend_set('ALPHA')
    gpu.state.depth_test_set('LESS_EQUAL')
    gpu.state.depth_mask_set(False)

    try:
        with gpu.matrix.push_pop():
            gpu.matrix.multiply_matrix(matrix)
            for kind, face_set in plan.items():
                color = _KIND_COLORS.get(kind, (0.8, 0.8, 0.2, 0.45))
                verts: list[tuple[float, float, float]] = []
                for fi in face_set:
                    if fi < 0 or fi >= len(mesh.polygons):
                        continue
                    verts.extend(_poly_triangulate_local(mesh.polygons[fi], mesh))

                if len(verts) < 3:
                    continue

                batch = batch_for_shader(shader, 'TRIS', {'pos': verts})
                shader.bind()
                shader.uniform_float('color', color)
                batch.draw(shader)

        gpu.shader.unbind()
        marker_shader = _shader()
        for seed in obj.cdm_geo_region_seeds:
            color = _KIND_COLORS.get(seed.kind, (1.0, 1.0, 0.2, 0.95))
            # seed.position is stored in world space (matches C# export)
            p = seed.position
            s = 0.12
            lines = [
                (p.x - s, p.y, p.z), (p.x + s, p.y, p.z),
                (p.x, p.y - s, p.z), (p.x, p.y + s, p.z),
                (p.x, p.y, p.z - s), (p.x, p.y, p.z + s),
            ]
            batch = batch_for_shader(marker_shader, 'LINES', {'pos': lines})
            marker_shader.bind()
            marker_shader.uniform_float('color', (color[0], color[1], color[2], 0.95))
            gpu.state.line_width_set(3.0)
            batch.draw(marker_shader)
    except Exception as exc:
        print('CDM region mesh overlay draw failed:', exc)
    finally:
        gpu.shader.unbind()
        gpu.state.blend_set('NONE')
        gpu.state.depth_test_set('NONE')
        gpu.state.depth_mask_set(True)
        gpu.state.line_width_set(1.0)


def register_draw_handler():
    global _DRAW_HANDLE
    if _DRAW_HANDLE is not None:
        return
    _DRAW_HANDLE = bpy.types.SpaceView3D.draw_handler_add(
        draw_region_mesh_overlay,
        (),
        'WINDOW',
        'POST_VIEW',
    )


def unregister_draw_handler():
    global _DRAW_HANDLE
    if _DRAW_HANDLE is None:
        return
    try:
        bpy.types.SpaceView3D.draw_handler_remove(_DRAW_HANDLE, 'WINDOW')
    except Exception:
        pass
    _DRAW_HANDLE = None


def tag_redraw(context):
    if context and context.screen:
        for area in context.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()
