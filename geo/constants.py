"""CDM Collider — shared constants."""

BOX_FACES = (
    (0, 1, 3, 2), (4, 6, 7, 5),
    (0, 4, 5, 1), (2, 3, 7, 6),
    (0, 2, 6, 4), (1, 5, 7, 3),
)

# AABB face winding (collider_tools / box_creation order)
AABB_BOX_FACES = (
    (0, 1, 2, 3),
    (4, 7, 6, 5),
    (0, 4, 5, 1),
    (1, 5, 6, 2),
    (2, 6, 7, 3),
    (4, 0, 3, 7),
)

AABB_PADDING_M = 0.001  # 1 mm clearance (Faces→AABB and Vertices→Hull)
# Legacy wall-slab clamp — do not use for selection tools.
MIN_AABB_DIM_M = 0.15
