"""Engine + region display helpers for Geo Regionen panel."""
from __future__ import annotations

import os

import bpy


def engine_diagnose() -> dict:
    from .cs_engine_bridge import (
        _CLI_PROJ,
        _corpus_index_path,
        corpus_summary,
        cs_engine_available,
        resolve_cli_dll,
        resolve_generation_mode,
    )

    dll = resolve_cli_dll()
    dll_ok = bool(dll)
    proj_ok = os.path.isfile(_CLI_PROJ)
    corpus_path = _corpus_index_path()
    corpus_ok = os.path.isfile(corpus_path)
    summary = corpus_summary()

    return {
        'available': cs_engine_available(),
        'dll_path': dll or '',
        'dll_ok': dll_ok,
        'proj_ok': proj_ok,
        'corpus_path': corpus_path,
        'corpus_ok': corpus_ok,
        'corpus_count': summary.get('model_count', 0) if summary else 0,
        'mode': resolve_generation_mode(),
    }


def select_resolution_mesh(context, obj) -> bool:
    from .helpers import ensure_object_mode

    if obj is None or obj.type != 'MESH':
        return False
    ensure_object_mode()
    view_layer = context.view_layer
    for o in view_layer.objects:
        o.select_set(False)
    obj.select_set(True)
    view_layer.objects.active = obj
    return True


def enter_mesh_face_pick_mode(context, obj) -> bool:
    """Edit Mode on resolution mesh, face select, nothing pre-selected."""
    if not select_resolution_mesh(context, obj):
        return False
    context.tool_settings.mesh_select_mode = (False, False, True)
    if context.mode != 'EDIT_MESH':
        bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='DESELECT')
    return context.mode == 'EDIT_MESH'


def refresh_region_view(context, obj=None) -> int:
    from .region_seed_colors import refresh_region_display
    from .region_seed_viz import tag_redraw

    painted = refresh_region_display(context, obj)
    tag_redraw(context)
    return painted
