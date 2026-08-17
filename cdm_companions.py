"""CDM product-set companion detection (identical copy in each addon).

When several CDM addons are installed, they share one N-Panel tab ``CDM``.
Alone, each keeps its standalone tab name.
"""
from __future__ import annotations

import bpy

# Extension package ids (blender_manifest.toml ``id``)
PRODUCTS: tuple[tuple[str, str, str], ...] = (
    ('cdm_architect', 'CDM Architect', 'Gebäude-Meshes / Assembler'),
    ('cdm_collider', 'CDM Collider', 'Collision / Geo LOD'),
    ('cdm_p3d_studio', 'CDM P3D Studio', 'P3D I/O / Loot / Resolution LODs'),
)

SET_CATEGORY = 'CDM'


def _addon_keys(context=None):
    ctx = context or bpy.context
    try:
        return list(ctx.preferences.addons.keys())
    except Exception:
        return []


def is_product_installed(package_id: str, context=None) -> bool:
    """True if ``package_id`` is registered (legacy or bl_ext.*.id)."""
    for key in _addon_keys(context):
        if key == package_id or key.endswith('.' + package_id):
            return True
    return False


def installed_products(context=None) -> list[str]:
    return [pid for pid, _label, _hint in PRODUCTS if is_product_installed(pid, context)]


def companion_count(self_id: str, context=None) -> int:
    """Number of *other* CDM products installed besides ``self_id``."""
    return sum(
        1 for pid, _l, _h in PRODUCTS
        if pid != self_id and is_product_installed(pid, context)
    )


def n_panel_category(standalone: str, self_id: str, context=None, *, unify: bool = True) -> str:
    """Shared ``CDM`` tab when companions are present and unify is on."""
    if unify and companion_count(self_id, context) > 0:
        return SET_CATEGORY
    return standalone


def resolve_category(standalone: str, self_id: str, context=None) -> str:
    """Category from companion detection + optional Prefs-Toggle."""
    unify = True
    try:
        from .addon_prefs import get_addon_preferences
        prefs = get_addon_preferences(context)
        if prefs is not None and hasattr(prefs, 'unify_n_panel_with_set'):
            unify = bool(prefs.unify_n_panel_with_set)
    except Exception:
        pass
    return n_panel_category(standalone, self_id, context, unify=unify)


def apply_n_panel_category(classes, category: str) -> None:
    """Set ``bl_category`` on panel classes before register."""
    for cls in classes:
        if getattr(cls, 'bl_space_type', None) == 'VIEW_3D' and hasattr(cls, 'bl_category'):
            cls.bl_category = category


def draw_companion_status(layout, self_id: str, context=None) -> None:
    """Preferences box: which CDM products are installed / missing."""
    box = layout.box()
    box.label(text='CDM Produkt-Set', icon='LINKED')
    installed = installed_products(context)
    n = len(installed)
    if n >= 3:
        box.label(text='Vollständiges Set aktiv — gemeinsames N-Panel: CDM', icon='CHECKMARK')
    elif n >= 2:
        box.label(text='Teil-Set — N-Panel zusammengeführt unter: CDM', icon='INFO')
    else:
        box.label(text='Einzelinstallation — eigenes N-Panel', icon='INFO')

    col = box.column(align=True)
    col.scale_y = 0.9
    for pid, label, hint in PRODUCTS:
        on = is_product_installed(pid, context)
        icon = 'CHECKMARK' if on else 'RADIOBUT_OFF'
        mark = '✓' if on else '–'
        mine = ' (dieses Addon)' if pid == self_id else ''
        col.label(text='{} {}{} — {}'.format(mark, label, mine, hint), icon=icon)

    missing = [label for pid, label, _h in PRODUCTS if not is_product_installed(pid, context)]
    if missing:
        hint = box.column(align=True)
        hint.scale_y = 0.85
        hint.label(text='Fehlt: ' + ', '.join(missing), icon='ADD')
        hint.label(text='Install from Disk → weiteres Produkt hinzufügen.', icon='BLANK1')
