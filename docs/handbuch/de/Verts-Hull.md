# Verts → Hull

**Convex Hull** um die gewählten Vertices. Koplanare Auswahl (eine Fläche) wird **2 mm** dick (±1 mm), nicht 15 cm.

## Ablauf

1. Edit Mode, Vertex-Select (oder Faces — die Verts kommen mit)
2. Die Punkte der Collision-Fläche markieren
3. N-Panel → **Verts → Hull**
4. Component landet in `GEO_Components` (cyan)

Normalen zeigen **nach außen**. Viewport-Shading bleibt unverändert.

Für gerade, achsparallele Wände (wie den **Cube**) ist [Faces → AABB](Faces-AABB.md) meist enger. Hull für Schrägen, Erker, unregelmäßige Teile.

Organische Meshes (wie den **Rock**) zerlegt ihr mit [V-HACD](VHACD-und-CoACD.md), nicht mit einem einzigen Hull über das ganze Mesh.
