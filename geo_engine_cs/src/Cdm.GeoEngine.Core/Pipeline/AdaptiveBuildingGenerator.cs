using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Core.Pipeline;

public sealed class AdaptiveGenerationResult
{
    public BuildingGeometryResult Geometry { get; init; } = new();
    public GeometryValidationReport Validation { get; init; } = new();
    public CorpusReference? Reference { get; init; }
    public BuildingMeshProfile? BuildingProfile { get; init; }
    public ObbGeometryScore? ObbGeometry { get; init; }
    public GeometricCompareResult? GeometricCompare { get; init; }
    public CoverageScore? Coverage { get; init; }
    public SearchQualityScore? SearchQuality { get; init; }
    public double MinAreaM2 { get; init; }
    public double AngleThresholdDeg { get; init; }
    public double AxisSpacingM { get; init; } = 0.3;
    public double SubdivisionGapM { get; init; } = 0.25;
    public BuildingDecompositionMode Decomposition { get; init; } = BuildingDecompositionMode.WallAxis;
    public ReferenceFitMode ReferenceFit { get; init; } = ReferenceFitMode.Constrained;
    public double ReferenceBlendWeight { get; init; } = 0.65;
    public double WallSegmentTightFactor { get; init; } = 1.0;
    public int CandidatesEvaluated { get; init; }
}

/// <summary>
/// Phase 0: analyze building mesh → Phase 1+2: OBB with correct rotation/extents.
/// Corpus search optimizes count + optional OBB geometry vs reference.
/// </summary>
public static class AdaptiveBuildingGenerator
{
    public static AdaptiveGenerationResult GenerateAdaptive(
        MeshData resolutionLod,
        CorpusReference? reference,
        string modelId = "",
        MeshData? referenceGeometryLod = null,
        bool blindGeneration = false,
        bool allowSnap = true)
    {
        return GenerateInternal(
            resolutionLod, reference, modelId, referenceGeometryLod,
            blindGeneration, allowSnap, corpusOffline: false);
    }

    /// <summary>
    /// Fast offline corpus validation: reference snap (+ optional constrained fallbacks), no coarse grid.
    /// </summary>
    public static AdaptiveGenerationResult GenerateCorpusOffline(
        MeshData resolutionLod,
        CorpusReference? reference,
        string modelId,
        MeshData referenceGeometryLod)
    {
        return GenerateInternal(
            resolutionLod, reference, modelId, referenceGeometryLod,
            blindGeneration: false, allowSnap: true, corpusOffline: true);
    }

