using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Pipeline;

namespace Cdm.GeoEngine.Tests;

public sealed class SegmentSpanCalibratorIntegrationTests
{
    [Fact]
    public void ShedW1_CalibratorAtFactor1_NearTargetCount()
    {
        var root = TestPaths.RepoRoot();
        var resPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        if (!File.Exists(resPath))
            return;

        var resolution = JsonMeshLoader.LoadResolutionFromFile(resPath);
        var profile = BuildingMeshAnalyzer.Analyze(resolution);
        var doors = DoorRegionExtractor.Extract(resolution);
        var span = SegmentSpanCalibrator.EstimateWallMaxSpanM(profile, 19);

        var result = BuildingGeometryEngine.Generate(resolution, new BuildingGeometryOptions
        {
            MinAreaM2 = 0.1,
            Decomposition = BuildingDecompositionMode.FaceDriven,
            Profile = profile,
            DoorRegions = doors,
            TargetComponentCount = 19,
            WallSegmentMaxSpanM = span,
            WallSegmentTightFactor = 1.0,
        });

        Assert.InRange(span, 1.3, 1.75);
        Assert.InRange(result.Components.Count, 17, 21);
    }
}
