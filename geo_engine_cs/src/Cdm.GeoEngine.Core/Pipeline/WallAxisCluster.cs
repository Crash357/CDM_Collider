using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Axis-aligned wall/floor/ceiling clustering (Python _cluster_by_wall_axis_faces),
/// using building profile axes instead of world XYZ.
/// </summary>
public static class WallAxisCluster
{
    public static IReadOnlyList<PatchCluster> Split(
        MeshData mesh,
        double minAreaM2,
        double axisSpacingM,
        BuildingMeshProfile profile)
    {
        var axes = BuildAxes(profile);
        var faceNormals = mesh.Faces.Select(f => MeshTopology.FaceNormal(mesh, f).Normalized()).ToArray();
        var bins = Enumerable.Range(0, 6).Select(_ => new List<int>()).ToArray();

        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var wn = faceNormals[fi];
            var best = 0;
            var bestDot = -2.0;
            for (var i = 0; i < axes.Length; i++)
            {
                var d = wn.Dot(axes[i]);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = i;
                }
            }

            if (bestDot > 0.5)
                bins[best].Add(fi);
        }

        var patches = new List<PatchCluster>();
        var spacing = System.Math.Max(0.1, axisSpacingM);

        for (var axisIdx = 0; axisIdx < bins.Length; axisIdx++)
        {
            var faces = bins[axisIdx];
            if (faces.Count == 0)
                continue;

            var ax = axes[axisIdx];
            var subBins = new Dictionary<int, List<int>>();

            foreach (var fi in faces)
            {
                var center = FaceCentroid(mesh, mesh.Faces[fi]);
                var bucket = (int)System.Math.Floor(center.Dot(ax) / spacing);
                if (!subBins.TryGetValue(bucket, out var list))
                {
                    list = new List<int>();
                    subBins[bucket] = list;
                }
                list.Add(fi);
            }

            foreach (var bucketFaces in subBins.Values)
            {
                var vertSet = new HashSet<int>();
                var wnSum = new Vec3(0, 0, 0);
                var totalArea = 0.0;

                foreach (var fi in bucketFaces)
                {
                    var area = MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
                    wnSum = wnSum.Add(faceNormals[fi].Scale(area));
                    totalArea += area;
                    foreach (var vi in mesh.Faces[fi])
                        vertSet.Add(vi);
                }

                if (vertSet.Count < 4 || totalArea < minAreaM2)
                    continue;

                var avgN = wnSum.Length() > 1e-6 ? wnSum.Normalized() : ax;
                patches.Add(new PatchCluster(
                    bucketFaces,
                    vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
                    totalArea,
                    avgN));
            }
        }

        return patches;
    }

    private static Vec3[] BuildAxes(BuildingMeshProfile profile)
    {
        var x = profile.AxisX.Normalized();
        var y = profile.AxisY.Normalized();
        var z = profile.AxisZ.Normalized();
        return new[]
        {
            x, x.Scale(-1),
            y, y.Scale(-1),
            z, z.Scale(-1),
        };
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}
