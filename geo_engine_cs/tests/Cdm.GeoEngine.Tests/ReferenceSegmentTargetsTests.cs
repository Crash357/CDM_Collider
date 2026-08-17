using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class ReferenceSegmentTargetsTests
{
    [Theory]
    [InlineData("sheds/shed_w4")]
    [InlineData("sheds/shed_w1")]
    [InlineData("military/houses/mil_barracks_round")]
    public void SmokeModels_ReferenceStats_InExpectedBand(string modelId)
    {
        var root = TestPaths.RepoRoot();
        var cat = modelId.StartsWith("military") ? "military" : "residential";
        var geoPath = Path.Combine(root, "p3d_files", cat, "_sandbox", "meshes",
            modelId.Split('/')[1], modelId.Contains("mil_") ? modelId.Split('/').Last() : modelId.Split('/').Last(),
            "geometry_lod.json");

        if (modelId.StartsWith("sheds"))
        {
            geoPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
                "sheds", modelId.Split('/')[1], "geometry_lod.json");
        }
        else
        {
            geoPath = Path.Combine(root, "p3d_files", "military", "_sandbox", "meshes",
                "houses", modelId.Split('/').Last(), "geometry_lod.json");
        }

        if (!File.Exists(geoPath))
            return;

        var geo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var stats = ReferenceSegmentStatsAnalyzer.Analyze(modelId, geo);
        Assert.True(stats.ComponentCount > 0);
        Assert.InRange(stats.MedianThicknessM, 0.05, 0.45);
        Assert.True(stats.MedianTangentM > 0.3);
    }
}
