# N-panel

Press **N** in the 3D Viewport, tab **CDM**, section **CDM Collider**.

![Collider panel](../images/npanel.png)

## Target

Optionally pin a mesh when nothing is selected. Otherwise the active object is used.

## Manual

For faces/verts: press **Tab** first (Edit Mode).

| Button | What it does |
|---|---|
| **Islands → Components** | Closed mesh islands as components |
| **Merge → Geometry LOD** | Merge components into one `Geometry` object |
| **Tag as Geo LOD** | Set DayZ Geometry LOD tags |
| **Faces → AABB** | Axis-aligned box around selected faces, 1 mm skin |
| **Verts → Hull** | Convex hull around selected vertices, 1 mm skin |
| **Fill Holes** | Close holes |
| **Open Islands** | Select open islands |

## Display

- **Geometry** (green) / **Components** (cyan)
- **An** — apply colours to existing geo
- **Aus** — reset overlay

The add-on does **not** switch Solid / Material Preview / Rendered.

## Auto-generation

Pick a method (OBB, HULL, **VHACD**, CoACD, …), then **1. Decompose** and **2. Merge**. See [V-HACD and CoACD](VHACD-and-CoACD.md).

**Gebäude — Blind generieren** only appears when **Gebäude Geo LOD (Beta-Test)** is enabled in Preferences. Default: off.

## Info & Check

**DayZ Check** validates component count (≤ 32) and verts per hull (≤ 255).
