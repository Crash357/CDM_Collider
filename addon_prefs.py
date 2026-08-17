"""Blender 4.x / 5.x extension-safe addon preferences lookup."""
from __future__ import annotations

import bpy

PACKAGE_ID = "cdm_collider"


def addon_module_key(context=None) -> str | None:
    """Registered addons key (legacy or bl_ext.user_default.*)."""
    ctx = context or bpy.context
    addons = ctx.preferences.addons
    if PACKAGE_ID in addons:
        return PACKAGE_ID
    for key in addons.keys():
        if key == PACKAGE_ID or key.endswith("." + PACKAGE_ID):
            return key
    return None


def get_addon_preferences(context=None):
    ctx = context or bpy.context
    key = addon_module_key(ctx)
    if key is None:
        return None
    addon = ctx.preferences.addons.get(key)
    return addon.preferences if addon else None


def building_geo_lod_enabled(context=None) -> bool:
    """True when experimental Building Geo LOD (collision) is enabled."""
    prefs = get_addon_preferences(context)
    if prefs is None:
        return False
    return bool(getattr(prefs, 'building_geo_lod_experimental', False))
