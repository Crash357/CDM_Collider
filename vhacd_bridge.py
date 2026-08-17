"""
CDM V-HACD Bridge — V-HACD 4.x Integration via subprocess.

Ruft TestVHACD.exe auf (Header-only V-HACD 4.x CLI-Tool).
Keine Python-Abhängigkeiten außer stdlib.

Pfad zur Exe: Addon-Preferences → 'V-HACD Executable'
"""

import os
import subprocess
import tempfile


# ---------------------------------------------------------------------------
# Exe-Suche
# ---------------------------------------------------------------------------

def find_vhacd_exe():
    """Sucht TestVHACD.exe: erst Preferences, dann addon-dir, dann PATH.
    Gibt den Pfad zurück oder None."""
    try:
        import bpy
        from .addon_prefs import get_addon_preferences
        prefs = get_addon_preferences()
        if prefs:
            p = prefs.vhacd_exe_path.strip()
            if p and os.path.isfile(p):
                return p
    except Exception:
        pass

    # Neben diesem Skript (addon-root)
    addon_dir = os.path.dirname(__file__)
    candidate = os.path.join(addon_dir, "TestVHACD.exe")
    if os.path.isfile(candidate):
        return candidate

    # Systemweiter PATH
    import shutil
    found = shutil.which("TestVHACD")
    if found:
        return found

    return None


# ---------------------------------------------------------------------------
# OBJ I/O
# ---------------------------------------------------------------------------

def _export_obj(verts, tris, filepath):
    """Schreibt Vertices + Dreiecke als minimale Wavefront-OBJ-Datei."""
    with open(filepath, 'w') as f:
        for x, y, z in verts:
            f.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")
        for a, b, c in tris:
            f.write(f"f {a + 1} {b + 1} {c + 1}\n")


def _parse_obj(filepath):
    """Liest OBJ zurück.  Gibt (verts_list, tris_list) zurück (0-basiert)."""
    verts = []
    tris  = []
    with open(filepath, 'r') as f:
        for line in f:
            parts = line.split()
            if not parts:
                continue
            if parts[0] == 'v':
                verts.append((float(parts[1]), float(parts[2]), float(parts[3])))
            elif parts[0] == 'f':
                # Indices können "1/1/1"-Format haben
                raw = [int(p.split('/')[0]) - 1 for p in parts[1:]]
                # Fan-Triangulierung für Polygone
                for i in range(1, len(raw) - 1):
                    tris.append((raw[0], raw[i], raw[i + 1]))
    return verts, tris


# ---------------------------------------------------------------------------
# Haupt-API
# ---------------------------------------------------------------------------

def run_vhacd(mesh_verts, mesh_tris,
              max_hulls=32,
              resolution=200_000,
              max_verts_per_hull=32,
              fill_mode='flood',
              error_percent=2.0,
              exe_path=None):
    """Führt V-HACD 4.x auf dem gegebenen Mesh aus.

    Parameters
    ----------
    mesh_verts       : list of (x, y, z)
    mesh_tris        : list of (a, b, c)  — 0-basierte Indizes
    max_hulls        : Maximale Anzahl Konvex-Hulls (1–256)
    resolution       : Voxel-Auflösung (10 000–10 000 000)
    max_verts_per_hull: Max. Vertices pro Hull (4–2048)
    fill_mode        : 'flood' | 'raycast' | 'surface'
    error_percent    : Erlaubter Volumenfehler 0.001–10.0
    exe_path         : Optionaler expliziter Pfad zur TestVHACD.exe

    Returns
    -------
    list of (verts, tris)  — eine Eintrag pro Konvex-Hull

    Raises
    ------
    RuntimeError  falls Exe fehlt, Timeout, oder V-HACD fehlschlägt
    """
    exe = exe_path or find_vhacd_exe()
    if not exe:
        raise RuntimeError(
            "TestVHACD.exe nicht gefunden.\n"
            "Bitte den Pfad in den Addon-Preferences unter "
            "'V-HACD Executable' eintragen."
        )

    with tempfile.TemporaryDirectory() as tmpdir:
        in_obj = os.path.join(tmpdir, "input.obj")
        _export_obj(mesh_verts, mesh_tris, in_obj)

        cmd = [
            exe, in_obj,
            "-h", str(max_hulls),
            "-r", str(resolution),
            "-v", str(max_verts_per_hull),
            "-f", fill_mode,
            "-e", str(error_percent),
            "-a", "false",   # synchron (kein std::thread in Blender)
            "-g", "false",   # kein Progress-Logging im Subprocess
            "-o", "obj",     # Ausgabe als OBJ-Dateien
        ]

        try:
            result = subprocess.run(
                cmd,
                cwd=tmpdir,
                capture_output=True,
                timeout=120,
            )
        except subprocess.TimeoutExpired:
            raise RuntimeError("V-HACD: Timeout nach 120 Sekunden.")
        except FileNotFoundError:
            raise RuntimeError(f"V-HACD Exe nicht gefunden: {exe}")

        if result.returncode != 0:
            err = result.stderr.decode("utf-8", errors="replace")
            raise RuntimeError(
                f"V-HACD Fehler (exit {result.returncode}): {err[:500]}"
            )

        # Ausgabe-OBJ lesen: {baseName}{index:03d}.obj
        # baseName = input-Dateiname ohne Extension = "input"
        # → input000.obj, input001.obj, ...
        hulls = []
        idx = 0
        while True:
            hull_path = os.path.join(tmpdir, f"input{idx:03d}.obj")
            if not os.path.isfile(hull_path):
                break
            verts, tris = _parse_obj(hull_path)
            if len(verts) >= 4:
                hulls.append((verts, tris))
            idx += 1

    return hulls
