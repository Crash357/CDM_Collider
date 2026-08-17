using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Analysis;

/// <summary>
/// Blind post-pass: assign Resolution vertices to nearest component, re-fit tight OBBs.
/// </summary>
public static class BlindComponentRefiner
{
    public static List<MeshComponent> Refit(
        MeshData resolutionLod,
        IReadOnlyList<MeshComponent> components,
        BuildingMeshProfile profile)
    {
        if (components.Count == 0 || resolutionLod.Vertices.Count == 0)
            return components.ToList();

        var buckets = new List<List<Vec3>>(components.Count);
        for (var i = 0; i < components.Count; i++)
            buckets.Add(new List<Vec3>(components[i].Mesh.Vertices));

        var centroids = components
            .Select(c => Vec3.Centroid(c.Mesh.Vertices))
            .ToList();

        foreach (var v in resolutionLod.Vertices)
        {
            var best = 0;
            var bestD = double.MaxValue;
            for (var i = 0; i < centroids.Count; i++)
            {
                var d = Vec3.Distance(v, centroids[i]);
                if (d >= bestD)
                    continue;
                bestD = d;
                best = i;
            }
            buckets[best].Add(v);
        }

        var refined = new List<MeshComponent>(components.Count);
        for (var i = 0; i < components.Count; i++)
        {
            var comp = components[i];
            var samples = buckets[i].Count >= 4 ? buckets[i] : comp.Mesh.Vertices;
            var hint = EstimateNormal(comp, profile);
            var mesh = ObbFitter.BuildPatchMeshTight(samples, hint, profile)
                ?? ObbFitter.BuildPatchMesh(samples, hint, profile);
            if (mesh == null)
            {
                refined.Add(comp);
                continue;
            }
            mesh.Name = comp.Name;
            refined.Add(new MeshComponent { Name = comp.Name, Mesh = mesh });
        }

        return refined;
    }

    private static Vec3 EstimateNormal(MeshComponent comp, BuildingMeshProfile profile)
    {
        if (comp.Mesh.Vertices.Count == 8)
        {
            var parsed = BoxMeshParser.TryParse(comp.Mesh.Vertices);
            if (parsed != null)
                return parsed.AxisN;
        }
        return profile.AxisZ;
    }
}
