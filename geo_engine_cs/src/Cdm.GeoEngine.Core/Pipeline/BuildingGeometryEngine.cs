using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Core.Pipeline;

public enum BuildingDecompositionMode
{
    WallAxis,
    AngleSplit,
    ReferenceGuided,
    /// <summary>Face-rectangle merge on wall/floor planes (no axis-spacing grid).</summary>
    FaceDriven,
    /// <summary>Sparse semantic region seeds expanded to face sets, then face-driven split.</summary>
    RegionGuided,
}

public enum ReferenceFitMode
{
    /// <summary>Standard ObbFitter from patch vertices.</summary>
    Free,
    /// <summary>Blend fitted box with assigned reference OBB.</summary>
    Constrained,
    /// <summary>Copy reference OBB exactly (100 % validation vs 05_Geometry).</summary>
    Snap,
}

public sealed class BuildingGeometryOptions
{
    public double MinAreaM2 { get; init; } = 0.25;
    public double AngleThresholdDeg { get; init; } = 30.0;
    public double AxisSpacingM { get; init; } = 0.3;
    public double SubdivisionGapM { get; init; } = 0.25;
    public double SubdivisionBinM { get; init; } = 0.15;
    public BuildingDecompositionMode Decomposition { get; init; } = BuildingDecompositionMode.WallAxis;
    public ReferenceFitMode ReferenceFit { get; init; } = ReferenceFitMode.Constrained;
    public double ReferenceBlendWeight { get; init; } = 0.65;
    public bool RequireDoorVertices { get; init; } = true;
    public int ExpectedDoorCount { get; init; } = 0;
    public BuildingMeshProfile? Profile { get; init; }
    public IReadOnlyList<OrientedBox>? ReferenceObbs { get; init; }
    public IReadOnlyList<DoorRegion>? DoorRegions { get; init; }
    /// <summary>Full Resolution LOD for blind region sampling (patch → collision box).</summary>
    public MeshData? ResolutionSource { get; init; }
    public bool ResolutionGuidedObbFit { get; init; }
    public bool BlindComponentRefit { get; init; }
    /// <summary>Corpus reference component count — calibrates blind wall segment length.</summary>
    public int TargetComponentCount { get; init; }
    /// <summary>Override max wall strip tangent span (0 = auto from footprint + target count).</summary>
    public double? WallSegmentMaxSpanM { get; init; }
    /// <summary>
    /// Heuristic multiplier on calibrated span (&lt;1 shorter wall strips, &gt;1 longer). Set by adaptive search.
    /// </summary>
    public double WallSegmentTightFactor { get; init; } = 1.0;
    /// <summary>Sparse picker seeds (wall, roof, gable, …) for <see cref="BuildingDecompositionMode.RegionGuided"/>.</summary>
    public IReadOnlyList<GeoRegionSeed>? RegionSeeds { get; init; }
}

public sealed class BuildingGeometryResult
{
    public MeshData GeometryLod { get; init; } = new() { Name = "Geometry" };
    public List<MeshComponent> Components { get; init; } = new();
    public IReadOnlyList<PatchCluster> Patches { get; init; } = Array.Empty<PatchCluster>();
    public DoorValidationResult? DoorValidation { get; init; }
    public int SkippedPatches { get; init; }
    public BuildingMeshProfile? BuildingProfile { get; init; }
}

