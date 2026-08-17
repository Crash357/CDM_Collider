using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class ObbFitterTests
{
    [Fact]
    public void FitPatch_RotatedWall_AlignsLongEdge()
    {
        var n = new Vec3(0, 1, 0);
        var u = new Vec3(1, 0, 0);
        var v = new Vec3(0, 0, 1);
        var verts = new List<Vec3>();
        for (var x = 0; x <= 4; x++)
        {
            for (var z = 0; z <= 2; z++)
                verts.Add(u.Scale(x).Add(n.Scale(0)).Add(v.Scale(z)));
        }

        var obb = ObbFitter.FitPatch(verts, n);
        Assert.NotNull(obb);
        Assert.True(obb!.ExtentU > obb.ExtentV);
        Assert.True(System.Math.Abs(obb.AxisU.Dot(u)) > 0.95);
    }

    [Fact]
    public void BuildingAnalyzer_ReadsFootprintSize()
    {
        var mesh = new MeshData { Name = "BoxBuilding" };
        mesh.Vertices.AddRange(new[]
        {
            new Vec3(0, 0, 0), new Vec3(5, 0, 0), new Vec3(5, 3, 0), new Vec3(0, 3, 0),
            new Vec3(0, 0, 2), new Vec3(5, 0, 2), new Vec3(5, 3, 2), new Vec3(0, 3, 2),
        });
        mesh.Faces.Add(new[] { 0, 1, 2, 3 });

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        Assert.True(profile.SizeM.X >= 4.9);
        Assert.True(profile.SizeM.Y >= 2.9);
        Assert.True(profile.HeightM >= 1.9);
    }

    [Fact]
    public void FitPatch_SingleSidedWall_CentersSlabOnSurface()
    {
        var n = new Vec3(1, 0, 0);
        var verts = new List<Vec3>();
        for (var y = 0.0; y <= 2.0; y += 2.0)
        for (var z = 0.0; z <= 1.0; z += 1.0)
            verts.Add(new Vec3(0, y, z));

        var profile = new BuildingMeshProfile
        {
            WallThicknessM = 0.2,
            HorizontalSlabM = 0.12,
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            SizeM = new Vec3(4, 4, 3),
            HeightM = 3,
        };

        var obb = ObbFitter.FitPatch(verts, n, profile);
        Assert.NotNull(obb);
        Assert.InRange(obb!.Center.X, -0.11, 0.11);
        Assert.InRange(obb.ExtentN, 0.09, 0.11);
    }

    [Fact]
    public void FitPatch_SingleSidedWall_UsesVertexCentroidOnNormal()
    {
        var n = new Vec3(1, 0, 0);
        var verts = new List<Vec3>
        {
            new(0.05, 0, 0), new(0.05, 2, 0), new(0.05, 2, 2), new(0.05, 0, 2),
        };
        var profile = new BuildingMeshProfile
        {
            WallThicknessM = 0.2,
            HorizontalSlabM = 0.12,
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            SizeM = new Vec3(4, 4, 3),
            HeightM = 3,
        };

        var obb = ObbFitter.FitPatch(verts, n, profile);
        Assert.NotNull(obb);
        Assert.InRange(obb!.Center.X, 0.04, 0.06);
    }
}
