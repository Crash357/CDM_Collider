using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class PatchDiagnosticsTests
{
    [Fact]
    public void Analyze_ReportsTangentSpanAndThickness()
    {
        var mesh = new MeshData { Name = "Wall" };
        AddQuad(mesh, new Vec3(0, 0, 0), new Vec3(0, 4, 0), new Vec3(0, 4, 2), new Vec3(0, 0, 2));
        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var patches = FaceDrivenDecomposer.Split(mesh, 0.05, profile);

        Assert.NotEmpty(patches);
        var diag = PatchDiagnostics.Analyze(mesh, patches, profile);
        Assert.Equal(patches.Count, diag.Count);
        Assert.True(diag[0].TangentSpanM > 3.5);
        Assert.True(diag[0].ThicknessM < 0.5);
    }

    private static void AddQuad(MeshData mesh, Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var o = mesh.Vertices.Count;
        mesh.Vertices.AddRange(new[] { a, b, c, d });
        mesh.Faces.Add(new[] { o, o + 1, o + 2, o + 3 });
    }
}
