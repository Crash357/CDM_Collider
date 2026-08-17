using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>Merge coplanar/antiparallel wall patches before OBB fitting (Python _merge_antiparallel_clusters).</summary>
public static class PatchMerger
{
    private const double CoplanarNormalDot = 0.996; // ~5°
    private const double DefaultGapM = 0.12;

    /// <summary>Blind pipeline: antiparallel → coplanar seam bridge → antiparallel.</summary>
    public static IReadOnlyList<PatchCluster> MergeForBlind(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile? profile = null,
        IReadOnlyList<DoorRegion>? doorRegions = null)
    {
        var merged = MergeAntiparallel(patches, profile);
        merged = MergeCoplanar(merged, profile, gapM: 0.35, seamBridgeOnly: false, doorRegions: doorRegions);
        return MergeAntiparallel(merged, profile);
    }

    /// <summary>FaceDriven: rectangles already merged — only bridge seams and opposite faces.</summary>
    public static IReadOnlyList<PatchCluster> MergeForFaceDriven(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile? profile = null)
    {
        var merged = MergeAntiparallel(patches, profile);
        return MergeCoplanar(merged, profile, gapM: 0.18, seamBridgeOnly: true);
    }

    /// <summary>Re-connect patches split by subdivision (same plane, shared edge or small gap).</summary>
    public static IReadOnlyList<PatchCluster> MergeCoplanar(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile? profile = null,
        double gapM = DefaultGapM,
        bool seamBridgeOnly = false,
        double? maxMergedSpanM = null,
        IReadOnlyList<DoorRegion>? doorRegions = null,
        double doorGapMarginM = 0.25)
    {
        if (patches.Count < 2)
            return patches;

        var spanLimit = maxMergedSpanM ?? MaxMergeSpanM(profile, seamBridgeOnly);
        var n = patches.Count;
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

        var frames = new (Vec3 n, Vec3 u, Vec3 v, double uMin, double uMax, double vMin, double vMax)[n];
        for (var i = 0; i < n; i++)
            frames[i] = TangentBounds(patches[i].WorldVertices, patches[i].DominantNormal, profile);

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var pi = patches[i];
                var pj = patches[j];
                if (!PatchSurfaceClassifier.CanMerge(pi.SurfaceKind, pj.SurfaceKind))
                    continue;

                var (ni, ui, vi, uMinI, uMaxI, vMinI, vMaxI) = frames[i];
                var (nj, uj, vj, uMinJ, uMaxJ, vMinJ, vMaxJ) = frames[j];

                var sharedN = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(
                    ni.Add(nj).Length() > 1e-6 ? ni.Add(nj).Normalized() : ni, profile);
                if (ni.Dot(nj) < CoplanarNormalDot)
                    continue;
                if (System.Math.Abs(ui.Dot(uj)) < 0.85)
                    continue;

                var effectiveGapM = gapM;
                if (doorRegions is { Count: > 0 })
                {
                    var doorSpan = DoorBridgeGapM(
                        sharedN, ui, vi, uMinI, uMaxI, vMinI, vMaxI, uMinJ, uMaxJ, vMinJ, vMaxJ,
                        doorRegions, doorGapMarginM);
                    if (doorSpan > effectiveGapM)
                        effectiveGapM = doorSpan;
                }

                if (seamBridgeOnly)
                {
                    if (!ShouldBridge(uMinI, uMaxI, vMinI, vMaxI, uMinJ, uMaxJ, vMinJ, vMaxJ, effectiveGapM, true))
                        continue;
                }
                else if (!IntervalAdjacent(uMinI, uMaxI, vMinI, vMaxI, uMinJ, uMaxJ, vMinJ, vMaxJ, effectiveGapM))
                {
                    continue;
                }

                var mergedU = System.Math.Max(uMaxI, uMaxJ) - System.Math.Min(uMinI, uMinJ);
                var mergedV = System.Math.Max(vMaxI, vMaxJ) - System.Math.Min(vMinI, vMinJ);
                if (System.Math.Max(mergedU, mergedV) > spanLimit)
                    continue;

                if (profile != null && !MergedHeightSpanAllowed(pi, pj, profile, spanLimit))
                    continue;

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

        var result = new List<PatchCluster>();
        foreach (var indices in groups.Values)
        {
            if (indices.Count == 1)
            {
                result.Add(patches[indices[0]]);
                continue;
            }

            result.Add(CombinePatches(indices.Select(i => patches[i]).ToList(), profile));
        }

        return result;
    }

