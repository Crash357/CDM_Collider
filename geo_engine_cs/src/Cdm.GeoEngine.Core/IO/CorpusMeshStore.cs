using System.Text.Json;
using System.Text.Json.Serialization;
using Cdm.GeoEngine.Core.Models;

namespace Cdm.GeoEngine.Core.IO;

public sealed class CorpusMeshEntry
{
    public string ModelId { get; init; } = "";
    public string? ResolutionMeshPath { get; init; }
    public string? GeometryMeshPath { get; init; }
    public int ResolutionVertices { get; init; }
    public int ResolutionFaces { get; init; }
    public int GeometryVertices { get; init; }
    public int GeometryFaces { get; init; }
    public int GeometryComponentCount { get; init; }
    public bool HasResolutionMesh => !string.IsNullOrEmpty(ResolutionMeshPath);
    public bool HasGeometryMesh => !string.IsNullOrEmpty(GeometryMeshPath);
    public bool HasFullPair => HasResolutionMesh && HasGeometryMesh && GeometryComponentCount > 0;
}

public sealed class CorpusMeshPair
{
    public string ModelId { get; init; } = "";
    public MeshData ResolutionLod { get; init; } = new();
    public MeshData GeometryLod { get; init; } = new();
    public CorpusMeshEntry Entry { get; init; } = new();
}

/// <summary>Loads baked mesh pairs from corpus_meshes_index.json + meshes/ folder.</summary>
public sealed class CorpusMeshStore
{
    public string SourcePath { get; }
    public string RootDirectory { get; }
    public IReadOnlyDictionary<string, CorpusMeshEntry> Entries { get; }

    private CorpusMeshStore(string sourcePath, string rootDirectory, Dictionary<string, CorpusMeshEntry> entries)
    {
        SourcePath = sourcePath;
        RootDirectory = rootDirectory;
        Entries = entries;
    }

    public static CorpusMeshStore? TryLoad(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return null;

        var json = File.ReadAllText(manifestPath);
        var dto = JsonSerializer.Deserialize<CorpusMeshesIndexDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty corpus meshes manifest.");
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";

        var entries = new Dictionary<string, CorpusMeshEntry>(StringComparer.OrdinalIgnoreCase);
        if (dto.Models != null)
        {
            foreach (var (modelId, model) in dto.Models)
            {
                var entry = ToEntry(modelId, model, root);
                if (entry != null)
                    entries[modelId] = entry;
            }
        }

        return new CorpusMeshStore(manifestPath, root, entries);
    }

    public static string DefaultManifestPath(string sandboxDir) =>
        Path.Combine(sandboxDir, "corpus_meshes_index.json");

    public CorpusMeshEntry? TryGetEntry(string modelId) =>
        Entries.TryGetValue(modelId, out var entry) ? entry : null;

    public bool TryLoadPair(string modelId, out CorpusMeshPair? pair)
    {
        pair = null;
        if (!Entries.TryGetValue(modelId, out var entry) || !entry.HasFullPair)
            return false;
        if (entry.ResolutionMeshPath == null || entry.GeometryMeshPath == null)
            return false;
        if (!File.Exists(entry.ResolutionMeshPath) || !File.Exists(entry.GeometryMeshPath))
            return false;

        pair = new CorpusMeshPair
        {
            ModelId = modelId,
            ResolutionLod = JsonMeshLoader.LoadResolutionFromFile(entry.ResolutionMeshPath),
            GeometryLod = JsonMeshLoader.LoadGeometryFromFile(entry.GeometryMeshPath),
            Entry = entry,
        };
        return true;
    }

    public MeshData? TryLoadResolution(string modelId)
    {
        if (!Entries.TryGetValue(modelId, out var entry) || entry.ResolutionMeshPath == null)
            return null;
        return File.Exists(entry.ResolutionMeshPath)
            ? JsonMeshLoader.LoadResolutionFromFile(entry.ResolutionMeshPath)
            : null;
    }

    public MeshData? TryLoadGeometry(string modelId)
    {
        if (!Entries.TryGetValue(modelId, out var entry) || entry.GeometryMeshPath == null)
            return null;
        return File.Exists(entry.GeometryMeshPath)
            ? JsonMeshLoader.LoadGeometryFromFile(entry.GeometryMeshPath)
            : null;
    }

    private static CorpusMeshEntry? ToEntry(string modelId, CorpusMeshModelDto model, string root)
    {
        var res = model.ResolutionLod1;
        var geo = model.GeometryLod;
        var resPath = ResolveMeshPath(root, res?.Path);
        var geoPath = ResolveMeshPath(root, geo?.Path);
        if (resPath == null && geoPath == null)
            return null;

        return new CorpusMeshEntry
        {
            ModelId = modelId,
            ResolutionMeshPath = resPath,
            GeometryMeshPath = geoPath,
            ResolutionVertices = res?.Vertices ?? 0,
            ResolutionFaces = res?.Faces ?? 0,
            GeometryVertices = geo?.Vertices ?? 0,
            GeometryFaces = geo?.Faces ?? 0,
            GeometryComponentCount = geo?.ComponentCount ?? 0,
        };
    }

    private static string? ResolveMeshPath(string root, string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            return null;
        var relNative = relPath.Replace('/', Path.DirectorySeparatorChar);

        var path = Path.Combine(root, relNative);
        if (File.Exists(path))
            return path;

        // Legacy layout: mesh dumps for the original "residential" category were baked
        // under p3d_files/residential/_sandbox/ before the multi-category _corpus/ index
        // was introduced. Newer manifests under p3d_files/_corpus/ still reference the
        // same relative "meshes/..." paths, so fall back to the legacy sandbox root.
        var legacyRoot = Path.Combine(root, "..", "residential", "_sandbox");
        var legacyPath = Path.GetFullPath(Path.Combine(legacyRoot, relNative));
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class CorpusMeshesIndexDto
    {
        public Dictionary<string, CorpusMeshModelDto>? Models { get; set; }
    }

    private sealed class CorpusMeshModelDto
    {
        [JsonPropertyName("resolution_lod_1")]
        public CorpusMeshLodDto? ResolutionLod1 { get; set; }
        public CorpusMeshLodDto? GeometryLod { get; set; }
        public List<string>? Errors { get; set; }
    }

    private sealed class CorpusMeshLodDto
    {
        public string? Path { get; set; }
        public int Vertices { get; set; }
        public int Faces { get; set; }

        [JsonPropertyName("component_count")]
        public int ComponentCount { get; set; }
    }
}