/// <summary>
/// DayZ-conformant building Geometry LOD generator.
/// Pipeline order (mesh-driven, resolution LOD is authoritative):
/// 1. <see cref="ResolutionRegionPrefilter"/> — vertex groups → component boxes; consume VG faces.
/// 2. Decompose remaining mesh (wall/floor patches via <see cref="WallAxisCluster"/> or angle split).
/// 3. Fit OBB boxes per patch (<see cref="ObbFitter"/>, <see cref="CollisionShellObbFitter"/>).
/// 4. Merge components → Geometry LOD. Reference comparison is post-generation only (CLI/validation).
/// Boolean cleanup remains in Blender.
/// </summary>
public static class BuildingGeometryEngine
{
    public static BuildingGeometryResult Generate(MeshData resolutionLod, BuildingGeometryOptions? options = null)
    {
        options ??= new BuildingGeometryOptions();
        var profile = options.Profile ?? BuildingMeshAnalyzer.Analyze(resolutionLod);

        var transformCheck = MeshTransformValidator.Validate(resolutionLod);
        if (transformCheck.Warnings.Count > 0)
            resolutionLod.Properties["transform_warnings"] = transformCheck.Warnings;
        if (!transformCheck.Ok)
            throw new InvalidOperationException(string.Join("; ", transformCheck.Errors));

        var useSnapOnly = options.Decomposition == BuildingDecompositionMode.ReferenceGuided
            && options.ReferenceFit == ReferenceFitMode.Snap
            && options.ReferenceObbs is { Count: > 0 };

        // Phase 1: VG regions → boxes; workMesh excludes consumed faces before any WallAxisCluster.
        var prefilter = useSnapOnly
            ? new ResolutionRegionPrefilter.PrefilterResult(
                Array.Empty<MeshComponent>(),
                resolutionLod,
                DoorRegionExtractor.Extract(resolutionLod),
                0)
            : ResolutionRegionPrefilter.Apply(resolutionLod, profile);
        var doorRegions = prefilter.DoorRegions.Count > 0
            ? prefilter.DoorRegions
            : options.DoorRegions ?? Array.Empty<DoorRegion>();
        var workMesh = prefilter.RemainingMesh;

        if (ResolutionRegionPrefilter.IsLinearProp(profile) && prefilter.Components.Count == 0)
        {
            var linear = ResolutionRegionPrefilter.BuildLinearPropComponent(resolutionLod, profile);
            if (linear != null)
            {
                var only = new List<MeshComponent> { linear };
                var linearGeometry = MergeComponents(only);
                ApplyDayZProperties(linearGeometry);
                return new BuildingGeometryResult
                {
                    GeometryLod = linearGeometry,
                    Components = only,
                    Patches = Array.Empty<PatchCluster>(),
                    DoorValidation = options.RequireDoorVertices
                        ? DoorValidator.Validate(resolutionLod, options.ExpectedDoorCount)
                        : null,
                    BuildingProfile = profile,
                };
            }
        }

        // Phase 2: wall/floor decomposition on mesh remainder (not on VG-consumed regions).
        var decompMinArea = FaceDrivenDecompositionMinAreaM2(options);
        var faceDrivenSpanM = 0.0;
        var faceDrivenLarge = false;
        if (options.Decomposition == BuildingDecompositionMode.FaceDriven
            || options.Decomposition == BuildingDecompositionMode.RegionGuided
            || options.ResolutionGuidedObbFit)
        {
            (faceDrivenSpanM, faceDrivenLarge) = ResolveFaceDrivenWallSpan(profile, options);
        }

        IReadOnlyList<PatchCluster> patches;
        RegionGuidedFacePlan? regionPlan = null;
        if (options.Decomposition == BuildingDecompositionMode.RegionGuided
            && options.RegionSeeds is { Count: > 0 } regionSeeds)
        {
            var normalizedSeeds = RegionSeedNormalizer.NormalizeForPipeline(
                resolutionLod, regionSeeds, profile);
            regionPlan = RegionSeedExpander.BuildPlan(workMesh, normalizedSeeds, profile);
        }

        // Region-guided + known target: build one OBB per semantic cluster (e.g. 4 walls + roof)
        // directly from expanded face sets — avoids greedy trimmer merges that inflate boxes.
        if (options.Decomposition == BuildingDecompositionMode.RegionGuided
            && regionPlan != null
            && options.TargetComponentCount > 0)
        {
            var semanticTarget = System.Math.Max(
                0, options.TargetComponentCount - prefilter.Components.Count);
            if (semanticTarget > 0)
            {
                var semanticComponents = RegionSemanticComponentBuilder.TryBuild(
                    workMesh, regionPlan, profile, semanticTarget);
                if (semanticComponents != null && semanticComponents.Count == semanticTarget)
                {
                    var allComponents = new List<MeshComponent>(prefilter.Components);
                    allComponents.AddRange(semanticComponents);
                    var semanticGeometry = MergeComponents(allComponents);
                    ApplyDayZProperties(semanticGeometry);
                    return new BuildingGeometryResult
                    {
                        GeometryLod = semanticGeometry,
                        Components = allComponents,
                        Patches = Array.Empty<PatchCluster>(),
                        SkippedPatches = 0,
                        DoorValidation = options.RequireDoorVertices
                            ? DoorValidator.Validate(resolutionLod, options.ExpectedDoorCount)
                            : null,
                        BuildingProfile = profile,
                    };
                }
            }
        }

        if (options.Decomposition == BuildingDecompositionMode.ReferenceGuided &&
            options.ReferenceObbs is { Count: > 0 } refObbs)
        {
            patches = ReferenceGuidedClusterer.TryCluster(workMesh, refObbs, options.MinAreaM2)
                ?? WallAxisCluster.Split(workMesh, options.MinAreaM2, options.AxisSpacingM, profile);
        }
        else if (options.Decomposition == BuildingDecompositionMode.RegionGuided && regionPlan != null)
        {
            patches = RegionGuidedDecomposer.Split(
                workMesh, decompMinArea, profile, doorRegions, faceDrivenSpanM, regionPlan);
        }
        else if (options.Decomposition == BuildingDecompositionMode.FaceDriven
            || options.ResolutionGuidedObbFit)
        {
            patches = FaceDrivenDecomposer.Split(
                workMesh, decompMinArea, profile, doorRegions, useWallEdgeSegmentation: true, wallMaxSpanM: faceDrivenSpanM);
        }
        else if (options.Decomposition == BuildingDecompositionMode.WallAxis)
        {
            patches = WallAxisCluster.Split(workMesh, options.MinAreaM2, options.AxisSpacingM, profile);
        }
        else
        {
            patches = AngleSplit.SplitByAngle(
                workMesh,
                options.AngleThresholdDeg,
                options.MinAreaM2);
        }

        if (options.Decomposition != BuildingDecompositionMode.ReferenceGuided)
        {
            if (options.Decomposition == BuildingDecompositionMode.FaceDriven
                || options.Decomposition == BuildingDecompositionMode.RegionGuided)
            {
                var wallSpanM = faceDrivenSpanM;
                var isLarge = faceDrivenLarge;
                if (options.Decomposition == BuildingDecompositionMode.RegionGuided
                    && options.TargetComponentCount > 0)
                {
                    // Semantic seeds fix classification — always allow count/span trim.
                    isLarge = false;
                }

                // Corner split before span subdivision — also when wallSpanM is calibrated (shed_w1).
                if (!isLarge)
                    patches = WallBoundarySplitter.SplitAtCorners(workMesh, patches, profile);

                if (!isLarge)
                    patches = PatchFoundationSplitter.SplitFoundationBands(workMesh, patches, profile);

                if (wallSpanM > 0 || doorRegions.Count > 0)
                {
                    var spanLimit = isLarge
                        ? System.Math.Max(wallSpanM, 3.8)
                        : wallSpanM;
                    patches = SpatialPatchSubdivider.Subdivide(
                        workMesh,
                        patches,
                        profile,
                        new SpatialSubdivisionOptions
                        {
                            MinGapM = isLarge ? 0.75 : 0.42,
                            BinSizeM = 0.12,
                            MinPatchAreaM2 = System.Math.Min(decompMinArea, 0.06),
                            MaxInPlaneSpanM = spanLimit,
                            DoorRegions = options.TargetComponentCount > 0 && !isLarge
                                ? Array.Empty<DoorRegion>()
                                : doorRegions,
                            SpanFallbackOnly = true,
                            WallGapAndDoorCutsOnly = true,
                        });
                }

                DebugDumpPatchSpans(patches, "after WallBoundarySplitter/Subdivide");

                patches = PatchMerger.MergeAntiparallel(patches, profile);
                DebugDumpPatchSpans(patches, "after MergeAntiparallel#1");
                var mergeSpan = isLarge ? System.Math.Max(wallSpanM, 4.5) : wallSpanM;
                if (mergeSpan > 0)
                {
                    patches = PatchMerger.MergeCoplanar(
                        patches, profile, gapM: 0.18, seamBridgeOnly: true, maxMergedSpanM: mergeSpan,
                        doorRegions: doorRegions);
                }
                else
                {
                    patches = PatchMerger.MergeCoplanar(
                        patches, profile, gapM: 0.18, seamBridgeOnly: true, doorRegions: doorRegions);
                }
                DebugDumpPatchSpans(patches, "after MergeCoplanar");

                patches = PatchMerger.MergeAntiparallel(patches, profile);
                DebugDumpPatchSpans(patches, "after MergeAntiparallel#2");

                if (!isLarge && wallSpanM > 0)
                {
                    patches = PatchMerger.MergeRoofPlateDuplicates(patches, profile).ToList();
                    patches = PatchMerger.MergeGableShadowPatches(patches, profile).ToList();
                    patches = PatchEndWallMerger.MergeEndCaps(workMesh, patches, profile);
                    patches = PatchEndCapTagger.TagEndCaps(workMesh, patches, profile);
                    patches = PatchMerger.MergeOverlappingRoofAndPlinthPatches(patches, profile).ToList();
                    DebugDumpPatchSpans(patches, "after MergeOverlappingRoofAndPlinthPatches");
                    patches = EnforceMaxWallPatchSpan(workMesh, patches, profile, wallSpanM);
                    patches = EnforceMaxSlopePatchSpan(workMesh, patches, profile, wallSpanM);
                    DebugDumpPatchSpans(patches, "after EnforceMaxSpan");
                    if (options.TargetComponentCount > 0)
                    {
                        var patchTarget = System.Math.Max(
                            1, options.TargetComponentCount - prefilter.Components.Count);
                        patches = TrimPatchCountToTarget(
                            workMesh, patches, profile, patchTarget, wallSpanM);
                        DebugDumpPatchSpans(patches, "after TrimPatchCountToTarget");
                    }
                }
            }
            else
            {
                patches = PatchMerger.MergeForBlind(patches, profile, doorRegions);
                patches = SpatialPatchSubdivider.Subdivide(
                    workMesh,
                    patches,
                    profile,
                    new SpatialSubdivisionOptions
                    {
                        MinGapM = options.SubdivisionGapM,
                        BinSizeM = options.SubdivisionBinM,
                        MinPatchAreaM2 = System.Math.Min(options.MinAreaM2, 0.05),
                        ReferenceObbs = options.ReferenceObbs,
                        DoorRegions = doorRegions,
                    });
            }
        }

        var components = new List<MeshComponent>(prefilter.Components);
        var skipped = 0;

        if (options.Decomposition == BuildingDecompositionMode.ReferenceGuided &&
            options.ReferenceFit == ReferenceFitMode.Snap &&
            options.ReferenceObbs is { Count: > 0 } snapObbs)
        {
            for (var i = 0; i < snapObbs.Count; i++)
            {
                var mesh = ReferenceObbSnap.BuildMesh(snapObbs[i]);
                var name = $"Component{i + 1:D2}";
                mesh.Name = name;
                components.Add(new MeshComponent { Name = name, Mesh = mesh });
            }
        }
        else
        {
            var compIdx = components.Count + 1;
            foreach (var patch in patches)
            {
                var box = BuildComponentMesh(patch, options, profile, workMesh);
                if (box == null)
                {
                    skipped++;
                    continue;
                }

                var name = $"Component{compIdx:D2}";
                box.Name = name;
                components.Add(new MeshComponent { Name = name, Mesh = box });
                compIdx++;
            }
        }

        if (options.Decomposition == BuildingDecompositionMode.RegionGuided
            && options.TargetComponentCount > 0
            && components.Count > options.TargetComponentCount)
        {
            var trimSource = options.ResolutionSource ?? resolutionLod;
            components = RegionComponentTrimmer.TrimToTarget(
                trimSource, components, options.TargetComponentCount, profile);
        }

        var geometry = MergeComponents(components);
        ApplyDayZProperties(geometry);

        if (options.BlindComponentRefit && options.ResolutionSource != null && components.Count > 0)
        {
            // disabled — nearest-centroid refit degrades OBB vs reference
        }

        DoorValidationResult? doorValidation = null;
        if (options.RequireDoorVertices)
        {
            doorValidation = DoorValidator.Validate(resolutionLod, options.ExpectedDoorCount);
        }

        return new BuildingGeometryResult
        {
            GeometryLod = geometry,
            Components = components,
            Patches = patches,
            DoorValidation = doorValidation,
            SkippedPatches = skipped,
            BuildingProfile = profile,
        };
    }

