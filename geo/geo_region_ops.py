"""Blender UI + picker operators for sparse geo region seeds."""
from __future__ import annotations

import bpy

from .geo_regions import (
    REGION_ESSENTIAL_KINDS,
    REGION_KIND_ITEMS,
    REGION_OPTIONAL_KINDS,
    add_seed,
    essential_requirement_rows,
    has_minimum_seeds,
    minimum_requirements_summary,
    region_label,
    remove_seeds_for_kind,
    resolve_resolution_obj,
    seed_count_by_kind,
    seeds_for_object,
)
from .region_picker_core import (
    clamp_overlay_position,
    cycle_region_kind,
    header_rect,
    overlay_rect,
    point_in_rect,
)
from .region_picker_draw import draw_region_picker_overlay
from .region_picker_raycast import raycast_face_on_object
from .geo_region_ui import engine_diagnose, refresh_region_view, select_resolution_mesh
from .region_seed_viz import register_draw_handler as register_region_viz_handler
from .region_seed_viz import unregister_draw_handler as unregister_region_viz_handler

_DRAW_HANDLE = None
_ACTIVE_PICKER = None


def _draw_overlay_callback():
    if _ACTIVE_PICKER is None:
        return
    try:
        context = bpy.context
        if context is None or context.scene is None:
            return
        scene = context.scene
        x = int(scene.cdm_geo_picker_overlay_x)
        y = int(scene.cdm_geo_picker_overlay_y)
        lod = resolve_resolution_obj(context)
        draw_region_picker_overlay(context, x, y, lod)
    except Exception as exc:
        print('CDM Geo Picker overlay draw failed:', exc)


def _register_draw_handler():
    global _DRAW_HANDLE
    if _DRAW_HANDLE is not None:
        return
    _DRAW_HANDLE = bpy.types.SpaceView3D.draw_handler_add(
        _draw_overlay_callback, (), 'WINDOW', 'POST_PIXEL',
    )


def _unregister_draw_handler():
    global _DRAW_HANDLE
    if _DRAW_HANDLE is None:
        return
    try:
        bpy.types.SpaceView3D.draw_handler_remove(_DRAW_HANDLE, 'WINDOW')
    except Exception:
        pass
    _DRAW_HANDLE = None


def _tag_redraw(context):
    if context and context.screen:
        for area in context.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()


def _draw_region_row(layout, scene, obj, kind_id: str, label: str, *, required: bool = False):
    counts = seed_count_by_kind(obj) if obj else {}
    count = counts.get(kind_id, 0)
    ok = count > 0

    row = layout.row(align=True)
    if required and not ok:
        row.alert = True

    icon = row.row(align=True)
    icon.scale_x = 0.55
    if required:
        icon.label(text='', icon='CHECKMARK' if ok else 'CANCEL')
    elif ok:
        icon.label(text='', icon='CHECKMARK')
    else:
        icon.label(text='', icon='BLANK1')

    op = row.operator(
        'cdm.geo_region_set_kind',
        text=label,
        depress=scene.cdm_geo_region_kind == kind_id,
    )
    op.kind = kind_id

    cnt = row.row(align=True)
    cnt.alignment = 'RIGHT'
    if ok:
        cnt.label(text=str(count), icon='NONE')
    elif required:
        cnt.label(text='fehlt', icon='ERROR')


