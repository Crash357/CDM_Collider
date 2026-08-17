"""Addon version for UI (Blender 5 extensions use blender_manifest.toml, not bl_info import)."""
from __future__ import annotations

import os

_FALLBACK = (1, 0, 3)


def version_tuple() -> tuple[int, int, int]:
    manifest = os.path.join(os.path.dirname(os.path.abspath(__file__)), "blender_manifest.toml")
    try:
        with open(manifest, encoding="utf-8") as f:
            for line in f:
                stripped = line.strip()
                if not stripped.startswith("version"):
                    continue
                if "=" not in stripped:
                    continue
                val = stripped.split("=", 1)[1].strip().strip('"').strip("'")
                parts = val.split(".")
                if len(parts) >= 3:
                    return int(parts[0]), int(parts[1]), int(parts[2])
    except (OSError, ValueError):
        pass
    return _FALLBACK


def version_label() -> str:
    v = version_tuple()
    return "v{}.{}.{}".format(*v)
