# CDM Collider

Blender-Addon für **DayZ Collision / Geometry LOD**.

Eigenständiges Addon (nicht die alte DayZ-Suite). Passt zu **CDM Architect** und **CDM P3D Studio**.

## Installation

1. [Release-ZIP](https://github.com/Crash357/CDM_Collider/releases) herunterladen (`cdm_collider_v1.0.3.zip`)
2. Blender **4.2+** → *Edit → Preferences → Get Extensions → Install from Disk*
3. Für V-HACD: mitgelieferte `TestVHACD.exe` (oder eigener Pfad in den Preferences)
4. Für die C# GeoEngine: [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Handbuch

Schritt-für-Schritt mit Screenshots (Cube + Rock). Repo ist privat — eingeloggt öffnen:

- **Deutsch:** https://github.com/Crash357/CDM_Collider/blob/main/docs/handbuch/de/README.md
- **English:** https://github.com/Crash357/CDM_Collider/blob/main/docs/handbuch/en/README.md

## Funktionen

- **Faces → AABB** — enge Box um die Auswahl, 1 mm Skin
- **Vertices → Hull** — Convex Hull, 1 mm Skin (keine 15-cm-Platte)
- **Vertex Groups → OBB / Hull**
- **V-HACD / CoACD** — Zerlegung in Convex Hulls (DayZ: max. 32 / 255 Verts)
- **Merge (Exact)** → Geometry LOD mit `ComponentXX`
- Viewport-Shading bleibt unverändert (Solid / Material / Rendered)

Gebäude-Auto-Geo ist **Beta-Test** und in den Preferences ausgeschaltet.

## Links

- YouTube: [Crash DayZ Modding](https://www.youtube.com/@crash_dayz_modding)
- Discord: https://discord.gg/9PM8BjWmp8

Lizenz: MIT
