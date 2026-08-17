"""CDM Collider — collection, selection, display helpers."""

import re

import bpy
import bmesh
import mathutils

_RES_NAME_RE = re.compile(
    r"(?:^|[_\s])(?:res[_\s]?(\d+(?:\.\d+)?)|resolution[_\s](\d+(?:\.\d+)?))",
    re.IGNORECASE,
)

def _get_target():
    return bpy.context.scene.cdm_target_object


def get_or_create_collection(name):
    if name in bpy.data.collections:
        return bpy.data.collections[name]
    col = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(col)
    return col


def move_to_collection(obj, col_name):
    col = get_or_create_collection(col_name)
    for c in list(obj.users_collection):
        c.objects.unlink(obj)
    col.objects.link(obj)


def ensure_object_mode():
    if bpy.context.active_object and bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')


def _get_active_mesh(operator, building_mode=False):
    """Nur das aktive Mesh — verhindert Geo von mehreren Gebäuden gleichzeitig."""
    ensure_object_mode()
    obj = bpy.context.active_object
    if not obj or obj.type != 'MESH':
        t = _get_target()
        if t and t.type == 'MESH':
            obj = t
        else:
            operator.report({'ERROR'},
                            "Select exactly one mesh (active object).")
            return None

    others = [o for o in bpy.context.selected_objects
              if o.type == 'MESH' and o.name != obj.name]
    if others and building_mode:
        operator.report({'WARNING'},
                        "Nur '{}' verarbeitet — {} weitere deselektieren.".format(
                            obj.name, len(others)))
    return obj


def _get_selected_meshes(operator):
    """Aktive Mesh-Auswahl oder cdm_target_object."""
    ensure_object_mode()
    selected = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    if not selected:
        t = _get_target()
        if not t or t.type != 'MESH':
            operator.report({'ERROR'}, "Select mesh object(s) or set a Target Object.")
            return None
        selected = [t]
    return selected


BUILDING_GEO_METHODS = frozenset({'SHELL', 'WALL', 'SPLIT', 'OBB'})


def _lod_resolution_value(obj) -> float | None:
    if not obj or obj.type != 'MESH':
        return None
    try:
        from ..dayz_lod_compat import get_lod_resolution
        return get_lod_resolution(obj)
    except Exception:
        return None


def _is_resolution_lod_mesh(obj) -> bool:
    res = _lod_resolution_value(obj)
    if res is not None and res < 1000.0:
        return True
    m = _RES_NAME_RE.search(obj.name if obj else "")
    if not m:
        return False
    n = (obj.name if obj else "").lower()
    return not any(x in n for x in ("geometry", "geometrie", "fire", "view", "memory"))


def _is_collision_geometry_lod(obj) -> bool:
    res = _lod_resolution_value(obj)
    if res is not None and res >= 1.0e12:
        return True
    n = (obj.name if obj else "").lower()
    return "geometry" in n or "geometrie" in n


