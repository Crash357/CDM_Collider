"""
CDM Collider — Operators and N-Panel.
Standalone product in the split CDM set (Architect / Collider / P3D Studio).
"""

import os

import bpy
from bpy_extras.io_utils import ExportHelper
from bpy.props import StringProperty

from . import geometry as geo

METHOD_ITEMS = [
    ('WALL',  "Wall — Building (Walls + Floors)",
     "One box per wall/floor/ceiling. Like manually created DayZ-Geo: thick connected rectangles. Recommended for buildings."),
    ('SPLIT', "Split — Minimal Components",
     "Building split: few OBB components with 1 mm seam offset. Best for houses with minimal component count."),
    ('SHELL', "Shell — Closed + Wall Boxes  [experimental]",
     "Phase 1: closed islands (mesh). Phase 2: axis-aligned wall grid boxes (8 verts). "
     "One active building only."),
    ('BBOX',  "BBox — Single Box (whole object)",
     "A single bounding box around the entire object. Ideal for simple cubes/containers."),
    ('OBB',   "OBB — Fine-Cluster",
     "Oriented box per face cluster (BFS). For complex geometry with stairs."),
    ('HULL',  "Hull — Organic (Rocks)",
     "Raw convex hull. For rocks, vehicles, irregular objects."),
    ('VHACD', "V-HACD — Convex Decomposition",
     "Hierarchical Approximate Convex Decomposition (V-HACD 4.x). "
     "Best for vehicles, rocks, props, organic shapes. "
     "Requires TestVHACD.exe — set path in Addon Preferences."),
    ('COACD', "CoACD — Collision-Aware Decomposition",
     "Collision-Aware Approximate Convex Decomposition (SIGGRAPH 2022). "
     "Fewer, better-fitting hulls than V-HACD. "
     "Uses the CoACD Python package in Blender's Python environment."),
]

VHACD_FILL_ITEMS = [
    ('flood',   "Flood Fill",   "Standard flood-fill (solid objects)"),
    ('raycast', "Raycast",      "Better for objects with holes/openings"),
    ('surface', "Surface Only", "Surface-only fill (thin shells)"),
]

_COACD_DAYZ_PRESET = {
    'threshold':   0.15,
    'max_hulls':   32,
    'preprocess':  'auto',
    'prep_res':    50,
    'mcts_iter':   100,
    'max_ch_vertex': 64,
}


def _coacd_dayz_preset_update(self, context):
    """Wenn DayZ-Preset aktiviert wird, alle CoACD-Parameter auf DayZ-Empfehlungen setzen."""
    if self.cdm_coacd_dayz_preset:
        self.cdm_coacd_threshold   = _COACD_DAYZ_PRESET['threshold']
        self.cdm_coacd_max_hulls   = _COACD_DAYZ_PRESET['max_hulls']
        self.cdm_coacd_preprocess  = _COACD_DAYZ_PRESET['preprocess']
        self.cdm_coacd_prep_res    = _COACD_DAYZ_PRESET['prep_res']
        self.cdm_coacd_mcts_iter   = _COACD_DAYZ_PRESET['mcts_iter']
        self.cdm_coacd_max_ch_vertex = _COACD_DAYZ_PRESET['max_ch_vertex']


def _coacd_params_from_scene(scene):
    """CoACD-Werte aus Scene — bei DayZ-Preset immer die festen Empfehlungswerte."""
    if scene.cdm_coacd_dayz_preset:
        return dict(_COACD_DAYZ_PRESET)
    max_hulls = scene.cdm_coacd_max_hulls
    if max_hulls <= 0:
        max_hulls = 32
    return {
        'threshold': scene.cdm_coacd_threshold,
        'max_hulls': max_hulls,
        'preprocess': scene.cdm_coacd_preprocess,
        'prep_res': scene.cdm_coacd_prep_res,
        'mcts_iter': scene.cdm_coacd_mcts_iter,
        'max_ch_vertex': scene.cdm_coacd_max_ch_vertex,
    }


def _run_auto_geo_method(scene, operator):
    """Gemeinsame Logik für Auto Geo LOD und Decompose (N-Panel-Parameter)."""
    if getattr(scene, 'cdm_engine_busy', False):
        operator.report({'WARNING'}, "C# GeoEngine läuft bereits.")
        return None

    method = scene.cdm_method
    is_decompose = operator.bl_idname == 'cdm.decompose'

    if method == 'VHACD':
        result = geo.create_geometry_vhacd(
            operator,
            max_hulls=scene.cdm_vhacd_max_hulls,
            resolution=scene.cdm_vhacd_resolution,
            max_verts_per_hull=scene.cdm_vhacd_max_verts,
            fill_mode=scene.cdm_vhacd_fill_mode,
            error_percent=scene.cdm_vhacd_error_pct,
        )
    elif method == 'COACD':
        cp = _coacd_params_from_scene(scene)
        result = geo.create_geometry_coacd(
            operator,
            threshold=cp['threshold'],
            max_hulls=cp['max_hulls'],
            preprocess_mode=cp['preprocess'],
            prep_resolution=cp['prep_res'],
            mcts_iterations=cp['mcts_iter'],
            max_ch_vertex=cp['max_ch_vertex'],
        )
    elif is_decompose:
        result = geo.create_geometry_decompose(
            operator, method=method,
            min_area=scene.cdm_min_area,
            angle_threshold=scene.cdm_angle_threshold)
    else:
        result = geo.create_geometry_auto_building(
            operator, method=method,
            min_area=scene.cdm_min_area,
            angle_threshold=scene.cdm_angle_threshold)

    if result and not is_decompose and method in ('VHACD', 'COACD'):
        merged = geo.create_geometry_merge_for_method(operator, method)
        if merged:
            result = merged
        elif bpy.data.collections.get('GEO_Components'):
            operator.report({'WARNING'},
                            "Merge fehlgeschlagen — GEO_Components manuell mergen.")
            result = bpy.data.collections['GEO_Components']
        else:
            result = None
    return result


