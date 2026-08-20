# CDM Collider

[![Donate](docs/donate.svg)](https://paypal.me/crash12345?country.x=DE&locale.x=de_DE)

Blender-Addon für **DayZ Collision / Geometry LOD**.

Eigenständiges Addon (nicht die alte DayZ-Suite). Passt zu **CDM Architect** und **CDM P3D Studio**.

## Installation

1. [Release-ZIP](https://github.com/Crash357/CDM_Collider/releases) herunterladen (`cdm_collider_v1.0.3.zip`)
2. Blender **4.2+** → *Edit → Preferences → Get Extensions → Install from Disk*
3. Für V-HACD: mitgelieferte `TestVHACD.exe` (oder eigener Pfad in den Preferences)
4. Für die C# GeoEngine: [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Handbuch

Wiki (eingeloggt, Repo ist privat):

**https://github.com/Crash357/CDM_Collider/wiki**

- Deutsch: https://github.com/Crash357/CDM_Collider/wiki
- English: https://github.com/Crash357/CDM_Collider/wiki/Home-EN

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

## Support

You are permitted to get a copy from this GitHub page and everything specified in the attached license ([GPL 3.0](LICENSE)). Creating add-ons is a lot of effort and work. The licensing and contribution of future add-ons will also depend on the fairness of all of you.

Alternatively, you can make a donation via PayPal:

[![Donate](docs/donate.svg)](https://paypal.me/crash12345?country.x=DE&locale.x=de_DE)

Lizenz: **GPLv3 (oder später)**. Mitgeliefert: V-HACD (BSD 3-Clause), CoACD (MIT). Nicht von Bohemia Interactive.
