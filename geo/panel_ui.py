"""CDM Collider — collapsible N-Panel sub-sections."""
from __future__ import annotations

import bpy

from .. import geometry as geo
from .geo_regions import (
    REGION_KIND_ITEMS,
    REGION_OPTIONAL_KINDS,
    has_minimum_seeds,
    region_label,
    resolve_resolution_obj,
    seed_count_by_kind,
    seeds_for_object,
)

_COACD_DAYZ_PRESET = {
    'threshold': 0.15,
    'max_hulls': 32,
    'preprocess': 'auto',
    'prep_res': 50,
    'mcts_iter': 100,
    'max_ch_vertex': 64,
}


def _cs_engine_status():
    try:
        from .cs_engine_bridge import (
            corpus_summary,
            cs_engine_available,
            generation_mode_display,
            resolve_generation_mode,
        )
        return (
            cs_engine_available(),
            corpus_summary(),
            resolve_generation_mode(),
            generation_mode_display(),
        )
    except Exception:
        return False, None, 'custom', ('Modus: Custom-Gebäude', 'INFO')


class CDM_PT_geo_building(bpy.types.Panel):
    bl_label = 'Gebäude — Geo LOD'
    bl_idname = 'CDM_PT_geo_building'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'CDM Collider'
    bl_parent_id = 'CDM_PT_main_panel'
    bl_order = 0
    bl_options = {'DEFAULT_CLOSED'}

    @classmethod
    def poll(cls, context):
        from ..addon_prefs import building_geo_lod_enabled
        return building_geo_lod_enabled(context)

    def draw_header(self, context):
        self.layout.label(icon='HOME')

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        cs_ok, corpus, gen_mode, (mode_label, mode_icon) = _cs_engine_status()

        info = layout.column(align=True)
        info.scale_y = 0.78
        if cs_ok:
            info.label(text='C# GeoEngine', icon='SCRIPT')
        else:
            info.label(text='C# GeoEngine nicht gebaut', icon='ERROR')
        if gen_mode == 'corpus' and corpus:
            info.label(
                text='Corpus: {} Modelle'.format(corpus.get('model_count', 0)),
                icon='ASSET_MANAGER',
            )
        elif corpus:
            info.label(
                text='Corpus: {} (Sandbox)'.format(corpus.get('model_count', 0)),
                icon='BLANK1',
            )
        info.label(text=mode_label, icon=mode_icon)

        layout.separator(factor=0.35)
        row = layout.row()
        row.scale_y = 1.75
        row.enabled = not scene.cdm_engine_busy
        row.operator(
            'cdm.building_auto_geo',
            text='Blind generieren',
            icon='TIME' if scene.cdm_engine_busy else 'AUTO',
        )
        blind_hint = layout.column(align=True)
        blind_hint.scale_y = 0.72
        blind_hint.label(
            text='1 mm Skin über Flächen — kein Corpus',
            icon='INFO',
        )

        if scene.cdm_engine_busy:
            busy = layout.box()
            busy.alert = True
            busy.label(text=scene.cdm_engine_status or 'Engine arbeitet…', icon='SORTTIME')

        if scene.cdm_auto_geo_model_id:
            vcol = layout.column(align=True)
            vcol.scale_y = 0.72
            pipe = getattr(scene, 'cdm_last_geo_pipeline', '') or '—'
            vcol.label(text='Pipeline: {}'.format(pipe), icon='INFO')
            icon = 'CHECKMARK' if scene.cdm_auto_geo_passed else 'ERROR'
            score_pct = int(round(scene.cdm_auto_geo_score * 100))
            score_txt = '{} — {:.0f}%'.format(
                scene.cdm_auto_geo_model_id,
                score_pct,
            ) if scene.cdm_auto_geo_score > 0 else '{} — Score n/v'.format(
                scene.cdm_auto_geo_model_id,
            )
            vcol.label(text=score_txt, icon=icon)
            if scene.cdm_auto_geo_obb_score > 0 or scene.cdm_auto_geo_coverage_score > 0:
                vcol.label(
                    text='OBB {:.0f}%  |  Cov {:.0f}%'.format(
                        scene.cdm_auto_geo_obb_score * 100,
                        scene.cdm_auto_geo_coverage_score * 100,
                    ),
                    icon='BLANK1',
                )

        adv = layout.row()
        adv.prop(scene, 'cdm_geo_show_advanced', text='Erweitert', icon='TRIA_DOWN' if scene.cdm_geo_show_advanced else 'TRIA_RIGHT', emboss=False)
        if scene.cdm_geo_show_advanced:
            layout.separator(factor=0.2)
            row_a = layout.row(align=True)
            row_a.label(text='Min Area:')
            row_a.prop(scene, 'cdm_min_area', text='m²')
            row_g = layout.row(align=True)
            row_g.label(text='Angle:')
            row_g.prop(scene, 'cdm_angle_threshold', text='°')
            row_d = layout.row(align=True)
            row_d.label(text='Density:')
            row_d.prop(scene, 'cdm_geo_density', text='')
            bcol = layout.column(align=True)
            bcol.scale_y = 1.15
            cs_row = bcol.row()
            cs_row.enabled = cs_ok and not scene.cdm_engine_busy
            cs_row.operator('cdm.building_cs_generate', text='Nur Components', icon='PLAY')
            fin_row = bcol.row()
            fin_row.enabled = not scene.cdm_engine_busy
            fin_row.operator('cdm.building_finalize', text='Finalize → Geometry', icon='CHECKMARK')
            bcol.separator(factor=0.15)
            bcol.operator('cdm.building_angle_split', text='Angle Split', icon='MOD_EDGESPLIT')
            bcol.operator('cdm.export_compare_dumps', text='Vergleichs-Dumps', icon='EXPORT')


