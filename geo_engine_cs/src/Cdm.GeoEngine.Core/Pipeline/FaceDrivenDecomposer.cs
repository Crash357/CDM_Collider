using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Decompose remaining mesh by actual wall/floor face geometry (not axis-spacing grid).
/// Classifies faces, projects wall/horizontal groups to 2D, merges adjacent rectangles.
/// </summary>
public static class FaceDrivenDecomposer
{
    private const double HorizontalDotThresh = 0.85;
    private const double WallDotThresh = 0.65;
    private const double MergeGapM = 0.12;
    private const double SlopeAngleDeg = 25.0;

    private enum FaceClass
    {
        WallPosX,
        WallNegX,
        WallPosY,
        WallNegY,
        Horizontal,
        HorizontalBase,
        Slope,
    }

    public static IReadOnlyList<PatchCluster> Split(
        MeshData mesh,
        double minAreaM2,
        BuildingMeshProfile profile,
        IReadOnlyList<DoorRegion>? doorRegions = null,
        bool useWallEdgeSegmentation = true,
        double wallMaxSpanM = 0,
        RegionGuidedFacePlan? regionPlan = null)
    {
        if (mesh.Faces.Count == 0)
            return Array.Empty<PatchCluster>();

        var faceNormals = mesh.Faces
            .Select(f => MeshTopology.FaceNormal(mesh, f).Normalized())
            .ToArray();

        var guided = regionPlan?.AllGuidedFaces;
        var useRegionGuided = regionPlan != null && guided is { Count: > 0 };

        HashSet<int> gableFaces;
        IReadOnlyList<PatchCluster> gablePatches;
        HashSet<int> plinthFaces;
        IReadOnlyList<PatchCluster> plinthPatches;
        HashSet<int> soffitFaces;
        IReadOnlyList<PatchCluster> soffitPatches;

        if (useRegionGuided)
        {
            gableFaces = RegionFaces(regionPlan!, GeoRegionKind.Gable);
            plinthFaces = RegionFaces(regionPlan!, GeoRegionKind.Plinth);
            soffitFaces = RegionFaces(regionPlan!, GeoRegionKind.Soffit);
            gablePatches = BuildRegionSlopePatches(mesh, gableFaces, minAreaM2, faceNormals);
            plinthPatches = MergeBaseLipFaces(mesh, plinthFaces.ToList(), minAreaM2, faceNormals);
            soffitPatches = MergeHorizontal(
                mesh, soffitFaces.ToList(), profile.AxisX, profile.AxisY, profile.AxisZ,
                System.Math.Min(minAreaM2, 0.04), faceNormals, profile);
        }
        else
        {
            (soffitFaces, soffitPatches) = PatchSoffitGrouper.Extract(mesh, profile, System.Math.Min(minAreaM2, 0.03));
            (gableFaces, gablePatches) = PatchGableSlopeGrouper.Extract(
                mesh, profile, soffitFaces, System.Math.Min(minAreaM2, 0.05));
            (plinthFaces, plinthPatches) = PatchPlinthGrouper.Extract(mesh, profile, System.Math.Min(minAreaM2, 0.04));
        }
        var bins = Enum.GetValues<FaceClass>().ToDictionary(c => c, _ => new List<int>());

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();
        var minZ = mesh.Vertices.Count > 0
            ? mesh.Vertices.Min(v => v.Dot(az))
            : 0.0;
        var groundBandTop = minZ + System.Math.Clamp(profile.SizeM.Z * 0.22, 0.85, 1.15);

        for (var fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (plinthFaces.Contains(fi) || soffitFaces.Contains(fi) || gableFaces.Contains(fi))
                continue;

            if (useRegionGuided)
            {
                var inGuided = guided!.Contains(fi);
                if (inGuided)
                {
                    if (ClassifyGuidedFace(fi, regionPlan!, mesh, faceNormals, profile, bins))
                        continue;
                    // BUGFIX (Region-Marking-Workflow Session 2): a guided face whose
                    // kind assignment doesn't match its true geometry (see the Floor
                    // case above) must fall through to the same true-geometry blind
                    // classification below, not be silently dropped — previously this
                    // branch always `continue`d for guided faces regardless of the
                    // ClassifyGuidedFace result, which either produced a garbage merge
                    // (when it force-classified) or (if changed to just skip) would
                    // have left the face completely unassigned/missing from the LOD.
                }
                else if (!regionPlan!.BlindFallbackForUnassigned)
                {
                    continue;
                }
            }

            var wn = faceNormals[fi];
            var absZ = System.Math.Abs(wn.Dot(az));
            var cz = FaceCentroid(mesh, mesh.Faces[fi]).Dot(az);

            if (absZ > HorizontalDotThresh)
            {
                bins[FaceClass.Horizontal].Add(fi);
                continue;
            }

            if (cz <= groundBandTop && absZ >= 0.12 && absZ < HorizontalDotThresh)
            {
                bins[FaceClass.HorizontalBase].Add(fi);
                continue;
            }

            var wallAxes = new[]
            {
                (ax, FaceClass.WallPosX),
                (ax.Scale(-1), FaceClass.WallNegX),
                (ay, FaceClass.WallPosY),
                (ay.Scale(-1), FaceClass.WallNegY),
            };

            var bestClass = FaceClass.Slope;
            var bestDot = -2.0;
            foreach (var (axis, cls) in wallAxes)
            {
                var d = wn.Dot(axis);
                if (d > bestDot)
                {
                    bestDot = d;
                    bestClass = cls;
                }
            }

            if (bestDot > WallDotThresh && absZ < PatchSurfaceClassifier.WallMaxVerticalDot)
                bins[bestClass].Add(fi);
            else
                bins[FaceClass.Slope].Add(fi);
        }

        var patches = new List<PatchCluster>();
        var wallOpts = new WallEdgeSegmentationOptions
        {
            MinAreaM2 = System.Math.Min(minAreaM2, 0.06),
            MaxSpanM = wallMaxSpanM > 0 ? wallMaxSpanM : WallEdgeSegmenter.DefaultMaxSpanM(profile),
            DoorRegions = doorRegions,
        };
        var rectMaxSpan = wallMaxSpanM > 0 ? wallMaxSpanM : WallInPlaneMaxSpanM(profile);

        foreach (var (cls, faces) in bins)
        {
            if (faces.Count == 0)
                continue;

            switch (cls)
            {
                case FaceClass.WallPosX:
                    if (useWallEdgeSegmentation)
                        patches.AddRange(WallEdgeSegmenter.SegmentWallFaces(mesh, faces, ax, profile, wallOpts));
                    else
                        patches.AddRange(MergeRectangles(mesh, faces, ax, profile, minAreaM2, faceNormals, rectMaxSpan));
                    break;
                case FaceClass.WallNegX:
                    if (useWallEdgeSegmentation)
                        patches.AddRange(WallEdgeSegmenter.SegmentWallFaces(mesh, faces, ax.Scale(-1), profile, wallOpts));
                    else
                        patches.AddRange(MergeRectangles(mesh, faces, ax.Scale(-1), profile, minAreaM2, faceNormals, rectMaxSpan));
                    break;
                case FaceClass.WallPosY:
                    if (useWallEdgeSegmentation)
                        patches.AddRange(WallEdgeSegmenter.SegmentWallFaces(mesh, faces, ay, profile, wallOpts));
                    else
                        patches.AddRange(MergeRectangles(mesh, faces, ay, profile, minAreaM2, faceNormals, rectMaxSpan));
                    break;
                case FaceClass.WallNegY:
                    if (useWallEdgeSegmentation)
                        patches.AddRange(WallEdgeSegmenter.SegmentWallFaces(mesh, faces, ay.Scale(-1), profile, wallOpts));
                    else
                        patches.AddRange(MergeRectangles(mesh, faces, ay.Scale(-1), profile, minAreaM2, faceNormals, rectMaxSpan));
                    break;
                case FaceClass.Horizontal:
                    patches.AddRange(MergeHorizontal(mesh, faces, ax, ay, az, minAreaM2, faceNormals, profile));
                    break;
                case FaceClass.HorizontalBase:
                    patches.AddRange(MergeBaseLipFaces(mesh, faces, minAreaM2, faceNormals));
                    break;
                case FaceClass.Slope:
                    patches.AddRange(MergeSlopeFaces(mesh, faces, minAreaM2, faceNormals));
                    break;
            }
        }

        patches.AddRange(gablePatches);
        patches.AddRange(plinthPatches);
        patches.AddRange(soffitPatches);

        if (Environment.GetEnvironmentVariable("CDM_GEO_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[geo-debug] FaceDrivenDecomposer.Split raw output: {patches.Count} patches");
            foreach (var p in patches)
            {
                var verts = p.WorldVertices;
                if (verts.Count == 0)
                    continue;
                var zs = verts.Select(v => v.Z).ToList();
                if (zs.Max() - zs.Min() > 1.0)
                {
                    Console.Error.WriteLine(
                        $"[geo-debug]   SUSPICIOUS kind={p.SurfaceKind} faces={p.FaceIndices.Count} "
                        + $"zspan=({zs.Min():F2}..{zs.Max():F2}) faceIdx=[{string.Join(",", p.FaceIndices.Take(10))}]");
                }
            }
        }

        return patches;
    }

