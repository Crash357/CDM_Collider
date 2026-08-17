using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class ReferenceSegmentStatsTests
{
    [Fact]
    public void Analyze_ThinBox_ReportsThicknessAndTangent()
    {
        var mesh = new MeshData { Name = "Geometry" };
        AppendBox(mesh, new Vec3(0, 0, 0), new Vec3(4, 0, 0), new Vec3(0, 0.15, 0));
        mesh.VertexGroups["component01"] = Enumerable.Range(0, mesh.Vertices.Count).ToList();

        var stats = ReferenceSegmentStatsAnalyzer.Analyze("test/wall", mesh);
        Assert.Equal(1, stats.ComponentCount);
        Assert.InRange(stats.MedianThicknessM, 0.08, 0.40);
        Assert.True(stats.MedianTangentM > 3.0);
    }

    private static void AppendBox(MeshData mesh, Vec3 origin, Vec3 u, Vec3 n)
    {
        var v = n.Cross(u).Normalized();
        var offset = mesh.Vertices.Count;
        foreach (var sn in new[] { 0.0, 1.0 })
        foreach (var su in new[] { 0.0, 1.0 })
        foreach (var sv in new[] { 0.0, 1.0 })
            mesh.Vertices.Add(origin.Add(u.Scale(su)).Add(n.Scale(sn)).Add(v.Scale(sv)));
        foreach (var face in GeometryLodConstants.BoxFaces)
            mesh.Faces.Add(face.Select(vi => offset + vi).ToArray());
    }
}
