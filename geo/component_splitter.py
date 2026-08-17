"""CDM Collider — minimal component split for DayZ buildings."""
import mathutils

from .clustering import _cluster_by_wall_axis, _merge_antiparallel_clusters, _thicken_verts

OFFSET_M = 0.001          # 1 mm seam offset between adjacent components
NEARBY_M = 3.0           # max distance [m] to primary for offset logic
MIN_THICKNESS = 0.5       # default solid depth [m] — matches DayZ geo style


def _cluster_footprint_area(world_verts, normal):
    """Approximate in-plane area from OBB tangent extents."""
    N = mathutils.Vector(normal).normalized()
    up = mathutils.Vector((0.0, 0.0, 1.0))
    if abs(N.dot(up)) > 0.99:
        up = mathutils.Vector((1.0, 0.0, 0.0))
    U = (up - up.dot(N) * N).normalized()
    V = N.cross(U).normalized()
    u_projs = [v.dot(U) for v in world_verts]
    v_projs = [v.dot(V) for v in world_verts]
    return (max(u_projs) - min(u_projs)) * (max(v_projs) - min(v_projs))


def _centroid(verts):
    c = mathutils.Vector((0.0, 0.0, 0.0))
    for v in verts:
        c += v
    return c / len(verts)


def _shift_corners(corners, offset_vec):
    return [v + offset_vec for v in corners]


def split_to_minimal_components(obj, min_area_m2=0.5, min_thickness=MIN_THICKNESS):
    """
    Building-oriented split: wall/floor/ceiling bins, sorted by area.
    Largest cluster → primary component; neighbours get 1 mm normal offset.
    Returns list of 8-corner OBB lists (mathutils.Vector).
    """
    clusters = _cluster_by_wall_axis(obj, min_area_m2=min_area_m2)
    clusters = _merge_antiparallel_clusters(clusters)
    if not clusters:
        return []

    scored = []
    for world_verts, avg_normal in clusters:
        area = _cluster_footprint_area(world_verts, avg_normal)
        scored.append((area, world_verts, avg_normal))
    scored.sort(key=lambda x: x[0], reverse=True)

    primary_verts, primary_n = scored[0][1], scored[0][2]
    primary_centroid = _centroid(primary_verts)
    primary_n = primary_n.normalized()

    results = []
    primary_corners = _thicken_verts(primary_verts, primary_n, min_thickness)
    if len(primary_corners) == 8:
        results.append(primary_corners)

    for area, world_verts, avg_normal in scored[1:]:
        N = mathutils.Vector(avg_normal).normalized()
        corners = _thicken_verts(world_verts, N, min_thickness)
        if len(corners) != 8:
            continue

        c = _centroid(world_verts)
        to_primary = c - primary_centroid
        if to_primary.length <= NEARBY_M:
            outward = N.copy()
            if to_primary.dot(outward) < 0.0:
                outward = -outward
            corners = _shift_corners(corners, outward * OFFSET_M)

        results.append(corners)

    return results


def generate_components_for_houses(obj, min_area_m2=0.5, min_thickness=MIN_THICKNESS):
    """Alias for split_to_minimal_components — building preset."""
    return split_to_minimal_components(obj, min_area_m2, min_thickness)
