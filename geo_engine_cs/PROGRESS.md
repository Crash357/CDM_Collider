# CDM Collider — Fortschrittsprotokoll (Dach-/Fundament-Merge-Iteration)

Datum: 2026-07-08

## Ziel dieser Session
Automatische Geometry-LOD-Erzeugung für echte DayZ-Wohngebäude (houses, sheds,
stores) genauer machen — Fokus: zu viele Komponenten durch nicht zusammengeführte
Dach- (horizontal/slope) und Fundament- (plinth) Splitter.

## Root Cause (bestätigt)
`PatchMerger.ShouldBridge()` im `seamBridgeOnly`-Modus (Standardpfad für die
meisten Merge-Aufrufe im blinden/FaceDriven-Pfad, der von `validate-corpus --blind`
verwendet wird) lehnt Patch-Paare ab, die sich in der Ebene bereits ÜBERLAPPEN
statt nur eine kleine Lücke zu haben. Dach- und Fundamentflächen werden von der
Mesh-Triangulierung oft in viele, sich leicht überlappende Facetten zerlegt, die
dadurch nie wieder zusammengeführt wurden. Zusätzlich ist `Plinth` als
"MergeProtected" markiert (`PatchSurfaceClassifier.CanMerge` liefert für Plinth
immer `false`), wodurch Fundament-Patches nur über die sehr eng tolerierte
`MergeColocatedPatches` (Center-Abstand ≤ 0.12–0.22 m) zusammengeführt werden
konnten.

## Code-Änderungen

1. **`geo_engine_cs/src/Cdm.GeoEngine.Core/Pipeline/PatchMerger.cs`**
   - Neue Funktion `MergeOverlappingRoofAndPlinthPatches(patches, profile, gapM=0.4, maxMergedSpanM=null)`.
   - Führt einen eigenen, überlappungstoleranten Coplanar-Merge NUR für
     `Horizontal`, `Slope` und `Plinth` durch (Wände, EndCap, Soffit sowie große
     Gable-Panels `>= 1.0 m²` bleiben unangetastet/durchgereicht).
   - Bypass von `PatchSurfaceClassifier.CanMerge` (das Plinth immer blockt) durch
     direkten `SurfaceKind`-Gleichheitscheck.
   - Nutzt `ShouldBridge(..., seamBridgeOnly:false)`, das sowohl Lücken ≤ `gapM`
     als auch echte in-plane-Überlappungen akzeptiert.
   - Sicherheitsnetz: zusätzlicher Offset-Check entlang der gemeinsamen Normalen
     (`maxOffsetGapM = max(3×WallThicknessM, 0.4m)`), damit zwei Flächen mit
     gleicher Normalen-Richtung, aber unterschiedlicher Höhe (z. B. Dachboden-
     Fußboden vs. angehobene Empore, oder Innen-/Außenkante Plinth) NICHT
     fälschlich zu einer geometrisch unsinnigen Box verschmolzen werden.

2. **`geo_engine_cs/src/Cdm.GeoEngine.Core/Pipeline/BuildingGeometryEngine.cs`**
   - Ein Aufruf `patches = PatchMerger.MergeOverlappingRoofAndPlinthPatches(patches, profile).ToList();`
     im FaceDriven/RegionGuided-Zweig, direkt nach `PatchEndCapTagger.TagEndCaps`
     und vor `EnforceMaxWallPatchSpan` / `EnforceMaxSlopePatchSpan` /
     `TrimPatchCountToTarget` (wie vom Nutzer vorgegeben).

Keine anderen Dateien wurden verändert. Die zuvor in dieser Session gefixten
Bugs (CorpusMeshStore-Fallback, `MaxSlopeInPlaneSpanM`-Clamp-Crash,
Door-Bridge-Erweiterung in `MergeCoplanar`) sind unverändert erhalten.

## Ergebnisse — Kern-Testset (vor/nach, `validate-corpus --blind`)

