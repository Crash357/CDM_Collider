using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Pipeline;

namespace Cdm.GeoEngine.Tests;

/// <summary>Fast smoke: shed_w4 within soft count band + geometric compare present.</summary>
public class GoldenSmokeBlindTests
{
    [Fact]
    public void Smoke_ShedW4_SoftCountAndGeometry()
    {
        var root = TestPaths.RepoRoot();
        var resPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w4", "resolution_lod_1.json");
        var geoPath = Path.Combine(root, "p3d_files", "residential", "_sandbox", "meshes",
            "sheds", "shed_w4", "geometry_lod.json");
        var corpusPath = Path.Combine(root, "p3d_files", "residential", "_sandbox",
            "building_corpus_index.json");
        if (!File.Exists(resPath) || !File.Exists(geoPath) || !File.Exists(corpusPath))
            return;

        var resolution = JsonMeshLoader.LoadResolutionFromFile(resPath);
        var referenceGeo = JsonMeshLoader.LoadGeometryFromFile(geoPath);
        var corpus = BuildingCorpusReader.Load(corpusPath);
        var reference = CorpusReferenceLookup.TryGetById(corpus, "sheds/shed_w4");

        var result = AdaptiveBuildingGenerator.GenerateAdaptive(
            resolution, reference, "sheds/shed_w4", referenceGeo,
            blindGeneration: true, allowSnap: false);

        Assert.InRange(result.Geometry.Components.Count, 18, 40);
        Assert.NotNull(result.GeometricCompare);
        Assert.True(result.GeometricCompare!.MaxCornerErrorM < 10.0,
            $"max corner {result.GeometricCompare.MaxCornerErrorM:F3}m");
    }
}