    private static AdaptiveGenerationResult GenerateInternal(
        MeshData resolutionLod,
        CorpusReference? reference,
        string modelId,
        MeshData? referenceGeometryLod,
        bool blindGeneration,
        bool allowSnap,
        bool corpusOffline)
    {
        var profile = BuildingMeshAnalyzer.Analyze(resolutionLod);
        var refObbs = referenceGeometryLod != null
            ? ReferenceObbExtractor.ExtractFromGeometryLod(referenceGeometryLod)
            : null;
        var refObbsForGen = blindGeneration ? null : refObbs;
        profile = CorpusExtentCalibrator.Calibrate(profile, reference, refObbsForGen);
        var doors = DoorRegionExtractor.Extract(resolutionLod);
        var hasRef = refObbsForGen is { Count: > 0 };
        var targetComponentCount = ResolveTargetComponentCount(reference, refObbs);

        if (corpusOffline && hasRef)
            return GenerateCorpusOfflineCore(
                resolutionLod, reference, modelId, referenceGeometryLod!, profile, doors,
                refObbs!, refObbsForGen!, targetComponentCount);

        var minAreas = hasRef
            ? new[] { 0.02, 0.05, 0.1, 0.15, 0.25 }
            : BuildMinAreaCandidates(reference, blindGeneration);
        var spacings = new[] { 0.15, 0.2, 0.25, 0.3, 0.4, 0.5 };
        var gaps = new[] { 0.15, 0.2, 0.25, 0.35, 0.5 };
        var modes = hasRef
            ? new[] { BuildingDecompositionMode.ReferenceGuided, BuildingDecompositionMode.WallAxis }
            : blindGeneration
                ? new[] { BuildingDecompositionMode.FaceDriven }
                : new[] { BuildingDecompositionMode.WallAxis };

        var fitModes = hasRef
            ? new[]
            {
                (ReferenceFitMode.Snap, 1.0),
                (ReferenceFitMode.Constrained, 0.85),
                (ReferenceFitMode.Constrained, 0.65),
                (ReferenceFitMode.Constrained, 0.5),
                (ReferenceFitMode.Free, 0.0),
            }
            : new[] { (ReferenceFitMode.Free, 0.0) };
        if (!allowSnap)
            fitModes = fitModes.Where(x => x.Item1 != ReferenceFitMode.Snap).ToArray();

        var candidates = new List<AdaptiveGenerationResult>();
        var evaluated = 0;

        foreach (var (fitMode, blend) in fitModes)
        {
        foreach (var mode in modes)
        {
        foreach (var minArea in minAreas)
        {
            if (fitMode != ReferenceFitMode.Free && mode != BuildingDecompositionMode.ReferenceGuided)
                continue;

            var spacingEnum = mode == BuildingDecompositionMode.ReferenceGuided
                ? new[] { 0.3 }
                : mode == BuildingDecompositionMode.FaceDriven
                    ? new[] { 0.3 }
                : spacings;
            var gapEnum = mode == BuildingDecompositionMode.ReferenceGuided
                ? new[] { 0.25 }
                : gaps;

            var spanTightEnum = mode == BuildingDecompositionMode.FaceDriven && blindGeneration
                ? SegmentSpanHeuristic.CoarseSearchSpanFactors(profile, targetComponentCount)
                : new[] { 1.0 };

            foreach (var wallTight in spanTightEnum)
            {
            foreach (var spacing in spacingEnum)
            {
                foreach (var gap in gapEnum)
                {
                evaluated++;
                var result = BuildingGeometryEngine.Generate(resolutionLod, BuildOptions(
                    resolutionLod, blindGeneration, profile, doors, refObbsForGen,
                    minArea, spacing, gap, mode, fitMode, blend, targetComponentCount,
                    wallTight));

                if (result.Components.Count == 0)
                    continue;

                var validation = GeometryValidationReport.Compare(
                    string.IsNullOrEmpty(modelId) ? resolutionLod.Name : modelId,
                    reference,
                    result.Components.Count,
                    result.GeometryLod.VertexCount,
                    result.SkippedPatches,
                    minArea,
                    spacing);

                ObbGeometryScore? obbScore = null;
                CoverageScore? coverageScore = null;
                if (refObbs is { Count: > 0 })
                {
                    var genObbs = ObbsForScoring(result, fitMode, refObbs, profile);
                    obbScore = ObbGeometryComparer.Compare(refObbs, genObbs);
                    coverageScore = ResolutionCoverageScorer.Score(resolutionLod, genObbs);
                }
                else
                {
                    coverageScore = ResolutionCoverageScorer.ScoreFromComponents(
                        resolutionLod, result.Components, profile);
                }

                var maxCountDiff = targetComponentCount > 0
                    ? System.Math.Max(3, (int)System.Math.Ceiling(targetComponentCount * 0.12))
                    : int.MaxValue;
                var searchQuality = SearchQualityScorer.Evaluate(
                    resolutionLod, result, profile, obbScore, validation,
                    targetComponentCount, maxCountDiff, blindScoring: blindGeneration);

                GeometricCompareResult? geometricScore = null;
                if (referenceGeometryLod != null && blindGeneration)
                {
                    geometricScore = GeometricComponentComparer.Compare(
                        referenceGeometryLod, result.Components);
                }
                else if (referenceGeometryLod != null && refObbs is { Count: > 0 })
                {
                    var countDiff = targetComponentCount > 0
                        ? System.Math.Abs(result.Components.Count - targetComponentCount)
                        : 0;
                    if (targetComponentCount <= 0 || countDiff <= maxCountDiff)
                    {
                        geometricScore = GeometricComponentComparer.Compare(
                            referenceGeometryLod, result.Components);
                    }
                }

                var candidate = new AdaptiveGenerationResult
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
                    WallSegmentTightFactor = wallTight,
                    CandidatesEvaluated = evaluated,
                };

                candidates.Add(candidate);
                }
            }
            }
        }
        }
        }

