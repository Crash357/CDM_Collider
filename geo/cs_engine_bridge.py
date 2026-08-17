"""Bridge to C# GeoEngine CLI (Building OBB pipeline)."""
from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile

import bmesh
import bpy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_CLI_NAME = "Cdm.GeoEngine.Cli.dll"
_CLI_PROJ = os.path.join(
    ROOT, "geo_engine_cs", "src", "Cdm.GeoEngine.Cli", "Cdm.GeoEngine.Cli.csproj",
)
# Shipped ZIP layout (no C# sources). Dev builds stay under bin/Release or bin/Debug.
_CLI_SHIPPED = os.path.join(ROOT, "geo_engine_cs", "cli", _CLI_NAME)
_CLI_RELEASE = os.path.join(
    ROOT, "geo_engine_cs", "src", "Cdm.GeoEngine.Cli",
    "bin", "Release", "net8.0", _CLI_NAME,
)
_CLI_DEBUG = os.path.join(
    ROOT, "geo_engine_cs", "src", "Cdm.GeoEngine.Cli",
    "bin", "Debug", "net8.0", _CLI_NAME,
)


def resolve_cli_dll() -> str | None:
    """First existing CLI dll: shipped publish, then Release, then Debug."""
    for path in (_CLI_SHIPPED, _CLI_RELEASE, _CLI_DEBUG):
        if os.path.isfile(path):
            return path
    return None


# Display / diagnose: resolved path, or the shipped location we expect after ZIP install.
_CLI_DLL = _CLI_SHIPPED
_CORPUS_INDEX = os.path.join(
    ROOT, "p3d_files", "_corpus", "building_corpus_index.json",
)
_CORPUS_INDEX_LEGACY = os.path.join(
    ROOT, "p3d_files", "residential", "_sandbox", "building_corpus_index.json",
)
_CORPUS_MESHES_INDEX = os.path.join(
    ROOT, "p3d_files", "_corpus", "corpus_meshes_index.json",
)
_CORPUS_MESHES_INDEX_LEGACY = os.path.join(
    ROOT, "p3d_files", "residential", "_sandbox", "corpus_meshes_index.json",
)
_SCENES_ROOT = os.path.join(ROOT, "p3d_files", "residential", "_sandbox", "scenes")

# UI reads corpus stats on every panel redraw — never json.load() the full index there.
_corpus_summary_cache: tuple[float, dict] | None = None
_corpus_id_cache: tuple[float, list[str]] | None = None


def _corpus_index_path() -> str:
    if os.path.isfile(_CORPUS_INDEX):
        return _CORPUS_INDEX
    return _CORPUS_INDEX_LEGACY


def _corpus_meshes_index_path() -> str:
    if os.path.isfile(_CORPUS_MESHES_INDEX):
        return _CORPUS_MESHES_INDEX
    return _CORPUS_MESHES_INDEX_LEGACY


def _append_corpus_cli_args(cmd: list[str]) -> None:
    """Corpus is dev-only (local DayZ reference data) — omit from CLI when not present."""
    corpus = _corpus_index_path()
    if os.path.isfile(corpus):
        cmd.extend(["--corpus", corpus])
    mesh_store = _corpus_meshes_index_path()
    if os.path.isfile(mesh_store):
        cmd.extend(["--mesh-store", mesh_store])


def _read_corpus_header() -> dict | None:
    """Parse only index header fields (before the huge models[] array)."""
    path = _corpus_index_path()
    if not os.path.isfile(path):
        return None
    try:
        with open(path, encoding="utf-8") as f:
            buf = ""
            while '"models"' not in buf:
                chunk = f.read(8192)
                if not chunk:
                    break
                buf += chunk
            if '"models"' in buf:
                head, _, _ = buf.partition('"models"')
                text = head.rstrip().rstrip(",") + "\n}"
            else:
                text = buf
            return json.loads(text)
    except (OSError, json.JSONDecodeError, ValueError):
        return None


def corpus_summary() -> dict | None:
    """Cached corpus stats for UI (lightweight header read)."""
    global _corpus_summary_cache
    if not os.path.isfile(_corpus_index_path()):
        return None
    try:
        mtime = os.path.getmtime(_corpus_index_path())
    except OSError:
        return None
    if _corpus_summary_cache and _corpus_summary_cache[0] == mtime:
        return _corpus_summary_cache[1]

    data = _read_corpus_header()
    if not data:
        return None
    result = {
        "model_count": data.get("model_count", 0),
        "with_doors": data.get("with_doors", 0),
        "with_scenes": data.get("with_scenes", 0),
        "schema": data.get("schema", ""),
    }
    _corpus_summary_cache = (mtime, result)
    return result


def _corpus_model_ids() -> list[str]:
    """Lazy id list for hint matching (full parse once per mtime)."""
    global _corpus_id_cache
    if not os.path.isfile(_corpus_index_path()):
        return []
    try:
        mtime = os.path.getmtime(_corpus_index_path())
    except OSError:
        return []
    if _corpus_id_cache and _corpus_id_cache[0] == mtime:
        return _corpus_id_cache[1]
    try:
        with open(_corpus_index_path(), encoding="utf-8") as f:
            data = json.load(f)
        ids = [m.get("id", "") for m in data.get("models", []) if m.get("id")]
    except (OSError, json.JSONDecodeError):
        ids = []
    _corpus_id_cache = (mtime, ids)
    return ids


def invalidate_corpus_cache() -> None:
    global _corpus_summary_cache, _corpus_id_cache
    _corpus_summary_cache = None
    _corpus_id_cache = None


