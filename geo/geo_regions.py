"""Sparse geo region seeds (wall, roof, gable, …) for RegionGuided C# pipeline."""
from __future__ import annotations

import json
from typing import Any

import bpy
from bpy.props import (
    CollectionProperty,
    EnumProperty,
    FloatVectorProperty,
    IntProperty,
    PointerProperty,
    StringProperty,
)
from bpy.types import PropertyGroup

REGION_KIND_ITEMS = [
    ('WALL_OUTER', 'Außenwände', 'Eine repräsentative Außenwandfläche markieren'),
    ('ROOF', 'Dach', 'Dachfläche (geneigt oder flach)'),
    ('WALL_INNER', 'Innenwände', 'Eine Innenwandfläche markieren (optional)'),
    ('FLOOR', 'Boden / Decke', 'Boden oder innere Decke'),
    ('GABLE', 'Giebel', 'Giebelfläche am Dachende'),
    ('PLINTH', 'Sockel', 'Sockel / Bodenleiste (optional)'),
    ('SOFFIT', 'Soffit', 'Dachuntersicht unter Vordach / Dachvorsprung (optional)'),
]

REGION_ESSENTIAL_KINDS = ('WALL_OUTER', 'ROOF')
REGION_OPTIONAL_KINDS = ('WALL_INNER', 'FLOOR', 'GABLE', 'PLINTH', 'SOFFIT')

_REGION_KIND_TO_CS = {
    'WALL_OUTER': 'WallOuter',
    'WALL_INNER': 'WallInner',
    'FLOOR': 'Floor',
    'ROOF': 'Roof',
    'GABLE': 'Gable',
    'PLINTH': 'Plinth',
    'SOFFIT': 'Soffit',
}

_CS_TO_REGION_KIND = {v: k for k, v in _REGION_KIND_TO_CS.items()}


class CDM_GeoRegionSeed(PropertyGroup):
    kind: EnumProperty(
        name='Region',
        items=REGION_KIND_ITEMS,
        default='WALL_OUTER',
    )
    face_index: IntProperty(name='Face Index', default=-1)
    position: FloatVectorProperty(name='Position', size=3, default=(0.0, 0.0, 0.0))
    normal: FloatVectorProperty(name='Normal', size=3, default=(0.0, 0.0, 1.0))


def region_label(kind: str) -> str:
    for item_id, label, _ in REGION_KIND_ITEMS:
        if item_id == kind:
            return label
    return kind


def seeds_for_object(obj: bpy.types.Object | None) -> list[CDM_GeoRegionSeed]:
    if obj is None or not hasattr(obj, 'cdm_geo_region_seeds'):
        return []
    return list(obj.cdm_geo_region_seeds)


def seed_count_by_kind(obj: bpy.types.Object | None) -> dict[str, int]:
    counts: dict[str, int] = {item_id: 0 for item_id, _, _ in REGION_KIND_ITEMS}
    for seed in seeds_for_object(obj):
        counts[seed.kind] = counts.get(seed.kind, 0) + 1
    return counts


def has_minimum_seeds(obj: bpy.types.Object | None) -> bool:
    """At least outer wall + roof OR floor."""
    counts = seed_count_by_kind(obj)
    if counts.get('WALL_OUTER', 0) < 1:
        return False
    return counts.get('ROOF', 0) >= 1 or counts.get('FLOOR', 0) >= 1


def essential_requirement_rows(obj: bpy.types.Object | None) -> list[tuple[str, str, bool, int]]:
    """(kind_id, label, fulfilled, count) for N-panel status."""
    counts = seed_count_by_kind(obj)
    roof_ok = counts.get('ROOF', 0) > 0 or counts.get('FLOOR', 0) > 0
    return [
        ('WALL_OUTER', 'Außenwände', counts.get('WALL_OUTER', 0) > 0, counts.get('WALL_OUTER', 0)),
        ('ROOF', 'Dach (oder Boden)', roof_ok, counts.get('ROOF', 0)),
        ('FLOOR', 'Boden (alt. zu Dach)', counts.get('FLOOR', 0) > 0, counts.get('FLOOR', 0)),
    ]


