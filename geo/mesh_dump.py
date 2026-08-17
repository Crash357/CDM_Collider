"""CDM Collider — Mesh als Zahlen exportieren (Debug / Vergleich)."""
import os
import re
from datetime import datetime

import mathutils

from .clustering import _WALL_AXES
from .islands import (
    get_evaluated_bmesh,
    iter_mesh_islands,
    _island_is_closed,
)

_AXIS_LABELS = ('+X', '-X', '+Y', '-Y', '+Z', '-Z')
_COMPONENT_RE = re.compile(r'^Component(\d+)$')


def _face_axis_label(world_normal):
    wn = mathutils.Vector(world_normal).normalized()
    best_i, best_dot = 0, -2.0
    for i, ax in enumerate(_WALL_AXES):
        d = wn.dot(ax)
        if d > best_dot:
            best_dot, best_i = d, i
    if best_dot > 0.5:
        return _AXIS_LABELS[best_i], best_dot
    return 'tilt', best_dot


def _fmt(v, digits=6):
    return f"{v:.{digits}f}"


def _header_line(char='-', width=80):
    return char * width


def _component_number(obj_name):
    m = _COMPONENT_RE.match(obj_name)
    if not m:
        return None
    return int(m.group(1))


def _sorted_geo_components(col):
    if not col:
        return []
    objs = [o for o in col.objects
            if o.type == 'MESH' and _component_number(o.name) is not None]
    return sorted(objs, key=lambda o: _component_number(o.name))


def _dump_single_mesh(bm, mw, lines, prefix=''):
    """Vertices / Edges / Faces eines einzelnen Mesh in lines schreiben."""
    nm = mw.to_3x3().inverted_safe().transposed()
    inv_mw = mw.inverted_safe()
    det = abs(mw.to_3x3().determinant())
    area_scale = det ** (2.0 / 3.0) if det > 1e-10 else 1.0

    lines.append(f"{prefix}Vertices: {len(bm.verts)}")
    lines.append(f"{prefix}Edges:    {len(bm.edges)}")
    lines.append(f"{prefix}Faces:    {len(bm.faces)}")
    lines.append("")

    lines.append(f"{prefix}VERTICES (Welt, Meter)")
    lines.append(f"{prefix}# idx   world_x       world_y       world_z")
    for v in bm.verts:
        wco = mw @ v.co
        lines.append(
            f"{prefix}{v.index:5d}   "
            f"{_fmt(wco.x)}  {_fmt(wco.y)}  {_fmt(wco.z)}")
    lines.append("")

    lines.append(f"{prefix}EDGES")
    lines.append(f"{prefix}# idx   v0    v1    length_m")
    for e in bm.edges:
        v0 = mw @ e.verts[0].co
        v1 = mw @ e.verts[1].co
        lines.append(
            f"{prefix}{e.index:5d}   {e.verts[0].index:4d}  {e.verts[1].index:4d}  "
            f"{_fmt((v1 - v0).length, 4)}")
    lines.append("")

    lines.append(f"{prefix}FACES")
    lines.append(f"{prefix}# idx   verts [..]              area_m2   normal           axis")
    for face in bm.faces:
        wn = (nm @ face.normal).normalized()
        axis, _ = _face_axis_label(wn)
        vert_ids = [str(v.index) for v in face.verts]
        area = face.calc_area() * area_scale
        lines.append(
            f"{prefix}{face.index:5d}   [{','.join(vert_ids):<24}]  "
            f"{_fmt(area, 4):>8}   "
            f"({_fmt(wn.x, 4)}, {_fmt(wn.y, 4)}, {_fmt(wn.z, 4)})   {axis}")
    lines.append("")

    if bm.verts:
        wverts = [mw @ v.co for v in bm.verts]
        xs = [p.x for p in wverts]
        ys = [p.y for p in wverts]
        zs = [p.z for p in wverts]
        lines.append(f"{prefix}AABB Welt:")
        lines.append(f"{prefix}  X: {_fmt(min(xs))} … {_fmt(max(xs))}")
        lines.append(f"{prefix}  Y: {_fmt(min(ys))} … {_fmt(max(ys))}")
        lines.append(f"{prefix}  Z: {_fmt(min(zs))} … {_fmt(max(zs))}")
        lines.append("")


