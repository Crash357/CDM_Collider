using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "collect" => RunCollect(args),
                "generate" => RunGenerate(args),
                "validate-doors" => RunValidateDoors(args),
                "merge-corpus" => RunMergeCorpus(args),
                "auto-generate" => RunAutoGenerate(args),
                "region-generate" => RunRegionGenerate(args),
                "compare-geometry" => RunCompareGeometry(args),
                "patch-diag" => RunPatchDiag(args),
                "ref-segment-stats" => RunRefSegmentStats(args),
                "validate-corpus" => RunValidateCorpus(args),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            return 2;
        }
    }

    private static string DefaultSandboxDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "p3d_files", "residential", "_sandbox"));

    private static string DefaultCorpusPath() =>
        Path.Combine(DefaultSandboxDir(), "building_corpus_index.json");

    private static string DefaultMeshStorePath() =>
        Path.Combine(DefaultSandboxDir(), "corpus_meshes_index.json");

    private static CorpusMeshStore? LoadMeshStore(string? manifestPath)
    {
        var path = manifestPath ?? DefaultMeshStorePath();
        return CorpusMeshStore.TryLoad(path);
    }

    private static int RunCollect(string[] args)
    {
        var inputDir = GetArg(args, "--input")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "geo_dump"));
        var output = GetArg(args, "--output")
            ?? Path.Combine(inputDir, "building_dataset.json");

        if (!Directory.Exists(inputDir))
            throw new DirectoryNotFoundException($"Input folder not found: {inputDir}");

        var datasets = DatasetCollector.CollectFromDirectory(inputDir);
        DatasetCollector.ExportDatasetJson(datasets, output);

        Console.WriteLine($"Collected {datasets.Count} building sample(s) from:");
        Console.WriteLine($"  {inputDir}");
        Console.WriteLine($"JSON written to:");
        Console.WriteLine($"  {output}");

        foreach (var ds in datasets)
        {
            var resV = ds.ResolutionLod?.VertexCount ?? 0;
            var geoV = ds.GeometryLod?.VertexCount ?? 0;
            var comps = ds.ReferenceComponents.Count > 0
                ? ds.ReferenceComponents.Count
                : ds.GeometryLod?.VertexGroups.Count ?? 0;
            Console.WriteLine(
                $"  - {ds.ModelName}: Res {resV}V, Geo {geoV}V, {comps} components, {ds.Doors.Count} door group(s)");
        }

        return 0;
    }

    private static int RunGenerate(string[] args)
    {
        var input = GetArg(args, "--input")
            ?? throw new ArgumentException("Missing --input (CDM building dump .txt or dataset JSON path).");
        var minArea = double.Parse(GetArg(args, "--min-area") ?? "0.25", System.Globalization.CultureInfo.InvariantCulture);
        var angle = double.Parse(GetArg(args, "--angle") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
        var output = GetArg(args, "--output");

        var mesh = input.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonMeshLoader.LoadResolutionFromFile(input)
            : CdmDumpParser.ParseMeshDump(File.ReadAllText(input));

        var result = BuildingGeometryEngine.Generate(mesh, new BuildingGeometryOptions
        {
            MinAreaM2 = minArea,
            AngleThresholdDeg = angle,
            RequireDoorVertices = false,
        });

        Console.WriteLine($"Generated Geometry LOD for '{mesh.Name}'");
        Console.WriteLine($"  Patches:    {result.Patches.Count}");
        Console.WriteLine($"  Components: {result.Components.Count} (skipped {result.SkippedPatches})");
        Console.WriteLine($"  Vertices:   {result.GeometryLod.VertexCount}");
        Console.WriteLine($"  Faces:      {result.GeometryLod.FaceCount}");

        if (output != null)
        {
            var report = BuildGenerationReport(result);
            File.WriteAllText(output, report);
            Console.WriteLine($"Report: {output}");
        }

        var jsonOut = GetArg(args, "--output-json");
        if (jsonOut != null)
        {
            ExportResultJson(result, jsonOut);
            Console.WriteLine($"JSON: {jsonOut}");
        }

        return 0;
    }

    private static int RunAutoGenerate(string[] args)
    {
        var input = GetArg(args, "--input")
            ?? throw new ArgumentException("Missing --input (resolution mesh JSON).");
        var modelId = GetArg(args, "--model-id") ?? "";
        var corpusPath = GetArg(args, "--corpus") ?? DefaultCorpusPath();
        var meshStorePath = GetArg(args, "--mesh-store");
        var refGeoPath = GetArg(args, "--reference-geometry");
        var blind = args.Any(a => string.Equals(a, "--blind", StringComparison.OrdinalIgnoreCase));
        var noSnap = args.Any(a => string.Equals(a, "--no-snap", StringComparison.OrdinalIgnoreCase));
        var jsonOut = GetArg(args, "--output-json")
            ?? Path.ChangeExtension(input, ".auto_result.json");

        var mesh = input.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonMeshLoader.LoadResolutionFromFile(input)
            : CdmDumpParser.ParseMeshDump(File.ReadAllText(input));

        CorpusMeshStore? meshStore = LoadMeshStore(meshStorePath);
        CorpusReference? reference = null;
        MeshData? referenceGeometry = null;
        if (!string.IsNullOrEmpty(refGeoPath) && File.Exists(refGeoPath))
            referenceGeometry = JsonMeshLoader.LoadGeometryFromFile(refGeoPath);

        if (File.Exists(corpusPath))
        {
            var corpus = BuildingCorpusReader.Load(corpusPath);
            var profile = BuildingMeshAnalyzer.Analyze(mesh);
            reference = !string.IsNullOrEmpty(modelId)
                ? CorpusReferenceLookup.TryGetById(corpus, modelId, meshStore)
                : CorpusReferenceLookup.TryFindByHint(corpus, mesh.Name, meshStore);

            if (reference == null)
            {
                var matched = CorpusFootprintMatcher.FindNearest(corpus, mesh, profile, meshStore);
                if (matched != null)
                {
                    reference = matched.Reference;
                    Console.WriteLine(
                        $"  CorpusMatch: {reference.ModelId} (score={matched.Score:F3}, "
                        + $"target={reference.GeometryComponentCount} comps)");
                }
            }

            modelId = reference?.ModelId ?? modelId;

            if (referenceGeometry == null && meshStore != null && !string.IsNullOrEmpty(modelId))
                referenceGeometry = meshStore.TryLoadGeometry(modelId);
        }

        // Blind = no reference OBB cheating; still uses corpus target component count.
        if (blind)
            noSnap = true;

        var adaptive = AdaptiveBuildingGenerator.GenerateAdaptive(
            mesh, reference, modelId, referenceGeometry, blindGeneration: blind, allowSnap: !noSnap);
        ExportAdaptiveJson(adaptive, jsonOut, blind, noSnap);

        if (adaptive.BuildingProfile != null)
        {
            var p = adaptive.BuildingProfile;
            Console.WriteLine($"  Building:   {p.SizeM.X:F2} x {p.SizeM.Y:F2} x {p.SizeM.Z:F2} m");
            Console.WriteLine($"  Footprint:  {p.FootprintAreaM2:F1} m²  wall={p.WallThicknessM:F3} m");
        }
        if (adaptive.ObbGeometry != null)
        {
            Console.WriteLine($"  OBB-Geo:    {adaptive.ObbGeometry.OverallScore:P0} "
                              + $"(ext={adaptive.ObbGeometry.ExtentScore:P0} rot={adaptive.ObbGeometry.RotationScore:P0})");
        }
        if (adaptive.GeometricCompare != null)
        {
            var g = adaptive.GeometricCompare;
            Console.WriteLine($"  Geo-Compare:{g.OverallScore:P0} status={g.OverallStatus} "
                              + $"max_corner={g.MaxCornerErrorM:F4}m mean_corner={g.MeanCornerErrorM:F4}m "
                              + $"pairs={g.MatchedPairs}/{g.ReferenceCount}");
        }
        if (adaptive.Coverage != null)
            Console.WriteLine($"  Coverage:   {adaptive.Coverage.FractionInside:P0} ({adaptive.Coverage.SamplesInside}/{adaptive.Coverage.SamplesTotal} faces)");
        if (adaptive.SearchQuality != null)
            Console.WriteLine($"  Search:     composite={adaptive.SearchQuality.Composite:P0} "
                              + $"(obb={adaptive.SearchQuality.ObbsOverall:P0} cov={adaptive.SearchQuality.Coverage:P0} val={adaptive.SearchQuality.Validation:P0})");

        Console.WriteLine($"Auto-generate: {modelId}");
        Console.WriteLine($"  Candidates: {adaptive.CandidatesEvaluated}");
        Console.WriteLine($"  Params:     min_area={adaptive.MinAreaM2:F3} axis_spacing={adaptive.AxisSpacingM:F2} subdiv_gap={adaptive.SubdivisionGapM:F2} fit={adaptive.ReferenceFit} span_factor={adaptive.WallSegmentTightFactor:F3}");
        Console.WriteLine($"  Components: {adaptive.Geometry.Components.Count} (skipped {adaptive.Geometry.SkippedPatches})");
        Console.WriteLine($"  Vertices:   {adaptive.Geometry.GeometryLod.VertexCount}");
        Console.WriteLine($"  Score:      {adaptive.Validation.OverallScore:P0} passed={adaptive.Validation.Passed}");
        foreach (var msg in adaptive.Validation.Messages)
            Console.WriteLine($"  {msg}");
        Console.WriteLine($"JSON: {jsonOut}");
        return adaptive.Validation.Passed || !adaptive.Validation.HasReference ? 0 : 3;
    }

    private static int RunRegionGenerate(string[] args)
    {
        var input = GetArg(args, "--input")
            ?? throw new ArgumentException("Missing --input (resolution mesh JSON).");
        var seedsPath = GetArg(args, "--seeds");
        var autoSeeds = args.Any(a => string.Equals(a, "--auto-seeds", StringComparison.OrdinalIgnoreCase));
        var modelId = GetArg(args, "--model-id") ?? "";
        var corpusPath = GetArg(args, "--corpus") ?? DefaultCorpusPath();
        var refGeoPath = GetArg(args, "--reference-geometry");
        var jsonOut = GetArg(args, "--output-json")
            ?? Path.ChangeExtension(input, ".region_result.json");

        var mesh = JsonMeshLoader.LoadResolutionFromFile(input);
        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        IReadOnlyList<GeoRegionSeed> seeds;
        if (autoSeeds)
        {
            seeds = RegionSeedAutoExtractor.Extract(mesh, profile);
            Console.WriteLine($"  Auto-seeds: {seeds.Count}");
        }
        else if (!string.IsNullOrEmpty(seedsPath) && File.Exists(seedsPath))
        {
            seeds = GeoRegionSeedLoader.LoadFromFile(seedsPath);
        }
        else if (mesh.Properties.TryGetValue("geo_region_seeds", out var embedded)
                 && embedded is IReadOnlyList<GeoRegionSeed> embeddedSeeds)
        {
            seeds = embeddedSeeds;
        }
        else
        {
            throw new ArgumentException("Provide --seeds file, embed geo_region_seeds in mesh JSON, or use --auto-seeds.");
        }

        var rawSeedCount = seeds.Count;
        seeds = RegionSeedNormalizer.NormalizeForPipeline(mesh, seeds, profile);
        if (rawSeedCount != seeds.Count)
            Console.WriteLine($"  Seeds normalized: {rawSeedCount} → {seeds.Count}");

        var targetCount = 0;
        MeshData? referenceGeometry = null;
        if (!string.IsNullOrEmpty(refGeoPath) && File.Exists(refGeoPath))
        {
            referenceGeometry = JsonMeshLoader.LoadGeometryFromFile(refGeoPath);
            targetCount = referenceGeometry.VertexGroups.Keys.Count(k =>
                k.StartsWith("component", StringComparison.OrdinalIgnoreCase));
        }

        if (targetCount <= 0 && File.Exists(corpusPath) && !string.IsNullOrEmpty(modelId))
        {
            var corpus = BuildingCorpusReader.Load(corpusPath);
            var reference = CorpusReferenceLookup.TryGetById(corpus, modelId, LoadMeshStore(GetArg(args, "--mesh-store")));
            targetCount = reference?.GeometryComponentCount ?? 0;
        }

        var wallSpan = SegmentSpanCalibrator.EstimateWallMaxSpanM(profile, targetCount);
        var result = BuildingGeometryEngine.Generate(mesh, new BuildingGeometryOptions
        {
            Decomposition = BuildingDecompositionMode.RegionGuided,
            RegionSeeds = seeds,
            TargetComponentCount = targetCount,
            WallSegmentMaxSpanM = wallSpan,
            RequireDoorVertices = false,
            Profile = profile,
            ResolutionSource = mesh,
        });

        var regionPlan = RegionSeedExpander.BuildPlan(mesh, seeds, profile);
        GeometricCompareResult? geoCompare = null;
        CoverageScore? coverage = null;
        if (referenceGeometry != null)
        {
            geoCompare = GeometricComponentComparer.Compare(referenceGeometry, result.Components);
            coverage = ResolutionCoverageScorer.ScoreFromComponents(mesh, result.Components, profile);
        }

        var dto = new
        {
            model_id = modelId,
            decomposition = BuildingDecompositionMode.RegionGuided.ToString(),
            seed_count = seeds.Count,
            region_faces = regionPlan.FacesByKind.ToDictionary(
                kv => kv.Key.ToString(),
                kv => kv.Value.Count),
            unassigned_faces = regionPlan.UnassignedFaceCount,
            target_component_count = targetCount,
            wall_segment_span_m = wallSpan,
            patches = result.Patches.Count,
            skipped = result.SkippedPatches,
            components = result.Components.Select(c => new
            {
                name = c.Name,
                vertices = c.Mesh.Vertices.Select(v => new[] { v.X, v.Y, v.Z }).ToList(),
                faces = c.Mesh.Faces.Select(f => f.ToArray()).ToList(),
            }).ToList(),
            geometric_compare = geoCompare == null ? null : ToGeometricCompareDto(geoCompare),
            coverage = coverage == null ? null : new
            {
                coverage.FractionInside,
                coverage.SamplesInside,
                coverage.SamplesTotal,
            },
            seeds = seeds.Select(s => new
            {
                kind = s.Kind.ToString(),
                face_index = s.FaceIndex,
                position = new[] { s.Position.X, s.Position.Y, s.Position.Z },
                normal = new[] { s.Normal.X, s.Normal.Y, s.Normal.Z },
            }).ToList(),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonOut, json);

        Console.WriteLine($"Region-generate: {modelId}");
        Console.WriteLine($"  Seeds:      {seeds.Count}");
        Console.WriteLine($"  Guided:     {regionPlan.AllGuidedFaces.Count} faces");
        Console.WriteLine($"  Unassigned: {regionPlan.UnassignedFaceCount} faces");
        Console.WriteLine($"  Components: {result.Components.Count} (target {targetCount}, skipped {result.SkippedPatches})");
        if (geoCompare != null)
        {
            Console.WriteLine($"  Geo-Compare:{geoCompare.OverallScore:P0} status={geoCompare.OverallStatus} "
                              + $"mean_ctr={geoCompare.MeanCenterDeltaM:F3}m pairs={geoCompare.MatchedPairs}/{geoCompare.ReferenceCount}");
        }
        if (coverage != null)
            Console.WriteLine($"  Coverage:   {coverage.FractionInside:P0}");
        Console.WriteLine($"JSON: {jsonOut}");
        return geoCompare == null || geoCompare.OverallScore >= 0.5 ? 0 : 3;
    }

    private static int RunCompareGeometry(string[] args)
    {
        var refPath = GetArg(args, "--reference")
            ?? throw new ArgumentException("Missing --reference (geometry LOD JSON).");
        var genPath = GetArg(args, "--generated")
            ?? throw new ArgumentException("Missing --generated (geometry LOD JSON).");
        var jsonOut = GetArg(args, "--output-json")
            ?? Path.ChangeExtension(genPath, ".compare.json");

        var refGeo = JsonMeshLoader.LoadGeometryFromFile(refPath);
        var genGeo = JsonMeshLoader.LoadGeometryFromFile(genPath);

        var refComps = GeometricComponentComparer.ExtractFromGeometryLod(refGeo);
        var genComps = GeometricComponentComparer.ExtractFromGeometryLod(genGeo);
        var compare = GeometricComponentComparer.Compare(refComps, genComps);

        var dto = new
        {
            reference_components = refComps.Count,
            generated_components = genComps.Count,
            geometric_compare = ToGeometricCompareDto(compare),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonOut, json);

        Console.WriteLine($"Compare-geometry: ref={refComps.Count} gen={genComps.Count}");
        Console.WriteLine($"  Geo status={compare.OverallStatus} mean_corner={compare.MeanCornerErrorM:F4}m "
                          + $"max_corner={compare.MaxCornerErrorM:F4}m pairs={compare.MatchedPairs}");
        Console.WriteLine($"JSON: {jsonOut}");
        return 0;
    }

    private static object ToGeometricCompareDto(GeometricCompareResult compare) => new
    {
        compare.OverallScore,
        overall_status = compare.OverallStatus.ToString(),
        compare.ReferenceCount,
        compare.GeneratedCount,
        compare.MatchedPairs,
        compare.MaxCornerErrorM,
        compare.MeanCornerErrorM,
        compare.MeanCenterDeltaM,
        pairs = compare.Pairs.Select(p => new
        {
            p.ReferenceName,
            p.GeneratedName,
            p.ReferenceIndex,
            p.GeneratedIndex,
            p.MaxCornerErrorM,
            p.MeanCornerErrorM,
            p.CenterDeltaM,
            extent_delta_m = new[] { p.ExtentDeltaU, p.ExtentDeltaV, p.ExtentDeltaN },
            status = p.Status.ToString(),
        }).ToList(),
    };

    private static int RunPatchDiag(string[] args)
    {
        var input = GetArg(args, "--input")
            ?? throw new ArgumentException("Missing --input (resolution LOD JSON).");
        var jsonOut = GetArg(args, "--output-json")
            ?? Path.ChangeExtension(input, ".patch_diag.json");
        var refGeoPath = GetArg(args, "--reference-geometry");

        var resolution = JsonMeshLoader.LoadResolutionFromFile(input);
        var profile = BuildingMeshAnalyzer.Analyze(resolution);
        var prefilter = ResolutionRegionPrefilter.Apply(resolution, profile);
        var doorRegions = prefilter.DoorRegions;
        var workMesh = prefilter.RemainingMesh;
        var minArea = 0.12;

        var patches = FaceDrivenDecomposer.Split(workMesh, minArea, profile, doorRegions);
        var wallSpanM = WallEdgeSegmenter.DefaultMaxSpanM(profile);
        patches = SpatialPatchSubdivider.Subdivide(
            workMesh, patches, profile,
            new SpatialSubdivisionOptions
            {
                MinGapM = 0.5,
                BinSizeM = 0.12,
                MinPatchAreaM2 = 0.06,
                MaxInPlaneSpanM = wallSpanM,
                DoorRegions = doorRegions,
                SpanFallbackOnly = true,
            });
        patches = PatchMerger.MergeAntiparallel(patches, profile);
        patches = PatchMerger.MergeCoplanar(
            patches, profile, gapM: 0.18, seamBridgeOnly: true, maxMergedSpanM: wallSpanM);

        var diag = PatchDiagnostics.Analyze(workMesh, patches, profile);
        GeometricCompareResult? geo = null;
        if (refGeoPath != null && File.Exists(refGeoPath))
        {
            var gen = BuildingGeometryEngine.Generate(resolution, new BuildingGeometryOptions
            {
                Decomposition = BuildingDecompositionMode.FaceDriven,
                ResolutionGuidedObbFit = true,
                ResolutionSource = resolution,
                Profile = profile,
                RequireDoorVertices = false,
            });
            var refGeo = JsonMeshLoader.LoadGeometryFromFile(refGeoPath);
            geo = GeometricComponentComparer.Compare(
                GeometricComponentComparer.ExtractFromGeometryLod(refGeo),
                GeometricComponentComparer.ExtractFromGeometryLod(gen.GeometryLod));
        }

        var dto = new
        {
            model = resolution.Name,
            patch_count = patches.Count,
            patches = diag.Select(d => new
            {
                d.Index,
                d.FaceCount,
                d.AreaM2,
                tangent_span_m = d.TangentSpanM,
                secondary_span_m = d.SecondarySpanM,
                thickness_m = d.ThicknessM,
                center_m = d.CenterM,
                normal = d.DominantNormal,
                d.Kind,
            }).ToList(),
            geometric_compare = geo == null ? null : ToGeometricCompareDto(geo),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonOut, json);

        var maxSpan = diag.Count > 0 ? diag.Max(d => d.TangentSpanM) : 0;
        Console.WriteLine($"Patch-diag: {patches.Count} patches, max_tangent_span={maxSpan:F2}m");
        if (geo != null)
            Console.WriteLine($"  geo_mean={geo.MeanCornerErrorM:F4}m geo_max={geo.MaxCornerErrorM:F4}m");
        Console.WriteLine($"JSON: {jsonOut}");
        return 0;
    }

    private static int RunRefSegmentStats(string[] args)
    {
        var input = GetArg(args, "--input");
        var modelId = GetArg(args, "--model-id");
        var jsonOut = GetArg(args, "--output-json");

        MeshData? geo = null;
        string id = modelId ?? "unknown";

        if (!string.IsNullOrEmpty(input))
        {
            geo = JsonMeshLoader.LoadGeometryFromFile(input);
            id = Path.GetFileNameWithoutExtension(input);
        }
        else if (!string.IsNullOrEmpty(modelId))
        {
            var meshStore = LoadMeshStore(GetArg(args, "--mesh-store"));
            if (meshStore == null || !meshStore.TryLoadPair(modelId, out var pair) || pair == null)
                throw new FileNotFoundException($"No baked mesh pair for {modelId}");
            geo = pair.GeometryLod;
            id = modelId;
        }
        else
        {
            throw new ArgumentException("Provide --input or --model-id.");
        }

        var stats = ReferenceSegmentStatsAnalyzer.Analyze(id, geo);
        var dto = new
        {
            stats.ModelId,
            stats.ComponentCount,
            tangent_m = new { min = stats.MinTangentM, median = stats.MedianTangentM, max = stats.MaxTangentM },
            thickness_m = new { min = stats.MinThicknessM, median = stats.MedianThicknessM, max = stats.MaxThicknessM },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        if (jsonOut != null)
            File.WriteAllText(jsonOut, json);

        Console.WriteLine($"Ref-segment-stats: {stats.ModelId} components={stats.ComponentCount}");
        Console.WriteLine($"  tangent median={stats.MedianTangentM:F2}m (max {stats.MaxTangentM:F2}m)");
        Console.WriteLine($"  thickness median={stats.MedianThicknessM:F3}m (max {stats.MaxThicknessM:F3}m)");
        if (jsonOut != null)
            Console.WriteLine($"JSON: {jsonOut}");
        return 0;
    }

    private static int RunValidateCorpus(string[] args)
    {
        var corpusPath = GetArg(args, "--corpus") ?? DefaultCorpusPath();
        var meshStorePath = GetArg(args, "--mesh-store");
        var outputPath = GetArg(args, "--output");
        var modelFilter = GetArg(args, "--model-id");
        var limit = int.TryParse(GetArg(args, "--limit"), out var lim) ? lim : (int?)null;
        var minComposite = double.Parse(
            GetArg(args, "--min-composite") ?? "0.72",
            System.Globalization.CultureInfo.InvariantCulture);
        var minCoverage = double.Parse(
            GetArg(args, "--min-coverage") ?? "0.55",
            System.Globalization.CultureInfo.InvariantCulture);
        var listOnly = args.Any(a => string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase));
        var fullSearch = args.Any(a => string.Equals(a, "--full-search", StringComparison.OrdinalIgnoreCase));
        var blind = args.Any(a => string.Equals(a, "--blind", StringComparison.OrdinalIgnoreCase));
        var meshKindFilter = GetArg(args, "--mesh-kind");

        if (!File.Exists(corpusPath))
            throw new FileNotFoundException("Corpus index not found", corpusPath);

        var corpus = BuildingCorpusReader.Load(corpusPath);
        var meshStore = LoadMeshStore(meshStorePath);
        if (meshStore == null && !listOnly)
            throw new FileNotFoundException(
                "Corpus mesh store not found — run tools/corpus_mesh_bake.py first",
                meshStorePath ?? DefaultMeshStorePath());

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var componentMatches = 0;
        var geoGood = 0;
        var results = new List<object>();
        IEnumerable<BuildingCorpusModelDto> models;
        if (blind && meshStore != null && string.IsNullOrEmpty(modelFilter))
        {
            var corpusById = corpus.Models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            models = meshStore.Entries.Values
                .Where(e => e.HasFullPair && e.GeometryComponentCount > 0)
                .Select(e => e.ModelId)
                .Where(id => corpusById.ContainsKey(id))
                .Select(id => corpusById[id])
                .Where(m => string.IsNullOrEmpty(meshKindFilter)
                    || string.Equals(m.MeshKind, meshKindFilter, StringComparison.OrdinalIgnoreCase));
            if (limit.HasValue)
                models = models.Take(limit.Value);
        }
        else
        {
            models = corpus.Models;
            if (!string.IsNullOrEmpty(modelFilter))
                models = models.Where(m => string.Equals(m.Id, modelFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(meshKindFilter))
                models = models.Where(m => string.Equals(m.MeshKind, meshKindFilter, StringComparison.OrdinalIgnoreCase));
            if (limit.HasValue)
                models = models.Take(limit.Value);
        }

        foreach (var model in models)
        {
            var reference = CorpusReferenceLookup.TryGetById(corpus, model.Id, meshStore);
            if (listOnly)
            {
                var meshFlag = reference?.HasFullMeshes == true ? "mesh" : "meta";
                Console.WriteLine(
                    $"REF {model.Id} [{meshFlag}]: {reference?.GeometryComponentCount ?? 0} components, "
                    + $"{reference?.GeometryVertices ?? 0} verts");
                continue;
            }

            if (meshStore == null || !meshStore.TryLoadPair(model.Id, out var pair) || pair == null)
            {
                skipped++;
                Console.WriteLine($"SKIP {model.Id} — no baked mesh pair");
                continue;
            }

            if (pair.Entry.GeometryComponentCount <= 0)
            {
                skipped++;
                Console.WriteLine($"SKIP {model.Id} — geometry has no components");
                continue;
            }

            if (pair.ResolutionLod.FaceCount <= 0)
            {
                skipped++;
                Console.WriteLine($"SKIP {model.Id} — empty resolution mesh");
                continue;
            }

            var adaptive = blind || fullSearch
                ? AdaptiveBuildingGenerator.GenerateAdaptive(
                    pair.ResolutionLod, reference, model.Id, pair.GeometryLod,
                    blindGeneration: blind, allowSnap: !blind)
                : AdaptiveBuildingGenerator.GenerateCorpusOffline(
                    pair.ResolutionLod, reference, model.Id, pair.GeometryLod);
            var search = adaptive.SearchQuality;
            var coverage = adaptive.Coverage?.FractionInside ?? 0;
            var composite = search?.Composite ?? adaptive.Validation.OverallScore;
            var obbScore = adaptive.ObbGeometry?.OverallScore ?? 0;
            var geo = adaptive.GeometricCompare;
            var countMatch = adaptive.Geometry.Components.Count == (reference?.GeometryComponentCount ?? -1);
            var structuralOk = obbScore >= 0.99 && countMatch && adaptive.Validation.OverallScore >= 0.85;
            var geoOk = geo != null && geo.MeanCornerErrorM <= 0.05 && geo.MaxCornerErrorM <= 0.25;
            var ok = blind
                ? countMatch && (geoOk || (geo != null && geo.MeanCornerErrorM <= 0.15 && obbScore >= 0.45))
                : composite >= minComposite && coverage >= minCoverage
                    || (structuralOk && coverage >= 0.30 && composite >= 0.65);

            var row = new
            {
                model_id = model.Id,
                mesh_kind = model.MeshKind,
                passed = ok,
                blind,
                composite,
                obb = adaptive.ObbGeometry?.OverallScore ?? 0,
                coverage,
                validation = adaptive.Validation.OverallScore,
                reference_components = reference?.GeometryComponentCount ?? 0,
                generated_components = adaptive.Geometry.Components.Count,
                component_match = countMatch,
                geo_mean_corner_m = geo?.MeanCornerErrorM,
                geo_max_corner_m = geo?.MaxCornerErrorM,
                geo_overall = geo?.OverallScore,
                geo_status = geo?.OverallStatus.ToString(),
                candidates = adaptive.CandidatesEvaluated,
            };
            results.Add(row);
            if (countMatch)
                componentMatches++;
            if (geo != null && geo.MeanCornerErrorM <= 0.05)
                geoGood++;

            if (ok)
            {
                passed++;
                Console.WriteLine(
                    $"OK   {model.Id} search={composite:P0} obb={adaptive.ObbGeometry?.OverallScore:P0} "
                    + $"cov={coverage:P0} comps={adaptive.Geometry.Components.Count}/{reference?.GeometryComponentCount}"
                    + (geo != null ? $" geo_mean={geo.MeanCornerErrorM:F3}m max={geo.MaxCornerErrorM:F3}m" : ""));
            }
            else
            {
                failed++;
                Console.WriteLine(
                    $"FAIL {model.Id} search={composite:P0} obb={adaptive.ObbGeometry?.OverallScore:P0} "
                    + $"cov={coverage:P0} comps={adaptive.Geometry.Components.Count}/{reference?.GeometryComponentCount}"
                    + (geo != null ? $" geo_mean={geo.MeanCornerErrorM:F3}m max={geo.MaxCornerErrorM:F3}m" : ""));
            }
            Console.Out.Flush();
        }

        if (listOnly)
        {
            Console.WriteLine($"Corpus reference listing: {corpus.ModelCount} models "
                              + $"(full meshes: {meshStore?.Entries.Values.Count(e => e.HasFullPair) ?? 0})");
            return 0;
        }

        var summary = new
        {
            corpus = corpusPath,
            mesh_store = meshStore?.SourcePath,
            blind,
            min_composite = minComposite,
            min_coverage = minCoverage,
            passed,
            failed,
            skipped,
            component_match = componentMatches,
            geo_mean_5cm = geoGood,
            results,
        };

        if (outputPath != null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                summary,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json);
            Console.WriteLine($"Report: {outputPath}");
        }

        Console.WriteLine($"Done: passed={passed} failed={failed} skipped={skipped}");
        return failed > 0 ? 3 : 0;
    }

    private static void ExportAdaptiveJson(
        AdaptiveGenerationResult adaptive,
        string jsonPath,
        bool blind = false,
        bool noSnap = false)
    {
        var dto = new
        {
            model_id = adaptive.Validation.ModelId,
            blind,
            no_snap = noSnap,
            min_area = adaptive.MinAreaM2,
            axis_spacing = adaptive.AxisSpacingM,
            subdivision_gap = adaptive.SubdivisionGapM,
            decomposition = adaptive.Decomposition.ToString(),
            reference_fit = adaptive.ReferenceFit.ToString(),
            reference_blend = adaptive.ReferenceBlendWeight,
            wall_segment_span_factor = adaptive.WallSegmentTightFactor,
            angle = adaptive.AngleThresholdDeg,
            candidates_evaluated = adaptive.CandidatesEvaluated,
            patches = adaptive.Geometry.Patches.Count,
            skipped = adaptive.Geometry.SkippedPatches,
            validation = new
            {
                adaptive.Validation.HasReference,
                adaptive.Validation.Passed,
                adaptive.Validation.ReferenceComponents,
                adaptive.Validation.GeneratedComponents,
                adaptive.Validation.ReferenceVertices,
                adaptive.Validation.GeneratedVertices,
                adaptive.Validation.ComponentScore,
                adaptive.Validation.VertexScore,
                adaptive.Validation.OverallScore,
                messages = adaptive.Validation.Messages,
            },
            building_profile = adaptive.BuildingProfile == null ? null : new
            {
                size_m = new[] { adaptive.BuildingProfile.SizeM.X, adaptive.BuildingProfile.SizeM.Y, adaptive.BuildingProfile.SizeM.Z },
                footprint_m2 = adaptive.BuildingProfile.FootprintAreaM2,
                height_m = adaptive.BuildingProfile.HeightM,
                wall_thickness_m = adaptive.BuildingProfile.WallThicknessM,
                axis_x = new[] { adaptive.BuildingProfile.AxisX.X, adaptive.BuildingProfile.AxisX.Y, adaptive.BuildingProfile.AxisX.Z },
            },
            obb_geometry = adaptive.ObbGeometry == null ? null : new
            {
                adaptive.ObbGeometry.OverallScore,
                adaptive.ObbGeometry.ExtentScore,
                adaptive.ObbGeometry.RotationScore,
                adaptive.ObbGeometry.CenterScore,
                adaptive.ObbGeometry.MatchedPairs,
                adaptive.ObbGeometry.ReferenceCount,
                adaptive.ObbGeometry.GeneratedCount,
            },
            geometric_compare = adaptive.GeometricCompare == null ? null : new
            {
                adaptive.GeometricCompare.OverallScore,
                overall_status = adaptive.GeometricCompare.OverallStatus.ToString(),
                adaptive.GeometricCompare.ReferenceCount,
                adaptive.GeometricCompare.GeneratedCount,
                adaptive.GeometricCompare.MatchedPairs,
                adaptive.GeometricCompare.MaxCornerErrorM,
                adaptive.GeometricCompare.MeanCornerErrorM,
                adaptive.GeometricCompare.MeanCenterDeltaM,
                pairs = adaptive.GeometricCompare.Pairs.Select(p => new
                {
                    p.ReferenceName,
                    p.GeneratedName,
                    p.ReferenceIndex,
                    p.GeneratedIndex,
                    p.MaxCornerErrorM,
                    p.MeanCornerErrorM,
                    p.CenterDeltaM,
                    extent_delta_m = new[] { p.ExtentDeltaU, p.ExtentDeltaV, p.ExtentDeltaN },
                    status = p.Status.ToString(),
                }).ToList(),
            },
            coverage = adaptive.Coverage == null ? null : new
            {
                adaptive.Coverage.FractionInside,
                adaptive.Coverage.SamplesInside,
                adaptive.Coverage.SamplesTotal,
            },
            search_quality = adaptive.SearchQuality == null ? null : new
            {
                adaptive.SearchQuality.Composite,
                adaptive.SearchQuality.ObbsOverall,
                adaptive.SearchQuality.Coverage,
                adaptive.SearchQuality.Validation,
                adaptive.SearchQuality.CountDiff,
                adaptive.SearchQuality.InCountCorridor,
            },
            components = adaptive.Geometry.Components.Select(c => new
            {
                name = c.Name,
                vertices = c.Mesh.Vertices.Select(v => new[] { v.X, v.Y, v.Z }).ToList(),
                faces = c.Mesh.Faces.Select(f => f.ToArray()).ToList(),
            }).ToList(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
    }

    private static int RunMergeCorpus(string[] args)
    {
        var input = GetArg(args, "--input") ?? DefaultCorpusPath();
        var meshStorePath = GetArg(args, "--mesh-store");

        var corpus = BuildingCorpusReader.Load(input);
        var meshStore = LoadMeshStore(meshStorePath);
        var datasets = BuildingCorpusReader.ToBuildingDatasets(corpus, meshStore);

        var fullMeshes = datasets.Count(d =>
            d.ResolutionLod != null && d.ResolutionLod.VertexCount > 0
            && d.GeometryLod != null && d.GeometryLod.VertexCount > 0
            && d.GeometryLod.Faces.Count > 0);

        Console.WriteLine($"Building corpus: {corpus.ModelCount} models");
        Console.WriteLine($"  with doors:       {corpus.WithDoors}");
        Console.WriteLine($"  with scenes:      {corpus.WithScenes}");
        Console.WriteLine($"  loaded datasets:  {datasets.Count}");
        Console.WriteLine($"  full mesh pairs:  {fullMeshes}");
        if (meshStore != null)
            Console.WriteLine($"  mesh store:       {meshStore.SourcePath}");
        Console.WriteLine($"Source: {input}");
        return 0;
    }

    private static int RunValidateDoors(string[] args)
    {
        var input = GetArg(args, "--input")
            ?? throw new ArgumentException("Missing --input (mesh dump or dataset JSON).");
        var expected = int.Parse(GetArg(args, "--doors") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

        var mesh = input.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonMeshLoader.LoadResolutionFromFile(input)
            : CdmDumpParser.ParseMeshDump(File.ReadAllText(input));

        var validation = DoorValidator.Validate(mesh, expected);
        Console.WriteLine(validation.IsValid ? "OK — door selections valid." : "FAILED — door selections invalid.");

        foreach (var err in validation.Errors)
            Console.WriteLine($"  ERROR: {err}");
        foreach (var warn in validation.Warnings)
            Console.WriteLine($"  WARN:  {warn}");
        foreach (var door in validation.FoundDoors)
            Console.WriteLine($"  door: {door.SelectionName} ({door.Vertices.Count} vertices)");

        return validation.IsValid ? 0 : 3;
    }

    private static void ExportResultJson(BuildingGeometryResult result, string jsonPath)
    {
        var dto = new
        {
            patches = result.Patches.Count,
            skipped = result.SkippedPatches,
            components = result.Components.Select(c => new
            {
                name = c.Name,
                vertices = c.Mesh.Vertices.Select(v => new[] { v.X, v.Y, v.Z }).ToList(),
                faces = c.Mesh.Faces.Select(f => f.ToArray()).ToList(),
            }).ToList(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
    }

    private static string BuildGenerationReport(BuildingGeometryResult result)
    {
        var lines = new List<string>
        {
            "CDM GeoEngine — Generation Report",
            $"Components: {result.Components.Count}",
            $"Patches:    {result.Patches.Count}",
            $"Vertices:   {result.GeometryLod.VertexCount}",
            $"Faces:      {result.GeometryLod.FaceCount}",
            "",
        };
        foreach (var comp in result.Components)
        {
            lines.Add($"COMPONENT: {comp.Name}");
            lines.Add($"  Vertices: {comp.Mesh.VertexCount}");
            lines.Add($"  Faces:    {comp.Mesh.FaceCount}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintHelp();
        return 1;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            CDM GeoEngine CLI — DayZ building Geometry LOD

            Commands:
              collect [--input geo_dump] [--output dataset.json]
                  Read all CDM compare dumps (Res LOD + Geometry) into JSON.

              generate --input building.txt [--min-area 0.25] [--angle 30]
                       [--output report.txt] [--output-json result.json]
                  Run OBB pipeline on a Resolution/building mesh dump.

              validate-doors --input building.txt [--doors 2]
                  Check doorN vertex groups (DayZ requirement).

              merge-corpus [--input building_corpus_index.json] [--mesh-store corpus_meshes_index.json]
                  Load merged CDM building corpus index (with optional full mesh pairs).

              auto-generate --input res_mesh.json [--model-id id] [--corpus index.json]
                            [--mesh-store corpus_meshes_index.json]
                            [--reference-geometry ref.json] [--blind] [--no-snap]
                            [--output-json result.json]
                  Corpus-guided adaptive Geometry generation (parameter search).
                  Loads reference Geometry LOD from --reference-geometry or baked corpus meshes.
                  --blind: generate without reference OBBs; still scores vs reference geometry.
                  --no-snap: keep reference-guided fit but disallow snap candidates.

              region-generate --input res_mesh.json [--seeds seeds.json | --auto-seeds]
                              [--model-id id] [--reference-geometry ref.json]
                              [--corpus index.json] [--output-json result.json]
                  Sparse semantic region picks (wall, roof, gable, …) expanded by engine.
                  --auto-seeds: derive one seed per region from mesh (sandbox tests).

              compare-geometry --reference ref_geometry.json --generated gen_geometry.json
                               [--output-json compare.json]
                  GeometricComponentComparer on two Geometry LOD JSON exports (e.g. post-Finalize).

              patch-diag --input resolution.json [--reference-geometry geo.json]
                           [--output-json patch_diag.json]
                  Export per-patch tangent span / thickness diagnostics (FaceDriven blind path).

              ref-segment-stats --input geometry.json | --model-id sheds/shed_w1
                                [--mesh-store corpus_meshes_index.json] [--output-json stats.json]
                  Reference OBB extent bands (target wall strip sizes).

              validate-corpus [--corpus index.json] [--mesh-store corpus_meshes_index.json]
                              [--model-id id] [--limit N] [--list] [--output report.json]
                              [--min-composite 0.72] [--min-coverage 0.55]
                  Offline heuristic search over all baked corpus mesh pairs (no Blender).
                  --list: metadata only. Default: full OBB + coverage + validation scoring.

            Examples:
              dotnet run --project src/Cdm.GeoEngine.Cli -- collect
              dotnet run --project src/Cdm.GeoEngine.Cli -- validate-corpus --model-id sheds/shed_w1
              dotnet run --project src/Cdm.GeoEngine.Cli -- validate-corpus --output Test/offline_report.json
            """);
        return 0;
    }
}