def cs_engine_available() -> bool:
    if resolve_cli_dll():
        return True
    return os.path.isfile(_CLI_PROJ)


def _cli_cmd(*args: str) -> list[str]:
    dll = resolve_cli_dll()
    if dll:
        return ["dotnet", dll, *args]
    return ["dotnet", "run", "--project", _CLI_PROJ, "--", *args]


def resolve_corpus_model_id(obj=None, blend_path: str | None = None) -> str | None:
    """Map active scene / blend file to corpus model id (e.g. sheds/shed_w1)."""
    blend_path = blend_path or (bpy.data.filepath or "")
    norm = blend_path.replace("\\", "/").lower()
    if norm:
        sandbox_marker = "/_sandbox/scenes/"
        if sandbox_marker in norm:
            left, rel = norm.split(sandbox_marker, 1)
            category = left.rsplit("/", 1)[-1]
            rel = rel.split("?", 1)[0]
            stem = os.path.splitext(os.path.basename(rel))[0]
            parent = os.path.dirname(rel).replace("\\", "/")
            local_id = "{}/{}".format(parent, stem) if parent and parent != "." else stem
            if category and category != "residential":
                return "{}/{}".format(category, local_id)
            return local_id
        marker = "/scenes/"
        if marker in norm:
            rel = norm.split(marker, 1)[1]
            stem = os.path.splitext(os.path.basename(rel))[0]
            parent = os.path.dirname(rel).replace("\\", "/")
            if parent and parent != ".":
                return "{}/{}".format(parent, stem)
            return stem

    if obj and obj.name:
        hint = obj.name.lower()
        for mid in _corpus_model_ids():
            if mid.split("/")[-1].lower() in hint:
                return mid

    blend_norm = blend_path.replace("\\", "/").lower()
    if blend_norm:
        stem = os.path.splitext(os.path.basename(blend_norm))[0]
        for mid in _corpus_model_ids():
            tail = mid.split("/")[-1].lower()
            if stem == tail or stem.startswith(tail + "_") or stem.endswith("_" + tail):
                return mid
    return None


def _mesh_resolution_json(obj) -> dict:
    depsgraph = bpy.context.evaluated_depsgraph_get()
    eval_obj = obj.evaluated_get(depsgraph)
    mesh = eval_obj.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    mw = eval_obj.matrix_world

    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()

    verts = [(mw @ v.co)[:] for v in bm.verts]
    faces = [[f.verts[i].index for i in range(len(f.verts))] for f in bm.faces]

    groups = {}
    mesh_data = eval_obj.data
    for vg in eval_obj.vertex_groups:
        idx = vg.index
        members = [
            vi for vi, v in enumerate(mesh_data.vertices)
            if any(g.group == idx and g.weight > 0.0 for g in v.groups)
        ]
        if members:
            groups[vg.name] = members

    bm.free()
    eval_obj.to_mesh_clear()

    return {
        "resolutionLod": {
            "name": obj.name,
            "vertices": verts,
            "faces": faces,
            "vertexGroups": groups,
            "geoRegionSeeds": _geo_region_seeds_json(obj),
            "transform": {
                "location": list(eval_obj.location),
                "rotation_euler": list(eval_obj.rotation_euler),
                "scale": list(eval_obj.scale),
                "applied": (
                    abs(eval_obj.scale.x - 1.0) < 0.001
                    and abs(eval_obj.scale.y - 1.0) < 0.001
                    and abs(eval_obj.scale.z - 1.0) < 0.001
                    and all(abs(v) < 0.001 for v in eval_obj.rotation_euler)
                ),
            },
        }
    }


_GEO_LOD_EXCLUDE = ("fire", "view", "component", "memory", "roadway", "hit", "path", "phys", "buoyancy")
_GEO_LOD_NAME_RE = re.compile(
    r"^(\d{2})_(?!Fire_|View_|Memory_|Roadway_|Hit|Path|Phys|Buoyancy)Geometry$",
    re.IGNORECASE,
)


def _is_collision_geometry_name(name: str) -> bool:
    n = name.lower()
    if "geometry" not in n:
        return False
    return not any(x in n for x in _GEO_LOD_EXCLUDE)


def _reference_component_count(obj) -> int:
    if not obj or obj.type != 'MESH':
        return 0
    return sum(
        1 for vg in obj.vertex_groups
        if vg.name.lower().startswith("component")
    )


def _is_plausible_reference_geometry(obj) -> bool:
    """DayZ-Referenz (wenige Components), nicht Addon-Output (512+ Boxen)."""
    if not obj or obj.type != 'MESH':
        return False
    n = _reference_component_count(obj)
    if n == 0 or n > 120:
        return False
    gcol = bpy.data.collections.get("GEO_Components")
    if gcol and obj.name in {o.name for o in gcol.objects}:
        return False
    return True


def _find_geometry_lod_obj(*, for_reference: bool = False):
    """Main collision Geometry LOD (04_Geometry, 05_Geometry, … — never Fire/View)."""
    for name in ("05_Geometry", "04_Geometry", "03_Geometry", "Geometry"):
        obj = bpy.data.objects.get(name)
        if obj and obj.type == "MESH" and _is_collision_geometry_name(obj.name):
            if for_reference and not _is_plausible_reference_geometry(obj):
                continue
            return obj

    numbered: list[tuple[int, object]] = []
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        m = _GEO_LOD_NAME_RE.match(obj.name)
        if m:
            numbered.append((int(m.group(1)), obj))
    if numbered:
        numbered.sort(key=lambda item: item[0])
        for _, obj in reversed(numbered):
            if for_reference and not _is_plausible_reference_geometry(obj):
                continue
            return obj

    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        if _is_collision_geometry_name(obj.name):
            if for_reference and not _is_plausible_reference_geometry(obj):
                continue
            return obj
    return None