def build_mesh_dump_text(obj, evaluated=True, title=None):
    """
    Gebäude-Mesh als Zahlen-Report.
    Vertices, Edges, Faces inkl. Island- und Achsen-Klassifikation.
    """
    if evaluated:
        bm, mw = get_evaluated_bmesh(obj)
    else:
        import bmesh
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        mw = obj.matrix_world.copy()

    face_island = {}
    face_closed = {}
    island_summaries = []

    for island_id, (island_verts, island_faces, face_ids) in enumerate(
            iter_mesh_islands(bm)):
        closed = _island_is_closed(island_faces, face_ids)
        island_summaries.append({
            'id': island_id,
            'closed': closed,
            'face_count': len(island_faces),
            'vert_count': len(island_verts),
        })
        for face in island_faces:
            face_island[face.index] = island_id
            face_closed[face.index] = closed

    closed_islands = sum(1 for s in island_summaries if s['closed'])
    open_islands = sum(1 for s in island_summaries if not s['closed'])
    open_face_count = sum(
        s['face_count'] for s in island_summaries if not s['closed'])

    label = title or "Gebäude (Quelle)"
    lines = [
        _header_line('='),
        f"CDM Collider — {label}",
        _header_line('='),
        f"Datum:              {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
        f"Objekt:             {obj.name}",
        f"Evaluated Mesh:     {'ja (Modifier angewendet)' if evaluated else 'nein'}",
        "",
        f"Vertices:           {len(bm.verts)}",
        f"Edges:              {len(bm.edges)}",
        f"Faces:              {len(bm.faces)}",
        f"Geschlossene Islands: {closed_islands}",
        f"Offene Islands:       {open_islands}",
        f"Offene Face-Indices:  {open_face_count}",
        "",
    ]

    _dump_single_mesh(bm, mw, lines)

    lines.append(_header_line())
    lines.append("ISLANDS (wie Addon sie sieht)")
    lines.append(_header_line())
    for info in island_summaries:
        typ = 'GESCHLOSSEN' if info['closed'] else 'OFFEN'
        lines.append(
            f"Island {info['id']:3d}: {typ:12s}  "
            f"faces={info['face_count']:5d}  verts={info['vert_count']:5d}")
    lines.append("")

    axis_bins = {label: 0 for label in _AXIS_LABELS}
    axis_bins['tilt'] = 0
    nm = mw.to_3x3().inverted_safe().transposed()
    for face in bm.faces:
        wn = (nm @ face.normal).normalized()
        axis, _ = _face_axis_label(wn)
        axis_bins[axis] = axis_bins.get(axis, 0) + 1

    lines.append(_header_line())
    lines.append("FACE-VERTEILUNG NACH ACHSE")
    lines.append(_header_line())
    for ax_label in _AXIS_LABELS + ('tilt',):
        lines.append(f"  {ax_label:>5s}:  {axis_bins.get(ax_label, 0):6d} faces")
    lines.append("")

    lines.append(_header_line('='))
    lines.append(f"Ende — {label}")
    lines.append(_header_line('='))

    stats = {
        'verts': len(bm.verts),
        'edges': len(bm.edges),
        'faces': len(bm.faces),
        'components': 0,
    }
    bm.free()
    return '\n'.join(lines), stats