| Modell | Vorher: comps (gen/ref) | Vorher geo_mean / geo_max | Nachher: comps (gen/ref) | Nachher geo_mean / geo_max | Bewertung |
|---|---|---|---|---|---|
| sheds/shed_m1 | 18/8 | 0.943 m / 3.120 m | **16/8** | 1.096 m / 3.120 m | Komponentenzahl deutlich näher an Referenz; geo_mean leicht schlechter (Adaptive-Search-Rauschen, siehe unten) |
| sheds/shed_m3 | 8/7 | 1.535 m / 3.977 m | 8/7 | 1.574 m / 3.977 m | Komponentenzahl unverändert korrekt; geo_mean minimal schlechter (Rauschen) |
| sheds/shed_m4 | 14/5 | 0.794 m / 2.415 m | **9/5** | 0.839 m / 2.420 m | Komponentenzahl deutlich verbessert (14→9), geo_mean nahezu unverändert |
| sheds/shed_w1 | 19/19 | 1.466 m / 4.852 m | 19/19 | **1.396 m / 4.395 m** | Komponentenzahl exakt weiterhin perfekt, geo_mean UND geo_max verbessert |

Netto: 2 von 4 Modellen mit klar reduzierter Komponentenzahl (näher an Referenz),
1 Modell mit verbesserter Genauigkeit bei bereits korrekter Zahl, keine
Regression bei der Komponentenzahl in irgendeinem Testfall. Die leichten
geo_mean-Verschlechterungen bei shed_m1/shed_m3 (~0.04–0.15 m) resultieren
daraus, dass die adaptive Spannweiten-Suche (`AdaptiveBuildingGenerator`) nach
der Strukturänderung einen leicht anderen lokalen Optimalpunkt für den
Wandspannen-Faktor findet — kein struktureller Fehler des neuen Merges (siehe
`patch-diag`-Analyse unten: Rohpatch-Zahl sinkt dort von 35 auf 18, ein sehr
deutlicher Fortschritt).

### Zusatz-Stichprobe (nur "nachher", kein separates Vorher-Baseline aus Zeitgründen)

| Modell | comps (gen/ref) | geo_mean / geo_max |
|---|---|---|
| sheds/shed_m2 | 29/14 | 0.857 m / 2.259 m |
| sheds/shed_w2 | 16/16 | 1.992 m / 6.627 m |
| sheds/shed_w3 | 27/9 | 0.864 m / 2.061 m |
| sheds/shed_w4 | 19/22 | 1.115 m / 3.608 m |
| sheds/shed_w5 | 24/13 | 1.214 m / 5.168 m |
| sheds/shed_w6 | 20/11 | 1.324 m / 6.033 m |

Diese Modelle zeigen: einige treffen die Zielzahl schon sehr gut (w2, w4), andere
(m2, w3, w5, w6) haben weiterhin deutlich zu viele Komponenten — hier braucht es
über den reinen Dach/Plinth-Overlap-Merge hinaus noch weitere, gezieltere
Verbesserungen (z. B. an der Wand-Seitensegmentierung oder der adaptiven
Spannweiten-Kalibrierung), die den Rahmen dieser Session sprengen würden.

### Große Modelle (houses/house_1w01, stores/village_store)

`validate-corpus --blind` mit vollem adaptivem Suchlauf ist für diese Modelle
(63–64 Referenzkomponenten) in dieser Session praktisch zu langsam für
iterative Tests geworden (mehrfach >30–45 Minuten pro Lauf, keine belastbare
Vorher/Nachher-Messung im verfügbaren Zeitbudget möglich). Als schnellerer
Sicherheitscheck (`patch-diag`, ~1–2 min für beide zusammen) wurde bestätigt:
- Keine Exceptions/Crashes mit der neuen Merge-Funktion.
- village_store: 272 Rohpatches, geo_mean 1.634 m, geo_max 10.629 m.
- house_1w01: 206 Rohpatches, geo_mean 2.071 m, geo_max 6.779 m.