def _find_reference_geometry_lod_obj(source_obj=None):
    """Referenz-Collision aus der Szene — kein generiertes Addon-Geometry."""
    ref = _find_geometry_lod_obj(for_reference=True)
    if ref is None or ref == source_obj:
        return None
    return ref


def _geometry_lod_json(obj) -> dict:
    """Export Geometry LOD with component islands for C# OBB reference comparison."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    eval_obj = obj.evaluated_get(depsgraph)
    mesh = eval_obj.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    mw = eval_obj.matrix_world

    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()

    verts = [(mw @ v.co)[:] for v in bm.verts]
    faces = [[f.verts[i].index for i in range(len(f.verts))] for f in bm.faces]

    groups = {}
    mesh_data = eval_obj.data
    for vg in eval_obj.vertex_groups:
        idx = vg.index
        members = [
            vi for vi, v in enumerate(mesh_data.vertices)
            if any(g.group == idx and g.weight > 0.0 for g in v.groups)
        ]
        if members:
            groups[vg.name] = members

    if not groups:
        seen = set()
        island_idx = 1
        for seed in bm.faces:
            if seed.index in seen:
                continue
            stack = [seed]
            island_verts = set()
            while stack:
                face = stack.pop()
                if face.index in seen:
                    continue
                seen.add(face.index)
                for v in face.verts:
                    island_verts.add(v.index)
                for edge in face.edges:
                    for nf in edge.link_faces:
                        if nf.index not in seen:
                            stack.append(nf)
            groups["component{:02d}".format(island_idx)] = sorted(island_verts)
            island_idx += 1

    bm.free()
    eval_obj.to_mesh_clear()

    return {
        "geometryLod": {
            "name": obj.name,
            "vertices": verts,
            "faces": faces,
            "vertexGroups": groups,
        }
    }


def export_geometry_lod_json(obj, path: str) -> None:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(_geometry_lod_json(obj), f)


def reference_geometry_components(obj) -> list[dict]:
    """Referenz-Components mit BBox für Prop-Hull-Zuordnung."""
    payload = _geometry_lod_json(obj)["geometryLod"]
    verts = payload["vertices"]
    groups = payload.get("vertexGroups") or {}
    out: list[dict] = []
    for name in sorted(groups.keys()):
        if not name.lower().startswith("component"):
            continue
        cverts = [verts[i] for i in groups[name] if i < len(verts)]
        if len(cverts) < 4:
            continue
        xs = [v[0] for v in cverts]
        ys = [v[1] for v in cverts]
        zs = [v[2] for v in cverts]
        mn = [min(xs), min(ys), min(zs)]
        mx = [max(xs), max(ys), max(zs)]
        out.append({
            "name": name,
            "bbox": {
                "min": mn,
                "max": mx,
                "center": [(mn[i] + mx[i]) * 0.5 for i in range(3)],
                "size": [mx[i] - mn[i] for i in range(3)],
            },
        })
    return out


def _import_components_from_cs_json(data: dict) -> tuple[int, dict]:
    from .auto_build import _corners_to_component
    from .constants import BOX_FACES
    from .helpers import get_or_create_collection, move_to_collection
    from .helpers import _centre_component_origin, _apply_geo_display

    get_or_create_collection("GEO_Components")
    count = 0
    idx = 1
    for comp in data.get("components", []):
        verts = comp.get("vertices") or []
        name = comp.get("name") or "Component{:02d}".format(idx)
        if len(verts) == 8:
            _corners_to_component([tuple(v) for v in verts], name, faces=BOX_FACES)
        else:
            mesh = bpy.data.meshes.new(name)
            mesh.from_pydata(verts, [], comp.get("faces") or [])
            mesh.validate()
            mesh.update()
            comp_obj = bpy.data.objects.new(name, mesh)
            move_to_collection(comp_obj, "GEO_Components")
            _centre_component_origin(comp_obj)
            _apply_geo_display(comp_obj, is_component=True)
        count += 1
        idx += 1

    stats = {
        "patches": data.get("patches", 0),
        "skipped": data.get("skipped", 0),
        "engine": "C# GeoEngine",
    }
    return count, stats


def generate_building_obb_cs(obj, min_area: float, angle_threshold: float):
    """
    Building geometry via adaptive heuristic search (never bare WallAxis 'generate').
    min_area / angle_threshold kept for API compatibility — search picks params.
    """
    _ = min_area, angle_threshold
    return generate_building_auto_cs(
        obj,
        model_id=resolve_corpus_model_id(obj) or None,
        blind=True,
        allow_snap=False,
    )


def _baked_geometry_mesh_path(model_id: str) -> str | None:
    """Path to baked reference Geometry LOD JSON from corpus_mesh_bake."""
    if not model_id:
        return None
    rel = model_id.replace("\\", "/")
    candidates = [
        os.path.join(ROOT, "p3d_files", "_corpus", "meshes", rel.replace("/", os.sep), "geometry_lod.json"),
        os.path.join(ROOT, "p3d_files", "residential", "_sandbox", "meshes", rel.replace("/", os.sep), "geometry_lod.json"),
    ]
    cat = rel.split("/", 1)[0] if "/" in rel else "residential"
    if cat not in ("residential",):
        candidates.insert(0, os.path.join(
            ROOT, "p3d_files", cat, "_sandbox", "meshes",
            rel.split("/", 1)[1].replace("/", os.sep) if "/" in rel else rel,
            "geometry_lod.json",
        ))
    for path in candidates:
        if os.path.isfile(path):
            return path
    return None


def _is_corpus_sandbox_blend(blend_path: str | None = None) -> bool:
    """True only for corpus validation scenes under _sandbox/scenes/."""
    norm = (blend_path or (bpy.data.filepath if bpy else "") or "").replace("\\", "/").lower()
    return "/_sandbox/scenes/" in norm


def resolve_generation_mode(obj=None, blend_path: str | None = None) -> str:
    """custom = Resolution LOD only; corpus = sandbox scene with baked reference."""
    blend_path = blend_path or (bpy.data.filepath if bpy else "")
    if not _is_corpus_sandbox_blend(blend_path):
        return "custom"
    model_id = resolve_corpus_model_id(obj, blend_path)
    if model_id and _baked_geometry_mesh_path(model_id):
        return "corpus"
    return "custom"


def generation_mode_display(active_obj=None) -> tuple[str, str]:
    mode = resolve_generation_mode(active_obj)
    if mode == "corpus":
        mid = resolve_corpus_model_id(active_obj) or "?"
        return "Modus: Corpus-Kalibrierung ({})".format(mid), "CHECKMARK"
    return "Modus: Custom-Geb\u00e4ude (Heuristik-Suche, 746 Corpus)", "INFO"


def generate_building_auto_cs(obj, model_id: str | None = None, blind: bool = True, allow_snap: bool = False):
    """
    Adaptive C# GeoEngine with heuristic parameter search.
    blind=True: no reference-OBB snap (honest custom generation).
    Corpus index (746 models) supplies target component count via model-id or footprint match.
    Returns (count, stats_dict).
    """
    model_id = model_id or resolve_corpus_model_id(obj) or ""
    ref_obj = _find_reference_geometry_lod_obj(obj)
    ref_geo = None
    if ref_obj is None and model_id:
        baked = _baked_geometry_mesh_path(model_id)
        if baked:
            ref_geo = baked

    with tempfile.TemporaryDirectory(prefix="cdm_cs_auto_") as tmp:
        mesh_in = os.path.join(tmp, "mesh.json")
        mesh_out = os.path.join(tmp, "result.json")
        with open(mesh_in, "w", encoding="utf-8") as f:
            json.dump([_mesh_resolution_json(obj)], f)

        cmd = [
            "auto-generate",
            "--input", mesh_in,
            "--output-json", mesh_out,
        ]
        _append_corpus_cli_args(cmd)

        if ref_obj is not None:
            ref_geo = os.path.join(tmp, "reference_geometry.json")
            export_geometry_lod_json(ref_obj, ref_geo)
        elif model_id and ref_geo is None:
            baked = _baked_geometry_mesh_path(model_id)
            if baked:
                ref_geo = baked
        if ref_geo:
            cmd.extend(["--reference-geometry", ref_geo])
        if model_id:
            cmd.extend(["--model-id", model_id])
        if blind:
            cmd.append("--blind")
        if not allow_snap:
            cmd.append("--no-snap")

        try:
            from .engine_status import set_phase
            if getattr(bpy.context.scene, "cdm_engine_busy", False):
                set_phase(bpy.context, "Heuristik-Suche (500+ Parameter)…", 0.35)
        except Exception:
            pass

        proc = subprocess.run(
            _cli_cmd(*cmd),
            capture_output=True, text=True, timeout=300,
            cwd=ROOT,
        )
        if proc.returncode not in (0, 3):
            err = (proc.stderr or proc.stdout or "unknown error").strip()
            raise RuntimeError(err[:800])

        with open(mesh_out, encoding="utf-8") as f:
            data = json.load(f)

    try:
        from .engine_status import set_phase
        if getattr(bpy.context.scene, "cdm_engine_busy", False):
            set_phase(bpy.context, "Components in Blender importieren…", 0.88)
    except Exception:
        pass

    count, stats = _import_components_from_cs_json(data)
    validation = data.get("validation") or {}
    obb_geo = data.get("obb_geometry") or {}
    stats.update({
        "engine": "C# Adaptive GeoEngine",
        "model_id": data.get("model_id") or model_id,
        "reference_geometry_object": ref_obj.name if ref_obj else "",
        "reference_component_count": _reference_component_count(ref_obj) if ref_obj else 0,
        "blind_mode": blind,
        "min_area": data.get("min_area"),
        "axis_spacing": data.get("axis_spacing"),
        "reference_fit": data.get("reference_fit"),
        "reference_blend": data.get("reference_blend"),
        "angle": data.get("angle"),
        "candidates": data.get("candidates_evaluated", 0),
        "validation": validation,
        "obb_geometry": obb_geo,
        "coverage": data.get("coverage") or {},
        "search_quality": data.get("search_quality") or {},
        "building_profile": data.get("building_profile"),
    })
    return count, stats


def _geo_region_seeds_json(obj) -> list[dict]:
    try:
        from .geo_regions import seeds_to_cli_json_list
        return seeds_to_cli_json_list(obj)
    except Exception:
        return []


def generate_building_region_cs(obj, model_id: str | None = None):
    """Region-guided C# GeoEngine from sparse picker seeds on the resolution mesh."""
    from .geo_regions import has_minimum_seeds, seeds_to_cli_json_list

    if not has_minimum_seeds(obj):
        raise RuntimeError("Mindestens Außenwand + Dach oder Boden markieren.")

    model_id = model_id or resolve_corpus_model_id(obj) or ""
    ref_obj = _find_reference_geometry_lod_obj(obj)
    ref_geo = None
    if ref_obj is None and model_id:
        ref_geo = _baked_geometry_mesh_path(model_id)

    with tempfile.TemporaryDirectory(prefix="cdm_cs_region_") as tmp:
        mesh_in = os.path.join(tmp, "mesh.json")
        seeds_path = os.path.join(tmp, "seeds.json")
        mesh_out = os.path.join(tmp, "result.json")
        payload = _mesh_resolution_json(obj)
        with open(mesh_in, "w", encoding="utf-8") as f:
            json.dump([payload], f)
        with open(seeds_path, "w", encoding="utf-8") as f:
            json.dump(seeds_to_cli_json_list(obj), f)

        cmd = [
            "region-generate",
            "--input", mesh_in,
            "--seeds", seeds_path,
            "--output-json", mesh_out,
        ]
        _append_corpus_cli_args(cmd)
        if ref_obj is not None:
            ref_geo = os.path.join(tmp, "reference_geometry.json")
            export_geometry_lod_json(ref_obj, ref_geo)
        elif model_id and ref_geo is None:
            baked = _baked_geometry_mesh_path(model_id)
            if baked:
                ref_geo = baked
        if ref_geo:
            cmd.extend(["--reference-geometry", ref_geo])
        if model_id:
            cmd.extend(["--model-id", model_id])

        proc = subprocess.run(
            _cli_cmd(*cmd),
            capture_output=True, text=True, timeout=180,
            cwd=ROOT,
        )
        if proc.returncode not in (0, 3):
            err = (proc.stderr or proc.stdout or "unknown error").strip()
            raise RuntimeError(err[:800])

        with open(mesh_out, encoding="utf-8") as f:
            data = json.load(f)

    count, stats = _import_components_from_cs_json(data)
    geo = data.get("geometric_compare") or {}
    coverage = data.get("coverage") or {}
    stats.update({
        "engine": "C# RegionGuided GeoEngine",
        "model_id": data.get("model_id") or model_id,
        "decomposition": "RegionGuided",
        "seed_count": data.get("seed_count", len(seeds_to_cli_json_list(obj))),
        "region_faces": data.get("region_faces") or {},
        "unassigned_faces": data.get("unassigned_faces", 0),
        "target_component_count": data.get("target_component_count", 0),
        "geometric_compare": geo,
        "coverage": coverage,
        "validation": {
            "generated_components": count,
            "reference_components": data.get("target_component_count", 0),
            "overall_score": geo.get("overall_score", geo.get("OverallScore", 0)),
            "passed": count > 0,
        },
    })
    return count, stats


