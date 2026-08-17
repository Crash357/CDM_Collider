using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Spatial ground-band faces (ref component17-style plinth strips) — mixed normals, one region.
/// </summary>
public static class PatchPlinthGrouper
{
    private const double MaxClusterSpanM = 2.2;

    public static (HashSet<int> PlinthFaces, IReadOnlyList<PatchCluster> PlinthPatches) Extract(
        MeshData mesh,
        BuildingMeshProfile profile,
        double minAreaM2 = 0.04)
    {
        if (mesh.Faces.Count == 0)
            return (new HashSet<int>(), Array.Empty<PatchCluster>());

        var az = profile.AxisZ.Normalized();
        var ay = profile.AxisY.Normalized();
        var minZ = mesh.Vertices.Min(v => v.Dot(az));
        var maxY = mesh.Vertices.Max(v => v.Dot(ay));
        var bandTop = minZ + System.Math.Clamp(profile.SizeM.Z * 0.24, 0.55, 0.95);
        var eaveMinY = maxY - System.Math.Clamp(profile.SizeM.Y * 0.28, 0.45, 0.65);

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var plinthFaces = new HashSet<int>();
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var cz = FaceCentroid(mesh, mesh.Faces[fi]).Dot(az);
            var cy = FaceCentroid(mesh, mesh.Faces[fi]).Dot(ay);
            if (cz > bandTop || cy >= eaveMinY)
                continue;
            if (cy > maxY - System.Math.Clamp(profile.SizeM.Y * 0.55, 0.85, 1.35) && cz < minZ + 0.55)
                continue;

            var n = faceNormals[fi];
            var absZ = System.Math.Abs(n.Dot(az));
            var absY = System.Math.Abs(n.Dot(profile.AxisY.Normalized()));
            var isDown = n.Dot(az) < -0.85;
            var isEndWall = absY > 0.85 && absZ < 0.2;
            var isShallow = absZ >= 0.12 && absZ < 0.55;

            if (isDown || isEndWall || isShallow)
                plinthFaces.Add(fi);
        }

        if (plinthFaces.Count == 0)
            return (plinthFaces, Array.Empty<PatchCluster>());

        var patches = ClusterPlinth(mesh, plinthFaces, faceNormals, profile, minAreaM2);
        return (plinthFaces, patches);
    }

    private static List<PatchCluster> ClusterPlinth(
        MeshData mesh,
        HashSet<int> allowed,
        Vec3[] faceNormals,
        BuildingMeshProfile profile,
        double minAreaM2)
    {
        var neighbors = BuildFaceNeighbors(mesh);
        var visited = new HashSet<int>();
        var patches = new List<PatchCluster>();

        foreach (var seed in allowed)
        {
            if (visited.Contains(seed))
                continue;

            var cluster = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            visited.Add(seed);

            while (queue.Count > 0)
            {
                var fi = queue.Dequeue();
                cluster.Add(fi);
                foreach (var nb in neighbors[fi])
                {
                    if (!allowed.Contains(nb) || visited.Contains(nb))
                        continue;
                    visited.Add(nb);
                    queue.Enqueue(nb);
                }
            }

            foreach (var sub in SplitOversizedCluster(mesh, cluster, profile, MaxClusterSpanM))
            {
                var patch = BuildPatch(mesh, sub, faceNormals, minAreaM2);
                if (patch != null)
                    patches.Add(patch);
            }
        }

        return patches;
    }

    private static IEnumerable<List<int>> SplitOversizedCluster(
        MeshData mesh,
        List<int> cluster,
        BuildingMeshProfile profile,
        double maxSpanM)
    {
        if (cluster.Count < 2)
            return new[] { cluster };

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var items = cluster
            .Select(fi =>
            {
                var c = FaceCentroid(mesh, mesh.Faces[fi]);
                return (fi, x: c.Dot(ax), y: c.Dot(ay));
            })
            .ToList();

        var xSpan = items.Max(t => t.x) - items.Min(t => t.x);
        var ySpan = items.Max(t => t.y) - items.Min(t => t.y);
        if (xSpan <= maxSpanM && ySpan <= maxSpanM)
            return new[] { cluster };

        var byX = xSpan >= ySpan;
        var sorted = items.OrderBy(t => byX ? t.x : t.y).ToList();
        var bestGap = 0.0;
        var bestIdx = -1;
        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var gap = (byX ? sorted[i + 1].x : sorted[i + 1].y)
                      - (byX ? sorted[i].x : sorted[i].y);
            if (gap <= bestGap)
                continue;
            bestGap = gap;
            bestIdx = i;
        }

        if (bestIdx < 0 || bestGap < 0.35)
            return new[] { cluster };

        var left = sorted.Take(bestIdx + 1).Select(t => t.fi).ToList();
        var right = sorted.Skip(bestIdx + 1).Select(t => t.fi).ToList();
        var result = new List<List<int>>();
        foreach (var side in new[] { left, right })
        {
            if (side.Count == 0)
                continue;
            result.AddRange(SplitOversizedCluster(mesh, side, profile, maxSpanM));
        }

        return result;
    }

    private static List<int>[] BuildFaceNeighbors(MeshData mesh)
    {
        var neighbors = Enumerable.Range(0, mesh.Faces.Count).Select(_ => new List<int>()).ToArray();
        var edgeMap = new Dictionary<(int, int), List<int>>();

        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var face = mesh.Faces[fi];
            for (var i = 0; i < face.Length; i++)
            {
                var a = face[i];
                var b = face[(i + 1) % face.Length];
                var edge = a < b ? (a, b) : (b, a);
                if (!edgeMap.TryGetValue(edge, out var list))
                {
                    list = new List<int>();
                    edgeMap[edge] = list;
                }
                list.Add(fi);
            }
        }

        foreach (var faces in edgeMap.Values)
        {
            for (var i = 0; i < faces.Count; i++)
            for (var j = i + 1; j < faces.Count; j++)
            {
                neighbors[faces[i]].Add(faces[j]);
                neighbors[faces[j]].Add(faces[i]);
            }
        }

        return neighbors;
    }

    private static PatchCluster? BuildPatch(
        MeshData mesh,
        List<int> faceIndices,
        Vec3[] faceNormals,
        double minAreaM2)
    {
        var vertSet = new HashSet<int>();
        var wnSum = new Vec3(0, 0, 0);
        var area = 0.0;
        foreach (var fi in faceIndices)
        {
            var a = MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            area += a;
            wnSum = wnSum.Add(faceNormals[fi].Scale(a));
            foreach (var vi in mesh.Faces[fi])
                vertSet.Add(vi);
        }

        if (vertSet.Count < 3 || area < minAreaM2)
            return null;

        var avgN = wnSum.Length() > 1e-6 ? wnSum.Normalized() : new Vec3(0, 0, 1);
        return new PatchCluster(
            faceIndices,
            vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
            area,
            avgN,
            SurfaceKind: PatchSurfaceKind.Plinth);
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}
