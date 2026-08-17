"""Safe RNA class registration (Blender extensions / reload-safe)."""
from __future__ import annotations

import bpy


def safe_register_class(cls) -> None:
    try:
        bpy.utils.register_class(cls)
    except (ValueError, RuntimeError) as exc:
        if 'already registered' not in str(exc).lower():
            raise
        try:
            bpy.utils.unregister_class(cls)
        except (ValueError, RuntimeError):
            pass
        bpy.utils.register_class(cls)


def safe_unregister_class(cls) -> None:
    try:
        bpy.utils.unregister_class(cls)
    except (ValueError, RuntimeError):
        pass