def create_building_geometry_cs(operator, min_area: float, angle_threshold: float):
    """
    Full C# pass: active Resolution LOD → GEO_Components via BuildingGeometryEngine.
    Phase 3 (Finalize) stays in Blender.
    """
    from .helpers import _get_meshes_for_geo, _clear_geo_components, get_or_create_collection

    if not cs_engine_available():
        operator.report({'ERROR'}, "C# GeoEngine nicht verfügbar — dotnet build geo_engine_cs ausführen.")
        return None

    meshes = _get_meshes_for_geo(operator, 'SHELL')
    if not meshes:
        return None
    obj = meshes[0]

    _clear_geo_components()
    get_or_create_collection('GEO_Components')

    try:
        count, stats = generate_building_auto_cs(
            obj,
            model_id=resolve_corpus_model_id(obj) or None,
            blind=True,
            allow_snap=False,
        )
    except (RuntimeError, OSError, json.JSONDecodeError) as exc:
        operator.report({'ERROR'}, "C# GeoEngine: {}".format(exc))
        return None

    if count == 0:
        operator.report({'ERROR'},
                        "C# GeoEngine: keine Components — Min Area / Angle anpassen.")
        return None

    bpy.context.scene.cdm_building_phase1_count = stats.get('patches', count)
    operator.report({'INFO'},
                    "C# GeoEngine '{}': {} Components aus {} Patches "
                    "({} übersprungen). Schritt 3: Finalize.".format(
                        obj.name, count, stats.get('patches', 0),
                        stats.get('skipped', 0)))
    return bpy.data.collections.get('GEO_Components')


