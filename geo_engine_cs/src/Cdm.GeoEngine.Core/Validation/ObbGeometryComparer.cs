using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Topology;

namespace Cdm.GeoEngine.Core.Validation;

/// <summary>Fit reference OBBs from Geometry LOD (vertex groups or mesh islands) and compare to generated boxes.</summary>
public static class ReferenceObbExtractor
{
    public static IReadOnlyList<OrientedBox> ExtractFromGeometryLod(MeshData geometryLod)
    {
        var fromGroups = ExtractFromVertexGroups(geometryLod);
        if (fromGroups.Count > 0)
            return fromGroups;

        return ExtractFromMeshIslands(geometryLod);
    }

    private static IReadOnlyList<OrientedBox> ExtractFromVertexGroups(MeshData geometryLod)
    {
        var list = new List<OrientedBox>();
        var profile = BuildingMeshAnalyzer.Analyze(geometryLod);
        foreach (var (name, indices) in geometryLod.VertexGroups.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!name.StartsWith("component", StringComparison.OrdinalIgnoreCase))
                continue;

            var verts = indices
                .Where(vi => vi >= 0 && vi < geometryLod.Vertices.Count)
                .Select(vi => geometryLod.Vertices[vi])
                .Distinct()
                .ToList();
            if (verts.Count < 4)
                continue;

            if (verts.Count == 8)
            {
                var parsed = BoxMeshParser.TryParse(verts);
                if (parsed != null)
                {
                    list.Add(parsed);
                    continue;
                }
            }

            var obb = ObbFitter.FitPatch(verts, EstimateDominantNormal(geometryLod, indices), profile);
            if (obb != null)
                list.Add(obb);
        }

        return list;
    }

    /// <summary>DayZ Geometry LOD often stores component names without vertex weights — boxes are separate islands.</summary>
    private static IReadOnlyList<OrientedBox> ExtractFromMeshIslands(MeshData geometryLod)
    {
        var list = new List<OrientedBox>();
        var profile = BuildingMeshAnalyzer.Analyze(geometryLod);

        foreach (var (vertIndices, faceIndices) in MeshTopology.EnumerateIslands(geometryLod))
        {
            if (faceIndices.Count == 0)
                continue;

            var verts = vertIndices
                .Where(vi => vi >= 0 && vi < geometryLod.Vertices.Count)
                .Select(vi => geometryLod.Vertices[vi])
                .ToList();
            if (verts.Count < 4)
                continue;

            OrientedBox? obb = null;
            if (verts.Count == 8)
                obb = BoxMeshParser.TryParse(verts);

            var normal = EstimateDominantNormalFromFaces(geometryLod, faceIndices);
            obb ??= ObbFitter.FitPatch(verts, normal, profile);
            if (obb != null)
                list.Add(obb);
        }

        return list;
    }

    private static Vec3 EstimateDominantNormal(MeshData mesh, IReadOnlyList<int> vertIndices)
    {
        var set = vertIndices.ToHashSet();
        var sum = new Vec3(0, 0, 0);
        var area = 0.0;
        foreach (var face in mesh.Faces)
        {
            if (!face.All(set.Contains))
                continue;
            if (face.Length < 3)
                continue;
            var a = mesh.Vertices[face[0]];
            var b = mesh.Vertices[face[1]];
            var c = mesh.Vertices[face[2]];
            var n = b.Sub(a).Cross(c.Sub(a));
            var len = n.Length();
            if (len < 1e-12)
                continue;
            sum = sum.Add(n.Scale(1.0 / len));
            area += len * 0.5;
        }

        return area > 1e-12 ? sum.Normalized() : new Vec3(0, 0, 1);
    }

    private static Vec3 EstimateDominantNormalFromFaces(MeshData mesh, IReadOnlyList<int> faceIndices)
    {
        var sum = new Vec3(0, 0, 0);
        var area = 0.0;
        foreach (var fi in faceIndices)
        {
            if (fi < 0 || fi >= mesh.Faces.Count)
                continue;
            var face = mesh.Faces[fi];
            if (face.Length < 3)
                continue;
            var a = mesh.Vertices[face[0]];
            var b = mesh.Vertices[face[1]];
            var c = mesh.Vertices[face[2]];
            var n = b.Sub(a).Cross(c.Sub(a));
            var len = n.Length();
            if (len < 1e-12)
                continue;
            sum = sum.Add(n.Scale(1.0 / len));
            area += len * 0.5;
        }

        return area > 1e-12 ? sum.Normalized() : new Vec3(0, 0, 1);
    }
}

public sealed class ObbGeometryScore
{
    public double ExtentScore { get; init; }
    public double RotationScore { get; init; }
    public double CenterScore { get; init; }
    public double OverallScore { get; init; }
    public int ReferenceCount { get; init; }
    public int GeneratedCount { get; init; }
    public int MatchedPairs { get; init; }
}

