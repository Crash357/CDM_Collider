# DayZ-Regeln

## Limits

| Regel | Wert |
|---|---|
| Components / Convex Hulls | max. **32** |
| Vertices pro Hull | max. **255** |
| Geometry LOD | geschlossene Convex-Teile, Vertex Groups `ComponentXX` |

**DayZ Check** im N-Panel prüft das am aktiven Geometry-Objekt.

## Normalen

Faces müssen **nach außen** zeigen (weg vom Component-Mittelpunkt). Faces→AABB und Verts→Hull machen das automatisch.

Im Viewport: Overlay **Face Orientation** — außen blau/grün, innen rot. Rot an der Außenseite = falsch rum.

## Skin

Auswahl-Tools legen **1 mm** Überstand an (2 mm bei einer flachen Fläche). Keine 15-cm-Mindestwand mehr.

## Viewport

Assignen stellt Solid/Material/Rendered **nicht** um. Farben liegen am Objekt und am Debug-Material (`cdm_comp_mat` / `cdm_geo`).

## Experimentell

**Gebäude Geo LOD** in den Preferences bleibt aus, bis die Auto-Pipeline für euer Mesh sitzt. Produktionsweg: manuell AABB/Hull → Merge.