def create_building_region_geo(operator, finalize: bool = True):
    """Resolution LOD + sparse region seeds → RegionGuided GeoEngine → optional Finalize."""
    from .auto_build import create_building_finalize
    from .engine_status import begin, end, set_phase
    from .geo_regions import has_minimum_seeds, resolve_resolution_obj, seeds_for_object
    from .helpers import _clear_geo_components, get_or_create_collection

    context = bpy.context
    scene = context.scene

    if not cs_engine_available():
        operator.report({'ERROR'}, "C# GeoEngine nicht verfügbar — dotnet build geo_engine_cs ausführen.")
        return None

    obj = resolve_resolution_obj(context)
    if obj is None:
        operator.report({'ERROR'}, "Kein Resolution LOD gefunden.")
        return None
    if not has_minimum_seeds(obj):
        operator.report({'ERROR'}, "Mindestens Außenwand + Dach oder Boden per Picker markieren.")
        return None

    if not scene.cdm_engine_busy:
        begin(context, "RegionGuided: Mesh + Seeds exportieren…")

    _clear_geo_components()
    get_or_create_collection('GEO_Components')

    try:
        set_phase(context, "C# RegionGuided Engine (CLI)…", 0.35)
        count, stats = generate_building_region_cs(
            obj,
            model_id=resolve_corpus_model_id(obj) or None,
        )
    except (RuntimeError, OSError, json.JSONDecodeError) as exc:
        if scene.cdm_engine_busy:
            end(context, "RegionGuided Fehler.", success=False)
        operator.report({'ERROR'}, "Region GeoEngine: {}".format(exc))
        return None

    if count == 0:
        if scene.cdm_engine_busy:
            end(context, "Keine Components.", success=False)
        operator.report({'ERROR'}, "Region GeoEngine: keine Components erzeugt.")
        return None

    set_phase(context, "Components importiert — prüfe Ergebnis…", 0.72)
    _warn_geo_alignment(operator, obj)
    context.view_layer.objects.active = obj
    scene.cdm_building_phase1_count = stats.get('patches', count)

    geo_cmp = stats.get('geometric_compare') or {}
    cov = stats.get('coverage') or {}
    score = float(geo_cmp.get('overall_score', geo_cmp.get('OverallScore', 0)) or 0)
    cov_frac = float(cov.get('fraction_inside', cov.get('FractionInside', 0)) or 0)
    target = int(stats.get('target_component_count', 0) or 0)

    scene.cdm_auto_geo_model_id = stats.get('model_id') or obj.name
    scene.cdm_auto_geo_score = score
    scene.cdm_auto_geo_obb_score = score
    scene.cdm_auto_geo_coverage_score = cov_frac
    if score > 0:
        components_passed = count > 0 and score >= 0.55
    elif target > 0:
        delta = abs(count - target)
        components_passed = count > 0 and delta <= max(2, int(target * 0.12))
    else:
        components_passed = count > 0
    scene.cdm_last_geo_pipeline = 'RegionGuided'
    scene.cdm_auto_geo_report = (
        "RegionGuided: {} Seeds → {} Components (Ziel {}), unassigned {} Faces".format(
            len(seeds_for_object(obj)),
            count,
            target or '?',
            stats.get('unassigned_faces', '?'),
        )
    )
    if score <= 0 and target > 0:
        scene.cdm_auto_geo_report += "\nComponent-Count: {} vs Ziel {} (kein OBB-Score)".format(
            count, target)

    geo_obj = None
    if finalize:
        set_phase(context, "Finalize → Geometry LOD…", 0.88)
        geo_obj = create_building_finalize(operator)

    pipeline_ok = _apply_finalize_outcome(scene, finalize, geo_obj, components_passed)

    if scene.cdm_engine_busy:
        if pipeline_ok:
            msg = "RegionGuided fertig: {} Components, Cov {:.0f}%".format(
                count, cov_frac * 100)
            if finalize and geo_obj:
                msg += " → Geometry LOD"
            end(context, msg, success=True)
        else:
            end(
                context,
                "Finalize fehlgeschlagen — {} Components, kein Geometry LOD.".format(count),
                success=False,
            )

    if finalize and not geo_obj:
        operator.report({'WARNING'},
                        "Components erzeugt, Finalize fehlgeschlagen — manuell Schritt 3.")
    else:
        operator.report(
            {'INFO'},
            "RegionGuided '{}': {} Components, Coverage {:.0f}%{}".format(
                obj.name,
                count,
                cov_frac * 100,
                " → Geometry LOD" if finalize and geo_obj else "",
            ),
        )

    if finalize:
        return geo_obj or bpy.data.collections.get('GEO_Components')
    return geo_obj or bpy.data.collections.get('GEO_Components')