# ---------------------------------------------------------------------------
# Main operator: Auto Building (one-click, face-cluster decomposition)
# ---------------------------------------------------------------------------

class CDM_OT_auto_building(bpy.types.Operator):
    """Generate collision components from the active mesh using N-Panel settings.
    All parameters come from the CDM sidebar — not from the viewport redo panel."""
    bl_idname  = "cdm.auto_building"
    bl_label   = "Generate Geometry LOD"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        result = _run_auto_geo_method(context.scene, self)
        return {'FINISHED'} if result else {'CANCELLED'}


# ---------------------------------------------------------------------------
# Direct operator: use the mesh exactly as-is (creates new Geometry object)
# ---------------------------------------------------------------------------

class CDM_OT_direct(bpy.types.Operator):
    """Closed islands -> individual Component objects in 'GEO_Components'.
    Open islands are skipped.
    Run 'Merge (Direct) -> Geometry LOD' afterwards.
    SubD and other modifiers are evaluated."""
    bl_idname  = "cdm.direct"
    bl_label   = "Proxy \u2192 Geo LOD (Direct)"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = geo.create_geometry_direct(self)
        return {'FINISHED'} if obj else {'CANCELLED'}


# ---------------------------------------------------------------------------
# Tag operator: add ComponentXX groups + DayZ props to existing object IN-PLACE
# ---------------------------------------------------------------------------

class CDM_OT_tag_geo_lod(bpy.types.Operator):
    """Sets ComponentXX vertex groups and DayZ LOD properties on the
selected object — without modifying the mesh.
Ideal when the geo mesh is already correct and just needs to be tagged properly."""
    bl_idname  = "cdm.tag_geo_lod"
    bl_label   = "Tag as Geo LOD (In-Place)"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        ok = geo.tag_as_geometry_lod(self)
        return {'FINISHED'} if ok else {'CANCELLED'}


# ---------------------------------------------------------------------------
# Fix-Operator: offene Islands schliessen (Fill Holes)
# ---------------------------------------------------------------------------

class CDM_OT_fix_open_meshes(bpy.types.Operator):
    """Closes all open boundary edges (Boundary-Loops) in the active mesh
    using bmesh.ops.holes_fill — equivalent to Edit Mode → Mesh → Fill → Fill Holes.
    Afterwards all components can be read correctly by DayZ export tools."""
    bl_idname  = "cdm.fix_open_meshes"
    bl_label   = "Close Open Meshes (Fill Holes)"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        import bmesh
        obj = context.active_object
        if obj is None or obj.type != 'MESH':
            self.report({'ERROR'}, "No active mesh object selected.")
            return {'CANCELLED'}

        was_edit = (obj.mode == 'EDIT')
        if was_edit:
            bpy.ops.object.mode_set(mode='OBJECT')

        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()

        # Boundary-Edges (nur 1 Face) sammeln
        open_edges = [e for e in bm.edges if len(e.link_faces) < 2]
        if not open_edges:
            bm.free()
            if was_edit:
                bpy.ops.object.mode_set(mode='EDIT')
            self.report({'INFO'}, "All islands already closed — nothing to do.")
            return {'FINISHED'}

        result = bmesh.ops.holes_fill(bm, edges=open_edges, sides=0)
        filled = len(result.get('faces', []))

        bm.to_mesh(obj.data)
        bm.free()
        obj.data.update()

        if was_edit:
            bpy.ops.object.mode_set(mode='EDIT')

        self.report({'INFO'}, f"Fill Holes: {filled} face(s) added — mesh is now closed.")
        return {'FINISHED'}


class CDM_OT_select_open_islands(bpy.types.Operator):
    """Selects only non-watertight islands in the active mesh.
    All closed islands are deselected.
    Automatically switches to Edit Mode (Face Select)."""
    bl_idname  = "cdm.select_open_islands"
    bl_label   = "Select Open Islands"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        ok = geo.select_open_islands(self)
        return {'FINISHED'} if ok else {'CANCELLED'}


class CDM_OT_merge_exact(bpy.types.Operator):
    """Exact mesh merge — for buildings, boxes, closed islands (NO convex hull)."""
    bl_idname  = "cdm.merge_exact"
    bl_label   = "Merge (Exact) \u2192 Geometry LOD"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = geo.create_geometry_merge_exact(self)
        return {'FINISHED'} if obj else {'CANCELLED'}


