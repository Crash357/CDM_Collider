# V-HACD and CoACD

Automatic split of irregular meshes into convex hulls. Example: **Rock**.

For building walls and the Cube, use [Faces → AABB](Faces-AABB.md) / [Verts → Hull](Verts-Hull.md) first.

## V-HACD (Rock)

1. Activate the Rock (Object Mode)
2. Auto-generation: method **VHACD**
3. DayZ-safe: Max Hulls **32**, Max Verts **255** (example run: 64 verts, 200 000 resolution)

![V-HACD in the N-panel](../images/rock-panel.png)

4. **1. Decompose** — needs `TestVHACD.exe` (bundled in the ZIP)
5. Cyan: individual hulls in `GEO_Components` (`Component01` …)

![V-HACD hulls](../images/rock-hulls.png)

6. **2. Merge Hull** → green `Geometry` LOD

![Geometry LOD from the rock](../images/rock-geometry.png)

EXE path: Preferences → CDM Collider → TestVHACD.exe

If **Merge → Geometry Collection** is on, merge may run right after decompose. Components stay in `GEO_Components` for inspection.

## CoACD

1. Method **CoACD**
2. Leave **DayZ Preset** on (32 hulls, sensible defaults)
3. **Decompose**, then **Merge Hull**

CoACD ships as a wheel in the ZIP (`bundled/`). On first run the add-on installs the dependency if needed.