def _apply_finalize_outcome(
    scene,
    finalize: bool,
    geo_obj,
    components_passed: bool,
) -> bool:
    """Pipeline OK only when components passed and Finalize succeeded (if requested)."""
    if finalize and not geo_obj:
        scene.cdm_auto_geo_passed = False
        note = "Finalize fehlgeschlagen — kein Geometry LOD."
        report = scene.cdm_auto_geo_report or ""
        scene.cdm_auto_geo_report = (
            report + ("\n" + note if report else note)
        )
        return False
    scene.cdm_auto_geo_passed = components_passed
    return components_passed


def _component_world_centers():
    col = bpy.data.collections.get('GEO_Components')
    if not col:
        return []
    out = []
    for comp in col.objects:
        if comp.type != 'MESH' or not comp.data.vertices:
            continue
        mw = comp.matrix_world
        verts = [mw @ v.co for v in comp.data.vertices]
        cx = sum(v.x for v in verts) / len(verts)
        cy = sum(v.y for v in verts) / len(verts)
        cz = sum(v.z for v in verts) / len(verts)
        xs = [v.x for v in verts]
        ys = [v.y for v in verts]
        zs = [v.z for v in verts]
        span = max(max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))
        out.append((cx, cy, cz, span))
    return out


def _warn_geo_alignment(operator, res_obj) -> None:
    """Warn when generated boxes are far from existing Geometry LOD (wrong source mesh)."""
    ref_obj = _find_reference_geometry_lod_obj(res_obj)
    if ref_obj is None or ref_obj == res_obj:
        return
    from .helpers import _world_triangles

    ref_verts, _ = _world_triangles(ref_obj)
    if not ref_verts:
        return
    ref_c = (
        sum(v[0] for v in ref_verts) / len(ref_verts),
        sum(v[1] for v in ref_verts) / len(ref_verts),
        sum(v[2] for v in ref_verts) / len(ref_verts),
    )
    ref_span = max(
        max(v[0] for v in ref_verts) - min(v[0] for v in ref_verts),
        max(v[1] for v in ref_verts) - min(v[1] for v in ref_verts),
        max(v[2] for v in ref_verts) - min(v[2] for v in ref_verts),
    )

    gen = _component_world_centers()
    if not gen:
        return
    gx = sum(c[0] for c in gen) / len(gen)
    gy = sum(c[1] for c in gen) / len(gen)
    gz = sum(c[2] for c in gen) / len(gen)
    g_span = max(c[3] for c in gen)
    import math
    dist = math.sqrt((gx - ref_c[0]) ** 2 + (gy - ref_c[1]) ** 2 + (gz - ref_c[2]) ** 2)

    if dist > max(2.0, ref_span * 0.35):
        operator.report({'ERROR'},
                        "Components {:.1f}m von Referenz-Geometry entfernt — "
                        "Resolution LOD '{}' prüfen (nicht Visual/Geometry).".format(
                            dist, res_obj.name))
    elif g_span > ref_span * 2.5 and ref_span > 0.5:
        operator.report({'WARNING'},
                        "Components deutlich größer als Referenz-Geometry "
                        "({:.1f}m vs {:.1f}m) — Terrain im Resolution LOD?".format(
                            g_span, ref_span))


