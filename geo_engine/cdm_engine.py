"""
CDM Geo Engine — Python ctypes wrapper
=======================================
Lädt cdm_geo_engine.dll (Windows) / .so (Linux/macOS) und stellt
generate_geo_boxes() bereit.

Verwendung in geo/:
    from ..geo_engine.cdm_engine import generate_geo_boxes, engine_available
"""

import ctypes
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))

if sys.platform == "win32":
    _LIB_NAME = "cdm_geo_engine.dll"
elif sys.platform == "darwin":
    _LIB_NAME = "cdm_geo_engine.dylib"
else:
    _LIB_NAME = "cdm_geo_engine.so"

_LIB_PATH = os.path.join(_HERE, _LIB_NAME)
_lib = None


class GeoEngineError(RuntimeError):
    """Raised when the native engine returns an error code."""


def _load():
    global _lib
    if _lib is not None:
        return _lib
    if not os.path.isfile(_LIB_PATH):
        return None
    try:
        _lib = ctypes.CDLL(_LIB_PATH)

        _lib.cdm_generate_geo_boxes.restype = ctypes.c_int
        _lib.cdm_generate_geo_boxes.argtypes = [
            ctypes.POINTER(ctypes.c_float),
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
            ctypes.c_int,
            ctypes.c_float,
            ctypes.c_float,
            ctypes.c_float,
            ctypes.POINTER(ctypes.c_float),
            ctypes.c_int,
        ]

        _lib.cdm_version.restype = ctypes.c_char_p
        _lib.cdm_version.argtypes = []

        _lib.cdm_generate_shell_boxes.restype = ctypes.c_int
        _lib.cdm_generate_shell_boxes.argtypes = [
            ctypes.POINTER(ctypes.c_float),
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_int),
            ctypes.c_int,
            ctypes.c_float,                  # angle_thresh_deg
            ctypes.c_float,                  # min_area
            ctypes.c_float,                  # shell_pad
            ctypes.POINTER(ctypes.c_float),
            ctypes.c_int,
        ]

        return _lib
    except OSError:
        _lib = None
        return None


def engine_available() -> bool:
    """True wenn die DLL geladen werden konnte."""
    return _load() is not None


def engine_version() -> str:
    lib = _load()
    if lib is None:
        return "not loaded"
    return lib.cdm_version().decode("utf-8")


def generate_geo_boxes(verts, tris,
                       angle_thresh: float = 30.0,
                       min_area: float = 0.5,
                       min_thickness: float = 0.5,
                       max_boxes: int = 512):
    """
    Parameter:
        verts         : list von (x,y,z) — Welt-Koordinaten
        tris          : list von (i0,i1,i2) — Dreieck-Indices (0-based)
        angle_thresh  : Cluster-Winkel-Schwellwert in Grad
        min_area      : Mindestfläche eines Clusters in m²
        min_thickness : Mindest-Boxtiefe in m
        max_boxes     : maximale Anzahl Output-Boxen

    Rückgabe:
        Liste von Boxen — jede Box = 8×(x,y,z) Tuples (OBB-Ecken).

    Raises:
        GeoEngineError bei Alloc-Fehler (-1) in der nativen Engine.
    """
    lib = _load()
    if lib is None:
        return []

    n_verts = len(verts)
    n_tris = len(tris)
    if n_verts == 0 or n_tris == 0:
        return []

    c_verts = (ctypes.c_float * (n_verts * 3))()
    for i, (x, y, z) in enumerate(verts):
        c_verts[i * 3 + 0] = x
        c_verts[i * 3 + 1] = y
        c_verts[i * 3 + 2] = z

    c_tris = (ctypes.c_int * (n_tris * 3))()
    for i, (a, b, c) in enumerate(tris):
        c_tris[i * 3 + 0] = a
        c_tris[i * 3 + 1] = b
        c_tris[i * 3 + 2] = c

    c_out = (ctypes.c_float * (max_boxes * 24))()

    n = lib.cdm_generate_geo_boxes(
        c_verts, n_verts,
        c_tris, n_tris,
        ctypes.c_float(angle_thresh),
        ctypes.c_float(min_area),
        ctypes.c_float(min_thickness),
        c_out,
        max_boxes,
    )

    if n < 0:
        raise GeoEngineError("cdm_generate_geo_boxes failed (allocation error)")
    if n == 0:
        return []

    result = []
    for b in range(n):
        base = b * 24
        corners = []
        for k in range(8):
            x = c_out[base + k * 3 + 0]
            y = c_out[base + k * 3 + 1]
            z = c_out[base + k * 3 + 2]
            corners.append((x, y, z))
        result.append(corners)

    return result


def generate_shell_boxes(verts, tris,
                         angle_thresh: float = 30.0,
                         min_area: float = 0.0,
                         shell_pad: float = 0.001,
                         max_boxes: int = 512):
    """
    Shell engine (C): face-angle slabs + antiparallel merge on closed mesh.
    """
    lib = _load()
    if lib is None or not hasattr(lib, 'cdm_generate_shell_boxes'):
        return []

    n_verts = len(verts)
    n_tris = len(tris)
    if n_verts == 0 or n_tris == 0:
        return []

    c_verts = (ctypes.c_float * (n_verts * 3))()
    for i, (x, y, z) in enumerate(verts):
        c_verts[i * 3 + 0] = x
        c_verts[i * 3 + 1] = y
        c_verts[i * 3 + 2] = z

    c_tris = (ctypes.c_int * (n_tris * 3))()
    for i, (a, b, c) in enumerate(tris):
        c_tris[i * 3 + 0] = a
        c_tris[i * 3 + 1] = b
        c_tris[i * 3 + 2] = c

    c_out = (ctypes.c_float * (max_boxes * 24))()

    n = lib.cdm_generate_shell_boxes(
        c_verts, n_verts,
        c_tris, n_tris,
        ctypes.c_float(angle_thresh),
        ctypes.c_float(min_area),
        ctypes.c_float(shell_pad),
        c_out,
        max_boxes,
    )

    if n < 0:
        raise GeoEngineError("cdm_generate_shell_boxes failed (allocation error)")
    if n == 0:
        return []

    result = []
    for b in range(n):
        base = b * 24
        corners = []
        for k in range(8):
            corners.append((
                c_out[base + k * 3 + 0],
                c_out[base + k * 3 + 1],
                c_out[base + k * 3 + 2],
            ))
        result.append(corners)
    return result
