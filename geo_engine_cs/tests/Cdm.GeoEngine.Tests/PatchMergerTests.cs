using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class PatchMergerTests
{
    [Fact]
    public void MergeAntiparallel_CombinesOppositeWallFaces()
    {
        var nPos = new Vec3(1, 0, 0);
        var nNeg = new Vec3(-1, 0, 0);
        var left = WallPatch(nPos, 0.0);
        var right = WallPatch(nNeg, 0.15);
        var profile = new BuildingMeshProfile
        {
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            SizeM = new Vec3(6, 6, 4),
            WallThicknessM = 0.15,
        };

        var merged = PatchMerger.MergeAntiparallel(new[] { left, right }, profile);
        Assert.Single(merged);
        Assert.True(merged[0].WorldVertices.Count > left.WorldVertices.Count);
    }

    [Fact]
    public void MergeCoplanar_ChainMergesThreeFragments()
    {
        var n = new Vec3(1, 0, 0);
        var profile = new BuildingMeshProfile
        {
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            SizeM = new Vec3(10, 10, 4),
        };
        var a = FragmentPatch(n, 0.0, yOffset: 0.0);
        var b = FragmentPatch(n, 0.0, yOffset: 2.05);
        var c = FragmentPatch(n, 0.0, yOffset: 4.1);

        var merged = PatchMerger.MergeCoplanar(new[] { a, b, c }, profile);
        Assert.Single(merged);
    }

    [Fact]
    public void IntervalAdjacent_DetectsEdgeTouch()
    {
        Assert.True(PatchMerger.IntervalAdjacent(0, 2, 0, 1, 2.05, 4, 0, 1, 0.12));
        Assert.False(PatchMerger.IntervalAdjacent(0, 2, 0, 1, 5, 7, 0, 1, 0.12));
    }

    [Fact]
    public void MergeCoplanar_CombinesSameNormalFragments()
    {
        var n = new Vec3(1, 0, 0);
        var profile = new BuildingMeshProfile
        {
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            SizeM = new Vec3(6, 6, 4),
        };
        var a = WallPatch(n, 0.0);
        var b = FragmentPatch(n, 0.0, yOffset: 0.5);
        var merged = PatchMerger.MergeCoplanar(new[] { a, b }, profile);
        Assert.Single(merged);
    }

    [Fact]
    public void WallThicknessFromPairs_ReadsCavityWidth()
    {
        var mesh = new MeshData { Name = "WallCavity" };
        AddQuad(mesh, new Vec3(0, 0, 0), new Vec3(0, 0, 2), new Vec3(0, 2, 2), new Vec3(0, 2, 0));
        AddQuad(mesh, new Vec3(0.15, 0, 0), new Vec3(0.15, 2, 0), new Vec3(0.15, 2, 2), new Vec3(0.15, 0, 2));

        var thickness = BuildingMeshAnalyzer.EstimateWallThicknessFromPairs(mesh);
        Assert.InRange(thickness, 0.14, 0.16);
    }

    [Fact]
    public void MergeOverlappingFootprintDuplicates_CombinesTwinRoofPlates()
    {
        var profile = new BuildingMeshProfile
        {
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            SizeM = new Vec3(6, 4, 4),
        };
        var n = new Vec3(0, 0, 1);
        var a = new PatchCluster(
            new[] { 0 },
            new List<Vec3>
            {
                new(-2.5, 0.2, -0.05), new(3.5, 0.2, -0.05), new(3.5, 3.5, -0.05), new(-2.5, 3.5, -0.05),
                new(-2.5, 0.2, 3.6), new(3.5, 0.2, 3.6), new(3.5, 3.5, 3.6), new(-2.5, 3.5, 3.6),
            },
            20,
            n,
            SurfaceKind: PatchSurfaceKind.Horizontal);
        var b = new PatchCluster(
            new[] { 1 },
            new List<Vec3>
            {
                new(-2.4, 0.3, 0.1), new(3.4, 0.3, 0.1), new(3.4, 3.4, 0.1), new(-2.4, 3.4, 0.1),
                new(-2.4, 0.3, 3.55), new(3.4, 0.3, 3.55), new(3.4, 3.4, 3.55), new(-2.4, 3.4, 3.55),
            },
            18,
            n,
            SurfaceKind: PatchSurfaceKind.Horizontal);

        var merged = PatchMerger.MergeOverlappingFootprintDuplicates(new[] { a, b }, profile);
        Assert.Single(merged);
    }

    private static void AddQuad(MeshData mesh, Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var baseIdx = mesh.Vertices.Count;
        mesh.Vertices.AddRange(new[] { a, b, c, d });
        mesh.Faces.Add(new[] { baseIdx, baseIdx + 1, baseIdx + 2, baseIdx + 3 });
    }

    private static PatchCluster FragmentPatch(Vec3 normal, double offset, double yOffset)
    {
        var u = new Vec3(0, 1, 0);
        var v = new Vec3(0, 0, 1);
        var verts = new List<Vec3>();
        for (var y = yOffset; y <= yOffset + 2.0; y += 2.0)
        for (var z = 0.0; z <= 1.0; z += 1.0)
            verts.Add(normal.Scale(offset).Add(u.Scale(y)).Add(v.Scale(z)));

        return new PatchCluster(new[] { 0 }, verts, 2.0, normal);
    }

    private static PatchCluster WallPatch(Vec3 normal, double offset)
    {
        var u = new Vec3(0, 1, 0);
        var v = new Vec3(0, 0, 1);
        var verts = new List<Vec3>();
        for (var y = 0.0; y <= 2.0; y += 2.0)
        for (var z = 0.0; z <= 1.0; z += 1.0)
            verts.Add(normal.Scale(offset).Add(u.Scale(y)).Add(v.Scale(z)));

        return new PatchCluster(
            new[] { 0 },
            verts,
            2.0,
            normal);
    }
}

public class CorpusExtentCalibratorTests
{
    [Fact]
    public void ExtentsFromReferenceObbs_ReadsThinAxis()
    {
        var obbs = new List<OrientedBox>
        {
            new()
            {
                ExtentN = 0.075, ExtentU = 1.0, ExtentV = 0.5,
                AxisN = new Vec3(1, 0, 0), AxisU = new Vec3(0, 1, 0), AxisV = new Vec3(0, 0, 1),
            },
            new()
            {
                ExtentN = 0.08, ExtentU = 0.8, ExtentV = 0.4,
                AxisN = new Vec3(0, 1, 0), AxisU = new Vec3(1, 0, 0), AxisV = new Vec3(0, 0, 1),
            },
        };

        var (wall, _) = CorpusExtentCalibrator.ExtentsFromReferenceObbs(obbs);
        Assert.InRange(wall, 0.14, 0.17);
    }
}