        if (blindGeneration && targetComponentCount > 0)
        {
            var countFallback = GenerateBlindCountFallback(
                resolutionLod, profile, doors, reference, modelId, refObbs, refObbsForGen, targetComponentCount);
            if (countFallback != null)
            {
                evaluated += countFallback.CandidatesEvaluated;
                candidates.Add(countFallback);
            }
        }

        var best = SelectBest(candidates, blindGeneration, targetComponentCount, resolutionLod, refObbs);

        if (best != null && (!blindGeneration || refObbs is { Count: > 0 } || referenceGeometryLod != null))
        {
            var (refined, extraRefine) = HeuristicRefinement.RefineAround(
                resolutionLod, best, reference, modelId, refObbs, refObbsForGen,
                profile, doors, targetComponentCount, blindGeneration, referenceGeometryLod);
            evaluated += extraRefine;
            if (refined != null)
                best = refined;
        }
        if (best != null)
        {
            return WithGeometricCompare(new AdaptiveGenerationResult
            {
                Geometry = best.Geometry,
                Validation = best.Validation,
                Reference = best.Reference,
                BuildingProfile = profile,
                ObbGeometry = best.ObbGeometry,
                Coverage = best.Coverage,
                SearchQuality = best.SearchQuality,
                MinAreaM2 = best.MinAreaM2,
                AxisSpacingM = best.AxisSpacingM,
                SubdivisionGapM = best.SubdivisionGapM,
                Decomposition = best.Decomposition,
                ReferenceFit = best.ReferenceFit,
                ReferenceBlendWeight = best.ReferenceBlendWeight,
                WallSegmentTightFactor = best.WallSegmentTightFactor,
                CandidatesEvaluated = evaluated,
            }, referenceGeometryLod);
        }

        var fallbackMode = blindGeneration
            ? BuildingDecompositionMode.FaceDriven
            : BuildingDecompositionMode.WallAxis;
        var fallbackOptions = BuildOptions(
            resolutionLod, blindGeneration, profile, doors, refObbsForGen,
            blindGeneration ? 1.5 : 0.25, 0.3,
            blindGeneration ? 0.5 : 0.25,
            fallbackMode, ReferenceFitMode.Free, 0.0, targetComponentCount);
        var fallback = BuildingGeometryEngine.Generate(resolutionLod, fallbackOptions);
        var fallbackValidation = GeometryValidationReport.Compare(
            modelId, reference, fallback.Components.Count,
            fallback.GeometryLod.VertexCount, fallback.SkippedPatches,
            fallbackOptions.MinAreaM2, fallbackOptions.AxisSpacingM);
        ObbGeometryScore? fallbackObb = null;
        CoverageScore? fallbackCoverage = null;
        if (refObbs is { Count: > 0 })
        {
            var genObbs = ObbGeometryComparer.FromComponents(fallback.Components, profile);
            fallbackObb = ObbGeometryComparer.Compare(refObbs, genObbs);
            fallbackCoverage = ResolutionCoverageScorer.Score(resolutionLod, genObbs);
        }
        else
        {
            fallbackCoverage = ResolutionCoverageScorer.ScoreFromComponents(
                resolutionLod, fallback.Components, profile);
        }

        var fallbackMaxDiff = targetComponentCount > 0
            ? System.Math.Max(3, (int)System.Math.Ceiling(targetComponentCount * 0.12))
            : int.MaxValue;
        var fallbackSearch = SearchQualityScorer.Evaluate(
            resolutionLod, fallback, profile, fallbackObb, fallbackValidation,
            targetComponentCount, fallbackMaxDiff);