def build_components_dump_text(objects, title, source_building=None):
    """Dump aller GEO_Components-Objekte (Phase 1 oder Phase 2)."""
    import bmesh

    lines = [
        _header_line('='),
        f"CDM Collider — {title}",
        _header_line('='),
        f"Datum:              {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
    ]
    if source_building:
        lines.append(f"Quell-Gebäude:      {source_building}")
    lines.append(f"Components:         {len(objects)}")
    lines.append("")

    if not objects:
        lines.append("(!) Keine Components vorhanden — Phase noch nicht ausgeführt?")
        lines.append("")
        lines.append(_header_line('='))
        return '\n'.join(lines), {'verts': 0, 'edges': 0, 'faces': 0, 'components': 0}

    total_v = total_e = total_f = 0

    for obj in objects:
        num = _component_number(obj.name)
        lines.append(_header_line())
        lines.append(f"COMPONENT {obj.name}  (#{num})")
        lines.append(_header_line())

        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.verts.ensure_lookup_table()
        bm.edges.ensure_lookup_table()
        bm.faces.ensure_lookup_table()
        mw = obj.matrix_world.copy()

        total_v += len(bm.verts)
        total_e += len(bm.edges)
        total_f += len(bm.faces)

        _dump_single_mesh(bm, mw, lines)
        bm.free()

    lines.append(_header_line('='))
    lines.append("GESAMT")
    lines.append(f"  Components: {len(objects)}")
    lines.append(f"  Vertices:   {total_v}")
    lines.append(f"  Edges:      {total_e}")
    lines.append(f"  Faces:      {total_f}")
    lines.append(_header_line('='))
    lines.append(f"Ende — {title}")
    lines.append(_header_line('='))

    return '\n'.join(lines), {
        'verts': total_v,
        'edges': total_e,
        'faces': total_f,
        'components': len(objects),
    }


def _is_geometry_lod(obj):
    if not obj or obj.type != 'MESH':
        return False
    if obj.name == 'Geometry':
        return True
    if any(c.name == 'Geometry' for c in obj.users_collection):
        return True
    return any(vg.name.startswith('Component') for vg in obj.vertex_groups)


def resolve_geometry_lod(context):
    """Fertiges Geometry LOD — aktiv gewählt oder Objekt 'Geometry'."""
    import bpy

    active = context.active_object
    if _is_geometry_lod(active):
        return active
    return bpy.data.objects.get('Geometry')


def resolve_source_building(context):
    """Visual-Mesh / Gebäude — nicht das Geometry LOD."""
    import bpy

    geo = resolve_geometry_lod(context)
    target = context.scene.cdm_target_object
    if target and target.type == 'MESH' and target != geo:
        return target

    active = context.active_object
    if active and active.type == 'MESH' and not _is_geometry_lod(active):
        return active

    return None


def _sorted_component_vertex_groups(obj):
    groups = [vg for vg in obj.vertex_groups
              if _component_number(vg.name) is not None]
    return sorted(groups, key=lambda vg: _component_number(vg.name))


def _vert_indices_in_group(obj, vg):
    idx = vg.index
    out = []
    for v in obj.data.vertices:
        for g in v.groups:
            if g.group == idx and g.weight > 0.499:
                out.append(v.index)
                break
    return out


def _face_indices_for_verts(mesh, vert_set):
    faces = []
    for poly in mesh.polygons:
        if all(vi in vert_set for vi in poly.vertices):
            faces.append(poly.index)
    return faces


