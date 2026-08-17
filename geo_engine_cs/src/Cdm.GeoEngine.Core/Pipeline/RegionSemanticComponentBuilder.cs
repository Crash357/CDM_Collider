using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Build collision boxes directly from expanded region face sets (picker semantics),
/// bypassing patch decomposition + greedy component trimmer when the semantic layout
/// already matches the corpus target count (typical sheds: 4 outer walls + 1 roof).
/// </summary>
public static class RegionSemanticComponentBuilder
{
    private const double HorizontalDot = 0.85;
    private const double WallDot = 0.65;

    public static List<MeshComponent>? TryBuild(
        MeshData mesh,
        RegionGuidedFacePlan plan,
        BuildingMeshProfile profile,
        int targetCount)
    {
        if (targetCount <= 0)
            return null;

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var wallBuckets = ClusterWallFaces(
            mesh,
            GetFaces(plan, GeoRegionKind.WallOuter),
            faceNormals,
            profile);

        var roofFaces = new HashSet<int>(GetFaces(plan, GeoRegionKind.Roof));
        roofFaces.UnionWith(GetFaces(plan, GeoRegionKind.Gable));
        roofFaces.UnionWith(GetFaces(plan, GeoRegionKind.Soffit));

        var clusters = new List<FaceCluster>();

        foreach (var bucket in wallBuckets.OrderBy(kv => kv.Key))
        {
            if (bucket.Value.Count == 0)
                continue;
            clusters.Add(new FaceCluster(
                bucket.Value,
                PatchSurfaceKind.Wall,
                BucketWallNormal(bucket.Key, profile)));
        }

        if (roofFaces.Count > 0)
        {
            var roofList = roofFaces.ToList();
            var roofN = DominantNormal(mesh, roofList, faceNormals, profile);
            clusters.Add(new FaceCluster(roofList, PatchSurfaceKind.Slope, roofN));
        }

        // Optional extra bands only when the target count has room (larger buildings).
        if (clusters.Count < targetCount)
        {
            var plinth = GetFaces(plan, GeoRegionKind.Plinth);
            if (plinth.Count > 0 && clusters.Count < targetCount)
            {
                clusters.Add(new FaceCluster(
                    plinth.ToList(),
                    PatchSurfaceKind.Plinth,
                    profile.AxisZ.Normalized().Scale(-1)));
            }
        }

        if (clusters.Count < targetCount)
        {
            var floor = GetFaces(plan, GeoRegionKind.Floor);
            if (floor.Count > 0 && clusters.Count < targetCount)
            {
                clusters.Add(new FaceCluster(
                    floor.ToList(),
                    PatchSurfaceKind.Horizontal,
                    profile.AxisZ.Normalized()));
            }
        }

        clusters = AdjustClusterCount(mesh, clusters, faceNormals, profile, targetCount);
        if (clusters.Count != targetCount)
            return null;

        clusters = clusters
            .OrderBy(c => SortKey(c, mesh, profile))
            .ToList();

        var components = new List<MeshComponent>(clusters.Count);
        for (var i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var verts = cluster.SurfaceKind == PatchSurfaceKind.Wall
                ? CollectWallFitPoints(mesh, cluster.Faces, cluster.HintNormal, profile)
                : CollectVertices(mesh, cluster.Faces);
            if (verts.Count < 4)
                return null;

            MeshData? meshBox;
            if (cluster.SurfaceKind == PatchSurfaceKind.Wall)
            {
                meshBox = ObbBoxBuilder.BuildPatchBox(verts, cluster.HintNormal)
                    ?? FaceBoundsObbFitter.BuildPatchMesh(
                        verts, cluster.HintNormal, profile, PatchSurfaceKind.Wall);
            }
            else
            {
                meshBox = FaceBoundsObbFitter.BuildPatchMesh(
                        verts, cluster.HintNormal, profile, cluster.SurfaceKind)
                    ?? ObbFitter.BuildPatchMeshTight(verts, cluster.HintNormal, profile);
            }

            if (meshBox == null)
                return null;

            var name = $"Component{i + 1:D2}";
            meshBox.Name = name;
            components.Add(new MeshComponent { Name = name, Mesh = meshBox });
        }

        return components;
    }

    private static List<FaceCluster> AdjustClusterCount(
        MeshData mesh,
        List<FaceCluster> clusters,
        Vec3[] faceNormals,
        BuildingMeshProfile profile,
        int targetCount)
    {
        var list = clusters.ToList();

        while (list.Count > targetCount)
        {
            var (i, j) = FindSmallestMergePair(list, mesh);
            var mergedFaces = list[i].Faces.Concat(list[j].Faces).Distinct().ToList();
            var kind = list[i].SurfaceKind == PatchSurfaceKind.Wall || list[j].SurfaceKind == PatchSurfaceKind.Wall
                ? PatchSurfaceKind.Wall
                : list[i].SurfaceKind;
            var hint = DominantNormal(mesh, mergedFaces, faceNormals, profile);
            list[i] = new FaceCluster(mergedFaces, kind, hint);
            list.RemoveAt(j);
        }

        return list;
    }

