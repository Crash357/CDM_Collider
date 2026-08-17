"""CDM Shell Engine — closed meshes + DayZ wall boxes."""
from .building_decompose import decompose_building_hybrid


def build_shell_boxes_from_objects(objects, min_area=1.0,
                                   angle_threshold_deg=30.0):
    all_closed = []
    all_boxes = []
    for obj in objects:
        closed, boxes, stats = decompose_building_hybrid(
            obj, min_area_m2=min_area,
            angle_threshold_deg=angle_threshold_deg)
        all_closed.extend(closed)
        all_boxes.extend(boxes)
    mode = "DayZ ({} closed + {} wall boxes)".format(
        len(all_closed), len(all_boxes))
    return all_closed, all_boxes, mode