class CDM_OT_geo_region_pick(bpy.types.Operator):
    """Viewport-Overlay: Pfeiltasten = Kategorie, LMB = Stichpunkt, Titelleiste ziehen"""
    bl_idname = 'cdm.geo_region_pick'
    bl_label = 'Region Picker (Overlay)'
    bl_options = {'REGISTER'}

    _dragging = False
    _drag_offset_x = 0
    _drag_offset_y = 0

    def invoke(self, context, event):
        global _ACTIVE_PICKER
        obj = resolve_resolution_obj(context)
        if obj is None:
            self.report({'ERROR'}, 'Kein Resolution LOD gefunden.')
            return {'CANCELLED'}

        if not select_resolution_mesh(context, obj):
            self.report({'ERROR'}, 'Mesh konnte nicht aktiviert werden.')
            return {'CANCELLED'}

        refresh_region_view(context, obj)

        self.report({'INFO'}, 'Object Mode — LMB auf Fläche klicken zum Markieren')

        _ACTIVE_PICKER = self
        _register_draw_handler()

        region = context.region
        if region:
            scene = context.scene
            scene.cdm_geo_picker_overlay_x, scene.cdm_geo_picker_overlay_y = clamp_overlay_position(
                scene.cdm_geo_picker_overlay_x,
                scene.cdm_geo_picker_overlay_y,
                region.width,
                region.height,
            )

        context.window_manager.modal_handler_add(self)
        _tag_redraw(context)
        return {'RUNNING_MODAL'}

    def modal(self, context, event):
        global _ACTIVE_PICKER
        scene = context.scene
        region = context.region
        if region is None:
            return {'PASS_THROUGH'}

        ox = int(scene.cdm_geo_picker_overlay_x)
        oy = int(scene.cdm_geo_picker_overlay_y)
        mx = event.mouse_region_x
        my = event.mouse_region_y

        if event.type in {'RIGHTMOUSE', 'ESC'} and event.value == 'PRESS':
            self._finish(context)
            return {'FINISHED'}

        if event.type in {'UP_ARROW', 'DOWN_ARROW', 'LEFT_ARROW', 'RIGHT_ARROW'} and event.value == 'PRESS':
            direction = 1 if event.type in {'DOWN_ARROW', 'RIGHT_ARROW'} else -1
            scene.cdm_geo_region_kind = cycle_region_kind(scene.cdm_geo_region_kind, direction)
            _tag_redraw(context)
            return {'RUNNING_MODAL'}

        if event.type == 'LEFTMOUSE':
            if event.value == 'PRESS':
                if point_in_rect(mx, my, header_rect(ox, oy)):
                    self._dragging = True
                    self._drag_offset_x = mx - ox
                    self._drag_offset_y = my - oy
                    return {'RUNNING_MODAL'}
                if point_in_rect(mx, my, overlay_rect(ox, oy)):
                    return {'RUNNING_MODAL'}

                obj = resolve_resolution_obj(context)
                if obj is None:
                    self.report({'ERROR'}, 'Kein Resolution LOD.')
                    return {'RUNNING_MODAL'}

                hit = raycast_face_on_object(context, event, obj)
                if hit is None:
                    self.report({'WARNING'}, 'Keine Fläche getroffen.')
                    return {'RUNNING_MODAL'}

                from .region_face_resolve import pick_face_from_raycast

                depsgraph = context.evaluated_depsgraph_get()
                face_i, world_pos, world_nrm = pick_face_from_raycast(obj, hit, depsgraph)
                if face_i < 0:
                    self.report({'WARNING'}, 'Flächen-Index nicht ermittelt.')
                    return {'RUNNING_MODAL'}

                add_seed(
                    obj,
                    scene.cdm_geo_region_kind,
                    face_i,
                    world_pos,
                    world_nrm,
                    replace_kind=False,
                )
                self.report({'INFO'}, '{} gesetzt'.format(region_label(scene.cdm_geo_region_kind)))
                refresh_region_view(context, obj)
                _tag_redraw(context)
                return {'RUNNING_MODAL'}

            if event.value == 'RELEASE' and self._dragging:
                self._dragging = False
                return {'RUNNING_MODAL'}

        if event.type == 'MOUSEMOVE' and self._dragging:
            nx = mx - self._drag_offset_x
            ny = my - self._drag_offset_y
            scene.cdm_geo_picker_overlay_x, scene.cdm_geo_picker_overlay_y = clamp_overlay_position(
                nx, ny, region.width, region.height,
            )
            _tag_redraw(context)
            return {'RUNNING_MODAL'}

        if event.type == 'RET' and event.value == 'PRESS':
            self._finish(context)
            return {'FINISHED'}

        return {'PASS_THROUGH'}

    def _finish(self, context):
        global _ACTIVE_PICKER
        from .helpers import ensure_object_mode

        ensure_object_mode()
        _ACTIVE_PICKER = None
        _unregister_draw_handler()
        _tag_redraw(context)

    def cancel(self, context):
        self._finish(context)


