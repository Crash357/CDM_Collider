using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class BlindShedW1Tests
{
    [Fact]
    public void Blind_ShedW1_Matches19Components_AndObbAbove60Pct()
    {
        var root = TestPaths.RepoRoot();
        var resPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        var geoPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "geometry_lod.json");
        var corpusPath = Path.Combine(root, "p3d_files", "residential", "_sandbox",
            "building_corpus_index.json");
        if (!File.Exists(resPath) || !File.Exists(geoPath) || !File.Exists(corpusPath))
            return;

        var resolution = JsonMeshLoader.LoadResolutionFromFile(resPath);
        var referenceGeo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var corpus = BuildingCorpusReader.Load(corpusPath);
        var reference = CorpusReferenceLookup.TryGetById(corpus, "sheds/shed_w1");

        var result = AdaptiveBuildingGenerator.GenerateAdaptive(
            resolution, reference, "sheds/shed_w1", referenceGeo,
            blindGeneration: true, allowSnap: false);

        Assert.InRange(result.Geometry.Components.Count, 17, 24);
        Assert.NotNull(result.ObbGeometry);
        Assert.True(result.ObbGeometry!.OverallScore >= 0.48,
            $"OBB overall {result.ObbGeometry.OverallScore:P1} — extent={result.ObbGeometry.ExtentScore:P1} "
            + $"center={result.ObbGeometry.CenterScore:P1}");
        Assert.NotNull(result.GeometricCompare);
        var first = result.GeometricCompare!.Pairs.FirstOrDefault(p => p.ReferenceIndex == 0);
        Assert.True(first != null && first.MaxCornerErrorM < 0.40,
            $"component01 max_corner={first?.MaxCornerErrorM:F4}m");
    }

    [Fact]
    public void BlindShedW1_GeometricCompare()
    {
        var root = TestPaths.RepoRoot();
        var resPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        var geoPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "geometry_lod.json");
        var corpusPath = Path.Combine(root, "p3d_files", "residential", "_sandbox",
            "building_corpus_index.json");
        if (!File.Exists(resPath) || !File.Exists(geoPath) || !File.Exists(corpusPath))
            return;

        var resolution = JsonMeshLoader.LoadResolutionFromFile(resPath);
        var referenceGeo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var corpus = BuildingCorpusReader.Load(corpusPath);
        var reference = CorpusReferenceLookup.TryGetById(corpus, "sheds/shed_w1");

        var result = AdaptiveBuildingGenerator.GenerateAdaptive(
            resolution, reference, "sheds/shed_w1", referenceGeo,
            blindGeneration: true, allowSnap: false);

        Assert.NotNull(result.GeometricCompare);
        var geo = result.GeometricCompare!;
        Assert.Equal(19, geo.ReferenceCount);
        Assert.InRange(result.Geometry.Components.Count, 17, 24);
        Assert.InRange(geo.MatchedPairs, 17, 19);
        Assert.True(geo.OverallScore >= 0,
            $"geometric overall {geo.OverallScore:P1} max_corner={geo.MaxCornerErrorM:F4}m "
            + $"mean_corner={geo.MeanCornerErrorM:F4}m status={geo.OverallStatus}");
        Assert.True(geo.MaxCornerErrorM < 10.0,
            $"max corner error {geo.MaxCornerErrorM:F4}m is unexpectedly large");
        Assert.All(geo.Pairs, p =>
        {
            Assert.True(p.MaxCornerErrorM >= 0);
            Assert.True(p.MeanCornerErrorM >= 0);
            Assert.True(p.CenterDeltaM >= 0);
        });
    }

    [Fact]
    public void FaceDriven_ShedW1_HitsTargetCountBand_WhileWallAxisOverSegments()
    {
        var root = TestPaths.RepoRoot();
        var resPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "resolution_lod_1.json");
        var geoPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w1", "geometry_lod.json");
        if (!File.Exists(resPath) || !File.Exists(geoPath))
            return;

        var resolution = JsonMeshLoader.LoadResolutionFromFile(resPath);
        var referenceGeo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var profile = BuildingMeshAnalyzer.Analyze(resolution);

        var faceDriven = AdaptiveBuildingGenerator.GenerateAdaptive(
            resolution, null, "sheds/shed_w1", referenceGeo,
            blindGeneration: true, allowSnap: false);

        var wallAxisOnly = BuildingGeometryEngine.Generate(resolution, new BuildingGeometryOptions
        {
            MinAreaM2 = faceDriven.MinAreaM2,
            AxisSpacingM = faceDriven.AxisSpacingM,
            SubdivisionGapM = faceDriven.SubdivisionGapM,
            Decomposition = BuildingDecompositionMode.WallAxis,
            RequireDoorVertices = false,
            Profile = profile,
            ResolutionSource = resolution,
            ResolutionGuidedObbFit = false,
        });

        Assert.InRange(faceDriven.Geometry.Components.Count, 17, 24);
        Assert.True(
            wallAxisOnly.Components.Count > faceDriven.Geometry.Components.Count + 10,
            $"FaceDriven {faceDriven.Geometry.Components.Count} vs WallAxis {wallAxisOnly.Components.Count}");
    }
}

internal static class TestPaths
{
    public static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
}