    private static (double WallSpanM, bool IsLarge) ResolveFaceDrivenWallSpan(
        BuildingMeshProfile profile,
        BuildingGeometryOptions options)
    {
        var wallSpanM = options.WallSegmentMaxSpanM is > 0
            ? options.WallSegmentMaxSpanM.Value
            : SegmentSpanCalibrator.EstimateWallMaxSpanM(profile, options.TargetComponentCount);
        var footprintLong = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);
        var isLarge = footprintLong > 10.0 || options.TargetComponentCount > 40;
        var spanFactor = SegmentSpanHeuristic.ClampSpanFactor(
            options.WallSegmentTightFactor > 0 ? options.WallSegmentTightFactor : 1.0);
        if (!isLarge && wallSpanM > 0 && System.Math.Abs(spanFactor - 1.0) > 1e-6)
            wallSpanM *= spanFactor;
        return (wallSpanM, isLarge);
    }

    /// <summary>
    /// TEMPORARY diagnostic for the region-marking-workflow investigation (Session 2):
    /// dumps per-patch AABB spans to stderr when CDM_GEO_DEBUG=1, so runaway
    /// merges that produce building-spanning boxes can be spotted stage-by-stage.
    /// Safe no-op otherwise.
    /// </summary>
    private static void DebugDumpPatchSpans(IReadOnlyList<PatchCluster> patches, string label)
    {
        if (Environment.GetEnvironmentVariable("CDM_GEO_DEBUG") != "1")
            return;
        Console.Error.WriteLine($"[geo-debug] {label}: {patches.Count} patches");
        foreach (var p in patches)
        {
            var verts = p.WorldVertices;
            if (verts.Count == 0)
                continue;
            var xs = verts.Select(v => v.X).ToList();
            var ys = verts.Select(v => v.Y).ToList();
            var zs = verts.Select(v => v.Z).ToList();
            Console.Error.WriteLine(
                $"[geo-debug]   kind={p.SurfaceKind} faces={p.FaceIndices.Count} normal=({p.DominantNormal.X:F2},{p.DominantNormal.Y:F2},{p.DominantNormal.Z:F2}) "
                + $"span=({xs.Max() - xs.Min():F2},{ys.Max() - ys.Min():F2},{zs.Max() - zs.Min():F2})");
        }
    }

    /// <summary>Merge near-duplicates when clearly over target; never split up for count. Protected patches stay intact.</summary>
    private static IReadOnlyList<PatchCluster> TrimPatchCountToTarget(
        MeshData workMesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        int targetCount,
        double wallSpanM)
    {
        var (protectedPatches, mergeable) = PartitionMergeProtected(workMesh, patches, profile);
        var protectedList = PatchMerger.MergeNearDuplicatePatches(
            protectedPatches, profile, maxCenterGapM: 0.68).ToList();
        var list = mergeable.ToList();
        var total = protectedList.Count + list.Count;
        var mergeHi = System.Math.Max(1, targetCount + 1 - protectedList.Count);

        if (total <= targetCount + 2)
        {
            list = PatchMerger.MergeNearDuplicatePatches(list, profile, maxCenterGapM: 0.42).ToList();
            var light = new List<PatchCluster>(protectedList);
            light.AddRange(list);
            light = PatchMerger.MergeGableShadowPatches(light, profile).ToList();
            light = PatchMerger.MergeRoofPlateDuplicates(light, profile).ToList();
            if (light.Count > targetCount)
                light = PatchMerger.MergeOverlappingFootprintDuplicates(light, profile).ToList();
            return PatchMerger.MergeColocatedPatches(light, profile, maxCenterGapM: 0.18).ToList();
        }

        for (var pass = 0; pass < 6 && list.Count > mergeHi; pass++)
        {
            var before = list.Count;
            var excess = list.Count - mergeHi;
            var gap = excess > 6 ? 0.24 + pass * 0.04 : 0.18 + pass * 0.03;
            var bridgeOnly = excess <= 2;
            var dupGap = excess > 3 ? 0.72 : 0.52;
            list = PatchMerger.MergeCoplanar(
                list, profile, gapM: gap, seamBridgeOnly: bridgeOnly,
                maxMergedSpanM: wallSpanM * (1.05 + pass * 0.06)).ToList();
            list = PatchMerger.MergeAntiparallel(list, profile).ToList();
            list = PatchMerger.MergeNearDuplicatePatches(list, profile, maxCenterGapM: dupGap).ToList();
            if (list.Count >= before && pass >= 2)
            {
                list = PatchMerger.MergeNearDuplicatePatches(list, profile, maxCenterGapM: 0.85).ToList();
                if (list.Count >= before)
                    break;
            }
        }

        if (protectedList.Count + list.Count > targetCount + 2)
        {
            list = PatchMerger.MergeNearDuplicatePatches(list, profile, maxCenterGapM: 0.72).ToList();
            list = PatchMerger.MergeCoplanar(
                list, profile, gapM: 0.22, seamBridgeOnly: true,
                maxMergedSpanM: wallSpanM * 1.15).ToList();
        }

        var result = new List<PatchCluster>(protectedList.Count + list.Count);
        result.AddRange(protectedList);
        result.AddRange(list);
        var         merged = PatchMerger.MergeGableShadowPatches(result, profile).ToList();
        merged = PatchMerger.MergeRoofPlateDuplicates(merged, profile).ToList();
        merged = PatchMerger.MergeColocatedPatches(merged, profile, maxCenterGapM: 0.12).ToList();
        if (merged.Count > targetCount + 1)
            merged = PatchMerger.MergeTinyFragments(merged, profile).ToList();
        if (merged.Count > targetCount + 1)
        {
            var (prot2, rest2) = PartitionMergeProtected(workMesh, merged, profile);
            rest2 = PatchMerger.MergeNearDuplicatePatches(rest2, profile, maxCenterGapM: 0.72).ToList();
            merged = new List<PatchCluster>(prot2);
            merged.AddRange(rest2);
            merged = PatchMerger.MergeGableShadowPatches(merged, profile).ToList();
            if (merged.Count > targetCount + 1)
            {
                merged = PatchMerger.MergeNearDuplicatePatches(
                    merged, profile, maxCenterGapM: 0.92, minNormalDot: 0.45).ToList();
                merged = PatchMerger.MergeColocatedPatches(merged, profile, maxCenterGapM: 0.22).ToList();
            }
        }

        for (var finalPass = 0; finalPass < 5 && merged.Count > targetCount + 1; finalPass++)
        {
            merged = PatchMerger.MergeNearDuplicatePatches(
                merged, profile, maxCenterGapM: 0.72 + finalPass * 0.05, minNormalDot: 0.50).ToList();
            if (finalPass <= 1)
                merged = PatchMerger.MergeGableShadowPatches(merged, profile).ToList();
            merged = PatchMerger.MergeRoofPlateDuplicates(merged, profile).ToList();
            merged = PatchMerger.MergeColocatedPatches(merged, profile, maxCenterGapM: 0.16).ToList();
        }

        if (merged.Count > targetCount)
            merged = PatchMerger.MergeOverlappingFootprintDuplicates(merged, profile).ToList();
        if (merged.Count > targetCount + 1)
        {
            merged = PatchMerger.MergeOverlappingFootprintDuplicates(
                merged, profile, minOverlapRatio: 0.74, maxPlanCenterGapM: 0.48, minAreaRatio: 0.65).ToList();
            merged = PatchMerger.MergeNearDuplicatePatches(
                merged, profile, maxCenterGapM: 0.88, minNormalDot: 0.42).ToList();
        }

        if (merged.Count > targetCount)
        {
            merged = PatchMerger.MergeTinyFragments(
                merged, profile, maxAreaM2: 0.48, maxCenterGapM: 0.88).ToList();
            merged = PatchMerger.MergeColocatedPatches(merged, profile, maxCenterGapM: 0.18).ToList();
        }

        return merged;
    }

    private static (List<PatchCluster> Protected, List<PatchCluster> Mergeable) PartitionMergeProtected(
        MeshData workMesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile)
    {
        var prot = new List<PatchCluster>();
        var rest = new List<PatchCluster>();
        foreach (var patch in patches)
        {
            if (PatchSurfaceClassifier.IsMergeProtected(patch.SurfaceKind))
            {
                prot.Add(patch);
                continue;
            }

            if (patch.GableEnd != GableEndKind.None && patch.AreaM2 >= 1.0)
            {
                prot.Add(patch);
                continue;
            }

            rest.Add(patch);
        }

        return (prot, rest);
    }

    private static IReadOnlyList<PatchCluster> EnforceMaxWallPatchSpan(
        MeshData workMesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        double wallSpanM)
    {
        var limit = wallSpanM * 1.05;
        var maxHeight = PatchHeightSplitter.MaxWallBandHeightM(profile, wallSpanM) * 1.05;
        var result = new List<PatchCluster>();
        var changed = false;

        foreach (var patch in patches)
        {
            if (patch.SurfaceKind != PatchSurfaceKind.Wall)
            {
                result.Add(patch);
                continue;
            }

            var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(workMesh, patch);
            var heightSpan = PatchHeightSplitter.HeightSpanM(verts, profile);
            var obb = FaceBoundsObbFitter.FitPatch(verts, patch.DominantNormal, profile, patch.SurfaceKind);
            var inPlaneSpan = obb == null
                ? PatchDiagnostics.AnalyzeOne(workMesh, patch, 0, profile).TangentSpanM
                : System.Math.Max(obb.ExtentU, obb.ExtentV) * 2.0;

            if (obb != null && inPlaneSpan <= limit && heightSpan <= maxHeight)
            {
                result.Add(patch);
                continue;
            }

            changed = true;
            IReadOnlyList<PatchCluster> split;
            if (heightSpan > maxHeight)
            {
                split = PatchHeightSplitter.SplitByHeightBands(
                    workMesh, patch, profile, PatchHeightSplitter.MaxWallBandHeightM(profile, wallSpanM));
            }
            else
            {
                split = SpatialPatchSubdivider.Subdivide(
                    workMesh,
                    new[] { patch },
                    profile,
                    new SpatialSubdivisionOptions
                    {
                        MinGapM = 0.5,
                        BinSizeM = 0.12,
                        MinPatchAreaM2 = 0.04,
                        MaxInPlaneSpanM = wallSpanM,
                        SpanFallbackOnly = true,
                        WallGapAndDoorCutsOnly = true,
                    });
            }

            result.AddRange(split.Count > 1 ? split : new[] { patch });
        }

        if (!changed)
            return patches;

        result = PatchMerger.MergeAntiparallel(result, profile).ToList();
        return result;
    }

    private static IReadOnlyList<PatchCluster> EnforceMaxSlopePatchSpan(
        MeshData workMesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        double wallSpanM)
    {
        var limit = PatchHeightSplitter.MaxSlopeInPlaneSpanM(profile, wallSpanM) * 1.05;
        var maxHeight = PatchHeightSplitter.MaxSlopeHeightSpanM(profile);
        var result = new List<PatchCluster>();
        var changed = false;

        foreach (var patch in patches)
        {
            if (patch.SurfaceKind != PatchSurfaceKind.Slope)
            {
                result.Add(patch);
                continue;
            }

            if (patch.GableEnd != GableEndKind.None)
            {
                result.Add(patch);
                continue;
            }

            var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(workMesh, patch);
            var heightSpan = PatchHeightSplitter.HeightSpanM(verts, profile);
            if (patch.AreaM2 >= 1.4 && heightSpan >= 1.0)
            {
                result.Add(patch);
                continue;
            }

            var diag = PatchDiagnostics.AnalyzeOne(workMesh, patch, 0, profile);
            var span = System.Math.Max(diag.TangentSpanM, heightSpan);

            if (span <= limit && heightSpan <= maxHeight)
            {
                result.Add(patch);
                continue;
            }

            changed = true;
            IReadOnlyList<PatchCluster> split;
            if (heightSpan > maxHeight)
            {
                split = PatchHeightSplitter.SplitByHeightBands(
                    workMesh, patch, profile, maxHeight * 0.9, minAreaM2: 0.03);
            }
            else
            {
                split = SpatialPatchSubdivider.Subdivide(
                    workMesh,
                    new[] { patch },
                    profile,
                    new SpatialSubdivisionOptions
                    {
                        MinGapM = 0.45,
                        BinSizeM = 0.12,
                        MinPatchAreaM2 = 0.03,
                        MaxInPlaneSpanM = limit * 0.92,
                        SpanFallbackOnly = true,
                        WallGapAndDoorCutsOnly = true,
                    });
            }

            result.AddRange(split.Count > 1 ? split : new[] { patch });
        }

        if (!changed)
            return patches;

        return result;
    }

    private static double FaceDrivenDecompositionMinAreaM2(BuildingGeometryOptions options)
    {
        if (options.Decomposition != BuildingDecompositionMode.FaceDriven
            && options.Decomposition != BuildingDecompositionMode.RegionGuided)
            return options.MinAreaM2;
        // Adaptive search may use large min_area for scoring; walls are often <0.3 m² per strip.
        return System.Math.Min(options.MinAreaM2, 0.12);
    }

    private static MeshData? BuildComponentMesh(
        PatchCluster patch,
        BuildingGeometryOptions options,
        BuildingMeshProfile profile,
        MeshData? patchSourceMesh = null)
    {
        if (patch.ReferenceIndex >= 0 &&
            options.ReferenceObbs is { Count: > 0 } refObbs &&
            patch.ReferenceIndex < refObbs.Count)
        {
            var refObb = refObbs[patch.ReferenceIndex];
            return options.ReferenceFit switch
            {
                ReferenceFitMode.Snap => ReferenceObbSnap.BuildMesh(refObb),
                ReferenceFitMode.Constrained => ConstrainedObbFitter.BuildPatchMesh(
                    patch.WorldVertices,
                    patch.DominantNormal,
                    refObb,
                    profile,
                    options.ReferenceBlendWeight),
                _ => ObbFitter.BuildPatchMesh(patch.WorldVertices, patch.DominantNormal, profile),
            };
        }

        if (options.ResolutionGuidedObbFit)
        {
            var src = patchSourceMesh ?? options.ResolutionSource;
            if (src != null)
            {
                var verts = CollectObbVertices(src, patch, profile);
                return FaceBoundsObbFitter.BuildPatchMesh(verts, patch.DominantNormal, profile, patch.SurfaceKind)
                    ?? ObbFitter.BuildPatchMeshTight(verts, patch.DominantNormal, profile);
            }
        }

        var fitVerts = patchSourceMesh != null
            ? CollectObbVertices(patchSourceMesh, patch, profile)
            : patch.WorldVertices;

        return FaceBoundsObbFitter.BuildPatchMesh(fitVerts, patch.DominantNormal, profile, patch.SurfaceKind)
            ?? ObbFitter.BuildPatchMeshTight(fitVerts, patch.DominantNormal, profile);
    }

    private static List<Vec3> CollectObbVertices(
        MeshData mesh,
        PatchCluster patch,
        BuildingMeshProfile profile) =>
        patch.SurfaceKind == PatchSurfaceKind.Slope
            ? FaceBoundsObbFitter.CollectGableObbVertices(mesh, patch, profile)
            : FaceBoundsObbFitter.CollectPatchFaceVertices(mesh, patch);

    public static MeshData MergeComponents(IReadOnlyList<MeshComponent> components)
    {
        var geometry = new MeshData { Name = "Geometry" };
        foreach (var comp in components)
        {
            var offset = geometry.Vertices.Count;
            var map = new int[comp.Mesh.Vertices.Count];
            for (var i = 0; i < comp.Mesh.Vertices.Count; i++)
            {
                map[i] = geometry.Vertices.Count;
                geometry.Vertices.Add(comp.Mesh.Vertices[i]);
            }

            foreach (var face in comp.Mesh.Faces)
                geometry.Faces.Add(face.Select(vi => map[vi]).ToArray());

            geometry.VertexGroups[comp.Name] = map.ToList();
        }

        return geometry;
    }

    private static void ApplyDayZProperties(MeshData geometry)
    {
        foreach (var (key, value) in GeometryLodConstants.DefaultObjectProperties)
            geometry.Properties[key] = value;
    }
}
