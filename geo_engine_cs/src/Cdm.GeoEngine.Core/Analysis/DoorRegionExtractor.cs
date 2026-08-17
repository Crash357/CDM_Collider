using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

public sealed record DoorRegion(string Name, Vec3 Min, Vec3 Max)
{
    public bool Contains(Vec3 p, double margin = 0.05) =>
        p.X >= Min.X - margin && p.X <= Max.X + margin &&
        p.Y >= Min.Y - margin && p.Y <= Max.Y + margin &&
        p.Z >= Min.Z - margin && p.Z <= Max.Z + margin;
}

public static class DoorRegionExtractor
{
    public static IReadOnlyList<DoorRegion> Extract(MeshData mesh)
    {
        var regions = new List<DoorRegion>();
        foreach (var (name, indices) in mesh.VertexGroups)
        {
            if (!name.StartsWith("doors", StringComparison.OrdinalIgnoreCase))
                continue;

            var verts = indices
                .Where(vi => vi >= 0 && vi < mesh.Vertices.Count)
                .Select(vi => mesh.Vertices[vi])
                .ToList();
            if (verts.Count < 3)
                continue;

            var min = verts[0];
            var max = verts[0];
            foreach (var v in verts)
            {
                min = Min(min, v);
                max = Max(max, v);
            }

            regions.Add(new DoorRegion(name, min, max));
        }

        return regions;
    }

    private static Vec3 Min(Vec3 a, Vec3 b) =>
        new(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));

    private static Vec3 Max(Vec3 a, Vec3 b) =>
        new(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));
}