def _create_building_face_skin(operator, obj, finalize: bool):
    """Experimental Blind: one 1 mm box per coplanar face patch → Geometry."""
    from .face_skin import create_face_skin_components
    from .merge import create_geometry_merge_exact
    from .engine_status import set_phase

    scene = bpy.context.scene
    if getattr(scene, "cdm_engine_busy", False):
        set_phase(bpy.context, "FaceSkin: 1 mm über Flächen…", 0.35)

    min_area = float(getattr(scene, "cdm_min_area", 0.05) or 0.05)
    angle = float(getattr(scene, "cdm_angle_threshold", 30.0) or 30.0)
    count, stats = create_face_skin_components(
        operator, obj, min_area=min_area, angle_threshold=angle,
    )
    if count == 0:
        return None

    scene.cdm_building_phase1_count = stats.get("emitted", count)
    bpy.context.view_layer.objects.active = obj

    geo_obj = None
    if finalize:
        if getattr(scene, "cdm_engine_busy", False):
            set_phase(bpy.context, "Geometry LOD zusammenführen…", 0.92)
        # Exact join only — knife-intersect would shred 2 mm skins.
        geo_obj = create_geometry_merge_exact(operator)

    passed = _apply_finalize_outcome(scene, finalize, geo_obj, bool(stats.get("passed")))
    status = "OK" if passed else "WARN"
    operator.report(
        {'INFO'} if passed else {'WARNING'},
        "[{}] FaceSkin '{}' — {} Boxen, außen {:.2f} mm, Skin {:.2f} mm{}".format(
            status,
            obj.name,
            count,
            float(stats.get("max_outside_m", 0.0)) * 1000.0,
            float(stats.get("min_clearance_m", 0.0)) * 1000.0,
            " → Geometry LOD" if finalize and geo_obj else "",
        ),
    )
    return geo_obj or bpy.data.collections.get("GEO_Components")


