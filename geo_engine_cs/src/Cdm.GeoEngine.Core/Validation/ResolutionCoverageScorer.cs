using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Validation;

public sealed class CoverageScore
{
    public double FractionInside { get; init; }
    public int SamplesInside { get; init; }
    public int SamplesTotal { get; init; }
}

/// <summary>Fraction of resolution face centroids covered by generated OBBs (VHACD-style residual).</summary>
public static class ResolutionCoverageScorer
{
    private const double Eps = 1e-4;

    public static CoverageScore Score(MeshData resolution, IReadOnlyList<OrientedBox> boxes)
    {
        if (resolution.Faces.Count == 0 || boxes.Count == 0)
        {
            return new CoverageScore
            {
                FractionInside = 0,
                SamplesInside = 0,
                SamplesTotal = resolution.Faces.Count,
            };
        }

        var inside = 0;
        foreach (var face in resolution.Faces)
        {
            if (face.Length < 3)
                continue;
            var centroid = FaceCentroid(resolution, face);
            if (IsInsideAny(centroid, boxes))
                inside++;
        }

        var total = resolution.Faces.Count;
        return new CoverageScore
        {
            FractionInside = total > 0 ? inside / (double)total : 0,
            SamplesInside = inside,
            SamplesTotal = total,
        };
    }

    public static CoverageScore ScoreFromComponents(
        MeshData resolution,
        IEnumerable<MeshComponent> components,
        BuildingMeshProfile? profile = null)
    {
        var boxes = ObbGeometryComparer.FromComponents(components, profile);
        return Score(resolution, boxes);
    }

    /// <summary>Expand collision boxes slightly when scoring resolution coverage (visual mesh vs Geo LOD).</summary>
    public static IReadOnlyList<OrientedBox> InflateObbs(IReadOnlyList<OrientedBox> boxes, double marginM)
    {
        if (marginM <= 0)
            return boxes;
        return boxes.Select(b => new OrientedBox
        {
            Center = b.Center,
            AxisN = b.AxisN,
            AxisU = b.AxisU,
            AxisV = b.AxisV,
            ExtentN = b.ExtentN + marginM,
            ExtentU = b.ExtentU + marginM,
            ExtentV = b.ExtentV + marginM,
        }).ToList();
    }

    public static double CoverageMarginM(IReadOnlyList<OrientedBox> boxes, BuildingMeshProfile? profile)
    {
        if (boxes.Count == 0)
            return 0.12;
        var maxExt = boxes.Max(b => System.Math.Max(b.ExtentN, System.Math.Max(b.ExtentU, b.ExtentV)));
        var wall = profile?.WallThicknessM ?? 0.1;
        if (maxExt < 1.5)
            return System.Math.Max(0.28, wall * 8);
        if (maxExt < 4.0)
            return System.Math.Clamp(wall * 5, 0.18, 0.35);
        return System.Math.Clamp(wall * 3, 0.12, 0.25);
    }

    public static CoverageScore ScoreWithCorpusMargin(
        MeshData resolution,
        IReadOnlyList<OrientedBox> boxes,
        BuildingMeshProfile? profile = null)
    {
        var margin = CoverageMarginM(boxes, profile);
        return Score(resolution, InflateObbs(boxes, margin));
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
        {
            if (vi >= 0 && vi < mesh.Vertices.Count)
                sum = sum.Add(mesh.Vertices[vi]);
        }

        return sum.Scale(1.0 / face.Length);
    }

    private static bool IsInsideAny(Vec3 point, IReadOnlyList<OrientedBox> boxes)
    {
        foreach (var box in boxes)
        {
            if (IsInside(point, box))
                return true;
        }

        return false;
    }

    private static bool IsInside(Vec3 point, OrientedBox box)
    {
        var local = point.Sub(box.Center);
        var n = System.Math.Abs(local.Dot(box.AxisN));
        var u = System.Math.Abs(local.Dot(box.AxisU));
        var v = System.Math.Abs(local.Dot(box.AxisV));
        return n <= box.ExtentN + Eps
               && u <= box.ExtentU + Eps
               && v <= box.ExtentV + Eps;
    }
}
