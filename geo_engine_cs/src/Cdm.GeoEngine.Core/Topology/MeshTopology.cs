using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.Topology;

public static class MeshTopology
{
    public static IEnumerable<(HashSet<int> Vertices, List<int> Faces)> EnumerateIslands(MeshData mesh)
    {
        var faceCount = mesh.Faces.Count;
        if (faceCount == 0)
            yield break;

        var visited = new bool[faceCount];
        var edgeToFaces = BuildEdgeToFaces(mesh);

        for (var seed = 0; seed < faceCount; seed++)
        {
            if (visited[seed])
                continue;

            var islandFaces = new List<int>();
            var islandVerts = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            visited[seed] = true;

            while (queue.Count > 0)
            {
                var fi = queue.Dequeue();
                islandFaces.Add(fi);
                foreach (var vi in mesh.Faces[fi])
                    islandVerts.Add(vi);

                foreach (var edge in FaceEdges(mesh.Faces[fi]))
                {
                    if (!edgeToFaces.TryGetValue(edge, out var neighbors))
                        continue;
                    foreach (var nb in neighbors)
                    {
                        if (visited[nb])
                            continue;
                        visited[nb] = true;
                        queue.Enqueue(nb);
                    }
                }
            }

            yield return (islandVerts, islandFaces);
        }
    }

    public static Vec3 FaceNormal(MeshData mesh, int[] face)
    {
        if (face.Length < 3)
            return new Vec3(0, 0, 1);
        var a = mesh.Vertices[face[0]];
        var b = mesh.Vertices[face[1]];
        var c = mesh.Vertices[face[2]];
        return b.Sub(a).Cross(c.Sub(a)).Normalized();
    }

    public static double FaceArea(MeshData mesh, int[] face)
    {
        if (face.Length < 3)
            return 0;
        var origin = mesh.Vertices[face[0]];
        double area = 0;
        for (var i = 1; i < face.Length - 1; i++)
        {
            var u = mesh.Vertices[face[i]].Sub(origin);
            var v = mesh.Vertices[face[i + 1]].Sub(origin);
            area += u.Cross(v).Length() * 0.5;
        }
        return area;
    }

    private static Dictionary<(int, int), List<int>> BuildEdgeToFaces(MeshData mesh)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            foreach (var edge in FaceEdges(mesh.Faces[fi]))
            {
                if (!map.TryGetValue(edge, out var list))
                {
                    list = new List<int>();
                    map[edge] = list;
                }
                list.Add(fi);
            }
        }
        return map;
    }

    private static IEnumerable<(int, int)> FaceEdges(int[] face)
    {
        for (var i = 0; i < face.Length; i++)
        {
            var a = face[i];
            var b = face[(i + 1) % face.Length];
            yield return a < b ? (a, b) : (b, a);
        }
    }
}
