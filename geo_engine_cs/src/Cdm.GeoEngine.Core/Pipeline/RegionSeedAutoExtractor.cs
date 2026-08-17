using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Derive one seed per region kind from mesh geometry (sandbox / regression tests).</summary>
public static class RegionSeedAutoExtractor
{
    private const double HorizontalDotThresh = 0.85;
    private const double WallDotThresh = 0.65;

    public static IReadOnlyList<GeoRegionSeed> Extract(MeshData mesh, BuildingMeshProfile profile)
    {
        if (mesh.Faces.Count == 0)
            return Array.Empty<GeoRegionSeed>();

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();
        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();
        var minZ = mesh.Vertices.Min(v => v.Dot(az));
        var maxZ = mesh.Vertices.Max(v => v.Dot(az));
        var centerX = mesh.Vertices.Average(v => v.Dot(ax));
        var centerY = mesh.Vertices.Average(v => v.Dot(ay));
        var footprintSpan = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);

        var (gableFaces, _) = PatchGableSlopeGrouper.Extract(mesh, profile, new HashSet<int>(), 0.05);
        var (plinthFaces, _) = PatchPlinthGrouper.Extract(mesh, profile, 0.04);

        var seeds = new List<GeoRegionSeed>();
        TryAdd(seeds, mesh, faceNormals,
            PickLargestArea(mesh, WallFaceIndices(mesh, faceNormals, ax, ay, az, centerX, centerY, footprintSpan, exterior: true)),
            GeoRegionKind.WallOuter);
        TryAdd(seeds, mesh, faceNormals,
            PickLargestArea(mesh, WallFaceIndices(mesh, faceNormals, ax, ay, az, centerX, centerY, footprintSpan, exterior: false)),
            GeoRegionKind.WallInner);
        TryAdd(seeds, mesh, faceNormals,
            PickLargestArea(mesh, FloorFaceIndices(mesh, faceNormals, az, minZ, maxZ)),
            GeoRegionKind.Floor);
        TryAdd(seeds, mesh, faceNormals,
            PickLargestArea(mesh, RoofFaceIndices(mesh, faceNormals, az, minZ, maxZ, gableFaces)),
            GeoRegionKind.Roof);
        TryAdd(seeds, mesh, faceNormals, PickLargestArea(mesh, gableFaces), GeoRegionKind.Gable);
        TryAdd(seeds, mesh, faceNormals, PickLargestArea(mesh, plinthFaces), GeoRegionKind.Plinth);

        return seeds;
    }

    private static HashSet<int> WallFaceIndices(
        MeshData mesh,
        Vec3[] faceNormals,
        Vec3 ax,
        Vec3 ay,
        Vec3 az,
        double centerX,
        double centerY,
        double footprintSpan,
        bool exterior)
    {
        var outSet = new HashSet<int>();
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var absZ = System.Math.Abs(faceNormals[fi].Dot(az));
            if (absZ > HorizontalDotThresh)
                continue;

            var bestDot = System.Math.Max(
                System.Math.Abs(faceNormals[fi].Dot(ax)),
                System.Math.Abs(faceNormals[fi].Dot(ay)));
            if (bestDot < WallDotThresh)
                continue;

            var c = FaceCentroid(mesh, mesh.Faces[fi]);
            var dx = c.Dot(ax) - centerX;
            var dy = c.Dot(ay) - centerY;
            var outward = dx * dx + dy * dy;
            var isExterior = outward > footprintSpan * footprintSpan * 0.02;
            if (exterior == isExterior)
                outSet.Add(fi);
        }
        return outSet;
    }

    private static HashSet<int> FloorFaceIndices(
        MeshData mesh,
        Vec3[] faceNormals,
        Vec3 az,
        double minZ,
        double maxZ)
    {
        var set = new HashSet<int>();
        var band = System.Math.Clamp((maxZ - minZ) * 0.2, 0.3, 1.2);
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (faceNormals[fi].Dot(az) > -0.55)
                continue;
            var cz = FaceCentroid(mesh, mesh.Faces[fi]).Dot(az);
            if (cz <= minZ + band)
                set.Add(fi);
        }
        return set;
    }

    private static HashSet<int> RoofFaceIndices(
        MeshData mesh,
        Vec3[] faceNormals,
        Vec3 az,
        double minZ,
        double maxZ,
        HashSet<int> gableFaces)
    {
        var set = new HashSet<int>();
        var band = System.Math.Clamp((maxZ - minZ) * 0.45, 0.5, 2.5);
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (gableFaces.Contains(fi))
                continue;
            var absZ = System.Math.Abs(faceNormals[fi].Dot(az));
            var cz = FaceCentroid(mesh, mesh.Faces[fi]).Dot(az);
            if (absZ > 0.45 && cz >= maxZ - band)
                set.Add(fi);
        }
        return set;
    }

    private static int PickLargestArea(MeshData mesh, HashSet<int> faces)
    {
        if (faces.Count == 0)
            return -1;

        var best = -1;
        var bestArea = 0.0;
        foreach (var fi in faces)
        {
            var area = MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            if (area > bestArea)
            {
                bestArea = area;
                best = fi;
            }
        }
        return best;
    }

    private static void TryAdd(
        List<GeoRegionSeed> seeds,
        MeshData mesh,
        Vec3[] faceNormals,
        int faceIndex,
        GeoRegionKind kind)
    {
        if (faceIndex < 0)
            return;
        var c = FaceCentroid(mesh, mesh.Faces[faceIndex]);
        seeds.Add(new GeoRegionSeed(kind, faceIndex, c, faceNormals[faceIndex]));
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}
