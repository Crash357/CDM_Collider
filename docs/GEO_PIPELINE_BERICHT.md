# Geo-Pipeline — Arbeitsbericht & Fazit

> Historischer Session-Bericht aus der **ungeteilten** DayZ-Suite (Addon 1.9.35).
> Aktuelles Produkt in diesem Repo: **CDM Collider 1.0.3**. Die Suite bleibt aufgeteilt
> (Architect / Collider / P3D Studio) — dieser Text ist Archiv, keine Produktbeschreibung.

**Stand:** 17. Juni 2026  
**Addon-Version damals:** 1.9.35 (`bl_info` / `blender_manifest.toml`)  
**Workspace damals:** `CDM_Blender_DayZ-Suite`

---

## Kurzfazit

Die **Region-Guided Geo-Pipeline mit Picker-Markierungen** ist technisch deutlich ausgebaut (C#-Engine, Seed-Normalisierung, Component-Trimmer, Merge, Mass, UI-Stabilität), aber das **praktische Ergebnis für den Nutzer bleibt sehr schlecht**: Markierte Seeds liefern in realen Blendfiles oft falsche Component-Zählungen, schlechte Box-Fits oder unbrauchbares Geometry-LOD — obwohl Sandbox-Benchmarks auf ausgewählten Corpus-Modellen teils „PASS“ melden.

**Blind generieren** (C# Heuristik ohne Markierungen) kann auf kleinen Sheds funktionieren; **markierungsgeführt** war das Ziel dieser Session und ist **noch nicht produktionsreif**.

---

## Ziel der Session

1. Region-Guided Pipeline mit **Overlay-Picker-Seeds** (nicht blind)
2. Component-Count an Referenz/Corpus angleichen
3. End-to-End: Seeds → Components → Merge → Geometry LOD
4. Stabilität (Timer/Crash, N-Panel, Bugbot-Findings)
5. Test-Blendfiles im `Test/`-Ordner

---

## Was implementiert / repariert wurde

### C# GeoEngine (`geo_engine_cs`)

| Bereich | Änderung |
|---------|----------|
| `RegionSeedNormalizer.cs` | Picker-Seeds normalisieren, Duplikate/Noise reduzieren |
| `RegionComponentTrimmer.cs` | Component-Count auf Ziel (Corpus/Referenz) trimmen/mergen |
| `BuildingGeometryEngine.cs` | RegionGuided-Pfad mit Seed-Policy |
| CLI / Export | `face_index: -1` für triangulierte Meshes |

### Blender-Python

| Bereich | Änderung |
|---------|----------|
| `geo/cs_engine_bridge.py` | Finalize-Outcome, RegionGuided, Partial-Success bei fehlgeschlagenem Merge |
| `geo/auto_build.py` | Merge für SPLIT/SHELL/VHACD/CoACD; SHELL `comp_idx`-Bug; OBB ≠ Blind-Heuristik |
| `geo/merge.py` | Component01-Naming, automatische Vertex-Masse |
| `geo/selection_aabb.py` | Edit-Mode-Rückkehr, lokale AABB, Mindestkante 15 cm |
| `p3d_io/` | Timer-Safe Collections, Mass `cdm_mass` / FHQWeights-Migration |
| `operators.py` | Engine-Busy-Guards, DayZ-Check per Component-VG, VHACD/CoACD-Merge |
| `lod_generator.py` | Limit-Check, Res-1.0-Erkennung, Custom-Presets in JSON (User-Config) |

### UI / Stabilität

- N-Panel-Crash: `selection_aabb.py` Syntaxfehler → Addon lud nicht (behoben)
- Modal-Operator `building_auto_geo`: kein geteilter Klassenstatus
- Custom LOD-Presets: `%APPDATA%\Blender Foundation\Blender\5.x\config\cdm_blender_dayz_suite\lod_custom_presets.json`

### Deploy

- Sync nach Blender 5.0 / 5.1 Extensions (`tools/deploy_to_blender.ps1`)

---

## Test- & Benchmark-Ergebnisse (automatisierbar)

| Modell / Tier | Ergebnis | Hinweis |
|---------------|----------|---------|
| Sandbox Sheds (10 Modelle) | PASS (Gate smoke) | Corpus-JSON, kontrollierte Umgebung |
| `shed_m4` (Corpus) | 5/5 Components, Score ~98% | **Corner-Errors 0,4–0,8 m** — formal OK, geometrisch schlecht |
| `mil_barracks_round` | 26/26 (Referenz-Runs) | Res-only Blend (`Test/mil_barracks_round_res_only.blend`) |
| `mil_barracks1` | 59/59 (Referenz-Runs) | größeres Gebäude |
| User `shed_m4_test` (14 Picker-Seeds) | vor Fix: 10 vs 5 Components | Nach C#-Trimmer verbessert, Nutzer-Blend ungleich Corpus-Export |
| `mil_guardhouse2` | FAIL (5/41) | große Gebäude, Trimmer nur reduziert |
| Blind `shed_w1` | PASS (1/1 Sandbox) | ohne Markierungen |

**Wichtig:** Sandbox „PASS“ = Component-Count ±12 %, Coverage ≥85 % — **kein** garantiertes visuelles/Gameplay-taugliches Geo.

---

## Bekannte Probleme (offen)

### Geo-Qualität (Hauptproblem)

- Picker-Seeds in Live-Blend ≠ Corpus-Training-Seeds (Export/Resolution-LOD-Unterschiede)
- Box-Fit (OBB) weit neben Referenz (`corner_error` Meter statt cm)
- Große Gebäude: Component-Explosion oder zu aggressive Reduktion
- Finalize (Intersect, Innenflächen) bricht sporadisch → nur `GEO_Components`, kein `Geometry`

### Tests & Umgebung

- Headless Blender mit vollem Addon-Load hängt (UVPackmaster / HardOps GPU)
- Live-Export Baracke headless: 53 vs 26 Components ≠ Corpus-JSON
- Outliner-Delete-Crash mit CollectionProperty (siehe `Test/shed_w1.crash.txt`)

### Produkt / UX

- Zwei parallele Workflows (Blind vs Region-Guided vs OBB Fine-Cluster) verwirren
- Erfolgsmeldungen obwohl Geometry-LOD fehlt (teilweise behoben, weiter beobachten)
- Region-Markierungen erzeugen falsche Erwartung („ich habe markiert, also muss es passen“)

---

## Test-Assets im Repo

| Datei | Zweck |
|-------|--------|
| `Test/shed_m4_test.blend` | User-Run RegionGuided, 14 Seeds |
| `Test/mil_barracks_round_res_only.blend` | nur `00_Res_1.000`, Collection Visuals |
| `Test/_shed_m4_diag.json` | Diagnose-Export |
| `tools/sandbox_region_benchmark.py` | Benchmark + Gate |
| `tools/sandbox_region_gate.py` | Smoke-Tier Gate |
| `tools/build_barracks_res_only_blend.py` | Res-only Blend bauen |

---

## Empfohlene nächste Schritte (nach Feierabend)

1. **Ein Referenz-Blend pro Problemfall** festlegen (Baracke, shed_m4 mit Seeds, guardhouse2) — gleiche Mesh-Quelle wie Picker (`00_Res_1.000`).
2. **Diagnose vor Generate:** Seeds exportieren + Component-Ziel aus Referenz-Geometry anzeigen (nicht nur Score).
3. **Fit-Metrik in UI:** max. Corner-Error / Coverage pro Component sichtbar machen (nicht nur PASS/FAIL).
4. **Große Gebäude:** Trimmer-Strategie (merge vs drop) an Referenz-OBBs koppeln, nicht nur Count.
5. **Sandbox vs Live:** Export-Pipeline angleichen (evaluated mesh, face_index, Welt vs lokal).
6. **Headless-Tests:** Addon-Load ohne GPU-Addons oder isolierter `geo_engine_cs` CLI-Test.

---

## Bugbot / Code-Qualität

Mehrere Bugbot-Runden durchlaufen. Behoben u. a.: Timer/Collection-RNA, Merge-Pfade, Mass-Export, Engine-Busy, CoACD-Limits, LOD-Preset-JSON, Syntax-Crash N-Panel.

Letzte bekannte Einschränkung: Geo-Qualität ist **kein** Bugbot-Thema — braucht Pipeline-/ML-ähnliche Iteration an Referenzdaten.

---

## Version-Historie (Session)

| Version | Inhalt (Auswahl) |
|---------|------------------|
| 1.9.29–1.9.32 | RegionGuided, Merge, Mass, Bugbot-Fixes |
| 1.9.33 | `selection_aabb` Syntaxfix (N-Panel wieder sichtbar) |
| 1.9.34 | LOD Generator Limit + Res-1.0-Erkennung |
| 1.9.35 | Custom LOD-Presets JSON in User-Config |

---

## Persönliches Fazit (Session)

> Viel Infrastruktur und Stabilität — aber **das eigentliche Versprechen** (markieren → gutes DayZ-Geo) ist **noch nicht erfüllt**. Sandbox-Zahlen täuschen Produktionsqualität vor. Nächste Session sollte weniger Feature-Breite, mehr **ein Blendfile end-to-end** mit messbarer Box-Qualität vs Referenz-Geometry priorisieren.

---

*Bericht erstellt/aktualisiert am Ende der Cursor-Session. Bei Fortsetzung: dieses Dokument fortschreiben, nicht duplizieren.*