def find_resolution_lod_mesh(prefer=None, operator=None, *, custom_mode=None):
    """
    Quell-Mesh für Gebäude-Geo: immer das Detail-/Resolution-Mesh, nie Collision-Geometry.

    Custom-Modus: frei benannte Meshes (heini, …) erlaubt; „Geometry“ / 05_Geometry
    werden verworfen und durch Resolution LOD ersetzt.
    """
    if custom_mode is None:
        try:
            from .cs_engine_bridge import resolve_generation_mode
            custom_mode = resolve_generation_mode() == "custom"
        except Exception:
            custom_mode = True

    if prefer and prefer.type == 'MESH':
        n = prefer.name.lower()
        if any(x in n for x in ("fire", "view", "memory", "roadway", "hitpoint")):
            if operator:
                operator.report({'ERROR'},
                                "'{}' ist kein gültiges Quell-Mesh.".format(prefer.name))
            return None
        if custom_mode and not _is_collision_geometry_lod(prefer):
            return prefer
        if not custom_mode and _is_resolution_lod_mesh(prefer):
            return prefer

    candidates: list[tuple[object, float]] = []
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        res = _lod_resolution_value(obj)
        if res is not None and res < 1000.0:
            candidates.append((obj, res))
            continue
        m = _RES_NAME_RE.search(obj.name)
        if m:
            val = m.group(1) or m.group(2)
            try:
                candidates.append((obj, float(val)))
            except ValueError:
                pass

    if candidates:
        candidates.sort(key=lambda item: (abs(item[1] - 1.0), item[1]))
        chosen = candidates[0][0]
        if prefer and prefer.type == 'MESH' and chosen.name != prefer.name and operator:
            if _is_collision_geometry_lod(prefer):
                operator.report({'WARNING'},
                                "Aktives Objekt ist Geometry LOD — "
                                "verwende Resolution LOD '{}'.".format(chosen.name))
            elif not _is_resolution_lod_mesh(prefer):
                operator.report({'INFO'},
                                "Resolution LOD '{}' wird verwendet "
                                "(nicht '{}').".format(chosen.name, prefer.name))
        return chosen

    if prefer and prefer.type == 'MESH':
        if operator and _is_collision_geometry_lod(prefer):
            operator.report({'ERROR'},
                            "'{}' ist Geometry LOD — Resolution LOD wählen.".format(
                                prefer.name))
            return None
        return prefer

    if operator:
        operator.report({'ERROR'},
                        "Kein Mesh gewählt und kein Resolution LOD in der Szene.")
    return None


def _get_meshes_for_geo(operator, method='OBB'):
    """Gebäude-Methoden: Resolution LOD; sonst volle Auswahl."""
    if method in BUILDING_GEO_METHODS:
        active = bpy.context.active_object
        target = _get_target()
        prefer = active if active and active.type == 'MESH' else target
        obj = find_resolution_lod_mesh(prefer, operator=operator)
        return [obj] if obj else None
    return _get_selected_meshes(operator)


def _vertex_group_has_verts(obj, vg):
    """True wenn mindestens ein Vertex Gewicht > 0 in dieser Gruppe hat."""
    idx = vg.index
    mesh = obj.data
    if not mesh.vertices:
        return False
    for v in mesh.vertices:
        for g in v.groups:
            if g.group == idx and g.weight > 0.0:
                return True
    return False


def _get_mesh_for_vertex_groups(operator):
    """Aktives Mesh zuerst, sonst Target — mit gewichteten Vertex Groups."""
    ensure_object_mode()
    active = bpy.context.active_object
    target = _get_target()

    for obj in (active, target):
        if not obj or obj.type != 'MESH':
            continue
        if len(obj.vertex_groups) == 0:
            continue
        usable = [vg for vg in obj.vertex_groups
                  if _vertex_group_has_verts(obj, vg)]
        if usable:
            return obj, usable

    if active and active.type == 'MESH' and len(active.vertex_groups) > 0:
        operator.report({'ERROR'},
                        "'{}': Vertex Groups vorhanden, aber keine "
                        "gewichteten Vertices.".format(active.name))
    elif target and target.type == 'MESH' and len(target.vertex_groups) > 0:
        operator.report({'ERROR'},
                        "Target '{}': Vertex Groups ohne Vertex-Gewichte.".format(
                            target.name))
    else:
        operator.report({'ERROR'},
                        "Mesh mit Vertex Groups wählen (aktiv) "
                        "oder Target Object setzen.")
    return None, []


def _next_component_index():
    """Nächster freier ComponentXX-Index in GEO_Components."""
    col = bpy.data.collections.get('GEO_Components')
    if not col:
        return 1
    max_idx = 0
    for o in col.objects:
        if not o.name.startswith('Component'):
            continue
        suffix = o.name[9:]  # len('Component') == 9
        try:
            max_idx = max(max_idx, int(suffix))
        except ValueError:
            pass
    return max_idx + 1


