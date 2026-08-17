using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>Calibrate wall/slab extents from reference OBBs and corpus peers.</summary>
public static class CorpusExtentCalibrator
{
    public static BuildingMeshProfile Calibrate(
        BuildingMeshProfile profile,
        CorpusReference? reference,
        IReadOnlyList<OrientedBox>? referenceObbs = null)
    {
        var wall = profile.WallThicknessM;
        var horiz = profile.HorizontalSlabM;

        if (referenceObbs is { Count: > 0 })
        {
            var fromRef = ExtentsFromReferenceObbs(referenceObbs);
            if (fromRef.wallM > 0)
                wall = fromRef.wallM;
            if (fromRef.horizM > 0)
                horiz = fromRef.horizM;
        }
        else if (reference is { GeometryComponentCount: > 0, GeometryVertices: > 0 })
        {
            // Corpus proxy: small buildings → thinner slabs
            var vPerComp = (double)reference.GeometryVertices / reference.GeometryComponentCount;
            if (vPerComp < 9)
            {
                wall = System.Math.Min(wall, 0.14);
                horiz = System.Math.Min(horiz, 0.11);
            }
        }

        return profile.WithExtents(wall, horiz);
    }

    public static (double wallM, double horizM) ExtentsFromReferenceObbs(IReadOnlyList<OrientedBox> obbs)
    {
        var thin = new List<double>();
        var flat = new List<double>();

        foreach (var obb in obbs)
        {
            var n = obb.AxisN.Normalized();
            if (System.Math.Abs(n.Z) > 0.85)
            {
                var extents = new[] { obb.ExtentN * 2, obb.ExtentU * 2, obb.ExtentV * 2 };
                var mid = extents.OrderBy(x => x).ElementAt(1);
                if (mid >= 0.06 && mid <= 0.35)
                    flat.Add(mid);
                continue;
            }

            var wallExtents = new[] { obb.ExtentN * 2, obb.ExtentU * 2, obb.ExtentV * 2 };
            var min = wallExtents.Min();
            if (min >= 0.08 && min <= 0.28)
                thin.Add(min);
        }

        var wallM = thin.Count > 0 ? Median(thin) : 0;
        var horizM = flat.Count > 0 ? Median(flat) : 0;
        return (wallM, horizM);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }
}