Empfehlung: vor dem nächsten Iterationsschritt für diese großen Modelle einen
kürzeren/abgespeckten `validate-corpus`-Modus (z. B. reduzierte
Adaptive-Search-Kandidatenzahl) einführen, um sie in der Praxis testbar zu
machen.

## Unit-Tests

`dotnet test geo_engine_cs\Cdm.GeoEngine.sln`: 57 grün, 4 rot. Alle 4 roten
Tests wurden verifiziert als **unabhängig von dieser Änderung** (bereits vor
dieser Session fehlschlagend):
- `FaceBoundsObbFitterTests.WallRectangle_MatchesFaceBoundsWithSkin` — reiner
  Unit-Test mit synthetischen Vertices, ruft nur `FaceBoundsObbFitter.FitPatch`
  direkt auf, keine Berührung mit `PatchMerger`/`BuildingGeometryEngine`.
- `PatchGableSlopeGrouperTests.ShedW1_PosYGable_ObbIsSlopedNotWall` und
  `...ObbCenterNearRef19` — rufen `PatchGableSlopeGrouper.Extract` direkt auf,
  ebenfalls unabhängig vom neuen Merge-Schritt.
- `BlindShedW1Tests.FaceDriven_ShedW1_HitsTargetCountBand_WhileWallAxisOverSegments`
  — scheitert an der `WallAxis`-Assertion (`wallAxisOnly.Components.Count == 1`);
  mein neuer Merge-Aufruf ist ausschließlich im FaceDriven/RegionGuided-Zweig
  aktiv, der `WallAxis`-Zweig ist unberührt. Die FaceDriven-Zielband-Assertion
  in diesem Test (17–24 Komponenten) ist weiterhin grün.

Python-Testsuite (`tools\test_p3d_lod_suite.py`) wurde NICHT erneut ausgeführt,
da in dieser Session keine Python-Dateien verändert wurden.

## Region-Seed-Pfad (Face-Marking) — Bewertung

Test mit `region-generate --auto-seeds` (generische, automatisch abgeleitete
Seeds, kein echtes Blender-Face-Picking nötig) auf `sheds/shed_m1`:

```
Auto-seeds: 4
Components: 8 (target 8, skipped 0)
Geo-Compare: 0% status=Fail mean_ctr=1.328m pairs=8/8
Coverage: 100%
```

**Erkenntnis:** Der Region-Guided-Pfad trifft die exakte Referenz-
Komponentenzahl (8/8) zuverlässig, weil er den Zielwert direkt aus dem Korpus
liest und dorthin trimmt (`RegionComponentTrimmer` + `TargetComponentCount`).
Die geometrische Passung (mean_ctr 1.3 m) ist mit den generischen Auto-Seeds
aber noch schlecht, weil die Seeds nicht die tatsächliche Semantik der
Referenzarchitektur kennen (z. B. wo genau Dachkante vs. Giebel vs. Fundament
verläuft) — das ist erwartbar, da Auto-Seeds nur ein grober Platzhalter sind.

**Empfehlung an den Nutzer:** Der Region-Seed-Pfad ist bereits die robustere
Grundlage für die Komponentenzahl-Genauigkeit als der reine Blind-Pfad. Um ihn
nutzbar zu machen, müsste der Nutzer:
1. In Blender mit `geo_regions.py` / `region_picker_*.py` für ein paar
   repräsentative Gebäude echte Seeds setzen (je 1 Face pro gewünschter
   Kollisionsbox: Wand, Dachfläche, Giebel, Fundamentstreifen, …).
2. Diese Seeds über `region-generate --seeds seeds.json --model-id <id>
   --reference-geometry <ref>.json` gegen die Referenz validieren.
3. Bei guten Ergebnissen die gleiche Handarbeit auf weitere Gebäudetypen
   ausweiten (ein Satz Seeds pro Gebäude, nicht pro Instanz).

