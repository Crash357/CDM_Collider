# N-Panel

Taste **N** im 3D-Viewport, Tab **CDM**, Abschnitt **CDM Collider**.

![Collider-Panel](images/npanel.png)

## Ziel

Optional ein Mesh festlegen, wenn nichts ausgewählt ist. Sonst gilt das aktive Objekt.

## Manuell

Für Faces/Verts: zuerst **Tab** (Edit Mode).

| Button | Funktion |
|---|---|
| **Islands → Components** | Geschlossene Mesh-Inseln als Components |
| **Merge → Geometry LOD** | Components zu einem `Geometry`-Objekt zusammenfügen |
| **Tag as Geo LOD** | DayZ Geometry-LOD-Tags setzen |
| **Faces → AABB** | Achsparallele Box um gewählte Faces, 1 mm Skin |
| **Verts → Hull** | Convex Hull um gewählte Vertices, 1 mm Skin |
| **Fill Holes** | Löcher schließen |
| **Open Islands** | Offene Inseln selektieren |

## Anzeige

- **Geometry** / **Components** — Viewport-Farbe
- **An** — Farben auf bestehende Geo anwenden
- **Aus** — Overlay zurücksetzen

Das Addon ändert **nicht** Solid / Material Preview / Rendered.

## Auto-Generation

Methode (OBB, HULL, VHACD, CoACD, …), dann **Decompose** und **Merge**. Siehe [V-HACD und CoACD](VHACD-und-CoACD.md).

Gebäude **Blind generieren** erscheint nur, wenn in den Preferences **Gebäude Geo LOD (experimentell)** an ist.

## Info & Check

**DayZ Check** prüft Component-Anzahl (≤ 32) und Verts pro Hull (≤ 255).