    private static IReadOnlyList<PatchCluster> MergeBaseLipFaces(
        MeshData mesh,
        List<int> faceIndices,
        double minAreaM2,
        Vec3[] faceNormals)
    {
        var allowed = faceIndices.ToHashSet();
        var visited = new HashSet<int>();
        var patches = new List<PatchCluster>();
        var cosThresh = System.Math.Cos(SlopeAngleDeg * System.Math.PI / 180.0);
        var neighbors = BuildFaceNeighbors(mesh);

        foreach (var seed in faceIndices)
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
                    if (fn.Dot(faceNormals[nb]) >= cosThresh)
                    {
                        visited.Add(nb);
                        queue.Enqueue(nb);
                    }
                }
            }

            var patch = BuildPatch(
                mesh, cluster, System.Math.Min(minAreaM2, 0.04), faceNormals,
                surfaceKind: PatchSurfaceKind.Plinth);
            if (patch != null)
                patches.Add(patch);
        }

        return patches;
    }

    public static IReadOnlyList<PatchCluster> MergeRectanglesForWall(
        MeshData mesh,
        List<int> faceIndices,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        double minAreaM2,
        Vec3[] faceNormals,
        double maxSpanM = 0)
        => MergeRectangles(mesh, faceIndices, wallNormal, profile, minAreaM2, faceNormals, maxSpanM);

    private static IReadOnlyList<PatchCluster> MergeRectangles(
        MeshData mesh,
        List<int> faceIndices,
        Vec3 wallNormal,
        BuildingMeshProfile profile,
        double minAreaM2,
        Vec3[] faceNormals,
        double maxSpanM = 0)
    {
        var (u, v, n) = BuildWallFrame(wallNormal, profile);
        var rects = faceIndices
            .Select(fi => BuildFaceRect(mesh, mesh.Faces[fi], u, v, n))
            .ToArray();
        var span = maxSpanM > 0 ? maxSpanM : WallInPlaneMaxSpanM(profile);
        return BuildPatchesFromRects(mesh, faceIndices, rects, u, v, n, minAreaM2, faceNormals, profile, isHorizontal: false, maxSpanM: span);
    }

    private static IReadOnlyList<PatchCluster> MergeHorizontal(
        MeshData mesh,
        List<int> faceIndices,
        Vec3 axisX,
        Vec3 axisY,
        Vec3 axisZ,
        double minAreaM2,
        Vec3[] faceNormals,
        BuildingMeshProfile profile)
    {
        var upFaces = new List<int>();
        var downFaces = new List<int>();
        foreach (var fi in faceIndices)
        {
            if (faceNormals[fi].Dot(axisZ) >= 0)
                upFaces.Add(fi);
            else
                downFaces.Add(fi);
        }

        var patches = new List<PatchCluster>();
        if (upFaces.Count > 0)
        {
            var rects = upFaces.Select(fi => BuildFaceRect(mesh, mesh.Faces[fi], axisX, axisY, axisZ)).ToArray();
            patches.AddRange(BuildPatchesFromRects(
                mesh, upFaces, rects, axisX, axisY, axisZ, minAreaM2, faceNormals, profile, isHorizontal: true));
        }

        if (downFaces.Count > 0)
        {
            var rects = downFaces.Select(fi => BuildFaceRect(mesh, mesh.Faces[fi], axisX, axisY, axisZ.Scale(-1))).ToArray();
            patches.AddRange(BuildPatchesFromRects(
                mesh, downFaces, rects, axisX, axisY, axisZ.Scale(-1), minAreaM2, faceNormals, profile, isHorizontal: true));
        }

        return patches;
    }

    private static IReadOnlyList<PatchCluster> MergeSlopeFaces(
        MeshData mesh,
        List<int> faceIndices,
        double minAreaM2,
        Vec3[] faceNormals)
    {
        var allowed = faceIndices.ToHashSet();
        var visited = new HashSet<int>();
        var patches = new List<PatchCluster>();
        var cosThresh = System.Math.Cos(SlopeAngleDeg * System.Math.PI / 180.0);
        var neighbors = BuildFaceNeighbors(mesh);

        foreach (var seed in faceIndices)
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
                    if (fn.Dot(faceNormals[nb]) >= cosThresh)
                    {
                        visited.Add(nb);
                        queue.Enqueue(nb);
                    }
                }
            }

            var patch = BuildPatch(mesh, cluster, minAreaM2, faceNormals, surfaceKind: PatchSurfaceKind.Slope);
            if (patch != null)
                patches.Add(patch);
        }

        return patches;
    }

    private static IReadOnlyList<PatchCluster> BuildPatchesFromRects(
        MeshData mesh,
        List<int> faceIndices,
        FaceRect[] rects,
        Vec3 u,
        Vec3 v,
        Vec3 dominantNormal,
        double minAreaM2,
        Vec3[] faceNormals,
        BuildingMeshProfile profile,
        bool isHorizontal,
        double maxSpanM = 0)
    {
        var span = maxSpanM > 0
            ? maxSpanM
            : isHorizontal ? HorizontalInPlaneMaxSpanM(profile) : WallInPlaneMaxSpanM(profile);
        var groups = MergeRectGroups(rects, RectsAdjacent, span);
        var patches = new List<PatchCluster>();

        foreach (var group in groups)
        {
            var faces = group.Select(i => faceIndices[i]).ToList();
            var patch = BuildPatch(mesh, faces, minAreaM2, faceNormals, dominantNormal,
                isHorizontal ? PatchSurfaceKind.Horizontal : PatchSurfaceKind.Wall);
            if (patch != null)
                patches.Add(patch);
        }

        return MergeOverlappingPatches(
            mesh, patches, u, v, dominantNormal, minAreaM2, faceNormals, dominantNormal, maxSpanM);
    }

    private static List<PatchCluster> MergeOverlappingPatches(
        MeshData mesh,
        List<PatchCluster> patches,
        Vec3 u,
        Vec3 v,
        Vec3 n,
        double minAreaM2,
        Vec3[] faceNormals,
        Vec3 dominantNormal,
        double maxSpanM)
    {
        if (patches.Count < 2)
            return patches;

        var rects = patches
            .Select(p => BuildBoundsRect(p.WorldVertices, u, v, n))
            .ToArray();
        var groups = MergeRectGroups(rects, RectsAdjacent, maxSpanM);
        if (groups.Count == patches.Count)
            return patches;

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
            var merged = BuildPatch(mesh, faces, minAreaM2, faceNormals, dominantNormal,
                patches[group[0]].SurfaceKind);
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
        double maxSpanM)
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

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
                parent[rb] = ra;
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (!shouldMerge(rects[i], rects[j], MergeGapM))
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

    private static PatchCluster? BuildPatch(
        MeshData mesh,
        List<int> faceIndices,
        double minAreaM2,
        Vec3[] faceNormals,
        Vec3? overrideNormal = null,
        PatchSurfaceKind surfaceKind = PatchSurfaceKind.Wall)
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

        if (vertSet.Count < 4 || totalArea < minAreaM2)
            return null;

        var avgN = overrideNormal
            ?? (wnSum.Length() > 1e-6 ? wnSum.Normalized() : new Vec3(0, 0, 1));

        return new PatchCluster(
            faceIndices,
            vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
            totalArea,
            avgN,
            SurfaceKind: surfaceKind);
    }

    private static FaceRect BuildFaceRect(MeshData mesh, int[] face, Vec3 u, Vec3 v, Vec3 n)
    {
        var uMin = double.PositiveInfinity;
        var uMax = double.NegativeInfinity;
        var vMin = double.PositiveInfinity;
        var vMax = double.NegativeInfinity;
        var nMin = double.PositiveInfinity;
        var nMax = double.NegativeInfinity;

        foreach (var vi in face)
        {
            var p = mesh.Vertices[vi];
            uMin = System.Math.Min(uMin, p.Dot(u));
            uMax = System.Math.Max(uMax, p.Dot(u));
            vMin = System.Math.Min(vMin, p.Dot(v));
            vMax = System.Math.Max(vMax, p.Dot(v));
            var pn = p.Dot(n);
            nMin = System.Math.Min(nMin, pn);
            nMax = System.Math.Max(nMax, pn);
        }

        return new FaceRect(uMin, uMax, vMin, vMax, nMin, nMax);
    }

    private static FaceRect BuildBoundsRect(IReadOnlyList<Vec3> verts, Vec3 u, Vec3 v, Vec3 n)
    {
        var uMin = double.PositiveInfinity;
        var uMax = double.NegativeInfinity;
        var vMin = double.PositiveInfinity;
        var vMax = double.NegativeInfinity;
        var nMin = double.PositiveInfinity;
        var nMax = double.NegativeInfinity;

        foreach (var p in verts)
        {
            uMin = System.Math.Min(uMin, p.Dot(u));
            uMax = System.Math.Max(uMax, p.Dot(u));
            vMin = System.Math.Min(vMin, p.Dot(v));
            vMax = System.Math.Max(vMax, p.Dot(v));
            var pn = p.Dot(n);
            nMin = System.Math.Min(nMin, pn);
            nMax = System.Math.Max(nMax, pn);
        }

        return new FaceRect(uMin, uMax, vMin, vMax, nMin, nMax);
    }

    /// <summary>
    /// First pass: merge face rects on the same wall plane when they share an edge,
    /// overlap in UV, or have a gap &lt;= MergeGapM along one axis while overlapping on the other.
    /// </summary>
    private static bool RectsAdjacent(FaceRect a, FaceRect b, double gap)
    {
        // BUGFIX (Region-Marking-Workflow Session 2): this check only ever looked
        // at the in-plane (U/V) extents and ignored NMin/NMax (the position along
        // the shared dominant-normal axis). For wall bins that's harmless (each
        // bin already only holds faces from one axis-aligned side, so N barely
        // varies — it's just skin thickness). But the SAME function is reused for
        // "Horizontal" faces, where a region-guided Floor seed (ground level,
        // N≈0m) and a region-guided flat Roof seed (N≈roof height, several
        // meters up) both land in the same up-facing bin and — because only U/V
        // overlap was checked — got silently merged into one absurd box spanning
        // the full building height. Require N (height) proximity too, so floor
        // and roof planes stay separate while genuinely coplanar tiles (which
        // already differ by ~0 in N) keep merging exactly as before.
        var nGap = RangeGap(a.NMin, a.NMax, b.NMin, b.NMax);
        if (nGap > gap)
            return false;

        // FaceClass bins already prevent WallPosX vs WallPosY merges.
        var uGap = RangeGap(a.UMin, a.UMax, b.UMin, b.UMax);
        var vGap = RangeGap(a.VMin, a.VMax, b.VMin, b.VMax);
        var uOverlap = RangesOverlap(a.UMin, a.UMax, b.UMin, b.UMax);
        var vOverlap = RangesOverlap(a.VMin, a.VMax, b.VMin, b.VMax);

        if (uOverlap && vGap <= gap)
            return true;
        if (vOverlap && uGap <= gap)
            return true;

        return uGap <= gap && vGap <= gap;
    }

    private static double WallInPlaneMaxSpanM(BuildingMeshProfile profile)
    {
        var longH = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);
        return System.Math.Clamp(longH / 2.0, 3.0, 6.5);
    }

    private static double HorizontalInPlaneMaxSpanM(BuildingMeshProfile profile)
        => System.Math.Max(profile.SizeM.X, profile.SizeM.Y) * 1.15;

    private static FaceRect UnionRect(FaceRect a, FaceRect b) =>
        new(
            System.Math.Min(a.UMin, b.UMin),
            System.Math.Max(a.UMax, b.UMax),
            System.Math.Min(a.VMin, b.VMin),
            System.Math.Max(a.VMax, b.VMax),
            System.Math.Min(a.NMin, b.NMin),
            System.Math.Max(a.NMax, b.NMax));

    /// <summary>
    /// Second pass: merge patch bounds when they overlap in the tangent plane on the same wall plane.
    /// </summary>
    private static bool RectsOverlapInTangentPlane(FaceRect a, FaceRect b, double gap)
    {
        if (!SameWallPlane(a, b, gap))
            return false;

        return RangesOverlap(a.UMin, a.UMax, b.UMin, b.UMax)
            && RangesOverlap(a.VMin, a.VMax, b.VMin, b.VMax);
    }

    private static bool SameWallPlane(FaceRect a, FaceRect b, double gap)
    {
        var aMid = (a.NMin + a.NMax) * 0.5;
        var bMid = (b.NMin + b.NMax) * 0.5;
        return System.Math.Abs(aMid - bMid) <= gap;
    }

    private static bool RangesOverlap(double aMin, double aMax, double bMin, double bMax)
        => aMin <= bMax && bMin <= aMax;

    private static double RangeGap(double aMin, double aMax, double bMin, double bMax)
    {
        if (aMax < bMin)
            return bMin - aMax;
        if (bMax < aMin)
            return aMin - bMax;
        return 0;
    }

    private static (Vec3 u, Vec3 v, Vec3 n) BuildWallFrame(Vec3 wallNormal, BuildingMeshProfile profile)
    {
        var n = wallNormal.Normalized();
        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();

        Vec3 u;
        if (System.Math.Abs(n.Dot(ax)) > 0.9)
            u = ay;
        else if (System.Math.Abs(n.Dot(ay)) > 0.9)
            u = ax;
        else
            u = ax;

        u = u.Sub(n.Scale(u.Dot(n)));
        if (u.Length() < 1e-6)
            u = az.Sub(n.Scale(az.Dot(n)));
        u = u.Normalized();
        var v = n.Cross(u).Normalized();
        return (u, v, n);
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

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }

    private static HashSet<int> RegionFaces(RegionGuidedFacePlan plan, GeoRegionKind kind)
        => plan.FacesByKind.TryGetValue(kind, out var set) ? set : new HashSet<int>();

    private static GeoRegionKind? KindForFace(int fi, RegionGuidedFacePlan plan)
    {
        foreach (var (kind, faces) in plan.FacesByKind)
        {
            if (faces.Contains(fi))
                return kind;
        }
        return null;
    }

    private static bool ClassifyGuidedFace(
        int fi,
        RegionGuidedFacePlan plan,
        MeshData mesh,
        Vec3[] faceNormals,
        BuildingMeshProfile profile,
        Dictionary<FaceClass, List<int>> bins)
    {
        var kind = KindForFace(fi, plan);
        if (kind == null)
            return false;

        switch (kind)
        {
            case GeoRegionKind.Gable:
            case GeoRegionKind.Plinth:
            case GeoRegionKind.Soffit:
                return true;
            case GeoRegionKind.WallOuter:
            case GeoRegionKind.WallInner:
                AddWallFace(fi, faceNormals[fi], profile, bins);
                return true;
            case GeoRegionKind.Floor:
            {
                // BUGFIX (Region-Marking-Workflow Session 2): unlike Roof below, this
                // used to force EVERY face labeled "Floor" into the Horizontal bin
                // unconditionally. Faces only get the Floor label here via the very
                // loose `AssignUnclaimedByNearestSeed` fallback (nearest-seed by XY
                // distance, not by actual orientation), so a real building's steep
                // wall/roof faces that had no dedicated seed of their own often ended
                // up mislabeled "Floor". Because those mislabeled faces still had
                // their genuine (steep) per-face normals and sat at many slightly
                // different heights along a slope, `MergeHorizontal`'s adjacency
                // merge chained them transitively into ONE patch spanning the
                // building's full height (e.g. mezzanine floor + roof slope fused
                // into a single absurd box). Only route a Floor-labeled face into the
                // Horizontal bin when it is actually near-flat; otherwise fall
                // through to the same true-geometry classification unguided faces get
                // (wall side / slope), which reflects the mesh's real shape.
                var absZ = System.Math.Abs(faceNormals[fi].Dot(profile.AxisZ.Normalized()));
                if (absZ > HorizontalDotThresh)
                {
                    bins[FaceClass.Horizontal].Add(fi);
                    return true;
                }
                return false;
            }
            case GeoRegionKind.Roof:
            {
                var absZ = System.Math.Abs(faceNormals[fi].Dot(profile.AxisZ.Normalized()));
                if (absZ > HorizontalDotThresh)
                    bins[FaceClass.Horizontal].Add(fi);
                else
                    bins[FaceClass.Slope].Add(fi);
                return true;
            }
            default:
                return false;
        }
    }

    private static void AddWallFace(
        int fi,
        Vec3 wn,
        BuildingMeshProfile profile,
        Dictionary<FaceClass, List<int>> bins)
    {
        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();
        var absZ = System.Math.Abs(wn.Dot(az));
        var wallAxes = new[]
        {
            (ax, FaceClass.WallPosX),
            (ax.Scale(-1), FaceClass.WallNegX),
            (ay, FaceClass.WallPosY),
            (ay.Scale(-1), FaceClass.WallNegY),
        };

        var bestClass = FaceClass.Slope;
        var bestDot = -2.0;
        foreach (var (axis, cls) in wallAxes)
        {
            var d = wn.Dot(axis);
            if (d > bestDot)
            {
                bestDot = d;
                bestClass = cls;
            }
        }

        if (bestDot > WallDotThresh && absZ < PatchSurfaceClassifier.WallMaxVerticalDot)
            bins[bestClass].Add(fi);
        else
            bins[FaceClass.Slope].Add(fi);
    }

    private static IReadOnlyList<PatchCluster> BuildRegionSlopePatches(
        MeshData mesh,
        HashSet<int> gableFaces,
        double minAreaM2,
        Vec3[] faceNormals)
        => gableFaces.Count == 0
            ? Array.Empty<PatchCluster>()
            : MergeSlopeFaces(mesh, gableFaces.ToList(), minAreaM2, faceNormals);

    private readonly record struct FaceRect(
        double UMin,
        double UMax,
        double VMin,
        double VMax,
        double NMin,
        double NMax);
}