class CDM_PT_geo_manual(bpy.types.Panel):
    bl_label = 'Manuell'
    bl_idname = 'CDM_PT_geo_manual'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'CDM Collider'
    bl_parent_id = 'CDM_PT_main_panel'
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 10

    def draw_header(self, context):
        self.layout.label(icon='TOOL_SETTINGS')

    def draw(self, context):
        layout = self.layout
        obj = context.active_object
        in_edit = obj is not None and obj.type == 'MESH' and context.mode == 'EDIT_MESH'
        hint = layout.box()
        hint.scale_y = 0.82
        if in_edit:
            hint.label(text='Edit Mode aktiv', icon='EDITMODE_HLT')
        else:
            row = hint.row()
            row.alert = True
            row.label(text='Für Auswahl: Edit Mode (Tab)', icon='INFO')
        layout.separator(factor=0.25)
        col = layout.column(align=True)
        col.scale_y = 1.55
        col.operator('cdm.direct', text='Islands → Components', icon='MESH_DATA')
        col.operator('cdm.merge_exact', text='Merge → Geometry LOD', icon='CHECKMARK')
        col.operator('cdm.tag_geo_lod', text='Tag as Geo LOD', icon='PROPERTIES')
        layout.separator(factor=0.35)
        col2 = layout.column(align=True)
        col2.scale_y = 1.4
        col2.operator('cdm.add_geo_from_selection', text='Faces → AABB', icon='CUBE')
        col2.operator('cdm.from_selection', text='Verts → Hull', icon='MESH_ICOSPHERE')
        layout.separator(factor=0.35)
        col3 = layout.column(align=True)
        col3.scale_y = 1.3
        col3.operator('cdm.fix_open_meshes', text='Fill Holes', icon='UV_SYNC_SELECT')
        col3.operator('cdm.select_open_islands', text='Open Islands', icon='RESTRICT_SELECT_OFF')


class CDM_PT_geo_display(bpy.types.Panel):
    bl_label = 'Anzeige'
    bl_idname = 'CDM_PT_geo_display'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'CDM Collider'
    bl_parent_id = 'CDM_PT_main_panel'
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 20

    def draw_header(self, context):
        self.layout.label(icon='COLOR')

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        row_gc = layout.row(align=True)
        row_gc.label(text='Geometry:')
        row_gc.prop(scene, 'cdm_geo_color', text='')
        row_cc = layout.row(align=True)
        row_cc.label(text='Components:')
        row_cc.prop(scene, 'cdm_comp_color', text='')
        btn = layout.row(align=True)
        btn.scale_y = 1.25
        btn.operator('cdm.recolor_geo', text='An', icon='BRUSH_DATA')
        btn.operator('cdm.reset_geo_display', text='Aus', icon='X')


