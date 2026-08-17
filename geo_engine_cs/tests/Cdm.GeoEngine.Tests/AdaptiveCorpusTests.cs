using Cdm.GeoEngine.Core.IO;
using Cdm.GeoEngine.Core.Models;
using Cdm.GeoEngine.Core.Pipeline;
using Cdm.GeoEngine.Core.Primitives;

namespace Cdm.GeoEngine.Tests;

public class AdaptiveCorpusTests
{
    [Fact]
    public void CorpusIndex_Loads240Models()
    {
        var path = FindCorpusIndex();
        if (!File.Exists(path))
            return;

        var corpus = BuildingCorpusReader.Load(path);
        Assert.True(corpus.ModelCount >= 200);
        Assert.NotEmpty(corpus.Models);
    }

    [Fact]
    public void CorpusReference_ShedW1_Has19Components()
    {
        var path = FindCorpusIndex();
        if (!File.Exists(path))
            return;

        var corpus = BuildingCorpusReader.Load(path);
        var reference = CorpusReferenceLookup.TryGetById(corpus, "sheds/shed_w1");
        Assert.NotNull(reference);
        Assert.Equal(19, reference!.GeometryComponentCount);
        Assert.True(reference.GeometryVertices > 100);
    }

    [Fact]
    public void AdaptiveGenerator_WithCorpusReference_PicksParameters()
    {
        var path = FindCorpusIndex();
        if (!File.Exists(path))
            return;

        var corpus = BuildingCorpusReader.Load(path);
        var reference = CorpusReferenceLookup.TryGetById(corpus, "bus/busstation_building");
        Assert.NotNull(reference);

        var mesh = BuildSimpleBuildingMesh();
        var result = AdaptiveBuildingGenerator.GenerateAdaptive(mesh, reference, "bus/busstation_building");

        Assert.True(result.CandidatesEvaluated > 20);
        Assert.True(result.Geometry.Components.Count > 0);
        Assert.True(result.Validation.HasReference);
        Assert.Equal(reference!.GeometryComponentCount, result.Validation.ReferenceComponents);
    }

    [Fact]
    public void AdaptiveGenerator_WithoutReference_StillProducesComponents()
    {
        var mesh = BuildSimpleBuildingMesh();
        var result = AdaptiveBuildingGenerator.GenerateAdaptive(mesh, null, "synthetic");

        Assert.True(result.Geometry.Components.Count > 0);
        Assert.False(result.Validation.HasReference);
    }

    private static MeshData BuildSimpleBuildingMesh()
    {
        var mesh = new MeshData { Name = "SyntheticBuilding" };
        var w = 4.0;
        var d = 3.0;
        var h = 2.5;
        mesh.Vertices.AddRange(new[]
        {
            new Vec3(0, 0, 0), new Vec3(w, 0, 0), new Vec3(w, d, 0), new Vec3(0, d, 0),
            new Vec3(0, 0, h), new Vec3(w, 0, h), new Vec3(w, d, h), new Vec3(0, d, h),
        });
        mesh.Faces.AddRange(new[]
        {
            new[] { 0, 1, 2, 3 },
            new[] { 4, 5, 6, 7 },
            new[] { 0, 1, 5, 4 },
            new[] { 1, 2, 6, 5 },
            new[] { 2, 3, 7, 6 },
            new[] { 3, 0, 4, 7 },
        });
        return mesh;
    }

    private static string FindCorpusIndex()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "p3d_files", "residential", "_sandbox", "building_corpus_index.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..",
                "p3d_files", "residential", "_sandbox", "building_corpus_index.json")),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
