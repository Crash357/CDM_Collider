using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Split patches that span too much building height (floor→roof mega-strips).</summary>
public static class PatchHeightSplitter
{
    public static double HeightSpanM(IReadOnlyList<Vec3> verts, BuildingMeshProfile profile)
    {
        if (verts.Count == 0)
            return 0;

        var az = profile.AxisZ.Normalized();
        var zs = verts.Select(v => v.Dot(az)).ToArray();
        return zs.Max() - zs.Min();
    }

    public static double MaxWallBandHeightM(BuildingMeshProfile profile, double wallSpanM)
    {
        var buildingH = profile.SizeM.Z > 0 ? profile.SizeM.Z : 4.0;
        return System.Math.Min(wallSpanM * 1.55, buildingH * 0.52);
    }

    public static double MaxSlopeInPlaneSpanM(BuildingMeshProfile profile, double wallSpanM)
    {
        var buildingH = profile.SizeM.Z > 0 ? profile.SizeM.Z : 4.0;
        var footprint = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);
        // Upper bound can fall below wallSpanM for very small/flat buildings — widen it
        // instead of crashing Math.Clamp (min must never exceed max).
        var upper = System.Math.Max(wallSpanM, System.Math.Min(buildingH * 0.98, 5.5));
        return System.Math.Clamp(
            System.Math.Max(wallSpanM * 1.35, footprint * 0.88),
            wallSpanM,
            upper);
    }

    public static double MaxSlopeHeightSpanM(BuildingMeshProfile profile)
        => profile.SizeM.Z > 0 ? profile.SizeM.Z * 0.92 : 4.0;

    public static IReadOnlyList<PatchCluster> SplitByHeightBands(
        MeshData mesh,
        PatchCluster patch,
        BuildingMeshProfile profile,
        double maxBandHeightM,
        double minAreaM2 = 0.04)
    {
        if (patch.FaceIndices.Count < 2 || maxBandHeightM <= 0)
            return new[] { patch };

        var az = profile.AxisZ.Normalized();
        var zByFace = new List<(int fi, double z)>();
        foreach (var fi in patch.FaceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            zByFace.Add((fi, FaceCentroid(mesh, mesh.Faces[fi]).Dot(az)));
        }

        if (zByFace.Count < 2)
            return new[] { patch };

        var zMin = zByFace.Min(t => t.z);
        var zMax = zByFace.Max(t => t.z);
        if (zMax - zMin <= maxBandHeightM * 1.05)
            return new[] { patch };

        var bands = new Dictionary<int, List<int>>();
        foreach (var (fi, z) in zByFace)
        {
            var band = (int)System.Math.Floor((z - zMin) / maxBandHeightM);
            if (!bands.TryGetValue(band, out var list))
            {
                list = new List<int>();
                bands[band] = list;
            }
            list.Add(fi);
        }

        var result = new List<PatchCluster>();
        foreach (var bandFaces in bands.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            var sub = BuildSubPatch(mesh, bandFaces, patch, minAreaM2);
            if (sub != null)
                result.Add(sub);
        }

        return result.Count > 0 ? result : new[] { patch };
    }

    private static PatchCluster? BuildSubPatch(
        MeshData mesh,
        List<int> faceIndices,
        PatchCluster parent,
        double minAreaM2)
    {
        if (faceIndices.Count == 0)
            return null;

        var vertSet = new HashSet<int>();
        var area = 0.0;
        foreach (var fi in faceIndices)
        {
            area += MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            foreach (var vi in mesh.Faces[fi])
                vertSet.Add(vi);
        }

        if (vertSet.Count < 3 || area < minAreaM2)
            return null;

        return new PatchCluster(
            faceIndices,
            vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
            area,
            parent.DominantNormal,
            parent.ReferenceIndex,
            parent.SurfaceKind,
            parent.GableEnd);
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}
