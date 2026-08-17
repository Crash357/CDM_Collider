"""CDM Collider — building decompose (delegiert an mesh-driven building_geo)."""

from .building_geo import generate_phase1_boxes, generate_phase2_boxes

from .islands import collect_closed_island_meshes



SKIN_M = 0.001





def decompose_building_phase1(obj):

    meshes, stats = collect_closed_island_meshes(obj, evaluated=True)

    stats['phase1_total'] = len(meshes)

    return meshes, stats





def decompose_building_phase1_boxes(obj, margin=SKIN_M,

                                    angle_threshold_deg=30.0,

                                    min_area_m2=0.25):

    _ = margin

    return generate_phase1_boxes(

        obj, angle_threshold_deg=angle_threshold_deg, min_area_m2=min_area_m2)





def closed_islands_to_boxes(*_args, **_kwargs):
    return []


def decompose_building_phase2(obj, min_area_m2=0.25, angle_threshold_deg=30.0,

                              skin_margin=SKIN_M):

    _ = skin_margin

    return generate_phase2_boxes(

        obj, min_area_m2=min_area_m2, angle_threshold_deg=angle_threshold_deg)





def decompose_building_hybrid(obj, min_area_m2=0.25, angle_threshold_deg=30.0,

                              skin_margin=SKIN_M):

    boxes1, s1 = decompose_building_phase1_boxes(

        obj, skin_margin, angle_threshold_deg=angle_threshold_deg,

        min_area_m2=min_area_m2)

    boxes2, s2 = decompose_building_phase2(

        obj, min_area_m2, angle_threshold_deg, skin_margin)

    stats = {**s1, **s2, 'phase1_total': len(boxes1), 'phase2_total': len(boxes2)}

    return boxes1, boxes2, stats


