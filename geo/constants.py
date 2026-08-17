"""CDM Collider — shared constants."""

BOX_FACES = (
    (0, 1, 3, 2), (4, 6, 7, 5),
    (0, 4, 5, 1), (2, 3, 7, 6),
    (0, 2, 6, 4), (1, 5, 7, 3),
)

# AABB face winding — outward normals (DayZ Geometry / Fire LOD).
AABB_BOX_FACES = (
    (0, 3, 2, 1),
    (4, 5, 6, 7),
    (0, 1, 5, 4),
    (1, 2, 6, 5),
    (2, 3, 7, 6),
    (4, 7, 3, 0),
)

AABB_PADDING_M = 0.001  # 1 mm clearance (Faces→AABB and Vertices→Hull)
# Legacy wall-slab clamp — do not use for selection tools.
MIN_AABB_DIM_M = 0.15

# AABB corners: 0–3 bottom z0 (x0y0, x1y0, x1y1, x0y1), 4–7 top z1.
# Winding is CCW from outside so DayZ Geometry LOD normals point OUT.