class CDM_OT_geo_region_clear(bpy.types.Operator):
    bl_idname = 'cdm.geo_region_clear'
    bl_label = 'Alle Regionen löschen'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = resolve_resolution_obj(context)
        if obj is None:
            self.report({'ERROR'}, 'Kein Resolution LOD.')
            return {'CANCELLED'}
        if hasattr(obj, 'cdm_geo_region_seeds'):
            obj.cdm_geo_region_seeds.clear()
        refresh_region_view(context, obj)
        self.report({'INFO'}, 'Stichpunkte gelöscht.')
        return {'FINISHED'}


class CDM_OT_geo_region_clear_kind(bpy.types.Operator):
    bl_idname = 'cdm.geo_region_clear_kind'
    bl_label = 'Aktive Region löschen'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = resolve_resolution_obj(context)
        if obj is None:
            self.report({'ERROR'}, 'Kein Resolution LOD.')
            return {'CANCELLED'}
        kind = context.scene.cdm_geo_region_kind
        n = remove_seeds_for_kind(obj, kind)
        refresh_region_view(context, obj)
        self.report({'INFO'}, '{}: {} entfernt'.format(region_label(kind), n))
        return {'FINISHED'}


class CDM_OT_building_region_geo(bpy.types.Operator):
    """Geo LOD aus markierten Region-Stichpunkten (C# RegionGuided)"""
    bl_idname = 'cdm.building_region_geo'
    bl_label = 'Geo aus Regionen generieren'
    bl_options = {'REGISTER', 'UNDO'}

    finalize: bpy.props.BoolProperty(name='Finalize', default=True)

    _timer = None
    _wait_frames = 4
    _running = False
    _finished = False

    def _stop_timer(self, context):
        wm = context.window_manager
        if self._timer is not None:
            try:
                wm.event_timer_remove(self._timer)
            except (TypeError, RuntimeError):
                pass
            self._timer = None

    def invoke(self, context, event):
        from ..addon_prefs import building_geo_lod_enabled
        if not building_geo_lod_enabled(context):
            self.report({'ERROR'}, 'In Preferences aktivieren: Gebäude Geo LOD (experimentell).')
            return {'CANCELLED'}

        if context.scene.cdm_engine_busy:
            self.report({'WARNING'}, 'C# GeoEngine läuft bereits.')
            return {'CANCELLED'}

        from .engine_status import begin
        begin(context, 'RegionGuided: starte C# Engine…')
        self._wait_frames = 4
        self._running = False
        self._finished = False
        wm = context.window_manager
        wm.modal_handler_add(self)
        self._timer = wm.event_timer_add(0.08, window=context.window)
        return {'RUNNING_MODAL'}

    def modal(self, context, event):
        from .engine_status import pulse, end, set_phase
        from .cs_engine_bridge import create_building_region_geo

        if event.type == 'ESC':
            self._stop_timer(context)
            end(context, 'Abgebrochen.', success=False)
            return {'CANCELLED'}

        if event.type == 'TIMER' and not self._finished:
            if not self._running:
                if self._wait_frames > 0:
                    pulse(context)
                    self._wait_frames -= 1
                    return {'RUNNING_MODAL'}
                self._running = True
                self._finished = True
                set_phase(context, 'RegionGuided Engine…', 0.2)
                ok = False
                err = None
                try:
                    result = create_building_region_geo(self, finalize=self.finalize)
                    ok = result is not None
                except Exception as exc:
                    err = str(exc)
                    ok = False
                self._stop_timer(context)
                if ok:
                    return {'FINISHED'}
                if context.scene.cdm_engine_busy:
                    end(context, 'Fehler.', success=False)
                if err:
                    self.report({'ERROR'}, err)
                return {'CANCELLED'}

        if self._running and not self._finished:
            pulse(context)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        from .cs_engine_bridge import create_building_region_geo
        result = create_building_region_geo(self, finalize=self.finalize)
        return {'FINISHED'} if result else {'CANCELLED'}


