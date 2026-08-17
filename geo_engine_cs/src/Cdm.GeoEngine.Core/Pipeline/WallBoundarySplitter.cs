using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Topological wall patch splits: corners (perpendicular wall edges) and thick single-shell patches.
/// </summary>
public static class WallBoundarySplitter
{
    private const double CornerNormalDot = 0.35;
    private const double CoplanarDot = 0.92;

    public static IReadOnlyList<PatchCluster> SplitAtCorners(
        MeshData mesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile)
    {
        if (patches.Count == 0)
            return patches;

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();
        var neighbors = BuildFaceNeighbors(mesh);
        var result = new List<PatchCluster>();

        foreach (var patch in patches)
        {
            if (patch.SurfaceKind != PatchSurfaceKind.Wall)
            {
                result.Add(patch);
                continue;
            }

            if (patch.FaceIndices.Count < 2)
            {
                result.Add(patch);
                continue;
            }

            var allowed = patch.FaceIndices.ToHashSet();
            var visited = new HashSet<int>();
            var subClusters = new List<List<int>>();

            foreach (var seed in patch.FaceIndices)
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
                    var fn = faceNormals[fi];

                    foreach (var nb in neighbors[fi])
                    {
                        if (!allowed.Contains(nb) || visited.Contains(nb))
                            continue;

                        if (IsCornerBoundary(faceNormals, fi, nb, fn))
                            continue;

                        visited.Add(nb);
                        queue.Enqueue(nb);
                    }
                }

                subClusters.Add(cluster);
            }

            if (subClusters.Count <= 1)
            {
                result.Add(patch);
                continue;
            }

            foreach (var faces in subClusters)
            {
                var sub = BuildSubPatch(mesh, faces, patch, faceNormals);
                if (sub != null)
                    result.Add(sub);
            }
        }

        return result;
    }

    /// <summary>
    /// Split patches whose thickness suggests two shells or framing — keep dominant outward shell.
    /// </summary>
    public static IReadOnlyList<PatchCluster> SplitThickSingleShells(
        MeshData mesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile)
    {
        var wallThick = profile.WallThicknessM;
        var result = new List<PatchCluster>();

        foreach (var patch in patches)
        {
            var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(mesh, patch);
            if (verts.Count < 4)
            {
                result.Add(patch);
                continue;
            }

            var n = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(patch.DominantNormal, profile);
            var nProj = verts.Select(p => p.Dot(n)).ToList();
            var thickness = nProj.Max() - nProj.Min();

            if (thickness <= wallThick * 2.2)
            {
                result.Add(patch);
                continue;
            }

            var mid = (nProj.Min() + nProj.Max()) * 0.5;
            var outerFaces = new List<int>();
            var innerFaces = new List<int>();

            foreach (var fi in patch.FaceIndices)
            {
                if (fi < 0 || fi >= mesh.Faces.Count)
                    continue;
                var c = FaceCentroid(mesh, mesh.Faces[fi]);
                if (c.Dot(n) >= mid)
                    outerFaces.Add(fi);
                else
                    innerFaces.Add(fi);
            }

            var faceNormals = mesh.Faces.Select(f => MeshTopology.FaceNormal(mesh, f).Normalized()).ToArray();
            var outer = BuildSubPatch(mesh, outerFaces, patch, faceNormals);
            var inner = BuildSubPatch(mesh, innerFaces, patch, faceNormals);
            if (outer != null)
                result.Add(outer);
            if (inner != null)
                result.Add(inner);
            if (outer == null && inner == null)
                result.Add(patch);
        }

        return result;
    }

    private static bool IsCornerBoundary(Vec3[] faceNormals, int fi, int nb, Vec3 fn)
    {
        var nn = faceNormals[nb];
        if (fn.Dot(nn) < CoplanarDot)
            return System.Math.Abs(fn.Dot(nn)) < CornerNormalDot || fn.Dot(nn) < 0.2;
        return false;
    }

    private static PatchCluster? BuildSubPatch(
        MeshData mesh,
        List<int> faceIndices,
        PatchCluster parent,
        Vec3[] faceNormals)
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

        if (vertSet.Count < 3 || area < 0.04)
            return null;

        return new PatchCluster(
            faceIndices,
            vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
            area,
            parent.DominantNormal,
            parent.ReferenceIndex,
            parent.SurfaceKind);
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
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
