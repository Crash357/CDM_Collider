using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.DayZ;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class FaceBoundsObbFitterTests
{
    [Fact]
    public void GableSlope_CenterTracksHeightMidpoint()
    {
        var profile = new BuildingMeshProfile
        {
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            WallThicknessM = 0.15,
            HorizontalSlabM = 0.12,
        };

        var verts = new List<Vec3>
        {
            new(3.0, -0.07, -0.67),
            new(3.0, 2.59, -0.67),
            new(3.0, 2.59, 2.52),
            new(3.0, -0.07, 2.52),
        };

        var n = new Vec3(-0.05, -0.63, 0.77).Normalized();
        var obb = FaceBoundsObbFitter.FitPatch(verts, n, profile, PatchSurfaceKind.Slope);
        Assert.NotNull(obb);
        Assert.InRange(obb!.Center.Z, 0.55, 1.35);
        Assert.InRange(obb.Center.X, 2.85, 3.15);
    }

    [Fact]
    public void WallRectangle_MatchesFaceBoundsWithSkin()
    {
        var profile = new BuildingMeshProfile
        {
            AxisX = new Vec3(1, 0, 0),
            AxisY = new Vec3(0, 1, 0),
            AxisZ = new Vec3(0, 0, 1),
            WallThicknessM = 0.15,
            HorizontalSlabM = 0.12,
        };

        var verts = new List<Vec3>
        {
            new(2.0, 0.0, 0.0),
            new(2.0, 3.0, 0.0),
            new(2.0, 3.0, 2.5),
            new(2.0, 0.0, 2.5),
        };

        var obb = FaceBoundsObbFitter.FitPatch(verts, new Vec3(1, 0, 0), profile);
        Assert.NotNull(obb);

        var skin = GeometryLodConstants.OverhangM;
        Assert.InRange(obb!.ExtentU, 1.25 - 0.01, 1.25 + 0.01);
        Assert.InRange(obb.ExtentV, 1.5 - 0.01, 1.5 + 0.01);
        Assert.InRange(obb.ExtentN, profile.WallThicknessM * 0.5 - skin, profile.WallThicknessM * 0.5 + skin);

        var outerX = obb.Corners.Max(c => c.X);
        Assert.InRange(outerX, 2.0 + skin - 1e-6, 2.0 + skin + 1e-6);
    }

    [Fact]
    public void CollectPatchFaceVertices_UsesFaceIndicesOnly()
    {
        var mesh = new MeshData
        {
            Vertices =
            {
                new Vec3(0, 0, 0),
                new Vec3(1, 0, 0),
                new Vec3(1, 1, 0),
                new Vec3(99, 99, 99),
            },
            Faces = { new[] { 0, 1, 2 } },
        };

        var patch = new PatchCluster(
            new[] { 0 },
            mesh.Vertices.Take(3).ToList(),
            0.5,
            new Vec3(0, 0, 1));

        var collected = FaceBoundsObbFitter.CollectPatchFaceVertices(mesh, patch);
        Assert.Equal(3, collected.Count);
        Assert.DoesNotContain(new Vec3(99, 99, 99), collected);
    }
}