class CDM_PT_geo_regions(bpy.types.Panel):
    bl_label = 'Geo Regionen'
    bl_idname = 'CDM_PT_geo_regions'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'CDM Collider'
    bl_parent_id = 'CDM_PT_main_panel'
    bl_order = 1
    bl_options = {'DEFAULT_CLOSED'}

    @classmethod
    def poll(cls, context):
        from ..addon_prefs import building_geo_lod_enabled
        return building_geo_lod_enabled(context)

    def draw_header(self, context):
        obj = resolve_resolution_obj(context)
        n = len(seeds_for_object(obj)) if obj else 0
        self.layout.label(text=' ({})'.format(n) if n else '', icon='EYEDROPPER')

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        obj = resolve_resolution_obj(context)

        try:
            from .engine_status import draw_busy_banner
            draw_busy_banner(layout, scene)
        except Exception:
            pass

        diag = engine_diagnose()
        eng = layout.box()
        eng.scale_y = 0.82
        if diag['available']:
            eng.label(text='C# GeoEngine: bereit', icon='SCRIPT')
            if diag['dll_ok']:
                eng.label(text='CLI: DLL gebaut', icon='CHECKMARK')
            else:
                eng.label(text='CLI: dotnet run (Projekt)', icon='INFO')
        else:
            row = eng.row()
            row.alert = True
            row.label(text='C# GeoEngine: nicht gebaut', icon='ERROR')
            eng.label(text='dotnet build geo_engine_cs', icon='BLANK1')

        if diag['corpus_ok']:
            eng.label(
                text='Corpus: {} Modelle (optional, lokal)'.format(diag['corpus_count']),
                icon='ASSET_MANAGER',
            )
        else:
            eng.label(text='Corpus: nicht installiert (Marking ohne Zielzahl)', icon='INFO')

        if scene.cdm_engine_busy:
            busy = layout.box()
            busy.alert = True
            busy.label(text=scene.cdm_engine_status or 'Engine arbeitet…', icon='SORTTIME')
            if 'RegionGuided' in (scene.cdm_engine_status or ''):
                busy.label(text='Region-Guided Pipeline läuft', icon='HOME')
            elif 'Heuristik' in (scene.cdm_engine_status or ''):
                busy.label(text='Blind / Heuristik-Suche läuft', icon='VIEWZOOM')

        if getattr(scene, 'cdm_last_geo_pipeline', ''):
            last = layout.box()
            last.scale_y = 0.82
            pipe = scene.cdm_last_geo_pipeline
            last.label(text='Letzter Lauf: {}'.format(pipe), icon='TIME')
            if scene.cdm_auto_geo_report:
                sub = last.column(align=True)
                sub.scale_y = 0.78
                for line in scene.cdm_auto_geo_report.split('\n')[:3]:
                    sub.label(text=line, icon='BLANK1')

        layout.separator(factor=0.25)

        if obj:
            layout.label(text=obj.name, icon='MESH_DATA')
            n_seeds = len(seeds_for_object(obj))
            if n_seeds:
                layout.label(text='{} Stichpunkte auf diesem Mesh'.format(n_seeds), icon='PINNED')
        else:
            layout.label(text='Kein Resolution LOD', icon='ERROR')

        pick = layout.row()
        pick.scale_y = 1.65
        pick.enabled = obj is not None and not scene.cdm_engine_busy
        pick.operator('cdm.geo_region_pick', text='Overlay Picker', icon='EYEDROPPER')

        hint = layout.column(align=True)
        hint.scale_y = 0.72
        hint.label(text='↑↓ Kategorie  |  LMB Stichpunkt', icon='INFO')
        hint.label(text='Farben = Vorschau auf dem Mesh', icon='COLOR')

        viz = layout.row()
        viz.prop(scene, 'cdm_geo_region_show_mesh', text='Regionen auf Mesh', toggle=True, icon='HIDE_OFF')
        if obj and seeds_for_object(obj):
            layout.operator(
                'cdm.geo_region_refresh_viz',
                text='Anzeige aktualisieren',
                icon='FILE_REFRESH',
            )

        workflow = layout.box()
        wf = workflow.column(align=True)
        wf.scale_y = 0.78
        wf.label(text='Ablauf:', icon='SEQUENCE')
        wf.label(text='1. Overlay Picker — Stichpunkte setzen')
        wf.label(text='2. Geo generieren (Region-Guided)')
        wf.label(text='Blind generieren = ohne Markierung')

        layout.separator(factor=0.2)
        req = layout.box()
        req.scale_y = 0.88
        ok_min, msg = minimum_requirements_summary(obj)
        if ok_min:
            req.label(text=msg, icon='CHECKMARK')
        else:
            r = req.row()
            r.alert = True
            r.label(text=msg, icon='ERROR')
        for kind_id, lbl, fulfilled, count in essential_requirement_rows(obj):
            if kind_id == 'FLOOR' and seed_count_by_kind(obj).get('ROOF', 0) > 0:
                continue
            row = req.row(align=True)
            if not fulfilled and kind_id != 'FLOOR':
                row.alert = True
            row.label(
                text='{}  ({})'.format(lbl, count) if count else lbl,
                icon='CHECKMARK' if fulfilled else 'CANCEL',
            )

        layout.label(text='Alle Kategorien:', icon='PIVOT_CURSOR')
        labels = {k: lbl for k, lbl, _ in REGION_KIND_ITEMS}
        col = layout.column(align=True)
        for kind_id in REGION_ESSENTIAL_KINDS:
            _draw_region_row(col, scene, obj, kind_id, labels[kind_id], required=True)

        more = layout.row()
        more.prop(
            scene,
            'cdm_geo_region_show_optional',
            text='Weitere Regionen',
            icon='TRIA_DOWN' if scene.cdm_geo_region_show_optional else 'TRIA_RIGHT',
            emboss=False,
        )
        if scene.cdm_geo_region_show_optional:
            opt = layout.column(align=True)
            for kind_id in REGION_OPTIONAL_KINDS:
                _draw_region_row(opt, scene, obj, kind_id, labels[kind_id])
            note = layout.column(align=True)
            note.scale_y = 0.72
            note.label(text='Soffit = Dachuntersicht / Vordach', icon='BLANK1')
            note.label(text='(selten nötig — unter Weitere Regionen)', icon='BLANK1')

        layout.separator(factor=0.3)
        clr = layout.row(align=True)
        clr.scale_y = 1.1
        clr.operator('cdm.geo_region_clear_kind', text='Aktiv', icon='X')
        clr.operator('cdm.geo_region_clear', text='Alle', icon='TRASH')

        ready = has_minimum_seeds(obj)
        gen = layout.row()
        gen.scale_y = 1.8
        gen.enabled = ready and not scene.cdm_engine_busy
        gen.operator('cdm.building_region_geo', text='Geo generieren', icon='HOME')

        if not ready:
            row = layout.row()
            row.scale_y = 0.75
            row.alert = True
            row.label(text='Pflicht: Außenwand + Dach (oder Boden)', icon='ERROR')