def _clear_geo_components():
    """Alle Objekte in GEO_Components entfernen."""
    col = bpy.data.collections.get('GEO_Components')
    if not col:
        return
    for old_obj in list(col.objects):
        bpy.data.objects.remove(old_obj, do_unlink=True)

def _centre_component_origin(comp_obj):
    """Origin auf geometrisches Zentrum — Komponente bleibt weltlich korrekt."""
    mesh = comp_obj.data
    verts = mesh.vertices
    if not verts:
        return
    cx = sum(v.co.x for v in verts) / len(verts)
    cy = sum(v.co.y for v in verts) / len(verts)
    cz = sum(v.co.z for v in verts) / len(verts)
    for v in verts:
        v.co.x -= cx
        v.co.y -= cy
        v.co.z -= cz
    mesh.update()
    comp_obj.location = (cx, cy, cz)
    # Neu erzeugte Objekte: location kann vor matrix_world hängen (v.a. Background/Import)
    loc = mathutils.Vector((cx, cy, cz))
    if (loc - comp_obj.matrix_world.translation).length > 1e-4:
        mw = comp_obj.matrix_world.copy()
        mw.translation = loc
        comp_obj.matrix_world = mw


def _world_triangles(src_obj):
    """Mesh triangulieren, Weltkoordinaten → (verts, tris)."""
    mw = src_obj.matrix_world
    bm = bmesh.new()
    bm.from_mesh(src_obj.data)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    verts_py = [(mw @ v.co).to_tuple() for v in bm.verts]
    tris_py = [(f.verts[0].index, f.verts[1].index, f.verts[2].index)
               for f in bm.faces]
    bm.free()
    return verts_py, tris_py

