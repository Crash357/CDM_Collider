"""Shared Preferences UI helpers (identical copy in each CDM addon)."""
from __future__ import annotations

import bpy

from .addon_version import version_label

YOUTUBE_URL = "https://www.youtube.com/@crash_dayz_modding"
DISCORD_URL = "https://discord.gg/9PM8BjWmp8"


def draw_prefs_header(layout, title: str, *, icon: str = 'PLUGIN') -> None:
    """Title row + version from blender_manifest.toml."""
    row = layout.row(align=True)
    row.label(text=title, icon=icon)
    row.label(text=version_label())


def draw_links(layout) -> None:
    box = layout.box()
    box.label(text="Links & Community", icon='URL')
    row = box.row(align=True)
    row.operator("wm.url_open", text="YouTube", icon="FILE_MOVIE").url = YOUTUBE_URL
    row.operator("wm.url_open", text="Discord", icon="COMMUNITY").url = DISCORD_URL


def draw_uninstall_button(layout, operator_id: str) -> None:
    box = layout.box()
    box.label(text="Addon entfernen", icon="TRASH")
    col = box.column(align=True)
    col.scale_y = 0.85
    col.label(text="Deinstalliert dieses Extension-Paket.", icon="INFO")
    col.label(text="Danach Blender neu starten.", icon="BLANK1")
    row = box.row()
    row.alert = True
    row.operator(operator_id, text="Uninstall Addon", icon="TRASH")


def schedule_extension_uninstall(pkg_id: str, display_name: str, report) -> set:
    """Deferred extensions.package_uninstall (safe for Blender 4.2+)."""
    context = bpy.context
    repo_index = None
    try:
        repos = context.preferences.extensions.repos
        for i, repo in enumerate(repos):
            module = getattr(repo, "module", "") or ""
            name = getattr(repo, "name", "") or ""
            if "user_default" in module or "user_default" in name.lower():
                repo_index = i
                break
    except Exception:
        repo_index = None

    if repo_index is None:
        try:
            bpy.ops.screen.userpref_show()
        except Exception:
            pass
        report(
            {"WARNING"},
            "Automatic uninstall not possible. "
            "Please remove the addon manually in Settings (Extensions).",
        )
        return {"FINISHED"}

    def _do_uninstall():
        try:
            bpy.ops.extensions.package_uninstall(
                repo_index=repo_index,
                pkg_id=pkg_id,
            )
        except Exception:
            pass
        return None

    bpy.app.timers.register(_do_uninstall, first_interval=0.3)
    report(
        {"INFO"},
        "Uninstalling {}… Please restart Blender afterwards.".format(display_name),
    )
    return {"FINISHED"}


def make_uninstall_operator(op_id: str, pkg_id: str, display_name: str):
    """Build a unique uninstall Operator class for one product."""

    class CDM_OT_product_uninstall(bpy.types.Operator):
        bl_options = {"INTERNAL"}

        def invoke(self, context, event):
            return context.window_manager.invoke_confirm(self, event)

        def execute(self, context):
            return schedule_extension_uninstall(pkg_id, display_name, self.report)

    CDM_OT_product_uninstall.bl_idname = op_id
    CDM_OT_product_uninstall.bl_label = "Uninstall Addon"
    CDM_OT_product_uninstall.bl_description = (
        "Uninstall {} completely from Blender".format(display_name)
    )
    CDM_OT_product_uninstall.__name__ = "CDM_OT_uninstall_{}".format(
        pkg_id.replace(".", "_")
    )
    CDM_OT_product_uninstall.__qualname__ = CDM_OT_product_uninstall.__name__
    CDM_OT_product_uninstall.__doc__ = (
        "Uninstalls {} completely from Blender. "
        "All addon files are removed. Restart Blender afterwards."
    ).format(display_name)
    return CDM_OT_product_uninstall