class CDM_OT_geo_region_refresh_viz(bpy.types.Operator):
    bl_idname = 'cdm.geo_region_refresh_viz'
    bl_label = 'Region-Anzeige aktualisieren'
    bl_options = {'INTERNAL'}

    def execute(self, context):
        obj = resolve_resolution_obj(context)
        n = refresh_region_view(context, obj)
        self.report({'INFO'}, '{} Flächen eingefärbt'.format(n))
        return {'FINISHED'}


class CDM_OT_geo_region_set_kind(bpy.types.Operator):
    bl_idname = 'cdm.geo_region_set_kind'
    bl_label = 'Region wählen'
    bl_options = {'INTERNAL'}

    kind: bpy.props.EnumProperty(items=REGION_KIND_ITEMS)

    def execute(self, context):
        context.scene.cdm_geo_region_kind = self.kind
        return {'FINISHED'}


CLASSES = (
    CDM_OT_geo_region_pick,
    CDM_OT_geo_region_clear,
    CDM_OT_geo_region_clear_kind,
    CDM_OT_geo_region_refresh_viz,
    CDM_OT_geo_region_set_kind,
    CDM_OT_building_region_geo,
    CDM_PT_geo_regions,
)


def _on_show_mesh_update(_self, context):
    refresh_region_view(context)