public static class ObbGeometryComparer
{
    public static ObbGeometryScore Compare(
        IReadOnlyList<OrientedBox> reference,
        IReadOnlyList<OrientedBox> generated)
    {
        if (reference.Count == 0 || generated.Count == 0)
        {
            return new ObbGeometryScore
            {
                ReferenceCount = reference.Count,
                GeneratedCount = generated.Count,
            };
        }

        var used = new HashSet<int>();
        var extentScores = new List<double>();
        var rotScores = new List<double>();
        var centerScores = new List<double>();

        foreach (var refObb in reference)
        {
            var bestIdx = -1;
            var bestCost = double.MaxValue;
            for (var i = 0; i < generated.Count; i++)
            {
                if (used.Contains(i))
                    continue;
                var cost = MatchCost(refObb, generated[i]);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
                continue;

            used.Add(bestIdx);
            var gen = generated[bestIdx];
            var aligned = AlignBoxes(refObb, gen);
            extentScores.Add(aligned.extent);
            rotScores.Add(aligned.rotation);
            centerScores.Add(aligned.center);
        }

        var extent = Average(extentScores);
        var rot = Average(rotScores);
        var center = Average(centerScores);
        var matchRatio = reference.Count > 0
            ? (double)extentScores.Count / reference.Count
            : 0;
        var overall = (extent * 0.55 + rot * 0.25 + center * 0.20) * matchRatio;

        return new ObbGeometryScore
        {
            ExtentScore = extent,
            RotationScore = rot,
            CenterScore = center,
            OverallScore = overall,
            ReferenceCount = reference.Count,
            GeneratedCount = generated.Count,
            MatchedPairs = extentScores.Count,
        };
    }

    public static IReadOnlyList<OrientedBox> FromComponents(
        IEnumerable<MeshComponent> components,
        BuildingMeshProfile? profile = null)
    {
        var list = new List<OrientedBox>();
        foreach (var comp in components)
        {
            if (comp.Mesh.Vertices.Count == 8)
            {
                var fromCorners = BoxMeshParser.TryParse(comp.Mesh.Vertices);
                if (fromCorners != null)
                {
                    list.Add(fromCorners);
                    continue;
                }
            }

            if (comp.Mesh.Vertices.Count < 4)
                continue;
            var hint = comp.Mesh.Faces.Count > 0
                ? MeshTopology.FaceNormal(comp.Mesh, comp.Mesh.Faces[0])
                : new Vec3(0, 0, 1);
            var obb = ObbFitter.FitPatch(comp.Mesh.Vertices, hint, profile);
            if (obb != null)
                list.Add(obb);
        }
        return list;
    }

    private static double MatchCost(OrientedBox a, OrientedBox b)
    {
        var aligned = AlignBoxes(a, b);
        return Vec3.Distance(a.Center, b.Center)
            + (1.0 - aligned.extent) * 2.0
            + (1.0 - aligned.rotation) * 1.5;
    }

    private static (double extent, double rotation, double center) AlignBoxes(OrientedBox a, OrientedBox b)
    {
        var aAxes = new[] { a.AxisU, a.AxisV, a.AxisN };
        var aExt = new[] { a.ExtentU, a.ExtentV, a.ExtentN };
        var bAxes = new[] { b.AxisU, b.AxisV, b.AxisN };
        var bExt = new[] { b.ExtentU, b.ExtentV, b.ExtentN };

        var perms = new[]
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
        };

        var bestExtent = 0.0;
        var bestRotation = 0.0;
        foreach (var p in perms)
        {
            var extent = (
                ExtentSim(aExt[0], bExt[p[0]]) +
                ExtentSim(aExt[1], bExt[p[1]]) +
                ExtentSim(aExt[2], bExt[p[2]])) / 3.0;
            var rotation = (
                System.Math.Abs(aAxes[0].Dot(bAxes[p[0]])) +
                System.Math.Abs(aAxes[1].Dot(bAxes[p[1]])) +
                System.Math.Abs(aAxes[2].Dot(bAxes[p[2]]))) / 3.0;
            if (extent + rotation > bestExtent + bestRotation)
            {
                bestExtent = extent;
                bestRotation = rotation;
            }
        }

        return (bestExtent, bestRotation, CenterSimilarity(a, b));
    }

    private static double ExtentSim(double x, double y)
    {
        var d = System.Math.Abs(x - y) / System.Math.Max(0.05, System.Math.Max(x, y));
        return System.Math.Clamp(1.0 - d, 0, 1);
    }

    private static double ExtentSimilarity(OrientedBox a, OrientedBox b) => AlignBoxes(a, b).extent;

    private static double RotationSimilarity(OrientedBox a, OrientedBox b) => AlignBoxes(a, b).rotation;

    private static double CenterSimilarity(OrientedBox a, OrientedBox b)
    {
        var d = Vec3.Distance(a.Center, b.Center);
        var scale = System.Math.Max(0.5, a.ExtentU + a.ExtentV + a.ExtentN);
        return System.Math.Clamp(1.0 - d / scale, 0, 1);
    }

    private static double Average(IReadOnlyList<double> values) =>
        values.Count == 0 ? 0 : values.Average();
}
