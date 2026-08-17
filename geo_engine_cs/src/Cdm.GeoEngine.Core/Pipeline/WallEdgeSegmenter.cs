using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

public sealed class WallEdgeSegmentationOptions
{
    public double MinAreaM2 { get; init; } = 0.06;
    public double MaxSpanM { get; init; } = 3.5;
    public double CoplanarDot { get; init; } = 0.996;
    public double DoorMarginM { get; init; } = 0.05;
    public double OverlapEpsilonM { get; init; } = 0.10;
    public IReadOnlyList<DoorRegion>? DoorRegions { get; init; }
}

/// <summary>
/// Topology-driven wall segmentation: flood faces on the same plane, cut at non-wall edges,
/// door openings, and max tangent span. Replaces rectangle-merge for blind FaceDriven walls.
/// </summary>
public static class WallEdgeSegmenter
{
    public static double DefaultMaxSpanM(BuildingMeshProfile profile)
    {
        var longH = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);
        if (longH < 6.0)
            return 0;
        if (longH > 7.0)
            return System.Math.Clamp(longH / 3.0, 3.5, 4.5);
        return System.Math.Clamp(longH / 3.5, 3.0, 4.0);
    }

    public static IReadOnlyList<PatchCluster> SegmentWallFaces(
        MeshData mesh,
        List<int> wallFaces,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        WallEdgeSegmentationOptions? options = null)
    {
        options ??= new WallEdgeSegmentationOptions();
        if (wallFaces.Count == 0)
            return Array.Empty<PatchCluster>();

        return RefineRectanglePatches(mesh, wallFaces, wallNormal, profile, options);
    }

    /// <summary>
    /// Rectangle merge with span cap, then door/max-span refinement (no per-face BFS flood).
    /// </summary>
    public static IReadOnlyList<PatchCluster> RefineRectanglePatches(
        MeshData mesh,
        List<int> wallFaces,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        WallEdgeSegmentationOptions options)
    {
        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var patches = FaceDrivenDecomposer.MergeRectanglesForWall(
            mesh, wallFaces, wallNormal, profile, options.MinAreaM2, faceNormals, options.MaxSpanM);

        patches = EnforceMaxSpan(mesh, patches.ToList(), wallNormal, profile, options);
        if (options.MaxSpanM > 0 || DefaultMaxSpanM(profile) > 0)
            return ResolveTangentOverlaps(mesh, patches.ToList(), wallNormal, profile, options);
        return patches;
    }

    /// <summary>Topology BFS segmentation (used in unit tests; resolution meshes may over-fragment).</summary>
    public static IReadOnlyList<PatchCluster> SegmentWallFacesByTopology(
        MeshData mesh,
        List<int> wallFaces,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        WallEdgeSegmentationOptions? options = null)
    {
        options ??= new WallEdgeSegmentationOptions();
        if (wallFaces.Count == 0)
            return Array.Empty<PatchCluster>();

        var allowed = wallFaces.ToHashSet();
        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();
        var neighbors = BuildFaceNeighbors(mesh);
        var visited = new HashSet<int>();
        var clusters = new List<List<int>>();

        foreach (var seed in wallFaces)
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
                    if (fn.Dot(faceNormals[nb]) < options.CoplanarDot)
                        continue;
                    if (EdgeCrossesDoor(mesh, mesh.Faces[fi], mesh.Faces[nb], options))
                        continue;

                    visited.Add(nb);
                    queue.Enqueue(nb);
                }
            }

            clusters.AddRange(SplitClusterByMaxSpan(mesh, cluster, wallNormal, profile, options, faceNormals));
        }

        var patches = clusters
            .Select(faces => BuildPatch(mesh, faces, options.MinAreaM2, faceNormals, wallNormal))
            .Where(p => p != null)
            .Cast<PatchCluster>()
            .ToList();

        patches = MergeAdjacentSegments(mesh, patches, wallNormal, profile, options);
        patches = EnforceMaxSpan(mesh, patches, wallNormal, profile, options);
        return ResolveTangentOverlaps(mesh, patches, wallNormal, profile, options);
    }

    private static List<PatchCluster> MergeAdjacentSegments(
        MeshData mesh,
        List<PatchCluster> patches,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        WallEdgeSegmentationOptions options)
    {
        if (patches.Count < 2)
            return patches;

        var (u, v, n) = BuildWallFrame(wallNormal, profile);
        var maxSpan = options.MaxSpanM > 0 ? options.MaxSpanM : DefaultMaxSpanM(profile);
        var rects = patches
            .Select(p =>
            {
                var r = BoundsRect(p.WorldVertices, u, v, n);
                var nProj = p.WorldVertices.Select(pt => pt.Dot(n)).ToList();
                return new FaceRect(r.UMin, r.UMax, r.VMin, r.VMax, nProj.Min(), nProj.Max());
            })
            .ToArray();

        var groups = MergeRectGroups(rects, RectsAdjacent, maxSpan, gapM: 0.12);
        if (groups.Count == patches.Count)
            return patches;

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var result = new List<PatchCluster>();
        foreach (var group in groups)
        {
            if (group.Count == 1)
            {
                result.Add(patches[group[0]]);
                continue;
            }

            var faces = group
                .SelectMany(i => patches[i].FaceIndices)
                .Distinct()
                .ToList();
            var merged = BuildPatch(mesh, faces, options.MinAreaM2, faceNormals, wallNormal);
            if (merged != null)
                result.Add(merged);
            else
            {
                foreach (var i in group)
                    result.Add(patches[i]);
            }
        }

        return result;
    }

    private static List<List<int>> MergeRectGroups(
        FaceRect[] rects,
        Func<FaceRect, FaceRect, double, bool> shouldMerge,
        double maxSpanM,
        double gapM)
    {
        var n = rects.Length;
        var parent = Enumerable.Range(0, n).ToArray();

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

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (!shouldMerge(rects[i], rects[j], gapM))
                    continue;
                if (maxSpanM > 0)
                {
                    var combined = UnionRect(rects[i], rects[j]);
                    if ((combined.UMax - combined.UMin) > maxSpanM
                        || (combined.VMax - combined.VMin) > maxSpanM)
                        continue;
                }
                Union(i, j);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        return groups.Values.ToList();
    }

    private static bool RectsAdjacent(FaceRect a, FaceRect b, double gap)
    {
        var uGap = RangeGap(a.UMin, a.UMax, b.UMin, b.UMax);
        var vGap = RangeGap(a.VMin, a.VMax, b.VMin, b.VMax);
        var uOverlap = RangesOverlap(a.UMin, a.UMax, b.UMin, b.UMax);
        var vOverlap = RangesOverlap(a.VMin, a.VMax, b.VMin, b.VMax);
        if (!SameWallPlane(a, b, gap))
            return false;
        if (uOverlap && vGap <= gap)
            return true;
        if (vOverlap && uGap <= gap)
            return true;
        return uGap <= gap && vGap <= gap;
    }

    private static bool SameWallPlane(FaceRect a, FaceRect b, double gap)
    {
        var aMid = (a.NMin + a.NMax) * 0.5;
        var bMid = (b.NMin + b.NMax) * 0.5;
        return System.Math.Abs(aMid - bMid) <= gap + 0.08;
    }

    private static FaceRect UnionRect(FaceRect a, FaceRect b) =>
        new(
            System.Math.Min(a.UMin, b.UMin),
            System.Math.Max(a.UMax, b.UMax),
            System.Math.Min(a.VMin, b.VMin),
            System.Math.Max(a.VMax, b.VMax),
            System.Math.Min(a.NMin, b.NMin),
            System.Math.Max(a.NMax, b.NMax));

    private static double RangeGap(double aMin, double aMax, double bMin, double bMax)
    {
        if (aMax < bMin)
            return bMin - aMax;
        if (bMax < aMin)
            return aMin - bMax;
        return 0;
    }

    private readonly record struct FaceRect(
        double UMin,
        double UMax,
        double VMin,
        double VMax,
        double NMin,
        double NMax);

    private static List<PatchCluster> EnforceMaxSpan(
        MeshData mesh,
        List<PatchCluster> patches,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        WallEdgeSegmentationOptions options)
    {
        var maxSpan = options.MaxSpanM > 0 ? options.MaxSpanM : DefaultMaxSpanM(profile);
        if (maxSpan <= 0)
            return patches;
        var result = new List<PatchCluster>();
        foreach (var patch in patches)
        {
            var diag = PatchDiagnostics.AnalyzeOne(mesh, patch, 0, profile);
            if (diag.TangentSpanM <= maxSpan * 1.05)
            {
                result.Add(patch);
                continue;
            }

            var split = SpatialPatchSubdivider.Subdivide(
                mesh,
                new[] { patch },
                profile,
                new SpatialSubdivisionOptions
                {
                    MinGapM = 1.0,
                    BinSizeM = 0.12,
                    MinPatchAreaM2 = options.MinAreaM2,
                    MaxInPlaneSpanM = maxSpan,
                    DoorRegions = options.DoorRegions,
                    SpanFallbackOnly = true,
                });
            result.AddRange(split);
        }

        return result;
    }

    private static IEnumerable<List<int>> SplitClusterByMaxSpan(
        MeshData mesh,
        List<int> cluster,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        WallEdgeSegmentationOptions options,
        Vec3[] faceNormals)
    {
        if (cluster.Count == 0)
            yield break;

        var (u, v, n) = BuildWallFrame(wallNormal, profile);
        var maxSpan = options.MaxSpanM > 0 ? options.MaxSpanM : DefaultMaxSpanM(profile);

        var faceData = cluster.Select(fi =>
        {
            var c = FaceCentroid(mesh, mesh.Faces[fi]);
            return (fi, pu: c.Dot(u), pv: c.Dot(v));
        }).ToList();

        var uMin = faceData.Min(f => f.pu);
        var uMax = faceData.Max(f => f.pu);
        var vMin = faceData.Min(f => f.pv);
        var vMax = faceData.Max(f => f.pv);
        var uSpan = uMax - uMin;
        var vSpan = vMax - vMin;

        var splitU = uSpan > maxSpan && uSpan >= vSpan;
        var span = splitU ? uSpan : vSpan;
        if (span <= maxSpan * 1.05)
        {
            yield return cluster;
            yield break;
        }

        var cuts = CollectSpanCuts(splitU ? uMin : vMin, splitU ? uMax : vMax, maxSpan);
        if (cuts.Count == 0)
        {
            yield return cluster;
            yield break;
        }

        var bounds = BuildBounds(splitU ? uMin : vMin, splitU ? uMax : vMax, cuts);
        var buckets = new Dictionary<int, List<int>>();
        foreach (var (fi, pu, pv) in faceData)
        {
            var coord = splitU ? pu : pv;
            var bin = FindBin(coord, bounds);
            if (!buckets.TryGetValue(bin, out var list))
            {
                list = new List<int>();
                buckets[bin] = list;
            }
            list.Add(fi);
        }

        foreach (var faces in buckets.Values)
        {
            if (faces.Count == 0)
                continue;
            foreach (var sub in SplitClusterByMaxSpan(mesh, faces, wallNormal, profile, options, faceNormals))
                yield return sub;
        }
    }

    private static IReadOnlyList<PatchCluster> ResolveTangentOverlaps(
        MeshData mesh,
        List<PatchCluster> patches,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        WallEdgeSegmentationOptions options)
    {
        if (patches.Count < 2)
            return patches;

        var (u, v, n) = BuildWallFrame(wallNormal, profile);
        var rects = patches
            .Select(p => BoundsRect(p.WorldVertices, u, v, n))
            .ToArray();
        var changed = true;
        var current = patches;

        while (changed && current.Count > 1)
        {
            changed = false;
            var next = new List<PatchCluster>(current);
            rects = next.Select(p => BoundsRect(p.WorldVertices, u, v, n)).ToArray();

            for (var i = 0; i < next.Count; i++)
            {
                for (var j = i + 1; j < next.Count; j++)
                {
                    var a = rects[i];
                    var b = rects[j];
                    if (!RangesOverlap(a.UMin, a.UMax, b.UMin, b.UMax)
                        || !RangesOverlap(a.VMin, a.VMax, b.VMin, b.VMax))
                        continue;

                    var overlapU = System.Math.Min(a.UMax, b.UMax) - System.Math.Max(a.UMin, b.UMin);
                    var overlapV = System.Math.Min(a.VMax, b.VMax) - System.Math.Max(a.VMin, b.VMin);
                    if (overlapU <= options.OverlapEpsilonM || overlapV <= options.OverlapEpsilonM)
                        continue;

                    var areaA = (a.UMax - a.UMin) * (a.VMax - a.VMin);
                    var areaB = (b.UMax - b.UMin) * (b.VMax - b.VMin);
                    var larger = areaA >= areaB ? i : j;
                    var cutCoord = larger == i
                        ? (System.Math.Min(a.UMax, b.UMax) + System.Math.Max(a.UMin, b.UMin)) * 0.5
                        : (System.Math.Min(b.UMax, a.UMax) + System.Math.Max(b.UMin, a.UMin)) * 0.5;

                    var split = SplitPatchAtCoord(mesh, next[larger], u, cutCoord, options.MinAreaM2, wallNormal);
                    if (split.Count != 2)
                        continue;

                    next.RemoveAt(larger);
                    next.InsertRange(larger, split);
                    changed = true;
                    break;
                }

                if (changed)
                    break;
            }

            current = next;
        }

        return current;
    }

    private static List<PatchCluster> SplitPatchAtCoord(
        MeshData mesh,
        PatchCluster patch,
        Vec3 axis,
        double cutCoord,
        double minAreaM2,
        Vec3 wallNormal)
    {
        var leftFaces = new List<int>();
        var rightFaces = new List<int>();

        foreach (var fi in patch.FaceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            var c = FaceCentroid(mesh, mesh.Faces[fi]);
            if (c.Dot(axis) < cutCoord)
                leftFaces.Add(fi);
            else
                rightFaces.Add(fi);
        }

        if (leftFaces.Count == 0 || rightFaces.Count == 0)
            return new List<PatchCluster> { patch };

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var left = BuildPatch(mesh, leftFaces, minAreaM2, faceNormals, wallNormal);
        var right = BuildPatch(mesh, rightFaces, minAreaM2, faceNormals, wallNormal);
        if (left == null || right == null)
            return new List<PatchCluster> { patch };

        return new List<PatchCluster> { left, right };
    }

    private static bool EdgeCrossesDoor(
        MeshData mesh,
        int[] faceA,
        int[] faceB,
        WallEdgeSegmentationOptions options)
    {
        if (options.DoorRegions is not { Count: > 0 })
            return false;

        var shared = FindSharedEdge(faceA, faceB);
        if (shared == null)
            return false;

        var (a, b) = shared.Value;
        var mid = mesh.Vertices[a].Add(mesh.Vertices[b]).Scale(0.5);
        return options.DoorRegions.Any(d => d.Contains(mid, options.DoorMarginM));
    }

    private static (int, int)? FindSharedEdge(int[] faceA, int[] faceB)
    {
        for (var i = 0; i < faceA.Length; i++)
        {
            var ea = faceA[i];
            var eb = faceA[(i + 1) % faceA.Length];
            for (var j = 0; j < faceB.Length; j++)
            {
                var fa = faceB[j];
                var fb = faceB[(j + 1) % faceB.Length];
                if ((ea == fa && eb == fb) || (ea == fb && eb == fa))
                    return (ea, eb);
            }
        }

        return null;
    }

    private static PatchCluster? BuildPatch(
        MeshData mesh,
        List<int> faceIndices,
        double minAreaM2,
        Vec3[] faceNormals,
        Vec3 overrideNormal)
    {
        var vertSet = new HashSet<int>();
        var wnSum = new Vec3(0, 0, 0);
        var totalArea = 0.0;

        foreach (var fi in faceIndices)
        {
            var area = MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
            wnSum = wnSum.Add(faceNormals[fi].Scale(area));
            totalArea += area;
            foreach (var vi in mesh.Faces[fi])
                vertSet.Add(vi);
        }

        if (vertSet.Count < 3 || totalArea < minAreaM2)
            return null;

        var avgN = overrideNormal.Length() > 1e-6 ? overrideNormal.Normalized() : wnSum.Normalized();
        return new PatchCluster(
            faceIndices,
            vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
            totalArea,
            avgN,
            SurfaceKind: PatchSurfaceKind.Wall);
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

    private static (Vec3 u, Vec3 v, Vec3 n) BuildWallFrame(Vec3 wallNormal, BuildingMeshProfile profile)
    {
        var n = wallNormal.Normalized();
        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();

        Vec3 u;
        if (System.Math.Abs(n.Dot(ax)) > 0.9)
            u = ay;
        else if (System.Math.Abs(n.Dot(ay)) > 0.9)
            u = ax;
        else
            u = ax;

        u = u.Sub(n.Scale(u.Dot(n)));
        if (u.Length() < 1e-6)
            u = profile.AxisZ.Sub(n.Scale(profile.AxisZ.Dot(n)));
        u = u.Normalized();
        var v = n.Cross(u).Normalized();
        return (u, v, n);
    }

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }

    private static List<double> CollectSpanCuts(double min, double max, double maxSpanM)
    {
        var cuts = new List<double>();
        var pos = min + maxSpanM;
        while (pos < max - maxSpanM * 0.4)
        {
            cuts.Add(pos);
            pos += maxSpanM;
        }

        return cuts;
    }

    private static List<(double lo, double hi)> BuildBounds(double min, double max, List<double> cuts)
    {
        var coords = new List<double> { min };
        coords.AddRange(cuts.Where(c => c > min + 0.04 && c < max - 0.04));
        coords.Add(max);
        coords.Sort();

        var bounds = new List<(double, double)>();
        for (var i = 0; i < coords.Count - 1; i++)
        {
            if (coords[i + 1] - coords[i] >= 0.08)
                bounds.Add((coords[i], coords[i + 1]));
        }

        return bounds;
    }

    private static int FindBin(double value, IReadOnlyList<(double lo, double hi)> bounds)
    {
        for (var i = 0; i < bounds.Count; i++)
        {
            if (value >= bounds[i].lo - 1e-6 && value <= bounds[i].hi + 1e-6)
                return i;
        }

        return System.Math.Max(0, bounds.Count - 1);
    }

    private static (double UMin, double UMax, double VMin, double VMax) BoundsRect(
        IReadOnlyList<Vec3> verts, Vec3 u, Vec3 v, Vec3 n)
    {
        var uMin = double.PositiveInfinity;
        var uMax = double.NegativeInfinity;
        var vMin = double.PositiveInfinity;
        var vMax = double.NegativeInfinity;
        foreach (var p in verts)
        {
            uMin = System.Math.Min(uMin, p.Dot(u));
            uMax = System.Math.Max(uMax, p.Dot(u));
            vMin = System.Math.Min(vMin, p.Dot(v));
            vMax = System.Math.Max(vMax, p.Dot(v));
            _ = p.Dot(n);
        }

        return (uMin, uMax, vMin, vMax);
    }

    private static bool RangesOverlap(double aMin, double aMax, double bMin, double bMax)
        => aMin <= bMax && bMin <= aMax;
}
