"""
CDM Collider — Collision geometry / Geo LOD for DayZ workflows.

Standalone Blender extension (VHACD, CoACD, C# GeoEngine, Geo-Regionen).
Detects CDM Architect / P3D Studio and can share one N-Panel tab ``CDM``.
"""

bl_info = {
    "name":        "CDM Collider",
    "author":      "CDM",
    "version":     (1, 0, 3),
    "blender":     (4, 2, 0),
    "location":    "View3D > Sidebar > CDM Collider (or CDM when Set)",
    "description": "Collision geometry, Geo LOD, VHACD and CoACD",
    "doc_url":     "https://www.youtube.com/@crash_dayz_modding",
    "tracker_url": "https://discord.gg/9PM8BjWmp8",
    "category":    "Object",
}

import os

import bpy
from bpy.props import StringProperty, BoolProperty

from . import properties, operators
from .geo import geo_region_ops, panel_ui
from . import cdm_companions as companions
from . import cdm_prefs_ui as prefs_ui

CDM_OT_collider_uninstall = prefs_ui.make_uninstall_operator(
    "cdm.collider_uninstall", "cdm_collider", "CDM Collider",
)


class CDM_ColliderPreferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    building_geo_lod_experimental: BoolProperty(
        name="Gebäude Geo LOD (Beta-Test)",
        description="Aktiviert die Collision-Geo-Pipeline für Gebäude "
                    "(C# GeoEngine, Geo-Regionen) als Beta-Test. "
                    "Standard: aus. Ergebnisse können ungenau sein.",
        default=False,
    )

    unify_n_panel_with_set: BoolProperty(
        name="N-Panel mit Set zusammenführen",
        description="Wenn Architect/P3D Studio installiert sind: gemeinsames N-Panel „CDM“",
        default=True,
    )

    vhacd_exe_path: StringProperty(
        name="TestVHACD.exe",
        description="Pfad zur TestVHACD.exe (V-HACD 4.x CLI). "
                    "Download: github.com/kmammou/v-hacd → app/TestVHACD.exe",
        subtype="FILE_PATH",
        default="",
    )

    def draw(self, context):
        layout = self.layout
        prefs_ui.draw_prefs_header(layout, "CDM Collider", icon="MOD_EXPLODE")
        prefs_ui.draw_links(layout)
        companions.draw_companion_status(layout, "cdm_collider", context)
        layout.prop(self, "unify_n_panel_with_set")

        box_geo = layout.box()
        box_geo.label(text="Gebäude Geo LOD", icon="HOME")
        box_geo.prop(self, "building_geo_lod_experimental", toggle=True)
        hint = box_geo.column(align=True)
        hint.scale_y = 0.85
        hint.label(text="Beta-Test — Collision-Geo für Gebäude.", icon="ERROR")
        hint.label(text="N-Panel: CDM Collider (oder CDM im Set).", icon="BLANK1")

        layout.separator()
        _addon_dir = os.path.dirname(os.path.abspath(__file__))
        _addon_exe = os.path.join(_addon_dir, "TestVHACD.exe")
        _exe_in_prefs = bool(self.vhacd_exe_path.strip())
        _prefs_exe_ok = False
        if _exe_in_prefs:
            try:
                _prefs_exe_ok = os.path.isfile(bpy.path.abspath(self.vhacd_exe_path))
            except Exception:
                _prefs_exe_ok = os.path.isfile(self.vhacd_exe_path.strip())
        _exe_found = _prefs_exe_ok or os.path.isfile(_addon_exe)

        box2 = layout.box()
        box2.label(text="V-HACD Convex Decomposition", icon="MOD_EXPLODE")
        box2.prop(self, "vhacd_exe_path")
        row = box2.row()
        row.alert = not _exe_found
        if _prefs_exe_ok:
            row.label(text="Exe-Pfad gesetzt.", icon="CHECKMARK")
        elif _exe_found:
            row.label(text="TestVHACD.exe im Addon-Ordner gefunden.", icon="CHECKMARK")
        else:
            row.label(text="Kein Pfad / keine TestVHACD.exe im Addon.", icon="ERROR")
        box2.operator(
            "wm.url_open",
            text="Download: github.com/kmammou/v-hacd",
            icon="URL",
        ).url = "https://github.com/kmammou/v-hacd"

        layout.separator()
        from .coacd_bridge import bundled_coacd_wheel_path
        _coacd_wheel = bundled_coacd_wheel_path()
        box4 = layout.box()
        box4.label(text="CoACD Convex Decomposition", icon="MOD_EXPLODE")
        box4.label(text="Läuft über Blenders Python — kein Exe-Pfad nötig.", icon="INFO")
        if _coacd_wheel:
            box4.label(text="Wheel liegt im Addon-Ordner: bundled/", icon="CHECKMARK")
        else:
            box4.label(text="Wheel fehlt (bundled/) — ZIP neu bauen.", icon="ERROR")
        box4.operator(
            "wm.url_open",
            text="Download / Doku: github.com/SarahWeiii/CoACD",
            icon="URL",
        ).url = "https://github.com/SarahWeiii/CoACD"

        layout.separator()
        box3 = layout.box()
        box3.label(text="Lizenz", icon="SCRIPT")
        box3.label(text="CDM Collider — GPLv3 © 2026 CDM")
        box3.label(text="V-HACD — BSD 3-Clause © 2011 Khaled Mamou")
        box3.label(text="CoACD — MIT © Xinyue Wei et al. (SIGGRAPH 2022)")

        prefs_ui.draw_uninstall_button(layout, "cdm.collider_uninstall")


def register():
    bpy.utils.register_class(CDM_ColliderPreferences)
    bpy.utils.register_class(CDM_OT_collider_uninstall)
    properties.register()
    operators.register()
    geo_region_ops.register()
    panel_ui.register()


def unregister():
    panel_ui.unregister()
    geo_region_ops.unregister()
    operators.unregister()
    properties.unregister()
    bpy.utils.unregister_class(CDM_OT_collider_uninstall)
    bpy.utils.unregister_class(CDM_ColliderPreferences)
