using Cdm.GeoEngine.Core.IO;

namespace Cdm.GeoEngine.Tests;

public class CorpusMeshStoreTests
{
    [Fact]
    public void CorpusMeshStore_ShedW1_LoadsFullPair()
    {
        var manifest = FindMeshManifest();
        if (!File.Exists(manifest))
            return;

        var store = CorpusMeshStore.TryLoad(manifest);
        Assert.NotNull(store);
        Assert.True(store!.TryLoadPair("sheds/shed_w1", out var pair));
        Assert.NotNull(pair);
        Assert.Equal(1144, pair!.ResolutionLod.VertexCount);
        Assert.Equal(154, pair.GeometryLod.VertexCount);
        Assert.Equal(19, pair.Entry.GeometryComponentCount);
    }

    [Fact]
    public void CorpusReference_WithMeshStore_HasPaths()
    {
        var corpusPath = FindCorpusIndex();
        var manifest = FindMeshManifest();
        if (!File.Exists(corpusPath) || !File.Exists(manifest))
            return;

        var corpus = BuildingCorpusReader.Load(corpusPath);
        var store = CorpusMeshStore.TryLoad(manifest);
        var reference = CorpusReferenceLookup.TryGetById(corpus, "sheds/shed_w1", store);
        Assert.NotNull(reference);
        Assert.True(reference!.HasFullMeshes);
        Assert.NotNull(reference.ResolutionMeshPath);
        Assert.NotNull(reference.GeometryMeshPath);
    }

    [Fact]
    public void ToBuildingDatasets_WithMeshStore_LoadsRealVertices()
    {
        var corpusPath = FindCorpusIndex();
        var manifest = FindMeshManifest();
        if (!File.Exists(corpusPath) || !File.Exists(manifest))
            return;

        var corpus = BuildingCorpusReader.Load(corpusPath);
        var store = CorpusMeshStore.TryLoad(manifest);
        var datasets = BuildingCorpusReader.ToBuildingDatasets(corpus, store);
        var shed = datasets.FirstOrDefault(d => d.ModelName == "sheds/shed_w1");
        Assert.NotNull(shed);
        Assert.Equal(1144, shed!.ResolutionLod?.VertexCount);
        Assert.Equal(154, shed.GeometryLod?.VertexCount);
        Assert.True(shed.GeometryLod!.Faces.Count > 100);
    }

    private static string FindCorpusIndex()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "p3d_files", "residential", "_sandbox", "building_corpus_index.json");
            if (File.Exists(path))
                return path;
        }
        return "";
    }

    private static string FindMeshManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "p3d_files", "residential", "_sandbox", "corpus_meshes_index.json");
            if (File.Exists(path))
                return path;
        }
        return "";
    }
}
