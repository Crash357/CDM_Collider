using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Collapse redundant picker seeds (many clicks per category) to a small representative set
/// before region flood-fill — markings guide semantics, not component count.
/// </summary>
public static class RegionSeedNormalizer
{
    private const double HorizontalDot = 0.85;
    private const double WallDot = 0.65;

    public static IReadOnlyList<GeoRegionSeed> NormalizeForPipeline(
        MeshData mesh,
        IReadOnlyList<GeoRegionSeed> seeds,
        BuildingMeshProfile profile)
    {
        if (seeds.Count <= 1)
            return seeds;

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var byKind = seeds.GroupBy(s => s.Kind).ToList();
        var result = new List<GeoRegionSeed>();

        foreach (var group in byKind)
        {
            var list = group.ToList();
            switch (group.Key)
            {
                case GeoRegionKind.WallOuter or GeoRegionKind.WallInner:
                    result.AddRange(CollapseWallSeeds(mesh, list, faceNormals, profile));
                    break;
                default:
                    result.Add(PickRepresentative(mesh, list, faceNormals, profile));
                    break;
            }
        }

        return result.Count > 0 ? result : seeds;
    }

    private static IReadOnlyList<GeoRegionSeed> CollapseWallSeeds(
        MeshData mesh,
        List<GeoRegionSeed> seeds,
        Vec3[] faceNormals,
        BuildingMeshProfile profile)
    {
        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();

        var buckets = new Dictionary<int, GeoRegionSeed>();
        foreach (var seed in seeds)
        {
            var fi = RegionSeedExpander.ResolveSeedFacePublic(mesh, seed);
            var n = seed.Normal.Length() > 1e-6
                ? seed.Normal.Normalized()
                : fi >= 0 ? faceNormals[fi] : profile.AxisZ;

            var absZ = System.Math.Abs(n.Dot(az));
            if (absZ > HorizontalDot)
                continue;

            var dotX = n.Dot(ax);
            var dotY = n.Dot(ay);
            var bucket = 0;
            if (System.Math.Abs(dotX) >= System.Math.Abs(dotY) && System.Math.Abs(dotX) >= WallDot)
                bucket = dotX >= 0 ? 0 : 1;
            else if (System.Math.Abs(dotY) >= WallDot)
                bucket = dotY >= 0 ? 2 : 3;
            else
                bucket = 4;

            if (!buckets.ContainsKey(bucket))
                buckets[bucket] = seed;
            else
            {
                var cur = buckets[bucket];
                if (SeedFaceArea(mesh, seed, faceNormals) > SeedFaceArea(mesh, cur, faceNormals))
                    buckets[bucket] = seed;
            }
        }

        if (buckets.Count == 0)
            return new[] { PickRepresentative(mesh, seeds, faceNormals, profile) };

        return buckets.Values.ToList();
    }

    private static GeoRegionSeed PickRepresentative(
        MeshData mesh,
        List<GeoRegionSeed> seeds,
        Vec3[] faceNormals,
        BuildingMeshProfile profile)
    {
        GeoRegionSeed? best = null;
        var bestArea = -1.0;
        foreach (var seed in seeds)
        {
            var area = SeedFaceArea(mesh, seed, faceNormals);
            if (area > bestArea)
            {
                bestArea = area;
                best = seed;
            }
        }
        return best ?? seeds[0];
    }

    private static double SeedFaceArea(MeshData mesh, GeoRegionSeed seed, Vec3[] faceNormals)
    {
        var fi = RegionSeedExpander.ResolveSeedFacePublic(mesh, seed);
        if (fi < 0 || fi >= mesh.Faces.Count)
            return seed.Position.Length();
        return MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
    }
}