    /// <summary>Merge same-kind patches with nearly identical centers (duplicate foundation strips).</summary>
    public static IReadOnlyList<PatchCluster> MergeNearDuplicatePatches(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile? profile = null,
        double maxCenterGapM = 0.45,
        double minNormalDot = 0.85)
    {
        if (patches.Count < 2)
            return patches;

        var merged = new bool[patches.Count];
        var result = new List<PatchCluster>();

        for (var i = 0; i < patches.Count; i++)
        {
            if (merged[i])
                continue;

            var pi = patches[i];
            if (PatchSurfaceClassifier.IsMergeProtected(pi.SurfaceKind))
            {
                result.Add(pi);
                merged[i] = true;
                continue;
            }

            if (pi.GableEnd != GableEndKind.None && pi.AreaM2 >= 1.0)
            {
                result.Add(pi);
                merged[i] = true;
                continue;
            }

            var bestJ = -1;
            var bestDist = maxCenterGapM;
            var ci = PatchCenter(pi);

            for (var j = i + 1; j < patches.Count; j++)
            {
                if (merged[j])
                    continue;

                var pj = patches[j];
                if (pj.GableEnd != GableEndKind.None && pj.AreaM2 >= 1.0)
                    continue;
                if (PatchSurfaceClassifier.IsMergeProtected(pj.SurfaceKind))
                    continue;
                if (!PatchSurfaceClassifier.CanMerge(pi.SurfaceKind, pj.SurfaceKind))
                    continue;
                if (pi.DominantNormal.Dot(pj.DominantNormal) < minNormalDot)
                    continue;

                var dist = Vec3.Distance(ci, PatchCenter(pj));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestJ = j;
                }
            }

            if (bestJ >= 0)
            {
                result.Add(CombinePatches(new[] { pi, patches[bestJ] }, profile));
                merged[i] = true;
                merged[bestJ] = true;
            }
        }

        for (var i = 0; i < patches.Count; i++)
        {
            if (!merged[i])
                result.Add(patches[i]);
        }

