# Verts → Hull

**Convex hull** around the selected vertices. A coplanar selection (one face) becomes **2 mm** thick (±1 mm), not 15 cm.

## Steps

1. Edit Mode, vertex select (or faces — the verts come along)
2. Select the points of the collision surface
3. N-panel → **Verts → Hull**
4. The component lands in `GEO_Components` (cyan)

Normals point **outward**. Viewport shading is left unchanged.

For straight, axis-aligned walls (like the **Cube**) [Faces → AABB](Faces-AABB.md) is usually tighter. Use Hull for slopes, bays, irregular parts.

Organic meshes (like the **Rock**) should be split with [V-HACD](VHACD-and-CoACD.md), not one hull over the whole mesh.