class CDM_OT_decompose(bpy.types.Operator):
    """Step 1 — create Component01, Component02… in GEO_Components.
    Same N-Panel settings as Auto Geo LOD; inspect before merging."""
    bl_idname  = "cdm.decompose"
    bl_label   = "1. Decompose to Components"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        result = _run_auto_geo_method(context.scene, self)
        return {'FINISHED'} if result else {'CANCELLED'}


class CDM_OT_decompose_building_phase1(bpy.types.Operator):
    """Phase 1 — geschlossene Islands als 8V-AABB (Dach, Schornstein …)."""
    bl_idname  = "cdm.decompose_building_phase1"
    bl_label   = "Building Phase 1 — Closed Islands"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        scene = context.scene
        result = geo.create_geometry_decompose_building_phase1(
            self,
            min_area=scene.cdm_min_area,
            angle_threshold=scene.cdm_angle_threshold,
        )
        return {'FINISHED'} if result else {'CANCELLED'}


class CDM_OT_export_geometry_lod_dump(bpy.types.Operator, ExportHelper):
    """Fertiges Geometry LOD als Zahlen exportieren (aktiv oder 'Geometry')."""
    bl_idname = "cdm.export_geometry_lod_dump"
    bl_label = "Geometry LOD Dump"
    bl_options = {'REGISTER'}

    filename_ext = ".txt"
    filter_glob: StringProperty(default="*.txt", options={'HIDDEN'})

    def invoke(self, context, event):
        geo_obj = geo.resolve_geometry_lod(context)
        name = (geo_obj.name if geo_obj else "Geometry") + "_geometry_lod"
        if bpy.data.is_saved and bpy.data.filepath:
            folder = os.path.dirname(bpy.path.abspath(bpy.data.filepath))
            self.filepath = os.path.join(folder, name + self.filename_ext)
        else:
            self.filepath = name + self.filename_ext
        return ExportHelper.invoke(self, context, event)

    def execute(self, context):
        geo_obj = geo.resolve_geometry_lod(context)
        if not geo_obj:
            self.report({'ERROR'},
                        "Geometry LOD wählen (Objekt 'Geometry' oder Component-Groups).")
            return {'CANCELLED'}

        filepath = bpy.path.abspath(self.filepath)
        ok, result = geo.export_geometry_lod_dump_to_file(geo_obj, filepath)
        if not ok:
            self.report({'ERROR'}, f"Speichern fehlgeschlagen: {result}")
            return {'CANCELLED'}

        self.report({'INFO'},
                    f"Geometry LOD: {os.path.basename(filepath)}  "
                    f"({result['components']}C, {result['verts']}V, {result['faces']}F)")
        return {'FINISHED'}


class CDM_OT_export_compare_dumps(bpy.types.Operator, ExportHelper):
    """4 Textdateien: Gebäude, Phase1, Phase2, fertiges Geometry LOD."""
    bl_idname = "cdm.export_compare_dumps"
    bl_label = "4 Dumps exportieren (Vergleich)"
    bl_options = {'REGISTER'}

    filename_ext = ".txt"
    filter_glob: StringProperty(default="*.txt", options={'HIDDEN'})

    def invoke(self, context, event):
        building = geo.resolve_source_building(context)
        name = (building.name if building else "vergleich") + "_vergleich"
        if bpy.data.is_saved and bpy.data.filepath:
            folder = os.path.dirname(bpy.path.abspath(bpy.data.filepath))
            self.filepath = os.path.join(folder, name + self.filename_ext)
        else:
            self.filepath = name + self.filename_ext
        return ExportHelper.invoke(self, context, event)

    def execute(self, context):
        building = geo.resolve_source_building(context)
        geometry = geo.resolve_geometry_lod(context)

        if not building and not geometry:
            self.report({'ERROR'},
                        "Weder Gebäude noch Geometry LOD gefunden. "
                        "Visual-Mesh oder Geometry wählen / Target Object setzen.")
            return {'CANCELLED'}

        phase1_count = context.scene.cdm_building_phase1_count
        ok, err, result = geo.export_compare_dumps(
            building, self.filepath, phase1_count=phase1_count,
            geometry_lod_obj=geometry)
        if not ok:
            self.report({'ERROR'}, f"Export fehlgeschlagen: {err}")
            return {'CANCELLED'}

        s0 = result['gebaeude']
        s1 = result['phase1']
        s2 = result['phase2']
        s3 = result['geometry_lod']
        names = [os.path.basename(p) for p in result['paths']]
        self.report({'INFO'},
                    f"4 Dumps gespeichert  |  Gebäude {s0['verts']}V  "
                    f"P1 {s1['components']}C  P2 {s2['components']}C  "
                    f"GeoLOD {s3['components']}C")
        return {'FINISHED'}


class CDM_OT_decompose_building_phase2(bpy.types.Operator):
    """Phase 2 — Wand-Boxen aus Innen/Außen-Paaren (8V AABB, jedes Gebäude)."""
    bl_idname  = "cdm.decompose_building_phase2"
    bl_label   = "Building Phase 2 — Wall Pairs"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        scene = context.scene
        result = geo.create_geometry_decompose_building_phase2(
            self,
            min_area=scene.cdm_min_area,
            angle_threshold=scene.cdm_angle_threshold,
        )
        return {'FINISHED'} if result else {'CANCELLED'}


