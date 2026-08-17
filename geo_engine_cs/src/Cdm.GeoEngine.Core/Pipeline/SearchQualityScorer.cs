using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Unified objective for heuristic search — OBB fit + resolution coverage + corpus validation.</summary>
public sealed class SearchQualityScore
{
    public double Composite { get; init; }
    public double ObbsOverall { get; init; }
    public double Coverage { get; init; }
    public double Validation { get; init; }
    public int CountDiff { get; init; }
    public bool InCountCorridor { get; init; }
}

public static class SearchQualityScorer
{
    public static SearchQualityScore Evaluate(
        MeshData resolutionLod,
        BuildingGeometryResult geometry,
        BuildingMeshProfile? profile,
        ObbGeometryScore? obbGeometry,
        GeometryValidationReport validation,
        int targetComponentCount,
        int maxCountDiff,
        bool blindScoring = false)
    {
        var count = geometry.Components.Count;
        var countDiff = targetComponentCount > 0
            ? System.Math.Abs(count - targetComponentCount)
            : 0;
        var inCorridor = targetComponentCount <= 0 || countDiff <= maxCountDiff;

        var obbOverall = obbGeometry?.OverallScore ?? 0;
        var coverage = ResolutionCoverageScorer
            .ScoreFromComponents(resolutionLod, geometry.Components, profile)
            .FractionInside;
        var validationScore = validation.OverallScore;

        double composite;
        if (obbGeometry is { ReferenceCount: > 0 })
        {
            var obbWeight = blindScoring ? 0.62 : 0.45;
            var covWeight = blindScoring ? 0.28 : 0.35;
            var valWeight = blindScoring ? 0.10 : 0.20;
            composite = obbOverall * obbWeight + coverage * covWeight + validationScore * valWeight;
            if (!inCorridor)
                composite *= System.Math.Max(0.15, 1.0 - countDiff / (double)System.Math.Max(1, targetComponentCount));
        }
        else
        {
            composite = coverage * 0.55 + validationScore * 0.45;
            if (!inCorridor)
                composite *= 0.5;
        }

        return new SearchQualityScore
        {
            Composite = composite,
            ObbsOverall = obbOverall,
            Coverage = coverage,
            Validation = validationScore,
            CountDiff = countDiff,
            InCountCorridor = inCorridor,
        };
    }

    public static bool IsBetterQuality(SearchQualityScore a, SearchQualityScore b)
    {
        if (a.InCountCorridor != b.InCountCorridor)
            return a.InCountCorridor;

        if (a.CountDiff != b.CountDiff)
            return a.CountDiff < b.CountDiff;

        if (System.Math.Abs(a.Composite - b.Composite) > 1e-6)
            return a.Composite > b.Composite;

        if (System.Math.Abs(a.ObbsOverall - b.ObbsOverall) > 1e-6)
            return a.ObbsOverall > b.ObbsOverall;

        return a.Coverage > b.Coverage;
    }
}