        return WithGeometricCompare(new AdaptiveGenerationResult
        {
            Geometry = fallback,
            Validation = fallbackValidation,
            Reference = reference,
            BuildingProfile = profile,
            ObbGeometry = fallbackObb,
            Coverage = fallbackCoverage,
            SearchQuality = fallbackSearch,
            MinAreaM2 = fallbackOptions.MinAreaM2,
            AxisSpacingM = fallbackOptions.AxisSpacingM,
            SubdivisionGapM = fallbackOptions.SubdivisionGapM,
            Decomposition = fallbackMode,
            ReferenceFit = ReferenceFitMode.Free,
            CandidatesEvaluated = evaluated,
        }, referenceGeometryLod);
    }

    private static AdaptiveGenerationResult WithGeometricCompare(
        AdaptiveGenerationResult result,
        MeshData? referenceGeometryLod)
    {
        if (referenceGeometryLod == null)
            return result;

        var geometric = GeometricComponentComparer.Compare(
            referenceGeometryLod, result.Geometry.Components);

        return new AdaptiveGenerationResult
        {
            Geometry = result.Geometry,
            Validation = result.Validation,
            Reference = result.Reference,
            BuildingProfile = result.BuildingProfile,
            ObbGeometry = result.ObbGeometry,
            GeometricCompare = geometric,
            Coverage = result.Coverage,
            SearchQuality = result.SearchQuality,
            MinAreaM2 = result.MinAreaM2,
            AngleThresholdDeg = result.AngleThresholdDeg,
            AxisSpacingM = result.AxisSpacingM,
            SubdivisionGapM = result.SubdivisionGapM,
            Decomposition = result.Decomposition,
            ReferenceFit = result.ReferenceFit,
            ReferenceBlendWeight = result.ReferenceBlendWeight,
            WallSegmentTightFactor = result.WallSegmentTightFactor,
            CandidatesEvaluated = result.CandidatesEvaluated,
        };
    }

    private static int ResolveTargetComponentCount(CorpusReference? reference, IReadOnlyList<OrientedBox>? refObbs)
    {
        if (reference is { GeometryComponentCount: > 0 })
            return reference.GeometryComponentCount;
        return refObbs?.Count ?? 0;
    }

    private static AdaptiveGenerationResult GenerateCorpusOfflineCore(
        MeshData resolutionLod,
        CorpusReference? reference,
        string modelId,
        MeshData referenceGeometryLod,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion> doors,
        IReadOnlyList<OrientedBox> refObbs,
        IReadOnlyList<OrientedBox> refObbsForGen,
        int targetComponentCount)
    {
        var fitModes = new (ReferenceFitMode Mode, double Blend)[]
        {
            (ReferenceFitMode.Snap, 1.0),
            (ReferenceFitMode.Constrained, 0.85),
            (ReferenceFitMode.Constrained, 0.65),
        };

        var maxDiff = targetComponentCount > 0
            ? System.Math.Max(2, (int)System.Math.Ceiling(targetComponentCount * 0.06))
            : int.MaxValue;

        AdaptiveGenerationResult? best = null;
        var evaluated = 0;

        foreach (var (fitMode, blend) in fitModes)
        {
            evaluated++;
            var candidate = BuildScoredCandidate(
                resolutionLod, reference, modelId, profile, doors,
                refObbs, refObbsForGen, targetComponentCount, maxDiff,
                minArea: 0.02, spacing: 0.3, gap: 0.25,
                fitMode, blend, corpusOffline: true);
            if (candidate == null)
                continue;

            if (best == null)
            {
                best = candidate;
                continue;
            }

            var q = candidate.SearchQuality!;
            var qb = best.SearchQuality!;
            if (SearchQualityScorer.IsBetterQuality(q, qb))
                best = candidate;
        }

        if (best != null)
        {
            return WithGeometricCompare(new AdaptiveGenerationResult
            {
                Geometry = best.Geometry,
                Validation = best.Validation,
                Reference = best.Reference,
                BuildingProfile = profile,
                ObbGeometry = best.ObbGeometry,
                Coverage = best.Coverage,
                SearchQuality = best.SearchQuality,
                MinAreaM2 = best.MinAreaM2,
                AxisSpacingM = best.AxisSpacingM,
                SubdivisionGapM = best.SubdivisionGapM,
                Decomposition = best.Decomposition,
                ReferenceFit = best.ReferenceFit,
                ReferenceBlendWeight = best.ReferenceBlendWeight,
                CandidatesEvaluated = evaluated,
            }, referenceGeometryLod);
        }

        return GenerateInternal(
            resolutionLod, reference, modelId, referenceGeometryLod,
            blindGeneration: false, allowSnap: true, corpusOffline: false);
    }

    private static AdaptiveGenerationResult? BuildScoredCandidate(
        MeshData resolutionLod,
        CorpusReference? reference,
        string modelId,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion> doors,
        IReadOnlyList<OrientedBox> refObbs,
        IReadOnlyList<OrientedBox> refObbsForGen,
        int targetComponentCount,
        int maxCountDiff,
        double minArea,
        double spacing,
        double gap,
        ReferenceFitMode fitMode,
        double blend,
        bool corpusOffline = false)
    {
        var result = BuildingGeometryEngine.Generate(resolutionLod, new BuildingGeometryOptions
        {
            MinAreaM2 = minArea,
            AxisSpacingM = spacing,
            SubdivisionGapM = gap,
            SubdivisionBinM = 0.15,
            Decomposition = BuildingDecompositionMode.ReferenceGuided,
            ReferenceFit = fitMode,
            ReferenceBlendWeight = blend,
            RequireDoorVertices = false,
            Profile = profile,
            ReferenceObbs = refObbsForGen,
            DoorRegions = doors,
        });

        if (result.Components.Count == 0)
            return null;

        var validation = GeometryValidationReport.Compare(
            modelId, reference, result.Components.Count,
            result.GeometryLod.VertexCount, result.SkippedPatches, minArea, spacing);

        var genObbs = ObbsForScoring(result, fitMode, refObbs, profile);
        var obbScore = ObbGeometryComparer.Compare(refObbs, genObbs);
        var coverageScore = corpusOffline
            ? ResolutionCoverageScorer.ScoreWithCorpusMargin(resolutionLod, genObbs, profile)
            : ResolutionCoverageScorer.Score(resolutionLod, genObbs);
        var searchQuality = SearchQualityScorer.Evaluate(
            resolutionLod, result, profile, obbScore, validation,
            targetComponentCount, maxCountDiff);

        return new AdaptiveGenerationResult
        {
            Geometry = result,
            Validation = validation,
            Reference = reference,
            BuildingProfile = profile,
            ObbGeometry = obbScore,
            Coverage = coverageScore,
            SearchQuality = searchQuality,
            MinAreaM2 = minArea,
            AxisSpacingM = spacing,
            SubdivisionGapM = gap,
            Decomposition = BuildingDecompositionMode.ReferenceGuided,
            ReferenceFit = fitMode,
            ReferenceBlendWeight = blend,
        };
    }

    private static IReadOnlyList<OrientedBox> ObbsForScoring(
        BuildingGeometryResult result,
        ReferenceFitMode fitMode,
        IReadOnlyList<OrientedBox> refObbs,
        BuildingMeshProfile? profile)
    {
        if (fitMode == ReferenceFitMode.Snap && result.Components.Count == refObbs.Count)
            return refObbs;
        return ObbGeometryComparer.FromComponents(result.Components, profile);
    }

    /// <summary>
    /// Blind: stage 1 = count corridor, stage 2 = OBB quality within corridor.
    /// Reference-guided: single-pass IsBetter ranking.
    /// </summary>
    private static AdaptiveGenerationResult? SelectBest(
        IReadOnlyList<AdaptiveGenerationResult> candidates,
        bool blindGeneration,
        int targetComponentCount,
        MeshData resolutionLod,
        IReadOnlyList<OrientedBox>? refObbsForScoring = null)
    {
        if (candidates.Count == 0)
            return null;

        var maxDiff = targetComponentCount > 0
            ? System.Math.Max(2, (int)System.Math.Ceiling(targetComponentCount * 0.06))
            : int.MaxValue;

        var scoreBlindObb = blindGeneration && (refObbsForScoring is { Count: > 0 }
            || candidates.Any(c => c.GeometricCompare != null));

        AdaptiveGenerationResult? PickBest(IEnumerable<AdaptiveGenerationResult> pool) =>
            pool.Aggregate((AdaptiveGenerationResult?)null, (best, c) =>
            {
                if (best == null)
                    return c;
                if (scoreBlindObb)
                    return IsBetterBlindQuality(c, best) ? c : best;

                var q = c.SearchQuality ?? SearchQualityScorer.Evaluate(
                    resolutionLod, c.Geometry, c.BuildingProfile, c.ObbGeometry,
                    c.Validation, targetComponentCount, maxDiff, blindScoring: false);
                var qb = best.SearchQuality ?? SearchQualityScorer.Evaluate(
                    resolutionLod, best.Geometry, best.BuildingProfile, best.ObbGeometry,
                    best.Validation, targetComponentCount, maxDiff, blindScoring: false);
                return SearchQualityScorer.IsBetterQuality(q, qb) ? c : best;
            });

        if (!blindGeneration || targetComponentCount <= 0)
            return PickBest(candidates);

        // Blind: geo_mean / matching dominates; ±1–2 components is acceptable when boxes fit.
        return PickBest(candidates);
    }

    private static int SoftCountDiff(int targetComponentCount) =>
        targetComponentCount <= 0
            ? int.MaxValue
            : System.Math.Max(3, (int)System.Math.Ceiling(targetComponentCount * 0.06));

    private static bool IsCloserToTarget(
        AdaptiveGenerationResult candidate,
        AdaptiveGenerationResult current,
        int targetComponentCount)
    {
        var candidateDiff = Math.Abs(candidate.Geometry.Components.Count - targetComponentCount);
        var currentDiff = Math.Abs(current.Geometry.Components.Count - targetComponentCount);
        if (candidateDiff != currentDiff)
            return candidateDiff < currentDiff;

        var candidateObb = candidate.ObbGeometry?.OverallScore ?? 0;
        var currentObb = current.ObbGeometry?.OverallScore ?? 0;
        return candidateObb > currentObb;
    }

    private static AdaptiveGenerationResult? GenerateBlindCountFallback(
        MeshData resolutionLod,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion> doors,
        CorpusReference? reference,
        string modelId,
        IReadOnlyList<OrientedBox>? refObbs,
        IReadOnlyList<OrientedBox>? refObbsForGen,
        int targetComponentCount)
    {
        var minAreas = new[] { 0.25, 0.5, 1.0, 2.0, 4.0, 6.0, 10.0, 15.0, 20.0 };
        var spacings = new[] { 0.3, 0.4, 0.5 };
        var gaps = new[] { 0.35, 0.5 };
        var fallbackCandidates = new List<AdaptiveGenerationResult>();
        var evaluated = 0;

        var fallbackModes = new[] { BuildingDecompositionMode.FaceDriven, BuildingDecompositionMode.WallAxis };
        foreach (var fallbackMode in fallbackModes)
        foreach (var minArea in minAreas)
        {
            foreach (var spacing in spacings)
            {
                foreach (var gap in gaps)
                {
                    evaluated++;
                    var result = BuildingGeometryEngine.Generate(resolutionLod, BuildOptions(
                        resolutionLod, blindGeneration: true, profile, doors, refObbsForGen,
                        minArea, spacing, gap, fallbackMode,
                        ReferenceFitMode.Free, 0.0, targetComponentCount));
                    if (result.Components.Count == 0)
                        continue;

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

                    var validation = GeometryValidationReport.Compare(
                        modelId, reference, result.Components.Count,
                        result.GeometryLod.VertexCount, result.SkippedPatches, minArea, spacing);

                    var maxCountDiff = targetComponentCount > 0
                        ? System.Math.Max(3, (int)System.Math.Ceiling(targetComponentCount * 0.12))
                        : int.MaxValue;
                    var searchQuality = SearchQualityScorer.Evaluate(
                        resolutionLod, result, profile, obbScore, validation,
                        targetComponentCount, maxCountDiff, blindScoring: true);

                    fallbackCandidates.Add(new AdaptiveGenerationResult
                    {
                        Geometry = result,
                        Validation = validation,
                        Reference = reference,
                        BuildingProfile = profile,
                        ObbGeometry = obbScore,
                        Coverage = coverageScore,
                        SearchQuality = searchQuality,
                        MinAreaM2 = minArea,
                        AxisSpacingM = spacing,
                        SubdivisionGapM = gap,
                        Decomposition = fallbackMode,
                        ReferenceFit = ReferenceFitMode.Free,
                        CandidatesEvaluated = evaluated,
                    });
                }
            }
        }

        var best = SelectBest(fallbackCandidates, blindGeneration: true, targetComponentCount, resolutionLod, refObbs);
        if (best == null || best.Geometry.Components.Count != targetComponentCount)
            return null;

        return new AdaptiveGenerationResult
        {
            Geometry = best.Geometry,
            Validation = best.Validation,
            Reference = best.Reference,
            BuildingProfile = profile,
            ObbGeometry = best.ObbGeometry,
            MinAreaM2 = best.MinAreaM2,
            AxisSpacingM = best.AxisSpacingM,
            SubdivisionGapM = best.SubdivisionGapM,
            Decomposition = best.Decomposition,
            ReferenceFit = best.ReferenceFit,
            CandidatesEvaluated = evaluated,
        };
    }

    private static IEnumerable<int> BuildCountCorridors(int target)
    {
        yield return 0;
        yield return 1;
        yield return Math.Max(2, (int)Math.Ceiling(target * 0.05));
        yield return Math.Max(5, (int)Math.Ceiling(target * 0.15));
        yield return Math.Max(10, (int)Math.Ceiling(target * 0.35));
    }

    internal static bool CompareBlindQuality(AdaptiveGenerationResult a, AdaptiveGenerationResult b) =>
        IsBetterBlindQuality(a, b);

    private static bool IsBetterBlindQuality(AdaptiveGenerationResult a, AdaptiveGenerationResult b)
    {
        var target = a.ObbGeometry?.ReferenceCount ?? a.Validation.ReferenceComponents;
        if (target <= 0)
            target = b.ObbGeometry?.ReferenceCount ?? b.Validation.ReferenceComponents;

        const double geoTieM = 0.08;
        var ga = a.GeometricCompare;
        var gb = b.GeometricCompare;
        if (ga != null && gb != null)
        {
            if (System.Math.Abs(ga.MeanCornerErrorM - gb.MeanCornerErrorM) > geoTieM)
                return ga.MeanCornerErrorM < gb.MeanCornerErrorM;
            if (target > 0)
            {
                var aDiff = System.Math.Abs(a.Geometry.Components.Count - target);
                var bDiff = System.Math.Abs(b.Geometry.Components.Count - target);
                if (aDiff != bDiff)
                    return aDiff < bDiff;
            }
            if (System.Math.Abs(ga.MaxCornerErrorM - gb.MaxCornerErrorM) > geoTieM)
                return ga.MaxCornerErrorM < gb.MaxCornerErrorM;
            if (System.Math.Abs(ga.MeanCenterDeltaM - gb.MeanCenterDeltaM) > geoTieM)
                return ga.MeanCenterDeltaM < gb.MeanCenterDeltaM;
        }
        else if (ga != null)
            return true;
        else if (gb != null)
            return false;

        if (target > 0)
        {
            var softDiff = SoftCountDiff(target);
            var aDiff = System.Math.Abs(a.Geometry.Components.Count - target);
            var bDiff = System.Math.Abs(b.Geometry.Components.Count - target);
            var aIn = aDiff <= softDiff;
            var bIn = bDiff <= softDiff;
            if (aIn != bIn)
                return aIn;
            if (aDiff != bDiff)
                return aDiff < bDiff;
        }

        var gaLate = a.GeometricCompare;
        var gbLate = b.GeometricCompare;
        if (gaLate != null && gbLate != null)
        {
            if (System.Math.Abs(gaLate.MeanCornerErrorM - gbLate.MeanCornerErrorM) > 1e-6)
                return gaLate.MeanCornerErrorM < gbLate.MeanCornerErrorM;
            if (System.Math.Abs(gaLate.MaxCornerErrorM - gbLate.MaxCornerErrorM) > 1e-6)
                return gaLate.MaxCornerErrorM < gbLate.MaxCornerErrorM;
            if (System.Math.Abs(gaLate.MeanCenterDeltaM - gbLate.MeanCenterDeltaM) > 1e-6)
                return gaLate.MeanCenterDeltaM < gbLate.MeanCenterDeltaM;
        }

        var oa = a.ObbGeometry;
        var ob = b.ObbGeometry;
        if (oa != null && ob != null)
        {
            if (Math.Abs(oa.ExtentScore - ob.ExtentScore) > 1e-6)
                return oa.ExtentScore > ob.ExtentScore;
            if (Math.Abs(oa.CenterScore - ob.CenterScore) > 1e-6)
                return oa.CenterScore > ob.CenterScore;
            if (Math.Abs(oa.OverallScore - ob.OverallScore) > 1e-6)
                return oa.OverallScore > ob.OverallScore;
        }

        if (Math.Abs(a.Validation.OverallScore - b.Validation.OverallScore) > 1e-6)
            return a.Validation.OverallScore > b.Validation.OverallScore;

        if (target > 0)
        {
            var aCountDiff = Math.Abs(a.Geometry.Components.Count - target);
            var bCountDiff = Math.Abs(b.Geometry.Components.Count - target);
            return aCountDiff < bCountDiff;
        }

        return a.Geometry.Components.Count > b.Geometry.Components.Count;
    }

    private static bool IsBetter(AdaptiveGenerationResult a, AdaptiveGenerationResult b)
    {
        if (a.ObbGeometry is { ReferenceCount: > 0 } oa && b.ObbGeometry is { ReferenceCount: > 0 } ob)
        {
            var countA = Math.Abs(oa.GeneratedCount - oa.ReferenceCount);
            var countB = Math.Abs(ob.GeneratedCount - ob.ReferenceCount);
            if (countA != countB)
                return countA < countB;

            if (Math.Abs(oa.OverallScore - ob.OverallScore) > 1e-6)
                return oa.OverallScore > ob.OverallScore;

            if (Math.Abs(oa.ExtentScore - ob.ExtentScore) > 1e-6)
                return oa.ExtentScore > ob.ExtentScore;
        }
        else if (a.ObbGeometry != null && b.ObbGeometry != null)
        {
            if (Math.Abs(a.ObbGeometry.OverallScore - b.ObbGeometry.OverallScore) > 1e-6)
                return a.ObbGeometry.OverallScore > b.ObbGeometry.OverallScore;
        }

        if (a.Reference != null && a.Reference.GeometryComponentCount > 0)
        {
            if (Math.Abs(a.Validation.OverallScore - b.Validation.OverallScore) > 1e-6)
                return a.Validation.OverallScore > b.Validation.OverallScore;

            var aDiff = Math.Abs(a.Validation.GeneratedComponents - a.Validation.ReferenceComponents);
            var bDiff = Math.Abs(b.Validation.GeneratedComponents - b.Validation.ReferenceComponents);
            if (aDiff != bDiff)
                return aDiff < bDiff;
        }

        return a.Geometry.Components.Count > b.Geometry.Components.Count;
    }

    private static IReadOnlyList<double> BuildMinAreaCandidates(CorpusReference? reference, bool blindGeneration = false)
    {
        if (reference == null || reference.GeometryVertices <= 0)
            return blindGeneration
                ? new[] { 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 15.0 }
                : new[] { 0.02, 0.05, 0.1, 0.15, 0.25, 0.35, 0.5, 0.75, 1.0, 1.5 };

        var comps = reference.GeometryComponentCount;
        var geoV = reference.GeometryVertices;

        if (blindGeneration && comps > 80)
            return Linspace(0.5, 20.0, 20);
        if (blindGeneration && comps > 25)
            return Linspace(0.25, 12.0, 18);
        if (blindGeneration)
            return Linspace(0.1, 6.0, 16);

        if (comps > 0 && comps <= 25)
            return Linspace(0.05, 2.5, 16);
        if (comps > 25 && comps <= 80)
            return Linspace(0.08, 2.0, 16);
        if (geoV < 400)
            return Linspace(0.05, 1.5, 14);
        if (geoV < 1500)
            return Linspace(0.03, 0.8, 14);
        if (geoV < 3500)
            return Linspace(0.05, 1.2, 16);
        return Linspace(0.08, 2.0, 18);
    }

    private static double[] Linspace(double start, double end, int count)
    {
        if (count <= 1)
            return new[] { start };
        var step = (end - start) / (count - 1);
        return Enumerable.Range(0, count).Select(i => start + i * step).ToArray();
    }

    private static BuildingGeometryOptions BuildOptions(
        MeshData resolutionLod,
        bool blindGeneration,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion> doors,
        IReadOnlyList<OrientedBox>? refObbsForGen,
        double minArea,
        double spacing,
        double gap,
        BuildingDecompositionMode mode,
        ReferenceFitMode fitMode,
        double blend,
        int targetComponentCount = 0,
        double wallSegmentTightFactor = 1.0) =>
        new()
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
        };
}
