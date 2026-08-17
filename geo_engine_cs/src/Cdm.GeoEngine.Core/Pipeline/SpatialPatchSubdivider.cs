using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Pipeline;

/// <summary>
/// Split wall/floor patches along in-plane gaps, door openings, and reference OBB boundaries.
/// </summary>
public static class SpatialPatchSubdivider
{
    public static IReadOnlyList<PatchCluster> Subdivide(
        MeshData mesh,
        IReadOnlyList<PatchCluster> patches,
        BuildingMeshProfile profile,
        SpatialSubdivisionOptions? options = null)
    {
        options ??= new SpatialSubdivisionOptions();
        var result = new List<PatchCluster>();

        foreach (var patch in patches)
        {
            result.AddRange(SubdividePatch(mesh, patch, profile, options));
        }

        return result;
    }

    private static IEnumerable<PatchCluster> SubdividePatch(
        MeshData mesh,
        PatchCluster patch,
        BuildingMeshProfile profile,
        SpatialSubdivisionOptions options)
    {
        if (patch.FaceIndices.Count == 0)
            yield break;

        var n = BuildingMeshAnalyzer.SnapNormalToBuildingAxes(patch.DominantNormal, profile);
        var u = BuildInPlaneU(n, profile);
        var v = n.Cross(u).Normalized();

        var faceData = new List<(int fi, double pu, double pv, double area)>();
        foreach (var fi in patch.FaceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            var face = mesh.Faces[fi];
            var c = FaceCentroid(mesh, face);
            faceData.Add((fi, c.Dot(u), c.Dot(v), MeshTopology.FaceArea(mesh, face)));
        }

        if (faceData.Count == 0)
            yield break;

        var uVals = faceData.Select(f => f.pu).ToList();
        var vVals = faceData.Select(f => f.pv).ToList();
        var uMin = uVals.Min();
        var uMax = uVals.Max();
        var vMin = vVals.Min();
        var vMax = vVals.Max();

        List<double> uCuts;
        List<double> vCuts;
        if (options.WallGapAndDoorCutsOnly && options.SpanFallbackOnly)
        {
            uCuts = new List<double>();
            vCuts = new List<double>();
        }
        else
        {
            var collectGaps = options.WallGapAndDoorCutsOnly || !options.SpanFallbackOnly;
            uCuts = collectGaps
                ? CollectCuts(uVals, uMin, uMax, options.MinGapM, options.BinSizeM)
                : new List<double>();
            vCuts = collectGaps
                ? CollectCuts(vVals, vMin, vMax, options.MinGapM, options.BinSizeM)
                : new List<double>();
        }

        var isHorizontal = System.Math.Abs(n.Dot(profile.AxisZ.Normalized())) > 0.85;
        if (!isHorizontal && options.MaxInPlaneSpanM > 0)
        {
            var uSpan = uMax - uMin;
            var vSpan = vMax - vMin;
            var needsSpanCut = System.Math.Max(uSpan, vSpan) > options.MaxInPlaneSpanM * 1.05;
            if (!options.SpanFallbackOnly || needsSpanCut)
            {
                uCuts.AddRange(CollectSpanGridCuts(uMin, uMax, options.MaxInPlaneSpanM));
                vCuts.AddRange(CollectSpanGridCuts(vMin, vMax, options.MaxInPlaneSpanM));
            }
        }

        if (options.ReferenceObbs is { Count: > 0 })
        {
            uCuts.AddRange(ReferenceBoundaryCuts(options.ReferenceObbs, n, u, v, uMin, uMax, isU: true));
            vCuts.AddRange(ReferenceBoundaryCuts(options.ReferenceObbs, n, u, v, vMin, vMax, isU: false));
        }

        if (options.DoorRegions is { Count: > 0 })
        {
            uCuts.AddRange(DoorCuts(options.DoorRegions, u, v, uMin, uMax, isU: true));
            vCuts.AddRange(DoorCuts(options.DoorRegions, u, v, vMin, vMax, isU: false));
        }

        var uBounds = BuildBounds(uMin, uMax, uCuts);
        var vBounds = BuildBounds(vMin, vMax, vCuts);

        if (uBounds.Count <= 1 && vBounds.Count <= 1)
        {
            yield return patch;
            yield break;
        }

        var buckets = new Dictionary<(int iu, int iv), List<int>>();
        foreach (var (fi, pu, pv, _) in faceData)
        {
            var iu = FindBin(pu, uBounds);
            var iv = FindBin(pv, vBounds);
            var key = (iu, iv);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<int>();
                buckets[key] = list;
            }
            list.Add(fi);
        }

