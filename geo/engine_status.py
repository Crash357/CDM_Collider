"""UI feedback while C# GeoEngine runs (panel, status bar, progress)."""
from __future__ import annotations

import bpy


def _status_set(context, message: str | None) -> None:
    """Blender 5+: workspace.status_text_set; older: window_manager.status_text_set."""
    ws = getattr(context, "workspace", None)
    if ws is not None and hasattr(ws, "status_text_set"):
        ws.status_text_set(message)
        return
    wm = context.window_manager
    if hasattr(wm, "status_text_set"):
        wm.status_text_set(message)


def begin(context, message: str) -> None:
    scene = context.scene
    scene.cdm_engine_busy = True
    scene.cdm_engine_status = message
    scene.cdm_engine_progress = 0.08
    wm = context.window_manager
    wm.progress_begin(0, 100)
    wm.progress_update(8)
    _status_set(context, "CDM GeoEngine: " + message)
    redraw(context)


def set_phase(context, message: str, progress: float | None = None) -> None:
    if not context.scene.cdm_engine_busy:
        return
    context.scene.cdm_engine_status = message
    if progress is not None:
        context.scene.cdm_engine_progress = max(0.0, min(1.0, progress))
        context.window_manager.progress_update(int(context.scene.cdm_engine_progress * 100))
    _status_set(context, "CDM GeoEngine: " + message)
    redraw(context)


def pulse(context) -> None:
    if not context.scene.cdm_engine_busy:
        return
    scene = context.scene
    p = scene.cdm_engine_progress
    scene.cdm_engine_progress = 0.12 + ((p - 0.12 + 0.04) % 0.72)
    context.window_manager.progress_update(int(scene.cdm_engine_progress * 100))
    redraw(context)


def end(context, message: str = "", success: bool = True) -> None:
    scene = context.scene
    scene.cdm_engine_busy = False
    scene.cdm_engine_status = message
    scene.cdm_engine_progress = 1.0 if success else 0.0
    context.window_manager.progress_end()
    _status_set(context, None)
    redraw(context)


def redraw(context) -> None:
    wm = context.window_manager
    for window in wm.windows:
        for area in window.screen.areas:
            area.tag_redraw()


def draw_busy_banner(layout, scene) -> None:
    """Prominent panel header while engine runs."""
    if not scene.cdm_engine_busy:
        return
    box = layout.box()
    box.alert = True
    col = box.column(align=True)
    col.scale_y = 1.15
    col.label(text="C# GeoEngine arbeitet", icon='TIME')
    if scene.cdm_engine_status:
        sub = col.column(align=True)
        sub.scale_y = 0.85
        sub.label(text=scene.cdm_engine_status, icon='BLANK1')
    row = col.row()
    row.enabled = False
    row.prop(scene, "cdm_engine_progress", slider=True, text="Fortschritt")
    layout.separator(factor=0.35)