    private static (int I, int J) FindSmallestMergePair(List<FaceCluster> list, MeshData mesh)
    {
        var bestI = 0;
        var bestJ = 1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var ci = ClusterCentroid(mesh, list[i].Faces);
                var cj = ClusterCentroid(mesh, list[j].Faces);
                var dist = Vec3.Distance(ci, cj);
                var kindPenalty = list[i].SurfaceKind == list[j].SurfaceKind ? 0.0 : 2.0;
                var wallPenalty = list[i].SurfaceKind == PatchSurfaceKind.Wall
                                  && list[j].SurfaceKind == PatchSurfaceKind.Wall
                    ? 0.0
                    : 0.5;
                var score = dist + kindPenalty + wallPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestI = i;
                    bestJ = j;
                }
            }
        }

        return (bestI, bestJ);
    }

    private static Dictionary<int, List<int>> ClusterWallFaces(
        MeshData mesh,
        IReadOnlyCollection<int> wallFaces,
        Vec3[] faceNormals,
        BuildingMeshProfile profile)
    {
        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();
        var buckets = new Dictionary<int, List<int>>();

        foreach (var fi in wallFaces)
        {
            if (fi < 0 || fi >= faceNormals.Length)
                continue;
            var n = faceNormals[fi];
            if (System.Math.Abs(n.Dot(az)) > HorizontalDot)
                continue;

            var dotX = n.Dot(ax);
            var dotY = n.Dot(ay);
            int bucket;
            if (System.Math.Abs(dotX) >= System.Math.Abs(dotY) && System.Math.Abs(dotX) >= WallDot)
                bucket = dotX >= 0 ? 0 : 1;
            else if (System.Math.Abs(dotY) >= WallDot)
                bucket = dotY >= 0 ? 2 : 3;
            else
                bucket = NearestWallBucket(n, ax, ay);

            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = new List<int>();
                buckets[bucket] = list;
            }
            list.Add(fi);
        }

        return buckets;
    }

    private static int NearestWallBucket(Vec3 n, Vec3 ax, Vec3 ay)
    {
        var candidates = new[] { ax, ax.Scale(-1), ay, ay.Scale(-1) };
        var best = 0;
        var bestDot = double.MinValue;
        for (var i = 0; i < candidates.Length; i++)
        {
            var d = System.Math.Abs(n.Dot(candidates[i]));
            if (d > bestDot)
            {
                bestDot = d;
                best = i;
            }
        }
        return best;
    }

    private static Vec3 BucketWallNormal(int bucket, BuildingMeshProfile profile)
    {
        return bucket switch
        {
            0 => profile.AxisX.Normalized(),
            1 => profile.AxisX.Normalized().Scale(-1),
            2 => profile.AxisY.Normalized(),
            3 => profile.AxisY.Normalized().Scale(-1),
            _ => profile.AxisX.Normalized(),
        };
    }

    private static HashSet<int> GetFaces(RegionGuidedFacePlan plan, GeoRegionKind kind)
    {
        return plan.FacesByKind.TryGetValue(kind, out var faces)
            ? faces
            : new HashSet<int>();
    }

    private static List<Vec3> CollectWallFitPoints(
        MeshData mesh,
        IReadOnlyList<int> faceIndices,
        Vec3 wallNormal,
        BuildingMeshProfile profile)
    {
        var n = wallNormal.Normalized();
        var wallThick = profile.WallThicknessM > 0 ? profile.WallThicknessM : ObbBoxBuilder.WallSlabM;
        var points = new List<Vec3>();

        foreach (var fi in faceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            var face = mesh.Faces[fi];
            var c = FaceCentroid(mesh, face);
            points.Add(c);
            foreach (var vi in face)
            {
                if (vi < 0 || vi >= mesh.Vertices.Count)
                    continue;
                points.Add(mesh.Vertices[vi]);
            }
        }

        if (points.Count == 0)
            return points;

        var outer = points.Max(p => p.Dot(n));
        var keepMin = outer - wallThick * 1.5 - ObbBoxBuilder.SkinM;
        return points.Where(p => p.Dot(n) >= keepMin).ToList();
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        var count = 0;
        foreach (var vi in face)
        {
            if (vi < 0 || vi >= mesh.Vertices.Count)
                continue;
            sum = sum.Add(mesh.Vertices[vi]);
            count++;
        }
        return count > 0 ? sum.Scale(1.0 / count) : new Vec3(0, 0, 0);
    }

    private static List<Vec3> CollectVertices(MeshData mesh, IReadOnlyList<int> faceIndices)
    {
        var verts = new List<Vec3>();
        var seen = new HashSet<int>();
        foreach (var fi in faceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            foreach (var vi in mesh.Faces[fi])
            {
                if (vi < 0 || vi >= mesh.Vertices.Count || !seen.Add(vi))
                    continue;
                verts.Add(mesh.Vertices[vi]);
            }
        }
        return verts;
    }

    private static Vec3 DominantNormal(
        MeshData mesh,
        IReadOnlyList<int> faceIndices,
        Vec3[] faceNormals,
        BuildingMeshProfile profile)
    {
        Vec3 sum = new(0, 0, 0);
        foreach (var fi in faceIndices)
        {
            if (fi < 0 || fi >= faceNormals.Length)
                continue;
            var area = MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            sum = sum.Add(faceNormals[fi].Scale(area));
        }
        return sum.Length() > 1e-6
            ? sum.Normalized()
            : profile.AxisZ.Normalized();
    }

    private static Vec3 ClusterCentroid(MeshData mesh, IReadOnlyList<int> faceIndices)
    {
        var verts = CollectVertices(mesh, faceIndices);
        return verts.Count > 0 ? Vec3.Centroid(verts) : new Vec3(0, 0, 0);
    }

    private static (double, double, double) SortKey(FaceCluster cluster, MeshData mesh, BuildingMeshProfile profile)
    {
        var c = ClusterCentroid(mesh, cluster.Faces);
        var az = profile.AxisZ.Normalized();
        var isRoof = cluster.SurfaceKind is PatchSurfaceKind.Horizontal or PatchSurfaceKind.Slope;
        return (isRoof ? 1.0 : 0.0, c.Dot(profile.AxisY.Normalized()), c.Dot(profile.AxisX.Normalized()));
    }

    private sealed record FaceCluster(
        List<int> Faces,
        PatchSurfaceKind SurfaceKind,
        Vec3 HintNormal);
}
