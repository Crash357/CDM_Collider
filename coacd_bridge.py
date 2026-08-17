"""
CDM CoACD Bridge — Collision-Aware Approximate Convex Decomposition.

Nutzt die CoACD Python-API direkt (kein Subprocess, kein Exe-Pfad nötig).

Installation (einmalig in Blenders Python):
    & "<Blender>\5.1\python\bin\python.exe" -m pip install coacd-1.0.11-cp39-abi3-win_amd64.whl

Quelle: https://github.com/SarahWeiii/CoACD
Lizenz: MIT © Xinyue Wei et al. (SIGGRAPH 2022)
"""

import os

import numpy as np

_COACD_WHEEL_NAME = "coacd-1.0.11-cp39-abi3-win_amd64.whl"


def bundled_coacd_wheel_path() -> str | None:
    """Wheel shipped inside addon ``bundled/`` (install ZIP)."""
    addon_dir = os.path.dirname(os.path.abspath(__file__))
    wheel = os.path.join(addon_dir, "bundled", _COACD_WHEEL_NAME)
    return wheel if os.path.isfile(wheel) else None


def run_coacd(mesh_verts, mesh_tris,
              threshold=0.05,
              max_hulls=0,
              preprocess_mode='auto',
              prep_resolution=50,
              mcts_iterations=100,
              max_ch_vertex=64,
              exe_path=None):
    """Fuehrt CoACD auf dem gegebenen Mesh aus (Python-API).

    Parameters
    ----------
    mesh_verts      : list of (x, y, z)
    mesh_tris       : list of (a, b, c)  -- 0-basierte Indizes
    threshold       : Konkavitaetsschwelle (0.01-1.0) -- kleiner = mehr Hulls
    max_hulls       : Max. Hulls (0 = unbegrenzt)
    preprocess_mode : 'auto' | 'on' | 'off'
    prep_resolution : Manifold-Preprocessing-Aufloesung (20-100)
    mcts_iterations : MCTS Suchiterationen (60-2000)
    max_ch_vertex   : Max. Verts pro Hull (DayZ-Limit: 255, Empfehlung: 64)
    exe_path        : wird ignoriert (Kompatibilitaetsparameter)

    Returns
    -------
    list of (verts, tris)

    Raises
    ------
    RuntimeError  falls CoACD nicht installiert ist
    """
    try:
        import coacd
    except ImportError:
        wheel = bundled_coacd_wheel_path()
        wheel_hint = wheel if wheel else f"bundled/{_COACD_WHEEL_NAME}"
        raise RuntimeError(
            "CoACD ist nicht installiert.\n"
            "Einmalig in Blenders Python ausfuehren:\n"
            f"  python.exe -m pip install \"{wheel_hint}\"\n"
            "Das Wheel liegt im Addon-Ordner unter bundled/ (Install-ZIP).\n"
            "Details: Edit > Preferences > Add-ons > CDM Collider"
        )

    verts_np = np.array(mesh_verts, dtype=np.float64)
    tris_np  = np.array(mesh_tris,  dtype=np.int32)

    mesh = coacd.Mesh(verts_np, tris_np)

    max_ch = -1 if max_hulls == 0 else max_hulls

    parts = coacd.run_coacd(
        mesh,
        threshold=threshold,
        max_convex_hull=max_ch,
        preprocess_mode=preprocess_mode,
        preprocess_resolution=prep_resolution,
        mcts_iterations=mcts_iterations,
        decimate=True,
        max_ch_vertex=max_ch_vertex,
    )

    # parts: list of (np.array Nx3 float, np.array Mx3 int)
    result = []
    for hull_verts, hull_tris in parts:
        v = [tuple(float(x) for x in row) for row in hull_verts]
        t = [tuple(int(x) for x in row) for row in hull_tris]
        if len(v) >= 4:
            result.append((v, t))
    return result
