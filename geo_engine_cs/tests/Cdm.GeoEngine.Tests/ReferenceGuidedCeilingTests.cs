using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

/// <summary>ReferenceGuided + ConstrainedObb ceiling — proves fit can work when segmentation is guided.</summary>
public class ReferenceGuidedCeilingTests
{
    [Fact]
    public void ShedW4_Constrained_BetterThanBlind()
    {
        var root = TestPaths.RepoRoot();
        var resPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w4", "resolution_lod_1.json");
        var geoPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w4", "geometry_lod.json");
        if (!File.Exists(resPath) || !File.Exists(geoPath))
            return;

        var resolution = JsonMeshLoader.LoadResolutionFromFile(resPath);
        var referenceGeo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var refObbs = ReferenceObbExtractor.ExtractFromGeometryLod(referenceGeo);
        if (refObbs.Count == 0)
            return;

        var profile = BuildingMeshAnalyzer.Analyze(resolution);

        var blind = BuildingGeometryEngine.Generate(resolution, new BuildingGeometryOptions
        {
            Decomposition = BuildingDecompositionMode.FaceDriven,
            ResolutionGuidedObbFit = true,
            ResolutionSource = resolution,
            Profile = profile,
            RequireDoorVertices = false,
        });

        var guided = BuildingGeometryEngine.Generate(resolution, new BuildingGeometryOptions
        {
            Decomposition = BuildingDecompositionMode.ReferenceGuided,
            ReferenceFit = ReferenceFitMode.Constrained,
            ReferenceBlendWeight = 0.85,
            ReferenceObbs = refObbs,
            Profile = profile,
            RequireDoorVertices = false,
        });

        var blindGeo = GeometricComponentComparer.Compare(
            GeometricComponentComparer.ExtractFromGeometryLod(referenceGeo),
            GeometricComponentComparer.ExtractFromGeometryLod(blind.GeometryLod));
        var guidedGeo = GeometricComponentComparer.Compare(
            GeometricComponentComparer.ExtractFromGeometryLod(referenceGeo),
            GeometricComponentComparer.ExtractFromGeometryLod(guided.GeometryLod));

        Assert.NotNull(blindGeo);
        Assert.NotNull(guidedGeo);
        Assert.True(
            guidedGeo!.MeanCornerErrorM < blindGeo!.MeanCornerErrorM,
            $"guided={guidedGeo.MeanCornerErrorM:F3} blind={blindGeo.MeanCornerErrorM:F3}");
    }
}