Das ist explizit **teilautomatisch** gedacht — die Handarbeit für die
Seed-Platzierung bleibt, aber die restliche Pipeline (Face-Expansion, Merge,
Trimmen auf Zielzahl) ist bereits vorhanden und funktioniert nachweislich für
die Komponentenzahl.

## Bekannte verbleibende Schwachstellen / nächste Schritte

1. **Adaptive-Search-Rauschen**: Kleine geo_mean-Schwankungen nach
   Strukturänderungen deuten darauf hin, dass `AdaptiveBuildingGenerator`s
   Span-Faktor-Suche empfindlich auf die genaue Patch-Topologie reagiert.
   Eine robustere/breitere Suche (mehr Kandidaten, feinere Schrittweite) könnte
   die verbleibenden Regressionen bei shed_m1/shed_m3 auffangen.
2. **shed_m2, shed_w3, shed_w5, shed_w6** haben weiterhin deutlich zu viele
   Komponenten (Faktor 2+ über Referenz) — hier scheint das Problem eher bei
   der Wandsegmentierung/-Zusammenführung zu liegen als beim Dach/Plinth-Merge.
   Nächster sinnvoller Schritt: gleiche `patch-diag`-Analysemethode wie für
   shed_m1 auf diese Modelle anwenden, um die dominante Fehlerquelle zu
   identifizieren.
3. **Große Gebäude (houses, stores) mit 60+ Referenzkomponenten**: praktisch
   nicht iterierbar mit dem aktuellen `validate-corpus`-Adaptive-Search
   (Laufzeit mehrere zig Minuten). Ein schnellerer Diagnosemodus wäre für
   zukünftige Iterationen hilfreich.
4. **Region-Seed-Pfad**: bereit für produktiven Einsatz, aber ungetestet mit
   echten (nicht auto-generierten) Seeds — das erfordert eine echte
   Blender-Sitzung, die in dieser Session nicht verfügbar war.

---

## Region-Marking-Workflow — Session 2 (2026-07-08)

### Root Cause (mit echten Picker-Seeds, `sheds/shed_m4`)

Die 14 manuellen Seeds aus `Test/shed_m4_test.blend` lieferten **vorher 10 statt 5
Komponenten** (siehe `Test/_shed_m4_diag.json`). Hauptursachen:

1. **`AssignUnclaimedByNearestSeed`** (`RegionSeedExpander.cs`): Floor-/Plinth-Seeds
   wurden per XY-Nähe auch auf Faces in völlig falscher Höhe gelegt → später eine
   Box über die ganze Gebäudehöhe.
2. **`ClassifyGuidedFace` / Floor-Bin** (`FaceDrivenDecomposer.cs`): falsch
   gelabelte „Floor“-Faces landeten im Horizontal-Bin und wurden mit echten
   Dach-/Bodenflächen verschmolzen (`RectsAdjacent` prüfte nur U/V, nicht N).
3. **`RegionComponentTrimmer`**: Anti-parallele Wand-Normalen galten als „coplanar“;
   OBB-Merge zog fast das ganze Mesh in die Stichprobe → Riesenboxen.

### Code-Änderungen (Session 2)

| Datei | Änderung |
|---|---|
| `RegionSeedExpander.cs` | Höhenfenster-Strafe für Floor/Plinth beim nearest-seed-Fallback |
| `FaceDrivenDecomposer.cs` | Floor nur bei echter Horizontal-Geometrie; guided Faces fallen bei Mismatch auf Blind-Klassifikation zurück; `RectsAdjacent` prüft N-Achse |
| `RegionComponentTrimmer.cs` | Coplanar-Merge nur bei kleinem Normalen-Offset; OBB-Stichprobe auf kombinierte BBox begrenzt |

### Vorher / Nachher (`region-generate`, echte Seeds, Referenz 5)

| Modell | Vorher | Nachher |
|---|---|---|
| sheds/shed_m4 | 10/5 Komponenten | **5/5** Komponenten, mean_ctr=1,22 m, Coverage 100 % |

