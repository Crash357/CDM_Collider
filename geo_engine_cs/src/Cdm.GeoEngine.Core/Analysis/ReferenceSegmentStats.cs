using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Core.Analysis;

public sealed record ReferenceSegmentStats(
    string ModelId,
    int ComponentCount,
    double MinTangentM,
    double MedianTangentM,
    double MaxTangentM,
    double MinThicknessM,
    double MedianThicknessM,
    double MaxThicknessM);

/// <summary>Reference Geometry LOD OBB extent bands (target wall strip sizes).</summary>
public static class ReferenceSegmentStatsAnalyzer
{
    public static ReferenceSegmentStats Analyze(string modelId, MeshData referenceGeometryLod)
    {
        var obbs = ReferenceObbExtractor.ExtractFromGeometryLod(referenceGeometryLod);
        var tangents = new List<double>();
        var thicknesses = new List<double>();

        foreach (var obb in obbs)
        {
            var extents = new[] { obb.ExtentN * 2, obb.ExtentU * 2, obb.ExtentV * 2 }
                .OrderBy(x => x)
                .ToArray();
            thicknesses.Add(extents[0]);
            tangents.Add(extents[2]);
        }

        return new ReferenceSegmentStats(
            modelId,
            obbs.Count,
            tangents.Count > 0 ? tangents.Min() : 0,
            Median(tangents),
            tangents.Count > 0 ? tangents.Max() : 0,
            thicknesses.Count > 0 ? thicknesses.Min() : 0,
            Median(thicknesses),
            thicknesses.Count > 0 ? thicknesses.Max() : 0);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) * 0.5
            : sorted[mid];
    }
}