        return result;
    }

    /// <summary>Absorb small wall/slope fragments at +X/+Y gable ends into the main gable panel.</summary>
    public static IReadOnlyList<PatchCluster> MergeGableShadowPatches(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        double maxShadowAreaM2 = 1.2,
        double maxCenterGapM = 0.95)
    {
        if (patches.Count < 2)
            return patches;

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var gableRoots = patches
            .Select((p, i) => (p, i))
            .Where(x => x.p.GableEnd != GableEndKind.None && x.p.AreaM2 >= 1.0)
            .ToList();
        if (gableRoots.Count == 0)
            return patches;

        var merged = new bool[patches.Count];
        var result = new List<PatchCluster>();

        foreach (var (gable, gi) in gableRoots)
        {
            if (merged[gi])
                continue;

            var group = new List<PatchCluster> { gable };
            merged[gi] = true;
            var gc = PatchCenter(gable);
            var endAxis = gable.GableEnd == GableEndKind.PosX ? ax : ay;

            for (var j = 0; j < patches.Count; j++)
            {
                if (merged[j] || j == gi)
                    continue;

                var p = patches[j];
                if (PatchSurfaceClassifier.IsMergeProtected(p.SurfaceKind))
                    continue;
                if (p.GableEnd != GableEndKind.None && p.GableEnd != gable.GableEnd)
                    continue;
                if (p.GableEnd != GableEndKind.None && p.AreaM2 >= 1.0)
                    continue;
                if (p.AreaM2 > maxShadowAreaM2 && p.GableEnd == GableEndKind.None)
                    continue;

                var sc = PatchCenter(p);
                if (sc.Dot(endAxis) < gc.Dot(endAxis) - 0.4)
                    continue;
                if (Vec3.Distance(gc, sc) > maxCenterGapM)
                    continue;

                group.Add(p);
                merged[j] = true;
            }

            result.Add(group.Count == 1 ? gable : CombinePatches(group, profile));
        }

        for (var i = 0; i < patches.Count; i++)
        {
            if (!merged[i])
                result.Add(patches[i]);
        }

        return result;
    }

    /// <summary>Merge tiny orphan patches into the nearest larger neighbor.</summary>
    public static IReadOnlyList<PatchCluster> MergeTinyFragments(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        double maxAreaM2 = 0.22,
        double maxCenterGapM = 0.72)
    {
        if (patches.Count < 2)
            return patches;

        static bool IsProtected(PatchCluster p) =>
            PatchSurfaceClassifier.IsMergeProtected(p.SurfaceKind)
            || (p.GableEnd != GableEndKind.None && p.AreaM2 >= 1.0);

        var skip = new HashSet<int>();
        var absorbed = new Dictionary<int, PatchCluster>();

        for (var i = 0; i < patches.Count; i++)
        {
            var pi = patches[i];
            if (IsProtected(pi) || pi.AreaM2 >= maxAreaM2)
                continue;

            var ci = PatchCenter(pi);
            var bestJ = -1;
            var bestDist = maxCenterGapM;
            for (var j = 0; j < patches.Count; j++)
            {
                if (i == j || skip.Contains(j))
                    continue;

                var pj = patches[j];
                if (IsProtected(pj))
                    continue;

                var dist = Vec3.Distance(ci, PatchCenter(pj));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestJ = j;
                }
            }

            if (bestJ < 0)
                continue;

            skip.Add(i);
            absorbed[bestJ] = absorbed.TryGetValue(bestJ, out var existing)
                ? CombinePatches(new[] { existing, pi }, profile)
                : CombinePatches(new[] { patches[bestJ], pi }, profile);
        }

        var result = new List<PatchCluster>();
        for (var i = 0; i < patches.Count; i++)
        {
            if (skip.Contains(i))
                continue;
            result.Add(absorbed.TryGetValue(i, out var combined) ? combined : patches[i]);
        }

        return result;
    }

    /// <summary>Merge roof/box duplicates with nearly identical XY footprint (e.g. Component14 + Component15).</summary>
    public static IReadOnlyList<PatchCluster> MergeOverlappingFootprintDuplicates(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        double minOverlapRatio = 0.82,
        double maxPlanCenterGapM = 0.40,
        double minAreaRatio = 0.72)
    {
        if (patches.Count < 2)
            return patches;

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();
        var maxZ = patches.SelectMany(p => p.WorldVertices).Max(v => v.Dot(az));
        var ridgeZMin = maxZ - System.Math.Clamp(profile.SizeM.Z * 0.18, 0.28, 0.55);

        static bool IsRoofLike(PatchCluster p, double ridgeMin, Vec3 az) =>
            (p.SurfaceKind is PatchSurfaceKind.Horizontal or PatchSurfaceKind.Slope)
            && !PatchSurfaceClassifier.IsMergeProtected(p.SurfaceKind)
            && (p.GableEnd == GableEndKind.None || p.AreaM2 < 1.0)
            && (p.SurfaceKind == PatchSurfaceKind.Horizontal
                || p.WorldVertices.Max(v => v.Dot(az)) >= ridgeMin);

        var candidates = patches
            .Select((p, i) => (p, i))
            .Where(x => IsRoofLike(x.p, ridgeZMin, az))
            .ToList();
        if (candidates.Count < 2)
            return patches;

        var skip = new HashSet<int>();
        var absorbed = new Dictionary<int, PatchCluster>();
        var footprints = candidates.ToDictionary(
            x => x.i,
            x => FootprintBounds(x.p, ax, ay));

        foreach (var (pi, i) in candidates.OrderByDescending(x => x.p.AreaM2))
        {
            if (skip.Contains(i) || absorbed.ContainsKey(i))
                continue;

            var (uMinI, uMaxI, vMinI, vMaxI) = footprints[i];
            var ci = PlanCenter(pi, ax, ay);

            foreach (var (pj, j) in candidates)
            {
                if (i == j || skip.Contains(j))
                    continue;

                var (uMinJ, uMaxJ, vMinJ, vMaxJ) = footprints[j];
                if (FootprintOverlapRatio(uMinI, uMaxI, vMinI, vMaxI, uMinJ, uMaxJ, vMinJ, vMaxJ) < minOverlapRatio)
                    continue;

                if (PlanCenterDistance(ci, PlanCenter(pj, ax, ay)) > maxPlanCenterGapM)
                    continue;

                var areaRatio = System.Math.Min(pi.AreaM2, pj.AreaM2) / System.Math.Max(pi.AreaM2, pj.AreaM2);
                if (areaRatio < minAreaRatio)
                    continue;

                var keep = pi.AreaM2 >= pj.AreaM2 ? i : j;
                var drop = keep == i ? j : i;
                if (skip.Contains(drop))
                    continue;

                skip.Add(drop);
                var larger = patches[keep];
                var smaller = patches[drop];
                absorbed[keep] = absorbed.TryGetValue(keep, out var existing)
                    ? CombinePatches(new[] { existing, smaller }, profile)
                    : CombinePatches(new[] { larger, smaller }, profile);
            }
        }

        var result = new List<PatchCluster>();
        for (var k = 0; k < patches.Count; k++)
        {
            if (skip.Contains(k))
                continue;
            result.Add(absorbed.TryGetValue(k, out var combined) ? combined : patches[k]);
        }

        return result;
    }

    /// <summary>Merge duplicate roof plates at the ridge elevation (horizontal + slope overlap).</summary>
    public static IReadOnlyList<PatchCluster> MergeRoofPlateDuplicates(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        double maxCenterGapM = 1.15)
    {
        if (patches.Count < 2)
            return patches;

        var az = profile.AxisZ.Normalized();
        var maxZ = patches.SelectMany(p => p.WorldVertices).DefaultIfEmpty(new Vec3(0, 0, 0))
            .Max(v => v.Dot(az));
        var roofMinZ = maxZ - System.Math.Clamp(profile.SizeM.Z * 0.14, 0.28, 0.5);

        var roofKinds = new HashSet<PatchSurfaceKind> { PatchSurfaceKind.Horizontal, PatchSurfaceKind.Slope };
        var roofIndices = patches
            .Select((p, i) => (p, i))
            .Where(x => roofKinds.Contains(x.p.SurfaceKind)
                        && x.p.GableEnd == GableEndKind.None
                        && !PatchSurfaceClassifier.IsMergeProtected(x.p.SurfaceKind)
                        && PatchCenter(x.p).Dot(az) >= roofMinZ)
            .Select(x => x.i)
            .ToList();
        if (roofIndices.Count < 2)
            return patches;

        var merged = new bool[patches.Count];
        var result = new List<PatchCluster>();

        foreach (var i in roofIndices)
        {
            if (merged[i])
                continue;

            var pi = patches[i];
            var group = new List<PatchCluster> { pi };
            merged[i] = true;
            var ci = PatchCenter(pi);

            for (var j = i + 1; j < patches.Count; j++)
            {
                if (merged[j] || !roofIndices.Contains(j))
                    continue;

                var pj = patches[j];
                if (Vec3.Distance(ci, PatchCenter(pj)) > maxCenterGapM)
                    continue;

                group.Add(pj);
                merged[j] = true;
            }

            result.Add(group.Count == 1 ? pi : CombinePatches(group, profile));
        }

        for (var k = 0; k < patches.Count; k++)
        {
            if (!merged[k])
                result.Add(patches[k]);
        }

        return result;
    }

    /// <summary>
    /// Roof (Horizontal/Slope) and foundation (Plinth) fragments are frequently split into many
    /// small, mutually OVERLAPPING footprint pieces by the underlying mesh triangulation grid —
    /// not just separated by clean seams. <see cref="MergeCoplanar"/> in seam-bridge-only mode
    /// (used for walls, where it correctly avoids fusing distinct wall planes) rejects any pair
    /// that already overlaps in-plane, so these roof/plinth splinters never reconnect and survive
    /// as dozens of near-duplicate slivers all the way to the final component count. This pass
    /// performs a dedicated, overlap-aware coplanar fusion restricted to roof and foundation
    /// surface kinds only; walls, end caps and soffits pass through completely untouched.
    /// </summary>
    public static IReadOnlyList<PatchCluster> MergeOverlappingRoofAndPlinthPatches(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        double gapM = 0.4,
        double? maxMergedSpanM = null)
    {
        if (patches.Count < 2)
            return patches;

        static bool IsTarget(PatchCluster p) =>
            (p.SurfaceKind is PatchSurfaceKind.Horizontal or PatchSurfaceKind.Slope or PatchSurfaceKind.Plinth)
            && !(p.GableEnd != GableEndKind.None && p.AreaM2 >= 1.0);

        var targetIdx = new List<int>();
        var passthrough = new List<PatchCluster>();
        for (var i = 0; i < patches.Count; i++)
        {
            if (IsTarget(patches[i]))
                targetIdx.Add(i);
            else
                passthrough.Add(patches[i]);
        }

        if (targetIdx.Count < 2)
            return patches;

        var spanLimit = maxMergedSpanM ?? MaxMergeSpanM(profile, seamBridgeOnly: false);
        // Two patches sharing a normal direction (e.g. both facing +Z) can still belong to
        // entirely different planes offset along that normal (attic floor vs. raised loft
        // floor, inner vs. outer plinth face). Only fuse them when their offset along the
        // shared normal is within roughly wall-thickness tolerance, so distinct height levels
        // never collapse into one oversized, geometrically wrong box.
        var maxOffsetGapM = System.Math.Max((profile.WallThicknessM > 0 ? profile.WallThicknessM : 0.15) * 3.0, 0.4);
        var n = targetIdx.Count;
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

        var frames = new (Vec3 n, Vec3 u, Vec3 v, double uMin, double uMax, double vMin, double vMax)[n];
        var offsets = new double[n];
        for (var i = 0; i < n; i++)
        {
            var p = patches[targetIdx[i]];
            frames[i] = TangentBounds(p.WorldVertices, p.DominantNormal, profile);
            offsets[i] = p.WorldVertices.Average(v => v.Dot(frames[i].n));
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var pi = patches[targetIdx[i]];
                var pj = patches[targetIdx[j]];
                if (pi.SurfaceKind != pj.SurfaceKind)
                    continue;

                var (ni, ui, _, uMinI, uMaxI, vMinI, vMaxI) = frames[i];
                var (nj, uj, _, uMinJ, uMaxJ, vMinJ, vMaxJ) = frames[j];
                if (ni.Dot(nj) < CoplanarNormalDot)
                    continue;
                if (System.Math.Abs(ui.Dot(uj)) < 0.85)
                    continue;
                if (System.Math.Abs(offsets[i] - offsets[j]) > maxOffsetGapM)
                    continue;

                if (!ShouldBridge(uMinI, uMaxI, vMinI, vMaxI, uMinJ, uMaxJ, vMinJ, vMaxJ, gapM, seamBridgeOnly: false))
                    continue;

                var mergedU = System.Math.Max(uMaxI, uMaxJ) - System.Math.Min(uMinI, uMinJ);
                var mergedV = System.Math.Max(vMaxI, vMaxJ) - System.Math.Min(vMinI, vMinJ);
                if (System.Math.Max(mergedU, mergedV) > spanLimit)
                    continue;

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
            list.Add(targetIdx[i]);
        }

        var result = new List<PatchCluster>(passthrough);
        foreach (var indices in groups.Values)
        {
            result.Add(indices.Count == 1
                ? patches[indices[0]]
                : CombinePatches(indices.Select(i => patches[i]).ToList(), profile));
        }

        return result;
    }

    /// <summary>Merge patches occupying the same anchor (duplicate plinth/wall boxes).</summary>
    public static IReadOnlyList<PatchCluster> MergeColocatedPatches(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile? profile = null,
        double maxCenterGapM = 0.15)
    {
        if (patches.Count < 2)
            return patches;

        var n = patches.Count;
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

        var centers = patches.Select(PatchCenter).ToArray();
        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            if (Vec3.Distance(centers[i], centers[j]) > maxCenterGapM)
                continue;

            var pi = patches[i];
            var pj = patches[j];
            if (PatchSurfaceClassifier.IsMergeProtected(pi.SurfaceKind)
                || PatchSurfaceClassifier.IsMergeProtected(pj.SurfaceKind))
            {
                if (pi.SurfaceKind != pj.SurfaceKind)
                    continue;
                Union(i, j);
                continue;
            }

            if (!PatchSurfaceClassifier.CanMerge(pi.SurfaceKind, pj.SurfaceKind))
                continue;

            if (pi.DominantNormal.Dot(pj.DominantNormal) < 0.35)
                continue;

            Union(i, j);
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

        var result = new List<PatchCluster>();
        foreach (var indices in groups.Values)
        {
            if (indices.Count == 1)
            {
                result.Add(patches[indices[0]]);
                continue;
            }

            result.Add(CombinePatches(indices.Select(i => patches[i]).ToList(), profile));
        }

        return result;
    }

    public static IReadOnlyList<PatchCluster> MergeAntiparallel(
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile? profile = null,
        double normalDotThresh = -0.8,
        double gapM = DefaultGapM)
    {
        if (patches.Count < 2)
            return patches;

        var wallThick = profile?.WallThicknessM ?? 0.15;
        var merged = new bool[patches.Count];
        var result = new List<PatchCluster>();

        for (var i = 0; i < patches.Count; i++)
        {
            if (merged[i])
                continue;

            var pi = patches[i];

            var bestJ = -1;
            var bestScore = double.MaxValue;

            for (var j = i + 1; j < patches.Count; j++)
            {
                if (merged[j])
                    continue;

                var pj = patches[j];
                if (!PatchSurfaceClassifier.CanMergeAntiparallel(pi.SurfaceKind, pj.SurfaceKind))
                    continue;
                if (pi.DominantNormal.Dot(pj.DominantNormal) > normalDotThresh)
                    continue;

                var wallAxis = CanonicalWallAxis(pi.DominantNormal, pj.DominantNormal, profile);
                if (wallAxis == null)
                    continue;

                var (_, _, _, uMinJ, uMaxJ, vMinJ, vMaxJ) = TangentBounds(
                    pj.WorldVertices, wallAxis.Value, profile);
                var (_, _, _, uMinI2, uMaxI2, vMinI2, vMaxI2) = TangentBounds(
                    pi.WorldVertices, wallAxis.Value, profile);

                var uOverlap = RangesOverlap(uMinI2, uMaxI2, uMinJ, uMaxJ);
                var vOverlap = RangesOverlap(vMinI2, vMaxI2, vMinJ, vMaxJ);
                if (!uOverlap || !vOverlap)
                    continue;

                var nGap = System.Math.Abs(
                    pi.WorldVertices.Average(p => p.Dot(wallAxis.Value)) -
                    pj.WorldVertices.Average(p => p.Dot(wallAxis.Value)));
                if (nGap > wallThick * 3.5)
                    continue;

                if (nGap < bestScore)
                {
                    bestScore = nGap;
                    bestJ = j;
                }
            }

            if (bestJ < 0)
                continue;

            var pair = patches[bestJ];
            result.Add(CombinePatches(new[] { pi, pair }, profile, antiparallel: true));
            merged[i] = true;
            merged[bestJ] = true;
        }

        for (var i = 0; i < patches.Count; i++)
        {
            if (!merged[i])
                result.Add(patches[i]);
        }

        return result;
    }

    /// <summary>
    /// DayZ door openings must not fragment a wall into permanently separate collision
    /// components — the doorway is handled by a separate proxy/hitpoint, not by leaving a
    /// gap in the static Geometry LOD. If a known DoorRegion's in-plane footprint spans the
    /// gap between two coplanar patches (within margin), allow bridging across that gap even
    /// though it exceeds the normal seam tolerance, so the two wall segments recombine into
    /// one continuous, closed box instead of staying fragmented.
    /// </summary>
    private static double DoorBridgeGapM(
        Vec3 n, Vec3 u, Vec3 v,
        double uMinA, double uMaxA, double vMinA, double vMaxA,
        double uMinB, double uMaxB, double vMinB, double vMaxB,
        IReadOnlyList<DoorRegion> doorRegions,
        double marginM)
    {
        var best = 0.0;
        foreach (var door in doorRegions)
        {
            var corners = new[]
            {
                door.Min, door.Max,
                new Vec3(door.Min.X, door.Max.Y, door.Min.Z),
                new Vec3(door.Max.X, door.Min.Y, door.Max.Z),
                new Vec3(door.Min.X, door.Min.Y, door.Max.Z),
                new Vec3(door.Max.X, door.Max.Y, door.Min.Z),
            };
            var uProj = corners.Select(c => c.Dot(u)).ToList();
            var vProj = corners.Select(c => c.Dot(v)).ToList();
            var doorUMin = uProj.Min();
            var doorUMax = uProj.Max();
            var doorVMin = vProj.Min();
            var doorVMax = vProj.Max();

            var uGap = RangeGap(uMinA, uMaxA, uMinB, uMaxB);
            var vGap = RangeGap(vMinA, vMaxA, vMinB, vMaxB);

            // Door bridges the gap on the U axis: its span must cover the empty interval
            // between the two patches, and the patches must still overlap on V.
            if (uGap > 0 && RangesOverlap(vMinA, vMaxA, vMinB, vMaxB))
            {
                var gapLo = System.Math.Min(uMaxA, uMaxB);
                var gapHi = System.Math.Max(uMinA, uMinB);
                if (doorUMin <= gapLo + marginM && doorUMax >= gapHi - marginM)
                    best = System.Math.Max(best, uGap + marginM);
            }

            if (vGap > 0 && RangesOverlap(uMinA, uMaxA, uMinB, uMaxB))
            {
                var gapLo = System.Math.Min(vMaxA, vMaxB);
                var gapHi = System.Math.Max(vMinA, vMinB);
                if (doorVMin <= gapLo + marginM && doorVMax >= gapHi - marginM)
                    best = System.Math.Max(best, vGap + marginM);
            }
        }

        return best;
    }

    /// <summary>Edge or gap adjacency on u or v with overlap on the other axis.</summary>
    public static bool IntervalAdjacent(
        double uMinA, double uMaxA, double vMinA, double vMaxA,
        double uMinB, double uMaxB, double vMinB, double vMaxB,
        double gapM)
    {
        var uGap = RangeGap(uMinA, uMaxA, uMinB, uMaxB);
        var vGap = RangeGap(vMinA, vMaxA, vMinB, vMaxB);
        return (uGap <= gapM && RangesOverlap(vMinA, vMaxA, vMinB, vMaxB))
            || (vGap <= gapM && RangesOverlap(uMinA, uMaxA, uMinB, uMaxB));
    }

    /// <summary>Bridge mesh seams only — not merge entire wall planes.</summary>
    public static bool ShouldBridge(
        double uMinA, double uMaxA, double vMinA, double vMaxA,
        double uMinB, double uMaxB, double vMinB, double vMaxB,
        double gapM,
        bool seamBridgeOnly = false)
    {
        var uGap = RangeGap(uMinA, uMaxA, uMinB, uMaxB);
        var vGap = RangeGap(vMinA, vMaxA, vMinB, vMaxB);
        var uOv = RangesOverlap(uMinA, uMaxA, uMinB, uMaxB);
        var vOv = RangesOverlap(vMinA, vMaxA, vMinB, vMaxB);

        if ((uGap > 0 && uGap <= gapM && vOv) || (vGap > 0 && vGap <= gapM && uOv))
            return true;

        if (seamBridgeOnly)
            return false;

        if (uOv && vOv)
        {
            var overlapU = System.Math.Min(uMaxA, uMaxB) - System.Math.Max(uMinA, uMinB);
            var overlapV = System.Math.Min(vMaxA, vMaxB) - System.Math.Max(vMinA, vMinB);
            return overlapU > gapM * 0.25 && overlapV > gapM * 0.25;
        }

        return false;
    }

    private static PatchCluster CombinePatches(
        IReadOnlyList<PatchCluster> group,
        BuildingMeshProfile? profile,
        bool antiparallel = false)
    {
        var faces = new List<int>();
        var area = 0.0;
        var nSum = new Vec3(0, 0, 0);

        foreach (var p in group)
        {
            faces.AddRange(p.FaceIndices);
            area += p.AreaM2;
            nSum = nSum.Add(p.DominantNormal.Scale(p.AreaM2));
        }

        var verts = group
            .SelectMany(p => p.WorldVertices)
            .GroupBy(v => (System.Math.Round(v.X, 4), System.Math.Round(v.Y, 4), System.Math.Round(v.Z, 4)))
            .Select(g => g.First())
            .ToList();

        Vec3 avgN;
        if (antiparallel && group.Count == 2)
        {
            var axis = CanonicalWallAxis(group[0].DominantNormal, group[1].DominantNormal, profile);
            avgN = axis ?? group[0].DominantNormal;
        }
        else
        {
            avgN = nSum.Length() > 1e-6
                ? nSum.Normalized()
                : group[0].DominantNormal;
        }

        var surfaceKind = group[0].SurfaceKind;
        if (group.Any(p => p.SurfaceKind == PatchSurfaceKind.Plinth))
            surfaceKind = PatchSurfaceKind.Plinth;
        else if (group.Any(p => p.SurfaceKind == PatchSurfaceKind.EndCap))
            surfaceKind = PatchSurfaceKind.EndCap;
        else if (group.Any(p => p.SurfaceKind == PatchSurfaceKind.Soffit))
            surfaceKind = PatchSurfaceKind.Soffit;
        else if (group.Any(p => p.SurfaceKind != surfaceKind))
        {
            surfaceKind = group
                .GroupBy(p => p.SurfaceKind)
                .OrderByDescending(g => g.Sum(p => p.AreaM2))
                .First()
                .Key;
        }

        if (surfaceKind != PatchSurfaceKind.Slope)
            avgN = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(avgN, profile);

        var gableEnd = group.Any(p => p.GableEnd == GableEndKind.PosY) ? GableEndKind.PosY
            : group.Any(p => p.GableEnd == GableEndKind.PosX) ? GableEndKind.PosX
            : GableEndKind.None;
        return new PatchCluster(faces, verts, area, avgN, SurfaceKind: surfaceKind, GableEnd: gableEnd);
    }

    private static bool MergedHeightSpanAllowed(
        PatchCluster a,
        PatchCluster b,
        BuildingMeshProfile profile,
        double spanLimitM)
    {
        if (a.SurfaceKind != b.SurfaceKind)
            return false;
        if (a.SurfaceKind != PatchSurfaceKind.Wall && a.SurfaceKind != PatchSurfaceKind.Slope)
            return true;

        var wallSpan = spanLimitM > 0 ? spanLimitM : 1.5;
        var verts = a.WorldVertices.Concat(b.WorldVertices).ToList();
        var heightSpan = PatchHeightSplitter.HeightSpanM(verts, profile);
        var maxHeight = a.SurfaceKind == PatchSurfaceKind.Wall
            ? PatchHeightSplitter.MaxWallBandHeightM(profile, wallSpan) * 1.08
            : PatchHeightSplitter.MaxWallBandHeightM(profile, wallSpan) * 1.18;
        return heightSpan <= maxHeight;
    }

    private static Vec3? CanonicalWallAxis(Vec3 nA, Vec3 nB, BuildingMeshProfile? profile)
    {
        if (profile == null)
            return null;

        var ax = profile.AxisX.Normalized();
        var ay = profile.AxisY.Normalized();
        var axScore = System.Math.Max(System.Math.Abs(nA.Dot(ax)), System.Math.Abs(nB.Dot(ax)));
        var ayScore = System.Math.Max(System.Math.Abs(nA.Dot(ay)), System.Math.Abs(nB.Dot(ay)));

        if (axScore > 0.70 && axScore >= ayScore)
            return ax;
        if (ayScore > 0.70)
            return ay;
        return null;
    }

    private static double MaxMergeSpanM(BuildingMeshProfile? profile, bool seamBridgeOnly)
    {
        if (profile == null)
            return seamBridgeOnly ? 4.0 : 50.0;

        var sx = profile.SizeM.X;
        var sy = profile.SizeM.Y;
        var sz = profile.SizeM.Z;
        if (seamBridgeOnly)
        {
            var horiz = System.Math.Max(sx, sy);
            return horiz * 0.65 + DefaultGapM;
        }

        return System.Math.Sqrt(sx * sx + sy * sy + sz * sz) + DefaultGapM;
    }

    private static bool RangesOverlap(double aMin, double aMax, double bMin, double bMax) =>
        aMax >= bMin && bMax >= aMin;

    private static (Vec3 n, Vec3 u, Vec3 v, double uMin, double uMax, double vMin, double vMax) TangentBounds(
        IReadOnlyList<Vec3> verts,
        Vec3 normal,
        BuildingMeshProfile? profile)
    {
        var n = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(normal, profile);
        var u = BuildInPlaneU(n, profile);
        var v = n.Cross(u).Normalized();

        var uProj = verts.Select(p => p.Dot(u)).ToList();
        var vProj = verts.Select(p => p.Dot(v)).ToList();
        return (n, u, v, uProj.Min(), uProj.Max(), vProj.Min(), vProj.Max());
    }

    private static Vec3 BuildInPlaneU(Vec3 n, BuildingMeshProfile? profile)
    {
        if (profile != null)
        {
            var ax = profile.AxisX.Normalized();
            var ay = profile.AxisY.Normalized();
            var az = profile.AxisZ.Normalized();

            if (System.Math.Abs(n.Dot(az)) > 0.85)
                return ax.Length() > 1e-6 ? ax : new Vec3(1, 0, 0);

            if (System.Math.Abs(n.Dot(ax)) > 0.70)
            {
                var u = ay.Sub(n.Scale(ay.Dot(n)));
                if (u.Length() > 1e-6)
                    return u.Normalized();
            }

            if (System.Math.Abs(n.Dot(ay)) > 0.70)
            {
                var u = ax.Sub(n.Scale(ax.Dot(n)));
                if (u.Length() > 1e-6)
                    return u.Normalized();
            }
        }

        var fallback = profile != null && profile.AxisX.Length() > 1e-6
            ? profile.AxisX.Sub(n.Scale(profile.AxisX.Dot(n))).Normalized()
            : new Vec3(0, 0, 1).Sub(n.Scale(n.Z)).Normalized();
        return fallback.Length() > 1e-6 ? fallback : new Vec3(1, 0, 0);
    }

    private static double RangeGap(double aMin, double aMax, double bMin, double bMax)
    {
        if (aMax < bMin)
            return bMin - aMax;
        if (bMax < aMin)
            return aMin - bMax;
        return 0;
    }

    public static Vec3 GetPatchCenter(PatchCluster patch) => PatchCenter(patch);

    public static PatchCluster CombinePatchGroup(
        IReadOnlyList<PatchCluster> group,
        BuildingMeshProfile? profile = null) =>
        CombinePatches(group, profile);

    private static Vec3 PatchCenter(PatchCluster patch)
    {
        if (patch.WorldVertices.Count == 0)
            return new Vec3(0, 0, 0);

        var sx = 0.0;
        var sy = 0.0;
        var sz = 0.0;
        foreach (var v in patch.WorldVertices)
        {
            sx += v.X;
            sy += v.Y;
            sz += v.Z;
        }

        var n = patch.WorldVertices.Count;
        return new Vec3(sx / n, sy / n, sz / n);
    }

    private static (double uMin, double uMax, double vMin, double vMax) FootprintBounds(
        PatchCluster patch,
        Vec3 ax,
        Vec3 ay)
    {
        var u = patch.WorldVertices.Select(p => p.Dot(ax)).ToList();
        var v = patch.WorldVertices.Select(p => p.Dot(ay)).ToList();
        return (u.Min(), u.Max(), v.Min(), v.Max());
    }

    private static (double u, double v) PlanCenter(PatchCluster patch, Vec3 ax, Vec3 ay)
    {
        var c = PatchCenter(patch);
        return (c.Dot(ax), c.Dot(ay));
    }

    private static double PlanCenterDistance((double u, double v) a, (double u, double v) b)
    {
        var du = a.u - b.u;
        var dv = a.v - b.v;
        return System.Math.Sqrt(du * du + dv * dv);
    }

    private static double FootprintOverlapRatio(
        double uMinA, double uMaxA, double vMinA, double vMaxA,
        double uMinB, double uMaxB, double vMinB, double vMaxB)
    {
        var overlapU = System.Math.Max(0, System.Math.Min(uMaxA, uMaxB) - System.Math.Max(uMinA, uMinB));
        var overlapV = System.Math.Max(0, System.Math.Min(vMaxA, vMaxB) - System.Math.Max(vMinA, vMinB));
        var overlapArea = overlapU * overlapV;
        var areaA = System.Math.Max(1e-6, (uMaxA - uMinA) * (vMaxA - vMinA));
        var areaB = System.Math.Max(1e-6, (uMaxB - uMinB) * (vMaxB - vMinB));
        return overlapArea / System.Math.Min(areaA, areaB);
    }
}
