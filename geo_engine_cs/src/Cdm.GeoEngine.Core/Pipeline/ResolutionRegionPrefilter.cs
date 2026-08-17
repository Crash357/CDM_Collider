using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Pipeline phase 1 (first): Resolution LOD vertex groups → component boxes (doors, ladder, …).
/// Boxes are fit from group vertices/faces with <see cref="GeometryLodConstants.OverhangM"/> skin.
/// Fully consumed faces are removed; remaining mesh is passed to wall/floor decomposition
/// (<see cref="WallAxisCluster"/>, etc.) — reference Geometry LOD comparison happens only after merge.
/// </summary>
public static class ResolutionRegionPrefilter
{
    private static readonly HashSet<string> SkipGroupPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "component", "res", "lod", "view", "fire", "ce", "memory", "roadway", "hit", "path", "phys",
    };

    public sealed record PrefilterResult(
        IReadOnlyList<MeshComponent> Components,
        MeshData RemainingMesh,
        IReadOnlyList<DoorRegion> DoorRegions,
        int ConsumedFaceCount);

    public static PrefilterResult Apply(MeshData mesh, BuildingMeshProfile profile)
    {
        var doors = DoorRegionExtractor.Extract(mesh);
        var components = new List<MeshComponent>();
        var consumedVerts = new HashSet<int>();
        var compIdx = 1;

        var orderedGroups = mesh.VertexGroups.Keys
            .OrderBy(Priority)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in orderedGroups)
        {
            if (!ShouldExtractGroup(name))
                continue;

            if (!mesh.VertexGroups.TryGetValue(name, out var indices))
                continue;

            var verts = indices
                .Where(vi => vi >= 0 && vi < mesh.Vertices.Count)
                .Select(vi => mesh.Vertices[vi])
                .Distinct()
                .ToList();
            if (verts.Count < 4)
                continue;

            var hintNormal = EstimateRegionNormal(mesh, indices, profile);
            var box = FaceBoundsObbFitter.BuildPatchMesh(verts, hintNormal, profile)
                ?? ObbFitter.BuildPatchMeshTight(verts, hintNormal, profile)
                ?? ObbFitter.BuildPatchMesh(verts, hintNormal, profile);
            if (box == null)
                continue;

            var compName = $"Component{compIdx:D2}";
            box.Name = compName;
            components.Add(new MeshComponent { Name = compName, Mesh = box });
            compIdx++;

            foreach (var vi in indices)
                consumedVerts.Add(vi);
        }

        var (remaining, consumedFaces) = ExcludeFullyConsumedFaces(mesh, consumedVerts);
        return new PrefilterResult(components, remaining, doors, consumedFaces);
    }

    public static bool IsLinearProp(BuildingMeshProfile profile)
    {
        var sx = profile.SizeM.X;
        var sy = profile.SizeM.Y;
        var sz = profile.SizeM.Z;
        var horiz = System.Math.Max(sx, sy);
        var minHoriz = System.Math.Min(sx, sy);
        if (horiz < 1e-3)
            return false;
        return sz / horiz >= 2.5 && minHoriz / horiz <= 0.35 && profile.FootprintAreaM2 < 2.5;
    }

    public static MeshComponent? BuildLinearPropComponent(MeshData mesh, BuildingMeshProfile profile)
    {
        if (mesh.Vertices.Count < 4)
            return null;

        var hint = profile.AxisZ;
        var box = ObbFitter.BuildPatchMesh(mesh.Vertices, hint, profile);
        if (box == null)
            return null;
        box.Name = "Component01";
        return new MeshComponent { Name = "Component01", Mesh = box };
    }

    private static int Priority(string name)
    {
        if (name.StartsWith("doors", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains("door", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Contains("ladder", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 10;
    }

    private static bool ShouldExtractGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        foreach (var prefix in SkipGroupPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static Vec3 EstimateRegionNormal(MeshData mesh, IReadOnlyList<int> indices, BuildingMeshProfile profile)
    {
        var vertSet = indices.ToHashSet();
        var nSum = new Vec3(0, 0, 0);
        var weight = 0.0;
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var face = mesh.Faces[fi];
            if (!face.Any(vertSet.Contains))
                continue;
            var area = MeshTopology.FaceArea(mesh, face);
            var n = MeshTopology.FaceNormal(mesh, face);
            nSum = nSum.Add(n.Scale(area));
            weight += area;
        }

        if (weight > 1e-6)
            return BuildingMeshAnalyzer.SnapNormalToBuildingAxes(nSum.Normalized(), profile);

        return profile.AxisX;
    }

    private static (MeshData Remaining, int ConsumedFaces) ExcludeFullyConsumedFaces(
        MeshData mesh,
        HashSet<int> consumedVerts)
    {
        if (consumedVerts.Count == 0)
            return (mesh, 0);

        var remap = new Dictionary<int, int>();
        var newVerts = new List<Vec3>();
        var newFaces = new List<int[]>();
        var consumedFaces = 0;

        foreach (var face in mesh.Faces)
        {
            if (face.All(consumedVerts.Contains))
            {
                consumedFaces++;
                continue;
            }

            var mapped = new int[face.Length];
            for (var i = 0; i < face.Length; i++)
            {
                var vi = face[i];
                if (!remap.TryGetValue(vi, out var ni))
                {
                    ni = newVerts.Count;
                    remap[vi] = ni;
                    newVerts.Add(mesh.Vertices[vi]);
                }
                mapped[i] = ni;
            }
            newFaces.Add(mapped);
        }

        var remaining = new MeshData
        {
            Name = mesh.Name,
            Vertices = newVerts,
            Faces = newFaces,
        };

        foreach (var (key, val) in mesh.VertexGroups)
        {
            if (ShouldExtractGroup(key))
                continue;
            var mappedIdx = val
                .Where(vi => remap.ContainsKey(vi))
                .Select(vi => remap[vi])
                .Distinct()
                .ToList();
            if (mappedIdx.Count > 0)
                remaining.VertexGroups[key] = mappedIdx;
        }

        return (remaining, consumedFaces);
    }
}
