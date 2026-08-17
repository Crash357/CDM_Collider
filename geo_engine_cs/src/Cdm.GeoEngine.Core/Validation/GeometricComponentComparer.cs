using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Validation;

public enum GeometricMatchStatus
{
    Good,
    Warn,
    Fail,
    Unmatched,
}

public sealed class GeometricComponentPairResult
{
    public string ReferenceName { get; init; } = "";
    public string GeneratedName { get; init; } = "";
    public int ReferenceIndex { get; init; }
    public int GeneratedIndex { get; init; }
    public double MaxCornerErrorM { get; init; }
    public double MeanCornerErrorM { get; init; }
    public double CenterDeltaM { get; init; }
    public double ExtentDeltaU { get; init; }
    public double ExtentDeltaV { get; init; }
    public double ExtentDeltaN { get; init; }
    public GeometricMatchStatus Status { get; init; }
}

public sealed class GeometricCompareResult
{
    public int ReferenceCount { get; init; }
    public int GeneratedCount { get; init; }
    public int MatchedPairs { get; init; }
    public double MaxCornerErrorM { get; init; }
    public double MeanCornerErrorM { get; init; }
    public double MeanCenterDeltaM { get; init; }
    public double OverallScore { get; init; }
    public GeometricMatchStatus OverallStatus { get; init; }
    public IReadOnlyList<GeometricComponentPairResult> Pairs { get; init; } = Array.Empty<GeometricComponentPairResult>();
}

public sealed class ComponentGeometry
{
    public string Name { get; init; } = "";
    public IReadOnlyList<Vec3> Corners { get; init; } = Array.Empty<Vec3>();
    public Vec3 Center { get; init; }
}

/// <summary>
/// Compare generated Geometry LOD components against reference using actual corner vertices,
/// not abstract OBB axis-permutation scores.
/// </summary>
public static class GeometricComponentComparer
{
    public const double GoodThresholdM = 0.001;
    public const double WarnThresholdM = 0.01;
    public const double FailThresholdM = 0.05;

    public static GeometricCompareResult Compare(
        MeshData referenceGeometryLod,
        IReadOnlyList<MeshComponent> generated)
    {
        var reference = ExtractFromGeometryLod(referenceGeometryLod);
        var gen = FromComponents(generated);
        return Compare(reference, gen);
    }

    public static GeometricCompareResult Compare(
        IReadOnlyList<ComponentGeometry> reference,
        IReadOnlyList<ComponentGeometry> generated)
    {
        if (reference.Count == 0 || generated.Count == 0)
        {
            return new GeometricCompareResult
            {
                ReferenceCount = reference.Count,
                GeneratedCount = generated.Count,
                OverallStatus = GeometricMatchStatus.Unmatched,
            };
        }

        var pairs = MatchByNearestCenter(reference, generated);
        if (pairs.Count == 0)
        {
            return new GeometricCompareResult
            {
                ReferenceCount = reference.Count,
                GeneratedCount = generated.Count,
                OverallStatus = GeometricMatchStatus.Fail,
            };
        }

        var pairResults = new List<GeometricComponentPairResult>();
        foreach (var (refIdx, genIdx) in pairs)
        {
            var refComp = reference[refIdx];
            var genComp = generated[genIdx];
            pairResults.Add(ComparePair(refComp, genComp, refIdx, genIdx));
        }

        var maxCorner = pairResults.Max(p => p.MaxCornerErrorM);
        var meanCorner = pairResults.Average(p => p.MeanCornerErrorM);
        var meanCenter = pairResults.Average(p => p.CenterDeltaM);
        var overallScore = ComputeOverallScore(pairResults, reference.Count);
        var overallStatus = WorstStatus(pairResults.Select(p => p.Status));

        return new GeometricCompareResult
        {
            ReferenceCount = reference.Count,
            GeneratedCount = generated.Count,
            MatchedPairs = pairResults.Count,
            MaxCornerErrorM = maxCorner,
            MeanCornerErrorM = meanCorner,
            MeanCenterDeltaM = meanCenter,
            OverallScore = overallScore,
            OverallStatus = overallStatus,
            Pairs = pairResults,
        };
    }

    public static IReadOnlyList<ComponentGeometry> ExtractFromGeometryLod(MeshData geometryLod)
    {
        var fromGroups = ExtractFromVertexGroups(geometryLod);
        if (fromGroups.Count > 0)
            return fromGroups;

        return ExtractFromMeshIslands(geometryLod);
    }

