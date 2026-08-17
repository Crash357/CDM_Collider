# DayZ rules

## Limits

| Rule | Value |
|---|---|
| Components / convex hulls | max. **32** |
| Vertices per hull | max. **255** |
| Geometry LOD | closed convex parts, vertex groups `ComponentXX` |

**DayZ Check** in the N-panel validates this on the active Geometry object.

## Normals

Faces must point **outward** (away from the component centre). Faces→AABB and Verts→Hull do this automatically.

In the viewport: overlay **Face Orientation** — outside blue/green, inside red. Red on the outside = flipped.

## Skin

Selection tools add **1 mm** of padding (2 mm on a flat face). There is no 15 cm minimum wall anymore.

## Viewport

Assign does **not** switch Solid / Material / Rendered. Colours live on the object and the debug material (`cdm_comp_mat` / `cdm_geo`).

## Beta test

**Gebäude Geo LOD** in Preferences is a **beta test** and stays **off** until the auto pipeline fits your mesh.

Production path:

- Cube / walls: manual AABB or Hull → Merge Exact
- Rock / props: V-HACD or CoACD → Merge Hull
