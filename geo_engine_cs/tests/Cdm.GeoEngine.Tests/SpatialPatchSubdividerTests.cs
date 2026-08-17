using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class SpatialPatchSubdividerTests
{
    [Fact]
    public void Subdivide_SplitsPatchAtGap()
    {
        var mesh = new MeshData { Name = "Wall" };
        // Two wall panels with gap along Y (building axis)
        AddQuad(mesh, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 1, 2), new Vec3(0, 0, 2));
        AddQuad(mesh, new Vec3(0, 2.5, 0), new Vec3(0, 3.5, 0), new Vec3(0, 3.5, 2), new Vec3(0, 2.5, 2));

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var patches = WallAxisCluster.Split(mesh, 0.05, 0.3, profile);
        var subdivided = SpatialPatchSubdivider.Subdivide(
            mesh, patches, profile,
            new SpatialSubdivisionOptions { MinGapM = 0.2, BinSizeM = 0.1, MinPatchAreaM2 = 0.05 });

        Assert.True(subdivided.Count >= patches.Count);
    }

    private static void AddQuad(MeshData mesh, Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var o = mesh.Vertices.Count;
        mesh.Vertices.AddRange(new[] { a, b, c, d });
        mesh.Faces.Add(new[] { o, o + 1, o + 2, o + 3 });
    }
}