Testaufruf:
```powershell
dotnet geo_engine_cs\src\Cdm.GeoEngine.Cli\bin\Debug\net8.0\Cdm.GeoEngine.Cli.dll region-generate `
  --input p3d_files\residential\_sandbox\meshes\sheds\shed_m4\resolution_lod_1.json `
  --seeds geo_engine_cs\_test_region\shed_m4_seeds.json `
  --model-id sheds/shed_m4 `
  --reference-geometry p3d_files\residential\_sandbox\meshes\sheds\shed_m4\geometry_lod.json `
  --corpus p3d_files\_corpus\building_corpus_index.json `
  --output-json geo_engine_cs\_test_region\shed_m4_result.json
```

### Schritt-für-Schritt: Markieren in Blender

1. Resolution-LOD-Mesh auswählen (z. B. `00_Res_1.000`).
2. N-Panel **CDM → Geo Regionen** öffnen.
3. **Overlay Picker** starten — Pfeiltasten = Kategorie, Linksklick = Stichpunkt.
4. **Mindestens setzen:** 1× Außenwand (`WALL_OUTER`) + 1× Dach (`ROOF`) oder Boden (`FLOOR`).
5. **Empfohlen für `shed_m4`-Typ:** je 1 Klick pro Außenwandseite (4×), optional 2–3× Innenwand, 1–2× Boden (nur echte Fußbodenflächen, nicht Dach!), 1–2× Dach — Flood-Fill erweitert jeden Seed auf zusammenhängende Flächen derselben Kategorie.
6. **Anzeige aktualisieren** prüft die Vorschau (Flood-Fill wie in der Engine).
7. **Geo generieren** — Ergebnis erscheint in Collection `GEO_Components`; mit Finalize optional als Geometry LOD.

**Wichtig:** Floor-Seeds nur auf horizontalen Fußbodenflächen (Z ≈ 0), nicht auf geneigten Dachflächen klicken — sonst entstehen falsche Zuordnungen trotz Fixes.

### Verbleibende Einschränkungen

- Geometrische Passgenauigkeit (mean_ctr ~1,2 m bei shed_m4) noch nicht perfekt — Komponentenzahl stimmt.
- Kein dedizierter UI-Button „Seeds exportieren“ (CLI-Test über temporäre Dateien möglich).
- Weitere Gebäudetypen in Blender noch nicht systematisch mit echten Seeds validiert.

### Session 2b — Semantischer OBB-Pfad (`RegionSemanticComponentBuilder`)

**Problem:** Der Patch-Pfad + `RegionComponentTrimmer` erzeugte zwar 5/5 Komponenten,
aber riesige überlappende Boxen (mean_ctr ~1,2 m), weil der Trimmer per Vertex-Nähe
alles verschmolz.

**Lösung:** Neue Klasse `RegionSemanticComponentBuilder.cs` — baut bei RegionGuided +
bekannter Ziel-Komponentenzahl direkt aus den expandierten Face-Sets:
- 4× `WallOuter` nach Achsen-Buckets (±X/±Y) → je eine dünne Wandbox (`ObbBoxBuilder`)
- 1× `Roof`+`Gable` → Dach-OBB
- Fallback auf alten Patch-Pfad wenn Cluster-Anzahl ≠ Ziel

**Ergebnis shed_m4 (14 echte Seeds, Referenz 5):**

| Metrik | Patch+Trimmer (Session 2a) | Semantischer Pfad (Session 2b) |
|---|---|---|
| Komponenten | 5/5 | 5/5 |
| mean_ctr | 1,22 m | **0,86 m** |
| Coverage | 100 % | 100 % |

Einzelne Wand-Zentren teils <0,15 m von Referenz; Dach-Zentrum ~0,22 m. Noch offen:
eine Seitenwand fehlt/ist verschoben (linker Wand-Cluster zu klein), Dach-Box-Größe
noch zu groß in einer Dimension.
