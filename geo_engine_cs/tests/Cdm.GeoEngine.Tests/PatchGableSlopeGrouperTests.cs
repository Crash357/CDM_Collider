using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class PatchGableSlopeGrouperTests
{
    [Fact]
    public void ShedW1_ExtractsPosXGableWithLowCenterZ()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        if (!File.Exists(path))
            return;

        var mesh = JsonMeshLoader.LoadResolutionFromFile(path);
        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var prefilter = ResolutionRegionPrefilter.Apply(mesh, profile);
        var workMesh = prefilter.RemainingMesh;
        var ax = profile.AxisX.Normalized();

        var (_, gablePatches) = PatchGableSlopeGrouper.Extract(workMesh, profile, new HashSet<int>(), 0.05);

        Assert.NotEmpty(gablePatches);
        var posX = gablePatches
            .Where(p => p.WorldVertices.Average(v => v.Dot(ax)) > 2.4)
            .ToList();
        Assert.NotEmpty(posX);

        var main = posX.OrderByDescending(p => p.AreaM2).First();
        var cz = main.WorldVertices.Average(v => v.Z);
        var cx = main.WorldVertices.Average(v => v.X);

        Assert.True(main.AreaM2 > 2.0, $"posX gable area={main.AreaM2:F2} m²");
        Assert.InRange(cz, 0.4, 1.6);
        Assert.InRange(cx, 2.7, 3.2);
    }

    [Fact]
    public void ShedW1_ExtractsPosYGableWithLowCenterZ()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        if (!File.Exists(path))
            return;

        var mesh = JsonMeshLoader.LoadResolutionFromFile(path);
        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var prefilter = ResolutionRegionPrefilter.Apply(mesh, profile);
        var workMesh = prefilter.RemainingMesh;
        var ay = profile.AxisY.Normalized();

        var (_, gablePatches) = PatchGableSlopeGrouper.Extract(workMesh, profile, new HashSet<int>(), 0.05);
        var posY = gablePatches
            .Where(p => p.WorldVertices.Average(v => v.Dot(ay)) > 1.8)
            .ToList();
        Assert.NotEmpty(posY);

        var main = posY.OrderByDescending(p => p.AreaM2).First();
        var cz = main.WorldVertices.Average(v => v.Z);
        var cy = main.WorldVertices.Average(v => v.Y);

        Assert.True(main.AreaM2 > 1.5, $"posY gable area={main.AreaM2:F2} m²");
        Assert.InRange(cz, 0.4, 1.6);
        Assert.InRange(cy, 2.0, 2.9);
    }

    [Fact]
    public void ShedW1_PosYGable_ObbIsSlopedNotWall()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        if (!File.Exists(path))
            return;

        var mesh = JsonMeshLoader.LoadResolutionFromFile(path);
        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var workMesh = ResolutionRegionPrefilter.Apply(mesh, profile).RemainingMesh;
        var ay = profile.AxisY.Normalized();
        var az = profile.AxisZ.Normalized();

        var (_, gablePatches) = PatchGableSlopeGrouper.Extract(workMesh, profile, new HashSet<int>(), 0.05);
        var main = gablePatches
            .OrderByDescending(p => p.WorldVertices.Average(v => v.Dot(ay)))
            .First();

        var verts = FaceBoundsObbFitter.CollectPatchFaceVertices(workMesh, main);
        var obb = FaceBoundsObbFitter.FitPatch(verts, main.DominantNormal, profile, PatchSurfaceKind.Slope);
        Assert.NotNull(obb);

        var n = obb!.AxisN.Normalized();
        Assert.True(System.Math.Abs(n.Dot(ay)) < 0.55, $"wall-like normal ({n.X:F2},{n.Y:F2},{n.Z:F2})");
        Assert.InRange(obb.Center.Z, 0.4, 1.6);
    }

    [Fact]
    public void ShedW1_PosYGable_ObbCenterNearRef19()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        if (!File.Exists(path))
            return;

        var mesh = JsonMeshLoader.LoadResolutionFromFile(path);
        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var workMesh = ResolutionRegionPrefilter.Apply(mesh, profile).RemainingMesh;

        var (_, gablePatches) = PatchGableSlopeGrouper.Extract(workMesh, profile, new HashSet<int>(), 0.05);
        var main = gablePatches.First(p => p.GableEnd == GableEndKind.PosY);

        var verts = FaceBoundsObbFitter.CollectGableObbVertices(workMesh, main, profile);
        var obb = FaceBoundsObbFitter.FitPatch(verts, main.DominantNormal, profile, PatchSurfaceKind.Slope);
        Assert.NotNull(obb);
        var ref19 = new Vec3(1.5226040221750736, 2.482973098754883, 1.163491278886795);
        var ctrDist = Vec3.Distance(obb!.Center, ref19);
        Assert.True(ctrDist < 0.55, $"center dist {ctrDist:F2}m obb=({obb.Center.X:F2},{obb.Center.Y:F2},{obb.Center.Z:F2})");
        Assert.InRange(obb.Center.Y, 2.15, 2.75);
        Assert.InRange(obb.Center.Z, 0.4, 1.6);
    }
}
