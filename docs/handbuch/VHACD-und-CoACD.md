# V-HACD und CoACD

Automatische Zerlegung unregelmäßiger Meshes in Convex Hulls (Felsen, Schrott, Props). Für Gebäudewände zuerst [Faces → AABB](Faces-AABB.md) / [Verts → Hull](Verts-Hull.md) nutzen.

## V-HACD

1. Methode **VHACD** im Abschnitt Auto-Generation
2. Max Hulls **32**, Max Verts **255** (DayZ)
3. **1. Decompose** — braucht `TestVHACD.exe` (liegt in der ZIP)
4. **2. Merge Hull** → Geometry LOD

Pfad zur EXE: Preferences → CDM Collider → TestVHACD.exe

## CoACD

1. Methode **CoACD**
2. **DayZ Preset** anlassen (32 Hulls, sinnvolle Defaults)
3. **Decompose**, dann **Merge Hull**

CoACD kommt als Wheel in der ZIP (`bundled/`). Beim ersten Lauf installiert das Addon die Abhängigkeit bei Bedarf.