class CDM_OT_building_angle_split(bpy.types.Operator):
    """OBB-Pipeline Phase 1 — Islands nach Flächenwinkel trennen (Preview)."""
    bl_idname  = "cdm.building_angle_split"
    bl_label   = "OBB Phase 1 — Angle Split"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        scene = context.scene
        result = geo.create_building_angle_split(
            self,
            min_area=scene.cdm_min_area,
            angle_threshold=scene.cdm_angle_threshold,
        )
        return {'FINISHED'} if result else {'CANCELLED'}


class CDM_OT_building_obb_boxes(bpy.types.Operator):
    """OBB-Pipeline Phase 2 — pro Patch eine orientierte 8V-Box."""
    bl_idname  = "cdm.building_obb_boxes"
    bl_label   = "OBB Phase 2 — Generate Boxes"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        scene = context.scene
        result = geo.create_building_obb_boxes(
            self,
            min_area=scene.cdm_min_area,
            angle_threshold=scene.cdm_angle_threshold,
        )
        return {'FINISHED'} if result else {'CANCELLED'}


class CDM_OT_building_cs_generate(bpy.types.Operator):
    """Resolution LOD → C# GeoEngine → GEO_Components (Angle-Split + OBB)."""
    bl_idname  = "cdm.building_cs_generate"
    bl_label   = "C# Generate Components"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return not context.scene.cdm_engine_busy

    def execute(self, context):
        if context.scene.cdm_engine_busy:
            self.report({'WARNING'}, "C# GeoEngine läuft bereits.")
            return {'CANCELLED'}
        from .geo.engine_status import begin, end
        scene = context.scene
        begin(context, "C# GeoEngine: Components generieren…")
        try:
            result = geo.create_building_geometry_cs(
                self,
                min_area=scene.cdm_min_area,
                angle_threshold=scene.cdm_angle_threshold,
            )
            end(context, "Fertig." if result else "Fehler.", success=bool(result))
            return {'FINISHED'} if result else {'CANCELLED'}
        except Exception as exc:
            end(context, "Fehler.", success=False)
            self.report({'ERROR'}, str(exc))
            return {'CANCELLED'}


class CDM_OT_building_auto_geo(bpy.types.Operator):
    """Blind: Geo LOD — 1 mm Skin über koplanare Flächen (FaceSkin)."""
    bl_idname  = "cdm.building_auto_geo"
    bl_label   = "Blind generieren"
    bl_options = {'REGISTER', 'UNDO'}

    def _stop_timer(self, context):
        wm = context.window_manager
        if self._timer is not None:
            try:
                wm.event_timer_remove(self._timer)
            except (TypeError, RuntimeError):
                pass
            self._timer = None

    def invoke(self, context, event):
        from .addon_prefs import building_geo_lod_enabled
        if not building_geo_lod_enabled(context):
            self.report({'ERROR'}, 'In Preferences aktivieren: Gebäude Geo LOD (Beta-Test).')
            return {'CANCELLED'}

        if context.scene.cdm_engine_busy:
            self.report({'WARNING'}, "C# GeoEngine läuft bereits.")
            return {'CANCELLED'}

        from .geo.engine_status import begin
        begin(context, "Starte FaceSkin (1 mm)…")
        self._timer = None
        self._wait_frames = 4
        self._running = False
        self._finished = False
        wm = context.window_manager
        wm.modal_handler_add(self)
        self._timer = wm.event_timer_add(0.08, window=context.window)
        return {'RUNNING_MODAL'}

    def modal(self, context, event):
        from .geo.engine_status import pulse, end, set_phase

        if event.type in {'ESC'}:
            self._stop_timer(context)
            end(context, "Abgebrochen.", success=False)
            return {'CANCELLED'}

        if event.type == 'TIMER' and not self._finished:
            if not self._running:
                if self._wait_frames > 0:
                    pulse(context)
                    self._wait_frames -= 1
                    return {'RUNNING_MODAL'}
                self._running = True
                self._finished = True
                set_phase(context, "FaceSkin: 1 mm über Flächen…", 0.2)
                ok = False
                err = None
                try:
                    geo.create_building_auto_geo(self, finalize=True)
                    geo_obj = bpy.data.objects.get("Geometry")
                    col = bpy.data.collections.get('GEO_Components')
                    has_comps = col and any(o.type == 'MESH' for o in col.objects)
                    ok = geo_obj is not None
                except Exception as exc:
                    err = str(exc)
                    ok = False
                    col = bpy.data.collections.get('GEO_Components')
                    has_comps = col and any(o.type == 'MESH' for o in col.objects)
                self._stop_timer(context)
                if ok:
                    end(context, "Fertig.", success=True)
                    return {'FINISHED'}
                if has_comps:
                    end(context, "Components erzeugt — Finalize manuell.", success=False)
                    self.report({'WARNING'},
                                "Finalize fehlgeschlagen — GEO_Components prüfen, Schritt 3.")
                    return {'FINISHED'}
                end(context, "Fehler.", success=False)
                if err:
                    self.report({'ERROR'}, err)
                return {'CANCELLED'}

        if self._running and not self._finished:
            pulse(context)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        from .geo.engine_status import begin, end
        begin(context, "C# GeoEngine: Generierung läuft…")
        try:
            geo.create_building_auto_geo(self, finalize=True)
            geo_obj = bpy.data.objects.get("Geometry")
            col = bpy.data.collections.get('GEO_Components')
            has_comps = col and any(o.type == 'MESH' for o in col.objects)
            ok = geo_obj is not None
            if ok:
                end(context, "Fertig.", success=True)
                return {'FINISHED'}
            if has_comps:
                end(context, "Components erzeugt — Finalize manuell.", success=False)
                self.report({'WARNING'},
                            "Finalize fehlgeschlagen — GEO_Components prüfen, Schritt 3.")
                return {'FINISHED'}
            end(context, "Fehler.", success=False)
            return {'CANCELLED'}
        except Exception as exc:
            end(context, "Fehler.", success=False)
            self.report({'ERROR'}, str(exc))
            return {'CANCELLED'}


