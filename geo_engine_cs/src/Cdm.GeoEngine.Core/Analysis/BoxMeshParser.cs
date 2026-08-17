using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>Recover OBB from 8 corner vertices without PCA drift.</summary>
public static class BoxMeshParser
{
    public static OrientedBox? TryParse(IReadOnlyList<Vec3> corners)
    {
        if (corners.Count != 8)
            return null;

        var center = Vec3.Centroid(corners);
        var rel = corners.Select(c => c.Sub(center)).ToList();

        var axisN = LongestAxis(rel);
        if (axisN.Length() < 1e-6)
            return null;

        var axisU = Orthogonal(axisN, rel);
        var axisV = axisN.Cross(axisU).Normalized();

        var nExt = ProjectExtents(corners, center, axisN);
        var uExt = ProjectExtents(corners, center, axisU);
        var vExt = ProjectExtents(corners, center, axisV);

        var obb = new OrientedBox
        {
            Center = center
                .Add(axisN.Scale((nExt.lo + nExt.hi) * 0.5))
                .Add(axisU.Scale((uExt.lo + uExt.hi) * 0.5))
                .Add(axisV.Scale((vExt.lo + vExt.hi) * 0.5)),
            AxisN = axisN,
            AxisU = axisU,
            AxisV = axisV,
            ExtentN = (nExt.hi - nExt.lo) * 0.5,
            ExtentU = (uExt.hi - uExt.lo) * 0.5,
            ExtentV = (vExt.hi - vExt.lo) * 0.5,
        };

        return new OrientedBox
        {
            Center = obb.Center,
            AxisN = obb.AxisN,
            AxisU = obb.AxisU,
            AxisV = obb.AxisV,
            ExtentN = obb.ExtentN,
            ExtentU = obb.ExtentU,
            ExtentV = obb.ExtentV,
            Corners = corners.ToList(),
        };
    }

    private static Vec3 LongestAxis(IReadOnlyList<Vec3> rel)
    {
        Vec3 best = rel[0];
        var bestLen = 0.0;
        for (var i = 0; i < rel.Count; i++)
        {
            for (var j = i + 1; j < rel.Count; j++)
            {
                var d = rel[j].Sub(rel[i]);
                var len = d.Length();
                if (len > bestLen)
                {
                    bestLen = len;
                    best = d.Normalized();
                }
            }
        }
        return best;
    }

    private static Vec3 Orthogonal(Vec3 axis, IReadOnlyList<Vec3> rel)
    {
        var refAxis = System.Math.Abs(axis.Dot(new Vec3(0, 0, 1))) < 0.9
            ? new Vec3(0, 0, 1)
            : new Vec3(1, 0, 0);
        var u = refAxis.Sub(axis.Scale(refAxis.Dot(axis)));
        if (u.Length() > 1e-6)
            return u.Normalized();

        foreach (var r in rel)
        {
            var cand = r.Sub(axis.Scale(r.Dot(axis)));
            if (cand.Length() > 1e-6)
                return cand.Normalized();
        }

        return new Vec3(1, 0, 0);
    }

    private static (double lo, double hi) ProjectExtents(
        IReadOnlyList<Vec3> corners, Vec3 origin, Vec3 axis)
    {
        var lo = double.MaxValue;
        var hi = double.MinValue;
        foreach (var c in corners)
        {
            var p = c.Sub(origin).Dot(axis);
            lo = System.Math.Min(lo, p);
            hi = System.Math.Max(hi, p);
        }
        return (lo, hi);
    }
}
