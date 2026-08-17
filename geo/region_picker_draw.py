"""Viewport overlay drawing for geo region picker (Blender GPU API)."""
from __future__ import annotations

import blf
import gpu
from gpu_extras.batch import batch_for_shader

from .region_picker_core import (
    HEADER_HEIGHT,
    HINT_LINE_HEIGHT,
    LINE_HEIGHT,
    OVERLAY_HEIGHT,
    OVERLAY_WIDTH,
    overlay_footer_height,
    overlay_hint_lines,
    overlay_hint_start_y,
    overlay_lines,
    overlay_list_top_y,
)
from .geo_regions import REGION_KIND_ITEMS, region_label, seed_count_by_kind

_FONT_ID = 0
_TITLE_SIZE = 13
_LIST_SIZE = 13
_HINT_SIZE = 11
_BG = (0.08, 0.09, 0.12, 0.88)
_FOOTER_BG = (0.06, 0.07, 0.10, 0.92)
_HEADER = (0.18, 0.35, 0.55, 0.95)
_ACTIVE = (0.25, 0.45, 0.72, 0.55)
_TEXT = (0.92, 0.94, 0.98, 1.0)
_MUTED = (0.65, 0.68, 0.72, 1.0)
_SEPARATOR = (0.28, 0.30, 0.34, 0.65)
_SHADER = None


def _ui_font_size(context, base: int) -> float:
    """Blender 5 blf.size(fontid, size) — scale via ui_scale instead of dpi arg."""
    try:
        return float(base) * float(context.preferences.system.ui_scale)
    except (AttributeError, TypeError, ValueError):
        return float(base)


def _shader():
    global _SHADER
    if _SHADER is None:
        _SHADER = gpu.shader.from_builtin('UNIFORM_COLOR')
    return _SHADER


def _prepare_blf():
    gpu.shader.unbind()
    gpu.state.blend_set('ALPHA')


def _restore_gpu_state():
    gpu.shader.unbind()
    gpu.state.blend_set('NONE')


def _blf_size(font_size: float):
    # Blender 5: blf.size(fontid, size) — no dpi parameter.
    blf.size(_FONT_ID, font_size)


def _draw_rect(x: float, y: float, w: float, h: float, color: tuple[float, float, float, float]):
    shader = _shader()
    verts = (
        (x, y),
        (x + w, y),
        (x + w, y + h),
        (x, y + h),
    )
    batch = batch_for_shader(shader, 'TRI_FAN', {'pos': verts})
    shader.bind()
    shader.uniform_float('color', color)
    gpu.state.blend_set('ALPHA')
    batch.draw(shader)
    gpu.shader.unbind()


def _draw_text(x: float, y: float, text: str, size: float, color: tuple[float, float, float, float]):
    _blf_size(size)
    blf.color(_FONT_ID, color[0], color[1], color[2], color[3])
    blf.position(_FONT_ID, x, y, 0)
    blf.draw(_FONT_ID, text)


def draw_region_picker_overlay(context, x: int, y: int, lod_obj) -> None:
    footer_h = overlay_footer_height()

    _draw_rect(x, y, OVERLAY_WIDTH, OVERLAY_HEIGHT, _BG)
    _draw_rect(x, y, OVERLAY_WIDTH, footer_h, _FOOTER_BG)
    _draw_rect(x, y + OVERLAY_HEIGHT - HEADER_HEIGHT, OVERLAY_WIDTH, HEADER_HEIGHT, _HEADER)
    _draw_rect(x + 6, y + footer_h, OVERLAY_WIDTH - 12, 1, _SEPARATOR)

    _prepare_blf()

    title_size = _ui_font_size(context, _TITLE_SIZE)
    title = 'CDM Geo Regionen — {}'.format(region_label(context.scene.cdm_geo_region_kind))
    title_y = y + OVERLAY_HEIGHT - HEADER_HEIGHT + (HEADER_HEIGHT - _TITLE_SIZE) // 2
    _draw_text(x + 10, title_y, title, title_size, _TEXT)

    counts = seed_count_by_kind(lod_obj)
    lod_name = lod_obj.name if lod_obj else 'Kein LOD'
    kind_labels = {k: lbl for k, lbl, _ in REGION_KIND_ITEMS}
    rows = overlay_lines(context.scene.cdm_geo_region_kind, counts, lod_name, kind_labels)

    list_size = _ui_font_size(context, _LIST_SIZE)
    ty = overlay_list_top_y(x, y)
    for text, active, set_mark in rows:
        if active:
            _draw_rect(x + 4, ty - 3, OVERLAY_WIDTH - 8, LINE_HEIGHT, _ACTIVE)
            _prepare_blf()
        prefix = '> ' if active else '  '
        suffix = '  *' if set_mark else ''
        color = _TEXT if active else _MUTED
        _draw_text(x + 10, ty, prefix + text + suffix, list_size, color)
        ty -= LINE_HEIGHT

    hint_size = _ui_font_size(context, _HINT_SIZE)
    hy = overlay_hint_start_y(x, y)
    for line in overlay_hint_lines():
        _draw_text(x + 10, hy, line, hint_size, _MUTED)
        hy += HINT_LINE_HEIGHT

    _restore_gpu_state()