def minimum_requirements_summary(obj: bpy.types.Object | None) -> tuple[bool, str]:
    if has_minimum_seeds(obj):
        return True, 'Pflicht erfüllt — Geo generieren bereit'
    counts = seed_count_by_kind(obj)
    missing = []
    if counts.get('WALL_OUTER', 0) < 1:
        missing.append('Außenwand')
    if counts.get('ROOF', 0) < 1 and counts.get('FLOOR', 0) < 1:
        missing.append('Dach oder Boden')
    return False, 'Fehlt: ' + ', '.join(missing)


def remove_seeds_for_kind(obj: bpy.types.Object, kind: str) -> int:
    if not hasattr(obj, 'cdm_geo_region_seeds'):
        return 0
    removed = 0
    for idx in range(len(obj.cdm_geo_region_seeds) - 1, -1, -1):
        if obj.cdm_geo_region_seeds[idx].kind == kind:
            obj.cdm_geo_region_seeds.remove(idx)
            removed += 1
    return removed


def add_seed(
    obj: bpy.types.Object,
    kind: str,
    face_index: int,
    position: tuple[float, float, float],
    normal: tuple[float, float, float],
    *,
    replace_kind: bool = True,
) -> None:
    if not hasattr(obj, 'cdm_geo_region_seeds'):
        return
    if replace_kind:
        remove_seeds_for_kind(obj, kind)
    seed = obj.cdm_geo_region_seeds.add()
    seed.kind = kind
    seed.face_index = face_index
    seed.position = position
    seed.normal = normal


def seeds_to_json_list(obj: bpy.types.Object | None) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    for seed in seeds_for_object(obj):
        cs_kind = _REGION_KIND_TO_CS.get(seed.kind)
        if not cs_kind:
            continue
        out.append({
            'kind': cs_kind,
            'face_index': int(seed.face_index),
            'position': [float(seed.position[i]) for i in range(3)],
            'normal': [float(seed.normal[i]) for i in range(3)],
        })
    return out


def seeds_to_cli_json_list(obj: bpy.types.Object | None) -> list[dict[str, Any]]:
    """Seeds for C# region-generate — mapped to triangulated export mesh face indices."""
    from .mesh_export_tri import remap_seed_to_export_mesh

    out: list[dict[str, Any]] = []
    for seed in seeds_for_object(obj):
        cs_kind = _REGION_KIND_TO_CS.get(seed.kind)
        if not cs_kind:
            continue
        pos = [float(seed.position[i]) for i in range(3)]
        nrm = [float(seed.normal[i]) for i in range(3)]
        export_fi = remap_seed_to_export_mesh(obj, pos, nrm) if obj is not None else -1
        out.append({
            'kind': cs_kind,
            'face_index': int(export_fi),
            'position': pos,
            'normal': nrm,
        })
    return out


def seeds_to_json_string(obj: bpy.types.Object | None) -> str:
    return json.dumps(seeds_to_json_list(obj))


def load_seeds_from_json_string(obj: bpy.types.Object, text: str) -> int:
    if not text.strip():
        return 0
    data = json.loads(text)
    if not isinstance(data, list):
        return 0
    obj.cdm_geo_region_seeds.clear()
    count = 0
    for item in data:
        cs_kind = str(item.get('kind', ''))
        bpy_kind = _CS_TO_REGION_KIND.get(cs_kind)
        if not bpy_kind:
            continue
        pos = item.get('position') or [0, 0, 0]
        normal = item.get('normal') or [0, 0, 1]
        add_seed(
            obj,
            bpy_kind,
            int(item.get('face_index', item.get('faceIndex', -1))),
            (float(pos[0]), float(pos[1]), float(pos[2])),
            (float(normal[0]), float(normal[1]), float(normal[2])),
            replace_kind=False,
        )
        count += 1
    return count


def resolve_resolution_obj(context) -> bpy.types.Object | None:
    from .helpers import _get_meshes_for_geo

    class _Op:
        def report(self, _tp, _msg):
            pass

    meshes = _get_meshes_for_geo(_Op(), 'SHELL')
    if meshes:
        return meshes[0]
    obj = context.scene.cdm_target_object if context.scene else None
    if obj and obj.type == 'MESH':
        return obj
    active = context.active_object
    if active and active.type == 'MESH':
        return active
    return None