class CDM_OT_building_finalize(bpy.types.Operator):
    """OBB-Pipeline Phase 3 — Join, Intersect, Cleanup → Geometry LOD."""
    bl_idname  = "cdm.building_finalize"
    bl_label   = "OBB Phase 3 — Finalize Geometry"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return not context.scene.cdm_engine_busy

    def execute(self, context):
        if context.scene.cdm_engine_busy:
            self.report({'WARNING'}, "C# GeoEngine läuft bereits.")
            return {'CANCELLED'}
        result = geo.create_building_finalize(self)
        return {'FINISHED'} if result else {'CANCELLED'}


class CDM_OT_merge_geo_lod(bpy.types.Operator):
    """Merge GEO_Components using the merge type that matches the current Method."""
    bl_idname  = "cdm.merge_geo_lod"
    bl_label   = "Merge \u2192 Geometry LOD"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = geo.create_geometry_merge_for_method(
            self, context.scene.cdm_method)
        return {'FINISHED'} if obj else {'CANCELLED'}


class CDM_OT_merge_components(bpy.types.Operator):
    """
    Merge GEO_Components via convex hull per component.
    For HULL / V-HACD / CoACD workflows (rocks, organic shapes).
    """
    bl_idname  = "cdm.merge_components"
    bl_label   = "Merge (Hull) \u2192 Geometry LOD"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = geo.create_geometry_merge_components(self)
        return {'FINISHED'} if obj else {'CANCELLED'}


# ---------------------------------------------------------------------------
# Secondary operators
# ---------------------------------------------------------------------------

class CDM_OT_bbox(bpy.types.Operator):
    """One bounding-box component per selected object (simple props / vehicles)"""
    bl_idname  = "cdm.bbox"
    bl_label   = "BBox (1 Box / Object)"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = geo.create_geometry_bbox(self)
        return {'FINISHED'} if obj else {'CANCELLED'}


class CDM_OT_add_geo_from_selection(bpy.types.Operator):
    """Axis-aligned box from selected faces → GEO_Components."""
    bl_idname  = "cdm.add_geo_from_selection"
    bl_label   = "Faces → AABB Component"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = geo.create_geometry_from_faces(self)
        return {'FINISHED'} if obj else {'CANCELLED'}


class CDM_OT_from_selection(bpy.types.Operator):
    """Convex hull from selected vertices → GEO_Components."""
    bl_idname  = "cdm.from_selection"
    bl_label   = "Vertices → Hull Component"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        obj = geo.create_geometry_from_selection(self)
        return {'FINISHED'} if obj else {'CANCELLED'}


class CDM_OT_from_vertex_groups(bpy.types.Operator):
    """OBB box per vertex group (e.g. Wall, Door1, Roof) → GEO_Components.
    Then run Merge → Geometry LOD."""
    bl_idname  = "cdm.from_vertex_groups"
    bl_label   = "Vertex Groups → OBB"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        col = geo.create_geometry_from_vertex_groups(self)
        return {'FINISHED'} if col else {'CANCELLED'}


class CDM_OT_hull_from_vertex_groups(bpy.types.Operator):
    """Convex hull per vertex group (e.g. Wall, Door1, Roof) → GEO_Components.
    Ideal for organic/irregular shapes. Then run Merge → Geometry LOD."""
    bl_idname  = "cdm.hull_from_vertex_groups"
    bl_label   = "Vertex Groups → Hull"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        col = geo.create_geometry_hull_from_vertex_groups(self)
        return {'FINISHED'} if col else {'CANCELLED'}


class CDM_OT_recolor_geo(bpy.types.Operator):
    """Re-applies the chosen viewport colors to all existing
    GEO_Components and the Geometry LOD object.
    Useful after loading a .blend file or after changing colors
    on already created objects."""
    bl_idname  = "cdm.recolor_geo"
    bl_label   = "Apply Colors"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        count = 0
        col = bpy.data.collections.get("GEO_Components")
        if col:
            for obj in col.objects:
                geo._apply_geo_display(obj, is_component=True)
                count += 1
        geo_obj = bpy.data.objects.get("Geometry")
        if geo_obj:
            geo._apply_geo_display(geo_obj, is_component=False)
            count += 1

        if count == 0:
            self.report({'WARNING'}, "No GEO_Components or Geometry objects found.")
        else:
            self.report({'INFO'}, f"Colors applied to {count} object(s).")
        return {'FINISHED'}


