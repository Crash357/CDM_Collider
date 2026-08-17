using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

public sealed record PatchCluster(
    IReadOnlyList<int> FaceIndices,
    IReadOnlyList<Vec3> WorldVertices,
    double AreaM2,
    Vec3 DominantNormal,
    int ReferenceIndex = -1,
    PatchSurfaceKind SurfaceKind = PatchSurfaceKind.Wall,
    GableEndKind GableEnd = GableEndKind.None);

public enum GableEndKind { None, PosX, PosY }

public static class AngleSplit
{
    public static IReadOnlyList<PatchCluster> SplitByAngle(
        MeshData mesh,
        double angleThresholdDeg = 30.0,
        double minAreaM2 = 0.05)
    {
        var cosThresh = System.Math.Cos(angleThresholdDeg * System.Math.PI / 180.0);
        var faceNormals = mesh.Faces.Select(f => MeshTopology.FaceNormal(mesh, f)).ToArray();
        var faceNeighbors = BuildFaceNeighbors(mesh);
        var patches = new List<PatchCluster>();

        foreach (var (_, islandFaces) in MeshTopology.EnumerateIslands(mesh))
        {
            if (islandFaces.Count == 0)
                continue;

            var allowed = islandFaces.ToHashSet();
            var visited = new HashSet<int>();

            foreach (var seed in islandFaces)
            {
                if (visited.Contains(seed))
                    continue;

                var clusterFaces = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                visited.Add(seed);

                while (queue.Count > 0)
                {
                    var fi = queue.Dequeue();
                    clusterFaces.Add(fi);
                    var fn = faceNormals[fi];

                    foreach (var nb in faceNeighbors[fi])
                    {
                        if (!allowed.Contains(nb) || visited.Contains(nb))
                            continue;
                        if (fn.Dot(faceNormals[nb]) >= cosThresh)
                        {
                            visited.Add(nb);
                            queue.Enqueue(nb);
                        }
                    }
                }

                var area = clusterFaces.Sum(fi => MeshTopology.FaceArea(mesh, mesh.Faces[fi]));
                if (area < minAreaM2)
                    continue;

                var vertSet = new HashSet<int>();
                foreach (var fi in clusterFaces)
                {
                    foreach (var vi in mesh.Faces[fi])
                        vertSet.Add(vi);
                }

                patches.Add(new PatchCluster(
                    clusterFaces,
                    vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
                    area,
                    DominantFaceNormal(mesh, clusterFaces, faceNormals)));
            }
        }

        return patches;
    }

    private static Vec3 DominantFaceNormal(MeshData mesh, List<int> faceIndices, Vec3[] faceNormals)
    {
        var best = new Vec3(0, 0, 1);
        var bestArea = 0.0;
        foreach (var fi in faceIndices)
        {
            var area = MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            if (area > bestArea)
            {
                bestArea = area;
                best = faceNormals[fi];
            }
        }
        return best;
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
            {
                for (var j = i + 1; j < faces.Count; j++)
                {
                    neighbors[faces[i]].Add(faces[j]);
                    neighbors[faces[j]].Add(faces[i]);
                }
            }
        }

        return neighbors;
    }
}
