using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Assign resolution faces to nearest reference Geometry-OBB (supervised decomposition).
/// </summary>
public static class ReferenceGuidedClusterer
{
    public static IReadOnlyList<PatchCluster>? TryCluster(
        MeshData mesh,
        IReadOnlyList<OrientedBox> refObbs,
        double minAreaM2)
    {
        if (refObbs.Count == 0 || mesh.Faces.Count == 0)
            return null;

        var buckets = Enumerable.Range(0, refObbs.Count).ToDictionary(i => i, _ => new List<int>());

        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var face = mesh.Faces[fi];
            var c = FaceCentroid(mesh, face);
            var fn = MeshTopology.FaceNormal(mesh, face).Normalized();

            var best = -1;
            var bestCost = double.MaxValue;
            for (var ri = 0; ri < refObbs.Count; ri++)
            {
                var obb = refObbs[ri];
                var align = System.Math.Max(
                    System.Math.Abs(fn.Dot(obb.AxisN)),
                    System.Math.Max(System.Math.Abs(fn.Dot(obb.AxisU)),
                        System.Math.Abs(fn.Dot(obb.AxisV))));
                if (align < 0.45)
                    continue;

                var dist = Vec3.Distance(c, obb.Center);
                var insideBonus = IsInsideExpanded(obb, c) ? -0.5 : 0;
                var cost = dist + insideBonus - align * 0.3;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = ri;
                }
            }

            if (best < 0)
            {
                best = NearestByDistance(c, refObbs);
            }

            buckets[best].Add(fi);
        }

        var patches = new List<PatchCluster>();
        foreach (var (ri, faceIndices) in buckets)
        {
            if (faceIndices.Count == 0)
                continue;

            var vertSet = new HashSet<int>();
            var area = 0.0;
            var nSum = new Vec3(0, 0, 0);
            foreach (var fi in faceIndices)
            {
                area += MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
                nSum = nSum.Add(MeshTopology.FaceNormal(mesh, mesh.Faces[fi]));
                foreach (var vi in mesh.Faces[fi])
                    vertSet.Add(vi);
            }

            if (vertSet.Count < 4 || area < minAreaM2 * 0.25)
                continue;

            var normal = nSum.Length() > 1e-6 ? nSum.Normalized() : refObbs[ri].AxisN;
            patches.Add(new PatchCluster(
                faceIndices,
                vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
                area,
                normal,
                ri));
        }

        return patches.Count > 0 ? patches : null;
    }

    private static int NearestByDistance(Vec3 c, IReadOnlyList<OrientedBox> refObbs)
    {
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < refObbs.Count; i++)
        {
            var d = Vec3.Distance(c, refObbs[i].Center);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    private static bool IsInsideExpanded(OrientedBox obb, Vec3 p, double margin = 0.15)
    {
        var d = p.Sub(obb.Center);
        var pn = System.Math.Abs(d.Dot(obb.AxisN));
        var pu = System.Math.Abs(d.Dot(obb.AxisU));
        var pv = System.Math.Abs(d.Dot(obb.AxisV));
        return pn <= obb.ExtentN + margin && pu <= obb.ExtentU + margin && pv <= obb.ExtentV + margin;
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}