class CDM_OT_reset_geo_display(bpy.types.Operator):
    """Resets the viewport display of all geo objects:
    No colored solid, no wireframe overlay, no 'In Front'.
    Objects will look like normal Blender objects again."""
    bl_idname  = "cdm.reset_geo_display"
    bl_label   = "Reset Colors"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        count = 0
        all_objs = []
        col = bpy.data.collections.get("GEO_Components")
        if col:
            all_objs.extend(col.objects)
        geo_obj = bpy.data.objects.get("Geometry")
        if geo_obj:
            all_objs.append(geo_obj)

        for obj in all_objs:
            obj.color         = (1.0, 1.0, 1.0, 1.0)
            obj.display_type  = 'SOLID'
            obj.show_wire     = False
            obj.show_in_front = False
            obj.visible_shadow = True
            if hasattr(obj, 'show_transparent'):
                obj.show_transparent = False
            # remove cdm display materials
            cdm_mats = {'cdm_geo_mat', 'cdm_comp_mat'}
            if any(s.material and s.material.name in cdm_mats
                   for s in obj.material_slots):
                obj.data.materials.clear()
            count += 1

        if (context.area and context.area.type == 'VIEW_3D'
                and context.space_data):
            context.area.tag_redraw()

        if count == 0:
            self.report({'WARNING'}, "No geo objects found.")
        else:
            self.report({'INFO'}, f"Display reset for {count} object(s).")
        return {'FINISHED'}


# ---------------------------------------------------------------------------
# DayZ Conformity Check
# ---------------------------------------------------------------------------

class CDM_OT_dayz_check(bpy.types.Operator):
    """Check the merged Geometry object for DayZ limits:
    max. 32 hulls (mesh islands), max. 255 vertices per hull."""
    bl_idname  = "cdm.dayz_check"
    bl_label   = "DayZ Check"
    bl_options = {'REGISTER'}

    def execute(self, context):
        import bmesh as _bmesh

        geo_obj = geo.resolve_geometry_lod(context)
        if geo_obj is None:
            self.report({'ERROR'}, "Kein Geometry LOD gefunden — zuerst finalisieren.")
            return {'CANCELLED'}
        if geo_obj.type != 'MESH':
            self.report({'ERROR'}, "'Geometry' is not a mesh object.")
            return {'CANCELLED'}

        bm = _bmesh.new()
        bm.from_mesh(geo_obj.data)
        bm.verts.ensure_lookup_table()

        from .dayz_lod_compat import component_group_sets
        parts = component_group_sets(geo_obj)
        if parts:
            islands = [len(members) for _name, members in parts]
        else:
            visited = set()
            islands = []
            for v in bm.verts:
                if v.index in visited:
                    continue
                island = set()
                queue = [v]
                while queue:
                    cur = queue.pop()
                    if cur.index in visited:
                        continue
                    visited.add(cur.index)
                    island.add(cur.index)
                    for edge in cur.link_edges:
                        ov = edge.other_vert(cur)
                        if ov.index not in visited:
                            queue.append(ov)
                islands.append(len(island))
        bm.free()

        num_hulls  = len(islands)
        if num_hulls == 0:
            self.report({'ERROR'},
                        "Keine Components — Geometry LOD leer oder nicht finalisiert.")
            return {'CANCELLED'}

        over_verts = [n for n in islands if n > 255]
        max_verts  = max(islands) if islands else 0

        # Konsolen-Ausgabe mit Details
        print(f"\n[CDM DayZ Check] Object: {geo_obj.name}")
        print(f"  Components: {num_hulls}  {'✓' if num_hulls <= 32 else '⚠ > 32!'}")
        for i, n in enumerate(sorted(islands, reverse=True), 1):
            marker = "  ⚠ > 255 Verts!" if n > 255 else ""
            print(f"  Component {i:02d}: {n:3d} Verts{marker}")
        print()

        problems = []
        if num_hulls > 32:
            problems.append(f"{num_hulls} Components (Limit: 32)")
        if over_verts:
            problems.append(f"{len(over_verts)} Component(s) > 255 Verts")

        if not problems:
            self.report({'INFO'},
                f"\u2713 DayZ-OK \u2014 {num_hulls} Components, max. {max_verts} Verts/Component")
        else:
            self.report({'WARNING'},
                f"\u26a0 DayZ-PROBLEM \u2014 " + "  |  ".join(problems)
                + f"  (max. {max_verts} Verts/Component)")
        return {'FINISHED'}


# ---------------------------------------------------------------------------
# N-Panel
# ---------------------------------------------------------------------------

class CDM_PT_main_panel(bpy.types.Panel):
    bl_label       = "CDM Collider"
    bl_idname      = "CDM_PT_main_panel"
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = "CDM Collider"
    bl_options     = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        scene  = context.scene

        try:
            from .geo.engine_status import draw_busy_banner
            draw_busy_banner(layout, scene)
        except Exception:
            pass

        row = layout.row(align=True)
        row.label(text="Ziel:", icon='OBJECT_DATA')
        row.prop(scene, "cdm_target_object", text="")

# ---------------------------------------------------------------------------
# Registration
# ---------------------------------------------------------------------------