def build_geometry_lod_dump_text(obj, title=None):
    """Dump des fertigen Geometry LOD inkl. Vertex Groups (ComponentXX)."""
    import bmesh

    label = title or "03 — Geometry LOD (fertig)"
    mesh = obj.data
    mw = obj.matrix_world.copy()

    lines = [
        _header_line('='),
        f"CDM Collider — {label}",
        _header_line('='),
        f"Datum:              {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
        f"Objekt:             {obj.name}",
        f"Collection:         {', '.join(c.name for c in obj.users_collection) or '-'}",
        "",
        "DAYZ-EIGENSCHAFTEN:",
    ]
    for key in ('LOD', 'autocenter', 'canbeoccluded', 'canocclude'):
        if key in obj:
            lines.append(f"  {key}: {obj[key]}")
    lines.append("")
    lines.append(f"Vertices (gesamt):  {len(mesh.vertices)}")
    lines.append(f"Edges (gesamt):     {len(mesh.edges)}")
    lines.append(f"Faces (gesamt):     {len(mesh.polygons)}")
    lines.append(f"Vertex Groups:      {len(obj.vertex_groups)}")
    comp_groups = _sorted_component_vertex_groups(obj)
    lines.append(f"Component-Groups:   {len(comp_groups)}")
    lines.append("")

    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.verts.ensure_lookup_table()
    bm.edges.ensure_lookup_table()
    bm.faces.ensure_lookup_table()

    lines.append(_header_line())
    lines.append("GESAMT-MESH")
    lines.append(_header_line())
    _dump_single_mesh(bm, mw, lines)

    total_v = total_e = total_f = 0
    for vg in comp_groups:
        num = _component_number(vg.name)
        vert_set = set(_vert_indices_in_group(obj, vg))
        if not vert_set:
            continue

        lines.append(_header_line())
        lines.append(f"VERTEX GROUP / COMPONENT {vg.name}  (#{num})")
        lines.append(f"  Vertices in Gruppe: {len(vert_set)}")
        lines.append(_header_line())

        lines.append("VERTICES (Welt, Meter)")
        lines.append("# idx   world_x       world_y       world_z")
        for vi in sorted(vert_set):
            wco = mw @ mesh.vertices[vi].co
            lines.append(
                f"{vi:5d}   {_fmt(wco.x)}  {_fmt(wco.y)}  {_fmt(wco.z)}")
        lines.append("")

        face_ids = _face_indices_for_verts(mesh, vert_set)
        lines.append(f"FACES in Component: {len(face_ids)}")
        lines.append("# face_idx   vert_indices [..]   area_m2")
        nm = mw.to_3x3().inverted_safe().transposed()
        det = abs(mw.to_3x3().determinant())
        area_scale = det ** (2.0 / 3.0) if det > 1e-10 else 1.0
        for fi in face_ids:
            poly = mesh.polygons[fi]
            v_str = ','.join(str(v) for v in poly.vertices)
            area = poly.area * area_scale
            lines.append(f"{fi:5d}       [{v_str:<20}]  {_fmt(area, 4)}")
        lines.append("")

        wco_list = [mw @ mesh.vertices[vi].co for vi in vert_set]
        xs = [p.x for p in wco_list]
        ys = [p.y for p in wco_list]
        zs = [p.z for p in wco_list]
        lines.append("AABB Welt (nur diese Component):")
        lines.append(f"  X: {_fmt(min(xs))} … {_fmt(max(xs))}")
        lines.append(f"  Y: {_fmt(min(ys))} … {_fmt(max(ys))}")
        lines.append(f"  Z: {_fmt(min(zs))} … {_fmt(max(zs))}")
        lines.append("")

        total_v += len(vert_set)
        total_f += len(face_ids)

    bm.free()

    lines.append(_header_line('='))
    lines.append("GESAMT (alle Components)")
    lines.append(f"  Components: {len(comp_groups)}")
    lines.append(f"  Vertices:   {total_v}  (in Gruppen, kann Überlappung haben)")
    lines.append(f"  Faces:      {total_f}")
    lines.append(_header_line('='))
    lines.append(f"Ende — {label}")
    lines.append(_header_line('='))

    return '\n'.join(lines), {
        'verts': len(mesh.vertices),
        'edges': len(mesh.edges),
        'faces': len(mesh.polygons),
        'components': len(comp_groups),
    }


def export_geometry_lod_dump_to_file(obj, filepath, title=None):
    text, stats = build_geometry_lod_dump_text(obj, title=title)
    ok, err = _write_text(filepath, text)
    if not ok:
        return False, err
    return True, stats


def export_mesh_dump_to_file(obj, filepath, evaluated=True, title=None):
    text, stats = build_mesh_dump_text(obj, evaluated=evaluated, title=title)
    try:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(text)
    except OSError as e:
        return False, str(e)
    return True, stats


def _write_text(filepath, text):
    try:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(text)
        return True, None
    except OSError as e:
        return False, str(e)