    public static IReadOnlyList<ComponentGeometry> FromComponents(IEnumerable<MeshComponent> components)
    {
        var list = new List<ComponentGeometry>();
        var index = 0;
        foreach (var comp in components)
        {
            var corners = ExtractCorners(comp.Mesh);
            if (corners.Count < 4)
                continue;

            list.Add(new ComponentGeometry
            {
                Name = string.IsNullOrEmpty(comp.Name) ? $"Component{index + 1:D2}" : comp.Name,
                Corners = corners,
                Center = Vec3.Centroid(corners),
            });
            index++;
        }
        return list;
    }

    private static IReadOnlyList<ComponentGeometry> ExtractFromVertexGroups(MeshData geometryLod)
    {
        var list = new List<ComponentGeometry>();
        foreach (var (name, indices) in geometryLod.VertexGroups.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!name.StartsWith("component", StringComparison.OrdinalIgnoreCase))
                continue;

            var corners = indices
                .Where(vi => vi >= 0 && vi < geometryLod.Vertices.Count)
                .Select(vi => geometryLod.Vertices[vi])
                .Distinct()
                .ToList();
            if (corners.Count < 4)
                continue;

            corners = NormalizeCorners(corners);
            list.Add(new ComponentGeometry
            {
                Name = name,
                Corners = corners,
                Center = Vec3.Centroid(corners),
            });
        }
        return list;
    }

    private static IReadOnlyList<ComponentGeometry> ExtractFromMeshIslands(MeshData geometryLod)
    {
        var list = new List<ComponentGeometry>();
        var index = 0;
        foreach (var (vertIndices, _) in MeshTopology.EnumerateIslands(geometryLod))
        {
            var corners = vertIndices
                .Where(vi => vi >= 0 && vi < geometryLod.Vertices.Count)
                .Select(vi => geometryLod.Vertices[vi])
                .Distinct()
                .ToList();
            if (corners.Count < 4)
                continue;

            corners = NormalizeCorners(corners);
            list.Add(new ComponentGeometry
            {
                Name = $"component{index + 1:D2}",
                Corners = corners,
                Center = Vec3.Centroid(corners),
            });
            index++;
        }
        return list;
    }

    private static IReadOnlyList<Vec3> ExtractCorners(MeshData mesh)
    {
        if (mesh.Vertices.Count == 8)
            return mesh.Vertices.ToList();

        if (mesh.Vertices.Count > 8)
        {
            var parsed = BoxMeshParser.TryParse(mesh.Vertices.Take(8).ToList());
            if (parsed?.Corners is { Count: 8 })
                return parsed.Corners.ToList();
        }

        return NormalizeCorners(mesh.Vertices);
    }

    private static List<Vec3> NormalizeCorners(IReadOnlyList<Vec3> verts)
    {
        if (verts.Count == 8)
        {
            var parsed = BoxMeshParser.TryParse(verts);
            return parsed?.Corners?.ToList() ?? verts.ToList();
        }

        var distinct = verts.Distinct().ToList();
        if (distinct.Count == 8)
        {
            var parsed = BoxMeshParser.TryParse(distinct);
            return parsed?.Corners?.ToList() ?? distinct;
        }

        var obb = BoxMeshParser.TryParse(distinct.Count >= 8 ? distinct.Take(8).ToList() : distinct);
        if (obb?.Corners is { Count: 8 })
            return obb.Corners.ToList();

        var fitted = ObbFitter.FitPatch(distinct, new Vec3(0, 0, 1));
        if (fitted != null)
            return ReferenceObbSnap.BuildCorners(fitted).ToList();

        return distinct;
    }

    private static List<(int RefIdx, int GenIdx)> MatchByNearestCenter(
        IReadOnlyList<ComponentGeometry> reference,
        IReadOnlyList<ComponentGeometry> generated)
    {
        var n = System.Math.Max(reference.Count, generated.Count);
        if (n == 0)
            return new List<(int, int)>();

        const double dummyCost = 1e6;
        var cost = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            if (i < reference.Count && j < generated.Count)
                cost[i, j] = MatchCost(reference[i], generated[j]);
            else
                cost[i, j] = dummyCost;
        }

        var assignment = HungarianSolver.Solve(cost, n);
        var pairs = new List<(int, int)>();
        for (var i = 0; i < reference.Count; i++)
        {
            var j = assignment[i];
            if (j >= 0 && j < generated.Count && cost[i, j] < dummyCost * 0.5)
                pairs.Add((i, j));
        }

        return pairs;
    }

    /// <summary>Center + corner error when both are 8-corner boxes; else center + extents.</summary>
    private static double MatchCost(ComponentGeometry reference, ComponentGeometry generated)
    {
        var refCorners = NormalizeCorners(reference.Corners);
        var genCorners = NormalizeCorners(generated.Corners);
        var centerDist = Vec3.Distance(reference.Center, generated.Center);

        if (refCorners.Count == 8 && genCorners.Count == 8)
        {
            var refBox = ParseObb(refCorners);
            var genBox = ParseObb(genCorners);
            if (refBox != null && genBox != null)
            {
                var boxRefExt = SortedExtents(refBox);
                var boxGenExt = SortedExtents(genBox);
                var boxExtentDiff = boxRefExt.Zip(boxGenExt, (a, b) => System.Math.Abs(a - b)).Sum();
                var boxAxisAlign = BestAxisAlignment(refBox, genBox);
                var (_, meanCorner) = CornerErrors(refCorners, genCorners);
                var kindPenalty = SurfaceKindMismatchPenalty(refBox, genBox);
                var heightPenalty = VerticalExtentMismatchPenalty(refCorners, genCorners, refBox, genBox);
                return meanCorner
                       + centerDist * 6.0
                       + boxExtentDiff * 1.5
                       + (1.0 - boxAxisAlign) * 0.75
                       + kindPenalty
                       + heightPenalty;
            }

            var (_, meanOnly) = CornerErrors(refCorners, genCorners);
            return meanOnly + centerDist * 2.0;
        }

        var refObb = ParseObb(reference.Corners);
        var genObb = ParseObb(generated.Corners);
        if (refObb == null || genObb == null)
            return centerDist;

        var refExt = SortedExtents(refObb);
        var genExt = SortedExtents(genObb);
        var extentDiff = refExt.Zip(genExt, (a, b) => System.Math.Abs(a - b)).Sum();
        var axisAlign = BestAxisAlignment(refObb, genObb);

        return centerDist * 2.0 + extentDiff * 1.5 + (1.0 - axisAlign) * 0.75
               + SurfaceKindMismatchPenalty(refObb, genObb);
    }

    private static double SurfaceKindMismatchPenalty(OrientedBox reference, OrientedBox generated)
    {
        var refKind = ClassifyObbSurfaceKind(reference);
        var genKind = ClassifyObbSurfaceKind(generated);
        if (refKind == genKind)
            return 0;

        return refKind switch
        {
            PatchSurfaceKind.Wall when genKind == PatchSurfaceKind.Slope => 4.0,
            PatchSurfaceKind.Slope when genKind == PatchSurfaceKind.Wall => 4.0,
            PatchSurfaceKind.Horizontal when genKind != PatchSurfaceKind.Horizontal => 5.0,
            PatchSurfaceKind.Plinth when genKind != PatchSurfaceKind.Plinth => 2.5,
            PatchSurfaceKind.Soffit when genKind != PatchSurfaceKind.Soffit => 4.0,
            PatchSurfaceKind.EndCap when genKind is PatchSurfaceKind.Wall or PatchSurfaceKind.EndCap => 0,
            PatchSurfaceKind.Slope when genKind == PatchSurfaceKind.EndCap => 3.5,
            PatchSurfaceKind.Slope when genKind == PatchSurfaceKind.Wall => 3.5,
            _ when genKind == PatchSurfaceKind.Plinth => 2.5,
            _ when genKind == PatchSurfaceKind.Soffit => 2.5,
            _ => 2.0,
        };
    }

    private static PatchSurfaceKind ClassifyObbSurfaceKind(OrientedBox obb)
    {
        var n = obb.AxisN.Normalized();
        var az = System.Math.Abs(n.Z);
        if (az >= 0.85)
            return PatchSurfaceKind.Horizontal;
        var ax = System.Math.Abs(n.X);
        var ay = System.Math.Abs(n.Y);
        if ((ax >= 0.70 || ay >= 0.70) && az < PatchSurfaceClassifier.WallMaxVerticalDot)
            return PatchSurfaceKind.Wall;
        return PatchSurfaceKind.Slope;
    }

    private static double VerticalExtentMismatchPenalty(
        IReadOnlyList<Vec3> refCorners,
        IReadOnlyList<Vec3> genCorners,
        OrientedBox refBox,
        OrientedBox genBox)
    {
        var refZSpan = refCorners.Max(c => c.Z) - refCorners.Min(c => c.Z);
        var genZSpan = genCorners.Max(c => c.Z) - genCorners.Min(c => c.Z);
        var dz = System.Math.Abs(refBox.Center.Z - genBox.Center.Z);
        var penalty = 0.0;

        if (dz > 0.35)
            penalty += dz * 5.0;

        var spanGap = System.Math.Max(0, refZSpan - genZSpan);
        if (spanGap > 0.55 && refZSpan > 1.0)
            penalty += spanGap * 1.5;

        return penalty;
    }

    private static OrientedBox? ParseObb(IReadOnlyList<Vec3> corners)
    {
        if (corners.Count != 8)
            return null;
        return BoxMeshParser.TryParse(corners);
    }

    private static double[] SortedExtents(OrientedBox obb) =>
        new[] { obb.ExtentU, obb.ExtentV, obb.ExtentN }.OrderBy(e => e).ToArray();

    private static double BestAxisAlignment(OrientedBox reference, OrientedBox generated)
    {
        var refAxes = new[] { reference.AxisU, reference.AxisV, reference.AxisN };
        var genAxes = new[] { generated.AxisU, generated.AxisV, generated.AxisN };
        var perms = new[]
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
        };

        var best = 0.0;
        foreach (var p in perms)
        {
            var score =
                System.Math.Abs(refAxes[0].Dot(genAxes[p[0]])) +
                System.Math.Abs(refAxes[1].Dot(genAxes[p[1]])) +
                System.Math.Abs(refAxes[2].Dot(genAxes[p[2]]));
            if (score > best)
                best = score;
        }

        return best / 3.0;
    }

    private static GeometricComponentPairResult ComparePair(
        ComponentGeometry reference,
        ComponentGeometry generated,
        int refIdx,
        int genIdx)
    {
        var refCorners = NormalizeCorners(reference.Corners);
        var genCorners = NormalizeCorners(generated.Corners);
        var centerDelta = Vec3.Distance(reference.Center, generated.Center);
        var (maxCorner, meanCorner) = CornerErrors(refCorners, genCorners);
        var (du, dv, dn) = ExtentDeltas(refCorners, genCorners);

        return new GeometricComponentPairResult
        {
            ReferenceName = reference.Name,
            GeneratedName = generated.Name,
            ReferenceIndex = refIdx,
            GeneratedIndex = genIdx,
            MaxCornerErrorM = maxCorner,
            MeanCornerErrorM = meanCorner,
            CenterDeltaM = centerDelta,
            ExtentDeltaU = du,
            ExtentDeltaV = dv,
            ExtentDeltaN = dn,
            Status = StatusFromError(maxCorner),
        };
    }

    private static (double Max, double Mean) CornerErrors(
        IReadOnlyList<Vec3> referenceCorners,
        IReadOnlyList<Vec3> generatedCorners)
    {
        var n = System.Math.Min(referenceCorners.Count, generatedCorners.Count);
        if (n == 0)
            return (double.MaxValue, double.MaxValue);

        if (n == 1)
        {
            var d = Vec3.Distance(referenceCorners[0], generatedCorners[0]);
            return (d, d);
        }

        var cost = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            cost[i, j] = Vec3.Distance(referenceCorners[i], generatedCorners[j]);

        var assignment = MinAssignment(cost, n);
        var errors = assignment.Select(t => cost[t.Item1, t.Item2]).ToList();
        return (errors.Max(), errors.Average());
    }

    private static (double Du, double Dv, double Dn) ExtentDeltas(
        IReadOnlyList<Vec3> referenceCorners,
        IReadOnlyList<Vec3> generatedCorners)
    {
        var refObb = BoxMeshParser.TryParse(referenceCorners.Count == 8
            ? referenceCorners
            : referenceCorners.Take(8).ToList());
        var genObb = BoxMeshParser.TryParse(generatedCorners.Count == 8
            ? generatedCorners
            : generatedCorners.Take(8).ToList());

        if (refObb == null || genObb == null)
            return (0, 0, 0);

        var aligned = AlignExtents(refObb, genObb);
        return (
            System.Math.Abs(refObb.ExtentU - aligned.u),
            System.Math.Abs(refObb.ExtentV - aligned.v),
            System.Math.Abs(refObb.ExtentN - aligned.n));
    }

    private static (double u, double v, double n) AlignExtents(OrientedBox reference, OrientedBox generated)
    {
        var refAxes = new[] { reference.AxisU, reference.AxisV, reference.AxisN };
        var genAxes = new[] { generated.AxisU, generated.AxisV, generated.AxisN };
        var genExt = new[] { generated.ExtentU, generated.ExtentV, generated.ExtentN };

        var perms = new[]
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
        };

        var best = (u: genExt[0], v: genExt[1], n: genExt[2]);
        var bestScore = -1.0;
        foreach (var p in perms)
        {
            var score =
                System.Math.Abs(refAxes[0].Dot(genAxes[p[0]])) +
                System.Math.Abs(refAxes[1].Dot(genAxes[p[1]])) +
                System.Math.Abs(refAxes[2].Dot(genAxes[p[2]]));
            if (score > bestScore)
            {
                bestScore = score;
                best = (genExt[p[0]], genExt[p[1]], genExt[p[2]]);
            }
        }

        return best;
    }

    private static List<(int, int)> MinAssignment(double[,] cost, int n)
    {
        var bestCost = double.MaxValue;
        var bestPerm = Enumerable.Range(0, n).ToArray();
        var perm = new int[n];
        var used = new bool[n];

        void Search(int row, double running)
        {
            if (running >= bestCost)
                return;
            if (row == n)
            {
                bestCost = running;
                Array.Copy(perm, bestPerm, n);
                return;
            }

            for (var col = 0; col < n; col++)
            {
                if (used[col])
                    continue;
                used[col] = true;
                perm[row] = col;
                Search(row + 1, running + cost[row, col]);
                used[col] = false;
            }
        }

        Search(0, 0);
        return bestPerm.Select((col, row) => (row, col)).ToList();
    }

    public static GeometricMatchStatus StatusFromError(double maxCornerErrorM)
    {
        if (maxCornerErrorM <= GoodThresholdM)
            return GeometricMatchStatus.Good;
        if (maxCornerErrorM > FailThresholdM)
            return GeometricMatchStatus.Fail;
        return GeometricMatchStatus.Warn;
    }

    private static GeometricMatchStatus WorstStatus(IEnumerable<GeometricMatchStatus> statuses)
    {
        var worst = GeometricMatchStatus.Good;
        foreach (var s in statuses)
        {
            if (s == GeometricMatchStatus.Fail)
                return GeometricMatchStatus.Fail;
            if (s == GeometricMatchStatus.Warn)
                worst = GeometricMatchStatus.Warn;
        }
        return worst;
    }

    private static double ComputeOverallScore(
        IReadOnlyList<GeometricComponentPairResult> pairs,
        int referenceCount)
    {
        if (pairs.Count == 0 || referenceCount == 0)
            return 0;

        var pairScores = pairs.Select(p => ScoreFromError(p.MaxCornerErrorM, p.CenterDeltaM)).ToList();
        var matchRatio = (double)pairs.Count / referenceCount;
        return pairScores.Average() * matchRatio;
    }

    private static double ScoreFromError(double maxCornerErrorM, double centerDeltaM)
    {
        var error = System.Math.Max(maxCornerErrorM, centerDeltaM * 0.5);
        if (error <= GoodThresholdM)
            return 1.0;
        if (error <= WarnThresholdM)
            return 1.0 - 0.25 * (error - GoodThresholdM) / (WarnThresholdM - GoodThresholdM);
        if (error <= FailThresholdM)
            return 0.75 - 0.55 * (error - WarnThresholdM) / (FailThresholdM - WarnThresholdM);
        return System.Math.Max(0, 0.2 - (error - FailThresholdM) / 0.1);
    }
}

/// <summary>Kuhn-Munkres minimum assignment for square cost matrices (n &lt;= 64).</summary>
internal static class HungarianSolver
{
    public static int[] Solve(double[,] cost, int n)
    {
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];

        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = new double[n + 1];
            var used = new bool[n + 1];
            Array.Fill(minv, double.PositiveInfinity);

            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = double.PositiveInfinity;
                var j1 = 0;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                        continue;
                    var cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }
                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            } while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        var answer = new int[n];
        for (var j = 1; j <= n; j++)
        {
            if (p[j] == 0)
                continue;
            answer[p[j] - 1] = j - 1;
        }

        return answer;
    }
}