CLASSES = [
    CDM_OT_direct,
    CDM_OT_merge_exact,
    CDM_OT_merge_geo_lod,
    CDM_OT_merge_components,
    CDM_OT_tag_geo_lod,
    CDM_OT_fix_open_meshes,
    CDM_OT_select_open_islands,
    CDM_OT_recolor_geo,
    CDM_OT_reset_geo_display,
    CDM_OT_dayz_check,
    CDM_OT_auto_building,
    CDM_OT_decompose,
    CDM_OT_decompose_building_phase1,
    CDM_OT_decompose_building_phase2,
    CDM_OT_building_angle_split,
    CDM_OT_building_obb_boxes,
    CDM_OT_building_cs_generate,
    CDM_OT_building_auto_geo,
    CDM_OT_building_finalize,
    CDM_OT_export_compare_dumps,
    CDM_OT_export_geometry_lod_dump,
    CDM_OT_bbox,
    CDM_OT_add_geo_from_selection,
    CDM_OT_from_selection,
    CDM_OT_from_vertex_groups,
    CDM_OT_hull_from_vertex_groups,
    CDM_PT_main_panel,
]


def _auto_recolor(self, context):
    """Update callback: immediately apply color to all geo objects when the picker changes."""
    col = bpy.data.collections.get("GEO_Components")
    if col:
        for obj in col.objects:
            geo._apply_geo_display(obj, is_component=True)
    geo_obj = bpy.data.objects.get("Geometry")
    if geo_obj:
        geo._apply_geo_display(geo_obj, is_component=False)


