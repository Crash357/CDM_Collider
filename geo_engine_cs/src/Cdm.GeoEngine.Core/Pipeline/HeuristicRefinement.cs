using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Phase 2: local parameter search around the best coarse-grid candidate.</summary>
internal static class HeuristicRefinement
{
    public static (AdaptiveGenerationResult? Best, int ExtraEvaluations) RefineAround(
        MeshData resolutionLod,
        AdaptiveGenerationResult seed,
        CorpusReference? reference,
        string modelId,
        IReadOnlyList<OrientedBox>? refObbs,
        IReadOnlyList<OrientedBox>? refObbsForGen,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion> doors,
        int targetComponentCount,
        bool blindGeneration,
        MeshData? referenceGeometryLod = null)
    {
        var maxDiff = targetComponentCount > 0
            ? System.Math.Max(3, (int)System.Math.Ceiling(targetComponentCount * 0.12))
            : int.MaxValue;

        var best = seed;
        var bestQuality = seed.SearchQuality ?? SearchQualityScorer.Evaluate(
            resolutionLod, seed.Geometry, seed.BuildingProfile, seed.ObbGeometry,
            seed.Validation, targetComponentCount, maxDiff, blindScoring: blindGeneration);
        var extra = 0;

        if (blindGeneration && seed.Decomposition == BuildingDecompositionMode.FaceDriven)
        {
            foreach (var wallTight in SegmentSpanHeuristic.RefineSpanFactors(seed.WallSegmentTightFactor)
                         .Distinct())
            {
                if (System.Math.Abs(wallTight - seed.WallSegmentTightFactor) < 1e-6)
                    continue;

                extra++;
                var candidate = EvaluateCandidate(
                    resolutionLod, reference, modelId, refObbs, refObbsForGen,
                    profile, doors, targetComponentCount, blindGeneration, referenceGeometryLod,
                    seed.MinAreaM2, seed.AxisSpacingM, seed.SubdivisionGapM,
                    seed.Decomposition, seed.ReferenceFit, seed.ReferenceBlendWeight,
                    wallTight);

                if (candidate == null)
                    continue;

                if (!IsBetterCandidate(candidate, best, blindGeneration, refObbs, referenceGeometryLod))
                    continue;

                best = candidate;
                bestQuality = candidate.SearchQuality ?? bestQuality;
            }
        }

        var minAreaNeighbors = Neighbors(seed.MinAreaM2, 0.75, 1.0, 1.33);
        var blendNeighbors = Neighbors(seed.ReferenceBlendWeight, 0.85, 1.0, 1.15).Distinct().ToArray();
        var spacingNeighbors = Neighbors(seed.AxisSpacingM, 0.85, 1.0, 1.15);
        var gapNeighbors = Neighbors(seed.SubdivisionGapM, 0.85, 1.0, 1.15);

        foreach (var minArea in minAreaNeighbors)
        {
            foreach (var blend in blendNeighbors)
            {
                if (seed.ReferenceFit != ReferenceFitMode.Free && blend <= 0)
                    continue;

                var blendClamped = seed.ReferenceFit == ReferenceFitMode.Snap
                    ? 1.0
                    : System.Math.Clamp(blend, 0.35, 1.0);

                foreach (var spacing in spacingNeighbors)
                {
                    foreach (var gap in gapNeighbors)
                    {
                        extra++;
                        var candidate = EvaluateCandidate(
                            resolutionLod, reference, modelId, refObbs, refObbsForGen,
                            profile, doors, targetComponentCount, blindGeneration, referenceGeometryLod,
                            minArea, spacing, gap, seed.Decomposition, seed.ReferenceFit, blendClamped,
                            seed.WallSegmentTightFactor);

                        if (candidate == null)
                            continue;

                        var quality = candidate.SearchQuality ?? SearchQualityScorer.Evaluate(
                            resolutionLod, candidate.Geometry, candidate.BuildingProfile,
                            candidate.ObbGeometry, candidate.Validation,
                            targetComponentCount, maxDiff, blindScoring: blindGeneration);
                        var pick = IsBetterCandidate(candidate, best, blindGeneration, refObbs, referenceGeometryLod);
                        if (!pick)
                            continue;

                        best = candidate;
                        bestQuality = quality;
                    }
                }
            }
        }

        return (best, extra);
    }

