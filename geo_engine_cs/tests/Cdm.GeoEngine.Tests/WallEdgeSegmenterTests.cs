using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class WallEdgeSegmenterTests
{
    [Fact]
    public void SegmentWallFaces_GapBetweenPanels_YieldsTwoSegments()
    {
        var mesh = new MeshData { Name = "WallGap" };
        AddQuad(mesh, new Vec3(0, 0, 0), new Vec3(0, 1.5, 0), new Vec3(0, 1.5, 2), new Vec3(0, 0, 2));
        AddQuad(mesh, new Vec3(0, 2.5, 0), new Vec3(0, 4, 0), new Vec3(0, 4, 2), new Vec3(0, 2.5, 2));

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var faces = Enumerable.Range(0, mesh.Faces.Count).ToList();
        var patches = WallEdgeSegmenter.SegmentWallFacesByTopology(
            mesh, faces, new Vec3(1, 0, 0), profile,
            new WallEdgeSegmentationOptions { MinAreaM2 = 0.05, MaxSpanM = 6 });

        Assert.Equal(2, patches.Count);
    }

    [Fact]
    public void SegmentWallFaces_LongWall_SplitsByMaxSpan()
    {
        var mesh = new MeshData { Name = "LongWall" };
        AddQuad(mesh, new Vec3(0, 0, 0), new Vec3(0, 3, 0), new Vec3(0, 3, 2), new Vec3(0, 0, 2));
        AddQuad(mesh, new Vec3(0, 3, 0), new Vec3(0, 6, 0), new Vec3(0, 6, 2), new Vec3(0, 3, 2));

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var faces = Enumerable.Range(0, mesh.Faces.Count).ToList();
        const double maxSpan = 3.0;
        var patches = WallEdgeSegmenter.SegmentWallFacesByTopology(
            mesh, faces, new Vec3(1, 0, 0), profile,
            new WallEdgeSegmentationOptions { MinAreaM2 = 0.05, MaxSpanM = maxSpan });

        Assert.True(patches.Count >= 2);
        var diag = PatchDiagnostics.Analyze(mesh, patches, profile);
        Assert.All(diag, d => Assert.True(d.TangentSpanM <= maxSpan + 0.6, $"span {d.TangentSpanM}"));
    }

    private static void AddQuad(MeshData mesh, Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var o = mesh.Vertices.Count;
        mesh.Vertices.AddRange(new[] { a, b, c, d });
        mesh.Faces.Add(new[] { o, o + 1, o + 2, o + 3 });
    }
}
