# Faces → AABB

Enge **achsparallele Box** um die gewählten Faces. Dicke = Mesh-Span plus **1 mm je Seite** (keine 15-cm-Wandplatte).

Beispiel: **Cube** in der vorbereiteten Szene.

## Ablauf

1. Cube aktivieren, **Tab** → Edit Mode, Face-Select
2. Die Flächen markieren (hier: alle sechs Faces des Cubes)

![Faces in Edit Mode](../images/cube-faces.png)

3. N-Panel → **Faces → AABB**
4. In der Collection `GEO_Components` liegt `Component01` (cyan)

![AABB-Component](../images/cube-aabb.png)

Die Box sitzt 1 mm über den Faces. Face-Normalen zeigen **nach außen** (DayZ Geometry LOD).

Danach: [Merge → Geometry LOD](Merge-Geometry-LOD.md).

## Wann Hull statt AABB?

AABB ist achsparallel — bei schrägen Dächern oder gedrehten Wänden besser **[Verts → Hull](Verts-Hull.md)**. Unregelmäßige Props (Felsen): **[V-HACD](VHACD-und-CoACD.md)**.