def _recolor_material(mat, rgba):
    """Set viewport + BSDF color. HASHED/DITHERED keeps the hue in Solid
    shading; BLEND over a dark viewport reads as muddy gray."""
    mat.diffuse_color = rgba
    if hasattr(mat, 'metallic'):
        mat.metallic = 0.0
    if hasattr(mat, 'roughness'):
        mat.roughness = 1.0
    if hasattr(mat, 'specular_intensity'):
        mat.specular_intensity = 0.0
    opaque = rgba[3] >= 0.99
    # Blender 5.x: surface_render_method; 4.x: blend_method.
    if hasattr(mat, 'surface_render_method'):
        prop = mat.bl_rna.properties.get('surface_render_method')
        items = {item.identifier for item in prop.enum_items} if prop else set()
        if opaque:
            if 'OPAQUE' in items:
                mat.surface_render_method = 'OPAQUE'
            elif 'DITHERED' in items:
                mat.surface_render_method = 'DITHERED'
        elif 'DITHERED' in items:
            mat.surface_render_method = 'DITHERED'
        elif 'BLENDED' in items:
            mat.surface_render_method = 'BLENDED'
    elif hasattr(mat, 'blend_method'):
        mat.blend_method = 'OPAQUE' if opaque else 'HASHED'

    if mat.use_nodes and mat.node_tree:
        bsdf = next(
            (n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'),
            None,
        )
        if bsdf:
            bsdf.inputs['Base Color'].default_value = (rgba[0], rgba[1], rgba[2], 1.0)
            # Keep the shader opaque. Solid-view transparency comes from
            # mat.diffuse_color alpha (HASHED/DITHERED). Putting alpha on
            # Principled makes Studio lighting render the collider dark gray.
            if 'Alpha' in bsdf.inputs:
                bsdf.inputs['Alpha'].default_value = 1.0
            if 'Metallic' in bsdf.inputs:
                bsdf.inputs['Metallic'].default_value = 0.0
            if 'Roughness' in bsdf.inputs:
                bsdf.inputs['Roughness'].default_value = 1.0
            for spec_key in ('Specular IOR Level', 'Specular'):
                if spec_key in bsdf.inputs:
                    bsdf.inputs[spec_key].default_value = 0.0
                    break


def _get_or_create_display_material(name, rgba):
    """Debug material (Principled BSDF) matching the given RGBA — so
    Components/Geometry show colored in Material Preview / Rendered
    viewport shading too, not only in Solid + Object-Color mode."""
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
    _recolor_material(mat, rgba)
    return mat


def _ensure_geo_viewport_colors():
    """Do not change viewport shading — user keeps Solid / Material / Rendered."""
    return


def _apply_geo_display(obj, is_component=False):
    """Viewport object color + matching debug material.

    obj.color only shows in Solid shading with Color = Object, so a
    Principled-BSDF material with the same base color is (re-)assigned
    as slot 0 — that way Components/Geometry are colored regardless of
    the active viewport shading mode (Solid/Material Preview/Rendered).
    """
    try:
        scene = bpy.context.scene
        col = tuple(scene.cdm_comp_color if is_component else scene.cdm_geo_color)
    except Exception:
        col = (0.1, 0.7, 1.0, 0.7) if is_component else (0.1, 0.9, 0.15, 0.7)

    rgba = (col[0], col[1], col[2], col[3] if len(col) > 3 else 1.0)
    obj.color = rgba
    obj.display_type = 'SOLID'
    obj.show_wire = True
    obj.show_in_front = True
    obj.visible_shadow = False
    if hasattr(obj, 'show_transparent'):
        obj.show_transparent = False

    if obj.type == 'MESH':
        mesh = obj.data
        existing = mesh.materials[0] if mesh.materials else None
        if existing is not None:
            # Real export material already assigned (e.g. "cdm_geo") —
            # keep its name/slot, only recolor it for viewport display.
            _recolor_material(existing, rgba)
        else:
            mat_name = 'cdm_comp_mat' if is_component else 'cdm_geo_mat'
            mat = _get_or_create_display_material(mat_name, rgba)
            mesh.materials.append(mat)


def apply_geometry_lod_metadata(obj):
    """DayZ Geometry LOD (1e13): CDM resolution + legacy O2 tags + UVMap."""
    from ..dayz_lod_compat import LOD_GEOMETRY, sync_object_lod_props

    sync_object_lod_props(obj, LOD_GEOMETRY)
    obj["autocenter"] = 0
    obj["canbeoccluded"] = 1
    obj["canocclude"] = 0


def apply_scene_geometry_mass(obj):
    """Apply scene cdm_geo_density as vertex mass on Geometry LOD (1e13)."""
    if obj is None:
        return
    try:
        from ..dayz_lod_compat import apply_geometry_lod_mass
        density = float(bpy.context.scene.cdm_geo_density)
        if density <= 0:
            density = 100.0
        apply_geometry_lod_mass(obj, density)
    except Exception:
        pass


def _report_decomp_result(operator, label, total):
    """DayZ-Limits prüfen und Abschlussmeldung für V-HACD/CoACD."""
    if total == 0:
        operator.report({'ERROR'}, "{}: No hulls produced.".format(label))
        return None
    col = bpy.data.collections.get('GEO_Components')
    if col:
        bad_verts = [o.name for o in col.objects if len(o.data.vertices) > 255]
        if bad_verts:
            operator.report(
                {'WARNING'},
                "{}: {} hull(s) with >255 verts (DayZ limit): {}.".format(
                    label, len(bad_verts), ", ".join(bad_verts[:5])))
        if total > 32:
            operator.report(
                {'WARNING'},
                "{}: {} hulls — DayZ recommends max. 32.".format(label, total))
    operator.report(
        {'INFO'},
        "{}: {} components in 'GEO_Components'. "
        "Inspect, then Merge (Hull) → Geometry LOD.".format(label, total))
    return bpy.data.objects.get("Component01")

def get_bbox(obj):
    wm = obj.matrix_world
    corners = [wm @ mathutils.Vector(c) for c in obj.bound_box]
    return (min(v.x for v in corners), max(v.x for v in corners),
            min(v.y for v in corners), max(v.y for v in corners),
            min(v.z for v in corners), max(v.z for v in corners))

