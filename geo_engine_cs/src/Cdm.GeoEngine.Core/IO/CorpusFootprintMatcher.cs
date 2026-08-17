using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.IO;

/// <summary>
/// Match an unknown Resolution mesh to the nearest corpus model (746 samples)
/// using footprint, height, topology and expected component density — for blind heuristic search.
/// </summary>
public static class CorpusFootprintMatcher
{
    public sealed class MatchResult
    {
        public CorpusReference Reference { get; init; } = null!;
        public double Score { get; init; }
    }

    public static MatchResult? FindNearest(
        BuildingCorpusIndex corpus,
        MeshData resolutionLod,
        BuildingMeshProfile profile,
        CorpusMeshStore? meshStore = null)
    {
        if (corpus.Models.Count == 0)
            return null;

        var verts = resolutionLod.VertexCount;
        var faces = resolutionLod.FaceCount;
        var footprint = System.Math.Max(0.5, profile.FootprintAreaM2);
        var height = System.Math.Max(0.5, profile.HeightM);
        var volume = footprint * height;
        var expectedComps = ExpectedComponentCount(volume, height, footprint);
        var isLargeBuilding = height > 2.5 || footprint > 35.0;

        MatchResult? best = null;

        foreach (var model in corpus.Models)
        {
            var res = model.ResolutionLod1;
            var geo = model.GeometryLod;
            if (res is not { Vertices: > 0 } || geo is not { ComponentCount: > 0 })
                continue;

            var rv = res.Vertices;
            var rf = System.Math.Max(1, res.Faces);
            var comps = geo.ComponentCount;
            var meshKind = model.MeshKind ?? "";

            if (isLargeBuilding && string.Equals(meshKind, "prop_hull", StringComparison.OrdinalIgnoreCase))
                continue;

            var vertLog = System.Math.Abs(System.Math.Log((verts + 1.0) / (rv + 1.0)));
            var faceLog = faces > 0
                ? System.Math.Abs(System.Math.Log((faces + 1.0) / rf))
                : 0.0;
            var compDev = System.Math.Abs(System.Math.Log((comps + 1.0) / (expectedComps + 1.0)));

            var score = vertLog * 1.6 + faceLog * 1.0 + compDev * 2.4;

            if (height > 5.0 && comps < 8)
                score += 1.2;
            if (footprint > 80.0 && comps < 12)
                score += 1.0;
            if (isLargeBuilding && string.Equals(meshKind, "building", StringComparison.OrdinalIgnoreCase))
                score -= 0.15;
            if (isLargeBuilding && (model.Category ?? "").Contains("residential", StringComparison.OrdinalIgnoreCase))
                score -= 0.08;
            if (verts > rv * 3)
                score += 0.35;

            if (best != null && score >= best.Score)
                continue;

            var reference = CorpusReferenceLookup.TryGetById(corpus, model.Id, meshStore);
            if (reference == null)
                continue;

            best = new MatchResult { Reference = reference, Score = score };
        }

        return best;
    }

    /// <summary>Empirical component target from corpus building volumes.</summary>
    private static double ExpectedComponentCount(double volumeM3, double heightM, double footprintM2)
    {
        var fromVolume = 4.0 + System.Math.Log10(System.Math.Max(10.0, volumeM3)) * 4.2;
        var fromFootprint = 6.0 + System.Math.Sqrt(System.Math.Max(1.0, footprintM2)) * 0.55;
        var expected = (fromVolume + fromFootprint) * 0.5;
        if (heightM > 7.0)
            expected += 2.0;
        return System.Math.Clamp(expected, 4.0, 80.0);
    }
}
