using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Merge fragmented ±X/±Y end-wall strips into full-height gable/end panels (ref04/ref19).</summary>
public static class PatchEndWallMerger
{
    public static IReadOnlyList<PatchCluster> MergeEndCaps(
        MeshData mesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile)
    {
        var wallIdx = new List<int>();
        for (var i = 0; i < patches.Count; i++)
        {
            if (patches[i].SurfaceKind != PatchSurfaceKind.Wall)
                continue;

            var n = patches[i].DominantNormal.Normalized();
            var absAx = System.Math.Abs(n.Dot(profile.AxisX.Normalized()));
            var absAy = System.Math.Abs(n.Dot(profile.AxisY.Normalized()));
            if (absAx >= 0.85 || absAy >= 0.85)
                wallIdx.Add(i);
        }

        if (wallIdx.Count < 2)
            return patches;

        var parent = Enumerable.Range(0, patches.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }
        void Union(int a, int b) => parent[Find(b)] = Find(a);

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();

        for (var a = 0; a < wallIdx.Count; a++)
        for (var b = a + 1; b < wallIdx.Count; b++)
        {
            var i = wallIdx[a];
            var j = wallIdx[b];
            var pi = patches[i];
            var pj = patches[j];
            var axisI = EndWallPlaneAxis(pi, profile);
            var axisJ = EndWallPlaneAxis(pj, profile);
            if (axisI == null || axisJ == null || axisI != axisJ)
                continue;

            var ci = PatchMerger.GetPatchCenter(pi);
            var cj = PatchMerger.GetPatchCenter(pj);
            var onX = axisI == EndWallAxisKind.X;
            var planeGap = onX
                ? System.Math.Abs(ci.Dot(ax) - cj.Dot(ax))
                : System.Math.Abs(ci.Dot(ay) - cj.Dot(ay));
            if (planeGap > 0.12)
                continue;

            if (onX)
            {
                var orthGap = Vec3.Distance(
                    ci.Sub(ax.Scale(ci.Dot(ax))),
                    cj.Sub(ax.Scale(cj.Dot(ax))));
                if (orthGap > 0.55)
                    continue;
            }

            Union(i, j);
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < patches.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        var result = new List<PatchCluster>();
        foreach (var indices in groups.Values)
        {
            if (indices.Count == 1)
            {
                result.Add(patches[indices[0]]);
                continue;
            }

            var group = indices.Select(idx => patches[idx]).ToList();
            if (group.All(p => p.SurfaceKind == PatchSurfaceKind.Wall))
            {
                var combined = PatchMerger.CombinePatchGroup(group, profile);
                result.Add(combined with { SurfaceKind = PatchSurfaceKind.EndCap });
            }
            else
                result.AddRange(group);
        }

        return result;
    }

    private enum EndWallAxisKind { X, Y }

    private static EndWallAxisKind? EndWallPlaneAxis(PatchCluster patch, BuildingMeshProfile profile)
    {
        var n = patch.DominantNormal.Normalized();
        var absAx = System.Math.Abs(n.Dot(profile.AxisX.Normalized()));
        var absAy = System.Math.Abs(n.Dot(profile.AxisY.Normalized()));
        if (absAx >= 0.85 && absAx >= absAy)
            return EndWallAxisKind.X;
        if (absAy >= 0.85)
            return EndWallAxisKind.Y;
        return null;
    }
}
