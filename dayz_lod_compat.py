"""Optional DayZ / P3D LOD helpers for CDM Collider.

When CDM P3D Studio (``p3d_io``) is installed alongside Collider, Geometry LOD
tags and mass use the companion APIs. Without it, Collider still works with local
fallbacks so the addon is fully standalone.
"""
from __future__ import annotations

LOD_GEOMETRY = 1.0e13


def get_lod_resolution(obj) -> float | None:
    try:
        from p3d_io.lod_types import get_lod_resolution as _get  # type: ignore
        return _get(obj)
    except Exception:
        pass
    for pkg in ('cdm_p3d_studio', 'cdm_blender_dayz_suite'):
        try:
            mod = __import__('{}.p3d_io.lod_types'.format(pkg), fromlist=['get_lod_resolution'])
            return mod.get_lod_resolution(obj)
        except Exception:
            pass
    try:
        v = obj.get('cdm_lod_resolution', None)
        return float(v) if v is not None else None
    except Exception:
        return None


def sync_object_lod_props(obj, resolution: float) -> None:
    try:
        from p3d_io.lod_types import sync_object_lod_props as _sync  # type: ignore
        _sync(obj, resolution)
        return
    except Exception:
        pass
    for pkg in ('cdm_p3d_studio', 'cdm_blender_dayz_suite'):
        try:
            mod = __import__('{}.p3d_io.lod_types'.format(pkg), fromlist=['sync_object_lod_props'])
            mod.sync_object_lod_props(obj, resolution)
            return
        except Exception:
            pass
    try:
        obj['cdm_lod_resolution'] = float(resolution)
        obj['LOD'] = float(resolution)
    except Exception:
        pass


def apply_geometry_lod_mass(obj, density: float) -> None:
    try:
        from p3d_io.mass_utils import apply_geometry_lod_mass as _apply  # type: ignore
        _apply(obj, density)
        return
    except Exception:
        pass
    for pkg in ('cdm_p3d_studio', 'cdm_blender_dayz_suite'):
        try:
            mod = __import__('{}.p3d_io.mass_utils'.format(pkg), fromlist=['apply_geometry_lod_mass'])
            mod.apply_geometry_lod_mass(obj, density)
            return
        except Exception:
            pass


def component_group_sets(obj):
    try:
        from p3d_io.mass_utils import _component_group_sets as _sets  # type: ignore
        return _sets(obj)
    except Exception:
        pass
    for pkg in ('cdm_p3d_studio', 'cdm_blender_dayz_suite'):
        try:
            mod = __import__('{}.p3d_io.mass_utils'.format(pkg), fromlist=['_component_group_sets'])
            return mod._component_group_sets(obj)
        except Exception:
            pass
    return []