def create_building_auto_geo(operator, finalize: bool = True):
    """
    One-click experimental: Resolution LOD → 1 mm FaceSkin → optional Geometry.
    Props (garbage, barricade, …): convex hull per island.
    C# corpus / Constrained-Fit bleibt hinter „Nur Components“.
    """
    from .auto_build import create_building_finalize
    from .helpers import _get_meshes_for_geo, _clear_geo_components, get_or_create_collection
    from .mesh_kind import classify_mesh_kind
    from .prop_hull import create_prop_hull_geometry
    from .engine_status import set_phase

    meshes = _get_meshes_for_geo(operator, 'SHELL')
    if not meshes:
        return None
    obj = meshes[0]
    scene = bpy.context.scene
    model_id = resolve_corpus_model_id(obj) or ""
    mesh_kind = classify_mesh_kind(obj, model_id)
    mode = resolve_generation_mode(obj)

    if mesh_kind != "prop_hull":
        return _create_building_face_skin(operator, obj, finalize)

    if mesh_kind != "prop_hull" and not cs_engine_available():
        operator.report({'ERROR'}, "C# GeoEngine nicht verfügbar — dotnet build geo_engine_cs ausführen.")
        return None

    if mode == "corpus" and not os.path.isfile(_corpus_index_path()):
        operator.report({'ERROR'}, "Corpus-Index fehlt: {}".format(_corpus_index_path()))
        return None

    _clear_geo_components()
    get_or_create_collection('GEO_Components')

    if getattr(scene, "cdm_engine_busy", False):
        set_phase(bpy.context, "Resolution-Mesh vorbereiten…", 0.15)

    try:
        if mesh_kind == "prop_hull":
            ref_comps = None
            if mode == "corpus":
                ref_obj = _find_geometry_lod_obj()
                if ref_obj is not None:
                    ref_comps = reference_geometry_components(ref_obj)
            result = create_prop_hull_geometry(operator, obj, reference_components=ref_comps)
            if result is None:
                return None
            count, stats = result
            stats["model_id"] = model_id
        elif mode == "custom":
            ref_obj = _find_reference_geometry_lod_obj(obj)
            baked = _baked_geometry_mesh_path(resolve_corpus_model_id(obj) or "") if not ref_obj else None
            use_reference = ref_obj is not None or bool(baked)
            if ref_obj:
                operator.report({'INFO'},
                                "Referenz-Geometry '{}' ({} Components) — Constrained-Fit.".format(
                                    ref_obj.name, _reference_component_count(ref_obj)))
            elif use_reference and baked:
                operator.report({'INFO'},
                                "Corpus-Referenz-Geometry — Constrained-Fit.")
            else:
                bad = _find_geometry_lod_obj()
                if bad and not _is_plausible_reference_geometry(bad):
                    operator.report({'WARNING'},
                                    "Objekt 'Geometry' ist generierter Output ({} Components) — "
                                    "nicht als Referenz. Original 05_Geometry separat importieren.".format(
                                        _reference_component_count(bad)))
            count, stats = generate_building_auto_cs(
                obj,
                model_id=resolve_corpus_model_id(obj) or None,
                blind=not use_reference,
                allow_snap=False,
            )
        else:
            model_id = resolve_corpus_model_id(obj) or ""
            count, stats = generate_building_auto_cs(
                obj, model_id or None, blind=False, allow_snap=True,
            )
    except (RuntimeError, OSError, json.JSONDecodeError) as exc:
        operator.report({'ERROR'}, "GeoEngine: {}".format(exc))
        return None

    if count == 0:
        operator.report({'ERROR'}, "GeoEngine: keine Components erzeugt.")
        return None

    _warn_geo_alignment(operator, obj)
    bpy.context.view_layer.objects.active = obj

    bpy.context.scene.cdm_building_phase1_count = stats.get('patches', count)
    blend_label = bpy.path.basename(bpy.data.filepath) if bpy.data.filepath else obj.name
    scene.cdm_auto_geo_model_id = stats.get('model_id') or blend_label
    val = stats.get('validation') or {}
    obb = stats.get('obb_geometry') or {}
    search = stats.get('search_quality') or {}
    coverage = stats.get('coverage') or {}

    composite = float(search.get('Composite', search.get('composite', 0)) or 0)
    obb_score = float(obb.get('OverallScore', obb.get('overallScore', 0)) or 0)
    cov_frac = float(coverage.get('FractionInside', coverage.get('fractionInside', 0)) or 0)
    val_score = float(val.get('OverallScore', val.get('overallScore', 0)) or 0)
    has_search = mesh_kind == "building" and (composite > 0 or (obb.get('ReferenceCount') or 0) > 0)

    if mesh_kind == "prop_hull":
        scene.cdm_auto_geo_score = 1.0 if count > 0 else 0.0
        scene.cdm_auto_geo_obb_score = 0.0
        scene.cdm_auto_geo_coverage_score = 0.0
        components_passed = count > 0
    elif mode == "custom":
        scene.cdm_auto_geo_score = composite if has_search else (1.0 if count > 0 else 0.0)
        scene.cdm_auto_geo_obb_score = obb_score
        scene.cdm_auto_geo_coverage_score = cov_frac
        components_passed = count > 0 and (not has_search or composite >= 0.55)
    else:
        scene.cdm_auto_geo_score = composite if has_search else val_score
        scene.cdm_auto_geo_obb_score = obb_score
        scene.cdm_auto_geo_coverage_score = cov_frac
        components_passed = bool(
            (composite >= 0.72 if has_search else val.get('Passed', val.get('passed', False)))
            and (not has_search or cov_frac >= 0.55))
    msgs = list(val.get('messages') or val.get('Messages') or [])
    if search:
        msgs.append(
            "Search: {:.0f}% (OBB {:.0f}%, Coverage {:.0f}%, Corpus {:.0f}%)".format(
                composite * 100, obb_score * 100, cov_frac * 100, val_score * 100))
    scene.cdm_auto_geo_report = "\n".join(msgs) if msgs else "Kein Validierungsreport."
    scene.cdm_last_geo_pipeline = 'Blind' if stats.get('blind_mode') else 'Auto'

    if mode == "corpus" and has_search and composite < 0.72:
        _clear_geo_components()
        operator.report({'ERROR'},
                        "Heuristik-Suche: Qualität {:.0f}% unter 72% — abgelehnt.".format(composite * 100))
        return None

    geo_obj = None
    if finalize:
        if getattr(scene, "cdm_engine_busy", False):
            set_phase(bpy.context, "Geometry LOD finalisieren…", 0.92)
        geo_obj = create_building_finalize(operator)
    else:
        geo_obj = bpy.data.collections.get('GEO_Components')

    passed = _apply_finalize_outcome(scene, finalize, geo_obj, components_passed)
    if finalize and not geo_obj:
        operator.report({'WARNING'},
                        "Components erzeugt, Finalize fehlgeschlagen — manuell Schritt 3 ausführen.")
    score_pct = int(round(scene.cdm_auto_geo_score * 100))
    ref_c = val.get('ReferenceComponents', val.get('referenceComponents', val.get('reference_components', '?')))
    gen_c = val.get('GeneratedComponents', val.get('generatedComponents', val.get('generated_components', count)))
    status = "OK" if passed else "WARN"
    operator.report(
        {'INFO'} if passed else {'WARNING'},
        "[{}] {} — {} Components (Ref: {}), Score {}%, min_area={} angle={}{}".format(
            status,
            scene.cdm_auto_geo_model_id or obj.name,
            gen_c,
            ref_c,
            score_pct,
            stats.get('min_area', '?'),
            stats.get('angle', '?'),
            " → Geometry LOD" if finalize and geo_obj else "",
        ),
    )
    if finalize:
        return geo_obj or bpy.data.collections.get('GEO_Components')
    return geo_obj or bpy.data.collections.get('GEO_Components')


def export_resolution_mesh_json(obj, out_path: str) -> None:
    """Export evaluated Resolution mesh for batch validation."""
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump([_mesh_resolution_json(obj)], f)
