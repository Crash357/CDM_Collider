using Cdm.GeoEngine.Core.Analysis;
using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Tests;

public class CorpusFootprintMatcherTests
{
    [Fact]
    public void FindNearest_AncientHouseResolution_MatchesSlumHouse2()
    {
        var path = FindCorpusIndex();
        if (!File.Exists(path))
            return;

        var corpus = BuildingCorpusReader.Load(path);
        Assert.True(corpus.Models.Count > 100);

        var mesh = JsonMeshLoader.LoadResolutionFromFile(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "Test", "res1.json")));

        var profile = BuildingMeshAnalyzer.Analyze(mesh);
        var meshStorePath = Path.Combine(Path.GetDirectoryName(path)!, "corpus_meshes_index.json");
        var meshStore = CorpusMeshStore.TryLoad(meshStorePath);

        var match = CorpusFootprintMatcher.FindNearest(corpus, mesh, profile, meshStore);
        Assert.NotNull(match);
        Assert.Contains("house", match!.Reference.ModelId, StringComparison.OrdinalIgnoreCase);
        Assert.True(match.Reference.GeometryComponentCount is >= 8 and < 80,
            $"Unexpected component count: {match.Reference.GeometryComponentCount} for {match.Reference.ModelId}");
    }

    private static string FindCorpusIndex()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "p3d_files", "residential", "_sandbox", "building_corpus_index.json")),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
