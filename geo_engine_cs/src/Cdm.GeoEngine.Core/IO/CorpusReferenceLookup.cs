using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cdm.GeoEngine.Core.IO;

public sealed class CorpusReference
{
    public string ModelId { get; init; } = "";
    public int ResolutionVertices { get; init; }
    public int ResolutionFaces { get; init; }
    public int GeometryVertices { get; init; }
    public int GeometryFaces { get; init; }
    public int GeometryComponentCount { get; init; }
    public int DoorCount { get; init; }
    public bool DoorsMatch { get; init; }
    public string? RecordPath { get; init; }
    public string? ScenePath { get; init; }
    public string? ResolutionMeshPath { get; init; }
    public string? GeometryMeshPath { get; init; }
    public bool HasFullMeshes =>
        !string.IsNullOrEmpty(ResolutionMeshPath)
        && !string.IsNullOrEmpty(GeometryMeshPath)
        && GeometryComponentCount > 0;
}

/// <summary>Lookup reference targets from building_corpus_index.json.</summary>
public static class CorpusReferenceLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static CorpusReference? TryGetById(BuildingCorpusIndex corpus, string modelId, CorpusMeshStore? meshStore = null)
    {
        var model = corpus.Models.FirstOrDefault(
            m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        return model == null ? null : ToReference(model, corpus.SourcePath, meshStore);
    }

    public static CorpusReference? TryFindByHint(BuildingCorpusIndex corpus, string hint, CorpusMeshStore? meshStore = null)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return null;

        var norm = hint.Replace('\\', '/').ToLowerInvariant();
        var fileStem = Path.GetFileNameWithoutExtension(norm);

        foreach (var model in corpus.Models)
        {
            var id = model.Id.ToLowerInvariant();
            if (norm.Contains(id) || id.EndsWith("/" + fileStem, StringComparison.Ordinal))
                return ToReference(model, corpus.SourcePath, meshStore);
            if (string.Equals(fileStem, id.Split('/').LastOrDefault(), StringComparison.OrdinalIgnoreCase))
                return ToReference(model, corpus.SourcePath, meshStore);
        }

        return null;
    }

    public static IReadOnlyList<CorpusReference> LoadAll(string corpusIndexPath, CorpusMeshStore? meshStore = null)
    {
        var corpus = BuildingCorpusReader.Load(corpusIndexPath);
        return corpus.Models.Select(m => ToReference(m, corpus.SourcePath, meshStore)).ToList();
    }

    private static CorpusReference ToReference(
        BuildingCorpusModelDto model,
        string indexPath,
        CorpusMeshStore? meshStore = null)
    {
        var root = Path.GetDirectoryName(indexPath) ?? ".";
        var meshEntry = meshStore?.TryGetEntry(model.Id);
        return new CorpusReference
        {
            ModelId = model.Id,
            ResolutionVertices = meshEntry?.ResolutionVertices ?? model.ResolutionLod1?.Vertices ?? 0,
            ResolutionFaces = meshEntry?.ResolutionFaces ?? model.ResolutionLod1?.Faces ?? 0,
            GeometryVertices = meshEntry?.GeometryVertices ?? model.GeometryLod?.Vertices ?? 0,
            GeometryFaces = meshEntry?.GeometryFaces ?? model.GeometryLod?.Faces ?? 0,
            GeometryComponentCount = meshEntry?.GeometryComponentCount ?? model.GeometryLod?.ComponentCount ?? 0,
            DoorCount = model.DoorsInGeometry?.Count ?? 0,
            DoorsMatch = model.DoorsMatch,
            RecordPath = Path.Combine(root, model.Record.Replace('/', Path.DirectorySeparatorChar)),
            ScenePath = model.Scene == null
                ? null
                : Path.Combine(root, model.Scene.Replace('/', Path.DirectorySeparatorChar)),
            ResolutionMeshPath = meshEntry?.ResolutionMeshPath,
            GeometryMeshPath = meshEntry?.GeometryMeshPath,
        };
    }
}