def export_compare_dumps(building_obj, base_filepath, phase1_count=0,
                         geometry_lod_obj=None):
    """
    Schreibt 4 Vergleichs-Dateien neben base_filepath:
      *_00_gebaeude.txt     — Quell-Mesh
      *_01_phase1.txt       — GEO_Components Component01…NN
      *_02_phase2.txt       — GEO_Components Component(NN+1)…
      *_03_geometry_lod.txt — fertiges Geometry LOD (Hand / Merge)
    """
    import bpy

    base_filepath = bpy.path.abspath(base_filepath)
    folder = os.path.dirname(base_filepath)
    base = os.path.splitext(os.path.basename(base_filepath))[0]
    if base.endswith('_vergleich'):
        base = base[:-10]

    path_gebaeude = os.path.join(folder, f"{base}_00_gebaeude.txt")
    path_phase1 = os.path.join(folder, f"{base}_01_phase1.txt")
    path_phase2 = os.path.join(folder, f"{base}_02_phase2.txt")
    path_geometry = os.path.join(folder, f"{base}_03_geometry_lod.txt")

    if building_obj:
        text0, stats0 = build_mesh_dump_text(
            building_obj, evaluated=True, title="00 — Gebäude (Quelle)")
    else:
        text0 = "(!) Kein Gebäude gefunden — Target Object setzen oder Visual-Mesh wählen.\n"
        stats0 = {'verts': 0, 'edges': 0, 'faces': 0, 'components': 0}
    ok, err = _write_text(path_gebaeude, text0)
    if not ok:
        return False, err, {}

    col = bpy.data.collections.get('GEO_Components')
    all_comps = _sorted_geo_components(col)

    phase1_objs = []
    phase2_objs = []
    for obj in all_comps:
        num = _component_number(obj.name)
        if num is None:
            continue
        if phase1_count > 0 and num <= phase1_count:
            phase1_objs.append(obj)
        elif phase1_count > 0 and num > phase1_count:
            phase2_objs.append(obj)
        elif phase1_count <= 0:
            phase1_objs.append(obj)

    text1, stats1 = build_components_dump_text(
        phase1_objs,
        title="01 — Phase 1 (Closed Islands)",
        source_building=building_obj.name)
    ok, err = _write_text(path_phase1, text1)
    if not ok:
        return False, err, {}

    if phase1_count <= 0 and all_comps:
        note = (
            "\n\nHINWEIS: cdm_building_phase1_count = 0 — alle Components "
            "sind in Phase-1-Datei.\nPhase 2 noch nicht getrennt exportierbar. "
            "Zuerst Phase 1 ausführen.\n"
        )
        text1 += note
        _write_text(path_phase1, text1)

    text2, stats2 = build_components_dump_text(
        phase2_objs,
        title="02 — Phase 2 (Wand-Boxen)",
        source_building=building_obj.name)
    if phase1_count <= 0:
        text2 = (
            "(!) Phase 2 Dump leer — zuerst Phase 1 und Phase 2 ausführen.\n"
            f"cdm_building_phase1_count = {phase1_count}\n"
        )
        stats2 = {'verts': 0, 'edges': 0, 'faces': 0, 'components': 0}
    ok, err = _write_text(path_phase2, text2)
    if not ok:
        return False, err, {}

    if geometry_lod_obj:
        text3, stats3 = build_geometry_lod_dump_text(
            geometry_lod_obj, title="03 — Geometry LOD (fertig)")
    else:
        text3 = (
            "(!) Kein Geometry LOD gefunden.\n"
            "Objekt 'Geometry' wählen oder im N-Panel Target Object setzen.\n"
        )
        stats3 = {'verts': 0, 'edges': 0, 'faces': 0, 'components': 0}
    ok, err = _write_text(path_geometry, text3)
    if not ok:
        return False, err, {}

    return True, None, {
        'paths': [path_gebaeude, path_phase1, path_phase2, path_geometry],
        'gebaeude': stats0,
        'phase1': stats1,
        'phase2': stats2,
        'geometry_lod': stats3,
        'phase1_count': phase1_count,
    }