def _register_scene_props():
    bpy.types.Scene.cdm_target_object = bpy.props.PointerProperty(
        type=bpy.types.Object,
        name="CDM Target Object",
        description="Optional: object to use when nothing is selected")
    bpy.types.Scene.cdm_method = bpy.props.EnumProperty(
        name="Method", items=METHOD_ITEMS, default='OBB',
        description="How each face cluster is turned into a collision volume")
    bpy.types.Scene.cdm_min_area = bpy.props.FloatProperty(
        name="Min Area", default=0.25, min=0.0, soft_max=5.0,
        description="Ignore wall patches smaller than this area (m\u00b2). "
                    "Phase 2: 0.25 Standard, 0.5 = weniger Boxen.")
    bpy.types.Scene.cdm_angle_threshold = bpy.props.FloatProperty(
        name="Angle", default=30.0, min=5.0, max=90.0,
        description="Max angle (\u00b0) between adjacent faces to stay in the same cluster. "
                    "30\u00b0 = sharp splits (buildings). 45\u00b0 = soft. 15\u00b0 = very sharp.")
    bpy.types.Scene.cdm_building_phase1_count = bpy.props.IntProperty(
        name="Phase 1 Component Count",
        default=0, min=0,
        options={'HIDDEN'},
        description="Anzahl Components nach Phase 1 (f\u00fcr Dump-Trennung)")
    bpy.types.Scene.cdm_auto_geo_model_id = bpy.props.StringProperty(
        name="Auto Geo Model ID",
        default="",
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_auto_geo_score = bpy.props.FloatProperty(
        name="Auto Geo Score",
        default=0.0, min=0.0, max=1.0,
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_auto_geo_passed = bpy.props.BoolProperty(
        name="Auto Geo Passed",
        default=False,
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_auto_geo_report = bpy.props.StringProperty(
        name="Auto Geo Report",
        default="",
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_auto_geo_obb_score = bpy.props.FloatProperty(
        name="Auto Geo OBB Score",
        default=0.0, min=0.0, max=1.0,
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_auto_geo_coverage_score = bpy.props.FloatProperty(
        name="Auto Geo Coverage Score",
        default=0.0, min=0.0, max=1.0,
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_engine_busy = bpy.props.BoolProperty(
        name="Engine Busy",
        default=False,
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_engine_status = bpy.props.StringProperty(
        name="Engine Status",
        default="",
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_engine_progress = bpy.props.FloatProperty(
        name="Engine Progress",
        default=0.0, min=0.0, max=1.0,
        subtype='FACTOR',
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_last_geo_pipeline = bpy.props.StringProperty(
        name="Last Geo Pipeline",
        default="",
        options={'HIDDEN'},
    )
    bpy.types.Scene.cdm_geo_show_advanced = bpy.props.BoolProperty(
        name="Erweitert",
        default=False,
        description="Manuelle Schritte, Python-Fallback und Props/Felsen-Werkzeuge",
    )
    # V-HACD scene properties
    bpy.types.Scene.cdm_vhacd_max_hulls = bpy.props.IntProperty(
        name="Max Hulls", default=32, min=1, max=256,
        description="V-HACD: Maximum number of convex hulls to produce")
    bpy.types.Scene.cdm_vhacd_resolution = bpy.props.IntProperty(
        name="Resolution", default=200_000, min=10_000, max=10_000_000,
        description="V-HACD: Voxel resolution \u2014 higher = more accurate but slower")
    bpy.types.Scene.cdm_vhacd_max_verts = bpy.props.IntProperty(
        name="Max Verts/Hull", default=32, min=4, max=2048,
        description="V-HACD: Maximum vertices per output convex hull")
    bpy.types.Scene.cdm_vhacd_fill_mode = bpy.props.EnumProperty(
        name="Fill Mode", items=VHACD_FILL_ITEMS, default='flood',
        description="V-HACD: Voxel fill strategy")
    bpy.types.Scene.cdm_vhacd_error_pct = bpy.props.FloatProperty(
        name="Error %", default=2.0, min=0.001, max=10.0,
        description="V-HACD: Allowed volume error in percent \u2014 higher = fewer hulls")
    # CoACD scene properties
    bpy.types.Scene.cdm_coacd_threshold = bpy.props.FloatProperty(
        name="Threshold", default=0.05, min=0.001, max=1.0,
        description="CoACD: Concavity threshold \u2014 lower = more hulls, higher = fewer")
    bpy.types.Scene.cdm_coacd_max_hulls = bpy.props.IntProperty(
        name="Max Hulls", default=32, min=0, max=256,
        description="CoACD: Max convex hulls (0 = DayZ default 32)")
    bpy.types.Scene.cdm_coacd_preprocess = bpy.props.EnumProperty(
        name="Preprocess", default='auto',
        items=[('auto', "Auto", "Auto-detect"),
               ('on',   "On",   "Force on"),
               ('off',  "Off",  "Force off")],
        description="CoACD: Manifold preprocessing mode")
    bpy.types.Scene.cdm_coacd_prep_res = bpy.props.IntProperty(
        name="Prep Resolution", default=50, min=20, max=100,
        description="CoACD: Preprocessing resolution (20\u2013100)")
    bpy.types.Scene.cdm_coacd_mcts_iter = bpy.props.IntProperty(
        name="MCTS Iterations", default=100, min=60, max=2000,
        description="CoACD: Search iterations \u2014 higher = better quality, slower")
    bpy.types.Scene.cdm_coacd_max_ch_vertex = bpy.props.IntProperty(
        name="Max Verts/Hull", default=64, min=8, max=255,
        description="CoACD: Max vertices per convex hull \u2014 DayZ limit is 255")
    bpy.types.Scene.cdm_coacd_dayz_preset = bpy.props.BoolProperty(
        name="DayZ Preset",
        default=False,
        update=_coacd_dayz_preset_update,
        description="Apply DayZ-recommended CoACD values "
                    "(Threshold 0.15, Max Hulls 32, Max Verts/Hull 64)")
    bpy.types.Scene.cdm_geo_density = bpy.props.FloatProperty(
        name="Geo Density",
        default=100.0,
        min=0.001,
        description="Default density for auto vertex mass on new Geometry LOD "
                    "(mass = volume × density, per component)")
    bpy.types.Scene.cdm_geo_color = bpy.props.FloatVectorProperty(
        name="Geometry LOD Color", subtype='COLOR',
        size=4, min=0.0, max=1.0,
        default=(0.1, 0.9, 0.15, 0.7),
        update=_auto_recolor,
        description="Viewport color (RGBA) for the finished Geometry LOD object")
    bpy.types.Scene.cdm_comp_color = bpy.props.FloatVectorProperty(
        name="Component Color", subtype='COLOR',
        size=4, min=0.0, max=1.0,
        default=(0.1, 0.7, 1.0, 0.7),
        update=_auto_recolor,
        description="Viewport color (RGBA) for individual GEO_Components objects")


def _unregister_scene_props():
    for prop in ('cdm_target_object', 'cdm_method',
                 'cdm_min_area', 'cdm_angle_threshold',
                 'cdm_building_phase1_count',
                 'cdm_auto_geo_model_id', 'cdm_auto_geo_score',
                 'cdm_auto_geo_passed', 'cdm_auto_geo_report',
                 'cdm_auto_geo_obb_score', 'cdm_auto_geo_coverage_score',
                 'cdm_engine_busy', 'cdm_engine_status', 'cdm_engine_progress',
                 'cdm_last_geo_pipeline',
                 'cdm_geo_show_advanced',
                 'cdm_geo_density',
                 'cdm_geo_color', 'cdm_comp_color',
                 'cdm_vhacd_max_hulls', 'cdm_vhacd_resolution',
                 'cdm_vhacd_max_verts', 'cdm_vhacd_fill_mode',
                 'cdm_vhacd_error_pct',
                 'cdm_coacd_threshold', 'cdm_coacd_max_hulls',
                 'cdm_coacd_preprocess', 'cdm_coacd_prep_res',
                 'cdm_coacd_mcts_iter', 'cdm_coacd_max_ch_vertex',
                 'cdm_coacd_dayz_preset'):
        try:
            delattr(bpy.types.Scene, prop)
        except AttributeError:
            pass


def register():
    from .register_utils import safe_register_class
    from . import cdm_companions as companions
    cat = companions.resolve_category('CDM Collider', 'cdm_collider')
    companions.apply_n_panel_category(CLASSES, cat)
    _register_scene_props()      # props BEFORE classes (panel draw() needs them)
    for cls in CLASSES:
        safe_register_class(cls)
    # Make the CDM tab in the N-Panel visible immediately (Blender redraws
    # new tabs only on the next redraw — without this timer you need to
    # disable and re-enable the addon for the tab to appear)
    try:
        bpy.ops.wm.redraw_timer(type='DRAW_WIN_SWAP', iterations=1)
    except Exception:
        pass


def unregister():
    from .register_utils import safe_unregister_class
    for cls in reversed(CLASSES):   # classes (incl. panel) BEFORE props
        safe_unregister_class(cls)
    _unregister_scene_props()
