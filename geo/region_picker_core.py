"""Pure logic for geo region picker overlay (testable without Blender)."""
from __future__ import annotations

OVERLAY_WIDTH = 300
OVERLAY_MARGIN = 12
HEADER_HEIGHT = 28
LINE_HEIGHT = 22
HINT_LINE_HEIGHT = 14
FOOTER_PAD = 8
CONTENT_PAD = 6

# Must match order in geo_regions.REGION_KIND_ITEMS
REGION_KIND_ORDER = (
    'WALL_OUTER',
    'ROOF',
    'WALL_INNER',
    'FLOOR',
    'GABLE',
    'PLINTH',
    'SOFFIT',
)


def overlay_row_count() -> int:
    """LOD name row + one row per region kind."""
    return 1 + len(REGION_KIND_ORDER)


def overlay_hint_lines() -> list[str]:
    return [
        '↑ / ↓  Kategorie',
        'LMB    Stichpunkt setzen',
        'Ziehen Titelleiste = verschieben',
        'ESC    Beenden',
    ]


def overlay_footer_height() -> int:
    return FOOTER_PAD * 2 + len(overlay_hint_lines()) * HINT_LINE_HEIGHT


def overlay_list_height() -> int:
    return CONTENT_PAD + overlay_row_count() * LINE_HEIGHT


def compute_overlay_height() -> int:
    return HEADER_HEIGHT + overlay_list_height() + overlay_footer_height()


OVERLAY_HEIGHT = compute_overlay_height()


def all_region_kind_ids() -> list[str]:
    return list(REGION_KIND_ORDER)


def cycle_region_kind(current: str, direction: int) -> str:
    kinds = all_region_kind_ids()
    if not kinds:
        return current
    try:
        idx = kinds.index(current)
    except ValueError:
        idx = 0
    return kinds[(idx + direction) % len(kinds)]


def clamp_overlay_position(x: int, y: int, region_w: int, region_h: int) -> tuple[int, int]:
    max_x = max(OVERLAY_MARGIN, region_w - OVERLAY_WIDTH - OVERLAY_MARGIN)
    max_y = max(OVERLAY_MARGIN, region_h - OVERLAY_HEIGHT - OVERLAY_MARGIN)
    return (
        max(OVERLAY_MARGIN, min(int(x), max_x)),
        max(OVERLAY_MARGIN, min(int(y), max_y)),
    )


def overlay_rect(x: int, y: int) -> tuple[int, int, int, int]:
    return x, y, x + OVERLAY_WIDTH, y + OVERLAY_HEIGHT


def header_rect(x: int, y: int) -> tuple[int, int, int, int]:
    return x, y + OVERLAY_HEIGHT - HEADER_HEIGHT, x + OVERLAY_WIDTH, y + OVERLAY_HEIGHT


def overlay_footer_rect(x: int, y: int) -> tuple[int, int, int, int]:
    fh = overlay_footer_height()
    return x, y, x + OVERLAY_WIDTH, y + fh


def overlay_list_top_y(x: int, y: int) -> int:
    """Blf baseline Y for the first list row (bottom-up coords)."""
    return y + OVERLAY_HEIGHT - HEADER_HEIGHT - CONTENT_PAD - LINE_HEIGHT


def overlay_hint_start_y(x: int, y: int) -> int:
    return y + FOOTER_PAD


def point_in_rect(px: int, py: int, rect: tuple[int, int, int, int]) -> bool:
    x0, y0, x1, y1 = rect
    return x0 <= px <= x1 and y0 <= py <= y1


def overlay_lines(
    active_kind: str,
    seed_counts: dict[str, int],
    lod_name: str,
    kind_labels: dict[str, str],
) -> list[tuple[str, bool, bool]]:
    """(text, is_active, is_set) per line."""
    rows: list[tuple[str, bool, bool]] = []
    rows.append((lod_name or '—', False, False))
    for kind_id in REGION_KIND_ORDER:
        label = kind_labels.get(kind_id, kind_id)
        mark = seed_counts.get(kind_id, 0) > 0
        rows.append((label, kind_id == active_kind, mark))
    return rows