class CDM_PT_geo_experimental(bpy.types.Panel):
    bl_label = 'Auto-Generation'
    bl_idname = 'CDM_PT_geo_experimental'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'CDM Collider'
    bl_parent_id = 'CDM_PT_main_panel'
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 30

    def draw_header(self, context):
        self.layout.label(icon='MOD_EXPLODE')

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        row_m = layout.row(align=True)
        row_m.label(text='Method:')
        row_m.prop(scene, 'cdm_method', text='')

        is_vhacd = scene.cdm_method == 'VHACD'
        is_coacd = scene.cdm_method == 'COACD'

        if is_vhacd:
            vbox = layout.box()
            vbox.label(text='V-HACD', icon='SETTINGS')
            for label, prop in (
                ('Max Hulls', 'cdm_vhacd_max_hulls'),
                ('Resolution', 'cdm_vhacd_resolution'),
                ('Max Verts', 'cdm_vhacd_max_verts'),
            ):
                r = vbox.row(align=True)
                r.label(text=label + ':')
                r.prop(scene, prop, text='')
            vbox.operator('cdm.merge_components', text='Merge → Geometry', icon='CHECKMARK')
        elif is_coacd:
            cbox = layout.box()
            cbox.label(text='CoACD', icon='SETTINGS')
            cbox.prop(scene, 'cdm_coacd_dayz_preset', text='DayZ Preset', icon='RESTRICT_RENDER_OFF')
            if scene.cdm_coacd_dayz_preset:
                p = _COACD_DAYZ_PRESET
                inf = cbox.column(align=True)
                inf.enabled = False
                inf.scale_y = 0.85
                inf.label(text='Thr {:.2f}  Hulls {}'.format(p['threshold'], p['max_hulls']))
            else:
                for label, prop in (
                    ('Threshold', 'cdm_coacd_threshold'),
                    ('Max Hulls', 'cdm_coacd_max_hulls'),
                    ('Preprocess', 'cdm_coacd_preprocess'),
                    ('Prep Res', 'cdm_coacd_prep_res'),
                    ('MCTS Iter', 'cdm_coacd_mcts_iter'),
                    ('Max Verts', 'cdm_coacd_max_ch_vertex'),
                ):
                    r = cbox.row(align=True)
                    r.label(text=label + ':')
                    r.prop(scene, prop, text='')
            cbox.operator('cdm.merge_components', text='Merge → Geometry', icon='CHECKMARK')

        layout.separator(factor=0.25)
        acol = layout.column(align=True)
        acol.scale_y = 1.45
        acol.operator('cdm.auto_building', text='Auto Geo LOD', icon='MOD_EXPLODE')
        ts = layout.column(align=True)
        ts.scale_y = 1.2
        ts.operator('cdm.decompose', text='1. Decompose', icon='MESH_DATA')
        if geo.method_uses_hull_merge(scene.cdm_method):
            ts.operator('cdm.merge_components', text='2. Merge Hull', icon='MESH_ICOSPHERE')
        else:
            ts.operator('cdm.merge_exact', text='2. Merge Exact', icon='SNAP_FACE')


class CDM_PT_geo_info(bpy.types.Panel):
    bl_label = 'Info & Check'
    bl_idname = 'CDM_PT_geo_info'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'CDM Collider'
    bl_parent_id = 'CDM_PT_main_panel'
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 40

    def draw_header(self, context):
        self.layout.label(icon='INFO')

    def draw(self, context):
        layout = self.layout
        layout.label(text="Collection 'Geometry'", icon='OUTLINER_COLLECTION')
        row = layout.row(align=True)
        row.scale_y = 1.35
        row.operator('cdm.dayz_check', text='DayZ Check', icon='CHECKMARK')
        from ..addon_version import version_label
        layout.separator(factor=0.3)
        layout.label(text='CDM Collider {}'.format(version_label()), icon='BLENDER')
        lrow = layout.row(align=True)
        lrow.operator('wm.url_open', text='Discord', icon='COMMUNITY').url = 'https://discord.gg/9PM8BjWmp8'
        lrow.operator('wm.url_open', text='YouTube', icon='FILE_MOVIE').url = 'https://www.youtube.com/@crash_dayz_modding'


PANEL_CLASSES = (
    CDM_PT_geo_building,
    CDM_PT_geo_manual,
    CDM_PT_geo_display,
    CDM_PT_geo_experimental,
    CDM_PT_geo_info,
)


def register():
    from .. import cdm_companions as companions
    cat = companions.resolve_category('CDM Collider', 'cdm_collider')
    companions.apply_n_panel_category(PANEL_CLASSES, cat)
    for cls in PANEL_CLASSES:
        bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(PANEL_CLASSES):
        bpy.utils.unregister_class(cls)