    private static bool IsBetterCandidate(
        AdaptiveGenerationResult candidate,
        AdaptiveGenerationResult best,
        bool blindGeneration,
        IReadOnlyList<OrientedBox>? refObbs,
        MeshData? referenceGeometryLod)
    {
        if (blindGeneration && (refObbs is { Count: > 0 } || referenceGeometryLod != null
                                || candidate.GeometricCompare != null || best.GeometricCompare != null))
            return AdaptiveBuildingGenerator.CompareBlindQuality(candidate, best);

        var cq = candidate.SearchQuality;
        var bq = best.SearchQuality;
        if (cq != null && bq != null)
            return SearchQualityScorer.IsBetterQuality(cq, bq);
        return cq != null;
    }

    private static AdaptiveGenerationResult? EvaluateCandidate(
        MeshData resolutionLod,
        CorpusReference? reference,
        string modelId,
        IReadOnlyList<OrientedBox>? refObbs,
        IReadOnlyList<OrientedBox>? refObbsForGen,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion> doors,
        int targetComponentCount,
        bool blindGeneration,
        MeshData? referenceGeometryLod,
        double minArea,
        double spacing,
        double gap,
        BuildingDecompositionMode mode,
        ReferenceFitMode fitMode,
        double blend,
        double wallSegmentTightFactor = 1.0)
    {
        if (fitMode != ReferenceFitMode.Free && mode != BuildingDecompositionMode.ReferenceGuided)
            return null;

        var result = BuildingGeometryEngine.Generate(resolutionLod, new BuildingGeometryOptions
        {
            MinAreaM2 = minArea,
            AxisSpacingM = spacing,
            SubdivisionGapM = gap,
            SubdivisionBinM = System.Math.Min(0.15, gap * 0.6),
            Decomposition = mode,
            ReferenceFit = fitMode,
            ReferenceBlendWeight = blend,
            RequireDoorVertices = false,
            Profile = profile,
            ReferenceObbs = refObbsForGen,
            DoorRegions = doors,
            ResolutionSource = blindGeneration ? resolutionLod : null,
            ResolutionGuidedObbFit = blindGeneration,
            BlindComponentRefit = blindGeneration,
            TargetComponentCount = targetComponentCount,
            WallSegmentTightFactor = wallSegmentTightFactor,
        });

        if (result.Components.Count == 0)
            return null;

        var validation = GeometryValidationReport.Compare(
            modelId, reference, result.Components.Count,
            result.GeometryLod.VertexCount, result.SkippedPatches, minArea, spacing);

        ObbGeometryScore? obbScore = null;
        CoverageScore? coverageScore = null;
        if (refObbs is { Count: > 0 })
        {
            var genObbs = ObbGeometryComparer.FromComponents(result.Components, profile);
            obbScore = ObbGeometryComparer.Compare(refObbs, genObbs);
            coverageScore = ResolutionCoverageScorer.Score(resolutionLod, genObbs);
        }
        else
        {
            coverageScore = ResolutionCoverageScorer.ScoreFromComponents(
                resolutionLod, result.Components, profile);
        }

        var maxDiff = targetComponentCount > 0
            ? System.Math.Max(3, (int)System.Math.Ceiling(targetComponentCount * 0.12))
            : int.MaxValue;
        var searchQuality = SearchQualityScorer.Evaluate(
            resolutionLod, result, profile, obbScore, validation,
            targetComponentCount, maxDiff, blindScoring: blindGeneration);

        GeometricCompareResult? geometricScore = null;
        if (referenceGeometryLod != null && blindGeneration)
        {
            geometricScore = GeometricComponentComparer.Compare(
                referenceGeometryLod, result.Components);
        }

        return new AdaptiveGenerationResult
        {
            Geometry = result,
            Validation = validation,
            Reference = reference,
            BuildingProfile = profile,
            ObbGeometry = obbScore,
            GeometricCompare = geometricScore,
            Coverage = coverageScore,
            SearchQuality = searchQuality,
            MinAreaM2 = minArea,
            AxisSpacingM = spacing,
            SubdivisionGapM = gap,
            Decomposition = mode,
            ReferenceFit = fitMode,
            ReferenceBlendWeight = blend,
            WallSegmentTightFactor = wallSegmentTightFactor,
        };
    }

    private static double[] Neighbors(double center, double lowMul, double midMul, double highMul)
    {
        if (center <= 0)
            return new[] { center };
        return new[]
        {
            System.Math.Max(0.01, center * lowMul),
            center * midMul,
            center * highMul,
        };
    }
}