def _deferred_region_refresh():
    try:
        ctx = bpy.context
        if ctx.scene:
            refresh_region_view(ctx)
    except Exception:
        pass
    return None


def register():
    from .geo_regions import CDM_GeoRegionSeed
    from .. import cdm_companions as companions

    cat = companions.resolve_category('CDM Collider', 'cdm_collider')
    companions.apply_n_panel_category(CLASSES, cat)

    for cls in (CDM_GeoRegionSeed,) + CLASSES:
        bpy.utils.register_class(cls)

    bpy.types.Object.cdm_geo_region_seeds = bpy.props.CollectionProperty(type=CDM_GeoRegionSeed)
    bpy.types.Scene.cdm_geo_region_kind = bpy.props.EnumProperty(
        name='Aktive Geo-Region',
        items=REGION_KIND_ITEMS,
        default='WALL_OUTER',
    )
    bpy.types.Scene.cdm_geo_region_show_optional = bpy.props.BoolProperty(
        name='Weitere Regionen',
        description='Innenwände, Boden, Giebel, Sockel, Soffit',
        default=False,
    )
    bpy.types.Scene.cdm_geo_picker_overlay_x = bpy.props.IntProperty(
        name='Picker Overlay X',
        default=40,
        min=0,
        soft_max=4000,
    )
    bpy.types.Scene.cdm_geo_picker_overlay_y = bpy.props.IntProperty(
        name='Picker Overlay Y',
        default=140,
        min=0,
        soft_max=4000,
    )
    bpy.types.Scene.cdm_geo_region_show_mesh = bpy.props.BoolProperty(
        name='Regionen auf Mesh',
        description='Farbige Flächen-Vorschau der markierten Geo-Regionen im Viewport',
        default=True,
        update=_on_show_mesh_update,
    )
    register_region_viz_handler()
    bpy.app.timers.register(_deferred_region_refresh, first_interval=0.5)


def unregister():
    from .geo_regions import CDM_GeoRegionSeed

    if bpy.app.timers.is_registered(_deferred_region_refresh):
        bpy.app.timers.unregister(_deferred_region_refresh)

    global _ACTIVE_PICKER
    _ACTIVE_PICKER = None
    _unregister_draw_handler()
    unregister_region_viz_handler()

    if hasattr(bpy.types.Object, 'cdm_geo_region_seeds'):
        del bpy.types.Object.cdm_geo_region_seeds
    for attr in (
        'cdm_geo_region_kind',
        'cdm_geo_region_show_optional',
        'cdm_geo_picker_overlay_x',
        'cdm_geo_picker_overlay_y',
        'cdm_geo_region_show_mesh',
    ):
        if hasattr(bpy.types.Scene, attr):
            delattr(bpy.types.Scene, attr)

    for cls in reversed(CLASSES + (CDM_GeoRegionSeed,)):
        bpy.utils.unregister_class(cls)