        foreach (var (_, faceIndices) in buckets)
        {
            if (faceIndices.Count == 0)
                continue;

            var vertSet = new HashSet<int>();
            var area = 0.0;
            foreach (var fi in faceIndices)
            {
                area += MeshTopology.FaceArea(mesh, mesh.Faces[fi]);
                foreach (var vi in mesh.Faces[fi])
                    vertSet.Add(vi);
            }

            if (vertSet.Count < 4 || area < options.MinPatchAreaM2)
                continue;

            yield return new PatchCluster(
                faceIndices,
                vertSet.Select(vi => mesh.Vertices[vi]).ToList(),
                area,
                patch.DominantNormal,
                patch.ReferenceIndex,
                patch.SurfaceKind);
        }
    }

    private static Vec3 BuildInPlaneU(Vec3 n, BuildingMeshProfile profile)
    {
        var ax = System.Math.Abs(n.Dot(profile.AxisX));
        var ay = System.Math.Abs(n.Dot(profile.AxisY));
        var az = System.Math.Abs(n.Dot(profile.AxisZ));

        Vec3 tangent;
        if (az >= 0.85)
            tangent = profile.AxisX;
        else if (ax >= ay)
            tangent = profile.AxisY;
        else
            tangent = profile.AxisX;

        return tangent.Sub(n.Scale(tangent.Dot(n))).Normalized();
    }

    private static List<double> CollectCuts(
        IReadOnlyList<double> values,
        double min,
        double max,
        double minGapM,
        double binSizeM)
    {
        if (values.Count < 4 || max - min < minGapM * 2)
            return new List<double>();

        var bin = System.Math.Max(0.08, binSizeM);
        var start = System.Math.Floor(min / bin) * bin;
        var end = System.Math.Ceiling(max / bin) * bin;
        var occupied = new HashSet<int>();

        foreach (var val in values)
        {
            var idx = (int)System.Math.Floor((val - start) / bin);
            occupied.Add(idx);
        }

        var cuts = new List<double>();
        var runStart = -1;
        var lastIdx = (int)System.Math.Ceiling((end - start) / bin);
        for (var i = 0; i <= lastIdx; i++)
        {
            if (occupied.Contains(i))
            {
                if (runStart >= 0)
                {
                    var gap = (i - runStart) * bin;
                    if (gap >= minGapM)
                        cuts.Add(start + (runStart + i) * 0.5 * bin);
                    runStart = -1;
                }
            }
            else if (runStart < 0)
            {
                runStart = i;
            }
        }

        return cuts;
    }

    /// <summary>Target max in-plane wall segment length for FaceDriven blind mode.</summary>
    public static double DefaultWallMaxInPlaneSpanM(BuildingMeshProfile profile)
    {
        var longH = System.Math.Max(profile.SizeM.X, profile.SizeM.Y);
        if (longH > 7.0)
            return System.Math.Clamp(longH / 1.9, 5.0, 7.0);
        if (longH < 6.0)
            return 0;
        return System.Math.Clamp(longH / 3.5, 2.0, 4.0);
    }

    private static List<double> CollectSpanGridCuts(double min, double max, double maxSpanM)
    {
        if (maxSpanM <= 0 || max - min <= maxSpanM * 1.05)
            return new List<double>();

        var cuts = new List<double>();
        var pos = min + maxSpanM;
        while (pos < max - maxSpanM * 0.4)
        {
            cuts.Add(pos);
            pos += maxSpanM;
        }

        return cuts;
    }

    private static IEnumerable<double> ReferenceBoundaryCuts(
        IReadOnlyList<OrientedBox> refObbs,
        Vec3 patchNormal,
        Vec3 u,
        Vec3 v,
        double min,
        double max,
        bool isU)
    {
        foreach (var obb in refObbs)
        {
            var align = System.Math.Max(
                System.Math.Abs(obb.AxisN.Dot(patchNormal)),
                System.Math.Max(System.Math.Abs(obb.AxisU.Dot(patchNormal)),
                    System.Math.Abs(obb.AxisV.Dot(patchNormal))));
            if (align < 0.65)
                continue;

            foreach (var corner in obb.Corners)
            {
                var coord = isU ? corner.Dot(u) : corner.Dot(v);
                if (coord > min + 0.04 && coord < max - 0.04)
                    yield return coord;
            }
        }
    }

    private static IEnumerable<double> DoorCuts(
        IReadOnlyList<DoorRegion> doors,
        Vec3 u,
        Vec3 v,
        double min,
        double max,
        bool isU)
    {
        foreach (var door in doors)
        {
            var corners = new[]
            {
                door.Min, door.Max,
                new Vec3(door.Min.X, door.Max.Y, door.Min.Z),
                new Vec3(door.Max.X, door.Min.Y, door.Max.Z),
            };
            foreach (var c in corners)
            {
                var coord = isU ? c.Dot(u) : c.Dot(v);
                if (coord > min + 0.02 && coord < max - 0.02)
                    yield return coord;
            }
        }
    }

    private static List<(double lo, double hi)> BuildBounds(double min, double max, List<double> cuts)
    {
        var coords = new List<double> { min };
        coords.AddRange(cuts.Where(c => c > min + 0.04 && c < max - 0.04));
        coords.Add(max);
        coords.Sort();

        var merged = new List<double> { coords[0] };
        foreach (var c in coords.Skip(1))
        {
            if (c - merged[^1] < 0.06)
                merged[^1] = (merged[^1] + c) * 0.5;
            else
                merged.Add(c);
        }

        var bounds = new List<(double, double)>();
        for (var i = 0; i < merged.Count - 1; i++)
        {
            if (merged[i + 1] - merged[i] >= 0.08)
                bounds.Add((merged[i], merged[i + 1]));
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

    private static Vec3 FaceCentroid(MeshData mesh, int[] face)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var vi in face)
            sum = sum.Add(mesh.Vertices[vi]);
        return sum.Scale(1.0 / face.Length);
    }
}

public sealed class SpatialSubdivisionOptions
{
    public double MinGapM { get; init; } = 0.25;
    public double BinSizeM { get; init; } = 0.15;
    public double MinPatchAreaM2 { get; init; } = 0.05;
    /// <summary>Force grid splits on wall patches when tangent span exceeds this (0 = off).</summary>
    public double MaxInPlaneSpanM { get; init; }
    /// <summary>Skip gap-bin subdivision; only door cuts and max-span grid splits.</summary>
    public bool SpanFallbackOnly { get; init; }
    /// <summary>Gap detection along walls (panel breaks, openings) plus door cuts.</summary>
    public bool WallGapAndDoorCutsOnly { get; init; }
    public IReadOnlyList<OrientedBox>? ReferenceObbs { get; init; }
    public IReadOnlyList<DoorRegion>? DoorRegions { get; init; }
}
