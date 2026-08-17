using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Validation;

namespace Cdm.GeoEngine.Tests;

public class FaceDrivenCountTests
{
    [Fact]
    public void ShedW1_Adaptive_PrefersSoftCountBand()
    {
        var resolution = LoadRes("sheds", "shed_w1");
        var referenceGeo = LoadGeo("sheds", "shed_w1");
        var corpusPath = Path.Combine(TestPaths.RepoRoot(), "p3d_files", "residential", "_sandbox",
            "building_corpus_index.json");
        if (resolution == null || referenceGeo == null || !File.Exists(corpusPath))
            return;

        var corpus = BuildingCorpusReader.Load(corpusPath);
        var reference = CorpusReferenceLookup.TryGetById(corpus, "sheds/shed_w1");

        var result = AdaptiveBuildingGenerator.GenerateAdaptive(
            resolution, reference, "sheds/shed_w1", referenceGeo,
            blindGeneration: true, allowSnap: false);

        Assert.InRange(result.Geometry.Components.Count, 15, 45);
    }

    private static MeshData? LoadRes(string cat, string name)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "p3d_files", "residential", "_sandbox", "meshes",
            cat, name, "resolution_lod_1.json");
        return File.Exists(path) ? JsonMeshLoader.LoadResolutionFromFile(path) : null;
    }

    private static MeshData? LoadGeo(string cat, string name)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "p3d_files", "residential", "_sandbox", "meshes",
            cat, name, "geometry_lod.json");
        return File.Exists(path) ? JsonMeshLoader.LoadGeometryFromFile(path) : null;
    }
}
